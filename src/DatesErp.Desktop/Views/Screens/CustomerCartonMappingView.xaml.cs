using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Domain.Entities;
using DatesErp.Desktop.Services;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>§1.50.64 — ربط كراتين العملاء: كل تاجر له ماركته الخاصة بالكراتين، يخصم تلقائياً عند الإنتاج.</summary>
public partial class CustomerCartonMappingView : UserControl
{
    public CustomerCartonMappingView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshAll();
    }

    private void RefreshAll()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErp.Infrastructure.Persistence.DatesErpDbContext>();
            var auxSvc = scope.ServiceProvider.GetRequiredService<DatesErp.Application.Services.AuxiliaryManagementService>();

            CustomerBox.ItemsSource = db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.CustomerName).ToList();
            FinishedProductBox.ItemsSource = new[] { new { Id = 0, ProductNameAr = "— كل الأصناف —" } }
                .Concat(db.Products.AsNoTracking().Where(p => p.ItemType == "Finished" && p.IsActive)
                    .Select(p => new { Id = p.Id, ProductNameAr = p.ProductNameAr }).ToList()).ToList();
            FinishedProductBox.SelectedIndex = 0;

            PackagingBox.ItemsSource = new[] { new { Id = 0, PackageNameAr = "— كل العبوات —" } }
                .Concat(db.PackagingTypes.AsNoTracking().Where(p => p.IsActive)
                    .Select(p => new { Id = p.Id, PackageNameAr = p.PackageNameAr }).ToList()).ToList();
            PackagingBox.SelectedIndex = 0;

            var cartons = db.Products.AsNoTracking()
                .Where(p => (p.ItemType == "Auxiliary" || p.ItemType == "Pack" || p.GroupCode == "004") && p.IsActive
                            && (p.ProductNameAr.Contains("كرتون") || p.ProductNameAr.Contains("كرت") || p.GroupCode == "004"))
                .OrderBy(p => p.ProductNameAr).ToList();
            if (cartons.Count == 0)
                cartons = db.Products.AsNoTracking().Where(p => (p.ItemType == "Auxiliary" || p.ItemType == "Pack") && p.IsActive).OrderBy(p => p.ProductNameAr).ToList();

            GenericCartonBox.ItemsSource = cartons;
            CustomerCartonBox.ItemsSource = cartons;

            var specs = auxSvc.GetCustomerCartonMappings();
            var customers = db.Customers.AsNoTracking().ToDictionary(c => c.Id, c => c.CustomerName);
            var products = db.Products.AsNoTracking().ToDictionary(p => p.Id, p => p.ProductNameAr);
            var packs = db.PackagingTypes.AsNoTracking().ToDictionary(p => p.Id, p => p.PackageNameAr);

            var rows = specs.Select(s => new
            {
                s.Id,
                CustomerName = customers.GetValueOrDefault(s.CustomerId, $"عميل #{s.CustomerId}"),
                FinishedProductName = s.ProductId != null && products.ContainsKey(s.ProductId.Value) ? products[s.ProductId.Value] : "كل الأصناف",
                PackagingName = s.PackagingTypeId != null && packs.ContainsKey(s.PackagingTypeId.Value) ? packs[s.PackagingTypeId.Value] : "كل العبوات",
                GenericCartonName = s.GenericAuxiliaryProductId != null && products.ContainsKey(s.GenericAuxiliaryProductId.Value)
                    ? products[s.GenericAuxiliaryProductId.Value]
                    : (s.GenericAuxiliaryProductId == null ? "أي كرتون عام" : $"صنف #{s.GenericAuxiliaryProductId}"),
                CustomerCartonName = s.AuxiliaryProductId != null && products.ContainsKey(s.AuxiliaryProductId.Value)
                    ? products[s.AuxiliaryProductId.Value]
                    : (s.MaterialId != 0 ? $"مادة قديمة #{s.MaterialId}" : "-"),
                s.BrandName,
                s.Priority,
                IsActive = s.IsActive ? "فعال" : "موقوف"
            }).ToList();

            MappingGrid.ItemsSource = rows;
        }
        catch (Exception ex)
        {
            AppContainer.Get<DialogService>().HandleException(ex, "CustomerCartonMapping.Refresh");
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (CustomerBox.SelectedValue == null)
            {
                AppContainer.Get<DialogService>().Error("اختر العميل أولاً.");
                return;
            }
            if (CustomerCartonBox.SelectedValue == null)
            {
                AppContainer.Get<DialogService>().Error("اختر كرتون العميل (ماركته الخاصة) — مثلاً كراتين السلطان 8كجم.");
                return;
            }

            int customerId = (int)CustomerBox.SelectedValue;
            int? finishedId = FinishedProductBox.SelectedValue is int fid && fid != 0 ? fid : null;
            int? packId = PackagingBox.SelectedValue is int pid && pid != 0 ? pid : null;
            int? genericId = GenericCartonBox.SelectedValue is int gid ? gid : null;
            int customerCartonId = (int)CustomerCartonBox.SelectedValue;

            if (!int.TryParse(PriorityBox.Text, out var priority)) priority = 10;

            using var scope = AppContainer.NewScope();
            var auxSvc = scope.ServiceProvider.GetRequiredService<DatesErp.Application.Services.AuxiliaryManagementService>();
            var res = auxSvc.SaveCustomerCartonMapping(customerId, finishedId, packId, genericId, customerCartonId, BrandBox.Text, priority);
            if (!res.Ok) { AppContainer.Get<DialogService>().Error(res.Message); return; }
            AppContainer.Get<DialogService>().Info(res.Message);
            RefreshAll();
        }
        catch (Exception ex)
        {
            AppContainer.Get<DialogService>().HandleException(ex, "CustomerCartonMapping.Save");
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not Button btn || btn.Tag == null) return;
            if (!int.TryParse(btn.Tag.ToString(), out var id)) return;
            if (!AppContainer.Get<DialogService>().Confirm("حذف/إيقاف هذا الربط؟")) return;

            using var scope = AppContainer.NewScope();
            var auxSvc = scope.ServiceProvider.GetRequiredService<DatesErp.Application.Services.AuxiliaryManagementService>();
            var res = auxSvc.DeleteCustomerCartonMapping(id);
            if (!res.Ok) { AppContainer.Get<DialogService>().Error(res.Message); return; }
            AppContainer.Get<DialogService>().Info(res.Message);
            RefreshAll();
        }
        catch (Exception ex)
        {
            AppContainer.Get<DialogService>().HandleException(ex, "CustomerCartonMapping.Delete");
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshAll();
}
