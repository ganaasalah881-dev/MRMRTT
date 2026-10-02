using System.IO;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §v1.50.33 — إصلاحات ملاحظات المستخدم على شاشة الأصناف وقائمتي المخزون:
/// الأصناف: قائمة «الصنف الخام المصدر» تتحدث فوراً بعد حفظ خام جديد + إزالة خانة
/// «يحتاج معالجة قبل الإنتاج» (لا داعي لها) مع بقاء القيمة المحفوظة كما هي.
/// أرصدة المخزون: اسم الصنف/المادة لا يبقى فارغاً (يُستكمل من العبوة)، وأُضيفت
/// الوحدة وتاريخ الدفعة. حركات المخزون: أُزيل عمودا المستخدم/الجهاز وأُضيفت
/// الوحدة والعميل صاحب الشحنة.
/// </summary>
public class WarehouseAndItemsFixesTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Read(string rel) => File.ReadAllText(Path.Combine(RepoRoot(), rel));

    [Fact]
    public void Raw_Source_List_Refreshes_Right_After_Saving_A_New_Raw_Item()
    {
        string cs = Read("src/DatesErp.Desktop/Views/Screens/ItemsView.xaml.cs");
        // بعد الحفظ الناجح تُعاد قراءة القوائم — الخام الجديد يظهر فوراً بلا إغلاق الشاشة
        Assert.Matches(@"Info\(r\.Message\);\s*LoadLookups\(\);", cs);
        // الفلترة تشمل سجلات قديمة نوعها غير مضبوط: نوع Raw أو مجموعة الخام 001
        Assert.Contains("(p.ItemType == \"Raw\" || p.GroupCode == \"001\")", cs);
    }

    [Fact]
    public void Treatment_Checkbox_Is_Gone_While_Stored_Value_Survives()
    {
        string xaml = Read("src/DatesErp.Desktop/Views/Screens/ItemsView.xaml");
        string cs = Read("src/DatesErp.Desktop/Views/Screens/ItemsView.xaml.cs");
        Assert.DoesNotContain("TreatBox", xaml);
        Assert.DoesNotContain("TreatBox", cs);
        // الحفظ لا يمرر قيمة المعالجة (null = يُبقي المخزون) — عمداً لا false
        Assert.DoesNotContain("requiresTreatment:", cs);
    }

    [Fact]
    public void Balances_Never_Show_A_Blank_Item_And_Gain_Unit_And_Lot_Date()
    {
        string cs = Read("src/DatesErp.Desktop/Views/Screens/GenericListView.xaml.cs");
        Assert.Contains("package?.PackageNameAr ?? \"—\"", cs);
        Assert.Contains("\"الوحدة\"", cs);
        Assert.Contains("\"تاريخ الدفعة\"", cs);
        Assert.Contains("lot?.LotDate", cs);
        // الأعمدة القديمة الناقصة عادت كاملة: الصنف/المادة والعميل باقيان
        Assert.Contains("\"الصنف/المادة\"", cs);
        Assert.Contains("\"العميل\"", cs);
    }

    [Fact]
    public void Movements_Drop_User_Machine_And_Gain_Unit_And_Shipment_Customer()
    {
        string cs = Read("src/DatesErp.Desktop/Views/Screens/GenericListView.xaml.cs");
        Assert.Contains("\"العميل صاحب الشحنة\"", cs);
        Assert.Contains("t.CustomerId ?? lot?.CustomerId", cs);
        // عمودا المستخدم والجهاز أُزيلا من حركات المخزون (متاحان في سجل التدقيق)
        int bal = cs.IndexOf("ForBalances()", StringComparison.Ordinal);
        int mov = cs.IndexOf("ForMovements()", StringComparison.Ordinal);
        int movEnd = cs.IndexOf("ForMachines()", StringComparison.Ordinal);
        string movements = cs[mov..movEnd];
        Assert.DoesNotContain("\"المستخدم\"", movements);
        Assert.DoesNotContain("\"الجهاز\"", movements);
        Assert.Contains("\"الوحدة\"", movements);
        _ = bal;
    }
}
