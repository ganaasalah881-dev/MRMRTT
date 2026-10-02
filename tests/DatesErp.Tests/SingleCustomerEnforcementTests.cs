using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>§B76 — خطة العميل الواحد لا تقبل تسريب عملاء آخرين (فرض من الخلفية).</summary>
public class SingleCustomerEnforcementTests
{
    private static IPlanningService Plan(TestHost h)
        => h.Services.CreateScope().ServiceProvider.GetRequiredService<IPlanningService>();

    private static (int lotA, int custA, int custB, int fin) Setup(TestHost host)
    {
        var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var rcv = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var a = master.SaveCustomer(null, "SA", "عميل ألف", "جملة", "1", "-", true);
        var b = master.SaveCustomer(null, "SB", "عميل باء", "جملة", "2", "-", true);
        var raw = master.SaveProductFull(null, "001-700", "خام الواحد", "001", "Raw", "كجم", 0, 0, 0, null);
        var fin = master.SaveProductFull(null, "002-700", "تام الواحد", "002", "Finished", "كرتون", 10, 5, 2, null, raw.Id);
        var sh = rcv.SaveShipment(a.Id, null, null, new System.Collections.Generic.List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = raw.Id, QtyKg = 90000, PackageCount = 4500, UnitWeightKg = 20 } });
        rcv.ApproveShipment(sh.Id);
        var lot = db.Lots.Single(l => l.ShipmentId == sh.Id).Id;
        return (lot, a.Id, b.Id, fin.Id);
    }

    [Fact]
    public void Single_Scope_Rejects_Items_Of_Other_Customers()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (lot, a, b, fin) = Setup(host);
        TestCapacityFixture.DefineMissingRates(host);
        var r = Plan(host).SavePlan("تسريب", "Daily", "2026-09-01", "2026-09-01", 1, 1,
            new System.Collections.Generic.List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lot, CustomerId = a, ProductId = fin, PlannedCartons = 100, PlannedQtyKg = 1000 },
                new() { SourceType = "Manual", CustomerId = b, ProductId = fin, PlannedCartons = 50, PlannedQtyKg = 500 }
            }, null, "Single", a);
        Assert.False(r.Ok);                                      // مرفوضة من الخلفية
        Assert.Contains("عميل آخر", r.Message);
    }

    [Fact]
    public void Single_Scope_Accepts_Items_Of_That_Customer_Only()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (lot, a, _, fin) = Setup(host);
        TestCapacityFixture.DefineMissingRates(host);
        var r = Plan(host).SavePlan("نقي", "Daily", "2026-09-01", "2026-09-01", 1, 1,
            new System.Collections.Generic.List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lot, CustomerId = a, ProductId = fin, PlannedCartons = 100, PlannedQtyKg = 1000 },
                new() { SourceType = "Manual", CustomerId = a, ProductId = fin, PlannedCartons = 50, PlannedQtyKg = 500 }
            }, null, "Single", a);
        Assert.True(r.Ok, r.Message);
    }

    [Fact]
    public void Multi_Scope_Still_Allows_Several_Customers()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (lot, a, b, fin) = Setup(host);
        TestCapacityFixture.DefineMissingRates(host);
        var r = Plan(host).SavePlan("مجمع", "Daily", "2026-09-01", "2026-09-01", 1, 1,
            new System.Collections.Generic.List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lot, CustomerId = a, ProductId = fin, PlannedCartons = 100, PlannedQtyKg = 1000 },
                new() { SourceType = "Manual", CustomerId = b, ProductId = fin, PlannedCartons = 50, PlannedQtyKg = 500 }
            }, null, "Multi", null);
        Assert.True(r.Ok, r.Message);
    }
}
