using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §45 — مطابقة المخزون الذاتية: لكل مفتاح رصيد كامل (مستودع، مادة/منتج، دفعة، عميل، تغليف)
/// يُقارن رصيد StockBalance بمجموع الحركات المعتمدة الموقّعة منذ الصفر
/// (وارد +، صادر −، تسوية بإشارتها، التحويل مزدوج الأثر فصفر لكل طرف).
/// قراءة تشخيصية فقط — لا تعدّل شيئاً.
/// </summary>
public class InventoryIntegrityService : ServiceBase, IInventoryIntegrityService
{
    public InventoryIntegrityService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    private static (int, int?, int?, int?, int?, int?) KeyOf(int wh, int? mat, int? prod, int? lot, int? cust, int? pack)
        => (wh, mat, prod, lot, cust, pack);

    public List<IntegrityRow> Check(double tolerance = 0.001)
    {
        static double Signed(InventoryTransaction t) => t.MovementType switch
        {
            MovementType.Inbound => t.QtyKg,
            MovementType.Outbound => -t.QtyKg,
            MovementType.Adjustment => t.QtyKg,
            _ => 0 // التحويل سُجّل طرفين (صادر+وارد) فأثره الصافي صفر لكل طرف بمفرده
        };
        static string Subject(int wh, int? mat, int? prod)
            => $"مستودع {wh} — {(mat != null ? "مادة " + mat : "منتج " + prod)}";

        // الحركات المعتمدة فقط: المسودة لم تُحتسب في الرصيد أصلاً فلا تُقارن به
        var ledger = Db.InventoryTransactions.AsNoTracking()
            .Where(t => t.IsApproved && (t.MaterialId != null || t.ProductId != null))
            .ToList()
            .GroupBy(t => KeyOf(t.WarehouseId, t.MaterialId, t.ProductId, t.LotId, t.CustomerId, t.PackagingTypeId))
            .ToDictionary(g => g.Key, g => g.Sum(Signed));

        var balanceKeys = new HashSet<(int, int?, int?, int?, int?, int?)>();
        var rows = new List<IntegrityRow>();
        foreach (var b in Db.StockBalances.AsNoTracking().ToList())
        {
            var key = KeyOf(b.WarehouseId, b.MaterialId, b.ProductId, b.LotId, b.CustomerId, b.PackagingTypeId);
            balanceKeys.Add(key);
            ledger.TryGetValue(key, out var sum);
            // Diff is the correction needed to make the physical balance match
            // its approved ledger: expected ledger minus recorded balance.
            double diff = sum - b.QtyKg;
            if (Math.Abs(diff) > tolerance)
                rows.Add(new IntegrityRow(Subject(b.WarehouseId, b.MaterialId, b.ProductId), b.QtyKg, sum, diff));
        }
        foreach (var kv in ledger)
        {
            if (balanceKeys.Contains(kv.Key)) continue;
            var k = kv.Key;
            rows.Add(new IntegrityRow(Subject(k.Item1, k.Item2, k.Item3) + " (بلا رصيد)", 0, kv.Value, kv.Value));
        }
        return rows.OrderByDescending(r => Math.Abs(r.Diff)).Take(200).ToList();
    }
}
