using System.IO;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §v1.50.24 — إصلاحات دورة الإنتاج الفعلية الستة:
/// ① الحفظ يبدأ تنفيذ الأمر المسودة تلقائياً ② لا صمت عند تعذّر الحفظ
/// ③ لوحة الإدخال تختفي عندما لا يكون هناك ما يُدخل ④ ما أُقفل يومه يغادر
/// قائمة أمر اليوم والإقفال المتكرر رسالة ودية ⑤ أصناف الفحص: المنتَج أولاً
/// ⑥ شاشة الجودة بالتصميم المعتمد.
/// </summary>
public class ActualDeliveryFixesTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Read(string rel) => File.ReadAllText(Path.Combine(RepoRoot(), rel));

    [Fact]
    public void Save_Auto_Starts_Draft_Orders_And_Listing_Accepts_Them()
    {
        string svc = Read("src/DatesErp.Application/Services/ProductionDeliveryService.Actual.cs");
        // البدء التلقائي قبل فحص الحالة: المسودة/المعتمد/المجدول المعتمد.
        int auto = svc.IndexOf("order.Status is (DocStatuses.Draft or DocStatuses.Approved or DocStatuses.Scheduled)", StringComparison.Ordinal);
        // §v1.50.26: نص استثناء الحفظ كاملاً — ليختلط بالرسالة الجديدة لغير القابل للتسجيل
        int check = svc.IndexOf("يلزم أمر اليوم المعتمد غير المقفل (الملغى أو المقفل لا يُسجَّل)", StringComparison.Ordinal);
        Assert.True(auto > 0 && check > auto, "البدء التلقائي يجب أن يسبق فحص الحالة");
        Assert.Contains("order.Status = DocStatuses.InProgress;", svc);
        // القائمة تقبل الأمر المعتمد غير المقفل قبل بدء التنفيذ.
        Assert.Contains("bool can = exe == null && order.IsApproved && !order.IsClosed;", svc);
    }

    [Fact]
    public void Save_Never_Fails_Silently_And_Input_Panel_Hides_When_Not_Recordable()
    {
        string cs = Read("src/DatesErp.Desktop/Views/Screens/ProductionDeliveryView.xaml.cs");
        Assert.Contains("⚠ لا يمكن الحفظ: ", cs);
        Assert.Contains("ActualFieldsOuter.Visibility", cs);
        Assert.Contains("EmptyGuide.Visibility", cs);
        string xaml = Read("src/DatesErp.Desktop/Views/Screens/ProductionDeliveryView.xaml");
        Assert.Contains("x:Name=\"EmptyGuide\"", xaml);
        // ضغط قسم المخرجات الثانوية (§v1.50.26: 66–96)
        Assert.Contains("MinHeight=\"66\" MaxHeight=\"96\" RowHeight=\"30\"", xaml);
    }

    [Fact]
    public void Day_Closed_Orders_Leave_Todays_Sheet_And_Reclose_Is_Friendly()
    {
        Assert.Contains("DayClosed", Read("src/DatesErp.Core/Interfaces/Services/TodayProductionDto.cs"));
        string today = Read("src/DatesErp.Application/Services/ProductionOrderService.Today.cs");
        Assert.Contains("dayClosedOrders", today);
        string orders = Read("src/DatesErp.Desktop/Views/Screens/OrdersView.xaml.cs");
        Assert.Contains("next.Rows.Where(r => !r.DayClosed).ToList()", orders);
        Assert.Contains("DayClosedChip", orders);
        // حقول الفعلي بلا دخل بأمر الإنتاج
        string doc = Read("src/DatesErp.Desktop/Views/Screens/OrdersWindows.cs");
        Assert.DoesNotContain("AddCard(\"الإنتاج الفعلي\"", doc);
        Assert.DoesNotContain("AddCard(\"المقبول/المرفوض\"", doc);
        Assert.DoesNotContain("AddCard(\"المتبقي\"", doc);
        Assert.Contains("تسجيل فعلي اليوم", doc);
        // الإقفال المتكرر: نجاح ودّي لا استثناء
        string closure = Read("src/DatesErp.Application/Services/PlanClosureService.cs");
        Assert.Contains("مقفلة سابقاً — لا حاجة لإقفال آخر", closure);
    }

    [Fact]
    public void Quality_Lists_Produced_First_And_Uses_The_Adopted_Design()
    {
        // §v1.50.28: التدفق المعتمد — الفحص ينزل من تسليم الإنتاج مباشرة بلا اختيار صنف،
        // والنتائج صفات جودة (ليست أصنافاً)، والمعايير المعتمدة تظهر تلقائياً.
        string cs = Read("src/DatesErp.Desktop/Views/Screens/QualityView.xaml.cs");
        Assert.Contains("GetDeliverySources", cs);
        Assert.Contains("تسليم الإنتاج رقم", cs);
        string xaml = Read("src/DatesErp.Desktop/Views/Screens/QualityView.xaml");
        Assert.Contains("مصدر الفحص:", xaml);
        Assert.Contains("صف لكل عميل وصنف وعبوة كما في خطة الإنتاج", xaml);  // §v1.50.29: المصفوفة
        Assert.Contains("معايير الفحص المعتمدة", xaml);
        Assert.Contains("AlternatingRowBackground=\"{DynamicResource ThemeAlternateRowBrush}\"", xaml);
        Assert.Contains("HorizontalGridLinesBrush=\"{DynamicResource ThemeGridLineBrush}\"", xaml);
        Assert.Contains("Background=\"{DynamicResource ThemeTableBackgroundBrush}\"", xaml); // السطح يتبع الثيم ولا يكسر الداكن.
        // قاعدة الحفظ معروضة للمستخدم في الشاشة نفسها
        Assert.Contains("مجموع صفات كل صنف = كميته المستلمة", xaml);
        // أعمدة الصفات تُبنى في الكود من صفات الصنف (لا أعمدة ثابتة باسم نتيجة)
        Assert.Contains("BuildGradeColumns", Read("src/DatesErp.Desktop/Views/Screens/QualityView.xaml.cs"));
    }
}
