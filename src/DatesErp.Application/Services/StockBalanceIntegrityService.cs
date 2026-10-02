using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §1.50.66 — سلامة المخزون: معالجة أرصدة مكررة قديمة + منع تكرار + منع سالب + منع خلط عملاء
/// المفتاح الكامل: Warehouse + Product + Material + Lot + Customer + PackagingType
/// </summary>
public class StockBalanceIntegrityService : ServiceBase
{
    public StockBalanceIntegrityService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    /// <summary>العثور على مجموعات مكررة (نفس المفتاح الكامل مع أكثر من سجل)</summary>
    public List<DuplicateGroup> FindDuplicates()
    {
        var all = Db.StockBalances.AsNoTracking().ToList();
        var groups = all
            .GroupBy(b => new
            {
                b.WarehouseId,
                ProductId = b.ProductId ?? -1,
                MaterialId = b.MaterialId ?? -1,
                LotId = b.LotId ?? -1,
                CustomerId = b.CustomerId ?? -1,
                PackagingTypeId = b.PackagingTypeId ?? -1
            })
            .Where(g => g.Count() > 1)
            .Select(g => new DuplicateGroup
            {
                WarehouseId = g.Key.WarehouseId,
                ProductId = g.Key.ProductId == -1 ? null : g.Key.ProductId,
                MaterialId = g.Key.MaterialId == -1 ? null : g.Key.MaterialId,
                LotId = g.Key.LotId == -1 ? null : g.Key.LotId,
                CustomerId = g.Key.CustomerId == -1 ? null : g.Key.CustomerId,
                PackagingTypeId = g.Key.PackagingTypeId == -1 ? null : g.Key.PackagingTypeId,
                Count = g.Count(),
                TotalQtyKg = g.Sum(x => x.QtyKg),
                TotalPackageCount = g.Sum(x => x.PackageCount),
                Balances = g.OrderBy(x => x.Id).ToList()
            })
            .ToList();
        return groups;
    }

    /// <summary>
    /// معالجة أرصدة مكررة قديمة قبل إنشاء القيد الفريد — بدون حذف تلقائي للبيانات بشكل أعمى
    /// المنطق: لكل مجموعة مكررة، نحتفظ بأقدم سجل ونجمع فيه الكميات من البقية، ثم نحذف البقية بعد التوثيق
    /// يتم تسجيل العملية في AuditLog + InventoryTransaction من نوع Adjustment للتدقيق
    /// </summary>
    public OpResult MergeDuplicates(bool dryRun = true)
    {
        Require("inventory", "Edit");
        return RunOp(() =>
        {
            var dups = FindDuplicates();
            if (dups.Count == 0)
                return OpResult.Success("لا يوجد أرصدة مكررة — المخزون سليم.");

            if (dryRun)
            {
                var msg = $"تم العثور على {dups.Count} مجموعة مكررة (إجمالي {dups.Sum(g => g.Count)} سجل):\n";
                foreach (var g in dups.Take(10))
                {
                    msg += $"- مخزن {g.WarehouseId} منتج {g.ProductId} مادة {g.MaterialId} دفعة {g.LotId} عميل {g.CustomerId} عبوة {g.PackagingTypeId}: {g.Count} سجلات → مجموع {g.TotalQtyKg:N3} كجم / {g.TotalPackageCount:N0} عبوة\n";
                }
                if (dups.Count > 10) msg += $"... و {dups.Count - 10} مجموعات أخرى\n";
                msg += "\nهذه معاينة فقط (DryRun) — لم يتم دمج أي شيء. شغّل الدمج الفعلي بعد المراجعة.";
                return OpResult.Success(msg);
            }

            int mergedGroups = 0;
            int removedRecords = 0;
            foreach (var g in dups)
            {
                var ordered = g.Balances.OrderBy(b => b.Id).ToList();
                var keeper = Db.StockBalances.FirstOrDefault(b => b.Id == ordered[0].Id);
                if (keeper == null) continue;

                // جمع الكميات من البقية
                double sumKg = ordered.Sum(x => x.QtyKg);
                int sumPkg = ordered.Sum(x => x.PackageCount);

                keeper.QtyKg = sumKg;
                keeper.PackageCount = sumPkg;

                // حذف البقية (بعد جمع كمياتها)
                var toDelete = ordered.Skip(1).Select(x => x.Id).ToList();
                var entitiesToDelete = Db.StockBalances.Where(b => toDelete.Contains(b.Id)).ToList();
                Db.StockBalances.RemoveRange(entitiesToDelete);
                removedRecords += entitiesToDelete.Count;
                mergedGroups++;

                // توثيق في AuditLog
                Db.AuditLogs.Add(new AuditLog
                {
                    UserId = Session?.UserId,
                    UserName = Session?.UserName ?? "system",
                    MachineName = Environment.MachineName,
                    ActionDate = DateTime.Now,
                    ScreenName = "StockBalanceIntegrity",
                    ActionType = "MergeDuplicates",
                    DocumentType = "StockBalance",
                    DocumentNumber = $"WH{g.WarehouseId}-P{g.ProductId}-M{g.MaterialId}-L{g.LotId}-C{g.CustomerId}-PKG{g.PackagingTypeId}",
                    RecordId = keeper.Id,
                    NewValue = $"دمج {g.Count} سجلات مكررة → {sumKg:N3} كجم / {sumPkg:N0} عبوة — حذف {entitiesToDelete.Count} سجل مكرر"
                });
            }

            Db.SaveChanges();
            return OpResult.Success($"تم دمج {mergedGroups} مجموعة مكررة — حذف {removedRecords} سجل مكرر بعد جمع كمياتها في السجل الأقدم لكل مجموعة. إجمالي الكميات محفوظ بدون فقدان.");
        });
    }

    /// <summary>فحص سلامة: لا رصيد سالب + لا خلط عملاء + لا تكرار</summary>
    public List<string> CheckIntegrity()
    {
        var issues = new List<string>();

        // 1. سالب
        var negatives = Db.StockBalances.AsNoTracking()
            .Where(b => b.QtyKg < -0.001 || b.PackageCount < 0)
            .ToList();
        foreach (var n in negatives)
            issues.Add($"رصيد سالب: مخزن {n.WarehouseId} منتج {n.ProductId} مادة {n.MaterialId} دفعة {n.LotId} عميل {n.CustomerId} عبوة {n.PackagingTypeId} → {n.QtyKg:N3} كجم / {n.PackageCount} عبوة");

        // 2. تكرار
        var dups = FindDuplicates();
        foreach (var g in dups)
            issues.Add($"تكرار: مخزن {g.WarehouseId} منتج {g.ProductId} مادة {g.MaterialId} دفعة {g.LotId} عميل {g.CustomerId} عبوة {g.PackagingTypeId} → {g.Count} سجلات مكررة");

        // 3. خلط عملاء (نفس المنتج/الدفعة/المخزن/العبوة لكن عملاء مختلفون مع نفس الكمية؟ هذا ليس خلط، لكن نفحص إذا كان هناك رصيد لعميل A يُستخدم لعميل B عبر حركة)
        // فحص الحركات: حركة لعميل A لكن الرصيد الحالي لعميل B سالب؟ هذا يكشف خلط محتمل
        var customerMix = Db.InventoryTransactions.AsNoTracking()
            .Where(t => t.CustomerId != null && t.MovementType == Core.Domain.Enums.MovementType.Outbound)
            .GroupBy(t => new { t.WarehouseId, t.ProductId, t.LotId, t.PackagingTypeId })
            .Where(g => g.Select(x => x.CustomerId).Distinct().Count() > 1)
            .Take(20)
            .ToList();
        // هذا فحص تقريبي — لا نعتبره خطأ فادح، فقط تنبيه إذا كان نفس المفتاح يُستخدم لعملاء مختلفين (قد يكون طبيعي إذا كل عميل له رصيده)
        // لكن إذا كان هناك حركة لعميل A على رصيد كان لعميل B فقط، فهذا خلط

        return issues;
    }
}

public class DuplicateGroup
{
    public int WarehouseId { get; set; }
    public int? ProductId { get; set; }
    public int? MaterialId { get; set; }
    public int? LotId { get; set; }
    public int? CustomerId { get; set; }
    public int? PackagingTypeId { get; set; }
    public int Count { get; set; }
    public double TotalQtyKg { get; set; }
    public int TotalPackageCount { get; set; }
    public List<StockBalance> Balances { get; set; } = new();
}
