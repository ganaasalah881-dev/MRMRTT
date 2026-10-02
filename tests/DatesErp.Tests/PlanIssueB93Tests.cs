using Microsoft.EntityFrameworkCore;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B93 — ترحيل الخطة المعتمدة إلى أوامر: تجميع (تاريخ×وردية×خط)، المتبقي فقط،
/// رفض غير المعتمدة، فلترة الفترة، ومجموعات متعددة العملاء.
/// </summary>
public class PlanIssueB93Tests
{
    private static (TestHost host, int custA, int custB) Seed2()
    {
        var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        db.Customers.Add(new Customer { CustomerCode = "PI-A", CustomerName = "عميل ترحيل أ", IsActive = true });
        db.Customers.Add(new Customer { CustomerCode = "PI-B", CustomerName = "عميل ترحيل ب", IsActive = true });
        db.SaveChanges();
        return (host, db.Customers.Single(c => c.CustomerCode == "PI-A").Id,
            db.Customers.Single(c => c.CustomerCode == "PI-B").Id);
    }

    private static int SaveApprovedPlan(TestHost host, List<PlanItemDto> items)
    {
        using var scope = host.Services.CreateScope();
        var plan = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var r = plan.SavePlan("خطة ترحيل", "Period", "2026-09-01", "2026-09-06", 1, 1, items);
        Assert.True(r.Ok);
        Assert.True(plan.ApprovePlan(r.Id).Ok);
        return r.Id;
    }

    private static IProductionOrderService Orders(TestHost h)
        => h.Services.CreateScope().ServiceProvider.GetRequiredService<IProductionOrderService>();

    [Fact]
    public void Issue_Only_Today_Then_Advance_To_Next_Scheduled_Day()
    {
        var (host, a, b) = Seed2(); using var _host = host;
        int planId = SaveApprovedPlan(host, new List<PlanItemDto>
        {
            new() { CustomerId = a, ProductId = 3, PlannedCartons = 200, PlannedQtyKg = 1500, ScheduledDate = "2026-09-01", SuggestedShiftId = 1 },
            new() { CustomerId = a, ProductId = 4, PlannedCartons = 300, PlannedQtyKg = 600, ScheduledDate = "2026-09-01", SuggestedShiftId = 1 },
            new() { CustomerId = b, ProductId = 3, PlannedCartons = 100, PlannedQtyKg = 750, ScheduledDate = "2026-09-03", SuggestedShiftId = 2 },
        });

        host.SetBusinessDate("2026-09-01");
        var db = host.Get<DatesErpDbContext>();
        Assert.True(Orders(host).IssueTodayOrders().Ok);
        var first = Assert.Single(db.ProductionOrders.Include(o => o.Items));
        Assert.Equal(a, first.CustomerId); Assert.Equal(2, first.Items.Count);
        Assert.Equal(2100, first.Items.Sum(i => i.PlannedQtyKg));
        Assert.All(first.Items, i => Assert.NotNull(i.PlanItemId));
        Assert.Equal(new DateTime(2026, 9, 1), first.ProductionDate);
        var repeated = Orders(host).IssueTodayOrders();
        Assert.True(repeated.Ok); Assert.Single(db.ProductionOrders); // Safe idempotent success, no duplicate.
        // A future scheduled row is not issued early; advance the fixture clock to its actual work day.
        host.SetBusinessDate("2026-09-03");
        Assert.True(Orders(host).IssueTodayOrders().Ok);
        Assert.Equal(2, db.ProductionOrders.Count());
        Assert.Equal(3, db.ProductionOrderItems.Count());
        Assert.Equal(2850, db.ProductionOrderItems.Sum(i => i.PlannedQtyKg));
        Assert.All(db.ProductionOrders.Where(o => o.Id != first.Id), o => Assert.Equal(b, o.CustomerId));

    }

    [Fact]
    public void Unapproved_Plan_Rejected()
    {
        var (host, a, b) = Seed2(); using var _host = host;
        int planId;
        using (var scope = host.Services.CreateScope())
        {
            var plan = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var r = plan.SavePlan("خطة مسودة", "Period", "2026-09-01", "2026-09-06", 1, 1, new List<PlanItemDto>
            {
                new() { CustomerId = a, ProductId = 3, PlannedCartons = 200, PlannedQtyKg = 1500, ScheduledDate = "2026-09-01", SuggestedShiftId = 1 },
            });
            Assert.True(r.Ok);
            planId = r.Id;
        }
        host.SetBusinessDate("2026-09-01");
        Assert.False(host.Get<DatesErpDbContext>().ProductionPlans.Single(p => p.Id == planId).IsApproved);
        Assert.Empty(Orders(host).GetTodayProduction().Rows);
        var res = Orders(host).IssueTodayOrders();
        Assert.False(res.Ok);
        Assert.Empty(host.Get<DatesErpDbContext>().ProductionOrders);
    }

    [Fact]
    public void Issue_From_Plan_Can_Create_A_Future_Scheduled_Day()
    {
        var (host, a, b) = Seed2(); using var _host = host;
        int planId = SaveApprovedPlan(host, new List<PlanItemDto>
        {
            new() { CustomerId = a, ProductId = 3, PlannedCartons = 200, PlannedQtyKg = 1500, ScheduledDate = "2026-09-01", SuggestedShiftId = 1 },
            new() { CustomerId = b, ProductId = 3, PlannedCartons = 100, PlannedQtyKg = 750, ScheduledDate = "2026-09-03", SuggestedShiftId = 1 },
        });

        host.SetBusinessDate("2026-09-01");
        var future = Orders(host).IssueOrdersFromPlan(planId, "2026-09-03", "2026-09-03");
        Assert.True(future.Ok, future.Message); Assert.Single(future.Created);
        var futureOrder = Assert.Single(host.Get<DatesErpDbContext>().ProductionOrders);
        Assert.Equal(new DateTime(2026, 9, 3), futureOrder.ProductionDate);
        Assert.Equal(b, futureOrder.CustomerId);
        var issued = Orders(host).IssueTodayOrders(); Assert.True(issued.Ok, issued.Message);
        var order = host.Get<DatesErpDbContext>().ProductionOrders.Single(o => o.ProductionDate == new DateTime(2026, 9, 1));
        Assert.Equal(a, order.CustomerId);

    }

    [Fact]
    public void Scheduled_Sheet_Shows_Past_Current_And_Future_And_Issues_Past_Group_By_Its_Date()
    {
        var (host, a, b) = Seed2(); using var _host = host;
        int planId = SaveApprovedPlan(host, new List<PlanItemDto>
        {
            new() { CustomerId = a, ProductId = 3, PlannedCartons = 200, PlannedQtyKg = 1500, ScheduledDate = "2026-09-01", SuggestedShiftId = 1 },
            new() { CustomerId = b, ProductId = 4, PlannedCartons = 100, PlannedQtyKg = 200, ScheduledDate = "2026-09-03", SuggestedShiftId = 2 },
            new() { CustomerId = a, ProductId = 3, PlannedCartons = 50, PlannedQtyKg = 375, ScheduledDate = "2026-09-05", SuggestedShiftId = 1 },
        });

        host.SetBusinessDate("2026-09-03");
        var orders = Orders(host);
        var scheduled = orders.GetScheduledProduction();
        Assert.Equal(3, scheduled.Rows.Count);
        Assert.Equal(new[] { "01/09/2026", "03/09/2026", "05/09/2026" }, scheduled.Rows.Select(r => r.ScheduledDate).ToArray());
        Assert.Equal(1, scheduled.Rows.Count(r => r.IsToday));
        Assert.Contains(scheduled.Rows, r => r.ScheduledDate == "01/09/2026" && r.IsPending);

        // The grouping identity includes the *line*. Null does not mean
        // "any line"; both lines inherit line #1 from their approved plan.
        var late = orders.IssuePlanGroup(planId, "01/09/2026", a, 1, 1);
        Assert.True(late.Ok, late.Message); // late work remains attributable to its scheduled day
        var lateOrder = Assert.Single(host.Get<DatesErpDbContext>().ProductionOrders.Include(o => o.Items));
        Assert.Equal(new DateTime(2026, 9, 1), lateOrder.ProductionDate);
        Assert.Equal(200, lateOrder.Items.Single().PlannedCartons);
        var current = orders.IssuePlanGroup(planId, "03/09/2026", b, 2, 1);
        Assert.True(current.Ok, current.Message);
        var currentOrder = host.Get<DatesErpDbContext>().ProductionOrders.Include(o => o.Items).Single(o => o.ProductionDate == new DateTime(2026, 9, 3));
        Assert.Equal(100, currentOrder.Items.Single().PlannedCartons);
        Assert.Equal(200, currentOrder.Items.Single().PlannedQtyKg);
    }

    [Fact]
    public void All_Customers_Are_Issued_With_Their_Own_Identity()
    {
        var (host, a, b) = Seed2(); using var _host = host;
        int planId = SaveApprovedPlan(host, new List<PlanItemDto>
        {
            new() { CustomerId = a, ProductId = 3, PlannedCartons = 200, PlannedQtyKg = 1500, ScheduledDate = "2026-09-02", SuggestedShiftId = 1 },
            new() { CustomerId = b, ProductId = 4, PlannedCartons = 300, PlannedQtyKg = 600, ScheduledDate = "2026-09-02", SuggestedShiftId = 1 },
        });

        host.SetBusinessDate("2026-09-02");
        Assert.Equal(2, Orders(host).GetTodayProduction().Rows.Count);
        var r = Orders(host).IssueTodayOrders(); Assert.True(r.Ok, r.Message);
        var db = host.Get<DatesErpDbContext>();
        var orders = db.ProductionOrders.Include(o => o.Items).Where(o => o.SourcePlanId == planId).ToList();
        Assert.Equal(2, orders.Count);
        Assert.Equal(new[] { a, b }, orders.Select(o => o.CustomerId!.Value).OrderBy(i => i));
        Assert.All(orders, o => Assert.All(o.Items, i => Assert.Equal(o.CustomerId, i.CustomerId)));

    }
}
