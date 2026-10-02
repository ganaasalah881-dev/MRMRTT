using System.IO;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §v1.50.34 — طلب المستخدم: زر الحفظ في رأس شاشة أمر الإنتاج + الأزرار الكلاسيكية الناقصة
/// (إضافة/تعديل/بحث/حذف). انضباط التخطيط باقٍ: الكميات ثابتة من الخطة المعتمدة،
/// الحفظ من الرأس يخص ملاحظات الأمر، الإضافة إصدار من الخطة، والحذف للمسودة فقط.
/// </summary>
public class OrdersScreenToolbarTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Read(string rel) => File.ReadAllText(Path.Combine(RepoRoot(), rel));

    [Fact]
    public void Grid_Contains_Only_Issue_Actions_And_Classic_Actions_Live_In_The_Toolbar()
    {
        string xaml = Read("src/DatesErp.Desktop/Views/Screens/OrdersView.xaml");
        string screen = Read("src/DatesErp.Desktop/Views/Screens/OrdersView.xaml.cs");
        string cs = Read("src/DatesErp.Desktop/Views/Screens/OrdersWindows.cs");
        Assert.Contains("x:Name=\"IssueTodayBtn\"", xaml);
        Assert.Contains("x:Name=\"IssueSelectedBtn\"", xaml);
        Assert.Contains("x:Name=\"ApproveSelectedBtn\"", xaml);
        Assert.Contains("x:Name=\"SelectAllBtn\"", xaml);
        Assert.DoesNotContain("x:Name=\"AddFromPlanBtn\"", xaml);
        Assert.DoesNotContain("x:Name=\"EditBtn\"", xaml);
        Assert.DoesNotContain("x:Name=\"SaveBtn\"", xaml);
        Assert.DoesNotContain("x:Name=\"SearchBtn\"", xaml);
        Assert.DoesNotContain("x:Name=\"DeleteBtn\"", xaml);
        Assert.DoesNotContain("EditableCartons", xaml);
        Assert.Contains(".WithNew", screen);
        Assert.Contains("WithCustom(\"📥 إنزال من خطة\"", screen);
        Assert.Contains(".WithEdit", screen);
        Assert.Contains(".WithSave", screen);
        Assert.Contains(".WithSearch", screen);
        Assert.Contains(".WithDelete", screen);
        Assert.Contains(".WithPrint", screen);
        Assert.Contains("IssueSelectedBtn.IsEnabled = open.Any(r => r.IsPending) && canCreate", screen);
        Assert.Contains("ApproveSelectedBtn.IsEnabled", screen);
        Assert.Contains("ApproveSelected_Click", screen);
        Assert.Contains("TodayGrid.SelectedItem is TodayProductionRowDto current && current.IsPending", screen);
        Assert.DoesNotContain("WithCustom(\"📤 إصدار أوامر اليوم\"", screen);
        Assert.Contains("UpdateOrderHeader(_orderId, notes: _notesBox.Text ?? \"\")", cs);
        Assert.Contains("ملاحظات الأمر", cs);
        Assert.DoesNotContain("head.Children.Add(BuildTopBar());", cs);
    }

    [Fact]
    public void All_Classic_Buttons_Are_Present_At_The_Head()
    {
        string cs = Read("src/DatesErp.Desktop/Views/Screens/OrdersWindows.cs");
        Assert.Contains("التاريخ المجدول", Read("src/DatesErp.Desktop/Views/Screens/OrdersWindows.cs"));
        Assert.Contains("SearchOrders", cs);
        Assert.Contains("GetPendingPlanGroups", cs);
        Assert.Contains("IssuePlanGroup(", cs);
        var ordersScreen = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DatesErp.Desktop", "Views", "Screens", "OrdersView.xaml.cs"));
        Assert.Contains("ShowOrderInPlace", ordersScreen);
        Assert.Contains("SetPermissionModule(\"production\")", ordersScreen);
        Assert.DoesNotContain("x:Name=\"DocArea\"", File.ReadAllText(Path.Combine(RepoRoot(), "src", "DatesErp.Desktop", "Views", "Screens", "OrdersView.xaml")));
        Assert.Contains("DeleteOrder(_orderId)", cs);
    }

    [Fact]
    public void Planning_Discipline_Stays_Intact_Around_The_New_Buttons()
    {
        string svc = Read("src/DatesErp.Application/Services/ProductionOrderService.cs");
        // كميات البنود: القفل الأصلي باقٍ حرفياً (قرار معتمد بمجموعة القبول)
        Assert.Contains("أصناف أمر الإنتاج وكمياته ثابتة من الخطة المعتمدة", svc);
        // الجدولة (تاريخ/وردية/خط) مقفلة، والملاحظات وحدها تُحفظ
        Assert.Contains("تاريخ أمر الإنتاج وورديته وخطه من الخطة المعتمدة", svc);
        Assert.Contains("تم حفظ ملاحظات الأمر.", svc);
        // الحذف: مسودة فقط (الحارس الأصلي باقٍ)
        Assert.Contains("if (order.IsApproved) return OpResult.Fail(\"لا يمكن حذف أمر معتمد", svc);
    }

    [Fact]
    public void Add_Orders_Are_Issued_From_The_Approved_Plan_Only()
    {
        string today = Read("src/DatesErp.Application/Services/ProductionOrderService.Today.cs");
        // إصدار المجموعة الواحدة يمشي في مسار الإصدار اليومي نفسه بكل حراسه
        Assert.Contains("SaveTodayGroup(\"FromPlan\", planId, customerId, day.ToString(\"dd/MM/yyyy\"), shiftId, lineId, group.Select(e => FromPlan(e.Item)).ToList());", today);
        // حراسة الهوية والكمية الأصلية باقية في SaveTodayGroup
        Assert.Contains("هوية بند أمر الإنتاج وكميته يجب أن تطابق الخطة المعتمدة تماماً", today);
        Assert.Contains("أمر الإنتاج لا ينشأ يدوياً؛ يلزم مرجع خطة معتمدة", today);
        Assert.Contains("IssuePlanGroup", today);
        Assert.Contains("GetScheduledProduction", Read("src/DatesErp.Desktop/Views/Screens/OrdersView.xaml.cs"));
        Assert.Contains("ScheduledDate", Read("src/DatesErp.Core/Interfaces/Services/TodayProductionDto.cs"));
    }
}
