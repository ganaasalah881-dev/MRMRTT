using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B87 — اختبارات محرك التوزيع العادل v2:
/// فلتر التحويل الرسمي، تعبئة كل الورديات، تخطي الجمعة، الأرصدة الواعية بالأوامر،
/// صدق الفتات (لا تجاوز)، والعميل الفارغ (M6) + ترحيل الصفر.
/// </summary>
public class FairV2Tests
{
    /// <summary>بذرة صغيرة: دفعة خام مباشرة (بلا شحنة — كافية لاختبارات المحرك المعزولة).</summary>
    private static (TestHost host, int custId, int lotId) SeedLot(double kg, int rawProductId = 1, bool withCustomer = true)
    {
        var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        int? cid = null;
        if (withCustomer)
        {
            db.Customers.Add(new Customer { CustomerCode = "FV2", CustomerName = "عميل الاختبار", IsActive = true });
            db.SaveChanges();
            cid = db.Customers.Single(c => c.CustomerCode == "FV2").Id;
        }
        var lot = new Lot
        {
            LotCode = "LOT-FV2",
            ProductId = rawProductId, CustomerId = cid,
            InitialQtyKg = kg, InStockQtyKg = kg, Status = "Approved"
        };
        db.Lots.Add(lot);
        db.SaveChanges();
        return (host, cid ?? -1, lot.Id);
    }

    private static IPlanningService Plan(TestHost h)
        => h.Services.CreateScope().ServiceProvider.GetRequiredService<IPlanningService>();

    private static string D(DateTime d) => d.ToString("dd/MM/yyyy");

    /// <summary>يوم عمل مضمون (ليس جمعة) بعد اليوم بعدد أيام معين.</summary>
    private static DateTime Workday(int addDays)
    {
        var d = DateTime.Today.AddDays(addDays);
        if (d.DayOfWeek == DayOfWeek.Friday) d = d.AddDays(1);
        return d;
    }

    [Fact]
    public void ConversionFilter_RawKhalas_OnlyAllowedProduct_Planned()
    {
        var (host, _, _) = SeedLot(30000);
        var p = Plan(host).SuggestFairDistribution(D(DateTime.Today), D(DateTime.Today.AddDays(6)), 1, 1);
        Assert.True(p.Ok, p.Message);
        Assert.NotEmpty(p.Rows);
        // خام خلاص (1) ← خلاص ممتاز (3) فقط — سكري فاخر (4) مرفوض من هذه الدفعة
        Assert.All(p.Rows, r => Assert.Equal(3, r.ProductId));
    }

    [Fact]
    public void MultiShift_Overflow_SpreadsAcrossShifts_WithExactTotal()
    {
        var (host, _, _) = SeedLot(30000);
        var day = D(Workday(0));
        var p = Plan(host).SuggestFairDistribution(day, day, 1, 1);
        Assert.True(p.Ok, p.Message);
        // الصباحية تسع 20000 (8س×500×5كجم) فيفيض الباقي للمسائية
        Assert.True(p.Rows.Select(r => r.ShiftId).Distinct().Count() >= 2,
            "لم يفض المحرك إلى وردية ثانية رغم امتلاء الأولى");
        Assert.All(p.Rows, r => Assert.False(string.IsNullOrWhiteSpace(r.ShiftName)));
        Assert.Equal(30000, p.Rows.Sum(r => r.PlannedQtyKg), 0);
    }

    [Fact]
    public void Friday_Skipped_ByDefault_Produced_WhenOptedIn()
    {
        var (host, _, _) = SeedLot(20000);
        var fri = DateTime.Today;
        while (fri.DayOfWeek != DayOfWeek.Friday) fri = fri.AddDays(1);
        var f = D(fri);

        var p = Plan(host).SuggestFairDistribution(f, f, 1, 1);
        Assert.False(p.Ok);
        Assert.Contains("جمعة", p.Message);
        Assert.Equal(0, p.DaysUsed); // §L3: لا أيام إنتاج

        var p2 = Plan(host).SuggestFairDistribution(f, f, 1, 1, null, null, excludeFriday: false);
        Assert.True(p2.Ok, p2.Message);
        Assert.NotEmpty(p2.Rows);
    }

    [Fact]
    public void WeekRange_HasNoFridayRow_And_DaysUsed_CountsProducingDaysOnly()
    {
        var (host, _, _) = SeedLot(20000);
        var sat = DateTime.Today;
        while (sat.DayOfWeek != DayOfWeek.Saturday) sat = sat.AddDays(1);
        var p = Plan(host).SuggestFairDistribution(D(sat), D(sat.AddDays(6)), 1, 1, null, dailyKgOverride: 5000);
        Assert.True(p.Ok, p.Message);
        foreach (var r in p.Rows)
        {
            var rd = DateTime.ParseExact(r.Date, DatesErp.Core.Common.UiFormat.DatePattern, null);
            Assert.NotEqual(DayOfWeek.Friday, rd.DayOfWeek);
            Assert.True(r.PlannedCartons > 0 && r.PlannedQtyKg > 0);
        }
        // 20000 ÷ 5000 = 4 أيام إنتاج فعلية (السبت–الثلاثاء) — لا أيام صفرية (L3)
        Assert.Equal(4, p.DaysUsed);
        Assert.Equal(20000, p.Rows.Sum(r => r.PlannedQtyKg), 0);
        foreach (var g in p.Rows.GroupBy(r => r.Date))
            Assert.True(g.Sum(r => r.PlannedQtyKg) <= 5000 + 0.01, $"اليوم {g.Key} تجاوز الحصة");
    }

    [Fact]
    public void TargetProduct_ViolatingConversion_LoudSkip_NoRows()
    {
        var (host, _, _) = SeedLot(20000, rawProductId: 1);
        // سكري فاخر (4) من خام خلاص (1) — مخالف لبطاقة المنتج
        var p = Plan(host).SuggestFairDistribution(D(DateTime.Today), D(DateTime.Today.AddDays(6)), 1, 1,
            targetProductId: 4, dailyKgOverride: 10000);
        Assert.False(p.Ok);
        Assert.Contains("غير مسموح", p.Message);
        Assert.NotEmpty(p.SkippedNotes);
    }

    [Fact]
    public void OrphanRaw_NoAllowedProduct_LoudSkip()
    {
        var host = new TestHost();
        host.LoginAsAdmin();
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
            var raw = master.SaveProductFull(null, "001-900", "خام يتيم", "001", "Raw", "كجم", 0, 0, 0, null);
            db.Lots.Add(new Lot
            {
                LotCode = "LOT-ORPH", ProductId = raw.Id, CustomerId = null,
                InitialQtyKg = 5000, InStockQtyKg = 5000, Status = "Approved"
            });
            db.SaveChanges();
        }
        var p = Plan(host).SuggestFairDistribution(D(DateTime.Today), D(DateTime.Today.AddDays(6)), 1, 1);
        Assert.False(p.Ok);
        Assert.Contains("لا يوجد أي صنف تام مسموح", p.Message);
        Assert.NotEmpty(p.SkippedNotes);
    }

    [Fact]
    public void OrderAware_LivePlanConsumesLot_NothingLeftForFair()
    {
        var (host, custId, lotId) = SeedLot(20000);
        var future = D(Workday(2));
        using (var scope = host.Services.CreateScope())
        {
            var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            // خطة حية تستهلك الدفعة كاملة: 4000 كرتون × 5كجم = 8 ساعات بالصباحية ✓
            var plan = planning.SavePlan("خطة حجز", "Daily", future, future, 1, 1, new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = custId, ProductId = 3, PackagingTypeId = 1,
                        PlannedQtyKg = 20000, PlannedCartons = 4000, ScheduledDate = future,
                        SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 }
            });
            Assert.True(plan.Ok, plan.Message);
        }
        var p = Plan(host).SuggestFairDistribution(D(DateTime.Today), D(DateTime.Today.AddDays(6)), 1, 1);
        Assert.False(p.Ok);
        Assert.Contains("لا توجد أرصدة", p.Message);
    }

    [Fact]
    public void Crumb_BelowFullCarton_LeftHonestly_NeverExceeded()
    {
        var (host, _, _) = SeedLot(20003); // 3 كجم فتات دون العبوة (5كجم)
        var p = Plan(host).SuggestFairDistribution(D(DateTime.Today), D(DateTime.Today.AddDays(6)), 1, 1);
        Assert.True(p.Ok, p.Message);
        Assert.All(p.Rows, r =>
        {
            Assert.True(r.PlannedCartons >= 1, "صف صفري ممنوع");
            Assert.True(r.PlannedQtyKg > 0, "كمية صفرية ممنوعة");
        });
        Assert.Equal(20000, p.Rows.Sum(r => r.PlannedQtyKg), 0);
        Assert.True(Math.Abs(p.TotalRemainingKg - 3) < 0.05, $"الفتات المتوقع 3 كجم والمبلغ {p.TotalRemainingKg}");
    }

    [Fact]
    public void NullCustomer_FairBucket_And_SavePlan_EndToEnd()
    {
        var (host, _, lotId) = SeedLot(15000, withCustomer: false);
        var p = Plan(host).SuggestFairDistribution(D(DateTime.Today), D(DateTime.Today.AddDays(6)), 1, 1);
        Assert.True(p.Ok, p.Message);
        Assert.All(p.Rows, r => Assert.Null(r.CustomerId));
        Assert.Equal("بدون عميل", p.Customers.Single().CustomerName);
        Assert.Null(p.Customers.Single().CustomerId);

        var future = D(Workday(2));
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var plan = planning.SavePlan("خطة بلا عميل", "Daily", future, future, 1, 1, new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = null, ProductId = 3, PackagingTypeId = 1,
                        PlannedQtyKg = 10000, PlannedCartons = 2000, ScheduledDate = future,
                        SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 }
            });
            Assert.True(plan.Ok, plan.Message);
            Assert.Null(db.ProductionPlanItems.Single(i => i.PlanId == plan.Id).CustomerId);
        }
    }

    [Fact]
    public void Migrator_ZeroCustomer_Nullified()
    {
        var (host, _, lotId) = SeedLot(10000);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        db.Lots.Single(l => l.Id == lotId).CustomerId = 0;
        db.SaveChanges();
        var report = SchemaMigrator.Migrate(db);
        Assert.Null(db.Lots.Single(l => l.Id == lotId).CustomerId);
        Assert.Contains(report, x => x.Contains("عميل-صفر"));
    }
}
