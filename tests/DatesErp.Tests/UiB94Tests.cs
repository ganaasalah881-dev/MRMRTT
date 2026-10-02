using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B94 — نظام صلاحيات بلا قفز: لا زر ينقل لشاشة أخرى، وبوابة عرض مركزية (فحص مصدر فقط).
/// </summary>
public class UiB94Tests
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
    public void No_Jump_Button_From_Planning_To_Orders()
    {
        string planningXaml = Read("src/DatesErp.Desktop/Views/Screens/PlanningView.xaml");
        string planning = Read("src/DatesErp.Desktop/Views/Screens/PlanningView.xaml.cs");
        string main = Read("src/DatesErp.Desktop/Views/MainWindow.xaml.cs");
        Assert.DoesNotContain("IssueOrdersBtn", planningXaml);
        Assert.DoesNotContain("IssueOrders_Click", planning);
        Assert.DoesNotContain("OpenIssuePlanOrders", main);
        Assert.DoesNotContain("PendingIssuePlanIdToOpen", main);
    }

    [Fact]
    public void Issue_Stays_Inside_Orders_Screen()
    {
        string orders = Read("src/DatesErp.Desktop/Views/Screens/OrdersView.xaml.cs");
        string windows = Read("src/DatesErp.Desktop/Views/Screens/OrdersWindows.cs");
        Assert.Contains("IssueToday_Click", orders);
        Assert.Contains("IssueTodayOrders()", orders);
        Assert.DoesNotContain("class IssuePlanWindow", windows);
        Assert.DoesNotContain("class NewOrderPanel", windows);
        Assert.DoesNotContain("PendingIssuePlanIdToOpen", orders);
    }

    [Fact]
    public void OpenScreen_Has_Permission_Gate()
    {
        string main = Read("src/DatesErp.Desktop/Views/MainWindow.xaml.cs");
        Assert.Contains("CanOpenScreen", main);
        Assert.Contains("GatedModules", main);
        Assert.Contains("Can(module, \"View\")", main);
        Assert.Contains("if (!CanOpenScreen(code)) return;", main);
    }
}
