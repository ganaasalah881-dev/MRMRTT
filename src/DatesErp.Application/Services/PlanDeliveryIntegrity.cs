using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// Shared plan/finished-goods invariants. A plan may close only after EVERY
/// produced order line has an issued delivery of the same order, product, lot,
/// customer and package. The in-flight delivery is included before SaveChanges.
/// </summary>
internal static class PlanDeliveryIntegrity
{
    internal static List<string> MissingIssuedDeliveries(DatesErpDbContext db, int planId,
        ProductionDelivery current = null)
    {
        var failures = new List<string>();
        var orders = db.ProductionOrders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.SourcePlanId == planId && o.Status != DocStatuses.Cancelled).ToList();
        var orderIds = orders.Select(o => o.Id).ToList();
        var issued = db.ProductionDeliveryItems.AsNoTracking()
            .Where(i => i.OrderId != null && orderIds.Contains(i.OrderId.Value))
            .Join(db.ProductionDeliveries.AsNoTracking(), i => i.DeliveryId, d => d.Id,
                (i, d) => new { Item = i, Delivery = d })
            .Where(x => x.Delivery.SourceType == DeliverySources.FromActual && x.Delivery.IsApproved
                && (x.Delivery.Status == DocStatuses.Issued || x.Delivery.Status == DocStatuses.Completed))
            .Select(x => x.Item).ToList();

        foreach (var order in orders)
        {
            if (order.Items.All(i => i.ProducedQtyKg <= 0.001)) continue;
            if (!db.ProductionExecutions.AsNoTracking().Any(e => e.OrderId == order.Id && e.IsDayClosed))
            {
                failures.Add($"أمر {order.DocumentNumber}: لم تُقفل جلسة الإنتاج الفعلي.");
                continue;
            }
            foreach (var g in order.Items.Where(i => i.ProducedQtyKg > 0.001)
                .GroupBy(i => new { i.ProductId, i.LotId,
                    CustomerId = i.CustomerId ?? order.CustomerId, i.PackagingTypeId }))
            {
                double produced = g.Sum(i => i.ProducedQtyKg);
                double issuedKg = issued.Where(i => i.OrderId == order.Id && i.ProductId == g.Key.ProductId
                    && i.LotId == g.Key.LotId && i.CustomerId == g.Key.CustomerId
                    && i.PackagingTypeId == g.Key.PackagingTypeId).Sum(i => i.QtyKg);
                if (current != null && current.SourceType == DeliverySources.FromActual)
                    issuedKg += current.Items.Where(i => i.OrderId == order.Id && i.ProductId == g.Key.ProductId
                        && i.LotId == g.Key.LotId && i.CustomerId == g.Key.CustomerId
                        && i.PackagingTypeId == g.Key.PackagingTypeId).Sum(i => i.QtyKg);
                if (issuedKg + 0.01 < produced)
                    failures.Add($"أمر {order.DocumentNumber}: بقي {produced - issuedKg:N1} كجم من الصنف #{g.Key.ProductId} "
                        + $"/ الدفعة #{g.Key.LotId} / العبوة #{g.Key.PackagingTypeId} بلا أمر تسليم محرر.");
            }
        }
        return failures;
    }

    /// <summary>
    /// Called inside the caller's serializable transaction, both when the final
    /// delivery is issued and when a previously pending variance is settled.
    /// Never close on the first delivery of a multi-order plan.
    /// </summary>
    internal static string TryAutoClose(DatesErpDbContext db, ICurrentSession session,
        INumberingService numbering, IAuditService audit, int planId, ProductionDelivery current = null)
    {
        var plan = db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == planId);
        if (plan == null || !plan.IsApproved || plan.Status == DocStatuses.Cancelled || plan.IsClosed)
            return "";
        var info = new PlanClosureService(db, session, numbering, audit).GetInfo(planId);
        var missing = MissingIssuedDeliveries(db, planId, current);
        if (!info.CanClose || missing.Count > 0)
            return $" الخطة {plan.DocumentNumber} بقيت مفتوحة حتى اكتمال بنودها وتسويات العجز وأوامر تسليمها.";

        plan.IsClosed = true;
        plan.ClosedDate = db.BusinessNow;
        plan.ClosedBy = session?.UserId;
        plan.Status = DocStatuses.Closed;
        // Persist the new status before SQL recomputes all active reservations.
        db.SaveChanges();
        RecalculateLotReservations(db, plan.Items.Where(i => i.LotId != null).Select(i => i.LotId!.Value));
        audit.Log("إدارة الإنتاج", "إقفال خطة بعد اكتمال جميع تسليمات إنتاجها", "Plan", plan.DocumentNumber, plan.Id,
            new { السبب = "All production deliveries issued", أمر_التسليم = current?.DocumentNumber },
            new { الحالة_الجديدة = "مقفلة", المستخدم = session?.UserId });
        return $" أُقفلت الخطة {plan.DocumentNumber} بعد تحرير جميع تسليمات إنتاجها وتسوية العجز.";
    }

    /// <summary>Recalculate from persisted active plans after closing/reopening a plan.</summary>
    internal static void RecalculateLotReservations(DatesErpDbContext db, IEnumerable<int> lotIds)
    {
        foreach (var lotId in lotIds.Distinct())
        {
            var lot = db.Lots.FirstOrDefault(l => l.Id == lotId);
            if (lot == null) continue;
            double reserved = db.ProductionPlanItems.AsNoTracking().Where(i => i.LotId == lotId && !i.IsClosed)
                .Join(db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id,
                    (i, p) => new { Item = i, Plan = p })
                .Where(x => x.Plan.Status != DocStatuses.Closed && x.Plan.Status != DocStatuses.Cancelled
                    && !x.Plan.IsClosed)
                .Select(x => x.Item).RemainingKg().Sum();
            lot.ReservedQtyKg = Math.Max(0, reserved);
        }
    }
}
