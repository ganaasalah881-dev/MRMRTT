using System.Windows;
using System.Collections.Generic;
using System.Linq;
using DatesErp.Core.Domain.Entities;

namespace DatesErp.Desktop.Views;

public partial class ShipmentPickerWindow : Window
{
    public int? SelectedShipmentId { get; private set; }

    public ShipmentPickerWindow(List<Shipment> shipments)
    {
        InitializeComponent();
        Grid.ItemsSource = shipments;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is Shipment s)
        {
            SelectedShipmentId = s.Id;
            DialogResult = true;
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
