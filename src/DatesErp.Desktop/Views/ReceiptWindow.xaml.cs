using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views;

/// <summary>
/// §B99 — استلام الإنتاج التام: من بطاقة «أمر تسليم بانتظار الاستلام» (أمين المخزن)
/// أو «استلام جزئي — المتبقي».
/// إنشاء السند من أمر التسليم (بلا إعادة إدخال) ← الإصدار ← تنفيذ الاستلام بالفعلي (كلي/جزئي لكل بند).
/// </summary>
public partial class ReceiptWindow : Window
{
    private readonly int? _deliveryId;
    private readonly int? _receiptId;

    private ProductionDelivery _delivery;
    private FinishedGoodsReceipt _receipt;
    private List<FinishedGoodsReceipt> _receipts = new();

    /// <summary>سطر استلام: الفعلي يُدخال بالكيلو (معبأ بالمتبقي).</summary>
    public class RcptRow
    {
        public int ItemId { get; set; }
        public string CustomerName { get; set; }
        public string ProductName { get; set; }
        public string PackName { get; set; }
        public string LotCode { get; set; }
        public double NetKg { get; set; }
        public double ReceivedKg { get; set; }
        public double RemainingKg { get; set; }
        public double ActualKg { get; set; }
    }

    /// <summary>الفتح من بطاقة «أمر تسليم بانتظار الاستلام».</summary>
    public static ReceiptWindow FromDelivery(int deliveryId) => new(deliveryId, true);

    /// <summary>الفتح من بطاقة «استلام جزئي» (السند مباشرة).</summary>
    public static ReceiptWindow FromReceipt(int receiptId) => new(receiptId, false);

    private ReceiptWindow(int id, bool isDelivery)
    {
        InitializeComponent();
        _deliveryId = isDelivery ? id : null;
        _receiptId = isDelivery ? null : id;
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var session = AppContainer.Get<Infrastructure.Session.SessionContext>();

            var customers = db.Customers.AsNoTracking().ToDictionary(c => c.Id, c => c.CustomerName);
            var products = db.Products.AsNoTracking().ToDictionary(p => p.Id, p => p.ProductNameAr);
            var packs = db.PackagingTypes.AsNoTracking().ToDictionary(p => p.Id, p => p.PackageNameAr);
            var lots = db.Lots.AsNoTracking().ToDictionary(l => l.Id, l => l.LotCode);
            string Cust(int? id) => id != null && customers.TryGetValue(id.Value, out var c) ? c : "—";
            string Prod(int id) => products.TryGetValue(id, out var p) ? p : $"#{id}";
            string Pack(int? id) => id != null && packs.TryGetValue(id.Value, out var pk) ? pk : "—";
            string Lot(int? id) => id != null && lots.TryGetValue(id.Value, out var lt) ? lt : "—";

            bool canCreate = session.Can("finishedgoods", "Create");
            bool canReceive = session.Can("finishedgoods", "Approve");

            // ── أمر التسليم (إن كان الفتح منه) ──
            if (_deliveryId != null)
            {
                _delivery = db.ProductionDeliveries.AsNoTracking().Include(d => d.Items).FirstOrDefault(d => d.Id == _deliveryId.Value);
                if (_delivery == null)
                {
                    AppContainer.Get<DialogService>().Error("أمر التسليم غير موجود.");
                    Close();
                    return;
                }
                _receipts = db.FinishedGoodsReceipts.AsNoTracking()
                    .Where(r => r.DeliveryId == _delivery.Id && r.Status != DocStatuses.Cancelled)
                    .OrderBy(r => r.Id).ToList();
                Title = $"استلام التام — أمر التسليم {_delivery.DocumentNumber}";
                HeadTitle.Text = $"أمر التسليم: {_delivery.DocumentNumber} — {DeliverySources.ToArabic(_delivery.SourceType)}";
                HeadState.Text = $"الحالة: {DocStatuses.ToArabic(_delivery.Status)} | الاستلام: {_delivery.ReceiptStatus}";

                if (_delivery.BypassReason != null)
                {
                    BypassBanner.Visibility = Visibility.Visible;
                    BypassText.Text = $"⚠️ تجاوز فحص موثق (المصدر: {DeliverySources.ToArabic(_delivery.SourceType)}): {_delivery.BypassReason}";
                }
                else BypassBanner.Visibility = Visibility.Collapsed;

                DeliveryGrid.ItemsSource = _delivery.Items.Select(i => new
                {
                    CustomerName = Cust(i.CustomerId),
                    ProductName = Prod(i.ProductId),
                    PackName = Pack(i.PackagingTypeId),
                    LotCode = Lot(i.LotId),
                    QtyKg = i.QtyKg.ToString("N1"),
                    PackageCount = i.PackageCount.ToString("N0"),
                    ReceivedKg = i.ReceivedQtyKg.ToString("N1"),
                    RemainingKg = Math.Max(0, i.QtyKg - i.ReceivedQtyKg).ToString("N1")
                }).ToList();
            }
            else
            {
                TabDelivery.Visibility = Visibility.Collapsed;
                Tabs.SelectedItem = TabReceive;
            }

            // ── السندات المرتبطة ──
            if (_receiptId != null && (_deliveryId == null || !_receipts.Any(r => r.Id == _receiptId)))
                _receipts = db.FinishedGoodsReceipts.AsNoTracking()
                    .Where(r => r.Id == _receiptId.Value && r.Status != DocStatuses.Cancelled)
                    .ToList();
            CmbReceipt.ItemsSource = _receipts.Select(r => new { r.Id, Label = $"{r.DocumentNumber} — {r.ReceiptStatus}" }).ToList();
            var initial = _receiptId != null ? _receiptId.Value : (_receipts.FirstOrDefault(r => r.ReceiptStatus != "Full")?.Id ?? _receipts.LastOrDefault()?.Id);
            CmbReceipt.SelectedValuePath = "Id";
            if (initial != null) CmbReceipt.SelectedValue = initial;

            LoadReceiptGrid(db, customers, products, packs, lots, canReceive);

            // ── الأزرار الدور×الحالة ──
            BtnCreate.Visibility = (_delivery != null && _delivery.Status == DocStatuses.Issued && _receipts.Count == 0 && canCreate)
                ? Visibility.Visible : Visibility.Collapsed;
            BtnIssue.Visibility = (_receipt != null && _receipt.Status == DocStatuses.Draft && canCreate)
                ? Visibility.Visible : Visibility.Collapsed;
            BtnReceive.Visibility = (_receipt != null && (_receipt.Status == DocStatuses.Issued || _receipt.Status == DocStatuses.Completed)
                && _receipt.ReceiptStatus != "Full" && canReceive)
                ? Visibility.Visible : Visibility.Collapsed;

            if (_delivery != null && _delivery.Status == DocStatuses.Draft)
                AppContainer.Get<DialogService>().Info("أمر التسليم لم يُحرَّر بعد — بانتظار مدير الإنتاج. سيفتح الاستلام هنا بعد التحرير.");
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receipt.Load"); }
    }

    private void LoadReceiptGrid(DatesErpDbContext db,
        Dictionary<int, string> customers, Dictionary<int, string> products,
        Dictionary<int, string> packs, Dictionary<int, string> lots, bool prefill)
    {
        if (CmbReceipt.SelectedValue is int rcptId)
        {
            _receipt = db.FinishedGoodsReceipts.AsNoTracking().Include(r => r.Items).FirstOrDefault(r => r.Id == rcptId);
            if (_receipt != null)
            {
                ReceiveGrid.ItemsSource = _receipt.Items.Select(i => new RcptRow
                {
                    ItemId = i.Id,
                    CustomerName = i.CustomerId != null && customers.TryGetValue(i.CustomerId.Value, out var cu) ? cu : "—",
                    ProductName = products.TryGetValue(i.ProductId, out var pn) ? pn : $"#{i.ProductId}",
                    PackName = i.PackagingTypeId != null && packs.TryGetValue(i.PackagingTypeId.Value, out var pk) ? pk : "—",
                    LotCode = i.LotId != null && lots.TryGetValue(i.LotId.Value, out var lt) ? lt : "—",
                    NetKg = i.NetWeightKg,
                    ReceivedKg = i.ReceivedQtyKg,
                    RemainingKg = Math.Max(0, i.NetWeightKg - i.ReceivedQtyKg),
                    // §B99 — التعبئة: المتبقي كاملاً (يعدّل أمين المخزن للجزئي)
                    ActualKg = prefill ? Math.Max(0, i.NetWeightKg - i.ReceivedQtyKg) : 0
                }).ToList();
                if (_delivery == null)
                {
                    HeadTitle.Text = $"سند الاستلام: {_receipt.DocumentNumber} — أمر {_receipt.OrderId}";
                    HeadState.Text = $"الحالة: {DocStatuses.ToArabic(_receipt.Status)} | الاستلام: {_receipt.ReceiptStatus}";
                    Title = $"استلام التام — {_receipt.DocumentNumber}";
                }
            }
            else ReceiveGrid.ItemsSource = null;
        }
        else
        {
            _receipt = null;
            ReceiveGrid.ItemsSource = null;
        }
    }

    private void Receipt_Changed(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var customers = db.Customers.AsNoTracking().ToDictionary(c => c.Id, c => c.CustomerName);
            var products = db.Products.AsNoTracking().ToDictionary(p => p.Id, p => p.ProductNameAr);
            var packs = db.PackagingTypes.AsNoTracking().ToDictionary(p => p.Id, p => p.PackageNameAr);
            var lots = db.Lots.AsNoTracking().ToDictionary(l => l.Id, l => l.LotCode);
            LoadReceiptGrid(db, customers, products, packs, lots, true);

            var session = AppContainer.Get<Infrastructure.Session.SessionContext>();
            bool canCreate = session.Can("finishedgoods", "Create");
            bool canReceive = session.Can("finishedgoods", "Approve");
            BtnIssue.Visibility = (_receipt != null && _receipt.Status == DocStatuses.Draft && canCreate) ? Visibility.Visible : Visibility.Collapsed;
            BtnReceive.Visibility = (_receipt != null && (_receipt.Status == DocStatuses.Issued || _receipt.Status == DocStatuses.Completed)
                && _receipt.ReceiptStatus != "Full" && canReceive) ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receipt.Change"); }
    }

    /// <summary>إنشاء السند من أوامر أمر التسليم (بلا إعادة إدخال) ثم إصداره — أمر واحد لكل أمر إنتاج.</summary>
    private void Create_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var confirm = AppContainer.Get<DialogService>().Confirm(
                "ستُنشأ سندات الاستلام من بنود أمر التسليم كما هي (بلا إعادة إدخال) وتُصدر فوراً. المتابعة؟");
            if (!confirm) return;

            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFinishedGoodsService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var delivery = db.ProductionDeliveries.Include(d => d.Items).First(d => d.Id == _deliveryId);

            var skippedNoOrder = delivery.Items.Count(i => i.OrderId == null);
            var created = 0;
            foreach (var g in delivery.Items.Where(i => i.OrderId != null).GroupBy(i => i.OrderId.Value))
            {
                var items = g.Select(i => new FinishedGoodsItemDto
                {
                    ProductId = i.ProductId,
                    LotId = i.LotId,
                    PackagingTypeId = i.PackagingTypeId,
                    PackageCount = i.PackageCount,
                    NetWeightKg = i.QtyKg,
                    CustomerId = i.CustomerId,
                    DeliveryItemId = i.Id
                }).ToList();
                var r = svc.SaveReceipt(g.Key, null, DateTime.Today.ToString("yyyy-MM-dd"), items, delivery.Id);
                if (!r.Ok) throw new InvalidOperationException(r.Message);
                var iss = svc.Issue(r.Id);
                if (!iss.Ok) throw new InvalidOperationException(iss.Message);
                created++;
            }
            var msg = $"تم إنشاء {created} سند/سندات وإصدارها — يمكنك الآن تنفيذ الاستلام بالفعلي." +
                      (skippedNoOrder > 0 ? $"\n⚠️ {skippedNoOrder} بند بلا أمر إنتاج (مصدر خطة) — يُستلم من المسار المباشر." : "");
            AppContainer.Get<DialogService>().Info(msg);
            Load();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receipt.Create"); }
    }

    private void Issue_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_receipt == null) return;
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFinishedGoodsService>();
            var r = svc.Issue(_receipt.Id);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            Load();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receipt.Issue"); }
    }

    private void Receive_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_receipt == null) return;
            var rows = (ReceiveGrid.ItemsSource as System.Collections.IEnumerable)?.Cast<RcptRow>().ToList() ?? new List<RcptRow>();
            var dict = rows.Where(r => r.ActualKg > 0.001 && r.ActualKg <= r.RemainingKg + 0.001)
                .GroupBy(r => r.ItemId)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.ActualKg));
            if (dict.Count == 0)
            {
                AppContainer.Get<DialogService>().Error("أدخل الكمية الفعلية المستلمة لسطر واحد على الأقل (أو صحّح تجاوزاً للكمية المتبقية).");
                return;
            }
            var confirm = AppContainer.Get<DialogService>().Confirm(
                $"سيُستلم {dict.Values.Sum():N1} كجم فعلياً ويُقيد في مخزن الإنتاج التام (سند متابعة). المتابعة؟");
            if (!confirm) return;

            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IFinishedGoodsService>();
            var r = svc.Receive(_receipt.Id, dict);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            Load();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receipt.Receive"); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
