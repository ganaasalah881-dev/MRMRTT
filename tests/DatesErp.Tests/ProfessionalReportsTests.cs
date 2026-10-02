using DatesErp.Core.Interfaces.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §العملية الشاملة لتطوير التقارير — الحزمة الاحترافية (7 تقارير):
/// مسجلة في الفهرس، تعمل دون استثناء على قاعدة جديدة، وعدد خلايا كل صف يطابق الأعمدة،
/// والإجماليات الرقمية متناسقة مع الصفوف.
/// </summary>
public class ProfessionalReportsTests
{
    private static T Svc<T>(TestHost host) => (T)host.Services.CreateScope().ServiceProvider.GetService(typeof(T));

    private static readonly string[] Codes =
    {
        "raw_inventory", "finished_inventory", "aux_consumption", "secondary_outputs",
        "capacity_utilization", "plan_vs_actual", "order_execution"
    };

    [Fact]
    public void Professional_Reports_Are_Registered_In_Catalog()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc<IReportService>(host);
        var codes = svc.GetReports().Select(r => r.Code).ToList();
        foreach (var c in Codes) Assert.Contains(c, codes);
    }

    [Fact]
    public void Professional_Reports_Run_Clean_On_Fresh_Db()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc<IReportService>(host);
        foreach (var c in Codes)
        {
            var r = svc.Run(c, new Dictionary<string, string>());
            Assert.NotNull(r);
            Assert.True(r.Columns.Count > 0, c);
            foreach (var row in r.Rows)
                Assert.True(row.Length == r.Columns.Count, $"{c}: row/column mismatch");
            if (r.RowLinks != null && r.RowLinks.Count > 0) Assert.Equal(r.Rows.Count, r.RowLinks.Count);
        }
    }

    [Fact]
    public void Professional_Reports_Totals_Match_Rows()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc<IReportService>(host);
        var r = svc.Run("plan_vs_actual", new Dictionary<string, string>());
        Assert.NotNull(r);
        // §الإجمالي في الملخص = مجموع عمود المخطط إن وُجدت صفوف
        double sumRows = r.Rows.Sum(x => Convert.ToDouble(x[3]));
        if (r.Rows.Count > 0)
            Assert.Contains(r.Summary, kv => kv.Key.Contains("المخطط") && Math.Abs(double.Parse(kv.Value.Replace(",", "")) - sumRows) < 0.5);
    }
}
