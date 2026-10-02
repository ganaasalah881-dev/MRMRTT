using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>إكمال حتمي لبنود الاستلام الجديدة فقط. لا إدخال مستخدم ولا شاشة مستقلة.</summary>
internal sealed class ReceivingTreatmentLifecycle : ServiceBase
{
    public ReceivingTreatmentLifecycle(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    public void Synchronize()
    {
        var now = Db.BusinessNow;
        // الاستعلام القبلي لا يحمل كيانات؛ إعادة القراءة تحت المعاملة هي المرجع.
        var due = Db.ShipmentItems.Where(i => i.TreatmentRequired == true && i.TreatmentUntilDate <= now
            && i.TreatmentCompletedAt == null && Db.Shipments.Any(s => s.Id == i.ShipmentId && s.IsApproved)
            && Db.RawTreatments.Any(t => t.ReceivingItemId == i.Id && t.Status == TreatmentStatuses.InProgress));
        if (!due.Any()) return;
        void Complete()
        {
            foreach (var item in due.ToList())
            {
                var treatment = Db.RawTreatments.Single(t => t.ReceivingItemId == item.Id);
                if (treatment.Status != TreatmentStatuses.InProgress || item.TreatmentCompletedAt != null) continue;
                if (item.TreatmentUntilDate == null || item.TreatmentUntilDate > now
                    || treatment.ExpectedReadyAt != item.TreatmentUntilDate.Value.Date)
                    throw new DomainException("عدم اتساق موعد المعالجة مع بند الاستلام؛ لم يُحرّر المخزون.");
                var lot = Db.Lots.Single(l => l.Id == treatment.LotId && l.ShipmentItemId == item.Id);
                if (treatment.ReleasedQtyKg != 0 || treatment.RejectedQtyKg != 0
                    || Math.Abs(lot.UnderTreatmentQtyKg - treatment.QtyKg) > 0.001)
                    throw new DomainException("أرصدة بند المعالجة غير متسقة؛ يلزم مراجعة السجل دون تحرير مبكر.");
                var ship = Db.Shipments.Single(s => s.Id == item.ShipmentId);
                var refNo = treatment.TreatmentNo + "/DUE";
                PostStockMovement(WarehouseId("WTRT"), MovementType.Outbound, treatment.QtyKg,
                    treatment.PackageCount, ReferenceDocType.TreatmentRelease, refNo,
                    productId: lot.ProductId, lotId: lot.Id, customerId: lot.CustomerId,
                    packagingTypeId: lot.PackagingTypeId, notes: "انتهاء المدة المختارة داخل سند الاستلام");
                PostStockMovement(ship.ReceivingWarehouseId ?? WarehouseId("WRM"), MovementType.Inbound,
                    treatment.QtyKg, treatment.PackageCount, ReferenceDocType.TreatmentRelease, refNo,
                    productId: lot.ProductId, lotId: lot.Id, customerId: lot.CustomerId,
                    packagingTypeId: lot.PackagingTypeId, notes: "جاهز للإجراء التالي حسب دورة الإنتاج القائمة");
                lot.UnderTreatmentQtyKg -= treatment.QtyKg;
                lot.TreatmentReadyQtyKg += treatment.QtyKg;
                treatment.ReleasedQtyKg = treatment.QtyKg;
                treatment.Status = TreatmentStatuses.Released;
                treatment.CompletedAt = now;
                item.TreatmentCompletedAt = now;
                // ختم البند رمز تزامن، ورقم الحركة ثابت: التكرار/جهازان لا يضاعفان الرصيد.
                Db.SaveChanges();
            }
        }
        if (Db.Database.CurrentTransaction != null) Complete();
        else RunInTransaction(Complete);
    }
}
