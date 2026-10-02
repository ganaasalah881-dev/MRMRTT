// ═══════════════ §C1 — معالج «تصحيح السلسلة»: شجرة المستند + فك مترابط بترتيب صحيح ═══════════════
// المبدأ: التصحيح = عكس البناء. الأوامر (إن لم تُنفَّذ) تُلغى بسبب، ثم الخطط تُفك وتُحذف،
// ثم الشحنة يُلغى اعتمادها بقيد عكسي. أي أمر نُفّذ فعلياً = مانع صريح (الإقفال بتسوية يدوياً) —
// لا يعمل المعالج فوق استهلاك حقيقي أبداً. كل خطوة بصلاحيتها الخاصة وتدقيقها.
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

public class CorrectionService : ServiceBase
{
    public CorrectionService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    /// <summary>عقدة في شجرة السلسلة — للعرض في معالج التصحيح.</summary>
    public class ChainNode
    {
        public string Level { get; set; }      // 1 شحنة، 2 دفعة، 3 خطة، 4 أمر، 5 تنفيذ/جودة/تام
        public string DocType { get; set; }
        public string DocNumber { get; set; }
        public string StatusAr { get; set; }
        public string Info { get; set; }
        public string AutoAction { get; set; } // ما سيفعله المعالج تلقائياً
    }

    /// <summary>شجرة المستندات التابعة لشحنة (للعرض قبل التنفيذ).</summary>
    public List<ChainNode> GetShipmentChain(int shipmentId)
    {
        Require("receiving", "View");
        var nodes = new List<ChainNode>();
        var ship = Db.Shipments.AsNoTracking().Include(x => x.Lots).FirstOrDefault(x => x.Id == shipmentId);
        if (ship == null) throw new DomainException("سند الاستلام غير موجود.");
        nodes.Add(new ChainNode { Level = "1", DocType = "شحنة استلام", DocNumber = ship.DocumentNumber,
            StatusAr = DocStatuses.ToArabic(ship.Status), Info = $"{ship.TotalWeightKg:N1} كجم — {ship.Lots.Count} دفعة",
            AutoAction = ship.IsApproved ? "إلغاء اعتماد (قيد عكسي)" : "—" });

        var lotIds = ship.Lots.Select(l => l.Id).ToList();
        foreach (var lot in ship.Lots)
            nodes.Add(new ChainNode { Level = "2", DocType = "دفعة", DocNumber = lot.LotCode,
                StatusAr = DocStatuses.ToArabic(lot.Status),
                Info = $"أولي {lot.InitialQtyKg:N1} كجم — مستهلك {lot.ProducedQtyKg:N1} — معالج {lot.UnderTreatmentQtyKg:N1}",
                AutoAction = lot.ProducedQtyKg > 0 || lot.UnderTreatmentQtyKg > 0.001 ? "⛔ مانع: استُهلك/عولج" : "تُلغى مع الشحنة" });

        var planIds = Db.ProductionPlanItems.AsNoTracking()
            .Where(pi => pi.ShipmentId == ship.Id || (pi.LotId != null && lotIds.Contains(pi.LotId.Value)))
            .Select(pi => pi.PlanId).Distinct().ToList();
        foreach (var pl in Db.ProductionPlans.AsNoTracking().Where(p => planIds.Contains(p.Id) && p.Status != DocStatuses.Cancelled).ToList())
            nodes.Add(new ChainNode { Level = "3", DocType = "خطة إنتاج", DocNumber = pl.DocumentNumber,
                StatusAr = DocStatuses.ToArabic(pl.Status), Info = pl.IsClosed ? "مقفلة" : "قائمة",
                AutoAction = "فك اعتماد (إن اعتُمدت) ثم حذف رسمي" });

        var orderIds = Db.ProductionOrderItems.AsNoTracking()
            .Where(oi => oi.ShipmentId == ship.Id || (oi.LotId != null && lotIds.Contains(oi.LotId.Value)))
            .Select(oi => oi.OrderId).Distinct().ToList();
        foreach (var o in Db.ProductionOrders.AsNoTracking().Where(x => orderIds.Contains(x.Id) && x.Status != DocStatuses.Cancelled).ToList())
        {
            int exeCount = Db.ProductionExecutions.AsNoTracking().Count(e => e.OrderId == o.Id);
            nodes.Add(new ChainNode { Level = "4", DocType = "أمر إنتاج", DocNumber = o.DocumentNumber,
                StatusAr = DocStatuses.ToArabic(o.Status), Info = $"{exeCount} جلسة تنفيذ",
                AutoAction = exeCount > 0 ? "⛔ مانع: نُفّذ — يُقفل يدوياً بتسوية" : "إلغاء بسبب مكتوب" });
        }
        return nodes;
    }

    /// <summary>تنفيذ الفك المتسلسل: أوامر (بلا تنفيذ) ← خطط ← إلغاء اعتماد الشحنة. يتوقف عند أول مانع/فشل ويبلغ بما أُنجز.</summary>
    public OpResult RunShipmentCascade(int shipmentId, string reason)
    {
        Require("receiving", "ChainCorrection"); // صلاحية حساسة خاصة — لا تُمنح افتراضياً
        if (string.IsNullOrWhiteSpace(reason))
            return OpResult.Fail("تصحيح السلسلة يتطلب سبباً مكتوباً يُسجَّل في كل خطوة تدقيق.");

        var ship = Db.Shipments.Include(x => x.Lots).FirstOrDefault(x => x.Id == shipmentId);
        if (ship == null) return OpResult.Fail("سند الاستلام غير موجود.");
        if (!ship.IsApproved) return OpResult.Fail("السند غير معتمد — المسودة تُعدَّل بالحفظ العادي بلا معالج.");

        var lotIds = ship.Lots.Select(l => l.Id).ToList();
        var orderIds = Db.ProductionOrderItems.AsNoTracking()
            .Where(oi => oi.ShipmentId == ship.Id || (oi.LotId != null && lotIds.Contains(oi.LotId.Value)))
            .Select(oi => oi.OrderId).Distinct().ToList();
        var liveOrders = Db.ProductionOrders.Where(o => orderIds.Contains(o.Id) && o.Status != DocStatuses.Cancelled).ToList();

        // الموانع أولاً — لا عمل جزئي فوق استهلاك حقيقي
        var executed = liveOrders.Where(o => Db.ProductionExecutions.Any(e => e.OrderId == o.Id)).ToList();
        if (executed.Count > 0)
            return OpResult.Fail("لا يمكن الفك التلقائي: أوامر نُفّذت فعلياً — " + string.Join("، ", executed.Select(o => o.DocumentNumber))
                + ".\nأقفل أيام إنتاجها بتسوية موثقة ثم أقفل الخطة استثنائياً، وبعدها أعد المعالج.");
        var consumed = ship.Lots.Where(l => l.ProducedQtyKg > 0 || l.DeliveredQtyKg > 0 || l.UnderTreatmentQtyKg > 0.001).ToList();
        if (consumed.Count > 0)
            return OpResult.Fail("لا يمكن الفك: دفعات استُهلكت أو دخلت المعالجة — " + string.Join("، ", consumed.Select(l => l.LotCode)) + ".");

        var done = new List<string>();
        var orders = new ProductionOrderService(Db, Session, Numbering);
        foreach (var o in liveOrders)
        {
            var r = orders.CancelOrder(o.Id, $"تصحيح سلسلة الشحنة {ship.DocumentNumber} — {reason}");
            if (!r.Ok) return OpResult.Fail($"توقف المعالج عند الأمر {o.DocumentNumber}: {r.Message}\nأُنجز: {(done.Count == 0 ? "لا شيء" : string.Join("، ", done))}");
            done.Add($"أُلغي الأمر {o.DocumentNumber}");
        }

        var planIds = Db.ProductionPlanItems.AsNoTracking()
            .Where(pi => pi.ShipmentId == ship.Id || (pi.LotId != null && lotIds.Contains(pi.LotId.Value)))
            .Select(pi => pi.PlanId).Distinct().ToList();
        var plans = new PlanningService(Db, Session, Numbering);
        foreach (var pl in Db.ProductionPlans.AsNoTracking().Where(p => planIds.Contains(p.Id) && p.Status != DocStatuses.Cancelled).ToList())
        {
            if (pl.IsApproved)
            {
                var ru = plans.UnapprovePlan(pl.Id);
                if (!ru.Ok) return OpResult.Fail($"توقف المعالج عند الخطة {pl.DocumentNumber}: {ru.Message}\nأُنجز: {string.Join("، ", done)}");
                done.Add($"فُك اعتماد الخطة {pl.DocumentNumber}");
            }
            var rd = plans.DeletePlan(pl.Id);
            if (!rd.Ok) return OpResult.Fail($"توقف المعالج عند حذف الخطة {pl.DocumentNumber}: {rd.Message}\nأُنجز: {string.Join("، ", done)}");
            done.Add($"حُذفت الخطة {pl.DocumentNumber}");
        }

        var receiving = new ReceivingService(Db, Session, Numbering);
        var rs = receiving.UnapproveShipment(ship.Id, $"تصحيح سلسلة — {reason}");
        if (!rs.Ok) return OpResult.Fail($"توقف المعالج عند الشحنة: {rs.Message}\nأُنجز: {string.Join("، ", done)}");
        done.Add($"أُلغي اعتماد الشحنة {ship.DocumentNumber} (قيد عكسي)");

        return OpResult.Success("اكتمل تصحيح السلسلة بعكس البناء:\n- " + string.Join("\n- ", done)
            + "\nالسند الآن مسودة — صحّحه وأعد الاعتماد. كل خطوة مسجلة بالتدقيق مع السبب.", ship.Id, ship.DocumentNumber);
    }
}
