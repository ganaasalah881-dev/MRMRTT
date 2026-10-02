using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Views.Screens;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        Microsoft.Data.SqlClient.SqlConnectionStringBuilder? sqlMaster = null;
        string? sqlDatabase = null;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/MfgSystem;component/Themes/DateErpTheme.xaml") });
            string? configured = Environment.GetEnvironmentVariable("DATEERP_TEST_SQL");
            if (args.Contains("--sql"))
            {
                if (string.IsNullOrWhiteSpace(configured)) throw new InvalidOperationException("DATEERP_TEST_SQL required; no SQLite fallback.");
                sqlMaster = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configured) { InitialCatalog = "master" };
                sqlDatabase = "DateERP_WpfAcceptance_" + Guid.NewGuid().ToString("N");
                configured = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(sqlMaster.ConnectionString) { InitialCatalog = sqlDatabase }.ConnectionString;
            }
            else configured = null;
            using var host = new DatesErp.Tests.TestHost(sqlConnection: configured); host.LoginAsAdmin();
            PlanningScenarios.Run(host.Services, Check);
            using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(host.Services);
            var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<DatesErpDbContext>(scope.ServiceProvider);
            var plan = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IPlanningService>(scope.ServiceProvider);
            var shift = db.Shifts.Single(s => s.Id == 1);
            int rawA = db.Products.Single(p => p.ProductCode == "ACC-RA").Id, rawB = db.Products.Single(p => p.ProductCode == "ACC-RB").Id;
            int rawEmpty = db.Products.Single(p => p.ProductCode == "ACC-RC").Id;
            int finA = db.Products.Single(p => p.ProductCode == "ACC-A8").Id, finB = db.Products.Single(p => p.ProductCode == "ACC-A4").Id;
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ICapacityService>(scope.ServiceProvider).SetCapacity(finB, 1, 5000);
            var date = new DateTime(2026, 10, 5);
            var a = new LotEditorRow { ProductId = finA, LotCode = "A", ShiftId = 1, DateValue = date };
            var b = new LotEditorRow { ProductId = finB, LotCode = "B", ShiftId = 1, DateValue = date };
            var c = new LotEditorRow { ProductId = finA, LotCode = "C", ShiftId = 1, DateValue = date };
            var rows = new List<LotEditorRow> { a, b, c };
            foreach (var row in rows)
            {
                row.RawOptions = new() { new() { Id = rawA, Name = "سكري خام" }, new() { Id = rawB, Name = "برحي خام" }, new() { Id = rawEmpty, Name = "خام بلا تام" } };
                row.LoadFinishedProducts = raw => plan.GetFinishedProductsForRaw(raw).Select(p => new ProductOption { Id = p.Id, Name = p.ProductNameAr }).ToList();
                row.RawProductId = rawA;
            }
            var evaluator = new PlanningCapacityEvaluator(db);
            var window = new LotsEditorWindow(rows, "اختبار الطاقة التراكمية — بيانات مصطنعة فقط", false,
                date, date, new() { shift }, 1,
                additions => evaluator.Evaluate(additions, null, "05/10/2026", "05/10/2026", 1, 1), 1);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var grid = (DataGrid)typeof(LotsEditorWindow).GetField("_grid", flags)!.GetValue(window)!;
            var column = grid.Columns.OfType<DataGridTemplateColumn>().Single(x => Equals(x.Header, "الكراتين"));
            TextBox Editor(LotEditorRow row)
            { var box = (TextBox)column.CellTemplate.LoadContent(); box.DataContext = row; Flush(); return box; }
            void Enter(TextBox box, string value)
            { box.Text = value; box.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); Flush(); }
            PlanCapacityResult Current() => evaluator.Evaluate(rows.Where(r => r.IsChecked).Select(r => LotsEditorWindow.CapacityItem(r, 1)).ToList());
            var boxA = Editor(a); var boxB = Editor(b); var boxC = Editor(c);
            window.Show(); Flush();
            var rawColumn = grid.Columns.OfType<DataGridTemplateColumn>().Single(x => Equals(x.Header, "الصنف الخام *"));
            var rawCombo = (ComboBox)rawColumn.CellTemplate.LoadContent(); rawCombo.DataContext = a;
            var finishedColumn = grid.Columns.OfType<DataGridTemplateColumn>().First(x => x.Header?.ToString()?.Contains("الصنف التام") == true);
            var finishedCombo = (ComboBox)finishedColumn.CellTemplate.LoadContent(); finishedCombo.DataContext = a; Flush();
            Check(finishedCombo.Items.Count == 2, "Real WPF finished ComboBox contains only Sukkari 8 and 4");
            finishedCombo.SelectedValue = finA; Flush(); rawCombo.SelectedValue = rawB; Flush();
            Check(finishedCombo.Items.Count == 1 && a.ProductId == null, "Changing real raw ComboBox clears old finished selection");
            rawCombo.SelectedValue = rawEmpty; Flush(); Check(finishedCombo.Items.Count == 0 && a.LinkMessage == "لا توجد أصناف تامة مرتبطة بهذا الصنف الخام.", "Empty relationship blocks selection with exact message");
            rawCombo.SelectedValue = rawA; Flush(); finishedCombo.SelectedValue = finA; Flush();

            a.ReloadFinishedProducts(); Flush(); Check(a.ProductId == finA, "Revalidating unchanged Master list preserves selected finished item");
            Enter(boxA, "3000"); Check(a.IsChecked && Math.Abs(Current().UsagePercent - 60) < 1e-7, "3000 accepted; real WPF binding => 60 percent");
            Enter(boxB, "3500"); Check(Validation.GetHasError(boxB) && !b.IsChecked && b.CartonsText == "0", "3500 rejected immediately; bound source unchanged");
            Check(b.QuantityError.Contains("2,000") && b.QuantityError.Contains("1,500"), "Maximum 2000 and excess 1500 visible");
            Enter(boxB, "2000"); Check(!Validation.GetHasError(boxB) && Current().UsagePercent == 100, "2000 accepted, clears binding error; 100 percent");
            Enter(boxC, "1"); Check(Validation.GetHasError(boxC) && !c.IsChecked, "Any additional carton rejected");
            a.IsChecked = false; Flush(); Check(Math.Abs(Current().RemainingHours * 625 - 3000) < 1e-7, "Deselect/delete frees 3000 immediately");
            Enter(boxB, "1000"); Check(Math.Abs(Current().UsagePercent - 20) < 1e-7, "Edit refreshes percentage to 20");
            var summary = (TextBlock)typeof(LotsEditorWindow).GetField("_capacityBar", flags)!.GetValue(window)!;
            Check(summary.Text.Contains("20.00"), "Visible capacity bar is synchronized");
            Check(!string.IsNullOrEmpty(sqlDatabase) || configured == null, "Only an isolated test database was used");
            window.Close(); Console.WriteLine($"PASS: {_checks} actual WPF checks. No configured database was opened."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            app.Shutdown();
            if (sqlDatabase?.StartsWith("DateERP_WpfAcceptance_") == true && sqlMaster != null)
            {
                Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
                using var connection = new Microsoft.Data.SqlClient.SqlConnection(sqlMaster.ConnectionString); connection.Open();
                using var command = new Microsoft.Data.SqlClient.SqlCommand($"IF DB_ID('{sqlDatabase}') IS NOT NULL BEGIN ALTER DATABASE [{sqlDatabase}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{sqlDatabase}]; END", connection);
                command.ExecuteNonQuery();
            }
        }
    }
    private static void Check(bool value, string label)
    { if (!value) throw new InvalidOperationException(label); Console.WriteLine("PASS: " + label); _checks++; }
    private static void Flush() => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
        () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
}
