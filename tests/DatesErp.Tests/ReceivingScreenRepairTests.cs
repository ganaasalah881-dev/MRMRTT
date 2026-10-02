using System.Xml.Linq;
using DatesErp.Core.Exceptions;
using DatesErp.Desktop.Mvvm;

namespace DatesErp.Tests;

public class ReceivingScreenRepairTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
    private static string Source(string name) => File.ReadAllText(Path.Combine(Root(), "src/DatesErp.Desktop/Views/Screens", name));

    [Fact]
    public void Choice_Is_Empty_Initially_And_No_Clears_Date_And_Hides_Field_With_Notifications()
    {
        var row = new ReceivingItemRow(); var events = new List<string>();
        row.PropertyChanged += (_, e) => events.Add(e.PropertyName!);
        Assert.Null(row.TreatmentChoiceAr); Assert.False(row.ShowUntilDate);
        row.TreatmentChoiceAr = "نعم"; Assert.True(row.ShowUntilDate);
        row.TreatmentUntilDate = new DateTime(2026, 9, 15); row.TreatmentChoiceAr = "لا";
        Assert.False(row.ShowUntilDate); Assert.Null(row.TreatmentUntilDate);
        Assert.Contains(nameof(row.ShowUntilDate), events); Assert.Contains(nameof(row.TreatmentUntilDate), events);
        row.TreatmentChoiceAr = "نعم"; Assert.Null(row.TreatmentUntilDate);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("maybe")] [InlineData("1")]
    public void Missing_Or_Unsupported_Choice_Cannot_Pass_UI_Validation(string? choice)
    {
        var row = new ReceivingItemRow { TreatmentChoiceAr = choice };
        Assert.Throws<DomainException>(() => row.ValidateTreatment(new DateTime(2026, 9, 8)));
    }

    [Theory]
    [InlineData(-1)] [InlineData(-9)]
    public void Invalid_Or_Missing_Yes_Date_Fails_UI_Validation(int days)
    {
        var receipt = new DateTime(2026, 9, 8); var row = new ReceivingItemRow { TreatmentRequired = true };
        Assert.Throws<DomainException>(() => row.ValidateTreatment(receipt));
        row.TreatmentUntilDate = receipt.AddDays(days);
        Assert.Throws<DomainException>(() => row.ValidateTreatment(receipt));
        row.TreatmentUntilDate = receipt; row.ValidateTreatment(receipt);
    }

    [Fact]
    public void Grid_Contains_Inline_Choice_And_Conditional_DatePicker_Not_A_Master_Product_Flag()
    {
        var doc = XDocument.Parse(Source("ReceivingView.xaml")); XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var grid = Assert.Single(doc.Descendants(), e => (string?)e.Attribute(x + "Name") == "ItemsGrid");
        Assert.Equal("False", (string?)grid.Attribute("IsReadOnly")); Assert.Equal("False", (string?)grid.Attribute("CanUserDeleteRows"));
        Assert.Contains(grid.Descendants(), e => e.Name.LocalName == "ComboBox" && ((string?)e.Attribute("SelectedItem"))?.Contains("TreatmentChoiceAr") == true);
        var date = Assert.Single(grid.Descendants(), e => e.Name.LocalName == "DatePicker");
        Assert.Contains("TreatmentUntilDate", (string?)date.Attribute("SelectedDate"));
        Assert.Contains("IsEditable", (string?)date.Attribute("IsEnabled"));
        Assert.Contains("ShowUntilDate", grid.ToString()); Assert.DoesNotContain("RequiresTreatment", grid.ToString());
        var code = Source("ReceivingView.xaml.cs"); Assert.Contains("ItemsGrid.IsReadOnly = !editable", code);
        Assert.Contains("row.ValidateTreatment", code); Assert.DoesNotContain("ItemsGrid.Items.Refresh()", code);
        Assert.Contains("DataGridEditingUnit.Cell", code); Assert.Contains("DataGridEditingUnit.Row", code);
    }

    [Fact]
    public void No_Independent_Treatment_Or_Period_Window_And_Approval_Requires_Saved_View()
    {
        var code = Source("ReceivingView.xaml.cs");
        Assert.Contains("_currentId == 0 || _mode != \"View\"", code);
        Assert.Contains("_toolbar.ApproveBtn.IsEnabled = !_approved && _currentId > 0 && _mode == \"View\"", code);
        foreach (var name in new[] { "TreatmentSplitDialog.cs", "TreatmentStartDialog.cs", "RawTreatmentView.xaml", "RawTreatmentView.xaml.cs" })
            Assert.False(File.Exists(Path.Combine(Root(), "src/DatesErp.Desktop/Views/Screens", name)));
        Assert.Contains("\"treatment\" => ReceivingScreen()", Source("ScreenFactory.cs"));
    }

    [Fact]
    public void Approval_Banner_Cannot_Become_An_Accidental_Side_Column_When_Shown()
    {
        var doc = XDocument.Parse(Source("ReceivingView.xaml")); XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var banner = Assert.Single(doc.Descendants(), e => (string?)e.Attribute(x + "Name") == "LockBanner");
        Assert.Equal("Top", (string?)banner.Attribute("DockPanel.Dock"));
    }
}
