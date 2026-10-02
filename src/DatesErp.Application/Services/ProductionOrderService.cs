using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §7/§8 — أوامر الإنتاج: تحويل بنود الخطة المعتمدة إلى أوامر تنفيذية بمرجعها الكامل،
/// بلا إعادة إدخال: العميل والصنف والمنتج والكمية المخططة تُجلب من الخطة.
/// حراس: لا أمر أكبر من متبقي الخطة، لا أمر فوق طاقة الوردية (في الـ Backend لا الواجهة فقط)،
/// هوية الأمر مقفولة بعد بدء الإنتاج، وآلة حالات منضبطة:
/// مسودة → معتمد/مجدول → قيد التنفيذ → (متوقف ⇄ استئناف) → مكتمل، أو ملغي قبل الإنتاج.
/// </summary>
public partial class ProductionOrderService : ServiceBase, IProductionOrderService
{
    public ProductionOrderService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    // ═══════════════════════════ الإنشاء من الخطة ═══════════════════════════

    public OpResult SaveOrder(string sourceType, int? sourcePlanId, int? customerId, string productionDate, int? shiftId, int? lineId, List<OrderItemDto> items)
    {
        Require("production", "Create");
        return RunTodayWrite(() => SaveTodayGroup(sourceType, sourcePlanId, customerId, productionDate, shiftId, lineId, items));
    }


    /// <summary>§1.50.66.5 — هل دخل أمر الإنتاج مرحلة التنفيذ غير القابلة للرجوع؟</summary>
    private bool OrderHasIrreversibleExecution(int orderId)
    {
        try { return Db.ProductionExecutions.AsNoTracking().Any(e => e.OrderId == orderId && (e.IsDayClosed || e.ActualQtyKg > 0 || e.ConsumedRawKg > 0)); }
        catch { return false; }
    }

    /// <summary>§1.50.66.2/4 — إعادة التحقق من الخطة والدفعة قبل الاعتماد.</summary>
    private void ReverifyPlanAndLotForApproval(ProductionOrder order)
    {
        if (order.SourcePlanId != null)
        {
            var plan = Db.ProductionPlans.AsNoTracking().FirstOrDefault(p => p.Id == order.SourcePlanId);
            if (plan == null) throw new DomainException("الخطة المرجعية لأمر الإنتاج غير موجودة — لا يمكن الاعتماد.", "PLAN_MISSING");
            if (!plan.IsApproved || plan.Status != DocStatuses.Approved || plan.IsClosed)
                throw new DomainException($"لا يمكن اعتماد أمر الإنتاج: الخطة المرجعية {plan.DocumentNumber} ليست معتمدة حالياً (حالتها: {DocStatuses.ToArabic(plan.Status)}).", "PLAN_NOT_APPROVED");
        }
        // فحص الدفعات: حالة معتمدة ورصيد
        var lotIds = order.Items.Where(i => i.LotId != null).Select(i => i.LotId.Value).Distinct().ToList();
        if (lotIds.Count > 0)
        {
            var lots = Db.Lots.AsNoTracking().Where(l => lotIds.Contains(l.Id)).ToDictionary(l => l.Id);
            foreach (var item in order.Items.Where(i => i.LotId != null))
            {
                if (!lots.TryGetValue(item.LotId.Value, out var lot))
                    throw new DomainException($"الدفعة {item.LotId} غير موجودة.");
                if (lot.Status != DocStatuses.Approved)
                    throw new DomainException($"الدفعة {lot.LotCode} ليست معتمدة — لا يمكن اعتماد أمر إنتاج عليها.");
                if (lot.InStockQtyKg <= 0.001)
                    throw new DomainException($"الدفعة {lot.LotCode} رصيدها صفر.");
            }
        }
    }

    /// <summary>§8 — لا أمر أكبر من المتبقي في خطة الإنتاج (المخطط − أوامر سابقة غير ملغاة).</summary>
    private void CheckPlanRemaining(ProductionOrder order)
    {
        foreach (var item in order.Items.Where(i => i.PlanItemId != null))
        {
            var pi = Db.ProductionPlanItems.AsNoTracking().FirstOrDefault(i => i.Id == item.PlanItemId)
                     ?? throw new DomainException("بند الخطة المرتبط لم يعد موجوداً.", "BAD_PLAN_ITEM");
            double orderedBefore = Db.ProductionOrderItems.AsNoTracking()
                .Where(x => x.PlanItemId == pi.Id && x.OrderId != order.Id)
                .Join(Db.ProductionOrders.AsNoTracking(), x => x.OrderId, o => o.Id, (x, o) => new { x, o })
                .Where(z => z.o.Status != DocStatuses.Cancelled)
                .Sum(z => z.x.PlannedQtyKg);
            double remaining = pi.PlannedQtyKg - orderedBefore;
            if (item.PlannedQtyKg > remaining + 0.001)
            {
                string name = Db.Products.AsNoTracking().Where(p => p.Id == item.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-";
                throw new DomainException(
                    $"⛔ الكمية المطلوبة تتجاوز الكمية المتبقية في خطة الإنتاج.\n" +
                    $"الصنف: {name} | المخطط في الخطة: {pi.PlannedQtyKg:N1} كجم | أوامر سابقة: {orderedBefore:N1} كجم | المتبقي: {remaining:N1} كجم\n" +
                    $"المطلوب في هذا الأمر: {item.PlannedQtyKg:N1} كجم — قلّل الكمية أو أكمل المتبقي بأمر لاحق.",
                    "OVER_PLAN_REMAINING");
            }
            // §B86/M11: متبقي الكراتين أيضاً — كان الكيلو فقط فيمر تجاوز الكراتين.
            // يُفحص فقط عند تطابق العبوة (أوزان مختلفة = أعداد غير قابلة للمقارنة) مع سماح كرتونين لخطأ التقريب عبر التقسيمات
            if (pi.PlannedCartons > 0 && item.PlannedCartons > 0 && item.PackagingTypeId == pi.PackagingTypeId)
            {
                int orderedBoxesBefore = Db.ProductionOrderItems.AsNoTracking()
                    .Where(x => x.PlanItemId == pi.Id && x.OrderId != order.Id)
                    .Join(Db.ProductionOrders.AsNoTracking(), x => x.OrderId, o => o.Id, (x, o) => new { x, o })
                    .Where(z => z.o.Status != DocStatuses.Cancelled)
                    .Sum(z => z.x.PlannedCartons);
                int boxRemaining = pi.PlannedCartons - orderedBoxesBefore;
                if (item.PlannedCartons > boxRemaining + 2)
                {
                    string name = Db.Products.AsNoTracking().Where(p => p.Id == item.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-";
                    throw new DomainException(
                        $"⛔ عدد الكراتين يتجاوز المتبقي في خطة الإنتاج.\n" +
                        $"الصنف: {name} | المخطط: {pi.PlannedCartons:N0} كرتون | أوامر سابقة: {orderedBoxesBefore:N0} | المتبقي: {boxRemaining:N0}\n" +
                        $"المطلوب في هذا الأمر: {item.PlannedCartons:N0} كرتون — قلّل الكمية أو أكمل المتبقي بأمر لاحق.",
                        "OVER_PLAN_REMAINING");
                }
            }
        }
    }

    /// <summary>
    /// §11/§13 — لا أمر فوق طاقة الوردية: يحسب التحميل القائم في نفس اليوم/الوردية/الخط
    /// (بنود الخطط + الأوامر الأخرى غير الملغاة) ويمنع التجاوز — في الـ Backend لا الواجهة فقط.
    /// </summary>
    private List<string> CheckOrderCapacity(ProductionOrder order)
    {
        // §B85/H4: تُرجع تنبيهات الطاقة غير المعرَّفة (تُلحق برسالة الحفظ/الاعتماد) — كانت 500 صامتاً
        var capWarnings = new List<string>();
        if (order.ProductionDate == null || order.ShiftId == null) return capWarnings;
        int shiftId = order.ShiftId.Value;
        int lineId = order.LineId ?? 1;
        var day = order.ProductionDate.Value.Date;
        var dayEnd = day.AddDays(1);
        var shift = Db.Shifts.AsNoTracking().FirstOrDefault(s => s.Id == shiftId);
        double effHours = CapacityPolicy.EffectiveHours(shift?.EffectiveProductiveHours ?? 0, shift?.TotalHours ?? 0);
        string shiftName = shift?.ShiftNameAr ?? $"وردية {shiftId}";
        void WarnNoRate(int productId)
        {
            string nm = Db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"#{productId}";
            string w = $"⚠ تنبيه طاقة: الصنف «{nm}» بلا معدل/طاقة معرَّفة في {shiftName} يوم {day:dd/MM/yyyy} — لم تُفحص طاقته. حدّدها من «الأصناف ← طاقات الأصناف».";
            if (!capWarnings.Contains(w)) capWarnings.Add(w);
        }

        // 1) الأوامر الأخرى في نفس الفتحة (غير الملغاة) — تُحسب أولاً لأنها تستهلك طاقة بنودها من الخطط
        var otherOrderIds = Db.ProductionOrders.AsNoTracking()
            .Where(o => o.ProductionDate != null && o.ProductionDate >= day && o.ProductionDate < dayEnd
                        && o.ShiftId == shiftId && (o.LineId ?? 1) == lineId
                        && o.Status != DocStatuses.Cancelled && o.Id != order.Id)
            .Select(o => o.Id).ToList();
        var otherItems = Db.ProductionOrderItems.AsNoTracking()
            .Where(i => otherOrderIds.Contains(i.OrderId))
            .Select(i => new { i.PlanItemId, i.ProductId, i.PackagingTypeId, i.PlannedCartons }).ToList();
        double usedHours = 0;
        foreach (var oi in otherItems)
        {
            if (oi.PlannedCartons <= 0) continue;
            var r = OrderRateFor(oi.ProductId, shiftId, oi.PackagingTypeId);
            if (r > 0) usedHours += oi.PlannedCartons / r; // §B85/H4: بلا 500 صامتة — وبلا معدل لا تُحتسب ساعات
            else WarnNoRate(oi.ProductId);
        }

        // 2) بنود الخطط المجدولة في نفس الفتحة — يُحتسب فقط الجزء غير المغطى بأوامر
        // (المغطى محسوب ضمن الأوامر أعلاه) وباستثناء بنود هذا الأمر نفسه
        var linkedPlanItemIds = order.Items.Where(i => i.PlanItemId != null).Select(i => i.PlanItemId.Value).ToHashSet();
        // §إصلاح: التجميع في الذاكرة (AsEnumerable) — GroupBy/ToDictionary غير قابلة للترجمة لبعض مزودات SQL
        var orderedKgByPlanItem = Db.ProductionOrderItems.AsNoTracking()
            .Where(x => x.PlanItemId != null)
            .Join(Db.ProductionOrders.AsNoTracking(), x => x.OrderId, o => o.Id, (x, o) => new { x.PlanItemId, o.Status, x.PlannedQtyKg })
            .Where(z => z.Status != DocStatuses.Cancelled)
            .AsEnumerable()
            .GroupBy(z => z.PlanItemId.Value)
            .ToDictionary(g => g.Key, g => g.Sum(z => z.PlannedQtyKg));
        var planItems = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.ScheduledDate != null && i.ScheduledDate >= day && i.ScheduledDate < dayEnd
                        && (i.SuggestedShiftId ?? 1) == shiftId && (i.SuggestedLineId ?? 1) == lineId
                        && !i.IsClosed) // §B85/H6: البند المقفل لا يشغل طاقة
            .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
            // §B85/H6: الخطة المقفلة/الملغاة لا تشغل طاقة
            .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
            .Select(x => new { x.i.Id, x.i.ProductId, x.i.PackagingTypeId, x.i.PlannedCartons, x.i.PlannedQtyKg }).ToList()
            .Where(x => !linkedPlanItemIds.Contains(x.Id));
        foreach (var pi in planItems)
        {
            if (pi.PlannedCartons <= 0) continue;
            orderedKgByPlanItem.TryGetValue(pi.Id, out var orderedKg);
            double uncoveredKg = Math.Max(0, pi.PlannedQtyKg - orderedKg);
            if (uncoveredKg <= 0.001) continue; // مغطى بالكامل بأوامر محسوبة أعلاه
            double frac = pi.PlannedQtyKg > 0 ? uncoveredKg / pi.PlannedQtyKg : 1;
            var r = OrderRateFor(pi.ProductId, shiftId, pi.PackagingTypeId);
            if (r > 0) usedHours += pi.PlannedCartons * frac / r; // §B85/H4: بلا 500 صامتة
            else WarnNoRate(pi.ProductId);
        }

        // 3) بنود هذا الأمر — إن تجاوزت المتبقي تُرفض برسالة واضحة
        foreach (var item in order.Items)
        {
            if (item.PlannedCartons <= 0) continue;
            double rate = OrderRateFor(item.ProductId, shiftId, item.PackagingTypeId);
            // §B85/H4: معدل صفر = طاقة غير معرَّفة: تُقبل مع تنبيه (موحّد مع مسار الخطة) بدل رفض ∞
            if (rate <= 0) WarnNoRate(item.ProductId);
            double reqHours = rate > 0 ? item.PlannedCartons / rate : 0;
            if (usedHours + reqHours > effHours + 0.0001)
            {
                string name = Db.Products.AsNoTracking().Where(p => p.Id == item.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-";
                int availableCartons = (int)Math.Floor(Math.Max(0, effHours - usedHours) * rate);
                throw new DomainException(
                    $"⛔ لا يمكن إنشاء أمر الإنتاج: الكمية المطلوبة تتجاوز الطاقة الإنتاجية المتاحة للوردية.\n" +
                    $"الصنف: {name} | {shiftName} يوم {day:dd/MM/yyyy} | الخط {lineId}\n" +
                    $"الطاقة المتاحة: {availableCartons:N0} كرتون ({Math.Max(0, effHours - usedHours):N1} ساعة متبقية) | المطلوب: {item.PlannedCartons:N0} كرتون ({reqHours:N1} ساعة)\n" +
                    $"الحل: وزّع الكمية على أكثر من وردية (أمر لكل وردية) أو اختر يوماً آخر.",
                    "CAPACITY_EXCEEDED");
            }
            usedHours += reqHours; // بنود الأمر نفسه تتراكم على نفس الفتحة
        }
        return capWarnings;
    }

    /// <summary>معدل الصنف في وردية/عبوة — §عبر CapacityPolicy (مصدر واحد للترتيب).</summary>
    private double OrderRateFor(int productId, int shiftId, int? packagingTypeId)
        => CapacityPolicy.RateFor(Db, productId, shiftId, packagingTypeId);

    /// <summary>§7 — حساب المواد المساعدة من معادلات الاستهلاك لكل بند.</summary>
    private void CalculateMaterials(ProductionOrder order)
    {
        Db.ProductionOrderMaterials.RemoveRange(Db.ProductionOrderMaterials.Where(m => m.OrderId == order.Id));
        var aggOld = new Dictionary<int, double>();
        var aggNew = new Dictionary<int, decimal>(); // AuxiliaryProductId -> qty
        foreach (var item in order.Items)
        {
            int? itemCustomer = item.CustomerId ?? order.CustomerId;
            double cartons = item.PlannedCartons > 0
                ? item.PlannedCartons
                : (item.PlannedQtyKg / Math.Max(0.001, Db.Products.Where(p => p.Id == item.ProductId).Select(p => p.CartonWeightKg).FirstOrDefault()));
            // §1.50.63 — النظام الجديد: ProductAuxiliaryRequirement مع ربط كراتين العميل
            var bomReqs = Db.ProductAuxiliaryRequirements.Where(r => r.FinishedProductId == item.ProductId && r.IsActive).ToList();
            // §1.50.72 P3-4: المسار القديم (ConsumptionFormula) كان يُكتب **بالتوازي** مع الجديد
            // — صنفاً مسجلاً في النظامين (حالة انتقال شائعة) كان يُصْرَف مرتين عند تحرير الأمر.
            // القاعدة الآن: إن وُجدت متطلبات جديدة للصنف فهي الوحيدة الحاكمة (النظام الأحدث)،
            // ولا يُضاف شيء من المعادلات القديمة لنفس الصنف.
            if (bomReqs.Count == 0)
            {
                // §النظام القديم: ConsumptionFormula
                var formulas = Db.ConsumptionFormulas.Where(f => f.ProductId == item.ProductId && f.IsActive
                    && (f.CustomerId == null || f.CustomerId == itemCustomer)).ToList();
                foreach (var f in formulas)
                {
                    if (f.Mode == "Actual" || f.Mode == "PerHour" || f.Mode == "Unused") continue;
                    var matId = ResolveAuxMaterial(f, itemCustomer);
                    aggOld.TryGetValue(matId, out var cur);
                    aggOld[matId] = cur + f.QtyPerUnit * cartons;
                }
            }
            foreach (var r in bomReqs)
            {
                // تحقق هل الصنف المساعد يحتاج صرف عند الإصدار
                var cfg = Db.AuxiliaryProductConfigs.AsNoTracking().FirstOrDefault(c => c.ProductId == r.AuxiliaryProductId);
                if (cfg != null && !cfg.NeedsIssueOnOrder) continue;
                if (cfg != null && !cfg.IsActive) continue;
                // §1.50.64 — ربط الكراتين بالعميل: إذا كان الصنف المساعد كرتون وله ماركة خاصة بالعميل، استبدله
                int effectiveAuxId = ResolveAuxProductForCustomer(r.AuxiliaryProductId, itemCustomer, item.ProductId, item.PackagingTypeId);
                decimal qty = 0;
                switch (r.CalculationMethod)
                {
                    case "PerKg":
                        qty = (decimal)item.PlannedQtyKg * r.QtyPerCarton;
                        break;
                    case "PerCarton":
                    case "PerProduction":
                    default:
                        qty = (decimal)cartons * r.QtyPerCarton;
                        break;
                }
                aggNew.TryGetValue(effectiveAuxId, out var cur2);
                aggNew[effectiveAuxId] = cur2 + qty;
            }
        }
        // حفظ القديم
        foreach (var kv in aggOld)
        {
            order.Materials.Add(new ProductionOrderMaterial
            {
                OrderId = order.Id,
                MaterialId = kv.Key,
                CalculatedQty = Math.Round(kv.Value, 2),
                IsAutoCalculated = true,
                UnitOfMeasure = Db.AuxiliaryMaterials.Where(m => m.Id == kv.Key).Select(m => m.UnitOfMeasure).FirstOrDefault()
            });
        }
        // حفظ الجديد
        foreach (var kv in aggNew)
        {
            var prod = Db.Products.AsNoTracking().FirstOrDefault(p => p.Id == kv.Key);
            order.Materials.Add(new ProductionOrderMaterial
            {
                OrderId = order.Id,
                MaterialId = 0,
                AuxiliaryProductId = kv.Key,
                CalculatedQty = Math.Round((double)kv.Value, 3),
                IsAutoCalculated = true,
                UnitOfMeasure = prod?.UnitOfMeasure ?? "وحدة"
            });
        }
    }

    // ═══════════════════════════ بنود الخطة القابلة للأمر ═══════════════════════════

    public List<OrderableItemDto> GetOrderableItems(int planId)
    {
        var plan = Db.ProductionPlans.AsNoTracking().FirstOrDefault(p => p.Id == planId);
        if (plan == null || !plan.IsApproved || plan.IsClosed || plan.Status != DocStatuses.Approved) return new List<OrderableItemDto>();
        var day = Db.BusinessNow.Date;
        var items = Db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == planId && i.ScheduledDate != null && i.ScheduledDate.Value.Date == day && !i.IsClosed).OrderBy(i => i.PriorityNo).ToList();
        var result = new List<OrderableItemDto>();
        foreach (var pi in items)
        {
            double orderedKg = 0; int orderedCartons = 0;
            var prev = Db.ProductionOrderItems.AsNoTracking()
                .Where(x => x.PlanItemId == pi.Id)
                .Join(Db.ProductionOrders.AsNoTracking(), x => x.OrderId, o => o.Id, (x, o) => new { x, o })
                .Where(z => z.o.Status != DocStatuses.Cancelled)
                .Select(z => z.x).ToList();
            orderedKg = prev.Sum(x => x.PlannedQtyKg);
            orderedCartons = prev.Sum(x => x.PlannedCartons);

            var lot = pi.LotId != null ? Db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == pi.LotId) : null;
            // §B86/L2: متبقي الدفعة الحقيقي = الرصيد − حجوزات الخطط − الأوامر المستقلة (بلا ازدواج: أمر الخطة داخل حصة خطته)
            double lotAvail = lot != null ? Math.Max(0, lot.InStockQtyKg - lot.UnderTreatmentQtyKg) : 0;
            if (lot != null)
            {
                double planLive = Db.ProductionPlanItems.AsNoTracking()
                    .Where(i => i.LotId == lot.Id)
                    .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
                    .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
                    .Where(x => !x.i.IsClosed)
                    .Sum(x => x.i.PlannedQtyKg - x.i.ProducedQtyKg > 0 ? x.i.PlannedQtyKg - x.i.ProducedQtyKg : 0);
                double standaloneLive = Db.ProductionOrderItems.AsNoTracking()
                    .Where(i => i.LotId == lot.Id && i.PlanItemId == null)
                    .Join(Db.ProductionOrders.AsNoTracking(), i => i.OrderId, o => o.Id, (i, o) => new { i, o })
                    .Where(x => x.o.Status != DocStatuses.Cancelled && x.o.Status != DocStatuses.Closed)
                    .Where(x => !x.i.IsClosed)
                    .Sum(x => x.i.PlannedQtyKg - x.i.ProducedQtyKg > 0 ? x.i.PlannedQtyKg - x.i.ProducedQtyKg : 0);
                // §المعالجة والتعقيم (الموضع 11): بوابة أمر الإنتاج تستبعد ما تحت المعالجة
                lotAvail = Math.Max(0, lot.InStockQtyKg - lot.UnderTreatmentQtyKg - planLive - standaloneLive);
            }
            result.Add(new OrderableItemDto
            {
                PlanItemId = pi.Id,
                PlanId = planId,
                PlanNumber = plan.DocumentNumber,
                PlanTitle = plan.PlanTitle,
                PlanDate = plan.StartDate?.ToString("dd/MM/yyyy"),
                CustomerId = pi.CustomerId,
                CustomerName = pi.CustomerId != null
                    ? Db.Customers.AsNoTracking().Where(c => c.Id == pi.CustomerId).Select(c => c.CustomerName).FirstOrDefault()
                    : "-",
                LotId = pi.LotId,
                LotCode = lot?.LotCode ?? "-",
                RawName = lot != null
                    ? Db.Products.AsNoTracking().Where(p => p.Id == lot.ProductId).Select(p => p.ProductNameAr).FirstOrDefault()
                    : "-",
                LotRemainingKg = lotAvail,
                ProductId = pi.ProductId,
                ProductName = Db.Products.AsNoTracking().Where(p => p.Id == pi.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                PackagingTypeId = pi.PackagingTypeId,
                PackName = pi.PackagingTypeId != null
                    ? Db.PackagingTypes.AsNoTracking().Where(p => p.Id == pi.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault()
                    : "-",
                PlannedKg = pi.PlannedQtyKg,
                PlannedCartons = pi.PlannedCartons,
                OrderedKg = orderedKg,
                OrderedCartons = orderedCartons,
                RemainingKg = Math.Max(0, pi.PlannedQtyKg - orderedKg),
                RemainingCartons = Math.Max(0, pi.PlannedCartons - orderedCartons),
                ProducedKg = pi.ProducedQtyKg,
                ScheduledDate = pi.ScheduledDate?.ToString("dd/MM/yyyy"),
                SuggestedShiftId = pi.SuggestedShiftId,
                SuggestedLineId = pi.SuggestedLineId
            });
        }
        return result;
    }

    /// <summary>
    /// §B93 — ترحيل الخطة إلى أوامر (المرحلة التالية بعد الاعتماد):
    /// بنود الخطة المعتمدة ذات المتبقي تُجمَّع (تاريخ مجدول × وردية × خط) — أمر واحد لكل مجموعة
    /// بكامل المتبقي، عبر SaveOrder نفسه (كل حراسه: المتبقي/الطاقة/الهوية/التحويل).
    /// كل مجموعة معاملة مستقلة: فشل مجموعة لا يوقف البقية، والملخص يذكر كل شيء بصدق.
    /// </summary>
    public PlanIssueResult IssueOrdersFromPlan(int planId, string fromDate = null, string toDate = null, int? shiftId = null)
    {
        Require("production", "Create");
        var result = new PlanIssueResult { PlanId = planId };
        var plan = Db.ProductionPlans.AsNoTracking().FirstOrDefault(p => p.Id == planId);
        if (plan == null)
        {
            result.Message = "الخطة غير موجودة.";
            result.Failed.Add(result.Message);
            return result;
        }
        result.PlanNumber = plan.DocumentNumber;
        if (!plan.IsApproved || plan.Status != DocStatuses.Approved || plan.IsClosed)
        {
            result.Message = "لا يمكن ترحيل خطة غير معتمدة أو مغلقة.";
            result.Failed.Add(result.Message);
            return result;
        }
        DateTime? from = null, to = null;
        if (!string.IsNullOrWhiteSpace(fromDate))
        {
            if (!UiFormat.TryParseDate(fromDate, out var parsed))
            {
                result.Message = "تاريخ البداية غير صالح.";
                result.Failed.Add(result.Message);
                return result;
            }
            from = parsed.Date;
        }
        if (!string.IsNullOrWhiteSpace(toDate))
        {
            if (!UiFormat.TryParseDate(toDate, out var parsed))
            {
                result.Message = "تاريخ النهاية غير صالح.";
                result.Failed.Add(result.Message);
                return result;
            }
            to = parsed.Date;
        }
        if (from != null && to != null && from > to)
        {
            result.Message = "فترة الترحيل غير صحيحة: تاريخ البداية بعد النهاية.";
            result.Failed.Add(result.Message);
            return result;
        }

        var groups = GetPendingPlanGroups().Where(g => g.PlanId == planId).Where(g =>
        {
            if (!UiFormat.TryParseDate(g.ScheduledDate, out var d)) return false;
            var day = d.Date;
            return (!from.HasValue || day >= from.Value) && (!to.HasValue || day <= to.Value)
                && (!shiftId.HasValue || g.ShiftId == shiftId);
        }).ToList();
        foreach (var group in groups)
        {
            var issued = IssuePlanGroup(planId, group.ScheduledDate, group.CustomerId, group.ShiftId, group.LineId);
            if (!issued.Ok)
            {
                result.Failed.Add($"{group.ScheduledDate} / {group.CustomerName}: {issued.Message}");
                continue;
            }
            result.Created.Add(new IssuedOrderDto
            {
                OrderId = issued.Id,
                OrderNumber = issued.DocumentNumber,
                ProductionDate = group.ScheduledDate,
                ShiftName = group.ShiftName,
                LineName = group.LineName,
                ItemsCount = group.ItemsCount,
                TotalKg = group.PlannedKg
            });
        }
        result.Ok = result.Failed.Count == 0;
        result.Message = groups.Count == 0
            ? "لا توجد مجموعات خطة معتمدة ومتاحة ضمن الفترة المحددة — قد تكون صادرة سابقاً أو لا توجد جدولة بهذا التاريخ."
            : result.Failed.Count == 0
                ? $"تم إنشاء {result.Created.Count} أمر إنتاج من الخطة {plan.DocumentNumber}."
                : $"تم إنشاء {result.Created.Count} أمر، وتعذر إنشاء {result.Failed.Count} مجموعة. راجع التفاصيل.";
        return result;
    }

    /// <summary>§10/§12 — طاقة فتحة يوم/وردية/خط لصنف محدد — للعرض والتوزيع على الورديات.</summary>
    public OrderSlotInfo GetOrderSlot(int productId, int? packagingTypeId, int shiftId, int lineId, string date)
    {
        var shift = Db.Shifts.AsNoTracking().FirstOrDefault(s => s.Id == shiftId);
        double effHours = CapacityPolicy.EffectiveHours(shift?.EffectiveProductiveHours ?? 0, shift?.TotalHours ?? 0);
        double rate = OrderRateFor(productId, shiftId, packagingTypeId);
        UiFormat.TryParseDate(date, out var day);
        var dayEnd = day.Date.AddDays(1);

        double usedHours = 0;
        var orderIds = Db.ProductionOrders.AsNoTracking()
            .Where(o => o.ProductionDate != null && o.ProductionDate >= day.Date && o.ProductionDate < dayEnd
                        && o.ShiftId == shiftId && (o.LineId ?? 1) == lineId && o.Status != DocStatuses.Cancelled)
            .Select(o => o.Id).ToList();
        var orderItems = Db.ProductionOrderItems.AsNoTracking()
            .Where(i => orderIds.Contains(i.OrderId))
            .Select(i => new { i.ProductId, i.PackagingTypeId, i.PlannedCartons }).ToList();
        foreach (var oi in orderItems.Where(x => x.PlannedCartons > 0))
        {
            var r = OrderRateFor(oi.ProductId, shiftId, oi.PackagingTypeId);
            if (r > 0) usedHours += oi.PlannedCartons / r; // §B85/H4: بلا 500 صامتة
        }
        // بنود الخطط: الجزء غير المغطى بأوامر فقط (المغطى محسوب ضمن الأوامر أعلاه)
        // §إصلاح: التجميع في الذاكرة (AsEnumerable) — GroupBy/ToDictionary غير قابلة للترجمة لبعض مزودات SQL
        var orderedKgByPlanItem = Db.ProductionOrderItems.AsNoTracking()
            .Where(x => x.PlanItemId != null)
            .Join(Db.ProductionOrders.AsNoTracking(), x => x.OrderId, o => o.Id, (x, o) => new { x.PlanItemId, o.Status, x.PlannedQtyKg })
            .Where(z => z.Status != DocStatuses.Cancelled)
            .AsEnumerable()
            .GroupBy(z => z.PlanItemId.Value)
            .ToDictionary(g => g.Key, g => g.Sum(z => z.PlannedQtyKg));
        var planItems = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.ScheduledDate != null && i.ScheduledDate >= day.Date && i.ScheduledDate < dayEnd
                        && (i.SuggestedShiftId ?? 1) == shiftId && (i.SuggestedLineId ?? 1) == lineId
                        && !i.IsClosed) // §B85/H6: البند المقفل لا يشغل طاقة
            .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
            // §B85/H6: الخطة المقفلة/الملغاة لا تشغل طاقة
            .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
            .Select(x => new { x.i.Id, x.i.ProductId, x.i.PackagingTypeId, x.i.PlannedCartons, x.i.PlannedQtyKg }).ToList();
        foreach (var pi in planItems.Where(x => x.PlannedCartons > 0))
        {
            orderedKgByPlanItem.TryGetValue(pi.Id, out var orderedKg);
            double uncoveredKg = Math.Max(0, pi.PlannedQtyKg - orderedKg);
            if (uncoveredKg <= 0.001) continue;
            double frac = pi.PlannedQtyKg > 0 ? uncoveredKg / pi.PlannedQtyKg : 1;
            var r = OrderRateFor(pi.ProductId, shiftId, pi.PackagingTypeId);
            if (r > 0) usedHours += pi.PlannedCartons * frac / r; // §B85/H4: بلا 500 صامتة
        }

        int capacity = (int)(effHours * rate);
        int used = (int)(usedHours * rate);
        // §B85/H4: معدل صفر = طاقة غير معرَّفة — التوزيع التلقائي سيجد صفراً متاحاً فيوجَّه المستخدم لتعريف الطاقة
        string capNote = rate > 0 ? null
            : "⚠ تنبيه طاقة: هذا الصنف بلا معدل/طاقة معرَّفة في هذه الوردية — حدّدها من «الأصناف ← طاقات الأصناف» قبل التوزيع.";
        return new OrderSlotInfo
        {
            ShiftId = shiftId,
            ShiftName = shift?.ShiftNameAr ?? "-",
            ShiftStart = shift?.StartTime ?? "-",
            ShiftEnd = shift?.EndTime ?? "-",
            ProductionHours = effHours,
            RatePerHour = rate,
            CapacityCartons = capacity,
            UsedCartons = Math.Min(capacity, used),
            RemainingCartons = Math.Max(0, capacity - used),
            CapacityNote = capNote
        };
    }

    // ═══════════════════════════ آلة الحالات ═══════════════════════════

    /// <summary>§8 — الاعتماد: صرف المواد المحتسبة من مخزن المواد المساعدة وخصم أرصدة الخام من الدفعات — ذرياً.</summary>
    public OpResult ApproveOrder(int orderId)
    {
        Require("production", "Approve");
        var order = Db.ProductionOrders.Include(o => o.Items).Include(o => o.Materials)
            .FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("أمر الإنتاج غير موجود.");
        if (order.IsApproved) return OpResult.Fail("الأمر معتمد مسبقاً.");
        if (order.Status == DocStatuses.Cancelled) return OpResult.Fail("الأمر ملغي — لا يمكن اعتماده. أنشئ أمراً بديلاً.");
        if (order.Items.Count == 0) return OpResult.Fail("لا يمكن اعتماد أمر بدون بنود.");

        return RunOp(() =>
        {
            // §1.50.66.4 — إعادة التحقق قبل السماح بالتنفيذ
            ReverifyPlanAndLotForApproval(order);
            ValidateScheduledOrder(order, requireToday: false);
            GuardReceivingOrderReadiness(order.Items.Select(i => i.LotId));
            // §متبقي الخطة والطاقة يُعاد فحصهما عند الاعتماد أيضاً (قد تكونت أوامر بعد الحفظ)
            CheckPlanRemaining(order);
            var capWarnAppr = ValidateSourceCapacity(order);

            order.IsApproved = true;
            order.Status = order.ProductionDate != null && order.ShiftId != null ? DocStatuses.Scheduled : DocStatuses.Approved;
            order.ApprovedBy = Session?.UserId;
            order.ApprovedDate = DateTime.Now;
            foreach (var it in order.Items) it.Status = DocStatuses.Approved;

            // §قاعدة توازن الإنتاج: لا يُخصم الخام عند الاعتماد.
            // كان يُخصم هنا بوزن المنتج التام المخطط — أي بافتراض أن الخام = المنتج،
            // وهذه معادلة ثابتة ترفضها القاعدة لأن وزن الخارج يزيد عن الداخل لإضافة
            // الماء أثناء التشغيل. فالخام يُصرف عند الإقفال بالكمية المستهلكة فعلياً.
            var whRaw = WarehouseId("WRM");

            // MaterialId (legacy) and AuxiliaryProductId (new BOM) are different
            // inventory keys. Do not ever issue a ProductId as MaterialId=0.
            var whAux = WarehouseId("WAUX");
            var auxWarnings = new List<string>();
            int previousIssueCycles = Db.InventoryTransactions.AsNoTracking()
                .Where(t => t.OrderId == order.Id && t.ReferenceDocType == ReferenceDocType.MaterialIssue
                    && (t.ReferenceDocNumber == order.DocumentNumber
                        || t.ReferenceDocNumber.StartsWith(order.DocumentNumber + "#APP-")))
                .Select(t => t.ReferenceDocNumber).Distinct().Count();
            string approvalIssueRef = previousIssueCycles == 0 ? order.DocumentNumber
                : $"{order.DocumentNumber}#APP-{previousIssueCycles + 1}";
            foreach (var mat in order.Materials.Where(m => m.CalculatedQty > 0))
            {
                bool modern = mat.AuxiliaryProductId is > 0;
                if (!modern && mat.MaterialId <= 0)
                    throw new DomainException("بند مادة مساعدة بلا هوية مخزنية صالحة.", "AUX_IDENTITY");
                var balance = Db.StockBalances.FirstOrDefault(b => b.WarehouseId == whAux
                    && b.ProductId == (modern ? mat.AuxiliaryProductId : null)
                    && b.MaterialId == (modern ? null : mat.MaterialId)
                    && b.LotId == null && b.CustomerId == null && b.PackagingTypeId == null);
                double available = modern ? AuxiliaryStockPolicy.Available(balance, mat.UnitOfMeasure)
                    : balance?.QtyKg ?? 0;
                if (modern) AuxiliaryStockPolicy.Movement(mat.CalculatedQty, mat.UnitOfMeasure);
                double outstanding = Math.Max(0, mat.CalculatedQty - mat.ActualIssuedQty + mat.ReturnedQty);
                if (available + 0.001 < outstanding && StrictAuxEnabled())
                    throw new DomainException(
                        $"رصيد المادة المساعدة غير كافٍ للاعتماد. المتاح {available:N1} — المطلوب {outstanding:N1}",
                        "INSUFFICIENT_MATERIAL");
                double issueQty = Math.Min(outstanding, Math.Max(0, available));
                if (modern && !AuxiliaryStockPolicy.IsKg(mat.UnitOfMeasure))
                    issueQty = Math.Floor(issueQty + 0.001);
                if (issueQty > 0.001)
                {
                    var (kg, packages) = modern
                        ? AuxiliaryStockPolicy.Movement(issueQty, mat.UnitOfMeasure) : (issueQty, 0);
                    PostStockMovement(whAux, MovementType.Outbound, kg, packages,
                        ReferenceDocType.MaterialIssue, approvalIssueRef,
                        productId: modern ? mat.AuxiliaryProductId : null,
                        materialId: modern ? null : mat.MaterialId, orderId: order.Id,
                        notes: $"صرف مواد عند اعتماد أمر {order.DocumentNumber}");
                    mat.ActualIssuedQty += issueQty;
                }
                mat.Status = mat.ActualIssuedQty - mat.ReturnedQty > 0.001 ? DocStatuses.Issued : DocStatuses.Draft;
                if (issueQty + 0.001 < outstanding)
                    auxWarnings.Add($"مادة {(modern ? $"صنف #{mat.AuxiliaryProductId}" : $"مادة #{mat.MaterialId}")}: صرف {issueQty:N3} من {outstanding:N3} (المتبقي {outstanding - issueQty:N3}).");
            }

            Db.SaveChanges();
            string stateAr = order.Status == DocStatuses.Scheduled ? "معتمد ومجدول 📅" : "معتمد";
            // §B85/M7: تصحيح الرسالة — الخام يُصرف عند الإقفال لا الاعتماد؛ §B85/H4: تنبيه الطاقة غير المعرَّفة
            string capMsgAppr = capWarnAppr.Count > 0 ? "\n" + string.Join("\n", capWarnAppr) : "";
            return OpResult.Success($"تم اعتماد أمر الإنتاج {order.DocumentNumber} ({stateAr}) — صُرفت الكميات المتاحة فقط من المواد المساعدة، والخام يُصرف فعلياً عند إقفال يوم الإنتاج." +
                (auxWarnings.Count == 0 ? "" : "\nتنبيه نقص مواد مساعدة: " + string.Join("؛ ", auxWarnings))
                + capMsgAppr, order.Id, order.DocumentNumber);
        });
    }

    public OpResult UnapproveOrder(int orderId)
    {
        Require("production", "Cancel");
        var order = Db.ProductionOrders.Include(o => o.Items).FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("الأمر غير موجود.");
        if (!order.IsApproved) return OpResult.Fail("الأمر غير معتمد.");
        if (OrderHasIrreversibleExecution(orderId) || Db.ProductionExecutions.Any(e => e.OrderId == orderId))
            return OpResult.Fail("لا يمكن إلغاء الاعتماد: يوجد تنفيذ مسجل على الأمر — مرحلة غير قابلة للرجوع.");

        return RunOp(() =>
        {
            ReverseConsumption(order);
            order.IsApproved = false;
            order.Status = DocStatuses.Draft;
            Db.SaveChanges();
            return OpResult.Success("تم إلغاء الاعتماد وعكس كل حركات الصرف.");
        });
    }

    /// <summary>§15 — بدء الإنتاج: وقت البداية الفعلي + المستخدم + الحالة «قيد التنفيذ».</summary>
    public OpResult StartOrder(int orderId)
    {
        Require("execution", "Create");
        var order = Db.ProductionOrders.Include(o => o.Items).FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("أمر الإنتاج غير موجود.");
        if (!order.IsApproved) return OpResult.Fail("لا يمكن بدء الإنتاج لأمر غير معتمد — اعتمد الأمر أولاً.");
        if (order.IsClosed) return OpResult.Fail("الأمر مغلق مسبقاً.");
        if (order.Status == DocStatuses.InProgress) return OpResult.Fail("الأمر قيد التنفيذ بالفعل.");
        if (order.Status == DocStatuses.Completed || order.Status == DocStatuses.Closed)
            return OpResult.Fail("الأمر مكتمل — لا يمكن بدء الإنتاج من جديد.");
        if (order.Status == DocStatuses.Cancelled) return OpResult.Fail("الأمر ملغي — لا يمكن بدء الإنتاج.");
        if (Db.ProductionExecutions.Any(e => e.OrderId == orderId && e.Status == DocStatuses.InProgress))
            return OpResult.Fail("توجد جلسة تنفيذ فعالة بالفعل على هذا الأمر.");

        return RunOp(() =>
        {
            ValidateTodayOrder(order);
            GuardReceivingOrderReadiness(order.Items.Select(i => i.LotId));
            var exe = new ProductionExecution
            {
                DocumentNumber = Numbering.Next("EXE"),
                OrderId = orderId,
                LineId = order.LineId,
                ShiftId = order.ShiftId,
                StartDateTime = DateTime.Now,
                Status = DocStatuses.InProgress
            };
            Db.ProductionExecutions.Add(exe);
            order.Status = DocStatuses.InProgress;
            Db.SaveChanges();
            return OpResult.Success(
                $"🏭 بدأ الإنتاج للأمر {order.DocumentNumber} — الجلسة {exe.DocumentNumber}.\n" +
                $"وقت البداية: {DateTime.Now:dd/MM/yyyy HH:mm} | المستخدم: {Session?.UserName ?? "-"}", exe.Id, exe.DocumentNumber);
        });
    }

    /// <summary>§إيقاف مؤقت أثناء التنفيذ — لا يفقد أي بيانات ويُستأنف.</summary>
    public OpResult StopOrder(int orderId, string reason = null)
    {
        Require("execution", "Edit");
        // §B102 (سحب B98) — الإيقاف بلا سبب ممنوع على مستوى الخادم (التدقيق: سبب إجباري، 10 أحرف على الأقل)
        if (string.IsNullOrWhiteSpace(reason))
            return OpResult.Fail("سبب الإيقاف إجباري — اكتب سبب التوقف (عطل/نقص مواد/...).");
        if (reason.Trim().Length < 10)
            return OpResult.Fail("سبب الإيقاف قصير — 10 أحرف على الأقل حتى يكون قابلاً للتدقيق.");
        var order = Db.ProductionOrders.FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("أمر الإنتاج غير موجود.");
        if (order.Status != DocStatuses.InProgress)
            return OpResult.Fail($"لا يمكن إيقاف أمر حالته «{DocStatuses.ToArabic(order.Status)}» — الإيقاف يكون لأمر قيد التنفيذ فقط.");
        return RunOp(() =>
        {
            order.Status = DocStatuses.Stopped;
            // §B102 (سحب B98): لافتة سبب التوقف — تُعرض في مركز المهام
            order.StatusReason = string.IsNullOrWhiteSpace(reason) ? "موقوف مؤقتاً" : reason.Trim();
            if (!string.IsNullOrWhiteSpace(reason)) order.Notes = (order.Notes + $"\n[توقف {DateTime.Now:dd/MM/yyyy HH:mm}] {reason}").Trim();
            Db.SaveChanges();
            return OpResult.Success($"⏸ تم إيقاف الأمر {order.DocumentNumber} مؤقتاً — يمكن استئنافه في أي وقت.");
        });
    }

    /// <summary>§استئناف أمر متوقف.</summary>
    public OpResult ResumeOrder(int orderId)
    {
        Require("execution", "Edit");
        var order = Db.ProductionOrders.FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("أمر الإنتاج غير موجود.");
        if (order.Status != DocStatuses.Stopped)
            return OpResult.Fail($"لا يمكن استئناف أمر حالته «{DocStatuses.ToArabic(order.Status)}» — الاستئناف يكون لأمر متوقف فقط.");
        return RunOp(() =>
        {
            Db.Entry(order).Collection(o => o.Items).Load();
            ValidateTodayOrder(order);
            GuardReceivingOrderReadiness(Db.ProductionOrderItems.Where(i => i.OrderId == orderId).Select(i => i.LotId).ToList());
            order.Status = DocStatuses.InProgress;
            order.StatusReason = null; // §B102: الاستئناف يمسح لافتة التوقف
            Db.SaveChanges();
            return OpResult.Success($"▶ تم استئناف الأمر {order.DocumentNumber} — عاد إلى قيد التنفيذ.");
        });
    }

    /// <summary>§إلغاء الأمر قبل الإنتاج — مع عكس الصرف إن كان معتمداً، ويبقى في السجل للتدقيق.</summary>
    public OpResult CancelOrder(int orderId, string reason = null)
    {
        Require("production", "Cancel");
        var order = Db.ProductionOrders.Include(o => o.Items).Include(o => o.Materials).FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("أمر الإنتاج غير موجود.");
        if (order.Status == DocStatuses.Cancelled) return OpResult.Fail("الأمر ملغي مسبقاً.");
        if (order.Status is DocStatuses.InProgress or DocStatuses.Stopped or DocStatuses.Completed or DocStatuses.Closed)
            return OpResult.Fail(
                $"لا يمكن إلغاء أمر حالته «{DocStatuses.ToArabic(order.Status)}».\n" +
                "بعد بدء الإنتاج لا يُلغى الأمر — استخدم الإقفال أو الإرجاع حسب الصلاحيات، مع بقاء السجل كاملاً للتدقيق.");

        // §1.50.66.5 — لا إلغاء بعد التنفيذ غير القابل للرجوع
        if (OrderHasIrreversibleExecution(orderId) || Db.ProductionExecutions.Any(e => e.OrderId == orderId))
            return OpResult.Fail(
                "لا يمكن إلغاء أمر له جلسات تنفيذ مسجّلة — استُهلك خامه فعلياً ودخل مرحلة غير قابلة للرجوع.\n" +
                "استخدم الإقفال بتسوية موثقة ليبقى أثر الاستهلاك في السجل.");

        return RunOp(() =>
        {
            if (order.IsApproved) ReverseConsumption(order);
            order.IsApproved = false;
            order.Status = DocStatuses.Cancelled;
            if (!string.IsNullOrWhiteSpace(reason)) order.Notes = (order.Notes + $"\n[إلغاء {DateTime.Now:dd/MM/yyyy HH:mm}] {reason}").Trim();
            Db.SaveChanges();
            return OpResult.Success(
                $"تم إلغاء الأمر {order.DocumentNumber} وعكس صرفه إن وُجد.\n" +
                "المتبقي في الخطة أصبح متاحاً لأمر جديد.");
        });
    }

    /// <summary>عكس صرف المواد المساعدة (لإلغاء الاعتماد/الإلغاء).</summary>
    /// <remarks>
    /// §إصلاح حرج — خلق مخزون خام من العدم:
    /// كانت الدالة تُعيد <c>PlannedQtyKg</c> إلى رصيد الدفعة ومخزن الخام عند كل إلغاء،
    /// بافتراض أن الاعتماد خصم الخام. لكن <see cref="ApproveOrder"/> **لا يخصم الخام
    /// إطلاقاً** (قاعدة توازن الإنتاج: الخام يُصرف عند الإقفال بالمستهلك الفعلي عبر
    /// ConsumeLot). فكان العكس يضيف كمية لم تُخصم قط:
    ///   • أمر معتمد بلا تنفيذ ثم إلغاء ⟵ رصيد الدفعة يرتفع بالمخطط كاملاً من العدم.
    ///   • أمر أُقفل يومه جزئياً (حالته تبقى Scheduled فلا يمنعه حارس CancelOrder)
    ///     ثم إلغاء ⟵ يُضاف المخطط فوق ما استُهلك فعلاً، ويُحذف قيد الاستهلاك الحقيقي
    ///     بـ RemoveRange فيضيع أثره من دفتر الحركة.
    /// كرر ذلك على الأمر نفسه وتتضاعف الكمية بلا سقف.
    ///
    /// الصحيح: لا شيء يُعكس على الخام هنا لأن لا شيء صُرف. عكس الاستهلاك الفعلي — إن
    /// وُجد — من اختصاص مسار عكس الإقفال في ExecutionService حيث الكمية المستهلكة معروفة.
    /// المواد المساعدة تبقى تُعكس: هي فعلاً تُصرف عند الاعتماد.
    /// </remarks>
    private void ReverseConsumption(ProductionOrder order)
    {
        var whAux = WarehouseId("WAUX");
        int reversalCycle = Db.InventoryTransactions.AsNoTracking()
            .Where(t => t.OrderId == order.Id && t.ReferenceDocType == ReferenceDocType.Return
                && t.ReferenceDocNumber.StartsWith(order.DocumentNumber + "#REV-AUX-"))
            .Select(t => t.ReferenceDocNumber).Distinct().Count() + 1;
        foreach (var mat in Db.ProductionOrderMaterials.Where(m => m.OrderId == order.Id && m.ActualIssuedQty > 0))
        {
            if (mat.ConsumedQty > 0.001 || mat.WastedQty > 0.001)
                throw new DomainException("لا يمكن عكس اعتماد الأمر بعد استهلاك/هلاك مواد مساعدة؛ سوِّ المواد أولاً.", "AUX_ALREADY_CONSUMED");
            bool modern = mat.AuxiliaryProductId is > 0;
            if (!modern && mat.MaterialId <= 0)
                throw new DomainException("بند مادة مساعدة بلا هوية مخزنية صالحة.", "AUX_IDENTITY");
            // Gross issued remains in the audit/issue register. Only net stock
            // out (issued minus already returned) is reversed, under the SAME
            // ProductId or MaterialId used on the debit, never MaterialId=0.
            double outstanding = mat.ActualIssuedQty - mat.ReturnedQty;
            if (outstanding < -0.001)
                throw new DomainException("مرتجع المواد المساعدة يتجاوز المصروف؛ راجع حركة الأمر.", "AUX_RETURN_OVERFLOW");
            if (outstanding > 0.001)
            {
                var (kg, packages) = modern
                    ? AuxiliaryStockPolicy.Movement(outstanding, mat.UnitOfMeasure) : (outstanding, 0);
                PostStockMovement(whAux, MovementType.Inbound, kg, packages,
                    ReferenceDocType.Return, $"{order.DocumentNumber}#REV-AUX-{reversalCycle}",
                    productId: modern ? mat.AuxiliaryProductId : null,
                    materialId: modern ? null : mat.MaterialId, orderId: order.Id,
                    notes: $"قيد عكسي لصرف مواد الأمر {order.DocumentNumber}، بعد خصم المرتجعات السابقة");
                mat.ReturnedQty += outstanding;
            }
            mat.Status = DocStatuses.Draft;
        }
    }

    /// <summary>
    /// §23 — تعديل بيانات التنفيذ (التاريخ/الوردية/الخط/الملاحظات) فقط.
    /// الهوية (العميل/الصنف/المنتج/الخطة) مقفولة نهائياً، وبعد بدء الإنتاج لا تعديل إطلاقاً.
    /// </summary>
    /// <summary>
    /// §v1.50.34 — زر «حفظ» في رأس لوحة أمر الإنتاج: الملاحظات وحدها تُعدَّل من الشاشة.
    /// الجدولة (التاريخ/الوردية/الخط) وكميات البنود ثابتة من الخطة المعتمدة — الحراسة الأصلية باقية.
    /// </summary>
    public OpResult UpdateOrderHeader(int orderId, string productionDate = null, int? shiftId = null, int? lineId = null, string notes = null)
    {
        Require("production", "Edit");
        if (productionDate != null || shiftId != null || lineId != null)
            return OpResult.Fail("تاريخ أمر الإنتاج وورديته وخطه من الخطة المعتمدة؛ لا إعادة جدولة من شاشة أمر الإنتاج. راجع جهة التخطيط.");
        return RunOp(() =>
        {
            var order = Db.ProductionOrders.FirstOrDefault(o => o.Id == orderId);
            if (order == null) return OpResult.Fail("أمر الإنتاج غير موجود.");
            // §1.50.66.5 — منع التعديل بعد التنفيذ غير القابل للرجوع
            if (OrderHasIrreversibleExecution(orderId))
                return OpResult.Fail("لا يمكن تعديل أمر الإنتاج: دخل مرحلة التنفيذ الفعلي (ProductionExecutions) — مرحلة غير قابلة للرجوع.");
            // §قفل التتبع: بعد بدء التنفيذ (أو الإقفال/الإلغاء/الاكتمال) لا تُعدَّل حتى الملاحظات.
            if (order.Status is DocStatuses.InProgress or DocStatuses.Completed or DocStatuses.Closed or DocStatuses.Cancelled)
                return OpResult.Fail("بعد بدء التنفيذ لا تُعدَّل بيانات الأمر حتى الملاحظات — قفل الهوية والتتبع.");
            order.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
            Db.SaveChanges();
            return OpResult.Success("تم حفظ ملاحظات الأمر.");
        });
    }

    /// <summary>
    /// §B80 — تعديل بنود أمر إنتاج (مسودة لم يبدأ تنفيذها): كميات البنود القائمة فقط
    /// (كراتين/كجم) — الهوية مقفلة كما في تعديل الرأس. الحراس: كمية موجبة لكل بند،
    /// اتساق الكرتون/الكيلو، متبقي الخطة، طاقة الوردية، ثم إعادة احتساب المواد.
    /// </summary>
    public OpResult UpdateOrderItems(int orderId, List<OrderItemDto> items)
    {
        Require("production", "Edit");
        return OpResult.Fail("أصناف أمر الإنتاج وكمياته ثابتة من الخطة المعتمدة؛ لا تعديل أو إضافة أو حذف من أمر الإنتاج. راجع جهة التخطيط.");
    }

    /// <summary>§v1.50.34 — زر «بحث»: أحدث الأوامر المطابقة برقم المستند أو اسم العميل.</summary>
    public List<OrderSearchRowDto> SearchOrders(string term, int take = 50)
    {
        var recent = Db.ProductionOrders.AsNoTracking().OrderByDescending(o => o.Id).Take(400)
            .Select(o => new { o.Id, o.DocumentNumber, o.ProductionDate, o.Status,
                Customer = o.CustomerId != null ? Db.Customers.Where(c => c.Id == o.CustomerId).Select(c => c.CustomerName).FirstOrDefault() : null })
            .ToList();
        if (!string.IsNullOrWhiteSpace(term))
        {
            var t = term.Trim();
            recent = recent.Where(o => (o.DocumentNumber ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
                                    || (o.Customer ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        return recent.Take(take).Select(o => new OrderSearchRowDto
        {
            OrderId = o.Id,
            DocumentNumber = o.DocumentNumber,
            ProductionDate = o.ProductionDate?.ToString("dd/MM/yyyy") ?? "-",
            CustomerName = o.Customer ?? "-",
            StatusAr = DocStatuses.ToArabic(o.Status)
        }).ToList();
    }

    // ═══════════════════════════ بطاقة الملخص والسجل ═══════════════════════════

    public OrderCardDto GetOrderCard(int orderId)
    {
        var order = Db.ProductionOrders.AsNoTracking().Include(o => o.Items).FirstOrDefault(o => o.Id == orderId);
        if (order == null) return null;

        var firstLot = order.Items.Select(i => i.LotId).FirstOrDefault(l => l != null);
        var lot = firstLot != null ? Db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == firstLot) : null;
        var shift = order.ShiftId != null ? Db.Shifts.AsNoTracking().FirstOrDefault(s => s.Id == order.ShiftId) : null;

        // المخطط في الخطة للبنود المرتبطة (كجم + كرتون)
        var planItemIds = order.Items.Where(i => i.PlanItemId != null).Select(i => i.PlanItemId.Value).Distinct().ToList();
        var planItems = Db.ProductionPlanItems.AsNoTracking().Where(i => planItemIds.Contains(i.Id)).ToList();
        var customerNames = order.Items.Where(i => i.CustomerId != null)
            .Select(i => Db.Customers.AsNoTracking().Where(c => c.Id == i.CustomerId)
                .Select(c => c.CustomerName).FirstOrDefault())
            .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToList();
        var customerSummary = customerNames.Count <= 1
            ? customerNames.FirstOrDefault() ?? "-"
            : $"عدة عملاء ({customerNames.Count}): {string.Join("، ", customerNames)}";

        double acceptedKg = 0;
        var qcItems = Db.QualityCheckItems.AsNoTracking()
            .Join(Db.QualityChecks.AsNoTracking(), q => q.CheckId, c => c.Id, (q, c) => new { q, c })
            .Where(x => x.c.OrderId == orderId && x.c.IsApproved).ToList();
        acceptedKg = qcItems.Sum(x => x.q.AcceptedQtyKg);
        double rejectedKg = qcItems.Sum(x => x.q.RejectedQtyKg);

        double orderedKg = order.Items.Sum(i => i.PlannedQtyKg);
        double producedKg = order.Items.Sum(i => i.ProducedQtyKg);
        int orderedCartons = order.Items.Sum(i => i.PlannedCartons);
        int producedCartons = order.Items.Sum(i => i.ProducedCartons);

        // وقت النهاية المتوقع: تاريخ الإنتاج + بداية الوردية + ساعات الأمر بمعدل الصنف
        string expectedEnd = "-";
        double expectedHours = 0;
        if (order.ProductionDate != null && order.ShiftId != null)
        {
            foreach (var it in order.Items)
            {
                double rate = OrderRateFor(it.ProductId, order.ShiftId.Value, it.PackagingTypeId);
                    if (it.PlannedCartons > 0 && rate > 0) expectedHours += it.PlannedCartons / rate; // §B85/H4: بلا معدل لا يُقدَّر زمن
            }
            if (TimeSpan.TryParse(shift?.StartTime, out var startTs))
                expectedEnd = order.ProductionDate.Value.Date.Add(startTs).AddHours(expectedHours).ToString("dd/MM/yyyy HH:mm");
        }

        var card = new OrderCardDto
        {
            OrderId = order.Id,
            OrderNumber = order.DocumentNumber,
            Status = order.IsClosed ? DocStatuses.Closed : order.Status,
            StatusAr = DocStatuses.ToArabic(order.IsClosed ? DocStatuses.Closed : order.Status),
            CustomerName = customerSummary,
            RawName = lot != null
                ? Db.Products.AsNoTracking().Where(p => p.Id == lot.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-"
                : "-",
            ProductName = string.Join(" + ", order.Items.Select(i => i.ProductId).Distinct()
                .Select(pid => Db.Products.AsNoTracking().Where(p => p.Id == pid).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-")),
            PackName = string.Join(" + ", order.Items.Select(i => i.PackagingTypeId).Distinct()
                .Select(pid => pid == null ? "-" : Db.PackagingTypes.AsNoTracking().Where(p => p.Id == pid).Select(p => p.PackageNameAr).FirstOrDefault() ?? "-")),
            PlanNumber = order.SourcePlanId != null
                ? Db.ProductionPlans.AsNoTracking().Where(p => p.Id == order.SourcePlanId).Select(p => p.DocumentNumber).FirstOrDefault() ?? "-"
                : "-",
            LotCode = lot?.LotCode ?? "-",
            ShipmentNumber = lot?.ShipmentId != null
                ? Db.Shipments.AsNoTracking().Where(s => s.Id == lot.ShipmentId).Select(s => s.DocumentNumber).FirstOrDefault() ?? "-"
                : "-",
            ProductionDate = order.ProductionDate?.ToString("dd/MM/yyyy") ?? "-",
            ShiftName = shift != null ? $"{shift.ShiftNameAr} ({shift.StartTime}–{shift.EndTime})" : "-",
            LineName = order.LineId != null
                ? Db.ProductionLines.AsNoTracking().Where(l => l.Id == order.LineId).Select(l => l.LineNameAr).FirstOrDefault() ?? "-"
                : "-",
            StartTime = Db.ProductionExecutions.AsNoTracking().Where(e => e.OrderId == orderId && e.StartDateTime != null)
                .OrderBy(e => e.StartDateTime).Select(e => e.StartDateTime).FirstOrDefault()?.ToString("dd/MM/yyyy HH:mm") ?? "-",
            ExpectedEndTime = expectedEnd,
            PlannedInPlanKg = planItems.Sum(i => i.PlannedQtyKg),
            PlannedInPlanCartons = planItems.Sum(i => i.PlannedCartons),
            OrderedKg = orderedKg,
            OrderedCartons = orderedCartons,
            ProducedKg = producedKg,
            ProducedCartons = producedCartons,
            AcceptedKg = acceptedKg,
            RejectedKg = rejectedKg,
            RemainingKg = Math.Max(0, orderedKg - producedKg),
            ProgressPct = orderedKg > 0 ? Math.Round(Math.Min(100, producedKg / orderedKg * 100), 1) : 0,
            RatePerHour = order.Items.Count > 0 && order.ShiftId != null
                ? OrderRateFor(order.Items[0].ProductId, order.ShiftId.Value, order.Items[0].PackagingTypeId) : 0,
            ExpectedHours = Math.Round(expectedHours, 2),
            CreatedBy = Db.Users.AsNoTracking().Where(u => u.Id == order.CreatedBy).Select(u => u.FullName).FirstOrDefault() ?? "-",
            CreatedDate = order.CreatedDate.ToString("dd/MM/yyyy HH:mm")
        };
        return card;
    }

    public List<OrderEventDto> GetOrderEvents(int orderId)
    {
        var order = Db.ProductionOrders.AsNoTracking().FirstOrDefault(o => o.Id == orderId);
        if (order == null) return new List<OrderEventDto>();
        var events = new List<(DateTime time, string user, string action, string detail)>();

        string UserName(int? id) => id == null ? "-" : Db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.FullName).FirstOrDefault() ?? "-";
        string ActionAr(string a) => a switch
        {
            "Create" => "إنشاء الأمر",
            "Approve" => "اعتماد الأمر",
            "Cancel" => "إلغاء الأمر",
            "Edit" => "تعديل الأمر",
            "Issue" => "إصدار الأمر",
            _ => a
        };

        // الربط برقم المستند: حدث الإنشاء يُدقق قبل توليد المعرف (RecordId=0) فالرقم هو المرجع الثابت
        foreach (var a in Db.AuditLogs.AsNoTracking()
                     .Where(x => x.DocumentType == "ProductionOrder" && x.DocumentNumber == order.DocumentNumber)
                     .OrderBy(x => x.ActionDate).ToList())
            events.Add((a.ActionDate, a.UserName ?? "-", ActionAr(a.ActionType), ""));

        foreach (var e in Db.ProductionExecutions.AsNoTracking().Where(x => x.OrderId == orderId).OrderBy(x => x.Id).ToList())
        {
            if (e.StartDateTime != null)
                events.Add((e.StartDateTime.Value, UserName(e.CreatedBy), "بدء جلسة الإنتاج", $"الجلسة {e.DocumentNumber}"));
            if (e.IsDayClosed && e.EndDateTime != null)
                events.Add((e.EndDateTime.Value, UserName(e.ModifiedBy ?? e.CreatedBy), "إقفال يوم الإنتاج",
                    $"المنتَج {e.ActualQtyKg:N1} كجم ({e.ActualCartons:N0} كرتون) | حشف {e.HashfKg:N1} | نوى {e.NawaKg:N1} | هالك {e.WastageQtyKg:N1}"));
            else if (e.Status == DocStatuses.Completed && e.EndDateTime != null)
                events.Add((e.EndDateTime.Value, UserName(e.ModifiedBy ?? e.CreatedBy), "تسجيل إنتاج فعلي", $"{e.ActualQtyKg:N1} كجم"));
            foreach (var dt in Db.ExecutionDowntimes.AsNoTracking().Where(d => d.ExecutionId == e.Id))
                if (e.StartDateTime != null)
                    events.Add((e.StartDateTime.Value, UserName(e.CreatedBy), "تسجيل توقف", $"{dt.Hours:N1} ساعة — {dt.ReasonAr}"));
        }

        foreach (var c in Db.QualityChecks.AsNoTracking().Where(x => x.OrderId == orderId).OrderBy(x => x.Id).ToList())
        {
            if (c.CheckDate != null)
                events.Add((c.CheckDate.Value, UserName(c.ApprovedBy ?? c.CreatedBy),
                    c.IsApproved ? $"اعتماد الفحص — مقبول {c.AcceptedKg:N1} كجم" : "إرسال للفحص (فترة تبريد)",
                    c.DocumentNumber));
        }

        foreach (var r in Db.FinishedGoodsReceipts.AsNoTracking().Where(x => x.OrderId == orderId).ToList())
            if (r.DeliveryDate != null)
                events.Add((r.DeliveryDate.Value, UserName(r.ApprovedBy ?? r.CreatedBy),
                    r.ReceiptStatus == "Full" ? "استلام كامل في مخزن التام" : $"استلام في مخزن التام ({r.ReceiptStatus})", r.DocumentNumber));

        return events.OrderBy(e => e.time)
            .Select(e => new OrderEventDto { Time = e.time.ToString("dd/MM/yyyy HH:mm"), User = e.user, Action = e.action, Detail = e.detail })
            .ToList();
    }

    // ═══════════════════════════ المواد والإغلاق ═══════════════════════════

    public OpResult IssueMaterials(int orderId, Dictionary<int, double> qtys = null)
    {
        Require("materials", "Post");
        return RunOp(() =>
        {
            var order = Db.ProductionOrders.Include(o => o.Materials).FirstOrDefault(o => o.Id == orderId);
            if (order == null) throw new DomainException("الأمر غير موجود.");
            if (!order.IsApproved || order.Status == DocStatuses.Cancelled)
                throw new DomainException("لا يمكن صرف مواد لأمر غير معتمد.");
            var whAux = WarehouseId("WAUX");
            int count = 0;
            foreach (var mat in order.Materials)
            {
                bool modern = mat.AuxiliaryProductId is > 0;
                int key = modern ? mat.AuxiliaryProductId!.Value : mat.MaterialId;
                if (key <= 0) throw new DomainException("بند مادة مساعدة بلا هوية مخزنية صالحة.", "AUX_IDENTITY");
                // The caller keys modern lines by ProductId, legacy lines by MaterialId.
                double req = qtys != null && qtys.TryGetValue(key, out var qv) ? qv
                    : (qtys == null ? Math.Max(0, mat.CalculatedQty - mat.ActualIssuedQty + mat.ReturnedQty) : 0);
                if (double.IsNaN(req) || double.IsInfinity(req) || req < 0)
                    throw new DomainException("كمية الصرف غير صالحة.", "AUX_INVALID_QTY");
                if (req <= 0.001) continue;
                var (kg, packages) = modern
                    ? AuxiliaryStockPolicy.Movement(req, mat.UnitOfMeasure) : (req, 0);
                PostStockMovement(whAux, MovementType.Outbound, kg, packages,
                    ReferenceDocType.MaterialIssue, $"{order.DocumentNumber}#ISS-{Guid.NewGuid():N}",
                    productId: modern ? mat.AuxiliaryProductId : null,
                    materialId: modern ? null : mat.MaterialId, orderId: order.Id,
                    notes: $"صرف مواد إضافي لأمر {order.DocumentNumber}");
                mat.ActualIssuedQty += req;
                mat.Status = DocStatuses.Issued;
                count++;
            }
            Db.SaveChanges();
            return count == 0 ? OpResult.Fail("لا توجد كميات جديدة للصرف.")
                : OpResult.Success($"تم صرف المواد لـ {count} بند.");
        });
    }

    public OpResult ConsumeMaterials(int orderId, int materialId, double consumed, double wasted, string reason = null)
    {
        Require("materials", "Edit");
        // Historical API: materialId denotes MaterialId, never ProductId.
        var mat = Db.ProductionOrderMaterials.FirstOrDefault(m => m.OrderId == orderId
            && m.AuxiliaryProductId == null && m.MaterialId == materialId);
        return mat == null ? OpResult.Fail("بند المادة القديم غير موجود في الأمر.")
            : ConsumeOrderMaterial(orderId, mat.Id, consumed, wasted, reason);
    }

    /// <summary>Record consumption using the row's unambiguous identity (legacy OR new BOM).</summary>
    public OpResult ConsumeOrderMaterial(int orderId, int orderMaterialId, double consumed, double wasted, string reason = null)
    {
        Require("materials", "Edit");
        if (double.IsNaN(consumed) || double.IsNaN(wasted) || double.IsInfinity(consumed)
            || double.IsInfinity(wasted) || consumed < 0 || wasted < 0)
            return OpResult.Fail("المستهلك والهالك يجب أن يكونا كميتين غير سالبتين.");
        return RunOp(() =>
        {
            var mat = Db.ProductionOrderMaterials.FirstOrDefault(m => m.Id == orderMaterialId && m.OrderId == orderId)
                ?? throw new DomainException("بند المادة غير موجود في الأمر.");
            var order = Db.ProductionOrders.AsNoTracking().FirstOrDefault(o => o.Id == orderId);
            if (order == null || !order.IsApproved || order.Status == DocStatuses.Cancelled)
                throw new DomainException("لا يسجل استهلاك لأمر غير معتمد.");
            double net = mat.ActualIssuedQty - mat.ReturnedQty;
            if (consumed + wasted > net + 0.001)
                throw new DomainException($"إجمالي المستهلك والهالك ({consumed + wasted:N1}) يتجاوز صافي المصروف ({net:N1}).");
            if (mat.AuxiliaryProductId is > 0)
            {
                AuxiliaryStockPolicy.Movement(consumed, mat.UnitOfMeasure);
                AuxiliaryStockPolicy.Movement(wasted, mat.UnitOfMeasure);
            }
            mat.ConsumedQty = consumed;
            mat.WastedQty = wasted;
            mat.Status = DocStatuses.Completed;
            Db.SaveChanges();
            return OpResult.Success("تم تسجيل الاستهلاك والهالك.");
        });
    }

    public OpResult ReturnUnusedMaterials(int orderId)
    {
        Require("materials", "Post");
        return RunOp(() =>
        {
            var order = Db.ProductionOrders.Include(o => o.Materials).FirstOrDefault(o => o.Id == orderId);
            if (order == null) throw new DomainException("الأمر غير موجود.");
            var whAux = WarehouseId("WAUX");
            int n = 0;
            foreach (var mat in order.Materials)
            {
                double unused = mat.ActualIssuedQty - mat.ConsumedQty - mat.WastedQty - mat.ReturnedQty;
                if (unused <= 0.001) continue;
                bool modern = mat.AuxiliaryProductId is > 0;
                if (!modern && mat.MaterialId <= 0)
                    throw new DomainException("بند مادة مساعدة بلا هوية مخزنية صالحة.", "AUX_IDENTITY");
                var (kg, packages) = modern
                    ? AuxiliaryStockPolicy.Movement(unused, mat.UnitOfMeasure) : (unused, 0);
                PostStockMovement(whAux, MovementType.Inbound, kg, packages,
                    ReferenceDocType.Return, $"{order.DocumentNumber}#RET-{Guid.NewGuid():N}",
                    productId: modern ? mat.AuxiliaryProductId : null,
                    materialId: modern ? null : mat.MaterialId, orderId: order.Id,
                    notes: $"إرجاع فائض مواد من أمر {order.DocumentNumber}");
                mat.ReturnedQty += unused;
                n++;
            }
            Db.SaveChanges();
            return OpResult.Success(n == 0 ? "لا توجد مواد فائضة للإرجاع." : "تم إرجاع المواد الفائضة للمخزن.");
        });
    }

    /// <summary>§23 — حذف أمر مسودة لم يُعتمد.</summary>
    public OpResult DeleteOrder(int orderId)
    {
        Require("production", "Delete");
        var order = Db.ProductionOrders.FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("أمر الإنتاج غير موجود.");
        if (order.IsApproved) return OpResult.Fail("لا يمكن حذف أمر معتمد — ألغِ الاعتماد أو ألغِ الأمر.");
        if (OrderHasIrreversibleExecution(orderId) || Db.ProductionExecutions.Any(e => e.OrderId == orderId))
            return OpResult.Fail("لا يمكن حذف أمر له جلسات تنفيذ فعلية — مرحلة غير قابلة للرجوع.");
        return RunOp(() =>
        {
            Db.ProductionOrders.Remove(order);
            Db.SaveChanges();
            return OpResult.Success("تم حذف أمر الإنتاج (المسودة).");
        });
    }

    /// <summary>§8 — لا يُغلق أمر لم يكتمل إنتاجه أو لم يُسلَّم إنتاجه.</summary>
    public OpResult CloseOrder(int orderId, string reason = null)
    {
        Require("production", "Cancel");
        return RunOp(() =>
        {
            var order = Db.ProductionOrders.Include(o => o.Items).FirstOrDefault(o => o.Id == orderId)
                ?? throw new DomainException("أمر الإنتاج غير موجود.");
            if (order.Status == DocStatuses.Cancelled)
                throw new DomainException("لا يمكن إقفال أمر ملغى.", "ORDER_CANCELLED");
            var shortfall = order.Items.Where(i => i.ProducedQtyKg + 0.001 < i.PlannedQtyKg).ToList();
            if (shortfall.Count > 0 && string.IsNullOrWhiteSpace(reason) && string.IsNullOrWhiteSpace(order.CloseReason))
                throw new DomainException("الكمية الفعلية ناقصة عن المخطط؛ أدخل سبب التسوية الموثق للعجز قبل الإقفال.", "CLOSE_REASON_REQUIRED");
            // Free text can settle a SHORTAGE, never fabricate production or
            // bypass closing the actual production day and issuing its delivery.
            if (!Db.ProductionExecutions.AsNoTracking().Any(e => e.OrderId == orderId && e.IsDayClosed))
                throw new DomainException("لم يُقفل يوم الإنتاج الفعلي بعد — لا يكفي إدخال سبب للتسوية.", "ACTUAL_NOT_CLOSED");
            if (!order.IsClosed)
            {
                order.IsClosed = true;
                order.ClosedDate = Db.BusinessNow;
                order.Status = DocStatuses.Closed;
                foreach (var item in order.Items) item.IsClosed = true;
            }
            if (shortfall.Count > 0 && !string.IsNullOrWhiteSpace(order.CloseReason)
                && !string.IsNullOrWhiteSpace(reason)
                && !string.Equals(order.CloseReason, reason.Trim(), StringComparison.Ordinal))
                throw new DomainException("التسوية المعتمدة لا تُعدَّل بالنص؛ اطلب إجراء مراجعة موثقًا منفصلًا.", "SETTLEMENT_IMMUTABLE");
            bool newSettlement = shortfall.Count > 0 && !string.IsNullOrWhiteSpace(reason)
                && string.IsNullOrWhiteSpace(order.CloseReason);
            if (newSettlement)
            {
                // A production operator cannot settle a planned shortage by
                // typing arbitrary text; formal settlement needs planning approval.
                Require("planning", "Approve");
                order.CloseReason = reason.Trim();
                if (!(order.Notes ?? "").Contains("[تسوية عجز]"))
                    order.Notes = (order.Notes ?? "") + " [تسوية عجز] " + order.CloseReason;
            }
            Db.SaveChanges();
            if (newSettlement)
                new AuditService(Db, Session).Log("تسوية عجز الإنتاج", "اعتماد تسوية عجز", "ProductionOrder",
                    order.DocumentNumber, order.Id,
                    new { الحالة = "عجز غير مسوّى" },
                    new { السبب = order.CloseReason, المنتَج = order.Items.Sum(i => i.ProducedQtyKg),
                        العجز = shortfall.Sum(i => i.PlannedQtyKg - i.ProducedQtyKg), المخطّط = order.Items.Sum(i => i.PlannedQtyKg),
                        المعتمد_من = Session?.UserId });
            // The last settlement may arrive AFTER the last delivery; enforce
            // the same all-items/issued-delivery check in that order too.
            string planMessage = order.SourcePlanId == null ? "" :
                PlanDeliveryIntegrity.TryAutoClose(Db, Session, Numbering, new AuditService(Db, Session),
                    order.SourcePlanId.Value);
            Db.SaveChanges();
            return OpResult.Success(shortfall.Count > 0
                ? $"تم إقفال أمر الإنتاج {order.DocumentNumber} مع تسوية موثقة للعجز: {order.CloseReason}." + planMessage
                : $"أمر الإنتاج {order.DocumentNumber} مقفل بعد إقفال يوم الإنتاج الفعلي." + planMessage);
        });
    }

    /// <summary>§مواصفات العملاء: كرتون ماركة مستقلة لكل عميل — يمنع الخلط مع بقاء الحراس تحذيرية.</summary>
    public int ResolveAuxMaterial(DatesErp.Core.Domain.Entities.ConsumptionFormula f, int? customerId)
    {
        if (customerId != null)
        {
            var matGroup = Db.AuxiliaryMaterials.AsNoTracking().Where(m => m.Id == f.MaterialId).Select(m => m.GroupCode).FirstOrDefault();
            var spec = Db.AuxCustomerSpecs.AsNoTracking().Where(x => x.IsActive && x.CustomerId == customerId
                    && (x.ProductId == null || x.ProductId == f.ProductId)
                    && (x.PackagingTypeId == null || x.PackagingTypeId == f.PackagingTypeId))
                .OrderByDescending(x => x.Priority).FirstOrDefault();
            if (spec != null && (matGroup == "AG-CART" || spec.MaterialId == f.MaterialId))
                return spec.MaterialId;
        }
        return f.MaterialId;
    }

    /// <summary>§1.50.64 — حل صنف مساعد العميل: كراتين فارغ السلطان 8كجم → كراتين السلطان 8كجم.</summary>
    public int ResolveAuxProductForCustomer(int genericAuxProductId, int? customerId, int? productId, int? packagingTypeId)
    {
        if (customerId == null) return genericAuxProductId;
        // ابحث عن مواصفة عميل تطابق: العميل + (المنتج التام أو عام) + (العبوة أو عام) + الصنف المساعد العام أو أي كرتون
        var specs = Db.AuxCustomerSpecs.AsNoTracking()
            .Where(x => x.IsActive && x.CustomerId == customerId)
            .OrderByDescending(x => x.Priority)
            .ThenByDescending(x => x.ProductId != null)
            .ThenByDescending(x => x.PackagingTypeId != null)
            .ToList();

        foreach (var spec in specs)
        {
            // تطابق المنتج التام (إن حدد) والعبوة (إن حددت)
            if (spec.ProductId != null && productId != null && spec.ProductId != productId) continue;
            if (spec.PackagingTypeId != null && packagingTypeId != null && spec.PackagingTypeId != packagingTypeId) continue;

            // إذا حدد GenericAuxiliaryProductId، يجب أن يطابق المطلوب
            if (spec.GenericAuxiliaryProductId != null && spec.GenericAuxiliaryProductId != genericAuxProductId) continue;

            // إذا وجد AuxiliaryProductId محدد، استخدمه كبديل ماركة العميل
            if (spec.AuxiliaryProductId != null && spec.AuxiliaryProductId != 0)
                return spec.AuxiliaryProductId.Value;

            // نظام قديم: MaterialId → حاول تحويله إلى ProductId عبر الاسم أو اعتبره نفس المجموعة
            // إذا كان MaterialId يشير إلى مادة مساعدة اسمها يحتوي "كرتون"، نبحث عن منتج مساعد بنفس الاسم أو BrandName
            if (spec.MaterialId != 0)
            {
                // إذا كان لدينا منتج مساعد يحمل نفس اسم المادة أو BrandName، استخدمه
                var mat = Db.AuxiliaryMaterials.AsNoTracking().FirstOrDefault(m => m.Id == spec.MaterialId);
                if (mat != null)
                {
                    var brandedProduct = Db.Products.AsNoTracking()
                        .FirstOrDefault(p => (p.ItemType == "Auxiliary" || p.ItemType == "Pack" || p.GroupCode == "004")
                                            && (p.ProductNameAr.Contains(spec.BrandName ?? "") || p.ProductNameAr == mat.MaterialNameAr));
                    if (brandedProduct != null) return brandedProduct.Id;
                }
            }
        }

        // لا يوجد تخصيص — استخدم العام
        return genericAuxProductId;
    }

    private bool StrictAuxEnabled()
        => Db.SystemSettings.AsNoTracking().Any(x => x.SettingKey == "StrictAux" && x.SettingValue == "1");
}
