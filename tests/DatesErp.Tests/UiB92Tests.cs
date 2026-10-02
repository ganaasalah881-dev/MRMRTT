using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B92 — الإنزال اليدوي الفتري باختيار الإدارة (فحص مصدر فقط):
/// وردية كل بند يدوية + حمولة الأيام قبل الإنزال + يدوي مكتمل.
/// </summary>
public class UiB92Tests
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
    public void LotsEditor_Has_Manual_Shift_Selection_Per_Row()
    {
        string cs = Read("src/DatesErp.Desktop/Views/Screens/PlanningWindows.cs");
        Assert.Contains("class ShiftOption", Read("src/DatesErp.Desktop/Mvvm/PlanningRows.cs"));
        Assert.Contains("AllShifts", cs);
        Assert.Contains("الوردية 🕐 *", cs);
        Assert.Contains("Binding(\"ShiftId\")", cs);
        Assert.Contains("اختر وردية الإنتاج لهذا البند", cs);
    }

    [Fact]
    public void LotsEditor_Shows_Day_Load_Before_Insert()
    {
        string cs = Read("src/DatesErp.Desktop/Views/Screens/PlanningWindows.cs");
        Assert.Contains("ShowDayLoad", cs);
        Assert.Contains("حمولة الأيام المحددة", cs);
    }

    [Fact]
    public void Manual_Add_Has_Shift_And_Period_Date_Check()
    {
        string cs = Read("src/DatesErp.Desktop/Views/Screens/PlanningView.xaml.cs");
        Assert.Contains("new LotsEditorWindow(new() { row }", cs);
        var picker = Read("src/DatesErp.Desktop/Views/Screens/PlanningWindows.cs");
        Assert.Contains("if (row.ShiftId == null)", picker);
        Assert.Contains("row.DateValue.Value.Date < _planFrom.Value.Date", picker);
        Assert.Contains("row.DateValue.Value.Date > _planTo.Value.Date", picker);
    }


}
