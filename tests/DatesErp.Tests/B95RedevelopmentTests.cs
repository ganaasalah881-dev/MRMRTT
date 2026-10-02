using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B95 — إعادة تطوير (أ) دورة الفحص/الجودة و(ب) توحيد إقفال خطة الإنتاج.
/// أمر فحص معلَّق من الإقفال اليومي، معادلة التلخيص، سقف المنتَج، التغطية التراكمية،
/// التصحيح المعتمد بسبب، وإغلاق الأمر بعجز بتسوية موثقة.
/// </summary>
public class B95RedevelopmentTests
{
    // ── (أ1) الإقفال اليومي مع الإرسال للجودة ينشئ أمر فحص معلَّقاً بتاريخ متوقع +2 (تبريد) ──
    [Fact]
    public void DayClose_WithSendToQuality_CreatesPendingInspectionOrder()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrder(host, db, out var oid, out _);

        var exec = host.Get<IExecutionService>();
        var close = exec.CloseProductionDay(oid, 500, 67, 0, 0, 0, false, new List<DowntimeDto>(), true);
        Assert.True(close.Ok, close.Message);

        var pending = db.QualityChecks.Single(c => c.OrderId == oid);
        Assert.Equal(DocStatuses.Submitted, pending.Status);
        Assert.False(pending.IsApproved);
        Assert.Equal(close.Id, pending.ExecutionId);
        Assert.Equal(DateTime.Today.AddDays(2).Date, pending.ExpectedCheckDate?.Date);
    }

    // ── (أ2) معادلة التلخيص: المفحوص ≠ مقبول + مرفوض ← رفض ──
    [Fact]
    public void SaveCheck_EquationViolation_IsRejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrder(host, db, out var oid, out _);
        var close = host.Get<IExecutionService>()
            .CloseProductionDay(oid, 500, 67, 0, 0, 0, false, new List<DowntimeDto>(), true);
        Assert.True(close.Ok, close.Message);

        var r = host.Get<IQualityService>().SaveCheck(oid, close.Id, "2026-08-23", "نهائي",
            new List<QualityItemDto>
            { new() { ProductId = 3, CheckedQtyKg = 500, AcceptedQtyKg = 400, RejectedQtyKg = 50 } });
        Assert.False(r.Ok);
        Assert.Contains("معادلة", r.Message);
    }

    // ── (أ3) سقف المنتَج: نتيجة تتجاوز الإنتاج المسجل ← رفض ──
    [Fact]
    public void SaveCheck_OverProduction_IsRejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrder(host, db, out var oid, out _);
        var close = host.Get<IExecutionService>()
            .CloseProductionDay(oid, 500, 67, 0, 0, 0, false, new List<DowntimeDto>(), true);
        Assert.True(close.Ok, close.Message);

        var r = host.Get<IQualityService>().SaveCheck(oid, close.Id, "2026-08-23", "نهائي",
            new List<QualityItemDto>
            { new() { ProductId = 3, CheckedQtyKg = 600, AcceptedQtyKg = 590, RejectedQtyKg = 10 } });
        Assert.False(r.Ok);
        Assert.Contains("تتجاوز", r.Message);
    }

    // ── (أ4) فحصان جزئيان يتكاملان تراكمياً: كلاهما قابل للاعتماد (لا جمود تشغيلي) ──
    [Fact]
    public void SaveCheck_TwoPartials_Cumulate_And_BothApprovable()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrder(host, db, out var oid, out _);
        var close = host.Get<IExecutionService>()
            .CloseProductionDay(oid, 500, 67, 0, 0, 0, false, new List<DowntimeDto>(), true);
        Assert.True(close.Ok, close.Message);

        var quality = host.Get<IQualityService>();
        var q1 = quality.SaveCheck(oid, close.Id, "2026-08-23", "نهائي — بعد التبريد",
            new List<QualityItemDto>
            { new() { ProductId = 3, CheckedQtyKg = 300, AcceptedQtyKg = 290, RejectedQtyKg = 10 } });
        Assert.True(q1.Ok, q1.Message);
        var q2 = quality.SaveCheck(oid, null, "2026-08-23", "نهائي — بعد التبريد",
            new List<QualityItemDto>
            { new() { ProductId = 3, CheckedQtyKg = 200, AcceptedQtyKg = 195, RejectedQtyKg = 5 } });
        Assert.True(q2.Ok, q2.Message);

        // التغطية التراكمية 300 + 200 = 500 = الإنتاج ← كلاهما معتمد
        var ap2 = quality.ApproveCheck(q2.Id);
        Assert.True(ap2.Ok, ap2.Message);
        var ap1 = quality.ApproveCheck(q1.Id);
        Assert.True(ap1.Ok, ap1.Message);
    }

    // ── (أ5) الكميات السالبة مرفوضة ──
    [Fact]
    public void SaveCheck_NegativeQty_IsRejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrder(host, db, out var oid, out _);
        var close = host.Get<IExecutionService>()
            .CloseProductionDay(oid, 500, 67, 0, 0, 0, false, new List<DowntimeDto>(), true);
        Assert.True(close.Ok, close.Message);

        var r = host.Get<IQualityService>().SaveCheck(oid, close.Id, "2026-08-23", "نهائي",
            new List<QualityItemDto>
            { new() { ProductId = 3, CheckedQtyKg = 100, AcceptedQtyKg = -5, RejectedQtyKg = 105 } });
        Assert.False(r.Ok);
        Assert.Contains("سالبة", r.Message);
    }

    // ── (أ6) التصحيح المعتمد: يتطلب سبباً، يعيد الفتح، يسجل صفاً، ثم يُعاد الحفظ والاعتماد ──
    [Fact]
    public void ApprovedCheck_Correction_RequiresReason_Reopens_And_LogsRow()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrder(host, db, out var oid, out _);
        var close = host.Get<IExecutionService>()
            .CloseProductionDay(oid, 500, 67, 0, 0, 0, false, new List<DowntimeDto>(), true);
        Assert.True(close.Ok, close.Message);

        var quality = host.Get<IQualityService>();
        var q = quality.SaveCheck(oid, close.Id, "2026-08-23", "نهائي — بعد التبريد",
            new List<QualityItemDto>
            { new() { ProductId = 3, CheckedQtyKg = 500, AcceptedQtyKg = 490, RejectedQtyKg = 10 } });
        Assert.True(q.Ok, q.Message);
        Assert.True(quality.ApproveCheck(q.Id).Ok);

        var noReason = quality.RequestCorrection(q.Id, "   ");
        Assert.False(noReason.Ok);
        Assert.Contains("سبب", noReason.Message);

        var ok = quality.RequestCorrection(q.Id, "خطأ إدخال بالمقبول — إعادة عدّ");
        Assert.True(ok.Ok, ok.Message);

        var c = db.QualityChecks.Single(x => x.Id == q.Id);
        Assert.False(c.IsApproved);
        Assert.Equal(DocStatuses.InProgress, c.Status);
        var row = db.QualityCorrections.Single(x => x.CheckId == q.Id);
        Assert.Contains("إعادة عدّ", row.Reason);

        // إعادة الحفظ على الجلسة نفسها تستبدل البنود ثم يُعاد الاعتماد
        var re = quality.SaveCheck(oid, close.Id, "2026-08-24", "نهائي — بعد التبريد",
            new List<QualityItemDto>
            { new() { ProductId = 3, CheckedQtyKg = 500, AcceptedQtyKg = 485, RejectedQtyKg = 15 } });
        Assert.True(re.Ok, re.Message);
        Assert.True(quality.ApproveCheck(re.Id).Ok);
    }

    // ── (أ7) التصحيح على فحص غير معتمد مرفوض ──
    [Fact]
    public void Correction_On_UnapprovedCheck_IsRejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrder(host, db, out var oid, out _);
        var close = host.Get<IExecutionService>()
            .CloseProductionDay(oid, 500, 67, 0, 0, 0, false, new List<DowntimeDto>(), true);
        Assert.True(close.Ok, close.Message);

        var quality = host.Get<IQualityService>();
        var q = quality.SaveCheck(oid, close.Id, "2026-08-23", "نهائي",
            new List<QualityItemDto>
            { new() { ProductId = 3, CheckedQtyKg = 500, AcceptedQtyKg = 490, RejectedQtyKg = 10 } });
        Assert.True(q.Ok, q.Message);

        var r = quality.RequestCorrection(q.Id, "سبب تجريبي");
        Assert.False(r.Ok);
        Assert.Contains("غير معتمد", r.Message);
    }

    // ── (ب) إغلاق الأمر بعجز: بلا سبب ← رفض موجَّه، وبسبب ← تسوية موثقة محفوظة ──
    [Fact]
    public void CloseOrder_Shortfall_RequiresReason_Then_SettlesDocumented()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrder(host, db, out var oid, out _);
        var close = host.Get<IExecutionService>()
            .CloseProductionDay(oid, 300, 40, 0, 0, 0, false, new List<DowntimeDto>(), false);
        Assert.True(close.Ok, close.Message);

        var orders = host.Get<IProductionOrderService>();
        var noReason = orders.CloseOrder(oid);
        Assert.False(noReason.Ok);
        Assert.Contains("ناقص", noReason.Message);
        Assert.Contains("سبب التسوية", noReason.Message);

        var withReason = orders.CloseOrder(oid, "عجز 200 كجم: توقف خط معتمد — يُرحَّل للخطة القادمة");
        Assert.True(withReason.Ok, withReason.Message);
        Assert.Contains("تسوية موثقة", withReason.Message);
        Assert.Contains("العجز", withReason.Message);

        var o = db.ProductionOrders.Single(x => x.Id == oid);
        Assert.True(o.IsClosed);
        Assert.Contains("توقف خط", o.CloseReason);
    }
}
