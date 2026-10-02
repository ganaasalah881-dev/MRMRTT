using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §الخطة الطويلة متعددة العملاء والأيام:
/// خطة واحدة → أيام → وردية → عميل → صنف → كمية، مع تقدم مستقل لكل بند ولكل عميل،
/// خطة يومية لمدير الإنتاج، تعديل مرن للأيام المستقبلية مع إعادة فحص الطاقة،
/// وفوترة على المسلَّم فعلياً فقط دون تكرار.
/// </summary>
public class PlanProgressService : ServiceBase, IPlanProgressService
{
    public PlanProgressService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    private static string ExecStatusAr(string s) => s switch
    {
        "Completed" => "مكتمل ✅",
        "Partial" => "جزئي 🟠",
        "InProgress" => "قيد التنفيذ 🏭",
        _ => "لم يبدأ ⏳"
    };

    private static string ItemExecStatus(ProductionPlanItem i)
    {
        if (i.ProducedQtyKg <= 0 && i.DeliveredQtyKg <= 0) return "NotStarted";
        if (i.ProducedQtyKg + 0.001 >= i.PlannedQtyKg && i.DeliveredQtyKg + 0.001 >= i.PlannedQtyKg) return "Completed";
        if (i.DeliveredQtyKg > 0) return "Partial";
        return "InProgress";
    }

    private (double rate, int max) CapacityFor(int productId, int shiftId)
    {
        // §مصدر واحد: CapacityPolicy (ولا افتراض 8 ساعات عند غياب الوردية)
        var (r, c, _) = CapacityPolicy.Resolve(Db, productId, shiftId);
        return (r, c);
    }

    /// <summary>الساعات المستهلكة في يوم+وردية+خط عبر كل الخطط (للحسابات اللحظية).</summary>
    private double DayShiftUsedHours(int shiftId, int lineId, DateTime day, int? excludeItemId)
    {
        var dayEnd = day.Date.AddDays(1);
        var items = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.ScheduledDate != null && i.ScheduledDate >= day.Date && i.ScheduledDate < dayEnd
                        && (i.SuggestedShiftId ?? 1) == shiftId && (i.SuggestedLineId ?? 1) == lineId
                        && i.Id != (excludeItemId ?? -1)
                        && !i.IsClosed) // §B85/H6: البند المقفل لا يشغل طاقة
            .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
            // §B85/H6: الخطة المقفلة/الملغاة لا تشغل طاقة
            .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
            .Select(x => x.i)
            .ToList();
        double used = 0;
        foreach (var it in items)
        {
            var (rate, _) = CapacityFor(it.ProductId, shiftId);
            // §لا وزن كرتون ثابت: الساعات تُحسب من الكراتين والمعدل فقط، وإن غاب المعدل
            // فلا تُحتسب ساعات (والخطة تُرفض أصلاً عند الحفظ لعدم تعريف الطاقة).
            if (rate > 0 && it.PlannedCartons > 0) used += it.PlannedCartons / rate;
        }
        return used;
    }

    public List<PlanRowDto> GetDailyPlan(string date, int? planId = null)
    {
        if (!UiFormat.TryParseDate(date, out var day)) return new List<PlanRowDto>();
        var dayEnd = day.Date.AddDays(1);
        var q = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.ScheduledDate != null && i.ScheduledDate >= day.Date && i.ScheduledDate < dayEnd);
        if (planId != null) q = q.Where(i => i.PlanId == planId);
        var items = q.OrderBy(i => i.SuggestedShiftId).ThenBy(i => i.PriorityNo).ToList();

        return items.Select(i =>
        {
            int shiftId = i.SuggestedShiftId ?? 1;
            int lineId = i.SuggestedLineId ?? 1;
            var (rate, max) = CapacityFor(i.ProductId, shiftId);
            // §لا وزن كرتون ثابت: الساعات من الكراتين والمعدل فقط — ولا تحويل من الكيلو بوزن مخترع
            double req = rate > 0 && i.PlannedCartons > 0 ? i.PlannedCartons / rate : 0;
            double used = DayShiftUsedHours(shiftId, lineId, day, i.Id);
            var shift = Db.Shifts.AsNoTracking().FirstOrDefault(s => s.Id == shiftId);
            double eff = CapacityPolicy.EffectiveHours(shift?.EffectiveProductiveHours ?? 0, shift?.TotalHours ?? 0);
            return new PlanRowDto
            {
                ItemId = i.Id,
                PlanId = i.PlanId,
                PlanNumber = Db.ProductionPlans.AsNoTracking().Where(p => p.Id == i.PlanId).Select(p => p.DocumentNumber).FirstOrDefault(),
                Date = i.ScheduledDate?.ToString("dd/MM/yyyy"),
                ShiftId = shiftId,
                ShiftName = shift?.ShiftNameAr ?? "-",
                CustomerId = i.CustomerId,
                CustomerName = Db.Customers.AsNoTracking().Where(c => c.Id == i.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "—",
                LotCode = Db.Lots.AsNoTracking().Where(l => l.Id == i.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "—",
                ShipmentNo = Db.Shipments.AsNoTracking().Where(s => s.Id == i.ShipmentId).Select(s => s.DocumentNumber).FirstOrDefault() ?? "—",
                ProductName = Db.Products.AsNoTracking().Where(p => p.Id == i.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                PackName = Db.PackagingTypes.AsNoTracking().Where(p => p.Id == i.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault() ?? "-",
                PlannedKg = i.PlannedQtyKg,
                PlannedCartons = i.PlannedCartons,
                ProducedKg = i.ProducedQtyKg,
                AcceptedKg = i.AcceptedQtyKg,
                DeliveredKg = i.DeliveredQtyKg,
                RemainingKg = Math.Max(0, i.PlannedQtyKg - i.DeliveredQtyKg),
                ExecStatusAr = ExecStatusAr(ItemExecStatus(i)),
                RatePerHour = rate,
                MaxCapacity = max,
                RequiredHours = Math.Round(req, 2),
                HoursUsedOnDay = Math.Round(used + req, 2),
                HoursRemainingOnDay = Math.Round(Math.Max(0, eff - used - req), 2)
            };
        }).ToList();
    }

    public List<CustomerProgressDto> GetPlanProgressByCustomer(int planId)
    {
        var items = Db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == planId).ToList();
        return items.GroupBy(i => i.CustomerId ?? 0).Select(g =>
        {
            double planned = g.Sum(i => i.PlannedQtyKg);
            double produced = g.Sum(i => i.ProducedQtyKg);
            double accepted = g.Sum(i => i.AcceptedQtyKg);
            double delivered = g.Sum(i => i.DeliveredQtyKg);
            string status = delivered + 0.001 >= planned && planned > 0 ? "مكتمل ✅"
                          : produced > 0 || delivered > 0 ? "جزئي 🟠" : "لم يبدأ ⏳";
            return new CustomerProgressDto
            {
                CustomerId = g.Key,
                CustomerName = Db.Customers.AsNoTracking().Where(c => c.Id == g.Key).Select(c => c.CustomerName).FirstOrDefault() ?? "بدون عميل",
                Planned = planned,
                Produced = produced,
                Accepted = accepted,
                Delivered = delivered,
                Remaining = Math.Max(0, planned - delivered),
                StatusAr = status
            };
        }).OrderByDescending(x => x.Planned).ToList();
    }

    public List<DayStatusDto> GetPlanDayStatuses(int planId)
    {
        var items = Db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == planId && i.ScheduledDate != null).ToList();
        return items.GroupBy(i => i.ScheduledDate.Value.Date)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                double planned = g.Sum(i => i.PlannedQtyKg);
                double produced = g.Sum(i => i.ProducedQtyKg);
                double delivered = g.Sum(i => i.DeliveredQtyKg);
                string status = (delivered + 0.001 >= planned && planned > 0) ? "مكتمل ✅"
                              : (produced > 0 || delivered > 0) ? "جزئي 🟠" : "غير مكتمل ⏳";
                return new DayStatusDto
                {
                    Date = g.Key.ToString("dd/MM/yyyy"),
                    RowsCount = g.Count(),
                    PlannedKg = planned,
                    ProducedKg = produced,
                    StatusAr = status
                };
            }).ToList();
    }

    /// <summary>§B102 (سحب B98) — سجل التنفيذ اليومي: مخطط/مأمور/منتج/مقبول/مسلَّم + حالة اليوم الواضحة.</summary>
    public List<ExecutionLogDayDto> GetExecutionLog(int planId)
    {
        var items = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.PlanId == planId && i.ScheduledDate != null).ToList();
        var itemIds = items.Select(i => i.Id).ToList();
        var orderedByItem = Db.ProductionOrderItems.AsNoTracking()
            .Where(x => x.PlanItemId != null && itemIds.Contains(x.PlanItemId.Value))
            .Join(Db.ProductionOrders.AsNoTracking(), x => x.OrderId, o => o.Id, (x, o) => new { x, o })
            .Where(z => z.o.Status != DocStatuses.Cancelled)
            .GroupBy(z => z.x.PlanItemId)
            .ToDictionary(g => g.Key.Value, g => g.Sum(z => z.x.PlannedQtyKg));
        var today = DateTime.Today;

        return items.GroupBy(i => i.ScheduledDate.Value.Date)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                double planned = g.Sum(i => i.PlannedQtyKg);
                double produced = g.Sum(i => i.ProducedQtyKg);
                double accepted = g.Sum(i => i.AcceptedQtyKg);
                double delivered = g.Sum(i => i.DeliveredQtyKg);
                double ordered = g.Sum(i => orderedByItem.TryGetValue(i.Id, out var q) ? q : 0.0);
                bool isPast = g.Key < today;

                string status;
                if (planned <= 0) status = "—";
                else if (delivered + 0.001 >= planned) status = "مكتمل ✅";
                else if (produced + 0.001 >= planned) status = "مُنتَج — بانتظار التسليم 🟡";
                else if (produced > 0) status = isPast ? "جزئي 🟠" : "قيد التنفيذ 🔵";
                else if (ordered > 0) status = isPast ? "متعثر ⏰" : "بانتظار التشغيل ⚡";
                else status = isPast ? "متعثر ⏰" : "لم يبدأ ⚪";

                return new ExecutionLogDayDto
                {
                    Date = g.Key.ToString("dd/MM/yyyy"),
                    RowsCount = g.Count(),
                    PlannedKg = planned,
                    OrderedKg = ordered,
                    ProducedKg = produced,
                    AcceptedKg = accepted,
                    DeliveredKg = delivered,
                    Overdue = isPast && !(delivered + 0.001 >= planned && planned > 0),
                    StatusAr = status
                };
            }).ToList();
    }

    protected override System.Data.IsolationLevel TransactionIsolation => System.Data.IsolationLevel.Serializable;

    public OpResult UpdatePlanItem(int itemId, string newDate = null, double? newQtyKg = null, int? newShiftId = null, int? newCustomerId = null,
        int? newProductId = null, int? newPackagingTypeId = null)
    {
        Require("planning", "Edit");
        return RunOp(() => UpdatePlanItemCore(itemId, newDate, newQtyKg, newShiftId, newCustomerId, newProductId, newPackagingTypeId));
    }

    private OpResult UpdatePlanItemCore(int itemId, string newDate = null, double? newQtyKg = null, int? newShiftId = null, int? newCustomerId = null,
        int? newProductId = null, int? newPackagingTypeId = null)
    {
        var item = Db.ProductionPlanItems.FirstOrDefault(i => i.Id == itemId);
        if (item == null) return OpResult.Fail("البند غير موجود.");
        var plan = Db.ProductionPlans.FirstOrDefault(p => p.Id == item.PlanId);
        if (plan == null) return OpResult.Fail("الخطة غير موجودة.");
        if (plan.IsClosed) return OpResult.Fail("الخطة مقفلة — لا يمكن التعديل.");
        if (Db.ProductionOrderItems.Any(o => o.PlanItemId == item.Id))
            return OpResult.Fail("صدر أمر إنتاج لهذا البند؛ لا تعديل لكمية الخطة أو هويتها أو جدولتها أثناء التنفيذ. يلزم إجراء تخطيط رسمي يحفظ أثر الأوامر السابقة.");
        if (plan.IsApproved) return OpResult.Fail("الخطة معتمدة ومقفلة — فك الاعتماد أولاً (بصلاحية، وبعد إلغاء الحركات اللاحقة) ثم عدّل.");

        // §3 — الأيام المنفذة أو المقفلة لا تُعدل إلا بالصلاحية المناسبة
        string exec = ItemExecStatus(item);
        bool executed = exec != "NotStarted" || (item.ScheduledDate != null && item.ScheduledDate.Value.Date < DateTime.Today);
        if (executed && !Session.Can("planning", "Cancel"))
            return OpResult.Fail("هذا البند منفَّذ أو تاريخه مضى — تعديله يتطلب صلاحية إدارية (إلغاء/استثناء).");

        DateTime targetDate = item.ScheduledDate ?? DateTime.Today;
        if (newDate != null)
        {
            if (!UiFormat.TryParseDate(newDate, out var nd)) return OpResult.Fail("تاريخ غير صالح.");
            targetDate = nd.Date;
        }
        // §B80: تاريخ إنتاج البند يبقى مفروضاً داخل فترة الخطة — في كل مسارات التعديل
        if (plan.StartDate != null && targetDate < plan.StartDate.Value.Date)
            return OpResult.Fail($"التاريخ الجديد ({targetDate:dd/MM/yyyy}) قبل بداية فترة الخطة ({plan.StartDate.Value.Date:dd/MM/yyyy}).");
        if (plan.EndDate != null && targetDate > plan.EndDate.Value.Date)
            return OpResult.Fail($"التاريخ الجديد ({targetDate:dd/MM/yyyy}) بعد نهاية فترة الخطة ({plan.EndDate.Value.Date:dd/MM/yyyy}).");
        int targetShift = newShiftId ?? item.SuggestedShiftId ?? 1;
        double targetQty = newQtyKg ?? item.PlannedQtyKg;
        if (targetQty <= 0) return OpResult.Fail("الكمية يجب أن تكون أكبر من صفر.");

        // §تغيير الصنف/العبوة أثناء الخطة (اشتراطات العملاء المتغيرة): تحقق ثم أعد الحساب والفحص
        int targetProduct = newProductId ?? item.ProductId;
        int? targetPack = newPackagingTypeId ?? item.PackagingTypeId;
        if (newProductId != null && newProductId.Value != item.ProductId
            && !Db.Products.Any(p => p.Id == newProductId.Value && p.IsActive))
            return OpResult.Fail("الصنف الجديد غير موجود أو موقوف.");

        // §تتبع الصنف — قفل الهوية: لا يجوز تغيير صنف البند بعد وجود عمليات مرتبطة به
        if (newProductId != null && newProductId.Value != item.ProductId)
        {
            bool hasOps = item.ProducedQtyKg > 0 || item.AcceptedQtyKg > 0 || item.DeliveredQtyKg > 0
                || Db.ProductionOrderItems.Any(o => o.PlanItemId == item.Id)
                || Db.PlanClosingItems.Any(c => c.PlanItemId == item.Id);
            if (hasOps)
            {
                string oldName = Db.Products.AsNoTracking().Where(p => p.Id == item.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-";
                string newName = Db.Products.AsNoTracking().Where(p => p.Id == newProductId.Value).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-";
                return OpResult.Fail(
                    $"⛔ لا يمكن تغيير هوية الصنف بعد وجود عمليات مرتبطة بالبند.\n" +
                    $"البند: «{oldName}» ← توجد عمليات (إنتاج/فحص/تسليم/أوامر) — لا يمكن تحويله إلى «{newName}».\n" +
                    $"تغيير الصنف بعد التنفيذ يتم بإنشاء تعديل/إلغاء حسب الصلاحيات مع الاحتفاظ بالسجل السابق.");
            }
            // الصنف الجديد يجب أن يلتزم بالتحويل الرسمي لدفعة البند إن وُجدت
            string convErr = ProductIdentityGuard.CheckConversion(Db, newProductId.Value, item.LotId);
            if (convErr != null) return OpResult.Fail(convErr);
        }
        if (newPackagingTypeId != null && newPackagingTypeId.Value != item.PackagingTypeId
            && !Db.PackagingTypes.Any(p => p.Id == newPackagingTypeId.Value))
            return OpResult.Fail("العبوة الجديدة غير موجودة.");

        ProductIdentityGuard.EnsurePlanningLink(Db, targetProduct, item.LotId,
            item.LotId == null ? Db.Products.AsNoTracking().Where(p => p.Id == item.ProductId).Select(p => p.SourceProductId).FirstOrDefault() : null);

        // إعادة احتساب الكراتين عند أي تغيير في الكمية أو الصنف أو العبوة
        bool recompute = newQtyKg != null || newProductId != null || newPackagingTypeId != null;
        double packWeight = targetPack != null
            ? Db.PackagingTypes.AsNoTracking().Where(p => p.Id == targetPack.Value).Select(p => p.UnitWeightKg).FirstOrDefault()
            : 0;
        // §لا وزن افتراضي: يُقرأ من تعريف الصنف/العبوة، وإن غاب يبقى صفراً
        // (والعدد المطلوب يُحسب من الكراتين أصلاً — لا من الكيلو بوزن مخترع).
        if (packWeight <= 0)
            packWeight = UnitsPolicy.CartonWeight(Db, targetProduct, null);
        // §B86/M4a: بلا وزن كرتون لا تُشتق كراتين — رفض صريح بدل كرتون-زائف (كان Ceiling(qty/0) ← 1)
        if (recompute && packWeight <= 0 && targetQty > 0)
        {
            string wName = Db.Products.AsNoTracking().Where(p => p.Id == targetProduct).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"#{targetProduct}";
            return OpResult.Fail($"⛔ تعديل البند: وزن الكرتون غير معرَّف للصنف «{wName}» — لا يمكن اشتقاق عدد الكراتين.\nعرّف وزن الكرتون (أو القوالب × وزن القالب) في بطاقة الصنف أو العبوة أولاً.");
        }
        int targetCartons = recompute
            ? Math.Max(1, (int)Math.Ceiling(targetQty / packWeight))
            : item.PlannedCartons;

        var draft = Db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == plan.Id && i.Id != item.Id && !i.IsClosed)
            .Select(i => new PlanItemDto { ProductId = i.ProductId, PackagingTypeId = i.PackagingTypeId,
                PlannedCartons = i.PlannedCartons, PlannedQtyKg = i.PlannedQtyKg,
                ScheduledDate = i.ScheduledDate == null ? null : i.ScheduledDate.Value.ToString("dd/MM/yyyy"),
                SuggestedShiftId = i.SuggestedShiftId, SuggestedLineId = i.SuggestedLineId }).ToList();
        draft.Add(new PlanItemDto { ProductId = targetProduct, PackagingTypeId = targetPack,
            PlannedCartons = targetCartons, PlannedQtyKg = targetQty, ScheduledDate = targetDate.ToString("dd/MM/yyyy"),
            SuggestedShiftId = targetShift, SuggestedLineId = item.SuggestedLineId ?? plan.LineId });
        var capacity = new PlanningCapacityEvaluator(Db).Evaluate(draft, plan.Id,
            plan.StartDate?.ToString("dd/MM/yyyy"), plan.EndDate?.ToString("dd/MM/yyyy"), plan.ShiftId, plan.LineId);
        if (!capacity.IsValid) return OpResult.Fail(capacity.Error);

        // §B86/M4b: التعديل فوق رصيد الدفعة مسموح كالإنشاء (قاعدة الماء) — تنبيه بدل الرفض المتناقض
        string lotWarn = "";
        if (item.LotId != null && targetQty > item.PlannedQtyKg)
        {
            var lot = Db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == item.LotId);
            double reservedOthers = Db.ProductionPlanItems.AsNoTracking()
                .Where(x => x.LotId == item.LotId && x.Id != item.Id && x.PlanId != item.PlanId)
                .Sum(x => x.PlannedQtyKg);
            if (lot != null && targetQty + reservedOthers > lot.InStockQtyKg - lot.UnderTreatmentQtyKg + 0.001)
                lotWarn = $" ⚠ الكمية الجديدة ({targetQty:N1} كجم) تتجاوز رصيد الدفعة المتاح ({lot.InStockQtyKg - lot.UnderTreatmentQtyKg - reservedOthers:N1} كجم) — مقبولة (ماء التشغيل) وراجعها في التنفيذ.";
        }

        // §إصلاح: كان فحص ملكية الدفعة للعميل الجديد يقع **بعد** إسناد التاريخ والصنف
        // والعبوة والكمية على الكيان المتتبَّع. فعند رفضه تعود OpResult.Fail بينما
        // التعديلات باقية في ChangeTracker، فيكتبها أول SaveChanges لاحق في نفس النطاق
        // — رفض ظاهر وتعديل فعلي. كل الفحوص تسبق كل الإسنادات الآن.
        if (newCustomerId != null && item.LotId != null)
        {
            var ownerPre = Db.Lots.AsNoTracking().Where(l => l.Id == item.LotId)
                .Select(l => l.CustomerId).FirstOrDefault();
            if (ownerPre != null && ownerPre != newCustomerId)
                return OpResult.Fail("لا يمكن تغيير العميل: الدفعة مملوكة لعميل آخر.");
        }

        // تطبيق التغييرات بعد نجاح كل الفحوص
        string changes = "";
        item.ScheduledDate = targetDate;
        if (newShiftId != null) item.SuggestedShiftId = targetShift;
        if (newProductId != null && newProductId.Value != item.ProductId)
        {
            item.ProductId = targetProduct;
            changes += " الصنف ✓";
        }
        if (newPackagingTypeId != null && newPackagingTypeId.Value != item.PackagingTypeId)
        {
            item.PackagingTypeId = targetPack;
            changes += " العبوة ✓";
        }
        if (newQtyKg != null) item.PlannedQtyKg = targetQty;
        if (recompute) item.PlannedCartons = targetCartons;
        if (newCustomerId != null) item.CustomerId = newCustomerId;  // §الملكية فُحصت أعلاه قبل أي إسناد

        // §إصلاح تسرّب حجز: تعديل كمية البند كان يحفظ PlannedQtyKg الجديدة بلا تحديث
        // ReservedQtyKg على الدفعة، فيبقى الحجز على الكمية القديمة. تخفيض 1000⟵400
        // يترك 600 محجوزة بلا سند، ورفعها يترك الفارق غير محجوز فتُخطط مرتين.
        // (المسار الآخر UpdatePlan يستدعي ApplyLotReservations — هذا المسار كان يفوته.)
        int? touchedLot = item.LotId;
        Db.SaveChanges();
        if (touchedLot != null && newQtyKg != null)
        {
            var lotR = Db.Lots.FirstOrDefault(l => l.Id == touchedLot.Value);
            if (lotR != null)
            {
                double reserved = Db.ProductionPlanItems
                    .Where(i => i.LotId == touchedLot.Value)
                    .Join(Db.ProductionPlans, i => i.PlanId, p => p.Id, (i, p) => new { i, p })
                    .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
                    .Where(x => !x.i.IsClosed)
                    .Sum(x => x.i.PlannedQtyKg - x.i.ProducedQtyKg);
                lotR.ReservedQtyKg = Math.Max(0, reserved);
                Db.SaveChanges();
            }
        }
        return OpResult.Success($"تم تعديل البند — التاريخ {targetDate:dd/MM/yyyy}{changes} — وأُعيد فحص الطاقة تلقائياً." + lotWarn);
    }

    public List<BillableDto> GetBillableDeliveries(int customerId)
    {
        return Db.CustomerDeliveries.AsNoTracking()
            .Where(d => d.CustomerId == customerId && d.IsApproved)
            .OrderByDescending(d => d.DeliveryDate)
            .ToList()
            .Select(d => new BillableDto
            {
                DeliveryId = d.Id,
                DeliveryNumber = d.DocumentNumber,
                Date = d.DeliveryDate?.ToString("dd/MM/yyyy"),
                TotalQtyKg = d.TotalQtyKg,
                InvoicedQtyKg = d.InvoicedQtyKg,
                BillableQtyKg = Math.Max(0, d.TotalQtyKg - d.InvoicedQtyKg)
            }).ToList();
    }

    public OpResult MarkInvoiced(int deliveryId, double qty)
    {
        Require("delivery", "Post");
        var d = Db.CustomerDeliveries.FirstOrDefault(x => x.Id == deliveryId);
        if (d == null) return OpResult.Fail("سند التسليم غير موجود.");
        if (!d.IsApproved) return OpResult.Fail("السند غير معتمد.");
        if (qty <= 0) return OpResult.Fail("أدخل الكمية المراد فوترتها.");
        double remainingBillable = d.TotalQtyKg - d.InvoicedQtyKg;
        if (qty > remainingBillable + 0.001)
            return OpResult.Fail($"الكمية أكبر من المتاح للفوترة — المتبقي غير المفوتر: {remainingBillable:N1} كجم (ممنوع تكرار الفوترة لنفس الكمية).");
        // §1.50.72 P4-5: القراءة+الزيادة+الحفظ داخل معاملة واحدة (قابلة للقفل Serializable) —
        // كان مسار قراءة-ثم-كتابة بلا معاملة: تنافس على نفس السند كان يمرّر فوترة مكررة.
        return RunInTransaction(() =>
        {
            d.InvoicedQtyKg += qty;
            Db.SaveChanges();
            return OpResult.Success($"تم تسجيل فوترة {qty:N1} كجم من {d.DocumentNumber}. المتبقي القابل للفوترة: {d.TotalQtyKg - d.InvoicedQtyKg:N1} كجم.");
        });
    }
}
