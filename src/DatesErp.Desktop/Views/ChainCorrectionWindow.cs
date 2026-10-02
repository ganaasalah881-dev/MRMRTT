using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DatesErp.Application.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views;

/// <summary>
/// §C1 — معالج «تصحيح السلسلة»: يعرض شجرة المستندات التابعة لشحنة (دفعات/خطط/أوامر/تنفيذ)
/// وما سيفعله المعالج بكل عقدة، ثم ينفذ الفك المتسلسل بعكس البناء بسبب مكتوب إجباري.
/// محكوم بصلاحية receiving/ChainCorrection الحساسة (الزر نفسه مخفي لمن لا يملكها).
/// </summary>
public class ChainCorrectionWindow : Window
{
    private readonly int _shipmentId;
    private readonly DataGrid _grid;
    private readonly TextBox _reason;
    private readonly Button _run;

    public ChainCorrectionWindow(int shipmentId)
    {
        _shipmentId = shipmentId;
        Title = "معالج تصحيح السلسلة — فك مترابط موثق";
        Width = 900; Height = 600; MinWidth = 720; MinHeight = 480;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Text = "التصحيح = عكس البناء: تُلغى الأوامر (التي لم تُنفَّذ) بسبب، ثم تُفك الخطط وتُحذف، ثم يُلغى اعتماد الشحنة بقيد عكسي. "
                 + "أي أمر نُفّذ فعلياً يظهر كمانع ⛔ — يُقفل يدوياً بتسوية أولاً. كل خطوة تُسجَّل في التدقيق باسمك والسبب."
        };
        Grid.SetRow(hint, 0);
        root.Children.Add(hint);

        _grid = new DataGrid { IsReadOnly = true, RowHeight = 28, AutoGenerateColumns = false };
        _grid.Columns.Add(new DataGridTextColumn { Header = "المستوى", Binding = new Binding("Level"), Width = 60 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "النوع", Binding = new Binding("DocType"), Width = 110 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "الرقم", Binding = new Binding("DocNumber"), Width = 130 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "الحالة", Binding = new Binding("StatusAr"), Width = 100 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "تفاصيل", Binding = new Binding("Info"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "إجراء المعالج", Binding = new Binding("AutoAction"), Width = 200 });
        Grid.SetRow(_grid, 1);
        root.Children.Add(_grid);

        var reasonPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        reasonPanel.Children.Add(new TextBlock { Text = "السبب (إجباري — يُسجَّل في التدقيق):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _reason = new TextBox { Width = 420 };
        reasonPanel.Children.Add(_reason);
        Grid.SetRow(reasonPanel, 2);
        root.Children.Add(reasonPanel);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        _run = new Button { Content = "⚙ تنفيذ التصحيح المتسلسل", Width = 220, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
        _run.Click += Run_Click;
        btns.Children.Add(_run);
        var close = new Button { Content = "إغلاق", Width = 100, Height = 34 };
        close.Click += (_, _) => Close();
        btns.Children.Add(close);
        Grid.SetRow(btns, 3);
        root.Children.Add(btns);

        Content = root;
        Loaded += (_, _) => LoadChain();
    }

    private void LoadChain()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<CorrectionService>();
            _grid.ItemsSource = svc.GetShipmentChain(_shipmentId);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ChainCorrection.Load"); }
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_reason.Text))
        { AppContainer.Get<DialogService>().Error("اكتب سبب التصحيح — إجباري ويُسجَّل في التدقيق."); return; }
        if (!AppContainer.Get<DialogService>().Confirm("سيُنفَّذ الفك المتسلسل (أوامر ← خطط ← إلغاء اعتماد الشحنة) بالسبب المكتوب. متابعة؟")) return;
        try
        {
            _run.IsEnabled = false;
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<CorrectionService>();
            var r = svc.RunShipmentCascade(_shipmentId, _reason.Text.Trim());
            if (r.Ok)
            {
                AppContainer.Get<DialogService>().Info(r.Message);
                DialogResult = true;
                Close();
            }
            else AppContainer.Get<DialogService>().Error(r.Message);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ChainCorrection.Run"); }
        finally { _run.IsEnabled = true; }
    }
}
