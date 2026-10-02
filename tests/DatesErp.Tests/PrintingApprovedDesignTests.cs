using System.IO;
using DatesErp.Desktop.Printing;
using DatesErp.Desktop.Views;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §v1.50.25 — التصميم المعتمد للنماذج المطبوعة: A4 عمودي دائماً، خط أوضح،
/// مسطرة ظاهرة، حالة المستند بلونها، والتقارير مثل بقية النماذج.
/// </summary>
public class PrintingApprovedDesignTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
    private static string Read(string rel) => File.ReadAllText(Path.Combine(RepoRoot(), rel));

    [Fact]
    public void Official_Documents_Stay_Portrait_Regardless_Of_Column_Count()
    {
        // القاعدة القديمة كانت تُقلب أي جدول ≥7 أعمدة إلى أفقي — أُلغيت.
        string[] cells = Enumerable.Range(0, 12).Select(i => $"عمود {i + 1}").ToArray();
        var m = new PhaseDocModel
        {
            Columns = cells,
            Rows = new() { cells.Select(c => (object)c).ToArray() }
        };
        Assert.False(PrintSchema.FromPhase(m).Landscape);
    }

    [Fact]
    public void No_Design_Forces_Landscape_Anymore()
    {
        Assert.DoesNotContain("Landscape=true", Read("src/DatesErp.Desktop/Printing/DocumentDesigns.cs"));
        Assert.DoesNotContain(">=7", Read("src/DatesErp.Desktop/Printing/PrintSchema.cs"));
        Assert.Contains("Landscape = false", Read("src/DatesErp.Desktop/Services/ExportPrintService.cs"));
        Assert.DoesNotContain(">= 7", Read("src/DatesErp.Desktop/Services/ExportPrintService.cs"));
    }

    [Fact]
    public void Print_Font_Is_Clearer_And_Grid_Is_Ruled()
    {
        Assert.True(PrintLayout.FontSize >= 13, "خط النماذج يجب ألا يقل عن 13px");
        Assert.Equal(20, PrintLayout.LineHeight);
        string renderer = Read("src/DatesErp.Desktop/Printing/PrintRenderer.cs");
        Assert.Contains("#8CA0AC", renderer);          // مسطرة ظاهرة
        Assert.Contains("0.75", renderer);
        Assert.Contains("StatusBrush", renderer);      // الحالة بلونها
        Assert.Contains("مسودة", renderer);
    }

    [Fact]
    public void Status_Bar_Shows_The_Actual_Database_Target()
    {
        string shell = Read("src/DatesErp.Desktop/Views/MainWindow.xaml.cs");
        Assert.Contains("mfgsystem_local.db", shell);
        Assert.Contains("SQL Server (", shell);
    }
}
