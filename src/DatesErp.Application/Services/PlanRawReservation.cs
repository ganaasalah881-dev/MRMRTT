using DatesErp.Core.Domain.Entities;

namespace DatesErp.Application.Services;

/// <summary>
/// Reservations are expressed in kilograms of RAW input, not the finished
/// product's target weight. When the planner records an explicit source draw,
/// it is authoritative; legacy plans without a draw continue to reserve the
/// target weight conservatively. No fixed water/yield coefficient is imposed.
/// </summary>
internal static class PlanRawReservation
{
    internal static double PlannedKg(ProductionPlanItem item)
        => item.SourceQtyKg > 0 ? item.SourceQtyKg : item.PlannedQtyKg;

    internal static double RemainingKg(ProductionPlanItem item)
        => item.PlannedQtyKg > 0
            ? Math.Max(0, item.PlannedQtyKg - item.ProducedQtyKg) / item.PlannedQtyKg * PlannedKg(item)
            : 0;

    /// <summary>Database-side projection: translatable by both SQLite and SQL Server EF providers.</summary>
    internal static IQueryable<double> RemainingKg(this IQueryable<ProductionPlanItem> items)
        => items.Select(i => i.PlannedQtyKg > 0 && i.PlannedQtyKg > i.ProducedQtyKg
            ? (i.SourceQtyKg > 0 ? i.SourceQtyKg : i.PlannedQtyKg)
              * (i.PlannedQtyKg - i.ProducedQtyKg) / i.PlannedQtyKg
            : 0d);
}
