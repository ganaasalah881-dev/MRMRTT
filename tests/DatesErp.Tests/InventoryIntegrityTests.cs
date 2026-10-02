using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §45 — فحص مطابقة المخزون الذاتي: رصيد متفق مع دفتره لا يُبلغ،
/// ورصيد منحرف يُبلغ بفرقه، والحركات غير المعتمدة تُتجاهل.
/// </summary>
public class InventoryIntegrityTests
{
    private static DatesErpDbContext FreshDb(TestHost host)
        => new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);

    [Fact]
    public void Integrity_Consistent_Balance_Not_Reported_Corrupted_Reported_With_Diff()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var db = FreshDb(host);
        int wh = db.Warehouses.Select(w => w.Id).First();
        const int mat = 777777; // مفتاح اختبار معزول: لا مادة حقيقية بهذا الرقم في البذر
        var svc = new InventoryIntegrityService(db, null, null);
        bool KeyRow(InventoryIntegrityService s, out double diff)
        {
            var r = s.Check().FirstOrDefault(x => x.Subject.Contains($"مستودع {wh}") && x.Subject.Contains($"مادة {mat}")
                && !x.Subject.Contains("(بلا رصيد)"));
            diff = r?.Diff ?? 0; return r != null;
        }

        // حركة معتمدة +50 ورصيد مطابق → لا فرق على هذا المفتاح
        double before = db.StockBalances.Where(b => b.WarehouseId == wh && b.MaterialId == mat).Sum(b => b.QtyKg);
        db.InventoryTransactions.Add(new InventoryTransaction
        {
            TxnNumber = "INT-45-1", WarehouseId = wh, MaterialId = mat,
            MovementType = MovementType.Inbound, QtyKg = 50, IsApproved = true,
            ReferenceDocType = ReferenceDocType.Adjustment, ReferenceDocNumber = "INT-45-1"
        });
        var bal = db.StockBalances.FirstOrDefault(b => b.WarehouseId == wh && b.MaterialId == mat);
        if (bal == null) db.StockBalances.Add(new StockBalance { WarehouseId = wh, MaterialId = mat, QtyKg = before + 50 });
        else bal.QtyKg += 50;
        db.SaveChanges();
        Assert.False(KeyRow(svc, out _), "رصيد مطابق لدفتره يجب ألا يُبلَّغ");

        // حركة غير معتمدة +1000 → تُتجاهل ولا تخلق فرقاً
        db.InventoryTransactions.Add(new InventoryTransaction
        {
            TxnNumber = "INT-45-2", WarehouseId = wh, MaterialId = mat,
            MovementType = MovementType.Inbound, QtyKg = 1000, IsApproved = false,
            ReferenceDocType = ReferenceDocType.Adjustment, ReferenceDocNumber = "INT-45-2"
        });
        db.SaveChanges();
        Assert.False(KeyRow(svc, out _), "الحركات غير المعتمدة لا تدخل المطابقة");

        // إفساد الرصيد −10 → يُبلَّغ بفرق +10
        bal = db.StockBalances.First(b => b.WarehouseId == wh && b.MaterialId == mat);
        bal.QtyKg -= 10;
        db.SaveChanges();
        Assert.True(KeyRow(svc, out double diff), "الرصيد المنحرف يجب أن يُبلَّغ");
        Assert.True(Math.Abs(diff - 10) < 0.001, $"الفرق المبلَّغ يجب أن يكون 10 وكان {diff}");
    }
}
