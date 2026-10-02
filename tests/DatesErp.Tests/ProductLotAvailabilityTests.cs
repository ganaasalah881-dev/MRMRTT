using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// اختبارات «المتاح لكل صنف من الدفعة» (مطابق لـ v1.60 product_lot_remaining):
/// دفعة واحدة يمكن تخطيطها لعدة أصناف تامة، والمتاح لكل صنف يخصم حجوزات ذلك الصنف
/// فقط لا الأصناف الأخرى — فلا تداخل بين أصناف الدفعة الواحدة.
/// </summary>
public class ProductLotAvailabilityTests
{
    /// <summary>استلام شحنة 10000 كجم واعتمادها لتوليد دفعة.</summary>
    private static int SeedLot(TestHost host, double qty)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var receiving = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var cust = db.Customers.First();
        var raw = db.Products.First(p => p.GroupCode == "001");
        var save = receiving.SaveShipment(cust.Id, "2026-08-20", "2026-08-20",
            new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = raw.Id, PackageCount = 1, UnitWeightKg = qty, QtyKg = qty } });
        Assert.True(save.Ok, save.Message);
        var approve = receiving.ApproveShipment(save.Id);
        Assert.True(approve.Ok, approve.Message);
        return db.Lots.OrderByDescending(l => l.Id).First().Id;
    }

    [Fact]
    public void PerProductAvailability_DeductsOnlySameProductReservations()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lotId = SeedLot(host, 10000);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();

        // صنفان تامان (المزروعان: 002-001 و 002-002)
        var prod3 = db.Products.First(p => p.ProductCode == "002-001");
        var prod4 = db.Products.First(p => p.ProductCode == "002-002");

        // قبل أي حجز: المتاح لكل صنف = كامل الرصيد
        Assert.Equal(10000, planning.GetProductLotRemaining(lotId, prod3.Id), 1);
        Assert.Equal(10000, planning.GetProductLotRemaining(lotId, prod4.Id), 1);

        // حجز 3000 للصنف prod3 من الدفعة
        var plan = planning.SavePlan("خطة حجز صنف", "Daily", "2026-09-01", "2026-09-01", 1, 1,
            new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = db.Customers.First().Id,
                        ProductId = prod3.Id, PackagingTypeId = 1, PlannedQtyKg = 3000, PlannedCartons = 600,
                        ScheduledDate = "2026-09-01", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 }
            });
        Assert.True(plan.Ok, plan.Message);

        // المتاح لـ prod3 انخفض إلى 7000 بينما prod4 ما زال يرى كامل الرصيد (لا تداخل)
        Assert.Equal(7000, planning.GetProductLotRemaining(lotId, prod3.Id), 1);
        Assert.Equal(10000, planning.GetProductLotRemaining(lotId, prod4.Id), 1);
    }

    [Fact]
    public void SavePlan_AllowsQuantityExceedingRawStock_Because_Water_Gains_Weight()
    {
        // §قاعدة توازن الإنتاج: المخطط كمية منتج تام مستهدفة لا حجزاً للخام.
        // وفي تصنيع التمور يزيد وزن الخارج عن الداخل لإضافة الماء أثناء التشغيل،
        // فالتخطيط بأكثر من رصيد الخام مشروع — ولا معادلة ثابتة تربطهما.
        // الحجز الفعلي للخام يتم عند الإقفال بالكمية المستهلكة فعلياً.
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lotId = SeedLot(host, 5000);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var prod3 = db.Products.First(p => p.ProductCode == "002-001");
        int custId = db.Customers.First().Id;

        var p1 = planning.SavePlan("خطة بأكثر من رصيد الخام", "Daily", "2026-09-01", "2026-09-01", 1, 1,
            new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = custId, ProductId = prod3.Id,
                        PackagingTypeId = 1, PlannedQtyKg = 6000, PlannedCartons = 1200, PriorityNo = 1 }
            });
        Assert.True(p1.Ok, "التخطيط بأكثر من رصيد الخام مشروع: " + p1.Message);
    }

    [Fact]
    public void SavePlan_MultipleProductsFromSameLot_Share_The_Lot_Without_Fixed_Equation()
    {
        // §قاعدة توازن الإنتاج: لا معادلة ثابتة تربط المخطط برصيد الخام.
        // منتجان من دفعة واحدة يُخططان بحسب المستهدف، والخام يُصرف فعلياً عند الإقفال.
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lotId = SeedLot(host, 10000);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var prod3 = db.Products.First(p => p.ProductCode == "002-001");
        int custId = db.Customers.First().Id;

        var p1 = planning.SavePlan("خطة بمنتجين من دفعة واحدة", "Daily", "2026-09-01", "2026-09-01", 1, 1,
            new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = custId, ProductId = prod3.Id,
                        PackagingTypeId = 1, PlannedQtyKg = 6000, PlannedCartons = 1200, PriorityNo = 1 },
                new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = custId, ProductId = prod3.Id,
                        PackagingTypeId = 1, PlannedQtyKg = 5000, PlannedCartons = 1000, PriorityNo = 2 }
            });
        Assert.True(p1.Ok, "منتجان من دفعة واحدة بلا معادلة ثابتة: " + p1.Message);

        // وما زال النظام يمنع تخطيط صنف لا يُصنع من سلالة الدفعة (تتبع الصنف — قيد مختلف)
        var master = scope.ServiceProvider.GetRequiredService<DatesErp.Application.Services.MasterDataService>();
        var other = master.SaveProductFull(null, "002-009", "صنف من سلالة أخرى", "002", "Finished", "كرتون", 5, 1, 5,
            new List<(int, int?, int)>(), sourceProductId: db.Products.First(p => p.GroupCode == "001" && p.ProductCode != db.Products.First(q => q.Id == prod3.SourceProductId!.Value).ProductCode).Id);
        if (other.Ok)
        {
            var p2 = planning.SavePlan("خطة بصنف غريب", "Daily", "2026-09-02", "2026-09-02", 1, 1,
                new List<PlanItemDto>
                {
                    new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = custId, ProductId = other.Id,
                            PlannedQtyKg = 1000, PlannedCartons = 200, PriorityNo = 1 }
                });
            Assert.False(p2.Ok);   // تتبع الصنف ما زال يمنع التحويل غير المعرَّف
        }
    }
}
