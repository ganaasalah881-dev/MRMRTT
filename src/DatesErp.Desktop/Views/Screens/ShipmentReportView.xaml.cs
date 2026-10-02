using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §B101 — تقرير حركة شحنة العميل (تصميم مستقل جديد):
/// رأس الشحنة (دخولها/إنتاجها/أوامرها/دورتها) ← سلسلة المراحل الخمس ← مؤشرات ←
/// جدول الأوامر بمخرجات كل أمر (نقر مزدوج: التفاصيل) ← جدول تسليمات العميل حتى الاكتمال.
/// الطباعة/PDF/Excel يصدرون نفس البيانات عبر ShipmentJourneyService.ToReportResult.
/// </summary>
public partial class ShipmentReportView : UserControl
{
    private readonly List<int> _planIds = new();
    private readonly List<int> _custIds = new();
    private readonly List<int> _prodIds = new();
    private bool _loading;
    private ShipmentJourneyLine _current;

    public ShipmentReportView()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadPlans();
    }

    // ── الفلاتر ──

    private int? PlanId() => PlanBox.SelectedIndex >= 0 && PlanBox.SelectedIndex < _planIds.Count
        ? _planIds[PlanBox.SelectedIndex] : (int?)null;
    private int? CustId() => CustBox.SelectedIndex >= 0 && CustBox.SelectedIndex < _custIds.Count
        ? _custIds[CustBox.SelectedIndex] : (int?)null;
    private int? ProdId() => ProdBox.SelectedIndex >= 0 && ProdBox.SelectedIndex < _prodIds.Count
        ? _prodIds[ProdBox.SelectedIndex] : (int?)null;

    private void LoadPlans()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var withItems = db.ProductionPlanItems.AsNoTracking()
                .Where(i => i.CustomerId != null).Select(i => i.PlanId).Distinct().ToList();
            var plans = db.ProductionPlans.AsNoTracking()
                .Where(p => withItems.Contains(p.Id)).OrderByDescending(p => p.Id).ToList();
            var keep = PlanBox.SelectedItem?.ToString(); // استعادة التحديد عند التحديث (نمط إقفال الخطة)
            _planIds.Clear();
            _planIds.AddRange(plans.Select(p => p.Id));
            _loading = true;
            PlanBox.ItemsSource = plans.Select(p =>
                $"{p.DocumentNumber} — {p.PlanTitle} ({(p.StartDate?.ToString("dd/MM/yyyy") ?? "—")} ← {(p.EndDate?.ToString("dd/MM/yyyy") ?? "—")})").ToList();
            if (keep != null)
            {
                for (int i = 0; i < PlanBox.Items.Count; i++)
                    if (PlanBox.Items[i]?.ToString() == keep) { PlanBox.SelectedIndex = i; break; }
            }
            if (PlanBox.SelectedIndex < 0 && PlanBox.Items.Count > 0) PlanBox.SelectedIndex = 0;
            _loading = false;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ShipmentReport.LoadPlans"); }
        LoadCustomers();
        LoadProducts();
        SelectJourney();
    }

    private void LoadCustomers()
    {
        var pid = PlanId();
        if (pid == null) { _custIds.Clear(); _loading = true; CustBox.ItemsSource = new List<string>(); CustBox.SelectedIndex = -1; _loading = false; return; }
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var ids = db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.PlanId == pid && i.CustomerId != null).Select(i => i.CustomerId).Distinct().ToList();
        var custIds = ids.Where(i => i != null).Select(i => i!.Value).OrderBy(x => x).ToList();
        var names = db.Customers.AsNoTracking().Where(c => custIds.Contains(c.Id))
            .OrderBy(c => c.CustomerName).Select(c => c.CustomerName).ToList();
        _custIds.Clear();
        _custIds.AddRange(custIds);
        _loading = true;
        CustBox.ItemsSource = names;
        CustBox.SelectedIndex = names.Count > 0 ? 0 : -1;
        _loading = false;
    }

    private void LoadProducts()
    {
        var pid = PlanId();
        var cid = CustId();
        if (pid == null) { _prodIds.Clear(); _loading = true; ProdBox.ItemsSource = new List<string>(); ProdBox.SelectedIndex = -1; _loading = false; return; }
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var pids = db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.PlanId == pid && (cid == null || i.CustomerId == cid)).Select(i => i.ProductId).Distinct().ToList();
        var names = db.Products.AsNoTracking().Where(p => pids.Contains(p.Id))
            .OrderBy(p => p.ProductNameAr).Select(p => p.ProductNameAr).ToList();
        _prodIds.Clear();
        _prodIds.AddRange(pids);
        _loading = true;
        ProdBox.ItemsSource = names;
        ProdBox.SelectedIndex = names.Count > 0 ? 0 : -1;
        _loading = false;
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (sender == PlanBox) { LoadCustomers(); LoadProducts(); }
        else if (sender == CustBox) LoadProducts();
        SelectJourney();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadPlans();

    // ── عرض الشحنة ──

    private void SelectJourney()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            _current = new ShipmentJourneyService(db).GetJourneys(PlanId(), CustId(), ProdId()).FirstOrDefault();
            if (_current == null)
            {
                HTitle.Text = "لا توجد شحنة — اختر خطة الإنتاج (العميل/الصنف)";
                HStatus.Text = "—";
                HEntry.Text = HProdDate.Text = HOrders.Text = HScheduled.Text = HCycle.Text = HCurrent.Text = "";
                OrdersGrid.ItemsSource = null;
                CdsGrid.ItemsSource = null;
                OrderDetailBox.Visibility = Visibility.Collapsed;
                StepperPanel.Children.Clear();
                CountLabel.Text = "";
                return;
            }
            Fill(_current);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ShipmentReport.Select"); }
    }

    private void Fill(ShipmentJourneyLine l)
    {
        HTitle.Text = $"🚚 شحنة: {l.CustomerName} — {l.ProductName} — خطة {l.PlanNumber}";
        HStatus.Text = $"{l.StatusIcon} {l.FinalStatusAr}";
        var (chipBg, chipFg) = ChipColor(l.StatusIcon);
        StatusChip.Background = chipBg;
        HStatus.Foreground = chipFg;
        HEntry.Text = $"{l.EntryDate:dd/MM/yyyy HH:mm} — أنشأها: {l.EntryUser} — اعتمدتها: {l.ApproverUser} {(l.PlanApprovedDate != null ? $"يوم {l.PlanApprovedDate:dd/MM/yyyy HH:mm}" : "(لم تعتمد بعد)")}";
        HProdDate.Text = l.FirstProductionDate != null
            ? $"{l.FirstProductionDate:dd/MM/yyyy} (آخر أمر: {l.LastProductionDate:dd/MM/yyyy})"
            : "لم تدخل الإنتاج بعد";
        HOrders.Text = l.OrderCount > 0 ? $"{l.OrderCount} أمر" : "لا أوامر بعد";
        HScheduled.Text = l.PlannedDate != null ? l.PlannedDate.Value.ToString("dd/MM/yyyy") : "—";
        HCycle.Text = l.CycleDays != null ? $"{l.CycleDays} يوم" : "—";
        HCurrent.Text = CurrentStepAr(l);

        SetKpi(KPlanned, PPlanned, l.PlannedKg, l.PlannedKg);
        SetKpi(KProduced, PProduced, l.ProducedKg, l.PlannedKg);
        SetKpi(KAccepted, PAccepted, l.AcceptedKg, l.PlannedKg);
        SetKpi(KReceived, PReceived, l.ReceivedKg, l.PlannedKg);
        SetKpi(KDelivered, PDelivered, l.DeliveredKg, l.PlannedKg);

        FillStepper(l);

        OrdersGrid.ItemsSource = l.Orders;
        CdsGrid.ItemsSource = l.Deliveries;
        OrderDetailBox.Visibility = Visibility.Collapsed;
        CountLabel.Text = $"{l.Orders.Count} أمر · {l.Deliveries.Count} تسليم · {l.Stages.Count} مرحلة موثقة";
    }

    private static string CurrentStepAr(ShipmentJourneyLine l) => l.StatusIcon switch
    {
        "✅" => "اكتملت — سلّمت تاماً للعميل",
        "⏳" => "في مرحلة التسليم للعميل",
        "🟡" => "بانتظار التسليم (المخزن جاهز)",
        "🔍" => "في مرحلة فحص الجودة",
        _ => l.ProducedKg > 0 ? "التشغيل جارٍ" : "لم تبدأ التشغيل"
    };

    private static void SetKpi(TextBlock k, ProgressBar p, double value, double max)
    {
        k.Text = value.ToString("N0");
        p.Value = max > 0 ? Math.Min(100, value * 100 / max) : 0;
    }

    private static (Brush bg, Brush fg) ChipColor(string icon) => icon switch
    {
        "✅" => (new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7)), new SolidColorBrush(Color.FromRgb(0x14, 0x53, 0x2D))),
        "⏳" => (new SolidColorBrush(Color.FromRgb(0xFE, 0xF3, 0xC7)), new SolidColorBrush(Color.FromRgb(0x92, 0x40, 0x0E))),
        "🟡" => (new SolidColorBrush(Color.FromRgb(0xFE, 0xF9, 0xC3)), new SolidColorBrush(Color.FromRgb(0x85, 0x4D, 0x0E))),
        "🔍" => (new SolidColorBrush(Color.FromRgb(0xDB, 0xEA, 0xFE)), new SolidColorBrush(Color.FromRgb(0x1E, 0x40, 0xAF))),
        _ => (new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)), new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63)))
    };

    private void FillStepper(ShipmentJourneyLine l)
    {
        StepperPanel.Children.Clear();
        void AddChip(string title, string sub, string kind)
        {
            var (bg, fg) = kind switch
            {
                "done" => ChipColor("✅"),
                "active" => ChipColor("⏳"),
                _ => (new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)), new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)))
            };
            var chip = new Border
            {
                Background = bg,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(3, 0, 3, 0)
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, Foreground = fg, FontSize = 13.5 });
            sp.Children.Add(new TextBlock { Text = "  " + sub, Foreground = fg, FontSize = 12 });
            chip.Child = sp;
            StepperPanel.Children.Add(chip);
        }
        void Arrow() => StepperPanel.Children.Add(new TextBlock { Text = "←", FontSize = 16, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 1, 0) });

        AddChip("① الخطة", l.EntryDate.ToString("dd/MM"), "done");
        Arrow();
        AddChip("② الإنتاج", l.OrderCount > 0 ? $"{l.OrderCount} أمر · {l.FirstProductionDate:dd/MM}" : "لم يبدأ",
            l.OrderCount > 0 ? (l.AcceptedKg <= 0 ? "active" : "done") : "pending");
        Arrow();
        AddChip("③ الجودة", l.AcceptedKg > 0 ? $"مقبول {l.AcceptedKg:N0}" : "بانتظار",
            l.AcceptedKg > 0 ? "done" : (l.ProducedKg > 0 ? "active" : "pending"));
        Arrow();
        AddChip("④ مخزن التام", l.ReceivedKg > 0 ? $"مستلم {l.ReceivedKg:N0}" : "بانتظار",
            l.ReceivedKg > 0 ? "done" : "pending");
        Arrow();
        AddChip("⑤ تسليم العميل",
            l.DeliveredKg > 0 ? $"{l.DeliveredKg:N0} / {l.PlannedKg:N0}" : "بانتظار",
            l.DeliveredKg >= l.PlannedKg - 0.001 && l.PlannedKg > 0 ? "done" : (l.DeliveredKg > 0 ? "active" : "pending"));
    }

    // ── تفاصيل الأمر (نقر مزدوج) ──

    private void OrdersGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (OrdersGrid.SelectedItem is not OrderJourneyRow o) return;
        OrderDetailTitle.Text = $"📋 أمر {o.Seq} — {o.OrderNumber}  ({o.StatusAr})";
        ODCreated.Text = o.CreatedText;
        ODApproved.Text = o.ApprovedText;
        ODClosed.Text = string.IsNullOrWhiteSpace(o.ClosedText) ? "— (مفتوح)" : o.ClosedText;
        ODShift.Text = o.ShiftLine;
        ODQty.Text = $"{o.PlannedKg:N0} → {o.ProducedKg:N0} كجم / {o.PlannedCartons} كرتون";
        ODProduction.Text = "🏭 التشغيل:  " + o.ProductionText;
        ODQuality.Text = "🔍 الجودة:  " + o.QualityText;
        ODWarehouse.Text = "📦 تسليم الإنتاج:  " + o.WarehouseText;
        ODReceipt.Text = "📥 الاستلام:  " + o.ReceiptText;
        OrderDetailBox.Visibility = Visibility.Visible;
    }

    // ── التصدير (نفس بيانات الشاشة) ──

    private ReportResult BuildExport()
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        return new ShipmentJourneyService(db).ToReportResult(PlanId(), CustId(), ProdId());
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    { try { if (PlanId() == null) return; AppContainer.Get<ExportPrintService>().Print(BuildExport()); }
      catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ShipmentReport.Print"); } }
    private void Pdf_Click(object sender, RoutedEventArgs e)
    { try { if (PlanId() == null) return; AppContainer.Get<ExportPrintService>().ExportPdf(BuildExport()); }
      catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ShipmentReport.Pdf"); } }
    private void Excel_Click(object sender, RoutedEventArgs e)
    { try { if (PlanId() == null) return; AppContainer.Get<ExportPrintService>().ExportExcel(BuildExport()); }
      catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ShipmentReport.Excel"); } }
}
