using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B95 — الإقفال اليومي والإرسال للفحص: الإرسال ينشئ فحصاً معلقاً بكمية المنتَج (تبريد يومان)،
/// وبدون إرسال لا يُنشأ فحص. والمخرج الثانوي يضيفه المستخدم بلا ثوابت ولا تكرار.
/// </summary>
public class ExecutionB57Tests
{
    private static DatesErpDbContext Db(TestHost host)
        => new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);

    private static (int planId, int planItemId, int orderId) BuildApprovedOrder(TestHost host)
    {
        var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var plan = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var rcv = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var cust = master.SaveCustomer(null, "B57-C", "عميل الإقفال", "جملة", "777", "-", true);
        var raw = master.SaveProductFull(null, "001-B57", "خام الإقفال", "001", "Raw", "كجم", 20, 0, 0, null);
        var fin = master.SaveProductFull(null, "002-B57", "تام الإقفال", "002", "Finished", "كرتون", 10, 5, 2,
            new System.Collections.Generic.List<(int, int?, int)> { (1, null, 4000) }, raw.Id);
        var sh = rcv.SaveShipment(cust.Id, null, null, new System.Collections.Generic.List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = raw.Id, QtyKg = 50000, PackageCount = 2500, UnitWeightKg = 20 } });
        rcv.ApproveShipment(sh.Id);
        int lot = db.Lots.AsNoTracking().Where(l => l.ShipmentId == sh.Id).Select(l => l.Id).First();
        var r = plan.SavePlan("خطة B57", "Daily", "2026-09-01", "2026-09-01", 1, 1,
            new System.Collections.Generic.List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lot, CustomerId = cust.Id,
                        ProductId = fin.Id, PlannedCartons = 1000, PlannedQtyKg = 10000, PriorityNo = 1 }
            });
        Assert.True(r.Ok, r.Message);
        Assert.True(plan.ApprovePlan(r.Id).Ok);
        int item = db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == r.Id).Select(i => i.Id).First();
        int lotId = db.ProductionPlanItems.AsNoTracking().Where(i => i.Id == item).Select(i => i.LotId!.Value).First();
        int finId = db.ProductionPlanItems.AsNoTracking().Where(i => i.Id == item).Select(i => i.ProductId).First();
        host.SetBusinessDate("2026-09-01"); // Arrange production on its scheduled business day, not the machine date.
        var or = orders.SaveOrder("FromPlan", r.Id, cust.Id, "2026-09-01", 1, 1,
            new System.Collections.Generic.List<OrderItemDto>
            { new() { PlanItemId = item, LotId = lotId, CustomerId = cust.Id, ProductId = finId, PlannedCartons = 1000, PlannedQtyKg = 10000 } });
        Assert.True(or.Ok, or.Message);
        Assert.True(orders.ApproveOrder(or.Id).Ok);
        return (r.Id, item, or.Id);
    }

    [Fact]
    public void DayClose_With_SendToQuality_Creates_Pending_Check_With_Produced_Quantity()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (_, _, orderId) = BuildApprovedOrder(host);
        var svc = host.Services.CreateScope().ServiceProvider.GetRequiredService<IExecutionService>();

        var res = svc.CloseProductionDay(orderId, 5000, 500, 0, 0, 10, false, new List<DowntimeDto>(), true, null);
        Assert.True(res.Ok, res.Message);

        using var db = Db(host);
        var pending = db.QualityChecks.AsNoTracking().Single(c => c.OrderId == orderId);
        Assert.Equal(DocStatuses.Submitted, pending.Status);
        Assert.Equal(5000, pending.TotalCheckedKg, 1);   // كمية الفحص = المنتَج الفعلي
        Assert.Equal(System.DateTime.Today.AddDays(2), pending.ExpectedCheckDate?.Date);   // تبريد يومان
    }

    [Fact]
    public void DayClose_Without_SendToQuality_Creates_No_Check()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (_, _, orderId) = BuildApprovedOrder(host);
        var svc = host.Services.CreateScope().ServiceProvider.GetRequiredService<IExecutionService>();

        var res = svc.CloseProductionDay(orderId, 3000, 300, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        Assert.True(res.Ok, res.Message);

        using var db = Db(host);
        Assert.Empty(db.QualityChecks.AsNoTracking().Where(c => c.OrderId == orderId).ToList());
    }

    [Fact]
    public void User_Can_Add_A_ByProduct_With_No_Duplicates()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var master = host.Services.CreateScope().ServiceProvider.GetRequiredService<MasterDataService>();

        var r = master.SaveByProduct(null, "مخلفات فرز", "كجم");
        Assert.True(r.Ok, r.Message);
        var dup = master.SaveByProduct(null, "مخلفات فرز", "كجم");
        Assert.False(dup.Ok);                       // التكرار مرفوض
        Assert.Contains("بنفس الاسم", dup.Message);

        using var db = Db(host);
        Assert.Contains(db.ByProducts.AsNoTracking().ToList(), b => b.ByProductNameAr == "مخلفات فرز" && b.IsActive);
    }

    // §B78: شاشة الإقفال حُذفت لإعادة التصميم من الصفر — يُعاد حارس حقول التوقف مع الشاشة الجديدة
}
