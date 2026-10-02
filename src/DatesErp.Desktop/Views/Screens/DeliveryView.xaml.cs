using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

public class DelivBalanceRow : System.ComponentModel.INotifyPropertyChanged
{
    private int _productId;
    private int? _lotId;
    private int? _packagingTypeId;
    private string _productName = "—";
    private string _lotCode = "— اختر الدفعة —";
    private double _qty;
    private int _packages;
    private double _unitWeight;
    private string _packName = "—";
    private string _unit = "—";

    public int ProductId { get => _productId; set { _productId = value; OnChanged(nameof(ProductId)); OnChanged(nameof(IsEmptyRow)); } }
    public int? LotId { get => _lotId; set { _lotId = value; OnChanged(nameof(LotId)); OnChanged(nameof(IsEmptyRow)); } }
    public int? PackagingTypeId { get => _packagingTypeId; set { _packagingTypeId = value; OnChanged(nameof(PackagingTypeId)); } }
    public string ProductName { get => _productName; set { _productName = value ?? "—"; OnChanged(nameof(ProductName)); } }
    public string LotCode { get => _lotCode; set { _lotCode = value ?? "—"; OnChanged(nameof(LotCode)); } }
    public double Qty { get => _qty; set { _qty = value; OnChanged(nameof(Qty)); } }
    public int Packages { get => _packages; set { _packages = value; _qty = value * (_unitWeight > 0 ? _unitWeight : (CartonWeight > 0 ? CartonWeight : 0)); OnChanged(nameof(Packages)); OnChanged(nameof(Qty)); } }
    public double UnitWeight { get => _unitWeight; set { _unitWeight = value; OnChanged(nameof(UnitWeight)); } }
    public string PackName { get => _packName; set { _packName = value ?? "—"; OnChanged(nameof(PackName)); } }
    public string Unit { get => _unit; set { _unit = value ?? "—"; OnChanged(nameof(Unit)); } }
    public string QcStatus { get; set; }
    public string Code { get; set; } = "—";
    public string Grade { get; set; } = "—";
    public double CartonWeight { get; set; }
    public string FgReceipt { get; set; } = "—";
    public string Production { get; set; } = "—";
    public int StorageDays { get; set; }
    public string PlanNo { get; set; } = "—";
    public string OrderNo { get; set; } = "—";
    public bool QcReady { get; set; }

    // §1.50.56 — حقول إضافية للإدخال المباشر
    public double AvailableQty { get; set; }
    public int AvailablePackages { get; set; }
    public bool IsEmptyRow => ProductId == 0 && LotId == null;
    public bool IsInvalid => !IsEmptyRow && (Packages <= 0 || (AvailablePackages > 0 && Packages > AvailablePackages));
    public string ValidationError => IsInvalid ? (Packages <= 0 ? "أدخل كراتين > 0" : $"التجاوز: {Packages} > متاح {AvailablePackages}") : null;

    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
}

/// <summary>تسليم العملاء — من رصيد العميل في مخزن التام فقط (لا تسليم دفعة عميل لعميل آخر، لا تسليم فوق الرصيد).</summary>
public partial class DeliveryView : UserControl
{
    private List<object> _deliveries_all = new();
    private readonly ObservableCollection<DelivBalanceRow> _balances = new();
    private readonly ObservableCollection<DelivBalanceRow> _items = new();
    private List<int> _deliveryIds = new();
    private int _currentId, _currentCustomerId;
    private bool _locked;
    private Views.ErpToolbar _toolbar;

    public DeliveryView()
    {
        InitializeComponent();
        BalanceGrid.ItemsSource = _balances;
        ItemsGrid.ItemsSource = _items;
        // §قاعدة الكرتون: تعديل الكراتين يشتق الوزن المكافئ فوراً
        ItemsGrid.CellEditEnding += (_, e) =>
        {
            if (e.Row?.Item is DelivBalanceRow row && e.Column?.Header?.ToString() == "الكراتين *")
            {
                if (row.Packages < 0) row.Packages = 0;   // §B105/P1 — لا سالب في الشبكة
                double w2 = row.UnitWeight > 0 ? row.UnitWeight : (row.CartonWeight > 0 ? row.CartonWeight : 0);
                row.Qty = w2 > 0 ? Math.Round(row.Packages * w2, 1) : 0;
                ItemsGrid.Items.Refresh();
            }
        };
        // §1.50.72 P4-1: الحفظ التلقائي (DeliveryDraft_*.json) أُزيل — النمط نفسه الذي أزيل من
        // شاشة الخطط (مسودات تظهر بلا مصدر بعد انقطاع). السند المحفوظ وحده هو المرجع.
        ItemsGrid.PreviewKeyDown += ItemsGrid_PreviewKeyDown;
        Loaded += (_, _) => { Services.ComboBoxAutoShowHelper.Apply(this); Load(); };
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("الشحنات وتسليم العميل");
        chrome.SetScreenCode("MRPINV1005");
        // §1 — الترتيب القياسي الموحد للأزرار الأساسية
        _toolbar = new Views.ErpToolbar()
            .WithNew((_, _) => NewForm(), "سند تسليم جديد (F2)")
            .WithSave((_, _) => Save(), "حفظ سند التسليم — يبقى السند أمامك كما هو (F10)")
            .WithSearch((_, _) => { RefreshList(); DelivSearchBox.Focus(); }, "بحث في سندات التسليم المحفوظة (F9)")
            .WithUndo((_, _) => UndoSmart(), "تراجع: يلغي الإدخالات غير المحفوظة ويعيد آخر نسخة محفوظة — لا يحذف أي سند")
            .WithApprove((_, _) => Approve(), "🔒 اعتماد التسليم وخصم الرصيد")
            .WithUnapprove((_, _) => Unapprove(), "إلغاء التسليم وإعادة الكميات للرصيد")
            .WithPrint((_, _) => Print(), "طباعة الإذن (Ctrl+P)")
            .WithExcel((_, _) => Export())
            .WithNavigation((_, _) => Nav(0), (_, _) => Nav(-1), (_, _) => Nav(1), (_, _) => Nav(int.MaxValue))
            .WithList((_, _) => RefreshList(), "عرض كل سندات التسليم")
            // §إصلاح: MarkInvoiced كانت خدمة كاملة ومحمية من الفوترة المكررة لكنها غير قابلة
            // للوصول من أي شاشة، فبقي InvoicedQtyKg صفراً دائماً وعمودا «المفوتر/غير المفوتر»
            // في التقارير يعرضان صفراً والإجمالي.
            .WithCustom("💰 تسجيل فوترة", "ErpButton", (_, _) => MarkInvoiced(),
                "تسجيل الكمية المفوترة من السند المعتمد — يمنع تكرار الفوترة لنفس الكمية")
            .WithCustom("⎘ تكرار الصف", "ErpButton", (_, _) => DuplicateRow_Delivery(null,null), "تكرار الصف (7-ج)")
            .WithCustom("🗑 حذف المسودة", "ErpDangerButton", (_, _) => DeleteDraft(),
                "§B105: حذف سند مسودة غير معتمد (لم يخصم شيئاً) — المعتمد لا يُحذف")
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard"));
        chrome.SetToolbar(_toolbar);
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard");
    }

    private void Load()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            CustomerBox.ItemsSource = db.Customers.Where(c => c.IsActive).ToList();
            // §2 — الشاشة تفتح فارغة في وضع «مستند جديد» — نتائج البحث تظهر عند الضغط على «بحث»
            NewForm();

            // §التنقل من التقارير: فتح سند تسليم محدد فور تحميل الشاشة
            if (MainWindow.PendingDeliveryIdToOpen is int pendingDlv)
            {
                MainWindow.PendingDeliveryIdToOpen = null;
                OpenDelivery(pendingDlv);
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.Load"); }
    }

    /// <summary>§7/§8 — نتائج البحث في جدول واضح: نقرتان متتاليتان تفتحان السند في هذه الواجهة.</summary>
    private void RefreshList()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var list = db.CustomerDeliveries.OrderByDescending(d => d.Id).ToList();
            _deliveryIds = list.Select(d => d.Id).ToList();
            _deliveries_all = list.Select(d => new
            {
                Id = d.Id,
                DocNo = d.DocumentNumber,
                Customer = db.Customers.Where(c => c.Id == d.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "—",
                Date = Core.Common.UiFormat.D(d.DeliveryDate),
                Packages = d.TotalCartons,
                Qty = d.TotalQtyKg,
                StatusAr = d.IsApproved ? "معتمد ✅" : "مسودة 🟡"
            }).ToList().Cast<object>().ToList();
            ScreenSearch.Apply(DelivSearchBox, DeliveriesGrid, _deliveries_all);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.List"); }
    }

    /// <summary>§13 — التراجع: سند جديد ← إفراغ؛ سند محفوظ ← إعادة آخر نسخة محفوظة دون حذف.</summary>
    private void UndoSmart()
    {
        if (_currentId > 0) OpenDelivery(_currentId);
        else NewForm();
    }

    private void Customer_Changed(object sender, SelectionChangedEventArgs e)
    {
        var cust = CustomerBox.SelectedItem as Core.Domain.Entities.Customer;
        _balances.Clear();
        if (cust == null) { BalanceChip.Text = "رصيد العميل: —"; return; }
        _currentCustomerId = cust.Id;
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            // §أمر شاشة التسليم — القائمة تُبنى من خدمة الالتقاط: منتجات تامة فقط (§12)،
            // مرتبة FIFO (الأقدم استلاماً ثم الأقدم إنتاجاً — §2)، بحقول تتبع كاملة (§1/§6/§11)،
            // ووزن الكرتون من السياسة المركزية (§القاعدة الذهبية — بلا بديل ثابت 7.5).
            var pick = scope.ServiceProvider.GetRequiredService<DatesErp.Application.Services.DeliveryPickService>();
            var rows = pick.GetPickList(cust.Id, DateTime.Now);
            foreach (var r in rows)
            {
                // §B105/P4 — حالة فحص كل سطر رصيد (للعرض والمنع المبكر — القرار النهائي عند الاعتماد)
                var (ready, qcLabel) = DatesErp.Application.Services.QualityGate.DeliveryReadiness(db, r.LotId, r.ProductId, r.PackagingTypeId, cust.Id);
                _balances.Add(new DelivBalanceRow
                {
                    ProductId = r.ProductId,
                    LotId = r.LotId,
                    PackagingTypeId = r.PackagingTypeId,
                    ProductName = r.ProductName,
                    LotCode = r.LotCode,
                    Qty = r.AvailableKg,
                    Packages = r.AvailableCartons,
                    UnitWeight = r.CartonWeightKg,
                    PackName = r.PackName,
                    Unit = UnitsPolicy.FinishedOfficialUnit,
                    QcStatus = qcLabel,
                    QcReady = ready,
                    Code = r.ProductCode,
                    Grade = r.GradeAr,
                    CartonWeight = r.CartonWeightKg,
                    FgReceipt = r.FgReceiptDate,
                    Production = r.ProductionDate,
                    StorageDays = r.StorageDays,
                    PlanNo = r.PlanNo,
                    OrderNo = r.OrderNo
                });
            }
            BalanceChip.Text = $"رصيد العميل: {rows.Sum(r => r.AvailableCartons)} كرتون / {rows.Sum(r => r.AvailableKg):N1} كجم";
            // §1.50.72 P4-6: رصيد «كجم فقط» بلا كراتين (بلا وزن كرتون معرّف) لا يظهر في التسليم
            // بالكرتون — كان يخفي كمية حقيقية بلا أثر. يُبلَّغ عنها في الشريحة نفسها (لا حوار عند كل تبديل).
            double kgOnly = rows.Where(r => r.AvailableCartons <= 0 && r.AvailableKg > 0.001).Sum(r => r.AvailableKg);
            if (kgOnly > 0.001)
                BalanceChip.Text += $"   ⚠ كجم فقط (بلا وزن كرتون): {kgOnly:N1} كجم — عرّف وزن الكرتون ليصبح قابلًا للتسليم";
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.Balance"); }
    }

    private void Balance_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_locked) { AppContainer.Get<DialogService>().Error("السند مقفل (معتمد)."); return; }
        if (BalanceGrid.SelectedItem is DelivBalanceRow row)
        {
            // §B105/P4 — المرفوض/المحجوز لا يُضاف إطلاقاً؛ وبانتظار الفحص يُضاف بتحذير (الاعتماد سيرفضه)
            if (row.QcStatus != null && row.QcStatus.StartsWith("⛔"))
            { AppContainer.Get<DialogService>().Error($"لا يمكن تسليم هذه البضاعة: {row.QcStatus} — معالجة قرار الجودة أولاً."); return; }
            if (!row.QcReady)
                AppContainer.Get<DialogService>().Info("تنبيه: فحص هذه البضاعة غير معتمد بعد — الاعتماد سيرفض التسليم حتى يُعتمد الفحص.");
            _items.Add(new DelivBalanceRow
            {
                ProductId = row.ProductId,
                LotId = row.LotId,
                PackagingTypeId = row.PackagingTypeId,
                ProductName = row.ProductName,
                LotCode = row.LotCode,
                Qty = row.Qty,
                Packages = row.Packages,
                UnitWeight = row.UnitWeight,
                PackName = row.PackName,
                Unit = row.Unit,
                QcStatus = row.QcStatus,
                QcReady = row.QcReady
            });
        }
    }

    private void DeliverAll_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) { AppContainer.Get<DialogService>().Error("السند مقفل (معتمد)."); return; }
        _items.Clear();
        // §B105/P4 — «تسليم كامل المتاح» يشمل المعتمد جاهزاً فقط
        foreach (var b in _balances.Where(b => b.Qty > 0.001 && b.QcReady))
        {
            _items.Add(new DelivBalanceRow
            {
                ProductId = b.ProductId, LotId = b.LotId, PackagingTypeId = b.PackagingTypeId, ProductName = b.ProductName,
                LotCode = b.LotCode, Qty = b.Qty, Packages = b.Packages, UnitWeight = b.UnitWeight,
                PackName = b.PackName, Unit = b.Unit, QcStatus = b.QcStatus, QcReady = b.QcReady
            });
        }
    }

    /// <summary>§5/§7 — «متابعة التسليم»: توزيع الطلبات آلياً FIFO عبر الدفعات (بلا دمج أو فقدان تتبع)،
    /// ثم شاشة مراجعة (الصنف|الصفة|المتاح|المسلَّم) قبل الحفظ. الكمية الزائدة تُرفض برسالة الأمر حرفياً (§4).</summary>
    private void ContinueDelivery_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) { AppContainer.Get<DialogService>().Error("السند مقفل (معتمد)."); return; }
        if (_currentCustomerId == 0) { AppContainer.Get<DialogService>().Error("اختر العميل أولاً."); return; }
        if (_items.Count == 0) { AppContainer.Get<DialogService>().Error("حدّد الكميات المطلوبة في بنود التسليم أولاً (نقر مزدوج على الرصيد ثم عدّل الكراتين)."); return; }
        try
        {
            // تجميع طلبات المستخدم حسب الصنف: المستخدم يختار أي كمية من أي صنف (§3)
            var requests = _items.GroupBy(i => i.ProductId)
                .Select(g => (ProductId: g.Key, Cartons: g.Sum(x => x.Packages)))
                .ToList();
            using var scope = AppContainer.NewScope();
            var pick = scope.ServiceProvider.GetRequiredService<DatesErp.Application.Services.DeliveryPickService>();
            var allocated = pick.AllocateFifo(_currentCustomerId, requests, DateTime.Now);
            if (allocated.Count == 0) { AppContainer.Get<DialogService>().Error("لا توجد كميات للتسليم."); return; }

            // §7 — شاشة المراجعة: الصنف | الصفة | المتاح | المسلَّم
            var byProduct = _balances.GroupBy(b => b.ProductId).ToDictionary(g => g.Key,
                g => (Grade: g.First().Grade, Avail: g.Sum(x => x.Packages)));
            var review = allocated.GroupBy(a => a.ProductId).Select(g => new
            {
                Name = _balances.FirstOrDefault(b => b.ProductId == g.Key)?.ProductName ?? $"#{g.Key}",
                Grade = byProduct.TryGetValue(g.Key, out var m) ? m.Grade : "—",
                Avail = byProduct.TryGetValue(g.Key, out var m2) ? m2.Avail : 0,
                Delivered = g.Sum(x => x.PackageCount),
                Lots = string.Join(" + ", g.Select(x => $"{x.PackageCount} من {x.LotId}"))
            }).ToList();
            var win = new Views.DeliveryReviewWindow(review.Select(r => (r.Name, r.Grade, r.Avail, r.Delivered, r.Lots)).ToList()) { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() != true) return;

            // استبدال البنود بالسطور الموزَّعة (كل سطر بدفعته ووزنها المحسوب) ثم الحفظ
            var lots = _balances.ToDictionary(b => (b.ProductId, b.LotId ?? 0), b => b);
            _items.Clear();
            foreach (var a in allocated)
            {
                var src = lots.TryGetValue((a.ProductId, a.LotId ?? 0), out var b) ? b : null;
                _items.Add(new DelivBalanceRow
                {
                    ProductId = a.ProductId, LotId = a.LotId, PackagingTypeId = a.PackagingTypeId,
                    ProductName = src?.ProductName ?? $"#{a.ProductId}", LotCode = src?.LotCode ?? "—",
                    Qty = a.QtyKg, Packages = a.PackageCount, UnitWeight = src?.UnitWeight ?? 0,
                    PackName = src?.PackName ?? "—", Unit = UnitsPolicy.FinishedOfficialUnit,
                    QcStatus = src?.QcStatus, QcReady = src?.QcReady ?? false,
                    Code = src?.Code ?? "—", Grade = src?.Grade ?? "—", CartonWeight = src?.CartonWeight ?? 0,
                    FgReceipt = src?.FgReceipt ?? "—", Production = src?.Production ?? "—",
                    StorageDays = src?.StorageDays ?? 0, PlanNo = src?.PlanNo ?? "—", OrderNo = src?.OrderNo ?? "—"
                });
            }
            Save();
        }
        catch (Core.Exceptions.DomainException dex) { AppContainer.Get<DialogService>().Error(dex.Message); }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.ContinueFifo"); }
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsGrid.SelectedItem is DelivBalanceRow row) { _items.Remove(row); EnsureEmptyRow(); }
    }

    private bool CommitItems()
    {
        try
        {
            ItemsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            ItemsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            return true;
        }
        catch { return true; }
    }

    private void Save()
    {
        try
        {
            if (_locked) { AppContainer.Get<DialogService>().Error("السند مقفل (معتمد)."); return; }
            if (_currentCustomerId == 0) { AppContainer.Get<DialogService>().Error("اختر العميل."); return; }
            // §1.50.67 FIX متوسط: تجاهل الصفوف الفارغة placeholder عند الحفظ — مثل إصلاح الخطط
            // §1.50.72 P3-3: الصف غير الفارغ بلا كراتين صالحة لم يعد يُستبعد بصمت — يُبلَّغ عنه.
            var broken = _items.Where(r => !r.IsEmptyRow && r.ProductId != 0 && r.Packages <= 0).ToList();
            if (broken.Count > 0)
            {
                string detail = string.Join("؛ ", broken.Select(r => $"{r.ProductName} / {r.LotCode}"));
                AppContainer.Get<DialogService>().Error($"لا يمكن حفظ السند — البنود التالية بلا كراتين صالحة: {detail}\nعدّل الكراتين (أكبر من 0) أو احذف الصف.");
                return;
            }
            var validItems = _items.Where(r => !r.IsEmptyRow && r.ProductId != 0 && r.Packages > 0).ToList();
            if (validItems.Count == 0) { AppContainer.Get<DialogService>().Error("أضف بنداً من رصيد العميل (نقر مزدوج أو زر تسليم الكامل)."); return; }

            using var scope = AppContainer.NewScope();
            var svc = (ICustomerDeliveryService)scope.ServiceProvider.GetService(typeof(ICustomerDeliveryService));
            // لا نخمن أمر الرأس من أول دفعة: قد تشترك عدة خطط/أوامر في الدفعة نفسها.
            // الخدمة تتحقق من الجودة لكل بند عند الاعتماد (بلا OrderId)، وتملأ
            // رأس السند بأمر واحد فقط إذا ثبتت وحدانيته لكل البنود.
            int? orderId = null;
            var itemsDto = validItems.Select(i => new CustomerDeliveryItemDto
            {
                ProductId = i.ProductId, LotId = i.LotId, PackagingTypeId = i.PackagingTypeId,
                QtyKg = i.Qty, PackageCount = i.Packages
            }).ToList();
            // §B105/P2 — سند مسودة محفوظ ومفتوح ← تحديث نفس السند (لا إنشاء مكرر)
            OpResult r = _currentId > 0 && !_locked
                ? svc.Update(_currentId, _currentCustomerId, (DateBox.SelectedDate ?? DateTime.Now).ToString("dd/MM/yyyy"), orderId, itemsDto)
                : svc.Save(_currentCustomerId, (DateBox.SelectedDate ?? DateTime.Now).ToString("dd/MM/yyyy"), orderId, itemsDto);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            // §4/§5 — الحفظ ينجح ويبقى السند مفتوحاً في الواجهة كما هو
            bool wasUpdate = _currentId > 0 && _currentId == r.Id && DocNoBox.Text == r.DocumentNumber && DocNoBox.Text != "(تلقائي عند الحفظ)";
            _currentId = r.Id;
            DocNoBox.Text = r.DocumentNumber;
            AppContainer.Get<DialogService>().Info(wasUpdate
                ? $"تم تحديث سند التسليم {r.DocumentNumber} — اعتمده لخصم الكميات."
                : $"تم حفظ سند التسليم رقم: {r.DocumentNumber}\nالسند باقٍ أمامك — اعتمده لخصم الكميات من رصيد العميل.");
            RefreshList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.Save"); }
    }

    private void Approve()
    {
        try
        {
            if (_currentId == 0) { AppContainer.Get<DialogService>().Error("احفظ سند التسليم أولاً."); return; }
            if (!AppContainer.Get<DialogService>().Confirm("الاعتماد سيخصم الكميات نهائياً من رصيد العميل في مخزن التام. متابعة؟")) return;
            using var scope = AppContainer.NewScope();
            var svc = (ICustomerDeliveryService)scope.ServiceProvider.GetService(typeof(ICustomerDeliveryService));
            var r = svc.Approve(_currentId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            // §5 — السند يبقى في الواجهة بعد الاعتماد (مقفلاً)
            SetLocked(true);
            Customer_Changed(null, null);
            RefreshList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.Approve"); }
    }

    private void Unapprove()
    {
        try
        {
            if (_currentId == 0) return;
            if (!AppContainer.Get<DialogService>().Confirm("إلغاء التسليم سيعيد الكميات إلى رصيد العميل. متابعة؟")) return;
            using var scope = AppContainer.NewScope();
            var svc = (ICustomerDeliveryService)scope.ServiceProvider.GetService(typeof(ICustomerDeliveryService));
            var r = svc.Unapprove(_currentId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            SetLocked(false);
            Customer_Changed(null, null);
            RefreshList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.Unapprove"); }
    }

    /// <summary>§B105/P2 — حذف مسودة غير معتمدة (لم تخصم شيئاً) — المعتمد يُرفض في الخدمة.</summary>
    private void DeleteDraft()
    {
        if (_currentId == 0) { AppContainer.Get<DialogService>().Error("لا يوجد سند محفوظ ومفتوح لحذفه."); return; }
        if (_locked) { AppContainer.Get<DialogService>().Error("السند معتمد — لا يُحذف. ألغِ الاعتماد أولاً إن لزم التصحيح."); return; }
        if (!AppContainer.Get<DialogService>().Confirm($"حذف سند التسليم المسودة {DocNoBox.Text}؟")) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = (ICustomerDeliveryService)scope.ServiceProvider.GetService(typeof(ICustomerDeliveryService));
            var r = svc.DeleteDraft(_currentId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            NewForm();
            RefreshList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.DeleteDraft"); }
    }

    private void NewForm()
    {
        _currentId = 0;
        _items.Clear();
        try
        {
            using var _numScope = AppContainer.NewScope();
            var _num = _numScope.ServiceProvider.GetRequiredService<INumberingService>().Peek("CD");
            DocNoBox.Text = _num;
        }
        catch { DocNoBox.Text = "(تلقائي عند الحفظ)"; }
        DateBox.SelectedDate = DateTime.Now;
        SetLocked(false);
        AddEmptyRow();
    }

    private void AddEmptyRow()
    {
        _items.Add(new DelivBalanceRow());
    }

    private void EnsureEmptyRow()
    {
        if (_locked) return;
        if (_items.Count == 0 || !_items.Last().IsEmptyRow)
            AddEmptyRow();
    }

    private void AddEmptyRow_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) return;
        AddEmptyRow();
    }

    private void LotCode_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) return;
        if (sender is Button b && b.Tag is DelivBalanceRow row)
            OpenBatchPicker(row);
    }

    private void ProductName_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) return;
        if (sender is Button b && b.Tag is DelivBalanceRow row)
            OpenBatchPicker(row);
    }

    private void OpenBatchPicker(DelivBalanceRow row)
    {
        try
        {
            var dlg = new Views.FinishedBatchPickerWindow(_currentCustomerId > 0 ? _currentCustomerId : null) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.SelectedBatch != null)
            {
                var batch = dlg.SelectedBatch;
                row.ProductId = batch.ProductId;
                row.LotId = batch.LotId;
                row.PackagingTypeId = batch.PackagingTypeId;
                row.ProductName = batch.ProductName;
                row.LotCode = batch.LotCode;
                row.PackName = batch.PackName;
                row.Unit = batch.Unit;
                                // §1.50.67 FIX: لا وزن ثابت 7.5. §1.50.72 P3-1: كان يُترك الوزن صفراً ولا أحد يحدّثه،
                // فأي تعديل على «الكراتين» في هذا الصف يعيد حساب الوزن = كراتين × 0 = 0 بصمت
                // (ثم رفض الحفظ برسالة غامضة). وزن الدفعة متوفر من القائمة نفسها — يُملأ الآن.
                row.CartonWeight = batch.CartonWeight;
                row.UnitWeight = batch.CartonWeight;
                row.AvailableQty = batch.Qty;
                row.AvailablePackages = batch.Packages;
                row.Qty = batch.Qty;
                row.Packages = batch.Packages;
                EnsureEmptyRow();
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.PickBatch"); }
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) return;
        if (sender is Button b && b.Tag is DelivBalanceRow row)
        {
            _items.Remove(row);
            EnsureEmptyRow();
        }
    }


    private void SetLocked(bool locked)
    {
        _locked = locked;
        if (_toolbar != null)
        {
            if (_toolbar.SaveBtn != null) _toolbar.SaveBtn.IsEnabled = !locked;
            if (_toolbar.ApproveBtn != null) _toolbar.ApproveBtn.IsEnabled = !locked;
            if (_toolbar.UnapproveBtn != null) _toolbar.UnapproveBtn.IsEnabled = locked;
        }
    }

    private void Nav(int dir)
    {
        if (_deliveryIds.Count == 0) return;
        int idx = _deliveryIds.IndexOf(_currentId);
        idx = dir switch { 0 => 0, int.MaxValue => _deliveryIds.Count - 1, _ => Math.Clamp(idx + dir, 0, _deliveryIds.Count - 1) };
        OpenDelivery(_deliveryIds[idx]);
    }

    private void OpenDelivery(int id)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var d = db.CustomerDeliveries.Include(x => x.Items).FirstOrDefault(x => x.Id == id);
            if (d == null) return;
            _currentId = d.Id;
            _currentCustomerId = d.CustomerId;
            CustomerBox.SelectedValue = d.CustomerId;
            DateBox.SelectedDate = d.DeliveryDate;
            DocNoBox.Text = d.DocumentNumber;
            _items.Clear();
            // §1.50.72 P4-2: فحص الأوزان **قبل** البناء — كان الاستثناء يرمي في منتصف الحلقة
            // فتظهر رسالة خطأ عامة مع سند نصف محمّل (حالة الشاشة غير متسقة).
            foreach (var it in d.Items)
            {
                var prod0 = db.Products.AsNoTracking().FirstOrDefault(p => p.Id == it.ProductId);
                double w0 = it.PackagingTypeId != null
                    ? db.PackagingTypes.Where(k => k.Id == it.PackagingTypeId).Select(k => k.UnitWeightKg).FirstOrDefault()
                    : 0;
                if (w0 <= 0) w0 = it.CartonWeightKg > 0 ? it.CartonWeightKg : (prod0?.CartonWeightKg > 0 ? prod0.CartonWeightKg : 0);
                if (w0 <= 0)
                {
                    AppContainer.Get<DialogService>().Error($"لا يمكن فتح السند: وزن الكرتون غير معرف للصنف «{prod0?.ProductNameAr ?? it.ProductId.ToString()}» — عرّفه في بطاقة الصنف أولاً.");
                    return;
                }
            }
            foreach (var it in d.Items)
            {
                // §B105/P3 — العبوة ووزنها والوحدة تُعاد كما حُفظت (كانت تضيع فيُحسب الوزن بـ7.5 افتراضي)
                var prod = db.Products.AsNoTracking().FirstOrDefault(p => p.Id == it.ProductId);
                double unitW = it.PackagingTypeId != null
                    ? db.PackagingTypes.Where(k => k.Id == it.PackagingTypeId).Select(k => k.UnitWeightKg).FirstOrDefault()
                    : 0;
                if (unitW <= 0) unitW = it.CartonWeightKg > 0 ? it.CartonWeightKg : (prod?.CartonWeightKg > 0 ? prod.CartonWeightKg : 0);
                _items.Add(new DelivBalanceRow
                {
                    ProductId = it.ProductId,
                    LotId = it.LotId,
                    PackagingTypeId = it.PackagingTypeId,
                    ProductName = prod?.ProductNameAr ?? "-",
                    LotCode = db.Lots.Where(l => l.Id == it.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "—",
                    Qty = it.QtyKg,
                    Packages = it.PackageCount,
                    UnitWeight = unitW,
                    PackName = it.PackagingTypeId != null ? db.PackagingTypes.Where(k => k.Id == it.PackagingTypeId).Select(k => k.PackageNameAr).FirstOrDefault() ?? "—" : "—",
                    Unit = prod?.UnitOfMeasure ?? "—"
                });
            }
            SetLocked(d.IsApproved);
            if (!d.IsApproved) EnsureEmptyRow();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.Open"); }
    }

    private void Print()
    {
        if (_currentId <= 0) { AppContainer.Get<DialogService>().Error("احفظ السند أولًا؛ يمكن معاينة مسودة محفوظة دون اعتمادها. الطباعة تستخدم النسخة المحفوظة فقط."); return; }
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var m = Printing.StoredPrintModels.CustomerDelivery(db, _currentId);
            new PrintPreviewWindow(PhasePrint.Build(m), $"{m.DocTitle} {m.DocNo}")
            { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "DeliveryView.Print"); }
    }

    private void Export()
    {
        var report = new ReportResult
        {
            TitleAr = $"إذن تسليم عميل {DocNoBox.Text}",
            Columns = new List<string> { "الصنف", "الدفعة", "الكمية (كجم)", "العبوات" },
            Rows = _items.Select(i => new object[] { i.ProductName, i.LotCode, i.Qty, i.Packages }).ToList()
        };
        AppContainer.Get<ExportPrintService>().ExportExcel(report);
    }

    private void DeliveriesGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DeliveriesGrid.SelectedItem?.GetType().GetProperty("Id")?.GetValue(DeliveriesGrid.SelectedItem) is int id)
            OpenDelivery(id);
    }

    /// <summary>§بحث وفلترة لحظية على كل الأعمدة.</summary>
    /// <summary>§إصلاح: تسجيل الفوترة — الخدمة كانت موجودة بلا واجهة.</summary>
    private void MarkInvoiced()
    {
        if (_currentId == 0) { AppContainer.Get<DialogService>().Error("افتح سند تسليم معتمداً أولاً."); return; }
        var dlg = new Views.InputDialog("تسجيل فوترة", "الكمية المفوترة (كجم):") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        if (!double.TryParse(dlg.Value, out var qty) || qty <= 0)
        { AppContainer.Get<DialogService>().Error("أدخل كمية صحيحة أكبر من صفر."); return; }
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IPlanProgressService>();
            var r = svc.MarkInvoiced(_currentId, qty);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            RefreshList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delivery.Invoice"); }
    }

    private void DelivSearch_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => ScreenSearch.Apply(DelivSearchBox, DeliveriesGrid, _deliveries_all);

    // ══════════ 1.50.60 7-ج/7-هـ/7-ب ══════════
    private void DuplicateRow_Delivery(object sender, RoutedEventArgs e)
    {
        if (_locked) return;
        var src = ItemsGrid.SelectedItem as DelivBalanceRow ?? _items.LastOrDefault(x => !x.IsEmptyRow);
        if (src == null) return;
        _items.Add(new DelivBalanceRow
        {
            ProductId = src.ProductId, LotId = src.LotId, PackagingTypeId = src.PackagingTypeId,
            ProductName = src.ProductName, LotCode = src.LotCode,
            Qty = src.Qty, Packages = src.Packages, UnitWeight = src.UnitWeight,
            PackName = src.PackName, Unit = src.Unit,
            AvailableQty = src.AvailableQty, AvailablePackages = src.AvailablePackages
        });
    }

    private void ItemsGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_locked) return;
        if (e.Key == System.Windows.Input.Key.F2) { AddEmptyRow(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.F10) { Save(); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Enter)
        {
            if (ItemsGrid.CurrentColumn != null)
            {
                int idx = ItemsGrid.Columns.IndexOf(ItemsGrid.CurrentColumn);
                if (idx < ItemsGrid.Columns.Count - 1)
                {
                    ItemsGrid.CurrentCell = new DataGridCellInfo(ItemsGrid.SelectedItem, ItemsGrid.Columns[idx + 1]);
                    ItemsGrid.BeginEdit(); e.Handled = true;
                }
                else
                {
                    int rIdx = _items.IndexOf(ItemsGrid.SelectedItem as DelivBalanceRow);
                    if (rIdx >= 0 && rIdx < _items.Count - 1)
                    {
                        ItemsGrid.SelectedIndex = rIdx + 1;
                        ItemsGrid.CurrentCell = new DataGridCellInfo(_items[rIdx + 1], ItemsGrid.Columns[1]);
                        ItemsGrid.BeginEdit();
                    }
                    else AddEmptyRow();
                    e.Handled = true;
                }
            }
        }
        else if (e.Key == System.Windows.Input.Key.Down)
        {
            int rIdx = _items.IndexOf(ItemsGrid.SelectedItem as DelivBalanceRow);
            if (rIdx == _items.Count - 1) AddEmptyRow();
        }
    }

}

