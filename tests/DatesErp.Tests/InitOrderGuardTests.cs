using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §حارس ترتيب التهيئة (B53): يمنع عودة أعطال «الحدث أثناء InitializeComponent».
///
/// في WPF يُطلق TextChanged/SelectionChanged أثناء InitializeComponent لحظة تطبيق
/// Text="..." أو SelectedIndex — وقبل إنشاء العناصر اللاحقة في XAML. فإن لمس المعالج
/// عنصراً لم يُنشأ بعد وقعت NullReferenceException ورفضت الشاشة أن تُفتح
/// (عطل شاشة الأصناف B52 وعطل شاشة الاتصال B49 من العائلة نفسها).
///
/// القاعدة المفحوصة: أي معالج TextChanged موصول بعنصر يحمل Text ابتدائياً يجب أن يحرس
/// null على **كل** عنصر مسمّى يلمسه، لا على عنصر واحد فقط.
/// </summary>
public class InitOrderGuardTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }


    [Fact]
    public void Other_InitTime_Handlers_Guard_The_Latest_Control()
    {
        // الورديات والاستلام: الحرس يفحص آخر عنصر يُنشأ فيترتب عليه سلامة البقية —
        // نكتفي بالتأكد من بقاء الحرس موجوداً حتى لا يعود العطل خفية.
        var shifts = File.ReadAllText(Path.Combine(Root(), "src/DatesErp.Desktop/Views/Screens/ShiftsView.xaml.cs"));
        Assert.Contains("if (EffBox == null) return;", shifts);
        var rec = File.ReadAllText(Path.Combine(Root(), "src/DatesErp.Desktop/Views/Screens/ReceivingView.xaml.cs"));
        Assert.Contains("if (CalcTotalBox == null) return;", rec);
    }

    [Fact]
    public void ServerSide_Queries_Never_Use_Math_Max_Min()
    {
        // §B55: مزوّد SQL Server لا يترجم Math.Max/Math.Min داخل IQueryable
        // (عطل شاشة الخطط على DateFactory) — البديل شرطٌ ثلاثي يُترجم إلى CASE WHEN.
        // على مستوى النص: أي Sum/Where/Select يستدعِ Math.Max على أعمدة = عطل مؤكد على SQL Server.
        var dir = Path.Combine(Root(), "src/DatesErp.Application/Services");
        foreach (var f in Directory.GetFiles(dir, "*.cs"))
        {
            var lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                bool inQuery = l.Contains(".Sum(") || l.Contains(".Where(") || l.Contains(".Select(");
                bool mathOnColumns = l.Contains("Math.Max(0, x.") || l.Contains("Math.Min(0, x.")
                                     || l.Contains(".Sum(x => Math.Max") || l.Contains(".Sum(i => Math.Max");
                Assert.False(inQuery && mathOnColumns,
                    $"{Path.GetFileName(f)}:{i + 1} — Math.Max/Min داخل استعلام يُترجم للخادم: استخدم شرطاً ثلاثياً (a > 0 ? a : 0).");
            }
        }
    }
}
