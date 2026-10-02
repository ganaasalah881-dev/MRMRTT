using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DatesErp.Core.Common;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>§v1.50.36 — شاشة واحدة: بنود اليوم + مستند الأمر أسفلها. لا تبديل شاشات.</summary>
public partial class OrdersView : UserControl
{
    private TodayProductionDto _sheet;
    private readonly Func<TodayProductionDto> _load;
    private readonly Func<OpResult> _issue;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly bool _isolated;
    private OrderDocumentPanel _panel;
    private int? _shownOrderId;
    private bool _syncing;

    public OrdersView() : this(null, null) { }

    public OrdersView(Func<TodayProductionDto> load, Func<OpResult> issue)
    {
        InitializeComponent();
        _isolated = load != null;
        // شاشة أوامر الإنتاج تعرض كل الخطط المعتمدة المجدولة؛ إصدار «اليوم» له زر مستقل وحارس مستقل.
        _load = load ?? (() => { using var scope = AppContainer.NewScope(); return scope.ServiceProvider.GetRequiredService<IProductionOrderService>().GetScheduledProduction(); });
        _issue = issue ?? (() => { using var scope = AppContainer.NewScope(); return scope.ServiceProvider.GetRequiredService<IProductionOrderService>().IssueTodayOrders(); });
        _timer.Tick += (_, _) => RefreshToday();
        Loaded += (_, _) =>
        {
            RefreshToday(); _timer.Start();
            if (!_isolated && MainWindow.PendingOrderIdToOpen is int id)
            {
                MainWindow.PendingOrderIdToOpen = null;
                ShowOrderInPlace(id);
            }
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("أمر الإنتاج — شاشة واحدة: الخطط المجدولة + المستند");
        // عنوان الشاشة للعرض فقط؛ بوابة الأزرار والصلاحيات تستخدم كود الوحدة الثابت.
        chrome.SetPermissionModule("production");
        chrome.SetScreenCode("MRPMPS1007");
        chrome.SetToolbar(new Views.ErpToolbar()
            .WithNew((_, _) => AddFromPlan_Click(null, null), "➕ إضافة أمر")
            .WithCustom("📥 إنزال من خطة", "ErpPrimaryButton", (_, _) => AddFromPlan_Click(null, null), "إنشاء أمر من مجموعة خطة معتمدة فقط", "Create")
            .WithEdit((_, _) => Edit_Click(null, null))
            .WithSave((_, _) => Save_Click(null, null), "💾 حفظ")
            .WithSearch((_, _) => Search_Click(null, null), "بحث عن أمر إنتاج")
            .WithDelete((_, _) => Delete_Click(null, null))
            .WithRefresh((_, _) => RefreshToday())
            .WithPrint((_, _) => Print_Click(null, null))
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard")));
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard");
    }

    private void RefreshToday()
    {
        try
        {
            int? selected = (TodayGrid.SelectedItem as TodayProductionRowDto)?.PlanItemId;
            var next = _load();
            // صفوف الخطة للعرض والتحديد فقط؛ لا تهيئة لكمية قابلة للتحرير هنا.
            _sheet = next;
            DayLabel.Text = $"📅 اليوم: {next.Day:dd/MM/yyyy} — الخطط المجدولة السابقة والحالية والقادمة";
            var open = next.Rows.Where(r => !r.DayClosed).ToList();
            var todayRows = next.Rows.Where(r => r.IsToday).ToList();
            DayHint.Text = todayRows.Count > 0 && todayRows.All(r => r.DayClosed)
                ? "أُقفل يوم جميع بنود اليوم ✓ — بقاياهم في «تسليم الإنتاج» كسجلات تم التسجيل وفي التقارير."
                : next.Message;
            _syncing = true;
            TodayGrid.ItemsSource = open;
            TodayGrid.SelectedItem = open.FirstOrDefault(r => r.PlanItemId == selected)
                ?? open.FirstOrDefault(r => r.OrderId == _shownOrderId);
            _syncing = false;
            DayChip.Text = $"📅 اليوم: {next.Day:dd/MM/yyyy}";
            var closed = next.Rows.Count(r => r.DayClosed);
            ItemsChip.Text = $"📦 البنود المجدولة المعروضة: {open.Count}";
            IssuedChip.Text = $"🗂 أوامر صادرة: {next.Rows.Count(r => r.OrderId != null)}";
            PendingChip.Text = $"⏳ بانتظار الإصدار: {open.Count(r => r.IsPending)}";
            TotCartonsBox.Text = $"الكراتين المجدولة: {open.Sum(r => r.PlannedCartons):N0}";
            TotQtyBox.Text = $"الوزن المجدول: {open.Sum(r => r.PlannedKg):N1} كجم";
            TotCustsBox.Text = $"عملاء: {open.Select(r => r.CustomerId).Distinct().Count()}";
            TotLinesBox.Text = $"خطوط: {open.Select(r => r.LineId).Distinct().Count()}";
            DayClosedChip.Text = $"🔒 أُقفل يومه: {closed}";
            bool canCreate = _isolated || DatesErp.Desktop.Views.PermissionGate.Can("production", "Create");
            bool canApprove = _isolated || DatesErp.Desktop.Views.PermissionGate.Can("production", "Approve");
            IssueTodayBtn.IsEnabled = next.CanIssue && canCreate;
            // الإصدار اليدوي من الجدول يشمل التاريخ السابق والحالي والقادم، بعد تحديد صف/مجموعة معلقة.
            IssueSelectedBtn.IsEnabled = open.Any(r => r.IsPending) && canCreate;
            ApproveSelectedBtn.IsEnabled = open.Any(r => r.OrderId != null && r.Status == DocStatuses.ToArabic(DocStatuses.Draft)) && canApprove;
            if (TodayGrid.SelectedItem is TodayProductionRowDto row && row.OrderId is int oid)
                ShowOrderInPlace(oid);
        }
        catch (Exception ex)
        {
            _sheet = null; TodayGrid.ItemsSource = null;
            IssueTodayBtn.IsEnabled = false; IssueSelectedBtn.IsEnabled = false; ApproveSelectedBtn.IsEnabled = false;
            ItemsChip.Text = "📦 البنود المجدولة: 0"; IssuedChip.Text = "🗂 أوامر صادرة: 0";
            PendingChip.Text = "⏳ بانتظار الإصدار: 0";
            TotCartonsBox.Text = "الكراتين المجدولة: 0"; TotQtyBox.Text = "الوزن المجدول: 0 كجم";
            TotCustsBox.Text = "عملاء: 0"; TotLinesBox.Text = "خطوط: 0";
            ClearDetails();
            DayHint.Text = "تعذر تحميل الخطط المجدولة المعتمدة؛ لا إصدار من بيانات قديمة. حدّث الشاشة بعد معالجة الاتصال/الصلاحية.";
            if (!_isolated) AppContainer.Get<DialogService>().HandleException(ex, "Orders.Today");
            else throw;
        }
    }

    private void IssueToday_Click(object sender, RoutedEventArgs e)
    {
        if (_sheet?.CanIssue != true) return;
        if (!_isolated && !AppContainer.Get<DialogService>().Confirm("إصدار أوامر جميع بنود اليوم المعتمدة كما هي؟\nلا تغيير للأصناف أو الكميات، ولا صرف مخزون بمجرد الإصدار.")) return;
        IssueTodayBtn.IsEnabled = false;
        try
        {
            var result = _issue();
            if (!_isolated)
            {
                if (result.Ok) AppContainer.Get<DialogService>().Info(result.Message);
                else AppContainer.Get<DialogService>().Error(result.Message);
            }
            RefreshToday();
            if (!result.Ok) DayHint.Text = result.Message;
        }
        catch (Exception ex)
        {
            RefreshToday();
            if (!_isolated) AppContainer.Get<DialogService>().HandleException(ex, "Orders.IssueToday");
            else throw;
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_sheet?.Rows == null) return;
        bool allSelected = _sheet.Rows.Where(r => r.IsPending).All(r => r.IsSelected);
        foreach (var r in _sheet.Rows.Where(r => r.IsPending)) r.IsSelected = !allSelected;
    }

    private void IssueSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_sheet?.Rows == null) return;
        var sel = _sheet.Rows.Where(r => r.IsPending && r.IsSelected).ToList();
        // السماح بالنقر على الصف ثم الإصدار مباشرة، مع إبقاء مربع ✓ للتحديد المتعدد.
        if (sel.Count == 0 && TodayGrid.SelectedItem is TodayProductionRowDto current && current.IsPending)
            sel.Add(current);
        if (sel.Count == 0) { AppContainer.Get<DialogService>().Info("حدد صفاً معلقاً أو ضع علامة ✓ على بند واحد على الأقل."); return; }
        // التحديد يختار المجموعة الحقيقية؛ الإصدار ينقل كل بنودها الأصلية دون إصدار جزئي.
        var groups = sel.GroupBy(r => new { r.PlanId, r.ScheduledDate, r.CustomerId, r.ShiftId, r.LineId }).ToList();
        if (!_isolated && !AppContainer.Get<DialogService>().Confirm($"إصدار {groups.Count} مجموعة من البنود المحددة بكميات الخطة الأصلية؟")) return;
        try
        {
            int issued = 0;
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            // كل مجموعة تحمل تاريخها وعميلها وورديتها وخطها؛ لا نرسلها لمسار اليوم فقط.
            foreach (var g in groups)
            {
                var res = svc.IssuePlanGroup(g.Key.PlanId, g.Key.ScheduledDate, g.Key.CustomerId, g.Key.ShiftId, g.Key.LineId);
                if (!res.Ok) { AppContainer.Get<DialogService>().Error(res.Message); return; }
                issued++;
            }
            AppContainer.Get<DialogService>().Info($"تم إصدار {issued} أمر من مجموعات البنود المحددة ({groups.Count} مجموعة) بكميات الخطة الأصلية.");
            RefreshToday();
        }
        catch (Exception ex)
        {
            if (!_isolated) AppContainer.Get<DialogService>().HandleException(ex, "Orders.IssueSelected");
            else throw;
        }
    }

    private void ApproveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_sheet?.Rows == null) return;
        var selected = _sheet.Rows
            .Where(r => r.OrderId != null && r.Status == DocStatuses.ToArabic(DocStatuses.Draft) && r.IsSelected)
            .ToList();
        if (selected.Count == 0 && TodayGrid.SelectedItem is TodayProductionRowDto current
            && current.OrderId != null && current.Status == DocStatuses.ToArabic(DocStatuses.Draft))
            selected.Add(current);
        var orderIds = selected.Where(r => r.OrderId != null).Select(r => r.OrderId!.Value).Distinct().ToList();
        if (orderIds.Count == 0)
        {
            AppContainer.Get<DialogService>().Info("حدد أمراً في حالة مسودة أولاً، ثم اضغط «اعتماد الأمر».");
            return;
        }
        if (!_isolated && !AppContainer.Get<DialogService>().Confirm($"اعتماد {orderIds.Count} أمر إنتاج؟ سيُعاد فحص الخطة والطاقة والعبوة والدفعات قبل الانتقال للمرحلة التالية.")) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            foreach (var id in orderIds)
            {
                var result = svc.ApproveOrder(id);
                if (!result.Ok) { AppContainer.Get<DialogService>().Error(result.Message); return; }
            }
            AppContainer.Get<DialogService>().Info($"تم اعتماد {orderIds.Count} أمر إنتاج والانتقال إلى حالة الجدولة.");
            RefreshToday();
        }
        catch (Exception ex)
        {
            if (!_isolated) AppContainer.Get<DialogService>().HandleException(ex, "Orders.ApproveSelected");
            else throw;
        }
    }

    /// <summary>§v1.50.36 — يعرض المستند أسفل الجدول دون إخفاء قائمة اليوم.</summary>
    private void ShowOrderInPlace(int id)
    {
        if (_isolated) return;
        if (_shownOrderId == id && _panel != null) return;
        DetailsHost.Content = _panel = new OrderDocumentPanel(id)
        {
            Navigator = ShowOrderInPlace,
            Changed = RefreshToday,
            ReturnToList = ClearDetails
        };
        _shownOrderId = id;
        DetailsHint.Visibility = Visibility.Collapsed;
    }

    private void ClearDetails()
    {
        _panel = null; _shownOrderId = null;
        DetailsHost.Content = null;
        DetailsHint.Visibility = Visibility.Visible;
    }

    private void AddFromPlan_Click(object sender, RoutedEventArgs e)
        => OrderDocumentPanel.PromptAddFromPlan(Window.GetWindow(this), ShowOrderInPlace, RefreshToday);
    private void Search_Click(object sender, RoutedEventArgs e)
        => OrderDocumentPanel.PromptSearch(Window.GetWindow(this), ShowOrderInPlace);
    private void Save_Click(object sender, RoutedEventArgs e)
    { if (_panel == null) { AppContainer.Get<DialogService>().Info("اختر أمراً من الجدول أولاً."); return; } _panel.InvokeSave(); }
    private void Edit_Click(object sender, RoutedEventArgs e)
    { if (_panel == null) { AppContainer.Get<DialogService>().Info("اختر أمراً من الجدول أولاً."); return; } _panel.InvokeEdit(); }
    private void Delete_Click(object sender, RoutedEventArgs e)
    { if (_panel == null) { AppContainer.Get<DialogService>().Info("اختر أمراً من الجدول أولاً."); return; } _panel.InvokeDelete(); }
    private void Print_Click(object sender, RoutedEventArgs e)
    { if (_panel == null) { AppContainer.Get<DialogService>().Info("اختر أمراً من الجدول أولاً."); return; } _panel.InvokePrint(); }

    private void TodayGrid_DoubleClick(object sender, MouseButtonEventArgs e) => TodayGrid_SelectionChanged(sender, null);
    private void TodayGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if ((TodayGrid.SelectedItem as TodayProductionRowDto)?.OrderId is int id) ShowOrderInPlace(id);
        else if ((TodayGrid.SelectedItem as TodayProductionRowDto)?.IsPending == true)
            DetailsHint.Text = "هذا البند بانتظار الإصدار — حدده ثم اضغط «✅ إصدار المحدد فقط» أو استخدم «➕ إضافة أمر من الخطة».";
    }
}
