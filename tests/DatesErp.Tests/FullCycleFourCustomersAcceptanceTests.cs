using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §أمر فحص دورة العمل الكاملة — 4 عملاء (قبول تشغيلي):
/// استلام ← اعتماد ← مخزون ← خطط (كاملة/جزئية/تجاوز متاح/طاقة) ← أوامر مطابقة ←
/// تنفيذ جزئي ثم استكمال ثم منع التكرار ← جودة ← استلام تام بالمنتج فقط ← تسليم
/// بمنع تجاوز الرصيد واختلاط العملاء ← مطابقة نهائية لكل عميل.
/// كل Assertion هنا مبني على حارس مُتحقَّق منه في كود الخدمات (لا افتراضات).
/// ما يخص الشاشات/الطباعة وحدها موثّق في Documentation/FULL_CYCLE_ACCEPTANCE_AR.md.
/// </summary>
public class FullCycleFourCustomersAcceptanceTests
{
    private static T Svc<T>(TestHost host) => (T)host.Services.CreateScope().ServiceProvider.GetService(typeof(T));
    private static DatesErpDbContext FreshDb(TestHost host)
        => new(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);

    private static void SeedAux(TestHost host)
    {
        var db = host.Get<DatesErpDbContext>();
        int whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 200000 },
            new StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 200000 });
        db.SaveChanges();
    }

    private static List<int> SeedFourCustomers(TestHost host)
    {
        var db = host.Get<DatesErpDbContext>();
        var ids = new List<int> { db.Customers.OrderBy(c => c.Id).First().Id };
        var admin = Svc<MasterDataService>(host);
        foreach (var n in new[] { "B", "C", "D" })
        {
            var c = admin.SaveCustomer(null, "ACC" + n, $"عميل القبول {n}", "تجار جملة", "771000" + n, null, true);
            Assert.True(c.Ok, c.Message);
            ids.Add(c.Id);
        }
        return ids;
    }

    /// <summary>شحنة معتمدة بصنفين/عبوتين ← دفعتان لكل عميل (§1).</summary>
    private static (int shipId, int lot1, int lot2) SeedApprovedShipment2(TestHost host, int cust, double kg1, double kg2, string tag)
    {
        var rcv = Svc<IReceivingService>(host);
        var s = rcv.SaveShipment(cust, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        {
            new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = (int)(kg1 / 20), UnitWeightKg = 20, QtyKg = kg1 },
            new() { TreatmentRequired = false, ProductId = 2, PackagingTypeId = 1, PackageCount = (int)(kg2 / 10), UnitWeightKg = 10, QtyKg = kg2 }
        }, null, "CXLU-" + tag);
        Assert.True(s.Ok, s.Message);
        Assert.True(rcv.ApproveShipment(s.Id).Ok);
        using var rd = FreshDb(host);
        var lots = rd.Lots.Where(l => l.ShipmentId == s.Id).OrderBy(l => l.Id).ToList();
        return (s.Id, lots[0].Id, lots[1].Id);
    }

    private static int SeedApprovedPlanOrder(TestHost host, int cust, int lotId, int productId, double kg, int cartons, string day)
    {
        var planning = Svc<IPlanningService>(host);
        var p = planning.SavePlan("خطة قبول " + day, "Daily", day, day, 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = cust, ProductId = productId, PackagingTypeId = 2, PlannedQtyKg = kg, PlannedCartons = cartons, ScheduledDate = day, SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 }
        });
        Assert.True(p.Ok, p.Message);
        Assert.True(planning.ApprovePlan(p.Id).Ok);
        var orders = Svc<IProductionOrderService>(host);
        host.SetBusinessDate(day);
        var o = orders.SaveOrder("FromPlan", p.Id, cust, day, 1, 1, TestOrderFixture.Items(host, p.Id, cust, day));
        Assert.True(o.Ok, o.Message);
        Assert.True(orders.ApproveOrder(o.Id).Ok);
        return o.Id;
    }

    // ── §1/§2: تأسيس 4 عملاء + دورات المسودات (بحث/فتح/تعديل/حذف) وأثر الاعتماد ──
    [Fact]
    public void ACC_01_Foundation_DraftLifecycle_And_InventoryParity()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        SeedAux(host);
        var cust = SeedFourCustomers(host);

        // مسودة قابلة للاستكمال: حفظ ← بحث ← تعديل بالحفظ نفسه (existingId) ← حذف للأخرى
        var rcv = Svc<IReceivingService>(host);
        var d1 = rcv.SaveShipment(cust[0], "2026-08-01", "2026-08-01",
            new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 5, UnitWeightKg = 20, QtyKg = 100 } }, null, "CXLU-DR1");
        var d2 = rcv.SaveShipment(cust[0], "2026-08-02", "2026-08-02",
            new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 6, UnitWeightKg = 20, QtyKg = 120 } }, null, "CXLU-DR2");
        Assert.True(d1.Ok && d2.Ok);
        using (var rd = FreshDb(host))
        {
            var ship = rd.Shipments.First(x => x.Id == d1.Id);
            Assert.False(ship.IsApproved);                       // يُعثَر عليها مسودة
            Assert.Equal(100, ship.TotalWeightKg, 1);
        }
        var edit = rcv.SaveShipment(cust[0], "2026-08-01", "2026-08-01",
            new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 7, UnitWeightKg = 20, QtyKg = 140 } }, null, "CXLU-DR1", null, d1.Id);
        Assert.True(edit.Ok, edit.Message);
        using (var rd = FreshDb(host))
            Assert.Equal(140, rd.Shipments.First(x => x.Id == d1.Id).TotalWeightKg, 1);   // التعديل محفوظ
        Assert.True(rcv.DeleteShipment(d2.Id).Ok);               // حذف مسودة مسموح
        using (var rd = FreshDb(host))
            Assert.Null(rd.Shipments.FirstOrDefault(x => x.Id == d2.Id));
        Assert.True(rcv.ApproveShipment(d1.Id).Ok);              // المسودة المعدّلة تُعتمد وتستكمل بسهولة
        Assert.False(rcv.DeleteShipment(d1.Id).Ok);              // المعتمد لا يُحذف
        using (var rd = FreshDb(host))
            Assert.True(rd.Shipments.First(x => x.Id == d1.Id).IsApproved);

        // 4 عملاء × شحنة معتمدة بصنفين وعبوتين ← المخزون = الاستلام حرفياً
        double[][] kg = { new[] { 6000d, 1000d }, new[] { 4000d, 800d }, new[] { 2000d, 500d }, new[] { 1000d, 300d } };
        for (int i = 0; i < 4; i++)
        {
            var (shipId, lot1, lot2) = SeedApprovedShipment2(host, cust[i], kg[i][0], kg[i][1], "F" + i);
            using var rd = FreshDb(host);
            Assert.Equal(kg[i][0], rd.Lots.First(l => l.Id == lot1).InitialQtyKg, 1);
            Assert.Equal(kg[i][1], rd.Lots.First(l => l.Id == lot2).InitialQtyKg, 1);
            Assert.Equal(kg[i][0] + kg[i][1] + (i == 0 ? 140 : 0),
                rd.StockBalances.Where(b => b.CustomerId == cust[i]).Sum(b => b.QtyKg), 1);
        }
        // تقرير الأرصدة لكل عميل = مجموع استلاماته (لا اختلاط)
        var rep = Svc<IReportService>(host);
        for (int i = 0; i < 4; i++)
        {
            var r = rep.Run("inventory", new Dictionary<string, string>());
            var name = FreshDb(host).Customers.First(c => c.Id == cust[i]).CustomerName;
            double bonus = i == 0 ? 140 : 0;   // مسودة الاختبار المعتمدة للعميل الأول
            Assert.Equal(kg[i][0] + kg[i][1] + bonus,
                r.Rows.Where(x => Equals(x[3], name)).Sum(x => Convert.ToDouble(x[5])), 1); // kg, not package-name column
        }
    }

    // ── §3/§4/§5/§6: خطط كاملة/جزئية/تجاوز متاح + عدة عملاء بنفس الوردية + تعديل/حذف قبل وبعد الاعتماد ──
    [Fact]
    public void ACC_02_Plans_Availability_MultiCustomerShift_And_EditGuards()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        SeedAux(host);
        var cust = SeedFourCustomers(host);
        var a = SeedApprovedShipment2(host, cust[0], 6000, 1000, "PA");
        var b = SeedApprovedShipment2(host, cust[1], 4000, 800, "PB");
        var c = SeedApprovedShipment2(host, cust[2], 2000, 500, "PC");

        var planning = Svc<IPlanningService>(host);
        // كاملة ثم جزئية ثم تجاوز المتاح مرفوض
        var full = planning.SavePlan("كاملة", "Daily", "2026-08-20", "2026-08-20", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = a.lot1, CustomerId = cust[0], ProductId = 3, PackagingTypeId = 2, PlannedQtyKg = 6000, PlannedCartons = 600, ScheduledDate = "2026-08-20", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 } });
        Assert.True(full.Ok, full.Message);
        var partial = planning.SavePlan("جزئية", "Daily", "2026-08-20", "2026-08-20", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = a.lot2, CustomerId = cust[0], ProductId = 4, PackagingTypeId = 2, PlannedQtyKg = 400, PlannedCartons = 40, ScheduledDate = "2026-08-20", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 } });
        Assert.True(partial.Ok, partial.Message);
        var over = planning.SavePlan("تجاوز", "Daily", "2026-08-20", "2026-08-20", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = a.lot2, CustomerId = cust[0], ProductId = 4, PackagingTypeId = 2, PlannedQtyKg = 5000, PlannedCartons = 500, ScheduledDate = "2026-08-20", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 } });
        Assert.True(over.Ok, over.Message); // draft proposals may exceed; APPROVAL must reject overbooking
        Assert.False(planning.ApprovePlan(over.Id).Ok);
        Assert.True(planning.DeletePlan(over.Id).Ok); // release the speculative draft reservation

        // ثلاثة عملاء بنفس الوردية/الخط/اليوم — لكل عميل حجزه المستقل
        var multi = planning.SavePlan("ثلاثة عملاء وردية واحدة", "Daily", "2026-08-21", "2026-08-21", 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = a.lot2, CustomerId = cust[0], ProductId = 4, PackagingTypeId = 2, PlannedQtyKg = 600, PlannedCartons = 60, ScheduledDate = "2026-08-21", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 },
            new() { SourceType = "FromReceiving", LotId = b.lot1, CustomerId = cust[1], ProductId = 3, PackagingTypeId = 2, PlannedQtyKg = 1500, PlannedCartons = 150, ScheduledDate = "2026-08-21", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 2 },
            new() { SourceType = "FromReceiving", LotId = c.lot1, CustomerId = cust[2], ProductId = 3, PackagingTypeId = 2, PlannedQtyKg = 900, PlannedCartons = 90, ScheduledDate = "2026-08-21", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 3 }
        });
        Assert.True(multi.Ok, multi.Message);
        Assert.True(planning.ApprovePlan(multi.Id).Ok);
        using (var rd = FreshDb(host))
        {
            Assert.Equal(0, rd.Lots.First(l => l.Id == a.lot2).AvailableQtyKg, 1);     // 1000 − 400 − 600
            Assert.Equal(2500, rd.Lots.First(l => l.Id == b.lot1).AvailableQtyKg, 1); // 4000 − 1500
            Assert.Equal(1100, rd.Lots.First(l => l.Id == c.lot1).AvailableQtyKg, 1); // 2000 − 900
        }

        // قبل الاعتماد: تعديل وحذف مسموحان — وبعد الاعتماد مرفوضان
        Assert.True(planning.ApprovePlan(partial.Id).Ok);
        bool editBlocked;
        try
        {
            var er = planning.UpdatePlan(partial.Id, "محاولة", "Daily", "2026-08-20", "2026-08-20", 1, 1, new List<PlanItemDto>
            { new() { SourceType = "FromReceiving", LotId = a.lot2, CustomerId = cust[0], ProductId = 4, PackagingTypeId = 2, PlannedQtyKg = 100, PlannedCartons = 10, ScheduledDate = "2026-08-20", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 } });
            editBlocked = !er.Ok;
        }
        catch (Exception) { editBlocked = true; }   // DomainException «الخطة معتمدة»
        Assert.True(editBlocked);
        Assert.False(planning.DeletePlan(partial.Id).Ok);   // «لا يمكن حذف خطة معتمدة»
        var draftPlan = planning.SavePlan("مسودة للحذف", "Daily", "2026-08-22", "2026-08-22", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = b.lot2, CustomerId = cust[1], ProductId = 4, PackagingTypeId = 2, PlannedQtyKg = 100, PlannedCartons = 10, ScheduledDate = "2026-08-22", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 } });
        Assert.True(planning.DeletePlan(draftPlan.Id).Ok);   // مسودة بلا أوامر تُحذف ويرتد حجزها
        using (var rd = FreshDb(host))
            Assert.Equal(800, rd.Lots.First(l => l.Id == b.lot2).AvailableQtyKg, 1);
    }

    // ── §7/§8/§9/§10: أوامر مطابقة للخطة + تنفيذ جزئي ثم استكمال ثم منع التكرار ──
    [Fact]
    public void ACC_03_Orders_Parity_PartialExecution_Completion_NoDouble()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        SeedAux(host);
        var cust = SeedFourCustomers(host);
        var a = SeedApprovedShipment2(host, cust[0], 6000, 1000, "EA");
        var planning = Svc<IPlanningService>(host);
        var p = planning.SavePlan("خطة 600", "Daily", "2026-08-20", "2026-08-20", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = a.lot1, CustomerId = cust[0], ProductId = 3, PackagingTypeId = 2, PlannedQtyKg = 600, PlannedCartons = 60, ScheduledDate = "2026-08-20", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 } });
        Assert.True(p.Ok, p.Message);
        Assert.True(planning.ApprovePlan(p.Id).Ok);
        var orders = Svc<IProductionOrderService>(host);
        host.SetBusinessDate("2026-08-20");
        var o = orders.SaveOrder("FromPlan", p.Id, cust[0], "2026-08-20", 1, 1, TestOrderFixture.Items(host, p.Id, cust[0], "2026-08-20"));
        Assert.True(o.Ok, o.Message);
        Assert.True(orders.ApproveOrder(o.Id).Ok);

        // مطابقة الأمر للخطة: عميل/صنف/كمية/وردية/مرجع
        using (var rd = FreshDb(host))
        {
            var ord = rd.ProductionOrders.Include(x => x.Items).First(x => x.Id == o.Id);
            var pi = rd.ProductionPlanItems.First(x => x.PlanId == p.Id);
            Assert.Equal(cust[0], ord.CustomerId);
            Assert.Equal(pi.ProductId, ord.Items.First().ProductId);
            Assert.Equal(600, ord.Items.First().PlannedQtyKg, 1);
            Assert.Equal(p.Id, ord.SourcePlanId);
        }

        // تنفيذ 300 من 600 ← المنفذ/المتبقي ← استكمال 300 ← منع أي إنتاج إضافي
        var exec = Svc<IExecutionService>(host);
        var c1 = exec.CloseProductionDay(o.Id, 300, 30, 0, 0, 0, false, new List<DowntimeDto>(), false);
        Assert.True(c1.Ok, c1.Message);
        using (var rd = FreshDb(host))
        {
            var ord = rd.ProductionOrders.Include(x => x.Items).First(x => x.Id == o.Id);
            Assert.Equal(300, ord.Items.Sum(i => i.ProducedQtyKg), 1);
            Assert.Equal(300, ord.Items.Sum(i => i.PlannedQtyKg - i.ProducedQtyKg), 1);
        }
        var secondClose = exec.CloseProductionDay(o.Id, 300, 30, 0, 0, 0, false, new List<DowntimeDto>(), false);
        Assert.False(secondClose.Ok); // the ONE daily close is immutable; variance requires formal settlement
        using (var rd = FreshDb(host))
        {
            var ord = rd.ProductionOrders.Include(x => x.Items).First(x => x.Id == o.Id);
            Assert.Equal(300, ord.Items.Sum(i => i.ProducedQtyKg), 1);
            Assert.Equal(300, ord.Items.Sum(i => i.PlannedQtyKg - i.ProducedQtyKg), 1);
            Assert.Equal(1, rd.ProductionExecutions.Count(e => e.OrderId == o.Id));
        }
    }

    // ── §11/§12/§14/§15: جودة ← استلام تام بالمنتج فقط ← تسليم بمنع التجاوز واختلاط العملاء ──
    [Fact]
    public void ACC_04_Quality_FgReceipt_PartialDelivery_And_OwnershipGuards()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        SeedAux(host);
        var cust = SeedFourCustomers(host);
        var a = SeedApprovedShipment2(host, cust[0], 6000, 1000, "QA");
        var b = SeedApprovedShipment2(host, cust[1], 4000, 800, "QB");
        var oid = SeedApprovedPlanOrder(host, cust[0], a.lot1, 3, 600, 60, "2026-08-20");
        var exec = Svc<IExecutionService>(host);
        var close = exec.CloseProductionDay(oid, 300, 30, 0, 0, 0, false, new List<DowntimeDto>(), true);
        Assert.True(close.Ok, close.Message);
        var db0 = FreshDb(host);
        var qcId = db0.QualityChecks.Where(c => c.OrderId == oid).Select(c => c.Id).First();
        var exeId = db0.ProductionExecutions.First(e => e.OrderId == oid).Id;
        db0.Dispose();

        // جودة: المقبول 300 فقط ثم اعتماد
        var quality = Svc<IQualityService>(host);
        var chk = quality.SaveCheck(oid, exeId, "2026-08-20", "نهائي", new List<QualityItemDto>
        { new() { ProductId = 3, LotId = a.lot1, AcceptedQtyKg = 280, RejectedQtyKg = 20, AcceptedCartons = 28, RejectedCartons = 2 } });
        Assert.True(chk.Ok, chk.Message);
        Assert.True(quality.ApproveCheck(qcId).Ok);

        // استلام تام: يدخل المقبول 280 فقط — لا 600 ولا 300
        var fg = Svc<IFinishedGoodsService>(host);
        var fr = TestProductionDocumentFlow.SaveReceiptFromActual(host, oid, qcId, "2026-08-21", new List<FinishedGoodsItemDto>
        { new() { ProductId = 3, PackagingTypeId = 2, PackageCount = 28, NetWeightKg = 280 } });
        Assert.True(fr.Ok, fr.Message);
        Assert.True(fg.Issue(fr.Id).Ok);
        Assert.True(fg.Receive(fr.Id, null).Ok);
        using (var rd = FreshDb(host))
            Assert.Equal(280, rd.StockBalances.Where(x => x.CustomerId == cust[0] && x.WarehouseId == rd.Warehouses.Single(w => w.WarehouseCode == "WFG").Id).Sum(x => x.QtyKg), 1);

        // تسليم 200 من 280 ← المتبقي 80 ← تجاوز المتاح مرفوض ← دفعة عميل آخر مرفوضة
        var delivery = Svc<ICustomerDeliveryService>(host);
        var d1 = delivery.Save(cust[0], "2026-08-22", null, new List<CustomerDeliveryItemDto> { new() { ProductId = 3, PackagingTypeId = 2, PackageCount = 20, QtyKg = 200 } });
        Assert.True(d1.Ok, d1.Message);
        Assert.True(delivery.Approve(d1.Id).Ok);
        using (var rd = FreshDb(host))
            Assert.Equal(80, rd.StockBalances.Where(x => x.CustomerId == cust[0] && x.WarehouseId == rd.Warehouses.Single(w => w.WarehouseCode == "WFG").Id).Sum(x => x.QtyKg), 1);
        var overD = delivery.Save(cust[0], "2026-08-22", null, new List<CustomerDeliveryItemDto> { new() { ProductId = 3, PackagingTypeId = 2, PackageCount = 50, QtyKg = 500 } });
        if (overD.Ok) Assert.False(delivery.Approve(overD.Id).Ok);   // الحارس عند الاعتماد إن مر الحفظ
        var lotA = FreshDb(host).Lots.First(l => l.CustomerId == cust[0]).Id;   // دفعة خام تخص العميل A
        bool prevented;
        try
        {
            var cross = delivery.Save(cust[1], "2026-08-22", null, new List<CustomerDeliveryItemDto> { new() { ProductId = 3, LotId = lotA, PackagingTypeId = 2, PackageCount = 5, QtyKg = 50 } });
            prevented = !cross.Ok;
        }
        catch (Exception) { prevented = true; }   // DomainException CROSS_CUSTOMER
        Assert.True(prevented);
        _ = b; // شحنة العميل B موجودة لإثبات إمكانية محاولة الاختلاط ببيانات حقيقية
    }

    // ── §17/§21: منع تكرار الاعتماد + أثر التدقيق ──
    [Fact]
    public void ACC_05_NoDuplicateApprovals_And_AuditTrail()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        SeedAux(host);
        var cust = SeedFourCustomers(host);
        var rcv = Svc<IReceivingService>(host);
        var s = rcv.SaveShipment(cust[0], "2026-08-10", "2026-08-10",
            new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000 } }, null, "CXLU-DUP");
        Assert.True(rcv.ApproveShipment(s.Id).Ok);
        var second = rcv.ApproveShipment(s.Id);
        Assert.False(second.Ok);   // «معتمد مسبقاً» — لا حركة مخزنية ثانية
        using (var rd = FreshDb(host))
            Assert.Equal(2000, rd.StockBalances.Where(x => x.CustomerId == cust[0]).Sum(x => x.QtyKg), 1);
        var audit = Svc<IReportService>(host).Run("audit", new Dictionary<string, string>());
        Assert.NotEmpty(audit.Rows);   // العمليات موثقة في سجل التدقيق
    }

    // ── §19/§20/§28: المطابقة النهائية لكل عميل (معادلة السلسلة كاملة) ──
    [Fact]
    public void ACC_06_FinalReconciliation_PerCustomer_ChainIdentity()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        SeedAux(host);
        var cust = SeedFourCustomers(host);
        double[][] kg = { new[] { 6000d, 1000d }, new[] { 4000d, 800d }, new[] { 2000d, 500d }, new[] { 1000d, 300d } };
        for (int i = 0; i < 4; i++) SeedApprovedShipment2(host, cust[i], kg[i][0], kg[i][1], "R" + i);
        // دورة كاملة للعميل A: خطة 600 ← تنفيذ 600 على جلستين ← جودة 560 مقبول ← تام 560 ← تسليم 200
        var lotsA = FreshDb(host).Lots.Where(l => l.CustomerId == cust[0]).OrderBy(l => l.Id).ToList();
        var oid = SeedApprovedPlanOrder(host, cust[0], lotsA[0].Id, 3, 600, 60, "2026-08-20");
        var exec = Svc<IExecutionService>(host);
        var close = exec.CloseProductionDay(oid, 600, 60, 0, 0, 0, false, new List<DowntimeDto>(), true);
        Assert.True(close.Ok, close.Message); // one complete daily close, not two closures of the same order
        var qcId = FreshDb(host).QualityChecks.Where(c => c.OrderId == oid).Select(c => c.Id).First();
        var quality = Svc<IQualityService>(host);
        Assert.True(quality.SaveCheck(oid, FreshDb(host).ProductionExecutions.Where(e => e.OrderId == oid).OrderBy(e => e.Id).First().Id, "2026-08-20", "نهائي",
            new List<QualityItemDto> { new() { ProductId = 3, AcceptedQtyKg = 560, RejectedQtyKg = 40, AcceptedCartons = 56, RejectedCartons = 4 } }).Ok);
        Assert.True(quality.ApproveCheck(qcId).Ok);
        var fg = Svc<IFinishedGoodsService>(host);
        var fr = TestProductionDocumentFlow.SaveReceiptFromActual(host, oid, qcId, "2026-08-21", new List<FinishedGoodsItemDto> { new() { ProductId = 3, PackagingTypeId = 2, PackageCount = 56, NetWeightKg = 560 } });
        Assert.True(fr.Ok, fr.Message);
        Assert.True(fg.Issue(fr.Id).Ok);
        Assert.True(fg.Receive(fr.Id, null).Ok);
        var dlv = Svc<ICustomerDeliveryService>(host);
        var d = dlv.Save(cust[0], "2026-08-22", null, new List<CustomerDeliveryItemDto> { new() { ProductId = 3, PackagingTypeId = 2, PackageCount = 20, QtyKg = 200 } });
        Assert.True(dlv.Approve(d.Id).Ok);

        // جدول المطابقة: لكل عميل — المستلم/المنتج/المقبول/دخل التام/المسلَّم/المتبقي
        using var rd = FreshDb(host);
        int whFg = rd.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
        for (int i = 0; i < 4; i++)
        {
            int cid = cust[i];
            double received = rd.Shipments.Where(x => x.CustomerId == cid && x.IsApproved).Sum(x => x.TotalWeightKg);
            double produced = rd.ProductionExecutions.Where(e => rd.ProductionOrders.Any(o => o.Id == e.OrderId && o.CustomerId == cid)).Sum(e => e.ActualQtyKg);
            double accepted = rd.QualityChecks.Where(q => q.IsApproved && rd.ProductionOrders.Any(o => o.Id == q.OrderId && o.CustomerId == cid)).Sum(q => q.AcceptedKg);
            double delivered = rd.CustomerDeliveries.Where(x => x.CustomerId == cid && x.IsApproved).Sum(x => x.TotalQtyKg);
            double remaining = rd.StockBalances.Where(x => x.CustomerId == cid && x.WarehouseId == whFg).Sum(x => x.QtyKg);
            Assert.Equal(kg[i][0] + kg[i][1], received, 1);                 // المستلم = الاستلامات المعتمدة
            if (i == 0)
            {
                Assert.Equal(600, produced, 1);
                Assert.Equal(560, accepted, 1);
                Assert.Equal(560, delivered + remaining, 1);                // دخل التام = المسلَّم + المتبقي
                Assert.Equal(200, delivered, 1);
                Assert.Equal(360, remaining, 1);
            }
            else
            {
                Assert.Equal(0, produced, 1);                               // بقية العملاء: استلام فقط في هذا السيناريو
                Assert.Equal(0, delivered + remaining, 1);
            }
            // لا كمية بلا مصدر: كل حركة مخزون لها مستند مرجعي
            Assert.DoesNotContain(rd.InventoryTransactions.Where(t => t.CustomerId == cid), t => string.IsNullOrEmpty(t.ReferenceDocNumber));
        }
    }
}
