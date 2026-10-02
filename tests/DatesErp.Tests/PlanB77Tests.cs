using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>§B77 — أولوية العميل، النوع الشهري، واقتراح اليوم البديل عند رفض الطاقة.</summary>
public class PlanB77Tests
{
    private static ICapacityService Caps(TestHost h)
        => h.Services.CreateScope().ServiceProvider.GetRequiredService<ICapacityService>();
    private static IPlanningService Plan(TestHost h)
        => h.Services.CreateScope().ServiceProvider.GetRequiredService<IPlanningService>();

    [Fact]
    public void Monthly_Type_Ends_At_Month_End()
    {
        var s = new DateTime(2026, 9, 1);
        Assert.Equal(new DateTime(2026, 9, 30), PlanningService.PeriodEndDate("Monthly", s));
        var f = new DateTime(2026, 2, 10);
        Assert.Equal(new DateTime(2026, 3, 9), PlanningService.PeriodEndDate("Monthly", f));
    }

    [Fact]
    public void Customer_Priority_Leads_Fair_Distribution()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var rcv = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        // عميل بأولوية 1 وآخر بلا أولوية
        var vip = master.SaveCustomer(null, "V1", "عميل أولوية", "جملة", "1", "-", true, 1);
        var nor = master.SaveCustomer(null, "N1", "عميل عادي", "جملة", "2", "-", true, 0);
        var raw = master.SaveProductFull(null, "001-800", "خام الأولوية", "001", "Raw", "كجم", 0, 0, 0, null);
        var fin = master.SaveProductFull(null, "002-800", "تام الأولوية", "002", "Finished", "كرتون", 10, 5, 2, null, raw.Id);
        Assert.True(Caps(host).SaveHourlyRate(fin.Id, 100).Ok);
        foreach (var c in new[] { vip.Id, nor.Id })
        {
            var sh = rcv.SaveShipment(c, null, null, new System.Collections.Generic.List<ShipmentItemDto>
            { new() { TreatmentRequired = false, ProductId = raw.Id, QtyKg = 20000, PackageCount = 1000, UnitWeightKg = 20 } });
            rcv.ApproveShipment(sh.Id);
        }
        var p = Plan(host).SuggestFairDistribution("2026-09-01", "2026-09-03", 1, 1, fin.Id, 1000);
        Assert.True(p.Rows.Count > 0, "لا صفوف توزيع");
        Assert.Equal(vip.Id, p.Rows[0].CustomerId ?? -1);   // الأولوية تُخدم أولاً (§B87: العميل فارغ-القبول)
    }

    [Fact]
    public void Capacity_Rejection_Suggests_Nearest_Alternative_Day()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var rcv = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var cust = master.SaveCustomer(null, "AD", "عميل البديل", "جملة", "3", "-", true);
        var raw = master.SaveProductFull(null, "001-801", "خام البديل", "001", "Raw", "كجم", 0, 0, 0, null);
        var fin = master.SaveProductFull(null, "002-801", "تام البديل", "002", "Finished", "كرتون", 10, 5, 2, null, raw.Id);
        Assert.True(Caps(host).SaveHourlyRate(fin.Id, 100).Ok);      // 800/يوم بوردية 8س
        var sh = rcv.SaveShipment(cust.Id, null, null, new System.Collections.Generic.List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = raw.Id, QtyKg = 100000, PackageCount = 5000, UnitWeightKg = 20 } });
        rcv.ApproveShipment(sh.Id);
        var lot = db.Lots.Single(l => l.ShipmentId == sh.Id).Id;

        // يوم1 مشغول بـ500 (5س)؛ بند 400 إضافية (4س) ⇒ 9س > 8س ⇒ رفض + اقتراح يوم2 الفارغ
        var r = Plan(host).SavePlan("بديل", "Daily", "2026-09-01", "2026-09-01", 1, 1,
            new System.Collections.Generic.List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lot, CustomerId = cust.Id, ProductId = fin.Id, PlannedCartons = 500, PlannedQtyKg = 5000, ScheduledDate = "2026-09-01" },
                new() { SourceType = "FromReceiving", LotId = lot, CustomerId = cust.Id, ProductId = fin.Id, PlannedCartons = 400, PlannedQtyKg = 4000, ScheduledDate = "2026-09-01" }
            });
        Assert.False(r.Ok);
        Assert.Contains("أقرب يوم بديل", r.Message);   // الاقتراح موجود في رسالة الرفض
        Assert.Contains("02/09/2026", r.Message);
    }
}
