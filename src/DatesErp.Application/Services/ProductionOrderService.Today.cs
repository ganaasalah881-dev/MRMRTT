using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

public partial class ProductionOrderService
{
    public const string TodayOrdersOnlyMessage = "تم إيقاف مسار اختيار/تجزئة الأوامر. استخدم أمر إنتاج اليوم؛ الأصناف والكميات والتاريخ تأتي من خطة اليوم المعتمدة فقط.";
    public const string NoTodayPlanMessage = "لا توجد بنود إنتاج معتمدة ومجدولة لليوم. راجع جهة التخطيط؛ لا يمكن إنشاء أمر إنتاج.";
    protected override System.Data.IsolationLevel TransactionIsolation => System.Data.IsolationLevel.Serializable;

    private sealed record DayEntry(ProductionPlan Plan, ProductionPlanItem Item)
    {
        public int? Shift => Item.SuggestedShiftId ?? Plan.ShiftId;
        public int? Line => Item.SuggestedLineId ?? Plan.LineId;
    }

    private List<DayEntry> ApprovedScheduledEntries(DateTime? day = null)
    {
        // Production Order ينشأ فقط من خطة معتمدة (لا ملغاة ولا مغلقة).
        var plans = Db.ProductionPlans.AsNoTracking()
            .Where(p => p.IsApproved && p.Status == DocStatuses.Approved && !p.IsClosed)
            .ToDictionary(p => p.Id);
        var ids = plans.Keys.ToList();
        var items = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => ids.Contains(i.PlanId) && i.ScheduledDate != null);
        if (day.HasValue)
        {
            var start = day.Value.Date;
            var end = start.AddDays(1);
            items = items.Where(i => i.ScheduledDate >= start && i.ScheduledDate < end);
        }
        return items.OrderBy(i => i.ScheduledDate).ThenBy(i => i.PlanId).ThenBy(i => i.PriorityNo).ThenBy(i => i.Id)
            .ToList().Select(i => new DayEntry(plans[i.PlanId], i)).ToList();
    }

    private List<DayEntry> TodayEntries(DateTime day) => ApprovedScheduledEntries(day);

    private List<ProductionOrderItem> ExistingItems(IEnumerable<int> planItemIds)
    {
        var ids = planItemIds.ToList();
        return Db.ProductionOrderItems.AsNoTracking()
            .Where(i => i.PlanItemId != null && ids.Contains(i.PlanItemId.Value))
            .Join(Db.ProductionOrders.AsNoTracking().Where(o => o.Status != DocStatuses.Cancelled),
                i => i.OrderId, o => o.Id, (i, o) => i).ToList();
    }

    public TodayProductionDto GetTodayProduction()
        => BuildProductionSheet(Db.BusinessNow.Date);

    public TodayProductionDto GetScheduledProduction()
        => BuildProductionSheet(null);

    private TodayProductionDto BuildProductionSheet(DateTime? onlyDay)
    {
        // قراءة القائمة لا تنشئ أرقاماً ولا أوامر ولا حركات مخزون.
        if (Session == null || !Session.Can("production", "View"))
            throw new PermissionDeniedException("عرض خطط أوامر الإنتاج");
        var businessDay = Db.BusinessNow.Date;
        var entries = ApprovedScheduledEntries(onlyDay);
        var existing = ExistingItems(entries.Select(e => e.Item.Id));
        var orderIds = existing.Select(i => i.OrderId).Distinct().ToList();
        var orders = Db.ProductionOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionary(o => o.Id);
        var allOrderItems = Db.ProductionOrderItems.AsNoTracking().Where(i => orderIds.Contains(i.OrderId)).ToList();
        var validOrderIds = orders.Values.Where(o =>
        {
            var group = entries.Where(e => e.Plan.Id == o.SourcePlanId && e.Item.CustomerId == o.CustomerId
                && e.Shift == o.ShiftId && e.Line == o.LineId
                && e.Item.ScheduledDate?.Date == o.ProductionDate?.Date).ToList();
            var children = allOrderItems.Where(i => i.OrderId == o.Id).ToList();
            return group.Count > 0 && children.Count == group.Count
                && children.Select(i => i.PlanItemId).Distinct().Count() == children.Count
                && children.All(i => group.Any(e => Matches(e, o, i)));
        }).Select(o => o.Id).ToHashSet();
        var productIds = entries.Select(e => e.Item.ProductId).Distinct().ToList();
        var products = Db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionary(p => p.Id, p => p.ProductNameAr);
        var customerIds = entries.Select(e => e.Item.CustomerId).Where(i => i != null).Select(i => i.Value).Distinct().ToList();
        var customers = Db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionary(c => c.Id, c => c.CustomerName);
        var shifts = Db.Shifts.AsNoTracking().ToDictionary(s => s.Id, s => s.ShiftNameAr);
        var lines = Db.ProductionLines.AsNoTracking().ToDictionary(l => l.Id, l => l.LineNameAr);
        var dayClosedOrders = Db.ProductionExecutions.AsNoTracking()
            .Where(e => e.IsDayClosed).Select(e => e.OrderId).ToHashSet();
        var rows = entries.Select(e =>
        {
            var scheduled = e.Item.ScheduledDate?.Date;
            var isToday = scheduled == businessDay;
            var prev = existing.Where(i => i.PlanItemId == e.Item.Id).ToList();
            var oi = prev.Count == 1 ? prev[0] : null;
            var order = oi != null ? orders[oi.OrderId] : null;
            bool matches = order != null && validOrderIds.Contains(order.Id) && Matches(e, order, oi);
            bool closed = e.Plan.IsClosed || e.Plan.Status == DocStatuses.Closed || e.Item.IsClosed;
            return new TodayProductionRowDto
            {
                PlanId = e.Plan.Id, PlanItemId = e.Item.Id, PlanNumber = e.Plan.DocumentNumber,
                CustomerId = e.Item.CustomerId,
                CustomerName = e.Item.CustomerId is int c && customers.TryGetValue(c, out var cn) ? cn : "غير محدد في الخطة",
                ProductId = e.Item.ProductId, ProductName = products.GetValueOrDefault(e.Item.ProductId, "صنف غير موجود — راجع التخطيط"),
                PlannedCartons = e.Item.PlannedCartons, PlannedKg = e.Item.PlannedQtyKg,
                ScheduledDate = scheduled?.ToString("dd/MM/yyyy"), IsToday = isToday,
                ShiftId = e.Shift, ShiftName = e.Shift is int sh ? shifts.GetValueOrDefault(sh, "وردية غير موجودة") : "غير محددة في الخطة",
                LineId = e.Line, LineName = e.Line is int ln ? lines.GetValueOrDefault(ln, "خط غير موجود") : "غير محدد في الخطة",
                OrderId = matches ? order.Id : null, OrderNumber = matches ? order.DocumentNumber : "—",
                IsPending = prev.Count == 0 && !closed,
                DayClosed = order != null && dayClosedOrders.Contains(order.Id),
                Status = prev.Count > 0 && !matches ? "تعارض أمر سابق مع الخطة — يلزم مراجعة رسمية" :
                    closed ? "مقفل" : order != null ? DocStatuses.ToArabic(order.Status) : "جاهز للإصدار من الخطة"
            };
        }).ToList();
        string message = rows.Count == 0
            ? onlyDay.HasValue ? NoTodayPlanMessage : "لا توجد بنود في الخطط المعتمدة المجدولة للعرض."
            : onlyDay.HasValue
                ? "ماذا سننتج اليوم؟ وكم سننتج؟ البيانات من الخطة المعتمدة؛ لا إضافة أو تعديل هنا."
                : "الخطط المعتمدة المجدولة — السابقة والحالية والقادمة. الأصناف والكميات والعملاء من الخطة الأصلية.";
        return new TodayProductionDto
        {
            Day = businessDay,
            Rows = rows,
            CanIssue = rows.Any(r => r.IsToday && r.IsPending),
            Message = message
        };
    }

    private OpResult RunTodayWrite(Func<OpResult> work)
    {
        try { return RunOp(work); }
        catch (SqlException ex) when (ex.Number == 1205)
        { return OpResult.Fail("تزامن إصدار أوامر اليوم من جهاز آخر. لم تحفظ العملية جزئياً؛ حدّث الشاشة وأعد المحاولة."); }
    }

    public OpResult IssueTodayOrders()
    {
        Require("production", "Create");
        return RunTodayWrite(() =>
        {
            var day = Db.BusinessNow.Date;
            var entries = TodayEntries(day);
            if (entries.Count == 0) throw new DomainException(NoTodayPlanMessage);
            var existing = ExistingItems(entries.Select(e => e.Item.Id));
            // Never silently top up a legacy partial order or mask a conflicting prior order.
            foreach (var e in entries.Where(e => !e.Item.IsClosed && !e.Plan.IsClosed))
            {
                var prior = existing.Where(i => i.PlanItemId == e.Item.Id).ToList();
                if (prior.Count == 0) continue;
                var order = prior.Count == 1 ? Db.ProductionOrders.AsNoTracking().First(o => o.Id == prior[0].OrderId) : null;
                if (order == null || !Matches(e, order, prior[0]) || order.ProductionDate?.Date != day)
                    throw new DomainException("يوجد أمر سابق جزئي أو مخالف للخطة؛ لا تجزئة أو استكمال تلقائي. راجع جهة التخطيط قبل إصدار اليوم.");
            }
            var pending = entries.Where(e => !e.Item.IsClosed && !e.Plan.IsClosed && e.Plan.Status == DocStatuses.Approved && !existing.Any(i => i.PlanItemId == e.Item.Id)).ToList();
            int created = 0;
            foreach (var g in pending.GroupBy(e => new { e.Plan.Id, e.Item.CustomerId, e.Shift, e.Line }))
            {
                var result = SaveTodayGroup("FromPlan", g.Key.Id, g.Key.CustomerId, day.ToString("dd/MM/yyyy"),
                    g.Key.Shift, g.Key.Line, g.Select(e => FromPlan(e.Item)).ToList());
                if (!result.Ok) throw new DomainException(result.Message);
                created++;
            }
            if (Db.BusinessNow.Date != day) throw new DomainException("تغير يوم العمل أثناء الإصدار؛ حدّث الشاشة وأعد المحاولة.");
            return OpResult.Success(created == 0 ? "أوامر بنود اليوم صادرة أو مقفلة بالفعل؛ لم يُنشأ أمر مكرر." :
                $"تم إصدار {created} أمر من جميع بنود اليوم المعتمدة بالكميات الأصلية، دون إعادة اختيار أو تعديل. لم يُصرف مخزون عند الإصدار.");
        });
    }

    private static OrderItemDto FromPlan(ProductionPlanItem p) => new()
    {
        PlanItemId = p.Id, CustomerId = p.CustomerId, ProductId = p.ProductId, LotId = p.LotId,
        ShipmentId = p.ShipmentId, PackagingTypeId = p.PackagingTypeId,
        PlannedCartons = p.PlannedCartons, PlannedQtyKg = p.PlannedQtyKg
    };

    private OpResult SaveTodayGroup(string sourceType, int? planId, int? customerId, string productionDate,
        int? shiftId, int? lineId, List<OrderItemDto> requested)
        => SaveScheduledGroup(sourceType, planId, customerId, productionDate, shiftId, lineId, requested, requireToday: true);

    /// <summary>
    /// ينشئ أمراً من مجموعة بنود خطة معتمدة. مسار اليوم يستدعيه مع requireToday=true؛
    /// ومسار «إضافة من الخطط» يسمح بتاريخ الجدولة السابق أو الحالي أو اللاحق، مع بقاء
    /// الهوية والكميات ومراجع الخطة وحراس الطاقة كما هي.
    /// </summary>
    private OpResult SaveScheduledGroup(string sourceType, int? planId, int? customerId, string productionDate,
        int? shiftId, int? lineId, List<OrderItemDto> requested, bool requireToday)
    {
        if (sourceType != "FromPlan" || planId == null)
            throw new DomainException("أمر الإنتاج لا ينشأ يدوياً؛ يلزم مرجع خطة معتمدة (Approved Production Plan فقط).");
        if (!UiFormat.TryParseDate(productionDate, out var day))
            throw new DomainException("تاريخ أمر الإنتاج غير صالح؛ اختر تاريخ البند من الخطة.");
        day = day.Date;
        var businessDay = Db.BusinessNow.Date;
        if (requireToday && day != businessDay)
            throw new DomainException("أمر إنتاج اليوم يجب أن يطابق يوم العمل الحالي.");
        // الخطة السابقة غير المصدرة تبقى قابلة للترحيل كمتأخرات؛ يُحفظ تاريخها الأصلي.
        // بدء التنفيذ له حارس مستقل يسمح بالأمر المتأخر في يوم العمل الحالي فقط.

        var planCheck = Db.ProductionPlans.AsNoTracking().FirstOrDefault(p => p.Id == planId);
        if (planCheck == null) throw new DomainException("الخطة المرجعية غير موجودة.");
        if (!planCheck.IsApproved || planCheck.Status != DocStatuses.Approved || planCheck.IsClosed)
            throw new DomainException($"لا يمكن إنشاء أمر إنتاج من خطة غير معتمدة أو ملغاة أو مغلقة — حالة الخطة: {DocStatuses.ToArabic(planCheck.Status)}", "PLAN_NOT_APPROVED");
        if (requested == null || requested.Count == 0 || requested.Any(i => i.PlanItemId == null) || requested.Select(i => i.PlanItemId).Distinct().Count() != requested.Count)
            throw new DomainException("يلزم نقل بنود الخطة الأصلية كاملة دون بنود يدوية أو مكررة.");

        var entries = TodayEntries(day).Where(e => e.Plan.Id == planId && e.Plan.Status == DocStatuses.Approved && !e.Plan.IsClosed && !e.Item.IsClosed).ToList();
        if (entries.Count == 0)
            throw new DomainException(requireToday ? NoTodayPlanMessage : "لا توجد بنود خطة معتمدة ومجدولة بهذا التاريخ.");
        var existing = ExistingItems(entries.Select(e => e.Item.Id));
        var group = entries.Where(e => e.Item.CustomerId == customerId && e.Shift == shiftId && e.Line == lineId).ToList();
        var groupIds = group.Select(e => e.Item.Id).ToHashSet();
        var existingGroupItems = existing.Where(i => i.PlanItemId is int id && groupIds.Contains(id)).ToList();
        if (existingGroupItems.Count > 0)
            throw new DomainException(existingGroupItems.Count == group.Count
                ? "هذه المجموعة لها أمر إنتاج سابق؛ يمنع إنشاء أمر مكرر لنفس بنود الخطة."
                : "يوجد إصدار جزئي سابق لهذه المجموعة؛ لا يُستكمل الأمر تلقائياً ولا يُنشأ أمر ثانٍ. راجع التخطيط.");
        if (group.Count == 0 || !groupIds.SetEquals(requested.Select(i => i.PlanItemId.Value)))
            throw new DomainException($"بنود الأمر يجب أن تطابق مجموعة الخطة المعتمدة ليوم {day:dd/MM/yyyy} كاملة؛ لا اختيار جزئي أو صنف إضافي أو تكرار أمر سابق.");
        foreach (var e in group)
        {
            var dto = requested.Single(i => i.PlanItemId == e.Item.Id); var p = e.Item;
            if (dto.ProductId != p.ProductId || dto.LotId != p.LotId || dto.PackagingTypeId != p.PackagingTypeId
                || (dto.CustomerId != null && dto.CustomerId != p.CustomerId) || (dto.ShipmentId != null && dto.ShipmentId != p.ShipmentId)
                || dto.PlannedCartons != p.PlannedCartons || dto.PlannedQtyKg != p.PlannedQtyKg)
                throw new DomainException("هوية بند أمر الإنتاج وكميته يجب أن تطابق الخطة المعتمدة تماماً؛ لا زيادة أو تخفيض أو تبديل للصنف/العميل/الدفعة/العبوة.");
            if (p.PlannedCartons <= 0 || !double.IsFinite(p.PlannedQtyKg) || p.PlannedQtyKg <= 0)
                throw new DomainException("كمية الخطة غير صالحة؛ صحح الخطة رسمياً، لا كمية أمر الإنتاج.");
            if ((e.Plan.StartDate != null && day < e.Plan.StartDate.Value.Date) || (e.Plan.EndDate != null && day > e.Plan.EndDate.Value.Date))
                throw new DomainException("تاريخ بند الخطة خارج فترتها المعتمدة؛ راجع التخطيط.");
            ProductIdentityGuard.EnsurePlanningLink(Db, p.ProductId, p.LotId, null);
            UnitsPolicy.RequireItemType(Db, p.ProductId, "Finished", "أمر الإنتاج");
            UnitsPolicy.RequireCartonWeight(Db, p.ProductId, p.PackagingTypeId, p.PlannedCartons, "تعريف الخطة عند إصدار أمر الإنتاج");
            _ = UnitsPolicy.EnsureCartonKgConsistency(Db, p.ProductId, p.PackagingTypeId,
                p.PlannedQtyKg, p.PlannedCartons, "راجع تعريف العبوة والخطة رسمياً؛ لا تصحح كمية الأمر هنا");
            if (p.LotId is int lotId)
            {
                var lot = Db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == lotId);
                if (lot == null || lot.CustomerId != p.CustomerId || (p.ShipmentId != null && lot.ShipmentId != p.ShipmentId))
                    throw new DomainException("هوية العميل/الشحنة في الخطة لا تطابق دفعة الخام؛ راجع جهة التخطيط.");
            }
        }
        var order = new ProductionOrder { SourceType = "FromPlan", SourcePlanId = planId, CustomerId = customerId,
            ProductionDate = day, ShiftId = shiftId, LineId = lineId, Status = DocStatuses.Draft,
            DocumentNumber = Numbering.Next("ORD") };
        foreach (var e in group)
        {
            var p = e.Item;
            var def = UnitsPolicy.PackagingDefinition(Db, p.ProductId, p.PackagingTypeId);
            order.Items.Add(new ProductionOrderItem { PlanItemId = p.Id, CustomerId = p.CustomerId, ProductId = p.ProductId,
                LotId = p.LotId, ShipmentId = p.ShipmentId, PackagingTypeId = p.PackagingTypeId,
                PlannedCartons = p.PlannedCartons, PlannedQtyKg = p.PlannedQtyKg, Status = DocStatuses.Draft,
                CartonWeightKg = UnitsPolicy.CartonWeight(Db, p.ProductId, p.PackagingTypeId), MoldsCount = def.MoldsCount, MoldWeightKg = (decimal)def.MoldWeightKg });
        }
        ValidateSourceCapacity(order);
        Db.ProductionOrders.Add(order); Db.SaveChanges();
        CalculateMaterials(order); Db.SaveChanges();
        return OpResult.Success($"تم نقل بنود الخطة المعتمدة ليوم {day:dd/MM/yyyy} كما هي إلى أمر الإنتاج.", order.Id, order.DocumentNumber);
    }

    private static bool Matches(DayEntry e, ProductionOrder o, ProductionOrderItem i) =>
        o.SourceType == "FromPlan" && o.SourcePlanId == e.Plan.Id && o.CustomerId == e.Item.CustomerId
        && o.ShiftId == e.Shift && o.LineId == e.Line && i.PlanItemId == e.Item.Id
        && i.ProductId == e.Item.ProductId && i.CustomerId == e.Item.CustomerId && i.LotId == e.Item.LotId
        && i.ShipmentId == e.Item.ShipmentId && i.PackagingTypeId == e.Item.PackagingTypeId
        && i.PlannedCartons == e.Item.PlannedCartons && i.PlannedQtyKg == e.Item.PlannedQtyKg;

    // ═══ «إضافة أمر» من شاشة الإنتاج: مجموعات خطة معتمدة لم تُصدر بعد ═══
    private List<TodayPendingGroupDto> BuildPendingGroups(IEnumerable<DayEntry> source)
    {
        var entries = source.Where(e => e.Plan.Status == DocStatuses.Approved && !e.Plan.IsClosed && !e.Item.IsClosed).ToList();
        var existing = ExistingItems(entries.Select(e => e.Item.Id));
        return entries.Where(e => !existing.Any(i => i.PlanItemId == e.Item.Id))
            .GroupBy(e => new
            {
                e.Plan.Id,
                e.Plan.DocumentNumber,
                ScheduledDate = e.Item.ScheduledDate!.Value.Date,
                CustomerId = e.Item.CustomerId,
                e.Shift,
                e.Line
            })
            .Select(g => new TodayPendingGroupDto
            {
                PlanId = g.Key.Id,
                PlanNumber = g.Key.DocumentNumber,
                ScheduledDate = g.Key.ScheduledDate.ToString("dd/MM/yyyy"),
                CustomerId = g.Key.CustomerId,
                CustomerName = g.Key.CustomerId != null ? Db.Customers.AsNoTracking().Where(c => c.Id == g.Key.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "-" : "-",
                ShiftId = g.Key.Shift,
                ShiftName = g.Key.Shift != null ? Db.Shifts.AsNoTracking().Where(x => x.Id == g.Key.Shift).Select(x => x.ShiftNameAr).FirstOrDefault() ?? $"وردية #{g.Key.Shift}" : "-",
                LineId = g.Key.Line,
                LineName = g.Key.Line != null ? Db.ProductionLines.AsNoTracking().Where(x => x.Id == g.Key.Line).Select(x => x.LineNameAr).FirstOrDefault() ?? $"خط #{g.Key.Line}" : "-",
                ItemsCount = g.Count(),
                Cartons = g.Sum(x => x.Item.PlannedCartons),
                PlannedKg = g.Sum(x => x.Item.PlannedQtyKg)
            })
            .OrderBy(x => x.ScheduledDate).ThenBy(x => x.PlanId).ThenBy(x => x.CustomerName).ToList();
    }

    public List<TodayPendingGroupDto> GetTodayPendingGroups()
        => BuildPendingGroups(TodayEntries(Db.BusinessNow.Date));

    /// <summary>
    /// يعرض كل مجموعات الخطط المعتمدة المجدولة السابقة والحالية والقادمة، لا اليوم الحالي فقط.
    /// شاشة «أمر إنتاج اليوم» تبقى مقيدة بيوم العمل، أما زر «إضافة من الخطة» فيحتاج هذا المسار
    /// حتى لا تختفي الخطط المتأخرة أو المستقبلية ولا يُجبر المستخدم على إعادة إدخال أمر يدوي.
    /// </summary>
    public List<TodayPendingGroupDto> GetPendingPlanGroups()
    {
        if (Session == null || !Session.Can("production", "View"))
            throw new PermissionDeniedException("عرض الخطط المجدولة لأوامر الإنتاج");
        // لا نخفي خطة سابقة لم تُصدر بعد؛ تعرض الواجهة حالتها وتسمح بمراجعة/إصدارها.
        return BuildPendingGroups(ApprovedScheduledEntries());
    }

    public OpResult IssueTodayGroup(int planId, int? customerId, int? shiftId, int? lineId)
    {
        Require("production", "Create");
        return RunTodayWrite(() =>
        {
            var day = Db.BusinessNow.Date;
            var entries = TodayEntries(day)
                .Where(e => e.Plan.Id == planId && e.Plan.Status == DocStatuses.Approved && !e.Plan.IsClosed && !e.Item.IsClosed
                    && e.Item.CustomerId == customerId && e.Shift == shiftId && e.Line == lineId).ToList();
            var existing = ExistingItems(entries.Select(e => e.Item.Id));
            var group = entries.Where(e => !existing.Any(i => i.PlanItemId == e.Item.Id)).ToList();
            if (group.Count == 0) return OpResult.Fail("هذه المجموعة صادرة سابقاً أو لم تعد متاحة — حدّث الشاشة.");
            // المسار الواحد: نفس SaveTodayGroup حرفياً بنفس قواعد الهوية والكمية والطاقة واحتساب المواد.
            var result = SaveTodayGroup("FromPlan", planId, customerId, day.ToString("dd/MM/yyyy"), shiftId, lineId, group.Select(e => FromPlan(e.Item)).ToList());
            if (!result.Ok) return result;
            return OpResult.Success($"تم إصدار أمر الإنتاج {result.DocumentNumber} — {group.Count} بنداً بكميات الخطة الأصلية.", result.Id, result.DocumentNumber);
        });
    }

    /// <summary>إصدار مجموعة من خطة سابقة أو حالية أو مستقبلية مع حفظ تاريخ الجدولة الأصلي.</summary>
    public OpResult IssuePlanGroup(int planId, string scheduledDate, int? customerId, int? shiftId, int? lineId)
    {
        Require("production", "Create");
        return RunTodayWrite(() =>
        {
            if (!UiFormat.TryParseDate(scheduledDate, out var day))
                return OpResult.Fail("تاريخ الخطة غير صالح؛ حدّث الشاشة واختر تاريخاً مجدولاً صحيحاً.");
            day = day.Date;
            var entries = TodayEntries(day)
                .Where(e => e.Plan.Id == planId && e.Plan.Status == DocStatuses.Approved && !e.Plan.IsClosed && !e.Item.IsClosed
                    && e.Item.CustomerId == customerId && e.Shift == shiftId && e.Line == lineId).ToList();
            var existing = ExistingItems(entries.Select(e => e.Item.Id));
            var group = entries.Where(e => !existing.Any(i => i.PlanItemId == e.Item.Id)).ToList();
            if (group.Count == 0) return OpResult.Fail("هذه المجموعة صادرة سابقاً أو لم تعد متاحة — حدّث الشاشة.");
            var result = SaveScheduledGroup("FromPlan", planId, customerId, day.ToString("dd/MM/yyyy"), shiftId, lineId,
                group.Select(e => FromPlan(e.Item)).ToList(), requireToday: false);
            if (!result.Ok) return result;
            return OpResult.Success($"تم إنشاء أمر الإنتاج {result.DocumentNumber} ليوم {day:dd/MM/yyyy} — {group.Count} بنداً بكميات الخطة الأصلية.", result.Id, result.DocumentNumber);
        });
    }

    private void ValidateTodayOrder(ProductionOrder order)
        => ValidateScheduledOrder(order, requireToday: true);

    private void ValidateScheduledOrder(ProductionOrder order, bool requireToday)
    {
        if (order.ProductionDate == null || order.SourcePlanId == null || order.SourceType != "FromPlan")
            throw new DomainException("أمر الإنتاج يجب أن يكون مرتبطاً بخطة إنتاج معتمدة؛ لا أمر يدوي.");
        var day = order.ProductionDate.Value.Date;
        if (requireToday && day > Db.BusinessNow.Date)
            throw new DomainException("لا يبدأ التنفيذ قبل يوم الخطة؛ يمكن تنفيذ الأمر المتأخر اليوم إذا كان تاريخه أقدم.");
        var entries = TodayEntries(day).Where(e => e.Plan.Id == order.SourcePlanId && e.Plan.Status == DocStatuses.Approved && !e.Plan.IsClosed && !e.Item.IsClosed
            && e.Item.CustomerId == order.CustomerId && e.Shift == order.ShiftId && e.Line == order.LineId).ToList();
        if (entries.Count == 0 || order.Items.Count != entries.Count || order.Items.Any(i => !entries.Any(e => Matches(e, order, i)))
            || order.Items.Select(i => i.PlanItemId).Distinct().Count() != order.Items.Count)
            throw new DomainException("أمر الإنتاج لا يطابق بنود وكميات الخطة المعتمدة؛ أوقف التنفيذ وراجع جهة التخطيط.");
        var prev = ExistingItems(entries.Select(e => e.Item.Id));
        if (prev.Any(i => i.OrderId != order.Id)) throw new DomainException("يوجد أمر آخر لنفس بند الخطة؛ يمنع التنفيذ المكرر.");
        foreach (var i in order.Items) ProductIdentityGuard.EnsurePlanningLink(Db, i.ProductId, i.LotId, null);
    }

    private List<string> ValidateSourceCapacity(ProductionOrder order)
    {
        // An order adds no load to its plan. Re-evaluate the source plan exactly once, using the shared evaluator.
        var plan = Db.ProductionPlans.AsNoTracking().Include(p => p.Items).First(p => p.Id == order.SourcePlanId);
        var items = PlanningService.CapacityItems(plan);
        // A legacy plan may explicitly leave its execution slot unspecified. Never invent a shift or line here.
        var specified = items.Where(i => i.SuggestedShiftId != null && i.SuggestedLineId != null).ToList();
        if (specified.Count > 0)
        {
            var result = new PlanningCapacityEvaluator(Db).Evaluate(specified, plan.Id,
                plan.StartDate?.ToString("dd/MM/yyyy"), plan.EndDate?.ToString("dd/MM/yyyy"),
                ordersOfExcludedPlanCoveredByDraft: true);
            if (!result.IsValid) throw new DomainException(result.Error + "\nعالج التعريف أو الجدولة في الخطة؛ كمية الأمر لا تُعدّل هنا.");
        }
        return new List<string>();
    }
}
