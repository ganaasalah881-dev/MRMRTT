using DatesErp.Application.Services;
using DatesErp.Infrastructure.Persistence;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B101 — تقرير «رحلة شحنة العميل»: من أول دخولها (خطة) ← متى دخلت الإنتاج ← بكم أمر
/// ← مخرجات كل أمر (تشغيل/جودة/مخزن) ← التسليمات حتى اكتمال التسليم.
/// السلسلة المُجرَّبة: خطة لعميلَين (500 + 300) ← أمر واحد (بندان) ← إقفال 800 ← فحص معتمد
/// ← أمر تسليم ← استلام تام ← تسليمات عملاء.
/// </summary>
public class B101ShipmentJourneyTests
{
    /// <summary>الخطة + الأمر + التشغيل + الفحص المعتمد + أمر التسليم + الاستلام التام (بلا تسليمات عملاء بعد).</summary>
    private static (int planId, int orderId, int lotA, int lotB, int custB) SeedToReceipt(TestHost host)
    {
        var (planId, orderId, lotA, lotB, custB, qcId) = B100AvailabilityTests.SeedMultiCustomerClosed(host);
        B100AvailabilityTests.Qc_Delivery_Receive(host, orderId, qcId, lotA, lotB, custB, 500, 100, 300, 60, out _);
        return (planId, orderId, lotA, lotB, custB);
    }

    // ── 1) التسليم تام: اللحظات والإجماليات والحالة النهائية ──
    [Fact]
    public void Journey_Full_Delivery_Shows_Milestones_And_Complete_Status()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, orderId, lotA, lotB, custB) = SeedToReceipt(host);
        B100AvailabilityTests.Deliver(host, 1, orderId, lotA, 500, 100);   // كامل
        B100AvailabilityTests.Deliver(host, custB, orderId, lotB, 300, 60); // كامل

        var db = host.Get<DatesErpDbContext>();
        var journeys = new ShipmentJourneyService(db).GetJourneys(1, 3, planId);

        var l = Assert.Single(journeys);
        // اللحظات: متى دخلت، بكم أمر، بأي تاريخ
        Assert.Equal(1, l.OrderCount);
        Assert.NotNull(l.FirstProductionDate);
        Assert.Equal(DateTime.Today.AddDays(-2).Date, l.FirstProductionDate!.Value.Date); // «بتاريخ كم»
        Assert.True(l.EntryDate >= DateTime.Today && l.EntryDate <= DateTime.Now, "تاريخ الدخول خارج المنطقي");
        // الإجماليات
        Assert.Equal(500, l.PlannedKg, 1);
        Assert.Equal(500, l.ProducedKg, 1);
        Assert.Equal(500, l.AcceptedKg, 1);
        Assert.Equal(500, l.ReceivedKg, 1);
        Assert.Equal(500, l.DeliveredKg, 1);
        Assert.Equal(100, l.DeliveredCartons);
        Assert.NotNull(l.LastDeliveryDate);
        Assert.NotNull(l.CycleDays);
        // الحالة النهائية
        Assert.Equal("✅", l.StatusIcon);
        Assert.Contains("سلّمت تاماً", l.FinalStatusAr);
        // المراحل: دخول + أمر + تشغيل + جودة + تسليم إنتاج + استلام مخزن + تسليم عميل
        var stages = l.Stages.Select(s => s.StageAr).ToList();
        Assert.Contains("أول دخول — خطة الإنتاج", stages);
        Assert.Contains(stages, s => s.StartsWith("أمر 1 — إنتاج"));   // §B102: الخدمة تدمج مرحلة الأمر مع فرعية (تشغيل/فحص...)
        Assert.Contains(stages, s => s.Contains("تشغيل (جلسة)"));
        Assert.Contains(stages, s => s.Contains("فحص الجودة"));
        Assert.Contains(stages, s => s.Contains("تسليم الإنتاج للمخزن"));
        Assert.Contains(stages, s => s.Contains("استلام مخزن التام"));
        Assert.Contains("تسليم 1 — إلى العميل", stages);
        // كل مرحلة تحمل مستنداً وتاريخاً (التتبع الكامل)
        foreach (var s in l.Stages)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.DocNumber));
            Assert.False(string.IsNullOrWhiteSpace(s.DateText));
        }
    }

    // ── 2) التسليم الجزئي: يُقرأ بالضبط (250/500) والآخر «بانتظار التسليم» ──
    [Fact]
    public void Journey_Partial_Delivery_Shows_Exact_Amounts()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, orderId, lotA, lotB, custB) = SeedToReceipt(host);
        B100AvailabilityTests.Deliver(host, 1, orderId, lotA, 250, 50); // جزئي فقط

        var db = host.Get<DatesErpDbContext>();
        var j1 = new ShipmentJourneyService(db).GetJourneys(1, 3, planId).Single();
        Assert.Equal(250, j1.DeliveredKg, 1);
        Assert.Equal("⏳", j1.StatusIcon);
        Assert.Contains("250", j1.FinalStatusAr);
        Assert.Contains("500", j1.FinalStatusAr);

        var jb = new ShipmentJourneyService(db).GetJourneys(custB, 3, planId).Single();
        Assert.Equal(0, jb.DeliveredKg, 1);
        Assert.Equal("🟡", jb.StatusIcon); // مقبول 300 لم يُسلَّم بعد
        Assert.Contains("300", jb.FinalStatusAr);
    }

    // ── 3) قبل أي تسليم: المراحل تتوقف عند الاستلام وبلا «تسليم عميل» ──
    [Fact]
    public void Journey_Before_Any_Delivery_Stops_At_Warehouse_Receipt()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, orderId, lotA, lotB, custB) = SeedToReceipt(host);

        var db = host.Get<DatesErpDbContext>();
        var l = new ShipmentJourneyService(db).GetJourneys(1, 3, planId).Single();
        Assert.Equal("🟡", l.StatusIcon);
        Assert.Equal(0, l.DeliveredKg, 1);
        Assert.DoesNotContain(l.Stages, s => s.StageAr.Contains("إلى العميل"));
        Assert.Contains(l.Stages, s => s.StageAr.Contains("استلام مخزن التام"));
    }

    // ── 4) الفلاتر: عميل/صنف/خطة تعزل الشحنة الصحيحة ──
    [Fact]
    public void Journey_Filters_By_Customer_Product_Plan()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, _, _, _, custB) = SeedToReceipt(host);

        var db = host.Get<DatesErpDbContext>();
        var all = new ShipmentJourneyService(db).GetJourneys(null, null, planId);
        Assert.Equal(2, all.Count); // الخطة فيها عميلان
        Assert.Single(new ShipmentJourneyService(db).GetJourneys(1, 3, planId));
        Assert.Single(new ShipmentJourneyService(db).GetJourneys(custB, 3, planId));
        Assert.Empty(new ShipmentJourneyService(db).GetJourneys(1, 1, planId)); // الصنف 1 (خام) ليس في الخطة
    }

    // ── 5) التصدير (طباعة/PDF/Excel): ReportResult صالح ببنية مطابقة للبيانات ──
    [Fact]
    public void ToReportResult_Export_Is_Valid()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, orderId, lotA, lotB, custB) = SeedToReceipt(host);
        B100AvailabilityTests.Deliver(host, 1, orderId, lotA, 500, 100);
        B100AvailabilityTests.Deliver(host, custB, orderId, lotB, 300, 60);

        var db = host.Get<DatesErpDbContext>();
        var svc = new ShipmentJourneyService(db);
        var r = svc.ToReportResult(1, 3, planId);
        Assert.Equal(8, r.Columns.Count);
        Assert.True(r.Rows.Count >= 8, $"ترويسة + مراحل + حالة (فعلي: {r.Rows.Count})");   // §B102: التصدير مُصفّى بالعميل 1 — مراحل عميل التسليم الثاني لا تدخله
        foreach (var row in r.Rows)
            Assert.Equal(r.Columns.Count, row.Length);
        Assert.Contains(r.Rows, x => x[0]!.ToString()!.StartsWith("═══ شحنة"));
        Assert.Contains(r.Rows, x => x[0]!.ToString()!.Contains("سلّمت تاماً"));
        Assert.Equal("500", r.Summary["مسلَّم للعميل (كجم)"]);
        Assert.Equal("1", r.Summary["عدد الأوامر"]);
        Assert.Equal("1 من 1", r.Summary["سلّمت تاماً"]);
        Assert.NotNull(r.RowLinks);
        Assert.Empty(r.RowLinks);
        // بلا معايير: صف واحد «لا توجد شحنات»
        var empty = svc.ToReportResult(1, 1, planId);
        Assert.Equal(8, empty.Columns.Count);
        Assert.Single(empty.Rows);
    }
}
