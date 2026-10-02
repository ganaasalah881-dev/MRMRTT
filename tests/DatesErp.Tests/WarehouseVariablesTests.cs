using DatesErp.Application.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B70 — شاشة «متغيرات المخازن» (إعادة التصميم من الصفر):
/// المجموعات رقم + اسم فقط، والأزرار الخمسة الإلزامية في كل شاشة نصممها.
/// </summary>
public class WarehouseVariablesTests
{
    private static MasterDataService Svc(TestHost host)
        => host.Services.CreateScope().ServiceProvider.GetRequiredService<MasterDataService>();

    private static DatesErpDbContext Db(TestHost host)
        => new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);

    [Fact]
    public void Group_Saves_With_Number_And_Name_Only_And_AutoNumber()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var r = Svc(host).SaveGroupMinimal(null, "", "مواد تنظيف الصالة");
        Assert.True(r.Ok, r.Message);
        using var db = Db(host);
        var g = db.ItemGroups.Single(x => x.GroupNameAr == "مواد تنظيف الصالة");
        Assert.Equal("005", g.GroupCode);          // ترقيم تلقائي بعد 004
        Assert.True(string.IsNullOrEmpty(g.GroupType));   // رقم + اسم فقط
        Assert.True(string.IsNullOrEmpty(g.DefaultUnit));
    }

    [Fact]
    public void Group_Duplicate_Name_Or_Code_Rejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        Assert.True(Svc(host).SaveGroupMinimal(null, "010", "مجموعة الاختبار").Ok);
        var dupName = Svc(host).SaveGroupMinimal(null, "011", "مجموعة الاختبار");
        Assert.False(dupName.Ok);
        var dupCode = Svc(host).SaveGroupMinimal(null, "010", "اسم آخر");
        Assert.False(dupCode.Ok);
    }

    [Fact]
    public void Group_Delete_Removes_When_Free_Disables_When_Used()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);
        Assert.True(svc.SaveGroupMinimal(null, "020", "مجموعة حرة").Ok);
        Assert.True(svc.SaveGroupMinimal(null, "021", "مجموعة مستخدمة").Ok);

        // صنف يرتبط بالمجموعة 021
        var prod = svc.SaveProductFull(null, "021-001", "صنف مرتبط", "021", "Raw", "كجم", 0, 0, 0, null);
        Assert.True(prod.Ok, prod.Message);

        using (var db = Db(host))
        {
            var free = db.ItemGroups.Single(x => x.GroupCode == "020");
            var used = db.ItemGroups.Single(x => x.GroupCode == "021");
            Assert.True(svc.DeleteOrDisableItemGroup(free.Id).Ok);
            Assert.False(db.ItemGroups.Any(x => x.Id == free.Id));      // حُذفت فعلاً
            Assert.True(svc.DeleteOrDisableItemGroup(used.Id).Ok);
        }
        using var db2 = Db(host);
        var disabled = db2.ItemGroups.Single(x => x.GroupCode == "021");
        Assert.False(disabled.IsActive);                                 // أُوقفت ولم تُحذف
    }

    [Fact]
    public void Every_New_Screen_Carries_The_Five_Mandatory_Buttons()
    {
        // قاعدة المستخدم: كل شاشة نصممها تحمل إضافة · حفظ · تعديل · بحث · حذف
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        var xaml = File.ReadAllText(Path.Combine(dir!.FullName, "src/DatesErp.Desktop/Views/Screens/WarehouseVariablesView.xaml"));
        foreach (var label in new[] { "➕ إضافة", "💾 حفظ", "✏️ تعديل", "🔍 بحث", "🗑️ حذف" })
            Assert.Contains("Content=\"" + label + "\"", xaml);
    }

    [Fact]
    public void Unit_Saves_Name_Only_No_Code_And_Protected_Delete()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);
        Assert.True(svc.SaveUnitMinimal(null, "صندوق").Ok);
        Assert.False(svc.SaveUnitMinimal(null, "صندوق").Ok);          // تكرار مرفوض
        using (var db = Db(host))
        {
            var u = db.UnitsOfMeasure.Single(x => x.UnitNameAr == "صندوق");
            Assert.True(string.IsNullOrEmpty(u.UnitCode));            // لا ترقيم إطلاقاً
            Assert.True(svc.DeleteOrDisableUnit(u.Id).Ok);
            Assert.False(db.UnitsOfMeasure.Any(x => x.Id == u.Id));   // حرة ← حُذفت
            var used = db.UnitsOfMeasure.Single(x => x.UnitNameAr == "كجم");
            Assert.True(svc.DeleteOrDisableUnit(used.Id).Ok);
        }
        using var db2 = Db(host);
        Assert.False(db2.UnitsOfMeasure.Single(x => x.UnitNameAr == "كجم").IsActive); // مستخدمة ← أوقفت
    }

    [Fact]
    public void Units_Tab_Carries_The_Five_Mandatory_Buttons_Too()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        var xaml = File.ReadAllText(Path.Combine(dir!.FullName, "src/DatesErp.Desktop/Views/Screens/WarehouseVariablesView.xaml"));
        Assert.Contains("Header=\"📏 الوحدات\"", xaml);
        Assert.Equal(2, CountOf(xaml, "Content=\"➕ إضافة\""));   // تبويبان × خمسة أزرار
        Assert.Equal(2, CountOf(xaml, "Content=\"🗑️ حذف\""));
        Assert.DoesNotContain("UnitCodeBox", xaml);                 // الوحدات: اسم فقط بلا حقل رقم
    }

    private static int CountOf(string s, string sub)
    {
        int c = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { c++; i += sub.Length; }
        return c;
    }
}

public class ItemsRedesignTests
{
    private static MasterDataService Svc(TestHost host)
        => host.Services.CreateScope().ServiceProvider.GetRequiredService<MasterDataService>();

    private static DatesErpDbContext Db(TestHost host)
        => new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);

    [Fact]
    public void Raw_Item_Has_Only_Basics_And_Code_Starts_With_Group()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var r = Svc(host).SaveProductFull(null, "001-900", "سكري خام", "001", "Raw", "كجم", 0, 0, 0, null);
        Assert.True(r.Ok, r.Message);
        using var db = Db(host);
        var p = db.Products.Single(x => x.ProductCode == "001-900");
        Assert.Equal("Raw", p.ItemType);
        Assert.Equal("كجم", p.UnitOfMeasure);
        Assert.Equal(0, p.MoldsCount);          // الخام بلا قوالب
    }

    [Fact]
    public void Finished_Item_Gets_Molds_CartonWeight_And_Raw_Source()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);
        var raw = svc.SaveProductFull(null, "001-900", "سكري خام", "001", "Raw", "كجم", 0, 0, 0, null);
        Assert.True(raw.Ok, raw.Message);
        // سكري تام ← ينتمي لسكري خام؛ 5 قوالب × 0.5 = 2.5 كجم للكرتون
        var fin = svc.SaveProductFull(null, "002-900", "سكري تام", "002", "Finished", "كرتون", 2.5, 5, 0.5, null, raw.Id);
        Assert.True(fin.Ok, fin.Message);
        using var db = Db(host);
        var p = db.Products.Single(x => x.ProductCode == "002-900");
        Assert.Equal(5, p.MoldsCount);
        Assert.Equal(0.5, p.MoldWeightKg);
        Assert.Equal(2.5, p.CartonWeightKg);
        Assert.Equal(raw.Id, p.SourceProductId);
    }

    [Fact]
    public void Items_Screen_Carries_The_Five_Mandatory_Buttons()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        var xaml = File.ReadAllText(Path.Combine(dir!.FullName, "src/DatesErp.Desktop/Views/Screens/ItemsView.xaml"));
        foreach (var label in new[] { "➕ إضافة", "💾 حفظ", "✏️ تعديل", "🔍 بحث", "🗑️ حذف" })
            Assert.Contains("Content=\"" + label + "\"", xaml);
        // الخام فقط: لا تظهر حقول القوالب إلا لقسم التام (Visibility Collapsed افتراضياً)
        Assert.Contains("x:Name=\"FinishedFields\"", xaml);
        Assert.Contains("Visibility=\"Collapsed\"", xaml);
    }
}
