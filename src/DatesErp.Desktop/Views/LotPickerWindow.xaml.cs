using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views;

/// <summary>
/// نافذة اختيار دفعة خام للتخطيط — جميع الدفعات المتاحة معروضة فوراً مع تنبيه FIFO.
/// </summary>
public partial class LotPickerWindow : Window
{
    private List<AvailableLotDto> _all = new();
    private List<int> _currentPlanLotIds = new();
    public AvailableLotDto SelectedLot { get; private set; }

    public LotPickerWindow(List<int> currentPlanLotIds = null, int? customerId = null)
    {
        InitializeComponent();
        _currentPlanLotIds = currentPlanLotIds ?? new List<int>();
        Loaded += (_, _) =>
        {
            LoadLots(customerId);
            LotsGrid.Focus();
            if (LotsGrid.Items.Count > 0) LotsGrid.SelectedIndex = 0;
        };
    }

    private void LoadLots(int? customerId)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            _all = svc.GetAvailableLots(customerId, null);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            AppContainer.Get<DialogService>().HandleException(ex, "LotPicker.Load");
        }
    }

    private void ApplyFilter()
    {
        var term = SearchBox.Text?.Trim().ToLower() ?? "";
        var filtered = string.IsNullOrWhiteSpace(term)
            ? _all
            : _all.Where(r =>
                (r.CustomerName != null && r.CustomerName.ToLower().Contains(term)) ||
                (r.LotCode != null && r.LotCode.ToLower().Contains(term)) ||
                (r.ProductName != null && r.ProductName.ToLower().Contains(term)) ||
                (r.ShipmentNo != null && r.ShipmentNo.ToLower().Contains(term))
            ).ToList();

        // إثراء للعرض
        var display = filtered.Select(r => new
        {
            r.LotId,
            r.LotCode,
            r.CustomerName,
            r.ProductName,
            r.ShipmentNo,
            r.ArrivalDate,
            r.RemainingKg,
            r.AvailableForDateKg,
            r.ReceiptUnit,
            TreatmentStatus = r.RequiresTreatment ? (r.ReadyNowKg > 0 ? "جاهز" : "تحت المعالجة") : "لا يحتاج",
            DaysInStock = r.ArrivalDate != null ? (DateTime.Now.Date - r.ArrivalDate.Value.Date).Days : 0,
            Entity = r
        }).ToList();

        LotsGrid.ItemsSource = display;
        CountText.Text = $"النتائج: {display.Count} من {_all.Count} دفعة متاحة — مرتبة FIFO من الأقدم وصولاً";

        if (display.Count > 0)
        {
            var oldest = display.First();
            FifoHint.Text = $"أقدم دفعة متاحة: العميل «{oldest.CustomerName}» — دفعة {oldest.LotCode} وصلت {oldest.ArrivalDate:dd/MM/yyyy} — {oldest.RemainingKg:N1} كجم";
            FifoHint.Visibility = Visibility.Visible;
        }
        else
        {
            FifoHint.Visibility = Visibility.Collapsed;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        ApplyFilter();
        LotsGrid.Focus();
    }

    private void LotsGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (LotsGrid.SelectedItem != null)
            TrySelectCurrent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => TrySelectCurrent();

    private void TrySelectCurrent()
    {
        if (LotsGrid.SelectedItem == null)
        {
            MessageBox.Show("اختر دفعة من القائمة أولاً.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        dynamic sel = LotsGrid.SelectedItem;
        AvailableLotDto lot = sel.Entity as AvailableLotDto;
        if (lot == null) return;

        // §1.50.56 — فحص FIFO قبل الاختيار
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var warning = svc.CheckFifoWarning(lot.LotId, _currentPlanLotIds);
            if (warning.HasEarlier)
            {
                var result = MessageBox.Show(warning.Message + "\n\nهل تريد المتابعة؟", "تنبيه أقدمية FIFO", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;
            }
        }
        catch { /* فشل الفحص لا يمنع الاختيار */ }

        SelectedLot = lot;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
