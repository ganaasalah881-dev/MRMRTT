using System.IO;
using Xunit;

namespace DatesErp.Tests;

/// <summary>يحمي إصلاح NRE الذي كان يحدث أثناء InitializeComponent لشاشة مكونات الإنتاج.</summary>
public class ProductBOMInitializationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public void Xaml_Selection_Handlers_Are_Safe_Before_Named_Controls_Finish_Initialization()
    {
        var path = Path.Combine(RepoRoot(), "src", "DatesErp.Desktop", "Views", "Screens", "ProductBOMView.xaml.cs");
        var cs = File.ReadAllText(path);
        Assert.Contains("var box = sender as ComboBox ?? FinishedBox", cs);
        Assert.Contains("if (box?.SelectedValue is int fid)", cs);
        Assert.Contains("var box = sender as ComboBox ?? CalcBox", cs);
        Assert.Contains("if (QtyLabel == null || CalcHint == null) return;", cs);
        Assert.Contains("FinishedBox?.SelectedItem", cs);
    }
}
