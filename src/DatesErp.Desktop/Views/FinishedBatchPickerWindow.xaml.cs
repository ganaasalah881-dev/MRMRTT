using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views;

/// <summary>
/// نافذة اختيار دفعة تامة للتسليم — جميع الأرصدة المتاحة معروضة فوراً مع تنبيه FIFO.
/// </summary>
public partial class FinishedBatchPickerWindow : Window
{
    public class BatchRow
    {
        public int ProductId { get; set; }
        public int? LotId { get; set; }
        public int? PackagingTypeId { get; set; }
        public string ProductName { get; set; }
        public string LotCode { get; set; }
        public string CustomerName { get; set; }
        public int? CustomerId { get; set; }
        public double Qty { get; set; }
        public int Packages { get; set; }
        public string Unit { get; set; }
        public string PackName { get; set; }
        public string Grade { get; set; }
        public DateTime? ProductionDate { get; set; }
        public DateTime? FgReceiptDate { get; set; }
        public int StorageDays { get; set; }
        public double CartonWeight { get; set; }
        public object Entity { get; set; }
    }

    private List<BatchRow> _all = new();
    private int? _customerId;
    public BatchRow SelectedBatch { get; private set; }

    public FinishedBatchPickerWindow(int? customerId = null)
    {
        InitializeComponent();
        _customerId = customerId;
        Loaded += (_, _) =>
        {
            LoadBatches();
            BatchesGrid.Focus();
            if (BatchesGrid.Items.Count > 0) BatchesGrid.SelectedIndex = 0;
        };
    }

    private void LoadBatches()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var whFg = db.Warehouses.Where(w => w.WarehouseCode == "WFG").Select(w => w.Id).FirstOrDefault();
            var q = db.StockBalances.AsNoTracking().Where(b => b.WarehouseId == whFg && b.QtyKg > 0); // §1.50.66 — كل عبوة منفصلة، الفلترة لاحقاً تشمل PackagingTypeId
            if (_customerId != null) q = q.Where(b => b.CustomerId == _customerId);

            var balances = q.ToList();
            var productIds = balances.Select(b => b.ProductId).Where(id => id != null).Distinct().ToList();
            var products = db.Products.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToDictionary(p => p.Id);
            var lotIds = balances.Select(b => b.LotId).Where(id => id != null).Distinct().ToList();
            var lots = db.Lots.AsNoTracking().Where(l => lotIds.Contains(l.Id)).ToDictionary(l => l.Id);
            var customerIds = balances.Select(b => b.CustomerId).Where(id => id != null).Distinct().ToList();
            var customers = db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionary(c => c.Id);
            var packIds = balances.Select(b => b.PackagingTypeId).Where(id => id != null).Distinct().ToList();
            var packs = db.PackagingTypes.AsNoTracking().Where(p => packIds.Contains(p.Id)).ToDictionary(p => p.Id);

            // للإنتاج: تاريخ الإنتاج من ProductionOrders أو FinishedGoodsReceipts
            var fgReceipts = db.FinishedGoodsReceipts.AsNoTracking().ToList();

            _all = balances.Select(b =>
            {
                products.TryGetValue(b.ProductId ?? 0, out var prod);
                lots.TryGetValue(b.LotId ?? 0, out var lot);
                customers.TryGetValue(b.CustomerId ?? 0, out var cust);
                packs.TryGetValue(b.PackagingTypeId ?? 0, out var pack);
                var fg = fgReceipts.FirstOrDefault(r => r.Id == b.LotId || r.DeliveryId == b.LotId); // تقريبي
                return new BatchRow
                {
                    ProductId = b.ProductId ?? 0,
                    LotId = b.LotId,
                    PackagingTypeId = b.PackagingTypeId,
                    ProductName = prod?.ProductNameAr ?? "—",
                    LotCode = lot?.LotCode ?? "—",
                    CustomerName = cust?.CustomerName ?? "—",
                    CustomerId = b.CustomerId,
                    Qty = b.QtyKg,
                    Packages = b.PackageCount,
                    Unit = prod?.UnitOfMeasure ?? "كجم",
                    PackName = pack?.PackageNameAr ?? "—",
                    Grade = "سليم",
                    ProductionDate = lot?.LotDate ?? DateTime.Now,
                    FgReceiptDate = DateTime.Now,
                    StorageDays = 0,
                    CartonWeight = pack?.UnitWeightKg ?? (b.QtyKg > 0 && b.PackageCount > 0 ? b.QtyKg / b.PackageCount : 0),
                    Entity = b
                };
            })
            .OrderBy(r => r.ProductionDate)
            .ThenBy(r => r.LotCode)
            .ToList();

            ApplyFilter();
        }
        catch (Exception ex)
        {
            AppContainer.Get<DialogService>().HandleException(ex, "FinishedBatchPicker.Load");
        }
    }

    private void ApplyFilter()
    {
        var term = SearchBox.Text?.Trim().ToLower() ?? "";
        var filtered = string.IsNullOrWhiteSpace(term)
            ? _all
            : _all.Where(r =>
                (r.CustomerName != null && r.CustomerName.ToLower().Contains(term)) ||
                (r.ProductName != null && r.ProductName.ToLower().Contains(term)) ||
                (r.LotCode != null && r.LotCode.ToLower().Contains(term))
            ).ToList();

        BatchesGrid.ItemsSource = filtered;
        CountText.Text = $"النتائج: {filtered.Count} من {_all.Count} دفعة تامة متاحة — مرتبة FIFO من الأقدم إنتاجاً";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        ApplyFilter();
        BatchesGrid.Focus();
    }

    private void BatchesGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => TrySelectCurrent();

    private void Ok_Click(object sender, RoutedEventArgs e) => TrySelectCurrent();

    private void TrySelectCurrent()
    {
        if (BatchesGrid.SelectedItem is BatchRow row)
        {
            // فحص FIFO للتسليم: هل توجد دفعات أقدم لنفس العميل؟
            var earlier = _all.Where(b => b.CustomerId == row.CustomerId && b.LotId != row.LotId && (b.ProductionDate ?? DateTime.MaxValue) < (row.ProductionDate ?? DateTime.MaxValue)).OrderBy(b => b.ProductionDate).ToList();
            if (earlier.Count > 0)
            {
                var top = earlier.Take(2).ToList();
                var lines = string.Join("\n", top.Select(l => $"• دفعة {l.LotCode} بتاريخ {l.ProductionDate:dd/MM/yyyy} — {l.Qty:N1} كجم"));
                var msg = $"تنبيه FIFO — لديك دفعات أقدم للعميل «{row.CustomerName}» لم تُسلم بعد:\n{lines}\n\nتحاول تسليم دفعة بتاريخ {row.ProductionDate:dd/MM/yyyy} (دفعة {row.LotCode}) بينما توجد أقدم.\n\nهل تريد الاستمرار؟";
                var res = MessageBox.Show(msg, "تنبيه أقدمية FIFO — تسليم", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes) return;
            }

            SelectedBatch = row;
            DialogResult = true;
            Close();
        }
        else
        {
            MessageBox.Show("اختر دفعة من القائمة أولاً.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
