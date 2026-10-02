using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §B102.1 (إصلاح فحص) — لوحة «متاح العملاء»: ميزة B100 كانت مُرحّلة ومختبرة لكن بلا نقطة
/// دخول (كانت تُفتح من بطاقة في لوحة B101 الرئيسية). تعرض الملخص لكل عميل عبر كل الخطط
/// المعتمدة، ونقر مزدوج يفتح نافذة التفاصيل (بالأيام / دفعات مخزن التام / سجل التسليم).
/// </summary>
public class CustomerAvailabilityBoardView : UserControl
{
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, RowHeight = 30, IsReadOnly = true, FontSize = 13 };

    public CustomerAvailabilityBoardView()
    {
        FlowDirection = FlowDirection.RightToLeft;
        _grid.Columns.Add(new DataGridTextColumn { Header = "العميل", Binding = new System.Windows.Data.Binding("CustomerName"), Width = new DataGridLength(1.4, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "مخطط (كجم)", Binding = new System.Windows.Data.Binding("PlannedKg") { StringFormat = "N0" }, Width = 100 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "منتج (كجم)", Binding = new System.Windows.Data.Binding("ProducedKg") { StringFormat = "N0" }, Width = 100 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "في الفحص (كجم)", Binding = new System.Windows.Data.Binding("InInspectionKg") { StringFormat = "N0" }, Width = 100 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "مقبول (كجم)", Binding = new System.Windows.Data.Binding("AcceptedKg") { StringFormat = "N0" }, Width = 100 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "مسلَّم (كجم)", Binding = new System.Windows.Data.Binding("DeliveredKg") { StringFormat = "N0" }, Width = 100 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "قابل للتسليم الآن (كجم)", Binding = new System.Windows.Data.Binding("DeliverableKg") { StringFormat = "N0" }, Width = 140 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "الحالة", Binding = new System.Windows.Data.Binding("StatusAr"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.MouseDoubleClick += (_, _) => OpenDetails();

        var refresh = new Button { Content = "🔄 تحديث", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), Margin = new Thickness(0, 0, 8, 0) };
        refresh.Click += (_, _) => Load();
        var open = new Button { Content = "📂 تفاصيل العميل", Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton"), Margin = new Thickness(0, 0, 8, 0) };
        open.Click += (_, _) => OpenDetails();

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(refresh);
        bar.Children.Add(open);
        bar.Children.Add(new TextBlock
        {
            Text = "ما يمكن تحميله الآن لكل عميل (مقبول − مسلَّم) — نقرتان على أي صف تفتحان التفاصيل: الأيام، دفعات مخزن التام، سجل التسليم.",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = System.Windows.Media.Brushes.Gray,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap
        });

        var panel = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        panel.Children.Add(bar);
        panel.Children.Add(_grid);
        Content = panel;
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<ICustomerAvailabilityService>();
            _grid.ItemsSource = svc.GetBoardSummary();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "AvailBoard.Load"); }
    }

    private void OpenDetails()
    {
        if (_grid.SelectedItem is not CustomerAvailabilityDto row)
        { AppContainer.Get<DialogService>().Error("اختر عميلاً من اللوحة."); return; }
        var w = new Views.CustomerAvailabilityWindow(row.CustomerId) { Owner = Window.GetWindow(this) };
        w.ShowDialog();
        Load();
    }
}
