using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §أمر تطوير وفحص شاشة تسليم المنتج التام للعميل — اختبارات تشغيلية حقيقية:
/// تبني سلسلة كاملة (خام ← خطة ← أمر ← إنتاج ← جودة ← استلام تام ← رصيد عميل)
/// ثم تفحص قائمة الالتقاط والتوزيع FIFO والكميات الجزئية وحراسات تجاوز الرصيد
/// وعزل العملاء وبقاء التتبع وسيناريو §14 حرفياً (500/300/150 ← 250/300/لا شيء ← 250/0/150).
/// </summary>
public class DeliveryPickingDirectiveTests
{
    private static T Svc<T>(TestHost host) => (T)host.Services.CreateScope().ServiceProvider.GetService(typeof(T));
    private static DatesErpDbContext FreshDb(TestHost host)
        => new(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);

    /// <summary>سلسلة حقيقية كاملة تنتهي برصيد تام لعميل: دفعة × صنف × عدد كراتين بتاريخ استلام محدد.</summary>
    private static (int LotId, int OrderId, int PlanId) BuildFgChain(
        TestHost host, int cust, int rawProductId, int finishedProductId, int cartons, string day, string tag)
    {
        host.LoginAsAdmin(); // previous FGR helper switches identity to warehouse
        var db = host.Get<DatesErpDbContext>();
        double weight = db.Products.AsNoTracking().Single(p => p.Id == finishedProductId).CartonWeightKg;
        double kg = cartons * weight;

        // رصيد الخام المساعد يكفي كل السلاسل
        int whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        foreach (int mat in new[] { 1, 2 })
            if (!db.StockBalances.Any(b => b.WarehouseId == whAux && b.MaterialId == mat && b.QtyKg > kg))
                db.StockBalances.Add(new Core.Domain.Entities.StockBalance { WarehouseId = whAux, MaterialId = mat, QtyKg = 500000 });
        db.SaveChanges();

        // استلام خام فعلي ينشئ الدفعة
        var rcv = Svc<IReceivingService>(host);
        double rawKg = kg + 5000;
        var s = rcv.SaveShipment(cust, day, day, new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = rawProductId, PackagingTypeId = 2,
                  PackageCount = (int)(rawKg / 20), UnitWeightKg = 20, QtyKg = rawKg, ReceiptUnit = "سلة" } }, null, $"DP-{tag}");
        Assert.True(s.Ok, s.Message);
        Assert.True(rcv.ApproveShipment(s.Id).Ok);
        int lot;
        using (var rd = FreshDb(host)) lot = rd.Lots.OrderByDescending(l => l.Id).First().Id;

        // خطة ← أمر ← إقفال يوم الإنتاج
        var planning = Svc<IPlanningService>(host);
        var p = planning.SavePlan($"خطة تسليم {tag}", "Daily", day, day, 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lot, CustomerId = cust, ProductId = finishedProductId,
                  PlannedQtyKg = kg, PlannedCartons = cartons, ScheduledDate = day,
                  SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 } });
        Assert.True(p.Ok, p.Message);
        Assert.True(planning.ApprovePlan(p.Id).Ok);
        var orders = Svc<IProductionOrderService>(host);
        host.SetBusinessDate(day);
        var o = orders.SaveOrder("FromPlan", p.Id, cust, day, 1, 1, TestOrderFixture.Items(host, p.Id, cust, day));
        Assert.True(orders.ApproveOrder(o.Id).Ok);
        var exec = Svc<IExecutionService>(host);
        var close = exec.CloseProductionDay(o.Id, kg, cartons, 0, 0, 0, false, new List<DowntimeDto>(), true);
        Assert.True(close.Ok, close.Message);

        // جودة نهائية معتمدة: كل الكمية مقبولة
        int qcId, exeId;
        using (var rd = FreshDb(host))
        {
            qcId = rd.QualityChecks.Where(c => c.OrderId == o.Id).OrderBy(c => c.Id).Select(c => c.Id).First();
            exeId = rd.ProductionExecutions.First(e => e.OrderId == o.Id).Id;
        }
        var quality = Svc<IQualityService>(host);
        var chk = quality.SaveCheck(o.Id, exeId, day, "نهائي", new List<QualityItemDto>
        { new() { ProductId = finishedProductId, LotId = lot, CheckedQtyKg = kg, AcceptedQtyKg = kg, RejectedQtyKg = 0,
                  CheckedCartons = cartons, AcceptedCartons = cartons, RejectedCartons = 0 } });
        Assert.True(chk.Ok, chk.Message);
        Assert.True(quality.ApproveCheck(qcId).Ok);

        // استلام التام ← رصيد العميل بمخزن التام
        var fg = Svc<IFinishedGoodsService>(host);
        var fr = TestProductionDocumentFlow.SaveReceiptFromActual(host, o.Id, qcId, day, new List<FinishedGoodsItemDto>
        { new() { ProductId = finishedProductId, LotId = lot, PackagingTypeId = null, PackageCount = cartons, NetWeightKg = kg } });
        Assert.True(fr.Ok, fr.Message);
        Assert.True(fg.Issue(fr.Id).Ok); // draft receipt cannot post straight to stock
        var rec = fg.Receive(fr.Id, null);
        Assert.True(rec.Ok, rec.Message);
        return (lot, o.Id, p.Id);
    }

    // ── §1/§2/§6: قائمة الالتقاط — حقول كاملة، ترتيب FIFO، ومدة بقاء تُحسب لحظياً ──
    [Fact]
    public void Pick_01_PickList_Fields_FifoOrder_StorageDaysLive()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int cust = FreshDb(host).Customers.OrderBy(c => c.Id).First().Id;
        var (lotA, orderA, planA) = BuildFgChain(host, cust, 1, 3, 300, "2026-09-01", "A"); // استلام أقدم
        var (lotB, _, _) = BuildFgChain(host, cust, 1, 3, 400, "2026-09-05", "B");          // استلام أحدث

        var pick = Svc<DeliveryPickService>(host);
        var rows = pick.GetPickList(cust, new DateTime(2026, 9, 10));
        Assert.Equal(2, rows.Count);

        // §2 — FIFO واضح: الأقدم استلاماً بالتام في الأعلى
        Assert.Equal(lotA, rows[0].LotId);
        Assert.Equal("01/09/2026", rows[0].FgReceiptDate);
        Assert.Equal("05/09/2026", rows[1].FgReceiptDate);

        // §1 — كل حقل من حقول الصف له قيمة حقيقية من سلسلة التتبع
        var r0 = rows[0];
        Assert.Equal("002-001", r0.ProductCode);
        Assert.Equal(300, r0.AvailableCartons);
        Assert.Equal(2250, r0.AvailableKg, 1);          // 300 × 7.5 من بطاقة الصنف — لا وزن ثابت مقنّع
        Assert.Equal(7.5, r0.CartonWeightKg, 2);
        Assert.NotEqual("—", r0.PlanNo);                 // رقم الخطة المصدر
        Assert.NotEqual("—", r0.OrderNo);                // رقم أمر الإنتاج
        Assert.NotEqual("—", r0.ProductionDate);         // تاريخ الإنتاج من التنفيذ
        Assert.NotEqual("—", r0.LotCode);
        Assert.NotEqual("بانتظار الفحص", r0.GradeAr);    // الصفة من فحص نهائي معتمد

        // §6 — مدة البقاء = اليوم − تاريخ الاستلام، تُحسب عند الطلب ولا تُخزَّن
        Assert.Equal(9, r0.StorageDays);                 // 10/09 − 01/09
        Assert.Equal(5, rows[1].StorageDays);            // 10/09 − 05/09
        var later = pick.GetPickList(cust, new DateTime(2026, 9, 20));
        Assert.Equal(19, later[0].StorageDays);          // نفس السطر، حساب جديد — لا رقم ثابت

        // §11 — سلسلة الرابط كاملة: الدفعة ← أمر ← خطة
        using (var rd2 = FreshDb(host))
        {
            string planNo = rd2.ProductionPlans.Single(x => x.Id == planA).DocumentNumber;
            string orderNo = rd2.ProductionOrders.Single(x => x.Id == orderA).DocumentNumber;
            Assert.Equal(planNo, r0.PlanNo);
            Assert.Equal(orderNo, r0.OrderNo);
        }
    }

    // ── §5: طلب 500 وصنف موزع 300 (01/09) + 400 (05/09) ← تقسيم FIFO 300 + 200 بلا دمج ──
    [Fact]
    public void Pick_02_Fifo_Split_500_Over_300_400_KeepsLotTrace()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int cust = FreshDb(host).Customers.OrderBy(c => c.Id).First().Id;
        var (lotA, _, _) = BuildFgChain(host, cust, 1, 3, 300, "2026-09-01", "A");
        var (lotB, _, _) = BuildFgChain(host, cust, 1, 3, 400, "2026-09-05", "B");

        var items = Svc<DeliveryPickService>(host).AllocateFifo(cust, new List<(int, int)> { (3, 500) }, new DateTime(2026, 9, 10));
        Assert.Equal(2, items.Count);
        // الأقدم أولاً بالكامل ثم الأحدث بالباقي — كل سطر بدفعته (لا دمج ولا فقدان تتبع)
        Assert.Equal(lotA, items[0].LotId); Assert.Equal(300, items[0].PackageCount); Assert.Equal(2250, items[0].QtyKg, 1);
        Assert.Equal(lotB, items[1].LotId); Assert.Equal(200, items[1].PackageCount); Assert.Equal(1500, items[1].QtyKg, 1);
        Assert.NotEqual(items[0].LotId, items[1].LotId);
    }

    // ── §4/§9: تجاوز الرصيد والكميات غير الصالحة تُرفض فوراً برسالة الأمر حرفياً ──
    [Fact]
    public void Pick_03_OverBalance_And_InvalidQty_Rejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int cust = FreshDb(host).Customers.OrderBy(c => c.Id).First().Id;
        BuildFgChain(host, cust, 1, 3, 300, "2026-09-01", "A");
        BuildFgChain(host, cust, 1, 3, 400, "2026-09-05", "B"); // المتاح الكلي 700
        var pick = Svc<DeliveryPickService>(host);
        var asOf = new DateTime(2026, 9, 10);

        // المتاح الكلي 700 — أي طلب فوقه (701 = المتاح+1، 5000) يُرفض فوراً برسالة الأمر حرفياً
        var ex501 = Assert.Throws<DomainException>(() => pick.AllocateFifo(cust, new List<(int, int)> { (3, 701) }, asOf));
        Assert.Contains("الكمية المطلوبة أكبر من الرصيد المتاح", ex501.Message);
        var exBig = Assert.Throws<DomainException>(() => pick.AllocateFifo(cust, new List<(int, int)> { (3, 5000) }, asOf));
        Assert.Contains("الكمية المطلوبة أكبر من الرصيد المتاح", exBig.Message);
        // 0 وسالب ← رفض (§4: الكمية يجب أن تكون أكبر من صفر)
        Assert.Throws<DomainException>(() => pick.AllocateFifo(cust, new List<(int, int)> { (3, 0) }, asOf));
        Assert.Throws<DomainException>(() => pick.AllocateFifo(cust, new List<(int, int)> { (3, -1) }, asOf));
        // 250 و500 ضمن المتاح ← مقبولة
        Assert.Single(pick.AllocateFifo(cust, new List<(int, int)> { (3, 250) }, asOf));
        Assert.Equal(2, pick.AllocateFifo(cust, new List<(int, int)> { (3, 500) }, asOf).Count);
    }

    // ── §14 حرفياً: 500/300/150 ← تسليم 250/300/لا شيء ← الباقي 250/0/150 والتسليم التالي يرى أرصدة جديدة ──
    [Fact]
    public void Pick_04_DirectiveScenario_500_300_150_Deliver_250_300_None()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int cust = FreshDb(host).Customers.OrderBy(c => c.Id).First().Id;
        var (lotA, _, _) = BuildFgChain(host, cust, 1, 3, 500, "2026-09-01", "A"); // صنف 1: 500 كرتون
        var (lotB, _, _) = BuildFgChain(host, cust, 2, 4, 300, "2026-09-02", "B"); // صنف 2: 300 كرتون
        var (lotC, _, _) = BuildFgChain(host, cust, 2, 4, 150, "2026-09-04", "C"); // صنف 3: 150 كرتوناً

        var pick = Svc<DeliveryPickService>(host);
        var asOf = new DateTime(2026, 9, 10);
        var before = pick.GetPickList(cust, asOf);
        Assert.Equal(500, before.Single(r => r.LotId == lotA).AvailableCartons);
        Assert.Equal(300, before.Single(r => r.LotId == lotB).AvailableCartons);
        Assert.Equal(150, before.Single(r => r.LotId == lotC).AvailableCartons);

        // المستخدم يختار: 250 من الصنف الأول، 300 كامل الصنف الثاني، ويتخطى الثالث
        var items = pick.AllocateFifo(cust, new List<(int, int)> { (3, 250), (4, 300) }, asOf);
        var dlv = Svc<ICustomerDeliveryService>(host);
        var d = dlv.Save(cust, "10/09/2026", null, items);
        Assert.True(d.Ok, d.Message);
        var ap = dlv.Approve(d.Id);
        Assert.True(ap.Ok, ap.Message);

        // الأرصدة بعد الاعتماد فوراً: 250 / 0 (اختفى) / 150
        var after = pick.GetPickList(cust, asOf);
        Assert.Equal(250, after.Single(r => r.LotId == lotA).AvailableCartons);
        Assert.DoesNotContain(after, r => r.LotId == lotB);                       // صفر ← لا يظهر
        Assert.Equal(150, after.Single(r => r.LotId == lotC).AvailableCartons);

        // §11 — المتبقي من تسليم جزئي يبقى مربوطاً بخطته وأمره ودفعته
        var remA = after.Single(r => r.LotId == lotA);
        Assert.Equal(before.Single(r => r.LotId == lotA).PlanNo, remA.PlanNo);
        Assert.Equal(before.Single(r => r.LotId == lotA).OrderNo, remA.OrderNo);
        Assert.Equal(before.Single(r => r.LotId == lotA).LotCode, remA.LotCode);

        // §14 — مستند تسليم جديد لنفس العميل يبدأ من الأرصدة الجديدة (250 متاح لا 500)
        var second = pick.AllocateFifo(cust, new List<(int, int)> { (3, 250) }, asOf);
        Assert.Equal(lotA, second.Single().LotId);
        Assert.Equal(250, second.Single().PackageCount);
        var exOver = Assert.Throws<DomainException>(() => pick.AllocateFifo(cust, new List<(int, int)> { (3, 251) }, asOf));
        Assert.Contains("الكمية المطلوبة أكبر من الرصيد المتاح", exOver.Message);

        // §8 — تقرير التسليم يعكس المستند المعتمد بالكرتون الرسمي
        var rep = Svc<IReportService>(host).Run("delivery", new Dictionary<string, string>());
        Assert.Equal(UnitsPolicy.FinishedQtyHeader, rep.Columns[3]);
        Assert.Equal(550, rep.Rows.Sum(r => Convert.ToDouble(r[3])), 1); // 250 + 300
    }

    // ── §10/§12: عزل العملاء مطلق، والنواتج الجانبية خارج قائمة التسليم ──
    [Fact]
    public void Pick_05_CustomerIsolation_ByProductsExcluded()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = FreshDb(host);
        int cust1 = db.Customers.OrderBy(c => c.Id).First().Id;
        var other = host.Get<MasterDataService>().SaveCustomer(null, "PICK-C2", "عميل مستقل", "جملة", "7000", null, true);
        Assert.True(other.Ok, other.Message);
        int cust2 = other.Id;
        var (lotA, _, _) = BuildFgChain(host, cust1, 1, 3, 300, "2026-09-01", "A");

        // ناتج جانبي برصيد في مخزن التام لنفس العميل — §12: لا يدخل قائمة التسليم (كجم دائماً)
        using (var wd = FreshDb(host))
        {
            int whFg = wd.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
            int byId = wd.Products.First(p => p.ItemType == "ByProduct").Id;
            wd.StockBalances.Add(new Core.Domain.Entities.StockBalance
            { WarehouseId = whFg, CustomerId = cust1, ProductId = byId, QtyKg = 1000, PackageCount = 0 });
            wd.SaveChanges();
        }

        var pick = Svc<DeliveryPickService>(host);
        var rows1 = pick.GetPickList(cust1, new DateTime(2026, 9, 10));
        Assert.Single(rows1);                                  // الناتج الجانبي مستبعد
        Assert.Equal(lotA, rows1[0].LotId);
        Assert.Equal(UnitsPolicy.FinishedOfficialUnit, rows1[0].CartonWeightKg > 0 ? UnitsPolicy.FinishedOfficialUnit : ""); // الوزن من البطاقة والوحدة الرسمية كرتون

        // §10 — العميل الثاني لا يرى أي شيء من رصيد الأول
        var rows2 = pick.GetPickList(cust2, new DateTime(2026, 9, 10));
        Assert.DoesNotContain(rows2, r => r.LotId == lotA);
        Assert.Empty(rows2.Where(r => r.ProductId == 3));
    }

    // ── §9 عند الاعتماد: إعادة فحص الرصيد لحظة الاعتماد (تزامن) ──
    [Fact]
    public void Pick_06_Approve_RechecksBalance_Concurrency()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int cust = FreshDb(host).Customers.OrderBy(c => c.Id).First().Id;
        var (lotA, _, _) = BuildFgChain(host, cust, 1, 3, 300, "2026-09-01", "A");
        var pick = Svc<DeliveryPickService>(host);
        var dlv = Svc<ICustomerDeliveryService>(host);

        // مسودتان كل واحدة 250 — الحفظ يقبلهما (الرصيد 300 يكفي واحدة فقط عند الاعتماد)
        var items = pick.AllocateFifo(cust, new List<(int, int)> { (3, 250) }, new DateTime(2026, 9, 10));
        var d1 = dlv.Save(cust, "10/09/2026", null, items); Assert.True(d1.Ok, d1.Message);
        var d2 = dlv.Save(cust, "10/09/2026", null, items); Assert.True(d2.Ok, d2.Message);

        Assert.True(dlv.Approve(d1.Id).Ok);                    // الأولى تعتمد وتخصم 250 ← المتبقي 50
        var second = dlv.Approve(d2.Id);                       // الثانية تُرفض عند الاعتماد: 250 > 50
        Assert.False(second.Ok);
        Assert.Contains("رصيد العميل", second.Message);        // INSUFFICIENT_CUSTOMER_BALANCE — إعادة فحص لحظة الاعتماد

        // §13 — المسودة تُعدَّل وتُحذف؛ المعتمد مقفل
        var upd = dlv.Update(d2.Id, cust, "11/09/2026", null,
            pick.AllocateFifo(cust, new List<(int, int)> { (3, 50) }, new DateTime(2026, 9, 11)));
        Assert.True(upd.Ok, upd.Message);                      // تعديل المسودة مسموح
        Assert.True(dlv.Approve(d2.Id).Ok);                    // الآن 50 = المتبقي بالضبط
        Assert.False(dlv.Update(d2.Id, cust, "11/09/2026", null, items).Ok);   // المعتمد مقفل
        host.LoginAsAdmin(); // deletion and reversal require explicit supervisor permission
        Assert.False(dlv.DeleteDraft(d2.Id).Ok);                               // لا حذف بعد الاعتماد

        // الإلغاء يعكس الأثر: الرصيد يعود 50
        Assert.True(dlv.Unapprove(d2.Id).Ok);
        var after = pick.GetPickList(cust, new DateTime(2026, 9, 12));
        Assert.Equal(50, after.Single(r => r.LotId == lotA).AvailableCartons);
        using var rd = FreshDb(host);
        int whFg = rd.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
        Assert.Equal(50, rd.StockBalances.Single(b => b.WarehouseId == whFg && b.LotId == lotA).PackageCount);
    }

    // ── §3: تخطي صنف完全 — الأصناف غير المطلوبة لا تدخل المستند ──
    [Fact]
    public void Pick_07_SkippedItems_NeverEnterDocument()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int cust = FreshDb(host).Customers.OrderBy(c => c.Id).First().Id;
        var (lotA, _, _) = BuildFgChain(host, cust, 1, 3, 300, "2026-09-01", "A");
        var (lotB, _, _) = BuildFgChain(host, cust, 2, 4, 200, "2026-09-03", "B");
        var pick = Svc<DeliveryPickService>(host);
        var dlv = Svc<ICustomerDeliveryService>(host);

        // طلب الصنف الأول فقط — الثاني مُتخطى
        var items = pick.AllocateFifo(cust, new List<(int, int)> { (3, 100) }, new DateTime(2026, 9, 10));
        Assert.All(items, i => Assert.Equal(3, i.ProductId));
        var d = dlv.Save(cust, "10/09/2026", null, items);
        Assert.True(d.Ok, d.Message);
        Assert.True(dlv.Approve(d.Id).Ok);

        using var rd = FreshDb(host);
        var docItems = rd.CustomerDeliveryItems.Where(i => i.DeliveryId == d.Id).ToList();
        Assert.All(docItems, i => Assert.Equal(3, i.ProductId));   // الصنف المتخطى absent
        int whFg = rd.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
        Assert.Equal(200, rd.StockBalances.Single(b => b.WarehouseId == whFg && b.LotId == lotB).PackageCount); // لم يُمس
        Assert.Equal(200, rd.StockBalances.Single(b => b.WarehouseId == whFg && b.LotId == lotA).PackageCount); // 300 − 100
    }

    // ── التقرير التحليلي للتسليمات: بند/دفعة/خطة/أمر/صفة + مجاميع عميل وإجمالي + فلاتر الفترة والعميل ──
    [Fact]
    public void Pick_08_DeliveryAnalysisReport_LinesTraceTotalsFilters()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int cust = FreshDb(host).Customers.OrderBy(c => c.Id).First().Id;
        var (lotA, orderA, planA) = BuildFgChain(host, cust, 1, 3, 500, "2026-09-01", "A");
        var (lotB, _, _) = BuildFgChain(host, cust, 2, 4, 300, "2026-09-02", "B");
        var pick = Svc<DeliveryPickService>(host);
        var dlv = Svc<ICustomerDeliveryService>(host);
        var items = pick.AllocateFifo(cust, new List<(int, int)> { (3, 250), (4, 300) }, new DateTime(2026, 9, 10));
        var d = dlv.Save(cust, "10/09/2026", null, items);
        Assert.True(d.Ok, d.Message);
        Assert.True(dlv.Approve(d.Id).Ok);

        var rep = Svc<IReportService>(host).Run("delivery_analysis", new Dictionary<string, string>());
        Assert.Equal("رقم الخطة", rep.Columns[8]);
        Assert.Equal("رقم الأمر", rep.Columns[9]);
        Assert.Equal(UnitsPolicy.FinishedQtyHeader, rep.Columns[10]);   // الكرتون هو الرسمي (§القاعدة الذهبية)
        Assert.Equal(UnitsPolicy.FinishedWeightHeader, rep.Columns[11]);

        // صف لكل بند مسلَّم (صنف×دفعة) بتتبعه الكامل
        var lines = rep.Rows.Where(x => !Convert.ToString(x[0]).StartsWith("إجمالي") && !Convert.ToString(x[0]).Contains("الإجمالي")).ToList();
        Assert.Equal(2, lines.Count);
        var l3 = lines.Single(x => Convert.ToString(x[4]) == "002-001");
        Assert.Equal("سليم (مطابق)", l3[5]);                            // الصفة من فحص نهائي معتمد
        Assert.NotEqual("—", l3[8]); Assert.NotEqual("—", l3[9]);        // خطة وأمر حقيقيان
        Assert.Equal(250.0, Convert.ToDouble(l3[10]), 1);                // كرتون رسمي
        Assert.Equal(1875.0, Convert.ToDouble(l3[11]), 1);               // 250 × 7.5
        var l4 = lines.Single(x => Convert.ToString(x[4]) == "002-002");
        Assert.Equal(300.0, Convert.ToDouble(l4[10]), 1);
        Assert.Equal(600.0, Convert.ToDouble(l4[11]), 1);                // 300 × 2

        // مجاميع: إجمالي العميل ثم الإجمالي العام (كرتوناً وكجم)
        var custTotal = rep.Rows.Single(x => Convert.ToString(x[0]).StartsWith("إجمالي تسليمات العميل"));
        Assert.Equal(550.0, Convert.ToDouble(custTotal[10]), 1);
        Assert.Equal(2475.0, Convert.ToDouble(custTotal[11]), 1);
        var grand = rep.Rows.Single(x => Convert.ToString(x[0]).StartsWith("الإجمالي العام"));
        Assert.Equal(550.0, Convert.ToDouble(grand[10]), 1);
        Assert.Equal("1 / 0", rep.Summary["معتمد / مسودة"]);

        // السلسلة محفوظة: رقم أمر البند هو أمر الإنتاج الحقيقي ورقم الخطة خطتها
        using (var rd = FreshDb(host))
        {
            Assert.Equal(rd.ProductionOrders.Single(o => o.Id == orderA).DocumentNumber, l3[9]);
            Assert.Equal(rd.ProductionPlans.Single(pp => pp.Id == planA).DocumentNumber, l3[8]);
            Assert.Equal(rd.Lots.Single(l => l.Id == lotA).LotCode, l3[6]);
            Assert.Equal(rd.Lots.Single(l => l.Id == lotB).LotCode, l4[6]);
        }

        // فلتر الفترة: يوم التسليم يظهر، وما بعده لا يظهر
        var inRange = Svc<IReportService>(host).Run("delivery_analysis",
            new Dictionary<string, string> { ["from"] = "2026-09-10", ["to"] = "2026-09-10" });
        Assert.Equal(2, inRange.Rows.Count(x => !Convert.ToString(x[0]).Contains("إجمالي") && !Convert.ToString(x[0]).Contains("الإجمالي")));
        var outRange = Svc<IReportService>(host).Run("delivery_analysis",
            new Dictionary<string, string> { ["from"] = "2026-09-11", ["to"] = "2026-09-30" });
        Assert.Empty(outRange.Rows.Where(x => !Convert.ToString(x[0]).Contains("إجمالي") && !Convert.ToString(x[0]).Contains("الإجمالي")));

        // المسودة تظهر ب حالتها ولا تُقرأ كنهائية: إجمالي «معتمد / مسودة» يتغير
        var draft = dlv.Save(cust, "12/09/2026", null, pick.AllocateFifo(cust, new List<(int, int)> { (3, 50) }, new DateTime(2026, 9, 12)));
        Assert.True(draft.Ok, draft.Message);
        var withDraft = Svc<IReportService>(host).Run("delivery_analysis", new Dictionary<string, string>());
        Assert.Equal("1 / 1", withDraft.Summary["معتمد / مسودة"]);
        Assert.Contains(withDraft.Rows, x => Convert.ToString(x[12]) == "مسودة");
    }
}
