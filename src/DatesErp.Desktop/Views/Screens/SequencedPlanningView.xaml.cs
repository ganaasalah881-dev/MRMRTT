using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>Setup-first screen for NEW plans. Existing, context-less documents route to PlanningView.</summary>
public partial class SequencedPlanningView : UserControl
{
    private sealed class PlanListRow
    {
        public int Id { get; set; }
        public string DocumentNumber { get; set; }
        public string Title { get; set; }
        public string Period { get; set; }
        public string Route { get; set; }
        public string Status { get; set; }
    }
    private sealed class CardUi
    {
        public ProductionPlanWorkSlot Slot { get; set; }
        public string Label { get; set; }
    }
    private sealed class SlotRow
    {
        public int? Id { get; set; }
        public int LotId { get; set; }
        public int ProductId { get; set; }
        public int PackId { get; set; }
        public string Customer { get; set; }
        public string Lot { get; set; }
        public string Product { get; set; }
        public string Pack { get; set; }
        public string Weight { get; set; }
        public string RawPerCarton { get; set; }
        public string CartonsText { get; set; }
        public string MaxText { get; set; }
    }

    private readonly int? _requestedPlanId;
    private readonly ObservableCollection<SlotRow> _rows = new();
    private List<SequencedLotOption> _offers = new();
    private List<CardUi> _cards = new();
    private int? _planId;
    private int? _slotId;
    private bool _loading;
    private bool _hadSavedItems;
    private bool _locked;
    private string _savedFingerprint = "";

    public SequencedPlanningView(int? planId = null)
    {
        _requestedPlanId = planId;
        InitializeComponent();
        RowsGrid.ItemsSource = _rows;
        Loaded += (_, _) =>
        {
            try
            {
                LoadSetup(); RefreshPlans();
                if (_requestedPlanId is int id) OpenManagedPlan(id);
                if (MainWindow.PendingPlanIdToOpen == _requestedPlanId) MainWindow.PendingPlanIdToOpen = null;
                SetupArea.Visibility = _planId == null ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) { Fail(ex, "SequencedPlanning.Load"); }
        };
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("خطة الإنتاج · إعداد النطاق ثم بطاقات اليوم والوردية");
        chrome.SetPermissionModule("planning");
        chrome.SetScreenCode("MRPMPS1001");
        chrome.SetToolbar(new Views.ErpToolbar()
            .WithNew((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("planning"), "إعداد خطة جديدة (F2)")
            .WithSave((_, _) => SaveCurrent(), "حفظ بطاقة اليوم/الوردية (F10)")
            .WithSearch((_, _) => { RefreshPlans(); PlansGrid.Focus(); }, "الخطط السابقة")
            .WithApprove((_, _) => Approve_Click(null, null), "اعتماد الخطة بعد اكتمال كل البطاقات")
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard")));
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard");
    }

    private void LoadSetup()
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        ModeBox.ItemsSource = new[] { "عميل واحد", "عدة عملاء مختارين", "توزيع عادل للعملاء" };
        ModeBox.SelectedIndex = 0;
        CustomersBox.ItemsSource = db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.CustomerName).ToList();
        ShiftsBox.ItemsSource = db.Shifts.AsNoTracking().Where(s => s.IsActive)
            .ToList().OrderBy(s => TimeSpan.TryParse(s.StartTime, out var start) ? start : TimeSpan.MaxValue)
            .Take(2).ToList();
        LineBox.ItemsSource = db.ProductionLines.AsNoTracking().Where(l => l.IsActive && l.CapacityPerShift > 0)
            .OrderBy(l => l.Id).ToList();
        if (LineBox.Items.Count > 0) LineBox.SelectedIndex = 0;
        StartBox.SelectedDate = EndBox.SelectedDate = DateTime.Today;
        var session = scope.ServiceProvider.GetRequiredService<ICurrentSession>();
        RuleBtn.Visibility = session.Can("products", "Edit") && session.Can("products", "Approve")
            ? Visibility.Visible : Visibility.Collapsed;
        NoProductionBtn.Visibility = session.Can("planning", "Approve") ? Visibility.Visible : Visibility.Collapsed;
        ApproveBtn.Visibility = NoProductionBtn.Visibility;
        ReturnBtn.Visibility = NoProductionBtn.Visibility;
        UnapproveBtn.Visibility = session.Can("planning", "Cancel") ? Visibility.Visible : Visibility.Collapsed;
        CancelSetupBtn.Visibility = UnapproveBtn.Visibility;
        CreateBtn.IsEnabled = session.Can("planning", "Create");
    }

    private void RefreshPlans()
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var managed = db.ProductionPlanContexts.AsNoTracking().Select(c => c.PlanId).ToHashSet();
        PlansGrid.ItemsSource = db.ProductionPlans.AsNoTracking().Where(p => p.PlanType != "Template")
            .OrderByDescending(p => p.Id).Take(250).ToList().Select(p => new PlanListRow
            {
                Id = p.Id, DocumentNumber = p.DocumentNumber, Title = p.PlanTitle,
                Period = $"{p.StartDate:dd/MM/yyyy} ← {p.EndDate:dd/MM/yyyy}",
                Route = managed.Contains(p.Id) ? "متسلسلة" : "تاريخية",
                Status = DocStatuses.ToArabic(p.Status)
            }).ToList();
    }

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CustomersBox != null && CustomersBox.SelectedItems.Count > 0) CustomersBox.UnselectAll();
        if (SetupHelp != null) SetupHelp.Text = ModeBox.SelectedIndex == 0
            ? "اختر عميلًا واحدًا؛ الأصناف/الدفعات ستقتصر عليه. وصفة الخام المعتمدة إلزامية."
            : "اختر عميلين أو أكثر مع Ctrl؛ الدفعات لكل عميل، ولا يمكن تغيير النطاق بعد الحفظ.";
    }
    private void Start_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EndBox != null && StartBox.SelectedDate is DateTime date && EndBox.SelectedDate < date)
            EndBox.SelectedDate = date;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var chosenCustomers = CustomersBox.SelectedItems.Cast<Customer>().Select(c => c.Id).ToList();
            var chosenShifts = ShiftsBox.SelectedItems.Cast<Shift>().OrderBy(s =>
                TimeSpan.TryParse(s.StartTime, out var t) ? t : TimeSpan.MaxValue).Select(s => s.Id).ToList();
            if (ModeBox.SelectedIndex == 0 && chosenCustomers.Count != 1)
            { Error("نطاق العميل الواحد يقتضي تحديد عميل واحد فقط."); return; }
            if (ModeBox.SelectedIndex > 0 && chosenCustomers.Count < 2)
            { Error("حدد عميلين أو أكثر في النطاق المتعدد/التوزيع."); return; }
            if (StartBox.SelectedDate == null || EndBox.SelectedDate == null || LineBox.SelectedItem is not ProductionLine line)
            { Error("حدّد بداية الفترة ونهايتها وخط الإنتاج قبل اختيار الأصناف."); return; }
            var setup = new SequencedPlanSetup
            {
                Title = TitleBox.Text, CustomerMode = ModeBox.SelectedIndex switch
                { 0 => "Single", 1 => "SelectedMulti", _ => "Fair" },
                StartDate = StartBox.SelectedDate.Value, EndDate = EndBox.SelectedDate.Value,
                CustomerIds = chosenCustomers, ShiftIds = chosenShifts, LineId = line.Id
            };
            using var scope = AppContainer.NewScope();
            var created = scope.ServiceProvider.GetRequiredService<SequencedPlanningService>().Create(setup);
            if (!created.Ok) { Error(created.Message); return; }
            Info(created.Message);
            RefreshPlans(); OpenManagedPlan(created.Id);
        }
        catch (Exception ex) { Fail(ex, "SequencedPlanning.Create"); }
    }

    private void RefreshPlans_Click(object sender, RoutedEventArgs e)
    {
        try { RefreshPlans(); } catch (Exception ex) { Fail(ex, "SequencedPlanning.RefreshPlans"); }
    }
    private void OpenPlan_Click(object sender, MouseButtonEventArgs e)
    {
        if (PlansGrid.SelectedItem is not PlanListRow row) return;
        (Window.GetWindow(this) as MainWindow)?.OpenPlanById(row.Id); // routes by persisted context
    }

    private void OpenManagedPlan(int id, int? selectSlot = null)
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<SequencedPlanningService>();
        var plan = db.ProductionPlans.AsNoTracking().FirstOrDefault(p => p.Id == id);
        var context = db.ProductionPlanContexts.AsNoTracking().FirstOrDefault(c => c.PlanId == id);
        if (plan == null || context == null) { Error("هذه الخطة تاريخية؛ افتحها عبر مسار الخطط السابقة."); return; }
        _planId = id;
        _locked = plan.IsApproved || plan.IsClosed || plan.Status is DocStatuses.Cancelled or "UnderApproval";
        SetupArea.Visibility = Visibility.Collapsed;
        CardArea.Visibility = Visibility.Visible;
        var custIds = db.ProductionPlanScopeCustomers.AsNoTracking().Where(x => x.PlanId == id).Select(x => x.CustomerId).ToList();
        var names = db.Customers.AsNoTracking().Where(c => custIds.Contains(c.Id)).Select(c => c.CustomerName).ToList();
        var shiftNames = db.Shifts.AsNoTracking().ToDictionary(s => s.Id, s => s.ShiftNameAr);
        var line = db.ProductionLines.AsNoTracking().Where(l => l.Id == context.LineId).Select(l => l.LineNameAr).FirstOrDefault();
        PlanHeader.Text = $"{plan.DocumentNumber} · {plan.PlanTitle} · {DocStatuses.ToArabic(plan.Status)}";
        ScopeSummary.Text = $"النطاق: {string.Join("، ", names)} · الفترة: {plan.StartDate:dd/MM/yyyy} — {plan.EndDate:dd/MM/yyyy} · الخط: {line} · النمط: {context.CustomerMode}";
        _loading = true;
        try
        {
            _cards = service.GetSlots(id).Select(s => new CardUi { Slot = s,
                Label = $"{s.WorkDate:ddd dd/MM/yyyy} · {(shiftNames.TryGetValue(s.ShiftId, out var name) ? name : $"وردية #{s.ShiftId}")} · {StateAr(s.State)}" }).ToList();
            CardsList.ItemsSource = _cards;
            var target = selectSlot is int sid ? _cards.FindIndex(c => c.Slot.Id == sid) : -1;
            if (target < 0) target = _cards.FindIndex(c => c.Slot.State is "Editing" or "NeedsReview" or "Pending");
            CardsList.SelectedIndex = target >= 0 ? target : Math.Max(0, _cards.Count - 1);
        }
        finally { _loading = false; }
        if (CardsList.SelectedItem is CardUi card) LoadCard(card);
        FairBtn.Visibility = context.CustomerMode == "Fair" ? Visibility.Visible : Visibility.Collapsed;
        SubmitBtn.IsEnabled = !_locked;
        ApproveBtn.IsEnabled = !_locked;
        ReturnBtn.IsEnabled = plan.Status == "UnderApproval";
        UnapproveBtn.IsEnabled = plan.IsApproved && !plan.IsClosed;
        CancelSetupBtn.IsEnabled = !plan.IsApproved && plan.Status == DocStatuses.Draft
            && !db.ProductionPlanItems.AsNoTracking().Any(i => i.PlanId == id);
    }

    private static string StateAr(string state) => state switch
    {
        "Pending" => "بانتظار السابقة", "Editing" => "قيد الإعداد", "Ready" => "مكتملة",
        "NeedsReview" => "تحتاج مراجعة", "NoProductionApproved" => "عدم تشغيل موثق", _ => state
    };

    private void Card_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CardsList.SelectedItem is not CardUi card || _planId == null) return;
        var earliest = _cards.FirstOrDefault(c => c.Slot.State is not ("Ready" or "NoProductionApproved"));
        if (!_locked && earliest != null && card.Slot.SequenceNo > earliest.Slot.SequenceNo)
        {
            Error("أكمل اليوم والوردية السابقة بالترتيب قبل فتح هذه البطاقة.");
            _loading = true;
            CardsList.SelectedItem = _cards.FirstOrDefault(c => c.Slot.Id == _slotId) ?? earliest;
            _loading = false;
            return;
        }
        if (_slotId != card.Slot.Id && _slotId != null && Fingerprint() != _savedFingerprint
            && !AppContainer.Get<DialogService>().Confirm("هناك كميات غير محفوظة؛ هل تريد الانتقال وإهمالها؟"))
        {
            _loading = true;
            CardsList.SelectedItem = _cards.FirstOrDefault(c => c.Slot.Id == _slotId);
            _loading = false;
            return;
        }
        try { LoadCard(card); } catch (Exception ex) { Fail(ex, "SequencedPlanning.Card"); }
    }

    private void LoadCard(CardUi card)
    {
        _slotId = card.Slot.Id;
        CardHeader.Text = $"{card.Label} · البطاقة {card.Slot.SequenceNo + 1} من {_cards.Count}";
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<SequencedPlanningService>();
        _offers = service.GetEligibleLots(_planId!.Value, card.Slot.Id).ToList();
        LotsGrid.ItemsSource = _offers;
        var existing = db.ProductionPlanItems.AsNoTracking().Where(i => i.WorkSlotId == card.Slot.Id)
            .OrderBy(i => i.PriorityNo).ToList();
        _hadSavedItems = existing.Count > 0;
        _rows.Clear();
        foreach (var item in existing)
        {
            var option = _offers.FirstOrDefault(o => o.LotId == item.LotId
                && o.ProductId == item.ProductId && o.PackagingTypeId == item.PackagingTypeId);
            _rows.Add(new SlotRow
            {
                Id = item.Id, LotId = item.LotId ?? 0, ProductId = item.ProductId, PackId = item.PackagingTypeId ?? 0,
                Customer = option?.CustomerName ?? db.Customers.AsNoTracking().Where(c => c.Id == item.CustomerId)
                    .Select(c => c.CustomerName).FirstOrDefault() ?? "—",
                Lot = option?.LotCode ?? db.Lots.AsNoTracking().Where(l => l.Id == item.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "—",
                Product = option?.ProductName ?? db.Products.AsNoTracking().Where(p => p.Id == item.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "—",
                Pack = option?.PackagingName ?? db.PackagingTypes.AsNoTracking().Where(p => p.Id == item.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault() ?? "—",
                Weight = (option?.CartonWeightKg ?? (item.PlannedCartons > 0 ? item.PlannedQtyKg / item.PlannedCartons : 0)).ToString("N3"),
                RawPerCarton = (option?.RawKgPerCarton ?? (item.PlannedCartons > 0 ? (decimal)(item.SourceQtyKg / item.PlannedCartons) : 0m)).ToString("N3"),
                CartonsText = item.PlannedCartons.ToString(CultureInfo.CurrentCulture)
            });
        }
        CapacityHint.Text = "الكميات محفوظة لكل بطاقة على حدة؛ حساب الحد يأخذ الإشغال والدفعات المفرج عنها في الاعتبار.";
        _savedFingerprint = Fingerprint();
        SetCardButtons(card);
    }

    private void SetCardButtons(CardUi card)
    {
        bool editable = !_locked && card.Slot.State is not ("Pending" or "NoProductionApproved");
        RowsGrid.IsReadOnly = !editable;
        AddBtn.IsEnabled = editable; RemoveBtn.IsEnabled = editable; SaveBtn.IsEnabled = editable;
        CompleteBtn.IsEnabled = editable; RuleBtn.IsEnabled = editable; QuoteBtn.IsEnabled = editable;
        FairBtn.IsEnabled = editable && !_hadSavedItems;
        NoProductionBtn.IsEnabled = editable && _rows.Count == 0;
    }

    private static SlotRow NewRow(SequencedLotOption option, int? cartons = null) => new()
    {
        LotId = option.LotId, ProductId = option.ProductId, PackId = option.PackagingTypeId,
        Customer = option.CustomerName, Lot = option.LotCode, Product = option.ProductName,
        Pack = option.PackagingName, Weight = option.CartonWeightKg.ToString("N3"),
        RawPerCarton = option.RawKgPerCarton.ToString("N3"),
        CartonsText = cartons?.ToString(CultureInfo.CurrentCulture) ?? ""
    };

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (LotsGrid.SelectedItem is not SequencedLotOption offer) { Error("اختر دفعة وصنفًا من القائمة المؤهلة."); return; }
        if (offer.MaximumCartons < 1) { Error(offer.Reason ?? "الكمية الصالحة تساوي صفرًا."); return; }
        _rows.Add(NewRow(offer));
        RowsGrid.SelectedItem = _rows.Last();
        CapacityHint.Text = $"{offer.ProductName} · {offer.LotCode}: الحد المبدئي {offer.MaximumCartons:N0} كرتون ({offer.Reason}). أدخل كميتك واحسب الحد مجددًا.";
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (RowsGrid.SelectedItem is SlotRow row)
        {
            _rows.Remove(row);
            CapacityHint.Text = "أُزيل البند من المسودة؛ احفظ البطاقة لتثبيت التغيير في القاعدة. لحذف جميع البنود احفظ البطاقة الفارغة مع سبب ثم وثّق عدم التشغيل.";
        }
    }
    private void Rows_EditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => { if (_slotId != null && !_locked) QuoteRows(); }));
    }
    private void Quote_Click(object sender, RoutedEventArgs e) => QuoteRows();

    private List<SequencedPlanItemInput> DraftInputs(bool reportError)
    {
        RowsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RowsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var inputs = new List<SequencedPlanItemInput>();
        foreach (var row in _rows)
        {
            if (!int.TryParse(row.CartonsText, NumberStyles.Integer, CultureInfo.CurrentCulture, out var qty) || qty <= 0)
            {
                if (reportError) Error($"أدخل عدد كراتين صحيحًا موجبًا لبند {row.Product} / {row.Lot}.");
                return null;
            }
            inputs.Add(new SequencedPlanItemInput { ItemId = row.Id, LotId = row.LotId,
                ProductId = row.ProductId, PackagingTypeId = row.PackId, Cartons = qty });
        }
        return inputs;
    }

    private void QuoteRows()
    {
        if (_slotId == null || _planId == null || _rows.Count == 0) return;
        try
        {
            var inputs = DraftInputs(false);
            if (inputs == null)
            { CapacityHint.Text = "أدخل كراتين موجبة لجميع البنود لحساب سقف تشاركي موثوق؛ لا يُعرض رقم تخميني."; return; }
            using var scope = AppContainer.NewScope();
            var service = scope.ServiceProvider.GetRequiredService<SequencedPlanningService>();
            var notes = new List<string>();
            for (int index = 0; index < inputs.Count; index++)
            {
                var other = inputs.Where((_, j) => j != index).ToList();
                var q = service.Quote(_planId.Value, _slotId.Value, inputs[index], other);
                _rows[index].MaxText = q.Error == null ? $"{q.MaximumCartons:N0}" : "موقوف";
                notes.Add(q.Error ?? $"{_rows[index].Product}: {q.MaximumCartons:N0} كرتون — {q.LimitingReason} (وقت {q.TimeMaximumCartons:N0}، خط {q.LineMaximumCartons:N0}، خام {q.RawMaximumCartons:N0}، طاقة صنف {q.ProductShiftMaximumCartons:N0})");
            }
            RowsGrid.Items.Refresh();
            CapacityHint.Text = string.Join(" | ", notes);
        }
        catch (Exception ex) { Fail(ex, "SequencedPlanning.Quote"); }
    }

    private bool SaveCurrent()
    {
        if (_planId == null || _slotId == null) { Error("احفظ إعداد الخطة أولاً."); return false; }
        if (_locked) { Error("الخطة/البطاقة مقفلة."); return false; }
        try
        {
            var inputs = DraftInputs(true);
            if (inputs == null) return false;
            if (inputs.Count == 0 && !_hadSavedItems)
            { Error("اختر بندًا وكمية أولًا؛ عدم التشغيل له مسار توثيق مستقل."); return false; }
            if (Fingerprint() == _savedFingerprint && _hadSavedItems) return true;
            string reason = null;
            if (_hadSavedItems)
            {
                var dlg = new Views.InputDialog("سبب تعديل البطاقة", "ما سبب تغيير البنود أو الكميات؟", "")
                    { Owner = Window.GetWindow(this) };
                if (dlg.ShowDialog() != true) return false;
                reason = dlg.Value;
            }
            using var scope = AppContainer.NewScope();
            var r = scope.ServiceProvider.GetRequiredService<SequencedPlanningService>()
                .SaveSlot(_planId.Value, _slotId.Value, inputs, reason);
            if (!r.Ok) { Error(r.Message); QuoteRows(); return false; }
            OpenManagedPlan(_planId.Value, _slotId.Value);
            Info(r.Message);
            return true;
        }
        catch (Exception ex) { Fail(ex, "SequencedPlanning.Save"); return false; }
    }
    private void Save_Click(object sender, RoutedEventArgs e) => SaveCurrent();

    private void Complete_Click(object sender, RoutedEventArgs e)
    {
        if (_slotId == null || _planId == null || !SaveCurrent()) return;
        try
        {
            int previous = _slotId.Value;
            using var scope = AppContainer.NewScope();
            var r = scope.ServiceProvider.GetRequiredService<SequencedPlanningService>().CompleteSlot(_planId.Value, previous);
            if (!r.Ok) { Error(r.Message); return; }
            OpenManagedPlan(_planId.Value);
            Info(r.Message);
        }
        catch (Exception ex) { Fail(ex, "SequencedPlanning.Complete"); }
    }

    private void NoProduction_Click(object sender, RoutedEventArgs e)
    {
        if (_planId == null || _slotId == null || _rows.Count > 0) { Error("وثّق عدم التشغيل لوردية خالية من البنود فقط."); return; }
        try
        {
            var dlg = new Views.InputDialog("اعتماد عدم تشغيل الوردية", "السبب (10 أحرف على الأقل):", "")
                { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            using var scope = AppContainer.NewScope();
            var r = scope.ServiceProvider.GetRequiredService<SequencedPlanningService>()
                .ApproveNoProduction(_planId.Value, _slotId.Value, dlg.Value);
            if (!r.Ok) { Error(r.Message); return; }
            OpenManagedPlan(_planId.Value); Info(r.Message);
        }
        catch (Exception ex) { Fail(ex, "SequencedPlanning.NoProduction"); }
    }

    private void Fair_Click(object sender, RoutedEventArgs e)
    {
        if (_planId == null || _slotId == null || _rows.Count > 0) { Error("لا يستبدل الاقتراح بنود الوردية الحالية؛ ابدأ ببطاقة فارغة."); return; }
        try
        {
            using var scope = AppContainer.NewScope();
            var p = scope.ServiceProvider.GetRequiredService<SequencedPlanningService>()
                .SuggestFairSlot(_planId.Value, _slotId.Value);
            foreach (var item in p.Items)
            {
                var offer = _offers.FirstOrDefault(o => o.LotId == item.LotId && o.ProductId == item.ProductId
                    && o.PackagingTypeId == item.PackagingTypeId);
                if (offer != null) _rows.Add(NewRow(offer, item.Cartons));
            }
            QuoteRows();
            Info($"اقتراح قابل للتعديل ولم يُحفظ بعد. راجع حصة كل عميل، ثم احفظ البطاقة.\n{string.Join("\n", p.Warnings)}");
        }
        catch (Exception ex) { Fail(ex, "SequencedPlanning.Fair"); }
    }

    private void Rule_Click(object sender, RoutedEventArgs e)
    {
        if (LotsGrid.SelectedItem is not SequencedLotOption offer) { Error("اختر الصنف والدفعة والعبوة لتعريف سحب الخام."); return; }
        try
        {
            var kg = new Views.InputDialog("وصفة خام معتمدة", "كجم خام داخلة لكل كرتون تام (ليست وزن المنتج الخارج):",
                offer.RawKgPerCarton > 0 ? offer.RawKgPerCarton.ToString(CultureInfo.CurrentCulture) : "")
                { Owner = Window.GetWindow(this) };
            if (kg.ShowDialog() != true) return;
            if (!decimal.TryParse(kg.Value, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) || value <= 0)
            { Error("أدخل مقدار خام موجبًا لكل كرتون."); return; }
            var why = new Views.InputDialog("اعتماد وصفة الخام", "مصدر/سبب الوصفة المعتمدة (10 أحرف على الأقل):", "")
                { Owner = Window.GetWindow(this) };
            if (why.ShowDialog() != true) return;
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            int rawProduct = db.Lots.AsNoTracking().Where(l => l.Id == offer.LotId).Select(l => l.ProductId).First();
            var r = scope.ServiceProvider.GetRequiredService<SequencedPlanningService>()
                .SaveRawRule(offer.ProductId, rawProduct, offer.PackagingTypeId, value, why.Value);
            if (!r.Ok) { Error(r.Message); return; }
            if (CardsList.SelectedItem is CardUi card) LoadCard(card);
            Info(r.Message);
        }
        catch (Exception ex) { Fail(ex, "SequencedPlanning.RawRule"); }
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (_planId == null) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var r = scope.ServiceProvider.GetRequiredService<IPlanningService>().SubmitPlan(_planId.Value);
            if (!r.Ok) { Error(r.Message); return; }
            OpenManagedPlan(_planId.Value); Info(r.Message);
        }
        catch (Exception ex) { Fail(ex, "SequencedPlanning.Submit"); }
    }
    private void Approve_Click(object sender, RoutedEventArgs e)
    {
        if (_planId == null) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var r = scope.ServiceProvider.GetRequiredService<IPlanningService>().ApprovePlan(_planId.Value);
            if (!r.Ok) { Error(r.Message); return; }
            OpenManagedPlan(_planId.Value); Info(r.Message);
        }
        catch (Exception ex) { Fail(ex, "SequencedPlanning.Approve"); }
    }

    private void Return_Click(object sender, RoutedEventArgs e)
    {
        if (_planId == null) return;
        try
        {
            var why = new Views.InputDialog("إعادة الخطة للتعديل", "الملاحظة/سبب الإرجاع (10 أحرف على الأقل):", "")
                { Owner = Window.GetWindow(this) };
            if (why.ShowDialog() != true) return;
            using var scope = AppContainer.NewScope();
            var r = scope.ServiceProvider.GetRequiredService<IPlanningService>().ReturnPlan(_planId.Value, why.Value);
            if (!r.Ok) { Error(r.Message); return; }
            OpenManagedPlan(_planId.Value); Info(r.Message);
        }
        catch (Exception ex) { Fail(ex, "SequencedPlanning.Return"); }
    }

    private void Unapprove_Click(object sender, RoutedEventArgs e)
    {
        if (_planId == null || !AppContainer.Get<DialogService>().Confirm(
            "فك اعتماد الخطة قبل إصدار أي أمر إنتاج فقط؟ يحتفظ النظام بجميع البنود وسجلها.")) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var r = scope.ServiceProvider.GetRequiredService<IPlanningService>().UnapprovePlan(_planId.Value);
            if (!r.Ok) { Error(r.Message); return; }
            OpenManagedPlan(_planId.Value); Info(r.Message);
        }
        catch (Exception ex) { Fail(ex, "SequencedPlanning.Unapprove"); }
    }

    private void CancelSetup_Click(object sender, RoutedEventArgs e)
    {
        if (_planId == null) return;
        try
        {
            var why = new Views.InputDialog("إلغاء إعداد خطة بلا بنود", "اذكر سبب إلغاء الإعداد (10 أحرف على الأقل):", "")
                { Owner = Window.GetWindow(this) };
            if (why.ShowDialog() != true) return;
            using var scope = AppContainer.NewScope();
            var r = scope.ServiceProvider.GetRequiredService<SequencedPlanningService>()
                .CancelUnstarted(_planId.Value, why.Value);
            if (!r.Ok) { Error(r.Message); return; }
            Info(r.Message);
            (Window.GetWindow(this) as MainWindow)?.OpenScreen("planning");
        }
        catch (Exception ex) { Fail(ex, "SequencedPlanning.CancelSetup"); }
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        if (_planId == null) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var revisions = db.ProductionPlanRevisions.AsNoTracking().Where(r => r.PlanId == _planId.Value)
                .OrderByDescending(r => r.RevisionNo).Take(30).ToList();
            var log = string.Join("\n\n", revisions.Select(r =>
                $"#{r.RevisionNo} · {r.ChangedAt:dd/MM/yyyy HH:mm} · {r.Action} · المستخدم #{r.ChangedBy}\n" +
                $"السبب: {r.Reason}\nقبل: {r.BeforeJson ?? "—"}\nبعد: {r.AfterJson ?? "—"}"));
            MessageBox.Show(Window.GetWindow(this), log.Length == 0 ? "لا يوجد سجل." : log,
                "سجل تعديلات خطة الإنتاج (آخر 30 نسخة)", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { Fail(ex, "SequencedPlanning.History"); }
    }

    private string Fingerprint() => string.Join(";", _rows.Select(r => $"{r.Id}:{r.LotId}:{r.ProductId}:{r.PackId}:{r.CartonsText}"));
    private static void Error(string text) => AppContainer.Get<DialogService>().Error(text);
    private static void Info(string text) => AppContainer.Get<DialogService>().Info(text);
    private static void Fail(Exception ex, string where) => AppContainer.Get<DialogService>().HandleException(ex, where);
}
