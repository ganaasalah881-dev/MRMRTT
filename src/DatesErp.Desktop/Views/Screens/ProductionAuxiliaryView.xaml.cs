using System.Windows;
using System.Windows.Controls;
using DatesErp.Application.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

public partial class ProductionAuxiliaryView : UserControl
{
    private List<int> _orderIds = new();

    public ProductionAuxiliaryView()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadOrders();
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("صرف الأصناف المساعدة للإنتاج");
        chrome.SetScreenCode("MRPAUX1003");
        chrome.SetToolbar(new Views.ErpToolbar()
            .WithSave((_, _) => IssueAll_Click(null, null), "صرف كل المتبقي")
            .WithRefresh((_, _) => Refresh())
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard")));
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard");
    }

    private void LoadOrders()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var orders = db.ProductionOrders.Where(o => o.IsApproved).OrderByDescending(o => o.Id).Take(200).ToList();
            _orderIds = orders.Select(o => o.Id).ToList();
            OrderBox.ItemsSource = orders;
            OrderBox.DisplayMemberPath = "DocumentNumber";
            OrderBox.SelectedValuePath = "Id";
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ProdAux.LoadOrders"); }
    }

    private void Order_Changed(object sender, SelectionChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (OrderBox.SelectedValue is not int oid) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<AuxiliaryManagementService>();

            var order = db.ProductionOrders.AsNoTracking().FirstOrDefault(o => o.Id == oid);
            if (order != null)
            {
                var items = db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == oid).ToList();
                int totalCartons = items.Sum(i => i.PlannedCartons);
                OrderInfo.Text = $"الأمر {order.DocumentNumber} — {totalCartons:N0} كرتون — {items.Count} بنداً";
            }

            var needs = svc.CalculateNeedsForOrder(oid);
            NeedsGrid.ItemsSource = needs;

            var history = db.AuxiliaryIssueTransactions.AsNoTracking().Where(t => t.OrderId == oid).OrderByDescending(t => t.IssueDate).ToList();
            // نحتاج أسماء الأصناف للسجل
            var prodIds = history.Where(h => h.AuxiliaryProductId != null).Select(h => h.AuxiliaryProductId.Value).Distinct().ToList();
            var prodNames = db.Products.AsNoTracking().Where(p => prodIds.Contains(p.Id)).ToDictionary(p => p.Id, p => p.ProductNameAr);
            var historyDisplay = history.Select(h => new
            {
                h.IssueDate,
                h.DocumentNumber,
                AuxiliaryProductName = h.AuxiliaryProductId != null && prodNames.TryGetValue(h.AuxiliaryProductId.Value, out var nm) ? nm : $"مادة #{h.MaterialId}",
                h.RequiredQty,
                h.IssuedQty,
                h.Unit,
                h.UserName,
                h.WarehouseId,
                h.BalanceBefore,
                h.BalanceAfter
            }).ToList();
            HistoryGrid.ItemsSource = historyDisplay;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ProdAux.Refresh"); }
    }

    private void IssueOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is AuxiliaryNeedDto need)
        {
            if (OrderBox.SelectedValue is not int oid) return;
            if (need.AuxiliaryProductId == null)
            {
                AppContainer.Get<DialogService>().Error("هذا الصنف من النظام القديم — استخدم شاشة صرف المواد القديمة.");
                return;
            }
            if (need.RemainingQty <= 0.001)
            {
                AppContainer.Get<DialogService>().Info("لا يوجد متبقي للصرف لهذا الصنف — المطلوب مصروف بالكامل.");
                return;
            }

            var dlg = new Views.InputDialog($"صرف {need.AuxiliaryProductName}", $"الكمية المطلوب صرفها (المتبقي {need.RemainingQty:N3} {need.Unit}):", need.RemainingQty.ToString("0.###"));
            if (dlg.ShowDialog() != true) return;
            if (!double.TryParse(dlg.Value, out var qty) || qty <= 0)
            {
                AppContainer.Get<DialogService>().Error("أدخل كمية صحيحة.");
                return;
            }

            try
            {
                using var scope = AppContainer.NewScope();
                var svc = scope.ServiceProvider.GetRequiredService<AuxiliaryManagementService>();
                var r = svc.IssueAuxiliary(oid, need.AuxiliaryProductId.Value, qty, $"صرف يدوي من شاشة الأصناف المساعدة");
                if (!r.Ok) AppContainer.Get<DialogService>().Error(r.Message);
                else
                {
                    AppContainer.Get<DialogService>().Info(r.Message);
                    Refresh();
                }
            }
            catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ProdAux.IssueOne"); }
        }
    }

    private void IssueAll_Click(object sender, RoutedEventArgs e)
    {
        if (OrderBox.SelectedValue is not int oid)
        {
            AppContainer.Get<DialogService>().Error("اختر أمر الإنتاج أولاً.");
            return;
        }
        if (!AppContainer.Get<DialogService>().Confirm("صرف جميع الأصناف المساعدة المتبقية لهذا الأمر؟ سيتم التحقق من المخزون لكل صنف.")) return;

        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<AuxiliaryManagementService>();
            var r = svc.IssueAllRemaining(oid);
            if (!r.Ok) AppContainer.Get<DialogService>().Error(r.Message);
            else AppContainer.Get<DialogService>().Info(r.Message);
            Refresh();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ProdAux.IssueAll"); }
    }

    private void Return_Click(object sender, RoutedEventArgs e)
    {
        if (OrderBox.SelectedValue is not int oid) return;
        var dlgProd = new Views.ProductPickerWindow("") { Owner = Window.GetWindow(this) };
        if (dlgProd.ShowDialog() != true || dlgProd.SelectedProduct == null) return;

        var dlgQty = new Views.InputDialog($"إرجاع {dlgProd.SelectedProduct.ProductNameAr}", "كمية الإرجاع:");
        if (dlgQty.ShowDialog() != true) return;
        if (!double.TryParse(dlgQty.Value, out var qty) || qty <= 0) return;

        var dlgReason = new Views.InputDialog("سبب الإرجاع", "اكتب سبب الإرجاع (إجباري للتدقيق):");
        if (dlgReason.ShowDialog() != true) return;
        if (string.IsNullOrWhiteSpace(dlgReason.Value))
        {
            AppContainer.Get<DialogService>().Error("سبب الإرجاع إجباري.");
            return;
        }

        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<AuxiliaryManagementService>();
            var r = svc.ReturnAuxiliary(oid, dlgProd.SelectedProduct.Id, qty, dlgReason.Value.Trim());
            if (!r.Ok) AppContainer.Get<DialogService>().Error(r.Message);
            else
            {
                AppContainer.Get<DialogService>().Info(r.Message);
                Refresh();
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ProdAux.Return"); }
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is AuxiliaryNeedDto need)
        {
            AppContainer.Get<DialogService>().Info($"تفاصيل احتساب {need.AuxiliaryProductName}:\n{need.CalculationDetails}\n\nالمطلوب: {need.RequiredQty:N3} {need.Unit}\nالمصروف: {need.IssuedQty:N3}\nالمتبقي: {need.RemainingQty:N3}\nالمتاح: {need.AvailableQty:N3}\nالحالة: {need.StockStatusAr}");
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
}
