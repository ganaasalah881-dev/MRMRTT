using DatesErp.Core.Interfaces.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §حارس فهرس التقارير — يمنع تكرار العيب الذي وقع فعلاً:
/// التقريران shipment_full و shipment_tracking كانا **منفَّذين بالكامل** (204 سطر
/// + 187 سطر اختبارات) لكن بلا ReportDefinition، فلم يظهرا في القائمة ولم يستطع
/// أحد فتحهما. الاختبارات القائمة لم تكتشف ذلك لأنها تستدعي Run(code) مباشرةً
/// **متجاوزةً القائمة** التي يراها المستخدم.
///
/// الدرس: اختبار يستدعي تقريراً بكوده لا يُثبت أن المستخدم يستطيع الوصول إليه.
/// هذه الاختبارات تقيس التقاطع بين ما يُعرَض وما يعمل، في الاتجاهين.
/// </summary>
public class ReportCatalogIntegrityTests
{
    private static T Svc<T>(TestHost host) => (T)host.Services.CreateScope().ServiceProvider.GetService(typeof(T));

    /// <summary>كل تقرير معروض في القائمة يجب أن يُرجع نتيجة — لا null ولا استثناء.</summary>
    [Fact]
    public void Every_Listed_Report_Actually_Runs()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc<IReportService>(host);

        var broken = new List<string>();
        foreach (var def in svc.GetReports())
        {
            try
            {
                var r = svc.Run(def.Code, new Dictionary<string, string>());
                // §null هو العَرَض الدقيق للتقرير المعرَّف بلا منفِّذ: الموزِّع يسقط
                // إلى default ولا يجد له حالة فيُرجع null بلا استثناء — عطل صامت.
                if (r == null) broken.Add($"{def.Code} (يُرجع null — معرَّف بلا منفِّذ)");
                else if (r.Columns.Count == 0) broken.Add($"{def.Code} (بلا أعمدة)");
            }
            catch (Exception ex)
            {
                broken.Add($"{def.Code} (استثناء: {ex.GetType().Name}: {ex.Message})");
            }
        }

        Assert.True(broken.Count == 0,
            "تقارير معروضة في القائمة ولا تعمل:\n  - " + string.Join("\n  - ", broken));
    }

    /// <summary>لا كود مكرر — التكرار يجعل الاختيار في الواجهة غير محدد.</summary>
    [Fact]
    public void Report_Codes_Are_Unique()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var dups = Svc<IReportService>(host).GetReports()
            .GroupBy(r => r.Code).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dups.Count == 0, "أكواد تقارير مكررة: " + string.Join(", ", dups));
    }

    /// <summary>كل تقرير يحمل عنواناً وفئة — الفئة هي أساس التجميع في الشاشة.</summary>
    [Fact]
    public void Every_Report_Has_Title_And_Category()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var bad = Svc<IReportService>(host).GetReports()
            .Where(r => string.IsNullOrWhiteSpace(r.TitleAr) || string.IsNullOrWhiteSpace(r.Category))
            .Select(r => r.Code).ToList();
        Assert.True(bad.Count == 0, "تقارير بلا عنوان أو فئة: " + string.Join(", ", bad));
    }

    /// <summary>
    /// التقريران اللذان كانا محجوبين — تثبيت صريح كي لا يسقط تعريفهما مجدداً في
    /// أي إعادة هيكلة لاحقة.
    /// </summary>
    [Fact]
    public void Shipment_Reports_Are_Reachable_From_Catalog()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc<IReportService>(host);
        var defs = svc.GetReports();
        var codes = defs.Select(r => r.Code).ToList();

        Assert.Contains("shipment_full", codes);
        Assert.Contains("shipment_tracking", codes);

        // §وفلتر الشحنة يجب أن يكون موجوداً فعلاً: ReportService كان يملأ خيارات
        // هذا الفلتر لتعريف غير موجود، فكانت الحلقة تدور على مجموعة فارغة أبداً.
        var tracking = defs.First(r => r.Code == "shipment_tracking");
        Assert.Contains(tracking.Parameters, p => p.Key == "shipment");
    }

    /// <summary>عدد الروابط يطابق عدد الصفوف حين توجد — وإلا فتح المستند يصيب الصف الخطأ.</summary>
    [Fact]
    public void RowLinks_Align_With_Rows_When_Present()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc<IReportService>(host);
        foreach (var def in svc.GetReports())
        {
            var r = svc.Run(def.Code, new Dictionary<string, string>());
            if (r?.RowLinks == null || r.RowLinks.Count == 0) continue;
            Assert.True(r.RowLinks.Count == r.Rows.Count,
                $"{def.Code}: عدد الروابط {r.RowLinks.Count} لا يطابق عدد الصفوف {r.Rows.Count}");
        }
    }

    /// <summary>كل صف يطابق عدد الأعماد — خلل هنا يزيح البيانات عموداً كاملاً.</summary>
    [Fact]
    public void Every_Row_Matches_Column_Count()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc<IReportService>(host);
        foreach (var def in svc.GetReports())
        {
            var r = svc.Run(def.Code, new Dictionary<string, string>());
            if (r == null) continue;
            foreach (var row in r.Rows)
                Assert.True(row.Length == r.Columns.Count,
                    $"{def.Code}: صف بـ{row.Length} خلية مقابل {r.Columns.Count} عمود");
        }
    }
}
