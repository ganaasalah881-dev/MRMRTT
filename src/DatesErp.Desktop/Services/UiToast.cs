using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DatesErp.Desktop.Services;

/// <summary>
/// §44 — تنبيهات غير حاجبة (Toast) بأسلوب موحّد: نجاح/تحذير/خطأ بجانب شريط المهام،
/// تُغلق تلقائياً ولا تقاطع تدفق الإدخال كما تفعل نوافذ MessageBox.
/// </summary>
public sealed class ToastWindow : Window
{
    private readonly DispatcherTimer _timer = new();

    private ToastWindow(string msg, string kind)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FlowDirection = FlowDirection.RightToLeft;

        string accent = kind switch { "error" => "#B91C1C", "warn" => "#B45309", _ => "#15803D" };
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(12),
            Background = (SolidColorBrush)new BrushConverter().ConvertFromString("#1E293B"),
            BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(accent),
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(16, 10, 16, 10)
        };
        border.Child = new TextBlock
        {
            Text = msg,
            Foreground = Brushes.White,
            FontSize = 12.5,
            MaxWidth = 420,
            TextWrapping = TextWrapping.Wrap
        };
        Content = border;
        Loaded += (_, _) => Place();
        _timer.Interval = TimeSpan.FromSeconds(kind == "error" ? 6 : 3.5);
        _timer.Tick += (_, _) => { _timer.Stop(); Close(); };
        _timer.Start();
    }

    private void Place()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + 8;
        Top = wa.Bottom - ActualHeight - 8;
    }

    public static void Show(string msg, string kind = "ok")
    {
        var app = System.Windows.Application.Current;
        if (app == null) return;
        app.Dispatcher.Invoke(() =>
        {
            try { new ToastWindow(msg, kind).Show(); }
            catch { /* التنبيه تجميلي — لا يُفشل العملية */ }
        });
    }
}

public static class UiToast
{
    public static void Show(string msg, string kind = "ok") => ToastWindow.Show(msg, kind);
}

