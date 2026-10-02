using DatesErp.Core.Domain.Entities;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// Customer release is measured ONLY on the exact finished-goods identity:
/// customer + production lot + product + package (and source order line).
/// Unapproved, quarantined, rejected, missing or zero-accepted inspections
/// cannot grant even one kilogram, regardless of a 'Passed' header.
/// </summary>
public static class QualityGate
{
    public const string Passed = "Passed";
    public const string Quarantine = "Quarantine";
    public const string Rejected = "Rejected";

    private static (bool ok, string reason, double approved, int? pack) Release(
        DatesErpDbContext db, int? orderId, int? lotId, int? productId,
        int? packagingTypeId, int? customerId)
    {
        if (lotId == null || productId == null)
            return (false, "⛔ حدد الدفعة والصنف للتحقق من الإفراج المعتمد؛ لا يُستنتج الفحص من أمر آخر.", 0, null);
        var lot = db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == lotId);
        if (lot == null || (customerId != null && lot.CustomerId != null && lot.CustomerId != customerId))
            return (false, "⛔ الدفعة غير موجودة أو لا تتبع العميل المحدد.", 0, null);
        int? owner = customerId ?? lot.CustomerId;
        var lines = db.ProductionOrderItems.AsNoTracking()
            .Where(i => i.LotId == lotId && i.ProductId == productId
                && (orderId == null || i.OrderId == orderId)
                && (packagingTypeId == null || i.PackagingTypeId == packagingTypeId))
            .ToList()
            .Where(i => owner == null || i.CustomerId == null || i.CustomerId == owner)
            .ToList();
        if (lines.Count == 0)
            return (false, "⛔ لا يوجد بند إنتاج مطابق لهذه الدفعة والصنف والعميل والعبوة.", 0, null);
        var packs = lines.Select(i => i.PackagingTypeId).Distinct().ToList();
        if (packs.Count != 1)
            return (false, "⛔ تتعدد عبوات هذه الدفعة؛ حدّد العبوة في سند العميل للتحقق من نتيجة فحصها.", 0, null);
        int? pack = packs[0];
        var orderIds = lines.Select(i => i.OrderId).Distinct().ToList();
        var checks = db.QualityChecks.AsNoTracking()
            .Where(c => c.OrderId != null && orderIds.Contains(c.OrderId.Value) && c.IsApproved)
            .ToDictionary(c => c.Id);
        if (checks.Count == 0)
            return (false, "⛔ لا يوجد فحص جودة معتمد لهذه الدفعة/العبوة؛ لا يمكن تسليمها للعميل.", 0, pack);

        var checkIds = checks.Keys.ToList();
        var items = db.QualityCheckItems.AsNoTracking()
            .Where(q => checkIds.Contains(q.CheckId) && q.ProductId == productId && q.LotId == lotId)
            .ToList()
            .Where(q => (q.OrderItemId != null && lines.Any(i => i.Id == q.OrderItemId
                && i.OrderId == checks[q.CheckId].OrderId))
                // Historical, unkeyed checks are usable only if identity is
                // UNAMBIGUOUS. New checks always carry OrderItemId+package.
                || (q.OrderItemId == null && lines.Count == 1 && q.PackagingTypeId == pack))
            .Where(q => q.PackagingTypeId == pack
                && (owner == null || q.CustomerId == null || q.CustomerId == owner))
            .ToList();
        if (items.Count == 0)
            return (false, "⛔ لا يوجد بند فحص معتمد للدفعة والصنف والعبوة المحددة.", 0, pack);
        foreach (var q in items)
        {
            var check = checks[q.CheckId];
            if (check.Decision == Rejected)
                return (false, $"⛔ لا يمكن التسليم للعميل: قرار فحص الجودة «مرفوض تماماً / عوادم» ({check.DocumentNumber}).", 0, pack);
            if (check.Decision == Quarantine)
                return (false, $"⛔ لا يمكن التسليم للعميل: البضاعة تحت «حجز وتحريز مؤقت» ({check.DocumentNumber}).", 0, pack);
            if (check.Decision != Passed)
                return (false, "⛔ قرار الفحص غير صالح للإفراج.", 0, pack);
        }
        double approved = items.Sum(i => i.AcceptedQtyKg);
        if (approved <= 0.001)
            return (false, "⛔ المقبول المعتمد لهذه الدفعة والعبوة صفر — لا يمكن تسليم المرفوض للعميل.", 0, pack);
        return (true, null, approved, pack);
    }

    public static (bool ok, string reason) CustomerDeliveryAllowed(
        DatesErpDbContext db, int? orderId, int? lotId, int? productId,
        int? packagingTypeId = null, int? customerId = null)
    {
        var (ok, reason, _, _) = Release(db, orderId, lotId, productId, packagingTypeId, customerId);
        return (ok, reason);
    }

    /// <summary>Readiness is advisory UI state; approval always revalidates in its transaction.</summary>
    public static (bool ready, string label) DeliveryReadiness(
        DatesErpDbContext db, int? lotId, int productId, int? packagingTypeId = null, int? customerId = null)
    {
        var (ok, reason) = CustomerDeliveryAllowed(db, null, lotId, productId, packagingTypeId, customerId);
        if (ok) return (true, "✔ معتمد");
        if (reason != null && reason.Contains("مرفوض")) return (false, "⛔ مرفوض");
        if (reason != null && reason.Contains("حجز")) return (false, "⛔ محجوز");
        return (false, "⏳ بانتظار الإفراج");
    }

    /// <summary>
    /// Sum every issued customer delivery within the EXACT same stock key.
    /// The current document's matching lines are counted together so splitting
    /// an item across rows does not multiply the approved release.
    /// </summary>
    public static (bool ok, string reason) CustomerDeliveryQtyAllowed(
        DatesErpDbContext db, CustomerDelivery dlv, CustomerDeliveryItem item)
    {
        var (ok, reason, approved, pack) = Release(db, null, item.LotId, item.ProductId,
            item.PackagingTypeId, dlv.CustomerId);
        if (!ok) return (false, reason);
        double delivered = (from di in db.CustomerDeliveryItems.AsNoTracking()
                            join dd in db.CustomerDeliveries.AsNoTracking() on di.DeliveryId equals dd.Id
                            where dd.Id != dlv.Id && dd.IsApproved && dd.CustomerId == dlv.CustomerId
                                && di.ProductId == item.ProductId && di.LotId == item.LotId
                                && di.PackagingTypeId == pack
                            select di.QtyKg).Sum();
        double inCurrent = dlv.Items.Where(i => i.ProductId == item.ProductId && i.LotId == item.LotId
            && i.PackagingTypeId == pack).Sum(i => i.QtyKg);
        if (delivered + inCurrent > approved + 0.01)
        {
            string name = db.Products.AsNoTracking().Where(p => p.Id == item.ProductId)
                .Select(p => p.ProductNameAr).FirstOrDefault() ?? $"صنف #{item.ProductId}";
            return (false, $"⛔ لا يمكن تسليم {inCurrent:N1} كجم من «{name}» / الدفعة #{item.LotId} / العبوة #{pack}: "
                + $"المطابق المعتمد {approved:N1} كجم، والمسلّم سابقاً {delivered:N1} كجم."
                + " لا يُسلَّم إلا المقبول المعتمد لنفس الهوية.");
        }
        return (true, null);
    }

    /// <summary>Receiving into WFG precedes QC approval by policy; customer delivery does not.</summary>
    public static (bool ok, string reason) FinishedGoodsIssueAllowed(DatesErpDbContext db, int orderId)
    {
        if (!db.QualityChecks.AsNoTracking().Any(c => c.OrderId == orderId))
            return (false, "لا يمكن تسليم الإنتاج قبل إقفال يوم الإنتاج وإرساله إلى الجودة — نفّذ الإقفال اليومي أولاً.");
        return (true, null);
    }
}
