using DatesErp.Core.Common;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Tests;

/// <summary>Arrange-only helpers. Read the approved plan verbatim; never change an order request being tested,
/// relax a guard, or mutate approved planning rows to make a test pass.</summary>
internal static class TestOrderFixture
{
    internal static List<OrderItemDto> Items(TestHost host, int planId, int? customerId, string day)
    {
        Assert.True(UiFormat.TryParseDate(day, out var date));
        return host.Get<DatesErpDbContext>().ProductionPlanItems.AsNoTracking()
            .Where(p => p.PlanId == planId && p.CustomerId == customerId && p.ScheduledDate != null && p.ScheduledDate.Value.Date == date.Date)
            .OrderBy(p => p.Id).Select(p => new OrderItemDto { PlanItemId = p.Id, CustomerId = p.CustomerId,
                ProductId = p.ProductId, LotId = p.LotId, ShipmentId = p.ShipmentId, PackagingTypeId = p.PackagingTypeId,
                PlannedQtyKg = p.PlannedQtyKg, PlannedCartons = p.PlannedCartons }).ToList();
    }

    /// <summary>Replace the obsolete MANUAL setup of downstream stock-reversal tests with actual approved planning.</summary>
    internal static OpResult PlanThenOrder(TestHost host, int? customer, string day, int? shift, int? line, List<OrderItemDto> items)
    {
        host.SetBusinessDate(day);
        TestCapacityFixture.DefineMissingRates(host);
        var plan = host.Get<IPlanningService>().SavePlan("خطة تجهيز اختبار", "Daily", day, day, shift, line,
            items.Select(i => new PlanItemDto { SourceType = i.LotId != null ? "FromReceiving" : "Manual",
                CustomerId = i.CustomerId ?? customer, ProductId = i.ProductId, LotId = i.LotId, ShipmentId = i.ShipmentId,
                PackagingTypeId = i.PackagingTypeId, PlannedCartons = i.PlannedCartons, PlannedQtyKg = i.PlannedQtyKg,
                ScheduledDate = day, SuggestedShiftId = shift, SuggestedLineId = line }).ToList());
        Assert.True(plan.Ok, plan.Message);
        var approved = host.Get<IPlanningService>().ApprovePlan(plan.Id); Assert.True(approved.Ok, approved.Message);
        return host.Get<IProductionOrderService>().SaveOrder("FromPlan", plan.Id, customer, day, shift, line, Items(host, plan.Id, customer, day));
    }
}
