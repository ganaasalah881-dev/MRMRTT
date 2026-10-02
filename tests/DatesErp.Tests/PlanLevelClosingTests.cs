using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B95 — اختبارات الإقفال اليومي الموحد (عبر أوامر الإنتاج): إقفال كل الأوامر أو أمر واحد أو جزء من أمر،
/// والتحقق أن المتبقي يُعاد لمخزن الخام بنفس العميل والدفعة (حركة مرتجع) وأن الحجز يُعاد احتسابه —
/// فلا يوجد «حجز فوق حجز» — وأن الفحص يُرسل (تبريد يومان)، وأن الحراس ترفض التجاوز والسالب والفارغ.
/// </summary>
public class PlanLevelClosingTests
{
    /// <summary>خطة بعميلين وبندين + أمر تشغيل معتمد لكل بند (الخام يُصرف عند الإقفال لا الاعتماد).</summary>
    private static (TestHost host, int planId, int itemA, int itemB, int lotA, int lotB, int orderA, int orderB) SeedTwoItemPlan()
    {
        var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var receiving = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();

        // مواد مساعدة كافية
        var whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 100000 },
            new StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 100000 });

        // عميل ثانٍ وشحنتان (دفعتان لعميلين مختلفين)
        db.Customers.Add(new Customer { CustomerCode = "CX2", CustomerName = "عميل ثانٍ", IsActive = true });
        db.SaveChanges();
        int cust2 = db.Customers.Single(c => c.CustomerCode == "CX2").Id;

        var s1 = receiving.SaveShipment(1, null, null, new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 1, PackageCount = 1, UnitWeightKg = 10000, QtyKg = 10000 } });
        Assert.True(receiving.ApproveShipment(s1.Id).Ok);
        // §تتبع الصنف: دفعة العميل الثاني من خام سكري (2) لتطابق المنتج المخطط لها (4 = سكري)
        var s2 = receiving.SaveShipment(cust2, null, null, new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 2, PackagingTypeId = 1, PackageCount = 1, UnitWeightKg = 6000, QtyKg = 6000 } });
        Assert.True(receiving.ApproveShipment(s2.Id).Ok);
        var lotsList = db.Lots.OrderBy(l => l.Id).ToList();
        int lotA = lotsList[^2].Id;
        int lotB = lotsList[^1].Id;

        string day = DateTime.Today.AddDays(1).ToString("dd/MM/yyyy");
        var plan = planning.SavePlan("خطة الإقفال على مستويين", "Period", day, day, 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lotA, CustomerId = 1, ProductId = 3, PackagingTypeId = 1,
                    PlannedQtyKg = 10000, PlannedCartons = 2000, ScheduledDate = day, SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 },
            new() { SourceType = "FromReceiving", LotId = lotB, CustomerId = cust2, ProductId = 4, PackagingTypeId = 1,
                    PlannedQtyKg = 6000, PlannedCartons = 1200, ScheduledDate = day, SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 2 }
        });
        Assert.True(plan.Ok, plan.Message);
        Assert.True(planning.ApprovePlan(plan.Id).Ok);
        var itemIds = db.ProductionPlanItems.Where(i => i.PlanId == plan.Id).OrderBy(i => i.PriorityNo).Select(i => i.Id).ToList();

        // أمر لكل عميل (الاستخدام الصحيح: الأمر لعميل واحد ودفعة عميله)
        host.SetBusinessDate(day); // Arrange production on its scheduled business day, not the machine date.
        var order1 = orders.SaveOrder("FromPlan", plan.Id, 1, day, 1, 1, new List<OrderItemDto>
        {
            new() { PlanItemId = itemIds[0], LotId = lotA, CustomerId = 1, ProductId = 3, PackagingTypeId = 1, PlannedQtyKg = 10000, PlannedCartons = 2000 }
        });
        Assert.True(order1.Ok, order1.Message);
        var ao1 = orders.ApproveOrder(order1.Id); Assert.True(ao1.Ok, ao1.Message); // §B85/M7: الاعتماد لا يخصم الخام — يُصرف عند الإقفال
        host.SetBusinessDate(day); // Arrange production on its scheduled business day, not the machine date.

        var order2 = orders.SaveOrder("FromPlan", plan.Id, cust2, day, 1, 1, new List<OrderItemDto>
        {
            new() { PlanItemId = itemIds[1], LotId = lotB, CustomerId = cust2, ProductId = 4, PackagingTypeId = 1, PlannedQtyKg = 6000, PlannedCartons = 1200 }
        });
        Assert.True(order2.Ok, order2.Message);
        var ao2 = orders.ApproveOrder(order2.Id); Assert.True(ao2.Ok, ao2.Message);
        return (host, plan.Id, itemIds[0], itemIds[1], lotA, lotB, order1.Id, order2.Id);
    }

    [Fact]
    public void CloseWholePlan_ReturnsRemaindersToRaw_SendsQuality_AndAutoCloses()
    {
        var seed = SeedTwoItemPlan();
        using var host = seed.host;
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();

        double inStockA_before = db.Lots.AsNoTracking().Single(l => l.Id == seed.lotA).InStockQtyKg; // كامل (10,000) — الاعتماد لا يخصم الخام

        // §B95 — إقفال يوم كل أمر: الأول 8000 إنتاج + 200 حشف + 100 نوى ← متبقي 1700 يُعاد للخام، والثاني كاملاً
        var r = exec.CloseProductionDay(seed.orderA, 8000, 1600, 200, 100, 0, false,
            new List<DowntimeDto> { new() { Hours = 1, ReasonAr = "صيانة" } }, true, "إقفال كامل");
        Assert.True(r.Ok, r.Message);
        var r2 = exec.CloseProductionDay(seed.orderB, 6000, 1200, 0, 0, 0, false,
            new List<DowntimeDto>(), true, null);
        Assert.True(r2.Ok, r2.Message);

        // §B85/H1: الإقفال يصرف الخامه (10,000) ثم يعيد المتبقي (1,700) — الصافي −8,300 + حركة مرتجع موثقة
        double inStockA_after = db.Lots.AsNoTracking().Single(l => l.Id == seed.lotA).InStockQtyKg;
        Assert.Equal(inStockA_before - 8300, inStockA_after, 1);
        Assert.Contains(db.InventoryTransactions, t => t.ReferenceDocType == ReferenceDocType.Return
            && t.LotId == seed.lotA && t.QtyKg == 1700);


        // الفحص أُرسل لكل أمر (فترة تبريد يومان)
        var checks = db.QualityChecks.Where(c => c.ExpectedCheckDate != null).ToList();
        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.Equal(DateTime.Today.AddDays(2), c.ExpectedCheckDate));

        // §B79: مكتملة ≠ مقفلة — الإقفال قرار صريح من شاشة إقفال خطة الإنتاج
        Assert.False(db.ProductionPlans.AsNoTracking().Single(p => p.Id == seed.planId).IsClosed);
        var orderSvc = host.Services.CreateScope().ServiceProvider.GetRequiredService<IProductionOrderService>();
        Assert.True(orderSvc.CloseOrder(seed.orderA, "تسوية: عجز 2000 كجم — توقف صيانة موثق").Ok);   // ناقص ← بتسوية
        Assert.True(orderSvc.CloseOrder(seed.orderB).Ok);   // مكتمل ← مباشرة
        var planClosure = host.Services.CreateScope().ServiceProvider.GetRequiredService<IPlanClosureService>();
        var premature = planClosure.ClosePlanFinal(seed.planId);
        Assert.False(premature.Ok); // لا يكفي الإنتاج والتسوية وحدهما قبل تحرير أمر التسليم
        Assert.Contains("تسليم الإنتاج", premature.Message);
        var deliveries = host.Services.CreateScope().ServiceProvider.GetRequiredService<IProductionDeliveryService>();
        foreach (int oid in new[] { seed.orderA, seed.orderB })
        {
            int executionId = db.ProductionExecutions.Single(e => e.OrderId == oid).Id;
            var draft = deliveries.CreateDeliveryFromActual(executionId, DateTime.Today.ToString("dd/MM/yyyy"));
            Assert.True(draft.Ok, draft.Message);
            var issued = deliveries.IssueDelivery(draft.Id);
            Assert.True(issued.Ok, issued.Message);
        }
        var cl = planClosure.ClosePlanFinal(seed.planId); // قد يكون أُقفل آلياً مع التسليم الأخير
        Assert.True(cl.Ok, cl.Message);
        Assert.True(db.ProductionPlans.AsNoTracking().Single(p => p.Id == seed.planId).IsClosed);
        // الحجوزات تحررت عند الإقفال الصريح
        Assert.Equal(0, db.Lots.AsNoTracking().Single(l => l.Id == seed.lotA).ReservedQtyKg, 1);
        Assert.Equal(0, db.Lots.AsNoTracking().Single(l => l.Id == seed.lotB).ReservedQtyKg, 1);

        // جلسة التنفيذ تحفظ حساب الإقفال: المستهلك والمنتج والمتبقي والتوقفات
        var exeA = db.ProductionExecutions.Include(e => e.Downtimes).Single(e => e.OrderId == seed.orderA);
        Assert.Equal(10000, exeA.ConsumedRawKg, 1);   // كم خاماً استُلم
        Assert.Equal(8000, exeA.ActualQtyKg, 1);       // كم أُنتج تاماً
        Assert.Equal(1600, exeA.ActualCartons);        // كم كرتوناً
        Assert.Equal(200, exeA.HashfKg, 1);            // مخرجات ثانوية
        Assert.Equal(1700, exeA.RemainingInHallKg, 1); // المتبقي المُعاد للخام
        Assert.Single(exeA.Downtimes);
    }

    [Fact]
    public void CloseSingleItem_KeepsPlanOpen_OtherItemUnaffected()
    {
        var seed = SeedTwoItemPlan();
        using var host = seed.host;
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();

        // §B95 — إقفال يوم أمر واحد فقط (الخطة فترية — تبقى مفتوحة للأمر الآخر)
        var r = exec.CloseProductionDay(seed.orderA, 10000, 2000, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        Assert.True(r.Ok, r.Message);

        var plan = db.ProductionPlans.AsNoTracking().Single(p => p.Id == seed.planId);
        Assert.False(plan.IsClosed); // لم تكتمل كل البنود
        var itemB = db.ProductionPlanItems.AsNoTracking().Single(i => i.Id == seed.itemB);
        Assert.Equal(6000, itemB.PlannedQtyKg, 1); // البند الآخر لم يُمس
        Assert.Equal(0, itemB.ProducedQtyKg, 1);
    }

    [Fact]
    public void ClosePartOfOrder_KeepsItemOpen_And_ReservesRemainder()
    {
        var seed = SeedTwoItemPlan();
        using var host = seed.host;
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();

        Assert.Equal(10000, db.Lots.AsNoTracking().Single(l => l.Id == seed.lotA).ReservedQtyKg, 1);

        // §B95 — إقفال يوم جزئي: 4000 من 10000 — المتبقي يعود للخام ويبقى محجوزاً لاستكمال العمل
        var r = exec.CloseProductionDay(seed.orderA, 4000, 800, 0, 0, 0, false, new List<DowntimeDto>(), true, null);
        Assert.True(r.Ok, r.Message);

        var itemA = db.ProductionPlanItems.AsNoTracking().Single(i => i.Id == seed.itemA);
        Assert.Equal(4000, itemA.ProducedQtyKg, 1);
        // المخطط محفوظ (لا يُخفَض للمنتَج) والبند يبقى مفتوحاً للمتبقي — لا إغلاق بلا تسوية
        Assert.Equal(10000, itemA.PlannedQtyKg, 1);
        Assert.False(itemA.IsClosed);

        // §قاعدة توازن الإنتاج: لا يُخصم الخام عند الاعتماد — يُصرف عند الإقفال فعلياً.
        // §B85/H1: صُرف (10,000) وعاد المتبقي (6,000) = 6,000 رصيداً، والمتبقي المخطط (6,000) محجوز لاستكماله.
        Assert.Equal(6000, db.Lots.AsNoTracking().Single(l => l.Id == seed.lotA).InStockQtyKg, 1);
        Assert.Equal(6000, db.Lots.AsNoTracking().Single(l => l.Id == seed.lotA).ReservedQtyKg, 1);
    }

    [Fact]
    public void CloseDay_Guards_OverProduction_Negative_And_Empty()
    {
        var seed = SeedTwoItemPlan();
        using var host = seed.host;
        using var scope = host.Services.CreateScope();
        var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();

        // فوق المخطط ← مرفوض
        var over = exec.CloseProductionDay(seed.orderA, 999999, 0, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        Assert.False(over.Ok);
        Assert.Contains("تتجاوز", over.Message);

        // سالب ← مرفوض
        var neg = exec.CloseProductionDay(seed.orderA, -10, 0, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        Assert.False(neg.Ok);
        Assert.Contains("سالبة", neg.Message);

        // §B95 — إقفال بلا إنتاج ← مرفوض (يحمي من الإقفال الفارغ بالخطأ)
        var empty = exec.CloseProductionDay(seed.orderA, 0, 0, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        Assert.False(empty.Ok);
        Assert.Contains("بلا إنتاج", empty.Message);
    }
}
