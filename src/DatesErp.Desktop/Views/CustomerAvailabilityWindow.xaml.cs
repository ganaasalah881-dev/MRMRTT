using System.Windows;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;

namespace DatesErp.Desktop.Views;

/// <summary>
/// §B100 — تفاصيل «متاح العميل»: المخطط/المنتج/في الفحص/المقبول/المسلَّم/القابل للتسليم
/// + بالأيام + الدفعات المتاحة فعلياً في مخزن التام + سجل التسليم.
/// عرض للقراءة — كل إجراء (تسليم/فوترة) من شاشة الدور المختص.
/// </summary>
public partial class CustomerAvailabilityWindow : Window
{
    private readonly int _customerId;

    public CustomerAvailabilityWindow(int customerId)
    {
        InitializeComponent();
        _customerId = customerId;
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<ICustomerAvailabilityService>();
            var d = svc.GetCustomerAvailability(_customerId);

            Title = $"متاح العميل — {d.CustomerName}";
            HeadTitle.Text = $"📦 متاح العميل: {d.CustomerName}";
            HeadState.Text = d.Overdue ? "⏰ لديه أيام متعثرة" : "الوضع سليم ✅";

            if (d.Overdue)
            {
                OverdueBanner.Visibility = Visibility.Visible;
                OverdueText.Text = "⚠️ يوجد يوم أو أكثر من أيام هذا العميل قد مضت ولم يكتمل تسليمها — ألقِ نظرة على تبويب «بالأيام».";
            }
            else OverdueBanner.Visibility = Visibility.Collapsed;

            FPlanned.Text = $"{d.PlannedKg:N1} كجم";
            FProduced.Text = $"{d.ProducedKg:N1} كجم";
            FInInspection.Text = $"{d.InInspectionKg:N1} كجم";
            FAccepted.Text = $"{d.AcceptedKg:N1} كجم";
            FDelivered.Text = $"{d.DeliveredKg:N1} كجم";
            FDeliverable.Text = $"{d.DeliverableKg:N1} كجم";

            DaysGrid.ItemsSource = d.Days;
            StocksGrid.ItemsSource = d.Stocks;
            DeliveriesGrid.ItemsSource = d.Deliveries;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "CustAvail.Load"); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
