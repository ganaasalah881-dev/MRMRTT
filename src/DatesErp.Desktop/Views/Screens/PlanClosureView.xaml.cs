using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §B79 — شاشة «إقفال خطة الإنتاج»: تعرض الخطة وكل أوامرها وملخصاتها،
/// وتسمح بالإقفال الرسمي فقط عند اكتمال وإقفال جميع الأوامر المطلوبة،
/// مع إقفال استثنائي موثق بالسبب وإعادة فتح بصلاحية خاصة.
/// </summary>
public partial class PlanClosureView : UserControl
{
    public PlanClosureView()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadPlans();
    }

    /// <summary>§B83: معرفات الخطط بترتيب القائمة — التحديد بالقائمة لا بإعادة استعلام القاعدة.</summary>
    private readonly List<int> _planIds = new();

    private static IPlanClosureService Svc()
        => AppContainer.NewScope().ServiceProvider.GetRequiredService<IPlanClosureService>();

    private void LoadPlans()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErp.Infrastructure.Persistence.DatesErpDbContext>();
            var plans = db.ProductionPlans.AsNoTracking().OrderByDescending(p => p.Id).ToList();
            var keep = PlanPickBox.SelectedItem?.ToString();
            _planIds.Clear();
            _planIds.AddRange(plans.Select(p => p.Id));
            PlanPickBox.ItemsSource = plans.Select(p =>
                $"{p.DocumentNumber} — {p.PlanTitle} ({p.StartDate:dd/MM/yyyy} ← {p.EndDate:dd/MM/yyyy})").ToList();
            if (keep != null)
                for (int i = 0; i < PlanPickBox.Items.Count; i++)
                    if (PlanPickBox.Items[i].ToString() == keep) { PlanPickBox.SelectedIndex = i; break; }
            else if (PlanPickBox.Items.Count > 0) PlanPickBox.SelectedIndex = 0;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "PlanClosure.Load"); }
    }

    private int? SelectedPlanId()
        => PlanPickBox.SelectedIndex >= 0 && PlanPickBox.SelectedIndex < _planIds.Count
            ? _planIds[PlanPickBox.SelectedIndex] : (int?)null;

    private void PlanPick_Changed(object sender, SelectionChangedEventArgs e) => Fill();
    private void Refresh_Click(object sender, RoutedEventArgs e) { LoadPlans(); Fill(); }

    private void Fill()
    {
        var id = SelectedPlanId();
        if (id == null) return;
        try
        {
            var i = Svc().GetInfo(id.Value);
            PlanStatusBox.Text = $"حالة الخطة: {i.StatusAr}";
            HNumber.Text = i.PlanNumber; HType.Text = i.PlanTypeAr;
            HStart.Text = i.StartDate; HEnd.Text = i.EndDate;
            HClosedAt.Text = i.ClosedAt; HClosedBy.Text = i.ClosedByName;

            CTotalOrders.Text = $"إجمالي الأوامر: {i.TotalOrders}";
            COpen.Text = $"مفتوحة: {i.OpenOrders}";
            CInProgress.Text = $"قيد الإنتاج: {i.InProgressOrders}";
            CCompleted.Text = $"مكتملة: {i.CompletedOrders}";
            CClosed.Text = $"مقفلة: {i.ClosedOrders}";
            CCancelled.Text = $"ملغاة: {i.CancelledOrders}";
            CPlanned.Text = $"المخطط: {i.PlannedTotal:N0}";
            CProduced.Text = $"الفعلي: {i.ProducedTotal:N0}";
            CClosedQty.Text = $"المقفل: {i.ClosedTotal:N0}";
            CRemaining.Text = $"المتبقي: {i.Remaining:N0}";
            CSettled.Text = $"فروقات معالجة: {i.SettledVariance:N0}";
            CUnprocessed.Text = $"أوامر غير معالجة: {i.UnprocessedOrders}";

            // §B83: شارة الجاهزية الصريحة — الحكم والسبب من البيانات الفعلية لا من مظهر الشاشة
            if (i.IsClosed)
            {
                ReadinessBanner.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEC, 0xFD, 0xF5));
                ReadinessBanner.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x53, 0x2D));
                ReadinessBox.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x53, 0x2D));
                ReadinessBox.Text = $"🟢 الخطة مقفلة رسمياً — أُقفلت {i.ClosedAt} بواسطة {i.ClosedByName}. لا تعديل عادياً عليها (إعادة الفتح بصلاحية خاصة وسبب موثق).";
            }
            else if (i.CanClose)
            {
                ReadinessBanner.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEC, 0xFD, 0xF5));
                ReadinessBanner.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A));
                ReadinessBox.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x53, 0x2D));
                ReadinessBox.Text = "🟢 الخطة جاهزة للإقفال — جميع أوامر الإنتاج المطلوبة مقفلة، وجميع الفروقات معالجة، ولا توجد أوامر معلقة أو عمليات إنتاج مفتوحة.";
            }
            else
            {
                ReadinessBanner.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFE, 0xF2, 0xF2));
                ReadinessBanner.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
                ReadinessBox.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x99, 0x1B, 0x1B));
                ReadinessBox.Text = "🔴 الخطة غير جاهزة للإقفال — " + (i.Blockers.Count > 0 ? string.Join(" · ", i.Blockers) : "لا توجد أوامر مقفلة بعد.");
            }

            OrdersGrid.ItemsSource = i.Orders;
            CustomersGrid.ItemsSource = i.Customers;
            ProductsGrid.ItemsSource = i.Products;

            BlockersBox.Text = i.Blockers.Count == 0
                ? (i.IsClosed ? "الخطة مقفلة رسمياً — لا تعديل عادياً عليها." : "✅ جميع شروط الإقفال مستوفاة.")
                : "أسباب منع الإقفال:\n- " + string.Join("\n- ", i.Blockers);
            ClosePlanBtn.IsEnabled = i.CanClose;
            ForceCloseBtn.Visibility = !i.CanClose && !i.IsClosed && i.PlanId > 0 ? Visibility.Visible : Visibility.Collapsed;
            ReopenBtn.Visibility = i.IsClosed ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "PlanClosure.Fill"); }
    }

    // ═══════════ §B83: الأزرار الرئيسية — إضافة/بحث/تعديل/حذف (والحفظ = الإقفال نفسه) ═══════════

    /// <summary>➕ إضافة: خطة جديدة تُنشأ من شاشة الخطط (لا إنشاء خطط من شاشة الإقفال).</summary>
    private void AddPlan_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.OpenScreen("planning");

    /// <summary>🔍 بحث: نافذة البحث الموحدة في الخطط — اختيار خطة يحمّلها هنا فوراً.</summary>
    private void SearchPlan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var win = new DocSearchWindow("خطط الإنتاج",
                new List<SearchFieldDef>
                {
                    new() { Key = "doc", LabelAr = "رقم الخطة" },
                    new() { Key = "title", LabelAr = "العنوان" }
                },
                cond =>
                {
                    using var scope = AppContainer.NewScope();
                    var db = scope.ServiceProvider.GetRequiredService<DatesErp.Infrastructure.Persistence.DatesErpDbContext>();
                    var q = db.ProductionPlans.AsNoTracking().AsQueryable();
                    if (!string.IsNullOrWhiteSpace(cond.GetValueOrDefault("doc")))
                        q = q.Where(p => p.DocumentNumber.Contains(cond["doc"].Trim()));
                    if (!string.IsNullOrWhiteSpace(cond.GetValueOrDefault("title")))
                        q = q.Where(p => p.PlanTitle.Contains(cond["title"].Trim()));
                    var res = new SearchResult { Columns = new List<string> { "رقم الخطة", "العنوان", "الفترة", "الحالة" } };
                    foreach (var p in q.OrderByDescending(x => x.Id).ToList())
                        res.Rows.Add((p.Id, new object[]
                        {
                            p.DocumentNumber, p.PlanTitle,
                            $"{p.StartDate:dd/MM/yyyy} ← {p.EndDate:dd/MM/yyyy}",
                            p.IsClosed ? "مقفلة" : p.Status
                        }));
                    return res;
                });
            win.Owner = Window.GetWindow(this);
            if (win.ShowDialog() == true && win.SelectedId != null)
            {
                int idx = _planIds.IndexOf(win.SelectedId.Value);
                if (idx >= 0) { PlanPickBox.SelectedIndex = idx; Fill(); }
                else { LoadPlans(); idx = _planIds.IndexOf(win.SelectedId.Value); if (idx >= 0) PlanPickBox.SelectedIndex = idx; Fill(); }
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "PlanClosure.Search"); }
    }

    /// <summary>✏️ تعديل: يفتح الخطة في شاشة الخطط — المقفلة تُرفض (إعادة الفتح أولاً).</summary>
    private void EditPlan_Click(object sender, RoutedEventArgs e)
    {
        var id = SelectedPlanId(); if (id == null) { AppContainer.Get<DialogService>().Error("اختر خطة أولاً."); return; }
        var info = Svc().GetInfo(id.Value);
        if (info.IsClosed)
        { AppContainer.Get<DialogService>().Error("الخطة مقفلة — التعديل العادي ممنوع بعد الإقفال.\nإن لزم التصحيح: «إعادة فتح الخطة» بصلاحية خاصة وسبب يُسجل في التدقيق."); return; }
        MainWindow.PendingPlanIdToOpen = id.Value;
        (Window.GetWindow(this) as MainWindow)?.OpenScreen("planning");
    }

    /// <summary>🗑️ حذف: مسودة لم تصدر منها أوامر فقط — الحراس كاملة في الخدمة.</summary>
    private void DeletePlan_Click(object sender, RoutedEventArgs e)
    {
        var id = SelectedPlanId(); if (id == null) { AppContainer.Get<DialogService>().Error("اختر خطة أولاً."); return; }
        var info = Svc().GetInfo(id.Value);
        if (!AppContainer.Get<DialogService>().Confirm($"حذف الخطة {info.PlanNumber}؟\n(المسودة التي لم تصدر منها أوامر فقط — المعتمدة/المقفلة لا تُحذف)")) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var r = scope.ServiceProvider.GetRequiredService<IPlanningService>().DeletePlan(id.Value);
            if (!r.Ok) AppContainer.Get<DialogService>().Error(r.Message);
            else AppContainer.Get<DialogService>().Info(r.Message);
            LoadPlans(); Fill();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "PlanClosure.Delete"); }
    }

    private void ClosePlan_Click(object sender, RoutedEventArgs e)
    {
        var id = SelectedPlanId(); if (id == null) return;
        if (!AppContainer.Get<DialogService>().Confirm("إقفال الخطة رسمياً؟ لن يُسمح بالتعديل العادي بعدها.")) return;
        try
        {
            var r = Svc().ClosePlanFinal(id.Value);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            Fill();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "PlanClosure.Close"); }
    }

    private void ForceClose_Click(object sender, RoutedEventArgs e)
    {
        var id = SelectedPlanId(); if (id == null) return;
        var dlg = new Views.InputDialog("إقفال استثنائي", "سبب الإقفال الاستثنائي (يُسجل في التدقيق مع المستخدم والوقت والأوامر غير المكتملة):")
        { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var r = Svc().ClosePlanFinal(id.Value, dlg.Value, force: true);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            Fill();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "PlanClosure.ForceClose"); }
    }

    private void Reopen_Click(object sender, RoutedEventArgs e)
    {
        var id = SelectedPlanId(); if (id == null) return;
        var dlg = new Views.InputDialog("إعادة فتح الخطة", "سبب إعادة الفتح (يُسجل في التدقيق مع الحالتين السابقة والجديدة):")
        { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var r = Svc().ReopenPlan(id.Value, dlg.Value);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            Fill();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "PlanClosure.Reopen"); }
    }
}
