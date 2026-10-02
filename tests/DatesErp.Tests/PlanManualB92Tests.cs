using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B92 — الخطط الفترية متعددة العملاء بالإنزال اليدوي: كل بند بتاريخه وورديته
/// باختيار الإدارة — يُحفظ ويُسترجع كما أُدخل.
/// </summary>
public class PlanManualB92Tests
{
    private static (TestHost host, int custA, int custB) Seed2()
    {
        var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        db.Customers.Add(new Customer { CustomerCode = "PM-A", CustomerName = "عميل يدوي أ", IsActive = true });
        db.Customers.Add(new Customer { CustomerCode = "PM-B", CustomerName = "عميل يدوي ب", IsActive = true });
        db.SaveChanges();
        return (host, db.Customers.Single(c => c.CustomerCode == "PM-A").Id,
            db.Customers.Single(c => c.CustomerCode == "PM-B").Id);
    }

    private static IPlanningService Plan(TestHost h)
        => h.Services.CreateScope().ServiceProvider.GetRequiredService<IPlanningService>();

    [Fact]
    public void Manual_Period_Plan_Keeps_PerItem_Date_And_Shift()
    {
        var (host, a, b) = Seed2(); using var _host = host;
        var r = Plan(host).SavePlan("خطة يدوية فترية", "Period", "2026-09-01", "2026-09-06", 1, 1, new List<PlanItemDto>
        {
            new() { CustomerId = a, ProductId = 3, PlannedCartons = 200, PlannedQtyKg = 1500, ScheduledDate = "2026-09-01", SuggestedShiftId = 1 },
            new() { CustomerId = b, ProductId = 4, PlannedCartons = 300, PlannedQtyKg = 600, ScheduledDate = "2026-09-03", SuggestedShiftId = 2 },
            new() { CustomerId = a, ProductId = 3, PlannedCartons = 100, PlannedQtyKg = 750, ScheduledDate = "2026-09-05", SuggestedShiftId = 3 },
        });
        Assert.True(r.Ok);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var items = db.ProductionPlanItems.Where(i => i.PlanId == r.Id).OrderBy(i => i.Id).ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal(new DateTime(2026, 9, 1), items[0].ScheduledDate);
        Assert.Equal(1, items[0].SuggestedShiftId);
        Assert.Equal(new DateTime(2026, 9, 3), items[1].ScheduledDate);
        Assert.Equal(2, items[1].SuggestedShiftId);
        Assert.Equal(new DateTime(2026, 9, 5), items[2].ScheduledDate);
        Assert.Equal(3, items[2].SuggestedShiftId);
        Assert.Equal(2, items.Select(i => i.CustomerId).Distinct().Count());
    }

    [Fact]
    public void LotLinked_Period_Plan_Keeps_PerItem_Date()
    {
        var (host, a, b) = Seed2(); using var _host = host;
        int lotA, lotB;
        using (var scopeSeed = host.Services.CreateScope())
        {
            var dbSeed = scopeSeed.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            dbSeed.Lots.Add(new Lot { LotCode = "LOT-PM-A", ProductId = 1, CustomerId = a, InitialQtyKg = 20000, InStockQtyKg = 20000, Status = "Approved" });
            dbSeed.Lots.Add(new Lot { LotCode = "LOT-PM-B", ProductId = 2, CustomerId = b, InitialQtyKg = 20000, InStockQtyKg = 20000, Status = "Approved" });
            dbSeed.SaveChanges();
            lotA = dbSeed.Lots.Single(l => l.LotCode == "LOT-PM-A").Id;
            lotB = dbSeed.Lots.Single(l => l.LotCode == "LOT-PM-B").Id;
        }
        var r = Plan(host).SavePlan("خطة دفعات فترية", "Period", "2026-09-01", "2026-09-06", 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lotA, CustomerId = a, ProductId = 3, PlannedCartons = 200, PlannedQtyKg = 1500, ScheduledDate = "2026-09-02", SuggestedShiftId = 2 },
            new() { SourceType = "FromReceiving", LotId = lotB, CustomerId = b, ProductId = 4, PlannedCartons = 300, PlannedQtyKg = 600, ScheduledDate = "2026-09-05", SuggestedShiftId = 1 },
        });
        Assert.True(r.Ok);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var items = db.ProductionPlanItems.Where(i => i.PlanId == r.Id).OrderBy(i => i.Id).ToList();
        Assert.Equal(new DateTime(2026, 9, 2), items[0].ScheduledDate);
        Assert.Equal(2, items[0].SuggestedShiftId);
        Assert.Equal(new DateTime(2026, 9, 5), items[1].ScheduledDate);
        Assert.Equal(1, items[1].SuggestedShiftId);
    }
}
