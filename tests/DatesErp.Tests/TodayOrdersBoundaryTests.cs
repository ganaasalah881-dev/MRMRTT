using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Tests;

public class TodayOrdersBoundaryTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
    private static ProductionPlan Seed(DatesErpDbContext db, DateTime day, int product = 3, int? customer = null)
    {
        // Explicit approved legacy snapshot: no unspecified shift/line is invented by an order.
        var p = new ProductionPlan { DocumentNumber = "TOD-" + Guid.NewGuid().ToString("N"), PlanTitle = "حدود أمر اليوم",
            StartDate = day, EndDate = day, IsApproved = true, Status = DocStatuses.Approved,
            Items = new() { new() { ProductId = product, PlannedCartons = 10, PlannedQtyKg = 75, ScheduledDate = day, CustomerId = customer } } };
        db.ProductionPlans.Add(p); db.SaveChanges(); return p;
    }
    [Fact]
    public void Midnight_Uses_Injected_Business_Clock_Not_Employee_Date_And_No_Rollover()
    {
        var clock = new Clock(); using var h = new TestHost(clock); h.LoginAsAdmin();
        var db = h.Get<DatesErpDbContext>(); Seed(db, clock.Now.Date);
        var s = h.Get<IProductionOrderService>(); Assert.Single(s.GetTodayProduction().Rows);
        clock.Now = clock.Now.AddDays(1);
        Assert.Empty(s.GetTodayProduction().Rows); Assert.False(s.IssueTodayOrders().Ok); Assert.Empty(db.ProductionOrders);
    }
    [Theory]
    [InlineData("approval")]
    [InlineData("cancel")]
    [InlineData("date")]
    public void Revoked_Or_Rescheduled_Plan_Is_Requeried_Before_Issuance(string change)
    {
        var clock = new Clock(); using var h = new TestHost(clock); h.LoginAsAdmin(); var db = h.Get<DatesErpDbContext>();
        var p = Seed(db, clock.Now.Date); var s = h.Get<IProductionOrderService>(); Assert.True(s.GetTodayProduction().CanIssue);
        if (change == "approval") p.IsApproved = false;
        if (change == "cancel") p.Status = DocStatuses.Cancelled;
        if (change == "date") p.Items[0].ScheduledDate = p.Items[0].ScheduledDate!.Value.AddDays(1);
        db.SaveChanges();
        Assert.False(s.IssueTodayOrders().Ok); Assert.Empty(db.ProductionOrders); Assert.Empty(s.GetTodayProduction().Rows);
    }
    [Fact]
    public void Undated_Period_Item_Is_Not_Assigned_To_Today_By_Order_Service()
    {
        var clock = new Clock(); using var h = new TestHost(clock); h.LoginAsAdmin(); var db = h.Get<DatesErpDbContext>();
        var p = Seed(db, clock.Now.Date); p.PlanType = "Period"; p.Items[0].ScheduledDate = null; db.SaveChanges();
        var s = h.Get<IProductionOrderService>(); Assert.Empty(s.GetTodayProduction().Rows); Assert.False(s.IssueTodayOrders().Ok);
    }
    [Fact]
    public void Any_Invalid_Group_Rolls_Back_Whole_Day_Without_Partial_Orders()
    {
        var clock = new Clock(); using var h = new TestHost(clock); h.LoginAsAdmin(); var db = h.Get<DatesErpDbContext>();
        Seed(db, clock.Now.Date, customer: 1); Seed(db, clock.Now.Date, product: 1); // second uses RAW, invalid for production
        var s = h.Get<IProductionOrderService>(); var r = s.IssueTodayOrders();
        Assert.False(r.Ok); Assert.Empty(db.ProductionOrders); Assert.Empty(db.ProductionOrderItems); Assert.Empty(db.ProductionOrderMaterials);
    }
    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    public void Existing_Partial_Or_Excess_Order_Is_Not_Topped_Up_Or_Reissued(int cartons)
    {
        var clock = new Clock(); using var h = new TestHost(clock); h.LoginAsAdmin(); var db = h.Get<DatesErpDbContext>();
        var p = Seed(db, clock.Now.Date); var s = h.Get<IProductionOrderService>(); Assert.True(s.IssueTodayOrders().Ok);
        var oi = db.ProductionOrderItems.Single(); oi.PlannedCartons = cartons; oi.PlannedQtyKg = cartons * 7.5; db.SaveChanges();
        Assert.False(s.IssueTodayOrders().Ok); Assert.Single(db.ProductionOrders);
        var row = Assert.Single(s.GetTodayProduction().Rows); Assert.Equal(10, row.PlannedCartons); Assert.Null(row.OrderId); Assert.False(row.IsPending);
    }
    [Fact]
    public void An_Extra_Stored_Item_Does_Not_Leak_Through_Open_Document_Link()
    {
        var clock = new Clock(); using var h = new TestHost(clock); h.LoginAsAdmin(); var db = h.Get<DatesErpDbContext>();
        var p = Seed(db, clock.Now.Date); var s = h.Get<IProductionOrderService>(); Assert.True(s.IssueTodayOrders().Ok);
        var order = db.ProductionOrders.Single();
        db.ProductionOrderItems.Add(new() { OrderId = order.Id, ProductId = 4, PlannedCartons = 1, PlannedQtyKg = 5 }); db.SaveChanges();
        var row = Assert.Single(s.GetTodayProduction().Rows); Assert.Equal(3, row.ProductId); Assert.Null(row.OrderId);
        Assert.False(s.ApproveOrder(order.Id).Ok);
    }
    [Fact]
    public void Two_Defined_Shifts_Are_Copied_Not_Auto_Reassigned()
    {
        var clock = new Clock(); using var h = new TestHost(clock); h.LoginAsAdmin(); var db = h.Get<DatesErpDbContext>();
        var p = Seed(db, clock.Now.Date); p.Items[0].SuggestedShiftId = 1;
        p.Items.Add(new() { ProductId = 3, CustomerId = 1, PlannedCartons = 10, PlannedQtyKg = 75, ScheduledDate = clock.Now.Date, SuggestedShiftId = 2 }); db.SaveChanges();
        var s = h.Get<IProductionOrderService>(); var r = s.IssueTodayOrders(); Assert.True(r.Ok, r.Message);
        Assert.Equal(new int?[] { 1, 2 }, s.GetTodayProduction().Rows.Select(x => x.ShiftId).OrderBy(x => x));
        Assert.Equal(new int?[] { 1, 2 }, db.ProductionOrders.Select(x => x.ShiftId).OrderBy(x => x).ToList());
    }
    [Fact]
    public void An_Inconsistent_Approved_Unit_Snapshot_Is_Rejected_Not_Recalculated()
    {
        var clock = new Clock(); using var h = new TestHost(clock); h.LoginAsAdmin(); var db = h.Get<DatesErpDbContext>();
        var p = Seed(db, clock.Now.Date); p.Items[0].PlannedQtyKg = 100; db.SaveChanges();
        var r = h.Get<IProductionOrderService>().IssueTodayOrders(); Assert.False(r.Ok); Assert.Empty(db.ProductionOrders);
        Assert.Equal(100, db.ProductionPlanItems.AsNoTracking().Single().PlannedQtyKg);
    }
}
