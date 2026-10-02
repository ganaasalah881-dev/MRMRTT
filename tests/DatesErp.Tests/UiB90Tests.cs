using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B90 — معرض التقارير المهني + توحيد التسطير وحواف الحقول (فحص مصدر فقط).
/// </summary>
public class UiB90Tests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string rel)
        => File.ReadAllText(Path.Combine(RepoRoot(), rel));

    [Fact]
    public void Reports_Catalog_Is_Searchable_With_Cards()
    {
        string xaml = Read("src/DatesErp.Desktop/Views/Screens/ReportsView.xaml");
        string cs = Read("src/DatesErp.Desktop/Views/Screens/ReportsView.xaml.cs");
        Assert.Contains("x:Name=\"CatalogSearchBox\"", xaml);
        Assert.Contains("CatalogSearch_Changed", cs);
        Assert.Contains("_reportsView.Filter", cs);
        Assert.Contains("x:Name=\"ReportsCount\"", xaml);
        Assert.Contains("Parameters.Count", xaml);
    }

    [Fact]
    public void Reports_Workspace_Has_Header_Filters_And_Subtitle()
    {
        string xaml = Read("src/DatesErp.Desktop/Views/Screens/ReportsView.xaml");
        string cs = Read("src/DatesErp.Desktop/Views/Screens/ReportsView.xaml.cs");
        Assert.Contains("x:Name=\"ReportSub\"", xaml);
        Assert.Contains("ReportSub.Text", cs);
        Assert.Contains("GridLength(360)", cs);
        // رؤية ملء العرض محفوظة
        Assert.Contains("Grid.SetColumnSpan(ReportPanel, 2)", cs);
    }

    [Fact]
    public void Theme_Unifies_Field_Borders_And_List_Selection()
    {
        string theme = Read("src/DatesErp.Desktop/Themes/DateErpTheme.xaml");
        // حواف الحقول الموحدة
        Assert.Contains("<Style TargetType=\"ComboBox\">", theme);
        Assert.Contains("<Style TargetType=\"ListBoxItem\">", theme);
        Assert.Contains("IsSelected", theme);
        // عناصر القوائم: خصائص ومحفزات فقط — لا قالب يعطل الافتراضي
        int i = theme.IndexOf("<Style TargetType=\"ListBoxItem\">", StringComparison.Ordinal);
        int end = theme.IndexOf("</Style>", i, StringComparison.Ordinal);
        string block = theme.Substring(i, end - i);
        Assert.DoesNotContain("ControlTemplate", block);
    }
}
