using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §فحص معمّق لخطط الإنتاج على أربعة محاور: عميل واحد · عدة عملاء · يومية · فترية.
///
/// كل اختبار هنا نشأ عن ثغرة **مُثبتة بالقراءة** في المنطق، لا عن تخمين:
///   1) DeletePlan لا يحرّر حجز الدفعات — كمية مجمّدة لخطة لم تعد موجودة.
///   2) UpdatePlan يفحص scopeMode الوارد لا المحفوظ — تسريب عميل آخر لخطة عميل واحد.
///   3) الخطة الشهرية تُطبع «فترة محددة» لأن "Monthly" تسقط في _.
///   4) فترة مقلوبة (النهاية قبل البداية) تُعطّل حارس التواريخ ضمناً فتمرّ بصمت.
///
/// وبقيتها تثبيت لسلوك قائم صحيح كي لا ينكسر لاحقاً.
/// </summary>
public class PlanningDeepAuditTests
{
    private const int Cust1 = 1;
    private const int RawKhalas = 1;      // 001-001 خام خلاص
    private const int FinKhalas = 3;      // 002-001 خلاص ممتاز 500جم · كرتون 7.5 كجم
    private const int Shift1 = 1, Line1 = 1;

    private static T Svc<T>(TestHost host) => (T)host.Services.CreateScope().ServiceProvider.GetService(typeof(T));
    private static DatesErpDbContext FreshDb(TestHost host)
        => new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);

    /// <summary>عميل إضافي لاختبارات تعدد العملاء.</summary>
    private static int AddCustomer(TestHost host, string code, string name)
    {
        using var db = FreshDb(host);
        db.Customers.Add(new Customer
        {
            CustomerCode = code, CustomerName = name, IsActive = true,
            RowVersion = Guid.NewGuid().ToByteArray()
        });
        db.SaveChanges();
        return db.Customers.Single(c => c.CustomerCode == code).Id;
    }

    /// <summary>شحنة خام معتمدة ⟵ دفعة مملوكة للعميل. تُرجع معرّف الدفعة.</summary>
    private static int ReceiveLot(TestHost host, int customerId, double qtyKg)
    {
        var receiving = Svc<IReceivingService>(host);
        var s = receiving.SaveShipment(customerId, null, null, new List<ShipmentItemDto>
        {
            new() { TreatmentRequired = false, ProductId = RawKhalas, PackagingTypeId = 3,
                    PackageCount = (int)(qtyKg / 20), UnitWeightKg = 20, QtyKg = qtyKg }
        });
        Assert.True(s.Ok, s.Message);
        Assert.True(receiving.ApproveShipment(s.Id).Ok);
        using var db = FreshDb(host);
        return db.Lots.OrderBy(l => l.Id).Last().Id;
    }

    private static PlanItemDto Item(int custId, int lotId, int cartons, string date) => new()
    {
        SourceType = "FromReceiving", LotId = lotId, CustomerId = custId,
        // §بلا PackagingTypeId عمداً: تحديد العبوة يغلب وزن كرتون الصنف
        // (UnitsPolicy.CartonWeight يفضّل UnitWeightKg للعبوة)، فالعبوة 1 = 5 كجم
        // بينما الصنف 3 = 7.5 كجم — وكل حسابات هذا الملف مبنية على 7.5.
        ProductId = FinKhalas,
        PlannedCartons = cartons, PlannedQtyKg = cartons * 7.5,
        ScheduledDate = date, SuggestedShiftId = Shift1, SuggestedLineId = Line1
    };

    private static double Reserved(TestHost host, int lotId)
    {
        using var db = FreshDb(host);
        return db.Lots.First(l => l.Id == lotId).ReservedQtyKg;
    }

    // ═══════════════════ 1) تسرّب الحجز عند حذف الخطة ═══════════════════

    /// <summary>
    /// حذف خطة مسودة يجب أن يحرّر حجز دفعاتها بالكامل.
    /// قبل الإصلاح: الحذف يزيل الخطة وبنودها (Cascade) بلا إعادة احتساب، فتبقى
    /// ReservedQtyKg محجوزة لخطة غير موجودة — كمية مجمّدة لا يحرّرها شيء.
    /// </summary>
    [Fact]
    public void DeletePlan_Releases_Lot_Reservations()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lot = ReceiveLot(host, Cust1, 100000);
        var planning = Svc<IPlanningService>(host);

        var p = planning.SavePlan("خطة للحذف", "Daily", "01/10/2026", "01/10/2026",
            Shift1, Line1, new List<PlanItemDto> { Item(Cust1, lot, 1000, "01/10/2026") });
        Assert.True(p.Ok, p.Message);
        Assert.Equal(7500, Reserved(host, lot), 1);   // 1000 × 7.5

        Assert.True(planning.DeletePlan(p.Id).Ok);
        Assert.Equal(0, Reserved(host, lot), 1);      // تحرّر بالكامل
    }

    /// <summary>حذف إحدى خطتين لا يحرّر إلا حصتها — وحجز الأخرى يبقى سليماً.</summary>
    [Fact]
    public void DeletePlan_Keeps_Other_Plans_Reservations()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lot = ReceiveLot(host, Cust1, 100000);
        var planning = Svc<IPlanningService>(host);

        var p1 = planning.SavePlan("أولى", "Daily", "01/10/2026", "01/10/2026",
            Shift1, Line1, new List<PlanItemDto> { Item(Cust1, lot, 1000, "01/10/2026") });
        var p2 = planning.SavePlan("ثانية", "Daily", "02/10/2026", "02/10/2026",
            Shift1, Line1, new List<PlanItemDto> { Item(Cust1, lot, 800, "02/10/2026") });
        Assert.True(p1.Ok && p2.Ok);
        Assert.Equal(7500 + 6000, Reserved(host, lot), 1);

        Assert.True(planning.DeletePlan(p1.Id).Ok);
        Assert.Equal(6000, Reserved(host, lot), 1);   // حصة الثانية وحدها
    }

    // ═══════════════════ 2) نطاق العميل الواحد ═══════════════════

    /// <summary>خطة عميل واحد ترفض بنداً لعميل آخر عند الإنشاء.</summary>
    [Fact]
    public void SinglePlan_Rejects_Foreign_Customer_On_Create()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int c2 = AddCustomer(host, "C002", "مصنع النخيل");
        int lot1 = ReceiveLot(host, Cust1, 50000);
        var planning = Svc<IPlanningService>(host);

        var r = planning.SavePlan("عميل واحد", "Daily", "01/10/2026", "01/10/2026",
            Shift1, Line1,
            new List<PlanItemDto> { Item(c2, lot1, 100, "01/10/2026") },
            scopeMode: "Single", singleCustomerId: Cust1);

        Assert.False(r.Ok);
        Assert.Contains("عميل آخر", r.Message);
    }

    /// <summary>
    /// الثغرة المُصلحة: تعديل خطة «عميل واحد» بتمرير scopeMode = null.
    /// الرأس يبقى Single (لأن plan.ScopeMode = scopeMode ?? plan.ScopeMode)، لكن الحارس
    /// كان يفحص المتغير الوارد فيقرؤه "Multi" ويسمح بالمرور — تسريب صامت.
    /// </summary>
    [Fact]
    public void UpdatePlan_Enforces_Single_Scope_Even_When_ScopeMode_Not_Passed()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int c2 = AddCustomer(host, "C002", "مصنع النخيل");
        int lot1 = ReceiveLot(host, Cust1, 50000);
        var planning = Svc<IPlanningService>(host);

        var p = planning.SavePlan("خطة عميل واحد", "Daily", "01/10/2026", "01/10/2026",
            Shift1, Line1, new List<PlanItemDto> { Item(Cust1, lot1, 100, "01/10/2026") },
            scopeMode: "Single", singleCustomerId: Cust1);
        Assert.True(p.Ok, p.Message);

        // تمرير scopeMode = null و singleCustomerId = null: الرأس يبقى Single
        var upd = planning.UpdatePlan(p.Id, "خطة عميل واحد", "Daily", "01/10/2026", "01/10/2026",
            Shift1, Line1, new List<PlanItemDto> { Item(c2, lot1, 100, "01/10/2026") });

        Assert.False(upd.Ok);
        Assert.Contains("عميل آخر", upd.Message);
    }

    /// <summary>ملكية الدفعة تُفرض دائماً: دفعة عميل لا تُخطط باسم غيره ولو في خطة متعددة.</summary>
    [Fact]
    public void MultiPlan_Still_Rejects_Planning_Lot_Under_Wrong_Owner()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int c2 = AddCustomer(host, "C002", "مصنع النخيل");
        int lot1 = ReceiveLot(host, Cust1, 50000);
        var planning = Svc<IPlanningService>(host);

        var r = planning.SavePlan("متعددة", "Daily", "01/10/2026", "01/10/2026",
            Shift1, Line1,
            new List<PlanItemDto> { Item(c2, lot1, 100, "01/10/2026") }); // دفعة عميل1 باسم عميل2

        Assert.False(r.Ok);
        Assert.Contains("مملوكة لعميل آخر", r.Message);
    }

    // ═══════════════════ 3) عدة عملاء في خطة واحدة ═══════════════════

    /// <summary>
    /// خطة واحدة لعميلين: كل بند يحتفظ بمرجعه ويحجز من دفعته هو — لا دمج ولا خلط.
    /// </summary>
    [Fact]
    public void MultiCustomer_Plan_Reserves_Each_Lot_Separately()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int c2 = AddCustomer(host, "C002", "مصنع النخيل");
        int lot1 = ReceiveLot(host, Cust1, 60000);
        int lot2 = ReceiveLot(host, c2, 40000);
        var planning = Svc<IPlanningService>(host);

        var r = planning.SavePlan("خطة مشتركة", "Daily", "01/10/2026", "01/10/2026",
            Shift1, Line1, new List<PlanItemDto>
            {
                Item(Cust1, lot1, 1000, "01/10/2026"),
                Item(c2, lot2, 500, "01/10/2026")
            });
        Assert.True(r.Ok, r.Message);

        Assert.Equal(7500, Reserved(host, lot1), 1);
        Assert.Equal(3750, Reserved(host, lot2), 1);

        using var db = FreshDb(host);
        var items = db.ProductionPlanItems.Where(i => i.PlanId == r.Id).ToList();
        Assert.Equal(2, items.Count);
        // المرجع الكامل محفوظ لكل بند: العميل والدفعة والشحنة.
        Assert.All(items, i => Assert.NotNull(i.CustomerId));
        Assert.All(items, i => Assert.NotNull(i.LotId));
        Assert.All(items, i => Assert.NotNull(i.ShipmentId));
        Assert.Equal(2, items.Select(i => i.CustomerId).Distinct().Count());
    }

    // ═══════════════════ 4) اليومية مقابل الفترية ═══════════════════

    [Fact]
    public void PeriodEndDate_Matches_Plan_Type()
    {
        var s = new DateTime(2026, 10, 1);
        Assert.Equal(s, DatesErp.Application.Services.PlanningService.PeriodEndDate("Daily", s));
        Assert.Equal(s.AddDays(6), DatesErp.Application.Services.PlanningService.PeriodEndDate("Weekly", s));
        Assert.Equal(new DateTime(2026, 10, 31), DatesErp.Application.Services.PlanningService.PeriodEndDate("Monthly", s));
        Assert.Equal(s, DatesErp.Application.Services.PlanningService.PeriodEndDate("Period", s));
    }

    /// <summary>الخطة اليومية: بند بتاريخ غير يوم الخطة مرفوض.</summary>
    [Fact]
    public void DailyPlan_Rejects_Item_Outside_Its_Day()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lot = ReceiveLot(host, Cust1, 50000);
        var planning = Svc<IPlanningService>(host);

        var r = planning.SavePlan("يومية", "Daily", "01/10/2026", "01/10/2026",
            Shift1, Line1, new List<PlanItemDto> { Item(Cust1, lot, 100, "02/10/2026") });

        Assert.False(r.Ok);
        Assert.Contains("خارج فترة الخطة", r.Message);
    }

    /// <summary>الخطة الفترية تقبل بنوداً موزعة على أيامها، وتحجز مجموعها.</summary>
    [Fact]
    public void PeriodPlan_Accepts_Items_Across_Its_Days()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lot = ReceiveLot(host, Cust1, 100000);
        var planning = Svc<IPlanningService>(host);

        var r = planning.SavePlan("أسبوعية", "Weekly", "01/10/2026", "07/10/2026",
            Shift1, Line1, new List<PlanItemDto>
            {
                Item(Cust1, lot, 1000, "01/10/2026"),
                Item(Cust1, lot, 1000, "03/10/2026"),
                Item(Cust1, lot, 1000, "06/10/2026")
            });
        Assert.True(r.Ok, r.Message);
        Assert.Equal(3 * 7500, Reserved(host, lot), 1);
    }

    /// <summary>
    /// الطاقة تُفحص **لكل يوم على حدة** لا على الفترة مجملةً: ثلاثة آلاف كرتون
    /// موزعة على ثلاثة أيام تمرّ، بينما تكديسها في يوم واحد يتجاوز سعة الوردية.
    /// هذا جوهر الفرق بين اليومية والفترية.
    /// </summary>
    [Fact]
    public void PeriodPlan_Capacity_Is_Per_Day_Not_Per_Period()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lot = ReceiveLot(host, Cust1, 100000);
        var planning = Svc<IPlanningService>(host);

        // سعة الصنف 3 في الوردية 1 = 4,000 كرتون يومياً.
        var spread = planning.SavePlan("موزعة", "Weekly", "01/10/2026", "07/10/2026",
            Shift1, Line1, new List<PlanItemDto>
            {
                Item(Cust1, lot, 3000, "01/10/2026"),
                Item(Cust1, lot, 3000, "02/10/2026"),
                Item(Cust1, lot, 3000, "03/10/2026")
            });
        Assert.True(spread.Ok, spread.Message);
        Assert.True(planning.ApprovePlan(spread.Id).Ok);

        // المكدَّسة في يوم واحد: 9,000 كرتون > 4,000 ⟵ ترفض.
        var stacked = planning.SavePlan("مكدسة", "Weekly", "10/10/2026", "16/10/2026",
            Shift1, Line1, new List<PlanItemDto>
            {
                Item(Cust1, lot, 3000, "10/10/2026"),
                Item(Cust1, lot, 3000, "10/10/2026"),
                Item(Cust1, lot, 3000, "10/10/2026")
            });
        if (stacked.Ok)
        {
            var appr = planning.ApprovePlan(stacked.Id);
            Assert.False(appr.Ok, "تكديس 9,000 كرتون في يوم واحد كان يجب أن يُرفض");
            Assert.Contains("الطاقة", appr.Message);
        }
    }

    /// <summary>فترة مقلوبة (النهاية قبل البداية) — كانت تُعطّل حارس التواريخ فتمرّ بصمت.</summary>
    [Fact]
    public void Plan_Rejects_Inverted_Period()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lot = ReceiveLot(host, Cust1, 50000);
        var planning = Svc<IPlanningService>(host);

        var r = planning.SavePlan("مقلوبة", "Period", "10/10/2026", "01/10/2026",
            Shift1, Line1, new List<PlanItemDto> { Item(Cust1, lot, 100, "05/10/2026") });

        Assert.False(r.Ok);
        Assert.Contains("غير صحيحة", r.Message);
    }

    // ═══════════════════ 5) دورة الحجز الكاملة ═══════════════════

    /// <summary>
    /// المحجوز لا يتجاوز المخزون في أي لحظة من دورة الخطة، وإقفالها يحرّره.
    /// هذا ما يقيسه فحص الاتساق في الفحص الذاتي — نثبته هنا على مسار حقيقي.
    /// </summary>
    [Fact]
    public void Reservation_Never_Exceeds_Stock_Through_Plan_Lifecycle()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lot = ReceiveLot(host, Cust1, 100000);
        var planning = Svc<IPlanningService>(host);

        void AssertConsistent(string stage)
        {
            using var db = FreshDb(host);
            var l = db.Lots.First(x => x.Id == lot);
            Assert.True(l.ReservedQtyKg >= -0.001, $"{stage}: حجز سالب");
            Assert.True(l.ReservedQtyKg + l.UnderTreatmentQtyKg <= l.InStockQtyKg + 0.001,
                $"{stage}: الالتزام {l.ReservedQtyKg + l.UnderTreatmentQtyKg:N1} يتجاوز المخزون {l.InStockQtyKg:N1}");
        }

        var p = planning.SavePlan("دورة كاملة", "Daily", "01/10/2026", "01/10/2026",
            Shift1, Line1, new List<PlanItemDto> { Item(Cust1, lot, 2000, "01/10/2026") });
        Assert.True(p.Ok, p.Message);
        AssertConsistent("بعد الحفظ");

        Assert.True(planning.ApprovePlan(p.Id).Ok);
        AssertConsistent("بعد الاعتماد");

        Assert.True(planning.UnapprovePlan(p.Id).Ok);
        AssertConsistent("بعد إلغاء الاعتماد");

        Assert.False(planning.ClosePlan(p.Id, "إقفال اختبار").Ok); // No silent closing of an unapproved/unissued plan.
        Assert.True(planning.ApprovePlan(p.Id).Ok);
        Assert.True(Svc<IPlanClosureService>(host).ClosePlanFinal(p.Id, "عدول موثق عن برنامج الإنتاج — اختبار تحرير الحجز", force: true).Ok);
        AssertConsistent("بعد الإقفال");
        Assert.Equal(0, Reserved(host, lot), 1);   // الإقفال يحرّر ما لم يُنتج
    }

    /// <summary>تعديل الكمية يعيد احتساب الحجز — لا يتراكم القديم مع الجديد.</summary>
    [Fact]
    public void UpdatePlan_Recomputes_Reservation_Without_Double_Counting()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lot = ReceiveLot(host, Cust1, 100000);
        var planning = Svc<IPlanningService>(host);

        var p = planning.SavePlan("للتعديل", "Daily", "01/10/2026", "01/10/2026",
            Shift1, Line1, new List<PlanItemDto> { Item(Cust1, lot, 1000, "01/10/2026") });
        Assert.True(p.Ok, p.Message);
        Assert.Equal(7500, Reserved(host, lot), 1);

        var u = planning.UpdatePlan(p.Id, "للتعديل", "Daily", "01/10/2026", "01/10/2026",
            Shift1, Line1, new List<PlanItemDto> { Item(Cust1, lot, 400, "01/10/2026") });
        Assert.True(u.Ok, u.Message);
        Assert.Equal(3000, Reserved(host, lot), 1);   // 400 × 7.5 — لا 7500 + 3000
    }

    // ═══════════════════ 6) مسار التعديل الثاني: UpdatePlanItem ═══════════════════

    /// <summary>
    /// تعديل كمية بند مفرد يجب أن يعيد احتساب حجز الدفعة.
    /// قبل الإصلاح: PlanProgressService.UpdatePlanItem يحفظ PlannedQtyKg الجديدة بلا
    /// تحديث ReservedQtyKg — تخفيض 1000⟵400 كرتون يترك 4,500 كجم محجوزة بلا سند.
    /// (المسار الآخر PlanningService.UpdatePlan يستدعي ApplyLotReservations.)
    /// </summary>
    [Fact]
    public void UpdatePlanItem_Recomputes_Lot_Reservation_On_Quantity_Change()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lot = ReceiveLot(host, Cust1, 100000);
        var planning = Svc<IPlanningService>(host);

        var p = planning.SavePlan("تعديل بند", "Daily", "01/10/2026", "01/10/2026",
            Shift1, Line1, new List<PlanItemDto> { Item(Cust1, lot, 1000, "01/10/2026") });
        Assert.True(p.Ok, p.Message);
        Assert.Equal(7500, Reserved(host, lot), 1);

        int itemId;
        using (var db = FreshDb(host))
            itemId = db.ProductionPlanItems.First(i => i.PlanId == p.Id).Id;

        // 400 كرتون × 7.5 = 3,000 كجم
        var upd = Svc<IPlanProgressService>(host).UpdatePlanItem(itemId, newQtyKg: 3000);
        Assert.True(upd.Ok, upd.Message);

        Assert.Equal(3000, Reserved(host, lot), 1);   // لا 7,500 المتجمدة
    }

    /// <summary>
    /// رفض تغيير العميل لا يجوز أن يترك تعديلات جزئية على البند.
    /// قبل الإصلاح: التاريخ والكمية تُسنَد ثم يُفحص العميل ويُرفض، فتبقى التعديلات
    /// في ChangeTracker ويكتبها أول SaveChanges لاحق — رفض ظاهر وتعديل فعلي.
    /// </summary>
    [Fact]
    public void UpdatePlanItem_Rejected_Customer_Change_Leaves_No_Partial_Edit()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int c2 = AddCustomer(host, "C002", "مصنع النخيل");
        int lot = ReceiveLot(host, Cust1, 100000);
        var planning = Svc<IPlanningService>(host);

        var p = planning.SavePlan("ثبات", "Weekly", "01/10/2026", "07/10/2026",
            Shift1, Line1, new List<PlanItemDto> { Item(Cust1, lot, 1000, "01/10/2026") });
        Assert.True(p.Ok, p.Message);

        int itemId;
        using (var db = FreshDb(host))
            itemId = db.ProductionPlanItems.First(i => i.PlanId == p.Id).Id;

        // تاريخ جديد صالح + عميل لا يملك الدفعة ⟵ يجب أن يُرفض الطلب كله
        var upd = Svc<IPlanProgressService>(host)
            .UpdatePlanItem(itemId, newDate: "03/10/2026", newCustomerId: c2);
        Assert.False(upd.Ok);
        Assert.Contains("مملوكة لعميل آخر", upd.Message);

        using (var db = FreshDb(host))
        {
            var it = db.ProductionPlanItems.First(i => i.Id == itemId);
            Assert.Equal(Cust1, it.CustomerId);                          // العميل لم يتغير
            Assert.Equal(new DateTime(2026, 10, 1), it.ScheduledDate);   // ولا التاريخ
        }
    }
}
