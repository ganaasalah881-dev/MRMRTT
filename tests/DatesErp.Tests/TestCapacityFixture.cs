using DatesErp.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
namespace DatesErp.Tests;
/// <summary>Explicit ample rate only for legacy non-capacity workflow fixtures. Never a production fallback.</summary>
internal static class TestCapacityFixture
{
    internal static void DefineMissingRates(TestHost host)
    {
        using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        foreach (var p in db.Products.Where(p => p.ItemType == "Finished" && p.HourlyProductionRate <= 0)) p.HourlyProductionRate = 10000;
        db.SaveChanges();
    }
}
