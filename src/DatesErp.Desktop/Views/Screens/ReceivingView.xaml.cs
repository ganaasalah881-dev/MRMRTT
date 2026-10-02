using System.Collections.ObjectModel;
using ItemRow = DatesErp.Desktop.Mvvm.ReceivingItemRow;
using DatesErp.Core.Common;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>شاشة استلام التمور — الإدخال المباشر من الجدول نفسه (1.50.54): اختيار الصنف من رقم/اسم داخل الصف.</summary>
public partial class ReceivingView : UserControl
{
    private List<object> _ship_all = new();
    private static string StatusToCode(string ar) => ar switch { "مرفوض/تالف" => "Rejected", "معلّق لاحقاً" => "Pending", "Moved" => "Moved", _ => "Received" };
    private static string CodeToStatus(string code) => code switch { "Rejected" => "مرفوض/تالف", "Pending" => "معلّق لاحقاً", _ => "مستلم" };

    private readonly ObservableCollection<ItemRow> _items = new();
    private List<int> _shipmentIds = new();
    private int _currentId;
    private int _remainingSourceId;
    private bool _locked;
    private bool _approved;
    private bool _loadingDocument;
    private string _mode = "New";
    private Views.ErpToolbar _toolbar;

    // §1.50.54 — قوائم للاختيار المباشر
    private List<PackagingType> _packagingTypes = new();
    private List<Product> _allProducts = new();
    // §1.50.60 7-ب/7-ج/7-هـ
    private System.Windows.Threading.DispatcherTimer _autoSaveTimer2;
    private DateTime _lastAutoSave2 = DateTime.MinValue;
    private string AutoSavePath2 => System.IO.Path.Combine(DatesErp.Infrastructure.Connection.AppConfig.ConfigDirectory, "drafts", $"ReceivingDraft_{(AppContainer.Provider?.GetService(typeof(ICurrentSession)) is ICurrentSession cs ? cs.UserId : 0)}.json");

    public ReceivingView()
    {
        InitializeComponent();
        ItemsGrid.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => RefreshTotals();
        var treatmentTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        treatmentTimer.Tick += (_, _) => RefreshVisibleTreatmentStates();
        _autoSaveTimer2 = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _autoSaveTimer2.Tick += (_, _) => AutoSaveDraft();
        ItemsGrid.PreviewKeyDown += ItemsGrid_PreviewKeyDown;
        Loaded += (_, _) => { Services.ComboBoxAutoShowHelper.Apply(this); Load(); treatmentTimer.Start(); _autoSaveTimer2.Start(); TryRestoreAutoSave(); };
        Unloaded += (_, _) => { treatmentTimer.Stop(); _autoSaveTimer2.Stop(); };
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("استلام الشحنات");
        chrome.SetScreenCode("MRPREC1001");
        chrome.SetToolbar(BuildToolbar());
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard");
    }

    private Views.ErpToolbar BuildToolbar()
    {
        _toolbar = new Views.ErpToolbar()
            .WithNew((_, _) => NewForm(), "أمر استلام جديد (F2)")
            .WithSave((_, _) => Save(), "حفظ أمر الاستلام — يبقى السند أمامك كما هو (F10)")
            .WithSearch((_, _) => OpenSearchWindow(), "بحث في سندات الاستلام (F9)")
            .WithEdit((_, _) => EditDocument())
            .WithUndo((_, _) => Undo(), "تراجع: يلغي الإدخالات غير المحفوظة ويعيد آخر نسخة محفوظة — لا يحذف أي مستند")
            .WithPrint((_, _) => Print(), "طباعة السند (Ctrl+P)")
            .WithDelete((_, _) => Delete())
            .WithApprove((_, _) => Approve(), "🔒 اعتماد وإنشاء الدفعات")
            .WithCustom("⎘ تكرار الصف", "ErpButton", (_, _) => DuplicateRow_Receiving(), "تكرار الصف السابق — إدخال 20 بند متشابه يصبح ثواني (7-ج)")
            .WithCustom("📥 استلام المتبقي", "ErpButton", (_, _) => ReceiveRemainingClick())
            .WithUnapprove((_, _) => Unapprove())
            .WithCustom("🛠 تصحيح معتمد", "ErpDangerButton", (_, _) => OpenQtyCorrection(), "تعديل كمية/عميل سند معتمد بقيد فرق موثق وسبب إجباري — بلا فك السلسلة (§C2)", "CorrectApprovedQty")
            .WithCustom("🔗 تصحيح السلسلة", "ErpDangerButton", (_, _) => OpenChainCorrection(), "معالج فك مترابط بعكس البناء (أوامر ← خطط ← إلغاء اعتماد) بسبب وتدقيق (§C1)", "ChainCorrection")
            .WithNavigation((_, _) => Nav(0), (_, _) => Nav(-1), (_, _) => Nav(1), (_, _) => Nav(int.MaxValue))
            .WithList((_, _) => RefreshList(), "عرض كل سندات الاستلام")
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard"));
        return _toolbar;
    }

    private void Load()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            CustomerBox.ItemsSource = db.Customers.Where(c => c.IsActive).ToList();
            EmployeeBox.ItemsSource = db.Employees.ToList();
            try { scope.ServiceProvider.GetRequiredService<DatesErp.Application.Services.MasterDataService>().SyncPackagingFromUnits(); } catch { }
            var unitNames = db.UnitsOfMeasure.AsNoTracking().Where(u => u.IsActive).Select(u => u.UnitNameAr).ToList();
            var allPacks = db.PackagingTypes.AsNoTracking().ToList();
            var packsFromUnits = allPacks.Where(pk => unitNames.Contains(pk.PackageNameAr)).ToList();
            _packagingTypes = packsFromUnits.Count > 0 ? packsFromUnits : allPacks;
            WarehouseBox.ItemsSource = db.Warehouses.Where(w => w.IsActive && w.WarehouseType == "Raw").OrderBy(w => w.WarehouseCode == "WRM" ? 0 : 1).ThenBy(w => w.Id).ToList();
            WarehouseBox.SelectedValue = db.Warehouses.Where(w => w.WarehouseCode == "WRM").Select(w => w.Id).FirstOrDefault();
            _allProducts = db.Products.Where(p => p.IsActive && p.ItemType == "Raw").OrderBy(p => p.ProductNameAr).ToList();

            NewForm();

            if (MainWindow.PendingShipmentIdToOpen is int pendingShip)
            {
                MainWindow.PendingShipmentIdToOpen = null;
                OpenShipment(pendingShip);
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.Load"); }
    }

    private void ReceiveRemainingClick()
    {
        try
        {
            if (_currentId == 0 || !_approved) { AppContainer.Get<DialogService>().Error("افتح سنداً معتمداً له بنود معلّقة أولاً."); return; }
            if (!_items.Any(i => StatusToCode(i.Status) == "Pending")) { AppContainer.Get<DialogService>().Error("لا توجد بنود معلقة."); return; }
            _remainingSourceId = _currentId;
            _currentId = 0; _approved = false; _mode = "New";
            DocNoBox.Text = "سند لاحق — لم يُحفظ بعد";
            ApplyMode();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.ReceiveRemaining"); }
    }

    private void OpenSearchWindow()
    {
        try
        {
            var win = new DocSearchWindow("بحث سندات الاستلام",
                new List<SearchFieldDef>
                {
                    new() { Key = "doc", LabelAr = "رقم السند" },
                    new() { Key = "customer", LabelAr = "العميل" },
                    new() { Key = "container", LabelAr = "الحاوية" }
                },
                cond =>
                {
                    using var scope = AppContainer.NewScope();
                    var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                    var q = db.Shipments.AsNoTracking().AsQueryable();
                    if (!string.IsNullOrWhiteSpace(cond.GetValueOrDefault("doc")))
                        q = q.Where(s => s.DocumentNumber.Contains(cond["doc"].Trim()));
                    if (!string.IsNullOrWhiteSpace(cond.GetValueOrDefault("container")))
                        q = q.Where(s => s.ContainerNumber.Contains(cond["container"].Trim()));
                    var list = q.OrderByDescending(s => s.Id).ToList();
                    var custDict = db.Customers.AsNoTracking().ToDictionary(c => c.Id, c => c.CustomerName);
                    if (!string.IsNullOrWhiteSpace(cond.GetValueOrDefault("customer")))
                    {
                        var term = cond["customer"].Trim().ToLower();
                        list = list.Where(s => custDict.TryGetValue(s.CustomerId, out var nm) && nm.ToLower().Contains(term)).ToList();
                    }
                    var res = new SearchResult { Columns = new List<string> { "رقم السند", "العميل", "التاريخ", "الوزن كجم", "الحالة" } };
                    foreach (var s in list)
                    {
                        string custName = custDict.TryGetValue(s.CustomerId, out var cn) ? cn : "—";
                        res.Rows.Add((s.Id, new object[]
                        {
                            s.DocumentNumber,
                            custName,
                            Core.Common.UiFormat.D(s.ReceivedDate),
                            s.TotalWeightKg,
                            s.IsApproved ? "معتمد 🟢" : "مسودة 🟡"
                        }));
                    }
                    return res;
                });
            win.Owner = Window.GetWindow(this);
            if (win.ShowDialog() == true && win.SelectedId is int id) OpenShipment(id);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.Search"); }
    }

    private void EditDocument()
    {
        if (_currentId == 0) { AppContainer.Get<DialogService>().Error("لا يوجد مستند محفوظ للتعديل — اضغط «جديد» لإنشاء سند."); return; }
        if (_approved) { AppContainer.Get<DialogService>().Error(Core.Common.UiFormat.MsgLocked + "\nالسند معتمد — ألغِ الاعتماد أولاً (حسب صلاحيتك)."); return; }
        _mode = "Edit";
        ApplyMode();
    }

    private void Undo()
    {
        if (_remainingSourceId > 0) OpenShipment(_remainingSourceId);
        else if (_currentId > 0) OpenShipment(_currentId);
        else NewForm();
    }

    private void ApplyMode()
    {
        bool editable = !_approved && (_mode == "New" || _mode == "Edit");
        _locked = !editable;
        CustomerBox.IsEnabled = editable;
        ArrivalDate.IsEnabled = editable;
        ReceivedDate.IsEnabled = editable;
        EmployeeBox.IsEnabled = editable;
        ContainerBox.IsEnabled = editable;
        WarehouseBox.IsEnabled = editable;
        NotesBox.IsEnabled = editable;
        ItemsGrid.IsReadOnly = false; // نتحكم بالصف عبر IsEditable
        foreach (var row in _items) row.IsEditable = editable;
        ItemStatusColumn.IsReadOnly = !editable || _remainingSourceId > 0;
        if (_remainingSourceId > 0)
        {
            CustomerBox.IsEnabled = ArrivalDate.IsEnabled = EmployeeBox.IsEnabled = ContainerBox.IsEnabled = false;
            WarehouseBox.IsEnabled = NotesBox.IsEnabled = false;
        }
        LockBanner.Visibility = _approved ? Visibility.Visible : Visibility.Collapsed;
        if (_toolbar != null)
        {
            if (_toolbar.SaveBtn != null) _toolbar.SaveBtn.IsEnabled = editable;
            if (_toolbar.EditBtn != null) _toolbar.EditBtn.IsEnabled = !_approved && _mode == "View";
            if (_toolbar.ApproveBtn != null) _toolbar.ApproveBtn.IsEnabled = !_approved && _currentId > 0 && _mode == "View";
            if (_toolbar.UnapproveBtn != null) _toolbar.UnapproveBtn.IsEnabled = _approved;
            if (_toolbar.DeleteBtn != null) _toolbar.DeleteBtn.IsEnabled = !_approved && _currentId > 0;
        }
        DocState.Text = _mode switch
        {
            "New" => "حالة المستند: مستند جديد — أدخل البيانات ثم اضغط حفظ",
            "View" => _approved ? $"حالة المستند: السند رقم {DocNoBox.Text} — معتمد 🔒 (عرض فقط)" : $"حالة المستند: السند رقم {DocNoBox.Text} — محفوظ (عرض) — اضغط «تعديل» لإجراء تغييرات",
            _ => $"حالة المستند: السند رقم {DocNoBox.Text} — وضع التعديل — احفظ التغييرات أو اضغط «تراجع» لإلغائها"
        };
    }

    private void ContainerBox_LostFocus(object sender, RoutedEventArgs e) => CheckDuplicateContainer(silent: true);

    private bool CheckDuplicateContainer(bool silent = false)
    {
        DuplicateWarn.Visibility = Visibility.Collapsed;
        var cn = ContainerBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(cn) || cn.Length < 3) return true;
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = (IReceivingService)scope.ServiceProvider.GetService(typeof(IReceivingService));
            var dup = svc.FindDuplicateContainers(cn, _currentId > 0 ? _currentId : null);
            if (dup.Count == 0) return true;
            var lines = string.Join("\n", dup.Take(3).Select(d => $"• {d.DocumentNumber} — {d.CustomerName} — {Core.Common.UiFormat.D(d.ReceivedDate)} — {d.TotalWeightKg:N0} كجم {(d.IsApproved ? "(معتمد)" : "(مسودة)") }"));
            var msg = $"⚠ رقم الحاوية «{cn}» ورد سابقاً في:\n{lines}{(dup.Count > 3 ? $"\n... و{dup.Count - 3} سندات أخرى" : "")}\n\nتأكد أن هذا ليس استلاماً مكرراً لنفس الحاوية.";
            DuplicateWarn.Text = "⚠ " + msg.Split('\n')[1];
            DuplicateWarn.Visibility = Visibility.Visible;
            if (!silent && !AppContainer.Get<DialogService>().Confirm(msg + "\n\nهل تريد المتابعة بالحفظ على أي حال؟")) return false;
            return true;
        }
        catch { return true; }
    }

    private bool CommitItems()
    {
        if (ItemsGrid.CommitEdit(DataGridEditingUnit.Cell, true) && ItemsGrid.CommitEdit(DataGridEditingUnit.Row, true)) return true;
        AppContainer.Get<DialogService>().Error("صحّح قيمة البند الجاري تحريره قبل المتابعة.");
        return false;
    }

    // ═══════════════════ الإدخال المباشر من الجدول (1.50.54) ═══════════════════

    private void AddEmptyRow()
    {
        var row = new ItemRow { RowNo = _items.Count + 1, IsEditable = !_locked, Status = "مستلم" };
        _items.Add(row);
    }

    private void EnsureEmptyRow()
    {
        if (_locked) return;
        if (_items.Count == 0 || _items.Last().ProductId != 0)
            AddEmptyRow();
        RefreshTotals();
    }

    private void RefreshTotals()
    {
        int n = 1;
        foreach (var i in _items) i.RowNo = n++;
        var valid = _items.Where(i => i.ProductId != 0).ToList();
        GrandTotal.Text = $"{valid.Sum(i => i.QtyKg):N1} كجم";
        ItemsCount.Text = valid.Count.ToString();
        PackagesTotal.Text = valid.Sum(i => i.PackageCount).ToString("N0");
        ContainerSummary.Text = string.IsNullOrWhiteSpace(ContainerBox.Text) ? "" : $"🚢 الحاوية: {ContainerBox.Text}";
    }

    private void ProductCode_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) return;
        if (sender is Button b && b.Tag is ItemRow row)
            OpenProductPicker(row, row.ProductCode);
    }

    private void ProductName_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) return;
        if (sender is Button b && b.Tag is ItemRow row)
            OpenProductPicker(row, row.ProductName);
    }

    private void OpenProductPicker(ItemRow row, string initialSearch = "")
    {
        try
        {
            if (!row.IsEditable && !_loadingDocument) return;
            var dlg = new Views.ProductPickerWindow(initialSearch) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true || dlg.SelectedProduct == null) return;

            var p = dlg.SelectedProduct;
            // جلب العبوة الافتراضية ووزنها
            string packName = "—";
            int? packId = p.DefaultPackagingTypeId;
            double? packWeight = null;
            if (packId.HasValue)
            {
                var pk = _packagingTypes.FirstOrDefault(x => x.Id == packId.Value);
                if (pk != null) { packName = pk.PackageNameAr; packWeight = pk.UnitWeightKg; }
            }
            else
            {
                // أول عبوة كافتراضي
                var first = _packagingTypes.FirstOrDefault();
                if (first != null) { packId = first.Id; packName = first.PackageNameAr; packWeight = first.UnitWeightKg; }
            }

            row.FillFromProduct(p, packName, packId, packWeight);
            // إذا كان هذا الصف هو الأخير (الفارغ) أضف صفاً جديداً جاهزاً
            EnsureEmptyRow();
            RefreshTotals();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.PickProduct"); }
    }

    private void PackCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (_loadingDocument) return;
            if (sender is ComboBox cb && cb.Tag is ItemRow row)
            {
                if (cb.SelectedItem is PackagingType pk && pk.UnitWeightKg > 0)
                {
                    row.PackName = pk.PackageNameAr;
                    row.UnitWeightKg = pk.UnitWeightKg;
                }
                else if (cb.SelectedValue is int id)
                {
                    var pk2 = _packagingTypes.FirstOrDefault(x => x.Id == id);
                    if (pk2 != null)
                    {
                        row.PackName = pk2.PackageNameAr;
                        if (pk2.UnitWeightKg > 0) row.UnitWeightKg = pk2.UnitWeightKg;
                    }
                }
                RefreshTotals();
            }
        }
        catch { }
    }

    private void PackageCount_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.Tag is ItemRow row)
        {
            if (int.TryParse(tb.Text, out var c)) row.PackageCount = c;
            RefreshTotals();
        }
    }

    private void UnitWeight_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.Tag is ItemRow row)
        {
            if (double.TryParse(tb.Text, out var w)) row.UnitWeightKg = w;
            RefreshTotals();
        }
    }

    private void Number_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void Decimal_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(c => char.IsDigit(c) || c == '.' || c == ',' || c == '٫');
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (_remainingSourceId > 0) { AppContainer.Get<DialogService>().Error("السند اللاحق يكمل البنود المعلقة الأصلية؛ لا حذف منها هنا."); return; }
        if (_locked) { AppContainer.Get<DialogService>().Error("السند في وضع العرض؛ اضغط تعديل على المسودة أولاً."); return; }
        if (!CommitItems()) return;
        if (sender is Button b && b.Tag is ItemRow row)
        {
            _items.Remove(row);
            EnsureEmptyRow();
            RefreshTotals();
        }
    }

    private void Save()
    {
        try
        {
            if (_locked) { AppContainer.Get<DialogService>().Error("السند في وضع العرض؛ اضغط تعديل على المسودة أولاً."); return; }
            if (!CommitItems()) return;
            if (ReceivedDate.SelectedDate == null) { AppContainer.Get<DialogService>().Error("حدد تاريخ الاستلام."); return; }
            // §1.50.67 FIX متوسط: تجاهل الصفوف غير المكتملة (عدد 0) عند الحفظ — مثل إصلاح الخطط
            var allValid = _items.Where(r => r.ProductId != 0).ToList();
            var validRows = allValid.Where(r => r.PackageCount > 0 && r.UnitWeightKg > 0).ToList();
            if (validRows.Count == 0)
            {
                if (allValid.Count > 0) { AppContainer.Get<DialogService>().Error("أكمل عدد الوحدات ووزن العبوة للبند الجديد (>0) أو احذفه — الصفوف الفارغة لا تُحفظ."); return; }
                AppContainer.Get<DialogService>().Error("أضف بنداً واحداً على الأقل — اختر صنفاً من الجدول."); return;
            }
            foreach (var row in validRows)
            {
                if (row.PackageCount <= 0) { AppContainer.Get<DialogService>().Error($"البند {row.RowNo} ({row.ProductName}): أدخل عدد الوحدات."); return; }
                if (row.UnitWeightKg <= 0) { AppContainer.Get<DialogService>().Error($"البند {row.RowNo} ({row.ProductName}): أدخل وزن العبوة."); return; }
                if (row.QtyKg <= 0) row.QtyKg = row.PackageCount * row.UnitWeightKg;
                row.ValidateTreatment(ReceivedDate.SelectedDate.Value);
            }
            var cust = CustomerBox.SelectedItem as Customer;
            if (cust == null) { AppContainer.Get<DialogService>().Error("اختر العميل المورد."); return; }
            if (_remainingSourceId == 0 && !CheckDuplicateContainer()) return;

            using var scope = AppContainer.NewScope();
            var svc = (IReceivingService)scope.ServiceProvider.GetService(typeof(IReceivingService));
            var emp = EmployeeBox.SelectedItem as Employee;
            var r = _remainingSourceId > 0
                ? svc.ReceiveRemaining(_remainingSourceId, validRows.Select(i => new ReceivingTreatmentChoiceDto { ShipmentItemId = i.ShipmentItemId, TreatmentRequired = i.TreatmentRequired, UntilDate = i.TreatmentUntilDate }).ToList(), ReceivedDate.SelectedDate)
                : svc.SaveShipment(cust.Id,
                ArrivalDate.SelectedDate?.ToString("dd/MM/yyyy"),
                (ReceivedDate.SelectedDate ?? DateTime.Now).ToString("dd/MM/yyyy"),
                validRows.Select(i => new ShipmentItemDto
                {
                    ProductId = i.ProductId,
                    PackagingTypeId = i.PackId,
                    PackageCount = i.PackageCount,
                    UnitWeightKg = i.UnitWeightKg,
                    QtyKg = i.QtyKg,
                    ReceiptUnit = i.ReceiptUnit,
                    ItemStatus = StatusToCode(i.Status),
                    TreatmentRequired = i.TreatmentRequired,
                    TreatmentUntilDate = i.TreatmentUntilDate
                }).ToList(),
                NotesBox.Text, ContainerBox.Text, emp?.Id,
                _currentId > 0 ? _currentId : null,
                WarehouseBox.SelectedValue as int?);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            OpenShipment(r.Id);
            AppContainer.Get<DialogService>().Info($"تم حفظ سند الاستلام رقم: {r.DocumentNumber}\nالمستند باقٍ أمامك — يمكنك طباعته أو تعديله أو اعتماده.");
            RefreshList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.Save"); }
    }

    private void Approve()
    {
        try
        {
            if (_currentId == 0 || _mode != "View") { AppContainer.Get<DialogService>().Error("احفظ سند الاستلام وتعديلاته أولاً قبل الاعتماد."); return; }
            int toTreat = _items.Count(i => i.TreatmentRequired == true && StatusToCode(i.Status) == "Received");
            var msg = "سيتم اعتماد الاستلام وإنشاء الدفعات وتقييد الوارد في مخزن الخام." + (toTreat > 0 ? $"\n🧪 و{toTreat} بنداً موجَّهاً لمستودع المعالجة — سيبقى محجوزاً حتى التاريخ المختار لكل بند." : "") + "\nمتابعة؟";
            if (!AppContainer.Get<DialogService>().Confirm(msg)) return;
            using var scope = AppContainer.NewScope();
            var svc = (IReceivingService)scope.ServiceProvider.GetService(typeof(IReceivingService));
            var r = svc.ApproveShipment(_currentId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            OpenShipment(_currentId);
            RefreshList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.Approve"); }
    }

    private void Unapprove()
    {
        try
        {
            if (_currentId == 0) return;
            var dlg = new InputDialog("سبب فك الاعتماد", "اكتب سبب فك اعتماد السند (يُسجَّل في التدقيق — إجباري):");
            if (dlg.ShowDialog() != true) return;
            if (string.IsNullOrWhiteSpace(dlg.Value)) { AppContainer.Get<DialogService>().Error("السبب إجباري لفك الاعتماد."); return; }
            if (!AppContainer.Get<DialogService>().Confirm("سيُسجَّل قيد عكسي وتُلغى الدفعات رسمياً — الأثر الأصلي محفوظ بالدفتر (§43). متابعة؟")) return;
            using var scope = AppContainer.NewScope();
            var svc = (IReceivingService)scope.ServiceProvider.GetService(typeof(IReceivingService));
            var r = svc.UnapproveShipment(_currentId, dlg.Value.Trim());
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            OpenShipment(_currentId);
            RefreshList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.Unapprove"); }
    }

    private void OpenQtyCorrection()
    {
        try
        {
            if (_currentId == 0) return;
            var w = new Views.QtyCorrectionWindow(_currentId) { Owner = Window.GetWindow(this) };
            if (w.ShowDialog() == true) { OpenShipment(_currentId); RefreshList(); }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.QtyCorrection"); }
    }

    private void OpenChainCorrection()
    {
        try
        {
            if (_currentId == 0) return;
            var w = new Views.ChainCorrectionWindow(_currentId) { Owner = Window.GetWindow(this) };
            if (w.ShowDialog() == true) { OpenShipment(_currentId); RefreshList(); }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.ChainCorrection"); }
    }

    private void Delete()
    {
        try
        {
            if (_currentId == 0) { AppContainer.Get<DialogService>().Error("لا يوجد سند محدد."); return; }
            if (!AppContainer.Get<DialogService>().Confirm("حذف سند الاستلام (مسودة)؟")) return;
            using var scope = AppContainer.NewScope();
            var svc = (IReceivingService)scope.ServiceProvider.GetService(typeof(IReceivingService));
            var r = svc.DeleteShipment(_currentId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            RefreshList();
            NewForm();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.Delete"); }
    }

    private void NewForm()
    {
        _remainingSourceId = 0;
        ItemsGrid.CancelEdit(DataGridEditingUnit.Cell);
        ItemsGrid.CancelEdit(DataGridEditingUnit.Row);
        _loadingDocument = false;
        _currentId = 0;
        _approved = false;
        _mode = "New";
        _items.Clear();
        CustomerBox.SelectedIndex = -1;
        EmployeeBox.SelectedIndex = -1;
        ArrivalDate.SelectedDate = null;
        ReceivedDate.SelectedDate = DateTime.Now;
        ContainerBox.Text = ""; NotesBox.Text = "";
        DuplicateWarn.Visibility = Visibility.Collapsed;
        var wrm = (WarehouseBox.ItemsSource as List<Warehouse>)?.FirstOrDefault(w => w.WarehouseCode == "WRM");
        if (wrm != null) WarehouseBox.SelectedValue = wrm.Id;
        try
        {
            using var _numScope = AppContainer.NewScope();
            var _num = _numScope.ServiceProvider.GetRequiredService<INumberingService>().Peek("SHIP");
            DocNoBox.Text = _num;
        }
        catch { DocNoBox.Text = "(تلقائي عند الحفظ)"; }
        AddEmptyRow();
        ApplyMode();
        RefreshTotals();
    }

    private void Nav(int dir)
    {
        if (_shipmentIds.Count == 0) return;
        int idx = _shipmentIds.IndexOf(_currentId);
        idx = dir switch { 0 => 0, int.MaxValue => _shipmentIds.Count - 1, _ => Math.Clamp(idx + dir, 0, _shipmentIds.Count - 1) };
        OpenShipment(_shipmentIds[idx]);
    }

    private void OpenShipment(int id)
    {
        try
        {
            ItemsGrid.CancelEdit(DataGridEditingUnit.Cell);
            ItemsGrid.CancelEdit(DataGridEditingUnit.Row);
            _loadingDocument = true;
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var states = scope.ServiceProvider.GetRequiredService<IReceivingService>().GetTreatmentStates(id).ToDictionary(x => x.ShipmentItemId);
            var ship = db.Shipments.Include(s => s.Items).FirstOrDefault(s => s.Id == id);
            if (ship == null) return;
            _remainingSourceId = 0;
            _currentId = ship.Id;
            DocNoBox.Text = ship.DocumentNumber;
            CustomerBox.SelectedValue = ship.CustomerId;
            ArrivalDate.SelectedDate = ship.ArrivalDate;
            ReceivedDate.SelectedDate = ship.ReceivedDate;
            EmployeeBox.SelectedValue = ship.ReceivedBy;
            ContainerBox.Text = ship.ContainerNumber ?? "";
            NotesBox.Text = ship.Notes ?? "";
            WarehouseBox.SelectedValue = ship.ReceivingWarehouseId ?? db.Warehouses.Where(w => w.WarehouseCode == "WRM").Select(w => w.Id).FirstOrDefault();
            _items.Clear();
            foreach (var it in ship.Items)
            {
                _items.Add(new ItemRow
                {
                    ProductId = it.ProductId,
                    PackId = it.PackagingTypeId,
                    ProductCode = db.Products.Where(p => p.Id == it.ProductId).Select(p => p.ProductCode).FirstOrDefault() ?? "-",
                    ProductName = db.Products.Where(p => p.Id == it.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                    PackName = db.PackagingTypes.Where(p => p.Id == it.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault() ?? "-",
                    ReceiptUnit = it.ReceiptUnit ?? "كرتون",
                    PackageCount = it.PackageCount,
                    UnitWeightKg = it.UnitWeightKg,
                    QtyKg = it.TotalWeightKg,
                    Status = CodeToStatus(it.Status),
                    ShipmentItemId = it.Id,
                    TreatmentRequired = it.TreatmentRequired,
                    HasLegacyTreatment = states.ContainsKey(it.Id) && states[it.Id].HasLegacyTreatment,
                    TreatmentUntilDate = states.ContainsKey(it.Id) ? states[it.Id].UntilDate : null,
                    TreatmentStateAr = states.ContainsKey(it.Id) ? states[it.Id].StateAr : ""
                });
            }
            _approved = ship.IsApproved;
            _mode = "View";
            ApplyMode();
            _loadingDocument = false;
            if (!_approved) EnsureEmptyRow();
            RefreshTotals();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.Open"); }
        finally { _loadingDocument = false; }
    }

    private void RefreshList()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var list = db.Shipments.OrderByDescending(s => s.Id).ToList();
            _shipmentIds = list.Select(s => s.Id).ToList();
            _ship_all = list.Select(s => new { Id = s.Id, DocNo = s.DocumentNumber, Customer = db.Customers.Where(c => c.Id == s.CustomerId).Select(c => c.CustomerName).FirstOrDefault(), Date = Core.Common.UiFormat.D(s.ReceivedDate), Weight = s.TotalWeightKg, StatusAr = s.IsApproved ? "معتمد 🟢" : "مسودة 🟡" }).ToList().Cast<object>().ToList();
            ScreenSearch.Apply(ShipsSearchBox, ShipGrid, _ship_all);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.List"); }
    }

    private void Print()
    {
        try
        {
            if (_currentId == 0) { AppContainer.Get<DialogService>().Error("احفظ السند أولاً قبل الطباعة — الطباعة تُنفَّذ من بيانات محفوظة."); return; }
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var model = Views.ReceivingPrintModel.Load(db, _currentId);
            if (model == null) { AppContainer.Get<DialogService>().Error("تعذر تحميل بيانات السند للطباعة."); return; }
            var doc = Views.ReceivingPrintDocument.Build(model);
            var preview = new Views.PrintPreviewWindow(doc, $"سند استلام {model.DocumentNumber}") { Owner = Window.GetWindow(this) };
            preview.ShowDialog();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.Print"); }
    }

    private void ShipsSearch_Changed(object sender, TextChangedEventArgs e) => ScreenSearch.Apply(ShipsSearchBox, ShipGrid, _ship_all);
    private void ShipGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ShipGrid.SelectedItem == null) return;
        dynamic sel = ShipGrid.SelectedItem;
        // ShipGrid bound via ScreenSearch — need to get Id from _ship_all
        int idx = ShipGrid.SelectedIndex;
        if (idx >= 0 && idx < _ship_all.Count)
        {
            var idProp = _ship_all[idx].GetType().GetProperty("Id");
            if (idProp != null) { int id = (int)idProp.GetValue(_ship_all[idx]); OpenShipment(id); return; }
        }
        try { int id = (int)sel.Id; OpenShipment(id); } catch { }
    }

    private void RefreshTreatment_Click(object sender, RoutedEventArgs e) => RefreshVisibleTreatmentStates();

    // §1.50.62 — نسخ من إيصال سابق (8) قوالب ونسخ سندات
    private void CopyFromPrevious_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_locked) { AppContainer.Get<DialogService>().Error("السند في وضع العرض؛ اضغط تعديل أو جديد أولاً."); return; }
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErp.Infrastructure.Persistence.DatesErpDbContext>();
            var recent = db.Shipments.AsNoTracking().OrderByDescending(s => s.Id).Take(20).ToList();
            if (recent.Count == 0) { AppContainer.Get<DialogService>().Info("لا توجد إيصالات سابقة للنسخ منها."); return; }
            var picker = new Views.ShipmentPickerWindow(recent) { Owner = System.Windows.Window.GetWindow(this) };
            if (picker.ShowDialog() != true || picker.SelectedShipmentId == null) return;
            var src = db.Shipments.Include(s => s.Items).FirstOrDefault(s => s.Id == picker.SelectedShipmentId.Value);
            if (src == null) return;
            int added = 0;
            foreach (var it in src.Items)
            {
                var prod = db.Products.AsNoTracking().FirstOrDefault(p => p.Id == it.ProductId);
                if (prod == null) continue;
                var pack = _packagingTypes.FirstOrDefault(p => p.Id == it.PackagingTypeId);
                _items.Add(new Mvvm.ReceivingItemRow
                {
                    ProductId = prod.Id,
                    ProductCode = prod.ProductCode,
                    ProductName = prod.ProductNameAr,
                    PackId = it.PackagingTypeId,
                    PackName = pack?.PackageNameAr ?? "",
                    PackageCount = it.PackageCount,
                    UnitWeightKg = it.UnitWeightKg,
                    QtyKg = it.TotalWeightKg,
                    ReceiptUnit = it.ReceiptUnit ?? prod.UnitOfMeasure ?? "كجم",
                    Status = "مستلم",
                    IsEditable = true,
                    RowNo = _items.Count + 1
                });
                added++;
            }
            EnsureEmptyRow();
            RefreshTotals();
            AppContainer.Get<DialogService>().Info($"تم نسخ {added} بنداً من الإيصال {src.DocumentNumber}.");
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Receiving.CopyFromPrevious"); }
    }
    private void RefreshVisibleTreatmentStates()
    {
        try
        {
            if (_currentId == 0) return;
            using var scope = AppContainer.NewScope();
            var states = scope.ServiceProvider.GetRequiredService<IReceivingService>().GetTreatmentStates(_currentId).ToDictionary(x => x.ShipmentItemId);
            foreach (var row in _items)
            {
                if (states.TryGetValue(row.ShipmentItemId, out var st))
                {
                    row.TreatmentStateAr = st.StateAr;
                    row.HasLegacyTreatment = st.HasLegacyTreatment;
                }
            }
        }
        catch { }
    }

    private void UntilDate_ValidationError(object sender, ValidationErrorEventArgs e) { }
    private void CompleteLegacy_Click(object sender, RoutedEventArgs e) { }

    // §دعم تحميل قائمة العبوات في كل صف (DataGridTemplateColumn ComboBox)
    private void ItemsGrid_Loaded(object sender, RoutedEventArgs e)
    {
        // يتم تعيين ItemsSource لكل ComboBox عبر حدث LoadingRow
    }

    private void ItemsGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        // لكل صف، ابحث عن ComboBox العبوة وعبئه
        // نستخدم VisualTreeHelper للعثور على ComboBox
        e.Row.Loaded += (s, _) =>
        {
            if (e.Row.DataContext is ItemRow)
            {
                var combo = FindVisualChild<ComboBox>(e.Row);
                if (combo != null && combo.Name == "PackCombo" || combo != null)
                {
                    // نحاول العثور على كل ComboBox في الصف
                    var combos = FindVisualChildren<ComboBox>(e.Row);
                    foreach (var cb in combos)
                    {
                        if (cb.ItemsSource == null)
                        {
                            cb.ItemsSource = _packagingTypes;
                        }
                    }
                }
            }
        };
    }

    private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    private static List<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var list = new List<T>();
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) list.Add(t);
            list.AddRange(FindVisualChildren<T>(child));
        }
        return list;
    }

    // ══════════ 1.50.60 7-ج/7-هـ/7-ب ══════════
    private void DuplicateRow_Receiving()
    {
        if (_locked) return;
        var src = ItemsGrid.SelectedItem as ItemRow ?? _items.LastOrDefault(x => x.ProductId != 0);
        if (src == null) return;
        var dup = new ItemRow
        {
            ProductId = src.ProductId, ProductCode = src.ProductCode, ProductName = src.ProductName,
            PackId = src.PackId, PackName = src.PackName,
            PackageCount = src.PackageCount, UnitWeightKg = src.UnitWeightKg, QtyKg = src.QtyKg,
            ReceiptUnit = src.ReceiptUnit, Status = src.Status,
            IsEditable = !_locked, RowNo = _items.Count + 1
        };
        _items.Add(dup);
        EnsureEmptyRow();
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
                    int rIdx = _items.IndexOf(ItemsGrid.SelectedItem as ItemRow);
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
            int rIdx = _items.IndexOf(ItemsGrid.SelectedItem as ItemRow);
            if (rIdx == _items.Count - 1) AddEmptyRow();
        }
    }

    private void AutoSaveDraft()
    {
        try
        {
            if (_locked) return;
            var valid = _items.Where(r => r.ProductId != 0).ToList();
            if (valid.Count == 0) return;
            var dir = System.IO.Path.GetDirectoryName(AutoSavePath2);
            System.IO.Directory.CreateDirectory(dir);
            var json = System.Text.Json.JsonSerializer.Serialize(valid.Select(r => new { r.ProductId, r.PackageCount, r.UnitWeightKg }).ToList());
            System.IO.File.WriteAllText(AutoSavePath2, json);
            _lastAutoSave2 = DateTime.Now;
        }
        catch { }
    }
    private void TryRestoreAutoSave()
    {
        try
        {
            if (!System.IO.File.Exists(AutoSavePath2)) return;
            var fi = new System.IO.FileInfo(AutoSavePath2);
            if ((DateTime.Now - fi.LastWriteTime).TotalHours > 24) return;
            if (_items.Count(x => x.ProductId != 0) > 0) return;
            if (!AppContainer.Get<DialogService>().Confirm($"يوجد حفظ تلقائي من {fi.LastWriteTime:dd/MM/yyyy HH:mm} — استعادة؟")) return;
            var json = System.IO.File.ReadAllText(AutoSavePath2);
            var list = System.Text.Json.JsonSerializer.Deserialize<List<AutoSaveRow2>>(json);
            if (list == null) return;
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            foreach (var r in list)
            {
                var prod = db.Products.AsNoTracking().FirstOrDefault(p => p.Id == r.ProductId);
                if (prod == null) continue;
                _items.Add(new ItemRow
                {
                    ProductId = prod.Id, ProductCode = prod.ProductCode, ProductName = prod.ProductNameAr,
                    PackageCount = r.PackageCount, UnitWeightKg = r.UnitWeightKg, QtyKg = r.PackageCount * r.UnitWeightKg,
                    IsEditable = true, Status = "مستلم"
                });
            }
            EnsureEmptyRow();
        }
        catch { }
    }
    private class AutoSaveRow2 { public int ProductId { get; set; } public int PackageCount { get; set; } public double UnitWeightKg { get; set; } }
}
