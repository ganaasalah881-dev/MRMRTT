using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B10 خطة قبول دورة الكرتون الفارغ:
/// 1) التولّد الآلي يورد الكراتين للمخزن بحركة CartonReturn.
/// 2) العدّ الفعلي يقيّد الفرق تسوية ويعدل الرصيد.
/// 3) البيع يخصم الرصيد ويرفض البيع فوقه ويحسب القيمة.
/// 4) معادلة التسوية: متولّد − مبيع ± فروقات = الرصيد.
/// </summary>
public class CartonManagementTests
{
    private static CartonService Svc(TestHost host)
        => new(host.Services.CreateScope().ServiceProvider.GetRequiredService<DatesErpDbContext>(),
               host.Services.GetRequiredService<ICurrentSession>(),
               host.Services.CreateScope().ServiceProvider.GetRequiredService<INumberingService>());

    private static DatesErpDbContext Db(TestHost host)
        => new(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);

    private static (int pid, int wid) Ids(TestHost host)
    {
        using var db = Db(host);
        return (db.Products.First(p => p.GroupCode == "004").Id,
                db.Warehouses.First(w => w.WarehouseCode == "WPK").Id);
    }

    [Fact]
    public void Auto_Post_Adds_Cartons_With_CartonReturn_Txn()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (pid, wid) = Ids(host);
        Svc(host).PostEmptyCartons(750, "CL-TEST-1");
        using var db = Db(host);
        var bal = db.StockBalances.First(b => b.ProductId == pid && b.WarehouseId == wid);
        Assert.Equal(750, bal.PackageCount);
        Assert.Contains(db.InventoryTransactions.ToList(),
            t => t.ReferenceDocType == ReferenceDocType.CartonReturn && t.PackageCount == 750 && t.ReferenceDocNumber == "CL-TEST-1");
    }

    [Fact]
    public void Count_Doc_Posts_Variance_And_Adjusts_Balance()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (pid, wid) = Ids(host);
        Svc(host).PostEmptyCartons(750, "CL-1");
        var r = Svc(host).CreateCountDoc(wid, "30/08/2026", "عدّ شهري", new List<(int, int)> { (pid, 740) });
        Assert.True(r.Ok, r.Message);
        using var db = Db(host);
        Assert.Equal(740, db.StockBalances.First(b => b.ProductId == pid && b.WarehouseId == wid).PackageCount);
        Assert.Contains(db.InventoryTransactions.ToList(),
            t => t.ReferenceDocType == ReferenceDocType.CartonCount && t.PackageCount == -10);
    }

    [Fact]
    public void Sale_Deducts_Computes_Value_And_Rejects_Over_Balance()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (pid, wid) = Ids(host);
        Svc(host).PostEmptyCartons(750, "CL-1");
        var ok = Svc(host).CreateSaleDoc(null, wid, 2.5, null, new List<(int, int)> { (pid, 100) });
        Assert.True(ok.Ok, ok.Message);
        using var db = Db(host);
        Assert.Equal(650, db.StockBalances.First(b => b.ProductId == pid && b.WarehouseId == wid).PackageCount);
        var doc = db.CartonSaleDocs.OrderByDescending(d => d.Id).First();
        Assert.Equal(250, doc.TotalAmount, 2);
        var over = Svc(host).CreateSaleDoc(null, wid, 2.5, null, new List<(int, int)> { (pid, 100000) });
        Assert.False(over.Ok); // البيع فوق الرصيد مرفوض
    }

    [Fact]
    public void Statement_Equation_Holds()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (pid, wid) = Ids(host);
        var svc = Svc(host);
        svc.PostEmptyCartons(750, "CL-1");
        svc.CreateCountDoc(wid, "30/08/2026", null, new List<(int, int)> { (pid, 740) });
        svc.CreateSaleDoc(null, wid, 2, null, new List<(int, int)> { (pid, 100) });
        var (inb, sold, adj) = svc.StatementTotals(null, null, null);
        Assert.Equal(750, inb);
        Assert.Equal(100, sold);
        Assert.Equal(-10, adj);
        using var db = Db(host);
        var current = db.StockBalances.First(b => b.ProductId == pid && b.WarehouseId == wid).PackageCount;
        Assert.Equal(current, inb - sold + adj); // معادلة التسوية
    }

    [Fact]
    public void Raw_Carton_Weight_Falls_Back_To_Product_Card()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var db = Db(host);
        var raw = db.Products.First(p => p.GroupCode == "001");
        var w = CartonService.RawCartonWeight(db, null, raw.Id);
        Assert.True(w > 0);
    }

    [Fact]
    public void Probe_Fresh_Db_Has_Seeded_Packs()
    {
        using var host = new TestHost();
        using var db = new DatesErp.Infrastructure.Persistence.DatesErpDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<DatesErp.Infrastructure.Persistence.DatesErpDbContext>().UseSqlite(host.Connection).Options);
        Assert.True(db.PackagingTypes.Count() >= 3, $"packs={db.PackagingTypes.Count()}");
        Assert.True(db.Products.Count(p => p.GroupCode == "004") >= 2, $"packs004={db.Products.Count(p => p.GroupCode == "004")}");
    }

    [Fact]
    public void Basket_Receipt_Generates_Basket_Empty_Not_Carton()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var db = Db(host);
        var bkE = db.PackagingTypes.FirstOrDefault(x => x.PackageCode == "BK20");
        if (bkE == null) { bkE = new PackagingType { PackageCode = "BK20", PackageNameAr = "سلة 20 كجم", UnitWeightKg = 20, UnitsPerPackage = 1 }; db.PackagingTypes.Add(bkE); db.SaveChanges(); }
        var basketE = db.Products.FirstOrDefault(x => x.ProductCode == "004-002");
        if (basketE == null) { basketE = new Product { ProductCode = "004-002", ProductNameAr = "سلة فارغة (مستعملة)", GroupCode = "004", ItemType = "Pack", UnitOfMeasure = "سلة" }; db.Products.Add(basketE); db.SaveChanges(); }
        if (basketE.SourcePackagingTypeId == null) { basketE.SourcePackagingTypeId = bkE.Id; db.SaveChanges(); }
        var bk = bkE.Id;
        var basketProd = basketE.Id;
        var cartonProd = db.Products.First(x => x.ProductCode == "004-001").Id;
        var wid = db.Warehouses.First(w => w.WarehouseCode == "WPK").Id;
        var svc = Svc(host);
        svc.PostEmptyCartons(50, "CL-B", wid, bk);      // وارد بسلة ← سلال فارغة
        svc.PostEmptyCartons(70, "CL-C", wid, null);    // افتراضي ← كرتون فارغ
        using var db2 = Db(host);
        Assert.Equal(50, db2.StockBalances.First(b => b.ProductId == basketProd && b.WarehouseId == wid).PackageCount);
        Assert.Equal(70, db2.StockBalances.First(b => b.ProductId == cartonProd && b.WarehouseId == wid).PackageCount);
    }
}
