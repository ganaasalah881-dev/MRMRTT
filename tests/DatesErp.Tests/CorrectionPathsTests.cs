using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §C1/§C2/§C3 — مسارات التصحيح بعد الاعتماد:
/// C3: إلغاء اعتماد الشحنة يبقي الأثر (قيد عكسي بدل الحذف) — دفعة ملغاة محفوظة وسبب بالتدقيق.
/// C2: تصحيح كمية/عميل سند معتمد بقيود فرق موثقة وحراسات استهلاك/مراجع، وبصلاحية حساسة.
/// C1: معالج تصحيح السلسلة — شجرة، فك مترابط بعكس البناء، مانع صريح عند وجود تنفيذ حقيقي.
/// الصلاحيات: العمليتان الحساستان لا تُمنحان افتراضياً لأي دور (المدير فقط يكتمل تلقائياً).
/// </summary>
public class CorrectionPathsTests
{
    private static readonly string D0 = DateTime.Today.ToString("yyyy-MM-dd");

    private static (TestHost host, int shipId, int lotId, string docNo) SeedApprovedShipment(double kg = 200, double unit = 20)
    {
        var host = new TestHost();
        host.LoginAsAdmin();
        var rcv = host.Get<IReceivingService>();
        var s = rcv.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        {
            new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 3, PackageCount = (int)(kg / unit), UnitWeightKg = unit, QtyKg = kg, ReceiptUnit = "سلة" }
        });
        Assert.True(s.Ok, s.Message);
        Assert.True(rcv.ApproveShipment(s.Id).Ok);
        var db = host.Get<DatesErpDbContext>();
        var lot = db.Lots.Where(l => l.ShipmentId == s.Id).OrderBy(l => l.Id).Last();
        return (host, s.Id, lot.Id, db.Shipments.Single(x => x.Id == s.Id).DocumentNumber);
    }

    // ═══ Corr_01 — §C3: إلغاء الاعتماد يبقى الأثر (عكس اتجاهي بدل الحذف) ═══

    [Fact]
    public void Unapprove_Keeps_Ledger_With_Reversing_Entry_And_Cancelled_Lot()
    {
        var (host, shipId, lotId, docNo) = SeedApprovedShipment();
        var db = host.Get<DatesErpDbContext>();
        var rcv = host.Get<IReceivingService>();

        // الأثر الأصلي بعد الاعتماد: قيد وارد ورصيد 200
        Assert.Contains(db.InventoryTransactions.AsNoTracking(), t => t.ReferenceDocType == ReferenceDocType.ShipmentReceipt && t.ReferenceDocNumber == docNo && t.MovementType == MovementType.Inbound);
        Assert.Equal(200, db.StockBalances.AsNoTracking().Where(b => b.ProductId == 1).Sum(b => b.QtyKg), 1);

        var r = rcv.UnapproveShipment(shipId, "خطأ في الكمية المدخلة — تصحيح");
        Assert.True(r.Ok, r.Message);

        // §43: القيد الأصلي محفوظ + قيد عكسي صادر — لا حذف لأي أثر
        var txns = db.InventoryTransactions.AsNoTracking().Where(t => t.ReferenceDocType == ReferenceDocType.Return && t.ReferenceDocNumber == docNo + "#UNAP").ToList();
        Assert.Single(txns);
        Assert.Equal(MovementType.Outbound, txns[0].MovementType);
        Assert.Equal($"{docNo}#UNAP", txns[0].ReferenceDocNumber);
        Assert.Contains(db.InventoryTransactions.AsNoTracking(), t => t.ReferenceDocType == ReferenceDocType.ShipmentReceipt && t.ReferenceDocNumber == docNo && t.MovementType == MovementType.Inbound);
        // الرصيد صفر والدفعة محفوظة بحالة ملغاة (لا حذف صفوف)
        Assert.Equal(0, db.StockBalances.AsNoTracking().Where(b => b.ProductId == 1).Sum(b => b.QtyKg), 1);
        var lot = db.Lots.AsNoTracking().Single(l => l.Id == lotId);
        Assert.Equal(DocStatuses.Cancelled, lot.Status);
        Assert.Equal(0, lot.InStockQtyKg, 1);
        Assert.Equal(DocStatuses.Draft, db.Shipments.AsNoTracking().Single(x => x.Id == shipId).Status);
        // سبب الفك بالتدقيق
        Assert.Contains(db.AuditLogs.AsNoTracking(), a => a.ActionType == "UnapproveShipment" && a.NewValue != null && a.NewValue.Contains("خطأ في الكمية"));

        // إعادة الاعتماد تبني دفعات جديدة نظيفة فوق الأثر القديم
        Assert.True(rcv.ApproveShipment(shipId).Ok);
        Assert.Equal(2, db.Lots.AsNoTracking().Count(l => l.ShipmentId == shipId)); // ملغاة + جديدة
        Assert.Equal(200, db.StockBalances.AsNoTracking().Where(b => b.ProductId == 1).Sum(b => b.QtyKg), 1);
        host.Dispose();
    }

    // ═══ Corr_02 — §C2: تصحيح كمية سند معتمد بقيد فرق موثق ═══

    [Fact]
    public void Qty_Correction_Down_And_Up_Post_Documented_Diff_Entries()
    {
        var (host, shipId, lotId, docNo) = SeedApprovedShipment();
        var db = host.Get<DatesErpDbContext>();
        var rcv = host.Get<IReceivingService>();
        int itemId = db.ShipmentItems.AsNoTracking().Single(i => i.ShipmentId == shipId).Id;

        // تناقص 200 → 150: قيد تسوية سالب بمرجع #CORR-
        var down = new List<ShipmentQtyCorrectionDto> { new() { ShipmentItemId = itemId, NewQtyKg = 150 } };
        var r = rcv.CorrectApprovedShipment(shipId, down, null, "وزن فعلي مصحح بعد إعادة الفرز");
        Assert.True(r.Ok, r.Message);
        var corr = db.InventoryTransactions.AsNoTracking().Single(t => t.ReferenceDocNumber == $"{docNo}#CORR-{itemId}");
        Assert.Equal(MovementType.Adjustment, corr.MovementType);
        Assert.Equal(150, db.Lots.AsNoTracking().Single(l => l.Id == lotId).InStockQtyKg, 1);
        Assert.Equal(150, db.StockBalances.AsNoTracking().Where(b => b.ProductId == 1).Sum(b => b.QtyKg), 1);
        var item = db.ShipmentItems.AsNoTracking().Single(i => i.Id == itemId);
        Assert.Equal(150, item.TotalWeightKg, 1);
        Assert.Equal(8, item.PackageCount); // ⌈150/20⌉
        Assert.Contains(db.AuditLogs.AsNoTracking(), a => a.ActionType == "CorrectShipment" && a.NewValue.Contains("200") && a.NewValue.Contains("150"));

        // تزايد 150 → 180: قيد وارد موجب بمرجع #CORR+
        r = rcv.CorrectApprovedShipment(shipId, new List<ShipmentQtyCorrectionDto> { new() { ShipmentItemId = itemId, NewQtyKg = 180 } }, null, "استكمال وزن ناقص");
        Assert.True(r.Ok, r.Message);
        var up = db.InventoryTransactions.AsNoTracking().Single(t => t.ReferenceDocNumber == $"{docNo}#CORR+{itemId}");
        Assert.Equal(MovementType.Inbound, up.MovementType);
        Assert.Equal(180, db.Lots.AsNoTracking().Single(l => l.Id == lotId).InStockQtyKg, 1);
        Assert.Equal(180, db.StockBalances.AsNoTracking().Where(b => b.ProductId == 1).Sum(b => b.QtyKg), 1);

        // السبب إجباري
        Assert.False(rcv.CorrectApprovedShipment(shipId, new List<ShipmentQtyCorrectionDto> { new() { ShipmentItemId = itemId, NewQtyKg = 170 } }, null, " ").Ok);
        host.Dispose();
    }

    [Fact]
    public void Qty_Correction_Blocked_When_Lot_Consumed_Or_Referenced_By_Plan()
    {
        var (host, shipId, lotId, docNo) = SeedApprovedShipment(kg: 2000, unit: 20);
        var db = host.Get<DatesErpDbContext>();
        var rcv = host.Get<IReceivingService>();
        int itemId = db.ShipmentItems.AsNoTracking().Single(i => i.ShipmentId == shipId).Id;

        // خطة مبنية على الدفعة = مرجع قائم → التصحيح الجزئي مرفوض مع إحالة لمعالج السلسلة
        var planning = host.Get<IPlanningService>();
        var p = planning.SavePlan("خطة مرتبطة", "Daily", D0, D0, 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3, PackagingTypeId = 1, PlannedQtyKg = 500, PlannedCartons = 100, ScheduledDate = D0, SuggestedShiftId = 1, SuggestedLineId = 1 }
        });
        Assert.True(p.Ok, p.Message);
        var blocked = rcv.CorrectApprovedShipment(shipId, new List<ShipmentQtyCorrectionDto> { new() { ShipmentItemId = itemId, NewQtyKg = 1900 } }, null, "محاولة بعد بناء خطة");
        Assert.False(blocked.Ok);
        Assert.Contains("السلسلة", blocked.Message);
        host.Dispose();
    }

    // ═══ Corr_03 — §C2: تصحيح العميل ينقل الأرصدة بأثر صفري موثق ═══

    [Fact]
    public void Customer_Correction_Moves_Balances_With_ZeroQty_Audit_Trail()
    {
        var (host, shipId, lotId, docNo) = SeedApprovedShipment();
        var db = host.Get<DatesErpDbContext>();
        db.Customers.Add(new Customer { CustomerCode = "CORR-C2", CustomerName = "العميل المصحح", IsActive = true });
        db.SaveChanges();
        int cust2 = db.Customers.AsNoTracking().Single(c => c.CustomerCode == "CORR-C2").Id;

        var r = host.Get<IReceivingService>().CorrectApprovedShipment(shipId, new List<ShipmentQtyCorrectionDto>(), cust2, "أدخل السند باسم عميل خاطئ");
        Assert.True(r.Ok, r.Message);

        // الرصيد انتقل بالكامل: القديم صفر والجديد 200 — بلا تغيير في إجمالي المخزون
        Assert.Equal(0, db.StockBalances.AsNoTracking().Where(b => b.ProductId == 1 && b.CustomerId == 1).Sum(b => b.QtyKg), 1);
        Assert.Equal(200, db.StockBalances.AsNoTracking().Where(b => b.ProductId == 1 && b.CustomerId == cust2).Sum(b => b.QtyKg), 1);
        Assert.Equal(cust2, db.Lots.AsNoTracking().Single(l => l.Id == lotId).CustomerId);
        Assert.Equal(cust2, db.Shipments.AsNoTracking().Single(x => x.Id == shipId).CustomerId);
        // أثر موثق بقيدَي النقل الكامل: سحب من القديم (#CUST-) وإدخال للجديد (#CUST+)
        Assert.Contains(db.InventoryTransactions.AsNoTracking(), t => t.ReferenceDocNumber == $"{docNo}#CUST-{lotId}" && t.MovementType == MovementType.Outbound);
        Assert.Contains(db.InventoryTransactions.AsNoTracking(), t => t.ReferenceDocNumber == $"{docNo}#CUST+{lotId}" && t.MovementType == MovementType.Inbound);
        // الدفتر يبقى مصدر الحقيقة: مجموع القيود لكل مفتاح عميل = ما يظهر برصيده
        Assert.Equal(0, db.InventoryTransactions.AsNoTracking().Where(t => t.LotId == lotId && t.CustomerId == 1).Sum(t => t.QtyKg), 1);
        Assert.Equal(200, db.InventoryTransactions.AsNoTracking().Where(t => t.LotId == lotId && t.CustomerId == cust2).Sum(t => t.QtyKg), 1);
        Assert.Contains(db.AuditLogs.AsNoTracking(), a => a.ActionType == "CorrectShipment" && a.NewValue.Contains("العميل"));
        host.Dispose();
    }

    // ═══ Corr_04 — §C1: معالج تصحيح السلسلة (شجرة + فك بعكس البناء + مانع التنفيذ) ═══

    private static (int planId, int orderId) SeedPlanOrderOnShipment(TestHost host, int shipId, int lotId)
    {
        var db = host.Get<DatesErpDbContext>();
        var planning = host.Get<IPlanningService>();
        var p = planning.SavePlan("خطة السلسلة", "Daily", D0, D0, 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3, PackagingTypeId = 1, PlannedQtyKg = 500, PlannedCartons = 100, ScheduledDate = D0, SuggestedShiftId = 1, SuggestedLineId = 1 }
        });
        Assert.True(p.Ok, p.Message);
        Assert.True(planning.ApprovePlan(p.Id).Ok);
        var orders = host.Get<IProductionOrderService>();
        var issued = orders.IssueTodayOrders();
        Assert.True(issued.Ok, issued.Message);
        int orderId = db.ProductionOrders.Single(o => o.SourcePlanId == p.Id).Id;
        Assert.True(orders.ApproveOrder(orderId).Ok);
        return (p.Id, orderId);
    }

    [Fact]
    public void Cascade_Unwinds_Orders_Then_Plans_Then_Shipment_In_Reverse_Build_Order()
    {
        var (host, shipId, lotId, docNo) = SeedApprovedShipment(kg: 2000, unit: 20);
        var db = host.Get<DatesErpDbContext>();
        var (planId, orderId) = SeedPlanOrderOnShipment(host, shipId, lotId);
        var corr = host.Get<CorrectionService>();

        // الشجرة تعرض السلسلة كاملة مع الإجراء التلقائي لكل عقدة
        var chain = corr.GetShipmentChain(shipId);
        Assert.Contains(chain, n => n.DocType == "شحنة استلام");
        Assert.Contains(chain, n => n.DocType == "خطة إنتاج");
        Assert.Contains(chain, n => n.DocType == "أمر إنتاج");

        var r = corr.RunShipmentCascade(shipId, "تصحيح جذري — الكمية والعميل خاطئان");
        Assert.True(r.Ok, r.Message);

        // عكس البناء: أمر ملغى ← خطة محذوفة ← شحنة مسودة بقيد عكسي
        Assert.Equal(DocStatuses.Cancelled, db.ProductionOrders.AsNoTracking().Single(o => o.Id == orderId).Status);
        Assert.DoesNotContain(db.ProductionPlans.AsNoTracking(), pl => pl.Id == planId && pl.Status != DocStatuses.Cancelled);
        Assert.Equal(DocStatuses.Draft, db.Shipments.AsNoTracking().Single(x => x.Id == shipId).Status);
        Assert.Contains(db.InventoryTransactions.AsNoTracking(), t => t.ReferenceDocType == ReferenceDocType.Return && t.ReferenceDocNumber == $"{docNo}#UNAP");
        Assert.Equal(0, db.StockBalances.AsNoTracking().Where(b => b.ProductId == 1).Sum(b => b.QtyKg), 1);
        Assert.Equal(DocStatuses.Cancelled, db.Lots.AsNoTracking().Single(l => l.Id == lotId).Status);
        // الحجز الذي صنعته الخطة تحرر مع الإلغاء
        Assert.Equal(0, db.Lots.AsNoTracking().Single(l => l.Id == lotId).ReservedQtyKg, 1);
        host.Dispose();
    }

    [Fact]
    public void Cascade_Refuses_When_Any_Order_Has_Real_Execution()
    {
        var (host, shipId, lotId, docNo) = SeedApprovedShipment(kg: 2000, unit: 20);
        var db = host.Get<DatesErpDbContext>();
        var (planId, orderId) = SeedPlanOrderOnShipment(host, shipId, lotId);

        // تنفيذ حقيقي (بدء + إيقاف بسبب) — لا يجوز للفك التلقائي أن يعمل فوقه
        var orders = host.Get<IProductionOrderService>();
        Assert.True(orders.StartOrder(orderId).Ok);
        Assert.True(orders.StopOrder(orderId, "عطل في ماكينة العجن — بانتظار الصيانة").Ok);

        var corr = host.Get<CorrectionService>();
        var r = corr.RunShipmentCascade(shipId, "محاولة فوق تنفيذ حقيقي");
        Assert.False(r.Ok);
        Assert.Contains("نُفّذت فعلياً", r.Message);
        // لا شيء تغيّر: الأمر والخطة والشحنة على حالها
        Assert.NotEqual(DocStatuses.Cancelled, db.ProductionOrders.AsNoTracking().Single(o => o.Id == orderId).Status);
        Assert.Contains(db.ProductionPlans.AsNoTracking(), pl => pl.Id == planId);
        Assert.Equal(DocStatuses.Approved, db.Shipments.AsNoTracking().Single(x => x.Id == shipId).Status);
        host.Dispose();
    }

    // ═══ Corr_05 — الصلاحيات: العمليتان الحساستان للمدير فقط افتراضياً ═══

    [Fact]
    public void Correction_Ops_Are_Sensitive_And_Denied_For_Ordinary_Roles()
    {
        // الكتالوج: العمليتان حساستان
        var chainOp = PermissionService.OperationCatalog.Single(o => o.Code == "ChainCorrection");
        var qtyOp = PermissionService.OperationCatalog.Single(o => o.Code == "CorrectApprovedQty");
        Assert.True(chainOp.Sensitive);
        Assert.True(qtyOp.Sensitive);

        using var host = new TestHost();
        // المدير يملكهما (الاكتمال التلقائي)
        var admin = host.LoginAsAdmin();
        Assert.True(admin.Can("receiving", "ChainCorrection"));
        Assert.True(admin.Can("receiving", "CorrectApprovedQty"));

        // بقية الأدوار: لا ترى ولا تنفذ — الزر مخفي (§B74) والخدمة ترفض
        foreach (var user in new[] { "warehouse", "production", "quality" })
        {
            var ctx = host.LoginAs(user);
            Assert.False(ctx.Can("receiving", "ChainCorrection"));
            Assert.False(ctx.Can("receiving", "CorrectApprovedQty"));
        }

        // مستوى الخدمة: أمين مخزن يملك receiving كاملة لكنه يُرفض في العمليتين الحساستين
        var (host2, shipId, lotId, docNo) = SeedApprovedShipment();
        host2.LoginAs("warehouse");
        var rcv = host2.Get<IReceivingService>();
        Assert.Throws<PermissionDeniedException>(() =>
            rcv.CorrectApprovedShipment(shipId, new List<ShipmentQtyCorrectionDto>(), null, "محاولة غير مخولة"));
        var corr = host2.Get<CorrectionService>();
        Assert.Throws<PermissionDeniedException>(() => corr.RunShipmentCascade(shipId, "محاولة غير مخولة"));
        host2.Dispose();
    }
}
