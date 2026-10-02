using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §7/§8 — تسليم الإنتاج للعميل مع الحراس:
/// لا تسليم أكبر من رصيد العميل في مخزن التام، لا تسليم دفعة عميل لعميل آخر، لا تكرار التسليم.
/// </summary>
public class CustomerDeliveryService : ServiceBase, ICustomerDeliveryService
{
    protected override System.Data.IsolationLevel TransactionIsolation => System.Data.IsolationLevel.Serializable;
    public CustomerDeliveryService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    /// <summary>§B105/P1 — حارس الكمية: صفر وسالب ممنوعان (كان السالب يخصم الرصيد ويعكس المسلَّم سالباً).</summary>
    private static string QtyGuard(CustomerDeliveryItemDto it)
    {
        if (it.QtyKg <= 0) return "كمية البند يجب أن تكون أكبر من صفر — لا تُقبل بنود صفرية أو سالبة.";
        if (it.PackageCount < 0) return "عدد العبوات لا يمكن أن يكون سالباً.";
        return null;
    }

    /// <summary>حراس البند المشتركة (الحفظ والتعديل): كمية + ملكية دفعة + تحويل + نوع تام.</summary>
    private void ItemGuards(int customerId, CustomerDeliveryItemDto it)
    {
        string q = QtyGuard(it);
        if (q != null) throw new DomainException(q, "INVALID_QTY");

        // §8 — الدفعة يجب أن تخص نفس العميل
        if (it.LotId is int lotId)
        {
            var lot = Db.Lots.FirstOrDefault(l => l.Id == lotId);
            if (lot == null) throw new DomainException("الدفعة غير موجودة.");
            if (lot.CustomerId != null && lot.CustomerId != customerId)
                throw new DomainException($"لا يمكن تسليم كمية العميل إلى عميل آخر — الدفعة {lot.LotCode} تخص عميلاً مختلفاً.", "CROSS_CUSTOMER");
        }

        // §تتبع الصنف: لا تسليم خلاص من دفعة سكري — التحويل الرسمي فقط حسب بطاقة المنتج
        ProductIdentityGuard.EnsureConversionAllowed(Db, it.ProductId, it.LotId);

        // §نظام الوحدات: التسليم للعميل منتجات تامة فقط (002)
        UnitsPolicy.RequireItemType(Db, it.ProductId, "Finished", "تسليم العميل");
    }

    private readonly record struct PlanScope(int PlanId, int? SingleOrderId);

    /// <summary>
    /// A delivery's header OrderId can be guessed from the first lot in the UI;
    /// it is NOT evidence of which plan supplied a physical stock key. Only an
    /// order LINE whose complete identity matches an eligible plan line may
    /// establish a source. The warehouse balance itself has no plan dimension.
    /// </summary>
    private PlanScope ResolvePlanScope(CustomerDelivery delivery, CustomerDeliveryItem item)
    {
        if (item.LotId is not int lotId)
            throw new DomainException("لا يمكن إثبات خطة بند التسليم بلا دفعة محددة؛ راجع رصيد مخزن التام.", "PLAN_SCOPE");

        var orderLines = (from oi in Db.ProductionOrderItems.AsNoTracking()
                          join o in Db.ProductionOrders.AsNoTracking() on oi.OrderId equals o.Id
                          where oi.LotId == lotId && oi.ProductId == item.ProductId
                              && oi.PackagingTypeId == item.PackagingTypeId
                              && (oi.CustomerId == delivery.CustomerId
                                  || (oi.CustomerId == null && o.CustomerId == delivery.CustomerId))
                          select new { oi.PlanItemId, o.SourcePlanId, o.Id }).ToList();
        if (orderLines.Count == 0)
            throw new DomainException(
                "لا يمكن إسناد التسليم إلى خطة: لا يوجد بند أمر إنتاج مطابق للدفعة والصنف والعميل والعبوة.",
                "PLAN_SCOPE");

        foreach (var line in orderLines)
        {
            if (line.SourcePlanId is not int planId)
                throw new DomainException(
                    "لا يمكن إثبات خطة التسليم: يوجد بند أمر إنتاج مطابق غير مرتبط بخطة. راجع مصدر المخزون قبل الاعتماد.",
                    "PLAN_SCOPE");
            // An explicit PlanItemId must point INSIDE this very plan and match the
            // physical key. A missing historical link is accepted only if that
            // order's plan has a matching line. Never borrow a line from another plan.
            bool matchingPlanLine = Db.ProductionPlanItems.AsNoTracking().Any(pi =>
                pi.PlanId == planId && (line.PlanItemId == null || pi.Id == line.PlanItemId)
                && pi.LotId == lotId && pi.ProductId == item.ProductId
                && pi.CustomerId == delivery.CustomerId && pi.PackagingTypeId == item.PackagingTypeId
                && pi.ScheduledDate != null);
            if (!matchingPlanLine)
                throw new DomainException(
                    "رابط بند أمر الإنتاج بالخطة لا يطابق الدفعة والصنف والعميل والعبوة؛ أوقف التسليم وراجع البيانات.",
                    "PLAN_LINK_MISMATCH");
        }

        var planIds = orderLines.Select(l => l.SourcePlanId!.Value).Distinct().ToList();
        if (planIds.Count != 1)
            throw new DomainException(
                $"الدفعة #{lotId} / الصنف #{item.ProductId} / العبوة #{item.PackagingTypeId} مرتبطة بأكثر من خطة إنتاج. "
                + "لا يمكن استنتاج مصدر المخزون من الدفعة أو أمر رأس السند؛ سوِّ المصدر قبل الاعتماد.",
                "PLAN_AMBIGUOUS");
        var orderIds = orderLines.Select(l => l.Id).Distinct().ToList();
        return new PlanScope(planIds[0], orderIds.Count == 1 ? orderIds[0] : null);
    }

    public OpResult Save(int customerId, string deliveryDate, int? orderId, List<CustomerDeliveryItemDto> items)
    {
        Require("delivery", "Create");
        if (items == null || items.Count == 0) return OpResult.Fail("أدخل بنداً واحداً على الأقل.");
        foreach (var itq in items)
        {
            string q = QtyGuard(itq);
            if (q != null) return OpResult.Fail(q);
        }
        var customer = Db.Customers.FirstOrDefault(c => c.Id == customerId);
        if (customer == null) return OpResult.Fail("العميل غير موجود.");

        return RunOp(() =>
        {
            var dlv = new CustomerDelivery
            {
                DocumentNumber = Numbering.Next("CD"),
                CustomerId = customerId,
                OrderId = orderId,
                DeliveryDate = UiFormat.TryParseDate(deliveryDate, out var d) ? d : DateTime.Now,
                Status = DocStatuses.Draft
            };
            foreach (var it in items)
            {
                ItemGuards(customerId, it);
                // §مؤجَّل بقرار: توحيد الكرتون/الكيلو عند التسليم يغيّر كميات تاريخية ويحتاج
                // قرار منتج (أي المدخلين مرجعي). موثق في أمر العمل — لا يُطبَّق ضمن هذه الحزمة.

                dlv.Items.Add(new CustomerDeliveryItem
                {
                    ProductId = it.ProductId,
                    LotId = it.LotId,
                    PackagingTypeId = it.PackagingTypeId,
                    PackageCount = it.PackageCount,
                    QtyKg = it.QtyKg,
                    // §القاعدة 7: وزن الكرتون وقت التسليم — لا يتغير بتعريف العبوة لاحقاً
                    CartonWeightKg = UnitsPolicy.CartonWeight(Db, it.ProductId, it.PackagingTypeId)
                });
            }
            dlv.TotalQtyKg = dlv.Items.Sum(i => i.QtyKg);
            dlv.TotalCartons = dlv.Items.Sum(i => i.PackageCount);
            Db.CustomerDeliveries.Add(dlv);
            Db.SaveChanges();
            return OpResult.Success($"تم حفظ سند تسليم العميل {dlv.DocumentNumber}.", dlv.Id, dlv.DocumentNumber);
        });
    }

    /// <summary>§B105/P2 — تعديل سند مسودة: يستبدل البنود بنفس حراس الحفظ. المعتمد يُرفض.</summary>
    public OpResult Update(int deliveryId, int customerId, string deliveryDate, int? orderId, List<CustomerDeliveryItemDto> items)
    {
        Require("delivery", "Edit");
        if (items == null || items.Count == 0) return OpResult.Fail("أدخل بنداً واحداً على الأقل.");
        foreach (var itq in items)
        {
            string q = QtyGuard(itq);
            if (q != null) return OpResult.Fail(q);
        }
        var customer = Db.Customers.FirstOrDefault(c => c.Id == customerId);
        if (customer == null) return OpResult.Fail("العميل غير موجود.");

        return RunOp(() =>
        {
            var dlv = Db.CustomerDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId)
                      ?? throw new DomainException("سند التسليم غير موجود.");
            if (dlv.IsApproved || dlv.Status == DocStatuses.Completed)
                throw new DomainException("السند معتمد — لا يُعدَّل. ألغِ الاعتماد أولاً ثم عدّل.", "APPROVED_LOCK");

            dlv.CustomerId = customerId;
            // NULL means no verified single source order; do not retain a stale
            // guessed header when a draft's lines or customer are changed.
            dlv.OrderId = orderId;
            dlv.DeliveryDate = UiFormat.TryParseDate(deliveryDate, out var d) ? d : dlv.DeliveryDate;

            Db.CustomerDeliveryItems.RemoveRange(dlv.Items);
            dlv.Items.Clear();
            foreach (var it in items)
            {
                ItemGuards(customerId, it);
                dlv.Items.Add(new CustomerDeliveryItem
                {
                    ProductId = it.ProductId,
                    LotId = it.LotId,
                    PackagingTypeId = it.PackagingTypeId,
                    PackageCount = it.PackageCount,
                    QtyKg = it.QtyKg,
                    CartonWeightKg = UnitsPolicy.CartonWeight(Db, it.ProductId, it.PackagingTypeId)
                });
            }
            dlv.TotalQtyKg = dlv.Items.Sum(i => i.QtyKg);
            dlv.TotalCartons = dlv.Items.Sum(i => i.PackageCount);
            Db.SaveChanges();
            return OpResult.Success($"تم تحديث سند التسليم {dlv.DocumentNumber}.", dlv.Id, dlv.DocumentNumber);
        });
    }

    /// <summary>§B105/P2 — حذف مسودة فقط: لم تخصم شيئاً فلا أثر مخزني — المعتمد لا يُحذف أبداً.</summary>
    public OpResult DeleteDraft(int deliveryId)
    {
        Require("delivery", "Delete");
        var dlv = Db.CustomerDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId);
        if (dlv == null) return OpResult.Fail("سند التسليم غير موجود.");
        if (dlv.IsApproved || dlv.Status == DocStatuses.Completed)
            return OpResult.Fail("السند معتمد — لا يُحذف. إن لزم التصحيح: ألغِ الاعتماد (قيد عكسي) ثم عدّل.");
        return RunOp(() =>
        {
            string docNo = dlv.DocumentNumber;
            Db.CustomerDeliveryItems.RemoveRange(dlv.Items);
            Db.CustomerDeliveries.Remove(dlv);
            Db.SaveChanges();
            return OpResult.Success($"تم حذف سند التسليم المسودة {docNo}.");
        });
    }

    public OpResult Approve(int deliveryId)
    {
        Require("delivery", "Approve");
        var dlv = Db.CustomerDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId);
        if (dlv == null) return OpResult.Fail("سند التسليم غير موجود.");
        if (dlv.IsApproved) return OpResult.Fail("سند التسليم معتمد مسبقاً — لا يسمح بتكرار التسليم.");

        return RunOp(() =>
        {
            // QC is checked per physical stock key AFTER the effective lot is
            // resolved, inside this same serializable approval transaction.
            var whFg = WarehouseId("WFG");
            // Resolve EVERY line, including its physical lot, before posting any
            // movement. The header order is derived only when the complete document
            // has exactly one verified source order; a guessed order never selects a plan.
            var scopes = new Dictionary<int, PlanScope>();
            foreach (var item in dlv.Items)
            {
                if (item.QtyKg <= 0)
                    throw new DomainException("كمية البند يجب أن تكون أكبر من صفر — لا تُقبل بنود صفرية أو سالبة.", "INVALID_QTY");

                // §B105/P6 — a draft without a lot can inherit one UNIQUE stock
                // lot; the full stock key includes PackagingTypeId (§1.50.66).
                if (item.LotId == null)
                {
                    var lotsWith = Db.StockBalances.AsNoTracking()
                        .Where(b => b.WarehouseId == whFg && b.ProductId == item.ProductId
                            && b.CustomerId == dlv.CustomerId && b.QtyKg > 0.001 && b.LotId != null
                            && b.PackagingTypeId == item.PackagingTypeId)
                        .Select(b => b.LotId!.Value).Distinct().ToList();
                    if (lotsWith.Count > 1)
                    {
                        string pname0 = Db.Products.AsNoTracking().Where(p => p.Id == item.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"صنف #{item.ProductId}";
                        throw new DomainException(
                            $"الصنف «{pname0}» له رصيد في {lotsWith.Count} دفعات مختلفة — حدّد الدفعة في البند حتى لا يُخصم اعتباطياً.",
                            "LOT_REQUIRED");
                    }
                    if (lotsWith.Count == 1) item.LotId = lotsWith[0];
                }
                var scope = ResolvePlanScope(dlv, item);
                if (item.DeliveredPlanId is int priorPlan && priorPlan != scope.PlanId)
                    throw new DomainException(
                        "تغيّر مصدر خطة هذا البند منذ الاعتماد السابق؛ حدّث السند المسودة بعد مراجعة المصدر ولا تستبدل إسناده صامتاً.",
                        "PLAN_CHANGED");
                scopes.Add(item.Id, scope);
            }
            var uniqueOrders = scopes.Values.Select(s => s.SingleOrderId).Distinct().ToList();
            dlv.OrderId = uniqueOrders.Count == 1 ? uniqueOrders[0] : null;

            var usedOutRefs = new HashSet<string>(); // §CD-FIX: تميز المراجع داخل الاعتماد الواحد
            foreach (var item in dlv.Items)
            {
                int? effectiveLot = item.LotId;
                var (qualityOk, qualityReason) = QualityGate.CustomerDeliveryAllowed(Db,
                    null, effectiveLot, item.ProductId, item.PackagingTypeId, dlv.CustomerId);
                if (!qualityOk) throw new DomainException(qualityReason);

                // §8 — رصيد العميل المتاح في مخزن التام يجب أن يغطي الكمية
                // §1.50.66 — مفتاح كامل: Warehouse + Product + Lot + Customer + PackagingType
                var balance = Db.StockBalances.FirstOrDefault(b =>
                    b.WarehouseId == whFg && b.ProductId == item.ProductId
                    && (effectiveLot == null || b.LotId == effectiveLot) && b.CustomerId == dlv.CustomerId
                    && b.PackagingTypeId == item.PackagingTypeId);
                var available = balance?.QtyKg ?? 0;
                if (available < item.QtyKg - 0.001)
                    throw new DomainException(
                        $"الكمية أكبر من رصيد العميل في مخزن التام.\nالمتاح: {available:N1} كجم — المطلوب: {item.QtyKg:N1} كجم",
                        "INSUFFICIENT_CUSTOMER_BALANCE");

                // §B95 — سقف المطابق المعتمد: بعد الرصيد (لتبقى رسائله) وقبل الخصم
                var (qok, qreason) = QualityGate.CustomerDeliveryQtyAllowed(Db, dlv, item);
                if (!qok) throw new DomainException(qreason);

                // §CD-FIX: إعادة الاعتماد بعد الإلغاء كانت مستحيلة (حارس DUPLICATE §8 يمنع صرفاً ثانياً
                // بنفس رقم السند). الاعتماد المتكرر يُرحَّل بمرجع مميز #R2… مع بقاء الحارس لغيره.
                string outRef = dlv.DocumentNumber;
                int priorOut = Db.InventoryTransactions.Count(t =>
                    t.ReferenceDocType == ReferenceDocType.CustomerDelivery
                    && t.MovementType == MovementType.Outbound && t.WarehouseId == whFg
                    && t.ProductId == item.ProductId && t.LotId == item.LotId
                    && (t.ReferenceDocNumber == dlv.DocumentNumber
                        || t.ReferenceDocNumber.StartsWith(dlv.DocumentNumber + "#R")));
                if (priorOut > 0) outRef = $"{dlv.DocumentNumber}#R{priorOut + 1}";
                for (int bump = 2; !usedOutRefs.Add(outRef + "|" + item.ProductId + "|" + item.LotId); bump++)
                    outRef = $"{dlv.DocumentNumber}#R{priorOut + bump}";
                PostStockMovement(whFg, MovementType.Outbound, item.QtyKg, item.PackageCount,
                    ReferenceDocType.CustomerDelivery, outRef,
                    productId: item.ProductId, lotId: item.LotId, customerId: dlv.CustomerId,
                    orderId: dlv.OrderId, packagingTypeId: item.PackagingTypeId,
                    notes: $"تسليم عميل — سند {dlv.DocumentNumber}");

                if (item.LotId is int lotId)
                {
                    var lot = Db.Lots.First(l => l.Id == lotId);
                    lot.DeliveredQtyKg += item.QtyKg;
                }
                // The exact verified plan is stored on THIS line in the same
                // transaction as its stock movement and plan progress.
                var planId = scopes[item.Id].PlanId;
                PlanSync.SyncDeliveredForPlan(Db, planId, dlv.CustomerId, item.ProductId,
                    item.LotId, item.PackagingTypeId, item.QtyKg, dlv.DocumentNumber);
                item.DeliveredPlanId = planId;
            }
            dlv.IsApproved = true;
            dlv.IsPosted = true;
            dlv.Status = DocStatuses.Completed;
            dlv.PostedDate = dlv.ApprovedDate = DateTime.Now;
            dlv.ApprovedBy = Session?.UserId;
            Db.SaveChanges();
            return OpResult.Success($"تم اعتماد التسليم وخصم {dlv.TotalQtyKg:N1} كجم من رصيد العميل.", dlv.Id, dlv.DocumentNumber);
        });
    }

    public OpResult Unapprove(int deliveryId)
    {
        Require("delivery", "Cancel");
        var dlv = Db.CustomerDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId);
        if (dlv == null) return OpResult.Fail("السند غير موجود.");
        if (!dlv.IsApproved) return OpResult.Fail("السند غير معتمد.");

        return RunOp(() =>
        {
            var whFg = WarehouseId("WFG");
            // New rows reverse ONLY the persisted plan. Old approved rows have
            // NULL here: allow reversal only if the complete source identity is
            // still uniquely provable, never from a guessed header or first lot.
            // Fail before posting any inbound movement if a legacy row is ambiguous.
            var reversePlans = dlv.Items.ToDictionary(i => i.Id,
                i => i.DeliveredPlanId ?? ResolvePlanScope(dlv, i).PlanId);
            // §إصلاح حرج — الإلغاء بقيد عكسي (وارد) لا بحذف حركات دفتر الأستاذ.
            // §CD-FIX: تسلسل العكس تراكمي عبر الإلغاءات المتكررة (#REV1 ثم #REV2…) حتى لا يصطدم حارس DUPLICATE.
            var usedRevRefs = new HashSet<string>();
            foreach (var item in dlv.Items)
            {
                int priorRev = Db.InventoryTransactions.Count(t =>
                    t.ReferenceDocType == ReferenceDocType.CustomerDelivery
                    && t.MovementType == MovementType.Inbound && t.WarehouseId == whFg
                    && t.ProductId == item.ProductId && t.LotId == item.LotId
                    && t.ReferenceDocNumber.StartsWith(dlv.DocumentNumber + "#REV"));
                string revRef = $"{dlv.DocumentNumber}#REV{priorRev + 1}";
                for (int bump = 2; !usedRevRefs.Add(revRef + "|" + item.ProductId + "|" + item.LotId); bump++)
                    revRef = $"{dlv.DocumentNumber}#REV{priorRev + bump}";
                PostStockMovement(whFg, MovementType.Inbound, item.QtyKg, item.PackageCount,
                    ReferenceDocType.CustomerDelivery, revRef,
                    productId: item.ProductId, lotId: item.LotId, customerId: dlv.CustomerId,
                    orderId: dlv.OrderId, packagingTypeId: item.PackagingTypeId,
                    notes: $"إلغاء سند التسليم {dlv.DocumentNumber} — قيد عكسي");
                if (item.LotId is int lotId)
                {
                    var lot = Db.Lots.FirstOrDefault(l => l.Id == lotId);
                    if (lot != null) lot.DeliveredQtyKg -= item.QtyKg;
                }
                // Never repeat an order/lot lookup for a new delivery: the
                // persisted plan is the source of truth even if orders change.
                int planId = reversePlans[item.Id];
                PlanSync.UnsyncDeliveredForPlan(Db, planId, dlv.CustomerId, item.ProductId,
                    item.LotId, item.PackagingTypeId, item.QtyKg);
                item.DeliveredPlanId = planId; // also records a uniquely proven legacy reversal
            }
            dlv.IsApproved = false;
            dlv.IsPosted = false;
            dlv.Status = DocStatuses.Draft;
            dlv.InvoicedQtyKg = 0; // §CD-FIX: قرار المستخدم — الإلغاء يصفّر الفوترة (لا فوترة على سند ملغي)
            Db.SaveChanges();
            return OpResult.Success("تم إلغاء التسليم وإعادة الكميات إلى رصيد العميل.");
        });
    }
}
