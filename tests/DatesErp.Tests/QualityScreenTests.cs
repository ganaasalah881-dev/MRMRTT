using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §شاشة فحص وتأكيد جودة التمور بتصميم الاستمارة المعتمدة:
/// قرار الجودة، تنزيل البنود آلياً من الأمر/الجلسة، معايير الفحص المخبري والحسي،
/// الفحص اليدوي بلا أمر، وبنود الفحص من المجموعة 002 فقط.
/// </summary>
public class QualityScreenTests
{
    private sealed record Ctx(int Customer, int Raw, int Fin, int Lot, int OrderId);

    private static Ctx BuildOrderWithProduction(TestHost host)
    {
        using var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var receiving = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
        var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var c = master.SaveCustomer(null, "TQ2", "شركة الفحص", "جملة", "777", "-", true);
        var raw = master.SaveProductFull(null, "Q-R1", "سكري خام", "001", "Raw", "كجم", 20, 0, 0, null);
        var fin = master.SaveProductFull(null, "Q-F1", "سكري تام", "002", "Finished", "كرتون", 7.5, 1, 0.5, null, sourceProductId: raw.Id);
        Assert.True(c.Ok && raw.Ok && fin.Ok);

        var s = receiving.SaveShipment(c.Id, null, null, new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = raw.Id, QtyKg = 5000, PackageCount = 250, UnitWeightKg = 20 } });
        Assert.True(s.Ok && receiving.ApproveShipment(s.Id).Ok);
        int lot = db.Lots.Single(l => l.ShipmentId == s.Id).Id;

        TestCapacityFixture.DefineMissingRates(host);
        var plan = planning.SavePlan("خطة الفحص", "Daily", "2026-09-01", "2026-09-01", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lot, CustomerId = c.Id, ProductId = fin.Id, PlannedQtyKg = 3000, PriorityNo = 1 } });
        Assert.True(plan.Ok && planning.ApprovePlan(plan.Id).Ok);
        int planItemId = db.ProductionPlanItems.Single(i => i.PlanId == plan.Id).Id;
        host.SetBusinessDate("2026-09-01"); // Arrange production on its scheduled business day, not the machine date.

        var order = orders.SaveOrder("FromPlan", plan.Id, c.Id, "2026-09-01", 1, 1, TestOrderFixture.Items(host, plan.Id, c.Id, "2026-09-01"));
        Assert.True(order.Ok, order.Message);
        var approveOrder = orders.ApproveOrder(order.Id); Assert.True(approveOrder.Ok, approveOrder.Message);

        // إنتاج فعلي 3000 كجم حتى يصبح الأمر مصدراً صالحاً للفحص
        var close = exec.CloseProductionDay(order.Id, 3000, 400, 0, 0, 0, false, new List<DowntimeDto>(), false);
        Assert.True(close.Ok, close.Message);
        return new Ctx(c.Id, raw.Id, fin.Id, lot, order.Id);
    }

    [Fact]
    public void SaveCheck_From_Order_With_LabStandards_And_Decision_Persists_Everything()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = BuildOrderWithProduction(host);

        using var scope = host.Services.CreateScope();
        var quality = scope.ServiceProvider.GetRequiredService<IQualityService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var r = quality.SaveCheck(ctx.OrderId, null, "2026-09-03", "نهائي — بعد التبريد",
            new List<QualityItemDto>
            { new() { ProductId = ctx.Fin, LotId = ctx.Lot, AcceptedQtyKg = 2950, RejectedQtyKg = 50, Notes = "عينة ممتازة" } },
            null,
            new QualityLabDto
            {
                Decision = "Quarantine",
                MoisturePct = 15.2,
                BrixDeg = 70.1,
                SkinSeparationPct = 1.8,
                ImpuritiesPct = 0.2,
                SampleCartons = 12,
                InspectorNotes = "حجز مؤقت لحين مطابقة الرطوبة"
            });
        Assert.True(r.Ok, r.Message);

        var check = db.QualityChecks.Include(c => c.Items).Single(c => c.Id == r.Id);
        Assert.Equal("Quarantine", check.Decision);
        Assert.Equal(15.2, check.MoisturePct, 1);
        Assert.Equal(70.1, check.BrixDeg, 1);
        Assert.Equal(1.8, check.SkinSeparationPct, 1);
        Assert.Equal(0.2, check.ImpuritiesPct, 1);
        Assert.Equal(12, check.SampleCartons);
        Assert.Equal("حجز مؤقت لحين مطابقة الرطوبة", check.InspectorNotes);
        Assert.Equal("عينة ممتازة", check.Items.Single().Notes);
        Assert.Equal(2950, check.AcceptedKg, 1);
        Assert.Equal(50, check.RejectedKg, 1);

        // الاعتماد يعمل ويحمل القرار في الرسالة
        var ap = quality.ApproveCheck(r.Id);
        Assert.True(ap.Ok, ap.Message);
        Assert.Contains("حجز", ap.Message);
    }

    [Fact]
    public void Manual_Check_Without_Order_Is_Saved()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = BuildOrderWithProduction(host);

        using var scope = host.Services.CreateScope();
        var quality = scope.ServiceProvider.GetRequiredService<IQualityService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var r = quality.SaveCheck(null, null, "2026-09-04", "أثناء العملية",
            new List<QualityItemDto> { new() { ProductId = ctx.Fin, AcceptedQtyKg = 100 } });
        Assert.True(r.Ok, r.Message);
        var check = db.QualityChecks.Single(c => c.Id == r.Id);
        Assert.Null(check.OrderId);
        Assert.Equal("Passed", check.Decision); // القرار الافتراضي مطابق ومقبول
    }

    [Fact]
    public void Check_Items_Must_Be_Finished_002_Only()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = BuildOrderWithProduction(host);

        using var scope = host.Services.CreateScope();
        var quality = scope.ServiceProvider.GetRequiredService<IQualityService>();

        // محاولة فحص صنف خام (001) في بنود الفحص ← مرفوضة (نظام الوحدات)
        var raw = quality.SaveCheck(ctx.OrderId, null, "2026-09-03", "نهائي",
            new List<QualityItemDto> { new() { ProductId = ctx.Raw, LotId = ctx.Lot, AcceptedQtyKg = 100 } });
        Assert.False(raw.Ok);
        Assert.Contains("002", raw.Message);
    }

    [Fact]
    public void Decision_Rejected_Is_Preserved_And_Approval_Confirms_It()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = BuildOrderWithProduction(host);

        using var scope = host.Services.CreateScope();
        var quality = scope.ServiceProvider.GetRequiredService<IQualityService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var r = quality.SaveCheck(ctx.OrderId, null, "2026-09-03", "نهائي",
            new List<QualityItemDto> { new() { ProductId = ctx.Fin, LotId = ctx.Lot, AcceptedQtyKg = 0, RejectedQtyKg = 3000 } },
            null,
            new QualityLabDto { Decision = "Rejected", MoisturePct = 22.5, InspectorNotes = "رطوبة مرتفعة جداً — عوادم" });
        Assert.True(r.Ok, r.Message);
        Assert.Equal("Rejected", db.QualityChecks.Single(c => c.Id == r.Id).Decision);

        var ap = quality.ApproveCheck(r.Id);
        Assert.True(ap.Ok, ap.Message);
        Assert.Contains("مرفوض", ap.Message);
    }
}
