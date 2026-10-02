using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B88 — اختبارات الإقفال متعدد الأصناف (M13) + تجميع الصرف (DUPLICATE) + التوقفات (M10) + صلاحية اليدوي (L1).
/// </summary>
public class CloseB88Tests
{
    private sealed record Ctx2(int Customer, int Fin1, int Fin2, int Lot1, int Lot2, int Item1, int Item2, int OrderId, string OrderNo);

    /// <summary>أمر ببندين (صنفان × دفعتان): بند1 1500كجم/200كرتون (7.5كجم) + بند2 1000كجم/200كرتون (5كجم).</summary>
    private static Ctx2 BuildTwoItemOrder(TestHost host, string tag)
    {
        using var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var receiving = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var c = master.SaveCustomer(null, $"CB-{tag}", $"عميل الإقفال {tag}", "جملة", "778", "-", true);
        var raw1 = master.SaveProductFull(null, $"{tag}-R1", $"خام {tag} أ", "001", "Raw", "كجم", 20, 0, 0, null);
        var raw2 = master.SaveProductFull(null, $"{tag}-R2", $"خام {tag} ب", "001", "Raw", "كجم", 20, 0, 0, null);
        var fin1 = master.SaveProductFull(null, $"{tag}-F1", $"تام {tag} أ", "002", "Finished", "كرتون", 7.5, 1, 0.5, null, sourceProductId: raw1.Id);
        var fin2 = master.SaveProductFull(null, $"{tag}-F2", $"تام {tag} ب", "002", "Finished", "كرتون", 5, 1, 0.5, null, sourceProductId: raw2.Id);
        Assert.True(c.Ok && raw1.Ok && raw2.Ok && fin1.Ok && fin2.Ok);

        int LotFor(int custId, int rawId, string suffix)
        {
            var s = receiving.SaveShipment(custId, null, null, new List<ShipmentItemDto>
            { new() { TreatmentRequired = false, ProductId = rawId, QtyKg = 5000, PackageCount = 250, UnitWeightKg = 20 } });
            Assert.True(s.Ok && receiving.ApproveShipment(s.Id).Ok);
            return db.Lots.Single(l => l.ShipmentId == s.Id).Id;
        }
        int lot1 = LotFor(c.Id, raw1.Id, "A");
        int lot2 = LotFor(c.Id, raw2.Id, "B");

        TestCapacityFixture.DefineMissingRates(host);
        var plan = planning.SavePlan($"خطة {tag}", "Daily", "2026-09-01", "2026-09-01", 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lot1, CustomerId = c.Id, ProductId = fin1.Id, PlannedQtyKg = 1500, PlannedCartons = 200, PriorityNo = 1 },
            new() { SourceType = "FromReceiving", LotId = lot2, CustomerId = c.Id, ProductId = fin2.Id, PlannedQtyKg = 1000, PlannedCartons = 200, PriorityNo = 2 }
        });
        Assert.True(plan.Ok, plan.Message);
        Assert.True(planning.ApprovePlan(plan.Id).Ok);
        int pi1 = db.ProductionPlanItems.Single(i => i.PlanId == plan.Id && i.ProductId == fin1.Id).Id;
        int pi2 = db.ProductionPlanItems.Single(i => i.PlanId == plan.Id && i.ProductId == fin2.Id).Id;
        host.SetBusinessDate("2026-09-01"); // Arrange production on its scheduled business day, not the machine date.

        var order = orders.SaveOrder("FromPlan", plan.Id, c.Id, "2026-09-01", 1, 1, new List<OrderItemDto>
        {
            new() { PlanItemId = pi1, LotId = lot1, CustomerId = c.Id, ProductId = fin1.Id, PlannedQtyKg = 1500, PlannedCartons = 200 },
            new() { PlanItemId = pi2, LotId = lot2, CustomerId = c.Id, ProductId = fin2.Id, PlannedQtyKg = 1000, PlannedCartons = 200 }
        });
        Assert.True(order.Ok, order.Message);
        Assert.True(orders.ApproveOrder(order.Id).Ok);
        Assert.True(orders.StartOrder(order.Id).Ok);

        int oi1 = db.ProductionOrderItems.Single(i => i.OrderId == order.Id && i.ProductId == fin1.Id).Id;
        int oi2 = db.ProductionOrderItems.Single(i => i.OrderId == order.Id && i.ProductId == fin2.Id).Id;
        string orderNo = db.ProductionOrders.Single(o => o.Id == order.Id).DocumentNumber;
        return new Ctx2(c.Id, fin1.Id, fin2.Id, lot1, lot2, oi1, oi2, order.Id, orderNo);
    }

    private static IExecutionService Exec(TestHost h)
        => h.Services.CreateScope().ServiceProvider.GetRequiredService<IExecutionService>();

    private static DatesErpDbContext Db(TestHost h)
        => h.Services.CreateScope().ServiceProvider.GetRequiredService<DatesErpDbContext>();

    [Fact]
    public void MultiProductClose_PerItem_Succeeds_WithBreakdown()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = BuildTwoItemOrder(host, "M1");

        var r = Exec(host).CloseProductionDay(ctx.OrderId, 0, 0, 0, 0, 0, false,
            new List<DowntimeDto>(), false, null, null, consumedRawKg: 2500,
            itemQtys: new List<CloseItemQtyDto>
            {
                new() { OrderItemId = ctx.Item1, ProducedKg = 1500, ProducedCartons = 200 },
                new() { OrderItemId = ctx.Item2, ProducedKg = 1000, ProducedCartons = 200 }
            });
        Assert.True(r.Ok, r.Message);
        Assert.Contains("تفصيل البنود", r.Message);

        using var db = Db(host);
        var i1 = db.ProductionOrderItems.AsNoTracking().Single(i => i.Id == ctx.Item1);
        var i2 = db.ProductionOrderItems.AsNoTracking().Single(i => i.Id == ctx.Item2);
        Assert.Equal(1500, i1.ProducedQtyKg);
        Assert.Equal(200, i1.ProducedCartons);
        Assert.Equal(1000, i2.ProducedQtyKg);
        Assert.Equal(200, i2.ProducedCartons);
        var exe = db.ProductionExecutions.AsNoTracking().Single(e => e.OrderId == ctx.OrderId);
        Assert.Equal(2500, exe.ActualQtyKg);
        Assert.Equal(400, exe.ActualCartons);
    }

    [Fact]
    public void PerItem_OverKg_Rejected_With_Item_Name()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = BuildTwoItemOrder(host, "M2");

        var r = Exec(host).CloseProductionDay(ctx.OrderId, 0, 0, 0, 0, 0, false,
            new List<DowntimeDto>(), false, null, null, consumedRawKg: 2500,
            itemQtys: new List<CloseItemQtyDto>
            {
                new() { OrderItemId = ctx.Item1, ProducedKg = 1600, ProducedCartons = 200 },
                new() { OrderItemId = ctx.Item2, ProducedKg = 1000, ProducedCartons = 200 }
            });
        Assert.False(r.Ok);
        Assert.Contains("متبقيه", r.Message);
    }

    [Fact]
    public void PerItem_CartonMismatch_Rejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = BuildTwoItemOrder(host, "M3");

        // بند1: 1500كجم مقابل 100 كرتون × 7.5 = 750 — تناقض صريح
        var r = Exec(host).CloseProductionDay(ctx.OrderId, 0, 0, 0, 0, 0, false,
            new List<DowntimeDto>(), false, null, null, consumedRawKg: 2500,
            itemQtys: new List<CloseItemQtyDto>
            {
                new() { OrderItemId = ctx.Item1, ProducedKg = 1500, ProducedCartons = 100 },
                new() { OrderItemId = ctx.Item2, ProducedKg = 1000, ProducedCartons = 200 }
            });
        Assert.False(r.Ok);
        Assert.Contains("لا تطابق", r.Message);
    }

    [Fact]
    public void LegacyTotals_MultiItem_Distributes_By_Order()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = BuildTwoItemOrder(host, "M4");

        // المسار القديم بلا تفصيل: الإجمالي يُفحص على الصنف الأول ويتوزع بالترتيب كما قبل B88
        var r = Exec(host).CloseProductionDay(ctx.OrderId, 1500, 200, 0, 0, 0, false,
            new List<DowntimeDto>(), false, null, null, consumedRawKg: 1500);
        Assert.True(r.Ok, r.Message);

        using var db = Db(host);
        var i1 = db.ProductionOrderItems.AsNoTracking().Single(i => i.Id == ctx.Item1);
        var i2 = db.ProductionOrderItems.AsNoTracking().Single(i => i.Id == ctx.Item2);
        Assert.Equal(1500, i1.ProducedQtyKg);
        Assert.Equal(200, i1.ProducedCartons);
        Assert.Equal(0, i2.ProducedQtyKg);
        Assert.Equal(0, i2.ProducedCartons);
    }

    [Fact]
    public void SameLot_TwoItems_SingleConsumeMovement_NoDuplicate()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using (var scope = host.Services.CreateScope())
        {
            var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
            var receiving = scope.ServiceProvider.GetRequiredService<IReceivingService>();
            var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

            var c = master.SaveCustomer(null, "CB-D1", "عميل التجميع", "جملة", "779", "-", true);
            var raw = master.SaveProductFull(null, "D1-R", "خام التجميع", "001", "Raw", "كجم", 20, 0, 0, null);
            var fin = master.SaveProductFull(null, "D1-F", "تام التجميع", "002", "Finished", "كرتون", 7.5, 1, 0.5, null, sourceProductId: raw.Id);
            var s = receiving.SaveShipment(c.Id, null, null, new List<ShipmentItemDto>
            { new() { TreatmentRequired = false, ProductId = raw.Id, QtyKg = 8000, PackageCount = 400, UnitWeightKg = 20 } });
            Assert.True(s.Ok && receiving.ApproveShipment(s.Id).Ok);
            int lot = db.Lots.Single(l => l.ShipmentId == s.Id).Id;

            TestCapacityFixture.DefineMissingRates(host);
            var plan = planning.SavePlan("خطة التجميع", "Daily", "2026-09-01", "2026-09-01", 1, 1, new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lot, CustomerId = c.Id, ProductId = fin.Id, PlannedQtyKg = 1500, PlannedCartons = 200, PriorityNo = 1 },
                new() { SourceType = "FromReceiving", LotId = lot, CustomerId = c.Id, ProductId = fin.Id, PlannedQtyKg = 1500, PlannedCartons = 200, PriorityNo = 2 }
            });
            Assert.True(plan.Ok && planning.ApprovePlan(plan.Id).Ok);
            var pi = db.ProductionPlanItems.Where(i => i.PlanId == plan.Id).OrderBy(i => i.Id).Select(i => i.Id).ToList();

            // بندان من الدفعة نفسها — قبل B88 كانا ينشران حركتي صرف مكررتين (DUPLICATE)
            host.SetBusinessDate("2026-09-01"); // Arrange production on its scheduled business day, not the machine date.
            var order = orders.SaveOrder("FromPlan", plan.Id, c.Id, "2026-09-01", 1, 1, new List<OrderItemDto>
            {
                new() { PlanItemId = pi[0], LotId = lot, CustomerId = c.Id, ProductId = fin.Id, PlannedQtyKg = 1500, PlannedCartons = 200 },
                new() { PlanItemId = pi[1], LotId = lot, CustomerId = c.Id, ProductId = fin.Id, PlannedQtyKg = 1500, PlannedCartons = 200 }
            });
            Assert.True(order.Ok, order.Message);
            Assert.True(orders.ApproveOrder(order.Id).Ok);
            Assert.True(orders.StartOrder(order.Id).Ok);

            var items = db.ProductionOrderItems.Where(i => i.OrderId == order.Id).OrderBy(i => i.Id).ToList();
            string orderNo = db.ProductionOrders.Single(o => o.Id == order.Id).DocumentNumber;

            var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();
            var r = exec.CloseProductionDay(order.Id, 0, 0, 0, 0, 0, false,
                new List<DowntimeDto>(), false, null, null, consumedRawKg: 3000,
                itemQtys: new List<CloseItemQtyDto>
                {
                    new() { OrderItemId = items[0].Id, ProducedKg = 1500, ProducedCartons = 200 },
                    new() { OrderItemId = items[1].Id, ProducedKg = 1500, ProducedCartons = 200 }
                });
            Assert.True(r.Ok, r.Message);

            int posts = db.InventoryTransactions.AsNoTracking().Count(t =>
                t.ReferenceDocNumber == orderNo && t.LotId == lot && t.MovementType == MovementType.Outbound);
            Assert.Equal(1, posts);
        }
    }

    [Fact]
    public void Downtimes_Persisted_With_Times()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = BuildTwoItemOrder(host, "M5");

        var r = Exec(host).CloseProductionDay(ctx.OrderId, 0, 0, 0, 0, 0, false,
            new List<DowntimeDto> { new() { Hours = 2.5, ReasonAr = "عطل سير", StartTime = "14:30", EndTime = "17:00" } },
            false, null, null, consumedRawKg: 2500,
            itemQtys: new List<CloseItemQtyDto>
            {
                new() { OrderItemId = ctx.Item1, ProducedKg = 1500, ProducedCartons = 200 },
                new() { OrderItemId = ctx.Item2, ProducedKg = 1000, ProducedCartons = 200 }
            });
        Assert.True(r.Ok, r.Message);

        using var db = Db(host);
        int exeId = db.ProductionExecutions.AsNoTracking().Single(e => e.OrderId == ctx.OrderId).Id;
        var dt = db.ExecutionDowntimes.AsNoTracking().Single(d => d.ExecutionId == exeId);
        Assert.Equal(2.5, dt.Hours);
        Assert.Equal("عطل سير", dt.ReasonAr);
        Assert.Equal("14:30", dt.StartTime);
        Assert.Equal("17:00", dt.EndTime);
    }

    [Fact]
    public void ManualOrder_Is_Disabled_Regardless_Of_Legacy_Permission()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int custId, finId;
        using (var scope = host.Services.CreateScope())
        {
            var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var c = master.SaveCustomer(null, "CB-L1", "عميل اليدوي", "جملة", "780", "-", true);
            var raw = master.SaveProductFull(null, "L1-R", "خام اليدوي", "001", "Raw", "كجم", 20, 0, 0, null);
            var fin = master.SaveProductFull(null, "L1-F", "تام اليدوي", "002", "Finished", "كرتون", 5, 1, 0.5, null, sourceProductId: raw.Id);
            Assert.True(c.Ok && raw.Ok && fin.Ok);
            custId = c.Id; finId = fin.Id;
        }

        using (var scope = host.Services.CreateScope())
        {
            var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            List<OrderItemDto> ManualItems() => new()
            { new() { ProductId = finId, PlannedQtyKg = 1000, PlannedCartons = 200 } };

            // Even an administrator cannot revive the retired manual-order path.
            var session = host.Services.GetRequiredService<SessionContext>();
            foreach (var granted in new[] { true, false, true })
            {
                session.PermissionCache[("manualorder", "Create")] = granted;
                var denied = orders.SaveOrder("Manual", null, custId, "2026-09-01", 1, 1, ManualItems());
                Assert.False(denied.Ok);
                Assert.Contains("لا ينشأ يدوياً", denied.Message);
            }
            Assert.Empty(scope.ServiceProvider.GetRequiredService<DatesErpDbContext>().ProductionOrders);

        }
    }

    [Fact]
    public void PlanOrder_Ignores_Manual_Permission()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        // سحب صلاحية اليدوي ثم بناء أمر من خطة كامل — يجب أن ينجح (البوابة لليدوي فقط)
        var session = host.Services.GetRequiredService<SessionContext>();
        session.PermissionCache[("manualorder", "Create")] = false;
        var ctx = BuildTwoItemOrder(host, "M6");
        Assert.True(ctx.OrderId > 0);
    }
}
