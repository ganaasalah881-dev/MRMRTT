using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Session;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §7 — لوحة «مهامي»: الموظف لا يبحث عن عمله، النظام يعرض له عمله.
///
/// ### لماذا لوحة واحدة لا لوحة لكل دور
/// اللوحة تبني نفسها من **قدرات المستخدم الحالي**. من يملك قدرات التخطيط والجودة معاً
/// يرى مجموع العدّادين تلقائياً. لا شاشة مبرمجة لكل مسمى وظيفي — التزاماً بالقيد الحاكم.
///
/// ### الاستطلاع الدوري (قرار Q5)
/// مؤقّت كل 60 ثانية يحدّث **العدّادات فقط** (استعلام عدّ مفهرس على المستخدم الحالي).
/// تفاصيل المهام لا تُحمَّل دورياً — بل عند فتح الشاشة أو بتغيير تبويب أو بضغط «تحديث».
/// المؤقّت يتوقف عند مغادرة الشاشة، وفشل الاستعلام صامت للمستخدم (لئلا تنبثق رسالة خطأ كل دقيقة).
/// </summary>
public partial class MyTasksView : UserControl
{
    private DispatcherTimer _poll;
    private string _tab = "required";
    private Views.ErpChrome _chrome;

    public MyTasksView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => StopPolling();
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        _chrome = chrome;
        chrome.SetModule("المهام وسير العمل");
        chrome.SetScreenCode("MRPWFT1000");
        chrome.SetToolbar(new Views.ErpToolbar()
            .WithRefresh((_, _) => ReloadAll())
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard")));
        chrome.SetBody(this);
        chrome.SetPermissionModule(PermissionModules.Tasks);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // تبويب الإشراف يظهر فقط لمن يملك القدرة — لا زر ميت ولا رفض بعد النقر
        TabAll.Visibility = PermissionGate.Can(PermissionModules.Tasks, "ViewAll")
            ? Visibility.Visible : Visibility.Collapsed;

        ReloadAll();
        StartPolling();
    }

    // ══════════════════ الاستطلاع الدوري (Q5) ══════════════════

    private void StartPolling()
    {
        if (_poll != null) return;
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _poll.Tick += (_, _) => RefreshCountersQuietly();
        _poll.Start();
        RefreshHint.Text = "🔄 العدّادات تتحدّث كل 60 ثانية";
    }

    private void StopPolling()
    {
        if (_poll == null) return;
        _poll.Stop();
        _poll = null;
    }

    /// <summary>
    /// تحديث العدّادات وحدها. **صامت عند الفشل عمداً:** انقطاع الشبكة لدقيقة لا يجوز
    /// أن يفتح نافذة خطأ كل دقيقة — يُسجَّل ويُعاد في الدورة التالية.
    /// </summary>
    private void RefreshCountersQuietly()
    {
        try { LoadCounters(); }
        catch (Exception ex) { ErrorLog.Write(ex, "MyTasks.Poll"); }
    }

    // ══════════════════ التحميل ══════════════════

    private void ReloadAll()
    {
        try
        {
            LoadCounters();
            LoadCards();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "MyTasks.Load"); }
    }

    private int CurrentUserId() => AppContainer.Get<SessionContext>().UserId;

    private void LoadCounters()
    {
        using var scope = AppContainer.NewScope();
        var svc = scope.ServiceProvider.GetRequiredService<IWorkflowTaskService>();
        var c = svc.GetMyCounters(CurrentUserId());

        CountersPanel.Children.Clear();
        AddCounter("🔴", "متأخرة", c.Overdue, "#DC2626");
        AddCounter("⚡", "عاجلة", c.Urgent, "#EA580C");
        AddCounter("🟠", "مستحقة اليوم", c.DueToday, "#D97706");
        AddCounter("📋", "مفتوحة", c.Open, "#0A246A");
        AddCounter("🔵", "قيد العمل", c.InProgress, "#2563EB");

        RefreshHint.Text = $"🔄 آخر تحديث {DateTime.Now:HH:mm:ss} — كل 60 ثانية";
        _chrome?.SetCount(c.Live);
    }

    private void AddCounter(string icon, string label, int value, string color)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(value > 0 ? color : "#94A3B8");
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
        panel.Children.Add(new TextBlock { Text = icon, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new TextBlock
        {
            Text = value.ToString(),
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = brush,
            Margin = new Thickness(5, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            Foreground = Brushes.DimGray,
            VerticalAlignment = VerticalAlignment.Center
        });
        CountersPanel.Children.Add(panel);
    }

    private void Tab_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _tab = Tabs.SelectedItem == TabInProgress ? "inprogress"
             : Tabs.SelectedItem == TabDoneToday ? "donetoday"
             : Tabs.SelectedItem == TabUpcoming ? "upcoming"
             : Tabs.SelectedItem == TabAll ? "all"
             : "required";
        try { LoadCards(); }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "MyTasks.Tab"); }
    }

    /// <summary>
    /// ترتيب البطاقات ثابت لكل المستخدمين (§7): المتأخر ⟵ العاجل ⟵ الأقدم استحقاقاً.
    /// الخدمة ترتب بالأولوية ثم الاستحقاق، ونرفع المتأخر فوقها هنا.
    /// </summary>
    private void LoadCards()
    {
        using var scope = AppContainer.NewScope();
        var svc = scope.ServiceProvider.GetRequiredService<IWorkflowTaskService>();
        int uid = CurrentUserId();
        var today = DateTime.Now.Date;

        List<WorkflowTask> tasks = _tab switch
        {
            "all" => svc.GetAllTasks(),
            "donetoday" => svc.GetMyTasks(uid, includeDone: true)
                              .Where(t => t.ActedDate?.Date == today).ToList(),
            "inprogress" => svc.GetMyTasks(uid).Where(t => t.State == WorkflowTaskStates.InProgress).ToList(),
            "upcoming" => svc.GetMyTasks(uid)
                             .Where(t => t.DueDate.HasValue && t.DueDate.Value.Date > today).ToList(),
            _ => svc.GetMyTasks(uid).Where(t => t.State == WorkflowTaskStates.Open).ToList()
        };

        tasks = tasks
            .OrderByDescending(t => t.IsOverdue)
            .ThenBy(t => t.Priority)
            .ThenBy(t => t.DueDate ?? DateTime.MaxValue)
            .ToList();

        CardsList.Items.Clear();
        foreach (var t in tasks) CardsList.Items.Add(BuildCard(t));

        EmptyText.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _tab switch
        {
            "donetoday" => "لم تنجز مهاماً اليوم بعد.",
            "upcoming" => "لا مهام مجدولة في الأيام القادمة.",
            "inprogress" => "لا مهام قيد العمل — التقط مهمة من «مطلوب مني».",
            "all" => "لا مهام مفتوحة في النظام.",
            _ => "✅ لا مهام مطلوبة منك الآن.\nستظهر هنا تلقائياً فور توجيهها إليك."
        };
    }

    /// <summary>بطاقة المهمة — نقرتان تفتحان نافذة التنفيذ (§13).</summary>
    private UIElement BuildCard(WorkflowTask t)
    {
        // اللون يحمل المعنى: أحمر متأخر · برتقالي عاجل/اليوم · أزرق قيد العمل · أخضر منجز
        string accent =
            t.State == WorkflowTaskStates.Done ? "#16A34A"
            : t.IsOverdue ? "#DC2626"
            : t.Priority == WorkflowTaskPriority.Urgent ? "#EA580C"
            : t.State == WorkflowTaskStates.InProgress ? "#2563EB"
            : t.DueDate?.Date == DateTime.Now.Date ? "#D97706"
            : "#94A3B8";

        var border = new Border
        {
            BorderBrush = (Brush)new BrushConverter().ConvertFromString(accent),
            BorderThickness = new Thickness(0, 0, 4, 0),
            Background = Brushes.White,
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(10, 8, 10, 8),
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = t.Id
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(new TextBlock
        {
            Text = t.Title,
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)),
            TextWrapping = TextWrapping.Wrap
        });

        var meta = new List<string> { $"📄 {t.DocumentNumber ?? "-"}" };
        if (t.BusinessDate.HasValue) meta.Add($"📅 يوم {t.BusinessDate:dd/MM/yyyy}");
        if (t.DueDate.HasValue) meta.Add($"⏰ الاستحقاق {t.DueDate:dd/MM/yyyy}");
        meta.Add($"🔑 {WorkflowCapabilities.NameOf(t.RequiredCapability)}");
        left.Children.Add(new TextBlock
        {
            Text = string.Join("   ·   ", meta),
            FontSize = 11,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        if (t.IsOverdue)
            left.Children.Add(new TextBlock
            {
                Text = $"⚠️ متأخرة {(DateTime.Now.Date - t.DueDate.Value.Date).Days} يوماً",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Firebrick,
                Margin = new Thickness(0, 3, 0, 0)
            });

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(new TextBlock
        {
            Text = WorkflowTaskStates.ToArabic(t.State),
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)new BrushConverter().ConvertFromString(accent),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        right.Children.Add(new TextBlock
        {
            Text = t.TaskNumber,
            FontSize = 10,
            FontFamily = new FontFamily("Consolas"),
            Foreground = Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        border.Child = grid;
        border.MouseLeftButtonUp += (_, e) => { if (e.ClickCount >= 2) OpenTask(t.Id); };
        border.ToolTip = "نقرتان لفتح المهمة وتنفيذها";
        return border;
    }

    private void OpenTask(int taskId)
    {
        try
        {
            var win = new TaskWindow(taskId) { Owner = Window.GetWindow(this) };
            win.ShowDialog();
            ReloadAll(); // الحالة تغيّرت غالباً
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "MyTasks.Open"); }
    }
}
