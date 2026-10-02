using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DatesErp.Desktop.Services;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>حقل لون مركزي: إدخال HEX مع لوحة ألوان مرئية، بلا اعتماد على مكتبة خارجية.</summary>
public sealed class ThemeColorField : Border
{
    private readonly TextBox _hex = new();
    private readonly Border _swatch = new();
    private readonly TextBlock _label = new();
    private bool _internal;

    public string KeyName { get; set; }
    public string Label { get => _label.Text; set => _label.Text = value ?? ""; }
    public string Value
    {
        get => _hex.Text;
        set
        {
            _internal = true;
            _hex.Text = value ?? "#000000";
            _internal = false;
            PaintSwatch();
        }
    }

    public event EventHandler ValueChanged;

    public ThemeColorField()
    {
        Padding = new Thickness(8);
        Margin = new Thickness(4);
        Background = Brushes.Transparent;
        BorderBrush = (Brush)System.Windows.Application.Current?.TryFindResource("BorderBrushStd") ?? Brushes.LightGray;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(5);

        var panel = new StackPanel();
        _label.FontWeight = FontWeights.Bold;
        _label.Margin = new Thickness(0, 0, 0, 5);
        _label.SetResourceReference(TextBlock.FontFamilyProperty, "ThemeBodyFontFamily");
        panel.Children.Add(_label);

        var line = new DockPanel();
        _swatch.Width = 30;
        _swatch.Height = 28;
        _swatch.Margin = new Thickness(5, 0, 0, 0);
        _swatch.CornerRadius = new CornerRadius(4);
        _swatch.Cursor = System.Windows.Input.Cursors.Hand;
        _swatch.MouseLeftButtonUp += (_, _) => OpenPicker();
        DockPanel.SetDock(_swatch, Dock.Right);
        line.Children.Add(_swatch);

        _hex.MinWidth = 112;
        _hex.Text = "#000000";
        _hex.ToolTip = "صيغة HEX مثال: #14532D أو #CC14532D";
        _hex.TextChanged += (_, _) =>
        {
            PaintSwatch();
            if (!_internal) ValueChanged?.Invoke(this, EventArgs.Empty);
        };
        line.Children.Add(_hex);
        panel.Children.Add(line);
        Child = panel;
        PaintSwatch();
    }

    private void PaintSwatch()
    {
        var color = ThemeManager.ParseColor(_hex.Text, Colors.Transparent);
        _swatch.Background = new SolidColorBrush(color);
    }

    private void OpenPicker()
    {
        var picker = new ThemeColorPickerWindow(Value) { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() == true) Value = picker.Value;
    }
}

internal sealed class ThemeColorPickerWindow : Window
{
    private readonly TextBox _hex = new();
    private readonly Border _preview = new();
    public string Value { get; private set; }

    private static readonly string[] Palette =
    {
        "#0A246A", "#14532D", "#0F766E", "#7C3AED", "#C9A227", "#F59E0B", "#DC2626", "#0284C7",
        "#111827", "#1E293B", "#334155", "#64748B", "#94A3B8", "#CBD5E1", "#F1F5F9", "#FFFFFF",
        "#15803D", "#16A34A", "#22C55E", "#B45309", "#B91C1C", "#F87171", "#38BDF8", "#A855F7"
    };

    public ThemeColorPickerWindow(string value)
    {
        Value = value;
        Title = "اختيار لون الثيم";
        Width = 390;
        Height = 290;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = FlowDirection.RightToLeft;
        Background = Brushes.White;

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = "اختر لوناً أو أدخل قيمة HEX", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) });
        var row = new DockPanel();
        _preview.Width = 36; _preview.Height = 30; _preview.Margin = new Thickness(6, 0, 0, 0); _preview.CornerRadius = new CornerRadius(4);
        DockPanel.SetDock(_preview, Dock.Right); row.Children.Add(_preview);
        _hex.Text = value; _hex.VerticalContentAlignment = VerticalAlignment.Center;
        _hex.TextChanged += (_, _) => UpdatePreview(); row.Children.Add(_hex); root.Children.Add(row);

        var grid = new UniformGrid { Columns = 8, Margin = new Thickness(0, 12, 0, 10) };
        foreach (var hex in Palette)
        {
            var button = new Button { Width = 32, Height = 26, Margin = new Thickness(2), Tag = hex, ToolTip = hex,
                Background = new SolidColorBrush(ThemeManager.ParseColor(hex, Colors.Transparent)), BorderBrush = Brushes.Gray };
            button.Click += (_, _) => { _hex.Text = (string)button.Tag; };
            grid.Children.Add(button);
        }
        root.Children.Add(grid);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        var ok = new Button { Content = "موافق", Style = (Style)System.Windows.Application.Current.TryFindResource("ErpPrimaryButton"), MinWidth = 85, Margin = new Thickness(3) };
        ok.Click += (_, _) => { if (!IsValid()) { MessageBox.Show("أدخل لوناً بصيغة HEX صحيحة.", "لون غير صالح", MessageBoxButton.OK, MessageBoxImage.Warning); return; } Value = _hex.Text.Trim(); DialogResult = true; Close(); };
        var cancel = new Button { Content = "إلغاء", Style = (Style)System.Windows.Application.Current.TryFindResource("ErpButton"), MinWidth = 85, Margin = new Thickness(3) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        actions.Children.Add(ok); actions.Children.Add(cancel); root.Children.Add(actions);
        Content = root;
        UpdatePreview();
    }

    private bool IsValid() => _hex.Text.Trim().StartsWith("#") && (ThemeManager.ParseColor(_hex.Text.Trim(), Colors.Transparent) != Colors.Transparent || _hex.Text.Trim().Equals("#00000000", StringComparison.OrdinalIgnoreCase));
    private void UpdatePreview() => _preview.Background = new SolidColorBrush(ThemeManager.ParseColor(_hex.Text, Colors.Transparent));
}
