using System.Windows;
using System.Collections.Generic;
using DatesErp.Core.Domain.Entities;

namespace DatesErp.Desktop.Views;

public partial class TemplatePickerWindow : Window
{
    public int? SelectedTemplateId { get; private set; }
    public string SelectedTemplateName { get; private set; }

    public TemplatePickerWindow(List<ProductionPlan> templates)
    {
        InitializeComponent();
        Grid.ItemsSource = templates;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is ProductionPlan p)
        {
            SelectedTemplateId = p.Id;
            SelectedTemplateName = p.PlanTitle;
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
