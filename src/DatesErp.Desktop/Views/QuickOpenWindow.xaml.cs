using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DatesErp.Desktop.Screens;

namespace DatesErp.Desktop.Views;

/// <summary>
/// §الفتح السريع (F12 / Ctrl+K) — كالأنظمة الكبيرة: اكتب اسم الشاشة وانتقل إليها
/// فوراً بالبحث في كل شاشات النظام وأقسامه بلا تنقل عبر القوائم.
/// </summary>
public class QuickOpenWindow : Window
{
    public class ScreenOption
    {
        public string Code { get; set; }
        public string Display { get; set; }
        public string Dept { get; set; }
    }

    private readonly TextBox _search = new();
    private readonly ListBox _list = new();
    private readonly ObservableCollection<ScreenOption> _all = new();
    // §B84/B8: مؤشر نتائج (كان الفشل الصامت: لا نتائج = قائمة فارغة بلا تفسير).
    private readonly TextBlock _status = new() { FontSize = 11.5, Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0) };

    public string SelectedCode { get; private set; }

    public QuickOpenWindow()
    {
        Title = "الفتح السريع — اكتب اسم الشاشة";
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 520; Height = 480;
        ResizeMode = ResizeMode.NoResize;
        Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#F1F5F9");

        foreach (var s in ScreenCatalog.All)
            _all.Add(new ScreenOption { Code = s.Code, Dept = s.Group, Display = $"{s.Icon} {s.Title}" });

        _search.FontSize = 14;
        _search.Padding = new Thickness(8, 6, 8, 6);
        _search.TextChanged += (_, _) => ApplyFilter();
        _search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && _list.Items.Count > 0) { _list.Focus(); _list.SelectedIndex = 0; e.Handled = true; }
            // §B84/B8: التلميح يقول "اضغط Enter" لكن Enter في البحث لم يكن يعمل — الآن يفتح الأول.
            else if (e.Key == Key.Enter)
            {
                if (_list.SelectedItem == null && _list.Items.Count > 0) _list.SelectedIndex = 0;
                Pick(); e.Handled = true;
            }
            else if (e.Key == Key.Escape) { DialogResult = false; Close(); e.Handled = true; }
        };

        _list.FontSize = 13;
        _list.ItemsSource = _all;
        _list.DisplayMemberPath = nameof(ScreenOption.Display);
        _list.MouseDoubleClick += (_, _) => Pick();
        _list.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Pick(); e.Handled = true; }
        };

        var hint = new TextBlock
        {
            Text = "اكتب ثم اضغط Enter — أو انقر نقراً مزدوجاً. أمثلة: العملاء، الخطط، الإقفال، التقارير...",
            FontSize = 12,
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = "⚡ الفتح السريع للشاشات",
            FontSize = 15, FontWeight = FontWeights.Bold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0A, 0x24, 0x6A)),
            Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(_search);
        var listBorder = new Border
        {
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7F, 0x9D, 0xB9)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 8, 0, 0),
            Child = _list
        };
        var gridHost = new Grid();
        gridHost.Children.Add(listBorder);
        panel.Children.Add(gridHost);
        gridHost.Height = 300;
        panel.Children.Add(_status);
        panel.Children.Add(hint);
        Content = panel;

        Loaded += (_, _) => { _search.Focus(); ApplyFilter(); };
    }

    private void ApplyFilter()
    {
        string term = _search.Text?.Trim().ToLowerInvariant() ?? "";
        var filtered = string.IsNullOrEmpty(term)
            ? _all.ToList()
            : _all.Where(o => o.Display.ToLowerInvariant().Contains(term) || o.Dept.ToLowerInvariant().Contains(term) || o.Code.Contains(term)).ToList();
        _list.ItemsSource = filtered;
    }

    private void Pick()
    {
        if (_list.SelectedItem is ScreenOption opt)
        {
            SelectedCode = opt.Code;
            DialogResult = true;
            Close();
        }
    }
}
