using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B99 — نوافذ الجودة واستلام التام (كود النوافذ UI؛ هنا تُختبر الخدمات التي تقف خلفها
/// والسلاسل الكاملة: إقفال+جودة ← اعتماد ← تسليم ← استلام كلي/جزئي ← البطاقات).
/// </summary>
public class B99QualityTests
{
    /// <summary>أمر سريع + إقفال اليوم (بإنتاج فعلي) — مع خيار الإرسال للجودة (ينشئ فحصاً Submitted).</summary>
    private static (int orderId, int lotId, int? qcId) SeedClosed(TestHost host, int producedKg = 500, int producedCtn = 100, bool sendToQuality = true)
    {
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrderPacked(host, db, out var oid, out var lotId);
        var close = host.Get<IExecutionService>()
            .CloseProductionDay(oid, producedKg, producedCtn, 0, 0, 0, false, new List<DowntimeDto>(), sendToQuality);
        if (!close.Ok) throw new InvalidOperationException(close.Message);
        int? qc = db.QualityChecks.AsNoTracking().Where(c => c.OrderId == oid).Select(c => c.Id).FirstOrDefault();
        return (oid, lotId, qc);
    }

    // ── 1) الفحص الكامل ثم الاعتماد: المجاميع + مزامنة المقبول إلى الخطة ──
    [Fact]
    public void QC_Full_Save_Approve_SyncsAccepted()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot, qcId) = SeedClosed(host);
        Assert.NotNull(qcId);
        var db = host.Get<DatesErpDbContext>();
        var qc = db.QualityChecks.Single(c => c.Id == qcId);
        Assert.Equal(DocStatuses.Submitted, qc.Status);
        Assert.Equal(DateTime.Today.AddDays(2).Date, qc.ExpectedCheckDate?.Date); // فترة التبريد

        host.LoginAs("quality");
        var quality = host.Get<IQualityService>();
        // الفحص يستكمل فحص الإقفال المعلَّق (بنفس الجلسة) — كل المنتَج مقبول
        var r = quality.SaveCheck(oid, qc.ExecutionId, DateTime.Today.ToString("yyyy-MM-dd"), "نهائي",
            new List<QualityItemDto>
            {
                new() { ProductId = 3, LotId = lot, AcceptedQtyKg = 500, RejectedQtyKg = 0,
                        AcceptedCartons = 100, RejectedCartons = 0 }
            }, null,
            new QualityLabDto { Decision = "Passed", MoisturePct = 16.5, BrixDeg = 68.5, SampleCartons = 10 });
        Assert.True(r.Ok, r.Message);
        var saved = db.QualityChecks.Include(c => c.Items).Single(c => c.Id == qcId);
        Assert.Equal(DocStatuses.Completed, saved.Status);
        Assert.Equal(500, saved.AcceptedKg, 1);
        Assert.Equal(100, saved.AcceptedCartons, 1);

        var ap = quality.ApproveCheck(qcId.Value);
        Assert.True(ap.Ok, ap.Message);
        Assert.True(db.QualityChecks.Single(c => c.Id == qcId).IsApproved);
    }

    // ── 2) حراس الفحص: فوق المنتَج مرفوض + التغطية المزدوجة مرفوضة ──
    [Fact]
    public void QC_Rejects_OverProduced_And_DoubleCover()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot, qcId) = SeedClosed(host);
        var db = host.Get<DatesErpDbContext>();
        var qc = db.QualityChecks.Single(c => c.Id == qcId);

        host.LoginAs("quality");
        var quality = host.Get<IQualityService>();

        var over = quality.SaveCheck(oid, qc.ExecutionId, DateTime.Today.ToString("yyyy-MM-dd"), "نهائي",
            new List<QualityItemDto>
            {
                new() { ProductId = 3, LotId = lot, AcceptedQtyKg = 600, RejectedQtyKg = 0 }
            });
        Assert.False(over.Ok);
        Assert.Contains("تتجاوز الكمية المنتجة", over.Message);

        // التغطية الكاملة ثم محاولة فحص ثانٍ ← تغطية مزدوجة
        var full = quality.SaveCheck(oid, qc.ExecutionId, DateTime.Today.ToString("yyyy-MM-dd"), "نهائي",
            new List<QualityItemDto>
            {
                new() { ProductId = 3, LotId = lot, AcceptedQtyKg = 490, RejectedQtyKg = 10 }
            });
        Assert.True(full.Ok, full.Message);
        var dup = quality.SaveCheck(oid, null, DateTime.Today.ToString("yyyy-MM-dd"), "نهائي",
            new List<QualityItemDto>
            {
                new() { ProductId = 3, LotId = lot, AcceptedQtyKg = 10, RejectedQtyKg = 0 }
            });
        Assert.False(dup.Ok);
        Assert.Contains("مغطى بفحص سابق", dup.Message);
    }

    // ── 3) فحصان جزئيان تراكمان ثم الاعتماد التراكمي ──
    [Fact]
    public void QC_Partial_Trajectory_Cumulative_Approval()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot, qcId) = SeedClosed(host);
        var db = host.Get<DatesErpDbContext>();
        var qc = db.QualityChecks.Single(c => c.Id == qcId);

        host.LoginAs("quality");
        var quality = host.Get<IQualityService>();

        var p1 = quality.SaveCheck(oid, qc.ExecutionId, DateTime.Today.ToString("yyyy-MM-dd"), "نهائي",
            new List<QualityItemDto>
            {
                new() { ProductId = 3, LotId = lot, AcceptedQtyKg = 200, RejectedQtyKg = 0 }
            });
        Assert.True(p1.Ok, p1.Message);
        Assert.Equal(DocStatuses.InProgress, db.QualityChecks.Single(c => c.Id == qcId).Status); // جزئي

        // الاعتماد قبل الاكتمال التراكمي ← مرفوض
        var early = quality.ApproveCheck(qcId.Value);
        Assert.False(early.Ok);
        Assert.Contains("ليس «مكتملاً»", early.Message);

        var p2 = quality.SaveCheck(oid, null, DateTime.Today.ToString("yyyy-MM-dd"), "نهائي",
            new List<QualityItemDto>
            {
                new() { ProductId = 3, LotId = lot, AcceptedQtyKg = 300, RejectedQtyKg = 0 }
            });
        Assert.True(p2.Ok, p2.Message);
        Assert.Equal(DocStatuses.Completed, db.QualityChecks.Single(c => c.Id == p2.Id).Status);

        var ap = quality.ApproveCheck(p2.Id);
        Assert.True(ap.Ok, ap.Message); // تغطية تراكمية 200+300 = 500
    }

    // ── 4) التصحيح المعتمد: صلاحية خاصة (الإداري) + سبب إجباري + قيد في سجل التصحيحات ──
    [Fact]
    public void QC_Approved_Correction_Gated_And_Audited()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot, qcId) = SeedClosed(host);
        var db = host.Get<DatesErpDbContext>();
        var qc = db.QualityChecks.Single(c => c.Id == qcId);

        host.LoginAs("quality");
        var quality = host.Get<IQualityService>();
        var ok = quality.SaveCheck(oid, qc.ExecutionId, DateTime.Today.ToString("yyyy-MM-dd"), "نهائي",
            new List<QualityItemDto>
            {
                new() { ProductId = 3, LotId = lot, AcceptedQtyKg = 500, RejectedQtyKg = 0 }
            });
        Assert.True(ok.Ok, ok.Message);
        Assert.True(quality.ApproveCheck(qcId.Value).Ok);

        // دور الجودة ليس من حقه «تعديل بعد الاعتماد» (صلاحية حساسة تُمنح من مصفوفة الصلاحيات)
        Assert.Throws<DatesErp.Core.Exceptions.PermissionDeniedException>(
            () => quality.RequestCorrection(qcId.Value, "خطأ في الوزن المسجل — إعادة عدّ الكراتين"));

        host.LoginAsAdmin();
        var noReason = quality.RequestCorrection(qcId.Value, "  ");
        Assert.False(noReason.Ok);
        var r = quality.RequestCorrection(qcId.Value, "خطأ في الوزن المسجل — إعادة عدّ الكراتين");
        Assert.True(r.Ok, r.Message);
        var reopened = db.QualityChecks.Single(c => c.Id == qcId);
        Assert.False(reopened.IsApproved);
        Assert.Equal(DocStatuses.InProgress, reopened.Status);
        Assert.Single(db.QualityCorrections.Where(c => c.CheckId == qcId));
    }

    // ── 5) السلسلة الكاملة: اعتماد فحص ← أمر تسليم ← سند ← استلام كلي ← رصيد التام + إغلاق ──
    [Fact]
    public void FullChain_QC_Delivery_Receive_Full()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot, qcId) = SeedClosed(host);
        var db = host.Get<DatesErpDbContext>();
        var qc = db.QualityChecks.Single(c => c.Id == qcId);

        // الجودة: اعتماد كامل (مقبول 490 / مرفوض 10)
        host.LoginAs("quality");
        var quality = host.Get<IQualityService>();
        var ok = quality.SaveCheck(oid, qc.ExecutionId, DateTime.Today.ToString("yyyy-MM-dd"), "نهائي",
            new List<QualityItemDto>
            {
                new() { ProductId = 3, LotId = lot, AcceptedQtyKg = 490, RejectedQtyKg = 10,
                        AcceptedCartons = 98, RejectedCartons = 2 }
            }, null, new QualityLabDto { Decision = "Passed" });
        Assert.True(ok.Ok, ok.Message);
        Assert.True(quality.ApproveCheck(qcId.Value).Ok);

        // الإنتاج: أمر تسليم من التنفيذ الفعلي + تحريره (الجودة لا تنشئ الاستلام)
        host.LoginAs("production");
        var del = host.Get<IProductionDeliveryService>();
        var sd = del.CreateDeliveryFromActual(qc.ExecutionId.Value, DateTime.Today.ToString("yyyy-MM-dd"));
        Assert.True(sd.Ok, sd.Message);
        Assert.True(del.IssueDelivery(sd.Id).Ok);

        // المخزن: سند من بنود التسليم (بلا إعادة إدخال) + إصدار + استلام الفعلي
        host.LoginAs("warehouse");
        var fg = host.Get<IFinishedGoodsService>();
        var delItem = db.ProductionDeliveryItems.Single(i => i.DeliveryId == sd.Id);
        var sr = fg.SaveReceipt(oid, null, DateTime.Today.ToString("yyyy-MM-dd"),
            new List<FinishedGoodsItemDto>
            {
                new() { ProductId = 3, LotId = lot, PackagingTypeId = 1, PackageCount = 100,
                        NetWeightKg = 500, CustomerId = 1, DeliveryItemId = delItem.Id }
            }, sd.Id);
        Assert.True(sr.Ok, sr.Message);
        Assert.True(fg.Issue(sr.Id).Ok);
        var recv = fg.Receive(sr.Id, null); // null = الاستلام الكامل بالمتبقي
        Assert.True(recv.Ok, recv.Message);

        // الأثر: سند Full + تسليم Full/مغلق + رصيد التام بالدفعة والعميل + إغلاق الأمر
        var rcpt = db.FinishedGoodsReceipts.Include(r => r.Items).Single(r => r.Id == sr.Id);
        Assert.Equal("Full", rcpt.ReceiptStatus);
        Assert.Equal(DocStatuses.Completed, rcpt.Status);
        var delivery = db.ProductionDeliveries.Single(d => d.Id == sd.Id);
        Assert.Equal("Full", delivery.ReceiptStatus);
        Assert.Equal(DocStatuses.Completed, delivery.Status);

        int wfg = db.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
        var bal = db.StockBalances.Single(b => b.WarehouseId == wfg && b.ProductId == 3 && b.LotId == lot && b.CustomerId == 1);
        Assert.Equal(500, bal.QtyKg, 1);

        var order = db.ProductionOrders.Single(o => o.Id == oid);
        Assert.True(order.IsClosed); // اكتمال الإنتاج + الاستلام → إغلاق تلقائي
    }

    // ── 6) الاستلام الجزئي يتراكم وفوق المتبقي مرفوض ──
    [Fact]
    public void Receive_Partial_Accumulates_CapsAtRemaining()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot, qcId) = SeedClosed(host);
        var db = host.Get<DatesErpDbContext>();
        var qc = db.QualityChecks.Single(c => c.Id == qcId);

        host.LoginAs("quality");
        var quality = host.Get<IQualityService>();
        Assert.True(quality.SaveCheck(oid, qc.ExecutionId, DateTime.Today.ToString("yyyy-MM-dd"), "نهائي",
            new List<QualityItemDto>
            {
                new() { ProductId = 3, LotId = lot, AcceptedQtyKg = 500, RejectedQtyKg = 0 }
            }).Ok);
        Assert.True(quality.ApproveCheck(qcId.Value).Ok);

        host.LoginAs("production");
        var del = host.Get<IProductionDeliveryService>();
        var sd = del.CreateDeliveryFromActual(qc.ExecutionId.Value, DateTime.Today.ToString("yyyy-MM-dd"));
        Assert.True(sd.Ok, sd.Message);
        Assert.True(del.IssueDelivery(sd.Id).Ok);

        host.LoginAs("warehouse");
        var fg = host.Get<IFinishedGoodsService>();
        var delItem = db.ProductionDeliveryItems.Single(i => i.DeliveryId == sd.Id);
        var sr = fg.SaveReceipt(oid, null, DateTime.Today.ToString("yyyy-MM-dd"),
            new List<FinishedGoodsItemDto>
            {
                new() { ProductId = 3, LotId = lot, PackagingTypeId = 1, PackageCount = 100,
                        NetWeightKg = 500, CustomerId = 1, DeliveryItemId = delItem.Id }
            }, sd.Id);
        Assert.True(sr.Ok, sr.Message);
        Assert.True(fg.Issue(sr.Id).Ok);
        var rcptItem = db.FinishedGoodsReceiptItems.Single(i => i.ReceiptId == sr.Id);

        // 200 ثم 150 = 350 — جزئي متراكم
        Assert.True(fg.Receive(sr.Id, new Dictionary<int, double> { [rcptItem.Id] = 200 }).Ok);
        Assert.Equal("Partial", db.FinishedGoodsReceipts.Single(r => r.Id == sr.Id).ReceiptStatus);
        Assert.Equal("Partial", db.ProductionDeliveries.Single(d => d.Id == sd.Id).ReceiptStatus);
        Assert.True(fg.Receive(sr.Id, new Dictionary<int, double> { [rcptItem.Id] = 150 }).Ok);
        Assert.Equal(350, db.FinishedGoodsReceiptItems.Single(i => i.Id == rcptItem.Id).ReceivedQtyKg, 1);

        // فوق المتبقي (150) مرفوض
        var over = fg.Receive(sr.Id, new Dictionary<int, double> { [rcptItem.Id] = 200 });
        Assert.False(over.Ok);
        Assert.Contains("أكبر من المتبقي", over.Message);

        // الإكمال: 150 → Full
        Assert.True(fg.Receive(sr.Id, new Dictionary<int, double> { [rcptItem.Id] = 150 }).Ok);
        Assert.Equal("Full", db.FinishedGoodsReceipts.Single(r => r.Id == sr.Id).ReceiptStatus);
        Assert.Equal("Full", db.ProductionDeliveries.Single(d => d.Id == sd.Id).ReceiptStatus);
    }

    // ── 7) البطاقات عبر السلسلة: جاهز للفحص ← فحص معتمد ← تسليم بانتظار الاستلام ← استلام اكتمل ──
    [Fact]
    public void Board_Cards_Through_The_Chain()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot, qcId) = SeedClosed(host);
        var db = host.Get<DatesErpDbContext>();
        var qc = db.QualityChecks.Single(c => c.Id == qcId);

        // الجودة ترى «جاهز للفحص»
        host.LoginAs("quality");
        var qBoard = host.Get<ITaskCenterService>().GetBoard();
        Assert.Contains(qBoard.Action, c => c.DocType == "QC" && c.DocId == qcId && c.Title.Contains("جاهز للفحص"));

        // بعد الاعتماد (مطابق): الإنتاج يرى «فحص معتمد — متبقٍ قابل للتسليم»
        var quality = host.Get<IQualityService>();
        Assert.True(quality.SaveCheck(oid, qc.ExecutionId, DateTime.Today.ToString("yyyy-MM-dd"), "نهائي",
            new List<QualityItemDto>
            {
                new() { ProductId = 3, LotId = lot, AcceptedQtyKg = 500, RejectedQtyKg = 0 }
            }).Ok);
        Assert.True(quality.ApproveCheck(qcId.Value).Ok);

        host.LoginAs("production");
        var pBoard = host.Get<ITaskCenterService>().GetBoard();
        Assert.Contains(pBoard.Action, c => c.DocType == "QC" && c.Title.Contains("متبقٍ قابل للتسليم"));

        // أمر تسليم محرَّر ← المخزن يرى «أمر تسليم بانتظار الاستلام»
        var del = host.Get<IProductionDeliveryService>();
        var sd = del.CreateDeliveryFromActual(qc.ExecutionId.Value, DateTime.Today.ToString("yyyy-MM-dd"));
        Assert.True(sd.Ok, sd.Message);
        Assert.True(del.IssueDelivery(sd.Id).Ok);

        host.LoginAs("warehouse");
        var wBoard0 = host.Get<ITaskCenterService>().GetBoard();
        Assert.Contains(wBoard0.Action, c => c.DocType == "Delivery" && c.DocId == sd.Id && c.Title.Contains("بانتظار الاستلام"));

        // استلام كامل ← يغادر «الاستلام» ويظهر «اكتمل اليوم»
        var fg = host.Get<IFinishedGoodsService>();
        var delItem = db.ProductionDeliveryItems.Single(i => i.DeliveryId == sd.Id);
        var sr = fg.SaveReceipt(oid, null, DateTime.Today.ToString("yyyy-MM-dd"),
            new List<FinishedGoodsItemDto>
            {
                new() { ProductId = 3, LotId = lot, PackagingTypeId = 1, PackageCount = 100,
                        NetWeightKg = 500, CustomerId = 1, DeliveryItemId = delItem.Id }
            }, sd.Id);
        Assert.True(sr.Ok, sr.Message);
        Assert.True(fg.Issue(sr.Id).Ok);
        Assert.True(fg.Receive(sr.Id, null).Ok);

        var wBoard1 = host.Get<ITaskCenterService>().GetBoard();
        Assert.DoesNotContain(wBoard1.Action, c => c.DocType == "Delivery" && c.DocId == sd.Id);
        Assert.Contains(wBoard1.DoneToday, c => c.DocType == "Receipt" && c.DocId == sr.Id);
    }
}
