using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views;

/// <summary>
/// §B99 — محضر فحص الجودة: من بطاقة «إنتاج جاهز للفحص / إعادة فحص / قيد التنفيذ».
/// إدخال النتائج (مقبول/مرفوض بالكرتون — الكيلو يُشتق) + القرار + المعمل، ثم الاعتماد.
/// المحضر المعتمد يقفل — التعديل بعده «تصحيح معتمد» بسبب مسجل (صلاحية خاصة).
/// </summary>
public partial class QCWindow : Window
{
    private int _checkId;   // §سحب:غير readonly — يتابع الفحص الجديد المنشأ يدوياً
    private QualityCheck _check;
    private ProductionOrder _order;
    private List<QcInputRow> _inputRows = new();

    /// <summary>سطر إدخال الفحص (كرتون أولاً — وحدة الإنتاج التام).</summary>
    public class QcInputRow
    {
        public int OrderItemId { get; set; }
        public int ProductId { get; set; }
        public int? CustomerId { get; set; }
        public int? LotId { get; set; }
        public int? PackagingTypeId { get; set; }
        public string ProductName { get; set; }
        public string LotCode { get; set; }
        public string PackageName { get; set; }
        public double CartonWeightKg { get; set; }
        public int RemainingCtn { get; set; }
        public int AcceptedCtn { get; set; }
        public int RejectedCtn { get; set; }
        public string AcceptedKg => (AcceptedCtn * CartonWeightKg).ToString("N1", CultureInfo.InvariantCulture);
        public string RejectedKg => (RejectedCtn * CartonWeightKg).ToString("N1", CultureInfo.InvariantCulture);
    }

    public QCWindow(int checkId)
    {
        InitializeComponent();
        _checkId = checkId;
        Loaded += (_, _) => Load();
    }

    private static string D(double? v) => v.HasValue ? v.Value.ToString("N1") : "—";

    private void Load()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var session = AppContainer.Get<Infrastructure.Session.SessionContext>();

            _check = db.QualityChecks.AsNoTracking().Include(c => c.Items).FirstOrDefault(c => c.Id == _checkId);
            if (_check == null)
            {
                AppContainer.Get<DialogService>().Error("محضر الفحص غير موجود.");
                Close();
                return;
            }
            _order = _check.OrderId != null
                ? db.ProductionOrders.AsNoTracking().Include(o => o.Items).FirstOrDefault(o => o.Id == _check.OrderId.Value)
                : null;

            Title = $"محضر فحص الجودة — {_check.DocumentNumber}";
            HeadTitle.Text = $"محضر: {_check.DocumentNumber} — {_check.CheckType}";
            HeadState.Text = $"الحالة: {QualityCheckStatuses.ToArabic(_check.Status)}" +
                              (_check.IsApproved ? $" | اعتمده: {_check.ApprovedDate?.ToString("dd/MM")} " : "");

            // ── لافتات ──
            var today = DateTime.Today;
            if (_check.ExpectedCheckDate != null && _check.ExpectedCheckDate.Value.Date > today && !_check.IsApproved)
            {
                CoolingBanner.Visibility = Visibility.Visible;
                CoolingText.Text = $"⏳ فترة التبريد: النتيجة النهائية أدق بعد {(_check.ExpectedCheckDate.Value.Date - today).TotalDays:N0} يوم — الفحص المبكر مسموح على مسؤوليتك.";
            }
            else CoolingBanner.Visibility = Visibility.Collapsed;

            if (_check.IsApproved && _check.Decision == "Quarantine")
            {
                QuarantineBanner.Visibility = Visibility.Visible;
                QuarantineText.Text = "🚫 الكمية محجوزة وتحريز مؤقت — إعادة الفحص تتم على المعالج منها عبر قفل يوم إنتاج جديد (فحص جديد).";
            }
            else QuarantineBanner.Visibility = Visibility.Collapsed;

            // ── رأس المعلومات ──
            var customers = db.Customers.AsNoTracking().ToDictionary(c => c.Id, c => c.CustomerName);
            var lots = db.Lots.AsNoTracking().ToDictionary(l => l.Id, l => l.LotCode);
            var shifts = db.Shifts.AsNoTracking().ToDictionary(s => s.Id, s => s.ShiftNameAr);
            var lines = db.ProductionLines.AsNoTracking().ToDictionary(l => l.Id, l => l.LineNameAr);

            FOrder.Text = _order != null
                ? $"{_order.DocumentNumber} — {(_order.ShiftId != null && shifts.TryGetValue(_order.ShiftId.Value, out var sh) ? sh : "—")} / {(_order.LineId != null && lines.TryGetValue(_order.LineId.Value, out var ln) ? ln : "—")}"
                : "فحص يدوي (بلا أمر)";
            var firstLot = _check.Items.FirstOrDefault(i => i.LotId != null)?.LotId;
            FCustomer.Text = (_order?.CustomerId != null && customers.TryGetValue(_order.CustomerId.Value, out var cu) ? cu : "—") +
                             " / " + (firstLot != null && lots.TryGetValue(firstLot.Value, out var lt) ? lt : "—");
            FDate.Text = $"{_check.CheckDate?.ToString("dd/MM/yyyy") ?? "—"} / متوقع: {_check.ExpectedCheckDate?.ToString("dd/MM/yyyy") ?? "—"}" +
                         (_check.InspectorName != null ? $" | الفاحص: {_check.InspectorName}" : "");

            FAccepted.Text = $"{_check.AcceptedKg:N1} كجم ({_check.AcceptedCartons:N0} كرتون)";
            FRejected.Text = $"{_check.RejectedKg:N1} كجم ({_check.RejectedCartons:N0} كرتون)";
            FDecision.Text = DecisionAr(_check.Decision) + (_check.InspectorNotes != null ? $"\n{_check.InspectorNotes}" : "");

            // ── نتائج محفوظة ──
            var products = db.Products.AsNoTracking().ToDictionary(p => p.Id, p => p.ProductNameAr);
            ResultsGrid.ItemsSource = _check.Items.Select(i => new
            {
                ProductName = products.TryGetValue(i.ProductId, out var pn) ? pn : $"#{i.ProductId}",
                LotCode = i.LotId != null && lots.TryGetValue(i.LotId.Value, out var lc) ? lc : "—",
                CheckedKg = i.CheckedQtyKg.ToString("N1"),
                AcceptedKg = i.AcceptedQtyKg.ToString("N1"),
                RejectedKg = i.RejectedQtyKg.ToString("N1"),
                CheckedCtn = i.CheckedCartons.ToString("N0"),
                AcceptedCtn = i.AcceptedCartons.ToString("N0"),
                RejectedCtn = i.RejectedCartons.ToString("N0")
            }).ToList();

            bool hasLab = _check.MoisturePct > 0 || _check.BrixDeg > 0 || _check.SkinSeparationPct > 0 || _check.ImpuritiesPct > 0 || _check.SampleCartons > 0;
            LabPanel.Visibility = hasLab ? Visibility.Visible : Visibility.Collapsed;
            if (hasLab)
                FLab.Text = $"رطوبة {_check.MoisturePct:N1}% | Brix {_check.BrixDeg:N1}° | انفصال {_check.SkinSeparationPct:N1}% | شوائب {_check.ImpuritiesPct:N2}% | عينة {_check.SampleCartons:N0} كرتون";

            // ── تبويب التاريخ ──
            var corr = db.QualityCorrections.AsNoTracking().Where(c => c.CheckId == _check.Id).OrderByDescending(c => c.CorrectedDate).ToList();
            CorrectionsGrid.ItemsSource = corr.Select(c => new
            {
                Date = c.CorrectedDate.ToString("dd/MM/yyyy HH:mm"),
                User = c.CorrectedByName,
                Reason = c.Reason
            }).ToList();
            HistoryGrid.ItemsSource = db.AuditLogs.AsNoTracking()
                .Where(a => a.DocumentNumber == _check.DocumentNumber)
                .OrderByDescending(a => a.ActionDate).Take(25)
                .ToList()   // §سحب: switch expression غير قابل للترجمة في شجرة EF
                .Select(a => new
                {
                    Time = a.ActionDate.ToString("dd/MM/yyyy HH:mm"),
                    User = a.UserName,
                    Action = a.ActionType switch
                    {
                        "Create" => "إنشاء", "Edit" => "تعديل", "Approve" => "اعتماد",
                        "Cancel" => "إلغاء", "Delete" => "حذف", "Post" => "ترحيل", _ => a.ActionType ?? "—"
                    }
                }).ToList();

            // ── تبويب الإدخال: يبني أسطر الفحص من المنتَج المتبقي ──
            bool canEdit = !_check.IsApproved && session.Can("quality", "Create") && _order != null;
            _inputRows = BuildInputRows(db);
            InputGrid.ItemsSource = _inputRows;
            foreach (var f in new[] { TxtMoisture, TxtBrix, TxtSkin, TxtImpurities, TxtSample, TxtNotes })
                f.IsEnabled = canEdit;
            CmbDecision.IsEnabled = canEdit;

            if (canEdit)
            {
                TxtMoisture.Text = _check.MoisturePct > 0 ? _check.MoisturePct.ToString("0.0") : "";
                TxtBrix.Text = _check.BrixDeg > 0 ? _check.BrixDeg.ToString("0.0") : "";
                TxtSkin.Text = _check.SkinSeparationPct > 0 ? _check.SkinSeparationPct.ToString("0.0") : "";
                TxtImpurities.Text = _check.ImpuritiesPct > 0 ? _check.ImpuritiesPct.ToString("0.00") : "";
                TxtSample.Text = _check.SampleCartons > 0 ? _check.SampleCartons.ToString() : "";
                TxtNotes.Text = _check.InspectorNotes ?? "";
            }

            // ── أزرار الدور×الحالة ──
            BtnSave.Visibility = canEdit && _inputRows.Any(r => r.RemainingCtn > 0 || r.AcceptedCtn > 0 || r.RejectedCtn > 0) ? Visibility.Visible : Visibility.Collapsed;
            BtnApprove.Visibility = !_check.IsApproved && session.Can("quality", "Approve") && _check.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            BtnCorrect.Visibility = _check.IsApproved && session.Can("quality", "EditAfterApproval") ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "QC.Load"); }
    }

    /// <summary>أسطر الإدخال: لكل (صنف×دفعة) في المنتَج — المتبقي بعد فحوصات أخرى، مُعبأ تلقائياً.</summary>
    private List<QcInputRow> BuildInputRows(DatesErpDbContext db)
    {
        var rows = new List<QcInputRow>();
        var products = db.Products.AsNoTracking().ToDictionary(p => p.Id, p => p.ProductNameAr);
        var lots = db.Lots.AsNoTracking().ToDictionary(l => l.Id, l => l.LotCode);
        var packs = db.PackagingTypes.AsNoTracking().ToDictionary(p => p.Id, p => p.PackageNameAr);
        foreach (var item in _order.Items.Where(i => i.ProducedQtyKg > 0.001 || i.ProducedCartons > 0))
        {
            double weight = item.CartonWeightKg > 0 ? item.CartonWeightKg
                : UnitsPolicy.CartonWeight(db, item.ProductId, item.PackagingTypeId);
            if (weight <= 0) continue;
            double otherCtn = db.QualityCheckItems.AsNoTracking()
                .Where(i => i.CheckId != _check.Id && i.OrderItemId == item.Id)
                .Join(db.QualityChecks.AsNoTracking(), i => i.CheckId, c => c.Id, (i, c) => new { i, c })
                .Where(x => x.c.OrderId == _order.Id).Sum(x => x.i.CheckedCartons);
            int produced = item.ProducedCartons > 0 ? item.ProducedCartons
                : (int)Math.Round(item.ProducedQtyKg / weight);
            int remaining = Math.Max(0, produced - (int)otherCtn);
            var existing = _check.Items.FirstOrDefault(q => q.OrderItemId == item.Id);
            rows.Add(new QcInputRow
            {
                OrderItemId = item.Id, ProductId = item.ProductId,
                CustomerId = item.CustomerId ?? _order.CustomerId,
                LotId = item.LotId, PackagingTypeId = item.PackagingTypeId,
                ProductName = products.GetValueOrDefault(item.ProductId, $"#{item.ProductId}"),
                LotCode = item.LotId != null ? lots.GetValueOrDefault(item.LotId.Value, "—") : "—",
                PackageName = item.PackagingTypeId != null ? packs.GetValueOrDefault(item.PackagingTypeId.Value, "—") : "—",
                CartonWeightKg = weight, RemainingCtn = remaining,
                // No manufactured 'all accepted' measurement. User records results.
                AcceptedCtn = existing != null ? (int)existing.AcceptedCartons : 0,
                RejectedCtn = existing != null ? (int)existing.RejectedCartons : 0
            });
        }
        return rows;
    }

    private static string DecisionAr(string d) => d switch
    {
        "Quarantine" => "🚫 حجز وتحريز مؤقت",
        "Rejected" => "🗑 مرفوض / عوادم",
        _ => "✅ مطابق — مقبول للإفراج"
    };

    private static double P(string s) => double.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private void Decision_Changed(object sender, SelectionChangedEventArgs e) { }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var items = _inputRows
                .Where(r => r.AcceptedCtn + r.RejectedCtn > 0)
                .Select(r => new QualityItemDto
                {
                    OrderItemId = r.OrderItemId, ProductId = r.ProductId,
                    CustomerId = r.CustomerId, LotId = r.LotId, PackagingTypeId = r.PackagingTypeId,
                    CheckedQtyKg = 0,   // يُشتق = مقبول + مرفوض
                    AcceptedQtyKg = r.AcceptedCtn * r.CartonWeightKg,
                    RejectedQtyKg = r.RejectedCtn * r.CartonWeightKg,
                    CheckedCartons = 0,
                    AcceptedCartons = r.AcceptedCtn,
                    RejectedCartons = r.RejectedCtn
                }).ToList();
            if (items.Count == 0)
            {
                AppContainer.Get<DialogService>().Error("أدخل كمية مفحوصة (مقبول + مرفوض) لسطر واحد على الأقل.");
                return;
            }
            // §1.50.72 P4-4: سقف محلي واضح — كان إدخال أكبر من المتبقي يُرفض من الخدمة برسالة
            // تغطية أقل بديهية.
            var over = _inputRows.Where(r => r.AcceptedCtn + r.RejectedCtn > r.RemainingCtn).ToList();
            if (over.Count > 0)
            {
                string detail = string.Join("؛ ", over.Select(r => $"«{r.ProductName}»: {r.AcceptedCtn + r.RejectedCtn} > المتبقي {r.RemainingCtn}"));
                AppContainer.Get<DialogService>().Error($"الكمية المفحوصة تتجاوز المتبقي للسطر — {detail}\nلا يمكن فحص أكثر من ما لم يُفحص بعد.");
                return;
            }
            var lab = new QualityLabDto
            {
                Decision = (CmbDecision.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Passed",
                MoisturePct = P(TxtMoisture.Text),
                BrixDeg = P(TxtBrix.Text),
                SkinSeparationPct = P(TxtSkin.Text),
                ImpuritiesPct = P(TxtImpurities.Text),
                SampleCartons = (int)P(TxtSample.Text),
                InspectorNotes = string.IsNullOrWhiteSpace(TxtNotes.Text) ? null : TxtNotes.Text.Trim()
            };
            if (lab.Decision == "Passed" && items.Sum(i => i.AcceptedQtyKg) <= 0.001)
            {
                AppContainer.Get<DialogService>().Error("لا يمكن تسجيل 100% مرفوض بقرار «مطابق»؛ اختر رفضاً/حجزاً أو صحّح النتائج.");
                return;
            }
            // المقبول الجزئي جائز بسقف الكمية المقبولة وحدها، والمرفوض محجوز.
            double totalRej = items.Sum(i => i.RejectedQtyKg);
            string decWarn = lab.Decision == "Passed" && totalRej > 0.001
                ? $"⚠ المحضر يحتوي مرفوضات ({totalRej:N1} كجم) وقراره «مطابق» — تأكد أن هذا مقصود.\n"
                : "";
            var confirm = AppContainer.Get<DialogService>().Confirm(
                $"سيُحفظ الفحص {(_check.Items.Count > 0 ? "بالتعديل" : "جديداً")} بقرار: {DecisionAr(lab.Decision)}\n" +
                $"مقبول: {items.Sum(i => i.AcceptedQtyKg):N1} كجم | مرفوض: {items.Sum(i => i.RejectedQtyKg):N1} كجم\n{decWarn}المتابعة؟");
            if (!confirm) return;

            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IQualityService>();
            var r = svc.SaveCheck(_order.Id, _check.ExecutionId, DateTime.Today.ToString("yyyy-MM-dd"),
                string.IsNullOrWhiteSpace(_check.CheckType) ? "نهائي" : _check.CheckType, items, null, lab);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            if (r.Id != _checkId) _checkId = r.Id; // أُنشئ فحص جديد (يدوي بلا جلسة) — نتابع عليه
            Load();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "QC.Save"); }
    }

    private void Approve_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var confirm = AppContainer.Get<DialogService>().Confirm(
                "اعتماد المحضر يفرج عن المقبول للتسليم ويغلق المحضر (لا تعديل بعده إلا بتصحيح معتمد). المتابعة؟");
            if (!confirm) return;
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IQualityService>();
            var r = svc.ApproveCheck(_checkId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            Load();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "QC.Approve"); }
    }

    private void Correct_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new InputDialog("تصحيح معتمد", "سبب التصحيح (إجباري — يُسجَّل في التدقيق):") { Owner = this };
            if (dlg.ShowDialog() != true) return;
            if (string.IsNullOrWhiteSpace(dlg.Value))
            {
                AppContainer.Get<DialogService>().Error("التصحيح المعتمد يتطلب سبباً مكتوباً.");
                return;
            }
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IQualityService>();
            var r = svc.RequestCorrection(_checkId, dlg.Value.Trim());
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            Load();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "QC.Correct"); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
