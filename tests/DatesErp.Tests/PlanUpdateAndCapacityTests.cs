using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// اختبارات تعديل الخطة القائمة (بدل إنشاء نسخة مكررة) + سيناريو الطاقة:
/// خطة 3500 كرتون ضمن طاقة 5000 كرتون يجب أن تُقبل (لا تُرفض).
/// </summary>
public class PlanUpdateAndCapacityTests
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
    public void Plan_With_3500_Cartons_Within_5000_Capacity_Is_Accepted()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lotId = SeedLot(host, 100000); // خامة كافية

        // طاقة الصنف 3 في الوردية 1 = 5000 كرتون
        var cap = Svc<ICapacityService>(host);
        Assert.True(cap.SetCapacity(3, 1, 5000).Ok);
        var (rate, capacity) = cap.GetCapacity(3, 1);
        Assert.Equal(5000, capacity);

        // خطة 3500 كرتون (أقل من 5000) يجب أن تُقبل
        var planning = Svc<IPlanningService>(host);
        var r = planning.SavePlan("خطة ضمن الطاقة", "Daily", "2026-11-01", "2026-11-01", 1, 1,
            new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3,
                        PlannedQtyKg = 3500 * 7.5, PlannedCartons = 3500,
                        ScheduledDate = "2026-11-01", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 }
            });
        Assert.True(r.Ok, r.Message); // يجب ألا تُرفض
    }

    [Fact]
    public void UpdatePlan_Updates_Existing_Plan_Without_Duplication()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lotId = SeedLot(host, 100000);

        var cap = Svc<ICapacityService>(host);
        Assert.True(cap.SetCapacity(3, 1, 5000).Ok);

        var planning = Svc<IPlanningService>(host);
        var created = planning.SavePlan("خطة أصلية", "Daily", "2026-11-01", "2026-11-01", 1, 1,
            new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3,
                        PlannedQtyKg = 15000, PlannedCartons = 2000,
                        ScheduledDate = "2026-11-01", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 }
            });
        Assert.True(created.Ok, created.Message);
        int planId = created.Id;

        // عدد الخطط قبل التعديل
        int countBefore;
        using (var db = FreshDb(host)) countBefore = db.ProductionPlans.Count();

        // تعديل الخطة القائمة: تغيير الكمية إلى 2500 كرتون
        var updated = planning.UpdatePlan(planId, "خطة معدّلة", "Daily", "2026-11-01", "2026-11-01", 1, 1,
            new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3,
                        PlannedQtyKg = 2500 * 7.5, PlannedCartons = 2500,
                        ScheduledDate = "2026-11-01", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 }
            });
        Assert.True(updated.Ok, updated.Message);
        Assert.Equal(planId, updated.Id); // نفس الخطة — ليست نسخة جديدة

        // لم تُنشأ خطة إضافية
        using (var db = FreshDb(host))
        {
            Assert.Equal(countBefore, db.ProductionPlans.Count());
            var plan = db.ProductionPlans.Include(p => p.Items).Single(p => p.Id == planId);
            Assert.Equal("خطة معدّلة", plan.PlanTitle);
            Assert.Single(plan.Items);
            Assert.Equal(2500, plan.Items[0].PlannedCartons);
        }
    }

    [Fact]
    public void UpdatePlan_On_Approved_Plan_Is_Rejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lotId = SeedLot(host, 100000);

        var cap = Svc<ICapacityService>(host);
        Assert.True(cap.SetCapacity(3, 1, 5000).Ok);

        var planning = Svc<IPlanningService>(host);
        var created = planning.SavePlan("خطة للاعتماد", "Daily", "2026-11-01", "2026-11-01", 1, 1,
            new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3,
                        PlannedQtyKg = 15000, PlannedCartons = 2000,
                        ScheduledDate = "2026-11-01", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 }
            });
        Assert.True(created.Ok, created.Message);
        Assert.True(planning.ApprovePlan(created.Id).Ok);

        // محاولة تعديل خطة معتمدة → رفض
        var upd = planning.UpdatePlan(created.Id, "محاولة تعديل", "Daily", "2026-11-01", "2026-11-01", 1, 1,
            new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3,
                        PlannedQtyKg = 7500, PlannedCartons = 1000,
                        ScheduledDate = "2026-11-01", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 }
            });
        Assert.False(upd.Ok);
        Assert.Contains("معتمدة", upd.Message);
    }
}
