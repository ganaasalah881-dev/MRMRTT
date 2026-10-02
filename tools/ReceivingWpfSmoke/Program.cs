using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Desktop.Mvvm;
using DatesErp.Desktop.Views.Screens;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/MfgSystem;component/Themes/DateErpTheme.xaml") });
            // Do not Show() the receiving view: no DB initialization / Loaded event / production connection.
            var defaultGrid = new DataGrid { Style = (Style)app.FindResource(typeof(DataGrid)) };
            Check(defaultGrid.IsReadOnly, "Actual ERP theme defaults DataGrid to read-only");
            var view = new ReceivingView();
            var grid = (DataGrid)view.FindName("ItemsGrid");
            Check(!grid.IsReadOnly, "Receiving grid explicitly overrides theme in new mode");
            var row = new ReceivingItemRow { ProductName = "خام تجريبي" };
            ((IList)grid.ItemsSource).Add(row);
            Check(row.TreatmentRequired == null, "New row has no implicit Yes or No");
            ApplyMode(view, "Edit", false);
            Check(!grid.IsReadOnly && row.IsEditable, "Draft edit mode enables destination/status and row actions");
            ApplyMode(view, "View", false);
            Check(grid.IsReadOnly && !row.IsEditable, "Saved draft view mode locks editing");
            ApplyMode(view, "View", true);
            Check(grid.IsReadOnly && !row.IsEditable, "Approved mode locks editing");
            var banner = (Border)view.FindName("LockBanner");
            Check(banner.Visibility == Visibility.Visible && DockPanel.GetDock(banner) == Dock.Top,
                "Approved banner is top-docked, never an accidental side column");
            ApplyMode(view, "Edit", false);
            var choiceColumn = (DataGridTemplateColumn)grid.Columns.Single(c => Equals(c.Header, "المعالجة *"));
            var choice = (ComboBox)choiceColumn.CellTemplate.LoadContent(); choice.DataContext = row;
            var untilColumn = grid.Columns.OfType<DataGridTemplateColumn>().Single(c => c.Header == null);
            var panel = (StackPanel)untilColumn.CellTemplate.LoadContent(); panel.DataContext = row;
            var picker = panel.Children.OfType<DatePicker>().Single();
            Flush();
            Check(panel.Visibility == Visibility.Collapsed, "Until date hidden for missing decision");
            choice.SelectedItem = "نعم"; Flush();
            Check(row.TreatmentRequired == true && panel.Visibility == Visibility.Visible, "Yes reveals DatePicker in same row");
            picker.SelectedDate = new DateTime(2026, 10, 8); Flush();
            Check(row.TreatmentUntilDate == new DateTime(2026, 10, 8), "Arbitrary month date reaches row binding");
            choice.SelectedItem = "لا"; Flush();
            Check(row.TreatmentRequired == false && row.TreatmentUntilDate == null && panel.Visibility == Visibility.Collapsed,
                "No clears persisted input and hides both label and picker");
            ApplyMode(view, "View", true); Flush();
            Check(!choice.IsEnabled && !picker.IsEnabled, "Approved inline controls cannot edit choice or date");
            Console.WriteLine($"PASS: {_checks} real WPF checks; no database opened.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { app.Shutdown(); }
    }
    private static void ApplyMode(ReceivingView view, string mode, bool approved)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ReceivingView).GetField("_mode", flags)!.SetValue(view, mode);
        typeof(ReceivingView).GetField("_approved", flags)!.SetValue(view, approved);
        typeof(ReceivingView).GetMethod("ApplyMode", flags)!.Invoke(view, null);
    }
    private static void Flush() => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
        () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message); _checks++;
    }
}
