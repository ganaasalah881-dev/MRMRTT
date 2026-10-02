using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>§B101 — مرحلة واحدة في رحلة شحنة العميل (سطر في التصدير المطبوع).</summary>
public class JourneyStageRow
{
    public string StageAr { get; set; } = "";
    public string DocNumber { get; set; } = "-";
    public string DateText { get; set; } = "-";
    public string ShiftLine { get; set; } = "-";
    public double QtyKg { get; set; }
    public int Cartons { get; set; }
    public string StatusAr { get; set; } = "-";
    public string Detail { get; set; } = "-";
}

/// <summary>§B101 — صف أمر إنتاج في شاشة «تقرير حركة شحنة العميل» (جدول الأوامر + تفاصيل الأمر).</summary>
public class OrderJourneyRow
{
    public int OrderId { get; set; }
    public int Seq { get; set; }
    public string OrderNumber { get; set; } = "-";
    public string ProductionDate { get; set; } = "—";
    public string CreatedText { get; set; } = "—";
    public string ApprovedText { get; set; } = "—";
    public string ClosedText { get; set; } = "";
    public string ShiftLine { get; set; } = "—";
    public string StatusAr { get; set; } = "—";
    public double PlannedKg { get; set; }
    public int PlannedCartons { get; set; }
    public double ProducedKg { get; set; }
    /// <summary>التشغيل: «1 جلسة — خرج 800 كجم (حصة الشحنة 500) · خسارة 0 · حشف 0 · نوى 0»</summary>
    public string ProductionText { get; set; } = "لا جلسات تشغيل مسجلة";
    /// <summary>الجودة: «مقبول 500 · مرفوض 0 · ناجح ✓ · معتمد 06/09/2026»</summary>
    public string QualityText { get; set; } = "لا فحص بعد";
    /// <summary>تسليم الإنتاج للمخزن + حالة الاستلام</summary>
    public string WarehouseText { get; set; } = "لا أمر تسليم بعد";
    /// <summary>بنود استلام مخزن التام</summary>
    public string ReceiptText { get; set; } = "لا سندات استلام بعد";
}

/// <summary>§B101 — صف تسليم عميل في شاشة «تقرير حركة شحنة العميل».</summary>
public class CustomerDeliveryJourneyRow
{
    public int DeliveryId { get; set; }
    public int Seq { get; set; }
    public string DocNumber { get; set; } = "-";
    public string DateText { get; set; } = "—";
    public double QtyKg { get; set; }
    public int Cartons { get; set; }
    public string StatusAr { get; set; } = "—";
    public string ApprovalText { get; set; } = "—";
    public double InvoicedKg { get; set; }
    public double BillableKg { get; set; }
}

/// <summary>
/// §B101 — شحنة عميل كاملة: من أول دخولها (سطر خطة) حتى تسليمها تاماً للعميل.
/// الشحنة = سطر خطة إنتاج مرتبط بعميل: خطة ← أوامر (بعدد/تواريخ) ← تشغيل ← فحص ← مخزن التام ← تسليمات العميل.
/// </summary>
public class ShipmentJourneyLine
{
    public int PlanItemId { get; set; }
    public int PlanId { get; set; }
    public string PlanNumber { get; set; } = "";
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = "";
    public int ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public double PlannedKg { get; set; }
    public int PlannedCartons { get; set; }
    public DateTime? PlannedDate { get; set; }

    // ── لحظات الرحلة (الإجابات المباشرة على أسئلة المدير) ──
    /// <summary>متى دخلت: تاريخ إنشاء الخطة (أول وجود للشحنة في النظام).</summary>
    public DateTime EntryDate { get; set; }
    /// <summary>اعتماد الخطة (بها تصبح الشحنة رسمية).</summary>
    public DateTime? PlanApprovedDate { get; set; }
    public string EntryUser { get; set; } = "—";
    public string ApproverUser { get; set; } = "—";
    /// <summary>بكم أمر شُغّلت.</summary>
    public int OrderCount { get; set; }
    /// <summary>متى دخلت الإنتاج: أول تاريخ إنتاج لأوامرها.</summary>
    public DateTime? FirstProductionDate { get; set; }
    public DateTime? LastProductionDate { get; set; }

    // ── الإجماليات (من القيم المُزامَنة نفسها التي تعرضها «متاح العملاء») ──
    public double ProducedKg { get; set; }
    public double AcceptedKg { get; set; }
    public double RejectedKg { get; set; }
    public double ReceivedKg { get; set; }
    public double DeliveredKg { get; set; }
    public int DeliveredCartons { get; set; }
    public double InvoicedKg { get; set; }
    public DateTime? LastDeliveryDate { get; set; }

    /// <summary>✅ سلّمت تاماً / ⏳ جزئي / 🟡 بانتظار التسليم / 🔍 بانتظار الجودة / ⚪ لم تبدأ.</summary>
    public string StatusIcon { get; set; } = "";
    public string FinalStatusAr { get; set; } = "";
    /// <summary>أيام الدورة: من اعتماد الخطة حتى آخر تسليم.</summary>
    public int? CycleDays { get; set; }

    // ── جداول الشاشة ──
    public List<OrderJourneyRow> Orders { get; set; } = new();
    public List<CustomerDeliveryJourneyRow> Deliveries { get; set; } = new();
    /// <summary>المرحلة كاملة سطراًسطراً (للطباعة/تصدير + الاختبارات).</summary>
    public List<JourneyStageRow> Stages { get; set; } = new();
}

/// <summary>
/// §B101 — تتبع شحنة العميل من أول دخولها حتى تسليمها تام.
/// التصميم: كل استعلامات EF بسيطة (بلا قيمة nullable داخل الشرط) والترتيب كله في الذاكرة —
/// بيانات التشغيل الصغيرة الحجم لا تحتاج استعلاماً لكل سطر، والأهم أن الترجمة لا تفاجئنا.
/// </summary>
public class ShipmentJourneyService
{
    private readonly DatesErpDbContext _db;

    public ShipmentJourneyService(DatesErpDbContext db) => _db = db;

    public List<ShipmentJourneyLine> GetJourneys(int? custId, int? prodId, int? planId)
    {
        var db = _db;
        // 1) خطوط الشحنات (بنود خطة مرتبطة بعملاء) — شروط مفاتيح سليمة فقط
        var planItems = db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.CustomerId != null
                     && (custId == null || i.CustomerId == custId)
                     && (prodId == null || i.ProductId == prodId)
                     && (planId == null || i.PlanId == planId))
            .OrderBy(i => i.PlanId).ThenBy(i => i.Id)
            .ToList();
        if (planItems.Count == 0) return new List<ShipmentJourneyLine>();

        // 2) كل البيانات المرتبطة مرة واحدة — بفلاتر coalesce بسيطة تُترجم دائماً
        var planIds = planItems.Select(i => i.PlanId).Distinct().ToList();
        var planItemIds = planItems.Select(i => i.Id).ToList();
        var custIds = planItems.Select(i => i.CustomerId!.Value).Distinct().ToList();
        var prodIds = planItems.Select(i => i.ProductId).Distinct().ToList();

        var plans = db.ProductionPlans.AsNoTracking().Where(p => planIds.Contains(p.Id)).ToList();
        var custs = db.Customers.AsNoTracking().Where(c => custIds.Contains(c.Id)).ToList();
        var prods = db.Products.AsNoTracking().Where(p => prodIds.Contains(p.Id)).ToList();
        var users = db.Users.AsNoTracking().ToList();
        var shifts = db.Shifts.AsNoTracking().ToList();
        var lines = db.ProductionLines.AsNoTracking().ToList();
        var warehouses = db.Warehouses.AsNoTracking().ToList();

        var orderItems = db.ProductionOrderItems.AsNoTracking()
            .Where(i => planItemIds.Contains(i.PlanItemId ?? -1)).ToList();
        orderItems = orderItems.Where(i => i.PlanItemId != null && planItemIds.Contains(i.PlanItemId.Value)).ToList();
        var orderIds = orderItems.Select(o => o.OrderId).Distinct().ToList();
        var orders = db.ProductionOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToList();
        var execs = db.ProductionExecutions.AsNoTracking()
            .Where(e => orderIds.Contains(e.OrderId))
            .OrderBy(e => e.StartDateTime).ThenBy(e => e.Id).ToList();
        var qcs = db.QualityChecks.AsNoTracking()
            .Where(q => orderIds.Contains(q.OrderId ?? -1)).OrderBy(q => q.Id).ToList();
        qcs = qcs.Where(q => q.OrderId != null).ToList();
        var qcIds = qcs.Select(q => q.Id).ToList();
        var qcItems = db.QualityCheckItems.AsNoTracking()
            .Where(i => qcIds.Contains(i.CheckId)).ToList();
        var executionIds = execs.Select(e => e.Id).ToList();
        // Operational deliveries now originate from a closed execution, not a
        // quality-check ID. Keep the old FromCheck link for historical read-only
        // records, but never confuse two numeric IDs from different tables.
        var pds = db.ProductionDeliveries.AsNoTracking()
            .Where(d => (d.SourceType == DeliverySources.FromActual && executionIds.Contains(d.SourceId))
                || (d.SourceType == DeliverySources.FromCheck && qcIds.Contains(d.SourceId)))
            .OrderBy(d => d.Id).ToList();
        var pdIds = pds.Select(d => d.Id).ToList();
        var pdItems = db.ProductionDeliveryItems.AsNoTracking()
            .Where(i => pdIds.Contains(i.DeliveryId)).ToList();
        var fgs = db.FinishedGoodsReceipts.AsNoTracking()
            .Where(f => orderIds.Contains(f.OrderId)).OrderBy(f => f.Id).ToList();
        var fgIds = fgs.Select(f => f.Id).ToList();
        var fgItems = db.FinishedGoodsReceiptItems.AsNoTracking()
            .Where(i => fgIds.Contains(i.ReceiptId)).ToList();
        var cds = db.CustomerDeliveries.AsNoTracking()
            .Where(c => custId == null || c.CustomerId == custId).OrderBy(c => c.Id).ToList();
        var cdIds = cds.Select(c => c.Id).ToList();
        var cdItems = db.CustomerDeliveryItems.AsNoTracking()
            .Where(i => cdIds.Contains(i.DeliveryId)).ToList();

        // دلائل أسماء (بلا تكرار مفاتيح — كل كيان بمفتاح وحيد)
        var planD = plans.ToDictionary(p => p.Id);
        var custD = custs.ToDictionary(c => c.Id);
        var prodD = prods.ToDictionary(p => p.Id);
        var shiftD = shifts.ToDictionary(s => s.Id);
        var lineD = lines.ToDictionary(l => l.Id);
        var whD = warehouses.ToDictionary(w => w.Id);
        var orderD = orders.ToDictionary(o => o.Id);

        var result = new List<ShipmentJourneyLine>();
        foreach (var pi in planItems)
        {
            if (!planD.TryGetValue(pi.PlanId, out var plan)) continue;
            var cust = pi.CustomerId.HasValue && custD.TryGetValue(pi.CustomerId.Value, out var c) ? c : null;
            var prod = prodD.TryGetValue(pi.ProductId, out var pr) ? pr : null;

            var line = new ShipmentJourneyLine
            {
                PlanItemId = pi.Id,
                PlanId = pi.PlanId,
                PlanNumber = plan.DocumentNumber,
                CustomerId = pi.CustomerId!.Value,
                CustomerName = cust?.CustomerName ?? $"عميل #{pi.CustomerId}",
                ProductId = pi.ProductId,
                ProductName = prod?.ProductNameAr ?? $"صنف #{pi.ProductId}",
                PlannedKg = pi.PlannedQtyKg,
                PlannedCartons = pi.PlannedCartons,
                PlannedDate = pi.ScheduledDate,
                EntryDate = plan.CreatedDate,
                PlanApprovedDate = plan.ApprovedDate,
                EntryUser = UserName(users, plan.CreatedBy),
                ApproverUser = UserName(users, plan.ApprovedBy),
                ProducedKg = pi.ProducedQtyKg,
                AcceptedKg = pi.AcceptedQtyKg,
                DeliveredKg = pi.DeliveredQtyKg,
            };

            // أوامر الشحنة: بنود الأمر المرتبطة بسطر الخطة
            var myOrderItems = orderItems.Where(o => o.PlanItemId == pi.Id).ToList();
            var myOrderIds = myOrderItems.Select(o => o.OrderId).Distinct().ToList();
            var myOrders = myOrderIds.Where(orderD.ContainsKey).Select(id => orderD[id]).ToList();
            var myLots = myOrderItems.Where(o => o.LotId != null).Select(o => o.LotId!.Value).ToHashSet();
            line.OrderCount = myOrders.Count;
            var prodDates = myOrders.Where(o => o.ProductionDate != null).Select(o => o.ProductionDate!.Value).ToList();
            line.FirstProductionDate = prodDates.Count > 0 ? prodDates.Min() : null;
            line.LastProductionDate = prodDates.Count > 0 ? prodDates.Max() : null;
            // المرفوض: من آخر فحص لكل أمر (النتيجة الحالية للدفعة)
            foreach (var oid in myOrderIds)
            {
                var last = qcs.Where(q => q.OrderId == oid).OrderBy(q => q.Id).LastOrDefault();
                if (last == null) continue;
                line.RejectedKg += qcItems.Where(i => i.CheckId == last.Id && i.LotId != null && myLots.Contains(i.LotId!.Value))
                    .Sum(i => i.RejectedQtyKg);
            }

            // ── المرحلة 0: أول دخول (الخطة) ──
            line.Stages.Add(new JourneyStageRow
            {
                StageAr = "أول دخول — خطة الإنتاج",
                DocNumber = plan.DocumentNumber,
                DateText = plan.CreatedDate.ToString("dd/MM/yyyy HH:mm"),
                QtyKg = pi.PlannedQtyKg,
                Cartons = pi.PlannedCartons,
                StatusAr = DocStatusAr(plan.Status),
                Detail = $"أنشأها: {line.EntryUser} — اعتمدتها: {line.ApproverUser} " +
                         $"{(plan.ApprovedDate != null ? $"يوم {plan.ApprovedDate:dd/MM/yyyy HH:mm}" : "— بعد")} — " +
                         $"تاريخ التشغيل المجدول: {(pi.ScheduledDate != null ? pi.ScheduledDate.Value.ToString("dd/MM/yyyy") : "—")}"
            });

            // ── المراحل 1..N: كل أمر ونتائجه ──
            for (int n = 0; n < myOrders.Count; n++)
            {
                var o = myOrders[n];
                var tag = $"أمر {n + 1} — إنتاج";
                var lineItems = myOrderItems.Where(i => i.OrderId == o.Id).ToList();
                var oRow = new OrderJourneyRow
                {
                    OrderId = o.Id,
                    Seq = n + 1,
                    OrderNumber = o.DocumentNumber,
                    ProductionDate = o.ProductionDate != null ? o.ProductionDate.Value.ToString("dd/MM/yyyy") : "—",
                    CreatedText = $"{o.CreatedDate:dd/MM/yyyy HH:mm} ({UserName(users, o.CreatedBy)})",
                    ApprovedText = o.ApprovedDate != null ? $"{o.ApprovedDate:dd/MM/yyyy HH:mm} ({UserName(users, o.ApprovedBy)})" : "—",
                    ClosedText = o.IsClosed ? $"أُقفل {o.ClosedDate:dd/MM/yyyy HH:mm} {Trunc(o.CloseReason, 60)}" : "",
                    ShiftLine = ShiftLineText(shiftD, lineD, o.ShiftId, o.LineId),
                    StatusAr = DocStatusAr(o.Status),
                    PlannedKg = lineItems.Sum(i => i.PlannedQtyKg),
                    PlannedCartons = lineItems.Sum(i => i.PlannedCartons),
                    ProducedKg = lineItems.Sum(i => i.ProducedQtyKg),
                };

                // التشغيل (جلسات يوم الإنتاج)
                var oExecs = execs.Where(e => e.OrderId == o.Id).ToList();
                if (oExecs.Count > 0)
                {
                    var totalKg = oExecs.Sum(e => e.ActualQtyKg);
                    var totalCtn = oExecs.Sum(e => e.ActualCartons);
                    oRow.ProductionText =
                        $"{oExecs.Count} جلسة — إخراج {totalKg:N0} كجم / {totalCtn:N0} كرتون (حصة الشحنة {oRow.ProducedKg:N0})" +
                        $" · خسارة {oExecs.Sum(e => e.WastageQtyKg):N0}" +
                        $" · حشف {oExecs.Sum(e => e.HashfKg):N0}" +
                        $" · نوى {oExecs.Sum(e => e.NawaKg):N0}" +
                        $" · خام مستهلك {oExecs.Sum(e => e.ConsumedRawKg):N0} كجم" +
                        $" · أول جلسة {oExecs.First().StartDateTime:dd/MM/yyyy HH:mm}";
                }
                foreach (var ex in oExecs)
                {
                    line.Stages.Add(new JourneyStageRow
                    {
                        StageAr = $"{tag} · تشغيل (جلسة)",
                        DocNumber = ex.DocumentNumber,
                        DateText = ex.StartDateTime != null ? ex.StartDateTime.Value.ToString("dd/MM/yyyy HH:mm")
                            : (ex.EndDateTime != null ? ex.EndDateTime.Value.ToString("dd/MM/yyyy HH:mm") : "—"),
                        ShiftLine = ShiftLineText(shiftD, lineD, ex.ShiftId, ex.LineId),
                        QtyKg = ex.ActualQtyKg,
                        Cartons = ex.ActualCartons,
                        StatusAr = ex.IsDayClosed ? "أُغلقت الجلسة" : "جلسة مفتوحة",
                        Detail = $"حصة هذه الشحنة: {lineItems.Sum(i => i.ProducedQtyKg):N0} كجم — " +
                                 $"خسارة: {ex.WastageQtyKg:N0} كجم — حشف: {ex.HashfKg:N0} — نوى: {ex.NawaKg:N0} — خام مستهلك: {ex.ConsumedRawKg:N0} كجم"
                    });
                }

                // فحص الجودة
                var qcsOfOrder = qcs.Where(q => q.OrderId == o.Id).ToList();
                var lastQc = qcsOfOrder.LastOrDefault();
                if (lastQc != null)
                {
                    var lineAccepted = qcItems.Where(i => i.CheckId == lastQc.Id && i.LotId != null && myLots.Contains(i.LotId!.Value))
                        .Sum(i => i.AcceptedQtyKg);
                    var lineRejected = qcItems.Where(i => i.CheckId == lastQc.Id && i.LotId != null && myLots.Contains(i.LotId!.Value))
                        .Sum(i => i.RejectedQtyKg);
                    oRow.QualityText =
                        $"مقبول {lineAccepted:N0} · مرفوض {lineRejected:N0} (حصة الشحنة) — {DecisionAr(lastQc.Decision)}" +
                        (lastQc.IsApproved ? $" — معتمد {lastQc.ApprovedDate:dd/MM/yyyy}" : " — لم يُعتمد بعد") +
                        $" — الفاحص: {lastQc.InspectorName ?? "—"}";
                }
                foreach (var qc in qcsOfOrder)
                {
                    var lineAccepted = qcItems.Where(i => i.CheckId == qc.Id && i.LotId != null && myLots.Contains(i.LotId!.Value))
                        .Sum(i => i.AcceptedQtyKg);
                    var lineRejected = qcItems.Where(i => i.CheckId == qc.Id && i.LotId != null && myLots.Contains(i.LotId!.Value))
                        .Sum(i => i.RejectedQtyKg);
                    line.Stages.Add(new JourneyStageRow
                    {
                        StageAr = $"{tag} · فحص الجودة",
                        DocNumber = qc.DocumentNumber,
                        DateText = qc.CheckDate != null ? qc.CheckDate.Value.ToString("dd/MM/yyyy") : "—",
                        QtyKg = qc.AcceptedKg,
                        Cartons = (int)qc.AcceptedCartons,
                        StatusAr = qc.IsApproved ? $"معتمد — {DecisionAr(qc.Decision)}" : DecisionAr(qc.Decision),
                        Detail = $"حصة هذه الشحنة: مقبول {lineAccepted:N0} / مرفوض {lineRejected:N0} كجم — " +
                                 $"الفاحص: {qc.InspectorName ?? "—"} — " +
                                 $"رطوبة: {qc.MoisturePct:0.##}% / Brix: {qc.BrixDeg:0.##}° — " +
                                 $"اعتماد: {(qc.ApprovedDate != null ? $"{qc.ApprovedDate:dd/MM/yyyy HH:mm} ({UserName(users, qc.ApprovedBy)})" : "—")}"
                    });
                }

                // تسليم الإنتاج → مخزن التام
                var oPds = pds.Where(d =>
                    (d.SourceType == DeliverySources.FromActual && oExecs.Any(e => e.Id == d.SourceId)
                     || d.SourceType == DeliverySources.FromCheck && qcsOfOrder.Any(q => q.Id == d.SourceId))
                    && pdItems.Any(i => i.DeliveryId == d.Id && i.ProductId == pi.ProductId
                        && i.CustomerId == pi.CustomerId && (myLots.Count == 0 || i.LotId is int lid && myLots.Contains(lid))))
                    .ToList();
                if (oPds.Count > 0)
                {
                    var pd = oPds.Last();
                    var share = pdItems.Where(i => i.DeliveryId == pd.Id && i.CustomerId == line.CustomerId
                        && i.ProductId == line.ProductId && (myLots.Count == 0 || i.LotId is int lid && myLots.Contains(lid)));
                    oRow.WarehouseText =
                        $"{pd.DocumentNumber} بتاريخ {pd.DeliveryDate:dd/MM/yyyy} — حصة الشحنة {share.Sum(i => i.QtyKg):N0} كجم — " +
                        $"استلام المخزن: {ReceiptAr(pd.ReceiptStatus)}" +
                        (pd.BypassReason != null ? $" — تجاوز موثق: {Trunc(pd.BypassReason, 60)}" : "");
                }
                foreach (var pd in oPds)
                {
                    var linePdT = pdItems.Where(i => i.DeliveryId == pd.Id && i.CustomerId == line.CustomerId
                        && i.ProductId == line.ProductId && (myLots.Count == 0 || i.LotId is int lid && myLots.Contains(lid)));
                    line.Stages.Add(new JourneyStageRow
                    {
                        StageAr = $"{tag} · تسليم الإنتاج للمخزن",
                        DocNumber = pd.DocumentNumber,
                        DateText = pd.DeliveryDate != null ? pd.DeliveryDate.Value.ToString("dd/MM/yyyy") : "—",
                        QtyKg = linePdT.Sum(i => i.QtyKg),
                        Cartons = linePdT.Sum(i => i.PackageCount),
                        StatusAr = DocStatusAr(pd.Status) + " — استلام: " + ReceiptAr(pd.ReceiptStatus),
                        Detail = pd.BypassReason != null ? $"تجاوز موثق: {Trunc(pd.BypassReason, 80)}"
                            : pd.SourceType == DeliverySources.FromActual ? "صدر من جلسة الإنتاج الفعلي (المسار الرسمي)"
                            : "سجل تاريخي: صدر من فحص الجودة"
                    });
                }

                // استلام مخزن التام (سندات — قد تتكرر للجزئي)
                var oFgs = fgs.Where(f => f.OrderId == o.Id).ToList();
                if (oFgs.Count > 0)
                {
                    var fg = oFgs.Last();
                    var share = fgItems.Where(i => i.ReceiptId == fg.Id && i.LotId != null && myLots.Contains(i.LotId!.Value));
                    var keeper = whD.TryGetValue(fg.WarehouseId, out var wh) ? wh.WarehouseNameAr : "-";
                    oRow.ReceiptText =
                        $"{fg.DocumentNumber} بتاريخ {fg.DeliveryDate:dd/MM/yyyy} — حصة الشحنة {share.Sum(i => i.ReceivedQtyKg > 0 ? i.ReceivedQtyKg : i.NetWeightKg):N0} كجم — " +
                        $"الحالة: {ReceiptAr(fg.ReceiptStatus)} — {keeper} — أمين المخزن: {UserName(users, fg.WarehouseKeeperId)} — سندات منفذة: {fg.ReceiveCount}";
                }
                foreach (var fg in oFgs)
                {
                    var lineFg = fgItems.Where(i => i.ReceiptId == fg.Id && i.LotId != null && myLots.Contains(i.LotId!.Value));
                    line.Stages.Add(new JourneyStageRow
                    {
                        StageAr = $"{tag} · استلام مخزن التام",
                        DocNumber = fg.DocumentNumber,
                        DateText = fg.DeliveryDate != null ? fg.DeliveryDate.Value.ToString("dd/MM/yyyy") : "—",
                        QtyKg = lineFg.Sum(i => i.ReceivedQtyKg > 0 ? i.ReceivedQtyKg : i.NetWeightKg),
                        Cartons = lineFg.Sum(i => i.PackageCount),
                        StatusAr = ReceiptAr(fg.ReceiptStatus),
                        Detail = $"المخزن: {(whD.TryGetValue(fg.WarehouseId, out var wh2) ? wh2.WarehouseNameAr : "-")} — " +
                                 $"أمين المخزن: {UserName(users, fg.WarehouseKeeperId)} — " +
                                 $"بنود استلام منفذة: {fg.ReceiveCount}"
                    });
                }

                line.Orders.Add(oRow);
            }

            // ── تسليمات العميل الخاصة بهذه الشحنة ──
            var myCds = cds.Where(c => c.CustomerId == line.CustomerId
                && cdItems.Any(i => i.DeliveryId == c.Id && i.ProductId == line.ProductId
                    && ((i.LotId != null && myLots.Contains(i.LotId.Value)) || (c.OrderId != null && myOrderIds.Contains(c.OrderId.Value))))
                ).ToList();
            for (int m = 0; m < myCds.Count; m++)
            {
                var cd = myCds[m];
                var items = cdItems.Where(i => i.DeliveryId == cd.Id && i.ProductId == line.ProductId
                    && ((i.LotId != null && myLots.Contains(i.LotId.Value)) || (cd.OrderId != null && myOrderIds.Contains(cd.OrderId.Value)))).ToList();
                var qty = items.Sum(i => i.QtyKg);
                var ctns = items.Sum(i => i.PackageCount);
                var invoicedShare = cd.TotalQtyKg > 0 ? cd.InvoicedQtyKg * qty / cd.TotalQtyKg : 0;
                line.InvoicedKg += invoicedShare;
                line.DeliveredCartons += cd.IsApproved ? ctns : 0;
                if (cd.IsApproved && cd.DeliveryDate != null)
                    line.LastDeliveryDate = line.LastDeliveryDate == null || cd.DeliveryDate.Value > line.LastDeliveryDate.Value
                        ? cd.DeliveryDate : line.LastDeliveryDate;
                line.Deliveries.Add(new CustomerDeliveryJourneyRow
                {
                    DeliveryId = cd.Id,
                    Seq = m + 1,
                    DocNumber = cd.DocumentNumber,
                    DateText = cd.DeliveryDate != null ? cd.DeliveryDate.Value.ToString("dd/MM/yyyy") : "—",
                    QtyKg = qty,
                    Cartons = ctns,
                    StatusAr = cd.IsApproved ? $"مُعتمد — {DocStatusAr(cd.Status)}" : DocStatusAr(cd.Status),
                    ApprovalText = cd.ApprovedDate != null ? $"{cd.ApprovedDate:dd/MM/yyyy HH:mm} ({UserName(users, cd.ApprovedBy)})" : "لم يُعتمد",
                    InvoicedKg = invoicedShare,
                    BillableKg = Math.Max(0, qty - invoicedShare),
                });
                line.Stages.Add(new JourneyStageRow
                {
                    StageAr = $"تسليم {m + 1} — إلى العميل",
                    DocNumber = cd.DocumentNumber,
                    DateText = cd.DeliveryDate != null ? cd.DeliveryDate.Value.ToString("dd/MM/yyyy") : "—",
                    QtyKg = qty,
                    Cartons = ctns,
                    StatusAr = cd.IsApproved ? $"مُعتمد — {DocStatusAr(cd.Status)}" : DocStatusAr(cd.Status),
                    Detail = $"اعتماد: {(cd.ApprovedDate != null ? $"{cd.ApprovedDate:dd/MM/yyyy HH:mm} ({UserName(users, cd.ApprovedBy)})" : "—")} — " +
                             $"فُوتر (نظام المبيعات): {invoicedShare:N0} كجم — متبقي للفوترة: {Math.Max(0, qty - invoicedShare):N0} كجم"
                });
            }

            // ── الحالة النهائية + أيام الدورة ──
            line.ReceivedKg = fgItems.Where(i => i.LotId != null && myLots.Contains(i.LotId!.Value)
                && i.CustomerId == line.CustomerId && i.ProductId == line.ProductId)
                .Sum(i => i.ReceivedQtyKg > 0 ? i.ReceivedQtyKg : i.NetWeightKg);
            ComputeFinalStatus(line);
            if (line.LastDeliveryDate != null && line.PlanApprovedDate != null)
                line.CycleDays = (int)Math.Ceiling((line.LastDeliveryDate.Value - line.PlanApprovedDate.Value).TotalDays);

            result.Add(line);
        }
        return result;
    }

    /// <summary>§B101 — التقرير كـ ReportResult جاهز للطباعة/PDF/Excel (يستهلكه التصدير من الشاشة).</summary>
    public ReportResult ToReportResult(int? custId, int? prodId, int? planId)
    {
        var journeys = GetJourneys(custId, prodId, planId);
        var r = new ReportResult
        {
            TitleAr = "تقرير حركة شحنة العميل — من أول دخولها حتى تسليمها تام للعميل",
            PeriodLabel = $"حتى {DateTime.Now:dd/MM/yyyy HH:mm}",
            RowLinks = new List<DocLinkDto>(),
        };
        r.Columns.AddRange(new[] { "المرحلة", "المستند", "التاريخ", "الوردية/الخط", "الكمية (كجم)", "الكراتين", "الحالة", "التفاصيل" });
        if (journeys.Count == 0)
        {
            r.Rows.Add(new object[] { "لا توجد شحنات (بنود خطة مرتبطة بعميل) بالمعايير المحددة", "-", "-", "-", 0, 0, "-", "-" });
            r.Summary["عدد الشحنات"] = "0";
            return r;
        }
        foreach (var l in journeys)
        {
            r.Rows.Add(new object[]
            {
                $"═══ شحنة {l.CustomerName} · {l.ProductName} · خطة {l.PlanNumber} ═══",
                "-", l.PlannedDate?.ToString("dd/MM/yyyy") ?? "-", "-",
                l.PlannedKg, l.PlannedCartons,
                $"مخطط {l.PlannedKg:N0} كجم / {l.PlannedCartons} كرتون",
                $"📌 دخلت النظام: {l.EntryDate:dd/MM/yyyy HH:mm} ({l.EntryUser}) — دخلت الإنتاج: {(l.FirstProductionDate != null ? l.FirstProductionDate.Value.ToString("dd/MM/yyyy") : "— بعد")} — شُغّلت بـ {l.OrderCount} أمر"
            });
            foreach (var s in l.Stages)
                r.Rows.Add(new object[] { s.StageAr, s.DocNumber, s.DateText, s.ShiftLine, s.QtyKg, s.Cartons, s.StatusAr, s.Detail });
            r.Rows.Add(new object[]
            {
                $"── حالة الشحنة: {l.StatusIcon} {l.FinalStatusAr} ──",
                "-", "-", "-", l.PlannedKg, l.DeliveredCartons,
                $"{l.StatusIcon} {l.FinalStatusAr}",
                $"مخطط {l.PlannedKg:N0} | مُنتج {l.ProducedKg:N0} | مقبول {l.AcceptedKg:N0} | مرفوض {l.RejectedKg:N0} | " +
                $"مستلم (تام) {l.ReceivedKg:N0} | مسلَّم {l.DeliveredKg:N0} | مُفوتر {l.InvoicedKg:N0} | " +
                $"الدورة: {(l.CycleDays != null ? l.CycleDays.Value + " يوم من اعتماد الخطة" : "—")}"
            });
        }
        var entryDates = journeys.Select(l => l.EntryDate).ToList();
        var firstProd = journeys.Where(l => l.FirstProductionDate != null).Select(l => l.FirstProductionDate!.Value).ToList();
        r.Summary["عدد الشحنات"] = journeys.Count.ToString("N0");
        r.Summary["عدد الأوامر"] = journeys.Sum(l => l.OrderCount).ToString("N0");
        r.Summary["دخول النظام"] = entryDates.Count > 0 ? entryDates.Min().ToString("dd/MM/yyyy") : "—";
        r.Summary["دخلت الإنتاج"] = firstProd.Count > 0 ? firstProd.Min().ToString("dd/MM/yyyy") : "—";
        r.Summary["مخطط (كجم)"] = journeys.Sum(l => l.PlannedKg).ToString("N0");
        r.Summary["مُنتَج (كجم)"] = journeys.Sum(l => l.ProducedKg).ToString("N0");
        r.Summary["مقبول (كجم)"] = journeys.Sum(l => l.AcceptedKg).ToString("N0");
        r.Summary["مسلَّم للعميل (كجم)"] = journeys.Sum(l => l.DeliveredKg).ToString("N0");
        r.Summary["سلّمت تاماً"] = $"{journeys.Count(l => l.StatusIcon == "✅")} من {journeys.Count}";
        return r;
    }

    private static void ComputeFinalStatus(ShipmentJourneyLine l)
    {
        double planned = l.PlannedKg;
        if (l.DeliveredKg >= planned - 0.001 && planned > 0)
        { l.StatusIcon = "✅"; l.FinalStatusAr = $"سلّمت تاماً ({l.DeliveredKg:N0} كجم)"; return; }
        if (l.DeliveredKg > 0)
        { l.StatusIcon = "⏳"; l.FinalStatusAr = $"جزئي: {l.DeliveredKg:N0} من {planned:N0} كجم"; return; }
        if (l.AcceptedKg > 0)
        { l.StatusIcon = "🟡"; l.FinalStatusAr = $"بانتظار التسليم ({l.AcceptedKg:N0} كجم مقبول)"; return; }
        if (l.ProducedKg > 0)
        { l.StatusIcon = "🔍"; l.FinalStatusAr = $"بانتظار الجودة ({l.ProducedKg:N0} كجم منتج)"; return; }
        l.StatusIcon = "⚪"; l.FinalStatusAr = "لم تبدأ بعد";
    }

    private static string UserName(List<AppUser> users, int? id)
    {
        if (id == null) return "—";
        var u = users.FirstOrDefault(x => x.Id == id.Value);
        return u?.FullName ?? u?.UserName ?? "—";
    }

    private static string ShiftLineText(Dictionary<int, Shift> shifts, Dictionary<int, ProductionLine> lines, int? shift, int? line)
    {
        var s = shift != null && shifts.TryGetValue(shift.Value, out var sv) ? sv.ShiftNameAr : null;
        var l = line != null && lines.TryGetValue(line.Value, out var lv) ? lv.LineNameAr : null;
        if (s == null && l == null) return "—";
        return s == null ? $"خط: {l}" : l == null ? $"وردية: {s}" : $"وردية: {s} · خط: {l}";
    }

    private static string DecisionAr(string d) => d switch
    {
        "Passed" => "ناجح ✓",
        "Failed" => "مرفوض ✗",
        "ConditionalPassed" => "ناجح بشروط",
        _ => d ?? "—"
    };

    private static string ReceiptAr(string s) => s switch
    {
        "None" => "لم يُستلم بعد",
        "Partial" => "استلام جزئي ◐",
        "Full" => "استلام كامل ✓",
        _ => s ?? "—"
    };

    private static string DocStatusAr(string s) => s switch
    {
        "Draft" => "مسودة",
        "Submitted" => "مقدم",
        "Approved" => "معتمد",
        "Issued" => "مُحرَّر",
        "Scheduled" => "مجدول",
        "InProgress" => "قيد التنفيذ",
        "Stopped" => "موقوف",
        "Completed" => "مكتمل",
        "Closed" => "مقفل",
        "Cancelled" => "ملغي",
        "PendingDelivery" => "بانتظار تسليم العميل",
        _ => s ?? "—"
    };

    private static string Trunc(string s, int max)
        => string.IsNullOrWhiteSpace(s) ? "" : s.Length > max ? s[..max] + "…" : s;
}
