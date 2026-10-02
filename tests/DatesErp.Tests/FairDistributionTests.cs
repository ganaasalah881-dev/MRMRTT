using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// اختبارات محرك التوزيع العادل الآلي — تحاكي سيناريو موسم التمور الحقيقي:
/// عميل بأربع حاويات، عميل بحاوية واحدة، عميل بحاويتين — وتتحقق أن الخطة
/// لا تُغرق السوق بعميل واحد ولا تُؤخر صاحب الحاوية الواحدة، مع فترة حرة
/// (أسبوع أو 20 يوماً أو أكثر) وعدد أيام كل شحنة في المخازن.
/// </summary>
public class FairDistributionTests
{
    /// <summary>زرع سيناريو الموسم: عميل 🅰 4 حاويات (الأقدم)، 🅱 حاوية واحدة، 🅲 حاويتان.</summary>
    private static (TestHost host, int custA, int custB, int custC) SeedSeason()
    {
        var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        db.Customers.AddRange(
            new Customer { CustomerCode = "FA", CustomerName = "مصنع التمور الحديث — أربع حاويات", IsActive = true },
            new Customer { CustomerCode = "FB", CustomerName = "شركة الريان — حاوية واحدة", IsActive = true },
            new Customer { CustomerCode = "FC", CustomerName = "مؤسسة البركة — حاويتان", IsActive = true });
        db.SaveChanges();
        int a = db.Customers.Single(c => c.CustomerCode == "FA").Id;
        int b = db.Customers.Single(c => c.CustomerCode == "FB").Id;
        int c = db.Customers.Single(c => c.CustomerCode == "FC").Id;

        // حاويات 🅰 الأربع (وصلت أيام -20، -18، -16، -14) × 20 طن
        int day = -20;
        foreach (var w in new[] { 20000, 20000, 20000, 20000 })
        {
            var s = new Shipment
            {
                DocumentNumber = $"SHP-A{day}", CustomerId = a, ContainerNumber = $"CONT-A{day}",
                ArrivalDate = DateTime.Today.AddDays(day), Status = "Approved", IsApproved = true,
                TotalWeightKg = w
            };
            s.Items.Add(new ShipmentItem { ProductId = 1, PackageCount = 1, UnitWeightKg = w, TotalWeightKg = w });
            db.Shipments.Add(s);
            day += 2;
        }
        // حاوية 🅱 الوحيدة (وصلت يوم -2) — 18 طناً
        var sb = new Shipment
        {
            DocumentNumber = "SHP-B1", CustomerId = b, ContainerNumber = "CONT-B1",
            ArrivalDate = DateTime.Today.AddDays(-2), Status = "Approved", IsApproved = true, TotalWeightKg = 18000
        };
        sb.Items.Add(new ShipmentItem { ProductId = 1, PackageCount = 1, UnitWeightKg = 18000, TotalWeightKg = 18000 });
        db.Shipments.Add(sb);
        // حاويتا 🅲 (-10، -8) × 20 طناً
        foreach (var dd in new[] { -10, -8 })
        {
            var sc = new Shipment
            {
                DocumentNumber = $"SHP-C{dd}", CustomerId = c, ContainerNumber = $"CONT-C{dd}",
                ArrivalDate = DateTime.Today.AddDays(dd), Status = "Approved", IsApproved = true, TotalWeightKg = 20000
            };
            sc.Items.Add(new ShipmentItem { ProductId = 1, PackageCount = 1, UnitWeightKg = 20000, TotalWeightKg = 20000 });
            db.Shipments.Add(sc);
        }
        db.SaveChanges();

        // الدفعات مباشرة (اعتماد الاستلام تم نمذجته مسبقاً): رصيد لكل بند شحنة
        foreach (var ship in db.Shipments.ToList())
        {
            var item = ship.Items.First();
            db.Lots.Add(new Lot
            {
                LotCode = $"LOT-{ship.DocumentNumber}",
                ShipmentId = ship.Id, ShipmentItemId = item.Id, ProductId = item.ProductId,
                CustomerId = ship.CustomerId, LotDate = ship.ArrivalDate,
                InitialQtyKg = item.TotalWeightKg, InStockQtyKg = item.TotalWeightKg,
                Status = "Approved"
            });
        }
        db.SaveChanges();
        return (host, a, b, c);
    }

    [Fact]
    public void Season_FairPlan_FlexiblePeriod_AllCustomersGetEarlyDays_NoFlooding()
    {
        var (host, a, b, c) = SeedSeason();
        using var scope = host.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();

        var start = DateTime.Today.ToString("dd/MM/yyyy");
        var end = DateTime.Today.AddDays(13).ToString("dd/MM/yyyy"); // فترة حرة: 14 يوماً
        var proposal = svc.SuggestFairDistribution(start, end, shiftId: 1, lineId: 1);

        Assert.True(proposal.Ok, proposal.Message);
        Assert.NotEmpty(proposal.Rows);
        Assert.True(proposal.DaysUsed > 0 && proposal.DaysUsed <= 14);
        Assert.Equal(3, proposal.Customers.Count);

        // كل البنود داخل الفترة المحددة (التاريخ بالصيغة الموحدة 28/08/2026)
        Assert.All(proposal.Rows, r =>
        {
            var rd = DateTime.ParseExact(r.Date, DatesErp.Core.Common.UiFormat.DatePattern, null);
            Assert.True(rd >= DateTime.Today && rd <= DateTime.Today.AddDays(13),
                $"بند خارج الفترة: {r.Date}");
            Assert.True(r.PlannedCartons > 0 && r.PlannedQtyKg > 0);
        });

        // ✅ صاحب الحاوية الواحدة 🅱 لا يقف آخر الطابور: له يوم إنتاج ضمن أول 3 أيام
        var bDays = proposal.Customers.Single(x => x.CustomerId == b).ProductionDays;
        Assert.NotEmpty(bDays);
        Assert.Contains(bDays, d => d == DateTime.Today.ToString("MM-dd")
                                 || d == DateTime.Today.AddDays(1).ToString("MM-dd")
                                 || d == DateTime.Today.AddDays(2).ToString("MM-dd"));

        // ✅ لا إغراق: نصيب أي عميل لا يتجاوز رصيده المتاح
        foreach (var cust in proposal.Customers)
            Assert.True(cust.AllocatedKg <= cust.TotalAvailableKg + 0.01,
                $"تجاوز رصيد العميل {cust.CustomerName}");

        // ✅ مجموع المخصص لكل عميل = مجموع بنوده
        foreach (var cust in proposal.Customers)
        {
            double sum = proposal.Rows.Where(r => r.CustomerId == cust.CustomerId).Sum(r => r.PlannedQtyKg);
            Assert.Equal(cust.AllocatedKg, sum, 1);
        }

        // ✅ كل عميل يعرف أيام إنتاجه (في أي يوم ننتج له)
        Assert.All(proposal.Customers, cust => Assert.NotEmpty(cust.ProductionDays));

        // ✅ FIFO: أول إنتاج عميل 🅰 من أقدم حاوياته (وصول -20)
        var firstARow = proposal.Rows.First(r => r.CustomerId == a);
        Assert.Equal(DateTime.Today.AddDays(-20).ToString("dd/MM/yyyy"), firstARow.ArrivalDate);

        // ✅ أيام الشحنة في المخازن ظاهرة وصحيحة
        Assert.Equal(20, firstARow.DaysInStock);
        var bRow = proposal.Rows.First(r => r.CustomerId == b);
        Assert.Equal(2, bRow.DaysInStock);
    }

    [Fact]
    public void Season_FairPlan_20DayPeriod_ConsumesAllBalances()
    {
        var (host, _, _, _) = SeedSeason();
        using var scope = host.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();

        var start = DateTime.Today.ToString("dd/MM/yyyy");
        var end = DateTime.Today.AddDays(19).ToString("dd/MM/yyyy"); // 20 يوماً
        var proposal = svc.SuggestFairDistribution(start, end, 1, 1);

        Assert.True(proposal.Ok, proposal.Message);
        // إجمالي الأرصدة 138 طناً والطاقة اليومية كبيرة — يجب استهلاك كل الرصيد قبل نهاية الفترة
        Assert.True(proposal.TotalRemainingKg <= 0.01,
            $"متبقٍ {proposal.TotalRemainingKg} كجم رغم سعة الفترة");
        Assert.Equal(138000, proposal.Rows.Sum(r => r.PlannedQtyKg), 0);
    }

    [Fact]
    public void FairPlan_DailyQuotaOverride_And_TargetProduct_AreRespected()
    {
        var (host, _, _, _) = SeedSeason();
        using var scope = host.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();

        var start = DateTime.Today.ToString("dd/MM/yyyy");
        var end = DateTime.Today.AddDays(6).ToString("dd/MM/yyyy"); // أسبوع واحد

        // حصة يومية يدوية 10 أطنان ← الإجمالي ≤ 7 × 10000 + هامش عبوة
        var p1 = svc.SuggestFairDistribution(start, end, 1, 1, null, dailyKgOverride: 10000);
        Assert.True(p1.Ok, p1.Message);
        Assert.Equal(10000, p1.DailyQuotaKg, 0);
        foreach (var g in p1.Rows.GroupBy(r => r.Date))
            Assert.True(g.Sum(r => r.PlannedQtyKg) <= 10000 + 0.01,
                $"اليوم {g.Key} تجاوز الحصة اليومية");

        // صنف مستهدف واحد ← كل البنود من نفس الصنف التام
        var p2 = svc.SuggestFairDistribution(start, end, 1, 1, targetProductId: 3, dailyKgOverride: 10000);
        Assert.True(p2.Ok, p2.Message);
        Assert.All(p2.Rows, r => Assert.Equal(3, r.ProductId));
    }

    [Fact]
    public void FairPlan_ShortPeriod_ReportsRemainingWarning()
    {
        var (host, _, _, _) = SeedSeason();
        using var scope = host.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();

        // يوم واحد فقط بحصة صغيرة — سيبقى رصيد كبير والرسالة تحذر
        // §B87: الجمعة عطلة افتراضية — اليوم الوحيد يجب ألا يكون جمعة
        var single = DateTime.Today.DayOfWeek == DayOfWeek.Friday ? DateTime.Today.AddDays(1) : DateTime.Today;
        var start = single.ToString("dd/MM/yyyy");
        var proposal = svc.SuggestFairDistribution(start, start, 1, 1, null, dailyKgOverride: 5000);
        Assert.True(proposal.Ok, proposal.Message);
        Assert.True(proposal.TotalRemainingKg > 0);
        Assert.Contains("وسّع الفترة", proposal.Message);
    }

    [Fact]
    public void FairPlan_NoAvailableStock_FailsWithClearMessage()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var p = svc.SuggestFairDistribution(DateTime.Today.ToString("dd/MM/yyyy"),
            DateTime.Today.AddDays(7).ToString("dd/MM/yyyy"), 1, 1);
        Assert.False(p.Ok);
        Assert.Contains("لا توجد أرصدة", p.Message);
    }
}

/// <summary>اختبار تعديل الصنف/العبوة لبند مستقبلي أثناء الخطة (اشتراطات العملاء المتغيرة).</summary>
public class PlanItemProductChangeTests
{
    [Fact]
    public void UpdatePlanItem_ChangeProduct_RechecksCapacity_ChangePack_RecomputesCartons()
    {
        var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var progress = scope.ServiceProvider.GetRequiredService<IPlanProgressService>();

        // §تتبع الصنف: شحنة من خام سكري (2) لتطابق المنتج المخطط (4 = سكري فاخر)
        var receiving = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var s = receiving.SaveShipment(1, null, null, new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 2, PackagingTypeId = 1, PackageCount = 1, UnitWeightKg = 20000, QtyKg = 20000 } });
        Assert.True(receiving.ApproveShipment(s.Id).Ok);
        int lotId = db.Lots.OrderBy(l => l.Id).Last().Id;

        // منتج سكري ثانٍ (بديل تغيير الصنف — نفس السلالة، معدل 500/س للوردية 2 = 3000 كرتون ÷ 6 ساعات)
        // §B86: طاقة صريحة بدل الاعتماد على الافتراضي 500 القديم (أُزيل في B85/H4 — كان هذا الاختبار سيفشل بمعدل-صفر)
        var master = scope.ServiceProvider.GetRequiredService<DatesErp.Application.Services.MasterDataService>();
        var created = master.SaveProductFull(null, "002-006", "سكري عادي 500جم", "002", "Finished", "كرتون", 5, 1, 0.5,
            new List<(int, int?, int)> { (2, null, 3000) }, sourceProductId: 2);
        Assert.True(created.Ok, created.Message);
        int altProduct = created.Id;

        // بند مستقبلي: صنف 4 (معدل 1083.3/س للوردية 2) × عبوة 5 كجم ← 4000 كرتون = 3.7 ساعة ≤ 6 ✓
        var future = DateTime.Today.AddDays(2).ToString("dd/MM/yyyy");
        var plan = planning.SavePlan("خطة اختبار تغيير الصنف", "Period", future, future, 2, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 4, PackagingTypeId = 1,
                    PlannedQtyKg = 20000, PlannedCartons = 4000, ScheduledDate = future,
                    SuggestedShiftId = 2, SuggestedLineId = 1, PriorityNo = 1 }
        });
        Assert.True(plan.Ok, plan.Message);
        int itemId = db.ProductionPlanItems.Single(i => i.PlanId == plan.Id).Id;

        // 1) تغيير الصنف إلى سكري عادي (معدل 500/س): 4000 كرتون = 8 ساعات > 6 ← يُرفض بالطاقة
        var bad = progress.UpdatePlanItem(itemId, newProductId: altProduct);
        Assert.False(bad.Ok);
        Assert.Contains("الطاقة", bad.Message);
        Assert.Equal(4, db.ProductionPlanItems.AsNoTracking().Single(i => i.Id == itemId).ProductId); // لم يتغير

        // 2) تغيير العبوة إلى 10 كجم: الكراتين تُعاد آلياً = 2000 ← 1.85 ساعة ✓
        var ok = progress.UpdatePlanItem(itemId, newPackagingTypeId: 2);
        Assert.True(ok.Ok, ok.Message);
        var updated = db.ProductionPlanItems.AsNoTracking().Single(i => i.Id == itemId);
        Assert.Equal(2, updated.PackagingTypeId);
        Assert.Equal(2000, updated.PlannedCartons);

        // 3) الآن تغيير الصنف إلى سكري عادي أصبح ممكناً: 2000 كرتون ÷ 500 = 4 ساعات ≤ 6 ✓
        var ok2 = progress.UpdatePlanItem(itemId, newProductId: altProduct);
        Assert.True(ok2.Ok, ok2.Message);
        Assert.Equal(altProduct, db.ProductionPlanItems.AsNoTracking().Single(i => i.Id == itemId).ProductId);
        Assert.Contains("الصنف", ok2.Message);
    }
}
