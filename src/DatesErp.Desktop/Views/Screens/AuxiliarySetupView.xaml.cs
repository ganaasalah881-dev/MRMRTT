using System.Windows;
using System.Windows.Controls;
using DatesErp.Application.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

public partial class AuxiliarySetupView : UserControl
{
    private List<AuxiliarySetupDto> _all = new();

    public AuxiliarySetupView()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("تهيئة الأصناف المساعدة");
        chrome.SetScreenCode("MRPAUX1001");
        chrome.SetToolbar(new Views.ErpToolbar()
            .WithSave((_, _) => Save_Click(null, null), "حفظ التهيئة")
            .WithRefresh((_, _) => Load())
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard")));
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard");
    }

    private void Load()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<AuxiliaryManagementService>();

            var products = db.Products.AsNoTracking()
                .Where(p => (p.ItemType == "Auxiliary" || p.ItemType == "Pack" || p.GroupCode == "004" || p.ItemType == "Finished") && p.IsActive)
                .OrderBy(p => p.ProductNameAr).ToList();

            ProductBox.ItemsSource = products;
            GroupBox.ItemsSource = db.ItemGroups.AsNoTracking().Where(g => g.IsActive).ToList();
            UnitBox.ItemsSource = db.UnitsOfMeasure.AsNoTracking().Where(u => u.IsActive).Select(u => u.UnitNameAr).ToList();

            _all = svc.GetAuxiliarySetups();
            SetupGrid.ItemsSource = _all;

            HintLabel.Text = $"إجمالي الأصناف المساعدة المهيأة: {_all.Count} — منها {_all.Count(x => x.IsConfigured)} مهيأ و {_all.Count(x => !x.IsConfigured)} غير مهيأ بعد. اختر صنفاً من الأعلى وحدد طريقة الصرف.";
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "AuxSetup.Load"); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ProductBox.SelectedValue is not int pid)
        {
            AppContainer.Get<DialogService>().Error("اختر الصنف المساعد من بطاقة الأصناف أولاً.");
            return;
        }

        string method = (MethodBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "ByUnit";
        double.TryParse(WeightBox.Text, out var w);

        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<AuxiliaryManagementService>();
            var r = svc.SaveAuxiliarySetup(pid, GroupBox.Text, UnitBox.Text, w, method, NeedsIssueCheck.IsChecked == true, ActiveCheck.IsChecked == true);
            if (!r.Ok) AppContainer.Get<DialogService>().Error(r.Message);
            else
            {
                AppContainer.Get<DialogService>().Info(r.Message);
                Load();
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "AuxSetup.Save"); }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is AuxiliarySetupDto dto)
        {
            ProductBox.SelectedValue = dto.ProductId;
            GroupBox.Text = dto.GroupCode;
            UnitBox.Text = dto.BaseUnit;
            WeightBox.Text = dto.UnitWeightKg.ToString();
            MethodBox.SelectedIndex = dto.DispensingMethod switch { "ByKilo" => 1, "PerProduction" => 2, _ => 0 };
            NeedsIssueCheck.IsChecked = dto.NeedsIssueOnOrder;
            ActiveCheck.IsChecked = dto.IsActive;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Load();
}
