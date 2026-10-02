using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DatesErp.Core.Common;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Screens;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views;

/// <summary>
/// §B97 — نافذة تفاصيل خطة الإنتاج (نمط النقر المزدوج):
/// رأس كامل + بنود + تقدم العملاء + توزيع يومي + تاريخ المستند + شريط إجراء للدور×الحالة.
/// قراءة فقط — كل الحقل المجمّع آلياً؛ الإجراء الوحيد هو قرار الاعتماد/الإرجاع/الإرسال.
/// </summary>
public partial class PlanDetailWindow : Window
{
    private readonly int _planId;
    private string _status = DocStatuses.Draft;

    public PlanDetailWindow(int planId)
    {
        InitializeComponent();
        _planId = planId;
        Loaded += (_, _) => Load();
    }

    /// <summary>§B102.1 (إصلاح فحص) — نقطة الدخول إلى «تشغيل اليوم» (B98): كانت النافذة بلا فاتح بعد الدمج.</summary>


    private void Load()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var session = AppContainer.Get<Infrastructure.Session.SessionContext>();

            var plan = db.ProductionPlans.AsNoTracking()
                .Include(p => p.Items).FirstOrDefault(p => p.Id == _planId);
            if (plan == null)
            {
                AppContainer.Get<DialogService>().Error("الخطة غير موجودة.");
                Close();
                return;
            }
            _status = plan.Status ?? DocStatuses.Draft;

            Title = $"تفاصيل خطة الإنتاج — {plan.DocumentNumber}";
            HeadTitle.Text = $"خطة الإنتاج: {plan.PlanTitle}";
            HeadStatus.Text = $"الحالة: {StatusAr(_status)}";

            // لافتة سبب الحالة (إرجاع/توقف) — §B97
            if (!string.IsNullOrWhiteSpace(plan.StatusReason))
            {
                ReasonBanner.Visibility = Visibility.Visible;
                ReasonText.Text = $"⚠️ سبب الإرجاع: {plan.StatusReason}";
            }
            else ReasonBanner.Visibility = Visibility.Collapsed;

            FNumber.Text = plan.DocumentNumber;
            FTitle.Text = plan.PlanTitle;
            FType.Text = plan.PlanType == "Daily" ? "يومية" : "فترية (خطط طويلة)";
            FPeriod.Text = plan.StartDate != null && plan.EndDate != null
                ? $"{plan.StartDate:dd/MM/yyyy} → {plan.EndDate:dd/MM/yyyy}"
                : (plan.StartDate?.ToString("dd/MM/yyyy") ?? "—");

            var users = db.Users.AsNoTracking().ToDictionary(u => u.Id, u => u.FullName);
            FCreator.Text = plan.CreatedBy != null && users.TryGetValue(plan.CreatedBy.Value, out var cn) ? cn : "—";
            FCreated.Text = plan.CreatedDate.ToString("dd/MM/yyyy HH:mm");
            FApprover.Text = plan.IsApproved
                ? (plan.ApprovedBy != null && users.TryGetValue(plan.ApprovedBy.Value, out var an) ? an : "—") +
                  (plan.ApprovedDate != null ? $" — {plan.ApprovedDate:dd/MM/yyyy HH:mm}" : "")
                : "لم تُعتمد بعد";

            double totalKg = plan.Items.Sum(i => i.PlannedQtyKg);
            int totalCtn = plan.Items.Sum(i => i.PlannedCartons);
            FTotal.Text = $"{totalKg:N1} كجم ({totalCtn:N0} كرتون) — {plan.Items.Count} بند";

            if (string.IsNullOrWhiteSpace(plan.Notes)) NotesBox.Visibility = Visibility.Collapsed;
            else { NotesBox.Visibility = Visibility.Visible; FNotes.Text = plan.Notes; }

            // ── بنود الخطة (كل الحقول مجمّعة — لا إعادة إدخال) ──
            var products = db.Products.AsNoTracking().ToDictionary(p => p.Id, p => p.ProductNameAr);
            var packs = db.PackagingTypes.AsNoTracking().ToDictionary(p => p.Id, p => p.PackageNameAr);
            var customers = db.Customers.AsNoTracking().ToDictionary(c => c.Id, c => c.CustomerName);
            var lots = db.Lots.AsNoTracking().ToDictionary(l => l.Id, l => l.LotCode);
            var shifts = db.Shifts.AsNoTracking().ToDictionary(s => s.Id, s => s.ShiftNameAr);
            var lines = db.ProductionLines.AsNoTracking().ToDictionary(l => l.Id, l => l.LineNameAr);

            ItemsGrid.ItemsSource = plan.Items.OrderBy(i => i.ScheduledDate).ThenBy(i => i.Id).Select(i => new
            {
                Date = i.ScheduledDate?.ToString("dd/MM/yyyy") ?? "—",
                Shift = i.SuggestedShiftId != null && shifts.TryGetValue(i.SuggestedShiftId.Value, out var sh) ? sh : "—",
                Line = i.SuggestedLineId != null && lines.TryGetValue(i.SuggestedLineId.Value, out var ln) ? ln : "—",
                Customer = i.CustomerId != null && customers.TryGetValue(i.CustomerId.Value, out var cu) ? cu : "—",
                Lot = i.LotId != null && lots.TryGetValue(i.LotId.Value, out var lt) ? lt : "—",
                Product = products.ContainsKey(i.ProductId) ? products[i.ProductId] : $"#{i.ProductId}",
                Pack = i.PackagingTypeId != null && packs.TryGetValue(i.PackagingTypeId.Value, out var pk) ? pk : "—",
                PlannedKg = i.PlannedQtyKg.ToString("N1"),
                PlannedCtn = i.PlannedCartons.ToString("N0"),
                ProducedKg = i.ProducedQtyKg.ToString("N1"),
                AcceptedKg = i.AcceptedQtyKg.ToString("N1"),
                RemainingKg = Math.Max(0, i.PlannedQtyKg - i.ProducedQtyKg).ToString("N1"),
                ExecStatus = ExecAr(i.ExecutionStatus)
            }).ToList();

            // ── تقدم العملاء + التوزيع اليومي + سجل التنفيذ (خدمة التقدم القائمة) ──
            var progress = scope.ServiceProvider.GetRequiredService<IPlanProgressService>();
            try
            {
                CustomerGrid.ItemsSource = progress.GetPlanProgressByCustomer(_planId);
                DayGrid.ItemsSource = progress.GetPlanDayStatuses(_planId);
                // §B98 — سجل التنفيذ: مخطط/مأمور/منتج/مقبول/مسلَّم + حالة اليوم (مكتمل/متعثر/قيد التنفيذ/لم يبدأ)
                ExecLogGrid.ItemsSource = progress.GetExecutionLog(_planId);
            }
            catch { CustomerGrid.ItemsSource = null; DayGrid.ItemsSource = null; ExecLogGrid.ItemsSource = null; }

            // ── تاريخ المستند: من سجل التدقيق المركزي ──
            HistoryGrid.ItemsSource = db.AuditLogs.AsNoTracking()
                .Where(a => a.DocumentNumber == plan.DocumentNumber)
                .OrderByDescending(a => a.ActionDate).Take(30)
                .Select(a => new
                {
                    Time = a.ActionDate.ToString("dd/MM/yyyy HH:mm"),
                    User = a.UserName,
                    Action = ActionAr(a.ActionType),
                    Detail = DetailAr(plan, a)
                }).ToList();

            // ── شريط الإجراء: أزرار الدور × الحالة ──
            bool canEdit = session.Can("planning", "Edit");
            bool canApprove = session.Can("planning", "Approve");
            bool canCancel = session.Can("planning", "Cancel");
            bool isCreator = plan.CreatedBy == session.UserId;
            bool isUnderReview = _status == "UnderApproval" || _status == DocStatuses.Submitted;

            BtnSubmit.Visibility = (canEdit && isCreator && (_status == DocStatuses.Draft) && !plan.IsClosed) ? Visibility.Visible : Visibility.Collapsed;
            BtnApprove.Visibility = (canApprove && isUnderReview && !plan.IsClosed) ? Visibility.Visible : Visibility.Collapsed;
            BtnReturn.Visibility = (canApprove && isUnderReview && !plan.IsClosed) ? Visibility.Visible : Visibility.Collapsed;
            BtnUnapprove.Visibility = (canCancel && plan.IsApproved && !plan.IsClosed) ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "PlanDetail.Load"); }
    }

    private void Submit_Click(object sender, RoutedEventArgs e) => DoAction(svc => svc.SubmitPlan(_planId), "إرسال للاعتماد");
    private void Approve_Click(object sender, RoutedEventArgs e)
    {
        if (!AppContainer.Get<DialogService>().Confirm("هل تريد اعتماد خطة الإنتاج هذه رسمياً؟\nستتوفر فوراً لمدير الإنتاج لإصدار أوامر التشغيل.")) return;
        DoAction(svc => svc.ApprovePlan(_planId), "اعتماد");
    }
    private void Return_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialog("إرجاع الخطة للصانع للتعديل", "سبب الإرجاع (إجباري — 10 أحرف فأكثر):") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if ((dlg.Value ?? "").Trim().Length < 10)
        {
            AppContainer.Get<DialogService>().Error("السبب إجباري — 10 أحرف على الأقل.");
            return;
        }
        DoAction(svc => svc.ReturnPlan(_planId, dlg.Value), "إرجاع");
    }
    private void Unapprove_Click(object sender, RoutedEventArgs e)
    {
        if (!AppContainer.Get<DialogService>().Confirm("إلغاء الاعتماد يعيد الخطة مسودة (يعاد احتساب الحجوزات).\nمسموح فقط ما لم تصدر أوامر من الخطة.")) return;
        DoAction(svc => svc.UnapprovePlan(_planId), "إلغاء اعتماد");
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void DoAction(Func<IPlanningService, OpResult> act, string label)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var r = act(svc);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info($"{label}: {r.Message}");
            Close(); // عودة للوحة — اللوحة تُحدَّث تلقائياً
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, $"PlanDetail.{label}"); }
    }

    private static string StatusAr(string s)
    {
        if (s == "UnderApproval") return "بانتظار اعتماد المدير العام";
        return DocStatuses.ToArabic(s);
    }

    private static string ExecAr(string s) => s switch
    {
        "Completed" => "✅ مكتمل",
        "Partial" => "🟠 جزئي",
        "InProgress" => "🔵 قيد التنفيذ",
        _ => "⚪ لم يبدأ"
    };

    private static string ActionAr(string a) => a switch
    {
        "Create" => "إنشاء",
        "Edit" => "تعديل",
        "Approve" => "اعتماد",
        "Cancel" => "إلغاء",
        "Issue" => "تحرير",
        "Delete" => "حذف",
        "Post" => "ترحيل",
        _ => a ?? "—"
    };

    /// <summary>سطر التاريخ: يبرز الانتقال بين الحالات إن وُجد في سجل التنقّل.</summary>
    private static string DetailAr(DatesErp.Core.Domain.Entities.ProductionPlan plan, DatesErp.Core.Domain.Entities.AuditLog a)
    {
        // استخراج الحالة القديمة/الجديدة من JSON القيمة إن أمكن
        string Old(string j)
        {
            if (string.IsNullOrWhiteSpace(j)) return null;
            var i = j.IndexOf("\"Status\":\"", StringComparison.Ordinal);
            if (i < 0) return null;
            int s = i + 10, e = j.IndexOf('"', s);
            return e > s ? j[s..e] : null;
        }
        var o = Old(a.OldValue);
        var n = Old(a.NewValue);
        if (o != null && n != null && o != n)
            return $"الحالة: {StatusAr(o)} ← {StatusAr(n)}";
        return a.ActionType == "Create" ? "إنشاء المستند" : null;
    }
}
