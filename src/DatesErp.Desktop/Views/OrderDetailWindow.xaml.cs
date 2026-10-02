using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Screens;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views;

/// <summary>
/// §B98 — أمر التشغيل: تفاصيل كاملة + أزرار الدورة (بدء/إيقاف/استئناف/إقفال اليوم/إلغاء)
/// حسب الدور×الحالة — بلا تنقّل بين الشاشات (B94).
/// </summary>
public partial class OrderDetailWindow : Window
{
    private readonly int _orderId;
    private string _status = DocStatuses.Draft;

    public OrderDetailWindow(int orderId)
    {
        InitializeComponent();
        _orderId = orderId;
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var session = AppContainer.Get<Infrastructure.Session.SessionContext>();

            var order = db.ProductionOrders.AsNoTracking().Include(o => o.Items).FirstOrDefault(o => o.Id == _orderId);
            if (order == null)
            {
                AppContainer.Get<DialogService>().Error("أمر الإنتاج غير موجود.");
                Close();
                return;
            }
            _status = order.Status ?? DocStatuses.Draft;

            Title = $"أمر التشغيل — {order.DocumentNumber}";
            HeadTitle.Text = $"أمر التشغيل: {order.DocumentNumber}";
            HeadStatus.Text = $"الحالة: {DocStatuses.ToArabic(_status)}";

            if (!string.IsNullOrWhiteSpace(order.StatusReason))
            {
                ReasonBanner.Visibility = Visibility.Visible;
                ReasonText.Text = $"⚠️ سبب التوقف: {order.StatusReason}";
            }
            else ReasonBanner.Visibility = Visibility.Collapsed;

            var customers = db.Customers.AsNoTracking().ToDictionary(c => c.Id, c => c.CustomerName);
            var shifts = db.Shifts.AsNoTracking().ToDictionary(s => s.Id, s => s.ShiftNameAr);
            var lines = db.ProductionLines.AsNoTracking().ToDictionary(l => l.Id, l => l.LineNameAr);
            var lots = db.Lots.AsNoTracking().ToDictionary(l => l.Id, l => l.LotCode);
            var products = db.Products.AsNoTracking().ToDictionary(p => p.Id, p => p.ProductNameAr);
            var packs = db.PackagingTypes.AsNoTracking().ToDictionary(p => p.Id, p => p.PackageNameAr);

            FNumber.Text = order.DocumentNumber;
            FCustomer.Text = order.CustomerId != null && customers.TryGetValue(order.CustomerId.Value, out var cu) ? cu : "—";
            FDate.Text = order.ProductionDate?.ToString("dd/MM/yyyy") ?? "—";
            FShift.Text = (order.ShiftId != null && shifts.TryGetValue(order.ShiftId.Value, out var sh) ? sh : "—") + " / " +
                          (order.LineId != null && lines.TryGetValue(order.LineId.Value, out var ln) ? ln : "—");
            FPlan.Text = order.SourcePlanId != null
                ? db.ProductionPlans.AsNoTracking().Where(p => p.Id == order.SourcePlanId).Select(p => p.DocumentNumber).FirstOrDefault() ?? "—"
                : "بدون خطة (يدوي)";
            var firstLot = order.Items.FirstOrDefault(i => i.LotId != null)?.LotId;
            FLot.Text = firstLot != null && lots.TryGetValue(firstLot.Value, out var lt) ? lt : "—";

            double plannedKg = order.Items.Sum(i => i.PlannedQtyKg);
            int plannedCtn = order.Items.Sum(i => i.PlannedCartons);
            double producedKg = order.Items.Sum(i => i.ProducedQtyKg);
            int producedCtn = order.Items.Sum(i => i.ProducedCartons);
            FPlanned.Text = $"{plannedCtn:N0} كرتون ({plannedKg:N1} كجم)";
            FProduced.Text = $"{producedCtn:N0} كرتون ({producedKg:N1} كجم)" +
                             (plannedKg > 0 ? $" — {Math.Round(producedKg / plannedKg * 100, 0):N0}%" : "");

            ItemsGrid.ItemsSource = order.Items.Select(i => new
            {
                ProductName = products.TryGetValue(i.ProductId, out var pn) ? pn : $"#{i.ProductId}",
                PackName = i.PackagingTypeId != null && packs.TryGetValue(i.PackagingTypeId.Value, out var pk) ? pk : "—",
                CustomerName = i.CustomerId != null && customers.TryGetValue(i.CustomerId.Value, out var cc) ? cc : "—",
                LotCode = i.LotId != null && lots.TryGetValue(i.LotId.Value, out var lc) ? lc : "—",
                PlannedCartons = i.PlannedCartons.ToString("N0"),
                PlannedKg = i.PlannedQtyKg.ToString("N1"),
                ProducedCartons = i.ProducedCartons.ToString("N0"),
                ProducedKg = i.ProducedQtyKg.ToString("N1")
            }).ToList();

            HistoryGrid.ItemsSource = db.AuditLogs.AsNoTracking()
                .Where(a => a.DocumentNumber == order.DocumentNumber)
                .OrderByDescending(a => a.ActionDate).Take(25)
                .ToList()   // §سحب: switch expression غير قابل للترجمة في شجرة EF
                .Select(a => new
                {
                    Time = a.ActionDate.ToString("dd/MM/yyyy HH:mm"),
                    User = a.UserName,
                    Action = a.ActionType switch
                    {
                        "Create" => "إنشاء", "Edit" => "تعديل", "Approve" => "اعتماد/صرف",
                        "Cancel" => "إلغاء", "Issue" => "تحرير", "Delete" => "حذف", "Post" => "ترحيل", _ => a.ActionType ?? "—"
                    }
                }).ToList();

            // ── شريط الإجراء: الدور × الحالة ──
            // بطاقة الأمر قد تكون ليوم سابق؛ «التسجيل الفعلي» يسمح بالأمر المتأخر
            // بينما «بدء التنفيذ» يظل رافضاً للمستقبل عبر حارس الخدمة.
            bool isScheduledOrder = session.Can("production", "View") && scope.ServiceProvider.GetRequiredService<IProductionOrderService>().GetScheduledProduction().Rows.Any(r => r.OrderId == order.Id);
            bool canStart = isScheduledOrder && session.Can("execution", "Create");
            bool canStop = session.Can("execution", "Edit");
            bool canCancel = session.Can("production", "Cancel");
            bool started = _status == DocStatuses.InProgress || _status == DocStatuses.Stopped || _status == DocStatuses.Completed || _status == DocStatuses.Closed;

            BtnStart.Visibility = (canStart && order.IsApproved && !order.IsClosed && (_status == DocStatuses.Scheduled || _status == DocStatuses.Approved)) ? Visibility.Visible : Visibility.Collapsed;
            BtnStop.Visibility = (canStop && _status == DocStatuses.InProgress) ? Visibility.Visible : Visibility.Collapsed;
            BtnResume.Visibility = (isScheduledOrder && canStop && _status == DocStatuses.Stopped) ? Visibility.Visible : Visibility.Collapsed;
            // يفتح مسار التسجيل مباشرة للأمر المعتمد المجدول أو المتوقف؛ الحفظ
            // يبدأ جلسة التنفيذ تلقائياً ثم ينفذ الإقفال مرة واحدة.
            BtnClose.Visibility = (canStop && order.IsApproved && !order.IsClosed
                && (_status == DocStatuses.InProgress || _status == DocStatuses.Stopped
                    || _status == DocStatuses.Scheduled || _status == DocStatuses.Approved)) ? Visibility.Visible : Visibility.Collapsed;
            BtnCancel.Visibility = (canCancel && !order.IsClosed && _status != DocStatuses.Cancelled && !started) ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "OrderDetail.Load"); }
    }

    private void Start_Click(object sender, RoutedEventArgs e) => Do(svc => svc.StartOrder(_orderId), "بدء التشغيل");
    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialog("إيقاف مؤقت للأمر", "سبب الإيقاف (إجباري — عطل/نقص مواد/...):") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if ((dlg.Value ?? "").Trim().Length < 10)
        {
            AppContainer.Get<DialogService>().Error("سبب الإيقاف إجباري — 10 أحرف على الأقل.");
            return;
        }
        Do(svc => svc.StopOrder(_orderId, dlg.Value.Trim()), "إيقاف");
    }
    private void Resume_Click(object sender, RoutedEventArgs e) => Do(svc => svc.ResumeOrder(_orderId), "استئناف");

    private void CloseDay_Click(object sender, RoutedEventArgs e)
    {
        Services.ErrorLog.WriteInfo($"OrderDetailWindow.CloseDay_Click OrderId={_orderId}");
        Screens.ProductionDeliveryView.OpenForOrder(_orderId, this);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InputDialog("إلغاء الأمر", "سبب الإلغاء (إجباري):") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if ((dlg.Value ?? "").Trim().Length < 10)
        {
            AppContainer.Get<DialogService>().Error("سبب الإلغاء إجباري — 10 أحرف على الأقل.");
            return;
        }
        Do(svc => svc.CancelOrder(_orderId, dlg.Value.Trim()), "إلغاء");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Do(Func<IProductionOrderService, OpResult> act, string label)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            var r = act(svc);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info($"{label}: {r.Message}");
            Close(); // اللوحة تعيد رسم نفسها بالحالة الجديدة
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, $"OrderDetail.{label}"); }
    }

    /// <summary>نافذة إقفال اليوم: الفعلي المنتَج (كجم + كراتين) + الإرسال للجودة.</summary>
}
