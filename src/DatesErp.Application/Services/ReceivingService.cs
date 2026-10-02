using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>§7 — استلام التمور واعتماد الشحنات وتوليد الدفعات (Lots) داخل معاملات ذرية.</summary>
public class ReceivingService : ServiceBase, IReceivingService
{
    public ReceivingService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    public OpResult SaveShipment(int customerId, string arrivalDate, string receivedDate, List<ShipmentItemDto> items, string notes = null, string containerNumber = null, int? receivedBy = null, int? existingId = null, int? warehouseId = null)
    {
        Require("receiving", existingId == null ? "Create" : "Edit");
        if (items == null || items.Count == 0) return OpResult.Fail("أدخل بنداً واحداً على الأقل.");
        var customer = Db.Customers.FirstOrDefault(c => c.Id == customerId);
        if (customer == null) return OpResult.Fail("العميل غير موجود.");

        return RunOp(() =>
        {
            Shipment ship;
            if (existingId != null)
            {
                ship = Db.Shipments.Include(x => x.Items).FirstOrDefault(x => x.Id == existingId)
                       ?? throw new DomainException("أمر الاستلام غير موجود.");
                if (ship.IsApproved) throw new DomainException("أمر الاستلام معتمد — ألغِ الاعتماد أولاً للتعديل.");
                // §B107 — تُحذف أجزاء درجات الإصابة صراحةً مع بنودها. الحذف المتتالي
                // معرَّف في النموذج، لكن التصريح به هنا يحمي قواعد قديمة أُنشئ فيها
                // الجدول بالترحيل الآمن بلا قيد أجنبي.
                var oldItemIds = ship.Items.Select(i => i.Id).ToList();
                Db.ShipmentItemTreatmentParts.RemoveRange(
                    Db.ShipmentItemTreatmentParts.Where(p => oldItemIds.Contains(p.ShipmentItemId)));
                Db.ShipmentItems.RemoveRange(ship.Items);
                ship.Items.Clear();
            }
            else
            {
                ship = new Shipment { DocumentNumber = Numbering.Next("SHIP") };
                Db.Shipments.Add(ship);
            }
            ship.CustomerId = customerId;
            ship.ArrivalDate = UiFormat.TryParseDate(arrivalDate, out var a) ? a : null;
            ship.ReceivedDate = UiFormat.TryParseDate(receivedDate, out var r) ? r.Date : Db.BusinessNow.Date;
            ship.ReceivedBy = receivedBy ?? Session?.UserId;
            ship.ContainerNumber = containerNumber;
            ship.Notes = notes;
            // §المخازن المتعددة: يحفظ مخزن الاستلام الفعلي — الاعتماد يقيّد الوارد فيه تحديداً
            if (warehouseId != null && Db.Warehouses.Any(w => w.Id == warehouseId && w.IsActive))
                ship.ReceivingWarehouseId = warehouseId;
            ship.Status = DocStatuses.Draft;
            foreach (var it in items)
            {
                // §تتبع الصنف: كل بند استلام يجب أن يسجل صنفاً صريحاً — لا استلام باسم عام («تمور» فئة وليست صنفاً)
                if (it.ProductId <= 0)
                    throw new DomainException("يجب تحديد الصنف صراحةً لكل بند استلام — لا يُقبل استلام بدون اسم صنف حقيقي (سكري، خلاص...).", "NO_PRODUCT");
                var prod = Db.Products.FirstOrDefault(p => p.Id == it.ProductId)
                           ?? throw new DomainException("الصنف المحدد في بند الاستلام غير موجود في بطاقة الأصناف.");
                if (!prod.IsActive)
                    throw new DomainException($"الصنف «{prod.ProductNameAr}» موقوف — لا يمكن الاستلام به.");

                // §نظام الوحدات: الاستلام للمواد الخام فقط (001) — الكمية القياسية كجم
                UnitsPolicy.RequireItemType(Db, it.ProductId, "Raw", "الاستلام");

                var qty = it.QtyKg > 0 ? it.QtyKg : it.PackageCount * it.UnitWeightKg;
                if (!double.IsFinite(it.QtyKg) || it.QtyKg < 0 || !double.IsFinite(it.UnitWeightKg)
                    || it.UnitWeightKg < 0 || it.PackageCount < 0 || !double.IsFinite(qty) || qty <= 0)
                    throw new DomainException("كمية أو عدد طرود غير صالح في أحد البنود.");

                var until = ReceivingLineTreatmentPolicy.Validate(it.TreatmentRequired, it.TreatmentUntilDate, ship.ReceivedDate.Value);
                if (it.TreatmentParts?.Count > 0)
                    throw new DomainException("إدخال الدرجات والمدد الثابتة لم يعد معتمداً؛ اختر نعم/لا وحتى تاريخ داخل البند.");
                var dest = it.TreatmentRequired == true ? ReceiptDestinations.Treatment : ReceiptDestinations.RawStore;

                var newItem = new ShipmentItem
                {
                    ProductId = it.ProductId,
                    PackagingTypeId = it.PackagingTypeId,
                    PackageCount = it.PackageCount,
                    UnitWeightKg = it.UnitWeightKg,
                    TotalWeightKg = qty,
                    // §قاعدة الاستلام: الخام قد يصل بأي عبوة — سلة/كيس/كرتون/غيرها.
                    // فتُسجَّل وحدة الاستلام الأصلية كما وردت، ولا تُفرض في الكود،
                    // والكيلو يبقى الوزن المرجعي في TotalWeightKg. ومجموعة الصنف (001) هي
                    // التي تحدد أنه خام — لا اسم الوحدة، فـ«كرتون» قد تكون عبوة خام.
                    ReceiptUnit = !string.IsNullOrWhiteSpace(it.ReceiptUnit)
                        ? it.ReceiptUnit.Trim()
                        : (Db.PackagingTypes.AsNoTracking().Where(k => k.Id == it.PackagingTypeId)
                               .Select(k => k.PackageNameAr).FirstOrDefault()
                           ?? Db.Products.AsNoTracking().Where(k => k.Id == it.ProductId)
                               .Select(k => k.UnitOfMeasure).FirstOrDefault()
                           ?? UnitsPolicy.UnitKg),
                    Status = string.IsNullOrWhiteSpace(it.ItemStatus) ? "Received" : it.ItemStatus,
                    Destination = dest,
                    TreatmentRequired = it.TreatmentRequired,
                    TreatmentUntilDate = until
                };
                ship.Items.Add(newItem);


            }
            ship.TotalWeightKg = ship.Items.Sum(i => i.TotalWeightKg);
            ship.TotalCartons = ship.Items.Sum(i => i.PackageCount);
            ship.ItemCount = ship.Items.Count;
            Db.SaveChanges();
            return OpResult.Success(existingId != null ? "تم حفظ التعديلات على سند الاستلام." : "تم حفظ أمر الاستلام بنجاح.", ship.Id, ship.DocumentNumber);
        });
    }

    /// <summary>§كشف تكرار رقم الحاوية: سندات سابقة بنفس الرقم (تحذير قبل الحفظ).</summary>
    public List<DuplicateContainerMatch> FindDuplicateContainers(string containerNumber, int? excludeShipmentId = null)
    {
        var cn = containerNumber?.Trim();
        if (string.IsNullOrWhiteSpace(cn)) return new List<DuplicateContainerMatch>();
        return Db.Shipments.AsNoTracking()
            .Where(s => s.ContainerNumber != null && s.ContainerNumber.Trim() == cn && (excludeShipmentId == null || s.Id != excludeShipmentId))
            .OrderByDescending(s => s.Id)
            .Select(s => new DuplicateContainerMatch
            {
                ShipmentId = s.Id,
                DocumentNumber = s.DocumentNumber,
                CustomerName = Db.Customers.Where(c => c.Id == s.CustomerId).Select(c => c.CustomerName).FirstOrDefault(),
                ReceivedDate = s.ReceivedDate,
                TotalWeightKg = s.TotalWeightKg,
                IsApproved = s.IsApproved
            }).ToList();
    }

    /// <summary>§23 — حذف سند لم يُعتمد بعد (المسودات فقط).</summary>
    public OpResult DeleteShipment(int shipmentId)
    {
        Require("receiving", "Delete");
        return RunOp(() =>
        {
            var ship = Db.Shipments.FirstOrDefault(x => x.Id == shipmentId);
            if (ship == null) throw new DomainException("أمر الاستلام غير موجود.");
            if (ship.IsApproved) throw new DomainException("لا يمكن حذف استلام معتمد — ألغِ الاعتماد أولاً.");
            // §v1.50.27: حتى المسودة لا تُحذف إذا كانت مرجَعة من مستندات إنتاج قائمة (بيانات قديمة).
            var refs = Db.ProductionPlanItems.AsNoTracking().Where(pi => pi.ShipmentId == shipmentId)
                .Join(Db.ProductionPlans.AsNoTracking().Where(pl => pl.Status != DocStatuses.Cancelled),
                    pi => pi.PlanId, pl => pl.Id, (pi, pl) => pl.DocumentNumber)
                .Union(Db.ProductionOrderItems.AsNoTracking().Where(oi => oi.ShipmentId == shipmentId)
                    .Join(Db.ProductionOrders.AsNoTracking().Where(o => o.Status != DocStatuses.Cancelled),
                        oi => oi.OrderId, o => o.Id, (oi, o) => o.DocumentNumber))
                .Take(4).ToList();
            if (refs.Count > 0)
                throw new DomainException("لا يمكن حذف السند: هو مرجَع من مستندات إنتاج قائمة ("
                    + string.Join("، ", refs) + ").");
            Db.Shipments.Remove(ship);
            Db.SaveChanges();
            return OpResult.Success("تم حذف سند الاستلام (المسودة).");
        });
    }

    /// <summary>الاعتماد ينشئ الدفعات ويقيد الوارد في مخزن الخام باسم العميل — معاملة واحدة.</summary>
    public OpResult ApproveShipment(int shipmentId)
    {
        Require("receiving", "Approve");
        var ship = Db.Shipments.Include(s => s.Items).FirstOrDefault(s => s.Id == shipmentId);
        if (ship == null) return OpResult.Fail("أمر الاستلام غير موجود.");
        if (ship.IsApproved) return OpResult.Fail("أمر الاستلام معتمد مسبقاً.");
        if (ship.Items.Count == 0) return OpResult.Fail("لا يمكن اعتماد استلام بدون بنود.");
        var receivedItems = ship.Items.Where(i => i.Status != "Rejected" && i.Status != "Pending").ToList();
        if (receivedItems.Count == 0) return OpResult.Fail("لا توجد بنود مستلمة للاعتماد — علّم البنود المستلمة أو أكمل الاستلام الجزئي.");

        return RunOp(() =>
        {
            // §المخازن المتعددة: مخزن الاستلام المختار في السند — أو الافتراضي WRM للسندات القديمة
            var whRaw = ship.ReceivingWarehouseId ?? WarehouseId("WRM");
            // إعادة التحقق لكل البنود، حتى المعلقة والمرفوضة؛ لا ترقية ضمنية لمسودة قديمة.
            foreach (var item in ship.Items)
            {
                item.TreatmentUntilDate = ReceivingLineTreatmentPolicy.Validate(
                    item.TreatmentRequired, item.TreatmentUntilDate, ship.ReceivedDate ?? Db.BusinessNow);
                item.Destination = item.TreatmentRequired == true ? ReceiptDestinations.Treatment : ReceiptDestinations.RawStore;
                if (!Db.Products.Any(p => p.Id == item.ProductId && p.IsActive))
                    throw new DomainException("الصنف موقوف أو غير موجود — راجع البند قبل الاعتماد.");
                UnitsPolicy.RequireItemType(Db, item.ProductId, "Raw", "الاستلام");
                if (!double.IsFinite(item.TotalWeightKg) || item.TotalWeightKg <= 0 || item.PackageCount < 0)
                    throw new DomainException("كمية أو عدد طرود غير صالح قبل الاعتماد.");
            }
            int startedCount = 0; double startedQty = 0;
            foreach (var item in receivedItems)
            {
                var lot = new Lot
                {
                    LotCode = Numbering.Next("LOT"),
                    ShipmentId = ship.Id,
                    ShipmentItemId = item.Id,
                    ProductId = item.ProductId,
                    CustomerId = ship.CustomerId,
                    PackagingTypeId = item.PackagingTypeId,
                    LotDate = ship.ReceivedDate ?? DateTime.Now,
                    InitialQtyKg = item.TotalWeightKg,
                    InStockQtyKg = item.TotalWeightKg,
                    Status = DocStatuses.Approved
                };
                Db.Lots.Add(lot);
                Db.SaveChanges(); // للحصول على معرف الدفعة قبل قيد الحركة
                PostStockMovement(whRaw, MovementType.Inbound, item.TotalWeightKg, item.PackageCount,
                    ReferenceDocType.ShipmentReceipt, ship.DocumentNumber,
                    productId: item.ProductId, lotId: lot.Id, customerId: ship.CustomerId,
                    packagingTypeId: item.PackagingTypeId,
                    notes: $"استلام شحنة {ship.DocumentNumber}");
                item.Status = DocStatuses.Approved;

                if (item.TreatmentRequired == true)
                {
                    var startAt = ship.ReceivedDate.Value.Date;
                    var until = item.TreatmentUntilDate.Value;
                    StartTreatmentCore(lot, null, item.TotalWeightKg, item.PackageCount,
                        startAt, (until - startAt).TotalHours, ship.ReceivedBy ?? Session?.UserId,
                        $"معالجة بند الاستلام {ship.DocumentNumber} حتى {until:dd/MM/yyyy}",
                        checkEligibility: false, sourceWarehouseId: whRaw, receivingItemId: item.Id, exactReadyAt: until);
                    startedCount++;
                    startedQty += item.TotalWeightKg;
                }
                else lot.TreatmentReadyQtyKg = item.TotalWeightKg;
            }

            ship.IsApproved = true;
            ship.Status = DocStatuses.Approved;
            ship.ApprovedBy = Session?.UserId;
            ship.ApprovedDate = DateTime.Now;
            Db.SaveChanges();
            EnsureReceiptTreatmentsCurrent();
            var pendingCount = ship.Items.Count(i => i.Status == "Pending");
            var rejCount = ship.Items.Count(i => i.Status == "Rejected");
            return OpResult.Success($"تم اعتماد الاستلام وإنشاء {receivedItems.Count} دفعة تلقائياً."
                + (pendingCount > 0 ? $" تبقّى {pendingCount} بنداً معلّقاً لاستلام لاحق." : "")
                + (rejCount > 0 ? $" رُفض {rejCount} بنداً." : "")
                + (startedCount > 0
                    ? $"\n🧪 بدأت {startedCount} عملية معالجة تلقائياً على {startedQty:N1} كجم — تُتابع حالتها وحتى تاريخ داخل بنود سند الاستلام."
                    : ""), ship.Id, ship.DocumentNumber);
        });
    }

    public OpResult UnapproveShipment(int shipmentId, string reason = null)
    {
        Require("receiving", "Cancel");
        var ship = Db.Shipments.Include(s => s.Lots).FirstOrDefault(s => s.Id == shipmentId);
        if (ship == null) return OpResult.Fail("أمر الاستلام غير موجود.");
        if (!ship.IsApproved) return OpResult.Fail("أمر الاستلام غير معتمد.");

        // §v1.50.27 — حارس السلامة المرجعية: إلغاء الاعتماد يحذف الدفعات؛ فإن كانت
        // الدفعات مرتبطة بخطط أو أوامر إنتاج قائمة لتمزّق السلسلة (وهي الثغرة التي
        // أبلغ عنها المستخدم: استلام معتمد ← خطط ← إلغاء الاعتماد والحذف ← أوامر يتيمة).
        // يُمنع الإلغاء وتُعرض أرقام المستندات التابعة ليتخلى عنها رسمياً أولاً.
        var lotIds = ship.Lots.Select(l => l.Id).ToList();
        var planRefs = Db.ProductionPlanItems.AsNoTracking()
            .Where(pi => pi.ShipmentId == ship.Id || (pi.LotId != null && lotIds.Contains(pi.LotId.Value)))
            .Join(Db.ProductionPlans.AsNoTracking().Where(pl => pl.Status != DocStatuses.Cancelled),
                pi => pi.PlanId, pl => pl.Id, (pi, pl) => pl.DocumentNumber)
            .Distinct().Take(4).ToList();
        if (planRefs.Count > 0)
            return OpResult.Fail("لا يمكن إلغاء اعتماد الاستلام: دفعاته مرتبطة بخطط إنتاج قائمة ("
                + string.Join("، ", planRefs) + "). ألغِ تلك الخطط رسمياً أولاً.");
        var orderRefs = Db.ProductionOrderItems.AsNoTracking()
            .Where(oi => oi.ShipmentId == ship.Id || (oi.LotId != null && lotIds.Contains(oi.LotId.Value)))
            .Join(Db.ProductionOrders.AsNoTracking().Where(o => o.Status != DocStatuses.Cancelled),
                oi => oi.OrderId, o => o.Id, (oi, o) => o.DocumentNumber)
            .Distinct().Take(4).ToList();
        if (orderRefs.Count > 0)
            return OpResult.Fail("لا يمكن إلغاء اعتماد الاستلام: دفعاته مرتبطة بأوامر إنتاج قائمة ("
                + string.Join("، ", orderRefs) + "). ألغِ تلك الأوامر رسمياً أولاً.");

        return RunOp(() =>
        {
            // منع الإلغاء إذا استُهلك من الدفعات أي كمية (§8)
            foreach (var lot in ship.Lots)
            {
                if (lot.ProducedQtyKg > 0 || lot.DeliveredQtyKg > 0)
                    throw new DomainException($"لا يمكن إلغاء الاستلام: الدفعة {lot.LotCode} استُهلك منها بالفعل.");
                // §B107 — الاعتماد قد يكون بدأ معالجةً تلقائياً، فالكمية غادرت مخزن الخام
                // إلى WTRT. عكسُ الاستلام هنا كان سيُنقص رصيداً لم يعد موجوداً ويكسر
                // ثابت التوازن. الإلغاء يمر بإلغاء المعالجة أولاً — فعل بشري موثّق.
                if (lot.UnderTreatmentQtyKg > 0.001 || Db.RawTreatments.Any(t => t.LotId == lot.Id))
                    throw new DomainException(
                        $"لا يمكن إلغاء الاستلام: الدفعة {lot.LotCode} دخلت دورة المعالجة والتعقيم.\n"
                        + "لا يُسمح بإلغاء الحجز أو تغيير الموعد يدوياً بعد الاعتماد؛ راجع حركات السند من تفاصيل الاستلام.",
                        "IN_TREATMENT");
                // §C3 — الإلغاء بقيد عكسي لا بمحو الدفتر (فلسفة §43): الحركة الأصلية تبقى،
                // ويُضاف قيد إرجاع يعكس الرصيد من نفس المخزن، وتبقى الدفعة معلَّمة «ملغاة» —
                // فالسجل الكامل (من استلم ومتى ولماذا أُلغي) لا يضيع أبداً.
                var whRaw = ship.ReceivingWarehouseId ?? WarehouseId("WRM");
                PostStockMovement(whRaw, MovementType.Outbound, lot.InitialQtyKg, 0,
                    ReferenceDocType.Return, $"{ship.DocumentNumber}#UNAP",
                    productId: lot.ProductId, lotId: lot.Id, customerId: lot.CustomerId,
                    packagingTypeId: lot.PackagingTypeId,
                    notes: $"إلغاء اعتماد الاستلام {ship.DocumentNumber} — الدفعة {lot.LotCode}"
                        + (string.IsNullOrWhiteSpace(reason) ? "" : $" — السبب: {reason}"));
                lot.Status = DocStatuses.Cancelled;
                lot.InStockQtyKg = 0;
                lot.ReservedQtyKg = 0;
            }
            ship.IsApproved = false;
            ship.Status = DocStatuses.Draft;
            Db.AuditLogs.Add(new Core.Domain.Entities.AuditLog
            {
                UserId = Session?.UserId > 0 ? Session.UserId : null,
                UserName = Session?.UserName ?? "system",
                MachineName = System.Environment.MachineName,
                ActionDate = DateTime.Now,
                ScreenName = "Receiving",
                ActionType = "UnapproveShipment",
                DocumentType = "Shipment",
                DocumentNumber = ship.DocumentNumber,
                RecordId = ship.Id,
                OldValue = "معتمد",
                NewValue = "مسودة (إلغاء اعتماد بقيد عكسي)" + (string.IsNullOrWhiteSpace(reason) ? "" : $" — السبب: {reason}")
            });
            Db.SaveChanges();
            return OpResult.Success("تم إلغاء اعتماد الاستلام وعكس أرصدته.");
        });
    }

    /// <summary>§استلام جزئي: سند لاحق يكمل البنود المعلّقة في سند معتمد (سلسلة ParentShipmentId).</summary>
    /// <summary>
    /// §C2 — تصحيح كمية/عميل سند استلام **معتمد** بقيد فرق موثق — بلا فك السلسلة.
    /// مسموح فقط ما دامت الدفعات لم تُستهلك ولم تدخل المعالجة ولم تُبنَ عليها خطط/أوامر؛
    /// وإلا فالطريق هو معالج تصحيح السلسلة (§C1). كل شيء بسبب مكتوب وتدقيق إلحاقي.
    /// </summary>
    public OpResult CorrectApprovedShipment(int shipmentId, List<ShipmentQtyCorrectionDto> corrections, int? newCustomerId, string reason)
    {
        Require("receiving", "CorrectApprovedQty"); // صلاحية حساسة — لا تُمنح افتراضياً لأي دور
        if (string.IsNullOrWhiteSpace(reason))
            return OpResult.Fail("التصحيح المعتمد يتطلب سبباً مكتوباً يُسجَّل في التدقيق.");
        bool hasQty = corrections != null && corrections.Count > 0;
        if (!hasQty && newCustomerId == null)
            return OpResult.Fail("حدّد تصحيح كمية أو عميلاً جديداً.");

        return RunOp(() =>
        {
            var ship = Db.Shipments.Include(x => x.Items).Include(x => x.Lots).FirstOrDefault(x => x.Id == shipmentId)
                ?? throw new DomainException("سند الاستلام غير موجود.");
            if (!ship.IsApproved)
                throw new DomainException("التصحيح المعتمد للمستندات المعتمدة فقط — المسودة تُعدَّل بالحفظ العادي.");

            var lotIds = ship.Lots.Select(l => l.Id).ToList();
            foreach (var lot in ship.Lots)
            {
                if (lot.ProducedQtyKg > 0 || lot.DeliveredQtyKg > 0)
                    throw new DomainException($"لا يمكن التصحيح: الدفعة {lot.LotCode} استُهلك منها فعلياً — استخدم التسوية أو تصحيح السلسلة.");
                if (lot.UnderTreatmentQtyKg > 0.001 || Db.RawTreatments.Any(t => t.LotId == lot.Id))
                    throw new DomainException($"لا يمكن التصحيح: الدفعة {lot.LotCode} في دورة المعالجة.");
            }
            if (Db.ProductionPlanItems.AsNoTracking().Any(pi => pi.ShipmentId == ship.Id || (pi.LotId != null && lotIds.Contains(pi.LotId.Value))))
                throw new DomainException("لا يمكن التصحيح المباشر: بُنيت خطط على هذا السند — استخدم معالج «تصحيح السلسلة».");
            if (Db.ProductionOrderItems.AsNoTracking().Any(oi => oi.ShipmentId == ship.Id || (oi.LotId != null && lotIds.Contains(oi.LotId.Value))))
                throw new DomainException("لا يمكن التصحيح المباشر: بُنيت أوامر إنتاج على هذا السند — استخدم معالج «تصحيح السلسلة».");

            var whRaw = ship.ReceivingWarehouseId ?? WarehouseId("WRM");
            var changes = new List<string>();

            if (hasQty)
                foreach (var c in corrections)
                {
                    var item = ship.Items.FirstOrDefault(i => i.Id == c.ShipmentItemId)
                        ?? throw new DomainException($"بند الشحنة رقم {c.ShipmentItemId} غير موجود.");
                    if (c.NewQtyKg <= 0)
                        throw new DomainException($"الكمية المصححة يجب أن تكون أكبر من صفر (البند {item.Id}).");
                    double delta = c.NewQtyKg - item.TotalWeightKg;
                    if (Math.Abs(delta) < 0.001) continue;
                    var lot = ship.Lots.FirstOrDefault(l => l.ShipmentItemId == item.Id);
                    if (delta > 0)
                        PostStockMovement(whRaw, MovementType.Inbound, delta, 0,
                            ReferenceDocType.ShipmentReceipt, $"{ship.DocumentNumber}#CORR+{item.Id}",
                            productId: item.ProductId, lotId: lot?.Id, customerId: ship.CustomerId,
                            packagingTypeId: item.PackagingTypeId,
                            notes: $"تصحيح زيادة على سند معتمد ({delta:N1} كجم) — السبب: {reason}");
                    else
                        PostStockMovement(whRaw, MovementType.Adjustment, -delta, 0,
                            ReferenceDocType.ShipmentReceipt, $"{ship.DocumentNumber}#CORR-{item.Id}",
                            productId: item.ProductId, lotId: lot?.Id, customerId: ship.CustomerId,
                            packagingTypeId: item.PackagingTypeId,
                            notes: $"تصحيح نقص على سند معتمد ({delta:N1} كجم) — السبب: {reason}");
                    double oldQty = item.TotalWeightKg;
                    item.TotalWeightKg = c.NewQtyKg;
                    if (item.UnitWeightKg > 0) item.PackageCount = (int)Math.Round(item.TotalWeightKg / item.UnitWeightKg);
                    if (lot != null)
                    {
                        lot.InitialQtyKg += delta;
                        lot.InStockQtyKg += delta;
                    }
                    changes.Add($"بند {item.Id}: {oldQty:N1} ← {c.NewQtyKg:N1} كجم");
                }
            ship.TotalWeightKg = ship.Items.Sum(i => i.TotalWeightKg);

            if (newCustomerId != null && newCustomerId.Value != ship.CustomerId)
            {
                var newCust = Db.Customers.AsNoTracking().FirstOrDefault(c => c.Id == newCustomerId.Value)
                    ?? throw new DomainException("العميل الجديد غير موجود.");
                string oldName = Db.Customers.AsNoTracking().Where(c => c.Id == ship.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? $"#{ship.CustomerId}";
                // §تعديل 1.50.52 — النقل بقيدين كاملين لا بقلب مفتاح الرصيد خلف ظهر الدفتر:
                // سحب كامل من العميل القديم (#CUST-) ثم إدخال كامل للعميل الجديد (#CUST+).
                // هكذا يبقى دفتر الحركات هو الحقيقة، والفحص العميق (D1) يطابق الأرصدة مع القيود.
                foreach (var lot in ship.Lots)
                {
                    PostStockMovement(whRaw, MovementType.Outbound, lot.InStockQtyKg, 0,
                        ReferenceDocType.ShipmentReceipt, $"{ship.DocumentNumber}#CUST-{lot.Id}",
                        productId: lot.ProductId, lotId: lot.Id, customerId: ship.CustomerId,
                        packagingTypeId: lot.PackagingTypeId,
                        notes: $"تصحيح عميل السند المعتمد — سحب من «{oldName}» إلى «{newCust.CustomerName}» — السبب: {reason}");
                    PostStockMovement(whRaw, MovementType.Inbound, lot.InStockQtyKg, 0,
                        ReferenceDocType.ShipmentReceipt, $"{ship.DocumentNumber}#CUST+{lot.Id}",
                        productId: lot.ProductId, lotId: lot.Id, customerId: newCustomerId.Value,
                        packagingTypeId: lot.PackagingTypeId,
                        notes: $"تصحيح عميل السند المعتمد — إدخال باسم «{newCust.CustomerName}» — السبب: {reason}");
                }
                foreach (var lot in ship.Lots) lot.CustomerId = newCustomerId.Value;
                ship.CustomerId = newCustomerId.Value;
                changes.Add($"العميل: {oldName} ← {newCust.CustomerName}");
            }

            Db.AuditLogs.Add(new Core.Domain.Entities.AuditLog
            {
                UserId = Session?.UserId > 0 ? Session.UserId : null,
                UserName = Session?.UserName ?? "system",
                MachineName = System.Environment.MachineName,
                ActionDate = DateTime.Now,
                ScreenName = "Receiving",
                ActionType = "CorrectShipment",
                DocumentType = "Shipment",
                DocumentNumber = ship.DocumentNumber,
                RecordId = ship.Id,
                OldValue = "معتمد كما هو",
                NewValue = string.Join(" | ", changes) + $" — السبب: {reason}"
            });
            Db.SaveChanges();
            return OpResult.Success($"تم التصحيح الموثق للسند {ship.DocumentNumber}:\n" + string.Join("\n", changes) + $".\nالقيود محفوظة في دفتر الحركة بالسبب والتدقيق.", ship.Id, ship.DocumentNumber);
        });
    }

    public OpResult ReceiveRemaining(int shipmentId, List<ReceivingTreatmentChoiceDto> choices = null, DateTime? receivedDate = null)
    {
        Require("receiving", "Create");
        var src = Db.Shipments.Include(x => x.Items).FirstOrDefault(x => x.Id == shipmentId);
        if (src == null) return OpResult.Fail("السند الأصلي غير موجود.");
        if (!src.IsApproved) return OpResult.Fail("أكمل اعتماد السند الأصلي أولاً.");
        var pend = src.Items.Where(i => i.Status == "Pending").ToList();
        if (pend.Count == 0) return OpResult.Fail("لا توجد بنود معلّقة متبقية في هذا السند.");
        if (choices != null && (choices.Count != pend.Count || choices.Select(c => c.ShipmentItemId).Distinct().Count() != pend.Count
            || choices.Any(c => !pend.Any(i => i.Id == c.ShipmentItemId))))
            return OpResult.Fail("اختيارات السند اللاحق يجب أن تطابق البنود المعلقة مرة واحدة لكل بند.");
        return RunOp(() =>
        {
            var ship = new Shipment
            {
                DocumentNumber = Numbering.Next("SHIP"),
                CustomerId = src.CustomerId,
                ContainerNumber = src.ContainerNumber,
                ArrivalDate = src.ArrivalDate,
                ReceivedDate = receivedDate?.Date ?? Db.BusinessNow.Date,
                ParentShipmentId = src.Id,
                ReceivingWarehouseId = src.ReceivingWarehouseId,
                ReceivedBy = src.ReceivedBy,
                Status = DocStatuses.Draft
            };
            foreach (var it in pend)
            {
                var choice = choices?.Single(c => c.ShipmentItemId == it.Id);
                var required = choices == null ? it.TreatmentRequired : choice.TreatmentRequired;
                var until = ReceivingLineTreatmentPolicy.Validate(required,
                    choices == null ? it.TreatmentUntilDate : choice.UntilDate, ship.ReceivedDate.Value);
                it.Status = "Moved"; // انتقلت للسند اللاحق — تبقى للأثر التدقيقي
                var moved = new ShipmentItem
                {
                    ProductId = it.ProductId,
                    PackagingTypeId = it.PackagingTypeId,
                    PackageCount = it.PackageCount,
                    UnitWeightKg = it.UnitWeightKg,
                    TotalWeightKg = it.TotalWeightKg,
                    ReceiptUnit = it.ReceiptUnit,
                    Status = "Received",
                    Destination = required == true ? ReceiptDestinations.Treatment : ReceiptDestinations.RawStore,
                    TreatmentRequired = required,
                    TreatmentUntilDate = until
                };
                ship.Items.Add(moved);
            }
            Db.Shipments.Add(ship);
            Db.SaveChanges();
            return OpResult.Success($"أُنشئ سند الاستلام اللاحق {ship.DocumentNumber} بـ {ship.Items.Count} بنداً معلّقاً — اعتمده ليدخل المخزون.", ship.Id, ship.DocumentNumber);
        });
    }
    public OpResult ProcessDueTreatments()
    {
        // عملية نظام حتمية، لا تاريخ/حالة/كمية من المستخدم؛ يجوز لمؤقت التطبيق استدعاؤها.
        EnsureReceiptTreatmentsCurrent();
        return OpResult.Success("تم تحديث الحالات المستحقة وفق تاريخ النظام.");
    }

    public List<ReceivingTreatmentStateDto> GetTreatmentStates(int shipmentId)
    {
        Require("receiving", "View");
        return ReadTreatmentStates(shipmentId);
    }

    internal List<ReceivingTreatmentStateDto> ReadTreatmentStates(int shipmentId)
    {
        var ship = Db.Shipments.AsNoTracking().Include(s => s.Items).FirstOrDefault(s => s.Id == shipmentId);
        if (ship == null) return new();
        var now = Db.BusinessNow;
        var result = new List<ReceivingTreatmentStateDto>();
        foreach (var item in ship.Items.OrderBy(i => i.Id))
        {
            var dto = new ReceivingTreatmentStateDto
            {
                ShipmentItemId = item.Id, TreatmentRequired = item.TreatmentRequired,
                UntilDate = item.TreatmentUntilDate, CompletedAt = item.TreatmentCompletedAt
            };
            if (item.TreatmentRequired == null && ship.IsApproved)
            {
                var lots = Db.Lots.Where(l => l.ShipmentItemId == item.Id).Select(l => l.Id);
                var old = Db.RawTreatments.AsNoTracking().Where(t => lots.Contains(t.LotId)).ToList();
                dto.HasLegacyTreatment = old.Count > 0;
                dto.UntilDate = old.Count > 0 ? old.Max(t => t.ExpectedReadyAt) : null;
                dto.StateAr = old.Any(t => t.Status == TreatmentStatuses.InProgress)
                    ? "قيد المعالجة — سجل سابق يخضع لضوابط الوقت والجودة الأصلية"
                    : old.Count > 0 ? "معالجة سابقة — راجع سجل الحركات" : "سجل سابق — لا معالجة مسجلة";
            }
            else if (item.TreatmentRequired == null) dto.StateAr = "يلزم اختيار نعم/لا قبل الحفظ والاعتماد";
            else if (item.TreatmentRequired == false) dto.StateAr = "لا يحتاج معالجة";
            else if (item.Status is "Pending" or "Rejected" or "Moved") dto.StateAr = "لم يدخل المخزون — " + (item.Status == "Pending" ? "معلق" : item.Status == "Rejected" ? "مرفوض" : "نقل لسند لاحق");
            else if (!ship.IsApproved) dto.StateAr = "قيد المعالجة حسب التاريخ — مسودة لم تُرحّل للمخزون";
            else if (item.TreatmentCompletedAt != null) dto.StateAr = "مكتملة — جاهز للإجراء التالي";
            else dto.StateAr = item.TreatmentUntilDate <= now ? "انتهت المدة — يلزم تحديث المخزون" : "قيد المعالجة";
            result.Add(dto);
        }
        return result;
    }

}
