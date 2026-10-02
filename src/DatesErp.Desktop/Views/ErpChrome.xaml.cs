using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Session;

namespace DatesErp.Desktop.Views;

/// <summary>
/// إطار النافذة الكلاسيكي الموحّد لكل الشاشات — مطابق للتصميم المعتمد:
/// شريط عنوان أزرق + شريط معلومات + شريط أدوات + محتوى + شريط تدقيق سفلي.
/// </summary>
public partial class ErpChrome : UserControl
{
    public static readonly DependencyProperty TitleTextProperty =
        DependencyProperty.Register(nameof(TitleText), typeof(string), typeof(ErpChrome),
            new PropertyMetadata("نظام إدارة وتصنيع التمور"));

    public string TitleText
    {
        get => (string)GetValue(TitleTextProperty);
        set => SetValue(TitleTextProperty, value);
    }

    /// <summary>تاريخ إنشاء/حفظ المستند إن وجد.</summary>
    public event EventHandler CloseRequested;

    public ErpChrome()
    {
        InitializeComponent();
        Loaded += (_, _) => FillMeta();
    }

    private void FillMeta()
    {
        try
        {
            var session = AppContainer.Get<SessionContext>();
            UserText.Text = session.UserName ?? "—";
            MachineText.Text = $"الجهاز: {Environment.MachineName} | التاريخ: \u200E{DateTime.Now.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}";
        }
        catch { }
        DateText.Text = $"التاريخ: \u200E{DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}";
        AuditText.Text = "جميع العمليات تسجل تلقائياً في سجل التدقيق المركزي";
    }

    public void SetModule(string module) => ModuleText.Text = module;
    public void SetScreenCode(string code) => ScreenCodeText.Text = code;
    public void SetCount(int count) => CountText.Text = $"{count} سجل";
    /// <summary>شريط الأدوات الحالي — تستخدمه الاختصارات العامة (F2/F3/F5/F9/F10/Ctrl+P).</summary>
    public ErpToolbar CurrentToolbar { get; private set; }
    /// <summary>
    /// §12 — وحدة الصلاحيات لهذه الشاشة (products / employees / ...). تضعها MainWindow مرة واحدة
    /// عند فتح الشاشة من كتالوج الشاشات، فتسري بوابة الأزرار على الشريط تلقائياً بلا تعديل في الشاشات.
    /// </summary>
    public string PermissionModule { get; private set; }

    public void SetPermissionModule(string module)
    {
        PermissionModule = module;
        CurrentToolbar?.ForModule(module);
    }

    public void SetToolbar(UIElement toolbar)
    {
        ToolbarArea.Content = toolbar;
        CurrentToolbar = toolbar as ErpToolbar;
        if (!string.IsNullOrWhiteSpace(PermissionModule)) CurrentToolbar?.ForModule(PermissionModule);
    }
    public void SetBody(UIElement body) => BodyArea.Content = body;

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // §B84/B1: زر ✕ كان ميتاً في ~17 شاشة بلا مشتركين في CloseRequested —
        // الافتراضي الآن: عودة للوحة المؤشرات (المشتركون الصريحون لا يتأثرون).
        if (CloseRequested != null) CloseRequested(this, EventArgs.Empty);
        else (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard");
    }

    /// <summary>§B54: تصغير نافذة النظام فعلياً — كان الزر ميتاً.</summary>
    private void Min_Click(object sender, RoutedEventArgs e)
    {
        var w = Window.GetWindow(this);
        if (w != null) w.WindowState = WindowState.Minimized;
    }

    /// <summary>§B54: تكبير/استعادة نافذة النظام — كان الزر ميتاً.</summary>
    private void Max_Click(object sender, RoutedEventArgs e)
    {
        var w = Window.GetWindow(this);
        if (w == null) return;
        w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
}
