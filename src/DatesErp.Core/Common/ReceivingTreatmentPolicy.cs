using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;

namespace DatesErp.Core.Common;

/// <summary>قواعد تقسيم بند الاستلام مشتركة بين الشاشة والخدمة؛ لا تغيّر رصيداً أو تنشئ صنفاً.</summary>
// Legacy grade policy retained for historical-data regression tests only. New receipts use ReceivingLineTreatmentPolicy.
public static class ReceivingTreatmentPolicy
{
    public const double QuantityTolerance = 0.001;

    public static string Destination(string value)
    {
        var code = (value ?? "").Trim();
        if (code.Length == 0 || code.Equals(ReceiptDestinations.RawStore, StringComparison.OrdinalIgnoreCase))
            return ReceiptDestinations.RawStore;
        if (code.Equals(ReceiptDestinations.Treatment, StringComparison.OrdinalIgnoreCase))
            return ReceiptDestinations.Treatment;
        throw new DomainException("وجهة الاستلام غير صالحة — اختر مخزن الخام أو مستودع المعالجة.", "INVALID_DESTINATION");
    }

    public static string Level(string value)
    {
        var level = (value ?? "").Trim();
        if (level.Length == 0) return InfestationLevels.Medium;
        foreach (var code in InfestationLevels.All)
        {
            var arabic = code switch
            {
                InfestationLevels.Light => "خفيفة",
                InfestationLevels.High => "شديدة",
                _ => "متوسطة"
            };
            if (level.Equals(code, StringComparison.OrdinalIgnoreCase) || level == arabic || level == "إصابة " + arabic)
                return code;
        }
        throw new DomainException("درجة الإصابة غير صالحة — المسموح: خفيفة، متوسطة، شديدة.", "INVALID_LEVEL");
    }

    /// <summary>
    /// فارغ = جزء متوسط بكامل الكمية. صفر طرود في جزء = توزيع تلقائي للباقي.
    /// نحترم الأعداد الموجبة المدخلة ونوزع الباقي بطريقة أكبر البواقي كي لا يزيد أو ينقص طرد.
    /// النتيجة نسخ مستقلة؛ إلغاء النافذة لا يغيّر أجزاء السند الأصلي.
    /// </summary>
    public static List<TreatmentPartDto> NormalizeParts(double itemQtyKg, int itemPackages,
        IEnumerable<TreatmentPartDto> parts)
    {
        if (!double.IsFinite(itemQtyKg) || itemQtyKg <= 0 || itemPackages < 0)
            throw new DomainException("كمية البند أو عدد طروده غير صالح.", "INVALID_QUANTITY");
        var source = parts?.ToList() ?? new List<TreatmentPartDto>();
        if (source.Count == 0)
            source.Add(new TreatmentPartDto { QtyKg = itemQtyKg, PackageCount = itemPackages });

        var result = new List<TreatmentPartDto>();
        foreach (var part in source)
        {
            if (part == null || !double.IsFinite(part.QtyKg) || part.QtyKg <= 0 || part.PackageCount < 0)
                throw new DomainException("لا يجوز جزء بكمية صفر أو سالبة أو غير رقمية، ولا عدد طرود سالب.", "INVALID_PART");
            var level = Level(part.InfestationLevel);
            double hours = InfestationLevels.DefaultHours(level);
            if (part.DurationHours is double specified &&
                (!double.IsFinite(specified) || Math.Abs(specified - hours) > QuantityTolerance))
                throw new DomainException("مدة درجة الإصابة في الاستلام ثابتة: خفيفة 120، متوسطة 168، شديدة 240 ساعة.", "INVALID_DURATION");
            result.Add(new TreatmentPartDto
            {
                InfestationLevel = level, QtyKg = part.QtyKg, PackageCount = part.PackageCount,
                DurationHours = hours, Notes = part.Notes
            });
        }
        double sum = result.Sum(p => p.QtyKg);
        if (!double.IsFinite(sum) || Math.Abs(sum - itemQtyKg) > QuantityTolerance)
            throw new DomainException($"مجموع أجزاء درجات الإصابة ({sum:N3} كجم) لا يساوي كمية البند ({itemQtyKg:N3} كجم).", "PARTS_MISMATCH");

        long specifiedPackages = result.Sum(p => (long)p.PackageCount);
        if (specifiedPackages > itemPackages)
            throw new DomainException("مجموع طرود الأجزاء أكبر من عدد طرود البند.", "PACKAGES_MISMATCH");
        int remaining = itemPackages - (int)specifiedPackages;
        var automatic = result.Select((p, i) => (part: p, index: i)).Where(x => x.part.PackageCount == 0).ToList();
        if (automatic.Count == 0 && remaining != 0)
            throw new DomainException("مجموع طرود الأجزاء لا يساوي عدد طرود البند. صحّح الأعداد أو ضع صفراً للتوزيع التلقائي.", "PACKAGES_MISMATCH");
        if (automatic.Count > 0 && remaining > 0)
        {
            double weight = automatic.Sum(x => x.part.QtyKg);
            var shares = automatic.Select(x =>
            {
                double quota = x.part.QtyKg / weight * remaining;
                return (x.part, x.index, whole: (int)Math.Floor(quota), fraction: quota - Math.Floor(quota));
            }).ToList();
            foreach (var share in shares) share.part.PackageCount = share.whole;
            int extra = remaining - shares.Sum(x => x.whole);
            foreach (var share in shares.OrderByDescending(x => x.fraction).ThenBy(x => x.index).Take(extra))
                share.part.PackageCount++;
        }
        if (result.Sum(p => (long)p.PackageCount) != itemPackages)
            throw new DomainException("تعذر توزيع الطرود دون فرق؛ راجع تقسيم الكمية.", "PACKAGES_MISMATCH");
        return result;
    }
}
