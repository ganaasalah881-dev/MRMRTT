using DatesErp.Core.Domain.Enums;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>سطر تقرير حركة الموردين: الأصناف والأعمدة الثمانية + المتبقي.</summary>
public sealed record SmRow(string Code, string Name, string Unit,
    double Open, double Purch, double SupIn, double PurchRet, double Issue, double Sales, double SalesRet, double Remain);

/// <summary>مجموعة مورد واحد: التعريف + أسطر الأصناف + إجماليات الأعمدة الثمانية.</summary>
public sealed record SmGroup(string Code, string Name, List<SmRow> Rows, double[] Totals);

/// <summary>
/// §B103 — حسابات تقرير حركة الموردين حسب الأصناف (تحليلي كميات) في طبقة التطبيق:
/// قابلة للاختبار Headless ومصدر واحد للشاشة والطباعة معاً.
/// ربط البيانات بحركات المخزون المعتمدة (لا حركة بدون مستند §9):
/// المشتريات = استلامات التمور · التوريد المخزني = وارد الإنتاج/التام/الإفراج + التسويات ·
/// مردود المشتريات = مرتجعات مواد صادرة · الصرف المخزني = صرف المواد والاستهلاك ·
/// المبيعات = تسليمات العملاء وبيع الكرتون · مردود المبيعات = مرتجعات عملاء واردة ·
/// الرصيد الافتتاح = صافي ما قبل الفترة لكل (مورد، صنف). الكمية بوحدة الصنف: كجم ← الوزن وإلا العبوات.
/// </summary>
public class SupplierMovementService
{
    private readonly DatesErpDbContext _db;
    public SupplierMovementService(DatesErpDbContext db) { _db = db; }

    /// <summary>الأطراف الذين لهم حركات مخزون (دور العميل المورد) — لقائمة الفلتر.</summary>
    public List<(int Id, string Code, string Name)> GetParties()
    {
        var ids = _db.InventoryTransactions.AsNoTracking().Where(t => t.CustomerId != null)
            .Select(t => t.CustomerId).Distinct().ToList();
        // §1.50.66 — إصلاح Tuple داخل Expression Tree: AsEnumerable قبل إنشاء ValueTuple
        return _db.Customers.AsNoTracking().Where(c => ids.Contains(c.Id))
            .OrderBy(c => c.CustomerCode)
            .AsEnumerable()
            .Select(c => (c.Id, c.CustomerCode, c.CustomerName)).ToList();
    }

    /// <summary>الكمية بوحدة الصنف: أصناف الوزن بالكيلو وما عداها بعدد العبوات.</summary>
    private static double QtyOf(Core.Domain.Entities.InventoryTransaction t, string unit)
        => unit != null && unit.Contains("كجم") ? t.QtyKg : t.PackageCount;

    private static double Signed(Core.Domain.Entities.InventoryTransaction t, string unit)
    {
        double q = QtyOf(t, unit);
        return t.MovementType == MovementType.Inbound ? q
            : t.MovementType == MovementType.Outbound ? -q
            : t.MovementType == MovementType.Adjustment ? q : 0; // التحويل بين مخازن لا يغيّر رصيد الشركة
    }

    public List<SmGroup> Compute(DateTime from, DateTime to, int? partyId, string search = null)
    {
        var toEx = to.Date.AddDays(1);
        search = (search ?? "").Trim();
        var txns = _db.InventoryTransactions.AsNoTracking()
            .Where(t => t.IsApproved && t.TxnDate < toEx && (partyId == null || t.CustomerId == partyId))
            .ToList();
        if (txns.Count == 0) return new List<SmGroup>();

        var partyIds = txns.Where(t => t.CustomerId != null).Select(t => t.CustomerId!.Value).Distinct().ToList();
        // §1.50.66 — إصلاح Tuple داخل Expression Tree: AsEnumerable قبل إنشاء ValueTuple
        var customers = _db.Customers.AsNoTracking().Where(c => partyIds.Contains(c.Id))
            .AsEnumerable()
            .ToDictionary(c => c.Id, c => (CustomerCode: c.CustomerCode, CustomerName: c.CustomerName));
        var prodIds = txns.Where(t => t.ProductId != null).Select(t => t.ProductId!.Value).Distinct().ToList();
        var products = _db.Products.AsNoTracking().Where(p => prodIds.Contains(p.Id))
            .AsEnumerable()
            .ToDictionary(p => p.Id, p => (p.ProductCode, p.ProductNameAr, p.UnitOfMeasure));
        var matIds = txns.Where(t => t.ProductId == null && t.MaterialId != null).Select(t => t.MaterialId!.Value).Distinct().ToList();
        var aux = _db.AuxiliaryMaterials.AsNoTracking().Where(a => matIds.Contains(a.Id))
            .AsEnumerable()
            .ToDictionary(a => a.Id, a => (a.MaterialCode, a.MaterialNameAr, a.UnitOfMeasure));

        var byPartyItem = new Dictionary<int, Dictionary<string, double[]>>();
        var itemMeta = new Dictionary<string, (string Code, string Name, string Unit)>();
        foreach (var t in txns)
        {
            if (t.CustomerId == null) continue;
            string key; string unit;
            if (t.ProductId != null)
            {
                if (!products.TryGetValue(t.ProductId.Value, out var pr)) continue;
                key = "P" + t.ProductId.Value; unit = pr.UnitOfMeasure;
                itemMeta.TryAdd(key, (pr.ProductCode, pr.ProductNameAr, pr.UnitOfMeasure));
            }
            else if (t.MaterialId != null)
            {
                if (!aux.TryGetValue(t.MaterialId.Value, out var am)) continue;
                key = "M" + t.MaterialId.Value; unit = am.UnitOfMeasure;
                itemMeta.TryAdd(key, (am.MaterialCode, am.MaterialNameAr, am.UnitOfMeasure));
            }
            else continue;

            if (!byPartyItem.TryGetValue(t.CustomerId.Value, out var items))
                byPartyItem[t.CustomerId.Value] = items = new Dictionary<string, double[]>();
            if (!items.TryGetValue(key, out var b)) items[key] = b = new double[8];
            // [0] افتتاح [1] مشتريات [2] توريد مخزني [3] مردود مشتريات [4] صرف [5] مبيعات [6] مردود مبيعات [7] متبقٍ
            if (t.TxnDate < from) { b[0] += Signed(t, unit); continue; }
            switch (t.ReferenceDocType)
            {
                case ReferenceDocType.ShipmentReceipt when t.MovementType == MovementType.Inbound:
                    b[1] += QtyOf(t, unit); break;
                case ReferenceDocType.FinishedGoodsReceipt when t.MovementType == MovementType.Inbound:
                case ReferenceDocType.ProductionExecution when t.MovementType == MovementType.Inbound:
                case ReferenceDocType.TreatmentRelease when t.MovementType == MovementType.Inbound:
                    b[2] += QtyOf(t, unit); break;
                case ReferenceDocType.Adjustment:
                    b[2] += Signed(t, unit); break;
                case ReferenceDocType.Return when t.MovementType == MovementType.Outbound:
                case ReferenceDocType.MaterialReturn when t.MovementType == MovementType.Outbound:
                    b[3] += QtyOf(t, unit); break;
                case ReferenceDocType.MaterialIssue when t.MovementType == MovementType.Outbound:
                case ReferenceDocType.ProductionExecution when t.MovementType == MovementType.Outbound:
                case ReferenceDocType.TreatmentStart when t.MovementType == MovementType.Outbound:
                    b[4] += QtyOf(t, unit); break;
                case ReferenceDocType.CustomerDelivery when t.MovementType == MovementType.Outbound:
                case ReferenceDocType.CartonSale when t.MovementType == MovementType.Outbound:
                    b[5] += QtyOf(t, unit); break;
                case ReferenceDocType.Return when t.MovementType == MovementType.Inbound:
                    b[6] += QtyOf(t, unit); break;
            }
        }

        var groups = new List<SmGroup>();
        foreach (var (pid, items) in byPartyItem.OrderBy(kv => customers.TryGetValue(kv.Key, out var c) ? c.CustomerCode : kv.Key.ToString()))
        {
            var rows = new List<SmRow>();
            foreach (var (key, b) in items.OrderBy(kv => itemMeta.TryGetValue(kv.Key, out var im) ? im.Code : kv.Key))
            {
                double remain = b[0] + b[1] + b[2] - b[3] - b[4] - b[5] + b[6];
                if (b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0 && b[4] == 0 && b[5] == 0 && b[6] == 0) continue;
                var im2 = itemMeta[key];
                if (search.Length > 0 && !im2.Code.Contains(search, StringComparison.OrdinalIgnoreCase)
                    && !(im2.Name ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                rows.Add(new SmRow(im2.Code, im2.Name, im2.Unit, b[0], b[1], b[2], b[3], b[4], b[5], b[6], remain));
            }
            if (rows.Count == 0) continue;
            var totals = new double[8];
            for (int i = 0; i < 7; i++) totals[i] = rows.Sum(r => i switch { 0 => r.Open, 1 => r.Purch, 2 => r.SupIn, 3 => r.PurchRet, 4 => r.Issue, 5 => r.Sales, _ => r.SalesRet });
            totals[7] = rows.Sum(r => r.Remain);
            var cc = customers.TryGetValue(pid, out var cu) ? cu : (CustomerCode: "?", CustomerName: "?");
            groups.Add(new SmGroup(cc.CustomerCode ?? "?", cc.CustomerName ?? "?", rows, totals));
        }
        return groups;
    }
}
