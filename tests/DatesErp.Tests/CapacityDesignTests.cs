using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// أمر التطوير — الطاقة الإنتاجية حسب الوردية من شاشة الأصناف:
/// المعدل محسوب (الطاقة ÷ الساعات)، تغيير ساعات الوردية يعيد الحساب،
/// الخطة تقرأ طاقة الصنف تلقائياً وترفض التجاوز بالصيغة المعتمدة.
/// </summary>
public class CapacityDesignTests
{
    private static T Svc<T>(TestHost host) => (T)host.Services.CreateScope().ServiceProvider.GetService(typeof(T));
    private static DatesErpDbContext FreshDb(TestHost host)
        => new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);

    [Fact]
    public void Rate_Is_Computed_From_Capacity_And_Shift_Hours()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();

        // §4 — الطاقة القصوى هي المُدخل، والمعدل يُحسب: 4000 ÷ 8 = 500
        var cap = Svc<ICapacityService>(host);
        var r = cap.SetCapacity(3, 1, 4000); // الصنف 3، الوردية 1 (8 ساعات فعلية)
        Assert.True(r.Ok, r.Message);
        var (rate, capacity) = cap.GetCapacity(3, 1);
        Assert.Equal(500, rate, 1);
        Assert.Equal(4000, capacity);

        // وردية ثانية 6 ساعات: 3000 ÷ 6 = 500
        Assert.True(cap.SetCapacity(3, 2, 3000).Ok);
        var (rate2, cap2) = cap.GetCapacity(3, 2);
        Assert.Equal(500, rate2, 1);
        Assert.Equal(3000, cap2);

        // صنف مختلف بطاقة مختلفة: 8000 ÷ 8 = 1000
        Assert.True(cap.SetCapacity(4, 1, 8000).Ok);
        var (rateB, capB) = cap.GetCapacity(4, 1);
        Assert.Equal(1000, rateB, 1);
        Assert.Equal(8000, capB);

        // الشبكة الكاملة للصنف: كل الورديات ظاهرة بساعاتها وطاقاتها ومعدلاتها المحسوبة
        var rows = cap.GetProductCapacities(3);
        Assert.True(rows.Count >= 3);
        var s1 = rows.First(x => x.ShiftId == 1);
        Assert.Equal(8, s1.ProductionHours, 1);
        Assert.Equal(4000, s1.MaxCapacity);
        Assert.Equal(500, s1.RatePerHour, 1);
    }

    [Fact]
    public void Changing_Shift_Hours_Recomputes_Capacity_From_Stored_Rate()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();

        var cap = Svc<ICapacityService>(host);
        Assert.True(cap.SetCapacity(3, 1, 4000).Ok); // معدل محفوظ = 500/س

        // §5 — تغيير ساعات الوردية 8 → 6 يعيد حساب الطاقة: 500 × 6 = 3000
        var shifts = Svc<IShiftService>(host);
        var r = shifts.SaveShift(1, "الوردية الصباحية", "06:00", "12:00", 6, 0, 6);
        Assert.True(r.Ok, r.Message);

        var (rate, capacity) = cap.GetCapacity(3, 1);
        Assert.Equal(500, rate, 1);      // المعدل محفوظ لكل صنف — لا يُفترض موحداً
        Assert.Equal(3000, capacity);    // الطاقة أُعيد حسابها وفق الساعات الجديدة

        // §12 — كل صنف له معدله الخاص: الصنف 4 بمعدل 1000 → 6 ساعات = 6000
        Assert.True(cap.SetCapacity(4, 1, 6000).Ok);
        var (rateB, capB) = cap.GetCapacity(4, 1);
        Assert.Equal(1000, rateB, 1);
        Assert.Equal(6000, capB);

        // إرجاع الوردية لـ 8 ساعات
        Assert.True(shifts.SaveShift(1, "الوردية الصباحية", "06:00", "14:00", 8, 0, 8).Ok);
    }

    [Fact]
    public void Plan_Rejects_Excess_With_Approved_Message_And_Allows_Remaining_Hours()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();

        // خامة كافية
        var receiving = Svc<IReceivingService>(host);
        var s = receiving.SaveShipment(1, null, null, new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 4000, UnitWeightKg = 20, QtyKg = 80000 } });
        Assert.True(receiving.ApproveShipment(s.Id).Ok);
        int lotId;
        using (var db = FreshDb(host)) lotId = db.Lots.Single().Id;

        // طاقة الصنف 3 في الوردية 1 = 4000 كرتون (500/س × 8 س)
        var cap = Svc<ICapacityService>(host);
        Assert.True(cap.SetCapacity(3, 1, 4000).Ok);

        var planning = Svc<IPlanningService>(host);

        // §7 — محاولة 4500 > 4000 → رفض بالصيغة المعتمدة
        var over = planning.SavePlan("تجاوز", "Daily", "2026-10-01", "2026-10-01", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3, PlannedQtyKg = 33750, PlannedCartons = 4500, ScheduledDate = "2026-10-01", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 } });
        Assert.False(over.Ok);
        Assert.Contains("الكمية المطلوبة أكبر من الطاقة الإنتاجية المتاحة للصنف في هذه الوردية", over.Message);
        Assert.Contains("الطاقة المتاحة: 4,000 كرتون", over.Message);
        Assert.Contains("المطلوب: 4,500 كرتون", over.Message);
        Assert.Contains("الزيادة: 500 كرتون", over.Message);

        // §8 — خطة 2000 كرتون: الساعات المطلوبة = 2000 ÷ 500 = 4 ساعات، والمتبقي 4 ساعات
        var info = planning.GetShiftCapacityInfo(1, 1, "2026-10-01", 3);
        Assert.Equal(8.0, info.TotalHours, 1);

        var ok = planning.SavePlan("ضمن الطاقة", "Daily", "2026-10-01", "2026-10-01", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3, PlannedQtyKg = 15000, PlannedCartons = 2000, ScheduledDate = "2026-10-01", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 } });
        Assert.True(ok.Ok, ok.Message);

        var info2 = planning.GetShiftCapacityInfo(1, 1, "2026-10-01", 3);
        Assert.Equal(4.0, info2.UsedHours, 1);      // 2000 ÷ 500
        Assert.Equal(4.0, info2.RemainingHours, 1); // 8 − 4

        // §9/§10 — استغلال الساعات المتبقية بخطة أخرى لعميل آخر في نفس الوردية (بدفعة مملوكة له)
        using (var db = FreshDb(host))
        {
            db.Customers.Add(new Core.Domain.Entities.Customer { CustomerCode = "C002", CustomerName = "عميل ثان", IsActive = true, RowVersion = Guid.NewGuid().ToByteArray() });
            db.SaveChanges();
        }
        int cust2;
        using (var db = FreshDb(host)) cust2 = db.Customers.Single(c => c.CustomerCode == "C002").Id;
        var s2 = receiving.SaveShipment(cust2, null, null, new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 2000, UnitWeightKg = 20, QtyKg = 40000 } });
        Assert.True(receiving.ApproveShipment(s2.Id).Ok);
        int lot2;
        using (var db = FreshDb(host)) lot2 = db.Lots.OrderBy(l => l.Id).Last().Id;

        var second = planning.SavePlan("استغلال المتبقي", "Daily", "2026-10-01", "2026-10-01", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lot2, CustomerId = cust2, ProductId = 3, PlannedQtyKg = 15000, PlannedCartons = 2000, ScheduledDate = "2026-10-01", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 } });
        Assert.True(second.Ok, second.Message); // 4 ساعات المتبقية بالضبط — مقبول

        // تجاوز إجمالي الساعات الإنتاجية للوردية → مرفوض
        var third = planning.SavePlan("تجاوز الإجمالي", "Daily", "2026-10-01", "2026-10-01", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lot2, CustomerId = cust2, ProductId = 3, PlannedQtyKg = 7500, PlannedCartons = 1000, ScheduledDate = "2026-10-01", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 } });
        Assert.False(third.Ok);
        Assert.Contains("الطاقة الإنتاجية المتاحة", third.Message);
    }
}
