using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §B96 — أوامر تسليم الإنتاج (إدارة الإنتاج — يحررها مدير الإنتاج):
/// مسودة ← مُصدَرة ← مستلمة (عبر سندات الاستلام المخزنية).
/// المصدر التشغيلي الجديد: جلسة الإنتاج الفعلي المكتملة فقط؛ وتبقى مصادر الفحص/الخطة للقراءة التاريخية والتوافق مع المستندات القديمة.
/// البند = أمر + صنف + دفعة + عميل + كمية — عميل أو عدة عملاء، صنف أو أكثر.
/// سقفان: سقف المصدر (المقبول/المنتَج) + سقف فيزيائي موحد (كل المصادر ≤ المنتَج).
/// </summary>
public partial class ProductionDeliveryService : ServiceBase, IProductionDeliveryService
{
    // Isolation and deadlock policy is shared with the actual-production path
    // (declared in the other partial file). Cancellation and issue must also
    // reload/validate inside that serializable transaction.
    private readonly IAuditService _audit;

    public ProductionDeliveryService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering, IAuditService audit)
        : base(db, session, numbering)
    {
        _audit = audit;
    }

    public OpResult SaveDelivery(string sourceType, int sourceId, string deliveryDate, List<ProductionDeliveryItemDto> items,
        string bypassReason = null, string notes = null)
    {
        Require("production", "Create");
        // Historical sources remain readable, but cannot create an order that the
        // finished-goods warehouse is prohibited from receiving.
        if (sourceType != DeliverySources.FromActual)
            return OpResult.Fail("مصادر التسليم القديمة للقراءة التاريخية فقط — أنشئ أمر التسليم من جلسة الإنتاج الفعلي المكتملة.");
        if (items == null || items.Count == 0)
            return OpResult.Fail("أدخل بنداً واحداً على الأقل في أمر التسليم.");

        // §B96 — التجاوز بصلاحية وسبب مكتوب (لا تجاوز صامت إطلاقاً)
        bool bypass = DeliverySources.IsBypass(sourceType);
        if (bypass)
        {
            Require("production", "BypassInspection");
            if (string.IsNullOrWhiteSpace(bypassReason))
                return OpResult.Fail("التسليم من الخطة/الإقفال يتجاوز الفحص — أدخل سبب التجاوز مكتوباً ليُحفظ موثقاً في الأمر.");
        }

        // §التحقق من المصدر وحالته
        QualityCheck check = null;
        ProductionPlan plan = null;
        ProductionOrder checkOrder = null;
        ProductionExecution execution = null;
        if (sourceType == DeliverySources.FromActual)
        {
            execution = Db.ProductionExecutions.AsNoTracking().FirstOrDefault(e => e.Id == sourceId);
            if (execution == null) return OpResult.Fail("جلسة الإنتاج الفعلي غير موجودة — احفظ الفعلي أولاً.");
            if (!execution.IsDayClosed || execution.Status != DocStatuses.Completed)
                return OpResult.Fail("لا يمكن إنشاء أمر تسليم قبل إقفال جلسة الإنتاج الفعلي.");
            checkOrder = Db.ProductionOrders.Include(o => o.Items)
                .FirstOrDefault(o => o.Id == execution.OrderId && o.IsApproved && o.Status != DocStatuses.Cancelled);
            if (checkOrder == null)
                return OpResult.Fail("أمر الإنتاج المرتبط بالفعلي غير موجود أو غير معتمد.");
        }
        else if (sourceType == DeliverySources.FromCheck)
        {
            check = Db.QualityChecks.Include(c => c.Items).FirstOrDefault(c => c.Id == sourceId);
            if (check == null) return OpResult.Fail("محضر الفحص غير موجود — تحقق من الرقم.");
            // §B102 — الهوية قبل الحالة: الفحص اليدوي بلا أمر لا يُسلَّم منه إطلاقاً (حتى لو اعتُمِد)
            if (check.OrderId == null) return OpResult.Fail("الفحص اليدوي بلا أمر لا يُسلَّم منه — التسليم من إنتاج أمر تشغيل فقط.");
            if (!check.IsApproved) return OpResult.Fail($"محضر الفحص {check.DocumentNumber} غير معتمد — لا يُسلَّم إلا من محضر معتمد.");
            checkOrder = Db.ProductionOrders.Include(o => o.Items).FirstOrDefault(o => o.Id == check.OrderId.Value);
            if (checkOrder == null) return OpResult.Fail("أمر المحضر غير موجود.");
        }
        else
        {
            plan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == sourceId);
            if (plan == null) return OpResult.Fail("الخطة غير موجودة — تحقق من الرقم.");
            if (!plan.IsApproved) return OpResult.Fail("الخطة غير معتمدة — اعتمدها أولاً.");
            if (plan.Status == DocStatuses.Cancelled) return OpResult.Fail("الخطة ملغاة — لا يُسلَّم منها.");
            if (sourceType == DeliverySources.FromClosing && !plan.IsClosed)
                return OpResult.Fail("الخطة غير مقفلة — التسليم من الإقفال يتطلب خطة مقفلة (أو سلِّم من الخطة مباشرة).");
        }

        var srcLines = BuildSourceLines(sourceType, sourceId, check, checkOrder, plan, execution);

        return RunOp(() =>
        {
            var delivery = new ProductionDelivery
            {
                DocumentNumber = Numbering.Next("PDL"),
                DeliveryDate = UiFormat.TryParseDate(deliveryDate, out var d) ? d : DateTime.Now,
                SourceType = sourceType,
                SourceId = sourceId,
                BypassReason = bypass ? bypassReason.Trim() : null,
                Status = DocStatuses.Draft,
                ReceiptStatus = "None",
                Notes = notes
            };

            PopulateDeliveryItems(delivery, sourceType, srcLines, items);

            Db.ProductionDeliveries.Add(delivery);
            Db.SaveChanges();
            if (bypass)
                _audit.Log("الإنتاج", "تسليم بتجاوز الفحص", "ProductionDelivery", delivery.DocumentNumber, delivery.Id,
                    new { المصدر = DeliverySources.ToArabic(sourceType) }, new { السبب = delivery.BypassReason });
            double total = delivery.Items.Sum(i => i.QtyKg);
            return OpResult.Success(
                $"تم إنشاء أمر تسليم الإنتاج {delivery.DocumentNumber} من {DeliverySources.ToArabic(sourceType)} — {delivery.Items.Count} بنود ({total:N1} كجم)." +
                (bypass ? " (بتجاوز موثق للفحص)" : "") + " — بانتظار تحرير مدير الإنتاج.",
                delivery.Id, delivery.DocumentNumber);
        });
    }

    private void PopulateDeliveryItems(ProductionDelivery delivery, string sourceType,
        List<DeliverySourceLine> srcLines, List<ProductionDeliveryItemDto> items)
    {
            var takenPerLine = new Dictionary<DeliverySourceLine, double>();
            foreach (var it in items)
            {
                // §نظام الوحدات: التسليم للمنتجات التامة فقط (002)
                UnitsPolicy.RequireItemType(Db, it.ProductId, "Finished", "بند أمر تسليم الإنتاج");
                if (it.QtyKg <= 0)
                    throw new DomainException($"كمية البند للصنف «{ProdName(it.ProductId)}» يجب أن تكون أكبر من صفر.");

                // Packaging is part of the physical identity of the source line.
                // Two sizes of the same product/lot/customer must never share a cap.
                // §تعدد العملاء: حين يُترك عميل البند فارغاً كان الشرط l.CustomerId == (it.CustomerId ?? l.CustomerId)
                // يتحقق لكل السطور، فيُلتقط أول سطر بالصدفة ويُقيَّد التام لعميل غير صاحبه.
                // فإن تعددت السطور المطابقة صنفاً ودفعةً، العميل إلزامي صريح.
                var candidates = srcLines.Where(l => l.ProductId == it.ProductId && l.LotId == it.LotId
                    && l.PackagingTypeId == it.PackagingTypeId).ToList();
                DeliverySourceLine line;
                if (it.CustomerId != null)
                    line = candidates.FirstOrDefault(l => l.CustomerId == it.CustomerId);
                else if (candidates.Count > 1)
                    throw new DomainException(
                        $"⛔ الصنف «{ProdName(it.ProductId)}»" +
                        (it.LotId != null ? $" من الدفعة «{LotCode(it.LotId)}»" : "") +
                        $" مشترك بين {candidates.Count} عملاء في {DeliverySources.ToArabic(sourceType)} — حدد عميل البند صراحةً.\n" +
                        "العملاء: " + string.Join(" · ", candidates.Select(c => c.CustomerName ?? "بلا عميل")),
                        "AMBIGUOUS_CUSTOMER");
                else
                    line = candidates.FirstOrDefault();
                if (line == null)
                    throw new DomainException(
                        $"لا يوجد سطر مطابق في {DeliverySources.ToArabic(sourceType)} للصنف «{ProdName(it.ProductId)}»" +
                        (it.LotId != null ? $" والدفعة «{LotCode(it.LotId)}»" : "") + " — راجع الملء الآلي.",
                        "NO_SOURCE_LINE");

                // §سقف المصدر: لا تسليم فوق المتبقي (المقبول/المنتَج ناقص ما سُلِّم سابقاً)
                // RemainingQtyKg محسوب مرة واحدة قبل الحلقة من المحفوظ، فبندان في نفس المستند
                // على سطر واحد كانا يُقاسان كلاهما على المتبقي الكامل ويتجاوزانه معاً — يُجمَّع محلياً.
                takenPerLine.TryGetValue(line, out double takenBefore);
                if (takenBefore + it.QtyKg > line.RemainingQtyKg + 0.01)
                    throw new DomainException(
                        $"⛔ كمية البند ({it.QtyKg:N1} كجم) تتجاوز المتبقي القابل للتسليم ({line.RemainingQtyKg:N1} كجم) " +
                        $"للصنف «{line.ProductName}» في {DeliverySources.ToArabic(sourceType)}." +
                        (takenBefore > 0 ? $"\nمأخوذ في بنود أخرى من هذا المستند: {takenBefore:N1} كجم." : ""),
                        "OVER_SOURCE");
                if (sourceType == DeliverySources.FromActual)
                {
                    if (!double.IsFinite(it.QtyKg) || it.QtyKg <= 0 || line.PackageCount <= 0 || line.AvailableQtyKg <= 0)
                        throw new DomainException("كمية أو عدد كراتين مصدر التسليم الفعلي غير صالح.", "INVALID_DELIVERY_QTY");
                    int expectedCartons = (int)Math.Round(it.QtyKg / line.AvailableQtyKg * line.PackageCount);
                    double expectedKg = expectedCartons * line.AvailableQtyKg / line.PackageCount;
                    if (expectedCartons <= 0 || it.PackageCount != expectedCartons
                        || Math.Abs(it.QtyKg - expectedKg) > Math.Max(1.0, it.QtyKg * 0.02))
                        throw new DomainException("عدد كراتين بند التسليم لا يتوافق مع الكمية وعبوة بند الإنتاج الفعلي.", "DELIVERY_CARTON_MISMATCH");
                }
                takenPerLine[line] = takenBefore + it.QtyKg;

                // §السقف الفيزيائي الموحد: كل المصادر معاً لا تتجاوز إنتاج الأمر (منع ازدواج محضر+خطة)
                if (line.OrderId is int lineOrderId)
                {
                    double produced = Db.ProductionOrderItems.AsNoTracking()
                        .Where(o => o.OrderId == lineOrderId && o.ProductId == it.ProductId).Sum(o => o.ProducedQtyKg);
                    double deliveredAll = Db.ProductionDeliveryItems.AsNoTracking()
                        .Join(Db.ProductionDeliveries.AsNoTracking(), i => i.DeliveryId, d => d.Id, (i, d) => new { i, d })
                        .Where(x => x.d.Status != DocStatuses.Cancelled && x.d.Id != delivery.Id && x.i.OrderId == lineOrderId && x.i.ProductId == it.ProductId)
                        .Sum(x => x.i.QtyKg);
                    // §المباشر القديم يُرى أيضاً (المربوط داخل deliveredAll أصلاً فلا ازدواج)
                    double directReceived = Db.FinishedGoodsReceiptItems.AsNoTracking()
                        .Join(Db.FinishedGoodsReceipts.AsNoTracking(), i => i.ReceiptId, r => r.Id, (i, r) => new { i, r })
                        .Where(x => x.r.Status != DocStatuses.Cancelled && x.r.DeliveryId == null
                            && x.r.OrderId == lineOrderId && x.i.ProductId == it.ProductId)
                        .Sum(x => x.i.ReceivedQtyKg);
                    double thisDoc = delivery.Items.Where(i => i.OrderId == lineOrderId && i.ProductId == it.ProductId).Sum(i => i.QtyKg);
                    if (deliveredAll + directReceived + thisDoc + it.QtyKg > produced + 0.01)
                        throw new DomainException(
                            $"⛔ التسليم يتجاوز الإنتاج الفعلي للصنف «{line.ProductName}».\n" +
                            $"المنتَج: {produced:N1} كجم | المُسلَّم بأوامر أخرى: {deliveredAll:N1} | المستلَم مباشرةً: {directReceived:N1} | هذا الأمر بعد الإضافة: {thisDoc + it.QtyKg:N1}",
                            "OVER_PRODUCED");
                }

                // §تتبع الهوية: الدفعة المسلَّمة يجب أن تطابق دفعة بند الأمر (لا استبدال هوية)
                if (it.LotId is int lineLot && line.OrderId is int lineOrd)
                {
                    ProductIdentityGuard.EnsureConversionAllowed(Db, it.ProductId, lineLot);
                    bool lotOk = Db.ProductionOrderItems.AsNoTracking()
                        .Any(o => o.OrderId == lineOrd && o.ProductId == it.ProductId && o.LotId == lineLot);
                    if (!lotOk)
                        throw new DomainException($"⛔ الدفعة «{LotCode(lineLot)}» ليست دفعة هذا الصنف في الأمر المصدر.", "LOT_MISMATCH");
                }

                delivery.Items.Add(new ProductionDeliveryItem
                {
                    OrderId = line.OrderId,
                    ProductId = it.ProductId,
                    LotId = it.LotId,
                    CustomerId = line.CustomerId,
                    PackagingTypeId = it.PackagingTypeId,
                    PackageCount = it.PackageCount,
                    QtyKg = it.QtyKg
                });
            }

    }

    public OpResult CreateDeliveryFromActual(int executionId, string deliveryDate = null, string notes = null)
    {
        Require("production", "Create");
        var execution = Db.ProductionExecutions.AsNoTracking().FirstOrDefault(e => e.Id == executionId);
        if (execution == null) return OpResult.Fail("جلسة الإنتاج الفعلي غير موجودة.");
        if (!execution.IsDayClosed || execution.Status != DocStatuses.Completed)
            return OpResult.Fail("لا يمكن إنشاء أمر تسليم قبل حفظ الإنتاج الفعلي وإقفاله.");
        if (Db.ProductionDeliveries.Any(d => d.SourceType == DeliverySources.FromActual
            && d.SourceId == executionId && d.Status != DocStatuses.Cancelled))
            return OpResult.Fail("يوجد أمر تسليم إنتاج مرتبط بهذا الفعلي — افتحه للتحرير قبل تحريره للمخزن.");

        var ctx = GetSourceContext(DeliverySources.FromActual, executionId);
        var items = ctx.Lines.Where(l => l.RemainingQtyKg > 0.001)
            .Select(l => new ProductionDeliveryItemDto
            {
                OrderId = l.OrderId,
                ProductId = l.ProductId,
                LotId = l.LotId,
                CustomerId = l.CustomerId,
                PackagingTypeId = l.PackagingTypeId,
                PackageCount = l.PackageCount,
                QtyKg = Math.Round(l.RemainingQtyKg, 3)
            }).ToList();
        if (items.Count == 0) return OpResult.Fail("لا توجد كمية فعلية قابلة للتسليم في جلسة الإنتاج.");
        return SaveDelivery(DeliverySources.FromActual, executionId,
            deliveryDate ?? DateTime.Now.ToString("dd/MM/yyyy"), items, null, notes);
    }

    public OpResult UpdateDelivery(int deliveryId, string deliveryDate, List<ProductionDeliveryItemDto> items, string notes = null)
    {
        Require("production", "Edit");
        if (items == null || items.Count == 0) return OpResult.Fail("أدخل بنداً واحداً على الأقل في أمر التسليم.");
        var delivery = Db.ProductionDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId);
        if (delivery == null) return OpResult.Fail("أمر التسليم غير موجود.");
        if (delivery.SourceType != DeliverySources.FromActual)
            return OpResult.Fail("لا يمكن تعديل أمر تسليم قديم غير نازل من الإنتاج الفعلي.");
        if (delivery.Status != DocStatuses.Draft)
            return OpResult.Fail("لا يمكن تعديل أمر التسليم بعد تحريره للمخزن.");
        if (delivery.Items.Any(i => i.ReceivedQtyKg > 0.001))
            return OpResult.Fail("بدأ استلام هذا الأمر — لا يمكن تعديله.");

        var ctx = GetSourceContext(DeliverySources.FromActual, delivery.SourceId);
        // مسودة الأمر الحالي لا تحجب نفسها عند إعادة حفظها.
        foreach (var line in ctx.Lines)
        {
            var old = delivery.Items.Where(i => i.ProductId == line.ProductId && i.LotId == line.LotId
                && i.CustomerId == line.CustomerId && i.PackagingTypeId == line.PackagingTypeId)
                .Sum(i => i.QtyKg);
            line.DeliveredQtyKg = Math.Max(0, line.DeliveredQtyKg - old);
            line.RemainingQtyKg = Math.Max(0, line.AvailableQtyKg - line.DeliveredQtyKg);
        }

        return RunOp(() =>
        {
            Db.ProductionDeliveryItems.RemoveRange(delivery.Items);
            delivery.Items.Clear();
            Db.SaveChanges(); // لا تحتسب حواجز السقف بنود المسودة القديمة أثناء إعادة البناء
            PopulateDeliveryItems(delivery, DeliverySources.FromActual, ctx.Lines, items);
            delivery.DeliveryDate = UiFormat.TryParseDate(deliveryDate, out var d) ? d : delivery.DeliveryDate;
            delivery.Notes = notes;
            delivery.ModifiedBy = Session?.UserId;
            delivery.ModifiedDate = DateTime.Now;
            Db.SaveChanges();
            return OpResult.Success($"تم تعديل أمر تسليم الإنتاج {delivery.DocumentNumber} — ما زال مسودة بانتظار تحرير مدير الإنتاج.", delivery.Id, delivery.DocumentNumber);
        });
    }

    public OpResult IssueDelivery(int deliveryId)
    {
        Require("production", "Approve");
        return RunOp(() =>
        {
            Db.ChangeTracker.Clear();
            var delivery = Db.ProductionDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId);
            if (delivery == null) throw new DomainException("أمر التسليم غير موجود.");
            if (delivery.SourceType != DeliverySources.FromActual)
                throw new DomainException("أوامر التسليم القديمة للقراءة التاريخية فقط — لا تُحرَّر للمخزن.");
            if (delivery.Status != DocStatuses.Draft)
                throw new DomainException("لا يُحرَّر إلا أمر تسليم مسودة من الإنتاج الفعلي.");
            if (delivery.Items.Count == 0)
                throw new DomainException("لا يمكن تحرير أمر بلا بنود.");
            delivery.Status = DocStatuses.Issued;
            delivery.IsApproved = true;
            delivery.ApprovedBy = Session?.UserId;
            delivery.ApprovedDate = DateTime.Now;
            string planMessage = CloseLinkedPlanForDelivery(delivery);
            Db.SaveChanges();
            return OpResult.Success($"تم تحرير أمر التسليم {delivery.DocumentNumber} إلى المخازن — بانتظار أمر استلام أمين مخزن التام.{planMessage}");
        });
    }

    /// <summary>
    /// إقفال الخطة هو أثر تحرير أمر تسليم الإنتاج، لا أثر اعتماد أمر الإنتاج
    /// ولا أثر تسجيل الفعلي ولا أثر استلام المخزن. الصلاحية حُكمت في IssueDelivery
    /// عبر production/Approve، ولا تُستخدم بوابة بديلة.
    /// </summary>
    private string CloseLinkedPlanForDelivery(ProductionDelivery delivery)
    {
        if (delivery.SourceType != DeliverySources.FromActual) return "";
        var execution = Db.ProductionExecutions.AsNoTracking().FirstOrDefault(e => e.Id == delivery.SourceId)
            ?? throw new DomainException("جلسة الإنتاج الفعلي المرتبطة بأمر التسليم غير موجودة.", "ACTUAL_MISSING");
        var order = Db.ProductionOrders.AsNoTracking().FirstOrDefault(o => o.Id == execution.OrderId);
        if (order?.SourcePlanId == null) return "";
        var plan = Db.ProductionPlans.FirstOrDefault(p => p.Id == order.SourcePlanId.Value)
            ?? throw new DomainException("الخطة المرتبطة بالإنتاج الفعلي غير موجودة.", "PLAN_MISSING");
        if (plan.Status == DocStatuses.Cancelled)
            throw new DomainException("الخطة المرتبطة ملغاة — لا يمكن تحرير أمر التسليم.", "PLAN_CANCELLED");
        if (!plan.IsApproved)
            throw new DomainException("الخطة المرتبطة غير معتمدة — لا يمكن تحرير أمر التسليم.", "PLAN_NOT_APPROVED");
        // The current delivery is Issued in this transaction but may still be
        // Draft in the database. Include it exactly once in the coverage check.
        return PlanDeliveryIntegrity.TryAutoClose(Db, Session, Numbering, _audit,
            plan.Id, delivery);
    }

    public OpResult CancelDelivery(int deliveryId)
    {
        Require("production", "Cancel");
        return RunOp(() =>
        {
            // Load and validate INSIDE the serializable transaction. A delivery
            // preloaded on this workstation can be received on another between
            // the click and this lock; never cancel using its stale navigation.
            Db.ChangeTracker.Clear();
            var delivery = Db.ProductionDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId);
            if (delivery == null) throw new DomainException("أمر التسليم غير موجود.");
            if (delivery.SourceType != DeliverySources.FromActual)
                throw new DomainException("أوامر التسليم القديمة للقراءة التاريخية فقط.");
            if (delivery.Status == DocStatuses.Cancelled)
                throw new DomainException("أمر التسليم ملغى مسبقاً.");
            if (delivery.Status == DocStatuses.Completed)
                throw new DomainException("الأمر مستلم بالكامل — ألغِ سندات الاستلام أولاً.");
            // Pending receipts are invalidated together with their source. A
            // receipt already posted must be reversed separately before cancel.
            var receipts = Db.FinishedGoodsReceipts.Include(r => r.Items)
                .Where(r => r.DeliveryId == delivery.Id && r.Status != DocStatuses.Cancelled).ToList();
            if (delivery.Items.Any(i => i.ReceivedQtyKg > 0.001)
                || receipts.Any(r => r.Items.Any(i => i.ReceivedQtyKg > 0.001)))
                throw new DomainException("بدأ استلام هذا الأمر — ألغِ سندات الاستلام أولاً ثم ألغِ الأمر.", "HAS_RECEIPTS");

            bool wasIssued = delivery.Status == DocStatuses.Issued && delivery.IsApproved;
            foreach (var receipt in receipts)
            {
                receipt.Status = DocStatuses.Cancelled;
                receipt.IsApproved = false;
                receipt.ReceiptStatus = "None";
            }
            delivery.Status = DocStatuses.Cancelled;
            delivery.IsApproved = false;
            delivery.ReceiptStatus = "None";
            Db.SaveChanges();

            string planMessage = "";
            if (wasIssued)
            {
                var orderId = Db.ProductionExecutions.AsNoTracking()
                    .Where(e => e.Id == delivery.SourceId).Select(e => e.OrderId).FirstOrDefault();
                var planId = Db.ProductionOrders.AsNoTracking()
                    .Where(o => o.Id == orderId).Select(o => o.SourcePlanId).FirstOrDefault();
                if (planId != null)
                {
                    var plan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == planId.Value);
                    if (plan?.IsClosed == true && plan.Status == DocStatuses.Closed)
                    {
                        plan.IsClosed = false;
                        plan.Status = DocStatuses.Approved;
                        plan.ClosedDate = null;
                        plan.ClosedBy = null;
                        Db.SaveChanges(); // SQL reservation query now sees the reopened plan.
                        PlanDeliveryIntegrity.RecalculateLotReservations(Db,
                            plan.Items.Where(i => i.LotId != null).Select(i => i.LotId!.Value));
                        _audit.Log("إدارة الإنتاج", "إعادة فتح خطة بعد إلغاء تسليم إنتاج", "Plan", plan.DocumentNumber, plan.Id,
                            new { الحالة_السابقة = "مقفلة", أمر_التسليم = delivery.DocumentNumber },
                            new { الحالة_الجديدة = "معتمدة", المستخدم = Session?.UserId });
                        planMessage = $" وأُعيد فتح الخطة {plan.DocumentNumber} وحجوزاتها.";
                    }
                }
            }
            Db.SaveChanges();
            return OpResult.Success($"تم إلغاء أمر التسليم {delivery.DocumentNumber} وإبطال {receipts.Count} سند استلام غير مُرحَّل." + planMessage);
        });
    }

    public DeliverySourceContext GetSourceContext(string sourceType, int sourceId)
    {
        var ctx = new DeliverySourceContext { SourceType = sourceType, SourceId = sourceId };
        if (sourceType == DeliverySources.FromActual)
        {
            var execution = Db.ProductionExecutions.AsNoTracking().FirstOrDefault(e => e.Id == sourceId);
            if (execution == null) return ctx;
            ctx.SourceNumber = execution.DocumentNumber;
            ctx.SourceDate = UiFormat.D(execution.EndDateTime);
            var order = Db.ProductionOrders.Include(o => o.Items).AsNoTracking().FirstOrDefault(o => o.Id == execution.OrderId);
            ctx.Lines = BuildSourceLines(sourceType, sourceId, null, order, null, execution);
        }
        else if (sourceType == DeliverySources.FromCheck)
        {
            var check = Db.QualityChecks.Include(c => c.Items).AsNoTracking().FirstOrDefault(c => c.Id == sourceId);
            if (check == null) return ctx;
            ctx.SourceNumber = check.DocumentNumber;
            ctx.SourceDate = UiFormat.D(check.CheckDate);
            var order = check.OrderId != null
                ? Db.ProductionOrders.Include(o => o.Items).AsNoTracking().FirstOrDefault(o => o.Id == check.OrderId.Value)
                : null;
            ctx.Lines = BuildSourceLines(sourceType, sourceId, check, order, null, null);
        }
        else if (sourceType == DeliverySources.FromPlan || sourceType == DeliverySources.FromClosing)
        {
            var plan = Db.ProductionPlans.Include(p => p.Items).AsNoTracking().FirstOrDefault(p => p.Id == sourceId);
            if (plan == null) return ctx;
            ctx.SourceNumber = plan.DocumentNumber;
            ctx.SourceDate = UiFormat.D(plan.StartDate);
            ctx.Lines = BuildSourceLines(sourceType, sourceId, null, null, plan, null);
        }
        return ctx;
    }

    public List<(int Id, string Label)> GetSourceDocs(string sourceType)
    {
        if (sourceType == DeliverySources.FromActual)
            return Db.ProductionExecutions.AsNoTracking()
                .Where(e => e.IsDayClosed && e.Status == DocStatuses.Completed)
                .OrderByDescending(e => e.Id).Take(200).ToList()
                .Select(e => (e.Id, $"{e.DocumentNumber} — {UiFormat.D(e.EndDateTime)} — {e.ActualCartons:N0} كرتون"))
                .ToList();
        if (sourceType == DeliverySources.FromCheck)
            return Db.QualityChecks.AsNoTracking().Include(c => c.Items)
                .Where(c => c.IsApproved && c.OrderId != null)
                .OrderByDescending(c => c.Id).Take(200).ToList()
                .Select(c => (c.Id, $"{c.DocumentNumber} — {UiFormat.D(c.CheckDate)} — مقبول {c.Items.Sum(i => i.AcceptedQtyKg):N1} كجم"))
                .ToList();
        if (sourceType == DeliverySources.FromPlan)
            return Db.ProductionPlans.AsNoTracking()
                .Where(p => p.IsApproved && p.Status != DocStatuses.Cancelled && !p.IsClosed)
                .OrderByDescending(p => p.Id).Take(200).ToList()
                .Select(p => (p.Id, $"{p.DocumentNumber} — {UiFormat.D(p.StartDate)}"))
                .ToList();
        if (sourceType == DeliverySources.FromClosing)
            return Db.ProductionPlans.AsNoTracking()
                .Where(p => p.IsApproved && p.IsClosed)
                .OrderByDescending(p => p.Id).Take(200).ToList()
                .Select(p => (p.Id, $"{p.DocumentNumber} — {UiFormat.D(p.StartDate)} (مقفلة)"))
                .ToList();
        return new List<(int, string)>();
    }

    public ProductionDeliveryCard GetDelivery(int deliveryId)
    {
        var d = Db.ProductionDeliveries.Include(x => x.Items).AsNoTracking().FirstOrDefault(x => x.Id == deliveryId);
        if (d == null) return null;
        var card = new ProductionDeliveryCard
        {
            Id = d.Id,
            DocumentNumber = d.DocumentNumber,
            DeliveryDate = UiFormat.D(d.DeliveryDate),
            SourceType = d.SourceType,
            SourceTypeAr = DeliverySources.ToArabic(d.SourceType),
            SourceId = d.SourceId,
            SourceNumber = SourceNumber(d.SourceType, d.SourceId),
            BypassReason = d.BypassReason,
            Status = d.Status,
            StatusAr = DocStatuses.ToArabic(d.Status),
            ReceiptStatus = d.ReceiptStatus
        };
        foreach (var i in d.Items)
            card.Lines.Add(new ProductionDeliveryLineRow
            {
                Id = i.Id,
                OrderId = i.OrderId,
                OrderNumber = i.OrderId != null ? Db.ProductionOrders.AsNoTracking().Where(o => o.Id == i.OrderId.Value).Select(o => o.DocumentNumber).FirstOrDefault() : null,
                ProductId = i.ProductId,
                ProductName = ProdName(i.ProductId),
                LotId = i.LotId,
                LotCode = i.LotId != null ? LotCode(i.LotId) : null,
                CustomerId = i.CustomerId,
                CustomerName = i.CustomerId != null ? CustName(i.CustomerId) : null,
                PackagingTypeId = i.PackagingTypeId,
                PackageCount = i.PackageCount,
                QtyKg = i.QtyKg,
                ReceivedQtyKg = i.ReceivedQtyKg,
                RemainingQtyKg = Math.Max(0, i.QtyKg - i.ReceivedQtyKg)
            });
        return card;
    }

    public List<ProductionDeliveryCard> GetDeliveries(string statusFilter = null)
    {
        var ids = Db.ProductionDeliveries.AsNoTracking()
            .Where(d => statusFilter == null || d.Status == statusFilter)
            .OrderByDescending(d => d.Id).Take(300).Select(d => d.Id).ToList();
        return ids.Select(GetDelivery).Where(c => c != null).ToList();
    }

    // ── سطور المصدر: المتاح (مقبول/منتَج) ناقص ما سُلِّم بأوامر غير ملغاة ──
    private List<DeliverySourceLine> BuildSourceLines(string sourceType, int sourceId,
        QualityCheck check, ProductionOrder order, ProductionPlan plan, ProductionExecution execution)
    {
        var lines = new List<DeliverySourceLine>();
        if (sourceType == DeliverySources.FromActual)
        {
            if (order == null) return lines;
            var actualItems = order.Items
                .Where(i => i.ProducedQtyKg > 0.001 || i.ProducedCartons > 0)
                .GroupBy(i => new { i.ProductId, i.LotId, CustomerId = i.CustomerId ?? order.CustomerId, i.PackagingTypeId });
            foreach (var g in actualItems)
            {
                double produced = g.Sum(i => i.ProducedQtyKg);
                int packages = g.Sum(i => i.ProducedCartons);
                double delivered = Db.ProductionDeliveryItems.AsNoTracking()
                    .Join(Db.ProductionDeliveries.AsNoTracking(), i => i.DeliveryId, d => d.Id, (i, d) => new { i, d })
                    .Where(x => x.d.Status != DocStatuses.Cancelled && x.d.SourceType == DeliverySources.FromActual
                        && x.d.SourceId == sourceId && x.i.ProductId == g.Key.ProductId && x.i.LotId == g.Key.LotId
                        && x.i.CustomerId == g.Key.CustomerId
                        && x.i.PackagingTypeId == g.Key.PackagingTypeId)
                    .Sum(x => x.i.QtyKg);
                lines.Add(new DeliverySourceLine
                {
                    OrderId = order.Id,
                    OrderNumber = order.DocumentNumber,
                    ProductId = g.Key.ProductId,
                    ProductName = ProdName(g.Key.ProductId),
                    LotId = g.Key.LotId,
                    LotCode = g.Key.LotId != null ? LotCode(g.Key.LotId) : null,
                    CustomerId = g.Key.CustomerId,
                    CustomerName = g.Key.CustomerId != null ? CustName(g.Key.CustomerId) : null,
                    PackagingTypeId = g.Key.PackagingTypeId,
                    PackageCount = packages,
                    AvailableQtyKg = produced,
                    DeliveredQtyKg = delivered,
                    RemainingQtyKg = Math.Max(0, produced - delivered)
                });
            }
        }
        else if (sourceType == DeliverySources.FromCheck)
        {
            if (check == null || order == null) return lines;
            foreach (var g in check.Items.GroupBy(i => new { i.ProductId, i.LotId }))
            {
                double accepted = g.Sum(i => i.AcceptedQtyKg);
                if (accepted <= 0.001) continue;
                // §عميل السطر: بند الأمر المطابق (صنف + دفعة) ثم أول بند للصنف ثم عميل الأمر
                int? cust = order.Items.FirstOrDefault(i => i.ProductId == g.Key.ProductId && i.LotId == g.Key.LotId)?.CustomerId
                    ?? order.Items.FirstOrDefault(i => i.ProductId == g.Key.ProductId)?.CustomerId
                    ?? order.CustomerId;
                double delivered = Db.ProductionDeliveryItems.AsNoTracking()
                    .Join(Db.ProductionDeliveries.AsNoTracking(), i => i.DeliveryId, d => d.Id, (i, d) => new { i, d })
                    .Where(x => x.d.Status != DocStatuses.Cancelled && x.d.SourceType == DeliverySources.FromCheck
                        && x.d.SourceId == sourceId && x.i.ProductId == g.Key.ProductId && x.i.LotId == g.Key.LotId)
                    .Sum(x => x.i.QtyKg);
                lines.Add(new DeliverySourceLine
                {
                    OrderId = order.Id,
                    OrderNumber = order.DocumentNumber,
                    ProductId = g.Key.ProductId,
                    ProductName = ProdName(g.Key.ProductId),
                    LotId = g.Key.LotId,
                    LotCode = g.Key.LotId != null ? LotCode(g.Key.LotId) : null,
                    CustomerId = cust,
                    CustomerName = cust != null ? CustName(cust) : null,
                    AvailableQtyKg = accepted,
                    DeliveredQtyKg = delivered,
                    RemainingQtyKg = Math.Max(0, accepted - delivered)
                });
            }
        }
        else
        {
            if (plan == null) return lines;
            // §التجاوز يسلِّم المنتَج فقط (لا المخطط النظري): المنتَج المزامَن من الأوامر
            var planTypes = new[] { DeliverySources.FromPlan, DeliverySources.FromClosing };
            foreach (var pi in plan.Items)
            {
                if (pi.ProducedQtyKg <= 0.001) continue;
                int? ordId = Db.ProductionOrderItems.AsNoTracking()
                    .Where(o => o.PlanItemId == pi.Id).OrderBy(o => o.Id)
                    .Select(o => (int?)o.OrderId).FirstOrDefault();
                double delivered = Db.ProductionDeliveryItems.AsNoTracking()
                    .Join(Db.ProductionDeliveries.AsNoTracking(), i => i.DeliveryId, d => d.Id, (i, d) => new { i, d })
                    .Where(x => x.d.Status != DocStatuses.Cancelled && planTypes.Contains(x.d.SourceType)
                        && x.d.SourceId == sourceId && x.i.ProductId == pi.ProductId
                        && x.i.LotId == pi.LotId && x.i.CustomerId == pi.CustomerId)
                    .Sum(x => x.i.QtyKg);
                lines.Add(new DeliverySourceLine
                {
                    OrderId = ordId,
                    OrderNumber = ordId != null ? Db.ProductionOrders.AsNoTracking().Where(o => o.Id == ordId.Value).Select(o => o.DocumentNumber).FirstOrDefault() : null,
                    ProductId = pi.ProductId,
                    ProductName = ProdName(pi.ProductId),
                    LotId = pi.LotId,
                    LotCode = pi.LotId != null ? LotCode(pi.LotId) : null,
                    CustomerId = pi.CustomerId,
                    CustomerName = pi.CustomerId != null ? CustName(pi.CustomerId) : null,
                    AvailableQtyKg = pi.ProducedQtyKg,
                    DeliveredQtyKg = delivered,
                    RemainingQtyKg = Math.Max(0, pi.ProducedQtyKg - delivered)
                });
            }
        }
        return lines;
    }

    private string SourceNumber(string sourceType, int sourceId)
    {
        if (sourceType == DeliverySources.FromActual)
            return Db.ProductionExecutions.AsNoTracking().Where(e => e.Id == sourceId).Select(e => e.DocumentNumber).FirstOrDefault() ?? $"#{sourceId}";
        if (sourceType == DeliverySources.FromCheck)
            return Db.QualityChecks.AsNoTracking().Where(c => c.Id == sourceId).Select(c => c.DocumentNumber).FirstOrDefault() ?? $"#{sourceId}";
        return Db.ProductionPlans.AsNoTracking().Where(p => p.Id == sourceId).Select(p => p.DocumentNumber).FirstOrDefault() ?? $"#{sourceId}";
    }

    private string ProdName(int id)
        => Db.Products.AsNoTracking().Where(p => p.Id == id).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"صنف #{id}";

    private string LotCode(int? id)
        => id == null ? null : Db.Lots.AsNoTracking().Where(l => l.Id == id.Value).Select(l => l.LotCode).FirstOrDefault() ?? $"دفعة #{id}";

    private string CustName(int? id)
        => id == null ? null : Db.Customers.AsNoTracking().Where(c => c.Id == id.Value).Select(c => c.CustomerName).FirstOrDefault() ?? $"عميل #{id}";
}
