using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B100 — «متاح العملاء» في «مهامي»: المنتج/المقبول/المسلَّم/القابل للتسليم لكل عميل
/// عبر كل الخطط المعتمدة + الفصل بين العملاء في مخزن التام + تفاصيل النافذة.
/// </summary>
public class B100AvailabilityTests
{
    public static string D(int offset) => (DateTime.Today.AddDays(offset)).ToString("yyyy-MM-dd");

    /// <summary>
    /// خطة يوم ماضٍ لعميلَين (دفعتان) ← أمر (بندان) ← إقفال كامل (800 كجم) مع إرسال للجودة.
    /// </summary>
    public static (int planId, int orderId, int lotA, int lotB, int custB, int qcId) SeedMultiCustomerClosed(TestHost host)
    {
        var db = host.Get<DatesErpDbContext>();
        int whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 8000 },
            new StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 8000 });
        db.SaveChanges();

        // §B102 (إصلاح بذرة B100): العميل الثاني يُنشأ أولاً وتُستلم شحنته باسمه —
        // كانت الشحنة الثانية باسم العميل 1 ثم تُخطط للثاني فيرفضها حارس الملكية (عن حق).
        var cust = host.Get<MasterDataService>().SaveCustomer(null, "B100-C2", "عميل ثانٍ", "جملة", "777", "-", true);
        Assert.True(cust.Ok, cust.Message);
        int custB = cust.Id;

        var rcv = host.Get<IReceivingService>();
        var s1 = rcv.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000 } });
        Assert.True(s1.Ok, s1.Message);
        rcv.ApproveShipment(s1.Id);
        int lotA = db.Lots.OrderBy(l => l.Id).Last().Id;
        var s2 = rcv.SaveShipment(custB, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000 } });
        Assert.True(s2.Ok, s2.Message);
        rcv.ApproveShipment(s2.Id);
        int lotB = db.Lots.OrderBy(l => l.Id).Last().Id;

        var planSvc = host.Get<IPlanningService>();
        string day = D(-2); // يوم ماضٍ — سيظهر «متعثر» حتى يكتمل التسليم
        var p = planSvc.SavePlan("خطة B100", "Daily", day, day, 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lotA, CustomerId = 1, ProductId = 3, PackagingTypeId = 1,
                    PlannedQtyKg = 500, PlannedCartons = 100, ScheduledDate = day, SuggestedShiftId = 1, SuggestedLineId = 1 },
            new() { SourceType = "FromReceiving", LotId = lotB, CustomerId = custB, ProductId = 3, PackagingTypeId = 1,
                    PlannedQtyKg = 300, PlannedCartons = 60, ScheduledDate = day, SuggestedShiftId = 1, SuggestedLineId = 1 }
        });
        Assert.True(p.Ok, p.Message);
        Assert.True(planSvc.ApprovePlan(p.Id).Ok);
        var planItems = db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == p.Id).OrderBy(i => i.Id).ToList();

        var orders = host.Get<IProductionOrderService>();
        // §B102 (إصلاح بذرة B100): أمر متعدد العملاء — الرأس بلا عميل والعملاء على البنود،
        // فحارس الملكية (الدفعة تخص عميلاً آخر) يمر، وتسليم B96 per-line يميز العميلين.
        var o = LegacyOrderFixture.ImportApproved(host, p.Id, null, day, 1, 1, new List<OrderItemDto>
        {
            new() { PlanItemId = planItems[0].Id, LotId = lotA, CustomerId = 1, ProductId = 3, PackagingTypeId = 1,
                    PlannedQtyKg = 500, PlannedCartons = 100 },
            new() { PlanItemId = planItems[1].Id, LotId = lotB, CustomerId = custB, ProductId = 3, PackagingTypeId = 1,
                    PlannedQtyKg = 300, PlannedCartons = 60 }
        });
        Assert.True(o.Ok, o.Message);
        Assert.True(db.ProductionOrders.Single(x => x.Id == o.Id).IsApproved); // Historical approval, not new issuance.

        var close = host.Get<IExecutionService>()
            .CloseProductionDay(o.Id, 800, 160, 0, 0, 0, false, new List<DowntimeDto>(), true);
        Assert.True(close.Ok, close.Message);
        int qcId = db.QualityChecks.Single(c => c.OrderId == o.Id).Id;
        return (p.Id, o.Id, lotA, lotB, custB, qcId);
    }

    /// <summary>السلسلة: اعتماد الفحص (كامل) ← أمر تسليم (العميلان) ← تحرير ← استلام كامل.</summary>
    internal static void Qc_Delivery_Receive(TestHost host, int orderId, int qcId, int lotA, int lotB, int custB,
        double qtyA, int ctnA, double qtyB, int ctnB, out int deliveryId)
    {
        var db = host.Get<DatesErpDbContext>();

        host.LoginAs("quality");
        var quality = host.Get<IQualityService>();
        var qc = db.QualityChecks.Include(c => c.Items).Single(c => c.Id == qcId);
        var sc = quality.SaveCheck(orderId, qc.ExecutionId, DateTime.Today.ToString("yyyy-MM-dd"), "نهائي",
            new List<QualityItemDto>
            {
                new() { ProductId = 3, LotId = lotA, AcceptedQtyKg = qtyA, RejectedQtyKg = 0, AcceptedCartons = ctnA, RejectedCartons = 0 },
                new() { ProductId = 3, LotId = lotB, AcceptedQtyKg = qtyB, RejectedQtyKg = 0, AcceptedCartons = ctnB, RejectedCartons = 0 }
            }, null, new QualityLabDto { Decision = "Passed" });
        Assert.True(sc.Ok, sc.Message);
        Assert.True(quality.ApproveCheck(qcId).Ok);

        host.LoginAs("production");
        var del = host.Get<IProductionDeliveryService>();
        var sd = del.CreateDeliveryFromActual(qc.ExecutionId.Value, DateTime.Today.ToString("yyyy-MM-dd"));
        Assert.True(sd.Ok, sd.Message);
        Assert.True(del.IssueDelivery(sd.Id).Ok);
        deliveryId = sd.Id;

        host.LoginAs("warehouse");
        var fg = host.Get<IFinishedGoodsService>();
        var delItems = db.ProductionDeliveryItems.AsNoTracking().Where(i => i.DeliveryId == sd.Id).OrderBy(i => i.Id).ToList();
        var sr = fg.SaveReceipt(orderId, null, DateTime.Today.ToString("yyyy-MM-dd"),
            delItems.Select(i => new FinishedGoodsItemDto
            {
                ProductId = i.ProductId, LotId = i.LotId, PackagingTypeId = i.PackagingTypeId,
                PackageCount = i.PackageCount, NetWeightKg = i.QtyKg, CustomerId = i.CustomerId, DeliveryItemId = i.Id
            }).ToList(), sd.Id);
        Assert.True(sr.Ok, sr.Message);
        Assert.True(fg.Issue(sr.Id).Ok);
        Assert.True(fg.Receive(sr.Id, null).Ok);
    }

    /// <summary>تسليم جزئي/كلي لعميل (الإداري — لا مستخدم بيع مبذوق).</summary>
    internal static void Deliver(TestHost host, int customerId, int orderId, int lotId, double qty, int ctn)
    {
        host.LoginAsAdmin();
        var cds = host.Get<ICustomerDeliveryService>();
        var r = cds.Save(customerId, DateTime.Today.ToString("yyyy-MM-dd"), orderId,
            new List<CustomerDeliveryItemDto>
            { new() { ProductId = 3, LotId = lotId, PackagingTypeId = 1, PackageCount = ctn, QtyKg = qty } });
        Assert.True(r.Ok, r.Message);
        var ap = cds.Approve(r.Id);
        Assert.True(ap.Ok, ap.Message);
    }

    // ── 1) اللوحة: قبل الفحص «منتج بانتظار الفحص» وبلا قابل للتسليم؛ بعد السلسلة: الأرقام الكاملة + التسلسل ──
    [Fact]
    public void Board_Customers_BeforeAndAfter_The_Chain()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, oid, lotA, lotB, custB, qcId) = SeedMultiCustomerClosed(host);

        host.LoginAs("production");
        // قبل الفحص: المنتج 800 لكن المقبول 0 ← لا شيء قابل للتسليم
        var b0 = host.Get<ITaskCenterService>().GetBoard();
        Assert.Equal(2, b0.Customers.Count);
        var c0 = b0.Customers.Single(c => c.CustomerId == 1);
        Assert.Equal(500, c0.ProducedKg, 1);
        Assert.Equal(0, c0.AcceptedKg, 1);
        Assert.Equal(0, c0.DeliverableKg, 1);
        Assert.Contains("في الفحص", c0.StatusAr);   // §B102: نص الخدمة الفعلي «متعثر — في الفحص»

        Qc_Delivery_Receive(host, oid, qcId, lotA, lotB, custB, 500, 100, 300, 60, out _);
        // تسليم جزئي للعميل 1: 250 من 500
        Deliver(host, 1, oid, lotA, 250, 50);

        var b1 = host.Get<ITaskCenterService>().GetBoard();
        var c1 = b1.Customers.Single(c => c.CustomerId == 1);
        Assert.Equal(500, c1.PlannedKg, 1);
        Assert.Equal(500, c1.ProducedKg, 1);
        Assert.Equal(500, c1.AcceptedKg, 1);
        Assert.Equal(250, c1.DeliveredKg, 1);
        Assert.Equal(250, c1.DeliverableKg, 1); // المقبولات المتبقية = ما يُحمَّل الآن
        Assert.True(c1.Overdue);               // يوم ماضٍ لم يكتمل

        var c2 = b1.Customers.Single(c => c.CustomerId == custB);
        Assert.Equal(300, c2.ProducedKg, 1);
        Assert.Equal(300, c2.AcceptedKg, 1);
        Assert.Equal(0, c2.DeliveredKg, 1);
        Assert.Equal(300, c2.DeliverableKg, 1);
        Assert.True(c2.Overdue);

        // التسلسل: المتعثر أولاً ثم الأعلى قابلية (custB 300 قبل العميل 1 بـ 250)
        Assert.Equal(custB, b1.Customers[0].CustomerId);
        Assert.Equal(1, b1.Customers[1].CustomerId);
    }

    // ── 2) تفاصيل النافذة: الأيام + الدفعات المتاحة الفعلية + سجل التسليم ──
    [Fact]
    public void Detail_Show_Days_LotStocks_And_DeliveryHistory()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, oid, lotA, lotB, custB, qcId) = SeedMultiCustomerClosed(host);
        Qc_Delivery_Receive(host, oid, qcId, lotA, lotB, custB, 500, 100, 300, 60, out _);
        Deliver(host, 1, oid, lotA, 250, 50);

        var svc = host.Get<ICustomerAvailabilityService>();
        var d = svc.GetCustomerAvailability(1);
        Assert.Equal(250, d.DeliverableKg, 1);
        Assert.True(d.Overdue);

        // بالأيام: يوم ماضٍ منتج كامل ومسلَّم جزئياً
        var day = Assert.Single(d.Days);
        Assert.Equal("مُنتَج — بانتظار التسليم 🟡", day.StatusAr);
        Assert.True(day.Overdue);

        // الدفعات المتاحة: دفعة العميل 1 فقط — متبقية 250 (استُلم 500 وُسُلِّم 250)
        var stock = Assert.Single(d.Stocks);
        Assert.Equal(lotCode(host, lotA), stock.LotCode);
        Assert.Equal(250, stock.QtyKg, 1);

        // سجل التسليم: عملية واحدة 250
        var del = Assert.Single(d.Deliveries);
        Assert.Equal(250, del.QtyKg, 1);
        Assert.Contains("مُعتمد", del.StatusAr);
    }

    private static string lotCode(TestHost host, int lotId)
        => host.Get<DatesErpDbContext>().Lots.AsNoTracking().Single(l => l.Id == lotId).LotCode;

    // ── 3) لا خلط عملاء: دفعة العميل الثاني لا تُسلَّم للأول (حارس الحفظ) ولا تختلط أرصدتهما ──
    [Fact]
    public void CrossCustomer_Guard_And_SeparateStocks()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, oid, lotA, lotB, custB, qcId) = SeedMultiCustomerClosed(host);
        Qc_Delivery_Receive(host, oid, qcId, lotA, lotB, custB, 500, 100, 300, 60, out _);

        // أرصدة التام مفصلة بالعميل والدفعة
        var db = host.Get<DatesErpDbContext>();
        int wfg = db.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
        Assert.Equal(500, db.StockBalances.Single(b => b.WarehouseId == wfg && b.ProductId == 3 && b.LotId == lotA && b.CustomerId == 1).QtyKg, 1);
        Assert.Equal(300, db.StockBalances.Single(b => b.WarehouseId == wfg && b.ProductId == 3 && b.LotId == lotB && b.CustomerId == custB).QtyKg, 1);
        // لا رصيد للعميل 1 من دفعة B ولا بالعكس
        Assert.False(db.StockBalances.Any(b => b.WarehouseId == wfg && b.LotId == lotB && b.CustomerId == 1));
        Assert.False(db.StockBalances.Any(b => b.WarehouseId == wfg && b.LotId == lotA && b.CustomerId == custB));

        // تسليم دفعة العميل الثاني للعميل الأول ← مرفوض عند الحفظ
        var cds = host.Get<ICustomerDeliveryService>();
        var bad = cds.Save(1, DateTime.Today.ToString("yyyy-MM-dd"), oid,
            new List<CustomerDeliveryItemDto>
            { new() { ProductId = 3, LotId = lotB, PackagingTypeId = 1, PackageCount = 10, QtyKg = 50 } });
        Assert.False(bad.Ok);
        Assert.Contains("عميل", bad.Message);
    }

    // ── 4) التنبيهات العميقة: أرقام سلاسل الجودة/الاستلام/التسليم ──
    [Fact]
    public void ManagementAlerts_Deep_Numbers()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, oid, lotA, lotB, custB, qcId) = SeedMultiCustomerClosed(host);
        Qc_Delivery_Receive_Partial(host, oid, qcId, lotA, lotB, custB, out int srId, out int delId);

        // تأخير تاريخ أمر التسليم ليظهر في تنبيه «تجاوز موعده»
        var db = host.Get<DatesErpDbContext>();
        var del = db.ProductionDeliveries.First(d => d.Id == delId);
        del.DeliveryDate = DateTime.Today.AddDays(-1);
        db.SaveChanges();

        host.LoginAsAdmin();
        var alerts = host.Get<ITaskCenterService>().GetBoard().Alerts;
        Assert.Contains(alerts, a => a.Contains("🏬") && a.Contains("1 أمر تسليم تجاوز موعده"));
        Assert.Contains(alerts, a => a.Contains("🟠") && a.Contains("سند استلام جزئي"));
        // مقبول 800 كامل ومسلَّم 0 ← جاهز للشحن (استلم المخزن 550 فقط لكن المقبول لا ينتظر الاستلام)
        Assert.Contains(alerts, a => a.Contains("📦") && a.Contains("800") && a.Contains("2 عميل"));
    }

    [Fact]
    public void ManagementAlerts_LateQC_When_CoolingExpired()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, oid, lotA, lotB, custB, qcId) = SeedMultiCustomerClosed(host);

        // محاكاة مضي مدة التبريد: الفحص ما زال «مقدم» وتاريخه المتوقع قد مضى
        var db = host.Get<DatesErpDbContext>();
        var qc = db.QualityChecks.First(c => c.Id == qcId);
        qc.ExpectedCheckDate = DateTime.Today.AddDays(-1);
        db.SaveChanges();

        var alerts = host.Get<ITaskCenterService>().GetBoard().Alerts;
        Assert.Contains(alerts, a => a.Contains("🧊") && a.Contains("فحص تجاوز تاريخه المتوقع"));
    }

    // ── 5) تقرير إقفال الخطة: المقبول (جودة) والمسلَّم (عميل) لكل عميل ──
    [Fact]
    public void Closure_Report_Accepted_And_Delivered_Per_Customer()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, oid, lotA, lotB, custB, qcId) = SeedMultiCustomerClosed(host);
        Qc_Delivery_Receive(host, oid, qcId, lotA, lotB, custB, 500, 100, 300, 60, out _);
        Deliver(host, 1, oid, lotA, 250, 50); // جزئي للعميل الأول

        var info = host.Get<IPlanClosureService>().GetInfo(planId);
        var rowA = info.Customers.Single(c => c.Name == host.Get<DatesErpDbContext>().Customers.Single(x => x.Id == 1).CustomerName);
        Assert.Equal(500, rowA.Planned, 1);
        Assert.Equal(500, rowA.Produced, 1);
        Assert.Equal(500, rowA.Accepted, 1);
        Assert.Equal(250, rowA.Delivered, 1);

        var db = host.Get<DatesErpDbContext>();
        var rowB = info.Customers.Single(c => c.Name == db.Customers.Single(x => x.Id == custB).CustomerName);
        Assert.Equal(300, rowB.Accepted, 1);
        Assert.Equal(0, rowB.Delivered, 1);
    }

    // ── 6) السلسلة تترك أثراً تدقيقياً لكل مستند (ما تعمل عليه فلاتر «سجل التدقيق») ──
    [Fact]
    public void AuditTrail_Covers_The_Chain_Documents()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, oid, lotA, lotB, custB, qcId) = SeedMultiCustomerClosed(host);
        Qc_Delivery_Receive(host, oid, qcId, lotA, lotB, custB, 500, 100, 300, 60, out int delId);
        Deliver(host, 1, oid, lotA, 250, 50);

        var db = host.Get<DatesErpDbContext>();
        string orderNo = db.ProductionOrders.AsNoTracking().Single(o => o.Id == oid).DocumentNumber;
        string qcNo = db.QualityChecks.AsNoTracking().Single(c => c.Id == qcId).DocumentNumber;
        string delNo = db.ProductionDeliveries.AsNoTracking().Single(d => d.Id == delId).DocumentNumber;
        string rcptNo = db.FinishedGoodsReceipts.AsNoTracking()
            .Where(r => r.DeliveryId == delId).Select(r => r.DocumentNumber).First();
        string cdNo = db.CustomerDeliveries.AsNoTracking().OrderByDescending(d => d.Id).Select(d => d.DocumentNumber).First();

        foreach (var doc in new[] { orderNo, qcNo, delNo, rcptNo, cdNo })
            Assert.True(db.AuditLogs.Any(a => a.DocumentNumber == doc), $"بلا أثر تدقيق للمستند {doc}");
    }

    /// <summary>سلسلة الفحص/التسليم/الاستلام باستلام جزئي (لإثارة تنبيه السند الجزئي).</summary>
    internal static void Qc_Delivery_Receive_Partial(TestHost host, int oid, int qcId, int lotA, int lotB, int custB,
        out int receiptId, out int deliveryId)
    {
        var db = host.Get<DatesErpDbContext>();
        host.LoginAs("quality");
        var quality = host.Get<IQualityService>();
        var qc = db.QualityChecks.Include(c => c.Items).Single(c => c.Id == qcId);
        var sc = quality.SaveCheck(oid, qc.ExecutionId, DateTime.Today.ToString("yyyy-MM-dd"), "نهائي",
            new List<QualityItemDto>
            {
                new() { ProductId = 3, LotId = lotA, AcceptedQtyKg = 500, RejectedQtyKg = 0, AcceptedCartons = 100, RejectedCartons = 0 },
                new() { ProductId = 3, LotId = lotB, AcceptedQtyKg = 300, RejectedQtyKg = 0, AcceptedCartons = 60, RejectedCartons = 0 }
            }, null, new QualityLabDto { Decision = "Passed" });
        Assert.True(sc.Ok, sc.Message);
        Assert.True(quality.ApproveCheck(qcId).Ok);

        host.LoginAs("production");
        var del = host.Get<IProductionDeliveryService>();
        var sd = del.CreateDeliveryFromActual(qc.ExecutionId.Value, DateTime.Today.ToString("yyyy-MM-dd"));
        Assert.True(sd.Ok, sd.Message);
        Assert.True(del.IssueDelivery(sd.Id).Ok);
        deliveryId = sd.Id;

        host.LoginAs("warehouse");
        var fg = host.Get<IFinishedGoodsService>();
        var delItems = db.ProductionDeliveryItems.AsNoTracking().Where(i => i.DeliveryId == sd.Id).OrderBy(i => i.Id).ToList();
        var sr = fg.SaveReceipt(oid, null, DateTime.Today.ToString("yyyy-MM-dd"),
            delItems.Select(i => new FinishedGoodsItemDto
            {
                ProductId = i.ProductId, LotId = i.LotId, PackagingTypeId = i.PackagingTypeId,
                PackageCount = i.PackageCount, NetWeightKg = i.QtyKg, CustomerId = i.CustomerId, DeliveryItemId = i.Id
            }).ToList(), sd.Id);
        Assert.True(sr.Ok, sr.Message);
        receiptId = sr.Id;
        Assert.True(fg.Issue(sr.Id).Ok);
        // استلام جزئي: 250 من 500 للعميل الأول + كامل 300 للثاني
        var items = db.FinishedGoodsReceiptItems.AsNoTracking().Where(i => i.ReceiptId == sr.Id).ToList();
        var itemA = items.Single(i => i.LotId == lotA);
        var itemB = items.Single(i => i.LotId == lotB);
        var recv = fg.Receive(sr.Id, new Dictionary<int, double> { [itemA.Id] = 250, [itemB.Id] = 300 });
        Assert.True(recv.Ok, recv.Message);
        Assert.Equal("Partial", db.FinishedGoodsReceipts.AsNoTracking().Single(r => r.Id == sr.Id).ReceiptStatus);
    }

    // ── 7) اكتمال تسليم العميلين يغادر «المتعثر» وتصبح البطاقات «مكتمل» ──
    [Fact]
    public void Full_Delivery_Completes_Customer_Cards()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, oid, lotA, lotB, custB, qcId) = SeedMultiCustomerClosed(host);
        Qc_Delivery_Receive(host, oid, qcId, lotA, lotB, custB, 500, 100, 300, 60, out _);

        host.LoginAs("production");
        Assert.All(host.Get<ITaskCenterService>().GetBoard().Customers, c => Assert.True(c.Overdue));

        Deliver(host, 1, oid, lotA, 500, 100);
        Deliver(host, custB, oid, lotB, 300, 60);

        var board = host.Get<ITaskCenterService>().GetBoard();
        Assert.Equal(2, board.Customers.Count); // إقفال خطة الإنتاج لا يحذف تاريخ اكتمال شحن العملاء من اللوحة
        Assert.All(board.Customers, c =>
        {
            Assert.False(c.Overdue);
            Assert.Contains("مكتمل", c.StatusAr);
            Assert.Equal(0, c.DeliverableKg, 1);
        });
        // رصيدهما في التام صفر الآن
        var db = host.Get<DatesErpDbContext>();
        int wfg = db.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
        Assert.False(db.StockBalances.Any(b => b.WarehouseId == wfg && b.CustomerId == 1 && b.QtyKg > 0.001));
        Assert.False(db.StockBalances.Any(b => b.WarehouseId == wfg && b.CustomerId == custB && b.QtyKg > 0.001));
    }
}
