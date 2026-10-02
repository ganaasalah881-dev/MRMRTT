using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §7 — الدورة القانونية لاستلام الإنتاج التام:
/// أمر التسليم (متعدد الأصناف) ← الإصدار للمخزن (بلا أثر على الأرصدة)
/// ← سند الاستلام المخزني هو وحده ما يحرّك أرصدة مخزن التام (كلي/جزئي لكل صنف).
/// </summary>
public class FinishedGoodsService : ServiceBase, IFinishedGoodsService
{
    // The delivery status and its remaining quantities must be locked until a
    // receipt is saved/issued/posted; another workstation may cancel it at once.
    protected override System.Data.IsolationLevel TransactionIsolation => System.Data.IsolationLevel.Serializable;

    public FinishedGoodsService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    public OpResult SaveReceipt(int orderId, int? qualityCheckId, string deliveryDate, List<FinishedGoodsItemDto> items, int? deliveryId = null)
    {
        Require("finishedgoods", "Create");
        if (items == null || items.Count == 0) return OpResult.Fail("أدخل بنداً واحداً على الأقل.");
        if (deliveryId == null)
            return OpResult.Fail("لا يمكن إنشاء أمر استلام الإنتاج مباشرة من أمر الإنتاج أو الفعلي. اختر أمر تسليم إنتاج محرراً من مدير الإنتاج.");
        var order = Db.ProductionOrders.Include(o => o.Items).FirstOrDefault(o => o.Id == orderId);
        if (order == null) return OpResult.Fail("أمر الإنتاج غير موجود.");
        // المسار الرسمي الوحيد: أمين مخزن التام ينشئ أمر الاستلام من أمر تسليم فعلي محرر.
        var delivery = Db.ProductionDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == deliveryId.Value);
        if (delivery == null) return OpResult.Fail("أمر تسليم الإنتاج غير موجود — تحقق من الرقم.");
        if (delivery.SourceType != DeliverySources.FromActual)
            return OpResult.Fail("أمر الاستلام لا يُنشأ إلا من أمر تسليم نازل من الإنتاج الفعلي.");
        var sourceExecution = Db.ProductionExecutions.AsNoTracking().FirstOrDefault(e => e.Id == delivery.SourceId);
        if (sourceExecution == null || sourceExecution.OrderId != orderId)
            return OpResult.Fail("أمر التسليم لا يرتبط بالتنفيذ الفعلي لأمر الإنتاج المحدد.");
        if (delivery.Status == DocStatuses.Draft) return OpResult.Fail("أمر التسليم مسودة — يجب تحريره من مدير الإنتاج أولاً.");
        if (delivery.Status == DocStatuses.Cancelled) return OpResult.Fail("أمر التسليم ملغى.");
        if (delivery.Status == DocStatuses.Completed) return OpResult.Fail("أمر التسليم مستلم بالكامل مسبقاً.");
        var delOrders = delivery.Items.Where(i => i.OrderId != null).Select(i => i.OrderId.Value).Distinct().ToList();
        if (!delOrders.Contains(orderId)) return OpResult.Fail("الأمر المحدد ليس من أوامر أمر التسليم المحدد.");
        if (delivery.Status != DocStatuses.Issued)
            return OpResult.Fail("أمر التسليم ليس محرراً للمخزن.");

        return RunOp(() =>
        {
            if (!Db.ProductionDeliveries.AsNoTracking().Any(d => d.Id == deliveryId.Value
                && d.SourceType == DeliverySources.FromActual && d.Status == DocStatuses.Issued && d.IsApproved))
                throw new DomainException("أمر تسليم الإنتاج لم يعد محررًا للمخزن — حدّث المستند قبل الاستلام.", "DELIVERY_NOT_ISSUED");
            var rcpt = new FinishedGoodsReceipt
            {
                DocumentNumber = Numbering.Next("FGR"),
                OrderId = orderId,
                QualityCheckId = qualityCheckId,
                DeliveryId = deliveryId,
                DeliveryDate = UiFormat.TryParseDate(deliveryDate, out var d) ? d : DateTime.Now,
                WarehouseId = WarehouseId("WFG"),
                Status = DocStatuses.Draft,
                ReceiptStatus = "None"
            };
            // A delivery line, not the client DTO or current product master data,
            // is authoritative for product, lot, customer, package and cartons.
            if (items.GroupBy(i => i.DeliveryItemId).Any(g => g.Key == null || g.Count() > 1))
                throw new DomainException("كل بند استلام يحتاج بند أمر تسليم مستقلًا وغير مكرر.", "DUP_LINE");
            foreach (var it in items)
            {
                UnitsPolicy.RequireItemType(Db, it.ProductId, "Finished", "استلام الإنتاج التام");
                if (!double.IsFinite(it.NetWeightKg) || it.NetWeightKg <= 0)
                    throw new DomainException("كمية الاستلام يجب أن تكون موجبة ومحددة.", "INVALID_RECEIPT_QTY");

                // §B96 — بند التسليم هو الحاكم (المتبقي + الهوية)؛ لا يوجد مسار استلام مباشر.
                int? effCust = null;
                int? effLine = null;
                if (it.DeliveryItemId == null)
                    throw new DomainException("حدد بند أمر التسليم لكل صنف في السند المربوط.", "NO_DELIVERY_LINE");
                var line = delivery.Items.FirstOrDefault(l => l.Id == it.DeliveryItemId.Value)
                    ?? throw new DomainException("بند التسليم غير تابع لأمر التسليم المحدد.", "NO_DELIVERY_LINE");
                if (line.OrderId != orderId || line.ProductId != it.ProductId)
                    throw new DomainException("أمر الإنتاج أو الصنف لا يطابق بند أمر التسليم المحدد.", "LINE_MISMATCH");
                if (it.LotId != null && it.LotId != line.LotId)
                    throw new DomainException("الدفعة لا تطابق بند أمر التسليم المحدد.", "LOT_MISMATCH");
                if (it.LotId == null) it.LotId = line.LotId;
                if (it.CustomerId != null && it.CustomerId != line.CustomerId)
                    throw new DomainException("عميل الاستلام لا يطابق بند أمر التسليم.", "CUSTOMER_MISMATCH");
                if (it.PackagingTypeId != null && it.PackagingTypeId != line.PackagingTypeId)
                    throw new DomainException("عبوة الاستلام لا تطابق عبوة أمر التسليم.", "PACKAGING_MISMATCH");
                it.PackagingTypeId = line.PackagingTypeId;
                // §B86/H8 بالمثل: المسودات لا تحجب بعضها — السقف على المستلَم ويُعاد فحصه عند الاستلام
                double lineRemaining = line.QtyKg - line.ReceivedQtyKg;
                if (it.NetWeightKg > lineRemaining + 0.001)
                    throw new DomainException(
                        $"⛔ كمية البند ({it.NetWeightKg:N1} كجم) تتجاوز المتبقي في بند أمر التسليم ({lineRemaining:N1} كجم).",
                        "OVER_DELIVERY");
                if (line.PackageCount <= 0 || line.QtyKg <= 0)
                    throw new DomainException("بند أمر التسليم لا يحدد عدد كراتين موجبًا — أصلح المصدر قبل الاستلام.", "SOURCE_CARTONS_MISSING");
                // New receipts must account for cartons already POSTED for this
                // delivery line. Rounding each receipt independently (250/500×67
                // twice = 34+34) both overstates its document and misleads the UI.
                int postedCartons = (int)Math.Round(line.ReceivedQtyKg / line.QtyKg * line.PackageCount);
                int cumulativeCartons = (int)Math.Round((line.ReceivedQtyKg + it.NetWeightKg)
                    / line.QtyKg * line.PackageCount);
                int expectedCartons = cumulativeCartons - postedCartons;
                if (it.PackageCount == 0) it.PackageCount = expectedCartons; // derive from issued line
                double expectedKg = it.PackageCount * line.QtyKg / line.PackageCount;
                if (expectedCartons <= 0 || it.PackageCount != expectedCartons
                    || Math.Abs(it.NetWeightKg - expectedKg) > Math.Max(1.0, it.NetWeightKg * 0.02))
                    throw new DomainException("كمية الاستلام لا تطابق عدد الكراتين الكاملة لِعبوة أمر التسليم.", "RECEIPT_CARTON_MISMATCH");
                effCust = line.CustomerId;
                effLine = line.Id;

                rcpt.Items.Add(new FinishedGoodsReceiptItem
                {
                    ProductId = it.ProductId,
                    LotId = it.LotId,
                    CustomerId = effCust,
                    DeliveryItemId = effLine,
                    PackagingTypeId = it.PackagingTypeId,
                    PackageCount = it.PackageCount,
                    NetWeightKg = it.NetWeightKg,
                    ReceivedQtyKg = 0,
                    // Historical weight comes from the issued delivery, not an editable
                    // package definition that could have changed after production.
                    CartonWeightKg = line.QtyKg / line.PackageCount
                });
            }
            // The ledger movement key contains customer and packaging. Same product
            // and lot in different packages may share a receipt; identical keys may not.
            if (rcpt.Items.GroupBy(i => new { i.ProductId, i.LotId, i.CustomerId, i.PackagingTypeId })
                .Any(g => g.Count() > 1))
                throw new DomainException("بندا استلام لهما نفس الصنف والدفعة والعميل والعبوة — استلمهما بسندين منفصلين.", "DUP_LINE");
            Db.FinishedGoodsReceipts.Add(rcpt);
            Db.SaveChanges();
            // الإصدار لا يمس الأرصدة؛ الترحيل الفعلي يتم لاحقاً من إجراء الاستلام فقط.
            rcpt.CreatedBy = Session?.UserId;
            return OpResult.Success($"تم إنشاء أمر استلام الإنتاج {rcpt.DocumentNumber} من أمر التسليم المحرر {delivery.DocumentNumber} — أصدره ثم نفّذ الاستلام (المستخدم: {Session?.UserName} — التاريخ: {DateTime.Now:dd/MM/yyyy}).", rcpt.Id, rcpt.DocumentNumber);
        });
    }

    /// <summary>الإصدار إلى المخزن — لا يمس أي رصيد (§7).</summary>
    public OpResult Issue(int receiptId)
    {
        Require("finishedgoods", "Approve");
        return RunOp(() =>
        {
            Db.ChangeTracker.Clear();
            var rcpt = Db.FinishedGoodsReceipts.FirstOrDefault(r => r.Id == receiptId);
            if (rcpt == null) throw new DomainException("أمر الاستلام غير موجود.");
            if (rcpt.DeliveryId == null)
                throw new DomainException("أمر الاستلام لا يملك مرجع أمر تسليم إنتاج.");
            if (rcpt.Status != DocStatuses.Draft)
                throw new DomainException("لا يمكن إصدار إلا سند استلام مسودة غير ملغى.");
            if (!Db.ProductionDeliveries.AsNoTracking().Any(d => d.Id == rcpt.DeliveryId.Value
                && d.SourceType == DeliverySources.FromActual && d.Status == DocStatuses.Issued && d.IsApproved))
                throw new DomainException("أمر تسليم الإنتاج مُلغى أو غير محرر — لا يمكن إصدار الاستلام.", "DELIVERY_NOT_ISSUED");
            rcpt.Status = DocStatuses.Issued;
            Db.SaveChanges();
            return OpResult.Success("تم إصدار أمر التسليم إلى المخزن — بانتظار سند الاستلام.");
        });
    }

    /// <summary>§7/§8 — سند الاستلام المخزني: وحده يؤثر على الأرصدة، كلياً أو جزئياً لكل صنف.</summary>
    public OpResult Receive(int receiptId, Dictionary<int, double> receivedByItemId)
    {
        Require("finishedgoods", "Approve");
        return RunOp(() =>
        {
            // Reload the receipt and all its lines under the same serializable
            // transaction as the source/status/stock checks. Preloading before
            // the transaction allowed two workstations to consume stale remainders.
            Db.ChangeTracker.Clear();
            var rcpt = Db.FinishedGoodsReceipts.Include(r => r.Items).FirstOrDefault(r => r.Id == receiptId);
            if (rcpt == null) throw new DomainException("أمر الاستلام غير موجود.");
            if (rcpt.DeliveryId == null)
                throw new DomainException("لا يمكن ترحيل استلام بلا أمر تسليم إنتاج مرتبط.");
            if (rcpt.ReceiptStatus == "Full") throw new DomainException("السند منفذ بالكامل مسبقاً.");
            if (rcpt.Status != DocStatuses.Issued && rcpt.Status != DocStatuses.Completed)
                throw new DomainException("لا يمكن الاستلام قبل إصدار أمر التسليم.");
            if (!Db.ProductionDeliveries.AsNoTracking().Any(d => d.Id == rcpt.DeliveryId.Value
                && d.SourceType == DeliverySources.FromActual && d.Status == DocStatuses.Issued && d.IsApproved))
                throw new DomainException("أمر تسليم الإنتاج مُلغى أو غير محرر — لا يمكن ترحيل الاستلام.", "DELIVERY_NOT_ISSUED");
            if (receivedByItemId != null && receivedByItemId.Any(kv =>
                !rcpt.Items.Any(i => i.Id == kv.Key) || !double.IsFinite(kv.Value) || kv.Value < 0))
                throw new DomainException("خريطة الاستلام تحتوي بندًا غير تابع للسند أو كمية غير صالحة.", "INVALID_RECEIPT_MAP");
            if (!rcpt.Items.Any(i => i.NetWeightKg - i.ReceivedQtyKg > 0.001
                && (receivedByItemId == null || (receivedByItemId.TryGetValue(i.Id, out var qty) && qty > 0.001))))
                throw new DomainException("لم تُدخل أي كمية مستلمة — لا أثر أو ترقيم لهذه المحاولة.", "NO_RECEIPT_QTY");
            var whFg = rcpt.WarehouseId;
            var orderCust = Db.ProductionOrders.Where(o => o.Id == rcpt.OrderId).Select(o => o.CustomerId).FirstOrDefault();
            double totalReceived = 0;
            rcpt.ReceiveCount++;
            rcpt.ReceiptNumber ??= Numbering.Next("RCV");
            var voucher = $"{rcpt.ReceiptNumber}#{rcpt.ReceiveCount}"; // لكل سند استلام (متابعة) ترقيم متسلسل
            var recvAcc = new Dictionary<int, double>(); // §B86/H8: مستلَم هذه الدفعة لكل صنف — بندَان لصنف واحد لا يتجاوزا السقف معاً
            // §B96 — المربوط: بنود التسليم للتحديث + مجمّع لكل بند (سندان لبند واحد لا يتجاوزاه معاً)
            var delLines = rcpt.DeliveryId != null
                ? Db.ProductionDeliveryItems.Where(i => i.DeliveryId == rcpt.DeliveryId.Value).ToList()
                : new List<ProductionDeliveryItem>();
            var recvAccLine = new Dictionary<int, double>();
            foreach (var item in rcpt.Items)
            {
                double remaining = item.NetWeightKg - item.ReceivedQtyKg;
                if (remaining <= 0.001) continue;
                // null explicitly means receive all (API). A non-null partial map
                // means ONLY listed rows; omitted rows stay at zero.
                double recv = receivedByItemId == null ? remaining
                    : (receivedByItemId.TryGetValue(item.Id, out var v) ? v : 0);
                if (recv <= 0) continue;
                if (recv > remaining + 0.001)
                    throw new DomainException($"الكمية المستلمة أكبر من المتبقي للبند ({remaining:N1} كجم).", "OVER_RECEIPT");
                // §B96 — المربوط: سقف بند التسليم أولاً (رسالة دقيقة) ثم السقف الفيزيائي الموحد (شبكة أمان ضد المباشر)
                ProductionDeliveryItem delLine = null;
                if (item.DeliveryItemId != null)
                {
                    delLine = delLines.FirstOrDefault(l => l.Id == item.DeliveryItemId.Value)
                        ?? throw new DomainException("بند أمر التسليم المربوط غير موجود.", "NO_DELIVERY_LINE");
                    recvAccLine.TryGetValue(delLine.Id, out var recvLineCall);
                    if (delLine.ReceivedQtyKg + recvLineCall + recv > delLine.QtyKg + 0.001)
                        throw new DomainException(
                            $"⛔ الاستلام يتجاوز بند أمر التسليم.\nالبند: {delLine.QtyKg:N1} كجم | المستلَم منه: {delLine.ReceivedQtyKg + recvLineCall:N1} | المطلوب: {recv:N1}",
                            "OVER_DELIVERY");
                    recvAccLine[delLine.Id] = recvLineCall + recv;
                    if (delLine.OrderId is int capOrder)
                    {
                        double producedCap = Db.ProductionOrderItems.AsNoTracking()
                            .Where(o => o.OrderId == capOrder && o.ProductId == item.ProductId)
                            .Sum(o => o.ProducedQtyKg);
                        // §بنود التسليم لنفس الأمر (مجموعة محلية — Contains تُترجم إلى IN)
                        var capLineIds = delLines.Where(l => l.OrderId == capOrder).Select(l => l.Id).ToHashSet();
                        double receivedAll = Db.FinishedGoodsReceiptItems.AsNoTracking()
                            .Join(Db.FinishedGoodsReceipts.AsNoTracking(), i => i.ReceiptId, r => r.Id, (i, r) => new { i, r })
                            .Where(x => x.r.Status != DocStatuses.Cancelled && x.i.Id != item.Id
                                && ((x.i.DeliveryItemId != null && capLineIds.Contains(x.i.DeliveryItemId.Value))
                                    || (x.i.DeliveryItemId == null && x.r.OrderId == capOrder)))
                            .Where(x => x.i.ProductId == item.ProductId)
                            .Sum(x => x.i.ReceivedQtyKg);
                        recvAcc.TryGetValue(item.ProductId, out var recvThisCall2);
                        if (receivedAll + item.ReceivedQtyKg + recvThisCall2 + recv > producedCap + 0.001)
                            throw new DomainException(
                                $"الاستلام يتجاوز المنتَج الفعلي للصنف (مباشر + مربوط معاً).\nالمنتَج: {producedCap:N1} كجم | المستلَم: {receivedAll + item.ReceivedQtyKg + recvThisCall2:N1} | المطلوب: {recv:N1}",
                                "EXCEED_ORDER_QTY");
                        recvAcc[item.ProductId] = recvThisCall2 + recv;
                    }
                    delLine.ReceivedQtyKg += recv;
                }
                else
                {
                // §B86/H8: سقف المنتَج يُفحص عند الاستلام أيضاً — مسودتان معاً قد تتجاوزا المنتَج الفعلي
                double receivedOthers = Db.FinishedGoodsReceiptItems
                    .Join(Db.FinishedGoodsReceipts, i => i.ReceiptId, r => r.Id, (i, r) => new { i, r })
                    .Where(x => x.r.OrderId == rcpt.OrderId && x.i.ProductId == item.ProductId
                        && x.r.Status != DocStatuses.Cancelled && x.i.Id != item.Id)
                    .Sum(x => x.i.ReceivedQtyKg);
                double producedCap = Db.ProductionOrderItems.AsNoTracking()
                    .Where(o => o.OrderId == rcpt.OrderId && o.ProductId == item.ProductId)
                    .Sum(o => o.ProducedQtyKg);
                recvAcc.TryGetValue(item.ProductId, out var recvThisCall);
                if (receivedOthers + item.ReceivedQtyKg + recvThisCall + recv > producedCap + 0.001)
                    throw new DomainException(
                        $"الاستلام يتجاوز المنتَج الفعلي للصنف.\nالمنتَج: {producedCap:N1} كجم | المستلَم في سندات أخرى: {receivedOthers:N1} | هذا السند بعد الاستلام: {item.ReceivedQtyKg + recvThisCall + recv:N1}",
                        "EXCEED_ORDER_QTY");

                recvAcc[item.ProductId] = recvThisCall + recv;
                }
                int pkgRecv;
                if (delLine != null)
                {
                    // One delivery line can be received by MULTIPLE receipts. Use
                    // its global cumulative quantity, not each receipt's ratio.
                    double previouslyReceived = delLine.ReceivedQtyKg - recv;
                    int before = (int)Math.Round(previouslyReceived / delLine.QtyKg * delLine.PackageCount);
                    int after = (int)Math.Round(delLine.ReceivedQtyKg / delLine.QtyKg * delLine.PackageCount);
                    pkgRecv = after - before;
                }
                else
                {
                    int before = item.ReceivedPackageCount;
                    int after = (int)Math.Round((item.ReceivedQtyKg + recv) / item.NetWeightKg * item.PackageCount);
                    pkgRecv = after - before;
                }
                item.ReceivedQtyKg += recv;
                item.ReceivedPackageCount += pkgRecv;
                totalReceived += recv;
                PostStockMovement(whFg, MovementType.Inbound, recv, pkgRecv,
                    ReferenceDocType.FinishedGoodsReceipt, voucher,
                    productId: item.ProductId, lotId: item.LotId, orderId: rcpt.OrderId,
                    customerId: item.CustomerId ?? orderCust, packagingTypeId: item.PackagingTypeId,
                    notes: $"استلام إنتاج تام — سند {rcpt.ReceiptNumber}");
            }

            if (totalReceived <= 0.001)
                throw new DomainException("لم تُدخل أي كمية مستلمة — أُلغيت المحاولة دون تغيير السند.", "NO_RECEIPT_QTY");

            bool full = rcpt.Items.All(i => i.ReceivedQtyKg + 0.001 >= i.NetWeightKg);
            rcpt.ReceiptStatus = full ? "Full" : "Partial";
            rcpt.IsApproved = true;
            rcpt.Status = DocStatuses.Completed;
            // §B96 — عكس التقدم على أمر التسليم المربوط
            if (rcpt.DeliveryId != null)
            {
                var delivery = Db.ProductionDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == rcpt.DeliveryId.Value);
                if (delivery != null)
                {
                    bool dFull = delivery.Items.Count > 0 && delivery.Items.All(i => i.ReceivedQtyKg + 0.001 >= i.QtyKg);
                    bool dAny = delivery.Items.Any(i => i.ReceivedQtyKg > 0.001);
                    delivery.ReceiptStatus = dFull ? "Full" : (dAny ? "Partial" : "None");
                    if (dFull) delivery.Status = DocStatuses.Completed;
                }
            }
            rcpt.ApprovedBy = Session?.UserId;
            rcpt.ApprovedDate = DateTime.Now;

            // إغلاق أمر الإنتاج تلقائياً عند الاكتمال الكامل
            if (full)
            {
                var order = Db.ProductionOrders.Include(o => o.Items).FirstOrDefault(o => o.Id == rcpt.OrderId);
                if (order != null && order.Items.All(i => i.IsClosed || i.ProducedQtyKg + 0.001 >= i.PlannedQtyKg))
                {
                    order.Status = DocStatuses.Completed;
                    order.IsClosed = true;
                    order.ClosedDate = DateTime.Now;
                }
                else if (order != null) order.Status = DocStatuses.PendingDelivery; // §B85/M9: ثابت معتمد بدل القيمة الحرة
            }

            Db.SaveChanges();
            return OpResult.Success(full
                ? "تم الاستلام الكامل وتقييد كامل الكمية في مخزن الإنتاج التام."
                : $"تم الاستلام الجزئي ({totalReceived:N1} كجم) وتقييدها في مخزن التام.", rcpt.Id, rcpt.ReceiptNumber);
        });
    }

    /// <summary>إلغاء السند يعكس الأرصدة بدقة ويحذف حركاته (§6).</summary>
    public OpResult Unapprove(int receiptId)
    {
        Require("finishedgoods", "Cancel");
        var rcpt = Db.FinishedGoodsReceipts.Include(r => r.Items).FirstOrDefault(r => r.Id == receiptId);
        if (rcpt == null) return OpResult.Fail("السند غير موجود.");
        if (!rcpt.IsApproved) return OpResult.Fail("السند غير معتمد.");

        return RunOp(() =>
        {
            var whFg = rcpt.WarehouseId;
            var orderCust = Db.ProductionOrders.Where(o => o.Id == rcpt.OrderId).Select(o => o.CustomerId).FirstOrDefault();
            var prefix = rcpt.ReceiptNumber ?? rcpt.DocumentNumber;
            // A historical receipt may predate ReceivedPackageCount. Never reverse
            // zero (or a rounded estimate) when the append-only ledger says otherwise.
            // Duplicate historical RCV numbers/stock keys require manual reconciliation.
            if (string.IsNullOrWhiteSpace(rcpt.ReceiptNumber)
                || Db.FinishedGoodsReceipts.AsNoTracking().Count(r => r.ReceiptNumber == rcpt.ReceiptNumber) != 1)
                throw new DomainException("لا يمكن إلغاء سند برقم استلام مفقود/مكرر؛ سوِّ السند التاريخي مع دفتر المخزون أولاً.", "AMBIGUOUS_RECEIPT");
            if (rcpt.Items.Where(i => i.ReceivedQtyKg > 0.001)
                .GroupBy(i => new { i.ProductId, i.LotId, Customer = i.CustomerId ?? orderCust, i.PackagingTypeId })
                .Any(g => g.Count() > 1))
                throw new DomainException("عدة بنود مستلمة تحمل نفس هوية المخزون؛ يلزم تسوية الحركات قبل الإلغاء.", "AMBIGUOUS_RECEIPT");
            foreach (var item in rcpt.Items.Where(i => i.ReceivedQtyKg > 0.001))
            {
                var voucherPrefix = rcpt.ReceiptNumber + "#";
                var ledger = Db.InventoryTransactions.AsNoTracking().Where(t =>
                        t.ReferenceDocType == ReferenceDocType.FinishedGoodsReceipt
                        && t.MovementType == MovementType.Inbound && t.ReferenceDocNumber != null
                        && t.ReferenceDocNumber.StartsWith(voucherPrefix)
                        && t.WarehouseId == whFg && t.OrderId == rcpt.OrderId
                        && t.ProductId == item.ProductId && t.LotId == item.LotId
                        && t.CustomerId == (item.CustomerId ?? orderCust)
                        && t.PackagingTypeId == item.PackagingTypeId)
                    .ToList();
                if (ledger.Count == 0 || Math.Abs(ledger.Sum(t => t.QtyKg) - item.ReceivedQtyKg) > 0.001
                    || ledger.Sum(t => t.PackageCount) != item.ReceivedPackageCount)
                    throw new DomainException("كراتين السند لا تطابق حركات الوارد؛ شغّل ترحيل السندات التاريخية أو سوِّ دفتر المخزون قبل الإلغاء.", "RECEIPT_LEDGER_MISMATCH");
            }
            // §إصلاح حرج — الإلغاء بقيد عكسي لا بحذف دفتر الأستاذ.
            // كان يحذف الحركات بـ StartsWith(prefix):
            //  • يدمّر سجلّاً إلحاقياً (قرارهم #48: «إلحاقي غير قابل للتعديل»)
            //  • وتصادم بادئات: عند السند رقم 10000 يصبح RCV-...-1000 بادئة له فيحذف حركاته
            //  • وكان يبحث الرصيد بلا CustomerId بينما Receive يكتب به ← قد يطرح من صف آخر
            // §B96 — أمر التسليم المربوط (يُحمَّل قبل التصفير ليُعكس عنه المستلَم بدقة)
            var delivery = rcpt.DeliveryId != null
                ? Db.ProductionDeliveries.Include(d => d.Items).FirstOrDefault(d => d.Id == rcpt.DeliveryId.Value)
                : null;
            int seq = 0;
            foreach (var item in rcpt.Items.Where(i => i.ReceivedQtyKg > 0))
            {
                int pkgBack = item.ReceivedPackageCount; // reverse exactly what the ledger posted
                seq++;
                PostStockMovement(whFg, MovementType.Outbound, item.ReceivedQtyKg, pkgBack,
                    ReferenceDocType.FinishedGoodsReceipt, $"{prefix}#REV{seq}",
                    productId: item.ProductId, lotId: item.LotId, orderId: rcpt.OrderId,
                    customerId: item.CustomerId ?? orderCust, packagingTypeId: item.PackagingTypeId,
                    notes: $"إلغاء سند الاستلام {rcpt.ReceiptNumber} — قيد عكسي");
                if (delivery != null && item.DeliveryItemId != null)
                {
                    var back = delivery.Items.FirstOrDefault(l => l.Id == item.DeliveryItemId.Value);
                    if (back != null) back.ReceivedQtyKg = Math.Max(0, back.ReceivedQtyKg - item.ReceivedQtyKg);
                }
                item.ReceivedQtyKg = 0;
                item.ReceivedPackageCount = 0;
            }
            rcpt.IsApproved = false;
            rcpt.ReceiptStatus = "None";
            rcpt.Status = DocStatuses.Issued;
            // §B96 — إعادة احتساب حالة أمر التسليم المربوط وإعادة فتحه إن اكتمل سابقاً
            string delReopenMsg = "";
            if (delivery != null)
            {
                bool dAny = delivery.Items.Any(i => i.ReceivedQtyKg > 0.001);
                delivery.ReceiptStatus = dAny ? "Partial" : "None";
                if (delivery.Status == DocStatuses.Completed)
                {
                    delivery.Status = DocStatuses.Issued;
                    delReopenMsg = " وأُعيد فتح أمر التسليم (استلامه لم يعد مكتملاً).";
                }
            }
            // §B86/H8: إلغاء آخر سند كامل يعيد فتح الأمر المغلق تلقائياً (التلقائي = Completed+مقفل؛ اليدوي = Closed فلا يُمس)
            string reopenMsg = "";
            bool otherFull = Db.FinishedGoodsReceipts.AsNoTracking()
                .Any(r => r.OrderId == rcpt.OrderId && r.Id != rcpt.Id && r.ReceiptStatus == "Full");
            if (!otherFull)
            {
                var ord = Db.ProductionOrders.FirstOrDefault(o => o.Id == rcpt.OrderId);
                if (ord != null && ord.IsClosed && ord.Status == DocStatuses.Completed)
                {
                    ord.IsClosed = false;
                    ord.ClosedDate = null;
                    reopenMsg = " وأُعيد فتح أمر الإنتاج (تسليمه لم يعد مكتملاً).";
                }
            }
            Db.SaveChanges();
            return OpResult.Success("تم إلغاء السند وعكس أرصدة مخزن التام بالكامل." + reopenMsg + delReopenMsg);
        });
    }
}
