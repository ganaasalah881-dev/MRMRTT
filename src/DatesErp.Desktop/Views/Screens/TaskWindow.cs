using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Session;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §13 — نافذة المهمة الموحدة: النمط المعياري لكل المهام.
///
/// ### النمط الملزم
/// <c>بطاقة ⟶ نقرتان ⟶ تفاصيل كاملة ⟶ تنفيذ الإجراء</c>
/// وليس <c>إضافة ⟶ بحث ⟶ اختيار ⟶ إعادة إدخال</c>.
///
/// ### قواعد بنيوية ملتزَم بها
/// شريط الأزرار **ثابت أسفل النافذة** (B44: لا صعود ونزول) · <c>RightToLeft</c> ·
/// **لا شاشة جديدة تحل محل شاشة قائمة**: زر «فتح المستند» يستدعي الشاشة القائمة
/// بدل إعادة بناء عرضها هنا.
///
/// ### الأزرار تُحكم بالقدرة وحدها
/// من لا يملك <c>RequiredCapability</c> لا يرى أزرار التنفيذ أصلاً — ولا يراها معطّلة.
/// والخادم يرفض على أي حال، فالإخفاء راحة للمستخدم لا حاجز أمني.
/// </summary>
public class TaskWindow : Window
{
    private readonly int _taskId;
    private WorkflowTask _task;
    private StackPanel _body;
    private WrapPanel _actions;

    public TaskWindow(int taskId)
    {
        _taskId = taskId;
        Title = "تفاصيل المهمة";
        Width = 900;
        Height = 650;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)new BrushConverter().ConvertFromString("#F1F5F9");
        Build();
    }

    private void Build()
    {
        _task = Load();
        if (_task == null)
        {
            Content = new TextBlock { Text = "المهمة غير موجودة.", Margin = new Thickness(20), FontSize = 14 };
            return;
        }

        Title = $"{_task.Title} — {_task.TaskNumber}";

        var root = new DockPanel();

        // ① الترويسة
        root.Children.Add(Header());
        DockPanel.SetDock((UIElement)root.Children[^1], Dock.Top);

        // شريط الأزرار — ثابت أسفل النافذة (B44)
        _actions = new WrapPanel
        {
            Margin = new Thickness(12, 8, 12, 12),
            FlowDirection = FlowDirection.RightToLeft
        };
        var actionBar = new Border
        {
            Background = (Brush)new BrushConverter().ConvertFromString("#F1F5F9"),
            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#CBD5E1"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = _actions
        };
        DockPanel.SetDock(actionBar, Dock.Bottom);
        root.Children.Add(actionBar);

        _body = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        BuildBody();
        BuildActions();
        Content = root;
    }

    private WorkflowTask Load()
    {
        using var scope = AppContainer.NewScope();
        return scope.ServiceProvider.GetRequiredService<IWorkflowTaskService>().GetById(_taskId);
    }

    // ══════════════════ ① الترويسة ══════════════════

    private UIElement Header()
    {
        string accent = _task.IsOverdue ? "#DC2626"
            : _task.Priority == WorkflowTaskPriority.Urgent ? "#EA580C" : "#0A246A";

        var sp = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
        sp.Children.Add(new TextBlock
        {
            Text = $"🔔 {_task.Title}",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)new BrushConverter().ConvertFromString(accent),
            TextWrapping = TextWrapping.Wrap
        });

        var bits = new List<string>
        {
            $"رقم المهمة: {_task.TaskNumber}",
            $"المستند: {_task.DocumentNumber ?? "-"}",
            $"النوع: {WorkflowTaskTypes.ToArabic(_task.TaskType)}",
            $"الحالة: {WorkflowTaskStates.ToArabic(_task.State)}",
            $"الأولوية: {WorkflowTaskPriority.ToArabic(_task.Priority)}"
        };
        if (_task.BusinessDate.HasValue) bits.Add($"يوم التشغيل: {_task.BusinessDate:dd/MM/yyyy}");
        if (_task.DueDate.HasValue) bits.Add($"الاستحقاق: {_task.DueDate:dd/MM/yyyy}");

        sp.Children.Add(new TextBlock
        {
            Text = string.Join("   ·   ", bits),
            FontSize = 11.5,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        if (_task.IsOverdue)
            sp.Children.Add(Warn($"⚠️ هذه المهمة متأخرة {(DateTime.Now.Date - _task.DueDate.Value.Date).Days} يوماً عن استحقاقها."));

        return new Border
        {
            Background = Brushes.White,
            BorderBrush = (Brush)new BrushConverter().ConvertFromString("#CBD5E1"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = sp
        };
    }

    // ══════════════════ ②③⑤ المحتوى ══════════════════

    private void BuildBody()
    {
        _body.Children.Clear();

        // ② الملخص — من اللقطة المحفوظة، بلا استعلامات إضافية
        var summary = ParseSummary();
        if (summary.Count > 0)
        {
            _body.Children.Add(SectionTitle("② الملخص"));
            var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int row = 0;
            foreach (var kv in summary)
            {
                g.RowDefinitions.Add(new RowDefinition());
                var k = new TextBlock { Text = kv.Key + ":", FontWeight = FontWeights.Bold, FontSize = 12, Margin = new Thickness(0, 2, 10, 2) };
                var v = new TextBlock { Text = kv.Value, FontSize = 12, Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap };
                Grid.SetRow(k, row); Grid.SetColumn(k, 0); g.Children.Add(k);
                Grid.SetRow(v, row); Grid.SetColumn(v, 1); g.Children.Add(v);
                row++;
            }
            _body.Children.Add(g);
        }

        // ⑥ ملاحظات الإجراء إن وُجدت
        if (!string.IsNullOrWhiteSpace(_task.ActionNotes))
        {
            _body.Children.Add(SectionTitle("⑥ ملاحظات"));
            _body.Children.Add(new TextBlock
            {
                Text = _task.ActionNotes,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });
        }

        // ⑤ الخط الزمني — من أرسلها، متى، ماذا فعل
        _body.Children.Add(SectionTitle("⑤ الخط الزمني للمهمة"));
        _body.Children.Add(BuildTimeline());
    }

    private Dictionary<string, string> ParseSummary()
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(_task.SummaryJson)) return result;
        try
        {
            using var doc = JsonDocument.Parse(_task.SummaryJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var p in doc.RootElement.EnumerateObject())
                result[p.Name] = p.Value.ToString();
        }
        catch { /* لقطة تالفة لا تمنع فتح المهمة */ }
        return result;
    }

    private UIElement BuildTimeline()
    {
        using var scope = AppContainer.NewScope();
        var history = scope.ServiceProvider.GetRequiredService<IWorkflowTaskService>().GetHistory(_taskId);

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            MaxHeight = 220,
            Margin = new Thickness(0, 0, 0, 10)
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "التوقيت", Binding = new System.Windows.Data.Binding("At") { StringFormat = "dd/MM/yyyy HH:mm" }, Width = 130 });
        grid.Columns.Add(new DataGridTextColumn { Header = "بواسطة", Binding = new System.Windows.Data.Binding("By"), Width = 140 });
        grid.Columns.Add(new DataGridTextColumn { Header = "من", Binding = new System.Windows.Data.Binding("From"), Width = 100 });
        grid.Columns.Add(new DataGridTextColumn { Header = "إلى", Binding = new System.Windows.Data.Binding("To"), Width = 100 });
        grid.Columns.Add(new DataGridTextColumn { Header = "القدرة", Binding = new System.Windows.Data.Binding("Cap"), Width = 150 });
        grid.Columns.Add(new DataGridTextColumn { Header = "ملاحظات", Binding = new System.Windows.Data.Binding("Notes"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        grid.ItemsSource = history.Select(h => new
        {
            h.At,
            By = h.ByUserName,
            From = WorkflowTaskStates.ToArabic(h.FromState),
            To = WorkflowTaskStates.ToArabic(h.ToState),
            Cap = h.ByCapability == null ? "-" : WorkflowCapabilities.NameOf(h.ByCapability),
            h.Notes
        }).ToList();

        return grid;
    }

    // ══════════════════ ④ الأزرار ══════════════════

    private void BuildActions()
    {
        _actions.Children.Clear();

        var cap = WorkflowCapabilities.IsDefined(_task.RequiredCapability)
            ? WorkflowCapabilities.Resolve(_task.RequiredCapability)
            : default;
        bool canAct = cap.Code != null && PermissionGate.Can(cap.Resource, cap.Operation);
        bool live = WorkflowTaskStates.IsLive(_task.State);
        int uid = AppContainer.Get<SessionContext>().UserId;

        if (live && canAct)
        {
            if (_task.ClaimedByUserId == null)
                _actions.Children.Add(Btn("🙋 التقاط المهمة", "ErpButton", () => Do(s => s.Claim(_taskId, uid))));
            else if (_task.ClaimedByUserId == uid)
                _actions.Children.Add(Btn("↩ التخلي عنها", "ErpButton", () => Do(s => s.Release(_taskId, uid))));

            _actions.Children.Add(Btn("✅ تنفيذ / اعتماد", "ErpApproveButton", () => CompleteWithNotes()));
            _actions.Children.Add(Btn("↩️ إعادة للتعديل", "ErpButton", () => WithReason("سبب الإعادة للتعديل:", r => Do(s => s.Return(_taskId, r)))));
            _actions.Children.Add(Btn("⛔ رفض", "ErpDangerButton", () => WithReason("سبب الرفض (إلزامي):", r => Do(s => s.Reject(_taskId, r)))));
        }
        else if (live && !canAct)
        {
            // شفافية بدل زر ميت: نقول لماذا لا يستطيع، ونسمّي الصلاحية المطلوبة
            _actions.Children.Add(new TextBlock
            {
                Text = $"🔒 لا تملك صلاحية تنفيذ هذه المهمة — المطلوب: {WorkflowCapabilities.NameOf(_task.RequiredCapability)}",
                Foreground = Brushes.DimGray,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 12, 0),
                TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            _actions.Children.Add(new TextBlock
            {
                Text = $"✔ {WorkflowTaskStates.ToArabic(_task.State)}" +
                       (_task.ActedDate.HasValue ? $" — {_task.ActedDate:dd/MM/yyyy HH:mm}" : ""),
                Foreground = Brushes.DimGray,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 12, 0)
            });
        }

        if (live && PermissionGate.Can(PermissionModules.Tasks, "Edit"))
            _actions.Children.Add(Btn("👤 إحالة لشخص", "ErpButton", ReassignDialog));

        _actions.Children.Add(Btn("📂 فتح المستند", "ErpButton", OpenDocument));
        _actions.Children.Add(Btn("🪟 نافذة التفاصيل", "ErpButton", OpenDetailWindow));   // §B102 (سحب B97–B100)
        _actions.Children.Add(Btn("🚪 إغلاق", "ErpButton", Close));
    }

    /// <summary>التنفيذ مع ملاحظة اختيارية — الملاحظة إلزامية للرفض والإعادة فقط.</summary>
    private void CompleteWithNotes()
    {
        var dlg = new InputDialog("تنفيذ المهمة", "ملاحظات (اختيارية):") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        Do(s => s.Complete(_taskId, "Approved", dlg.Value));
    }

    private void WithReason(string prompt, Action<string> then)
    {
        var dlg = new InputDialog("سبب القرار", prompt) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (string.IsNullOrWhiteSpace(dlg.Value))
        {
            AppContainer.Get<DialogService>().Error("السبب إلزامي — لا رفض ولا إعادة بلا سبب مكتوب.");
            return;
        }
        then(dlg.Value.Trim());
    }

    /// <summary>
    /// الإحالة تعرض **مالكي القدرة فقط**. عرض كل المستخدمين كان سيقود المستخدم
    /// إلى اختيار من لا يستطيع تنفيذها ثم يُرفض — إحباط بلا فائدة.
    /// </summary>
    private void ReassignDialog()
    {
        using var scope = AppContainer.NewScope();
        var svc = scope.ServiceProvider.GetRequiredService<IWorkflowTaskService>();
        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.DatesErpDbContext>();

        var holderIds = svc.GetCapabilityHolders(_task.RequiredCapability);
        var holders = db.Users.Where(u => holderIds.Contains(u.Id))
            .Select(u => new { u.Id, Label = u.FullName + " (" + u.UserName + ")" }).ToList();

        if (holders.Count == 0)
        {
            AppContainer.Get<DialogService>().Error(
                $"لا يوجد مستخدم يملك القدرة «{WorkflowCapabilities.NameOf(_task.RequiredCapability)}».\n" +
                "امنحها لمن يلزم من شاشة الصلاحيات أولاً.");
            return;
        }

        var win = new Window
        {
            Title = "إحالة المهمة",
            Width = 430,
            Height = 240,
            FlowDirection = FlowDirection.RightToLeft,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var combo = new ComboBox { ItemsSource = holders, DisplayMemberPath = "Label", SelectedValuePath = "Id", SelectedIndex = 0 };
        var reason = new TextBox { Margin = new Thickness(0, 6, 0, 0) };
        var ok = new Button
        {
            Content = "تنفيذ الإحالة",
            Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton"),
            Margin = new Thickness(0, 12, 0, 0)
        };
        ok.Click += (_, _) =>
        {
            if (combo.SelectedValue is not int toId) return;
            win.Close();
            Do(s => s.Reassign(_taskId, toId, reason.Text?.Trim()));
        };

        var p = new StackPanel { Margin = new Thickness(14) };
        p.Children.Add(new TextBlock { Text = "يُعرض من يملك القدرة المطلوبة فقط:", FontWeight = FontWeights.Bold, FontSize = 12 });
        p.Children.Add(combo);
        p.Children.Add(new TextBlock { Text = "السبب:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 0) });
        p.Children.Add(reason);
        p.Children.Add(ok);
        win.Content = p;
        win.ShowDialog();
    }

    /// <summary>
    /// فتح المستند في **شاشته القائمة** — لا نعيد بناء عرضه هنا ولا نستبدل شاشة قائمة (§13).
    /// </summary>
    private void OpenDocument()
    {
        var mw = Owner as MainWindow ?? System.Windows.Application.Current.MainWindow as MainWindow;
        if (mw == null) { AppContainer.Get<DialogService>().Error("تعذّر الوصول إلى النافذة الرئيسية."); return; }

        string screen = _task.DocumentType switch
        {
            WorkflowDocTypes.ProductionPlan or WorkflowDocTypes.ProductionPlanItem => "planning",
            WorkflowDocTypes.ProductionOrder => "orders",
            WorkflowDocTypes.QualityCheck => "quality",
            WorkflowDocTypes.ProductionDelivery => "proddelivery",
            WorkflowDocTypes.FinishedGoodsReceipt => "fgreceive",
            WorkflowDocTypes.CustomerDelivery => "delivery",
            _ => null
        };
        if (screen == null) { AppContainer.Get<DialogService>().Info("لا توجد شاشة مرتبطة بهذا النوع من المستندات."); return; }

        Close();
        if (screen == "planning" && _task.DocumentType == WorkflowDocTypes.ProductionPlan)
            mw.OpenPlanById(_task.DocumentId);
        else
            mw.OpenScreen(screen);
    }

    /// <summary>
    /// §B102 (سحب B97–B100): نوافذ التفاصيل السريعة المرحّلة من خط B101 —
    /// خطة/أمر/فحص/استلام/تسليم، كل نوع بنافذته المختصة (قراءة + إجراءات المرحلة).
    /// </summary>
    private void OpenDetailWindow()
    {
        try
        {
            System.Windows.Window w = _task.DocumentType switch
            {
                WorkflowDocTypes.ProductionPlan => new PlanDetailWindow(_task.DocumentId),
                WorkflowDocTypes.ProductionPlanItem => PlanWindowOfItem(_task.DocumentId),
                WorkflowDocTypes.ProductionOrder => new OrderDetailWindow(_task.DocumentId),
                WorkflowDocTypes.QualityCheck => new QCWindow(_task.DocumentId),
                WorkflowDocTypes.FinishedGoodsReceipt => ReceiptWindow.FromReceipt(_task.DocumentId),
                WorkflowDocTypes.ProductionDelivery or WorkflowDocTypes.CustomerDelivery => ReceiptWindow.FromDelivery(_task.DocumentId),
                _ => null
            };
            if (w == null) { AppContainer.Get<DialogService>().Info("لا توجد نافذة تفاصيل لهذا النوع — استخدم «فتح المستند»."); return; }
            w.Owner = this;
            w.ShowDialog();
            Load();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Task.DetailWindow"); }
    }

    private static System.Windows.Window PlanWindowOfItem(int planItemId)
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErp.Infrastructure.Persistence.DatesErpDbContext>();
        int planId = db.ProductionPlanItems.AsNoTracking().Where(i => i.Id == planItemId).Select(i => i.PlanId).FirstOrDefault();
        return planId > 0 ? new PlanDetailWindow(planId) : null;
    }

    private void Do(Func<IWorkflowTaskService, OpResult> work)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IWorkflowTaskService>();
            var r = work(svc);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            _task = Load();
            BuildBody();
            BuildActions();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Task.Action"); }
    }

    // ══════════════════ مساعدات ══════════════════

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.Bold,
        FontSize = 12.5,
        Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)),
        Margin = new Thickness(0, 6, 0, 4)
    };

    private static TextBlock Warn(string text) => new()
    {
        Text = text,
        Foreground = Brushes.Firebrick,
        FontWeight = FontWeights.Bold,
        FontSize = 11.5,
        Margin = new Thickness(0, 4, 0, 0),
        TextWrapping = TextWrapping.Wrap
    };

    private static Button Btn(string label, string style, Action onClick)
    {
        var b = new Button
        {
            Content = label,
            Style = (Style)System.Windows.Application.Current.FindResource(style),
            Margin = new Thickness(4, 2, 4, 2)
        };
        b.Click += (_, _) => onClick();
        return b;
    }
}
