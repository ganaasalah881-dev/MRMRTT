using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §المعالجة والتعقيم — المرحلة 2: منطق دورة المعالجة وحركة المخزون.
///
/// ### قاعدة الكميات التي تحكم كل دالة هنا
/// <c>InStockQtyKg</c> **لا يتغير** عند البدء ولا عند الإفراج: الكمية لم تدخل ولم
/// تخرج من المنشأة، بل انتقلت بين مستودعين. تغييرها كان سيوهم بنقص في المخزون.
/// يتغير عند الرفض/الإتلاف وحده. ومن ثم يبقى الثابت:
/// <c>رصيد(WRM) + رصيد(WTRT) = InStockQtyKg</c>
///
/// ### التوجيه بالقدرة لا بالمسمى
/// Create بدء · Approve إفراج · Cancel رفض/إلغاء · View عرض. من يملك القدرة ينفّذ
/// أياً كان مسماه الوظيفي.
/// </summary>
public class RawTreatmentService : ServiceBase, IRawTreatmentService
{
    private const string Module = PermissionModules.Treatment;

    public RawTreatmentService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    // ═══════════════════ البدء ═══════════════════

    public OpResult Start(TreatmentStartDto dto)
    {
        Require(Module, "Create");
        if (dto == null) return OpResult.Fail("لا توجد بيانات.");
        if (dto.QtyKg <= 0) return OpResult.Fail("الكمية يجب أن تكون أكبر من صفر.");

        var lot = Db.Lots.FirstOrDefault(l => l.Id == dto.LotId);
        if (lot == null) return OpResult.Fail("الدفعة غير موجودة.");

        return RunOp(() =>
        {
            var type = dto.TreatmentTypeId != null
                ? Db.TreatmentTypes.FirstOrDefault(t => t.Id == dto.TreatmentTypeId)
                : null;

            if (Db.ShipmentItems.Any(i => i.Id == lot.ShipmentItemId && i.TreatmentRequired != null))
                throw new DomainException("تُدار معالجة هذا البند حصراً من سند الاستلام؛ لا بدء منفصل أو مدة بديلة.");

            double hours = dto.DurationHours ?? type?.DefaultDurationHours ?? 0;
            var startedAt = dto.StartedAt ?? DateTime.Now;

            // §B107 — الجوهر المشترك في ServiceBase: نفس المنطق الذي يستدعيه اعتماد
            // سند الاستلام حين تكون وجهة البند «مستودع المعالجة». نسخة واحدة لا نسختان.
            var t = StartTreatmentCore(lot, dto.TreatmentTypeId, dto.QtyKg, dto.PackageCount,
                startedAt, hours, dto.ResponsibleUserId, dto.Notes);

            return OpResult.Success(
                $"بدأت المعالجة على {dto.QtyKg:N1} كجم من الدفعة {lot.LotCode}.\n"
                + $"⏱ الجاهزية المتوقعة: {t.ExpectedReadyAt:dd/MM/yyyy HH:mm}"
                + $" (بعد {FormatDuration(hours)}).",
                t.Id, t.TreatmentNo);
        });
    }

    // ═══════════════════ الإفراج ═══════════════════

    /// <summary>
    /// الإفراج عن كمية من المعالجة إلى وجهتها.
    /// §B106: كانت الوجهة **مخزن الخام إجباراً**. صارت قابلة للاختيار (قرار المستخدم:
    /// «ثم تخرج لمخزن التام أو لخط الإنتاج مباشرة»):
    ///   • <c>"WRM"</c> — مخزن الخام (الافتراضي، وهو السلوك السابق).
    ///   • <c>"WFG"</c> — مخزن التام مباشرة، للبضاعة التي لا تمر بخط الإنتاج.
    /// الصرف المباشر لخط الإنتاج يمر بأمر إنتاج (صرف المواد) ولا يُقيَّد كحركة مخزن هنا.
    /// </summary>
    public OpResult Release(int treatmentId, double qtyKg, string notes = null, string destWarehouseCode = "WRM")
    {
        Require(Module, "Approve");
        var t = Db.RawTreatments.FirstOrDefault(x => x.Id == treatmentId);
        if (t == null) return OpResult.Fail("عملية المعالجة غير موجودة.");
        if (t.ReceivingItemId != null)
            return OpResult.Fail("هذا بند استلام بموعد محدد: تُحدّث حالته آلياً عند حلول حتى تاريخ؛ لا إفراج أو إلغاء أو تجاوز يدوي.");

        // §الوجهة محصورة في مخزنين معلومين — لا يُمرَّر كود حر إلى حركة مخزون.
        string dest = string.IsNullOrWhiteSpace(destWarehouseCode) ? "WRM" : destWarehouseCode.Trim().ToUpperInvariant();
        if (dest != "WRM" && dest != "WFG")
            return OpResult.Fail("وجهة الإفراج غير صالحة — المسموح: مخزن الخام (WRM) أو مخزن التام (WFG).");


        return RunOp(() =>
        {
            if (t.Status != TreatmentStatuses.InProgress)
                throw new DomainException($"لا يمكن الإفراج: حالة العملية «{TreatmentStatuses.ToArabic(t.Status)}».");
            if (qtyKg <= 0) throw new DomainException("كمية الإفراج يجب أن تكون أكبر من صفر.");
            if (qtyKg > t.RemainingQtyKg + 0.001)
                throw new DomainException(
                    $"كمية الإفراج ({qtyKg:N1} كجم) تتجاوز المتبقي في العملية ({t.RemainingQtyKg:N1} كجم).");

            // §الوقت شرط ضروري: لا إفراج قبل اكتمال المدة. هذا حارس المنع الحقيقي،
            // هذا للمحرك التاريخي فقط؛ بنود التاريخ الجديد يكمّلها ReceivingTreatmentLifecycle.
            if (t.ExpectedReadyAt > Db.BusinessNow)
                throw new DomainException(
                    $"لم تكتمل مدة المعالجة بعد.\nالجاهزية المتوقعة: {t.ExpectedReadyAt:dd/MM/yyyy HH:mm}"
                    + $" — المتبقي: {FormatDuration((t.ExpectedReadyAt - DateTime.Now).TotalHours)}.");

            // §فحص الجودة حسب نوع المعالجة (قرار المستخدم س4) — لا لكل الأنواع
            var type = t.TreatmentTypeId != null
                ? Db.TreatmentTypes.FirstOrDefault(x => x.Id == t.TreatmentTypeId) : null;
            if (type != null && type.RequiresQualityCheck && !HasPassedQuality(t))
                throw new DomainException(
                    $"نوع المعالجة «{type.TypeNameAr}» يشترط فحص جودة معتمداً قبل الإفراج.\n"
                    + $"سجّل فحصاً ناجحاً للدفعة أولاً.");

            var lot = Db.Lots.First(l => l.Id == t.LotId);
            int packages = ProportionalPackages(t, qtyKg);

            // §رقم مرجعي فريد لكل إفراج: حارس التكرار في PostStockMovement يقارن
            // (المستند + النوع + المخزن + الدفعة)، فلو تكرر رقم العملية لرُفض الإفراج
            // الجزئي الثاني بوصفه تكراراً — وهو إفراج مشروع لا تكرار.
            string refNo = $"{t.TreatmentNo}/R{ReleaseSeq(t)}";

            MoveStock(WarehouseId("WTRT"), MovementType.Outbound, t, ReferenceDocType.TreatmentRelease,
                refNo, qtyKg, packages, lot, notes ?? $"إفراج من معالجة {t.TreatmentNo}");
            MoveStock(WarehouseId(dest), MovementType.Inbound, t, ReferenceDocType.TreatmentRelease,
                refNo, qtyKg, packages, lot,
                notes ?? $"إفراج من معالجة {t.TreatmentNo} إلى {(dest == "WFG" ? "مخزن التام" : "مخزن الخام")}");

            t.ReleasedQtyKg += qtyKg;
            lot.UnderTreatmentQtyKg = Math.Max(0, lot.UnderTreatmentQtyKg - qtyKg);
            // §B106: «جاهز للإنتاج» رصيدُ خامٍ متاح للتصنيع. فإن خرجت الكمية إلى مخزن
            // التام مباشرةً فهي لم تعد خاماً متاحاً، وزيادته هنا كانت ستحتسبها مرتين.
            if (dest == "WRM") lot.TreatmentReadyQtyKg += qtyKg;

            bool finished = t.RemainingQtyKg <= 0.001;
            if (finished)
            {
                t.Status = TreatmentStatuses.Released;
                t.CompletedAt = DateTime.Now;
            }
            if (!string.IsNullOrWhiteSpace(notes))
                t.Notes = string.IsNullOrWhiteSpace(t.Notes) ? notes : t.Notes + "\n" + notes;
            Db.SaveChanges();

            return OpResult.Success(
                finished
                    ? $"اكتملت المعالجة {t.TreatmentNo}: أُفرج عن {qtyKg:N1} كجم — الكمية جاهزة للإنتاج."
                    : $"إفراج جزئي: {qtyKg:N1} كجم جاهزة للإنتاج، وتبقّى {t.RemainingQtyKg:N1} كجم تحت المعالجة.",
                t.Id, t.TreatmentNo);
        });
    }

    // ═══════════════════ الرفض ═══════════════════

    public OpResult Reject(int treatmentId, double qtyKg, string reason)
    {
        Require(Module, "Cancel");
        var t = Db.RawTreatments.FirstOrDefault(x => x.Id == treatmentId);
        if (t == null) return OpResult.Fail("عملية المعالجة غير موجودة.");
        if (t.ReceivingItemId != null)
            return OpResult.Fail("هذا بند استلام بموعد محدد: تُحدّث حالته آلياً عند حلول حتى تاريخ؛ لا إفراج أو إلغاء أو تجاوز يدوي.");
        if (string.IsNullOrWhiteSpace(reason)) return OpResult.Fail("سبب الرفض إلزامي.");

        return RunOp(() =>
        {
            if (t.Status != TreatmentStatuses.InProgress)
                throw new DomainException($"لا يمكن الرفض: حالة العملية «{TreatmentStatuses.ToArabic(t.Status)}».");
            if (qtyKg <= 0) throw new DomainException("كمية الرفض يجب أن تكون أكبر من صفر.");
            if (qtyKg > t.RemainingQtyKg + 0.001)
                throw new DomainException(
                    $"كمية الرفض ({qtyKg:N1} كجم) تتجاوز المتبقي في العملية ({t.RemainingQtyKg:N1} كجم).");

            var lot = Db.Lots.First(l => l.Id == t.LotId);
            int packages = ProportionalPackages(t, qtyKg);
            string refNo = $"{t.TreatmentNo}/X{RejectSeq(t)}";

            // §المرفوض يخرج من مستودع المعالجة ولا يعود للخام — هدر لا مخزون
            MoveStock(WarehouseId("WTRT"), MovementType.Outbound, t, ReferenceDocType.TreatmentRelease,
                refNo, qtyKg, packages, lot, $"رفض من معالجة {t.TreatmentNo}: {reason}");

            t.RejectedQtyKg += qtyKg;
            lot.UnderTreatmentQtyKg = Math.Max(0, lot.UnderTreatmentQtyKg - qtyKg);
            // §هنا وحده ينقص المخزون: الكمية أُتلفت وغادرت المنشأة فعلاً
            lot.InStockQtyKg = Math.Max(0, lot.InStockQtyKg - qtyKg);
            lot.WastageQtyKg += qtyKg;

            if (t.RemainingQtyKg <= 0.001)
            {
                t.Status = t.ReleasedQtyKg > 0 ? TreatmentStatuses.Released : TreatmentStatuses.Rejected;
                t.CompletedAt = DateTime.Now;
            }
            t.Notes = string.IsNullOrWhiteSpace(t.Notes) ? $"رفض: {reason}" : t.Notes + $"\nرفض: {reason}";
            Db.SaveChanges();

            return OpResult.Success(
                $"رُفضت {qtyKg:N1} كجم من المعالجة {t.TreatmentNo} وسُجّلت هدراً.", t.Id, t.TreatmentNo);
        });
    }

    // ═══════════════════ الإلغاء ═══════════════════

    public OpResult Cancel(int treatmentId, string reason)
    {
        Require(Module, "Cancel");
        var t = Db.RawTreatments.FirstOrDefault(x => x.Id == treatmentId);
        if (t == null) return OpResult.Fail("عملية المعالجة غير موجودة.");
        if (t.ReceivingItemId != null)
            return OpResult.Fail("هذا بند استلام بموعد محدد: تُحدّث حالته آلياً عند حلول حتى تاريخ؛ لا إفراج أو إلغاء أو تجاوز يدوي.");

        return RunOp(() =>
        {
            if (t.Status != TreatmentStatuses.InProgress)
                throw new DomainException($"لا يمكن الإلغاء: حالة العملية «{TreatmentStatuses.ToArabic(t.Status)}».");
            // §الإلغاء لتصحيح خطأ إدخال فقط. بعد أي إفراج أو رفض صارت للعملية آثار
            // محاسبية، وعكسها الجزئي يفسد التتبع — يُصحَّح بحركة جديدة لا بمحو القديمة.
            if (t.ReleasedQtyKg > 0.001 || t.RejectedQtyKg > 0.001)
                throw new DomainException(
                    "لا يمكن إلغاء عملية أُفرج أو رُفض منها كمية — استخدم الإفراج أو الرفض للمتبقي.");

            var lot = Db.Lots.First(l => l.Id == t.LotId);
            string refNo = $"{t.TreatmentNo}/C";

            // عكس كامل لقيدَي البدء
            MoveStock(WarehouseId("WTRT"), MovementType.Outbound, t, ReferenceDocType.TreatmentRelease,
                refNo, t.QtyKg, t.PackageCount, lot, $"إلغاء بدء معالجة {t.TreatmentNo}: {reason}");
            MoveStock(WarehouseId("WRM"), MovementType.Inbound, t, ReferenceDocType.TreatmentRelease,
                refNo, t.QtyKg, t.PackageCount, lot, $"إلغاء بدء معالجة {t.TreatmentNo}: {reason}");

            lot.UnderTreatmentQtyKg = Math.Max(0, lot.UnderTreatmentQtyKg - t.QtyKg);
            t.Status = TreatmentStatuses.Cancelled;
            t.CompletedAt = DateTime.Now;
            t.Notes = string.IsNullOrWhiteSpace(t.Notes) ? $"إلغاء: {reason}" : t.Notes + $"\nإلغاء: {reason}";
            Db.SaveChanges();

            return OpResult.Success($"أُلغي بدء المعالجة {t.TreatmentNo} وعادت الكمية إلى الخام.", t.Id);
        });
    }

    // ═══════════════════ القراءة ═══════════════════

    public List<RawTreatment> GetByLot(int lotId)
    {
        Require(Module, "View");
        return Db.RawTreatments.AsNoTracking()
            .Where(t => t.LotId == lotId)
            .OrderBy(t => t.StartedAt).ThenBy(t => t.Id)
            .ToList();
    }

    public List<TreatmentRowDto> Search(string status = null, bool onlyOverdue = false, int? productId = null)
    {
        Require(Module, "View");
        var q = Db.RawTreatments.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(t => t.Status == status);
        if (productId != null) q = q.Where(t => t.ProductId == productId);

        // §المتأخرة تُفلتر بعد التحقيق: IsOverdue خاصية محسوبة لا تُترجم إلى SQL (فخ B64)
        var now = DateTime.Now;
        if (onlyOverdue)
            q = q.Where(t => t.Status == TreatmentStatuses.InProgress && t.ExpectedReadyAt < now);

        var rows = q.OrderBy(t => t.ExpectedReadyAt).ToList();
        var lots = Db.Lots.AsNoTracking().ToDictionary(l => l.Id, l => l.LotCode);
        var prods = Db.Products.AsNoTracking().ToDictionary(p => p.Id, p => p.ProductNameAr);
        var types = Db.TreatmentTypes.AsNoTracking().ToDictionary(x => x.Id, x => x.TypeNameAr);
        var users = Db.Users.AsNoTracking().ToDictionary(u => u.Id, u => u.FullName);

        return rows.Select(t => new TreatmentRowDto
        {
            Id = t.Id,
            TreatmentNo = t.TreatmentNo,
            LotId = t.LotId,
            LotCode = lots.TryGetValue(t.LotId, out var lc) ? lc : "-",
            ProductName = prods.TryGetValue(t.ProductId, out var pn) ? pn : "-",
            TreatmentTypeName = t.TreatmentTypeId != null && types.TryGetValue(t.TreatmentTypeId.Value, out var tn) ? tn : "-",
            QtyKg = t.QtyKg,
            PackageCount = t.PackageCount,
            StartedAt = t.StartedAt,
            DurationHours = t.DurationHours,
            ExpectedReadyAt = t.ExpectedReadyAt,
            CompletedAt = t.CompletedAt,
            ReleasedQtyKg = t.ReleasedQtyKg,
            RejectedQtyKg = t.RejectedQtyKg,
            RemainingQtyKg = t.RemainingQtyKg,
            Status = t.Status,
            StatusAr = TreatmentStatuses.ToArabic(t.Status),
            IsOverdue = t.IsOverdue,
            IsReadyByTime = t.IsReadyByTime,
            ResponsibleName = t.ResponsibleUserId != null && users.TryGetValue(t.ResponsibleUserId.Value, out var un) ? un : "-",
            Notes = t.Notes
        }).ToList();
    }

    public LotTreatmentStateDto GetLotState(int lotId)
    {
        Require(Module, "View");
        var lot = Db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == lotId);
        if (lot == null) return null;

        double rejected = Db.RawTreatments.AsNoTracking()
            .Where(t => t.LotId == lotId).Sum(t => (double?)t.RejectedQtyKg) ?? 0;

        return new LotTreatmentStateDto
        {
            LotId = lot.Id,
            LotCode = lot.LotCode,
            InStockQtyKg = lot.InStockQtyKg,
            UnderTreatmentQtyKg = lot.UnderTreatmentQtyKg,
            ReadyQtyKg = lot.TreatmentReadyQtyKg,
            RejectedQtyKg = rejected,
            // 🔵 المستلم الذي لم يدخل المعالجة قط
            NotTreatedQtyKg = Math.Max(0, lot.InStockQtyKg - lot.UnderTreatmentQtyKg - lot.TreatmentReadyQtyKg),
            ReservedQtyKg = lot.ReservedQtyKg,
            AvailableQtyKg = lot.AvailableQtyKg,
            RequiresTreatment = LotRequiresTreatment(lot)
        };
    }

    public double GetAvailableForDate(int lotId, DateTime forDate)
    {
        EnsureReceiptTreatmentsCurrent();
        var lot = Db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == lotId);
        if (lot == null) return 0;

        var until = Db.ShipmentItems.Where(i => i.Id == lot.ShipmentItemId && i.TreatmentRequired == true)
            .Select(i => i.TreatmentUntilDate).FirstOrDefault();
        if (until != null && forDate.Date < until.Value.Date) return 0;
        bool requires = LotRequiresTreatment(lot);

        // §الصنف الذي لا يشترط معالجة: المتاح هو المخزون غير المحجوز كالسابق تماماً
        // (قرار المستخدم س3) — وإلا عُطّلت خطوط إنتاج لا علاقة لها بالتعقيم.
        if (!requires)
            return Math.Max(0, lot.InStockQtyKg - lot.ReservedQtyKg - lot.UnderTreatmentQtyKg);

        // §المتاح في تاريخ D = الجاهز الآن + ما تكتمل معالجته حتى D − المحجوز
        var end = forDate.Date.AddDays(1).AddTicks(-1);
        double maturing = Db.RawTreatments.AsNoTracking()
            .Where(t => t.LotId == lotId
                     && t.Status == TreatmentStatuses.InProgress
                     && t.ExpectedReadyAt <= end)
            .Sum(t => (double?)(t.QtyKg - t.ReleasedQtyKg - t.RejectedQtyKg)) ?? 0;

        return Math.Max(0, lot.TreatmentReadyQtyKg + Math.Max(0, maturing) - lot.ReservedQtyKg - lot.ProducedQtyKg);
    }

    // ═══════════════════ مساعدات ═══════════════════

    private void MoveStock(int warehouseId, MovementType movement, RawTreatment t,
        ReferenceDocType refType, string refNo, double qtyKg, int packages, Lot lot, string notes)
    {
        PostStockMovement(warehouseId, movement, qtyKg, packages, refType, refNo,
            productId: t.ProductId, lotId: lot.Id, customerId: lot.CustomerId,
            packagingTypeId: lot.PackagingTypeId, notes: notes);
    }

    /// <summary>عدد الطرود بنسبة الكمية — الحساب بالكيلو والعرض بوحدة الاستلام.</summary>
    private static int ProportionalPackages(RawTreatment t, double qtyKg)
        => t.QtyKg <= 0 ? 0 : (int)Math.Round(t.PackageCount * (qtyKg / t.QtyKg), MidpointRounding.AwayFromZero);

    private int ReleaseSeq(RawTreatment t) => 1 + Db.InventoryTransactions
        .Count(x => x.ReferenceDocType == ReferenceDocType.TreatmentRelease
                 && x.ReferenceDocNumber.StartsWith(t.TreatmentNo + "/R")
                 && x.MovementType == MovementType.Inbound);

    private int RejectSeq(RawTreatment t) => 1 + Db.InventoryTransactions
        .Count(x => x.ReferenceDocType == ReferenceDocType.TreatmentRelease
                 && x.ReferenceDocNumber.StartsWith(t.TreatmentNo + "/X"));

    /// <summary>
    /// فحص جودة ناجح للدفعة بعد بدء المعالجة — يُشترط فقط لأنواع المعالجة التي
    /// تطلبه (قرار المستخدم س4).
    ///
    /// **الربط بالدفعة عبر <see cref="QualityCheckItem"/> لا عبر رأس المحضر**:
    /// LotId يقع على البند لأن المحضر الواحد يغطي عدة دفعات. والقرار في الحقل
    /// Decision بقيمة "Passed" — لا يوجد حقل Result في هذا النموذج.
    /// </summary>
    private bool HasPassedQuality(RawTreatment t)
        => Db.QualityCheckItems.AsNoTracking()
            .Where(i => i.LotId == t.LotId)
            .Join(Db.QualityChecks.AsNoTracking(), i => i.CheckId, q => q.Id, (i, q) => q)
            .Any(q => q.Decision == "Passed"
                   && q.Status == DocStatuses.Approved
                   && (q.CheckDate == null || q.CheckDate >= t.StartedAt));

    private static string FormatDuration(double hours)
    {
        if (hours <= 0) return "0 ساعة";
        int days = (int)(hours / 24);
        double rem = hours - days * 24;
        if (days > 0 && rem < 0.01) return $"{days} يوم";
        if (days > 0) return $"{days} يوم و{rem:N0} ساعة";
        return $"{hours:N0} ساعة";
    }
}
