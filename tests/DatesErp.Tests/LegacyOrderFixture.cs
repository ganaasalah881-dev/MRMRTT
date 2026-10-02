using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;

namespace DatesErp.Tests;

/// <summary>
/// Explicit historical/adversarial snapshot, NOT a way of creating new orders.
/// Downstream compatibility and final consumption/ownership guards must still be tested on
/// old multi-customer/manual data that the current creation API correctly refuses.
/// Do not use this fixture to test current issuance, approval, capacity, or starting production.
/// </summary>
internal static class LegacyOrderFixture
{
    internal static OpResult ImportApproved(TestHost host, int? plan, int? customer, string day, int? shift, int? line, List<OrderItemDto> items)
    {
        Assert.True(UiFormat.TryParseDate(day, out var date));
        var db = host.Get<DatesErpDbContext>();
        var order = new ProductionOrder { DocumentNumber = "LEG-" + Guid.NewGuid().ToString("N")[..10],
            SourceType = plan == null ? "Manual" : "FromPlan", SourcePlanId = plan, CustomerId = customer,
            ProductionDate = date, ShiftId = shift, LineId = line, IsApproved = true, Status = DocStatuses.InProgress };
        foreach (var i in items)
            order.Items.Add(new ProductionOrderItem { PlanItemId = i.PlanItemId, CustomerId = i.CustomerId ?? customer,
                ProductId = i.ProductId, LotId = i.LotId, ShipmentId = i.ShipmentId, PackagingTypeId = i.PackagingTypeId,
                PlannedCartons = i.PlannedCartons, PlannedQtyKg = i.PlannedQtyKg,
                CartonWeightKg = UnitsPolicy.CartonWeight(db, i.ProductId, i.PackagingTypeId), Status = DocStatuses.InProgress });
        db.ProductionOrders.Add(order); db.SaveChanges();
        return OpResult.Success("Imported historical test snapshot; current order API was not bypassed or changed.", order.Id, order.DocumentNumber);
    }
}
