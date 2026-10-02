using System.Globalization;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §أمر التطوير: شاشة الأصناف هي المصدر المركزي لطاقة الإنتاج.
/// القاعدة: الطاقة القصوى لكل صنف/وردية هي المُدخل. معدل الإنتاج/ساعة قيمة محسوبة
/// (الطاقة ÷ ساعات الإنتاج الفعلية للوردية) وليست مدخلاً مستقلاً قابلاً للتعارض.
/// عند تغيير ساعات الوردية يُعاد اشتقاق الطاقة من المعدل المحفوظ لكل صنف.
/// </summary>
public class CapacityService : ServiceBase, ICapacityService
{
    public CapacityService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    public List<ProductCapacityRow> GetProductCapacities(int productId)
    {
        var shifts = Db.Shifts.Where(s => s.IsActive).OrderBy(s => s.Id).ToList();
        var caps = Db.ProductShiftCapacities.AsNoTracking().Where(c => c.ProductId == productId && c.IsActive).ToList();
        var packs = Db.PackagingTypes.AsNoTracking().ToList();
        var rows = new List<ProductCapacityRow>();
        foreach (var sh in shifts)
        {
            double hours = CapacityPolicy.EffectiveHours(sh.EffectiveProductiveHours, sh.TotalHours);
            // صفوف الطاقة لهذه الوردية: العامة (بلا عبوة) ثم الخاصة بكل عبوة
            var shiftCaps = caps.Where(c => c.ShiftId == sh.Id).ToList();
            var generic = shiftCaps.FirstOrDefault(c => c.PackagingTypeId == null);
            if (generic != null || shiftCaps.Count == 0)
            {
                double rate = generic?.HourlyProductionRate ?? 0;
                int max = generic?.ShiftCapacity ?? (rate > 0 ? (int)Math.Round(rate * hours) : 0);
                rows.Add(new ProductCapacityRow
                {
                    ShiftId = sh.Id, ShiftName = sh.ShiftNameAr, ProductionHours = hours,
                    PackagingTypeId = null, PackagingName = "عام (أي عبوة)",
                    MaxCapacity = max, RatePerHour = rate > 0 ? Math.Round(rate, 1) : 0
                });
            }
            foreach (var pc in shiftCaps.Where(c => c.PackagingTypeId != null))
            {
                double rate = pc.HourlyProductionRate;
                int max = pc.ShiftCapacity > 0 ? pc.ShiftCapacity : (rate > 0 ? (int)Math.Round(rate * hours) : 0);
                rows.Add(new ProductCapacityRow
                {
                    ShiftId = sh.Id, ShiftName = sh.ShiftNameAr, ProductionHours = hours,
                    PackagingTypeId = pc.PackagingTypeId,
                    PackagingName = packs.FirstOrDefault(p => p.Id == pc.PackagingTypeId)?.PackageNameAr ?? "عبوة",
                    MaxCapacity = max, RatePerHour = rate > 0 ? Math.Round(rate, 1) : 0
                });
            }
        }
        return rows;
    }

    public OpResult SetCapacity(int productId, int shiftId, int maxCartons)
        => SetCapacity(productId, shiftId, null, maxCartons);

    public OpResult SetCapacity(int productId, int shiftId, int? packagingTypeId, int maxCartons)
    {
        Require("products", "Edit");
        if (maxCartons < 0) return OpResult.Fail("الطاقة القصوى لا يمكن أن تكون سالبة.");
        var shift = Db.Shifts.AsNoTracking().FirstOrDefault(s => s.Id == shiftId);
        if (shift == null) return OpResult.Fail("الوردية غير موجودة.");

        return RunOp(() =>
        {
            double hours = CapacityPolicy.EffectiveHours(shift.EffectiveProductiveHours, shift.TotalHours);
            // المعدل يُشتق من الطاقة القصوى وساعات الوردية — قيمة محسوبة
            double rate = hours > 0 ? Math.Round((double)maxCartons / hours, 1) : 0;

            var cap = Db.ProductShiftCapacities.FirstOrDefault(c =>
                c.ProductId == productId && c.ShiftId == shiftId && c.PackagingTypeId == packagingTypeId);
            // قراءة الوردية مجدداً بلا تتبع لضمان أحدث الساعات داخل المعاملة
            var shiftNow = Db.Shifts.AsNoTracking().FirstOrDefault(x => x.Id == shiftId);
            if (shiftNow != null) hours = shiftNow.EffectiveProductiveHours > 0 ? shiftNow.EffectiveProductiveHours : (shiftNow.TotalHours > 0 ? shiftNow.TotalHours : 8);
            rate = hours > 0 ? Math.Round((double)maxCartons / hours, 1) : 0;
            if (cap == null)
            {
                cap = new Core.Domain.Entities.ProductShiftCapacity
                { ProductId = productId, ShiftId = shiftId, PackagingTypeId = packagingTypeId, IsActive = true };
                Db.ProductShiftCapacities.Add(cap);
            }
            cap.HourlyProductionRate = rate;
            cap.ShiftCapacity = maxCartons;
            Db.SaveChanges();
            return OpResult.Success($"تم حفظ الطاقة: {maxCartons} كرتون — المعدل المحسوب {rate} كرتون/ساعة.");
        });
    }

    public (double rate, int capacity) GetCapacity(int productId, int shiftId)
        => GetCapacity(productId, shiftId, null);

    /// <summary>
    /// §الطاقة لصنف + عبوة + وردية: يبحث أولاً عن طاقة خاصة بالعبوة، ثم يرجوع للطاقة العامة
    /// للصنف/الوردية، ثم لمعدل الصنف العام. (سكري 7.5 كجم ≠ سكري 4 كجم)
    /// </summary>
    public (double rate, int capacity) GetCapacity(int productId, int shiftId, int? packagingTypeId)
    {
        // §مصدر واحد للقراءة: CapacityPolicy — ولا افتراض 8 ساعات عند غياب الوردية
        var (rate, capacity, _) = CapacityPolicy.Resolve(Db, productId, shiftId, packagingTypeId);
        return (rate, capacity);
    }

    public int RecomputeForShift(int shiftId)
    {
        var shift = Db.Shifts.FirstOrDefault(s => s.Id == shiftId);
        if (shift == null) return 0;
        double hours = CapacityPolicy.EffectiveHours(shift.EffectiveProductiveHours, shift.TotalHours);
        var caps = Db.ProductShiftCapacities.Where(c => c.ShiftId == shiftId && c.IsActive).ToList();
        foreach (var cap in caps)
        {
            if (cap.HourlyProductionRate > 0)
                cap.ShiftCapacity = (int)Math.Round(cap.HourlyProductionRate * hours);
        }
        Db.SaveChanges();
        return caps.Count;
    }
    /// <summary>§B73: الإنتاج بالساعة لكل صنف — المصدر المعتمد لطاقات الورديات.
    /// الوحدة مرتبطة بوحدة الصنف نفسها ولا تُفرض وحدة ثابتة.</summary>
    public OpResult SaveHourlyRate(int productId, double ratePerHour)
    {
        Require("products", "Edit");
        if (double.IsNaN(ratePerHour) || ratePerHour <= 0)
            return OpResult.Fail("الإنتاج بالساعة يجب أن يكون رقماً صالحاً أكبر من صفر — القيم السالبة والمعدومة ممنوعة.");
        return RunOp(() =>
        {
            var p = Db.Products.FirstOrDefault(x => x.Id == productId) ?? throw new DomainException("الصنف غير موجود.");
            p.HourlyProductionRate = ratePerHour;
            // §منع التلاعب: الشاشة الجديدة هي المصدر — تُوقَف أرقام الورديات اليدوية القديمة
            // حتى لا يقرأ التخطيط رقماً غير محسوب من الإنتاج بالساعة × ساعات الوردية.
            foreach (var c in Db.ProductShiftCapacities.Where(c => c.ProductId == productId && c.IsActive))
                c.IsActive = false;
            Db.SaveChanges();
            return OpResult.Success($"تم حفظ الإنتاج بالساعة للصنف «{p.ProductNameAr}»: {ratePerHour:N0} {p.UnitOfMeasure}/ساعة.");
        });
    }

    /// <summary>§B73: تصفير الإنتاج بالساعة (زر حذف في شاشة الطاقات).</summary>
    public OpResult ClearHourlyRate(int productId)
    {
        Require("products", "Edit");
        return RunOp(() =>
        {
            var p = Db.Products.FirstOrDefault(x => x.Id == productId) ?? throw new DomainException("الصنف غير موجود.");
            p.HourlyProductionRate = 0;
            Db.SaveChanges();
            return OpResult.Success($"أُزيل الإنتاج بالساعة للصنف «{p.ProductNameAr}».");
        });
    }

    /// <summary>§B73: طاقة اليوم كاملة = مجموع (الإنتاج بالساعة × ساعات الإنتاج الفعلية) لكل الورديات النشطة.</summary>
    public double GetDayCapacity(int productId)
    {
        var hourly = Db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => p.HourlyProductionRate).FirstOrDefault();
        if (hourly <= 0) return 0;
        return Db.Shifts.AsNoTracking().Where(s => s.IsActive).AsEnumerable()
            .Sum(s => hourly * CapacityPolicy.EffectiveHours(s.EffectiveProductiveHours, s.TotalHours));
    }
}

/// <summary>§الورديات تحدد الوقت المتاح فقط — لا طاقة للأصناف هنا.</summary>
public class ShiftService : ServiceBase, IShiftService
{
    private readonly ICapacityService _capacity;

    public ShiftService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering, ICapacityService capacity)
        : base(db, session, numbering)
    {
        _capacity = capacity;
    }

    /// <summary>§B84/V3: وقت وردية صالح = HH:mm (يقبل 6:00 أيضاً).</summary>
    private static bool IsShiftTime(string s)
        => TimeSpan.TryParseExact((s ?? "").Trim(), new[] { "hh\\:mm", "h\\:mm" }, CultureInfo.InvariantCulture, out _);

    public OpResult SaveShift(int? id, string name, string start, string end, double totalHours, double downtimeHours, double effectiveHours)
    {
        Require("products", "Edit");
        if (string.IsNullOrWhiteSpace(name)) return OpResult.Fail("أدخل اسم الوردية.");
        // §B84/V3: الأوقات حرة النص كانت تُخزَّن كما هي فخطأ كتابي واحد يكسر حساب الطاقة —
        // الآن صيغة HH:mm إلزامية (كل البذور والاختبارات عليها أصلاً).
        if (!IsShiftTime(start)) return OpResult.Fail("وقت بداية الوردية غير صحيح — الصيغة المطلوبة HH:mm (مثال: 06:00).");
        if (!IsShiftTime(end)) return OpResult.Fail("وقت نهاية الوردية غير صحيح — الصيغة المطلوبة HH:mm (مثال: 14:00).");

        return RunOp(() =>
        {
            var shift = id == null ? new Core.Domain.Entities.Shift() : Db.Shifts.FirstOrDefault(s => s.Id == id);
            if (id != null && shift == null) return OpResult.Fail("الوردية غير موجودة.");

            shift.ShiftNameAr = name;
            shift.StartTime = start;
            shift.EndTime = end;
            shift.TotalHours = totalHours;
            shift.PlannedDowntimeHours = downtimeHours;
            // ساعات الإنتاج الفعلية = الإجمالية − التوقفات (إن لم تُدخل يدوياً)
            shift.EffectiveProductiveHours = effectiveHours > 0 ? effectiveHours : Math.Max(0, totalHours - downtimeHours);
            shift.IsActive = true;

            if (id == null) Db.Shifts.Add(shift);
            Db.SaveChanges();

            // §تغيير ساعات الوردية → إعادة حساب طاقة كل صنف لهذه الوردية من معدله المحفوظ
            var recomputed = _capacity.RecomputeForShift(shift.Id);
            return OpResult.Success($"تم حفظ الوردية «{name}». أُعيد حساب الطاقة لـ {recomputed} صنف وفق الساعات الجديدة.");
        });
    }

    public OpResult DeleteShift(int id)
    {
        Require("products", "Delete");
        var shift = Db.Shifts.FirstOrDefault(s => s.Id == id);
        if (shift == null) return OpResult.Fail("الوردية غير موجودة.");
        if (Db.ProductionPlanItems.Any(i => i.SuggestedShiftId == id))
            return OpResult.Fail("لا يمكن حذف وردية مستخدمة في خطط إنتاج.");
        return RunOp(() =>
        {
            Db.Shifts.Remove(shift);
            Db.SaveChanges();
            return OpResult.Success("تم حذف الوردية.");
        });
    }
}
