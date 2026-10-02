using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views;

/// <summary>
/// §C2 — نافذة «تصحيح سند معتمد»: تعديل كمية بند و/أو تصحيح العميل لسند استلام معتمد
/// بقيد فرق موثق وسبب إجباري — بلا فك السلسلة. تعمل فقط ما دامت الدفعات لم تُستهلك
/// ولم تُبنَ عليها خطط (وإلا رفضت الخدمة وأحالت إلى معالج السلسلة).
/// محكومة بصلاحية receiving/CorrectApprovedQty الحساسة.
/// </summary>
public class QtyCorrectionWindow : Window
{
    public class CorrRow
    {
        public int ItemId { get; set; }
        public string Product { get; set; }
        public string LotCode { get; set; }
        public double OldQty { get; set; }
        public double NewQty { get; set; }
    }

    private readonly int _shipmentId;
    private readonly DataGrid _grid;
    private readonly ComboBox _custBox;
    private readonly TextBox _reason;
    private int? _originalCustomerId;

    public QtyCorrectionWindow(int shipmentId)
    {
        _shipmentId = shipmentId;
        Title = "تصحيح سند استلام معتمد — كمية/عميل بقيد موثق";
        Width = 860; Height = 560; MinWidth = 700; MinHeight = 440;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Text = "عدّل «الكمية المصححة» للبنود الخاطئة فقط (اترك الباقي كما هو)، و/أو اختر العميل الصحيح. "
                 + "يُسجَّل قيد فرق في دفتر الحركة بالسبب، ويُحدَّث الرصيد والدفعة والسند — بلا حذف لأي أثر. "
                 + "إن كانت الدفعات استُهلكت أو بُنيت عليها خطط فسيرفض النظام ويحيلك إلى معالج «تصحيح السلسلة»."
        };
        Grid.SetRow(hint, 0);
        root.Children.Add(hint);

        _grid = new DataGrid { RowHeight = 28, AutoGenerateColumns = false };
        _grid.Columns.Add(new DataGridTextColumn { Header = "الصنف", Binding = new Binding("Product"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "الدفعة", Binding = new Binding("LotCode"), Width = 130, IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "الكمية الأصلية (كجم)", Binding = new Binding("OldQty"), Width = 130, IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "الكمية المصححة (كجم) *", Binding = new Binding("NewQty"), Width = 150 });
        Grid.SetRow(_grid, 1);
        root.Children.Add(_grid);

        var custPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        custPanel.Children.Add(new TextBlock { Text = "تصحيح العميل (اختياري):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _custBox = new ComboBox { Width = 300, DisplayMemberPath = "CustomerName", SelectedValuePath = "Id" };
        custPanel.Children.Add(_custBox);
        Grid.SetRow(custPanel, 2);
        root.Children.Add(custPanel);

        var reasonPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        reasonPanel.Children.Add(new TextBlock { Text = "السبب (إجباري — يُسجَّل في التدقيق):", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _reason = new TextBox { Width = 420 };
        reasonPanel.Children.Add(_reason);
        Grid.SetRow(reasonPanel, 3);
        root.Children.Add(reasonPanel);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var run = new Button { Content = "✔ تنفيذ التصحيح الموثق", Width = 220, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
        run.Click += Run_Click;
        btns.Children.Add(run);
        var close = new Button { Content = "إغلاق", Width = 100, Height = 34 };
        close.Click += (_, _) => Close();
        btns.Children.Add(close);
        Grid.SetRow(btns, 4);
        root.Children.Add(btns);

        Content = root;
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var ship = db.Shipments.AsNoTracking().Include(x => x.Items).Include(x => x.Lots).FirstOrDefault(x => x.Id == _shipmentId);
            if (ship == null) { AppContainer.Get<DialogService>().Error("السند غير موجود."); Close(); return; }
            _originalCustomerId = ship.CustomerId;
            _grid.ItemsSource = ship.Items.Select(i => new CorrRow
            {
                ItemId = i.Id,
                Product = db.Products.AsNoTracking().Where(p => p.Id == i.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"#{i.ProductId}",
                LotCode = ship.Lots.FirstOrDefault(l => l.ShipmentItemId == i.Id)?.LotCode ?? "—",
                OldQty = i.TotalWeightKg,
                NewQty = i.TotalWeightKg
            }).ToList();
            var customers = db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.CustomerName).ToList();
            _custBox.ItemsSource = customers;
            _custBox.SelectedItem = customers.FirstOrDefault(c => c.Id == ship.CustomerId);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "QtyCorrection.Load"); }
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_reason.Text))
        { AppContainer.Get<DialogService>().Error("اكتب سبب التصحيح — إجباري ويُسجَّل في التدقيق."); return; }
        var rows = (_grid.ItemsSource as List<CorrRow>) ?? new List<CorrRow>();
        var corrections = rows.Where(r2 => System.Math.Abs(r2.NewQty - r2.OldQty) > 0.0005)
            .Select(r2 => new ShipmentQtyCorrectionDto { ShipmentItemId = r2.ItemId, NewQtyKg = r2.NewQty }).ToList();
        int? newCust = _custBox.SelectedValue as int?;
        if (newCust == _originalCustomerId) newCust = null;
        if (corrections.Count == 0 && newCust == null)
        { AppContainer.Get<DialogService>().Error("لا تغييرات: عدّل كمية أو اختر عميلاً مختلفاً."); return; }
        if (!AppContainer.Get<DialogService>().Confirm("سيُسجَّل التصحيح بقيد فرق موثق وبسببك المكتوب. متابعة؟")) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = (IReceivingService)scope.ServiceProvider.GetRequiredService(typeof(IReceivingService));
            var r = svc.CorrectApprovedShipment(_shipmentId, corrections, newCust, _reason.Text.Trim());
            if (r.Ok)
            {
                AppContainer.Get<DialogService>().Info(r.Message);
                DialogResult = true;
                Close();
            }
            else AppContainer.Get<DialogService>().Error(r.Message);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "QtyCorrection.Run"); }
    }
}
