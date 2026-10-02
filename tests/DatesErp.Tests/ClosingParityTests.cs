using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B95 — مسار إقفال واحد (CloseProductionDay عبر أمر الإنتاج): حُذف مسار بنود الخطة الموازي نهائياً.
/// هذه الاختبارات تثبت ضمانات المسار الواحد: إرجاع الخام المتبقي إلى المخزن بحركة موثقة،
/// وتسجيل المخرجات الثانوية ديناميكياً، وتحمل الدفعات بلا نوع عبوة (انحدار NoPack).
/// </summary>
public class ClosingParityTests
{
    private sealed record Ctx(int Customer, int Raw, int Fin, int Lot, int OrderId, int ByProduct);

    /// <summary>خام 5,000 كجم ← أمر بـ3,000 ← إنتاج 2,850 (380 كرتون) فيتبقى خام في الصالة.</summary>
    private static Ctx BuildOrderWithStartedProduction(TestHost host, string tag, double planKg = 3000)
    {
        using var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var receiving = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var c = master.SaveCustomer(null, $"CP-{tag}", $"عميل التكافؤ {tag}", "جملة", "777", "-", true);
        var raw = master.SaveProductFull(null, $"{tag}-R", $"خام {tag}", "001", "Raw", "كجم", 20, 0, 0, null);
        var fin = master.SaveProductFull(null, $"{tag}-F", $"تام {tag}", "002", "Finished", "كرتون", 7.5, 1, 0.5, null, sourceProductId: raw.Id);
        Assert.True(c.Ok && raw.Ok && fin.Ok, $"{c.Message} {raw.Message} {fin.Message}");

        var s = receiving.SaveShipment(c.Id, null, null, new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = raw.Id, QtyKg = 5000, PackageCount = 250, UnitWeightKg = 20 } });
        Assert.True(s.Ok && receiving.ApproveShipment(s.Id).Ok);
        int lot = db.Lots.Single(l => l.ShipmentId == s.Id).Id;

        TestCapacityFixture.DefineMissingRates(host);
        var plan = planning.SavePlan($"خطة {tag}", "Daily", "2026-09-01", "2026-09-01", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lot, CustomerId = c.Id, ProductId = fin.Id, PlannedQtyKg = planKg, PriorityNo = 1 } });
        Assert.True(plan.Ok && planning.ApprovePlan(plan.Id).Ok);
        int planItemId = db.ProductionPlanItems.Single(i => i.PlanId == plan.Id).Id;
        host.SetBusinessDate("2026-09-01"); // Arrange production on its scheduled business day, not the machine date.

        var order = orders.SaveOrder("FromPlan", plan.Id, c.Id, "2026-09-01", 1, 1, TestOrderFixture.Items(host, plan.Id, c.Id, "2026-09-01"));
        Assert.True(order.Ok, order.Message);
        var approveOrder = orders.ApproveOrder(order.Id); Assert.True(approveOrder.Ok, approveOrder.Message);
        Assert.True(orders.StartOrder(order.Id).Ok);

        int bp = db.Products.AsNoTracking().Where(b => b.ItemType == "ByProduct" && b.IsActive)
            .OrderBy(b => b.Id).Select(b => b.Id).First();
        return new Ctx(c.Id, raw.Id, fin.Id, lot, order.Id, bp);
    }

    private static double LotStock(TestHost host, int lotId)
    {
        using var scope = host.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<DatesErpDbContext>()
            .Lots.AsNoTracking().Single(l => l.Id == lotId).InStockQtyKg;
    }

    [Fact]
    public void CloseProductionDay_Returns_Leftover_Raw_To_Inventory_With_A_Movement()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = BuildOrderWithStartedProduction(host, "R1");
        double before = LotStock(host, ctx.Lot);

        using (var scope = host.Services.CreateScope())
        {
            var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();
            // §الخام المستهلك فعلياً 3,000 · خرج 2,850 + 130 مخرج = 2,980
            // → يتبقى 20 كجم في الصالة تعود للدفعة. والخام يُصرف هنا لا عند الاعتماد.
            var r = exec.CloseProductionDay(ctx.OrderId, 2850, 380, 0, 0, 0, false,
                new List<DowntimeDto>(), false, null,
                new List<ByProductQtyDto> { new() { ByProductId = ctx.ByProduct, QtyKg = 130 } },
                consumedRawKg: 3000);
            Assert.True(r.Ok, r.Message);
        }

        // 5,000 − 3,000 مصرف + 20 عائداً = 2,020
        double after = LotStock(host, ctx.Lot);
        Assert.Equal(2020, after, 1);
        Assert.True(after < before,
            $"الخام المتبقي لم يعد إلى المخزن: قبل={before:N1} بعد={after:N1}");

        using var s2 = host.Services.CreateScope();
        var db = s2.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        bool hasReturn = db.InventoryTransactions.AsNoTracking().Any(t =>
            t.LotId == ctx.Lot && t.MovementType == Core.Domain.Enums.MovementType.Inbound && t.QtyKg > 0);
        Assert.True(hasReturn, "لا حركة مرتجع موثقة للخام العائد");
    }

    [Fact]
    public void DayClose_Stores_ByProducts_Dynamically_And_Tolerates_Lots_Without_Pack()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();

        // §B95 — المسار الواحد: إقفال يوم الأمر يخزن المخرجات الثانوية ديناميكياً
        var a = BuildOrderWithStartedProduction(host, "A");
        using (var scope = host.Services.CreateScope())
        {
            var r = scope.ServiceProvider.GetRequiredService<IExecutionService>()
                .CloseProductionDay(a.OrderId, 2850, 380, 0, 0, 0, false, new List<DowntimeDto>(), false, null,
                    new List<ByProductQtyDto> { new() { ByProductId = a.ByProduct, QtyKg = 130 } });
            Assert.True(r.Ok, r.Message);
        }
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            int execId = db.ProductionExecutions.Single(e => e.OrderId == a.OrderId).Id;
            double q = db.ExecutionByProducts.AsNoTracking().Where(x => x.ExecutionId == execId).Sum(x => (double)x.Qty);
            Assert.Equal(130, q, 1);
        }

        // §الدفعات بلا نوع عبوة هي الحالة الشائعة — يجب ألا يُسقطها الإقفال (انحدار NoPack من المسار المحذوف)
        var b = BuildOrderWithStartedProduction(host, "B");
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            int? packOfLot = db.Lots.AsNoTracking().Single(l => l.Id == b.Lot).PackagingTypeId;
            Assert.Null(packOfLot);
            var r = scope.ServiceProvider.GetRequiredService<IExecutionService>()
                .CloseProductionDay(b.OrderId, 2850, 380, 0, 0, 0, false, new List<DowntimeDto>(), false, null,
                    new List<ByProductQtyDto> { new() { ByProductId = b.ByProduct, QtyKg = 130 } });
            Assert.True(r.Ok, r.Message);
            int execId = db.ProductionExecutions.Single(e => e.OrderId == b.OrderId).Id;
            double q = db.ExecutionByProducts.AsNoTracking().Where(x => x.ExecutionId == execId).Sum(x => (double)x.Qty);
            Assert.Equal(130, q, 1);
        }
    }
}
