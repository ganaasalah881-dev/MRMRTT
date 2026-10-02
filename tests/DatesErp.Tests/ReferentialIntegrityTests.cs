using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §v1.50.27 — الثغرة التي أبلغ عنها المستخدم: استلام معتمد ← خطط منه ← إلغاء
/// اعتماد الاستلام وحذفه (فُقدت الدفعات وتيمّت الخطط) ← أوامر يتيمة. الحراس:
/// لا إلغاء اعتماد استلام دوافعه مرجَعة، ولا حذف سند مرجَع، ولا تعديل/حذف
/// خطة بنودها مرجَعة من أوامر — مع بقاء المسارات المشروعة تعمل.
/// </summary>
public class ReferentialIntegrityTests
{
    private static string D(int offset) => DateTime.Today.AddDays(offset).ToString("yyyy-MM-dd");

    /// <summary>شحنة مستلمة ومعتمدة (دفعة واحدة) — نمط B100.</summary>
    private static (int shipId, int lotId) SeedShipment(TestHost host)
    {
        host.LoginAsAdmin();
        var rcv = host.Get<IReceivingService>();
        var s = rcv.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000 } });
        Assert.True(s.Ok, s.Message);
        Assert.True(rcv.ApproveShipment(s.Id).Ok);
        var db = host.Get<DatesErpDbContext>();
        return (s.Id, db.Lots.OrderBy(l => l.Id).Last().Id);
    }

    /// <summary>خطة معتمدة من دفعة الشحنة.</summary>
    private static int SeedPlan(TestHost host, int lotId, bool approve)
    {
        string day = D(-1);
        var planSvc = host.Get<IPlanningService>();
        var p = planSvc.SavePlan("خطة ثغرة", "Daily", day, day, 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3, PackagingTypeId = 1,
                 PlannedQtyKg = 500, PlannedCartons = 100, ScheduledDate = day, SuggestedShiftId = 1, SuggestedLineId = 1 } });
        Assert.True(p.Ok, p.Message);
        if (approve) Assert.True(planSvc.ApprovePlan(p.Id).Ok);
        return p.Id;
    }

    [Fact]
    public void Unapprove_Receiving_With_Referencing_Plan_Is_Blocked()
    {
        using var host = new TestHost();
        var (shipId, lotId) = SeedShipment(host);
        int planId = SeedPlan(host, lotId, approve: true);
        var db = host.Get<DatesErpDbContext>();
        string planNo = db.ProductionPlans.AsNoTracking().Single(p => p.Id == planId).DocumentNumber;

        var r = host.Get<IReceivingService>().UnapproveShipment(shipId);
        Assert.False(r.Ok);
        Assert.Contains("خطط إنتاج قائمة", r.Message);
        Assert.Contains(planNo, r.Message);
        // السلسلة سليمة: الاستلام ما زال معتمداً والدفعة موجودة
        Assert.True(db.Shipments.AsNoTracking().Single(s => s.Id == shipId).IsApproved);
        Assert.True(db.Lots.AsNoTracking().Any(l => l.Id == lotId));
    }

    [Fact]
    public void Unapprove_Blocked_Even_When_Only_A_Draft_Plan_References_The_Lot()
    {
        using var host = new TestHost();
        var (shipId, lotId) = SeedShipment(host);
        SeedPlan(host, lotId, approve: false);
        var r = host.Get<IReceivingService>().UnapproveShipment(shipId);
        Assert.False(r.Ok);
        Assert.Contains("خطط إنتاج قائمة", r.Message);
    }

    [Fact]
    public void Unapprove_Without_Plan_References_Still_Works()
    {
        using var host = new TestHost();
        var (shipId, lotId) = SeedShipment(host);
        var r = host.Get<IReceivingService>().UnapproveShipment(shipId);
        Assert.True(r.Ok, r.Message);
        var db = host.Get<DatesErpDbContext>();
        Assert.False(db.Shipments.AsNoTracking().Single(s => s.Id == shipId).IsApproved);
        // الإلغاء الحديث يحفظ هوية الدفعة للتدقيق ويعكس الرصيد بقيد، لا يمحو التاريخ.
        var lot = db.Lots.AsNoTracking().Single(l => l.Id == lotId);
        Assert.Equal(DocStatuses.Cancelled, lot.Status);
        Assert.Equal(0, lot.InStockQtyKg, 2);
        Assert.Equal(0, db.InventoryTransactions.AsNoTracking().Where(t => t.LotId == lotId).Sum(t => t.QtyKg), 2);
    }

    [Fact]
    public void Delete_Receiving_Referenced_By_Plan_Items_Is_Blocked()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        // شحنة مسودة (بلا دفعات) يُحشر مرجع بند خطة عليها يدوياً — محاكاة بيانات قديمة
        var rcv = host.Get<IReceivingService>();
        var s = rcv.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 10, UnitWeightKg = 20, QtyKg = 200 } });
        Assert.True(s.Ok, s.Message);
        db.ProductionPlanItems.Add(new ProductionPlanItem { PlanId = SeedPlanFromManual(host), ShipmentId = s.Id, ProductId = 3, PlannedQtyKg = 10, PlannedCartons = 1 });
        db.SaveChanges();
        var r = rcv.DeleteShipment(s.Id);
        Assert.False(r.Ok);
        Assert.Contains("مرجَع من مستندات إنتاج قائمة", r.Message);
        Assert.True(db.Shipments.AsNoTracking().Any(x => x.Id == s.Id));
    }

    private static int SeedPlanFromManual(TestHost host)
    {
        var plan = new ProductionPlan { DocumentNumber = "PLAN-MANUAL-1", PlanTitle = "يدوي", PlanType = "Daily", Status = DocStatuses.Draft, ScopeMode = "Multi" };
        host.Get<DatesErpDbContext>().ProductionPlans.Add(plan);
        host.Get<DatesErpDbContext>().SaveChanges();
        return plan.Id;
    }

    [Fact]
    public void Update_Plan_With_Issued_Orders_Is_Blocked()
    {
        using var host = new TestHost();
        var (shipId, lotId) = SeedShipment(host);
        int planId = SeedPlan(host, lotId, approve: true);
        var db = host.Get<DatesErpDbContext>();
        var planItems = db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == planId).ToList();
        var o = LegacyOrderFixture.ImportApproved(host, planId, 1, D(-1), 1, 1, new List<OrderItemDto>
        { new() { PlanItemId = planItems[0].Id, LotId = lotId, CustomerId = 1, ProductId = 3, PackagingTypeId = 1, PlannedQtyKg = 500, PlannedCartons = 100 } });
        Assert.True(o.Ok, o.Message);

        string day = D(-1);
        var r = host.Get<IPlanningService>().UpdatePlan(planId, "تعديل ممنوع", "Daily", day, day, 1, 1,
            new List<PlanItemDto> { new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3,
                PackagingTypeId = 1, PlannedQtyKg = 100, PlannedCartons = 20, ScheduledDate = day, SuggestedShiftId = 1, SuggestedLineId = 1 } });
        Assert.False(r.Ok);
        Assert.Contains("صدرت منها أوامر إنتاج قائمة", r.Message);
        // البنود لم تُمَس
        Assert.Equal(planItems.Count, db.ProductionPlanItems.AsNoTracking().Count(i => i.PlanId == planId));
    }

    [Fact]
    public void Delete_Plan_Whose_Items_Are_Referenced_By_Orders_Is_Blocked_Even_Without_SourcePlanId()
    {
        using var host = new TestHost();
        var (shipId, lotId) = SeedShipment(host);
        int planId = SeedPlan(host, lotId, approve: true);
        var db = host.Get<DatesErpDbContext>();
        var planItems = db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == planId).ToList();
        var o = LegacyOrderFixture.ImportApproved(host, planId, 1, D(-1), 1, 1, new List<OrderItemDto>
        { new() { PlanItemId = planItems[0].Id, LotId = lotId, CustomerId = 1, ProductId = 3, PackagingTypeId = 1, PlannedQtyKg = 500, PlannedCartons = 100 } });
        Assert.True(o.Ok, o.Message);
        // بيانات قديمة: الصلة SourcePlanId مفقودة — البنود وحدها تربط
        var order = db.ProductionOrders.Single(x => x.Id == o.Id);
        order.SourcePlanId = null;
        db.SaveChanges();
        var planSvc = host.Get<IPlanningService>();
        // §v1.50.35: فك الاعتماد ممنوع بوجود أوامر قائمة — ألغِ الحركة التالية أولاً
        var un = planSvc.UnapprovePlan(planId);
        Assert.False(un.Ok);
        Assert.Contains("حركات لاحقة", un.Message);
        var r = planSvc.DeletePlan(planId);
        Assert.False(r.Ok);
        Assert.True(r.Message.Contains("معتمدة") || r.Message.Contains("مرجَعة"), r.Message);
        Assert.True(db.ProductionPlans.AsNoTracking().Any(p => p.Id == planId));
    }

    [Fact]
    public void Update_Draft_Plan_Without_Orders_Still_Works()
    {
        using var host = new TestHost();
        var (shipId, lotId) = SeedShipment(host);
        int planId = SeedPlan(host, lotId, approve: false);
        string day = D(-1);
        var r = host.Get<IPlanningService>().UpdatePlan(planId, "عنوان معدّل — مشروع", "Daily", day, day, 1, 1,
            new List<PlanItemDto> { new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3,
                PackagingTypeId = 1, PlannedQtyKg = 400, PlannedCartons = 80, ScheduledDate = day, SuggestedShiftId = 1, SuggestedLineId = 1 } });
        Assert.True(r.Ok, r.Message);
    }
}
