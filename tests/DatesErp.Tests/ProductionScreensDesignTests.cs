using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §التصميم المعتمد — نشر لغة شاشة التخطيط (شرائح السياق + أشرطة الإجماليات + الجداول
/// المرسّمة بصفوف متبادلة) على شاشة أمر إنتاج اليوم وشاشة تسليم الإنتاج،
/// وإضافة الفلترة بالحالة والترتيب بالنقر على نافذة بحث الخطط المنبثقة.
/// </summary>
public class ProductionScreensDesignTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Read(string rel) => File.ReadAllText(Path.Combine(RepoRoot(), rel));

    [Fact]
    public void Orders_Screen_Uses_The_Adopted_Design()
    {
        // §شاشة أمر إنتاج اليوم بنفس لغة شاشة التخطيط
        string xaml = Read("src/DatesErp.Desktop/Views/Screens/OrdersView.xaml");
        string cs = Read("src/DatesErp.Desktop/Views/Screens/OrdersView.xaml.cs");

        // شرائح السياق الأربع + شريط الإجماليات
        Assert.Contains("DayChip", xaml); Assert.Contains("ItemsChip", xaml);
        Assert.Contains("IssuedChip", xaml); Assert.Contains("PendingChip", xaml);
        Assert.Contains("TotCartonsBox", xaml); Assert.Contains("TotQtyBox", xaml);
        Assert.Contains("TotCustsBox", xaml); Assert.Contains("TotLinesBox", xaml);
        Assert.Contains("DayChip.Text", cs); Assert.Contains("TotCartonsBox.Text", cs);

        // تنسيق الجدول الموحد: صفوف متبادلة وخطوط أفقية وإطار الهوية
        Assert.Contains("AlternatingRowBackground=\"{DynamicResource ThemeAlternateRowBrush}\"", xaml);
        Assert.Contains("HorizontalGridLinesBrush=\"{DynamicResource ThemeGridLineBrush}\"", xaml);
        Assert.Contains("BorderBrush=\"{DynamicResource ThemeGridLineBrush}\"", xaml);
        Assert.Contains("RowHeight=\"32\"", xaml);
        Assert.Contains("ColumnHeaderHeight=\"36\"", xaml);
        // عمود الوزن المعتمد في التصميم
        Assert.Contains("الوزن (كجم)", xaml);

        // العقود الوظيفية باقية: الجذر ListArea وجدول اليوم وإجراء الإصدار من هذه الشاشة فقط
        Assert.Contains("x:Name=\"ListArea\"", xaml);
        Assert.Contains("x:Name=\"TodayGrid\"", xaml);
        Assert.Contains("IssueTodayBtn", xaml);
        Assert.Contains("IssueToday_Click", cs);
        Assert.Contains("IssueTodayOrders()", cs);
        // §v1.50.36 — شاشة واحدة: المستند أسفل الجدول (لا DocArea متبادل)
        Assert.Contains("x:Name=\"DetailsHost\"", xaml);
        Assert.Contains("ShowOrderInPlace", cs);
        Assert.DoesNotContain("x:Name=\"DocArea\"", xaml);
    }

    [Fact]
    public void ProductionDelivery_Screen_Uses_The_Adopted_Design()
    {
        // §شاشة تسليم الإنتاج بنفس الشكل: شرائح سياق الأمر + بانر حالة + جداول مرسّمة
        string xaml = Read("src/DatesErp.Desktop/Views/Screens/ProductionDeliveryView.xaml");
        string cs = Read("src/DatesErp.Desktop/Views/Screens/ProductionDeliveryView.xaml.cs");

        // شرائح سياق الأمر بدل السطر النصي
        Assert.Contains("CustChip", xaml); Assert.Contains("PlanChip", xaml); Assert.Contains("ShiftChip", xaml);
        Assert.Contains("CustChip.Text", cs); Assert.Contains("PlanChip.Text", cs); Assert.Contains("ShiftChip.Text", cs);

        // بانر الحالة وجدولا البنود والمخلفات بالتنسيق الموحد
        Assert.Contains("x:Name=\"StatusLabel\"", xaml);
        Assert.Contains("AlternatingRowBackground=\"{DynamicResource ThemeAlternateRowBrush}\"", xaml);
        Assert.Contains("HorizontalGridLinesBrush=\"{DynamicResource ThemeGridLineBrush}\"", xaml);
        Assert.Contains("RowHeight=\"32\"", xaml);
        Assert.Contains("FieldLabel", xaml);

        // العقود الوظيفية باقية: المحدد، حقول الفعلي، المخلفات، وزر الحفظ الأخضر الموحد
        Assert.Contains("x:Name=\"OrderBox\"", xaml);
        Assert.Contains("x:Name=\"ItemsGrid\"", xaml);
        Assert.Contains("x:Name=\"SecondaryGrid\"", xaml);
        Assert.Contains("x:Name=\"RawBox\"", xaml);
        Assert.Contains("x:Name=\"DowntimeBox\"", xaml);
        Assert.Contains("x:Name=\"ReasonBox\"", xaml);
        Assert.Contains("x:Name=\"NotesBox\"", xaml);
        Assert.Contains("x:Name=\"SaveButton\"", xaml);
        Assert.Contains("ErpApproveButton", xaml);
        Assert.Contains("SaveActualProduction", cs);
        Assert.Contains("CanRecord", cs);
        // السطر النصي القديم زال لصالح الشرائح
        Assert.DoesNotContain("SourceLabel", xaml);
        Assert.DoesNotContain("SourceLabel", cs);
    }

    [Fact]
    public void Plans_Search_Popup_Has_Status_Filter_And_Column_Sort()
    {
        // §فلترة نافذة الخطط: شرائح حالة (الكل + حالات الخطط) تُدمج مع البحث النصي،
        // والنقر على رأس أي عمود يرتّب به مع سهم اتجاه.
        string popup = Read("src/DatesErp.Desktop/Views/PlanSearchWindow.cs");

        // الفلتر بالحالة
        Assert.Contains("_statusFilter", popup);
        Assert.Contains("BuildStatusFilter", popup);
        Assert.Contains("\"الكل\"", popup);
        Assert.Contains("GroupName = \"planStatusFilter\"", popup);

        // الترتيب بالنقر على رأس العمود مع أسهم الاتجاه
        Assert.Contains("OnColumnSort", popup);
        Assert.Contains("Sorting += OnColumnSort", popup);
        Assert.Contains("SortMemberPath", popup);
        Assert.Contains("UpdateHeaderGlyphs", popup);
        Assert.Contains("SortFiltered", popup);

        // عقد الإنزال بالنقر المزدوج باقٍ كما هو
        Assert.Contains("MouseDoubleClick", popup);
        Assert.Contains("SelectedPlanId", popup);
        Assert.Contains("DialogResult = true", popup);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // §الفحص الشامل (v1.50.19): لا نافذة بلا نقطة دخول — «كود بلا واجهة»
    // اكتُشف هذا الحارس بعد أن وُجدت نافذة «المجموعات والفئات» بلا فتّاح منذ
    // إعادة بناء بطاقة الصنف، فأصبحت SaveItemGroup/SaveItemCategory بلا واجهة.
    // ═════════════════════════════════════════════════════════════════════════
    [Fact]
    public void Every_Window_Class_Is_Reachable_From_Another_File()
    {
        string root = RepoRoot();
        string dir = Path.Combine(root, "src/DatesErp.Desktop");
        var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').Contains("/obj/") && !f.Replace('\\', '/').Contains("/bin/"))
            .ToList();
        var texts = files.ToDictionary(f => f, File.ReadAllText);

        var windows = new List<(string Name, string File)>();
        foreach (var (file, text) in texts)
            foreach (Match m in Regex.Matches(text, @"\bclass (\w*Window)\b"))
                windows.Add((m.Groups[1].Value, file));

        Assert.True(windows.Count >= 20, "قراءة نوافذ الواجهة فشلت (" + windows.Count + ").");
        // «الملف الحي»: إن كانت النافذة تُستخدم داخل ملفها (أكثر من مجرد تصريحها) وكان ذلك
        // الملف يحوي صنفاً آخر يذكره ملفات خارجية (مثل الشاشة نفسها) فهي متاحة عبره — وإلا فهي يتيمة.
        var classNamesPerFile = texts.ToDictionary(kv => kv.Key, kv =>
            Regex.Matches(kv.Value, @"\b(?:class|record)\s+(\w+)").Select(m => m.Groups[1].Value).Distinct().ToList());
        bool fileIsLive(string file) => classNamesPerFile[file].Any(c =>
            texts.Where(kv => kv.Key != file).Any(kv => kv.Value.Contains(c)));
        foreach (var (name, file) in windows)
        {
            bool usedInOwnFile = Regex.Matches(texts[file], @"\b" + Regex.Escape(name) + @"\b").Count >= 2;
            bool reachable = texts.Where(kv => kv.Key != file).Any(kv => kv.Value.Contains(name))
                || (usedInOwnFile && fileIsLive(file));
            Assert.True(reachable,
                $"النافذة «{name}» لا يذكرها أي ملف آخر في الواجهات — نافذة بلا نقطة دخول (كود بلا واجهة). " +
                "إما ربطها بزر/مسار فتح أو حذفها.");
        }
    }

}
