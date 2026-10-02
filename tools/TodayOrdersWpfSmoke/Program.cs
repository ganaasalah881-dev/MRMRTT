using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Views.Screens;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static class Program
{
    private static int count;
    [STAThread]
    private static int Main(string[] args)
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        SqlConnectionStringBuilder? master = null; string? database = null;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/MfgSystem;component/Themes/DateErpTheme.xaml") });
            string? connection = null;
            if (args.Contains("--sql"))
            {
                var configured = Environment.GetEnvironmentVariable("DATEERP_TEST_SQL") ?? throw new Exception("DATEERP_TEST_SQL required; no SQLite fallback.");
                master = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" };
                database = "DateERP_TodayWpf_" + Guid.NewGuid().ToString("N");
                connection = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = database }.ConnectionString;
            }
            using var host = new DatesErp.Tests.TestHost(sqlConnection: connection); host.LoginAsAdmin();
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var items = scope.ServiceProvider.GetRequiredService<MasterDataService>();
            var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            var day = db.BusinessNow.Date; string D(DateTime d) => d.ToString("dd/MM/yyyy");
            var raw = items.SaveProductFull(null, "WPF-RAW", "سكري خام", "001", "Raw", "كجم", 20, 0, 0, null!);
            var a = items.SaveProductFull(null, "WPF-8", "سكري تام 8 كجم", "002", "Finished", "كرتون", 8, 1, 8, new() { (1, null!, 5000) }, raw.Id);
            var b = items.SaveProductFull(null, "WPF-4", "سكري تام 4 كجم", "002", "Finished", "كرتون", 4, 1, 4, new() { (1, null!, 5000) }, raw.Id);
            var third = items.SaveProductFull(null, "WPF-OTHER", "صنف خارج خطة اليوم", "002", "Finished", "كرتون", 4, 1, 4, new() { (1, null!, 5000) }, raw.Id);
            Check(raw.Ok && a.Ok && b.Ok && third.Ok, "Real Master Items create the two finished products and an unrelated catalogue option");
            PlanItemDto Row(int product, int cartons, DateTime date) => new() { ProductId = product, CustomerId = 1, SelectedRawProductId = raw.Id,
                PlannedCartons = cartons, PlannedQtyKg = cartons * (product == a.Id ? 8 : 4), ScheduledDate = D(date), SuggestedShiftId = 1, SuggestedLineId = 1 };
            var plan = planning.SavePlan("خطة اختبار WPF", "Daily", D(day), D(day), 1, 1, new() { Row(a.Id, 3000, day), Row(b.Id, 2000, day) });
            Check(plan.Ok, "Save the mandatory 3000 + 2000 plan");
            var yesterday = planning.SavePlan("أمس", "Daily", D(day.AddDays(-1)), D(day.AddDays(-1)), 1, 1, new() { Row(third.Id, 10, day.AddDays(-1)) });
            var tomorrow = planning.SavePlan("غد", "Daily", D(day.AddDays(1)), D(day.AddDays(1)), 1, 1, new() { Row(third.Id, 10, day.AddDays(1)) });
            Check(yesterday.Ok && tomorrow.Ok && planning.ApprovePlan(yesterday.Id).Ok && planning.ApprovePlan(tomorrow.Id).Ok, "Approve yesterday and tomorrow negative fixtures");
            var view = new OrdersView(orders.GetTodayProduction, orders.IssueTodayOrders);
            var window = new Window { Content = view, Width = 1400, Height = 780, Title = "اختبار أمر إنتاج اليوم — قاعدة معزولة فقط" };
            window.Show(); Flush();
            var grid = (DataGrid)view.FindName("TodayGrid");
            var issue = (Button)view.FindName("IssueTodayBtn");
            Check(grid.Items.Count == 0 && !issue.IsEnabled, "Actual WPF opens empty while today's plan is draft; no yesterday/tomorrow rows");
            Check(planning.ApprovePlan(plan.Id).Ok, "Approve today's plan with the real service");
            window.Close();
            view = new OrdersView(orders.GetTodayProduction, orders.IssueTodayOrders);
            window = new Window { Content = view, Width = 1400, Height = 780, Title = "أمر إنتاج اليوم — اختبار معزول" };
            window.Show(); Flush(); grid = (DataGrid)view.FindName("TodayGrid"); issue = (Button)view.FindName("IssueTodayBtn");
            Check(grid.Items.Count == 2, "Actual WPF shows exactly two planned products on opening, without selecting a plan");
            var rows = grid.Items.Cast<TodayProductionRowDto>().ToList();
            Check(rows.Single(r => r.ProductId == a.Id).PlannedCartons == 3000 && rows.Single(r => r.ProductId == b.Id).PlannedCartons == 2000, "WPF ItemsSource has exactly 3000 and 2000");
            grid.SelectedIndex = 0; grid.CurrentCell = new DataGridCellInfo(grid.Items[0], grid.Columns[2]); Flush();
            Check(grid.IsReadOnly && !grid.BeginEdit(), "Actual DataGrid refuses quantity editing");
            Check(!grid.CanUserAddRows && !grid.CanUserDeleteRows && !grid.AutoGenerateColumns, "Actual DataGrid cannot add or remove planned items");
            Check(!Visuals(view).Any(v => v is ComboBox || v is DatePicker), "No master-product picker or alternative-date picker exists in the screen");
            Check(((TextBlock)view.FindName("DayLabel")).Text.Contains(D(day)), "Visible day comes from the database business clock");
            Check(rows.All(r => r.CustomerId == 1 && r.Unit == "كرتون" && r.ShiftId == 1), "Client, unit and shift are carried from the approved plan");
            Check(!db.ProductionOrders.Any(), "Opening the WPF screen creates no orders");
            issue.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush();
            Check(db.ProductionOrders.Count() == 1 && db.ProductionOrderItems.Count() == 2, "Clicking the real issue button saves the exact two approved lines");
            Check(db.ProductionOrderItems.Sum(i => i.PlannedCartons) == 5000 && !db.ProductionOrderItems.Any(i => i.ProductId == third.Id), "Stored quantities are 5000 total with no catalogue-only product");
            Check(grid.Items.Count == 2 && !issue.IsEnabled, "After save the same two rows remain visible and duplicate issue is disabled");
            issue.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush();
            Check(db.ProductionOrders.Count() == 1, "Even a repeated button event creates no duplicate");
            var order = db.ProductionOrders.Single();
            Check(!orders.UpdateOrderItems(order.Id, new() { new() { Id = db.ProductionOrderItems.First().Id, PlannedCartons = 3500, PlannedQtyKg = 28000 } }).Ok,
                "Backend rejects a quantity change independent of WPF read-only protection");
            Check(!orders.SaveOrder("Manual", null, 1, D(day), 1, 1, new() { new() { ProductId = third.Id, PlannedCartons = 1, PlannedQtyKg = 4 } }).Ok,
                "Backend rejects a third item through a manual API call");
            window.Close(); Console.WriteLine($"PASS {count}/{count}; actual Windows WPF; provider={(database == null ? "SQLite" : "SQL Server")}"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            app.Shutdown();
            if (database?.StartsWith("DateERP_TodayWpf_") == true && master != null)
            {
                SqlConnection.ClearAllPools(); using var c = new SqlConnection(master.ConnectionString); c.Open();
                using var cmd = new SqlCommand($"IF DB_ID('{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END", c);
                cmd.ExecuteNonQuery(); Console.WriteLine("Removed only this runner's isolated database.");
            }
        }
    }
    private static void Check(bool ok, string message)
    { Console.WriteLine((ok ? "PASS: " : "FAIL: ") + message); if (!ok) throw new Exception(message); count++; }
    private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Visuals(child)) yield return nested; }
    }
}
