using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Tests;

public class RemediationIntegrityTests
{
    private static (int Plan, int Lot, int FinB) Arrange(TestHost host, bool second = false, bool tomorrow = false)
    {
        host.LoginAsAdmin(); host.SetBusinessDate("2026-09-09");
        var db = host.Get<DatesErpDbContext>();
        var receive = host.Get<IReceivingService>();
        var s = receive.SaveShipment(1, null, null, new() { new() { ProductId = 1, TreatmentRequired = false,
            QtyKg = 10000, PackageCount = 500, UnitWeightKg = 20 } });
        Assert.True(s.Ok, s.Message); Assert.True(receive.ApproveShipment(s.Id).Ok);
        int lot = db.Lots.Single(l => l.ShipmentId == s.Id).Id;
        var master = host.Get<MasterDataService>();
        var fin = master.SaveProductFull(null, "REMED-B", "تام اختبار سلامة الإقفال", "002", "Finished", "كرتون", 5, 1, 5, null, sourceProductId: 1);
        Assert.True(fin.Ok, fin.Message); TestCapacityFixture.DefineMissingRates(host);
        var items = new List<PlanItemDto> { new() { SourceType = "FromReceiving", CustomerId = 1, LotId = lot,
            ProductId = 3, PackagingTypeId = 1, PlannedQtyKg = 100, PlannedCartons = 20, ScheduledDate = "2026-09-09" } };
        if (second) items.Add(new() { SourceType = "FromReceiving", CustomerId = 1, LotId = lot, ProductId = fin.Id,
            PlannedQtyKg = 200, PlannedCartons = 40, ScheduledDate = tomorrow ? "2026-09-10" : "2026-09-09" });
        var planning = host.Get<IPlanningService>();
        var p = planning.SavePlan("اختبار اكتمال الخطة", "Period", "2026-09-09", "2026-09-10", 1, 1, items);
        Assert.True(p.Ok, p.Message); Assert.True(planning.ApprovePlan(p.Id).Ok);
        return (p.Id, lot, fin.Id);
    }
    private static void ExecuteTodayAndClose(TestHost host, bool issueDelivery = true)
    {
        var db = host.Get<DatesErpDbContext>(); var orders = host.Get<IProductionOrderService>();
        var issue = orders.IssueTodayOrders(); Assert.True(issue.Ok, issue.Message);
        var o = db.ProductionOrders.Include(o => o.Items).Single();
        Assert.True(orders.ApproveOrder(o.Id).Ok); Assert.True(orders.StartOrder(o.Id).Ok);
        var kg = o.Items.Sum(i => i.PlannedQtyKg); var cartons = o.Items.Sum(i => i.PlannedCartons);
        var close = host.Get<IExecutionService>().CloseProductionDay(o.Id, kg, cartons, 0, 0, 0, false, new(), false, consumedRawKg: kg,
            itemQtys: o.Items.Select(i => new CloseItemQtyDto { OrderItemId = i.Id, ProducedKg = i.PlannedQtyKg, ProducedCartons = i.PlannedCartons }).ToList());
        Assert.True(close.Ok, close.Message); Assert.True(orders.CloseOrder(o.Id).Ok);
        if (issueDelivery)
        {
            var execId = db.ProductionExecutions.AsNoTracking().Single(e => e.OrderId == o.Id).Id;
            var service = host.Get<IProductionDeliveryService>();
            var draft = service.CreateDeliveryFromActual(execId, "2026-09-09");
            Assert.True(draft.Ok, draft.Message);
            Assert.True(service.IssueDelivery(draft.Id).Ok);
        }
    }
    [Fact]
    public void Approved_Plan_Without_Any_Order_Cannot_Be_Closed_As_Complete()
    {
        using var host = new TestHost(); var ctx = Arrange(host);
        var svc = host.Get<IPlanClosureService>();
        Assert.False(svc.GetInfo(ctx.Plan).CanClose);
        Assert.False(svc.ClosePlanFinal(ctx.Plan).Ok);
        Assert.False(host.Get<IPlanningService>().ClosePlan(ctx.Plan, "لا تجاوز عبر الواجهة القديمة").Ok);
        Assert.False(host.Get<DatesErpDbContext>().ProductionPlans.Single().IsClosed);
    }
    [Fact]
    public void Unissued_Future_Row_Blocks_Closure_After_Todays_Order_Closed()
    {
        using var host = new TestHost(); var ctx = Arrange(host, true, true); ExecuteTodayAndClose(host);
        var info = host.Get<IPlanClosureService>().GetInfo(ctx.Plan);
        Assert.False(info.CanClose); Assert.NotEqual("مكتملة", info.StatusAr);
        Assert.Equal(300, info.PlannedTotal); Assert.Equal(100, info.ProducedTotal); Assert.Equal(200, info.Remaining);
        Assert.False(host.Get<IPlanClosureService>().ClosePlanFinal(ctx.Plan).Ok);
    }
    [Fact]
    public void MultiProduct_Closure_Summary_Uses_Each_Line_Not_Whole_Order_Total()
    {
        using var host = new TestHost(); var ctx = Arrange(host, true); ExecuteTodayAndClose(host);
        var info = host.Get<IPlanClosureService>().GetInfo(ctx.Plan);
        Assert.True(info.IsClosed); Assert.Equal(2, info.Products.Count);
        Assert.Equal(300, info.Products.Sum(p => p.Planned)); Assert.Equal(300, info.Products.Sum(p => p.Produced));
        Assert.Equal(200, info.Products.Single(p => p.Name == "تام اختبار سلامة الإقفال").Produced);
    }
    [Fact]
    public void Partial_Legacy_Order_Does_Not_Disguise_Unissued_Plan_Quantity()
    {
        using var host = new TestHost(); var ctx = Arrange(host);
        var rows = TestOrderFixture.Items(host, ctx.Plan, 1, "2026-09-09"); rows[0].PlannedQtyKg = 50; rows[0].PlannedCartons = 10;
        var old = LegacyOrderFixture.ImportApproved(host, ctx.Plan, 1, "2026-09-09", 1, 1, rows);
        var close = host.Get<IExecutionService>().CloseProductionDay(old.Id, 50, 10, 0, 0, 0, false, new(), false, consumedRawKg: 50);
        Assert.True(close.Ok, close.Message); Assert.True(host.Get<IProductionOrderService>().CloseOrder(old.Id).Ok);
        var info = host.Get<IPlanClosureService>().GetInfo(ctx.Plan);
        Assert.False(info.CanClose); Assert.False(host.Get<IPlanClosureService>().ClosePlanFinal(ctx.Plan).Ok);
    }
    [Fact]
    public void Forced_Closure_Requires_A_Reason_Even_When_Orders_Are_Complete()
    {
        using var host = new TestHost(); var ctx = Arrange(host); ExecuteTodayAndClose(host, issueDelivery: false);
        var svc = host.Get<IPlanClosureService>(); Assert.True(svc.GetInfo(ctx.Plan).CanClose);
        Assert.False(svc.ClosePlanFinal(ctx.Plan, force: true).Ok);
        // A written override cannot hide produced stock lacking an issued delivery.
        Assert.False(svc.ClosePlanFinal(ctx.Plan, "تسوية معتمدة للاختبار", force: true).Ok);
        var db = host.Get<DatesErpDbContext>();
        int orderId = db.ProductionOrders.Single(o => o.SourcePlanId == ctx.Plan).Id;
        int executionId = db.ProductionExecutions.Single(e => e.OrderId == orderId).Id;
        var delivery = host.Get<IProductionDeliveryService>();
        var draft = delivery.CreateDeliveryFromActual(executionId);
        Assert.True(draft.Ok, draft.Message);
        Assert.True(delivery.IssueDelivery(draft.Id).Ok);
        Assert.True(svc.GetInfo(ctx.Plan).IsClosed);
    }
    [Fact]
    public void Receiving_Plan_Inherits_Missing_Customer_From_The_Lot()
    {
        using var host = new TestHost(); var ctx = Arrange(host);
        var svc = host.Get<IPlanningService>();
        var plan = svc.SavePlan("وراثة الملكية", "Daily", "2026-09-09", "2026-09-09", 1, 1, new()
        { new() { SourceType="FromReceiving", LotId=ctx.Lot, ProductId=3, PackagingTypeId=1, PlannedCartons=20, PlannedQtyKg=100 } });
        Assert.True(plan.Ok, plan.Message);
        Assert.Equal(1, host.Get<DatesErpDbContext>().ProductionPlanItems.Single(i => i.PlanId == plan.Id).CustomerId);
        Assert.True(svc.ApprovePlan(plan.Id).Ok);
        var order = host.Get<IProductionOrderService>().SaveOrder("FromPlan", plan.Id, 1, "2026-09-09", 1, 1,
            TestOrderFixture.Items(host, plan.Id, 1, "2026-09-09")); Assert.True(order.Ok, order.Message);
    }
    [Fact]
    public void Planning_Rejects_A_Shipment_Reference_That_Does_Not_Own_The_Lot()
    {
        using var host = new TestHost(); var ctx = Arrange(host);
        var receiving = host.Get<IReceivingService>();
        var other = receiving.SaveShipment(1, null, null, new() { new() { ProductId = 1, TreatmentRequired = false, QtyKg = 1000, PackageCount=50, UnitWeightKg=20 } });
        Assert.True(other.Ok, other.Message); Assert.True(receiving.ApproveShipment(other.Id).Ok);
        var before = host.Get<DatesErpDbContext>().ProductionPlans.Count();
        var p = host.Get<IPlanningService>().SavePlan("شحنة خاطئة", "Daily", "2026-09-09", "2026-09-09", 1, 1, new()
        { new() { SourceType="FromReceiving", LotId=ctx.Lot, ShipmentId=other.Id, CustomerId=1, ProductId=3, PackagingTypeId=1, PlannedCartons=20, PlannedQtyKg=100 } });
        Assert.False(p.Ok); Assert.Equal(before, host.Get<DatesErpDbContext>().ProductionPlans.Count());
    }
    [Fact]
    public void Draft_Update_Inherits_Owner_And_Conflicting_Update_Rolls_Back()
    {
        using var host = new TestHost(); var ctx = Arrange(host);
        var svc = host.Get<IPlanningService>(); var db = host.Get<DatesErpDbContext>();
        List<PlanItemDto> Rows(int? shipment = null) => new() { new() { SourceType="FromReceiving", LotId=ctx.Lot,
            ShipmentId=shipment, ProductId=3, PackagingTypeId=1, PlannedCartons=20, PlannedQtyKg=100 } };
        var draft = svc.SavePlan("مسودة", "Daily", "2026-09-09", "2026-09-09", 1, 1, Rows()); Assert.True(draft.Ok, draft.Message);
        var updated = svc.UpdatePlan(draft.Id, "تعديل صحيح", "Daily", "2026-09-09", "2026-09-09", 1, 1, Rows());
        Assert.True(updated.Ok, updated.Message);
        var before = db.ProductionPlanItems.AsNoTracking().Single(i => i.PlanId == draft.Id);
        Assert.Equal(1, before.CustomerId);
        var other = host.Get<IReceivingService>().SaveShipment(1, null, null, new() { new() { TreatmentRequired=false, ProductId=1, QtyKg=1000, PackageCount=50, UnitWeightKg=20 } });
        Assert.True(other.Ok, other.Message);
        var bad = svc.UpdatePlan(draft.Id, "تعديل مرفوض", "Daily", "2026-09-09", "2026-09-09", 1, 1, Rows(other.Id));
        Assert.False(bad.Ok);
        var after = db.ProductionPlanItems.AsNoTracking().Single(i => i.PlanId == draft.Id);
        Assert.Equal(before.Id, after.Id); Assert.Equal(before.ShipmentId, after.ShipmentId); Assert.Equal(1, after.CustomerId);
        Assert.Equal("تعديل صحيح", db.ProductionPlans.AsNoTracking().Single(p => p.Id == draft.Id).PlanTitle);
    }
    [Fact]
    public void Approval_Rejects_Legacy_Missing_Owner_Without_Silent_Repair()
    {
        using var host = new TestHost(); var ctx = Arrange(host);
        var svc = host.Get<IPlanningService>(); var db = host.Get<DatesErpDbContext>();
        var draft = svc.SavePlan("بيانات قديمة", "Daily", "2026-09-09", "2026-09-09", 1, 1, new()
        { new() { SourceType="FromReceiving", CustomerId=1, LotId=ctx.Lot, ProductId=3, PackagingTypeId=1, PlannedCartons=20, PlannedQtyKg=100 } });
        Assert.True(draft.Ok, draft.Message);
        // Deliberately emulate a pre-fix persisted draft, outside normal service validation.
        var item = db.ProductionPlanItems.Single(i => i.PlanId == draft.Id); item.CustomerId=null; db.SaveChanges();
        var denied = svc.ApprovePlan(draft.Id); Assert.False(denied.Ok); Assert.Contains("صحح الخطة رسمياً", denied.Message);
        Assert.Null(db.ProductionPlanItems.AsNoTracking().Single(i => i.PlanId == draft.Id).CustomerId);
        Assert.False(db.ProductionPlans.AsNoTracking().Single(p => p.Id == draft.Id).IsApproved);
    }

    [Fact]
    public void Compatibility_Close_Uses_Canonical_Closure_And_Audit()
    {
        using var host = new TestHost(); var ctx = Arrange(host); ExecuteTodayAndClose(host);
        var result = host.Get<IPlanningService>().ClosePlan(ctx.Plan, "إقفال كامل"); Assert.True(result.Ok, result.Message);
        var db = host.Get<DatesErpDbContext>();
        Assert.True(db.ProductionPlans.Single(p => p.Id == ctx.Plan).IsClosed);
        Assert.Contains(db.AuditLogs, a => a.RecordId == ctx.Plan
            && (a.ScreenName == "إقفال خطة الإنتاج" || a.ActionType.Contains("إقفال خطة")));
    }

}
