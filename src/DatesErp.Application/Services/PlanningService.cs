using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>§7 — خطط الإنتاج: حجز كميات الدفعات لكل الأصناف + فحص طاقة الورديات + رفض التجاوز.</summary>
public class PlanningService : ServiceBase, IPlanningService
{
    public PlanningService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    protected override System.Data.IsolationLevel TransactionIsolation => System.Data.IsolationLevel.Serializable;

    public PlanCapacityResult EvaluateCapacity(List<PlanItemDto> items, int? excludePlanId = null,
        string startDate = null, string endDate = null, int? shiftId = null, int? lineId = null)
    {
        if (Session == null || !Session.Can("planning", "View"))
            throw new PermissionDeniedException("عرض الطاقة");
        return new PlanningCapacityEvaluator(Db).Evaluate(items ?? new(), excludePlanId, startDate, endDate, shiftId, lineId);
    }

    private void ValidateCapacity(List<PlanItemDto> items, ProductionPlan plan)
    {
        foreach (var item in items) ProductIdentityGuard.EnsurePlanningLink(Db, item.ProductId, item.LotId, item.SelectedRawProductId);
        var check = new PlanningCapacityEvaluator(Db).Evaluate(items, plan.Id,
            plan.StartDate?.ToString("dd/MM/yyyy"), plan.EndDate?.ToString("dd/MM/yyyy"), plan.ShiftId, plan.LineId);
        if (!check.IsValid)
        {
            string suggestion = "";
            var rejected = check.Rows.LastOrDefault(r => r.ExcessCartons > 0 && r.Rate > 0);
            if (rejected != null && plan.StartDate != null && plan.EndDate != null)
            {
                var original = items[rejected.Index];
                for (var day = plan.StartDate.Value.Date; day <= plan.EndDate.Value.Date.AddDays(7); day = day.AddDays(1))
                {
                    if (UiFormat.TryParseDate(original.ScheduledDate, out var oldDay) && day == oldDay.Date) continue;
                    var candidate = new PlanItemDto { ProductId = original.ProductId, PackagingTypeId = original.PackagingTypeId,
                        PlannedCartons = original.PlannedCartons, PlannedQtyKg = original.PlannedQtyKg,
                        SuggestedShiftId = original.SuggestedShiftId ?? plan.ShiftId,
                        SuggestedLineId = original.SuggestedLineId ?? plan.LineId, ScheduledDate = day.ToString("dd/MM/yyyy") };
                    if (new PlanningCapacityEvaluator(Db).Evaluate(items.Where((_, i) => i != rejected.Index).Append(candidate).ToList(), plan.Id).IsValid)
                    { suggestion = $"\nأقرب يوم بديل: {day:dd/MM/yyyy}" + (day > plan.EndDate.Value.Date ? " (خارج فترة الخطة؛ مدّد الفترة أولاً)" : ""); break; }
                }
            }
            throw new DomainException(check.Error + suggestion, "CAPACITY_EXCEEDED");
        }
    }

    internal static List<PlanItemDto> CapacityItems(ProductionPlan plan) => plan.Items.Where(i => !i.IsClosed).Select(i => new PlanItemDto
    {
        ProductId = i.ProductId, PackagingTypeId = i.PackagingTypeId, LotId = i.LotId,
        PlannedCartons = i.PlannedCartons, PlannedQtyKg = i.PlannedQtyKg,
        ScheduledDate = i.ScheduledDate?.ToString("dd/MM/yyyy"),
        SuggestedShiftId = i.SuggestedShiftId ?? plan.ShiftId,
        SuggestedLineId = i.SuggestedLineId ?? plan.LineId
    }).ToList();

    /// <summary>§B75: نهاية الفترة حسب النوع — يومية=البداية، أسبوعية=+6 أيام.</summary>
    public static DateTime PeriodEndDate(string planType, DateTime start)
        => planType switch
        {
            "Weekly" => start.AddDays(6),
            "Monthly" => start.AddMonths(1).AddDays(-1),   // §B77 شهرية: حتى نهاية الشهر
            _ => start
        };

    /// <summary>Receiving owns the customer/shipment identity. Missing fields inherit that identity;
    /// explicit conflicting references are rejected at planning, not saved for issuance to fail later.</summary>
    private void ResolveReceivingIdentity(IEnumerable<PlanItemDto> items)
    {
        foreach (var dto in items.Where(i => i.LotId != null))
        {
            var lot = Db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == dto.LotId.Value)
                ?? throw new DomainException("الدفعة المحددة غير موجودة.");
            if (dto.ShipmentId != null && dto.ShipmentId != lot.ShipmentId)
                throw new DomainException("الشحنة المحددة لا تخص دفعة الخام؛ اختر بيانات الاستلام الأصلية.", "SHIPMENT_IDENTITY");
            if (dto.CustomerId != null && lot.CustomerId != null && dto.CustomerId != lot.CustomerId)
                throw new DomainException("الدفعة المحددة مملوكة لعميل آخر — لا يمكن تخطيطها لعميل مختلف.", "OWNERSHIP");
            dto.CustomerId ??= lot.CustomerId;
            dto.ShipmentId ??= lot.ShipmentId;
        }
    }

    public OpResult SavePlan(string title, string planType, string startDate, string endDate, int? shiftId, int? lineId, List<PlanItemDto> items, string notes = null, string scopeMode = null, int? singleCustomerId = null)
    {
        Require("planning", "Create");
        if (items == null || items.Count == 0) return OpResult.Fail("أدخل بنداً واحداً على الأقل في الخطة.");

        return RunOp(() =>
        {
            var plan = new ProductionPlan
            {
                DocumentNumber = Numbering.Next("PLAN"),
                PlanTitle = title,
                PlanType = planType ?? "Daily",
                ScopeMode = scopeMode ?? "Multi",
                SingleCustomerId = singleCustomerId,
                StartDate = UiFormat.TryParseDate(startDate, out var sd) ? sd : null,
                EndDate = UiFormat.TryParseDate(endDate, out var ed) ? ed : null,
                ShiftId = shiftId,
                LineId = lineId,
                Status = DocStatuses.Draft,
                Notes = notes
            };
            Db.ProductionPlans.Add(plan);
            Db.SaveChanges();

            // §الخطة الطويلة: لكل بند تاريخ إنتاج إلزامي داخل فترة الخطة.
            // بدونه كان البند بلا تاريخ يتخطى فحص الطاقة كلياً (الحارس يشترط ScheduledDate != null).
            // §B80: + رفض أي تاريخ بند خارج فترة الخطة.
            ApplyDefaultScheduledDates(items, sd, ed);
            ResolveReceivingIdentity(items);
            // §B76: خطة العميل الواحد لا تقبل تسريب عملاء آخرين — فرض من الخلفية لا من الواجهة فقط
            if ((scopeMode ?? "Multi") == "Single" && singleCustomerId != null)
                foreach (var dtoC in items)
                    if (dtoC.CustomerId != singleCustomerId)
                        throw new DomainException("خطة العميل الواحد لا تقبل بنوداً لعميل آخر — كل البنود يجب أن تكون للعميل المحدد في الرأس.");

            // §حرج: حارس الطاقة يشترط وردية على البند، فكان أي بند بلا SuggestedShiftId
            // يتخطى الفحص كلياً وبصمت. البند يرث وردية الخطة وخطها إن لم يُحددا له.
            foreach (var dtoInh in items)
            {
                dtoInh.SuggestedShiftId ??= shiftId;
                dtoInh.SuggestedLineId ??= lineId;
            }
            // §إصلاح حرج: تراكم استهلاك بنود «هذه الخطة نفسها» على نفس اليوم/الوردية/الخط.
            var capWarnings = new List<string>(); // §B85/H4: تنبيهات الطاقة غير المعرَّفة

            var usedPerLot = new Dictionary<int, double>();
            var priorPlanRes = PriorReservationByPlan(plan.Id);
            foreach (var dto in items)
            {
                // §نظام الوحدات: بنود الخطة منتجات تامة فقط (002) — واتساق الكرتون/الكيلو إلزامي
                UnitsPolicy.RequireItemType(Db, dto.ProductId, "Finished", "خطة الإنتاج");
                dto.PlannedQtyKg = UnitsPolicy.EnsureCartonKgConsistency(Db, dto.ProductId, dto.PackagingTypeId,
                    dto.PlannedQtyKg, dto.PlannedCartons, "خطة الإنتاج");

                // §الإنتاج التام unitه الكرتون: إن أُدخل الوزن بلا كراتين تُشتق الكراتين من
                // وزن كرتون الصنف/العبوة المعرَّف — فتُفحص الطاقة دائماً ولا تُتخطى.
                if (dto.PlannedCartons <= 0 && dto.PlannedQtyKg > 0)
                {
                    double w = UnitsPolicy.CartonWeight(Db, dto.ProductId, dto.PackagingTypeId);
                    if (w > 0) dto.PlannedCartons = (int)Math.Round(dto.PlannedQtyKg / w);
                }

                // §قاعدة توازن الإنتاج: المخطط كمية منتج تام مستهدفة، لا حجزاً للخام.
                // وفي تصنيع التمور يزيد وزن الخارج عن الداخل لإضافة الماء أثناء التشغيل،
                // فالتخطيط بكمية أكبر من رصيد الخام مشروع — ولا معادلة ثابتة تربطهما.
                // الحجز الفعلي للخام يتم عند اعتماد الأمر، وهناك يُمنع تجاوز الرصيد فعلاً.
                if (dto.SourceType == "FromReceiving" && dto.LotId is int lotId)
                {
                    if (!Db.Lots.AsNoTracking().Any(l => l.Id == lotId))
                        throw new DomainException("الدفعة المحددة غير موجودة.");
                }

                // §تتبع الصنف: التحويل الرسمي — لا يخطط خلاص من دفعة سكري ولا سكري من دفعة خلاص
                ProductIdentityGuard.EnsurePlanningLink(Db, dto.ProductId, dto.LotId, dto.SelectedRawProductId);

                // §3/§8 — فحص الطاقة الإنتاجية للوردية في اليوم المحدد (لكل بند)
                // مع تراكم بنود هذه الخطة نفسها — لا يكفي استثناء الخطة من خطط الآخرين.
                // Full normalized draft is checked together below, using the same evaluator as the picker.

                // §الملكية: دفعة العميل لا تُخطط إلا باسم صاحبها — لا دمج ولا تداخل
                if (dto.LotId is int lotOwn && dto.CustomerId is int custOwn)
                {
                    var lotOwner = Db.Lots.AsNoTracking().Where(l => l.Id == lotOwn).Select(l => l.CustomerId).FirstOrDefault();
                    if (lotOwner != null && lotOwner != custOwn)
                        throw new DomainException("الدفعة المحددة مملوكة لعميل آخر — لا يمكن تخطيطها لعميل مختلف.", "OWNERSHIP");
                }

                var ppi = new ProductionPlanItem
                {
                    PlanId = plan.Id,
                    SourceType = dto.SourceType,
                    LotId = dto.LotId,
                    ShipmentId = dto.ShipmentId ?? (dto.LotId != null ? Db.Lots.Where(l => l.Id == dto.LotId).Select(l => l.ShipmentId).FirstOrDefault() : null),
                    CustomerId = dto.CustomerId,
                    ProductId = dto.ProductId,
                    PackagingTypeId = dto.PackagingTypeId,
                    PlannedQtyKg = dto.PlannedQtyKg,
                    PlannedCartons = dto.PlannedCartons,
                    ScheduledDate = UiFormat.TryParseDate(dto.ScheduledDate, out var d) ? d : null,
                    SuggestedShiftId = dto.SuggestedShiftId,
                    SuggestedLineId = dto.SuggestedLineId,
                    PriorityNo = dto.PriorityNo,
                    Status = DocStatuses.Draft
                };
                // §تتبع سحب الخام: يحسم الوحدة/الوزن/المتاح ويلتقط التتبع على البند
                ResolveSourceDraw(ppi, dto, usedPerLot, priorPlanRes);
                plan.Items.Add(ppi);
            }

            // حجز كميات الدفعات (لكل الأصناف) بمجرد إنشاء الخطة
            ValidateCapacity(items, plan);
            ApplyLotReservations(plan);
            Db.SaveChanges();
            string capMsg = capWarnings.Count > 0 ? "\n" + string.Join("\n", capWarnings) : "";
            // §B86/#16: تنبيه الخطة الطويلة — مراجعة الإشغال الأسبوعي قبل التوسع
            string longMsg = plan.StartDate != null && plan.EndDate != null
                && (plan.EndDate.Value.Date - plan.StartDate.Value.Date).TotalDays > 7
                ? "\n📅 تنبيه خطة طويلة (أكثر من 7 أيام): راجع إشغال الورديات أسبوعياً وتأكد من توزيع البنود على الأيام بدل تكديسها."
                : "";
            return OpResult.Success("تم حفظ خطة الإنتاج بنجاح." + capMsg + longMsg, plan.Id, plan.DocumentNumber);
        });
    }

    /// <summary>
    /// §تعديل خطة قائمة (مسودة غير معتمدة): يستبدل البنود ويعيد فحص الطاقة والأرصدة والحجوزات.
    /// يعالج حجز الخطة القديم صحيحاً (يُستثنى من فحص المتاح حتى لا يُحتسب مرتين).
    /// </summary>
    /// <summary>§v1.50.27: هل لأبنية الخطة أوامر إنتاج قائمة (غير ملغاة) تعلق بها؟</summary>
    private bool PlanHasLiveOrderReferences(ProductionPlan plan)
    {
        if (Db.ProductionOrders.Any(o => o.SourcePlanId == plan.Id && o.Status != DocStatuses.Cancelled)) return true;
        var itemIds = plan.Items.Select(i => i.Id).ToList();
        if (itemIds.Count == 0) return false;
        var orderIds = Db.ProductionOrderItems.AsNoTracking()
            .Where(oi => oi.PlanItemId != null && itemIds.Contains(oi.PlanItemId.Value))
            .Select(oi => oi.OrderId).Distinct().ToList();
        return Db.ProductionOrders.AsNoTracking()
            .Any(o => orderIds.Contains(o.Id) && o.Status != DocStatuses.Cancelled);
    }

    /// <summary>§1.50.66.5 — هل دخلت الخطة مرحلة التنفيذ غير القابلة للرجوع (ProductionExecutions)؟</summary>
    private bool PlanHasIrreversibleExecution(int planId)
    {
        try
        {
            var orderIds = Db.ProductionOrders.AsNoTracking().Where(o => o.SourcePlanId == planId).Select(o => o.Id).ToList();
            if (orderIds.Count == 0) return false;
            return Db.ProductionExecutions.AsNoTracking().Any(e => orderIds.Contains(e.OrderId) && (e.IsDayClosed || e.ActualQtyKg > 0 || e.ConsumedRawKg > 0));
        }
        catch { return false; }
    }

    /// <summary>§1.50.66.3 — فحص جاهزية الدفعات والكميات المتاحة عند الاعتماد.</summary>
    private string CheckLotAvailabilityForApproval(ProductionPlan plan)
    {
        var demands = plan.Items.Where(i => i.LotId != null && !i.IsClosed)
            .GroupBy(i => i.LotId!.Value).ToList();
        if (demands.Count == 0) return null;
        var lotIds = demands.Select(d => d.Key).ToList();
        var lots = Db.Lots.AsNoTracking().Where(l => lotIds.Contains(l.Id)).ToDictionary(l => l.Id);
        foreach (var d in demands)
        {
            if (!lots.TryGetValue(d.Key, out var lot))
                return $"الدفعة {d.Key} غير موجودة — راجع الاستلام.";
            if (lot.Status != DocStatuses.Approved)
                return $"الدفعة {lot.LotCode} ليست معتمدة (حالتها: {DocStatuses.ToArabic(lot.Status)}) — لا يمكن اعتماد خطة عليها.";
            if (lot.InStockQtyKg <= 0.001)
                return $"الدفعة {lot.LotCode} رصيدها صفر — لا يمكن اعتماد خطة إنتاج عليها.";
            if (lot.Status == DocStatuses.Closed)
                return $"الدفعة {lot.LotCode} مقفلة — لا يمكن اعتماد خطة عليها.";
            // A future plan may reserve raw that will complete treatment before
            // its scheduled day. Check cumulative demands at EACH day; checking
            // only today's WTRT balance wrongly blocks a legitimate future plan,
            // while checking only the last day could allow an early overbooking.
            double cumulative = 0;
            foreach (var day in d.GroupBy(i => (i.ScheduledDate ?? Db.BusinessNow).Date)
                .OrderBy(g => g.Key))
            {
                cumulative += day.Sum(PlanRawReservation.PlannedKg);
                double available = LotAvailableExcluding(lot.Id, plan.Id, day.Key);
                if (cumulative > available + 0.001)
                    return $"⛔ الدفعة {lot.LotCode}: المطلوب {cumulative:N1} كجم حتى {day.Key:dd/MM/yyyy} في هذه الخطة، والمتاح الفعلي {available:N1} كجم بعد حجوزات الخطط والأوامر الأخرى — الكمية غير كافية. عند اختلاف وزن الخام عن التام أدخل «سحب الخام» المتوقع في كل بند؛ لا يُفترض معامل تحويل تلقائي.";
            }
        }
        return null;
    }

    public OpResult UpdatePlan(int planId, string title, string planType, string startDate, string endDate, int? shiftId, int? lineId, List<PlanItemDto> items, string notes = null, string scopeMode = null, int? singleCustomerId = null)
    {
        Require("planning", "Edit");
        if (items == null || items.Count == 0) return OpResult.Fail("أضف بنداً واحداً على الأقل في الخطة.");

        return RunOp(() =>
        {
            var plan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == planId)
                       ?? throw new DomainException("الخطة غير موجودة.");
            if (Db.ProductionPlanContexts.Any(c => c.PlanId == planId))
                throw new DomainException("هذه خطة متسلسلة: عدّل بطاقة اليوم والوردية المعنية فقط، لا تستبدل بنود الفترة كلها.");
            // §v1.50.27 — حارس السلامة المرجعية (قبل فحص الاعتماد ليكون السبب أدق):
            // التعديل يمحو بنود الخطة ويعيد إنشاءها؛ فإن كانت بنودها مرجَعة من أوامر
            // إنتاج قائمة (حتى ببيانات قديمة أُلغي اعتمادها بحلّ قديم) لتمزّق الأوامر.
            if (PlanHasLiveOrderReferences(plan))
                throw new DomainException("لا يمكن تعديل الخطة: صدرت منها أوامر إنتاج قائمة — ألغِ تلك الأوامر رسمياً أولاً.");
            // §1.50.66.5 — منع التعديل بعد دخول التنفيذ غير القابل للرجوع
            if (PlanHasIrreversibleExecution(plan.Id))
                throw new DomainException("لا يمكن تعديل الخطة: دخلت مرحلة التنفيذ الفعلي (ProductionExecutions) — مرحلة غير قابلة للرجوع.");
            if (plan.IsApproved) throw new DomainException("الخطة معتمدة — ألغِ الاعتماد أولاً للتعديل.");
            if (plan.IsClosed) throw new DomainException("الخطة مقفلة — لا يمكن التعديل.");

            plan.PlanTitle = title;
            plan.PlanType = planType ?? "Daily";
            plan.ScopeMode = scopeMode ?? plan.ScopeMode ?? "Multi";
            plan.SingleCustomerId = singleCustomerId;
            plan.StartDate = UiFormat.TryParseDate(startDate, out var sd) ? sd : null;
            plan.EndDate = UiFormat.TryParseDate(endDate, out var ed) ? ed : null;
            plan.ShiftId = shiftId;
            plan.LineId = lineId;
            plan.Notes = notes;

            // حذف البنود القديمة (ستُستبدل) — مع تحرير حجوزاتها ضمنياً بالاستثناء من الفحص
            Db.ProductionPlanItems.RemoveRange(plan.Items);
            plan.Items.Clear();

            // §الخطة الطويلة: لكل بند تاريخ إنتاج إلزامي داخل فترة الخطة.
            // §B80: + رفض أي تاريخ بند خارج فترة الخطة.
            ApplyDefaultScheduledDates(items, sd, ed);
            ResolveReceivingIdentity(items);
            // §B76: خطة العميل الواحد لا تقبل تسريب عملاء آخرين — فرض من الخلفية لا من الواجهة فقط
            // §إصلاح ثغرة: كان الفحص على scopeMode **الوارد** لا على المحفوظ في الخطة، فاستدعاء
            // UpdatePlan بـ scopeMode=null على خطة Single قائمة يتخطى الحارس كلياً — مع أن
            // السطر أعلاه يُبقي plan.ScopeMode = "Single". النتيجة: بنود عميل آخر تدخل خطة
            // عميل واحد. نفحص الآن القيمة الفعلية بعد الدمج.
            if (plan.ScopeMode == "Single" && plan.SingleCustomerId != null)
                foreach (var dtoC in items)
                    if (dtoC.CustomerId != plan.SingleCustomerId)
                        throw new DomainException("خطة العميل الواحد لا تقبل بنوداً لعميل آخر — كل البنود يجب أن تكون للعميل المحدد في الرأس.");

            // §حرج: حارس الطاقة يشترط وردية على البند، فكان أي بند بلا SuggestedShiftId
            // يتخطى الفحص كلياً وبصمت. البند يرث وردية الخطة وخطها إن لم يُحددا له.
            foreach (var dtoInh in items)
            {
                dtoInh.SuggestedShiftId ??= shiftId;
                dtoInh.SuggestedLineId ??= lineId;
            }
            // §إصلاح حرج: تراكم استهلاك بنود «هذه الخطة نفسها» على نفس اليوم/الوردية/الخط.
            var capWarnings = new List<string>(); // §B85/H4: تنبيهات الطاقة غير المعرَّفة

            var usedPerLot = new Dictionary<int, double>();
            var priorPlanRes = PriorReservationByPlan(plan.Id);
            foreach (var dto in items)
            {
                // §نظام الوحدات: بنود الخطة منتجات تامة فقط (002) — واتساق الكرتون/الكيلو إلزامي
                UnitsPolicy.RequireItemType(Db, dto.ProductId, "Finished", "خطة الإنتاج");
                dto.PlannedQtyKg = UnitsPolicy.EnsureCartonKgConsistency(Db, dto.ProductId, dto.PackagingTypeId,
                    dto.PlannedQtyKg, dto.PlannedCartons, "خطة الإنتاج");

                // §الإنتاج التام unitه الكرتون: إن أُدخل الوزن بلا كراتين تُشتق الكراتين من
                // وزن كرتون الصنف/العبوة المعرَّف — فتُفحص الطاقة دائماً ولا تُتخطى.
                if (dto.PlannedCartons <= 0 && dto.PlannedQtyKg > 0)
                {
                    double w = UnitsPolicy.CartonWeight(Db, dto.ProductId, dto.PackagingTypeId);
                    if (w > 0) dto.PlannedCartons = (int)Math.Round(dto.PlannedQtyKg / w);
                }

                // §كما في SavePlan: المخطط مستهدف إنتاجي لا حجز خام — لا معادلة ثابتة
                if (dto.SourceType == "FromReceiving" && dto.LotId is int lotId)
                {
                    if (!Db.Lots.AsNoTracking().Any(l => l.Id == lotId))
                        throw new DomainException("الدفعة المحددة غير موجودة.");
                }

                // §تتبع الصنف: التحويل الرسمي — لا يخطط خلاص من دفعة سكري ولا سكري من دفعة خلاص
                ProductIdentityGuard.EnsurePlanningLink(Db, dto.ProductId, dto.LotId, dto.SelectedRawProductId);

                // §3/§8 — فحص الطاقة الإنتاجية للوردية في اليوم المحدد (باستثناء هذه الخطة)
                // مع تراكم بنود هذه الخطة نفسها — لا يكفي استثناء الخطة من خطط الآخرين.
                // Full normalized draft is checked together below, using the same evaluator as the picker.

                // §الملكية: دفعة العميل لا تُخطط إلا باسم صاحبها
                if (dto.LotId is int lotOwn && dto.CustomerId is int custOwn)
                {
                    var lotOwner = Db.Lots.AsNoTracking().Where(l => l.Id == lotOwn).Select(l => l.CustomerId).FirstOrDefault();
                    if (lotOwner != null && lotOwner != custOwn)
                        throw new DomainException("الدفعة المحددة مملوكة لعميل آخر — لا يمكن تخطيطها لعميل مختلف.", "OWNERSHIP");
                }

                var ppi = new ProductionPlanItem
                {
                    PlanId = plan.Id,
                    SourceType = dto.SourceType,
                    LotId = dto.LotId,
                    ShipmentId = dto.ShipmentId ?? (dto.LotId != null ? Db.Lots.Where(l => l.Id == dto.LotId).Select(l => l.ShipmentId).FirstOrDefault() : null),
                    CustomerId = dto.CustomerId,
                    ProductId = dto.ProductId,
                    PackagingTypeId = dto.PackagingTypeId,
                    PlannedQtyKg = dto.PlannedQtyKg,
                    PlannedCartons = dto.PlannedCartons,
                    ScheduledDate = UiFormat.TryParseDate(dto.ScheduledDate, out var d) ? d : null,
                    SuggestedShiftId = dto.SuggestedShiftId,
                    SuggestedLineId = dto.SuggestedLineId,
                    PriorityNo = dto.PriorityNo,
                    Status = DocStatuses.Draft
                };
                // §تتبع سحب الخام: يحسم الوحدة/الوزن/المتاح ويلتقط التتبع على البند
                ResolveSourceDraw(ppi, dto, usedPerLot, priorPlanRes);
                plan.Items.Add(ppi);
            }

            // إعادة احتساب الحجوزات بالبنود الجديدة
            ValidateCapacity(items, plan);
            ApplyLotReservations(plan);
            Db.SaveChanges();
            string capMsgU = capWarnings.Count > 0 ? "\n" + string.Join("\n", capWarnings) : "";
            string longMsgU = plan.StartDate != null && plan.EndDate != null
                && (plan.EndDate.Value.Date - plan.StartDate.Value.Date).TotalDays > 7
                ? "\n📅 تنبيه خطة طويلة (أكثر من 7 أيام): راجع إشغال الورديات أسبوعياً وتأكد من توزيع البنود على الأيام بدل تكديسها."
                : "";
            return OpResult.Success("تم تحديث خطة الإنتاج بنجاح." + capMsgU + longMsgU, plan.Id, plan.DocumentNumber);
        });
    }

    /// <summary>المتاح الفعلي من دفعة بعد خصم حجوزات الخطط النشطة والأوامر، مع استثناء خطة محددة.</summary>
    private double LotAvailableExcluding(int lotId, int? excludePlanId, DateTime scheduledDay)
    {
        var lot = Db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == lotId);
        if (lot == null) return 0;
        double planCommitted = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.LotId == lotId && (excludePlanId == null || i.PlanId != excludePlanId))
            .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
            .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
            .Where(x => !x.i.IsClosed)
            .Select(x => x.i).RemainingKg().Sum();
        double orderCommitted = Db.ProductionOrderItems.AsNoTracking()
            .Where(i => i.LotId == lotId)
            .Join(Db.ProductionOrders.AsNoTracking(), i => i.OrderId, o => o.Id, (i, o) => new { i, o })
            .Where(x => x.o.Status != DocStatuses.Cancelled && x.o.Status != DocStatuses.Closed)
            .Where(x => !x.i.IsClosed)
            .Sum(x => x.i.PlannedQtyKg - x.i.ProducedQtyKg > 0 ? x.i.PlannedQtyKg - x.i.ProducedQtyKg : 0);
        // Remaining live treatment becomes available only ON/AFTER its readiness
        // date. Released quantities have already moved out of UnderTreatment,
        // so count only the in-progress remainder (no double counting).
        double maturing = 0;
        if (LotRequiresTreatment(lot))
        {
            var end = scheduledDay.Date.AddDays(1).AddTicks(-1);
            maturing = Db.RawTreatments.AsNoTracking()
                .Where(t => t.LotId == lotId && t.Status == TreatmentStatuses.InProgress
                    && t.ExpectedReadyAt <= end)
                .Sum(t => (double?)(t.QtyKg - t.ReleasedQtyKg - t.RejectedQtyKg)) ?? 0;
        }
        return Math.Max(0, Math.Round(lot.InStockQtyKg - lot.UnderTreatmentQtyKg
            + Math.Max(0, maturing) - planCommitted - orderCommitted, 3));
    }

    /// <summary>
    /// §المعالجة والتعقيم — يتحقق أن خام كل بند سيكون **جاهزاً في تاريخ إنتاج ذلك البند**.
    ///
    /// يُرجع رسالة الرفض أو <c>null</c> إن كان كل شيء سليماً. الرسالة تذكر
    /// **الرقم والسبب وأقرب موعد ممكن** — لأن المخطِّط يحتاج أن يعرف متى يستطيع،
    /// لا أن يُمنع وحسب.
    ///
    /// البنود بلا دفعة أو بلا تاريخ لا تُفحص: لا مرجع خام لها.
    /// </summary>
    private string CheckTreatmentReadiness(ProductionPlan plan)
    {
        // نجمع الطلب لكل (دفعة، تاريخ) حتى لا يمر بندان صغيران يتجاوزان معاً المتاح
        var demand = plan.Items
            .Where(i => i.LotId != null && i.ScheduledDate != null && !i.IsClosed)
            .GroupBy(i => (LotId: i.LotId.Value, Day: i.ScheduledDate.Value.Date))
            .Select(g => new { g.Key.LotId, g.Key.Day, Kg = g.Sum(x => x.SourceQtyKg > 0 ? x.SourceQtyKg : x.PlannedQtyKg) })
            .ToList();
        if (demand.Count == 0) return null;

        var lotIds = demand.Select(d => d.LotId).Distinct().ToList();
        var lots = Db.Lots.AsNoTracking().Where(l => lotIds.Contains(l.Id)).ToList();


        foreach (var d in demand.OrderBy(x => x.Day))
        {
            var lot = lots.FirstOrDefault(l => l.Id == d.LotId);
            if (lot == null || !LotRequiresTreatment(lot)) continue;
            var receiptUntil = Db.ShipmentItems.Where(i => i.Id == lot.ShipmentItemId && i.TreatmentRequired == true)
                .Select(i => i.TreatmentUntilDate).FirstOrDefault();
            if (receiptUntil != null && d.Day < receiptUntil.Value.Date)
                return $"الدفعة {lot.LotCode} قيد المعالجة حتى {receiptUntil:dd/MM/yyyy}؛ تاريخ الإنتاج يسبق الموعد المختار."; // صنف لا يشترط معالجة

            var end = d.Day.AddDays(1).AddTicks(-1);
            var live = Db.RawTreatments.AsNoTracking()
                .Where(t => t.LotId == lot.Id && t.Status == TreatmentStatuses.InProgress)
                .Select(t => new { t.ExpectedReadyAt, Kg = t.QtyKg - t.ReleasedQtyKg - t.RejectedQtyKg })
                .ToList();

            double maturing = live.Where(t => t.ExpectedReadyAt <= end).Sum(t => Math.Max(0, t.Kg));
            // §الحجوزات الأخرى تُطرح: خطة معتمدة أخرى التزام قائم لا يُنقض ضمناً
            double reservedOthers = Db.ProductionPlanItems.AsNoTracking()
                .Where(i => i.LotId == lot.Id && i.PlanId != plan.Id && !i.IsClosed)
                .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
                .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
                .Select(x => x.i).RemainingKg().Sum();

            double availableForDate = Math.Max(0,
                lot.TreatmentReadyQtyKg + maturing - Math.Max(0, reservedOthers) - lot.ProducedQtyKg);
            if (d.Kg <= availableForDate + 0.001) continue;

            // أقرب تاريخ تكتمل فيه الكمية المطلوبة — البديل العملي بدل «غير كافٍ»
            string soonest = "لا توجد معالجة جارية تكفي — ابدأ معالجة الكمية الناقصة.";
            double cum = Math.Max(0, lot.TreatmentReadyQtyKg - Math.Max(0, reservedOthers) - lot.ProducedQtyKg);
            foreach (var t in live.OrderBy(t => t.ExpectedReadyAt))
            {
                cum += Math.Max(0, t.Kg);
                if (cum >= d.Kg - 0.001)
                {
                    soonest = $"أقرب موعد لاكتمال الكمية: {t.ExpectedReadyAt:dd/MM/yyyy}.";
                    break;
                }
            }

            return $"⛔ لا يمكن اعتماد الخطة: خام الدفعة {lot.LotCode} لن يكون جاهزاً يوم {d.Day:dd/MM/yyyy}.\n"
                 + $"المطلوب: {d.Kg:N1} كجم | الجاهز حالياً: {lot.TreatmentReadyQtyKg:N1} كجم"
                 + $" | المتوقع جاهزيته حتى ذلك التاريخ: {maturing:N1} كجم"
                 + (reservedOthers > 0.001 ? $" | المحجوز لخطط أخرى: {reservedOthers:N1} كجم" : "")
                 + $"\nالمتاح للتخطيط في ذلك التاريخ: {availableForDate:N1} كجم.\n{soonest}";
        }
        return null;
    }

    private void ApplyLotReservations(ProductionPlan plan)
    {
        // تصفير حجوزات هذه الخطة السابقة ثم إعادة الاحتساب — يدعم التعديل متعدد الأصناف
        var lotIds = plan.Items.Where(i => i.LotId != null).Select(i => i.LotId.Value).Distinct().ToList();
        foreach (var lid in lotIds)
        {
            var lot = Db.Lots.FirstOrDefault(l => l.Id == lid);
            if (lot == null) continue;
            // الحجز = المتبقي غير المنتَج من مخطط الخطط النشطة (المنتَج استُهلك خامه أو أُنتج)
            double reserved = Db.ProductionPlanItems
                .Where(i => i.LotId == lid && i.PlanId != plan.Id)
                .Join(Db.ProductionPlans, i => i.PlanId, p => p.Id, (i, p) => new { i, p })
                .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
                .Where(x => !x.i.IsClosed)
                .Select(x => x.i).RemainingKg().Sum();
            lot.ReservedQtyKg = Math.Max(0, reserved);
        }
        // §الخطة المقفلة/الملغاة تُحرر حجوزاتها — لا تُعاد إضافتها
        bool planStillActive = plan.Status != DocStatuses.Closed && plan.Status != DocStatuses.Cancelled && !plan.IsClosed;
        if (planStillActive)
        {
            foreach (var lid in plan.Items.Where(i => i.LotId != null).Select(i => i.LotId.Value).Distinct())
            {
                var lot = Db.Lots.First(l => l.Id == lid);
                lot.ReservedQtyKg += plan.Items.Where(i => i.LotId == lid && !i.IsClosed).Sum(PlanRawReservation.RemainingKg);
            }
        }
    }

    /// <summary>
    /// §إعادة احتساب حجز دفعات بعينها **من الواقع** — بلا افتراض وجود خطة بذاتها.
    /// تلزم بعد حذف خطة: <see cref="ApplyLotReservations"/> تبني الحجز حول خطة قائمة،
    /// وهنا لم تعد قائمة، فيُجمع المتبقي من الخطط النشطة وحدها.
    /// </summary>
    private void RecomputeLotReservations(List<int> lotIds)
    {
        foreach (var lid in lotIds ?? new List<int>())
        {
            var lot = Db.Lots.FirstOrDefault(l => l.Id == lid);
            if (lot == null) continue;
            double reserved = Db.ProductionPlanItems
                .Where(i => i.LotId == lid)
                .Join(Db.ProductionPlans, i => i.PlanId, p => p.Id, (i, p) => new { i, p })
                .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
                .Where(x => !x.i.IsClosed)
                .Select(x => x.i).RemainingKg().Sum();
            lot.ReservedQtyKg = Math.Max(0, reserved);
        }
    }

    /// <summary>§الطاقة اللحظية: المتاح/المستخدم/المتبقي للوردية والخط في تاريخ محدد.</summary>
    public ShiftCapacityInfo GetShiftCapacityInfo(int shiftId, int lineId, string date, int? productId = null, int? excludePlanId = null)
    {
        var (used, eff, rate) = ShiftUsageHours(shiftId, lineId, date, productId ?? 0, excludePlanId);
        var shift = Db.Shifts.FirstOrDefault(s => s.Id == shiftId);
        return new ShiftCapacityInfo
        {
            ShiftName = shift?.ShiftNameAr ?? "-",
            TotalHours = eff,
            UsedHours = used,
            RemainingHours = Math.Max(0, Math.Round(eff - used, 2)),
            HourlyRate = rate,
            MaxCartons = (int)(eff * rate),
            RemainingCartons = (int)(Math.Max(0, eff - used) * rate)
        };
    }

    /// <summary>
    /// §المتاح لصنف محدد من دفعة (مطابق لـ v1.60 product_lot_remaining): يخصم فقط حجوزات
    /// هذا الصنف من الخطط النشطة لا حجوزات الأصناف الأخرى — فلا تداخل بين أصناف الدفعة الواحدة.
    /// سكري: رصيد الدفعة − حجوز سكري فقط | برمي: رصيد الدفعة − حجوز برمي فقط.
    /// </summary>
    public double GetProductLotRemaining(int lotId, int productId, int? excludePlanId = null)
    {
        var lot = Db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == lotId);
        if (lot == null) return 0;

        // حجوزات هذا الصنف فقط من بنود الخطط النشطة (غير المقفلة/الملغاة)
        var planCommitted = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.LotId == lotId && i.ProductId == productId
                        && (excludePlanId == null || i.PlanId != excludePlanId))
            .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
            .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
            .Where(x => !x.i.IsClosed)
            .Select(x => x.i).RemainingKg().Sum();

        // حجوزات هذا الصنف من أوامر الإنتاج النشطة (غير الملغاة/المكتملة)
        var orderCommitted = Db.ProductionOrderItems.AsNoTracking()
            .Where(i => i.LotId == lotId && i.ProductId == productId)
            .Join(Db.ProductionOrders.AsNoTracking(), i => i.OrderId, o => o.Id, (i, o) => new { i, o })
            .Where(x => x.o.Status != DocStatuses.Cancelled && x.o.Status != DocStatuses.Closed)
            .Where(x => !x.i.IsClosed)
            .Sum(x => x.i.PlannedQtyKg - x.i.ProducedQtyKg > 0 ? x.i.PlannedQtyKg - x.i.ProducedQtyKg : 0);

        // §المعالجة والتعقيم (الموضعان 8 و9): ما هو داخل دورة معالجة جارية ليس متاحاً
        return Math.Max(0, Math.Round(lot.InStockQtyKg - lot.UnderTreatmentQtyKg - planCommitted - orderCommitted, 3));
    }

    private void EnsureManagedPlanFresh(ProductionPlan tracked)
    {
        if (!Db.ProductionPlanContexts.AsNoTracking().Any(c => c.PlanId == tracked.Id)) return;
        // The legacy workflow loads its aggregate before opening its transaction.
        // For the sequenced workflow fail closed if any device changed the aggregate
        // between that read and the SERIALIZABLE submission/approval transaction.
        var current = Db.ProductionPlans.AsNoTracking().Include(p => p.Items)
            .FirstOrDefault(p => p.Id == tracked.Id);
        if (current == null || current.IsApproved != tracked.IsApproved || current.Status != tracked.Status
            || !(current.RowVersion ?? Array.Empty<byte>()).SequenceEqual(tracked.RowVersion ?? Array.Empty<byte>())
            || current.Items.Count != tracked.Items.Count
            || current.Items.Any(row => tracked.Items.All(old => old.Id != row.Id
                || !(old.RowVersion ?? Array.Empty<byte>()).SequenceEqual(row.RowVersion ?? Array.Empty<byte>()))))
            throw new DomainException("عدّل مستخدم آخر الخطة أو إحدى بطاقاتها؛ أعد فتحها وراجع أحدث نسخة قبل الاعتماد.");
    }

    /// <summary>§7 — إرسال الخطة للمدير العام للاعتماد الرسمي.</summary>
    public OpResult SubmitPlan(int planId)
    {
        Require("planning", "Edit");
        var plan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == planId);
        if (plan == null) return OpResult.Fail("الخطة غير موجودة.");
        if (plan.IsApproved) return OpResult.Fail("الخطة معتمدة مسبقاً.");
        if (plan.Items.Count == 0) return OpResult.Fail("لا يمكن إرسال خطة بدون بنود.");
        return RunOp(() =>
        {
            EnsureManagedPlanFresh(plan);
            var managedError = SequencedPlanningService.ValidateWorkflow(Db, plan);
            if (managedError != null) throw new DomainException(managedError);
            ValidateCapacity(CapacityItems(plan), plan);
            var oldStatus = plan.Status;
            plan.Status = "UnderApproval";
            plan.StatusReason = null; // §B102 (سحب B97): إعادة الإرسال تمسح لافتة الإرجاع
            SequencedPlanningService.LogWorkflow(Db, plan, Session?.UserId, "Submit", oldStatus, "إرسال جميع البطاقات المكتملة للاعتماد");
            Db.SaveChanges();
            return OpResult.Success("تم إرسال الخطة للاعتماد الرسمي.", plan.Id, plan.DocumentNumber);
        });
    }

    /// <summary>إرجاع الخطة للمخطط للتعديل مع ملاحظات.</summary>
    /// <summary>
    /// §B102 (سحب B97 مع ترحيل آمن): Four-Eyes صارم فقط بمفتاح «Workflow_FourEyes = Strict».
    /// غياب المفتاح = Off — فالمصنع يعمل بمدير واحد يعتمد خططه بنفسه، ولا يُكسر سيره
    /// بالتحديث؛ من يريد الفصل بين المنشئ والمعتمد يضبط المفتاح Strict صراحةً.
    /// </summary>
    private bool FourEyesOff()
    {
        var v = Db.SystemSettings.AsNoTracking()
            .Where(s => s.SettingKey == "Workflow_FourEyes").Select(s => s.SettingValue)
            .FirstOrDefault();
        return !string.Equals(v, "Strict", StringComparison.OrdinalIgnoreCase);
    }

    public OpResult ReturnPlan(int planId, string notes)
    {
        Require("planning", "Approve");
        // §B102 (سحب B97): سبب الإرجاع إجباري — 10 أحرف على الأقل ليعرف الصانع ماذا يصحح
        var reason = notes?.Trim() ?? "";
        if (reason.Length < 10)
            return OpResult.Fail("سبب الإرجاع إجباري — 10 أحرف على الأقل ليعرف الصانع ماذا يصحح.");
        var plan = Db.ProductionPlans.FirstOrDefault(p => p.Id == planId);
        if (plan == null) return OpResult.Fail("الخطة غير موجودة.");
        if (plan.IsApproved) return OpResult.Fail("الخطة معتمدة — استخدم إلغاء الاعتماد.");
        return RunOp(() =>
        {
            var oldStatus = plan.Status;
            plan.Status = DocStatuses.Draft;
            // §B97 — السبب حقل مستقل يظهر لافتةً على بطاقة المهمة، وNotes سابق للمرونة
            plan.StatusReason = reason;
            plan.Notes = "ملاحظات الإرجاع: " + reason;
            SequencedPlanningService.LogWorkflow(Db, plan, Session?.UserId, "Return", oldStatus, reason);
            Db.SaveChanges();
            return OpResult.Success("تم إرجاع الخطة للتعديل.", plan.Id, plan.DocumentNumber);
        });
    }

    public OpResult UnapprovePlan(int planId)
    {
        Require("planning", "Cancel");
        var plan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == planId);
        if (plan == null) return OpResult.Fail("الخطة غير موجودة.");
        if (!plan.IsApproved) return OpResult.Fail("الخطة غير معتمدة.");
        // §v1.50.35: فك الاعتماد بصلاحية Cancel فقط، وبعد إلغاء الحركات اللاحقة (أوامر غير ملغاة).
        if (PlanHasLiveOrderReferences(plan))
            return OpResult.Fail("لا يمكن فك الاعتماد: صدرت حركات لاحقة (أوامر إنتاج قائمة) من هذه الخطة. ألغِ تلك الأوامر أولاً ثم فك الاعتماد.");
        if (PlanHasIrreversibleExecution(plan.Id))
            return OpResult.Fail("لا يمكن فك اعتماد الخطة: دخلت مرحلة التنفيذ الفعلي (ProductionExecutions) — لا يمكن الرجوع.");
        return RunOp(() =>
        {
            var oldStatus = plan.Status;
            plan.IsApproved = false;
            plan.Status = DocStatuses.Draft;
            SequencedPlanningService.LogWorkflow(Db, plan, Session?.UserId, "Unapprove", oldStatus, "فك اعتماد الخطة دون حذف بنودها أو أوامرها");
            ApplyLotReservations(plan); // إعادة احتساب الحجوزات بدون هذه الخطة
            Db.SaveChanges();
            return OpResult.Success("تم إلغاء اعتماد الخطة وإعادة فتحها للتعديل.", plan.Id, plan.DocumentNumber);
        });
    }

    public OpResult DeletePlan(int planId)
    {
        Require("planning", "Delete");
        var plan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == planId);
        if (plan == null) return OpResult.Fail("الخطة غير موجودة.");
        if (Db.ProductionPlanContexts.Any(c => c.PlanId == planId))
            return OpResult.Fail("الخطة المتسلسلة تحفظ تاريخها؛ ألغِ إعدادها رسمياً قبل البنود، ولا تحذفها فعلياً.");
        if (plan.IsApproved) return OpResult.Fail("لا يمكن حذف خطة معتمدة — ألغِ الاعتماد أولاً.");
        // §C1 — الأوامر **الملغاة** لا تحبس خطتها (كان Any بلا استثناء يمنع مسار التصحيح المتسلسل)
        if (Db.ProductionOrders.Any(o => o.SourcePlanId == planId && o.Status != DocStatuses.Cancelled))
            return OpResult.Fail("لا يمكن حذف خطة صدرت منها أوامر إنتاج قائمة (غير ملغاة).");
        // §v1.50.27: حزام أمان — بنود مرجَعة من أوامر بلا SourcePlanId (بيانات قديمة).
        if (PlanHasLiveOrderReferences(plan))
            return OpResult.Fail("لا يمكن حذف الخطة: بنودها مرجَعة من أوامر إنتاج قائمة.");
        if (PlanHasIrreversibleExecution(plan.Id))
            return OpResult.Fail("لا يمكن حذف الخطة: دخلت مرحلة التنفيذ الفعلي — مرحلة غير قابلة للرجوع.");
        return RunOp(() =>
        {
            // §إصلاح تسرّب حجز: الحذف كان يزيل الخطة وبنودها (Cascade) بلا إعادة احتساب
            // الحجوزات، فتبقى ReservedQtyKg على الدفعة محجوزة لخطة لم تعد موجودة —
            // كمية مجمّدة إلى الأبد لا يحرّرها شيء، ولا شاشة تفسّر سببها.
            // نصفّر بنود الخطة أولاً ثم نعيد الاحتساب، فتُطرح حصتها كما في الإقفال.
            var affectedLots = plan.Items.Where(i => i.LotId != null)
                .Select(i => i.LotId.Value).Distinct().ToList();
            plan.Status = DocStatuses.Cancelled;
            plan.IsClosed = true;
            ApplyLotReservations(plan);

            Db.ProductionPlans.Remove(plan);
            Db.SaveChanges();

            // §بعد الحذف الفعلي نعيد الاحتساب من الواقع: أي حجز متبقٍ يعود لخطط أخرى فقط.
            RecomputeLotReservations(affectedLots);
            Db.SaveChanges();
            return OpResult.Success("تم حذف الخطة (المسودة) وتحرير حجوزات دفعاتها.");
        });
    }

    /// <summary>§7 — اعتماد الخطة تصبح معه جاهزة لأوامر الإنتاج.</summary>
    public OpResult ApprovePlan(int planId)
    {
        Require("planning", "Approve");
        var plan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == planId);
        if (plan == null) return OpResult.Fail("الخطة غير موجودة.");
        // §B102 (سحب B97) — Four-Eyes: المُعتمد لا يكون منشئ الخطة (Strict صراحةً؛ الغياب = Off)
        if (plan.CreatedBy != null && plan.CreatedBy == Session?.UserId && !FourEyesOff())
            return OpResult.Fail("المُعتمد لا يكون هو منشئ الخطة (Four-Eyes) — يُعتمد من مستخدم آخر مخوَّل الاعتماد (المدير العام).");
        if (plan.IsApproved) return OpResult.Fail("الخطة معتمدة مسبقاً.");
        if (plan.Items.Count == 0) return OpResult.Fail("لا يمكن اعتماد خطة بدون بنود.");

        // §لا خطة فوق خطة (فحص طاقة لا منع أعمى): يُسمح بخطة ثانية في نفس اليوم والوردية
        // ما دامت الطاقة الفعلية المتبقية تكفي، ويُرفض فقط التجاوز الذي يتعدى الطاقة.
        // §إصلاح حرج: تراكم بنود الخطة نفسها على نفس اليوم/الوردية/الخط عند الاعتماد أيضاً.
        // §المعالجة والتعقيم — حارس الاعتماد: لا تُعتمد خطة على خام لن يكون جاهزاً
        // في تاريخ إنتاجها. الفحص **حسب تاريخ كل بند** لا حسب إجمالي المستلم.
        var treatMsg = CheckTreatmentReadiness(plan);
        if (treatMsg != null) return OpResult.Fail(treatMsg);

        var lotMsg = CheckLotAvailabilityForApproval(plan);
        if (lotMsg != null) return OpResult.Fail(lotMsg);

        return RunOp(() =>
        {
            EnsureManagedPlanFresh(plan);
            // Existing drafts may predate identity validation. Never silently rewrite them at approval.
            foreach (var item in plan.Items.Where(i => i.LotId != null))
            {
                var source = Db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == item.LotId.Value)
                    ?? throw new DomainException("الدفعة المحددة غير موجودة.");
                var sourceOwner = source.CustomerId ?? (source.ShipmentId is int shipId
                    ? Db.Shipments.AsNoTracking().Where(s => s.Id == shipId).Select(s => s.CustomerId).FirstOrDefault() : null);
                if (item.CustomerId != sourceOwner || item.ShipmentId != source.ShipmentId)
                    throw new DomainException("هوية العميل/الشحنة في بند الخطة لا تطابق دفعة الاستلام؛ صحح الخطة رسمياً قبل اعتمادها.", "RECEIVING_IDENTITY");
            }
            // New sequenced plans must still match their frozen customer/shift/day context
            // and explicit raw recipe immediately before approval, not only at submission.
            var managedError = SequencedPlanningService.ValidateWorkflow(Db, plan);
            if (managedError != null) throw new DomainException(managedError);
            // §1.50.66.3 — تحقق نهائي من الكميات والطاقة قبل التثبيت
            var lotMsgInner = CheckLotAvailabilityForApproval(plan);
            if (lotMsgInner != null) throw new DomainException(lotMsgInner);
            var treatMsgInner = CheckTreatmentReadiness(plan);
            if (treatMsgInner != null) throw new DomainException(treatMsgInner);
            ValidateCapacity(CapacityItems(plan), plan);
            var oldStatus = plan.Status;
            plan.IsApproved = true;
            plan.StatusReason = null; // §B102 (سحب B97): الاعتماد يمسح لافتة الإرجاع
            plan.Status = DocStatuses.Approved;
            plan.ApprovedBy = Session?.UserId;
            SequencedPlanningService.LogWorkflow(Db, plan, Session?.UserId, "Approve", oldStatus, "اعتماد نهائي بعد مراجعة الخام والطاقة وجميع البطاقات");
            plan.ApprovedDate = DateTime.Now;
            foreach (var it in plan.Items) it.Status = DocStatuses.Approved;
            Db.SaveChanges();
            return OpResult.Success("تم اعتماد الخطة — أصبحت جاهزة لإنشاء أوامر الإنتاج.", plan.Id, plan.DocumentNumber);
        });
    }

    public OpResult ClosePlan(int planId, string notes)
    {
        // Compatibility API, not a second unchecked closure path: the canonical service
        // governs permission/completeness/transaction/audit; notes do not imply force.
        return new PlanClosureService(Db, Session, Numbering, new AuditService(Db, Session))
            .ClosePlanFinal(planId, notes);

    }

    /// <summary>
    /// §12/§13 — الدفعات المتاحة مع خصم كل الخطط النشطة لكل الأصناف.
    ///
    /// §المعالجة والتعقيم (الموضع 7): يُطرح <c>UnderTreatmentQtyKg</c> أيضاً، ويُحسب
    /// <c>AvailableForDateKg</c> حين يُمرَّر <paramref name="forDate"/>.
    /// **المعامل اختياري بقيمة null = السلوك القديم حرفياً**، فلا تتأثر أي شاشة
    /// قائمة لا تمرّره — التزاماً بمنع حذف أي وظيفة قائمة.
    /// </summary>
    public List<AvailableLotDto> GetAvailableLots(int? customerId = null, DateTime? forDate = null)
    {
        EnsureReceiptTreatmentsCurrent();
        var q = Db.Lots.AsQueryable();
        // §B67: فلترة العميل تشمل الدفعات التي ورثت عميلها من سند الاستلام (بيانات قديمة
        // بلا CustomerId على الدفعة) — فلا «تختفي أصناف العميل» أبداً.
        if (customerId != null)
            q = q.Where(l => l.CustomerId == customerId
                || (l.CustomerId == null && Db.Shipments.Any(x => x.Id == l.ShipmentId && x.CustomerId == customerId)));
        var rows = q.Where(l => l.Status == DocStatuses.Approved && l.InStockQtyKg > 0)
            .Select(l => new AvailableLotDto
            {
                LotId = l.Id,
                LotCode = l.LotCode,
                ProductId = l.ProductId,
                ProductName = Db.Products.Where(p => p.Id == l.ProductId).Select(p => p.ProductNameAr).FirstOrDefault(),
                CustomerId = l.CustomerId,
                CustomerName = Db.Customers.Where(c => c.Id == l.CustomerId).Select(c => c.CustomerName).FirstOrDefault(),
                ShipmentId = l.ShipmentId,
                ShipmentNo = Db.Shipments.Where(x => x.Id == l.ShipmentId).Select(x => x.DocumentNumber).FirstOrDefault(),
                ArrivalDate = Db.Shipments.Where(x => x.Id == l.ShipmentId).Select(x => x.ArrivalDate).FirstOrDefault(),
                InitialQtyKg = l.InitialQtyKg,
                ReservedQtyKg = l.ReservedQtyKg,
                RequiresTreatment = Db.ShipmentItems.Where(i => i.Id == l.ShipmentItemId)
                    .Select(i => i.TreatmentRequired).FirstOrDefault()
                    ?? Db.Products.Where(p => p.Id == l.ProductId).Select(p => p.RequiresTreatment).FirstOrDefault(),
                TreatmentUntilDate = Db.ShipmentItems.Where(i => i.Id == l.ShipmentItemId)
                    .Select(i => i.TreatmentUntilDate).FirstOrDefault(),
                ReadyNowKg = l.TreatmentReadyQtyKg - l.ProducedQtyKg,
                UnderTreatmentKg = l.UnderTreatmentQtyKg,
                // §B64: AvailableQtyKg خاصية محسوبة غير مخزّنة — لا تُترجم في استعلام خادمي؛
                // يُستبدل بالتعبير القابل للترجمة (يطرح الأعمدة المخزّنة).
                RemainingKg = l.InStockQtyKg - l.ReservedQtyKg - l.UnderTreatmentQtyKg
            })
            .Where(l => l.RemainingKg > 0 || (forDate != null && l.UnderTreatmentKg > 0))
            .ToList();

        // §تتبع سحب الخام: إثراء كل دفعة بسياق سطر الاستلام (الوحدة، وزن الوحدة، المستلم بالكيلو)
        // من ShipmentItem عبر ShipmentItemId — يُقرأ في الذاكرة لأن العلاقة تُبنى خارج الاستعلام.
        var srcIds = rows.Select(r => r.ShipmentId).Where(s => s != null).Distinct().ToList();
        var srcByLot = Db.Shipments.AsNoTracking()
            .Where(s => srcIds.Contains(s.Id))
            .Select(s => new { s.Id, s.DocumentNumber })
            .ToDictionary(x => x.Id, x => x.DocumentNumber);
        var shItemIds = Db.Lots.AsNoTracking()
            .Where(l => srcIds.Contains(l.ShipmentId.Value) && rows.Select(r => r.LotId).Contains(l.Id))
            .Select(l => new { l.Id, l.ShipmentItemId })
            .ToDictionary(x => x.Id, x => x.ShipmentItemId);
        var shItemMap = shItemIds.Values.Where(v => v != null).Distinct().ToList();
        var shItemRows = Db.ShipmentItems.AsNoTracking()
            .Where(i => shItemMap.Contains(i.Id))
            .Select(i => new { i.Id, i.ReceiptUnit, i.UnitWeightKg, i.PackageCount, i.TotalWeightKg })
            .ToDictionary(x => x.Id);
        foreach (var r in rows)
        {
            if (shItemIds.TryGetValue(r.LotId, out var siId) && siId != null && shItemRows.TryGetValue(siId.Value, out var si))
            {
                r.ReceiptUnit = si.ReceiptUnit;
                r.UnitWeightKg = si.UnitWeightKg;
                r.ReceivedUnits = si.PackageCount;
                r.ReceivedKg = si.TotalWeightKg;
            }
            else { r.ReceiptUnit = "كجم"; r.UnitWeightKg = 0; r.ReceivedUnits = r.RemainingKg; r.ReceivedKg = r.RemainingKg; }
            r.ShipmentNo ??= srcByLot.TryGetValue(r.ShipmentId ?? 0, out var no) ? no : r.ShipmentNo;
        }

        // §المعالجة والتعقيم: «المتوقع جاهزيته حتى تاريخ الخطة» يُحسب في الذاكرة —
        // تجميع المعالجات لكل دفعة لا يُترجم داخل الاستعلام أعلاه.
            var ids = rows.Where(r => r.RequiresTreatment).Select(r => r.LotId).ToList();
        if (ids.Count > 0)
        {
            var end = forDate?.Date.AddDays(1).AddTicks(-1) ?? Db.BusinessNow;
            var maturing = Db.RawTreatments.AsNoTracking()
                .Where(t => ids.Contains(t.LotId) && t.Status == TreatmentStatuses.InProgress)
                .Where(t => t.ExpectedReadyAt <= end)
                .GroupBy(t => t.LotId)
                .Select(g => new { LotId = g.Key, Kg = g.Sum(x => x.QtyKg - x.ReleasedQtyKg - x.RejectedQtyKg) })
                .ToDictionary(x => x.LotId, x => x.Kg);
            // §تاريخ اكتمال المعالجة الجارية — أقصى ExpectedReadyAt للدورات قيد التشغيل.
            var readyDates = Db.RawTreatments.AsNoTracking()
                .Where(t => ids.Contains(t.LotId) && t.Status == TreatmentStatuses.InProgress)
                .GroupBy(t => t.LotId)
                .Select(g => new { LotId = g.Key, Date = g.Max(x => x.ExpectedReadyAt) })
                .ToDictionary(x => x.LotId, x => x.Date);
            foreach (var rr in rows.Where(r => r.RequiresTreatment))
            {
                if (readyDates.TryGetValue(rr.LotId, out var d)) rr.TreatmentReadyDate = d;
            }

            foreach (var r in rows)
            {
                if (!r.RequiresTreatment)
                {
                    // الصنف الذي لا يشترط معالجة: المتاح كما كان تماماً (قرار س3)
                    r.AvailableForDateKg = r.RemainingKg;
                    continue;
                }
                r.ExpectedReadyByDateKg = maturing.TryGetValue(r.LotId, out var m) ? Math.Max(0, m) : 0;
                r.AvailableForDateKg = Math.Max(0,
                    r.ReadyNowKg + r.ExpectedReadyByDateKg - r.ReservedQtyKg);
            }
        }
        else
        {
            foreach (var r in rows) r.AvailableForDateKg = r.RemainingKg;
        }
        // §مرتبة من الأقدم وصولاً إلى الأحدث (FIFO للمخزن) — النشاط التلقائي يبدأ بالأنفس القديمة.
        return rows.OrderBy(r => r.ArrivalDate ?? DateTime.MaxValue).ThenBy(r => r.LotId).ToList();
    }

    /// <summary>
    /// §1.50.56 — فحص أقدمية FIFO: عند التخطيط لدفعة وصلت يوم 5 بينما توجد دفعات أقدم (يوم 3) لم تُخطط بعد، نبه المستخدم.
    /// </summary>
    public FifoWarningDto CheckFifoWarning(int selectedLotId, List<int> currentPlanLotIds = null)
    {
        currentPlanLotIds ??= new List<int>();
        var allAvailable = GetAvailableLots(null, null);
        var selected = allAvailable.FirstOrDefault(l => l.LotId == selectedLotId);
        if (selected == null)
        {
            // قد تكون الدفعة غير متاحة الآن (محجوزة بالكامل) — لا تنبيه
            return new FifoWarningDto { HasEarlier = false };
        }

        var selectedArrival = selected.ArrivalDate ?? DateTime.MaxValue;
        // دفعات أقدم من المختارة، ليست في الخطة الحالية، وما زالت متاحة
        var earlier = allAvailable
            .Where(l => l.LotId != selectedLotId
                        && !currentPlanLotIds.Contains(l.LotId)
                        && (l.ArrivalDate ?? DateTime.MaxValue) < selectedArrival)
            .OrderBy(l => l.ArrivalDate)
            .ThenBy(l => l.LotId)
            .ToList();

        if (earlier.Count == 0)
            return new FifoWarningDto { HasEarlier = false };

        // بناء رسالة تنبيه: اذكر أقدم 3 عملاء/دفعات
        var top = earlier.Take(3).ToList();
        var lines = string.Join("\n", top.Select(l => $"• العميل «{l.CustomerName ?? "—"}» — دفعة {l.LotCode} وصلت {l.ArrivalDate:dd/MM/yyyy} — متبقي {l.RemainingKg:N1} كجم"));
        var more = earlier.Count > 3 ? $"\n... و{earlier.Count - 3} دفعات أقدم أخرى" : "";
        var msg = $"تنبيه FIFO — لديك دفعات أقدم لم تُخطط بعد:\n{lines}{more}\n\nتحاول الآن التخطيط لدفعة العميل «{selected.CustomerName}» التي وصلت بتاريخ {selected.ArrivalDate:dd/MM/yyyy} (دفعة {selected.LotCode}).\n\nهل تريد الاستمرار وتجاوز الأقدمية؟";

        return new FifoWarningDto
        {
            HasEarlier = true,
            EarlierLots = earlier,
            Message = msg
        };
    }

    // ═══════════════════════════════════════════════════════════
    // § تتبع سحب الخام من الشحنة — سياق الوحدة/الكمية + التحويل + الحارس
    // ═══════════════════════════════════════════════════════════

    /// <summary>سياق كمية الشحنة/الدفعة (الصنف، وحدة الاستلام، المستلم، المتاح بالوحدة والكيلو).</summary>
    public ShipmentQuantityContext GetShipmentQuantityContext(int lotId)
    {
        var lot = Db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == lotId)
                  ?? throw new DomainException("الدفعة غير موجودة.");
        var sItem = lot.ShipmentItemId is int siId
            ? Db.ShipmentItems.AsNoTracking().FirstOrDefault(i => i.Id == siId) : null;
        int rawId = lot.ProductId;
        double availKg = SourceAvailableKg(lotId);
        double uw = sItem?.UnitWeightKg ?? 0;
        double receivedKg = sItem?.TotalWeightKg ?? lot.InitialQtyKg;
        double receivedUnits = sItem?.PackageCount ?? 0;
        string unit = sItem?.ReceiptUnit ?? "كجم";
        return new ShipmentQuantityContext
        {
            ShipmentId = lot.ShipmentId ?? 0,
            ShipmentNo = lot.ShipmentId is int sid
                ? Db.Shipments.AsNoTracking().Where(s => s.Id == sid).Select(s => s.DocumentNumber).FirstOrDefault() : null,
            LotId = lotId,
            RawProductId = rawId,
            RawProductName = Db.Products.AsNoTracking().Where(p => p.Id == rawId).Select(p => p.ProductNameAr).FirstOrDefault(),
            ReceiptUnit = unit,
            UnitWeightKg = uw,
            ReceivedUnits = receivedUnits,
            ReceivedKg = receivedKg,
            AvailableKg = Math.Max(0, availKg),
            AvailableUnits = uw > 0 ? Math.Round(Math.Max(0, availKg) / uw, 2)
                : string.Equals(unit, "كجم", StringComparison.OrdinalIgnoreCase) ? Math.Max(0, availKg) : 0,
            UsedKg = Math.Max(0, receivedKg - availKg)
        };
    }

    /// <summary>الخام المتاح من الشحنة/الدفعة (كجم) — يطرح الحجوزات وما تحت المعالجة.</summary>
    private double SourceAvailableKg(int lotId)
    {
        var lot = Db.Lots.AsNoTracking().Where(l => l.Id == lotId)
            .Select(l => new { l.ShipmentItemId, l.ShipmentId, l.ProductId }).FirstOrDefault()
            ?? throw new DomainException("الدفعة غير موجودة.");
        if (lot.ShipmentItemId != null)
            return Db.Lots.AsNoTracking()
                .Where(l => l.ShipmentItemId == lot.ShipmentItemId)
                .Sum(l => l.InStockQtyKg - l.ReservedQtyKg - l.UnderTreatmentQtyKg);
        return Db.Lots.AsNoTracking()
            .Where(l => l.ShipmentId == lot.ShipmentId && l.ProductId == lot.ProductId)
            .Sum(l => l.InStockQtyKg - l.ReservedQtyKg - l.UnderTreatmentQtyKg);
    }

    /// <summary>
    /// §حسم/توثيق سحب الخام لخطة ما: يقرأ الوحدة ووزنها من سطر الاستلام (لا من اسم الوحدة)،
    /// يحسب المكافئ (كيلو↔وحدات) إن وُجد وزن الوحدة، ويحرس عدم التجاوز عن المتاح.
    /// لا يعتمد أبداً على اسم الوحدة لتحديد طبيعة الصنف.
    /// </summary>
    private void ResolveSourceDraw(ProductionPlanItem item, PlanItemDto dto, Dictionary<int, double> usedPerLot,
        Dictionary<int, double> priorPlanRes)
    {
        if (dto.SourceQtyInUnit < 0 || dto.SourceQtyKg < 0 || dto.SourceUnitWeightKg < 0)
            throw new DomainException("كمية سحب الخام أو وزن وحدته لا يمكن أن تكون سالبة.", "SOURCE_NEGATIVE");
        var hasDraw = dto.SourceQtyInUnit > 0 || dto.SourceQtyKg > 0 || !string.IsNullOrWhiteSpace(dto.SourceUnit);
        if (!hasDraw) return;
        if (dto.LotId is not int lotId)
            throw new DomainException("سحب الخام للتعقّب يتطلب ربط البند بدفعة/شحنة.", "SOURCE_LOT");
        var lot = Db.Lots.AsNoTracking().Where(l => l.Id == lotId)
            .Select(l => new { l.ShipmentItemId, l.ShipmentId }).FirstOrDefault()
            ?? throw new DomainException("الدفعة غير موجودة.");
        var sItem = lot.ShipmentItemId is int siId
            ? Db.ShipmentItems.AsNoTracking().Where(i => i.Id == siId)
                .Select(i => new { i.ReceiptUnit, i.UnitWeightKg }).FirstOrDefault() : null;
        // الوحدة ووزنها من سطر الاستلام (مصدر الحقيقة)، وإن غابا نأخذ ما مرّرته الواجهة.
        string unit = sItem?.ReceiptUnit ?? dto.SourceUnit;
        double uw = sItem?.UnitWeightKg ?? dto.SourceUnitWeightKg;
        double qtyInUnit = dto.SourceQtyInUnit, kg = dto.SourceQtyKg;
        if (qtyInUnit > 0 && kg > 0 && uw > 0
            && Math.Abs(qtyInUnit * uw - kg) > Math.Max(0.01, kg * 0.0001))
            throw new DomainException("كمية سحب الخام بوحدة الاستلام لا تطابق وزنها بالكيلو؛ صحح إحدى القيمتين.", "SOURCE_UNIT_MISMATCH");
        if (qtyInUnit <= 0 && kg <= 0)
            throw new DomainException("أدخل كمية السحب أو الوزن المكافئ.", "SOURCE_QTY");
        if (qtyInUnit <= 0 && kg > 0)
        {
            if (uw <= 0) throw new DomainException("وزن الوحدة غير معروف من سطر الاستلام — أدخل الكمية بالوحدة أو عرّف وزن الوحدة.", "UNIT_WEIGHT");
            qtyInUnit = Math.Round(kg / uw, 2);
        }
        if (kg <= 0 && qtyInUnit > 0) kg = Math.Round(qtyInUnit * (uw > 0 ? uw : 0), 2);
        if (kg <= 0) throw new DomainException("الكمية المسحوبة يجب أن تكون موجبة.", "SOURCE_QTY");

        // المتاح مع استبعاد حجز هذه الخطة نفسها (الثقة المزدوجة عند التعديل).
        double avail = SourceAvailableKg(lotId)
            + (priorPlanRes.TryGetValue(lotId, out var pr) ? pr : 0);
        // §المعالجة: بعد التاريخ تُحتسب الكمية التي ستكتمل (تُضاف للمتاح) — لا يُعامل الخام قيد
        // المعالجة كمستحيل طوال الوقت. قبل التاريخ فإن المتاح يعكس فقط المفرَج فعلياً.
        // §FIX 1.50.70: Math.Max داخل Sum لا يُترجم في EF Core → نحول لتقييم عميل (ToList ثم Sum مع Math.Max)
        if (item.ScheduledDate != null)
        {
            var end = item.ScheduledDate.Value.Date.AddDays(1).AddTicks(-1);
            var maturing = Db.RawTreatments.AsNoTracking()
                .Where(t => t.LotId == lotId && t.Status == TreatmentStatuses.InProgress && t.ExpectedReadyAt <= end)
                .Select(t => t.QtyKg - t.ReleasedQtyKg - t.RejectedQtyKg)
                .ToList()
                .Sum(v => Math.Max(0, v));
            avail += maturing;
        }
        double usedSoFar = usedPerLot.TryGetValue(lotId, out var u) ? u : 0;
        double totalDraw = usedSoFar + kg;
        if (totalDraw > avail + 0.001)
        {
            double availUnits = uw > 0 ? Math.Floor(avail / uw) : 0;
            throw new DomainException(
                $"⛔ تجاوز المتاح فعلياً من الشحنة: المسحوب {totalDraw:N1} كجم / المتاح {avail:N1} كجم " +
                (uw > 0 ? $"({availUnits:N0} وحدة × {uw:N2} كجم)" : "") + ".",
                "SOURCE_OVERFLOW");
        }
        usedPerLot[lotId] = totalDraw;
        item.SourceUnit = unit;
        item.SourceQtyInUnit = qtyInUnit;
        item.SourceUnitWeightKg = uw;
        item.SourceQtyKg = kg;
    }

    /// <summary>حجز هذه الخطة (بحسب المخطط المنتهي) لكل دفعة — لاستثنائه من المتاح عند التعديل.</summary>
    private Dictionary<int, double> PriorReservationByPlan(int planId)
    {
        if (planId <= 0) return new();
        try
        {
            return Db.ProductionPlanItems.AsNoTracking()
                .Where(i => i.PlanId == planId && i.LotId != null)
                .GroupBy(i => i.LotId.Value)
                .Select(g => new { LotId = g.Key, Res = g.Sum(x => x.PlannedQtyKg > 0 && x.PlannedQtyKg > x.ProducedQtyKg
                    ? (x.SourceQtyKg > 0 ? x.SourceQtyKg : x.PlannedQtyKg) * (x.PlannedQtyKg - x.ProducedQtyKg) / x.PlannedQtyKg
                    : 0) })
                .ToDictionary(x => x.LotId, x => x.Res);
        }
        catch { return new(); }
    }

    /// <summary>§B68: الأصناف القابلة للتخطيط من دفعة محددة — يُستبعد كل صنف نفذ رصيده منها.
    /// بلا دفعة (إضافة يدوي/إدارة) تُرجع كل الأصناف التامة.</summary>
    // ═══ §1.50.62 — قوالب ونسخ سندات سابقة (8) ═══
    public List<ProductionPlan> GetTemplates()
    {
        return Db.ProductionPlans.AsNoTracking().Where(p => p.PlanType == "Template").OrderByDescending(p => p.Id).ToList();
    }

    public OpResult SaveAsTemplate(int planId, string templateName)
    {
        var src = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == planId);
        if (src == null) return OpResult.Fail("الخطة غير موجودة.");
        if (Db.ProductionPlanContexts.Any(c => c.PlanId == planId))
            return OpResult.Fail("لا يُحفظ قالب من خطة متسلسلة دون سياق العملاء والورديات ووصفة الخام.");
        if (string.IsNullOrWhiteSpace(templateName)) templateName = $"قالب - {src.PlanTitle}";
        var tpl = new ProductionPlan
        {
            PlanTitle = templateName,
            PlanType = "Template",
            ScopeMode = src.ScopeMode,
            SingleCustomerId = src.SingleCustomerId,
            ShiftId = src.ShiftId,
            LineId = src.LineId,
            Status = DocStatuses.Draft,
            DocumentNumber = Numbering.Next("PLAN"),
            CreatedBy = Session?.UserId
        };
        foreach (var it in src.Items)
        {
            tpl.Items.Add(new ProductionPlanItem
            {
                SourceType = it.SourceType,
                LotId = it.LotId,
                ShipmentId = it.ShipmentId,
                CustomerId = it.CustomerId,
                ProductId = it.ProductId,
                PackagingTypeId = it.PackagingTypeId,
                PlannedQtyKg = it.PlannedQtyKg,
                PlannedCartons = it.PlannedCartons,
                SuggestedShiftId = it.SuggestedShiftId,
                SuggestedLineId = it.SuggestedLineId,
                PriorityNo = it.PriorityNo,
                Status = DocStatuses.Draft
            });
        }
        Db.ProductionPlans.Add(tpl);
        Db.SaveChanges();
        return OpResult.Success($"تم حفظ القالب {tpl.DocumentNumber} — {templateName}", tpl.Id, tpl.DocumentNumber);
    }

    public OpResult CreateFromTemplate(int templateId, string newTitle)
    {
        var tpl = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == templateId && p.PlanType == "Template");
        if (tpl == null) return OpResult.Fail("القالب غير موجود.");
        if (string.IsNullOrWhiteSpace(newTitle)) newTitle = $"من قالب {tpl.PlanTitle}";
        var plan = new ProductionPlan
        {
            PlanTitle = newTitle,
            PlanType = "Daily",
            ScopeMode = tpl.ScopeMode,
            SingleCustomerId = tpl.SingleCustomerId,
            ShiftId = tpl.ShiftId,
            LineId = tpl.LineId,
            StartDate = Db.BusinessNow.Date,
            EndDate = Db.BusinessNow.Date,
            Status = DocStatuses.Draft,
            DocumentNumber = Numbering.Next("PLAN"),
            CreatedBy = Session?.UserId
        };
        foreach (var it in tpl.Items)
        {
            plan.Items.Add(new ProductionPlanItem
            {
                SourceType = it.SourceType,
                LotId = it.LotId,
                ShipmentId = it.ShipmentId,
                CustomerId = it.CustomerId,
                ProductId = it.ProductId,
                PackagingTypeId = it.PackagingTypeId,
                PlannedQtyKg = it.PlannedQtyKg,
                PlannedCartons = it.PlannedCartons,
                ScheduledDate = Db.BusinessNow.Date,
                SuggestedShiftId = it.SuggestedShiftId,
                SuggestedLineId = it.SuggestedLineId,
                PriorityNo = it.PriorityNo,
                Status = DocStatuses.Draft
            });
        }
        Db.ProductionPlans.Add(plan);
        Db.SaveChanges();
        return OpResult.Success($"تم إنشاء خطة {plan.DocumentNumber} من القالب.", plan.Id, plan.DocumentNumber);
    }

    public List<Shipment> GetRecentShipments(int take = 20)
    {
        return Db.Shipments.AsNoTracking().OrderByDescending(s => s.Id).Take(take).ToList();
    }

    public List<ShipmentItem> GetShipmentItems(int shipmentId)
    {
        return Db.ShipmentItems.AsNoTracking().Where(i => i.ShipmentId == shipmentId).ToList();
    }

    public List<Product> GetPlannableProducts(int? lotId = null)
    {
        if (lotId == null) return GetFinishedProducts(); // legacy unscoped catalogue, not a raw selector
        int? rawId = Db.Lots.AsNoTracking().Where(l => l.Id == lotId).Select(l => (int?)l.ProductId).FirstOrDefault();
        if (rawId == null) return new();
        return GetFinishedProductsForRaw(rawId.Value).Where(p => GetProductLotRemaining(lotId.Value, p.Id) > 0).ToList();
    }

    public List<Product> GetFinishedProductsForRaw(int rawProductId)
    {
        if (!Db.Products.AsNoTracking().Any(p => p.Id == rawProductId && p.IsActive && p.ItemType == "Raw")) return new();
        return Db.Products.AsNoTracking().Where(p => p.IsActive && p.ItemType == "Finished" && p.SourceProductId == rawProductId)
            .OrderBy(p => p.ProductNameAr).ThenBy(p => p.Id).ToList();
    }

    /// <summary>
    /// §نافذة الاختيار: الأصناف التامة التي تظهر في قائمة الصنف التام — مطابقة لفلتر v1.59:
    /// (المجموعة 002 أو صنف بلا مجموعة) ونشط. هذا يضمن ظهور أصناف المصنع حتى لو لم تُملأ المجموعة.
    /// </summary>
    /// <summary>§B56: الأصناف التامة فقط — النوع «Finished» هو انتماء مجموعة التام،
    /// فلا تظهر أصناف الخام/الثانوي في قوائم اختيار الصنف التام، وتظهر التامة بمجموعات مخصصة.</summary>
    public List<Product> GetFinishedProducts()
        => Db.Products.AsNoTracking()
            .Where(p => p.IsActive && p.ItemType == "Finished")
            .OrderBy(p => p.ProductNameAr)
            .ToList();

    /// <summary>
    /// ⚖ محرك التوزيع العادل الآلي v2 (B87/H5/M8):
    /// • فلتر التحويل الرسمي: الدفعة لا تُنتَج إلا بالأصناف المسموحة من خامها (بطاقة المنتج) —
    ///   والدوّار يدور بين المسموحة فقط، والدفعة بلا صنف مسموح تُتجاوَز بملاحظة صاخبة لا صفوف وهمية.
    /// • حصة كل (صنف×عبوة) بمعدلها الخاص في كل وردية — لا معدل مرجعي واحد يُفرَض على الجميع.
    /// • الأرصدة واعية بالأوامر: الرصيد − بنود الخطط الحية − بنود الأوامر المستقلة الحية (بلا ازدواج).
    /// • تخطي الجمعة افتراضياً (الأسبوع: السبت–الخميس) — اختياري من المعالج.
    /// • تعبئة كل الورديات النشطة: تبدأ بالأساسية ثم تفيض للبقية — كل بند موسوم بورديته.
    /// • لا بنود صفرية: الفتات دون العبوة الكاملة يُترَك برصيده ويُبلَّغ عنه بصدق (لا تجاوز أبداً).
    /// • DaysUsed = أيام الإنتاج الفعلية فقط (L3).
    /// النتيجة: لا يُغرق السوق بصنف عميل واحد، ولا ينتظر صاحب الحاوية الواحدة خلف أصحاب الحاويات.
    /// </summary>

    /// <summary>
    /// ⚖ محرك التوزيع العادل الآلي v3 (1.50.58):
    /// تحسين 1: الحصة اليومية = متوسط كل الأصناف التامة لكل وردية (لا منتج مرجعي واحد)
    /// تحسين 2: خيار الإنجاز التراكمي من بداية الموسم (1 يناير) — عدالة تاريخية
    /// تحسين 3: سقف العميل/اليوم = حصة اليوم / عدد العملاء النشطين × 1.5 — يمنع دفعة كبيرة تأكل اليوم كله
    /// + كل مزايا v2: فلتر التحويل، حصة كل صنف×عبوة بمعدلها، أرصدة واعية بالأوامر، تخطي الجمعة، تعبئة كل الورديات، لا صفوف صفرية
    /// </summary>
    public FairDistributionProposal SuggestFairDistribution(string startDate, string endDate, int shiftId, int lineId,
        int? targetProductId = null, double? dailyKgOverride = null, bool excludeFriday = true,
        bool useCumulative = false, bool capPerCustomerPerDay = true, bool useFullDay = true)
    {
        Require("planning", "View");
        var result = new FairDistributionProposal();

        if (!UiFormat.TryParseDate(startDate, out var d0) || !UiFormat.TryParseDate(endDate, out var d1) || d1 < d0)
        { result.Message = "تواريخ الخطة غير صحيحة — حدد فترة سليمة (من ≤ إلى)."; return result; }
        var days = new List<DateTime>();
        for (var d = d0.Date; d <= d1.Date; d = d.AddDays(1))
            if (!excludeFriday || d.DayOfWeek != DayOfWeek.Friday) days.Add(d);
        if (days.Count == 0)
        { result.Message = "الفترة لا تحتوي أي يوم عمل — كلها أيام جمعة مستثناة (ألغِ «تخطي الجمعة» من المعالج إن كان المصنع يعمل فيها)."; return result; }

        var shift = Db.Shifts.AsNoTracking().FirstOrDefault(s => s.Id == shiftId);
        if (shift == null) { result.Message = "اختر الوردية أولاً — اختيار إلزامي لمعدل الخطة."; return result; }
        // §1.50.59 تحسين 4: خيار يوم كامل / وردية واحدة فقط — يجبر اختيار الطاقة قبل الإنزال كما طلب المستخدم
        List<Shift> activeShifts;
        if (useFullDay)
        {
            activeShifts = Db.Shifts.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Id).ToList();
            if (activeShifts.Count == 0) activeShifts.Add(shift);
            activeShifts = activeShifts.OrderBy(x => x.Id == shiftId ? 0 : 1).ThenBy(x => x.Id).ToList();
        }
        else
        {
            activeShifts = new List<Shift> { shift }; // وردية واحدة فقط — الأساسية فقط
        }
        double Eff(Shift s2)
        {
            double e = CapacityPolicy.EffectiveHours(s2.EffectiveProductiveHours, s2.TotalHours);
            return e > 0 ? e : 8;
        }

        var fins = targetProductId != null
            ? Db.Products.AsNoTracking().Where(p => p.Id == targetProductId.Value && p.IsActive).ToList()
            : GetFinishedProducts();
        if (fins.Count == 0) { result.Message = "لا توجد أصناف تامة معرفّة — أضفها من شاشة الأصناف أولاً."; return result; }

        var packs = Db.PackagingTypes.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Id).ToList();
        if (packs.Count == 0) { result.Message = "لا توجد عبوات معرفّة — أضف العبوات أولاً."; return result; }

        int DefaultPackId(Product p) => packs.OrderBy(x => Math.Abs(x.UnitWeightKg - p.CartonWeightKg)).First().Id;
        double PackWeight(int packId) => packs.First(p => p.Id == packId).UnitWeightKg;

        var rateCache = new Dictionary<(int fin, int pack, int sh), (double rate, int cap, string src)>();
        (double rate, int cap, string src) RateOf(int finId, int packId, int shId)
        {
            var key = (finId, packId, shId);
            if (!rateCache.TryGetValue(key, out var v))
            { v = CapacityPolicy.Resolve(Db, finId, shId, packId); rateCache[key] = v; }
            return v;
        }

        var skipped = new List<string>();
        // تحسين 1: الحصة اليومية = متوسط كل الأصناف التامة (لا منتج مرجعي واحد)
        if (dailyKgOverride > 0)
        {
            result.DailyQuotaKg = dailyKgOverride.Value;
            result.CapacityNote = $"حصة يومية يدوية {dailyKgOverride.Value:N0} كجم تُوزَّع على كل الورديات النشطة ({activeShifts.Count}) — بمعدل كل صنف في ورديته.";
        }
        else
        {
            bool anyRate = fins.Any(f => activeShifts.Any(sh => { var rr = RateOf(f.Id, DefaultPackId(f), sh.Id); return rr.rate > 0 || rr.cap > 0; }));
            if (!anyRate)
            {
                result.CapacityNote = "لا طاقة معرَّفة لهذه الأصناف في الورديات النشطة — عرّفها من شاشة الأصناف";
                result.Message = result.CapacityNote;
                return result;
            }
            var parts = new List<string>();
            double sum = 0;
            foreach (var sh in activeShifts)
            {
                double totalKgForShift = 0;
                int count = 0;
                foreach (var f in fins)
                {
                    int packId = DefaultPackId(f);
                    double uw = PackWeight(packId);
                    if (uw <= 0) continue;
                    var (rate, cap, _) = RateOf(f.Id, packId, sh.Id);
                    if (rate <= 0 && cap <= 0) continue;
                    int cartons = cap > 0 ? cap : (int)Math.Round(rate * Eff(sh));
                    double kg = cartons * uw;
                    totalKgForShift += kg;
                    count++;
                }
                double avgKg = count > 0 ? totalKgForShift / count : 0;
                sum += avgKg;
                parts.Add($"{sh.ShiftNameAr} ≈ {avgKg:N0} كجم (متوسط {count} صنف)");
            }
            result.DailyQuotaKg = sum;
            result.CapacityNote = $"الحصة اليومية ≈ {sum:N0} كجم = {string.Join(" + ", parts)} (متوسط كل الأصناف التامة — أدق من منتج مرجعي واحد — تحسين 1.50.58)";
        }

        // M8: الالتزامات الحية لكل دفعة
        var planLiveByLot = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.LotId != null && !i.IsClosed)
            .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
            .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
            .ToList()
            .GroupBy(x => x.i.LotId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(x => PlanRawReservation.RemainingKg(x.i)));
        var standaloneLiveByLot = Db.ProductionOrderItems.AsNoTracking()
            .Where(i => i.LotId != null && i.PlanItemId == null && !i.IsClosed)
            .Join(Db.ProductionOrders.AsNoTracking(), i => i.OrderId, o => o.Id, (i, o) => new { i, o })
            .Where(x => x.o.Status != DocStatuses.Cancelled && x.o.Status != DocStatuses.Closed)
            .ToList()
            .GroupBy(x => x.i.LotId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.i.PlannedQtyKg - x.i.ProducedQtyKg > 0 ? x.i.PlannedQtyKg - x.i.ProducedQtyKg : 0));

        // تحسين 2: الإنجاز التراكمي من بداية الموسم (1 يناير) — عدالة تاريخية
        var histByCust = new Dictionary<int, double>();
        if (useCumulative)
        {
            var yearStart = new DateTime(DateTime.Today.Year, 1, 1);
            try
            {
                histByCust = Db.ProductionPlanItems.AsNoTracking()
                    .Where(i => i.CustomerId != null && !i.IsClosed)
                    .Join(Db.ProductionPlans.AsNoTracking().Where(p => p.IsApproved && p.StartDate >= yearStart), i => i.PlanId, p => p.Id, (i, p) => i)
                    .GroupBy(i => i.CustomerId!.Value)
                    .ToDictionary(g => g.Key, g => g.Sum(x => x.PlannedQtyKg));
            }
            catch { histByCust = new Dictionary<int, double>(); }
        }

        var lotsRaw = (from l in Db.Lots.AsNoTracking()
                       where l.InStockQtyKg > 0.01
                       join s in Db.Shipments.AsNoTracking() on l.ShipmentId equals s.Id into sj
                       from s in sj.DefaultIfEmpty()
                       orderby s.ArrivalDate ascending, l.Id ascending
                       select new { l, s }).ToList();
        var rawNames = Db.Products.AsNoTracking().AsEnumerable().ToDictionary(p => p.Id, p => p.ProductNameAr);
        var custNames = Db.Customers.AsNoTracking().AsEnumerable().ToDictionary(c => c.Id, c => c.CustomerName);
        var custPrio = Db.Customers.AsNoTracking().AsEnumerable().ToDictionary(c => c.Id, c => c.PriorityNo);
        var today = DateTime.Today;

        var allowedCache = new Dictionary<int, List<Product>>();
        List<Product> AllowedFor(int lotId)
        {
            if (!allowedCache.TryGetValue(lotId, out var list))
            {
                list = fins.Where(f => ProductIdentityGuard.CheckConversion(Db, f.Id, lotId) == null).ToList();
                allowedCache[lotId] = list;
            }
            return list;
        }

        var buckets = new Dictionary<int, CustBucket>();
        foreach (var row in lotsRaw)
        {
            var l = row.l;
            var s = row.s;
            double committed = (planLiveByLot.TryGetValue(l.Id, out var pl) ? pl : 0)
                             + (standaloneLiveByLot.TryGetValue(l.Id, out var so) ? so : 0);
            double remaining = Math.Max(0, l.InStockQtyKg - l.UnderTreatmentQtyKg - committed);
            if (remaining <= 0.01) continue;
            var allowed = AllowedFor(l.Id);
            if (allowed.Count == 0)
            {
                skipped.Add(targetProductId != null
                    ? $"⛔ الدفعة {l.LotCode}: الصنف المطلوب «{fins[0].ProductNameAr}» غير مسموح إنتاجه من خامها — راجع بطاقة المنتج."
                    : $"⛔ الدفعة {l.LotCode} (خام: {(rawNames.TryGetValue(l.ProductId, out var rn0) ? rn0 : "—")}): لا يوجد أي صنف تام مسموح — عرّف التحويل.");
                continue;
            }
            if (dailyKgOverride <= 0)
            {
                bool hasRate = allowed.Any(f => activeShifts.Any(sh => { var rr = RateOf(f.Id, DefaultPackId(f), sh.Id); return rr.rate > 0 || rr.cap > 0; }));
                if (!hasRate)
                {
                    skipped.Add($"⛔ الدفعة {l.LotCode}: أصنافها المسموحة ({string.Join("، ", allowed.Select(f => f.ProductNameAr))}) بلا معدل — عرّف الطاقة.");
                    continue;
                }
            }
            int? cid = l.CustomerId;
            int cidKey = cid ?? 0;
            if (!buckets.TryGetValue(cidKey, out var b))
            {
                b = new CustBucket
                {
                    CustomerId = cid,
                    Name = cid != null && custNames.TryGetValue(cid.Value, out var nm) ? nm : "بدون عميل",
                    OldestArrival = s?.ArrivalDate ?? DateTime.MaxValue
                };
                b.PriorityNo = cid != null && custPrio.TryGetValue(cid.Value, out var pr) ? pr : 0;
                buckets[cidKey] = b;
            }
            var seed = new LotSeed
            {
                Id = l.Id, LotCode = l.LotCode, CustomerId = cid, ShipmentId = l.ShipmentId,
                RawProductId = l.ProductId, Remaining = remaining,
                ArrivalDate = s?.ArrivalDate, ContainerNumber = s?.ContainerNumber, ShipmentNo = s?.DocumentNumber
            };
            seed.DaysInStock = seed.ArrivalDate != null ? Math.Max(0, (today - seed.ArrivalDate.Value.Date).Days) : 0;
            b.Lots.Add(seed);
            b.TotalAvailable += remaining;
            b.ShipmentIds.Add(l.ShipmentId ?? 0);
            if (seed.ArrivalDate != null && seed.ArrivalDate < b.OldestArrival) b.OldestArrival = seed.ArrivalDate.Value;
        }
        if (buckets.Count == 0)
        {
            result.SkippedNotes = skipped;
            result.Message = skipped.Count > 0
                ? "لا يمكن بناء اقتراح — كل الدفعات المتاحة مرفوضة:\n" + string.Join("\n", skipped.Take(5)) + (skipped.Count > 5 ? $"\n…و {skipped.Count - 5} ملاحظات أخرى." : "")
                : "لا توجد أرصدة خام متاحة للتوزيع — استلم شحنات واعتمدها أولاً.";
            return result;
        }

        // تحسين 2: تطبيق الإنجاز التراكمي على النسبة
        foreach (var b in buckets.Values)
        {
            b.Remaining = b.TotalAvailable;
            if (useCumulative && b.CustomerId != null && histByCust.TryGetValue(b.CustomerId.Value, out var h))
            {
                b.HistoricalKg = h;
                b.Allocated = h;
                double denom = h + b.TotalAvailable;
                b.Ratio = denom > 0 ? h / denom : 0;
            }
            else
            {
                b.HistoricalKg = 0;
                b.Allocated = 0;
                b.Ratio = 0;
            }
        }
        double totalRemaining = buckets.Values.Sum(b => b.TotalAvailable);
        var rows = new List<FairPlanRowDto>();
        var producedDays = new HashSet<DateTime>();
        var usedSources = new HashSet<string>();
        int prio = 1;

        // تحسين 3: سقف العميل/اليوم يمنع دفعة كبيرة تأكل اليوم كله
        foreach (var day in days)
        {
            if (totalRemaining <= 0.01) break;
            double dayKgLeft = dailyKgOverride > 0 ? dailyKgOverride.Value : result.DailyQuotaKg > 0 ? result.DailyQuotaKg : double.MaxValue;
            string dayStr = day.ToString("dd/MM/yyyy");
            var perCustToday = new Dictionary<int, double>(); // تحسين 3: تتبع حصة العميل اليوم
            foreach (var sh in activeShifts)
            {
                if (totalRemaining <= 0.01) break;
                if (dayKgLeft <= 0.01) break;
                double slotHoursLeft = Eff(sh);
                var usedCap = new Dictionary<int, int>();
                var tried = new HashSet<int?>();
                while (slotHoursLeft > 0.0001 && totalRemaining > 0.01 && dayKgLeft > 0.01)
                {
                    // تحسين 3: حساب عدد العملاء النشطين لحساب السقف اليومي للعميل
                    int activeCustCount = buckets.Values.Count(c => c.Remaining > 0.01 && !tried.Contains(c.CustomerId));
                    double perCustCap = activeCustCount > 0 && capPerCustomerPerDay
                        ? dayKgLeft / activeCustCount * 1.5
                        : double.MaxValue;

                    var next = buckets.Values.Where(c => c.Remaining > 0.01 && !tried.Contains(c.CustomerId))
                        .OrderBy(c => c.PriorityNo == 0 ? int.MaxValue : c.PriorityNo).ThenBy(c => c.Ratio).ThenBy(c => c.OldestArrival).FirstOrDefault();
                    if (next == null) break;
                    int nextKey = next.CustomerId ?? 0;
                    double todayForCust = perCustToday.TryGetValue(nextKey, out var t) ? t : 0;
                    if (capPerCustomerPerDay && todayForCust >= perCustCap - 0.01)
                    {
                        tried.Add(next.CustomerId);
                        continue;
                    }
                    double need = Math.Min(dayKgLeft, next.Remaining);
                    if (capPerCustomerPerDay)
                        need = Math.Min(need, perCustCap - todayForCust);
                    bool emitted = false;
                    foreach (var lot in next.Lots.Where(x => x.Remaining > 0.01).ToList())
                    {
                        var allowed = AllowedFor(lot.Id);
                        if (allowed.Count == 0) continue;
                        var fin = allowed[lot.PickNo % allowed.Count];
                        int packId = DefaultPackId(fin);
                        double uw = PackWeight(packId);
                        if (uw <= 0) continue;
                        var (rate, cap, rateSrc) = RateOf(fin.Id, packId, sh.Id);
                        int already = usedCap.TryGetValue(fin.Id, out var u) ? u : 0;
                        int maxByCap = cap > 0 ? Math.Max(0, cap - already) : int.MaxValue;
                        int maxByHours = rate > 0 ? (int)Math.Floor(slotHoursLeft * rate + 1e-6)
                            : (dailyKgOverride > 0 ? int.MaxValue : 0);
                        double take = Math.Min(need, lot.Remaining);
                        int cartons = (int)Math.Min(Math.Floor(take / uw + 1e-6),
                            Math.Min((double)maxByHours, (double)maxByCap));
                        if (cartons < 1) continue;
                        double kg = Math.Round(cartons * uw, 1);

                        rows.Add(new FairPlanRowDto
                        {
                            PriorityNo = prio++,
                            Date = dayStr,
                            ShiftId = sh.Id,
                            ShiftName = sh.ShiftNameAr,
                            CustomerId = next.CustomerId,
                            CustomerName = next.Name,
                            ShipmentId = lot.ShipmentId,
                            ShipmentNo = lot.ShipmentNo ?? "—",
                            ContainerNumber = lot.ContainerNumber ?? "—",
                            ArrivalDate = lot.ArrivalDate?.ToString("dd/MM/yyyy") ?? "—",
                            DaysInStock = lot.DaysInStock,
                            LotId = lot.Id,
                            LotCode = lot.LotCode,
                            RawName = rawNames.TryGetValue(lot.RawProductId, out var rn) ? rn : "—",
                            AvailableKg = Math.Round(lot.Remaining, 1),
                            ProductId = fin.Id,
                            ProductName = fin.ProductNameAr,
                            PackagingTypeId = packId,
                            PackName = packs.First(p => p.Id == packId).PackageNameAr,
                            PlannedCartons = cartons,
                            PlannedQtyKg = kg
                        });
                        if (!next.Days.Contains(day)) next.Days.Add(day);
                        producedDays.Add(day);

                        next.AllocatedCartons += cartons;
                        lot.PickNo++;
                        lot.Remaining -= kg; next.Remaining -= kg; totalRemaining -= kg; dayKgLeft -= kg;
                        perCustToday[nextKey] = todayForCust + kg;
                        if (rate > 0) slotHoursLeft -= cartons / rate;
                        if (cap > 0) usedCap[fin.Id] = already + cartons;
                        next.Allocated += kg;
                        double denom = next.Allocated + next.Remaining;
                        next.Ratio = denom > 0 ? next.Allocated / denom : 1.0;
                        if (!string.IsNullOrWhiteSpace(rateSrc))
                            usedSources.Add($"{fin.ProductNameAr} × {sh.ShiftNameAr}: {rate:0.#} كرتون/ساعة ← {rateSrc}");
                        if (lot.Remaining <= 0.01) next.Lots.Remove(lot);
                        emitted = true;
                        break;
                    }
                    if (!emitted) tried.Add(next.CustomerId);
                }
            }
        }

        result.Rows = rows;
        result.Customers = buckets.Values.Select(b => new FairCustomerSummaryDto
        {
            CustomerId = b.CustomerId,
            CustomerName = b.Name,
            ContainersCount = b.ShipmentIds.Where(x => x > 0).Distinct().Count(),
            TotalAvailableKg = Math.Round(b.TotalAvailable, 1),
            AllocatedKg = Math.Round(b.Allocated - b.HistoricalKg, 1),
            AllocatedCartons = b.AllocatedCartons,
            ProgressRatio = Math.Round(b.Ratio * 100, 1),
            ProductionDays = b.Days.OrderBy(x => x).Select(x => x.ToString("MM-dd")).ToList(),
            HistoricalKg = Math.Round(b.HistoricalKg, 1)
        }).OrderByDescending(c => c.TotalAvailableKg).ToList();
        result.TotalRemainingKg = Math.Round(totalRemaining, 1);
        result.DaysUsed = producedDays.Count;
        result.SkippedNotes = skipped;

        result.Ok = rows.Count > 0;
        result.Message = $"⚖ اقتراح توزيع عادل v3 (1.50.58): {rows.Count} بنداً على {result.DaysUsed} يوماً لـ{buckets.Count} عملاء — حصة يومية ≈ {result.DailyQuotaKg:N0} كجم.";
        if (useCumulative) result.Message += " (مع إنجاز تراكمي من بداية الموسم)";
        if (capPerCustomerPerDay) result.Message += " (مع سقف عميل/يوم)";
        if (totalRemaining > 0.01)
            result.Message += $"\n⚠ متبقٍ {totalRemaining:N0} كجم لا تسعها مدة الخطة — وسّع الفترة أو زد الحصة اليومية.";
        if (skipped.Count > 0)
            result.Message += "\n" + string.Join("\n", skipped.Take(3)) + (skipped.Count > 3 ? $"\n…و {skipped.Count - 3} ملاحظات تجاوُز أخرى." : "");
        if (usedSources.Count > 0)
            result.CapacityNote = (result.CapacityNote ?? "") + "\nمعدلات مستخدمة: " + string.Join("؛ ", usedSources.Take(4)) + (usedSources.Count > 4 ? "…" : "");
        return result;
    }


    private class LotSeed
    {
        public int Id; public string LotCode; public int? CustomerId; public int? ShipmentId;
        public int RawProductId; public double Remaining; public DateTime? ArrivalDate;
        public string ContainerNumber; public string ShipmentNo; public int DaysInStock;
        /// <summary>§B87: عدّاد الدوّار بين الأصناف المسموحة لهذه الدفعة.</summary>
        public int PickNo;
    }

    private class CustBucket
    {
        public int? CustomerId; public string Name; public List<LotSeed> Lots = new();
        public double TotalAvailable; public double Remaining;
        public double Allocated; public int AllocatedCartons;
        public double Ratio; public DateTime OldestArrival = DateTime.MaxValue;
        public int PriorityNo;
        public HashSet<int> ShipmentIds = new(); public List<DateTime> Days = new();
        public double HistoricalKg; // §1.50.58 تحسين 2: الإنجاز التراكمي
    }

    /// <summary>
    /// §B91 — فحص الخطة متعددة العملاء بنفس عمل محرك التوزيع:
    /// أيام العمل (تخطي الجمعة)، الورديات النشطة (وردية الخطة أولاً)، أولوية العميل ثم الأقل إنجازاً،
    /// معدلات (صنف×عبوة×وردية) بالكرتون/ساعة، سقوف الكراتين، التحويل الرسمي، والالتزامات الحية (M8) بلا ازدواج.
    /// يحاكي التوزيع يوماً بيوم: الحصة اليومية + المرحّل، والفائض يرحّل لليوم التالي،
    /// والباقي بعد آخر يوم = عجز صريح. لا يتجاوز الطاقة أبداً ولا ينتج صفوفاً صفرية.
    /// </summary>
    public PlanCheckResult CheckPlan(int planId, bool excludeFriday = true)
    {
        Require("planning", "View");
        var result = new PlanCheckResult();

        var plan = Db.ProductionPlans.Include(p => p.Items).AsNoTracking()
            .FirstOrDefault(p => p.Id == planId);
        if (plan == null)
        { result.Verdict = "الخطة غير موجودة."; return result; }
        result.PlanNumber = plan.DocumentNumber;
        result.PlanTitle = plan.PlanTitle;
        var items = plan.Items.Where(i => !i.IsClosed && i.PlannedQtyKg > 0.01).ToList();
        result.ItemsCount = items.Count;
        if (items.Count == 0)
        { result.Verdict = "الخطة بلا بنود قابلة للفحص — أضف بنوداً بكميات أولاً."; return result; }

        DateTime d0 = plan.StartDate?.Date ?? DateTime.Today;
        DateTime d1 = (plan.EndDate ?? plan.StartDate)?.Date ?? DateTime.Today;
        if (d1 < d0)
        { result.Verdict = "تواريخ الخطة غير صحيحة — تاريخ النهاية قبل البداية."; return result; }
        var days = new List<DateTime>();
        for (var d = d0; d <= d1; d = d.AddDays(1))
            if (!excludeFriday || d.DayOfWeek != DayOfWeek.Friday) days.Add(d);
        result.WorkDays = days.Count;
        if (days.Count == 0)
        { result.Verdict = "الفترة (من–إلى) لا تحتوي أي يوم عمل — كلها أيام جمعة مستثناة."; return result; }

        var activeShifts = Db.Shifts.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Id).ToList();
        if (plan.ShiftId != null)
            activeShifts = activeShifts.OrderBy(x => x.Id == plan.ShiftId.Value ? 0 : 1).ThenBy(x => x.Id).ToList();
        if (activeShifts.Count == 0)
        { result.Verdict = "لا توجد ورديات نشطة — فعّل وردية من شاشة الورديات أولاً."; return result; }
        double Eff(Shift s2)
        {
            double e = CapacityPolicy.EffectiveHours(s2.EffectiveProductiveHours, s2.TotalHours);
            return e > 0 ? e : 8;
        }

        var packs = Db.PackagingTypes.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Id).ToList();
        if (packs.Count == 0)
        { result.Verdict = "لا توجد عبوات معرفّة — أضف العبوات أولاً."; return result; }
        var products = Db.Products.AsNoTracking().ToDictionary(p => p.Id);
        int DefaultPackId(Product p) => packs.OrderBy(x => Math.Abs(x.UnitWeightKg - p.CartonWeightKg)).First().Id;
        double PackWeight(int packId) => packs.First(p => p.Id == packId).UnitWeightKg;

        var rateCache = new Dictionary<(int fin, int pack, int sh), (double rate, int cap, string src)>();
        (double rate, int cap, string src) RateOf(int finId, int packId, int shId)
        {
            var key = (finId, packId, shId);
            if (!rateCache.TryGetValue(key, out var v))
            { v = CapacityPolicy.Resolve(Db, finId, shId, packId); rateCache[key] = v; }
            return v;
        }

        var custNames = Db.Customers.AsNoTracking().AsEnumerable().ToDictionary(c => c.Id, c => c.CustomerName);
        var custPrio = Db.Customers.AsNoTracking().AsEnumerable().ToDictionary(c => c.Id, c => c.PriorityNo);
        var lots = Db.Lots.AsNoTracking().ToDictionary(l => l.Id);
        string CustName(int? cid) => cid != null && custNames.TryGetValue(cid.Value, out var nm) ? nm : "بدون عميل";

        // §M8 بلا ازدواج وبلا احتساب ذاتي: التزامات الخطط الأخرى + الأوامر المستقلة الحية فقط
        var planLiveByLot = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.LotId != null && !i.IsClosed && i.PlanId != planId)
            .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
            .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
            .ToList()
            .GroupBy(x => x.i.LotId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(x => PlanRawReservation.RemainingKg(x.i)));
        var standaloneLiveByLot = Db.ProductionOrderItems.AsNoTracking()
            .Where(i => i.LotId != null && i.PlanItemId == null && !i.IsClosed)
            .Join(Db.ProductionOrders.AsNoTracking(), i => i.OrderId, o => o.Id, (i, o) => new { i, o })
            .Where(x => x.o.Status != DocStatuses.Cancelled && x.o.Status != DocStatuses.Closed)
            .ToList()
            .GroupBy(x => x.i.LotId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.i.PlannedQtyKg - x.i.ProducedQtyKg > 0 ? x.i.PlannedQtyKg - x.i.ProducedQtyKg : 0));
        var lotRemaining = new Dictionary<int, double>();
        double LotAvail(int lotId)
        {
            if (!lotRemaining.TryGetValue(lotId, out var v))
            {
                double stock = lots.TryGetValue(lotId, out var l) ? l.InStockQtyKg : 0;
                double committed = (planLiveByLot.TryGetValue(lotId, out var pl) ? pl : 0)
                                 + (standaloneLiveByLot.TryGetValue(lotId, out var so) ? so : 0);
                v = Math.Max(0, stock - committed);
                lotRemaining[lotId] = v;
            }
            return v;
        }

        // ── تحويل البنود إلى طلبات فحص + فحوصات صاخبة (معدل/تحويل/دفعة) ──
        // §البنود تُوزَّع بحرية على أيام الفترة (تواريخ البنود المحفوظة لا تقيّد الفحص — كالتوزيع تماماً).
        var demands = new List<CheckDemand>();
        var usedSources = new HashSet<string>();
        foreach (var it in items)
        {
            if (!products.TryGetValue(it.ProductId, out var prod))
            { result.Warnings.Add($"⛔ بند بلا صنف معروف (معرف {it.ProductId}) — راجع بيانات الخطة."); continue; }
            int packId = it.PackagingTypeId ?? DefaultPackId(prod);
            double uw = packs.Any(p => p.Id == packId) ? PackWeight(packId) : 0;
            if (uw <= 0)
            { result.Warnings.Add($"⛔ «{prod.ProductNameAr}»: العبوة بلا وزن — عرّف وزن العبوة أولاً."); continue; }
            bool hasRate = activeShifts.Any(sh => RateOf(prod.Id, packId, sh.Id).rate > 0
                || RateOf(prod.Id, packId, sh.Id).cap > 0);
            if (!hasRate)
            {
                result.Warnings.Add($"⛔ «{prod.ProductNameAr}» ({CustName(it.CustomerId)}): بلا معدل ولا سقف في أي وردية نشطة — عرّف الطاقة من شاشة الأصناف.");
                demands.Add(new CheckDemand { Item = it, PackId = packId, UnitWeight = uw, Blocked = true });
                continue;
            }
            if (it.LotId != null)
            {
                string conv = ProductIdentityGuard.CheckConversion(Db, prod.Id, it.LotId);
                if (conv != null)
                {
                    result.Warnings.Add($"⛔ «{prod.ProductNameAr}» من الدفعة {(lots.TryGetValue(it.LotId.Value, out var lx) ? lx.LotCode : "?")}: {conv}");
                    demands.Add(new CheckDemand { Item = it, PackId = packId, UnitWeight = uw, Blocked = true });
                    continue;
                }
            }
            var dm = new CheckDemand { Item = it, PackId = packId, UnitWeight = uw, Remaining = it.PlannedQtyKg };
            // §الطاقة اليومية لهذا البند وحده — نفس رياضيات حصة التوزيع (سقف أو معدل×ساعات × وزن العبوة)
            double dayCap = 0;
            foreach (var sh in activeShifts)
            {
                var (rate, cap, src) = RateOf(prod.Id, packId, sh.Id);
                int cartons = cap > 0 ? cap : (rate > 0 ? (int)Math.Round(rate * Eff(sh)) : 0);
                dayCap += Math.Round(cartons * uw, 1);
                if (!string.IsNullOrWhiteSpace(src))
                    usedSources.Add($"{prod.ProductNameAr} × {sh.ShiftNameAr}: {rate:0.#} كرتون/ساعة ← {src}");
            }
            dm.DailyCapKg = dayCap;
            demands.Add(dm);
        }

        // ── المحاكاة: يوم ← ورديات ← عملاء (أولوية ثم أقل إنجازاً) ← بنود ──
        var buckets = demands.Where(d => !d.Blocked).GroupBy(d => d.Item.CustomerId)
            .Select(g => new CheckBucket
            {
                CustomerId = g.Key,
                Name = CustName(g.Key),
                PriorityNo = g.Key != null && custPrio.TryGetValue(g.Key.Value, out var pr) ? pr : 0,
                Demands = g.ToList()
            }).ToList();
        foreach (var b in buckets) { b.Required = b.Demands.Sum(d => d.Item.PlannedQtyKg); b.Remaining = b.Required; }
        double totalRequired = demands.Sum(d => d.Item.PlannedQtyKg);
        result.RequiredKg = Math.Round(totalRequired, 1);
        double dailyTarget = days.Count > 0 ? totalRequired / days.Count : totalRequired;

        double backlog = 0;
        var lotUsedGlobal = new Dictionary<int, double>(); // §استهلاك الدفعات مشترك بين كل بنود الخطة
        foreach (var day in days)
        {
            double dayDemand = dailyTarget + backlog;
            double dayAlloc = 0, hoursUsed = 0;
            double hoursTotal = activeShifts.Sum(Eff);
            if (buckets.Any(b => b.Remaining > 0.01))
            {
                foreach (var sh in activeShifts)
                {
                    double slotHoursLeft = Eff(sh);
                    var usedCap = new Dictionary<int, int>();
                    var tried = new HashSet<int?>();
                    while (slotHoursLeft > 0.0001 && buckets.Any(b => b.Remaining > 0.01))
                    {
                        var next = buckets.Where(b => b.Remaining > 0.01 && !tried.Contains(b.CustomerId))
                            .OrderBy(b => b.PriorityNo == 0 ? int.MaxValue : b.PriorityNo)
                            .ThenBy(b => b.Ratio).ThenBy(b => b.CustomerId ?? int.MaxValue).FirstOrDefault();
                        if (next == null) break;
                        bool emitted = false;
                        foreach (var dm in next.Demands.Where(d => d.Remaining > 0.01).ToList())
                        {
                            // §عجز الدفعة لحظياً: لا يستهلك بندٌ خاماً فوق المتاح للدفعة (مشترك مع بنود الخطة)
                            double lotCap = double.MaxValue;
                            if (dm.Item.LotId != null)
                                lotCap = LotAvail(dm.Item.LotId.Value) - lotUsedGlobal.GetValueOrDefault(dm.Item.LotId.Value);
                            var (rate, cap, _) = RateOf(dm.Item.ProductId, dm.PackId, sh.Id);
                            int already = usedCap.TryGetValue(dm.Item.ProductId, out var u) ? u : 0;
                            int maxByCap = cap > 0 ? Math.Max(0, cap - already) : int.MaxValue;
                            int maxByHours = rate > 0 ? (int)Math.Floor(slotHoursLeft * rate + 1e-6) : 0;
                            double take = Math.Min(dm.Remaining, lotCap);
                            int cartons = (int)Math.Min(Math.Floor(take / dm.UnitWeight + 1e-6),
                                Math.Min((double)maxByHours, (double)maxByCap));
                            if (cartons < 1) continue; // فتات أو سقف/ساعات ممتلئة — البند التالي
                            double kg = Math.Round(cartons * dm.UnitWeight, 1);
                            dm.Remaining -= kg; dm.Covered += kg;
                            next.Remaining -= kg; next.Covered += kg;
                            double denom = next.Covered + next.Remaining;
                            next.Ratio = denom > 0 ? next.Covered / denom : 1.0;
                            if (dm.Item.LotId != null)
                                lotUsedGlobal[dm.Item.LotId.Value] = lotUsedGlobal.GetValueOrDefault(dm.Item.LotId.Value) + kg;
                            if (rate > 0) { slotHoursLeft -= cartons / rate; hoursUsed += cartons / rate; }
                            if (cap > 0) usedCap[dm.Item.ProductId] = already + cartons;
                            dayAlloc += kg;
                            emitted = true;
                            break;
                        }
                        if (!emitted) tried.Add(next.CustomerId);
                    }
                }
            }
            backlog = Math.Max(0, dayDemand - dayAlloc);
            bool tight = hoursTotal > 0 && hoursUsed / hoursTotal >= 0.99;
            string status = backlog > 0.01 ? "Short" : (dayDemand <= 0.01 ? "Idle" : (tight ? "Full" : "Easy"));
            result.Days.Add(new PlanCheckDayDto
            {
                Date = day.ToString("dd/MM/yyyy"),
                DemandKg = Math.Round(dayDemand, 1),
                AllocatedKg = Math.Round(dayAlloc, 1),
                HoursUsed = Math.Round(hoursUsed, 1),
                HoursTotal = Math.Round(hoursTotal, 1),
                LoadPct = hoursTotal > 0 ? (int)Math.Round(hoursUsed / hoursTotal * 100) : 0,
                Status = status,
                StatusAr = status switch { "Short" => "🔴 عجز مرحّل", "Full" => "🟡 ممتلئ", "Idle" => "⚪ بلا حمل", _ => "🟢 مريح" }
            });
        }

        // ── التغطيات والحكم ──
        foreach (var b in buckets)
        {
            double sh = Math.Round(Math.Max(0, b.Required - b.Covered), 1);
            result.Customers.Add(new PlanCheckCustomerDto
            {
                CustomerId = b.CustomerId, CustomerName = b.Name,
                RequiredKg = Math.Round(b.Required, 1), CoveredKg = Math.Round(b.Covered, 1), ShortageKg = sh,
                StatusAr = sh > 0.01 ? "🔴 عجز" : "🟢 مغطى"
            });
        }
        foreach (var dm in demands)
        {
            double sh = Math.Round(Math.Max(0, dm.Item.PlannedQtyKg - dm.Covered), 1);
            var prod = products[dm.Item.ProductId];
            result.Items.Add(new PlanCheckItemDto
            {
                CustomerName = CustName(dm.Item.CustomerId),
                ProductName = prod.ProductNameAr,
                LotCode = dm.Item.LotId != null && lots.TryGetValue(dm.Item.LotId.Value, out var l) ? l.LotCode : "يدوي",
                RequiredKg = Math.Round(dm.Item.PlannedQtyKg, 1),
                CoveredKg = Math.Round(dm.Covered, 1),
                DailyCapKg = Math.Round(dm.DailyCapKg, 1),
                DaysNeeded = dm.DailyCapKg > 0 ? Math.Round(dm.Item.PlannedQtyKg / dm.DailyCapKg, 1) : double.PositiveInfinity,
                StatusAr = dm.Blocked ? "⛔ محجوب" : (sh > 0.01 ? "🔴 عجز" : "🟢 مغطى")
            });
        }
        // §عجز الدفعات: بنود مربوطة بدفعة استهلكت فوق المتاح لها (بعد خصم التزامات الغير)
        foreach (var g in demands.Where(d => !d.Blocked && d.Item.LotId != null).GroupBy(d => d.Item.LotId!.Value))
        {
            double avail = LotAvail(g.Key);
            double need = g.Sum(d => d.Item.PlannedQtyKg);
            if (need - avail > 0.01)
                result.Warnings.Add($"⚠ الدفعة {(lots.TryGetValue(g.Key, out var l) ? l.LotCode : "?")}: بنود الخطة تحتاج {need:N1} كجم والمتاح لها {avail:N1} كجم (بعد التزامات الخطط/الأوامر الأخرى) — عجز خام {need - avail:N1} كجم.");
        }

        double covered = demands.Sum(d => d.Covered);
        result.CoveredKg = Math.Round(covered, 1);
        result.ShortageKg = Math.Round(Math.Max(0, totalRequired - covered), 1);
        result.CustomersCount = buckets.Count;
        if (usedSources.Count > 0)
            result.CapacityNote = "معدلات مستخدمة: " + string.Join("؛ ", usedSources.Take(4)) + (usedSources.Count > 4 ? "…" : "");

        bool blocked = demands.Any(d => d.Blocked);
        int shortDays = result.Days.Count(d => d.Status == "Short");
        if (!blocked && result.ShortageKg <= 0.01)
            result.Verdict = $"✅ الخطة قابلة للتنفيذ — {result.CoveredKg:N1} كجم موزعة على {result.WorkDays} يوم عمل لـ{result.CustomersCount} عملاء ({result.ItemsCount} بنود) بنفس قواعد التوزيع.";
        else
        {
            result.Verdict = $"🔴 الخطة غير قابلة للتنفيذ كما هي — المغطى {result.CoveredKg:N1} من {result.RequiredKg:N1} كجم (عجز {result.ShortageKg:N1} كجم)";
            if (blocked) result.Verdict += " — بنود محجوبة (بلا معدل/تحويل)";
            if (shortDays > 0) result.Verdict += $" — {shortDays} أيام بعجز";
            result.Verdict += ".";
        }
        result.Ok = !blocked && result.ShortageKg <= 0.01;
        return result;
    }

    /// <summary>§B91 — طلب فحص: بند خطة + عبوة الفحص + المتبقي والمغطى والطاقة اليومية.</summary>
    private class CheckDemand
    {
        public ProductionPlanItem Item;
        public int PackId;
        public double UnitWeight;
        public double Remaining;
        public double Covered;
        public double DailyCapKg;
        public bool Blocked;
    }

    /// <summary>§B91 — سلة عميل للفحص: بنوده + نسبته للإنجاز (الأقل إنجازاً يتقدم).</summary>
    private class CheckBucket
    {
        public int? CustomerId; public string Name; public int PriorityNo;
        public List<CheckDemand> Demands = new();
        public double Required; public double Remaining; public double Covered; public double Ratio;
    }

    /// <summary>
    /// §الإقفال اليومي: إن اكتمل إنتاج كل بنود الخطة تُقفل تلقائياً — يحرر الحجوزات
    /// غير المستهلكة ويعيد الأرصدة متاحة، استعداداً لإصدار خطة اليوم التالي.
    /// </summary>
    public OpResult TryAutoCloseIfComplete(int planId)
    {
        var plan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == planId);
        if (plan == null || plan.IsClosed) return OpResult.Fail("");
        if (plan.Items.Count == 0 || !plan.Items.All(i => i.IsClosed || i.ProducedQtyKg + 0.001 >= i.PlannedQtyKg))
            return OpResult.Fail(""); // لم تكتمل بعد — تبقى مفتوحة (إجراء صامت)

        // §B79: مكتملة ≠ مقفلة — لا إقفال تلقائي؛ الإقفال قرار صريح من شاشة «إقفال خطة الإنتاج».
        return OpResult.Success($"✅ الخطة {plan.DocumentNumber} مكتملة — جاهزة للإقفال الرسمي من شاشة «إقفال خطة الإنتاج».");
    }

    /// <summary>
    /// §إصلاح حرج: كل بند يجب أن يحمل تاريخ إنتاج — وإلا تخطّى فحص الطاقة كلياً
    /// لأن الحارس يشترط ScheduledDate != null. البند بلا تاريخ يُجدول على بداية الخطة
    /// (لا يُرفض — جدولة رحومة تُخضعه للفحص بدل أن تُعطّل العمل).
    /// </summary>
    private static void ApplyDefaultScheduledDates(List<PlanItemDto> items, DateTime? planStart, DateTime? planEnd = null)
    {
        // §إصلاح: فترة مقلوبة (النهاية قبل البداية) كانت تمرّ بصمت — الحارس أدناه يشترط
        // planEnd >= planStart فيُلغي نفسه، فتُقبل كل التواريخ بلا فحص وتُحفظ خطة
        // بفترة مستحيلة. الرفض هنا صراحةً بدل تعطيل الحارس ضمناً.
        if (planStart != null && planEnd != null && planEnd.Value.Date < planStart.Value.Date)
            throw new DomainException(
                $"فترة الخطة غير صحيحة: تاريخ النهاية ({planEnd.Value.Date.ToString(UiFormat.DatePattern)}) "
                + $"قبل تاريخ البداية ({planStart.Value.Date.ToString(UiFormat.DatePattern)}).",
                "INVALID_PERIOD");

        string fallback = (planStart ?? DateTime.Today).Date.ToString(UiFormat.DatePattern);
        foreach (var i in items)
            if (!UiFormat.TryParseDate(i.ScheduledDate, out _))
                i.ScheduledDate = fallback;

        // §B80: فرض تاريخ كل إنتاج — تاريخ أي بند خارج فترة الخطة مرفوض صراحةً.
        // (الواجهة تعرض تاريخاً لكل بند في نافذة الاختيار وجدول البنود وتتحقق قبل الحفظ،
        // وهذا الحارس يسدّ أي مسار آخر — الخطة الطويلة لا تقبل بنداً في يوم خارج فترتها.)
        if (planStart != null && planEnd != null && planEnd.Value.Date >= planStart.Value.Date)
        {
            var outside = items
                .Where(i => UiFormat.TryParseDate(i.ScheduledDate, out var d)
                            && (d.Date < planStart.Value.Date || d.Date > planEnd.Value.Date))
                .ToList();
            if (outside.Count > 0)
            {
                string first = outside[0].ScheduledDate;
                throw new DomainException(
                    $"تاريخ الإنتاج ({first}) خارج فترة الخطة " +
                    $"({planStart.Value.Date.ToString(UiFormat.DatePattern)} ← {planEnd.Value.Date.ToString(UiFormat.DatePattern)}) — " +
                    $"{outside.Count} بند بتواريخ خارج الفترة.\nحدّد لكل بند تاريخ إنتاج داخل فترة الخطة.",
                    "DATE_OUT_OF_RANGE");
            }
        }
    }

    /// <summary>
    /// §إصلاح حرج — فحص الطاقة لبند مع تراكم بنود «الخطة نفسها».
    /// الخلل السابق: كان يُمرَّر excludePlanId وحده، فتُستثنى الخطة كلها من الحساب
    /// ولا تُحتسب بنودها على بعضها ← أمكن تحميل 16 ساعة في وردية 8 ساعات بوضع البنود في خطة واحدة.
    /// </summary>
    /// <summary>(الوردية، الخط، اليوم) ← ساعات مستخدمة/طاقة كلية/معدل الصنف — يراعي العبوة/المواصفة.</summary>
    internal (double usedHours, double effHours, double rate) ShiftUsageHours(int shiftId, int lineId, string schedDate, int productId, int? excludePlanId, int? packagingTypeId = null)
    {
        var check = new PlanningCapacityEvaluator(Db).Evaluate(Array.Empty<PlanItemDto>(), excludePlanId, schedDate, schedDate, shiftId, lineId);
        if (!check.IsValid) throw new DomainException(check.Error);
        var slot = check.Slots.SingleOrDefault();
        return (slot?.OtherHours ?? 0, slot?.TotalHours ?? 0, RateFor(productId, shiftId, packagingTypeId));
    }

    /// <summary>معدل صنف + عبوة + وردية: خاصة بالعبوة أولاً، ثم العامة، ثم معدل الصنف العام.</summary>
    private double RateFor(int productId, int shiftId, int? packagingTypeId)
    {
        var (r, _, _) = CapacityPolicy.Resolve(Db, productId, shiftId, packagingTypeId);
        return r;
    }
}
