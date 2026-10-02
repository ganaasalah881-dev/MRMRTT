using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §B79 — «إقفال خطة الإنتاج»: المستوى الإجمالي فوق أوامر الإنتاج.
/// الخطة تُقفل فقط عندما تكون جميع أوامرها المطلوبة مقفلة؛ والإقفال معاملة ذرية واحدة
/// تُسجَّل في التدقيق بالمستخدم والوقت والحالتين، ومع الاستثناء تُوثَّق الأسباب والأوامر المفتوحة.
/// </summary>
public class PlanClosureService : ServiceBase, IPlanClosureService
{
    private readonly IAuditService _audit;
    // The decision and close must not race with another terminal issuing/amending plan orders.
    protected override System.Data.IsolationLevel TransactionIsolation => System.Data.IsolationLevel.Serializable;

    public PlanClosureService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering, IAuditService audit)
        : base(db, session, numbering)
    {
        _audit = audit;
    }

    private static string OrderStateOf(ProductionOrder o) =>
        o.Status == DocStatuses.Cancelled ? "ملغى" :
        (o.IsClosed || o.Status == DocStatuses.Closed) &&
            o.Items.Any(i => i.PlannedQtyKg > i.ProducedQtyKg + 0.001) &&
            string.IsNullOrWhiteSpace(o.CloseReason) ? "عجز غير مسوّى" :
        (o.IsClosed || o.Status == DocStatuses.Closed) ? "مقفل" :
        o.Status is DocStatuses.InProgress or DocStatuses.Stopped ? "قيد الإنتاج" :
        o.Status == DocStatuses.Completed ? "مكتمل" : "مفتوح";

    /// <summary>الإقفال الفيزيائي بلا سبب عجز موثق لا يُسوّي الفرق.</summary>
    private static bool IsBlocking(string state) =>
        state is "مفتوح" or "قيد الإنتاج" or "مكتمل" or "عجز غير مسوّى";

    public PlanClosureInfo GetInfo(int planId)
    {
        var info = new PlanClosureInfo { PlanId = planId };
        var plan = Db.ProductionPlans.AsNoTracking().FirstOrDefault(p => p.Id == planId);
        if (plan == null) { info.Blockers.Add("الخطة غير موجودة."); return info; }

        info.PlanNumber = plan.DocumentNumber;
        info.PlanTypeAr = plan.PlanType switch { "Daily" => "يومية", "Weekly" => "أسبوعية", "Monthly" => "شهرية", _ => "فترية" };
        info.StartDate = plan.StartDate?.ToString("dd/MM/yyyy") ?? "—";
        info.EndDate = plan.EndDate?.ToString("dd/MM/yyyy") ?? "—";
        info.IsClosed = plan.IsClosed;
        info.ClosedAt = plan.ClosedDate?.ToString("dd/MM/yyyy HH:mm") ?? "—";
        info.ClosedByName = plan.ClosedBy == null ? "—"
            : Db.Users.AsNoTracking().Where(u => u.Id == plan.ClosedBy).Select(u => u.FullName).FirstOrDefault() ?? "—";

        var orders = Db.ProductionOrders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.SourcePlanId == planId).OrderBy(o => o.Id).ToList();

        foreach (var o in orders)
        {
            var state = OrderStateOf(o);
            double planned = o.Items.Sum(i => i.PlannedQtyKg);
            double produced = o.Items.Sum(i => i.ProducedQtyKg);
            double closed = o.IsClosed ? produced : o.Items.Where(i => i.IsClosed).Sum(i => i.ProducedQtyKg);
            info.Orders.Add(new OrderClosureRow
            {
                OrderId = o.Id,
                OrderNumber = o.DocumentNumber,
                CustomerName = o.CustomerId == null ? "—"
                    : Db.Customers.AsNoTracking().Where(c => c.Id == o.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "—",
                ProductNames = string.Join("، ", o.Items.Select(i => i.ProductId).Distinct().Select(pid =>
                    Db.Products.AsNoTracking().Where(p => p.Id == pid).Select(p => p.ProductNameAr).FirstOrDefault() ?? "—")),
                Date = o.ProductionDate?.ToString("dd/MM/yyyy") ?? "—",
                ShiftName = o.ShiftId == null ? "—"
                    : Db.Shifts.AsNoTracking().Where(s => s.Id == o.ShiftId).Select(s => s.ShiftNameAr).FirstOrDefault() ?? "—",
                Planned = planned,
                Produced = produced,
                Closed = closed,
                StateAr = state,
                IsCancelled = state == "ملغى"
            });

            switch (state)
            {
                case "مفتوح": info.OpenOrders++; break;
                case "قيد الإنتاج": info.InProgressOrders++; break;
                case "مكتمل": info.CompletedOrders++; break;
                case "عجز غير مسوّى": info.CompletedOrders++; break;
                case "مقفل": info.ClosedOrders++; break;
                case "ملغى": info.CancelledOrders++; break;
            }
            if (state != "ملغى")
            {
                info.PlannedTotal += planned;
                info.ProducedTotal += produced;
                info.ClosedTotal += closed;
                // لا تُعد فروقاً مسوّاة بمجرد إقفال يوم الإنتاج آلياً؛ يلزم سبب تسوية مسجل.
                if (state == "مقفل" && !string.IsNullOrWhiteSpace(o.CloseReason))
                    info.SettledVariance += Math.Max(0, planned - produced);
                if (IsBlocking(state))
                    info.Blockers.Add($"أمر {o.DocumentNumber} — {state}"
                        + (produced < planned - 0.001 ? $" (المنفذ {produced:N0} من {planned:N0})" : ""));
                if (produced > planned + 0.001)
                    info.Blockers.Add($"أمر {o.DocumentNumber} — الكمية الفعلية أكبر من المخطط ({produced:N0} > {planned:N0}).");
            }
        }
        info.Blockers.RemoveAll(string.IsNullOrEmpty);
        var planItems = Db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == planId).ToList();
        var linked = orders.SelectMany(o => o.Items.Select(i => new { Order = o, Item = i })).ToList();
        foreach (var item in planItems.Where(i => !i.IsClosed))
        {
            var matches = linked.Where(x => x.Item.PlanItemId == item.Id && x.Item.ProductId == item.ProductId
                && x.Item.LotId == item.LotId && x.Item.PackagingTypeId == item.PackagingTypeId
                && (x.Item.CustomerId ?? x.Order.CustomerId) == item.CustomerId).ToList();
            var active = matches.Where(x => x.Order.Status != DocStatuses.Cancelled).ToList();
            // §43: الأمر الملغي يُسوّي بند الخطة فقط إذا كان إلغاؤه موثقاً بسبب
            // (CancelOrder يكتب وسماً [إلغاء ...] في الملاحظات). إلغاء بلا توثيق ليس تسوية —
            // فلا تُقفل خطة على بند لم يُنتج ولم يُسوَّ إجراؤه ورقياً.
            var documented = matches.Where(x => x.Order.Status != DocStatuses.Cancelled
                || (x.Order.Notes ?? "").Contains("[إلغاء")).ToList();
            var coverage = active.Count > 0 ? active : documented;
            double uncoveredKg = Math.Max(0, item.PlannedQtyKg - coverage.Sum(x => x.Item.PlannedQtyKg));
            int uncoveredCartons = Math.Max(0, item.PlannedCartons - coverage.Sum(x => x.Item.PlannedCartons));
            if (coverage.Count == 0 && matches.Count > 0)
                info.Blockers.Add($"بند الخطة #{item.Id} لم يُنتج: أمره ملغي بلا سبب موثق — وثّق سبب الإلغاء أو أصدر أمراً بديلاً.");
            else if (coverage.Count == 0 || uncoveredKg > 0.001 || uncoveredCartons > 0)
                info.Blockers.Add($"بند الخطة #{item.Id} لم يصدر كاملاً بأمر إنتاج: {uncoveredKg:N3} كجم / {uncoveredCartons:N0} كرتون بلا أمر. راجع التخطيط.");
        }
        if (planItems.Count == 0) info.Blockers.Add("الخطة لا تحتوي بنود إنتاج.");
        // The approved baseline includes future/unissued rows; never disguise them by summing orders only.
        info.PlannedTotal = planItems.Sum(i => i.PlannedQtyKg);
        info.TotalOrders = orders.Count;
        info.Remaining = Math.Max(0, info.PlannedTotal - info.ProducedTotal);
        // §B83: الأوامر غير المعالجة — المكتمل وحده لا يكفي حتى يُقفل رسمياً (§4 من المواصفة)
        info.UnprocessedOrders = info.OpenOrders + info.InProgressOrders + info.CompletedOrders;

        // ملخصات العملاء والأصناف — التتبع لا يُدمج
        // §B102 — ملخص العملاء من بنود الخطة نفسها (لا من ترويسات الأوامر): هوية العميل على البند،
        // فالأمر الواحد قد يكون متعدد العملاء وترويسته فارغة — وكانت الصفوف تخرج باسم «—».
        // وتشمل المقبول/المسلَّم (قيم مُزامَنة على البنود) — دمج إصلاح B100 مع تعدد العملاء.
        var custPlanItems = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.PlanId == planId && i.CustomerId != null).ToList();
        var custNameById = Db.Customers.AsNoTracking().ToDictionary(c => c.Id, c => c.CustomerName);
        foreach (var g in custPlanItems.GroupBy(i => i.CustomerId.Value).OrderBy(g => g.Key))
        {
            double planned = g.Sum(i => i.PlannedQtyKg);
            double produced = g.Sum(i => i.ProducedQtyKg);
            info.Customers.Add(new ClosureSummaryRow
            {
                Name = custNameById.TryGetValue(g.Key, out var cnn) ? cnn : $"#{g.Key}",
                Planned = planned,
                Produced = produced,
                Accepted = g.Sum(i => i.AcceptedQtyKg),
                Delivered = g.Sum(i => i.DeliveredQtyKg),
                Closed = g.Where(i => i.IsClosed).Sum(i => i.ProducedQtyKg),
                Remaining = Math.Max(0, planned - produced),
                StateAr = g.All(i => i.IsClosed) ? "مقفل ✔" : "غير مكتمل"
            });
        }
        var productNames = Db.Products.AsNoTracking().ToDictionary(p => p.Id, p => p.ProductNameAr);
        foreach (var group in linked.Where(x => x.Order.Status != DocStatuses.Cancelled).GroupBy(x => x.Item.ProductId))
        {
            // Group by stable product identity, not a concatenated header/name (which multiplied totals).
            double planned = group.Sum(x => x.Item.PlannedQtyKg);
            double produced = group.Sum(x => x.Item.ProducedQtyKg);
            info.Products.Add(new ClosureSummaryRow
            {
                Name = productNames.GetValueOrDefault(group.Key, $"صنف #{group.Key}"), Planned = planned, Produced = produced,
                Closed = group.Where(x => x.Order.IsClosed || x.Item.IsClosed).Sum(x => x.Item.ProducedQtyKg),
                Remaining = Math.Max(0, planned - produced),
                StateAr = group.All(x => OrderStateOf(x.Order) == "مقفل") ? "مقفل ✔" : "غير مكتمل"
            });
        }

        // حالة الخطة المشتقة (مسودة→معتمدة→قيد التنفيذ→مكتملة جزئياً→مكتملة→مقفلة / ملغاة)
        info.StatusAr =
            plan.Status == DocStatuses.Cancelled ? "ملغاة" :
            plan.IsClosed ? "مقفلة" :
            info.TotalOrders > 0 && info.ClosedOrders + info.CancelledOrders == info.TotalOrders && info.ClosedOrders > 0 && info.Blockers.Count == 0 ? "مكتملة" :
            info.ClosedOrders > 0 || info.InProgressOrders > 0 ? (info.ProducedTotal > 0 && info.ClosedOrders == 0 ? "قيد التنفيذ" : "مكتملة جزئيًا") :
            plan.IsApproved ? "معتمدة" : "مسودة";

        if (!plan.IsApproved) info.Blockers.Add("الخطة غير معتمدة.");
        if (plan.Status == DocStatuses.Cancelled) info.Blockers.Add("الخطة ملغاة.");
        if (plan.IsClosed) info.Blockers.Add("الخطة مقفلة مسبقاً.");
        info.CanClose = plan.IsApproved && !plan.IsClosed && plan.Status != DocStatuses.Cancelled && info.Blockers.Count == 0;
        return info;
    }

    public OpResult ClosePlanFinal(int planId, string reason = null, bool force = false)
    {
        Require("planning", "Approve");
        return RunOp(() =>
        {
            var plan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == planId)
                       ?? throw new DomainException("الخطة غير موجودة.");
            var info = GetInfo(planId);
            // §v1.50.24: الإقفال المتكرر ليس خطأ — رسالة ودية بلا إجراء ولا إثارة استثناء.
            if (plan.IsClosed)
                return OpResult.Success($"الخطة {plan.DocumentNumber} مقفلة سابقاً — لا حاجة لإقفال آخر.");
            if (plan.Status == DocStatuses.Cancelled) throw new DomainException("الخطة ملغاة — لا يمكن إقفالها.");
            if (!plan.IsApproved) throw new DomainException("الخطة غير معتمدة — اعتمدها أولاً.");
            // إقفال الخطة في الدورة الحالية أثرٌ لأمر تسليم الإنتاج المحرر،
            // وليس زرّاً مستقلاً بعد اعتماد أمر الإنتاج أو تسجيل الفعلي.
            var actualExecutionIds = Db.ProductionExecutions.AsNoTracking()
                .Where(e => Db.ProductionOrders.Any(o => o.Id == e.OrderId && o.SourcePlanId == planId))
                .Select(e => e.Id);
            bool hasIssuedProductionDelivery = Db.ProductionDeliveries.AsNoTracking()
                .Any(d => d.SourceType == DeliverySources.FromActual
                    && (d.Status == DocStatuses.Issued || d.Status == DocStatuses.Completed)
                    && actualExecutionIds.Contains(d.SourceId));
            if (!hasIssuedProductionDelivery && !force)
                throw new DomainException("لا تُقفل الخطة من هذه الشاشة مباشرة. حرّر أمر تسليم الإنتاج من الإنتاج الفعلي أولاً.", "PLAN_CLOSE_REQUIRES_DELIVERY");
            // The exceptional path may document an entirely unproduced shortage,
            // but MUST NOT bypass delivery of any quantity that was produced.
            var undelivered = PlanDeliveryIntegrity.MissingIssuedDeliveries(Db, planId);
            if (undelivered.Count > 0)
                throw new DomainException("يوجد إنتاج فعلي بلا أمر تسليم محرر:\n- " + string.Join("\n- ", undelivered),
                    "PLAN_UNISSUED_PRODUCTION");
            if (force && string.IsNullOrWhiteSpace(reason))
                throw new DomainException("الإقفال الاستثنائي يتطلب سبباً مكتوباً يُسجَّل في التدقيق.");
            if (info.Blockers.Count > 0)
            {
                if (!force)
                    throw new DomainException(
                        $"لا يمكن إقفال الخطة لوجود {info.Blockers.Count} مانع:\n- " + string.Join("\n- ", info.Blockers));
                if (string.IsNullOrWhiteSpace(reason))
                    throw new DomainException("الإقفال الاستثنائي يتطلب سبباً مكتوباً يُسجَّل في التدقيق.");
            }

            var oldStatus = info.StatusAr;
            plan.IsClosed = true;
            plan.ClosedDate = DateTime.Now;
            plan.ClosedBy = Session?.UserId;
            plan.Status = DocStatuses.Closed;
            // §B79: تحرير حجوزات دفعات الخطة المقفلة (المستهلك فقط يبقى محجوزاً حتى الصرف)
            foreach (var lid in plan.Items.Where(i => i.LotId != null).Select(i => i.LotId.Value).Distinct().ToList())
            {
                var lot = Db.Lots.FirstOrDefault(l => l.Id == lid);
                if (lot == null) continue;
                double reserved = Db.ProductionPlanItems
                    .Where(i => i.LotId == lid && i.PlanId != plan.Id)
                    .Join(Db.ProductionPlans, i => i.PlanId, p => p.Id, (i, p) => new { i, p })
                    .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed && !x.i.IsClosed)
                    .Select(x => x.i).RemainingKg().Sum();
                lot.ReservedQtyKg = Math.Max(0, reserved);
            }
            Db.SaveChanges();

            _audit.Log("إقفال خطة الإنتاج", force ? "إقفال استثنائي" : "إقفال", "Plan", plan.DocumentNumber, plan.Id,
                new { الحالة_السابقة = oldStatus },
                new
                {
                    الحالة_الجديدة = "مقفلة",
                    استثناء = force,
                    السبب = reason,
                    الأوامر_غير_المكتملة = info.Blockers,
                    إجمالي_الكمية = info.PlannedTotal,
                    المنتَج = info.ProducedTotal,
                    المتبقي = info.Remaining,
                    عدد_الأوامر = info.TotalOrders
                });
            return OpResult.Success(
                force ? $"تم إقفال الخطة {plan.DocumentNumber} إقفالاً استثنائياً موثقاً بالسبب." :
                        $"تم إقفال الخطة {plan.DocumentNumber} رسمياً — جميع الأوامر المطلوبة مقفلة.");
        });
    }

    public OpResult ReopenPlan(int planId, string reason)
    {
        // §B84/S2: تفعيل صلاحية Reopen الميتة في الكتالوج — المنح التلقائي لحاملي الاعتماد
        // يتم في PermissionService.GrantReopenToApprovers عند كل إقلاع (فلا إقفال للأدوار القائمة).
        Require("planning", "Reopen");
        if (string.IsNullOrWhiteSpace(reason))
            return OpResult.Fail("إعادة الفتح تتطلب سبباً مكتوباً يُسجَّل في التدقيق.");
        return RunOp(() =>
        {
            var plan = Db.ProductionPlans.FirstOrDefault(p => p.Id == planId)
                       ?? throw new DomainException("الخطة غير موجودة.");
            if (!plan.IsClosed) return OpResult.Fail("الخطة غير مقفلة — لا داعي لإعادة الفتح.");
            plan.IsClosed = false;
            plan.ClosedDate = null;
            plan.ClosedBy = null;
            plan.Status = DocStatuses.Approved;
            Db.SaveChanges();
            PlanDeliveryIntegrity.RecalculateLotReservations(Db, Db.ProductionPlanItems.AsNoTracking()
                .Where(i => i.PlanId == planId && i.LotId != null).Select(i => i.LotId!.Value).ToList());
            Db.SaveChanges();
            var recalculated = GetInfo(planId).StatusAr;
            _audit.Log("إقفال خطة الإنتاج", "إعادة فتح", "Plan", plan.DocumentNumber, plan.Id,
                new { الحالة_السابقة = "مقفلة" }, new { الحالة_الجديدة = recalculated, السبب = reason });
            return OpResult.Success($"أُعيد فتح الخطة {plan.DocumentNumber} — الحالة المحتسبة الآن: {recalculated}.");
        });
    }
}
