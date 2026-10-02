using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Domain.Entities;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views;

/// <summary>
/// نافذة اختيار صنف — بحث برقم الصنف أو الاسم، تعبئة تلقائية في صف الاستلام.
/// </summary>
public partial class ProductPickerWindow : Window
{
    private List<ProductRow> _all = new();
    public Product SelectedProduct { get; private set; }

    public class ProductRow
    {
        public int Id { get; set; }
        public string ProductCode { get; set; }
        public string ProductNameAr { get; set; }
        public string UnitOfMeasure { get; set; }
        public string GroupCode { get; set; }
        public string ItemType { get; set; }
        public string DefaultPackName { get; set; }
        public Product Entity { get; set; }
    }

    private string _itemTypeFilter;

    public ProductPickerWindow(string initialSearch = "", string itemTypeFilter = null)
    {
        InitializeComponent();
        _itemTypeFilter = itemTypeFilter;
        Loaded += (_, _) =>
        {
            LoadProducts();
            // §1.50.54 تعديل: جميع الأصناف معروضة فوراً بلا كتابة — التصفية اختيارية
            if (!string.IsNullOrWhiteSpace(initialSearch))
                SearchBox.Text = initialSearch;
            ApplyFilter();
            ProductsGrid.Focus();
            if (ProductsGrid.Items.Count > 0) ProductsGrid.SelectedIndex = 0;
        };
    }

    private void LoadProducts()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            // §الاستلام للخام فقط (001) حسب القاعدة الحالية، لكن نعرض كل النشط مع تمييز الخام أولاً
            var packs = db.PackagingTypes.AsNoTracking().ToDictionary(p => p.Id, p => p.PackageNameAr);
            var productsQuery = db.Products.AsNoTracking().Where(p => p.IsActive);
            if (!string.IsNullOrWhiteSpace(_itemTypeFilter))
                productsQuery = productsQuery.Where(p => p.ItemType == _itemTypeFilter);
            var products = productsQuery
                .OrderBy(p => p.ItemType == "Raw" ? 0 : 1)
                .ThenBy(p => p.ProductNameAr)
                .ToList();

            _all = products.Select(p => new ProductRow
            {
                Id = p.Id,
                ProductCode = p.ProductCode,
                ProductNameAr = p.ProductNameAr,
                UnitOfMeasure = p.UnitOfMeasure ?? "كجم",
                GroupCode = p.GroupCode,
                ItemType = p.ItemType,
                DefaultPackName = p.DefaultPackagingTypeId.HasValue && packs.TryGetValue(p.DefaultPackagingTypeId.Value, out var pn) ? pn : "—",
                Entity = p
            }).ToList();

            ApplyFilter();
        }
        catch (Exception ex)
        {
            AppContainer.Get<DialogService>().HandleException(ex, "ProductPicker.Load");
        }
    }

    private void ApplyFilter()
    {
        var term = SearchBox.Text?.Trim().ToLower() ?? "";
        var filtered = string.IsNullOrWhiteSpace(term)
            ? _all
            : _all.Where(r =>
                (r.ProductCode != null && r.ProductCode.ToLower().Contains(term)) ||
                (r.ProductNameAr != null && r.ProductNameAr.ToLower().Contains(term)) ||
                (r.GroupCode != null && r.GroupCode.ToLower().Contains(term))
            ).ToList();

        ProductsGrid.ItemsSource = filtered;
        CountText.Text = $"النتائج: {filtered.Count} من {_all.Count} صنفاً — اكتب جزءاً من الرقم أو الاسم للتصفية";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        ApplyFilter();
        ProductsGrid.Focus();
    }

    private void ProductsGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ProductsGrid.SelectedItem is ProductRow row)
            SelectAndClose(row);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ProductsGrid.SelectedItem is ProductRow row)
            SelectAndClose(row);
        else
            MessageBox.Show("اختر صنفاً من القائمة أولاً.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SelectAndClose(ProductRow row)
    {
        SelectedProduct = row.Entity;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
