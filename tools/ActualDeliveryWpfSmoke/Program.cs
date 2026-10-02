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
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SqlConnectionStringBuilder? master = null; string? database = null;
        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/MfgSystem;component/Themes/DateErpTheme.xaml") });
            string? connection = null;
            if (args.Contains("--sql"))
            {
                master = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("DATEERP_TEST_SQL") ?? throw new Exception("DATEERP_TEST_SQL required; no fallback.")) { InitialCatalog = "master" };
                database = "DateERP_ActualWpf_" + Guid.NewGuid().ToString("N");
                connection = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = database }.ConnectionString;
            }
            using var host = new DatesErp.Tests.TestHost(sqlConnection: connection); host.LoginAsAdmin();
            using var scope = host.Services.CreateScope(); var sp = scope.ServiceProvider; var db = sp.GetRequiredService<DatesErpDbContext>();
            var masterData = sp.GetRequiredService<MasterDataService>(); var planning = sp.GetRequiredService<IPlanningService>(); var orders = sp.GetRequiredService<IProductionOrderService>();
            var raw = masterData.SaveProductFull(null, "WACT-R", "سكري خام", "001", "Raw", "كجم", 20, 0, 0, null!);
            var finished = masterData.SaveProductFull(null, "WACT-F", "سكري", "002", "Finished", "كرتون", 8, 1, 8, new() { (1, null!, 5000) }, raw.Id);
            var bp = masterData.SaveByProduct(null, "مخرج اختبار الواجهة", "كجم"); Check(raw.Ok && finished.Ok && bp.Ok, "Master definitions created by services");
            var receiving = sp.GetRequiredService<IReceivingService>();
            var ship = receiving.SaveShipment(1, null!, null!, new() { new() { ProductId = raw.Id, QtyKg = 40000, PackageCount = 2000, UnitWeightKg = 20, TreatmentRequired = false } });
            Check(ship.Ok && receiving.ApproveShipment(ship.Id).Ok, "Real raw receipt approved");
            int lot = db.Lots.AsNoTracking().Single(l => l.ShipmentId == ship.Id).Id; var day = db.BusinessNow.ToString("dd/MM/yyyy");
            var plan = planning.SavePlan("خطة سكري 3000", "Daily", day, day, 1, 1, new() { new() { ProductId = finished.Id, SelectedRawProductId = raw.Id, CustomerId = 1,
                SourceType = "FromReceiving", ShipmentId = ship.Id, LotId = lot, PlannedCartons = 3000, PlannedQtyKg = 24000, ScheduledDate = day, SuggestedShiftId = 1, SuggestedLineId = 1 } });
            Check(plan.Ok && planning.ApprovePlan(plan.Id).Ok && orders.IssueTodayOrders().Ok, "Approved today plan issued unchanged");
            int order = db.ProductionOrders.AsNoTracking().Single().Id; Check(orders.ApproveOrder(order).Ok && orders.StartOrder(order).Ok, "Order execution started through existing cycle");
            T Service<T>(Func<IProductionDeliveryService, T> action) { using var s = host.Services.CreateScope(); return action(s.ServiceProvider.GetRequiredService<IProductionDeliveryService>()); }
            var view = new ProductionDeliveryView(() => Service(s => s.GetActualDeliveryOrders()), () => Service(s => s.GetActualByProducts()), i => Service(s => s.SaveActualProduction(i)));
            var window = new Window { Content = view, Width = 1350, Height = 950, Title = "قبول واجهة تسجيل الفعلي — قاعدة معزولة فقط" }; window.Show(); Flush();
            var grid = (DataGrid)view.FindName("ItemsGrid"); var secondary = (DataGrid)view.FindName("SecondaryGrid");
            Check(grid.Items.Count == 1 && !grid.CanUserAddRows && !grid.CanUserDeleteRows, "Real WPF: one immutable source row, no add/delete");
            Check(grid.Columns.Where((_, i) => i != 4).All(c => c.IsReadOnly), "Real WPF: only actual cartons editable; customer/product/plan/unit/difference locked");
            Check(!Visuals(grid).OfType<ComboBox>().Any(), "No finished-product picker in actual grid");
            Check(((TextBox)view.FindName("RawBox")).Text == "" && ((ActualProductionRow)grid.Items[0]).Actual == "", "Raw and actual start empty, no plan-derived default");
            Edit(grid, 0, 4, "2700"); Check(((ActualProductionRow)grid.Items[0]).Difference == "300", "Actual TextBox binding computes 300 difference immediately");
            ((TextBox)view.FindName("RawBox")).Text = "22000"; ((TextBox)view.FindName("DowntimeBox")).Text = "1"; ((TextBox)view.FindName("ReasonBox")).Text = "عطل ماكينة";
            var byRow = (ActualSecondaryRow)secondary.Items[0]; var picker = Visuals(secondary).OfType<ComboBox>().Single(c => ReferenceEquals(c.DataContext, byRow));
            picker.SelectedItem = view.ByProductDefinitions.Single(d => d.Id == bp.Id); Flush(); Edit(secondary, 0, 1, "200");
            Check(byRow.Definition.Id == bp.Id && byRow.Unit == "كجم" && byRow.Quantity == "200", "Real secondary ComboBox and quantity binding use configured definition and unit");
            var save = (Button)view.FindName("SaveButton"); save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush();
            using var read = host.Services.CreateScope(); var stored = read.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var fg = stored.Warehouses.Single(w => w.WarehouseCode == "WFG");
            Check(stored.StockBalances.AsNoTracking().Any(b => b.WarehouseId == fg.Id && b.CustomerId == 1 && b.ProductId == finished.Id && b.LotId == lot && b.PackageCount == 2700 && b.QtyKg == 21600), "UI SAVE posts real 2700 cartons into correct customer/lot stock");
            Check(stored.QualityChecks.Count() == 1 && !stored.QualityChecks.Single().IsApproved && stored.ExecutionByProducts.Single().Qty == 200, "UI SAVE creates pending quality and persists selected waste");
            Check(!save.IsEnabled && grid.IsReadOnly && ((ActualProductionRow)grid.Items[0]).Difference == "300", "After SAVE, persisted screen is locked and difference remains 300");
            Console.WriteLine($"PASS {count}/{count}; actual Windows WPF events + {(master == null ? "SQLite" : "SQL Server")}; no screenshot substituted."); window.Close(); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            app.Shutdown();
            if (database?.StartsWith("DateERP_ActualWpf_") == true && master != null)
            {
                SqlConnection.ClearAllPools(); using var c = new SqlConnection(master.ConnectionString); c.Open();
                using var cmd = new SqlCommand($"IF DB_ID('{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END", c); cmd.ExecuteNonQuery();
            }
        }
    }
    private static void Edit(DataGrid grid, int row, int column, string text)
    {
        grid.SelectedIndex = row; grid.CurrentCell = new DataGridCellInfo(grid.Items[row], grid.Columns[column]); grid.ScrollIntoView(grid.Items[row]); grid.BeginEdit(); Flush();
        var edit = Visuals(grid).OfType<TextBox>().First(t => ReferenceEquals(t.DataContext, grid.Items[row])); edit.Text = text; edit.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true); Flush();
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); count++; Console.WriteLine("PASS: " + message); }
    private static void Flush() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<DependencyObject> Visuals(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) { var child = VisualTreeHelper.GetChild(parent, i); yield return child; foreach (var nested in Visuals(child)) yield return nested; }
    }
}
