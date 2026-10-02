using DatesErp.Application.Services;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>§7/§37/§38 — اختبارات سير العمل الكامل والتكامل عبر كل المراحل.</summary>
public class FullWorkflowTests
{
    /// <summary>الرحلة الكاملة: استلام ← دفعة ← خطة ← أمر (اعتماد يصرف المواد المساعدة؛ الخام يُصرف عند الإقفال) ← تنفيذ ← جودة ← تام ← تسليم عميل.</summary>
    [Fact]
    public void Full_Traceability_Workflow_End_To_End()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();

        // تعبئة رصيد المواد المساعدة حتى يسمح الاعتماد بالصرف (§8)
        var whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new Core.Domain.Entities.StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 10000 },
            new Core.Domain.Entities.StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 10000 });
        db.SaveChanges();

        // 1) استلام شحنة خام 10,000 كجم للعميل 1
        var receiving = host.Get<IReceivingService>();
        var r1 = receiving.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        {
            new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 500, UnitWeightKg = 20, QtyKg = 10000 }
        });
        Assert.True(r1.Ok, r1.Message);
        var r2 = receiving.ApproveShipment(r1.Id);
        Assert.True(r2.Ok, r2.Message);

        var lotId = db.Lots.Single().Id;
        Assert.Equal(10000, db.Lots.Single().InStockQtyKg, 1);
        // قيد الوارد في مخزن الخام مرتبط بمستند الاستلام (§9)
        Assert.Contains(db.InventoryTransactions, t => t.MovementType == MovementType.Inbound
            && t.ReferenceDocType == ReferenceDocType.ShipmentReceipt && t.QtyKg == 10000);

        // 2) خطة إنتاج بـ 3,000 كجم من الدفعة (500 كرتون × 7.2 للصنف 3)
        var planning = host.Get<IPlanningService>();
        var p1 = planning.SavePlan("خطة اختبار", "Daily", "2026-08-20", "2026-08-20", 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3,
                    PlannedQtyKg = 3000, PlannedCartons = 600, PackagingTypeId = 1, ScheduledDate = "2026-08-20", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 }
        });
        Assert.True(p1.Ok, p1.Message);
        Assert.True(planning.ApprovePlan(p1.Id).Ok);
        // الحجز خفض المتاح
        Assert.Equal(7000, db.Lots.Single().AvailableQtyKg, 1);

        // 3) أمر إنتاج من الخطة واعتماده — يجب أن يخصم الخام من المخازن فوراً (§8)
        var orders = host.Get<IProductionOrderService>();
        host.SetBusinessDate("2026-08-20"); // Arrange production on its scheduled business day, not the machine date.
        var o1 = orders.SaveOrder("FromPlan", p1.Id, 1, "2026-08-20", 1, 1, TestOrderFixture.Items(host, p1.Id, 1, "2026-08-20"));
        Assert.True(o1.Ok, o1.Message);
        var oid = o1.Id;

        double rawBefore = db.StockBalances.Where(b => b.WarehouseId == 1).Sum(b => b.QtyKg);
        var o2 = orders.ApproveOrder(oid);
        Assert.True(o2.Ok, o2.Message);
        double rawAfter = db.StockBalances.Where(b => b.WarehouseId == 1).Sum(b => b.QtyKg);
        Assert.Equal(rawBefore, rawAfter, 1); // §لا يُخصم الخام عند الاعتماد — يُصرف عند الإقفال فعلياً
        // §قاعدة توازن الإنتاج: لا يُخصم الخام عند الاعتماد — فالخام يُصرف عند الإقفال
        // بالكمية المستهلكة فعلياً، لا بوزن المنتج المخطط (لا معادلة ثابتة تربطهما).
        Assert.Equal(10000, db.Lots.Single().InStockQtyKg, 1);
        // المواد المساعدة صُرفت أيضاً
        Assert.True(db.ProductionOrderMaterials.All(m => m.ActualIssuedQty > 0));

        // 4) تنفيذ الإنتاج
        // §المسار الفعلي الذي تسلكه الشاشات: StartOrder ثم CloseProductionDay
        var orders2 = host.Get<IProductionOrderService>();
        var exec = host.Get<IExecutionService>();
        var st = orders2.StartOrder(oid);
        Assert.True(st.Ok, st.Message);
        var e2 = exec.CloseProductionDay(oid, 3000, 600, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        Assert.True(e2.Ok, e2.Message);
        int execId = db.ProductionExecutions.Single(x => x.OrderId == oid).Id;

        // 5) فحص الجودة واعتماده (مع أصناف ثانوية بالكيلو §8)
        var quality = host.Get<IQualityService>();
        var hashfProductId = db.Products.Single(p => p.ProductCode == "003-001").Id;
        var nawaProductId = db.Products.Single(p => p.ProductCode == "003-002").Id;
        var q1 = quality.SaveCheck(oid, execId, "2026-08-21", "نهائي",
            new List<QualityItemDto> { new() { ProductId = 3, LotId = lotId, CheckedQtyKg = 3000, AcceptedQtyKg = 2990, RejectedQtyKg = 10 } },
            new List<(int, double)> { (hashfProductId, 25.0), (nawaProductId, 40.0) }); // صنفا حشف ونوى المعرفان في بطاقة الأصناف
        Assert.True(q1.Ok, q1.Message);
        Assert.True(quality.ApproveCheck(q1.Id).Ok);
        Assert.Equal("كجم", db.Products.Single(b => b.Id == hashfProductId).UnitOfMeasure);

        // 6) أمر تسليم الإنتاج من الفعلي ثم استلام التام: الإصدار لا يمس الأرصدة، السند وحده يؤثر (§7)
        var deliveries = host.Get<IProductionDeliveryService>();
        var draft = deliveries.CreateDeliveryFromActual(execId, "2026-08-21", "تسليم ناتج التنفيذ الفعلي");
        Assert.True(draft.Ok, draft.Message);
        Assert.True(deliveries.IssueDelivery(draft.Id).Ok);
        var deliveryCard = deliveries.GetDelivery(draft.Id);
        var fg = host.Get<IFinishedGoodsService>();
        var f1 = fg.SaveReceipt(oid, q1.Id, "2026-08-21",
            deliveryCard.Lines.Select(l => new FinishedGoodsItemDto
            {
                ProductId = l.ProductId, LotId = l.LotId, PackagingTypeId = l.PackagingTypeId,
                PackageCount = l.PackageCount, NetWeightKg = l.QtyKg,
                CustomerId = l.CustomerId, DeliveryItemId = l.Id
            }).ToList(), draft.Id);
        Assert.True(f1.Ok, f1.Message);
        var did = f1.Id;
        double wfgBefore = db.StockBalances.Where(b => b.WarehouseId == 2).Sum(b => b.QtyKg);
        Assert.True(fg.Issue(did).Ok);
        Assert.Equal(wfgBefore, db.StockBalances.Where(b => b.WarehouseId == 2).Sum(b => b.QtyKg), 1); // الإصدار بلا أثر

        // استلام جزئي ثم كامل
        var itemId = db.FinishedGoodsReceiptItems.Single(i => i.ReceiptId == did).Id;
        var rec1 = fg.Receive(did, new Dictionary<int, double> { [itemId] = 1500 });
        Assert.True(rec1.Ok, rec1.Message);
        Assert.Equal(wfgBefore + 1500, db.StockBalances.Where(b => b.WarehouseId == 2).Sum(b => b.QtyKg), 1);
        var rec2 = fg.Receive(did, null); // explicit receive-all: an empty map means receive nothing
        Assert.True(rec2.Ok, rec2.Message);
        Assert.Equal(wfgBefore + 3000, db.StockBalances.Where(b => b.WarehouseId == 2).Sum(b => b.QtyKg), 1);
        Assert.NotNull(db.FinishedGoodsReceipts.Single(r => r.Id == did).ReceiptNumber); // سند RCV

        // 7) تسليم العميل من رصيده في مخزن التام
        var cd = host.Get<ICustomerDeliveryService>();
        var d1 = cd.Save(1, "2026-08-22", oid, new List<CustomerDeliveryItemDto>
        {
            new() { ProductId = 3, LotId = lotId, PackagingTypeId = 1, PackageCount = 598, QtyKg = 2990 }
        });
        Assert.True(d1.Ok, d1.Message);
        var d2 = cd.Approve(d1.Id);
        Assert.True(d2.Ok, d2.Message);
        Assert.Equal(10, db.StockBalances.Where(b => b.WarehouseId == 2).Sum(b => b.QtyKg), 1); // 10 كجم مرفوضة لا تُفرَج
        Assert.Equal(2990, db.Lots.Single(l => l.Id == lotId).DeliveredQtyKg, 1);

        // 8) التتبع الكامل: كل حركة مرتبطة بمستند (§9)
        Assert.DoesNotContain(db.InventoryTransactions, t => string.IsNullOrEmpty(t.ReferenceDocNumber));
        // 9) التدقيق سجل العمليات (§26)
        Assert.True(db.AuditLogs.Count() > 5);
        Assert.Contains(db.AuditLogs, a => a.ActionType == "Approve");
    }

    [Fact]
    public void Delivery_Cannot_Precede_Approved_Quality()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        SeedQuickOrder(host, db, out var oid, out var lotId);

        var fg = host.Get<IFinishedGoodsService>();
        var r = fg.SaveReceipt(oid, null, "2026-08-21", new List<FinishedGoodsItemDto>
        {
            new() { ProductId = 3, LotId = lotId, PackageCount = 10, NetWeightKg = 100 }
        });
        // لا استلام مباشر من الإنتاج الفعلي أو أمر الإنتاج؛ يجب إنشاء أمر التسليم أولاً.
        Assert.False(r.Ok);
        Assert.Contains("أمر تسليم", r.Message);
    }

    /// <summary>
    /// §B102 — بذرة بنمط خط B101 للاختبارات المرحّلة (B97/B98/B99): 500 كجم = 100 كرتون
    /// بعبوة «كرتون 5 كجم» (PackagingTypeId=1). وSeedQuickOrder الأصلية تبقى للاختبارات القديمة.
    /// </summary>
    internal static void SeedQuickOrderPacked(TestHost host, DatesErpDbContext db, out int orderId, out int lotId)
    {
        int whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new Core.Domain.Entities.StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 8000 },
            new Core.Domain.Entities.StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 8000 });
        db.SaveChanges();

        var rcv = host.Get<IReceivingService>();
        var s = rcv.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000 } });
        if (!s.Ok) throw new InvalidOperationException("استلام: " + s.Message);
        rcv.ApproveShipment(s.Id);
        lotId = db.Lots.OrderBy(l => l.Id).Last().Id;

        var orders = host.Get<IProductionOrderService>();
        var o = SeedApprovedTodayPlanOrder(host, db, lotId, 1);
        if (!o.Ok) throw new InvalidOperationException("أمر: " + o.Message);
        var ap = orders.ApproveOrder(o.Id);
        if (!ap.Ok) throw new InvalidOperationException("اعتماد أمر: " + ap.Message);
        orderId = o.Id;
    }

    internal static void SeedQuickOrder(TestHost host, DatesErpDbContext db, out int orderId, out int lotId)
    {
        var whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new Core.Domain.Entities.StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 5000 },
            new Core.Domain.Entities.StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 5000 });
        db.SaveChanges();

        var rec = host.Get<IReceivingService>();
        var s = rec.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        {
            new() { TreatmentRequired = false, ProductId = 1, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000 }
        });
        rec.ApproveShipment(s.Id);
        lotId = db.Lots.OrderBy(l => l.Id).Last().Id;

        var orders = host.Get<IProductionOrderService>();
        var o = SeedApprovedTodayPlanOrder(host, db, lotId, null);
        if (!o.Ok) throw new InvalidOperationException(o.Message);
        orderId = o.Id;
        orders.ApproveOrder(orderId);
    }

    // Downstream QC/inventory regression fixtures now originate in a real approved day plan,
    // preserving their original output quantities; no manual-order exception in production code.
    private static OpResult SeedApprovedTodayPlanOrder(TestHost host, DatesErpDbContext db, int lotId, int? pack)
    {
        var today = db.BusinessNow.ToString("dd/MM/yyyy");
        var planning = host.Get<IPlanningService>();
        var plan = planning.SavePlan("خطة اختبار دورة التشغيل", "Daily", today, today, 1, 1,
            new() { new() { ProductId = 3, PackagingTypeId = pack, CustomerId = 1, LotId = lotId,
                SourceType = "FromReceiving", PlannedQtyKg = 500, PlannedCartons = pack == 1 ? 100 : 67,
                ScheduledDate = today, SuggestedShiftId = 1, SuggestedLineId = 1 } });
        if (!plan.Ok) return plan;
        var approved = planning.ApprovePlan(plan.Id); if (!approved.Ok) return approved;
        var issued = host.Get<IProductionOrderService>().IssueTodayOrders(); if (!issued.Ok) return issued;
        var order = db.ProductionOrders.AsNoTracking().Single(o => o.SourcePlanId == plan.Id);
        return OpResult.Success("بذرة أمر من خطة اليوم المعتمدة", order.Id, order.DocumentNumber);
    }
}
