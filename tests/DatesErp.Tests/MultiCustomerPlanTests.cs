using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// تصميم شاشة خطة الإنتاج الجديد:
/// خطة واحدة لعدة عملاء، كل سطر بمرجعه الكامل (عميل←شحنة←دفعة←صنف←عبوة)،
/// لا دمج للملكيات، وأمر الإنتاج ينقل كل سطر كما هو.
/// </summary>
public class MultiCustomerPlanTests
{
    private static T Svc<T>(TestHost host) => (T)host.Services.CreateScope().ServiceProvider.GetService(typeof(T));
    private static DatesErpDbContext FreshDb(TestHost host)
        => new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);

    [Fact]
    public void One_Plan_Multiple_Customers_Rows_Keep_Full_References()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();

        // عميل ثانٍ
        using (var db = FreshDb(host))
        {
            db.Customers.Add(new Core.Domain.Entities.Customer { CustomerCode = "C002", CustomerName = "مصنع النخيل الحديث", IsActive = true, RowVersion = Guid.NewGuid().ToByteArray() });
            db.SaveChanges();
        }
        int cust2;
        using (var db = FreshDb(host)) cust2 = db.Customers.Single(c => c.CustomerCode == "C002").Id;

        // شحنة لكل عميل واعتمادهما (توليد دفعتين بملكية منفصلة)
        var receiving = Svc<IReceivingService>(host);
        var s1 = receiving.SaveShipment(1, null, null, new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 300, UnitWeightKg = 20, QtyKg = 6000 } });
        Assert.True(receiving.ApproveShipment(s1.Id).Ok);
        var s2 = receiving.SaveShipment(cust2, null, null, new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 200, UnitWeightKg = 20, QtyKg = 4000 } });
        Assert.True(receiving.ApproveShipment(s2.Id).Ok);

        int lot1, lot2;
        using (var db = FreshDb(host))
        {
            lot1 = db.Lots.OrderBy(l => l.Id).First().Id;
            lot2 = db.Lots.OrderBy(l => l.Id).Last().Id;
        }

        // §تتبع الصنف: البند الثاني لعميل 1 يجب أن يكون منتجاً مصنوعاً من نفس سلالة الدفعة (خلاص)
        int khalasStd;
        using (var scope = host.Services.CreateScope())
        {
            var master = scope.ServiceProvider.GetRequiredService<DatesErp.Application.Services.MasterDataService>();
            var created = master.SaveProductFull(null, "002-003", "خلاص عادي 250جم", "002", "Finished", "كرتون", 7.5, 1, 0.5,
                new List<(int, int?, int)>(), sourceProductId: 1);
            Assert.True(created.Ok, created.Message);
            khalasStd = created.Id;
        }

        // خطة واحدة بعميلين و3 بنود (عميل 1: بندان من نفس الدفعة بصنفين مختلفين — عميل 2: بند)
        var planning = Svc<IPlanningService>(host);
        TestCapacityFixture.DefineMissingRates(host);
        var plan = planning.SavePlan("خطة عميلين", "Period", "2026-09-01", "2026-09-03", 1, 1, new List<PlanItemDto>
        {
            // §نظام الوحدات: الكيلو = الكراتين × وزن الكرتون (5 كجم للعبوة 1 | 10 كجم للعبوة 2)
            new() { SourceType = "FromReceiving", LotId = lot1, ShipmentId = s1.Id, CustomerId = 1, ProductId = 3, PackagingTypeId = 1, PlannedQtyKg = 2000, PlannedCartons = 400, ScheduledDate = "2026-09-01", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 },
            new() { SourceType = "FromReceiving", LotId = lot1, ShipmentId = s1.Id, CustomerId = 1, ProductId = khalasStd, PackagingTypeId = 2, PlannedQtyKg = 1500, PlannedCartons = 150, ScheduledDate = "2026-09-02", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 2 },
            new() { SourceType = "FromReceiving", LotId = lot2, ShipmentId = s2.Id, CustomerId = cust2, ProductId = 3, PackagingTypeId = 1, PlannedQtyKg = 1000, PlannedCartons = 200, ScheduledDate = "2026-09-01", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 3 }
        });
        Assert.True(plan.Ok, plan.Message);
        Assert.True(planning.ApprovePlan(plan.Id).Ok);

        // التحقق: كل سطر احتفظ بمرجعه الكامل بلا دمج
        using (var db = FreshDb(host))
        {
            var items = db.ProductionPlanItems.Where(i => i.PlanId == plan.Id).OrderBy(i => i.PriorityNo).ToList();
            Assert.Equal(3, items.Count);
            Assert.Equal(1, items[0].CustomerId); Assert.Equal(lot1, items[0].LotId); Assert.Equal(s1.Id, items[0].ShipmentId);
            Assert.Equal(1, items[1].CustomerId); Assert.Equal(lot1, items[1].LotId);
            Assert.Equal(cust2, items[2].CustomerId); Assert.Equal(lot2, items[2].LotId); Assert.Equal(s2.Id, items[2].ShipmentId);

            // الحجوزات منفصلة لكل دفعة
            Assert.Equal(3500, db.Lots.Single(l => l.Id == lot1).ReservedQtyKg, 1);
            Assert.Equal(1000, db.Lots.Single(l => l.Id == lot2).ReservedQtyKg, 1);
        }

        // منع تخطيط دفعة عميل لعميل آخر (لا دمج ملكيات)
        TestCapacityFixture.DefineMissingRates(host);
        var bad = planning.SavePlan("خطة خاطئة", "Daily", "2026-09-05", "2026-09-05", 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lot1, CustomerId = cust2, ProductId = 3, PlannedQtyKg = 500, ScheduledDate = "2026-09-05", PriorityNo = 1 }
        });
        Assert.False(bad.Ok);
        Assert.Contains("عميل آخر", bad.Message);

        // §قاعدة توازن الإنتاج: المخطط مستهدف إنتاجي لا حجز خام، ولا معادلة ثابتة تربطه
        // برصيد الخام لأن وزن الخارج يزيد عن الداخل لإضافة الماء. لكن الطاقة ما زالت قيداً.
        TestCapacityFixture.DefineMissingRates(host);
        var over = planning.SavePlan("خطة زائدة", "Daily", "2026-09-05", "2026-09-05", 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lot1, CustomerId = 1, ProductId = 3, PlannedQtyKg = 99999, ScheduledDate = "2026-09-05", PriorityNo = 1 }
        });
        Assert.False(over.Ok);   // تُرفض لتجاوز الطاقة الإنتاجية للوردية لا لرصيد الخام
        Assert.Contains("الطاقة", over.Message);

        // أمر إنتاج من الخطة لعميل واحد — ينقل كل سطر كما هو (عميل/شحنة/دفعة)
        var orders = Svc<IProductionOrderService>(host);
        host.SetBusinessDate("2026-09-01"); // Arrange production on its scheduled business day, not the machine date.
        var o = orders.SaveOrder("FromPlan", plan.Id, 1, "2026-09-01", 1, 1, TestOrderFixture.Items(host, plan.Id, 1, "2026-09-01"));
        Assert.True(o.Ok, o.Message);
        // The second item is scheduled on the next day, never pulled into today's order.
        Assert.Single(host.Get<DatesErpDbContext>().ProductionOrderItems.Where(i => i.OrderId == o.Id));
        host.SetBusinessDate("2026-09-02");
        var later = orders.SaveOrder("FromPlan", plan.Id, 1, "2026-09-02", 1, 1, TestOrderFixture.Items(host, plan.Id, 1, "2026-09-02"));
        Assert.True(later.Ok, later.Message);
        using (var db = FreshDb(host))
        {
            var oItems = db.ProductionOrderItems.Where(i => i.OrderId == o.Id || i.OrderId == later.Id).ToList();
            Assert.Equal(2, oItems.Count);
            Assert.All(oItems, i =>
            {
                Assert.Equal(1, i.CustomerId);        // الملكية محفوظة لكل سطر
                Assert.Equal(s1.Id, i.ShipmentId);    // الشحنة محفوظة
                Assert.Equal(lot1, i.LotId);          // الدفعة محفوظة
            });
        }
    }

    [Fact]
    public void Capacity_Warning_And_Rejection_On_Excess()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();

        // استلام واعتماد لتوفير دفعة
        var receiving = Svc<IReceivingService>(host);
        var s = receiving.SaveShipment(1, null, null, new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 3000, UnitWeightKg = 20, QtyKg = 60000 } });
        Assert.True(receiving.ApproveShipment(s.Id).Ok);
        int lotId;
        using (var db = FreshDb(host)) lotId = db.Lots.Single().Id;

        var planning = Svc<IPlanningService>(host);
        // الصنف 3 في الوردية 1: 500 كرتون/س × 8 س = 4000 كحد أقصى
        var info = planning.GetShiftCapacityInfo(1, 1, "2026-09-10", 3);
        Assert.Equal(8.0, info.TotalHours, 1);
        Assert.Equal(4000, info.MaxCartons);

        // §نظام الوحدات: 2,500 كرتون × 7.5 كجم (وزن كرتون الصنف 3) = 18,750 كجم
        TestCapacityFixture.DefineMissingRates(host);
        var ok = planning.SavePlan("ضمن الطاقة", "Daily", "2026-09-10", "2026-09-10", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3, PlannedQtyKg = 18750, PlannedCartons = 2500, ScheduledDate = "2026-09-10", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 } });
        Assert.True(ok.Ok, ok.Message);

        // بعد حجز 2500 كرتون: المتبقي = 1500 كرتون ≈ 3 ساعات
        var info2 = planning.GetShiftCapacityInfo(1, 1, "2026-09-10", 3);
        Assert.Equal(5.0, info2.UsedHours, 1);
        Assert.Equal(3.0, info2.RemainingHours, 1);

        // محاولة تتجاوز المتبقي → رفض مع رسالة تفصيلية
        TestCapacityFixture.DefineMissingRates(host);
        var excess = planning.SavePlan("تجاوز", "Daily", "2026-09-10", "2026-09-10", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3, PlannedQtyKg = 15000, PlannedCartons = 2000, ScheduledDate = "2026-09-10", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 } });
        Assert.False(excess.Ok);
        Assert.Contains("الطاقة الإنتاجية", excess.Message);
        Assert.Contains("الزيادة", excess.Message);
    }
}
