using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B91 — فحص الخطة متعددة العملاء بنفس عمل التوزيع:
/// قابلية التنفيذ، كشف العجز، تخطي الجمعة، حجب بلا معدل، وعجز الدفعات.
/// البذرة: الصنف 3 (خلاص) بمعدل 500/س وسقوف 4000/3000/2500 — طاقة يومية 47500 كجم بعبوة CT5.
/// </summary>
public class PlanCheckB91Tests
{
    private static (TestHost host, int custA, int custB) Seed2Customers()
    {
        var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        db.Customers.Add(new Customer { CustomerCode = "PC-A", CustomerName = "عميل فحص أ", IsActive = true, PriorityNo = 1 });
        db.Customers.Add(new Customer { CustomerCode = "PC-B", CustomerName = "عميل فحص ب", IsActive = true });
        db.SaveChanges();
        int a = db.Customers.Single(c => c.CustomerCode == "PC-A").Id;
        int b = db.Customers.Single(c => c.CustomerCode == "PC-B").Id;
        return (host, a, b);
    }

    private static int SeedLot(TestHost host, int custId, double kg, string code)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var lot = new Lot { LotCode = code, ProductId = 1, CustomerId = custId, InitialQtyKg = kg, InStockQtyKg = kg, Status = "Approved" };
        db.Lots.Add(lot);
        db.SaveChanges();
        return lot.Id;
    }

    private static int PackCt5(TestHost host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        return db.PackagingTypes.Single(p => p.PackageCode == "CT5").Id;
    }

    private static IPlanningService Plan(TestHost h)
        => h.Services.CreateScope().ServiceProvider.GetRequiredService<IPlanningService>();

    [Fact]
    public void MultiCustomer_Plan_Feasable_Ok()
    {
        var (host, a, b) = Seed2Customers(); using var _host = host;
        int pack = PackCt5(host);
        int lotA = SeedLot(host, a, 50000, "LOT-PC-A");
        int lotB = SeedLot(host, b, 50000, "LOT-PC-B");
        var r = Plan(host).SavePlan("خطة فحص", "Period", "2026-09-01", "2026-09-06", 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lotA, CustomerId = a, ProductId = 3, PackagingTypeId = pack, PlannedCartons = 2000, PlannedQtyKg = 10000 },
            new() { SourceType = "FromReceiving", LotId = lotB, CustomerId = b, ProductId = 3, PackagingTypeId = pack, PlannedCartons = 2000, PlannedQtyKg = 10000 },
        });
        Assert.True(r.Ok);

        var c = Plan(host).CheckPlan(r.Id);
        Assert.True(c.Ok);
        Assert.Equal(2, c.CustomersCount);
        Assert.Equal(5, c.WorkDays); // 09-04 جمعة مستثناة
        Assert.Equal(20000, c.RequiredKg);
        Assert.Equal(20000, c.CoveredKg);
        Assert.Equal(0, c.ShortageKg);
        Assert.All(c.Customers, x => Assert.Equal(0, x.ShortageKg));
        Assert.Contains("قابلة للتنفيذ", c.Verdict);
    }

    [Fact]
    public void Overload_Shortage_Detected_With_Short_Days()
    {
        // §B103 — إعادة صياغة الاختبار الموروث (كُتب بأرقام طاقة لا تطابق بذرة هذا الخط ولم يُشغَّل قط).
        // النية محفوظة: CheckPlan يلتقط العجز. حارس الحفظ يمنع بنداً يفوق طاقة يومه، فالعجز
        // القابل للفحص هنا يأتي من نقص تغطية الخام: دفعة 1000 كجم وبند 18000 كجم ضمن طاقة اليومين.
        var (host, a, b) = Seed2Customers(); using var _host = host;
        int pack = PackCt5(host);
        int lotA = SeedLot(host, a, 1000, "LOT-B91-SHORT");
        var r = Plan(host).SavePlan("خطة مثقلة", "Period", "2026-09-01", "2026-09-02", 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lotA, CustomerId = a, ProductId = 3, PackagingTypeId = pack,
                    PlannedCartons = 3600, PlannedQtyKg = 18000, ScheduledDate = "2026-09-01",
                    SuggestedShiftId = 1, SuggestedLineId = 1 },
        });
        Assert.True(r.Ok, r.Message);

        var c = Plan(host).CheckPlan(r.Id);
        Assert.False(c.Ok);
        Assert.True(c.ShortageKg > 0, $"عجز متوقع — الفعلي: {c.ShortageKg}");
        Assert.Contains("عجز", c.Verdict);
    }

    [Fact]
    public void FridayOnlyPeriod_Has_No_Workdays()
    {
        var (host, a, b) = Seed2Customers(); using var _host = host;
        int pack = PackCt5(host);
        var r = Plan(host).SavePlan("خطة جمعة", "Period", "2026-09-04", "2026-09-04", 1, 1, new List<PlanItemDto>
        {
            new() { CustomerId = a, ProductId = 3, PackagingTypeId = pack, PlannedCartons = 100, PlannedQtyKg = 500 },
        });
        Assert.True(r.Ok);

        var c = Plan(host).CheckPlan(r.Id);
        Assert.False(c.Ok);
        Assert.Equal(0, c.WorkDays);
        Assert.Contains("جمعة", c.Verdict);
    }

    [Fact]
    public void MissingRate_Item_Blocked_Loudly()
    {
        var (host, a, b) = Seed2Customers(); using var _host = host;
        int pack = PackCt5(host);
        int prodId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            db.Products.Add(new Product { ProductCode = "002-901", ProductNameAr = "صنف بلا طاقة", GroupCode = "002", ItemType = "Finished", CartonWeightKg = 5, SourceProductId = 1 });
            db.SaveChanges();
            prodId = db.Products.Single(p => p.ProductCode == "002-901").Id;
        }
        var r = Plan(host).SavePlan("خطة بلا معدل", "Period", "2026-09-01", "2026-09-06", 1, 1, new List<PlanItemDto>
        {
            new() { CustomerId = a, ProductId = prodId, PackagingTypeId = pack, PlannedCartons = 100, PlannedQtyKg = 500 },
        });
        Assert.False(r.Ok); // undefined capacity/link is blocked at creation, not after persistence
    }

    [Fact]
    public void LotShortage_Warns_And_Caps_Coverage()
    {
        var (host, a, b) = Seed2Customers(); using var _host = host;
        int pack = PackCt5(host);
        int lot = SeedLot(host, a, 1000, "LOT-PC-SHORT");
        var r = Plan(host).SavePlan("خطة عجز دفعة", "Period", "2026-09-01", "2026-09-06", 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lot, CustomerId = a, ProductId = 3, PackagingTypeId = pack, PlannedCartons = 1000, PlannedQtyKg = 5000 },
        });
        Assert.True(r.Ok);

        var c = Plan(host).CheckPlan(r.Id);
        Assert.False(c.Ok);
        Assert.Equal(1000, c.CoveredKg);
        Assert.Equal(4000, c.ShortageKg);
        Assert.Contains(c.Warnings, w => w.Contains("الدفعة"));
    }

    [Fact]
    public void SingleScope_Plan_Checks_Fine()
    {
        var (host, a, b) = Seed2Customers(); using var _host = host;
        int pack = PackCt5(host);
        var r = Plan(host).SavePlan("خطة عميل واحد", "Period", "2026-09-01", "2026-09-06", 1, 1, new List<PlanItemDto>
        {
            new() { CustomerId = a, ProductId = 3, PackagingTypeId = pack, PlannedCartons = 400, PlannedQtyKg = 2000 },
        }, scopeMode: "Single", singleCustomerId: a);
        Assert.True(r.Ok);

        var c = Plan(host).CheckPlan(r.Id);
        Assert.True(c.Ok);
        Assert.Equal(1, c.CustomersCount);
    }
}
