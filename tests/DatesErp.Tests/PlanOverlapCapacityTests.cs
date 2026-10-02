using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §منع تداخل الخطط حسب الطاقة (لا منع أعمى): يُسمح بخطة ثانية في نفس اليوم والوردية
/// ما دامت الطاقة الفعلية المتبقية تكفي، ويُرفض فقط التجاوز الذي يتعدى الطاقة.
/// </summary>
public class PlanOverlapCapacityTests
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

    private static List<PlanItemDto> Item(int lotId, int cartons, double kg, string date, int priority = 1)
        => new()
        {
            new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3,
                    PlannedQtyKg = kg, PlannedCartons = cartons,
                    ScheduledDate = date, SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = priority }
        };

    [Fact]
    public void Second_Plan_Allowed_When_Capacity_Remains()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lotId = SeedLot(host, 200000);

        var cap = Svc<ICapacityService>(host);
        Assert.True(cap.SetCapacity(3, 1, 5000).Ok); // طاقة الوردية 5,000 كرتون

        var planning = Svc<IPlanningService>(host);

        // الخطة الأولى: 3,000 كرتون في 2026-12-01 → اعتماد
        var p1 = planning.SavePlan("الخطة الأولى", "Daily", "2026-12-01", "2026-12-01", 1, 1, Item(lotId, 3000, 3000 * 7.5, "2026-12-01"));
        Assert.True(p1.Ok, p1.Message);
        Assert.True(planning.ApprovePlan(p1.Id).Ok);

        // الخطة الثانية: نفس اليوم والوردية، 1,500 كرتون (المتبقي 2,000 يكفيها) → تُقبل وتعتمد
        var p2 = planning.SavePlan("الخطة الثانية", "Daily", "2026-12-01", "2026-12-01", 1, 1, Item(lotId, 1500, 1500 * 7.5, "2026-12-01"));
        Assert.True(p2.Ok, p2.Message); // لم تُرفض — الطاقة المتبقية تكفي
        var approve2 = planning.ApprovePlan(p2.Id);
        Assert.True(approve2.Ok, approve2.Message); // تُعتمد — لا منع أعمى
    }

    [Fact]
    public void Second_Plan_Rejected_When_Capacity_Exceeded()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lotId = SeedLot(host, 200000);

        var cap = Svc<ICapacityService>(host);
        Assert.True(cap.SetCapacity(3, 1, 5000).Ok); // طاقة الوردية 5,000 كرتون

        var planning = Svc<IPlanningService>(host);

        // الخطة الأولى: 4,000 كرتون → اعتماد (المتبقي 1,000)
        var p1 = planning.SavePlan("الخطة الأولى", "Daily", "2026-12-01", "2026-12-01", 1, 1, Item(lotId, 4000, 4000 * 7.5, "2026-12-01"));
        Assert.True(p1.Ok, p1.Message);
        Assert.True(planning.ApprovePlan(p1.Id).Ok);

        // الخطة الثانية: نفس اليوم والوردية، 2,000 كرتون (تتجاوز المتبقي 1,000) → تُرفض
        var p2 = planning.SavePlan("الخطة الثانية", "Daily", "2026-12-01", "2026-12-01", 1, 1, Item(lotId, 2000, 2000 * 7.5, "2026-12-01"));
        Assert.False(p2.Ok);
        Assert.Contains("الطاقة", p2.Message);
    }
}
