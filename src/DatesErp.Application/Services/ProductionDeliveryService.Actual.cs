using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

public partial class ProductionDeliveryService
{
    protected override bool RetryTransactionDeadlocks => true;
    protected override System.Data.IsolationLevel TransactionIsolation => System.Data.IsolationLevel.Serializable;

    /// <summary>
    /// §تقرأ المخرجات الثانوية من شاشة الأصناف (ItemType=="ByProduct") مباشرةً —
    /// وليس من الجدول الثابت ByProducts. العبوة والوحدة مأخوذتان من بطاقة الصنف.
    /// </summary>
    public List<ActualByProductDefinitionDto> GetActualByProducts()
    {
        if (Session == null || !Session.Can("production", "View")) throw new PermissionDeniedException("عرض الإنتاج");
        return Db.Products.AsNoTracking()
            .Where(p => p.IsActive && p.ItemType == "ByProduct")
            .OrderBy(p => p.ProductCode)
            .Select(p => new ActualByProductDefinitionDto { Id = p.Id, Name = p.ProductNameAr, Unit = p.UnitOfMeasure })
            .ToList();
    }

    private List<TodayProductionRowDto> GetFallbackPlanRows(IReadOnlyCollection<int> orderIds)
    {
        var ids = orderIds.Distinct().ToList();
        if (ids.Count == 0) return new();

        var orders = Db.ProductionOrders.AsNoTracking().Where(o => ids.Contains(o.Id)).ToDictionary(o => o.Id);
        var items = Db.ProductionOrderItems.AsNoTracking().Where(i => ids.Contains(i.OrderId)).OrderBy(i => i.Id).ToList();
        var planItemIds = items.Where(i => i.PlanItemId != null).Select(i => i.PlanItemId.Value).Distinct().ToList();
        var planItems = Db.ProductionPlanItems.AsNoTracking().Where(i => planItemIds.Contains(i.Id)).ToDictionary(i => i.Id);
        var planIds = planItems.Values.Select(i => i.PlanId).Distinct().ToList();
        var plans = Db.ProductionPlans.AsNoTracking().Where(p => planIds.Contains(p.Id)
            && p.IsApproved && p.Status == DocStatuses.Approved && !p.IsClosed).ToDictionary(p => p.Id);
        var productIds = planItems.Values.Select(i => i.ProductId).Distinct().ToList();
        var productNames = Db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id))
            .ToDictionary(p => p.Id, p => p.ProductNameAr);
        var customerIds = planItems.Values.Where(i => i.CustomerId != null).Select(i => i.CustomerId.Value).Distinct().ToList();
        var customerNames = Db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id))
            .ToDictionary(c => c.Id, c => c.CustomerName);
        var shiftIds = planItems.Values.Select(i => i.SuggestedShiftId).Concat(orders.Values.Select(o => o.ShiftId))
            .Where(i => i != null).Select(i => i.Value).Distinct().ToList();
        var shifts = Db.Shifts.AsNoTracking().Where(s => shiftIds.Contains(s.Id))
            .ToDictionary(s => s.Id, s => s.ShiftNameAr);
        var lineIds = planItems.Values.Select(i => i.SuggestedLineId).Concat(orders.Values.Select(o => o.LineId))
            .Where(i => i != null).Select(i => i.Value).Distinct().ToList();
        var lines = Db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id))
            .ToDictionary(l => l.Id, l => l.LineNameAr);
        var closed = Db.ProductionExecutions.AsNoTracking().Where(e => ids.Contains(e.OrderId) && e.IsDayClosed)
            .Select(e => e.OrderId).ToHashSet();

        return items.Where(i => orders.ContainsKey(i.OrderId) && i.PlanItemId is int pid && planItems.ContainsKey(pid)
                && orders[i.OrderId].SourcePlanId == planItems[pid].PlanId
                && plans.ContainsKey(planItems[pid].PlanId))
            .Select(i =>
            {
                var order = orders[i.OrderId];
                var plan = planItems[i.PlanItemId!.Value];
                var scheduled = plan.ScheduledDate?.Date;
                var shift = plan.SuggestedShiftId ?? order.ShiftId;
                var line = plan.SuggestedLineId ?? order.LineId;
                var customer = plan.CustomerId is int cid && customerNames.TryGetValue(cid, out var cn)
                    ? cn : "غير محدد في الخطة";
                return new TodayProductionRowDto
                {
                    PlanId = plan.PlanId, PlanItemId = plan.Id, PlanNumber = plans[plan.PlanId].DocumentNumber,
                    CustomerId = plan.CustomerId, CustomerName = customer,
                    ProductId = plan.ProductId, ProductName = productNames.GetValueOrDefault(plan.ProductId, "صنف غير موجود"),
                    PlannedCartons = plan.PlannedCartons, PlannedKg = plan.PlannedQtyKg,
                    ScheduledDate = scheduled?.ToString("dd/MM/yyyy"), IsToday = scheduled == Db.BusinessNow.Date,
                    ShiftId = shift, ShiftName = shift is int sid ? shifts.GetValueOrDefault(sid, "وردية غير موجودة") : "غير محددة في الخطة",
                    LineId = line, LineName = line is int lid ? lines.GetValueOrDefault(lid, "خط غير موجود") : "غير محدد في الخطة",
                    OrderId = order.Id, OrderNumber = order.DocumentNumber,
                    IsPending = false, DayClosed = closed.Contains(order.Id),
                    Status = closed.Contains(order.Id) ? "مقفل" : DocStatuses.ToArabic(order.Status)
                };
            }).ToList();
    }

    public List<ActualDeliveryOrderDto> GetActualDeliveryOrders(int? selectedOrderId = null)
    {
        var closedExeOrderIds = Db.ProductionExecutions.AsNoTracking()
            .Where(e => e.IsDayClosed)
            .Select(e => e.OrderId)
            .ToHashSet();

        // الأوامر التي لها تنفيذ مقفل ولم يُنشأ لها أمر تسليم بعد — تبقى ظاهرة حتى يُنشأ الأمر
        var closedExeList = Db.ProductionExecutions.AsNoTracking()
            .Where(e => e.IsDayClosed)
            .Select(e => new { e.Id, e.OrderId })
            .ToList();
        var deliveredExeIds = Db.ProductionDeliveries.AsNoTracking()
            .Where(d => d.SourceType == DeliverySources.FromActual && d.Status != DocStatuses.Cancelled)
            .Select(d => d.SourceId)
            .ToHashSet();
        var closedExeWithNoDelivery = closedExeList
            .Where(e => !deliveredExeIds.Contains(e.Id))
            .Select(e => e.OrderId)
            .ToHashSet();

        var orders = Db.ProductionOrders.AsNoTracking()
            .Where(o => selectedOrderId.HasValue ? o.Id == selectedOrderId.Value
                : (o.Status != DocStatuses.Cancelled &&
                   (!o.IsClosed && !closedExeOrderIds.Contains(o.Id) || closedExeWithNoDelivery.Contains(o.Id))))
            .OrderByDescending(o => o.Id)
            .ToList();

        if (orders.Count == 0) return new();

        var orderIds = orders.Select(o => o.Id).ToList();
        var items = Db.ProductionOrderItems.AsNoTracking().Where(i => orderIds.Contains(i.OrderId)).ToList();
        var productIds = items.Select(i => i.ProductId).Distinct().ToList();
        var products = Db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionary(p => p.Id, p => p.ProductNameAr);

        var planItemIds = items.Where(i => i.PlanItemId != null).Select(i => i.PlanItemId!.Value).Distinct().ToList();
        var planItems = Db.ProductionPlanItems.AsNoTracking().Where(pi => planItemIds.Contains(pi.Id)).ToDictionary(pi => pi.Id);

        var planIds = orders.Where(o => o.SourcePlanId != null).Select(o => o.SourcePlanId!.Value)
            .Concat(planItems.Values.Select(pi => pi.PlanId))
            .Distinct().ToList();
        var plans = Db.ProductionPlans.AsNoTracking().Where(p => planIds.Contains(p.Id)).ToDictionary(p => p.Id, p => p.DocumentNumber);

        var shiftIds = orders.Where(o => o.ShiftId != null).Select(o => o.ShiftId!.Value).Distinct().ToList();
        var shifts = Db.Shifts.AsNoTracking().Where(s => shiftIds.Contains(s.Id)).ToDictionary(s => s.Id, s => s.ShiftNameAr);

        var customerIds = orders.Where(o => o.CustomerId != null).Select(o => o.CustomerId!.Value)
            .Concat(items.Where(i => i.CustomerId != null).Select(i => i.CustomerId!.Value))
            .Concat(planItems.Values.Where(pi => pi.CustomerId != null).Select(pi => pi.CustomerId!.Value))
            .Distinct().ToList();
        var customers = Db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionary(c => c.Id, c => c.CustomerName);

        var executions = Db.ProductionExecutions.AsNoTracking()
            .Include(e => e.Downtimes).Include(e => e.ByProducts)
            .Where(e => orderIds.Contains(e.OrderId) && e.IsDayClosed)
            .ToDictionary(e => e.OrderId);

        var exeIds = executions.Values.Select(e => e.Id).ToList();
        var qualityChecks = Db.QualityChecks.AsNoTracking().Where(q => q.ExecutionId != null && exeIds.Contains(q.ExecutionId.Value)).ToDictionary(q => q.ExecutionId!.Value);
        var deliveries = Db.ProductionDeliveries.AsNoTracking()
            .Where(d => d.SourceType == DeliverySources.FromActual && exeIds.Contains(d.SourceId) && d.Status != DocStatuses.Cancelled)
            .ToDictionary(d => d.SourceId);

        // §المخرجات الثانوية من شاشة الأصناف (ItemType=ByProduct) — ByProductId يحمل ProductId.
        var byProductDefs = Db.Products.AsNoTracking()
            .Where(p => p.ItemType == "ByProduct")
            .ToDictionary(p => p.Id, p => new ActualByProductDefinitionDto { Id = p.Id, Name = p.ProductNameAr, Unit = p.UnitOfMeasure });

        var result = new List<ActualDeliveryOrderDto>();
        foreach (var order in orders)
        {
            var orderItems = items.Where(i => i.OrderId == order.Id).OrderBy(i => i.Id).ToList();
            executions.TryGetValue(order.Id, out var exe);
            var qc = exe == null ? null : qualityChecks.GetValueOrDefault(exe.Id);
            var productionDelivery = exe == null ? null : deliveries.GetValueOrDefault(exe.Id);

            var planNum = order.SourcePlanId != null && plans.TryGetValue(order.SourcePlanId.Value, out var pn) ? pn : "—";
            var shiftName = order.ShiftId != null && shifts.TryGetValue(order.ShiftId.Value, out var sn) ? sn : "وردية غير محددة";

            var itemCustomerNames = orderItems.Select(i =>
            {
                int? cid = i.CustomerId ?? (i.PlanItemId != null && planItems.TryGetValue(i.PlanItemId.Value, out var pi) ? pi.CustomerId : null) ?? order.CustomerId;
                return cid != null && customers.TryGetValue(cid.Value, out var cn) ? cn : null;
            }).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).Distinct().ToList();

            if (itemCustomerNames.Count == 0 && order.CustomerId != null && customers.TryGetValue(order.CustomerId.Value, out var orderCust))
            {
                itemCustomerNames.Add(orderCust);
            }

            var customerSummary = itemCustomerNames.Count == 0 ? "عام / غير محدد"
                : itemCustomerNames.Count == 1 ? itemCustomerNames[0]
                : $"عدة عملاء ({itemCustomerNames.Count}): {string.Join("، ", itemCustomerNames)}";

            bool can = exe == null && !order.IsClosed && order.Status != DocStatuses.Cancelled;

            result.Add(new ActualDeliveryOrderDto
            {
                OrderId = order.Id,
                Label = $"{order.DocumentNumber} — {customerSummary} — {shiftName}",
                Customer = customerSummary,
                Shift = shiftName,
                PlanNumber = planNum,
                ExecutionId = exe?.Id ?? 0,
                ProductionDeliveryId = productionDelivery?.Id ?? 0,
                Recorded = exe != null,
                CanRecord = can,
                CanCreateDelivery = exe != null && productionDelivery == null,
                Status = productionDelivery != null ? $"تم إنشاء أمر تسليم الإنتاج {productionDelivery.DocumentNumber} — {DocStatuses.ToArabic(productionDelivery.Status)}"
                    : exe != null ? "إنتاج فعلي محفوظ — لا يوجد استلام مخزني تلقائي؛ أنشئ أمر التسليم من هنا"
                    : can ? "أدخل الفعلي فقط؛ المخطط ثابت من أمر الإنتاج" : "الأمر غير قابل للتسجيل (مغلق أو ملغى).",
                ReceiptNumber = null,
                QualityNumber = qc?.DocumentNumber,
                ProductionDeliveryNumber = productionDelivery?.DocumentNumber,
                ProductionDeliveryStatus = productionDelivery == null ? null : DocStatuses.ToArabic(productionDelivery.Status),
                ConsumedRawKg = exe?.ConsumedRawKg ?? 0,
                DowntimeHours = exe?.Downtimes.Sum(d => d.Hours) ?? 0,
                DowntimeReason = exe == null ? null : string.Join("؛ ", exe.Downtimes.Select(d => d.ReasonAr)),
                Notes = exe?.ClosingNotes,
                RecordedByProductDefinitions = exe == null ? new() : exe.ByProducts
                    .Where(b => byProductDefs.ContainsKey(b.ByProductId))
                    .Select(b => byProductDefs[b.ByProductId]).ToList(),
                ByProducts = exe?.ByProducts.Select(b => new ByProductQtyDto { ByProductId = b.ByProductId, QtyKg = (double)b.Qty }).ToList() ?? new(),
                Items = orderItems.Select(i =>
                {
                    int? itemCustId = i.CustomerId ?? (i.PlanItemId != null && planItems.TryGetValue(i.PlanItemId.Value, out var pi) ? pi.CustomerId : null) ?? order.CustomerId;
                    string itemCustName = itemCustId != null && customers.TryGetValue(itemCustId.Value, out var cn) ? cn : customerSummary;

                    return new ActualDeliveryItemDto
                    {
                        OrderItemId = i.Id,
                        Product = i.ProductId != 0 && products.TryGetValue(i.ProductId, out var prodName) ? prodName : "صنف غير محدد",
                        Customer = itemCustName,
                        PlannedCartons = i.PlannedCartons,
                        ActualCartons = i.ProducedCartons
                    };
                }).ToList()
            });
        }
        return result;
    }

    public OpResult SaveActualProduction(ActualProductionDto input)
    {
        // One operation, not an implicit grant of warehouse or quality permissions.
        Require("production", "Create"); Require("execution", "Edit");
        ExecutionCloseTrace.Write($"SaveActualProduction ENTER OrderId={input?.OrderId.ToString() ?? "<null>"}");
        // تسجيل الفعلي ينتهي عند جلسة التنفيذ. لا يمنح ضمنياً صلاحيات المخازن
        // ولا ينشئ أمر/سند استلام تام؛ تلك مرحلة مستقلة بعد تحرير أمر التسليم.
        var result = RunOp(() =>
        {
            if (input == null) throw new DomainException("بيانات التنفيذ غير موجودة.");
            if (!double.IsFinite(input.ConsumedRawKg) || input.ConsumedRawKg <= 0)
                throw new DomainException("أدخل الخام المستهلك فعليًا بالكجم؛ لا يُستنتج من المخطط أو الإنتاج.");
            if (!double.IsFinite(input.DowntimeHours) || input.DowntimeHours < 0 || input.DowntimeHours > 24)
                throw new DomainException("ساعات التوقف يجب أن تكون بين صفر و24 ساعة.");
            if (input.DowntimeHours > 0 && string.IsNullOrWhiteSpace(input.DowntimeReason))
                throw new DomainException("أدخل سبب التوقف في خانة السبب الواحدة.");
            if (input.DowntimeHours == 0 && !string.IsNullOrWhiteSpace(input.DowntimeReason))
                throw new DomainException("أدخل ساعات التوقف أو أفرغ سببه عندما لا يوجد توقف.");
            // Fresh authoritative read under SERIALIZABLE: stale UI/context and competing clients cannot repost.
            Db.ChangeTracker.Clear();
            var canonical = new ProductionOrderService(Db, Session, Numbering).GetScheduledProduction().Rows
                .Where(r => r.OrderId == input.OrderId && !r.DayClosed).ToList();
            // GetScheduledProduction يرفض عمداً صفاً كاملاً إذا كان رأس أمر قديم
            // يحمل عميلاً واحداً بينما بنوده تحمل أكثر من عميل. هذا لا يعني أن
            // روابط البنود غير صالحة؛ نعيد التحقق من كل OrderItem ← PlanItem مباشرة.
            if (canonical.Count == 0)
            {
                canonical = GetFallbackPlanRows(new[] { input.OrderId })
                    .Where(r => r.OrderId == input.OrderId && !r.DayClosed).ToList();
                ExecutionCloseTrace.Write($"SaveActualProduction FALLBACK_ROWS OrderId={input.OrderId} Count={canonical.Count}");
            }
            if (canonical.Count == 0)
                throw new DomainException("الأمر ليس نسخة مطابقة لخطة معتمدة مجدولة وغير مقفلة؛ حدّث الشاشة.");
            if (canonical.Any(r => !UiFormat.TryParseDate(r.ScheduledDate, out var day) || day.Date > Db.BusinessNow.Date))
                throw new DomainException("لا يمكن تسجيل تنفيذ قبل يوم الخطة؛ يمكن تسجيل الأمر المتأخر حتى تاريخ اليوم.");
            var order = Db.ProductionOrders.Include(o => o.Items).Single(o => o.Id == input.OrderId);
            // §v1.50.24/§v1.50.75: التسجيل من بطاقة الأمر يبدأ التنفيذ تلقائياً
            // للأمر المعتمد المجدول أو المسودة المعتمدة، فلا يُجبر المستخدم على
            // تشغيل أمر متأخر من زر منفصل يمنعه تاريخ الجهاز.
            if (order.IsApproved && !order.IsClosed
                && order.Status is (DocStatuses.Draft or DocStatuses.Approved or DocStatuses.Scheduled))
                order.Status = DocStatuses.InProgress;
            if (!order.IsApproved || order.IsClosed || order.Status is not (DocStatuses.InProgress or DocStatuses.Stopped))
                throw new DomainException("يلزم أمر اليوم المعتمد غير المقفل (الملغى أو المقفل لا يُسجَّل).");
            if (Db.ProductionExecutions.Any(e => e.OrderId == order.Id && e.IsDayClosed)
                || order.Items.Any(i => i.ProducedCartons != 0 || i.ProducedQtyKg != 0 || i.IsClosed)
                || Db.QualityChecks.Any(q => q.OrderId == order.Id)
                || Db.FinishedGoodsReceipts.Any(r => r.OrderId == order.Id && r.Status != DocStatuses.Cancelled))
                throw new DomainException("يوجد تنفيذ أو فحص أو استلام سابق للأمر؛ لا يسمح بتكرار التسجيل أو مزج الدورة السابقة.");
            if (input.Items == null || input.Items.Any(i => i == null)
                || input.Items.Select(i => i.OrderItemId).Distinct().Count() != input.Items.Count
                || !input.Items.Select(i => i.OrderItemId).ToHashSet().SetEquals(order.Items.Select(i => i.Id)))
                throw new DomainException("يلزم تسجيل بنود الأمر نفسها مرة واحدة، دون إضافة صنف أو إسقاط بند.");
            if (order.Items.Any(i => i.LotId == null || i.PlannedCartons <= 0 || !double.IsFinite(i.PlannedQtyKg) || i.PlannedQtyKg <= 0))
                throw new DomainException("بيانات الخطة لا تتضمن دفعات الخام وكميات كرتونية سليمة؛ أصلح المرجع رسميًا دون اختيار صنف هنا.");
            var actual = new List<CloseItemQtyDto>();
            foreach (var row in input.Items)
            {
                var source = order.Items.Single(i => i.Id == row.OrderItemId);
                if (row.ActualCartons < 0 || row.ActualCartons > source.PlannedCartons)
                    throw new DomainException("الفعلي لا يكون سالبًا ولا يتجاوز المخطط؛ لا تعديل تلقائي للخطة.");
                actual.Add(new CloseItemQtyDto { OrderItemId = source.Id, ProducedCartons = row.ActualCartons,
                    ProducedKg = row.ActualCartons * (source.PlannedQtyKg / source.PlannedCartons) });
            }
            if (actual.Sum(i => (long)i.ProducedCartons) is <= 0 or > int.MaxValue)
                throw new DomainException("أدخل كمية إنتاج فعلية قابلة للتسليم.");
            var secondary = input.ByProducts ?? new();
            if (secondary.Any(b => b == null || !double.IsFinite(b.QtyKg) || b.QtyKg <= 0 || b.QtyKg >= 1e15 || Math.Abs(b.QtyKg - Math.Round(b.QtyKg, 4)) > 1e-9 || Math.Round(b.QtyKg, 4) <= 0)
                || secondary.Select(b => b.ByProductId).Distinct().Count() != secondary.Count)
                throw new DomainException("اختر كل مخرج ثانوي مرة واحدة بكمية موجبة صالحة للتخزين.");
            // §المخرجات الثانوية من شاشة الأصناف (ItemType=ByProduct) — ByProductId=ProductId.
            var definitions = GetActualByProducts().Select(b => b.Id).ToHashSet();
            if (secondary.Any(b => !definitions.Contains(b.ByProductId)))
                throw new DomainException("المخرج الثانوي غير معرَّف في شاشة الأصناف (نوع: مخرج ثانوي) أو موقوف — أضفه من بطاقة الأصناف.");
            var beforeExecutions = Db.ProductionExecutions.AsNoTracking()
                .Where(e => e.OrderId == order.Id)
                .OrderBy(e => e.Id)
                .Select(e => $"Id={e.Id},OrderId={e.OrderId},IsDayClosed={e.IsDayClosed},Status={e.Status},EndDateTime={e.EndDateTime:O}")
                .ToList();
            ExecutionCloseTrace.Write($"SaveActualProduction BEFORE_CLOSE OrderId={order.Id} Executions=[{string.Join(" | ", beforeExecutions)}]");
            var execution = new ExecutionService(Db, Session, Numbering)
                { JoinParentTransaction = true, RecordingActualDelivery = true };
            void Must(OpResult r)
            {
                ExecutionCloseTrace.Write($"CloseProductionDay RESULT OrderId={order.Id} Ok={r.Ok} Message={r.Message}");
                if (!r.Ok) throw new DomainException(r.Message);
            }
            Must(execution.CloseProductionDay(order.Id, actual.Sum(i => i.ProducedKg), actual.Sum(i => i.ProducedCartons),
                0, 0, 0, false, input.DowntimeHours > 0 ? new() { new DowntimeDto { Hours = input.DowntimeHours, ReasonAr = input.DowntimeReason.Trim() } } : new(),
                true, input.Notes?.Trim(), secondary, input.ConsumedRawKg, actual));
            var exe = Db.ProductionExecutions.AsNoTracking()
                .FirstOrDefault(e => e.OrderId == order.Id && e.IsDayClosed);
            ExecutionCloseTrace.Write($"SaveActualProduction AFTER_CLOSE OrderId={order.Id} ExecutionId={exe?.Id.ToString() ?? "<null>"} IsDayClosed={exe?.IsDayClosed.ToString() ?? "<null>"} Status={exe?.Status ?? "<null>"} EndDateTime={exe?.EndDateTime?.ToString("O") ?? "<null>"}");
            if (exe == null || exe.Status != DocStatuses.Completed || exe.EndDateTime == null)
                throw new DomainException(
                    "تم تسجيل العملية دون تثبيت إقفال يوم الإنتاج في سجل التنفيذ — لم تُعتمد العملية.",
                    "EXECUTION_CLOSE_NOT_PERSISTED");
            order.Status = DocStatuses.Completed;
            order.IsClosed = true;
            order.ClosedDate = Db.BusinessNow;
            Db.SaveChanges();
            var qc = Db.QualityChecks.SingleOrDefault(q => q.ExecutionId == exe.Id);
            if (qc != null)
            {
                qc.TotalCheckedCartons = actual.Sum(i => i.ProducedCartons);
                qc.CheckDate = Db.BusinessNow;
                qc.ExpectedCheckDate = Db.BusinessNow.Date.AddDays(2);
                foreach (var row in actual.Where(i => i.ProducedCartons > 0))
                {
                    var source = order.Items.Single(s => s.Id == row.OrderItemId);
                    qc.Items.Add(new QualityCheckItem
                    {
                        OrderItemId = source.Id, CustomerId = source.CustomerId ?? order.CustomerId,
                        ProductId = source.ProductId, LotId = source.LotId,
                        PackagingTypeId = source.PackagingTypeId, CheckedCartons = 0, CheckedQtyKg = 0,
                        Notes = $"مرسل للفحص: {row.ProducedCartons} كرتون / {row.ProducedKg:N1} كجم — لا كمية مقبولة قبل إدخال نتيجة الجودة"
                    });
                }
                Db.SaveChanges();
            }
            // لا يُنشأ هنا سند استلام مخزني ولا تُرحّل أرصدة مخزن التام.
            // أمر تسليم الإنتاج يُنشأ لاحقاً صراحةً من التنفيذ الفعلي بواسطة مدير الإنتاج.
            _audit.Log("الإنتاج الفعلي", "تسجيل الإنتاج الفعلي فقط — دون إنشاء استلام مخزني", "ProductionExecution", exe.DocumentNumber, exe.Id,
                newValues: new { order.Id, PlannedCartons = order.Items.Sum(i => i.PlannedCartons), exe.ActualCartons,
                    Difference = order.Items.Sum(i => i.PlannedCartons) - exe.ActualCartons, exe.ConsumedRawKg, QualityId = qc?.Id });
            return OpResult.Success($"تم تسجيل الإنتاج الفعلي {exe.ActualCartons:N0} كرتون؛ الفرق {order.Items.Sum(i => i.PlannedCartons) - exe.ActualCartons:N0}. لا يوجد استلام مخزني تلقائي — أنشئ أمر تسليم الإنتاج من التنفيذ ثم حرره للمخزن.", exe.Id, exe.DocumentNumber);
        });
        ExecutionCloseTrace.Write($"SaveActualProduction EXIT OrderId={input?.OrderId.ToString() ?? "<null>"} Ok={result.Ok} Message={result.Message}");
        return result;
    }
}
