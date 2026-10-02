using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>§B75 — إصلاح البنود المعطوبة في رأس خطة الإنتاج: النطاق والعميل يُحفظان ويُستعادان،
/// «أسبوعية» لها فترة مميزة، وبصمة الإنشاء/الاعتماد محفوظة.</summary>
public class PlanHeaderFixTests
{
    private static IPlanningService Plan(TestHost h)
        => h.Services.CreateScope().ServiceProvider.GetRequiredService<IPlanningService>();
    private static DatesErpDbContext Db(TestHost h)
        => new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(h.Connection).Options);

    private static int MakePlanWithLot(TestHost host, out int custId)
    {
        var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var rcv = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var cust = master.SaveCustomer(null, "HD", "عميل الرأس", "جملة", "1", "-", true);
        var raw = master.SaveProductFull(null, "001-600", "خام الرأس", "001", "Raw", "كجم", 0, 0, 0, null);
        var fin = master.SaveProductFull(null, "002-600", "تام الرأس", "002", "Finished", "كرتون", 10, 5, 2, null, raw.Id);
        var sh = rcv.SaveShipment(cust.Id, null, null, new System.Collections.Generic.List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = raw.Id, QtyKg = 50000, PackageCount = 2500, UnitWeightKg = 20 } });
        rcv.ApproveShipment(sh.Id);
        var lot = db.Lots.Single(l => l.ShipmentId == sh.Id).Id;
        custId = cust.Id;
        TestCapacityFixture.DefineMissingRates(host);
        var pl = Plan(host).SavePlan("رأس", "Daily", "2026-09-01", "2026-09-01", 1, 1,
            new System.Collections.Generic.List<PlanItemDto>
            { new() { SourceType = "FromReceiving", LotId = lot, CustomerId = cust.Id, ProductId = fin.Id, PlannedCartons = 100, PlannedQtyKg = 1000 } });
        Assert.True(pl.Ok, pl.Message);
        return pl.Id;
    }

    [Fact]
    public void Scope_And_Single_Customer_Persist_In_Header()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var rcv = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var cust = master.SaveCustomer(null, "SC", "عميل النطاق", "جملة", "2", "-", true);
        var raw = master.SaveProductFull(null, "001-601", "خام النطاق", "001", "Raw", "كجم", 0, 0, 0, null);
        var fin = master.SaveProductFull(null, "002-601", "تام النطاق", "002", "Finished", "كرتون", 10, 5, 2, null, raw.Id);
        var sh = rcv.SaveShipment(cust.Id, null, null, new System.Collections.Generic.List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = raw.Id, QtyKg = 50000, PackageCount = 2500, UnitWeightKg = 20 } });
        rcv.ApproveShipment(sh.Id);
        var lot = db.Lots.Single(l => l.ShipmentId == sh.Id).Id;

        TestCapacityFixture.DefineMissingRates(host);
        var pl = Plan(host).SavePlan("نطاق", "Daily", "2026-09-01", "2026-09-01", 1, 1,
            new System.Collections.Generic.List<PlanItemDto>
            { new() { SourceType = "FromReceiving", LotId = lot, CustomerId = cust.Id, ProductId = fin.Id, PlannedCartons = 50, PlannedQtyKg = 500 } },
            null, "Single", cust.Id);
        Assert.True(pl.Ok, pl.Message);

        using var db2 = Db(host);
        var plan = db2.ProductionPlans.Single(p => p.Id == pl.Id);
        Assert.Equal("Single", plan.ScopeMode);                 // النطاق محفوظ
        Assert.Equal(cust.Id, plan.SingleCustomerId);           // والعميل محفوظ بالرأس
    }

    [Fact]
    public void UpdatePlan_Persists_Scope_Change()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var id = MakePlanWithLot(host, out var cust);
        var up = Plan(host).UpdatePlan(id, "رأس", "Daily", "2026-09-01", "2026-09-01", 1, 1,
            new System.Collections.Generic.List<PlanItemDto>(), null, "Single", cust);
        // البنود فارغة ⇒ مرفوض؛ نمرر بنداً؟ نكتفي بفحص حفظ النطاق عبر حفظ جديد
        using var db = Db(host);
        var item = db.ProductionPlanItems.First(i => i.PlanId == id);
        var ok = Plan(host).UpdatePlan(id, "رأس", "Daily", "2026-09-01", "2026-09-01", 1, 1,
            new System.Collections.Generic.List<PlanItemDto>
            { new() { SourceType = "FromReceiving", LotId = item.LotId, CustomerId = cust, ProductId = item.ProductId, PlannedCartons = 100, PlannedQtyKg = 1000 } },
            null, "Single", cust);
        Assert.True(ok.Ok, ok.Message);
        using var db2 = Db(host);
        Assert.Equal("Single", db2.ProductionPlans.Single(p => p.Id == id).ScopeMode);
    }

    [Fact]
    public void Weekly_Period_Is_Start_Plus_Six_Days()
    {
        var start = new DateTime(2026, 9, 1);
        Assert.Equal(start, PlanningService.PeriodEndDate("Daily", start));
        Assert.Equal(start.AddDays(6), PlanningService.PeriodEndDate("Weekly", start));
        Assert.Equal(start, PlanningService.PeriodEndDate("Period", start)); // الفترة حرة
    }

    [Fact]
    public void CreatedBy_And_Approval_Stamps_Are_Stored()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var id = MakePlanWithLot(host, out _);
        using (var db = Db(host))
        {
            var plan = db.ProductionPlans.Single(p => p.Id == id);
            Assert.NotNull(plan.CreatedBy);        // المنشئ محفوظ
            Assert.NotEqual(default(DateTime), plan.CreatedDate);
            Assert.False(plan.IsApproved);
        }
        Assert.True(Plan(host).ApprovePlan(id).Ok);
        using var db2 = Db(host);
        var approved = db2.ProductionPlans.Single(p => p.Id == id);
        Assert.True(approved.IsApproved);
        Assert.NotNull(approved.ApprovedBy);       // المعتمِد محفوظ
        Assert.NotNull(approved.ApprovedDate);
    }
}
