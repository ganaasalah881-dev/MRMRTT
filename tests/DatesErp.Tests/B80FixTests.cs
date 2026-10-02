using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B80 — اختبارات إصلاحات ملاحظات المستخدم على ويندوز:
/// 1) فرض تاريخ كل إنتاج داخل فترة الخطة (DATE_OUT_OF_RANGE) —
/// 2) رفض أمر الإنتاج بصفر إنتاج —
/// 3) تعديل بنود أمر المسودة (UpdateOrderItems) وحرس الحالة —
/// 4) مزامنة العبوات من شاشة الوحدات.
/// </summary>
public class B80FixTests
{
    private static IPlanningService Plan(TestHost h)
        => h.Services.CreateScope().ServiceProvider.GetRequiredService<IPlanningService>();

    private sealed record Seed(int Customer, int Raw, int Fin);

    private static Seed SeedCompany(TestHost host, string code)
    {
        using var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var c = master.SaveCustomer(null, code, $"عميل {code}", "جملة", "777", "-", true);
        Assert.True(c.Ok, c.Message);
        var raw = master.SaveProductFull(null, $"{code}-R1", $"خام {code}", "001", "Raw", "كجم", 20, 0, 0, null);
        Assert.True(raw.Ok, raw.Message);
        var fin = master.SaveProductFull(null, $"{code}-F1", $"تام {code}", "002", "Finished", "كرتون 5كجم", 5, 0, 0, null, sourceProductId: raw.Id);
        Assert.True(fin.Ok, fin.Message);
        return new Seed(c.Id, raw.Id, fin.Id);
    }

    private static int Receive(TestHost host, int cust, int productId, double kg)
    {
        using var scope = host.Services.CreateScope();
        var receiving = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var s = receiving.SaveShipment(cust, "2026-09-01", "2026-09-01", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = productId, QtyKg = kg, PackageCount = (int)(kg / 20), UnitWeightKg = 20 } });
        Assert.True(s.Ok, s.Message);
        Assert.True(receiving.ApproveShipment(s.Id).Ok);
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        return db.Lots.Where(l => l.ShipmentId == s.Id && l.ProductId == productId).Single().Id;
    }

    // ══════════ 1) تاريخ كل إنتاج: خارج فترة الخطة مرفوض ══════════
    [Fact]
    public void Plan_Item_Date_Outside_Period_Is_Rejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var s = SeedCompany(host, "B80A");
        int lot = Receive(host, s.Customer, s.Raw, 10000);

        TestCapacityFixture.DefineMissingRates(host);
        var r = Plan(host).SavePlan("خطة فترية", "Period", "2026-09-01", "2026-09-05", 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lot, CustomerId = s.Customer, ProductId = s.Fin,
                    PlannedCartons = 100, PlannedQtyKg = 500, ScheduledDate = "2026-09-20" } // خارج الفترة
        });
        Assert.False(r.Ok);
        Assert.Contains("خارج فترة الخطة", r.Message);
    }

    [Fact]
    public void Plan_Item_Without_Date_Defaults_To_Plan_Start()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var s = SeedCompany(host, "B80B");
        int lot = Receive(host, s.Customer, s.Raw, 10000);

        TestCapacityFixture.DefineMissingRates(host);
        var r = Plan(host).SavePlan("خطة بلا تاريخ بند", "Period", "2026-09-01", "2026-09-05", 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lot, CustomerId = s.Customer, ProductId = s.Fin,
                    PlannedCartons = 100, PlannedQtyKg = 500 } // بلا تاريخ ← بداية الخطة
        });
        Assert.True(r.Ok, r.Message);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var item = db.ProductionPlanItems.Single(i => i.PlanId == r.Id);
        Assert.Equal(new DateTime(2026, 9, 1), item.ScheduledDate!.Value.Date);
    }

    // ══════════ 2) لا أمر إنتاج بصفر ══════════
    [Fact]
    public void Order_With_Zero_Quantity_Is_Rejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var s = SeedCompany(host, "B80C");
        int lot = Receive(host, s.Customer, s.Raw, 10000);

        int planId;
        using (var scope = host.Services.CreateScope())
        {
            var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            TestCapacityFixture.DefineMissingRates(host);
            var plan = planning.SavePlan("خطة الصفر", "Daily", "2026-09-01", "2026-09-01", 1, 1, new List<PlanItemDto>
            { new() { SourceType = "FromReceiving", LotId = lot, CustomerId = s.Customer, ProductId = s.Fin,
                      PlannedCartons = 100, PlannedQtyKg = 500, ScheduledDate = "2026-09-01" } });
            Assert.True(plan.Ok, plan.Message);
            planId = plan.Id;
            Assert.True(planning.ApprovePlan(planId).Ok);
        }

        using var scope2 = host.Services.CreateScope();
        var orders = scope2.ServiceProvider.GetRequiredService<IProductionOrderService>();
        var db = scope2.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var planItemId = db.ProductionPlanItems.Single(i => i.PlanId == planId).Id;

        // بند بلا كمية إطلاقاً ← الأمر مرفوض كاملاً (لا يُنشأ أمر فارغ)
        host.SetBusinessDate("2026-09-01");
        var zero = orders.SaveOrder("FromPlan", planId, s.Customer, "2026-09-01", 1, 1, new List<OrderItemDto>
        { new() { PlanItemId = planItemId, LotId = lot, CustomerId = s.Customer, ProductId = s.Fin,
                  PlannedQtyKg = 0, PlannedCartons = 0 } });
        Assert.False(zero.Ok);
        Assert.Contains("لا زيادة أو تخفيض", zero.Message);
        Assert.False(db.ProductionOrders.Any(o => o.SourcePlanId == planId), "يجب ألا يُنشأ أمر بصفر");
    }

    // ══════════ 3) تعديل بنود أمر المسودة ══════════
    [Fact]
    public void Order_Items_Are_Immutable_In_Draft_And_Approved_States()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var s = SeedCompany(host, "B80D");
        int lot = Receive(host, s.Customer, s.Raw, 10000);

        int planId;
        using (var scope = host.Services.CreateScope())
        {
            var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            TestCapacityFixture.DefineMissingRates(host);
            var plan = planning.SavePlan("خطة التعديل", "Daily", "2026-09-01", "2026-09-01", 1, 1, new List<PlanItemDto>
            { new() { SourceType = "FromReceiving", LotId = lot, CustomerId = s.Customer, ProductId = s.Fin,
                      PlannedCartons = 100, PlannedQtyKg = 500, ScheduledDate = "2026-09-01" } });
            Assert.True(plan.Ok, plan.Message);
            planId = plan.Id;
            Assert.True(planning.ApprovePlan(planId).Ok);
        }

        int orderId, itemId;
        using (var scope = host.Services.CreateScope())
        {
            var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var planItemId = db.ProductionPlanItems.Single(i => i.PlanId == planId).Id;
            host.SetBusinessDate("2026-09-01"); // Arrange production on its scheduled business day, not the machine date.
            var ord = orders.SaveOrder("FromPlan", planId, s.Customer, "2026-09-01", 1, 1, new List<OrderItemDto>
            { new() { PlanItemId = planItemId, LotId = lot, CustomerId = s.Customer, ProductId = s.Fin,
                      PlannedQtyKg = 500, PlannedCartons = 100 } });
            Assert.True(ord.Ok, ord.Message);
            orderId = ord.Id;
            itemId = db.ProductionOrderItems.Single(i => i.OrderId == orderId).Id;

            // تعديل مسودة: 100 ← 60 كرتون (وزن الكرتون 5 كجم ← 300 كجم)
            var upd = orders.UpdateOrderItems(orderId, new List<OrderItemDto>
            { new() { Id = itemId, PlannedCartons = 60 } });
            Assert.False(upd.Ok);
            Assert.Contains("ثابتة", upd.Message);
            var it = db.ProductionOrderItems.AsNoTracking().Single(i => i.Id == itemId);
            Assert.Equal(100, it.PlannedCartons);
            Assert.Equal(500, it.PlannedQtyKg, 1);

            // صفر مرفوض
            var zeroUpd = orders.UpdateOrderItems(orderId, new List<OrderItemDto>
            { new() { Id = itemId, PlannedCartons = 0 } });
            Assert.False(zeroUpd.Ok);

            // بعد الاعتماد: التعديل مرفوض
            Assert.True(orders.ApproveOrder(orderId).Ok);
            var afterApprove = orders.UpdateOrderItems(orderId, new List<OrderItemDto>
            { new() { Id = itemId, PlannedCartons = 40 } });
            Assert.False(afterApprove.Ok);
            Assert.Contains("ثابتة", afterApprove.Message);
        }
    }

    // ══════════ 4) مزامنة العبوات من شاشة الوحدات ══════════
    [Fact]
    public void Packaging_Syncs_From_Units_Screen()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();

        // وحدة جديدة من شاشة الوحدات
        db.UnitsOfMeasure.Add(new DatesErp.Core.Domain.Entities.UnitOfMeasure { UnitCode = "CTN5", UnitNameAr = "كرتون 5كجم", IsActive = true });
        db.SaveChanges();

        var r = master.SyncPackagingFromUnits();
        Assert.True(r.Ok, r.Message);
        Assert.Contains(db.PackagingTypes.AsNoTracking().ToList(), p => p.PackageNameAr == "كرتون 5كجم");

        // ثانيةً: لا تكرار (Idempotent)
        int before = db.PackagingTypes.AsNoTracking().Count(p => p.PackageNameAr == "كرتون 5كجم");
        Assert.True(master.SyncPackagingFromUnits().Ok);
        Assert.Equal(before, db.PackagingTypes.AsNoTracking().Count(p => p.PackageNameAr == "كرتون 5كجم"));
    }
}
