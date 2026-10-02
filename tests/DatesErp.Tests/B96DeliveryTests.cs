using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B96 — فصل أمر تسليم الإنتاج (إدارة الإنتاج) عن أمر الاستلام (المخازن):
/// مصدر التسليم التشغيلي (الإنتاج الفعلي) + فصل الاستلام + إقفال الخطة عند التحرير + حراس الصلاحيات.
/// </summary>
public class B96DeliveryTests
{
    private static (int orderId, int lotId) SeedAndClose(TestHost host, int producedKg = 500, int producedCtn = 67, bool sendToQuality = false)
    {
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrder(host, db, out var oid, out var lotId);
        var close = host.Get<IExecutionService>()
            .CloseProductionDay(oid, producedKg, producedCtn, 0, 0, 0, false, new List<DowntimeDto>(), sendToQuality);
        Assert.True(close.Ok, close.Message);
        return (oid, lotId);
    }

    private static (int planId, int orderId, int lotId) SeedPlanWithProduction(TestHost host)
    {
        var db = host.Get<DatesErpDbContext>();
        int whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 5000 },
            new StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 5000 });
        db.SaveChanges();

        var rcv = host.Get<IReceivingService>();
        var s = rcv.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000 } });
        rcv.ApproveShipment(s.Id);
        int lot = db.Lots.OrderBy(l => l.Id).Last().Id;

        var plan = host.Get<IPlanningService>();
        var p = plan.SavePlan("خطة B96", "Daily", "2026-08-20", "2026-08-20", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lot, CustomerId = 1, ProductId = 3, PlannedQtyKg = 500, PlannedCartons = 67 } });
        Assert.True(p.Ok, p.Message);
        Assert.True(plan.ApprovePlan(p.Id).Ok);
        int planItem = db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == p.Id).Select(i => i.Id).First();

        var orders = host.Get<IProductionOrderService>();
        host.SetBusinessDate("2026-08-20"); // Arrange production on its scheduled business day, not the machine date.
        var o = orders.SaveOrder("FromPlan", p.Id, 1, "2026-08-20", 1, 1, new List<OrderItemDto>
        { new() { PlanItemId = planItem, LotId = lot, CustomerId = 1, ProductId = 3, PlannedQtyKg = 500, PlannedCartons = 67 } });
        Assert.True(o.Ok, o.Message);
        Assert.True(orders.ApproveOrder(o.Id).Ok);

        var c = host.Get<IExecutionService>()
            .CloseProductionDay(o.Id, 500, 67, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        Assert.True(c.Ok, c.Message);
        return (p.Id, o.Id, lot);
    }

    private static (int orderId, int lotA, int lotB, int custB) SeedMultiCustomer(TestHost host)
    {
        var db = host.Get<DatesErpDbContext>();
        int whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 9000 },
            new StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 9000 });
        db.SaveChanges();

        var rcv = host.Get<IReceivingService>();
        var s1 = rcv.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000 } });
        rcv.ApproveShipment(s1.Id);
        int lotA = db.Lots.OrderBy(l => l.Id).Last().Id;
        var cust2 = host.Get<MasterDataService>().SaveCustomer(null, "B96-C2", "عميل ثانٍ", "جملة", "777", "-", true);
        var s2 = rcv.SaveShipment(cust2.Id, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000 } });
        rcv.ApproveShipment(s2.Id);
        int lotB = db.Lots.OrderBy(l => l.Id).Last().Id;

        var orders = host.Get<IProductionOrderService>();
        var o = LegacyOrderFixture.ImportApproved(host, null, null, "2026-08-21", 1, 1, new List<OrderItemDto>
        {
            new() { LotId = lotA, CustomerId = 1, ProductId = 3, PlannedQtyKg = 500, PlannedCartons = 67 },
            new() { LotId = lotB, CustomerId = cust2.Id, ProductId = 3, PlannedQtyKg = 500, PlannedCartons = 67 }
        });
        Assert.True(o.Ok, o.Message);
        Assert.True(db.ProductionOrders.Single(x => x.Id == o.Id).IsApproved); // Historical approval, not new issuance.
        return (o.Id, lotA, lotB, cust2.Id);
    }

    private static int SaveApprovedCheck(TestHost host, int orderId, List<QualityItemDto> items)
    {
        var quality = host.Get<IQualityService>();
        var q = quality.SaveCheck(orderId, null, "2026-08-23", "نهائي", items);
        Assert.True(q.Ok, q.Message);
        Assert.True(quality.ApproveCheck(q.Id).Ok);
        return q.Id;
    }

    // ── 1) المصدر التشغيلي الجديد: الإنتاج الفعلي المكتمل فقط + سقف المتبقي ──
    [Fact]
    public void Delivery_FromActual_AutoFill_And_Caps_Remaining()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot) = SeedAndClose(host);
        var db = host.Get<DatesErpDbContext>();
        var del = host.Get<IProductionDeliveryService>();
        int executionId = db.ProductionExecutions.Single(e => e.OrderId == oid).Id;
        var ctx = del.GetSourceContext(DeliverySources.FromActual, executionId);
        var line = Assert.Single(ctx.Lines);
        Assert.Equal(500, line.RemainingQtyKg, 1);
        Assert.Equal(1, line.CustomerId);

        var r = del.CreateDeliveryFromActual(executionId, "2026-08-24");
        Assert.True(r.Ok, r.Message);
        var card = del.GetDelivery(r.Id);
        Assert.Equal(500, Assert.Single(card.Lines).QtyKg, 1);

        var over = del.UpdateDelivery(r.Id, "2026-08-24", new List<ProductionDeliveryItemDto>
        { new() { OrderId = oid, ProductId = 3, LotId = lot, CustomerId = 1, QtyKg = 501 } });
        Assert.False(over.Ok);
        Assert.Contains("المتبقي", over.Message);

        var legacy = del.SaveDelivery(DeliverySources.FromCheck, 999999, "2026-08-24", new List<ProductionDeliveryItemDto>
        { new() { ProductId = 3, LotId = lot, CustomerId = 1, QtyKg = 1 } });
        Assert.False(legacy.Ok);
    }

    // ── 2) لا تسليم من محضر غير معتمد ولا من فحص يدوي ──
    [Fact]
    public void Delivery_FromUnapproved_Or_Manual_Check_IsRejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot) = SeedAndClose(host);
        var quality = host.Get<IQualityService>();
        var q = quality.SaveCheck(oid, null, "2026-08-23", "نهائي", new List<QualityItemDto>
        { new() { ProductId = 3, LotId = lot, CheckedQtyKg = 500, AcceptedQtyKg = 490, RejectedQtyKg = 10 } });
        Assert.True(q.Ok, q.Message);

        var del = host.Get<IProductionDeliveryService>();
        var r = del.SaveDelivery(DeliverySources.FromCheck, q.Id, "2026-08-24", new List<ProductionDeliveryItemDto>
        { new() { ProductId = 3, LotId = lot, CustomerId = 1, QtyKg = 490 } });
        Assert.False(r.Ok);
        Assert.Contains("التاريخية", r.Message);

        var m = quality.SaveCheck(null, null, "2026-08-24", "نهائي", new List<QualityItemDto>
        { new() { ProductId = 3, AcceptedQtyKg = 100 } });
        Assert.True(m.Ok, m.Message);
        var r2 = del.SaveDelivery(DeliverySources.FromCheck, m.Id, "2026-08-24", new List<ProductionDeliveryItemDto>
        { new() { ProductId = 3, QtyKg = 100 } });
        Assert.False(r2.Ok);
        Assert.Contains("التاريخية", r2.Message);
    }

    // ── 3) مصادر الخطة/الفحص/الإقفال تاريخية فقط (لا إنشاء حتى بتجاوز المدير) ──
    [Fact]
    public void Delivery_LegacySources_Are_ReadOnly_Even_With_BypassPermission()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, _, lot) = SeedPlanWithProduction(host);
        var del = host.Get<IProductionDeliveryService>();
        List<ProductionDeliveryItemDto> items = new()
        { new() { ProductId = 3, LotId = lot, CustomerId = 1, QtyKg = 500 } };
        foreach (var source in new[] { DeliverySources.FromCheck, DeliverySources.FromPlan, DeliverySources.FromClosing })
        {
            var denied = del.SaveDelivery(source, planId, "2026-08-24", items,
                "حتى سبب التجاوز لا يغير سياسة القراءة التاريخية");
            Assert.False(denied.Ok);
            Assert.Contains("التاريخية", denied.Message);
        }
        Assert.Empty(host.Get<DatesErpDbContext>().ProductionDeliveries);
    }

    // ── 4) إقفال الخطة نتيجة تحرير أمر التسليم، وليس إجراءً مستقلاً ──
    [Fact]
    public void Plan_Closes_Only_After_Issued_Actual_Delivery()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, orderId, _) = SeedPlanWithProduction(host);
        var db = host.Get<DatesErpDbContext>();
        var closure = host.Get<IPlanClosureService>();
        Assert.False(closure.ClosePlanFinal(planId).Ok);
        Assert.False(db.ProductionPlans.Single(p => p.Id == planId).IsClosed);

        var del = host.Get<IProductionDeliveryService>();
        int executionId = db.ProductionExecutions.Single(e => e.OrderId == orderId).Id;
        var draft = del.CreateDeliveryFromActual(executionId, "2026-08-24");
        Assert.True(draft.Ok, draft.Message);
        Assert.True(del.IssueDelivery(draft.Id).Ok);
        Assert.True(db.ProductionPlans.Single(p => p.Id == planId).IsClosed);
    }

    // ── 5) تعدد العملاء: بند لكل عميل ← التام يُقيَّد بعميل البند ──
    [Fact]
    public void MultiCustomer_Delivery_And_Receipt_Posts_PerLineCustomer()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var (oid, lotA, lotB, custB) = SeedMultiCustomer(host);
        var close = host.Get<IExecutionService>()
            .CloseProductionDay(oid, 1000, 134, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        Assert.True(close.Ok, close.Message);

        var del = host.Get<IProductionDeliveryService>();
        int executionId = db.ProductionExecutions.Single(e => e.OrderId == oid).Id;
        var ctx = del.GetSourceContext(DeliverySources.FromActual, executionId);
        Assert.Equal(2, ctx.Lines.Count);
        var r = del.CreateDeliveryFromActual(executionId, "2026-08-24");
        Assert.True(r.Ok, r.Message);
        Assert.True(del.IssueDelivery(r.Id).Ok);

        var fg = host.Get<IFinishedGoodsService>();
        var card = del.GetDelivery(r.Id);
        var fr = fg.SaveReceipt(oid, null, "2026-08-24",
            card.Lines.Select(l => new FinishedGoodsItemDto
            {
                ProductId = l.ProductId, LotId = l.LotId, PackageCount = 0,
                NetWeightKg = l.QtyKg, CustomerId = l.CustomerId, DeliveryItemId = l.Id
            }).ToList(), r.Id);
        Assert.True(fr.Ok, fr.Message);
        Assert.True(fg.Issue(fr.Id).Ok);
        var recv = fg.Receive(fr.Id, null);
        Assert.True(recv.Ok, recv.Message);

        int wfg = db.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
        var balA = db.StockBalances.Single(b => b.WarehouseId == wfg && b.ProductId == 3 && b.LotId == lotA && b.CustomerId == 1);
        Assert.Equal(500, balA.QtyKg, 1);
        var balB = db.StockBalances.Single(b => b.WarehouseId == wfg && b.ProductId == 3 && b.LotId == lotB && b.CustomerId == custB);
        Assert.Equal(500, balB.QtyKg, 1);
        Assert.Equal("Full", db.ProductionDeliveries.Single(d => d.Id == r.Id).ReceiptStatus);
    }

    // ── 6) الاستلام الجزئي يتراكم + فوق المتبقي مرفوض + الاكتمال يُكمل الأمر ──
    [Fact]
    public void Receipt_Partial_Accumulates_And_OverRemaining_Rejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var (oid, lot) = SeedAndClose(host);
        var del = host.Get<IProductionDeliveryService>();
        int executionId = db.ProductionExecutions.Single(e => e.OrderId == oid).Id;
        var r = del.CreateDeliveryFromActual(executionId, "2026-08-24");
        Assert.True(r.Ok, r.Message);
        Assert.True(del.IssueDelivery(r.Id).Ok);
        int lineId = db.ProductionDeliveryItems.Single(i => i.DeliveryId == r.Id).Id;

        var fg = host.Get<IFinishedGoodsService>();
        var fr1 = fg.SaveReceipt(oid, null, "2026-08-24", new List<FinishedGoodsItemDto>
        { new() { ProductId = 3, LotId = lot, PackageCount = 0, NetWeightKg = 500, CustomerId = 1, DeliveryItemId = lineId } }, r.Id);
        Assert.True(fr1.Ok, fr1.Message);
        Assert.True(fg.Issue(fr1.Id).Ok);
        int item1 = db.FinishedGoodsReceiptItems.Single(i => i.ReceiptId == fr1.Id).Id;
        var p1 = fg.Receive(fr1.Id, new Dictionary<int, double> { [item1] = 200 });
        Assert.True(p1.Ok, p1.Message);
        Assert.Equal("Partial", db.ProductionDeliveries.Single(d => d.Id == r.Id).ReceiptStatus);

        // فوق المتبقي (300) مرفوض
        var over = fg.SaveReceipt(oid, null, "2026-08-25", new List<FinishedGoodsItemDto>
        { new() { ProductId = 3, LotId = lot, PackageCount = 0, NetWeightKg = 301, CustomerId = 1, DeliveryItemId = lineId } }, r.Id);
        Assert.False(over.Ok);
        Assert.Contains("المتبقي", over.Message);

        // استكمال 300 ← الأمر مكتمل
        var fr2 = fg.SaveReceipt(oid, null, "2026-08-25", new List<FinishedGoodsItemDto>
        { new() { ProductId = 3, LotId = lot, PackageCount = 0, NetWeightKg = 300, CustomerId = 1, DeliveryItemId = lineId } }, r.Id);
        Assert.True(fr2.Ok, fr2.Message);
        Assert.True(fg.Issue(fr2.Id).Ok);
        Assert.True(fg.Receive(fr2.Id, null).Ok);
        var d = db.ProductionDeliveries.Single(x => x.Id == r.Id);
        Assert.Equal("Full", d.ReceiptStatus);
        Assert.Equal(DocStatuses.Completed, d.Status);
    }

    // ── 7) إلغاء أمر مستلَم مرفوض ← إلغاء السند يعكس ويعيد الفتح ← الإلغاء يُقبل ──
    [Fact]
    public void CancelDelivery_Blocked_By_Receipts_Then_Unapprove_Reopens()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var (oid, lot) = SeedAndClose(host);
        var del = host.Get<IProductionDeliveryService>();
        int executionId = db.ProductionExecutions.Single(e => e.OrderId == oid).Id;
        var r = del.CreateDeliveryFromActual(executionId, "2026-08-24");
        Assert.True(r.Ok, r.Message);
        Assert.True(del.IssueDelivery(r.Id).Ok);
        int lineId = db.ProductionDeliveryItems.Single(i => i.DeliveryId == r.Id).Id;

        var fg = host.Get<IFinishedGoodsService>();
        var fr = fg.SaveReceipt(oid, null, "2026-08-24", new List<FinishedGoodsItemDto>
        { new() { ProductId = 3, LotId = lot, PackageCount = 0, NetWeightKg = 500, CustomerId = 1, DeliveryItemId = lineId } }, r.Id);
        Assert.True(fg.Issue(fr.Id).Ok);
        Assert.True(fg.Receive(fr.Id, null).Ok);

        var blocked = del.CancelDelivery(r.Id);
        Assert.False(blocked.Ok);
        Assert.Contains("مستلم", blocked.Message);

        Assert.True(fg.Unapprove(fr.Id).Ok);
        var reopened = db.ProductionDeliveries.Single(d => d.Id == r.Id);
        Assert.Equal("None", reopened.ReceiptStatus);
        Assert.Equal(DocStatuses.Issued, reopened.Status);
        Assert.True(del.CancelDelivery(r.Id).Ok);

        int wfg = db.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
        Assert.Equal(0, db.StockBalances.Where(b => b.WarehouseId == wfg && b.ProductId == 3).Sum(b => b.QtyKg), 1);
    }

    // ── 8) المسار المباشر القديم مرفوض دائماً ──
    [Fact]
    public void Legacy_Direct_Receipt_Path_Is_Rejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var (oid, lot) = SeedAndClose(host, sendToQuality: true);
        var fg = host.Get<IFinishedGoodsService>();

        var fr = fg.SaveReceipt(oid, null, "2026-08-24", new List<FinishedGoodsItemDto>
        { new() { ProductId = 3, LotId = lot, PackageCount = 0, NetWeightKg = 500 } });
        Assert.False(fr.Ok);
        Assert.Contains("أمر تسليم", fr.Message);
        Assert.Empty(db.FinishedGoodsReceipts);
    }
}
