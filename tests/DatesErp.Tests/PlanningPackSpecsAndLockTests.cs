using DatesErp.Desktop.Views.Screens;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §v1.50.35 — ملاحظات شاشة الخطط: مواصفات العبوة من بطاقة الصنف، شريط طاقة مضغوط،
/// وقفل الخطة المعتمدة (فك الاعتماد بصلاحية وبعد إلغاء الحركات اللاحقة).
/// </summary>
public class PlanningPackSpecsAndLockTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
    private static string Read(string rel) => File.ReadAllText(Path.Combine(RepoRoot(), rel));

    [Fact]
    public void Popup_Pack_Specs_Come_From_Finished_Product_Card_Not_Generic_Pack()
    {
        string rows = Read("src/DatesErp.Desktop/Mvvm/PlanningRows.cs");
        Assert.Contains("prod != null && prod.CartonWeightKg > 0", rows);
        Assert.Contains("prod != null && prod.MoldsCount > 0", rows);
        Assert.Contains("ProductCapacityDisplay", rows);
        Assert.Contains("CartonWeightKg { get; set; }", rows);
        string win = Read("src/DatesErp.Desktop/Views/Screens/PlanningWindows.cs");
        Assert.Contains("Header = \"طاقة الصنف\"", win);
        Assert.Contains("ProductCapacityDisplay", win);
        string view = Read("src/DatesErp.Desktop/Views/Screens/PlanningView.xaml.cs");
        Assert.Contains("ToProductOpt", view);
        Assert.Contains("HourlyProductionRate", view);
    }

    [Fact]
    public void Capacity_Bar_Is_Compact_For_Weekly_Periods()
    {
        string cap = Read("src/DatesErp.Desktop/Views/Screens/PlanningView.Capacity.cs");
        Assert.DoesNotContain("Take(12)", cap);
        Assert.Contains("result.Slots.Count > 1", cap);
        Assert.Contains("CapacitySummary.ToolTip", cap);
        string xaml = Read("src/DatesErp.Desktop/Views/Screens/PlanningView.xaml");
        Assert.Contains("TextWrapping=\"NoWrap\"", xaml);
        Assert.Contains("MaxWidth=\"520\"", xaml);
    }

    [Fact]
    public void Approved_Plan_Locks_All_Edit_Buttons_Until_Unapprove()
    {
        string cs = Read("src/DatesErp.Desktop/Views/Screens/PlanningView.xaml.cs");
        Assert.Contains("RowsGrid.IsReadOnly = locked", cs);
        Assert.Contains("InsertActionsPanel.IsEnabled = !locked", cs);
        Assert.Contains("الخطة معتمدة ومقفلة — لا حذف بنود بعد الاعتماد", cs);
        Assert.Contains("الخطة معتمدة ومقفلة — لا تعديل إلا بعد فك الاعتماد", cs);
        string prog = Read("src/DatesErp.Application/Services/PlanProgressService.cs");
        Assert.Contains("if (plan.IsApproved) return OpResult.Fail(\"الخطة معتمدة ومقفلة", prog);
        string svc = Read("src/DatesErp.Application/Services/PlanningService.cs");
        Assert.Contains("PlanHasLiveOrderReferences(plan)", svc);
        Assert.Contains("ألغِ تلك الأوامر أولاً ثم فك الاعتماد", svc);
        Assert.Contains("Require(\"planning\", \"Cancel\")", svc);
    }

    [Fact]
    public void Recalc_Uses_Product_Card_When_Weights_Differ()
    {
        var row = new LotEditorRow
        {
            AllProducts = new()
            {
                new ProductOption { Id = 10, Name = "سكري 8كجم", CartonWeightKg = 8, MoldsCount = 16, HourlyRate = 400 }
            },
            AllPacks = new()
            {
                new PackOption { Id = 1, Name = "عام", UnitWeightKg = 5, MoldsCount = 5 }
            }
        };
        row.ProductId = 10;
        row.CartonsText = "10";
        Assert.Equal(8, row.PackWeight);
        Assert.Equal(16, row.MoldsCount);
        Assert.Equal(80, row.ComputedKg);
        Assert.Contains("كرتون", row.ProductCapacityDisplay);
    }
}
