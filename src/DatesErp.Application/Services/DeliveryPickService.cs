// ═══════════════ §أمر شاشة تسليم المنتج التام — قائمة الالتقاط والتوزيع FIFO ═══════════════
// الغرض: عند اختيار العميل يعرض النظام مخزونه من التام فقط (المنتجات الناقصة/التامة — بدون الخام
// وبدون النواتج الجانبية)، بصفوف مكتملة التتبع: كود الصنف، الصفة، وزن الكرتون، الرصيد المتاح،
// تاريخ استلام التام، تاريخ الإنتاج، مدة البقاء (محسوبة لحظياً — لا تُخزَّن أبداً)، رقم الخطة،
// ورقم الأمر. الترتيب الافتراضي FIFO: الأقدم استلاماً أولاً ثم الأقدم إنتاجاً.
// التوزيع: أي كمية مطلوبة ≤ المتاح تُوزَّع آلياً عبر دفعات الصنف دون دمج أو فقدان تتبع —
// الطلب الأكبر من دفعة واحدة يُقسَّم على الدفعات (مثال الأمر: 500 ← 300 + 200 من دفعتين).
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>صف واحد في قائمة الالتقاط = رصيد (دفعة × صنف × عبوة) لعميل واحد بمخزن التام.</summary>
public sealed record DeliveryPickRow(
    int ProductId, string ProductCode, string ProductName, string GradeAr,
    double CartonWeightKg, int AvailableCartons, double AvailableKg,
    string FgReceiptDate, string ProductionDate, int StorageDays,
    string PlanNo, string OrderNo, int? LotId, string LotCode,
    string PackName, int? PackagingTypeId);

public class DeliveryPickService
{
    private readonly DatesErpDbContext _db;
    public DeliveryPickService(DatesErpDbContext db) { _db = db; }

    /// <summary>§1 — قائمة رصيد العميل القابل للتسليم، مرتبة FIFO (§2)، مع مدة بقاء محسوبة لحظياً (§6).</summary>
    public List<DeliveryPickRow> GetPickList(int customerId, DateTime asOf)
    {
        int whFg = _db.Warehouses.AsNoTracking().Where(w => w.WarehouseCode == "WFG").Select(w => w.Id).FirstOrDefault();
        if (whFg == 0) return new();

        var balances = _db.StockBalances.AsNoTracking()
            .Where(b => b.WarehouseId == whFg && b.CustomerId == customerId
                        && (b.PackageCount > 0 || b.QtyKg > 0.001))
            .ToList();
        if (balances.Count == 0) return new();

        var prodIds = balances.Where(b => b.ProductId != null).Select(b => b.ProductId!.Value).Distinct().ToList();
        // §12 — المنتجات التامة فقط: النواتج الجانبية (كجم، بلا قاعدة كرتون) لا تدخل قائمة التسليم.
        var products = _db.Products.AsNoTracking().Where(p => prodIds.Contains(p.Id)).ToList()
            .Where(p => string.Equals(p.ItemType, "Finished", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(p => p.Id);

        var lotIds = balances.Where(b => b.LotId != null).Select(b => b.LotId!.Value).Distinct().ToList();
        var lots = _db.Lots.AsNoTracking().Where(l => lotIds.Contains(l.Id)).ToDictionary(l => l.Id, l => l.LotCode);
        var packIds = balances.Where(b => b.PackagingTypeId != null).Select(b => b.PackagingTypeId!.Value).Distinct().ToList();
        var packs = _db.PackagingTypes.AsNoTracking().Where(p => packIds.Contains(p.Id)).ToDictionary(p => p.Id, p => p.PackageNameAr);

        // تاريخ استلام التام = تاريخ أقدم سند استلام إنتاج تام معتمد للدفعة×الصنف (تاريخ المستند لا تاريخ الحركة).
        // §1.50.72 P2-1: السند المستلم يكون Status = "Completed" (لا "Approved" أبداً —
        // كانت الشرطة القديمة لا تطابق شيئاً، فعمودا التاريخ/مدة البقاء دائماً «—» وFIFO مكسور).
        // IsApproved هو العلم الفعلي الذي تضعه الخدمة عند الاستلام (ويعكسه الإلغاء).
        var fgIn = (from ri in _db.FinishedGoodsReceiptItems.AsNoTracking()
                    join r in _db.FinishedGoodsReceipts.AsNoTracking() on ri.ReceiptId equals r.Id
                    where r.WarehouseId == whFg && r.IsApproved
                          && ri.LotId != null && lotIds.Contains(ri.LotId.Value)
                    group r by new { ri.LotId, ri.ProductId } into g
                    select new { g.Key.LotId, g.Key.ProductId, First = g.Min(x => x.DeliveryDate) })
            .ToList()
            .Where(x => x.First != null)
            .ToDictionary(x => (x.LotId!.Value, x.ProductId), x => x.First!.Value);

        // أمر/خطة/إنتاج/جودة لكل دفعة — سلسلة التتبع الكاملة (§11).
        var orderRefs = (from oi in _db.ProductionOrderItems.AsNoTracking()
                         join o in _db.ProductionOrders.AsNoTracking() on oi.OrderId equals o.Id
                         where oi.LotId != null && lotIds.Contains(oi.LotId.Value)
                         select new { oi.LotId, o.Id, o.DocumentNumber, o.SourcePlanId, o.ProductionDate }).ToList();
        var orderIds = orderRefs.Select(r => r.Id).Distinct().ToList();
        var planIds = orderRefs.Where(r => r.SourcePlanId != null).Select(r => r.SourcePlanId!.Value).Distinct().ToList();
        var plans = _db.ProductionPlans.AsNoTracking()
            .Where(p => planIds.Contains(p.Id))
            .ToDictionary(p => p.Id, p => p.DocumentNumber);
        var exeFirst = _db.ProductionExecutions.AsNoTracking()
            .Where(e => orderIds.Contains(e.OrderId))
            .GroupBy(e => e.OrderId)
            .Select(g => new { g.Key, First = g.Min(e => e.StartDateTime) })
            .ToList().ToDictionary(x => x.Key, x => x.First);
        // الصفة/الدرجة = قرار الفحص النهائي المعتمد للأمر (سليم/مقبول… ) — وإلا «بانتظار الفحص».
        // §1.50.72 P2-2: التطابق الصارم CheckType == "نهائي" لم ينطبق على أي قيمة فعلية
        // ("نهائي — بعد التبريد (يومان)" و"نهائي — بعد التبريد") فكانت الصفة «بانتظار الفحص»
        // دائماً. الفلترة الآن بالاعتماد الفعلي + نوع يحتوي «نهائي» (يغطي كل المتغيرات).
        var decisions = _db.QualityChecks.AsNoTracking()
            .Where(c => c.OrderId != null && orderIds.Contains(c.OrderId.Value)
                        && c.IsApproved
                        && (c.CheckType == null || c.CheckType.Contains("نهائي")))
            .GroupBy(c => c.OrderId)
            .Select(g => new { g.Key, Dec = g.Max(c => c.Decision) })
            .ToList().ToDictionary(x => x.Key!.Value, x => x.Dec);

        var rows = new List<DeliveryPickRow>();
        foreach (var b in balances)
        {
            if (b.ProductId == null || !products.TryGetValue(b.ProductId.Value, out var prod)) continue;
            var refLot = orderRefs.FirstOrDefault(r => r.LotId == b.LotId);
            var fgDate = b.LotId != null && fgIn.TryGetValue((b.LotId.Value, b.ProductId.Value), out var d) ? d : (DateTime?)null;
            DateTime? prodDate = null;
            if (refLot != null)
                prodDate = exeFirst.TryGetValue(refLot.Id, out var ex) ? ex : refLot.ProductionDate;
            var grade = refLot != null && decisions.TryGetValue(refLot.Id, out var dec)
                ? (dec == "Passed" ? "سليم (مطابق)" : dec == "Quarantine" ? "محجوز" : "مرفوض")
                : "بانتظار الفحص";
            double ctnW = UnitsPolicy.CartonWeight(_db, prod.Id, b.PackagingTypeId);
            int cartons = b.PackageCount > 0 ? b.PackageCount : (int)Math.Round(UnitsPolicy.CartonsOf(b.QtyKg, ctnW));
            rows.Add(new DeliveryPickRow(
                prod.Id, prod.ProductCode ?? "", prod.ProductNameAr ?? "", grade,
                ctnW, cartons, b.QtyKg,
                fgDate?.ToString("dd/MM/yyyy") ?? "—",
                prodDate?.ToString("dd/MM/yyyy") ?? "—",
                fgDate.HasValue ? Math.Max(0, (asOf.Date - fgDate.Value.Date).Days) : 0,
                refLot?.SourcePlanId != null && plans.TryGetValue(refLot.SourcePlanId.Value, out var planNo) ? planNo : "—",
                refLot?.DocumentNumber ?? "—",
                b.LotId, b.LotId != null && lots.TryGetValue(b.LotId.Value, out var lc) ? lc : "—",
                b.PackagingTypeId != null && packs.TryGetValue(b.PackagingTypeId.Value, out var pn) ? pn : "—",
                b.PackagingTypeId));
        }
        // §2 — FIFO: الأقدم استلاماً بالتام أولاً، ثم الأقدم إنتاجاً، ثم الدفعة (ترتيب مستقر واضح).
        return rows
            .OrderBy(r => ParseDate(r.FgReceiptDate))
            .ThenBy(r => ParseDate(r.ProductionDate))
            .ThenBy(r => r.LotCode)
            .ToList();
    }

    /// <summary>§3/§4/§5 — توزيع الطلبات بالكرتون توزيعاً آلياً FIFO عبر دفعات كل صنف.
    /// أي طلب ≤ المتاح يُنفَّذ (كلي أو جزئي)؛ الطلب الزائد يُرفض فوراً برسالة الأمر حرفياً.
    /// كل سطر ناتج يحمل LotId الخاص بدفعته — لا دمج ولا فقدان تتبع إطلاقاً.
    /// §1.50.66 — التوزيع يحترم PackagingTypeId كجزء من مفتاح الرصيد: 4كجم لا يخلط مع 8كجم.</summary>
    public List<CustomerDeliveryItemDto> AllocateFifo(int customerId, List<(int ProductId, int Cartons)> requests, DateTime asOf)
    {
        var list = GetPickList(customerId, asOf);
        var result = new List<CustomerDeliveryItemDto>();
        foreach (var (pid, cartons) in requests)
        {
            if (cartons <= 0)
                throw new DomainException($"الكمية المطلوبة يجب أن تكون أكبر من صفر. الصنف رقم {pid}: المطلوب {cartons}.", "INVALID_QTY");
            var productRows = list.Where(r => r.ProductId == pid).ToList(); // مرتبة FIFO بالفعل — لكن نحترم العبوة: كل رصيد عبوة منفصل

            int remaining = cartons;
            foreach (var row in productRows)
            {
                if (remaining <= 0) break;
                int take = Math.Min(remaining, row.AvailableCartons);
                if (take <= 0) continue;
                result.Add(new CustomerDeliveryItemDto
                {
                    ProductId = pid,
                    LotId = row.LotId,
                    PackagingTypeId = row.PackagingTypeId,
                    PackageCount = take,
                    QtyKg = Math.Round(UnitsPolicy.KgOfCartons(take, row.CartonWeightKg), 1)
                });
                remaining -= take;
            }
            if (remaining > 0)
            {
                string name = productRows.Select(r => r.ProductName).FirstOrDefault() ?? $"#{pid}";
                int avail = productRows.Sum(r => r.AvailableCartons);
                throw new DomainException(
                    $"الكمية المطلوبة أكبر من الرصيد المتاح.\nالصنف «{name}»: المتاح {avail} كرتون — المطلوب {cartons} كرتون.",
                    "INSUFFICIENT_CUSTOMER_BALANCE");
            }
        }
        return result;
    }

    private static DateTime ParseDate(string s) =>
        DateTime.TryParseExact(s, "dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : DateTime.MaxValue;
}
