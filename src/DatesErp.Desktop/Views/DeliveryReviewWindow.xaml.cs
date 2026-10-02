using System.Windows;

namespace DatesErp.Desktop.Views;

/// <summary>§7 أمر شاشة التسليم — نافذة مراجعة الكميات الموزَّعة FIFO قبل الحفظ.</summary>
public partial class DeliveryReviewWindow : Window
{
    public sealed record ReviewRow(string Name, string Grade, int Avail, int Delivered, string Lots);

    public DeliveryReviewWindow(IEnumerable<(string Name, string Grade, int Avail, int Delivered, string Lots)> rows)
    {
        InitializeComponent();
        ReviewGrid.ItemsSource = rows.Select(r => new ReviewRow(r.Name, r.Grade, r.Avail, r.Delivered, r.Lots)).ToList();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
