using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §مصدر واحد لترتيب أسبقية قراءة الطاقة الإنتاجية.
///
/// التسلسل المعتمد (لا يُقرأ من أي مكان آخر):
///   1) طاقة الصنف في هذه الوردية لهذه العبوة        (ProductShiftCapacity بعبوة)
///   2) طاقة الصنف في هذه الوردية لأي عبوة            (ProductShiftCapacity بلا عبوة)
///   3) المعدل العام للصنف                           (Product.HourlyProductionRate)
///   4) لا شيء — صفر. والصفر يعني «غير معرَّف» ويُبلَّغ عنه، لا يُعوَّض برقم افتراضي.
///
/// §لا معدل افتراضي في الكود: كان التخطيط يعوّض 500 كرتون/ساعة بصمت عند غياب التعريف،
/// فتظهر أرقام مقترحة لا أساس لها. الغياب الآن صفر صريح.
///
/// §الطاقة القصوى قيمة مشتقة: المعدل × ساعات الإنتاج الفعلية للوردية.
/// </summary>
public static class CapacityPolicy
{
    /// <summary>ساعات الإنتاج الفعلية للوردية — الوقوع على ساعات العمل إن لم تُعرَّف.</summary>
    public static double EffectiveHours(double effectiveProductiveHours, double totalHours)
        => effectiveProductiveHours > 0 ? effectiveProductiveHours : (totalHours > 0 ? totalHours : 0);

    public static double EffectiveHours(double effectiveProductiveHours, double totalHours, double plannedDowntimeHours)
        => effectiveProductiveHours > 0 ? effectiveProductiveHours : Math.Max(0, totalHours - Math.Max(0, plannedDowntimeHours));

    /// <summary>
    /// يقرأ الطاقة بالترتيب المعتمد. يعيد المعدل والطاقة القصوى ومصدر القرار
    /// حتى تستطيع الشاشات أن تقول للمستخدم من أين جاء الرقم.
    /// </summary>
    public static (double Rate, int Capacity, string Source) Resolve(
        DatesErpDbContext db, int productId, int shiftId, int? packagingTypeId = null)
    {
        var shift = db.Shifts.AsNoTracking().FirstOrDefault(s => s.Id == shiftId);
        double hours = shift == null ? 0 : EffectiveHours(shift.EffectiveProductiveHours, shift.TotalHours);

        Core.Domain.Entities.ProductShiftCapacity cap = null;
        if (packagingTypeId != null)
            cap = db.ProductShiftCapacities.AsNoTracking().FirstOrDefault(c =>
                c.ProductId == productId && c.ShiftId == shiftId && c.PackagingTypeId == packagingTypeId && c.IsActive);
        if (cap == null || cap.HourlyProductionRate <= 0)
            cap = db.ProductShiftCapacities.AsNoTracking().FirstOrDefault(c =>
                c.ProductId == productId && c.ShiftId == shiftId && c.PackagingTypeId == null && c.IsActive);

        if (cap != null && cap.HourlyProductionRate > 0)
        {
            int max = cap.ShiftCapacity > 0 ? cap.ShiftCapacity : (int)Math.Round(cap.HourlyProductionRate * hours);
            return (cap.HourlyProductionRate, max,
                cap.PackagingTypeId != null ? "طاقة الصنف في هذه الوردية لهذه العبوة" : "طاقة الصنف في هذه الوردية");
        }

        double general = db.Products.AsNoTracking().Where(p => p.Id == productId)
            .Select(p => p.HourlyProductionRate).FirstOrDefault();
        if (general > 0)
            return (general, (int)Math.Round(general * hours), "المعدل العام للصنف (بلا إعداد لهذه الوردية)");

        return (0, 0, "غير معرَّف");
    }

    /// <summary>المعدل بالساعة فقط — لمن يحتاج المعدل دون الطاقة.</summary>
    public static double RateFor(DatesErpDbContext db, int productId, int shiftId, int? packagingTypeId = null)
        => Resolve(db, productId, shiftId, packagingTypeId).Rate;

    /// <summary>
    /// §المعدل قيمة محسوبة لا مدخل مستقل: الطاقة القصوى ÷ ساعات الإنتاج الفعلية.
    /// </summary>
    public static double DeriveRate(int maxCartons, double effectiveHours)
        => effectiveHours > 0 ? Math.Round((double)maxCartons / effectiveHours, 1) : 0;

    /// <summary>§الطاقة القصوى قيمة مشتقة: المعدل × ساعات الإنتاج الفعلية.</summary>
    public static int DeriveCapacity(double ratePerHour, double effectiveHours)
        => ratePerHour > 0 && effectiveHours > 0 ? (int)Math.Round(ratePerHour * effectiveHours) : 0;

    /// <summary>الساعات المطلوبة لكمية مخططة بمعدل معيّن.</summary>
    public static double RequiredHours(int cartons, double ratePerHour)
        => ratePerHour > 0 ? cartons / ratePerHour : 0;
}
