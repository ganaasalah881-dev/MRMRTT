using System.IO;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §v1.50.32 — تحسينات شاشة تسليم الإنتاج من مراجعة اللقطة الفعلية:
/// ١) جدول البنود يأخذ مساحة عمل حقيقية دائماً (حد أدنى 260) مع تنبيه الأمر بلا بنود.
/// ٢) عمود «الفرق عن المخطط» بلون حالة صريح: مطابق أخضر / نقص محمر / غير صالح رمادي.
/// ٣) سطر الإضافة الفارغ في المخرجات الثانوية مهدأ ومُوضَّح بتلميح، لا تظليل صارخ.
/// ٤) التواريخ في شريطي الكروم وساعة النافذة صيغة واحدة ثابتة الاتجاه (Invariant + LRM).
/// </summary>
public class DeliveryScreenPolishTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Read(string rel) => File.ReadAllText(Path.Combine(RepoRoot(), rel));

    [Fact]
    public void Items_Grid_Always_Keeps_A_Real_Working_Height()
    {
        string xaml = Read("src/DatesErp.Desktop/Views/Screens/ProductionDeliveryView.xaml");
        // صف النجمة بحد أدنى + الجدول نفسه — لا انضغاط لسطر واحد مهما كانت النافذة
        Assert.Contains("<RowDefinition Height=\"*\" MinHeight=\"260\"/>", xaml);
        Assert.Contains("MinHeight=\"260\" RowHeight=\"32\"", xaml);
        // الأمر بلا بنود يُشرح بدل جدول فارغ صامت
        string cs = Read("src/DatesErp.Desktop/Views/Screens/ProductionDeliveryView.xaml.cs");
        Assert.Contains("بلا بنود خطة", cs);
    }

    [Fact]
    public void Difference_Column_Shows_An_Explicit_Color_State()
    {
        string xaml = Read("src/DatesErp.Desktop/Views/Screens/ProductionDeliveryView.xaml");
        string rows = Read("src/DatesErp.Desktop/Mvvm/ActualProductionRows.cs");
        Assert.Contains("DiffState", rows);
        Assert.Contains("Changed(nameof(DiffState))", rows);
        Assert.Contains("DataTrigger Binding=\"{Binding DiffState}\" Value=\"match\"", xaml);
        Assert.Contains("✔ مطابق", xaml);
        Assert.Contains("الفرق عن المخطط", xaml);
    }

    [Fact]
    public void Secondary_New_Item_Row_Is_Calmed_And_Explained()
    {
        string xaml = Read("src/DatesErp.Desktop/Views/Screens/ProductionDeliveryView.xaml");
        string cs = Read("src/DatesErp.Desktop/Views/Screens/ProductionDeliveryView.xaml.cs");
        Assert.Contains("LoadingRow=\"Secondary_LoadingRow\"", xaml);
        Assert.Contains("is not ActualSecondaryRow", cs);
        Assert.Contains("سطر فارغ للإضافة", cs);
        // ضغط القسم المعتمد (66–96) باقٍ — الحارس الأصلي لا يُمس
        Assert.Contains("MinHeight=\"66\" MaxHeight=\"96\" RowHeight=\"30\"", xaml);
    }

    [Fact]
    public void Chrome_Dates_Are_Invariant_And_Left_To_Right()
    {
        string chrome = Read("src/DatesErp.Desktop/Views/ErpChrome.xaml.cs");
        string main = Read("src/DatesErp.Desktop/Views/MainWindow.xaml.cs");
        // علامة الاتجاه + صيغة ثابتة الأرقام والترتيب
        Assert.Contains("\\u200E", chrome);
        Assert.Contains("CultureInfo.InvariantCulture", chrome);
        Assert.Contains("\\u200E", main);
        Assert.DoesNotContain("{DateTime.Now:dd/MM/yyyy}", chrome);   // لا صيغة تفاعلية تعتمد على ثقافة الجهاز
        Assert.DoesNotContain("DateTime.Now.ToString(\"dd/MM/yyyy HH:mm:ss\")", main);
    }
}
