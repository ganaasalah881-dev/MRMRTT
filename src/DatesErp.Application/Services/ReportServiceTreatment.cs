using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §المعالجة والتعقيم — حزمة التقارير (النقطة 6 من متطلبات دورة المعالجة).
///
/// الموجود قبل هذا الملف كان عمودين فقط داخل تقرير «أرصدة الخام»: «تحت المعالجة»
/// و«جاهز للإنتاج». وهما **رصيد لحظي لا سِجل**، فيعجزان عن الإجابة عن أسئلة
/// التشغيل الفعلية: ما المتأخر عن موعده؟ كم استغرقت المعالجة فعلياً مقابل المخطط؟
/// ما نسبة المرفوض؟ من أفرج ومتى؟
///
/// ثلاثة تقارير:
///   treatment_log       — سِجل كل عملية معالجة بمراحلها وأعمارها.
///   treatment_overdue   — المتأخرات فقط، مرتبةً بالأقدم تأخراً (شاشة متابعة يومية).
///   treatment_performance — أداء المُدد: المخطط مقابل الفعلي ونِسب الرفض لكل نوع معالجة.
///
/// **لا منطق أعمال هنا**: قراءة فقط من RawTreatments، والحالة تبقى مشتقة كما
/// عرّفها الكيان (IsOverdue / IsReadyByTime) حتى لا يتفرّع تعريفان للجاهزية.
/// </summary>
public partial class ReportService
{
    private List<ReportDefinition> GetTreatmentDefinitions()
    {
        var products = Db.Products.AsNoTracking().OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.ProductNameAr }).ToList()
            .Select(x => (x.Id.ToString(), x.ProductNameAr)).ToList();
        var customers = Db.Customers.AsNoTracking().OrderBy(c => c.CustomerName)
            .Select(c => new { c.Id, c.CustomerName }).ToList()
            .Select(x => (x.Id.ToString(), x.CustomerName)).ToList();
        var types = Db.TreatmentTypes.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.Id)
            .Select(t => new { t.Id, t.TypeNameAr }).ToList()
            .Select(x => (x.Id.ToString(), x.TypeNameAr)).ToList();

        ReportParameter PDate(string key, string label) => new() { Key = key, LabelAr = label, Kind = "date" };
        ReportParameter PType() => new()
        {
            Key = "ttype", LabelAr = "نوع المعالجة", Kind = "list", Options = types
        };

        return new List<ReportDefinition>
        {
            new()
            {
                Code = "treatment_log",
                TitleAr = "سجل المعالجة والتعقيم (كل عملية بمراحلها وعمرها)",
                Category = "المعالجة والتعقيم",
                Parameters = new()
                {
                    PDate("from", "من تاريخ البدء"), PDate("to", "إلى تاريخ البدء"),
                    new() { Key = "customer", LabelAr = "العميل", Kind = "list", Options = customers },
                    new() { Key = "product", LabelAr = "الصنف", Kind = "list", Options = products },
                    PType(),
                    new() { Key = "tstatus", LabelAr = "الحالة", Kind = "list", Options = new()
                    {
                        (TreatmentStatuses.InProgress, "تحت المعالجة"),
                        (TreatmentStatuses.Released, "جاهزة للإنتاج"),
                        (TreatmentStatuses.Rejected, "مرفوضة"),
                        (TreatmentStatuses.Cancelled, "ملغاة")
                    } }
                }
            },
            new()
            {
                Code = "treatment_overdue",
                TitleAr = "المعالجات المتأخرة (تجاوزت موعد الجاهزية ولم يُفرج عنها)",
                Category = "المعالجة والتعقيم",
                Parameters = new()
                {
                    new() { Key = "product", LabelAr = "الصنف", Kind = "list", Options = products },
                    PType()
                }
            },
            new()
            {
                Code = "treatment_performance",
                TitleAr = "أداء المعالجة — المدة المخططة مقابل الفعلية ونِسب الرفض",
                Category = "المعالجة والتعقيم",
                Parameters = new() { PDate("from", "من تاريخ البدء"), PDate("to", "إلى تاريخ البدء") }
            }
        };
    }

    private ReportResult RunTreatmentReports(string code, Dictionary<string, string> p,
        DateTime? from, DateTime? to, int? custId, int? prodId)
    {
        return code switch
        {
            "treatment_log" => TreatmentLog(p, from, to, custId, prodId),
            "treatment_overdue" => TreatmentOverdue(p, prodId),
            "treatment_performance" => TreatmentPerformance(from, to),
            _ => null
        };
    }

    // ═══════════════════════════ مساعدات مشتركة ═══════════════════════════

    private string TrtProductName(int id) =>
        Db.Products.AsNoTracking().Where(x => x.Id == id).Select(x => x.ProductNameAr).FirstOrDefault() ?? "—";

    private string TrtTypeName(int? id) => id == null ? "—" :
        Db.TreatmentTypes.AsNoTracking().Where(x => x.Id == id).Select(x => x.TypeNameAr).FirstOrDefault() ?? "—";

    private string TrtUserName(int? id) => id == null ? "—" :
        Db.Users.AsNoTracking().Where(x => x.Id == id).Select(x => x.FullName).FirstOrDefault() ?? "—";

    /// <summary>
    /// المدة بصيغة يقرأها المشغّل: الساعات دون 48 تبقى ساعات، وما فوقها أيام.
    /// السبب أن «240 ساعة» لا تعني شيئاً لمن يخطط بالأيام، و«0.25 يوم» كذلك.
    /// </summary>
    private static string HumanDuration(double hours)
    {
        if (hours < 0) return "—";
        if (hours < 48) return $"{hours:N1} ساعة";
        return $"{hours / 24.0:N1} يوم";
    }

    /// <summary>الدفعة الأم وشحنتها وعميلها — عمود التتبع الذي لا ينقطع.</summary>
    private (string lotCode, int? shipmentId, string shipNo, int? custId, string custName) TrtLotInfo(int lotId)
    {
        var lot = Db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == lotId);
        if (lot == null) return ("—", null, "—", null, "—");
        var ship = lot.ShipmentId == null ? null
            : Db.Shipments.AsNoTracking().FirstOrDefault(s => s.Id == lot.ShipmentId);
        int? cid = ship?.CustomerId ?? lot.CustomerId;
        string cname = cid == null ? "—"
            : Db.Customers.AsNoTracking().Where(c => c.Id == cid).Select(c => c.CustomerName).FirstOrDefault() ?? "—";
        return (lot.LotCode ?? "—", lot.ShipmentId, ship?.DocumentNumber ?? "—", cid, cname);
    }

    // ═══════════════════════════ 1) سجل المعالجة ═══════════════════════════

    private ReportResult TreatmentLog(Dictionary<string, string> p, DateTime? from, DateTime? to,
        int? custId, int? prodId)
    {
        var r = new ReportResult
        {
            TitleAr = "سجل المعالجة والتعقيم — من الاستلام إلى الجاهزية",
            RowLinks = new List<DocLinkDto>()
        };
        r.Columns.AddRange(new[]
        {
            "رقم المعالجة", "الشحنة", "الدفعة", "العميل", "الصنف", "نوع المعالجة",
            "البدء", "المدة المخططة", "الجاهزية المتوقعة", "الاكتمال", "المدة الفعلية",
            "الكمية (كجم)", "الطرود", "أُفرج (كجم)", "رُفض (كجم)", "المتبقي (كجم)",
            "المسؤول", "الحالة", "ملاحظات"
        });

        int? typeId = p != null && p.TryGetValue("ttype", out var tv) && int.TryParse(tv, out var tvi) ? tvi : null;
        string status = p != null && p.TryGetValue("tstatus", out var sv) && !string.IsNullOrWhiteSpace(sv) ? sv : null;

        var q = Db.RawTreatments.AsNoTracking().AsQueryable();
        if (from != null) q = q.Where(t => t.StartedAt >= from);
        if (to != null) q = q.Where(t => t.StartedAt <= to.Value.AddDays(1));
        if (prodId != null) q = q.Where(t => t.ProductId == prodId);
        if (typeId != null) q = q.Where(t => t.TreatmentTypeId == typeId);
        if (status != null) q = q.Where(t => t.Status == status);

        var list = q.OrderByDescending(t => t.StartedAt).ThenByDescending(t => t.Id).ToList();

        double tQty = 0, tRel = 0, tRej = 0, tRem = 0;
        int overdue = 0, readyNotReleased = 0;

        foreach (var t in list)
        {
            var info = TrtLotInfo(t.LotId);
            // §فلتر العميل يُطبَّق بعد الجلب لأن العميل يأتي عبر الدفعة/الشحنة لا من صف المعالجة.
            if (custId != null && info.custId != custId) continue;

            double actualHours = t.CompletedAt != null
                ? (t.CompletedAt.Value - t.StartedAt).TotalHours : -1;

            // §الحالة المعروضة تحمل الاشتقاق الزمني: «تحت المعالجة» وحدها تُخفي أن المدة
            // انقضت والبضاعة تنتظر قراراً بشرياً بالإفراج.
            string statusAr = TreatmentStatuses.ToArabic(t.Status);
            if (t.Status == TreatmentStatuses.InProgress)
            {
                if (t.IsOverdue) { statusAr = "متأخرة ⛔"; overdue++; }
                else if (t.IsReadyByTime) { statusAr = "بلغت مدتها — تنتظر الإفراج 🟡"; readyNotReleased++; }
                else statusAr = "تحت المعالجة 🟠";
            }

            tQty += t.QtyKg; tRel += t.ReleasedQtyKg; tRej += t.RejectedQtyKg; tRem += t.RemainingQtyKg;

            r.Rows.Add(new object[]
            {
                t.TreatmentNo ?? "—", info.shipNo, info.lotCode, info.custName,
                TrtProductName(t.ProductId), TrtTypeName(t.TreatmentTypeId),
                UiFormat.DT(t.StartedAt), HumanDuration(t.DurationHours),
                UiFormat.DT(t.ExpectedReadyAt),
                t.CompletedAt != null ? UiFormat.DT(t.CompletedAt) : "—",
                actualHours >= 0 ? HumanDuration(actualHours) : "—",
                t.QtyKg, t.PackageCount, t.ReleasedQtyKg, t.RejectedQtyKg, t.RemainingQtyKg,
                TrtUserName(t.ResponsibleUserId), statusAr, t.Notes ?? "—"
            });
            // §التنقل إلى مستند الاستلام الأصلي — الدفعة ليست مستنداً قائماً بذاته.
            // §null لا كائن فارغ: الشاشة تفحص link == null لتعرض «لا مستند مرتبط»،
            // وكائن فارغ يتجاوز الفحص فيُستدعى OpenDocument("", 0).
            r.RowLinks.Add(info.shipmentId != null
                ? new DocLinkDto { DocType = "receiving", Id = info.shipmentId.Value }
                : null);
        }

        r.Summary["عدد عمليات المعالجة"] = r.Rows.Count.ToString("N0");
        r.Summary["إجمالي الكمية المُعالَجة (كجم)"] = tQty.ToString("N1");
        r.Summary["أُفرج عنه (كجم)"] = tRel.ToString("N1");
        r.Summary["مرفوض (كجم)"] = tRej.ToString("N1");
        r.Summary["ما زال داخل الدورة (كجم)"] = tRem.ToString("N1");
        r.Summary["متأخرة (عملية)"] = overdue.ToString("N0");
        r.Summary["بلغت مدتها وتنتظر الإفراج (عملية)"] = readyNotReleased.ToString("N0");
        // §معادلة الاتساق: لا ازدواجية ولا اختفاء — مطلب المستخدم رقم 3.
        r.Summary["المعادلة"] = $"المُعالَج {tQty:N1} = مفرَج {tRel:N1} + مرفوض {tRej:N1} + داخل الدورة {tRem:N1}";
        if (tQty > 0)
            r.Summary["نسبة الرفض"] = $"{tRej / tQty * 100:N2}%";
        return r;
    }

    // ═══════════════════════════ 2) المتأخرات ═══════════════════════════

    private ReportResult TreatmentOverdue(Dictionary<string, string> p, int? prodId)
    {
        var r = new ReportResult
        {
            TitleAr = "المعالجات المتأخرة — تجاوزت موعد الجاهزية ولم يُفرج عنها",
            RowLinks = new List<DocLinkDto>()
        };
        r.Columns.AddRange(new[]
        {
            "رقم المعالجة", "الشحنة", "الدفعة", "العميل", "الصنف", "نوع المعالجة",
            "البدء", "الجاهزية المتوقعة", "التأخير", "الكمية المحتجزة (كجم)", "الطرود", "المسؤول", "ملاحظات"
        });

        int? typeId = p != null && p.TryGetValue("ttype", out var tv) && int.TryParse(tv, out var tvi) ? tvi : null;
        var now = DateTime.Now;

        var q = Db.RawTreatments.AsNoTracking()
            .Where(t => t.Status == TreatmentStatuses.InProgress && t.ExpectedReadyAt < now);
        if (prodId != null) q = q.Where(t => t.ProductId == prodId);
        if (typeId != null) q = q.Where(t => t.TreatmentTypeId == typeId);

        // §الترتيب بالأقدم تأخراً: هذا تقرير عمل يومي، وأول صف فيه هو أولى ما يُعالَج.
        var list = q.OrderBy(t => t.ExpectedReadyAt).ToList();

        double heldKg = 0; double worstDays = 0;
        foreach (var t in list)
        {
            var info = TrtLotInfo(t.LotId);
            double lateHours = (now - t.ExpectedReadyAt).TotalHours;
            if (lateHours / 24.0 > worstDays) worstDays = lateHours / 24.0;
            heldKg += t.RemainingQtyKg;

            r.Rows.Add(new object[]
            {
                t.TreatmentNo ?? "—", info.shipNo, info.lotCode, info.custName,
                TrtProductName(t.ProductId), TrtTypeName(t.TreatmentTypeId),
                UiFormat.DT(t.StartedAt), UiFormat.DT(t.ExpectedReadyAt),
                HumanDuration(lateHours),
                t.RemainingQtyKg, t.PackageCount,
                TrtUserName(t.ResponsibleUserId), t.Notes ?? "—"
            });
            // §null لا كائن فارغ: الشاشة تفحص link == null لتعرض «لا مستند مرتبط»،
            // وكائن فارغ يتجاوز الفحص فيُستدعى OpenDocument("", 0).
            r.RowLinks.Add(info.shipmentId != null
                ? new DocLinkDto { DocType = "receiving", Id = info.shipmentId.Value }
                : null);
        }

        r.Summary["عدد المعالجات المتأخرة"] = list.Count.ToString("N0");
        // §الأثر التشغيلي: هذه كمية محجوبة عن الإنتاج الآن، وهي الرقم الذي يهم مدير الإنتاج.
        r.Summary["كمية محتجزة عن الإنتاج (كجم)"] = heldKg.ToString("N1");
        r.Summary["أقصى تأخير"] = list.Count == 0 ? "—" : $"{worstDays:N1} يوم";
        if (list.Count == 0) r.Summary["الحالة"] = "لا توجد معالجات متأخرة ✅";
        return r;
    }

    // ═══════════════════════════ 3) أداء المعالجة ═══════════════════════════

    private ReportResult TreatmentPerformance(DateTime? from, DateTime? to)
    {
        var r = new ReportResult
        {
            TitleAr = "أداء المعالجة — المدة المخططة مقابل الفعلية ونِسب الرفض لكل نوع",
            RowLinks = new List<DocLinkDto>()
        };
        r.Columns.AddRange(new[]
        {
            "نوع المعالجة", "المدة الافتراضية", "عدد العمليات", "مكتملة", "قيد التنفيذ", "متأخرة",
            "متوسط المدة المخططة", "متوسط المدة الفعلية", "الانحراف", "الكمية (كجم)",
            "أُفرج (كجم)", "رُفض (كجم)", "نسبة الرفض", "يتطلب فحص جودة"
        });

        var q = Db.RawTreatments.AsNoTracking().AsQueryable();
        if (from != null) q = q.Where(t => t.StartedAt >= from);
        if (to != null) q = q.Where(t => t.StartedAt <= to.Value.AddDays(1));
        var all = q.ToList();

        var types = Db.TreatmentTypes.AsNoTracking().ToList();
        double gQty = 0, gRel = 0, gRej = 0;
        int gCount = 0, gDone = 0, gLate = 0;

        // §التجميع يشمل «بلا نوع» أيضاً: إخفاؤها يجعل مجموع التقرير أقل من الواقع
        // ويوهم أن كل معالجة مصنَّفة.
        var groups = all.GroupBy(t => t.TreatmentTypeId).OrderBy(g => g.Key ?? int.MaxValue);

        foreach (var g in groups)
        {
            var ty = g.Key == null ? null : types.FirstOrDefault(x => x.Id == g.Key);
            var done = g.Where(t => t.CompletedAt != null).ToList();

            double planAvg = g.Average(t => t.DurationHours);
            double actAvg = done.Count == 0 ? -1
                : done.Average(t => (t.CompletedAt.Value - t.StartedAt).TotalHours);

            double qty = g.Sum(t => t.QtyKg);
            double rel = g.Sum(t => t.ReleasedQtyKg);
            double rej = g.Sum(t => t.RejectedQtyKg);
            int late = g.Count(t => t.IsOverdue);
            int inprog = g.Count(t => t.Status == TreatmentStatuses.InProgress);

            gQty += qty; gRel += rel; gRej += rej;
            gCount += g.Count(); gDone += done.Count; gLate += late;

            r.Rows.Add(new object[]
            {
                ty?.TypeNameAr ?? "بلا نوع محدد",
                ty != null ? HumanDuration(ty.DefaultDurationHours) : "—",
                g.Count(), done.Count, inprog, late,
                HumanDuration(planAvg),
                actAvg >= 0 ? HumanDuration(actAvg) : "—",
                // §الانحراف بالموجب تأخّر وبالسالب تعجّل — يقيس واقعية المدد المعرَّفة.
                actAvg >= 0 ? $"{(actAvg - planAvg >= 0 ? "+" : "")}{HumanDuration(Math.Abs(actAvg - planAvg))}" : "—",
                qty, rel, rej,
                qty > 0 ? $"{rej / qty * 100:N2}%" : "—",
                ty != null && ty.RequiresQualityCheck ? "نعم" : "لا"
            });
            r.RowLinks.Add(null); // صف تجميعي بلا مستند مصدر
        }

        r.Summary["أنواع المعالجة المستخدمة"] = r.Rows.Count.ToString("N0");
        r.Summary["إجمالي العمليات"] = gCount.ToString("N0");
        r.Summary["مكتملة"] = gDone.ToString("N0");
        r.Summary["متأخرة الآن"] = gLate.ToString("N0");
        r.Summary["إجمالي الكمية (كجم)"] = gQty.ToString("N1");
        r.Summary["أُفرج عنه (كجم)"] = gRel.ToString("N1");
        r.Summary["مرفوض (كجم)"] = gRej.ToString("N1");
        if (gQty > 0) r.Summary["نسبة الرفض الإجمالية"] = $"{gRej / gQty * 100:N2}%";
        return r;
    }
}
