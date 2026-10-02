using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Domain.Entities;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §B100 — سجل التدقيق بالفلاتر: مستخدم/إجراء/شاشة/مستند/فترة.
/// قراءة فقط — السجل إلزامي غير قابل للتعديل أو الحذف (§48).
/// §44 — التصفية والترقيم في قاعدة البيانات نفسها: لا تحميل شامل في الذاكرة.
/// </summary>
public partial class AuditFilterView : UserControl
{
    private const int PageSize = 500;
    private int _page, _total;

    private class AuditRowUi
    {
        public int Id { get; set; }
        public DateTime Date { get; set; }
        public string Time { get; set; }
        public string User { get; set; }
        public string Machine { get; set; }
        public string Action { get; set; }
        public string Screen { get; set; }
        public string Document { get; set; }
        public string OldValue { get; set; }
        public string NewValue { get; set; }
        public string Detail { get; set; }
    }

    public AuditFilterView()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadFilters();
    }

    private static string Trunc(string s, int max = 220)
        => string.IsNullOrWhiteSpace(s) ? "—" : (s.Length > max ? s[..max] + "…" : s);

    private void LoadFilters()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            // §44 — خيارات الفلاتر من قاعدة البيانات مباشرة (Distinct في الخادم)
            FUser.Items.Clear();
            FUser.Items.Add(new ComboBoxItem { Content = "— الكل —" });
            foreach (var u in db.AuditLogs.AsNoTracking().Select(a => a.UserName).Distinct().OrderBy(x => x).Take(300).ToList())
                FUser.Items.Add(new ComboBoxItem { Content = u ?? "—" });
            FAction.Items.Clear();
            FAction.Items.Add(new ComboBoxItem { Content = "— الكل —" });
            foreach (var a in db.AuditLogs.AsNoTracking().Select(a => a.ActionType).Distinct().OrderBy(x => x).Take(300).ToList())
                FAction.Items.Add(new ComboBoxItem { Content = a ?? "—" });
            FUser.SelectedIndex = 0;
            FAction.SelectedIndex = 0;
            Apply();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Audit.Load"); }
    }

    private void Apply_Click(object sender, RoutedEventArgs e) { _page = 0; Apply(); }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        FUser.SelectedIndex = 0;
        FAction.SelectedIndex = 0;
        FScreen.Text = "";
        FDoc.Text = "";
        FFrom.Text = "";
        FTo.Text = "";
        _page = 0;
        Apply();
    }

    private void PageOlder_Click(object sender, RoutedEventArgs e) { _page++; Apply(); }
    private void PageNewer_Click(object sender, RoutedEventArgs e) { _page = Math.Max(0, _page - 1); Apply(); }

    private void Apply()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            string user = (FUser.SelectedItem as ComboBoxItem)?.Content?.ToString();
            string action = (FAction.SelectedItem as ComboBoxItem)?.Content?.ToString();
            string screen = FScreen.Text?.Trim();
            string doc = FDoc.Text?.Trim();
            bool hasFrom = UiDate.TryParse(FFrom.Text?.Trim(), out var from);
            bool hasTo = UiDate.TryParse(FTo.Text?.Trim(), out var to);

            IQueryable<AuditLog> q = db.AuditLogs.AsNoTracking();
            if (!string.IsNullOrEmpty(user) && user != "— الكل —")
            {
                string u = user == "—" ? null : user;
                q = q.Where(a => a.UserName == u);
            }
            if (!string.IsNullOrEmpty(action) && action != "— الكل —")
            {
                string t = action == "—" ? null : action;
                q = q.Where(a => a.ActionType == t);
            }
            if (!string.IsNullOrEmpty(screen)) q = q.Where(a => a.ScreenName.Contains(screen));
            if (!string.IsNullOrEmpty(doc)) q = q.Where(a => a.DocumentNumber.Contains(doc));
            if (hasFrom) q = q.Where(a => a.ActionDate >= from.Date);
            if (hasTo) q = q.Where(a => a.ActionDate < to.Date.AddDays(1));

            _total = q.Count();
            int pageCount = Math.Max(1, (int)Math.Ceiling(_total / (double)PageSize));
            if (_page >= pageCount) _page = pageCount - 1;
            if (_page < 0) _page = 0;

            var rows = q.OrderByDescending(a => a.ActionDate)
                .Skip(_page * PageSize).Take(PageSize)
                .ToList()
                .Select(a => new AuditRowUi
                {
                    Id = a.Id,
                    Date = a.ActionDate,
                    Time = a.ActionDate.ToString("dd/MM/yyyy HH:mm:ss"),
                    User = a.UserName ?? "—",
                    Machine = a.MachineName ?? a.ComputerName ?? "—",
                    Action = a.ActionType ?? "—",
                    Screen = a.ScreenName ?? "—",
                    Document = a.DocumentNumber ?? "—",
                    OldValue = a.OldValue,
                    NewValue = a.NewValue,
                    Detail = $"{Trunc(a.OldValue)} ← {Trunc(a.NewValue)}"
                }).ToList();

            Grid.ItemsSource = rows;
            EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            CountLabel.Text = rows.Count == 0
                ? "لا توجد سجلات مطابقة — عدّل المرشحات ثم اضغط تطبيق"
                : $"عرض {rows.Count} من {_total} سجل — صفحة {_page + 1} من {pageCount}";
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Audit.Apply"); }
    }

    private static class UiDate
    {
        public static bool TryParse(string s, out DateTime d)
        {
            d = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            return DateTime.TryParseExact(s, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)
                || DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
        }
    }

    /// <summary>كل الحقل (ما قبل/ما بعد كاملاً) — نمط §B89.</summary>
    private void Grid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is not AuditRowUi r) return;
        var dlg = new RecordDetailsDialog("سجل التدقيق", new List<(string, string)>
        {
            ("التاريخ والوقت", r.Date.ToString("dd/MM/yyyy HH:mm:ss")),
            ("المستخدم", r.User),
            ("الجهاز", r.Machine),
            ("الإجراء", r.Action),
            ("الشاشة", r.Screen),
            ("المستند", r.Document),
            ("القيمة قبل", r.OldValue ?? "—"),
            ("القيمة بعد", r.NewValue ?? "—"),
            ("معرف السجل", r.Id.ToString())
        }) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }
}
