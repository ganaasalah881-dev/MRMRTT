using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §الطاقة حسب العبوة/المواصفة: نفس الصنف بعبوتين مختلفتين له طاقتان مختلفتان
/// (سكري 7.5 كجم = 4,000 كرتون/وردية، سكري 4 كجم = 8,000 كرتون/وردية)،
/// والخطة تُفحص بطاقة العبوة المحددة لا طاقة الصنف العامة.
/// </summary>
public class PackagingCapacityTests
{
    private static T Svc<T>(TestHost host) => (T)host.Services.CreateScope().ServiceProvider.GetService(typeof(T));
    private static DatesErpDbContext FreshDb(TestHost host)
        => new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);

    private static int SeedLot(TestHost host, double qtyKg)
    {
        var receiving = Svc<IReceivingService>(host);
        var s = receiving.SaveShipment(1, null, null, new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackageCount = (int)(qtyKg / 20), UnitWeightKg = 20, QtyKg = qtyKg } });
        Assert.True(s.Ok, s.Message);
        Assert.True(receiving.ApproveShipment(s.Id).Ok);
        using var db = FreshDb(host);
        return db.Lots.OrderByDescending(l => l.Id).First().Id;
    }

    [Fact]
    public void Same_Product_Different_Packaging_Has_Different_Capacity()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();

        int pack5, pack10;
        using (var db = FreshDb(host))
        {
            pack5 = db.PackagingTypes.OrderBy(p => p.Id).First().Id;   // عبوة 5 كجم
            pack10 = db.PackagingTypes.OrderBy(p => p.Id).Skip(1).First().Id; // عبوة 10 كجم
        }

        var cap = Svc<ICapacityService>(host);
        // الصنف 3، الوردية 1: عبوة 5 كجم = 4,000 كرتون | عبوة 10 كجم = 8,000 كرتون
        Assert.True(cap.SetCapacity(3, 1, pack5, 4000).Ok);
        Assert.True(cap.SetCapacity(3, 1, pack10, 8000).Ok);

        var (r5, c5) = cap.GetCapacity(3, 1, pack5);
        var (r10, c10) = cap.GetCapacity(3, 1, pack10);
        Assert.Equal(4000, c5);   // طاقة العبوة 5 كجم
        Assert.Equal(8000, c10);  // طاقة العبوة 10 كجم — مختلفة
        Assert.True(r10 > r5);    // المعدل يختلف حسب العبوة
    }

    [Fact]
    public void Plan_Capacity_Check_Uses_Packaging_Specific_Capacity()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lotId = SeedLot(host, 200000); // خامة كافية

        int pack5, pack10;
        using (var db = FreshDb(host))
        {
            pack5 = db.PackagingTypes.OrderBy(p => p.Id).First().Id;
            pack10 = db.PackagingTypes.OrderBy(p => p.Id).Skip(1).First().Id;
        }

        var cap = Svc<ICapacityService>(host);
        Assert.True(cap.SetCapacity(3, 1, pack5, 4000).Ok);   // عبوة 5 كجم = 4,000
        Assert.True(cap.SetCapacity(3, 1, pack10, 8000).Ok);  // عبوة 10 كجم = 8,000

        var planning = Svc<IPlanningService>(host);

        // 5,000 كرتون بعبوة 5 كجم (طاقتها 4,000) → تُرفض
        var rejected = planning.SavePlan("تجاوز عبوة 5", "Daily", "2026-12-01", "2026-12-01", 1, 1,
            new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3,
                        PackagingTypeId = pack5, PlannedQtyKg = 5000 * 5, PlannedCartons = 5000,
                        ScheduledDate = "2026-12-01", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 }
            });
        Assert.False(rejected.Ok);
        Assert.Contains("الطاقة", rejected.Message);

        // نفس الـ 5,000 كرتون بعبوة 10 كجم (طاقتها 8,000) → تُقبل
        var accepted = planning.SavePlan("ضمن عبوة 10", "Daily", "2026-12-02", "2026-12-02", 1, 1,
            new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3,
                        PackagingTypeId = pack10, PlannedQtyKg = 5000 * 10, PlannedCartons = 5000,
                        ScheduledDate = "2026-12-02", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 }
            });
        Assert.True(accepted.Ok, accepted.Message);
    }

    [Fact]
    public void Capacity_Falls_Back_To_Generic_When_No_Packaging_Capacity()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();

        var cap = Svc<ICapacityService>(host);
        // طاقة عامة للصنف 3 في الوردية 1 (بلا عبوة) = 6000
        Assert.True(cap.SetCapacity(3, 1, null, 6000).Ok);

        int pack5;
        using (var db = FreshDb(host)) pack5 = db.PackagingTypes.OrderBy(p => p.Id).First().Id;

        // لا توجد طاقة خاصة بالعبوة → ترجوع للطاقة العامة 6000
        var (r, c) = cap.GetCapacity(3, 1, pack5);
        Assert.Equal(6000, c);
    }
}
