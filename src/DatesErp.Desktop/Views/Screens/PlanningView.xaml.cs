using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §إصلاح: يُبلّغ عن التغيير — Renumber() يُعيد ترقيم الصفوف بعد كل تعديل في المجموعة،
/// وبلا إشعار كانت خلية «م» تعرض أرقاماً قديمة وتصبح وسوم أزرار الحذف خاطئة.
/// </summary>
/// <summary>§B58: خيار قائمة (وردية/خط/عبوة) لخلايا الجدولEditable.</summary>
public class OptUi { public int Id { get; set; } public string Name { get; set; } }

/// <summary>
/// شاشة إعداد واعتماد خطط الإنتاج (MPS) — مطابقة للنموذج المعتمد:
/// مسار المعاملة (إعداد ← اعتماد المدير العام ← أوامر التشغيل ← الإقفال)،
/// نطاق التخطيط (عدة عملاء/عميل محدد)، أزرار الإدراج، شريط طاقة الوردية،
/// خطة اليوم وحالة الأيام وتقدم العملاء المستقل.
/// </summary>
public partial class PlanningView : UserControl
{
    private List<Views.PlanSearchWindow.PlanSearchItem> _plans_all = new();
    private readonly ObservableCollection<PlanRowUi> _rows = new();
    private List<(int? id, string name)> _planCustomers = new();
    private List<int> _planIds = new();
    // §إصلاح: معرّفات الورديات/الخطوط الفعلية المعروضة — بدل افتراض SelectedIndex+1
    private List<int> _shiftIds = new();
    private List<int> _lineIds = new();
    private int SelectedShiftId() => ShiftBox.SelectedIndex >= 0 && ShiftBox.SelectedIndex < _shiftIds.Count ? _shiftIds[ShiftBox.SelectedIndex] : 1;
    private int SelectedLineId() => LineBox.SelectedIndex >= 0 && LineBox.SelectedIndex < _lineIds.Count ? _lineIds[LineBox.SelectedIndex] : 1;
    /// <summary>§v1.50.35 — خيار الصنف التام يحمل مواصفات البطاقة (وزن/قوالب/طاقة) لا عبوة عامة.</summary>
    internal static ProductOption ToProductOpt(DatesErp.Core.Domain.Entities.Product p) => new()
    {
        Id = p.Id,
        Name = $"{p.ProductNameAr} ({p.ProductCode})",
        CartonWeightKg = p.CartonWeightKg,
        MoldsCount = p.MoldsCount,
        MoldWeightKg = p.MoldWeightKg,
        HourlyRate = p.HourlyProductionRate,
        DefaultPackagingTypeId = p.DefaultPackagingTypeId
    };
    private int? _currentPlanId; // §1.50.67 FIX: nullable to fix CS0472 and Value usage
    private string _savedPlanFingerprint = ""; // §1.50.71 P1: بصمة آخر حالة محفوظة/محملة (رأس + بنود صالحة) — تكشف تعديلات غير محفوظة قبل الاعتماد
    private bool _locked;
    private bool _programmaticScope; // حارس: تغييرات النطاق البرمجية لا تفتح النوافذ تلقائياً
    private Views.ErpToolbar _toolbar;
    // §1.50.67: الحفظ التلقائي معطّل بطلب المستخدم؛ يُحذف فقط أي ملف مسودة قديم.
    private string AutoSavePath => System.IO.Path.Combine(DatesErp.Infrastructure.Connection.AppConfig.ConfigDirectory, "drafts", $"PlanningDraft_{(AppContainer.Provider?.GetService(typeof(ICurrentSession)) is ICurrentSession cs ? cs.UserId : 0)}.json");

    // §B58: قوائم الخلاياEditable (وردية/خط/عبوة) — تُقرأ من قاعدة البيانات في Load
    public List<OptUi> ShiftOptions { get; } = new();
    public List<OptUi> LineOptions { get; } = new();
    public List<OptUi> PackOptions { get; } = new();

    public PlanningView()
    {
        InitializeComponent();
        RowsGrid.ItemsSource = _rows;
        _rows.CollectionChanged += (_, e) =>
        {
            Renumber(); UpdateCapacityBar(); UpdateTotals();
            if (e.NewItems != null)
                foreach (PlanRowUi row in e.NewItems)
                {
                    row.PropertyChanged += RowUi_Changed;
                    row.QuantityGuard = q => CheckRowQuantity(row, q);
                }
            if (e.OldItems != null)
                foreach (PlanRowUi row in e.OldItems) { row.PropertyChanged -= RowUi_Changed; row.QuantityGuard = null; }
        };
        // §1.50.67 FIX: إلغاء الحفظ التلقائي — كان يحفظ خططاً وهمية (بناءً على طلب المستخدم).
        // §1.50.60 7-هـ: تنقل لوحة مفاتيح مثل Excel
        RowsGrid.PreviewKeyDown += RowsGrid_PreviewKeyDown;
        Loaded += (_, _) =>
        {
            Services.ComboBoxAutoShowHelper.Apply(this);
            Load();
            // §إصلاح: قائمة الخطط المحفوظة تُحمّل فور فتح الشاشة لتظهر مباشرة في شبكة السجل
            RefreshPlansList();
            // §1.50.67 FIX: إلغاء استعادة مسودة تلقائية — يسبب خطط وهمية
            // §1.50.69 FIX: حذف ملفات الحفظ التلقائي القديمة التي تسبب خطط وهمية + الدفعة برقم السند
            try
            {
                if (System.IO.File.Exists(AutoSavePath)) System.IO.File.Delete(AutoSavePath);
                var draftsDir = System.IO.Path.GetDirectoryName(AutoSavePath);
                if (System.IO.Directory.Exists(draftsDir))
                {
                    foreach (var f in System.IO.Directory.GetFiles(draftsDir, "PlanningDraft_*.json"))
                    {
                        try { System.IO.File.Delete(f); } catch { }
                    }
                }
            }
            catch { }
            // لا تُستعاد المسودة تلقائياً؛ ذلك كان ينشئ خططاً وهمية في هذا الإصدار.
            // §فتح خطة محددة طُلبت من شاشة أخرى (لوحة التحكم) ثم تصفير الطلب
            if (MainWindow.PendingPlanIdToOpen is int pid)
            {
                MainWindow.PendingPlanIdToOpen = null;
                OpenPlan(pid);
            }
        };
        Unloaded += (_, _) => { /* §1.50.69 FIX: لا حفظ تلقائي نهائياً + حذف المسودات */ try { if (System.IO.File.Exists(AutoSavePath)) System.IO.File.Delete(AutoSavePath); } catch { } };
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("خطة الإنتاج — التخطيط والجدولة (MPS)");
        // وحدة الصلاحيات ثابتة بالكود، وليست عنوان الشاشة المعروض للمستخدم.
        chrome.SetPermissionModule("planning");
        chrome.SetScreenCode("MRPMPS1001");
        _toolbar = new Views.ErpToolbar()
            .WithNew((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("planning"), "إعداد خطة جديدة بنطاق عملاء وورديات إلزامي (F2)")
            .WithSave((_, _) => Save_Click(null, null), "حفظ الخطة (F10)")
            // §إصلاح: الاعتماد كان مكتوباً في Approve() لكنه غير موصول بأي زر — فلم يكن ممكناً
            // اعتماد خطة من شاشتها إطلاقاً، وكان المسار الوحيد عبر لوحة التحكم وللمدير فقط.
            .WithApprove((_, _) => Approve(), "اعتماد الخطة ونقلها لأوامر التشغيل")
            .WithDelete((_, _) => DeletePlan())
            .WithUndo((_, _) => UndoInput(), "تراجع / مسح التعديلات والبدء من جديد")
            .WithSearch((_, _) => OpenPlansSearch(), "بحث واختيار من الخطط المحفوظة (F9)")
            .WithPrint((_, _) => Print())
            .WithExcel((_, _) => Export())
            .WithUnapprove((_, _) => Unapprove(), "إلغاء الاعتماد وإعادة الفتح")
            .WithNavigation((_, _) => Nav(0), (_, _) => Nav(-1), (_, _) => Nav(1), (_, _) => Nav(int.MaxValue))
            .WithCustom("📋 الخطط السابقة (F9)", "ErpButton", (_, _) => OpenPlansSearch())
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard"));
        if (_toolbar.UnapproveBtn != null) _toolbar.UnapproveBtn.Visibility = Visibility.Collapsed;
        if (_toolbar.ApproveBtn != null) _toolbar.ApproveBtn.Visibility = Visibility.Collapsed;
        chrome.SetToolbar(_toolbar);
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard");
    }

    private void Load()
    {
        // كل قسم يُحمَّل مستقلاً: فشل قسم (كالورديات) لا يجوز أن يمنع ظهور قائمة العملاء
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var shiftRows = db.Shifts.Where(s => s.IsActive).OrderBy(s => s.Id).ToList();
            _shiftIds = shiftRows.Select(s => s.Id).ToList();
            // §B58: خيارات خلايا الجدول (وردية/خط/عبوة) من قاعدة البيانات
            ShiftOptions.Clear(); ShiftOptions.AddRange(shiftRows.Select(x => new OptUi { Id = x.Id, Name = x.ShiftNameAr }));
            LineOptions.Clear(); LineOptions.AddRange(db.ProductionLines.AsNoTracking().OrderBy(x => x.Id).Select(x => new OptUi { Id = x.Id, Name = x.LineNameAr }));
            PackOptions.Clear(); PackOptions.Add(new OptUi { Id = 0, Name = "عام (أي عبوة)" });
            PackOptions.AddRange(db.PackagingTypes.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Id).Select(x => new OptUi { Id = x.Id, Name = x.PackageNameAr }));
            ShiftBox.ItemsSource = shiftRows
                .Select(s => $"{s.ShiftNameAr} (ساعات فعلية: {s.EffectiveProductiveHours} س)").ToList();
            var lineRows = db.ProductionLines.Where(l => l.IsActive).OrderBy(l => l.Id).ToList();
            _lineIds = lineRows.Select(l => l.Id).ToList();
            LineBox.ItemsSource = lineRows
                .Select(l => $"{l.LineNameAr} (طاقة: {l.CapacityPerShift} كجم)").ToList();
            if (ShiftBox.Items.Count > 0) ShiftBox.SelectedIndex = 0;
            if (LineBox.Items.Count > 0) LineBox.SelectedIndex = 0;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Load.ShiftsLines"); }

        RefreshCustomerList();

        try
        {
            using var _numScope = AppContainer.NewScope();
            var _num = _numScope.ServiceProvider.GetRequiredService<INumberingService>().Peek("PLAN");
            CodeBox.Text = _num;
        }
        catch { CodeBox.Text = "PLN-تلقائي"; }
        if (PlanMetaBox != null) PlanMetaBox.Text = "خطة جديدة — لم تُحفظ بعد · أنشأها: — · اعتمدها: —";
        // §2 — الشاشة تفتح فارغة في وضع «خطة جديدة» — الخطط المحفوظة تظهر عبر زر «قائمة الخطط / بحث»
        UpdateCapacityBar();
    }

    /// <summary>تحميل/إعادة تحميل قائمة العملاء لخطة العميل المحدد — مع إظهار سبب الفشل الحقيقي إن فشل.</summary>
    private void RefreshCustomerList()
    {
        try
        {
            if (SingleCustBox == null) return;
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var customers = db.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.CustomerName).ToList();
            SingleCustBox.ItemsSource = customers;
            if (customers.Count == 0)
                AppContainer.Get<DialogService>().Error("لا يوجد عملاء نشطون في البيانات الأساسية — أضف العملاء أولاً من شاشة بيانات العملاء.");
            else if (SingleCustBox.SelectedIndex < 0)
                SingleCustBox.Text = "-- اختر العميل --";
        }
        catch (Exception ex)
        {
            AppContainer.Get<DialogService>().HandleException(ex, "Planning.Load.Customers");
        }
    }

    // ══════════ نطاق التخطيط ونوع الخطة ══════════

    private void Scope_Changed(object sender, RoutedEventArgs e)
    {
        if (SingleCustPanel == null) return;
        bool single = SingleRadio.IsChecked == true;
        SingleCustPanel.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        // الخطة لعميل واحد: العميل يُحفظ في رأس النموذج فلا يظهر عموده بجوار البنود
        // §عميل واحد: لا يُعرض عمود العميل — كل البنود له، فالعمود تكرار بلا معلومة.
        if (CustomerColumn != null)
            CustomerColumn.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
        if (ScopeChip != null)
            ScopeChip.Text = single ? "النطاق: 👤 عميل محدد" : "النطاق: 👥 عدة عملاء (مجمع)";
        // في كل مرة يختار المستخدم «خطة لعميل محدد» تُعاد قراءة قائمة العملاء لضمان ألا تكون فارغة
        if (single) RefreshCustomerList();
    }

    /// <summary>نوع الخطة — يومية = تاريخ واحد فقط، الباقي فترة من-إلى. أزرار المدد السريعة أزيلت (1.50.57) بناءً على طلب المستخدم: الاكتفاء بتحديد التاريخ.</summary>
    private void TypeBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TypeBox == null || EndBox == null) return;
        bool isDaily = TypeBox.SelectedIndex == 0;
        bool isPeriod = TypeBox.SelectedIndex == 3;
        // يومية: تاريخ واحد فقط
        if (StartLabel != null) StartLabel.Text = isDaily ? "تاريخ الخطة *:" : "من تاريخ *:";
        if (EndPanel != null) EndPanel.Visibility = isDaily ? Visibility.Collapsed : Visibility.Visible;
        if (DateColumn != null) DateColumn.Visibility = isDaily ? Visibility.Collapsed : Visibility.Visible;
        if (isDaily)
        {
            if (StartBox?.SelectedDate != null)
            {
                EndBox.SelectedDate = StartBox.SelectedDate;
                // يومية: كل البنود بنفس تاريخ الخطة
                foreach (var r in _rows) r.DateValue = StartBox.SelectedDate;
                RowsGrid.Items.Refresh();
            }
            EndBox.IsEnabled = false;
        }
        else
        {
            EndBox.IsEnabled = isPeriod ? !_locked : true;
            if (!isPeriod && StartBox?.SelectedDate != null)
                EndBox.SelectedDate = PlanningService.PeriodEndDate(PlanTypeKey(), StartBox.SelectedDate.Value);
        }
        UpdateDateChips();
    }

    private void PlanDates_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TypeBox == null || StartBox == null || EndBox == null) return;
        bool isDaily = TypeBox.SelectedIndex == 0;
        if (isDaily && StartBox.SelectedDate != null)
        {
            EndBox.SelectedDate = StartBox.SelectedDate;
            // يومية: حدث كل البنود لتاريخ الخطة الواحد
            foreach (var r in _rows) r.DateValue = StartBox.SelectedDate;
            RowsGrid.Items.Refresh();
        }
        else if (TypeBox.SelectedIndex != 3 && StartBox.SelectedDate != null)
        {
            EndBox.SelectedDate = PlanningService.PeriodEndDate(PlanTypeKey(), StartBox.SelectedDate.Value);
        }
        UpdateDateChips();
        UpdateCapacityBar();
    }

    /// <summary>
    /// §التخطيط الأفقي: شرائح السياق أعلى الشاشة — «تاريخ الإنتاج المحدد» والفترة.
    /// تاريخ الإنتاج المحدد هو اليوم المُعيَّن للتنفيذ (تاريخ البداية)؛ وإن كانت الفترة أطول من يوم
    /// وُسِم بأنه بداية الفترة لأن لكل بند تاريخه المستقل في الجدول.
    /// </summary>
    private void UpdateDateChips()
    {
        try
        {
            var from = StartBox?.SelectedDate;
            var to = EndBox?.SelectedDate;
            string d(DateTime? v) => DatesErp.Core.Common.UiFormat.D(v);
            if (PeriodChip != null)
                PeriodChip.Text = from != null && to != null ? $"الفترة: {d(from)} – {d(to)}" : "الفترة: —";
            if (ScheduledDateChip != null)
            {
                if (from == null) ScheduledDateChip.Text = "📅 تاريخ الإنتاج المحدد: —";
                else if (to != null && to.Value.Date != from.Value.Date)
                    ScheduledDateChip.Text = $"📅 تاريخ الإنتاج المحدد: {d(from)} (بداية الفترة — لكل بند تاريخه في الجدول)";
                else ScheduledDateChip.Text = $"📅 تاريخ الإنتاج المحدد: {d(from)}";
            }
        }
        catch (Exception ex) { ErrorLog.Write(ex, "Planning.DateChips"); }
    }

    /// <summary>مطابق لـ v1.59 onSinglePlanCustomerChanged: اختيار العميل في الخطة الفردية يفتح نافذة دفعاته فوراً.</summary>
    private void SingleCust_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_programmaticScope || SingleCustBox == null || SingleCustBox.SelectedItem == null) return;
        if (SingleRadio.IsChecked != true) return;
        try
        {
            var cust = SingleCustBox.SelectedItem;
            var id = (int)cust.GetType().GetProperty("Id")!.GetValue(cust)!;
            var name = (string)cust.GetType().GetProperty("CustomerName")!.GetValue(cust)!;
            OpenLotsEditor(id, name);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.SingleCustomer"); }
    }

    // ══════════ الإدراج: أصناف العميل / العملاء ══════════

    /// <summary>👤 أصناف العميل (F4): عميل محدد ← نافذة شحناته ودفعاته ← اختيار الدفعة ← تفاصيل البند.</summary>
    private void PickLot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int? custId = null; string custName = null;
            if (SingleRadio.IsChecked == true)
            {
                if (SingleCustBox.SelectedItem == null)
                { AppContainer.Get<DialogService>().Error("اختر العميل المحدد أولاً."); return; }
                custId = (int)SingleCustBox.SelectedItem.GetType().GetProperty("Id").GetValue(SingleCustBox.SelectedItem);
                custName = (string)SingleCustBox.SelectedItem.GetType().GetProperty("CustomerName").GetValue(SingleCustBox.SelectedItem);
                AddRowForCustomer(custId.Value, custName);
            }
            else
            {
                OpenLotsEditor(null, null);
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.PickLot"); }
    }

    /// <summary>👥 أصناف العملاء (F6): النافذة المجمعة لشحنات ودفعات كافة العملاء (مطابقة لـ v1.59).</summary>
    private void MultiCustomers_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // §B76: في وضع «عميل محدد» لا تُفتح قائمة كل العملاء — دفعات عميله فقط
            if (SingleRadio.IsChecked == true)
            {
                if (SingleCustBox.SelectedItem == null)
                { AppContainer.Get<DialogService>().Error("اختر العميل المحدد أولاً."); return; }
                var cust = SingleCustBox.SelectedItem as DatesErp.Core.Domain.Entities.Customer;
                OpenLotsEditor(cust.Id, cust.CustomerName);
                return;
            }
            OpenLotsEditor(null, null);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.MultiCustomers"); }
    }

    /// <summary>👤 أصناف العميل (F4): فتح النافذة المنبثقة لدفعات العميل المحدد فقط.</summary>
    private void AddRowForCustomer(int custId, string custName)
    {
        OpenLotsEditor(custId, custName);
    }

    /// <summary>
    /// النافذة المنبثقة لاختيار الأصناف — مطابقة لنموذج v1.59:
    /// كل دفعة صف قابل للتحرير (الصنف التام ← العبوة ← الكراتين ← الخام يُحسب) مع تحديد متعدد
    /// وإدراج فردي وإنزال جماعي. إن مرَّ عميل محدد تُعرض دفعاته فقط.
    /// </summary>
    private void OpenLotsEditor(int? custId, string custName)
    {
        if (_locked) { AppContainer.Get<DialogService>().Error("الخطة معتمدة ومقفلة — فك الاعتماد أولاً (بصلاحية، وبعد إلغاء الحركات اللاحقة)."); return; }
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        int shiftId = SelectedShiftId();
        string rowDate = (StartBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy");

        var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        // فلتر مطابق لـ v1.59: أصناف المجموعة 002 أو بلا مجموعة — كي تظهر أصناف المصنع مهما كان تعبئة المجموعة
        var products = svc.GetFinishedProducts();
        var packs = db.PackagingTypes.Where(p => p.IsActive).ToList();
        if (packs.Count == 0 && products.Count > 0)
        {
            AppContainer.Get<DialogService>().Error("لا توجد عبوات معرفّة — أضف العبوات (الكراتين والقوالب) من شاشة العبوات أولاً.");
            return;
        }

        // §B67: مصدر واحد مُفلتر: عميل محدد ← دفعاته فقط (حتى الموروثة من السند)؛ عدة عملاء ← الكل
        var lotDtos = svc.GetAvailableLots(custId);
        var today = DateTime.Today;
        var rawByLot = db.Lots.AsNoTracking().ToDictionary(l => l.Id, l => l.ProductId);
        var editorRows = lotDtos.Select(l =>
        {
            var row = new LotEditorRow
            {
                LotId = l.LotId,
                ShipmentId = l.ShipmentId,
                ShipmentNo = l.ShipmentNo ?? "—",
                LotCode = l.LotCode,
                CustomerId = l.CustomerId, // §B87/M6: null = «بدون عميل» — يُحفَظ NULL لا صفراً
                CustomerName = l.CustomerName ?? (custName ?? "—"),
                RawName = l.ProductName ?? "—",
                Available = l.RemainingKg,
                // §المعالجة: حالتها على الدفعة (حتى يُعرض القيد باللون الأحمر ويُمنع الإنتاج قبل التاريخ)
                TreatmentRequired = l.RequiresTreatment,
                TreatmentUntilDate = l.TreatmentUntilDate,
                TreatmentReadyDate = l.TreatmentReadyDate,
                UnderTreatmentKg = l.UnderTreatmentKg,
                // §تتبع سحب الخام: سياق سطر الاستلام (الوحدة/وزنها/المستلم/المتاح) لعرضه عند الاختيار
                Ctx = svc.GetShipmentQuantityContext(l.LotId),
                DaysInStockText = l.ArrivalDate != null ? $"{Math.Max(0, (today - l.ArrivalDate.Value.Date).Days)} يوماً" : "",
                // §B68: لا تُعرض الأصناف التي نفذ رصيدها من هذه الدفعة
                AllProducts = svc.GetPlannableProducts(l.LotId).Select(ToProductOpt).ToList(),
                AllPacks = packs.Select(p => new PackOption { Id = p.Id, Name = p.PackageNameAr, UnitWeightKg = p.UnitWeightKg, MoldsCount = p.MoldsCount, MoldWeightKg = p.MoldWeightKg }).ToList(),
                // §B80: وحدة كل صنف تام كما في بطاقته — تظهر فور اختيار الصنف
                ProductUnits = products.ToDictionary(x => x.Id, x => string.IsNullOrWhiteSpace(x.UnitOfMeasure) ? "—" : x.UnitOfMeasure)
            };
            int? exclPlan = _currentPlanId.HasValue && _currentPlanId.Value > 0 ? _currentPlanId : (int?)null;
            foreach (var p2 in products)
                row.PerProductAvailable[p2.Id] = svc.GetProductLotRemaining(l.LotId, p2.Id, exclPlan);
            ConfigureRawSelector(row, rawByLot.TryGetValue(l.LotId, out var raw) ? raw : null, db, svc);
            row.SetContext(); // §يبني خيارات طريقة السحب من وحدة الاستلام الفعلية (يثبّت أيضاً حالة المعالجة)
            return row;
        }).Where(r => r.Available > 0 || r.UnderTreatmentKg > 0).ToList(); // §تُعرض الكميات تحت المعالجة أيضاً
        var allLots = lotDtos;

        if (editorRows.Count == 0)
        {
            // §B56: رسالة تشخيصية صادقة تفرز السببين: لا دفعات أصلاً، أم دفعات محجوزة/مستهلكة بالكامل
            if (allLots.Count == 0)
            {
                int anyLots = db.Lots.Count();
                AppContainer.Get<DialogService>().Error(custId != null
                    ? $"لا توجد دفعات خام في المخزن باسم العميل «{custName}» (إجمالي الدفعات بالنظام: {anyLots}).\n" +
                      "التخطيط يستهلك الخام من دفعات الاستلام — استلم خاماً لهذا العميل من شاشة الاستلام ثم عد للتخطيط."
                    : "لا توجد شحنات خام في المخزن حالياً — التخطيط يبني بنوده على دفعات الاستلام.\n" +
                      "استلم الخام أولاً من شاشة «الاستلام وسندات الاستلام» (يعتمد الاستلام فتتكوّن الدفعات)، ثم عد إلى الخطط.");
            }
            else
            {
                double totalReserved = allLots.Sum(l => l.ReservedQtyKg); // lotDtos
                AppContainer.Get<DialogService>().Error(
                    $"توجد {allLots.Count} دفعة بالمخزن لكن المتاح منها للتخطيط صفر — كل الكميات محجوزة لخطط/أوامر قائمة أو مستهلكة.\n" +
                    $"إجمالي المحجوز: {totalReserved:N1} كجم. أقفل أو احذف الخطط المنتهية لتحرير الحجوزات، أو استلم خاماً إضافياً.");
            }
            return;
        }

        string title = custId != null
            ? $"👤 أصناف وشحنات العميل: {custName} — اختر الصنف التام والكمية ثم إنزال"
            : "👥 أصناف وشحنات كافة العملاء المتاحة بالمستودع — دليل الاختيار المجمع";
        // §B80: فترة الخطة تُمرر للنافذة — تاريخ كل بند إلزامي داخلها
        DateTime? planFrom = StartBox.SelectedDate ?? DateTime.Today;
        DateTime? planTo = EndBox.SelectedDate ?? planFrom;
        // §B92: الاختيار اليدوي للوردية — الورديات النشطة + وردية الشاشة افتراضياً
        var shiftsForManual = db.Shifts.Where(s => s.IsActive).OrderBy(s => s.Id).ToList();
        var win = new LotsEditorWindow(editorRows, title, custId != null, planFrom, planTo, shiftsForManual, shiftId,
            additions => EvaluateDraft(_rows.Select(CapacityItem).Concat(additions).ToList()), SelectedLineId()) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() != true || win.Inserted.Count == 0) return;

        // تحويل الصفوف المدرجة إلى بنود الخطة (صنف كامل أو جزء — حسب ما أدخله المستخدم)
        InsertEditorRows(win.Inserted, products, packs, rowDate, shiftId,
            LineBox.SelectedIndex >= 0 ? SelectedLineId() : 1, db);
    }

    /// <summary>
    /// §B91 — 🔍 فحص الخطة: يوزّع بنود الخطة المحفوظة (عملاء/أصناف) على أيام الفترة (من–إلى)
    /// بنفس عمل محرك التوزيع، ويعرض الحكم (قابلة للتنفيذ/عجز) + الأيام + العملاء + الأصناف + التحذيرات.
    /// </summary>
    private void CheckPlan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_currentPlanId == null || _currentPlanId.Value == 0) { AppContainer.Get<DialogService>().Error("احفظ الخطة أولاً أو اختر خطة من السجل لفحصها."); return; }
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var result = svc.CheckPlan(_currentPlanId.Value);
            var win = new PlanCheckWindow(result) { Owner = Window.GetWindow(this) };
            win.ShowDialog();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Plan.Check"); }
    }

    /// <summary>
    /// ⚖ اقتراح توزيع عادل — معالج آلي بفترة حرة (أسبوع/20 يوماً/شهر...) ووردية إلزامية:
    /// يبني البنود من الأرصدة المتاحة بالتناوب العادل (الأقل إنجازاً أولاً + أقدم الحاويات FIFO)،
    /// يعرض نصيب كل عميل وأيام إنتاجه، ثم يتيح تنزيلها كاملة أو جزئياً بعد التعديل.
    /// </summary>
    private void Fair_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_locked) { AppContainer.Get<DialogService>().Error("الخطة معتمدة ومقفلة."); return; }
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var shifts = db.Shifts.Where(s => s.IsActive).ToList();
            var lines = db.ProductionLines.Where(l => l.IsActive).ToList();
            var products = svc.GetFinishedProducts();

            var start = StartBox.SelectedDate ?? DateTime.Today;
            var end = EndBox.SelectedDate ?? start.AddDays(6);
            var wiz = new FairDistributionWizardWindow(shifts, lines, products, start, end,
                ShiftBox.SelectedIndex >= 0 ? SelectedShiftId() : 1,
                LineBox.SelectedIndex >= 0 ? SelectedLineId() : 1)
            { Owner = Window.GetWindow(this) };
            if (wiz.ShowDialog() != true) return;

            var proposal = svc.SuggestFairDistribution(wiz.FromDate, wiz.ToDate, wiz.ShiftId, wiz.LineId, wiz.TargetProductId, wiz.DailyKg, wiz.ExcludeFriday, wiz.UseCumulative, wiz.CapPerCustomerPerDay, wiz.UseFullDay);
            if (!proposal.Ok || proposal.Rows.Count == 0)
            { AppContainer.Get<DialogService>().Error(proposal.Message ?? "لا توجد نتائج للتوزيع."); return; }

            var summary = new FairSummaryWindow(proposal) { Owner = Window.GetWindow(this) };
            if (summary.ShowDialog() != true) return;
            TypeBox.SelectedIndex = 3;
            if (Core.Common.UiFormat.TryParseDate(wiz.FromDate, out var chosenFrom)) StartBox.SelectedDate = chosenFrom;
            if (Core.Common.UiFormat.TryParseDate(wiz.ToDate, out var chosenTo)) EndBox.SelectedDate = chosenTo;
            ShiftBox.SelectedIndex = _shiftIds.IndexOf(wiz.ShiftId);
            LineBox.SelectedIndex = _lineIds.IndexOf(wiz.LineId);


            // تحويل الاقتراح إلى صفوف قابلة للتحرير في نافذة الاختيار (صنف كامل أو جزء = عدّل الكراتين)
            var packs = db.PackagingTypes.Where(p => p.IsActive).ToList();
            var editorRows = proposal.Rows.Select(r =>
            {
                var er = new LotEditorRow
                {
                    LotId = r.LotId, ShipmentId = r.ShipmentId, ShipmentNo = r.ShipmentNo,
                    LotCode = r.LotCode, CustomerId = r.CustomerId, CustomerName = r.CustomerName,
                    RawName = r.RawName, Available = r.AvailableKg,
                    DaysInStockText = $"{r.DaysInStock} يوماً",
                    PresetDate = r.Date,
                    // §تتبع سحب الخام: سياق سطر الاستلام لدفعة هذا البند.
                    Ctx = svc.GetShipmentQuantityContext(r.LotId),
                    ShiftId = r.ShiftId, ShiftName = r.ShiftName ?? "—",
                    ProductId = r.ProductId, PackId = r.PackagingTypeId,
                    CartonsText = r.PlannedCartons.ToString(),
                    AllProducts = products.Select(ToProductOpt).ToList(),
                    AllPacks = packs.Select(p => new PackOption { Id = p.Id, Name = p.PackageNameAr, UnitWeightKg = p.UnitWeightKg, MoldsCount = p.MoldsCount, MoldWeightKg = p.MoldWeightKg }).ToList()
                };
                var selectedProduct = er.ProductId;
                ConfigureRawSelector(er, db.Lots.AsNoTracking().Where(l => l.Id == r.LotId).Select(l => (int?)l.ProductId).FirstOrDefault(), db, svc);
                if (selectedProduct != null && er.AllProducts.Any(p => p.Id == selectedProduct)) er.ProductId = selectedProduct;
                er.SetContext(); // §يبني خيارات طريقة السحب من وحدة الاستلام الفعلية
                return er;
            }).ToList();

            DatesErp.Core.Common.UiFormat.TryParseDate(wiz.FromDate, out var fairFrom);
            DatesErp.Core.Common.UiFormat.TryParseDate(wiz.ToDate, out var fairTo);
            var fairUnits = products.ToDictionary(x => x.Id, x => string.IsNullOrWhiteSpace(x.UnitOfMeasure) ? "كرتون" : x.UnitOfMeasure);
            var fairCartonWeights = products.ToDictionary(x => x.Id, x => x.CartonWeightKg);
            foreach (var er2 in editorRows) { er2.ProductUnits = fairUnits; er2.ProductCartonWeights = fairCartonWeights; }
            LoadEditorRowTreatment(editorRows, db); // §يثبّت حالة المعالجة (أحمر/قيد) على بنود التوزيع
            var win = new LotsEditorWindow(editorRows,
                "⚖ بنود التوزيع العادل — راجع وعدّل (صنف كامل أو جزء، تاريخ ووردية) ثم أنزل للخطة", false, fairFrom, fairTo,
                shifts, wiz.ShiftId,
                additions => EvaluateDraft(_rows.Select(CapacityItem).Concat(additions).ToList()), wiz.LineId)
            { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() != true || win.Inserted.Count == 0) return;
            InsertEditorRows(win.Inserted, products, packs, wiz.FromDate, wiz.ShiftId, wiz.LineId, db);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Fair"); }
    }

    /// <summary>إنزال الصفوف المدرجة من نافذة الاختيار إلى جدول بنود الخطة (مشترك بين اليدوي والتوزيع العادل).</summary>
    private void InsertEditorRows(List<LotEditorRow> inserted, List<DatesErp.Core.Domain.Entities.Product> products,
        List<DatesErp.Core.Domain.Entities.PackagingType> packs, string fallbackDate, int shiftId, int lineId, DatesErpDbContext db)
    {
        var capacity = EvaluateDraft(_rows.Select(CapacityItem).Concat(inserted.Select(r => LotsEditorWindow.CapacityItem(r, lineId))).ToList());
        if (!capacity.IsValid) { AppContainer.Get<DialogService>().Error(capacity.Error); return; }
        foreach (var row in inserted)
        {
            var pack = packs.FirstOrDefault(p => p.Id == row.PackId);
            _rows.Add(new PlanRowUi
            {
                CustomerId = row.CustomerId,
                CustomerName = row.CustomerName,
                ShipmentId = row.ShipmentId,
                ShipmentNo = row.ShipmentNo,
                LotId = row.LotId,
                RawProductId = row.RawProductId,
                LotCode = row.LotCode,
                RawName = row.RawName,
                ProductId = row.ProductId ?? 0,
                ProductName = products.FirstOrDefault(p => p.Id == row.ProductId)?.ProductNameAr ?? "-",
                PackId = row.PackId,
                PackName = pack?.PackageNameAr ?? "-",
                UnitDisplay = products.FirstOrDefault(p => p.Id == row.ProductId) is var prod3 && prod3 != null && prod3.CartonWeightKg>0 ? $"{(prod3.UnitOfMeasure ?? "كرتون")} ({prod3.CartonWeightKg:N1} كجم)" : (products.FirstOrDefault(p => p.Id == row.ProductId)?.UnitOfMeasure ?? "كرتون"),
                CartonWeight = products.FirstOrDefault(p => p.Id == row.ProductId)?.CartonWeightKg ?? pack?.UnitWeightKg ?? 0,
                QtyKg = row.ComputedKg,
                Cartons = int.TryParse(row.CartonsText, out var c) ? c : 0,
                // §B80: التاريخ من عمود التاريخ في النافذة (إلزامي) ثم السقط المسبق ثم بداية الفترة
                DateValue = row.DateValue
                    ?? (DatesErp.Core.Common.UiFormat.TryParseDate(row.PresetDate, out var pdv) ? pdv : (DateTime?)null)
                    ?? (DatesErp.Core.Common.UiFormat.TryParseDate(fallbackDate, out var fdv) ? fdv : DateTime.Today),
                ShiftId = row.ShiftId ?? shiftId, // §B87: وردية البند من المحرك، أو وردية الشاشة لليدوي
                ShiftName = db.Shifts.AsNoTracking().Where(x => x.Id == (row.ShiftId ?? shiftId)).Select(x => x.ShiftNameAr).FirstOrDefault() ?? "-",
                LineId = lineId,
                LineName = db.ProductionLines.AsNoTracking().Where(x => x.Id == lineId).Select(x => x.LineNameAr).FirstOrDefault() ?? "-",
                // §1.50.66 — تتبع سحب الخام يُنقل من النافذة إلى البند مع وحدة السحب المختارة (سلة/كرتون/كجم)
                SourceUnit = row.SourceUnit ?? row.Ctx?.ReceiptUnit ?? "كجم",
                SourceQtyInUnit = row.SourceQtyInUnit,
                SourceUnitWeightKg = row.SourceUnitWeightKg > 0 ? row.SourceUnitWeightKg : (row.Ctx?.UnitWeightKg ?? 0),
                SourceQtyKg = row.SourceQtyKg,
                SourceModes = row.SourceModes ?? new List<string> { row.Ctx?.ReceiptUnit ?? "كجم", "كجم" }.Distinct().ToList(),
                SourceMode = row.SourceMode ?? row.Ctx?.ReceiptUnit ?? "كجم"
            });
            if (!_planCustomers.Any(x => x.id == row.CustomerId))
                _planCustomers.Add((row.CustomerId, row.CustomerName));
        }
        UpdateCapacityBar();
        RowsGrid.Items.Refresh();
        AppContainer.Get<DialogService>().Info($"تم إنزال ({inserted.Count}) بند إلى الخطة بنجاح.");
    }

    /// <summary>✍️ إضافة يدوي (بدون دفعة).</summary>
    private void Manual_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_locked) { AppContainer.Get<DialogService>().Error("الخطة معتمدة ومقفلة."); return; }
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var customers = db.Customers.Where(c => c.IsActive).ToList();
            var fixedCustomer = SingleRadio.IsChecked == true ? SingleCustBox.SelectedItem as DatesErp.Core.Domain.Entities.Customer : null;
            if (SingleRadio.IsChecked == true && fixedCustomer == null) { AppContainer.Get<DialogService>().Error("اختر العميل أولاً."); return; }
            if (fixedCustomer != null) customers = new() { fixedCustomer };
            string Label(DatesErp.Core.Domain.Entities.Customer c) => $"{c.CustomerName} ({c.CustomerCode})";
            var customerDialog = new Views.EntityFormDialog("عميل البند اليدوي", new List<Views.FieldDef>
            { new() { Key = "customer", LabelAr = "العميل", Kind = "combo", Options = customers.Select(Label).ToArray() } }) { Owner = Window.GetWindow(this) };
            if (customerDialog.ShowDialog() != true) return;
            var customer = customers.FirstOrDefault(c => Label(c) == customerDialog.Values["customer"]?.ToString());
            if (customer == null) return;
            var packs = db.PackagingTypes.Where(p => p.IsActive).ToList();
            var products = svc.GetFinishedProducts();
            var row = new LotEditorRow { CustomerId = customer.Id, CustomerName = customer.CustomerName,
                LotCode = "يدوي — دون دفعة", AllPacks = packs.Select(p => new PackOption { Id = p.Id, Name = p.PackageNameAr,
                    UnitWeightKg = p.UnitWeightKg, MoldsCount = p.MoldsCount, MoldWeightKg = p.MoldWeightKg }).ToList(),
                ProductUnits = products.ToDictionary(p => p.Id, p => p.UnitOfMeasure ?? "كرتون"),
                ProductCartonWeights = products.ToDictionary(p => p.Id, p => p.CartonWeightKg) };
            ConfigureRawSelector(row, null, db, svc);
            var window = new LotsEditorWindow(new() { row }, "اختر الخام ثم الصنف التام المرتبط والكمية", true,
                StartBox.SelectedDate ?? DateTime.Today, EndBox.SelectedDate ?? StartBox.SelectedDate ?? DateTime.Today,
                db.Shifts.Where(s => s.IsActive).ToList(), SelectedShiftId(),
                additions => EvaluateDraft(_rows.Select(CapacityItem).Concat(additions).ToList()), SelectedLineId()) { Owner = Window.GetWindow(this) };
            if (window.ShowDialog() == true && window.Inserted.Count > 0)
                InsertEditorRows(window.Inserted, products, packs, (StartBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy"), SelectedShiftId(), SelectedLineId(), db);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Manual"); }
    }

    /// <summary>§يحمّل حالة المعالجة لصفوف الاختيار (من الدفعة/سطر الاستلام/الدورات الجارية) — يثبّت القيد الأحمر.</summary>
    private static void LoadEditorRowTreatment(List<LotEditorRow> rows, DatesErpDbContext db)
    {
        var lotIds = rows.Where(r => r.LotId != null).Select(r => r.LotId.Value).Distinct().ToList();
        if (lotIds.Count == 0) return;
        var lots = db.Lots.AsNoTracking().Where(l => lotIds.Contains(l.Id))
            .Select(l => new { l.Id, l.ShipmentItemId, l.ProductId, l.UnderTreatmentQtyKg }).ToList();
        var siIds = lots.Select(l => l.ShipmentItemId).Where(s => s != null).Distinct().ToList();
        var shipItems = db.ShipmentItems.AsNoTracking().Where(i => siIds.Contains(i.Id))
            .Select(i => new { i.Id, i.TreatmentRequired, i.TreatmentUntilDate }).ToList();
        var readyDates = db.RawTreatments.AsNoTracking()
            .Where(t => lotIds.Contains(t.LotId) && t.Status == DatesErp.Core.Domain.Entities.TreatmentStatuses.InProgress)
            .GroupBy(t => t.LotId)
            .Select(g => new { LotId = g.Key, Date = g.Max(x => x.ExpectedReadyAt) })
            .ToDictionary(x => x.LotId, x => x.Date);
        foreach (var r in rows)
        {
            var lot = lots.FirstOrDefault(l => l.Id == r.LotId);
            if (lot == null) continue;
            var si = shipItems.FirstOrDefault(i => i.Id == lot.ShipmentItemId);
            r.TreatmentRequired = (si?.TreatmentRequired
                ?? db.Products.AsNoTracking().Where(p => p.Id == lot.ProductId).Select(p => (bool?)p.RequiresTreatment).FirstOrDefault())
                ?? false;
            r.TreatmentUntilDate = si?.TreatmentUntilDate;
            r.TreatmentReadyDate = readyDates.TryGetValue(lot.Id, out var d) ? d : null;
            r.UnderTreatmentKg = lot.UnderTreatmentQtyKg;
            r.Recalc();
        }
    }

    private static void ConfigureRawSelector(LotEditorRow row, int? rawId, DatesErpDbContext db, IPlanningService svc)
    {
        row.RawOptions = db.Products.AsNoTracking().Where(p => p.IsActive && p.ItemType == "Raw")
            .OrderBy(p => p.ProductNameAr).ToList().Select(ToProductOpt).ToList();
        row.LoadFinishedProducts = raw => svc.GetFinishedProductsForRaw(raw).Select(ToProductOpt).ToList();
        row.RawProductId = rawId;
        if (rawId == null) row.ReloadFinishedProducts();
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) { AppContainer.Get<DialogService>().Error("الخطة معتمدة ومقفلة — لا حذف بنود بعد الاعتماد."); return; }
        if (sender is Button b && b.Tag is PlanRowUi r1) _rows.Remove(r1);
        else if (sender is Button b2 && b2.DataContext is PlanRowUi r2) _rows.Remove(r2);
        EnsureEmptyRow();
    }

    // ═══════════════════ الإدخال المباشر من الجدول (1.50.56) ═══════════════════
    private void AddEmptyRow_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) return;
        AddEmptyRow();
    }

    private void AddEmptyRow()
    {
        var row = new PlanRowUi { No = _rows.Count + 1, CustomerName = "— اختر العميل/الدفعة —", LotCode = "—", ProductName = "—", UnitDisplay = "—", Priority = _rows.Count + 1 };
        // يومية: تاريخ البند = تاريخ الخطة الواحد
        if (TypeBox?.SelectedIndex == 0 && StartBox?.SelectedDate != null) row.DateValue = StartBox.SelectedDate;
        else if (StartBox?.SelectedDate != null) row.DateValue = StartBox.SelectedDate;
        _rows.Add(row);
    }

    private void EnsureEmptyRow()
    {
        if (_locked) return;
        if (_rows.Count == 0 || _rows.Last().LotId != null || _rows.Last().ProductId != 0)
            AddEmptyRow();
    }

    private void CustomerName_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) return;
        if (sender is Button b && b.Tag is PlanRowUi row) OpenLotPicker(row);
    }

    private void LotCode_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) return;
        if (sender is Button b && b.Tag is PlanRowUi row) OpenLotPicker(row);
    }

    private void ProductName_Click(object sender, RoutedEventArgs e)
    {
        if (_locked) return;
        if (sender is Button b && b.Tag is PlanRowUi row)
        {
            // اختيار صنف تام من جميع الأصناف التامة معروضة فوراً
            var dlg = new Views.ProductPickerWindow("", "Finished") { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.SelectedProduct != null)
            {
                var p = dlg.SelectedProduct;
                row.ProductId = p.Id;
                row.ProductName = p.ProductNameAr;
                row.UnitDisplay = p.UnitOfMeasure ?? "كرتون";
                row.CartonWeight = p.CartonWeightKg > 0 ? p.CartonWeightKg : 0;
                if (row.CartonWeight <= 0) { AppContainer.Get<DialogService>().Error($"وزن الكرتون غير معرف للصنف «{p.ProductNameAr}» — عرّفه في بطاقة الصنف قبل الإضافة."); return; }
                RowsGrid.Items.Refresh();
                EnsureEmptyRow();
            }
        }
    }

    private void OpenLotPicker(PlanRowUi row)
    {
        try
        {
            var currentIds = _rows.Where(r => r.LotId != null).Select(r => r.LotId.Value).ToList();
            var dlg = new Views.LotPickerWindow(currentIds) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.SelectedLot != null)
            {
                var lot = dlg.SelectedLot;
                row.LotId = lot.LotId;
                row.LotCode = lot.LotCode;
                row.CustomerId = lot.CustomerId;
                row.CustomerName = lot.CustomerName ?? "—";
                row.ShipmentId = lot.ShipmentId;
                row.ShipmentNo = lot.ShipmentNo;
                row.RawProductId = lot.ProductId;
                row.RawName = lot.ProductName;
                row.SourceQtyKg = lot.RemainingKg;
                row.SourceUnit = lot.ReceiptUnit ?? "كجم";
                // إذا لم يكن الصنف التام محدداً، اقترح نفس الخام أو أول تام مرتبط
                if (row.ProductId == 0)
                {
                    row.ProductId = lot.ProductId;
                    row.ProductName = lot.ProductName ?? "—";
                    row.UnitDisplay = lot.ReceiptUnit ?? "كجم";
                }
                row.QtyKg = lot.AvailableForDateKg > 0 ? lot.AvailableForDateKg : lot.RemainingKg;
                if (row.CartonWeight > 0) row.Cartons = (int)Math.Ceiling(row.QtyKg / row.CartonWeight);
                RowsGrid.Items.Refresh();
                UpdateTotals();
                EnsureEmptyRow();
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.PickLotInline"); }
    }

    private void RowsGrid_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        // لا حاجة حالياً — لكن يبقي البنية لتعيين قوائم الورديات/الخطوط مستقبلاً
    }


    private void Renumber()
    {
        int n = 1;
        foreach (var r in _rows) { r.No = n; r.Priority = n; n++; }
    }

    // §1.50.57 — أزرار المدد السريعة أزيلت بناءً على طلب المستخدم (الاكتفاء بتحديد التاريخ). تبقى الدالة للتوافق لكن لا تُستدعى.
    private void Duration_Click(object sender, RoutedEventArgs e) { }

    // ══════════ شريط طاقة الوردية ══════════

    private void CapacityInputs_Changed(object sender, EventArgs e) => UpdateCapacityBar();

    /// <summary>
    /// §إصلاح شامل لشريط الطاقة — كان:
    ///  • يأخذ معدل «صنف أول بند» فقط (والخطة متعددة الأصناف بمعدلات مختلفة)
    ///  • يتجاهل العبوة رغم أن الـ Backend يحسب الطاقة لكل عبوة
    ///  • يجمع كراتين كل الأيام ويقارنها بطاقة يوم واحد
    ///  • يتجاهل وردية كل بند
    ///  • يفبرك rate=500 عند غياب التعريف
    ///  • يبتلع الأخطاء بـ catch { }
    /// الآن: يُحسب لكل (يوم × وردية) بمعدل كل صنف وعبوته — نفس منطق EnsureSlotCapacity في الـ Backend.
    /// </summary>
    // ══════════ الحفظ وسير الاعتماد ══════════

    /// <summary>§B58: تحرير خلية (كراتين/تاريخ/وردية/خط) يُحدّث الطاقة والعدادات فوراً.</summary>
    private void RowUi_Changed(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlanRowUi.CartonsText) or nameof(PlanRowUi.Date) or nameof(PlanRowUi.DateValue)
            or nameof(PlanRowUi.ShiftId) or nameof(PlanRowUi.LineId) or nameof(PlanRowUi.PackId) or nameof(PlanRowUi.QtyKg)
            or nameof(PlanRowUi.QuantityError) or nameof(PlanRowUi.Cartons)
            or nameof(PlanRowUi.SourceMode) or nameof(PlanRowUi.SourceQtyKg) or nameof(PlanRowUi.SourceDisplay))
        { UpdateCapacityBar(); UpdateTotals(); }
    }

    /// <summary>§B58: عدادات الخطة (بنود/كراتين/وزن/عملاء) + تلميح الجدول الفارغ — كمخطط المرجع.</summary>
    private void UpdateTotals()
    {
        if (TotItemsBox == null) return;
        var valid = _rows.Where(r => r.LotId != null || r.ProductId != 0).ToList();
        TotItemsBox.Text = $"البنود: {valid.Count}";
        TotCartonsBox.Text = $"كراتين: {valid.Sum(r => r.Cartons):N0}";
        TotQtyBox.Text = $"الوزن: {valid.Sum(r => r.QtyKg):N1} كجم";
        TotCustsBox.Text = $"عملاء: {valid.Select(r => r.CustomerId).Distinct().Count()}";
        GridHintPlan.Visibility = valid.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>§B58: «من استلام مباشر» — فتح نافذة الدفعات المتاحة بلا عميل محدد.</summary>
    private void DirectReceipt_Click(object sender, RoutedEventArgs e)
    { if (_locked) { AppContainer.Get<DialogService>().Error("الخطة معتمدة ومقفلة."); return; } OpenLotsEditor(null, null); }

    // §1.50.62 — قوالب ونسخ سندات (8)
    private void SaveAsTemplate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_currentPlanId == null) { AppContainer.Get<DialogService>().Error("احفظ الخطة أولاً قبل حفظها كقالب."); return; }
            var dlg = new Views.InputDialog("حفظ كقالب","اسم القالب:", $"قالب - {TitleBox.Text}") { Owner = System.Windows.Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<DatesErp.Core.Interfaces.Services.IPlanningService>();
            // Use PlanningService directly for template methods
            var ps = scope.ServiceProvider.GetRequiredService<DatesErp.Application.Services.PlanningService>();
            var r = ps.SaveAsTemplate(_currentPlanId.Value, dlg.Value);
            if (!r.Ok) AppContainer.Get<DialogService>().Error(r.Message);
            else AppContainer.Get<DialogService>().Info(r.Message);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.SaveAsTemplate"); }
    }
    private void LoadTemplate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var ps = scope.ServiceProvider.GetRequiredService<DatesErp.Application.Services.PlanningService>();
            var templates = ps.GetTemplates();
            if (templates.Count == 0) { AppContainer.Get<DialogService>().Info("لا توجد قوالب محفوظة بعد — احفظ خطة كقالب أولاً."); return; }
            var picker = new Views.TemplatePickerWindow(templates) { Owner = System.Windows.Window.GetWindow(this) };
            if (picker.ShowDialog() != true || picker.SelectedTemplateId == null) return;
            // إذا كانت خطة حالية مفتوحة، نسأل هل نستبدل البنود أم ننشئ جديدة
            if (AppContainer.Get<DialogService>().Confirm("هل تريد إنشاء خطة جديدة من القالب؟\nنعم = إعداد خطة جديدة بالنطاق والورديات، لا = استبدال بنود الخطة التاريخية الحالية"))
            {
                // Legacy templates cannot create a new plan without the required
                // customer/shift setup; preserve templates for historic-plan edits only.
                AppContainer.Get<DialogService>().Info("أنشئ الخطة الجديدة من الإعداد أولًا. لا تُستورد بنود قالب قديم خارج نطاق العملاء والورديات ووصفة الخام.");
                (Window.GetWindow(this) as MainWindow)?.OpenScreen("planning");
                return;
            }
            else
            {
                // استبدال بنود الخطة الحالية ببنود القالب
                var tpl = templates.First(t => t.Id == picker.SelectedTemplateId.Value);
                // تحميل بنود القالب إلى الجدول الحالي
                RowsGrid.ItemsSource = null;
                _rows.Clear();
                // نحتاج إلى تحويل بنود القالب إلى PlanRowUi — نستخدم نفس منطق تحميل الخطة
                using var scope2 = AppContainer.NewScope();
                var db = scope2.ServiceProvider.GetRequiredService<DatesErp.Infrastructure.Persistence.DatesErpDbContext>();
                var tplFull = db.ProductionPlans.Include(p => p.Items).First(p => p.Id == tpl.Id);
                foreach (var it in tplFull.Items)
                {
                    // نستخدم نفس آلية إضافة صف يدوي مع تعبئة من المنتج/الدفعة
                    var prod = db.Products.AsNoTracking().FirstOrDefault(p => p.Id == it.ProductId);
                    if (prod == null) continue;
                    var row = new PlanRowUi { No = _rows.Count + 1, ProductId = prod.Id, ProductName = prod.ProductNameAr, CartonsText = it.PlannedCartons.ToString(), QtyKg = it.PlannedQtyKg, LotId = it.LotId, CustomerId = it.CustomerId };
                    _rows.Add(row);
                }
                RowsGrid.ItemsSource = _rows;
                AppContainer.Get<DialogService>().Info($"تم تحميل {tplFull.Items.Count} بنداً من القالب {tpl.PlanTitle} إلى الخطة الحالية — احفظ الخطة.");
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.LoadTemplate"); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_currentPlanId is null or 0)
            {
                AppContainer.Get<DialogService>().Info("الخطط الجديدة تبدأ بإعداد نطاق العملاء والتواريخ والورديات قبل اختيار الأصناف.");
                (Window.GetWindow(this) as MainWindow)?.OpenScreen("planning");
                return;
            }
            if (_locked) { AppContainer.Get<DialogService>().Error("الخطة معتمدة ومقفلة."); return; }
            RowsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            RowsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            UpdateCapacityBar();
            if (string.IsNullOrWhiteSpace(TitleBox.Text)) { AppContainer.Get<DialogService>().Error("أدخل عنوان الخطة."); return; }
            // §1.50.67 FIX2: عند الضغط على صنف جديد يتشفر الحفظ — تجاهل الصفوف غير المكتملة (كراتين 0) عند الحفظ
            // مثل UpdateCapacityBar: فقط البنود المكتملة (هوية + كراتين>0) تُحفظ، الصف الجديد الفارغ لا يعطل الحفظ
            var allValid = _rows.Where(r => r.LotId != null || r.ProductId != 0).ToList();
            var validRows = allValid.Where(r => r.Cartons > 0 && int.TryParse(r.CartonsText, out var nn) && nn > 0).ToList();
            if (validRows.Count == 0)
            {
                if (allValid.Count > 0) { AppContainer.Get<DialogService>().Error("أكمل كمية الكراتين للبند الجديد (يجب أن تكون >0) أو احذفه — الصفوف الفارغة لا تُحفظ."); return; }
                AppContainer.Get<DialogService>().Error("أضف بنداً واحداً على الأقل — اختر دفعة من الجدول مباشرة."); return;
            }
            // فحص الطاقة لا يعطل الزر؛ بعد اكتمال البيانات يُعرض سببه هنا عند الضغط على حفظ.
            if (!_capacityValid) { AppContainer.Get<DialogService>().Error(RemainingBadge.Text); return; }
            // §B80: فرض تاريخ كل إنتاج — كل بند بتاريخ صالح داخل فترة الخطة (قبل الخلفية أيضاً)
            var perStart = (StartBox.SelectedDate ?? DateTime.Today).Date;
            var perEnd = (EndBox.SelectedDate ?? DateTime.Today).Date;
            foreach (var rowD in validRows)
            {
                if (!Core.Common.UiFormat.TryParseDate(rowD.Date, out var rdD))
                { AppContainer.Get<DialogService>().Error($"البند ({rowD.No}) «{rowD.ProductName}» بلا تاريخ إنتاج — حدّد تاريخ كل بند في عمود «تاريخ الإنتاج»."); return; }
                if (rdD.Date < perStart || rdD.Date > perEnd)
                { AppContainer.Get<DialogService>().Error($"تاريخ البند ({rowD.No}) «{rowD.ProductName}» ({rowD.Date}) خارج فترة الخطة ({perStart:dd/MM/yyyy} ← {perEnd:dd/MM/yyyy})."); return; }
            }

            string ptype = TypeBox.SelectedIndex switch { 0 => "Daily", 1 => "Weekly", 2 => "Monthly", _ => "Period" };
            // §B75: النطاق والعميل المحدد يُحفظان في رأس الخطة
            string scopeMode = SingleRadio.IsChecked == true ? "Single" : "Multi";
            int? singleCustId = scopeMode == "Single"
                ? (SingleCustBox.SelectedItem as DatesErp.Core.Domain.Entities.Customer)?.Id
                : null;
            using var scope = AppContainer.NewScope();
            var svc = (IPlanningService)scope.ServiceProvider.GetService(typeof(IPlanningService));
            var itemsDto = validRows.Select(row => new PlanItemDto
            {
                SourceType = row.LotId != null ? "FromReceiving" : "Manual",
                LotId = row.LotId,
                SelectedRawProductId = row.RawProductId,
                ShipmentId = row.ShipmentId,
                CustomerId = row.CustomerId,
                ProductId = row.ProductId,
                PackagingTypeId = row.PackId,
                PlannedQtyKg = row.QtyKg,
                PlannedCartons = row.Cartons,
                ScheduledDate = row.Date,
                SuggestedShiftId = row.ShiftId,
                SuggestedLineId = row.LineId,
                PriorityNo = row.Priority,
                SourceUnit = row.SourceUnit,
                SourceQtyInUnit = row.SourceQtyInUnit,
                SourceUnitWeightKg = row.SourceUnitWeightKg,
                SourceQtyKg = row.SourceQtyKg
            }).ToList();

            // §تعديل خطة قائمة (مسودة) بدل إنشاء نسخة مكررة — الحفظ يعمل كحفظ وتحديث معاً
            OpResult r = _currentPlanId.HasValue && _currentPlanId.Value > 0
                ? svc.UpdatePlan(_currentPlanId.Value, TitleBox.Text, ptype,
                    (StartBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy"),
                    (EndBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy"),
                    SelectedShiftId(), SelectedLineId(), itemsDto, NotesBox.Text, scopeMode, singleCustId)
                : svc.SavePlan(TitleBox.Text, ptype,
                    (StartBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy"),
                    (EndBox.SelectedDate ?? DateTime.Today).ToString("dd/MM/yyyy"),
                    SelectedShiftId(), SelectedLineId(), itemsDto, NotesBox.Text, scopeMode, singleCustId);

            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            _currentPlanId = r.Id;
            CodeBox.Text = r.DocumentNumber;
            FillPlanMeta();
            AppContainer.Get<DialogService>().Info(r.Message + "\nحُفظت البنود كما أدخلتها (عميل/شحنة/دفعة/صنف/عبوة).\nأرسلها للاعتماد للمدير العام عند الجاهزية.");
            SetStatusUI("Draft");
            RefreshPlansList();
            // §B108: إعادة تحميل البنود من القاعدة بعد الحفظ — الحفظ يعيد بناء البنود،
            // فمعرفاتها في الذاكرة تصبح قديمة، و«تعديل البند» يعتمد عليها.
            if (_currentPlanId.HasValue && _currentPlanId.Value > 0) OpenPlan(_currentPlanId.Value);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Save"); }
    }

    private void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlanId == null || _currentPlanId.Value == 0) { AppContainer.Get<DialogService>().Error("احفظ الخطة أولاً."); return; }
        using var scope = AppContainer.NewScope();
        var svc = (IPlanningService)scope.ServiceProvider.GetService(typeof(IPlanningService));
        var r = svc.SubmitPlan(_currentPlanId.Value);
        if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
        AppContainer.Get<DialogService>().Info(r.Message);
        SetStatusUI("UnderApproval");
        RefreshPlansList();
    }

    /// <summary>§إصلاح: زر «اعتماد الخطة» داخل الشبكة — يحفظ أولاً إن لزم ثم يعتمد.</summary>
    private void ApproveAction_Click(object sender, RoutedEventArgs e)
    {
        // §B58: «تعليق» = حفظ مسودة بلا اعتماد؛ «اعتماد» = حفظ ثم اعتماد/إرسال
        if (HoldRadio.IsChecked == true) { Save_Click(sender, e); return; }
        ApproveAction_ClickCore(sender, e);
    }

    /// <summary>
    /// §1.50.71 P1: بصمة حالة الخطة كما ستُحفظ (رأس + البنود الصالحة فقط) — تكشف تعديلات
    /// غير محفوظة بعد آخر حفظ/تحميل قبل الاعتماد (حارس فقدان البيانات). الصفوف الفارغة
    /// (بلا هوية أو كراتين=0) مستثناة لأن Save_Click يتجاهلها أصلاً فلا تُعد «تعديلاً».
    /// </summary>
    private string PlanFingerprint()
    {
        var parts = new List<string>
        {
            TitleBox?.Text ?? "",
            (StartBox?.SelectedDate ?? DateTime.MinValue).ToString("yyyy-MM-dd"),
            (EndBox?.SelectedDate ?? DateTime.MinValue).ToString("yyyy-MM-dd"),
            TypeBox?.SelectedIndex.ToString() ?? "",
            SingleRadio?.IsChecked == true ? "Single" : "Multi",
            NotesBox?.Text ?? ""
        };
        foreach (var r in _rows)
        {
            if (r.LotId == null && r.ProductId == 0) continue;   // بلا هوية — لن يُحفظ
            if (!(r.Cartons > 0 && int.TryParse(r.CartonsText, out var nn) && nn > 0)) continue; // كراتين صفر — Save_Click يتخطاها
            parts.Add($"R|{r.LotId}|{r.RawProductId}|{r.ShipmentId}|{r.CustomerId}|{r.ProductId}|{r.PackId}|" +
                      $"{nn}|{r.QtyKg:F3}|{r.Date}|{r.ShiftId}|{r.LineId}|{r.Priority}|{r.SourceUnit}|" +
                      $"{r.SourceQtyInUnit:F3}|{r.SourceUnitWeightKg:F3}|{r.SourceQtyKg:F3}");
        }
        return string.Join("‡", parts);
    }

    private void ApproveAction_ClickCore(object sender, RoutedEventArgs e)
    {
        if (_locked) { AppContainer.Get<DialogService>().Error("الخطة معتمدة ومقفلة."); return; }
        if (_currentPlanId == null || _currentPlanId.Value == 0)
        {
            if (!AppContainer.Get<DialogService>().Confirm("الخطة غير محفوظة بعد. هل تريد حفظها ثم اعتمادها؟")) return;
            Save_Click(sender, e);
            if (_currentPlanId == null || _currentPlanId.Value == 0) return;   // الحفظ فشل — رسالة الخطأ ظهرت بالفعل
        }
        Approve();
    }

    private void Approve()
    {
        try
        {
            if (_currentPlanId == null || _currentPlanId.Value == 0) { AppContainer.Get<DialogService>().Error("احفظ الخطة أولاً أو اختر خطة من السجل."); return; }
            // §1.50.71 P1: خطة محفوظة لكن فيها تعديلات بعد آخر حفظ (رأس/فترة/بنود) → احفظها قبل الاعتماد.
            // هنا (في Approve نفسه) لا في زر بعينه: يغلق مساري الاعتماد (زر الشاشة وشريط الأدوات).
            if (PlanFingerprint() != _savedPlanFingerprint)
            {
                if (!AppContainer.Get<DialogService>().Confirm("في الخطة تعديلات غير محفوظة منذ آخر حفظ. ستُحفظ قبل الاعتماد — متابعة؟")) return;
                Save_Click(null, null);
                if (PlanFingerprint() != _savedPlanFingerprint) return;   // الحفظ فشل (رسالة الخطأ ظهرت بالفعل)
            }
            if (!AppContainer.Get<DialogService>().Confirm("اعتماد الخطة رسمياً ونقلها لأوامر التشغيل؟")) return;
            using var scope = AppContainer.NewScope();
            var svc = (IPlanningService)scope.ServiceProvider.GetService(typeof(IPlanningService));
            var r = svc.ApprovePlan(_currentPlanId.Value);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            SetLocked(true);
            SetStatusUI("Approved");
            RefreshPlansList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Approve"); }
    }

    private void ReturnForRevision_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlanId == null || _currentPlanId.Value == 0) return;
        var dlg = new Views.InputDialog("إعادة الخطة للمدير التنفيذي للتعديل", "سبب الإعادة / الملاحظات:") { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;
        using var scope = AppContainer.NewScope();
        var svc = (IPlanningService)scope.ServiceProvider.GetService(typeof(IPlanningService));
        var r = svc.ReturnPlan(_currentPlanId.Value, dlg.Value);
        if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
        AppContainer.Get<DialogService>().Info(r.Message);
        SetLocked(false);
        SetStatusUI("RevisionRequired");
        RefreshPlansList();
    }

    private void Unapprove()
    {
        if (_currentPlanId == null || _currentPlanId.Value == 0) return;
        if (!AppContainer.Get<DialogService>().Confirm("إلغاء الاعتماد وإعادة فتح الخطة للتعديل؟")) return;
        using var scope = AppContainer.NewScope();
        var svc = (IPlanningService)scope.ServiceProvider.GetService(typeof(IPlanningService));
        var r = svc.UnapprovePlan(_currentPlanId.Value);
        if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
        AppContainer.Get<DialogService>().Info(r.Message);
        SetLocked(false);
        SetStatusUI("Draft");
        RefreshPlansList();
    }

    private void DeletePlan()
    {
        if (_currentPlanId == null || _currentPlanId.Value == 0) { AppContainer.Get<DialogService>().Error("لا توجد خطة محددة."); return; }
        if (!AppContainer.Get<DialogService>().Confirm("حذف الخطة (المسودة)؟")) return;
        using var scope = AppContainer.NewScope();
        var svc = (IPlanningService)scope.ServiceProvider.GetService(typeof(IPlanningService));
        var r = svc.DeletePlan(_currentPlanId.Value);
        if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
        AppContainer.Get<DialogService>().Info(r.Message);
        NewPlan();
        RefreshPlansList();
    }

    /// <summary>
    /// §إصلاح — تراجع بوظيفتين حسب المعيار المعلن («جديد ← إفراغ؛ محفوظ ← إعادة آخر نسخة محفوظة — لا حذف أبداً»).
    /// كان يمسح البنود دائماً، فيفقد المستخدم خطة محفوظة من العرض بدل استعادتها.
    /// </summary>
    private void UndoInput()
    {
        if (_currentPlanId.HasValue && _currentPlanId.Value > 0)
        {
            OpenPlan(_currentPlanId.Value);   // استعادة آخر نسخة محفوظة
            AppContainer.Get<DialogService>().Info("أُعيدت آخر نسخة محفوظة من الخطة.");
            return;
        }
        _rows.Clear();
        NotesBox.Text = "";
        UpdateCapacityBar();
    }

    private void NewPlan()
    {
        _programmaticScope = true;
        try
        {
            _currentPlanId = 0;
            _rows.Clear();
            _planCustomers.Clear();
            try
            {
                using var _numScope = AppContainer.NewScope();
                var _num = _numScope.ServiceProvider.GetRequiredService<INumberingService>().Peek("PLAN");
                CodeBox.Text = _num;
            }
            catch { CodeBox.Text = "PLN-تلقائي"; }
            TitleBox.Text = "";
            NotesBox.Text = "";
            StartBox.SelectedDate = EndBox.SelectedDate = null;
            MultiRadio.IsChecked = true;
            SetLocked(false);
            SetStatusUI("Draft");
            TypeBox_Changed(null, null); // إعادة تطبيق قاعدة «اليومية = تاريخ واحد»
            UpdateCapacityBar();
            AddEmptyRow();
            _savedPlanFingerprint = PlanFingerprint();   // §1.50.71 P1: خط الأساس لخطة جديدة (فارغة)
        }
        finally { _programmaticScope = false; }
    }

    // ══════════ الحالة والقفل ══════════

    private void SetStatusUI(string status)
    {
        string text; System.Windows.Media.Color fg, bg, bd;
        switch (status)
        {
            case "Approved":
                text = "معتمدة ومجدولة 🟢 (مقفل 🔒)";
                fg = System.Windows.Media.Color.FromRgb(0x15, 0x80, 0x3D);
                bg = System.Windows.Media.Color.FromRgb(0xDC, 0xFC, 0xE7);
                bd = System.Windows.Media.Color.FromRgb(0x86, 0xEF, 0xAC);
                break;
            case "UnderApproval":
                text = "بانتظار اعتماد المدير العام ⏳";
                fg = System.Windows.Media.Color.FromRgb(0x92, 0x40, 0x0E);
                bg = System.Windows.Media.Color.FromRgb(0xFE, 0xF3, 0xC7);
                bd = System.Windows.Media.Color.FromRgb(0xFC, 0xD3, 0x4D);
                break;
            case "RevisionRequired":
                text = "معادة للتعديل من المدير العام ↩️";
                fg = System.Windows.Media.Color.FromRgb(0x99, 0x1B, 0x1B);
                bg = System.Windows.Media.Color.FromRgb(0xFE, 0xE2, 0xE2);
                bd = System.Windows.Media.Color.FromRgb(0xFC, 0xA5, 0xA5);
                break;
            default:
                text = "مسودة قيد الإعداد 📝";
                fg = System.Windows.Media.Color.FromRgb(0x03, 0x69, 0xA1);
                bg = System.Windows.Media.Color.FromRgb(0xE0, 0xF2, 0xFE);
                bd = System.Windows.Media.Color.FromRgb(0xBA, 0xE6, 0xFD);
                break;
        }
        StatusText.Text = text;
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(fg);
        StatusBanner.Background = new System.Windows.Media.SolidColorBrush(bg);
        StatusBanner.BorderBrush = new System.Windows.Media.SolidColorBrush(bd);

        // مسار المعاملة
        SetStep(Step1, status is "Draft" or "RevisionRequired");
        SetStep(Step2, status == "UnderApproval" || status == "Approved");
        SetStep(Step3, status == "Approved");
        SetStep(Step4, false);

        SubmitBtn.Visibility = status is "Draft" or "RevisionRequired" ? Visibility.Visible : Visibility.Collapsed;
        ReturnBtn.Visibility = status == "UnderApproval" ? Visibility.Visible : Visibility.Collapsed;
        if (_toolbar != null && _toolbar.UnapproveBtn != null)
            _toolbar.UnapproveBtn.Visibility = status == "Approved" ? Visibility.Visible : Visibility.Collapsed;
        // §إصلاح: زر الاعتماد يظهر ما دامت الخطة غير معتمدة — كان مخفياً دائماً فلا اعتماد من الشاشة
        // §1.50.73: زر الاعتماد في شريط الأدوات يبقى مخفياً دائماً — الاعتماد فعل واحد
        // في مكان واحد: زر «اعتماد الخطة» في بطاقة الأزرار (يدير مساري اعتماد/تعليق
        // عبر الراديو + الحارس). شريط الأدوات كان يعرض نسخة ثانية منه = زران متكرران.

        if (_toolbar != null && _toolbar.DeleteBtn != null)
            _toolbar.DeleteBtn.Visibility = status == "Approved" ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void SetStep(Border step, bool active)
    {
        step.Background = new System.Windows.Media.SolidColorBrush(active
            ? System.Windows.Media.Color.FromRgb(0xDC, 0xFC, 0xE7)
            : System.Windows.Media.Color.FromRgb(0xE2, 0xE8, 0xF0));
        step.BorderBrush = new System.Windows.Media.SolidColorBrush(active
            ? System.Windows.Media.Color.FromRgb(0x86, 0xEF, 0xAC)
            : System.Windows.Media.Colors.Transparent);
        if (step.Child is TextBlock tb)
            tb.Foreground = new System.Windows.Media.SolidColorBrush(active
                ? System.Windows.Media.Color.FromRgb(0x15, 0x80, 0x3D)
                : System.Windows.Media.Color.FromRgb(0x64, 0x74, 0x8B));
    }

    private string PlanTypeKey() => TypeBox.SelectedIndex switch { 0 => "Daily", 1 => "Weekly", 2 => "Monthly", _ => "Period" };

    /// <summary>§B75: عرض منشئ الخطة ومعتمدها وتاريخيهما في الرأس — بيانات محفوظة تُقرأ حية.</summary>
    private void FillPlanMeta()
    {
        if (PlanMetaBox == null) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            if (!_currentPlanId.HasValue) { PlanMetaBox.Text = "خطة جديدة — لم تُحفظ بعد · أنشأها: — · اعتمدها: —"; return; }
            var plan = db.ProductionPlans.AsNoTracking().FirstOrDefault(pl => pl.Id == _currentPlanId.Value);
            if (plan == null) { PlanMetaBox.Text = "خطة جديدة — لم تُحفظ بعد · أنشأها: — · اعتمدها: —"; return; }
            string NameOf(int? uid) => uid == null ? "—"
                : db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.FullName).FirstOrDefault() ?? "—";
            PlanMetaBox.Text = $"أنشأها: {NameOf(plan.CreatedBy)} في {plan.CreatedDate:dd/MM/yyyy HH:mm}" +
                               (plan.IsApproved ? $" · اعتمدها: {NameOf(plan.ApprovedBy)} في {plan.ApprovedDate:dd/MM/yyyy HH:mm}" : " · لم تُعتمد بعد");
        }
        catch { }
    }

    private void SetLocked(bool locked)
    {
        _locked = locked;
        StartBox.IsEnabled = !locked;
        EndBox.IsEnabled = !locked;
        TypeBox.IsEnabled = !locked;
        TitleBox.IsEnabled = !locked;
        NotesBox.IsEnabled = !locked;
        ShiftBox.IsEnabled = !locked;
        LineBox.IsEnabled = !locked;
        MultiRadio.IsEnabled = !locked;
        SingleRadio.IsEnabled = !locked;
        SingleCustBox.IsEnabled = !locked;
        RowsGrid.IsReadOnly = locked;
        if (InsertActionsPanel != null) InsertActionsPanel.IsEnabled = !locked;
        if (EditRowBtn != null) EditRowBtn.IsEnabled = !locked;
        if (HoldRadio != null) HoldRadio.IsEnabled = !locked;
        if (ApproveRadio != null) ApproveRadio.IsEnabled = !locked;
        if (_toolbar != null)
        {
            // §FIX 1.50.73: القفل وحده يتحكم في تفعيل الحفظ؛ التحقق يتم عند الضغط.
            if (_toolbar.SaveBtn != null) _toolbar.SaveBtn.IsEnabled = !locked;
            if (_toolbar.NewBtn != null) _toolbar.NewBtn.IsEnabled = true; // خطة جديدة دائماً متاحة
            if (_toolbar.ApproveBtn != null) _toolbar.ApproveBtn.IsEnabled = !locked;
            if (_toolbar.DeleteBtn != null) _toolbar.DeleteBtn.IsEnabled = !locked;
        }
        // §FIX 1.50.73: نفس القاعدة لزر الحفظ الرئيسي في الشاشة.
        if (SaveActionBtn != null) SaveActionBtn.IsEnabled = !locked;
        if (ApproveActionBtn != null) ApproveActionBtn.IsEnabled = !locked;
        if (SubmitBtn != null) SubmitBtn.IsEnabled = !locked;
    }

    // ══════════ التنقل والسجل ══════════

    private void Nav(int dir)
    {
        if (_planIds.Count == 0) return;
        int idx = _currentPlanId.HasValue ? _planIds.IndexOf(_currentPlanId.Value) : -1;
        idx = dir switch { 0 => 0, int.MaxValue => _planIds.Count - 1, _ => Math.Clamp(idx + dir, 0, _planIds.Count - 1) };
        OpenPlan(_planIds[idx]);
    }

    private void OpenPlan(int id)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var plan = db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == id);
            if (plan == null) return;
            if (db.ProductionPlanContexts.AsNoTracking().Any(c => c.PlanId == id))
            {
                (Window.GetWindow(this) as MainWindow)?.OpenPlanById(id);
                return; // Never edit a sequenced card in the historic all-items editor.
            }
            _currentPlanId = plan.Id;
            CodeBox.Text = plan.DocumentNumber;
            TitleBox.Text = plan.PlanTitle;
            NotesBox.Text = plan.Notes;
            StartBox.SelectedDate = plan.StartDate;
            EndBox.SelectedDate = plan.EndDate;
            TypeBox.SelectedIndex = plan.PlanType switch { "Daily" => 0, "Weekly" => 1, "Monthly" => 2, _ => 3 };
            if (plan.ShiftId != null) { int si = _shiftIds.IndexOf(plan.ShiftId.Value); if (si >= 0) ShiftBox.SelectedIndex = si; }
            if (plan.LineId != null) { int li = _lineIds.IndexOf(plan.LineId.Value); if (li >= 0) LineBox.SelectedIndex = li; }
            // §B75: استعادة نطاق التخطيط والعميل المحدد من الرأس
            // §B106.2 — الحارس _programmaticScope كان يُفعَّل لاحقاً في هذه الدالة فقط،
            // بينما إسنادُ SingleCustBox.SelectedIndex هنا يُطلق SelectionChanged فوراً،
            // فيستدعي SingleCust_Changed -> OpenLotsEditor: تُفتح نافذة «أصناف وشحنات
            // العميل» فوق الخطة بمجرد النقر المزدوج عليها لعرضها. الفتح استعراضٌ لا
            // إدخال، فالحارس يلزم حول الإسناد نفسه.
            _programmaticScope = true;
            try
            {
                if (plan.ScopeMode == "Single") SingleRadio.IsChecked = true; else MultiRadio.IsChecked = true;
                if (plan.SingleCustomerId != null)
                    for (int ci = 0; ci < SingleCustBox.Items.Count; ci++)
                        if ((SingleCustBox.Items[ci] as DatesErp.Core.Domain.Entities.Customer)?.Id == plan.SingleCustomerId) { SingleCustBox.SelectedIndex = ci; break; }
            }
            finally { _programmaticScope = false; }
            FillPlanMeta();

            _rows.Clear();
            foreach (var it in plan.Items.OrderBy(i => i.PriorityNo))
            {
                _rows.Add(new PlanRowUi
                {
                    ItemId = it.Id,   // §B108: يتيح تعديل البند المحفوظ من الجدول الرئيسي
                    CustomerId = it.CustomerId,
                    CustomerName = db.Customers.Where(c => c.Id == it.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "—",
                    ShipmentId = it.ShipmentId,
                    ShipmentNo = db.Shipments.Where(s => s.Id == it.ShipmentId).Select(s => s.DocumentNumber).FirstOrDefault() ?? "—",
                    LotId = it.LotId,
                    LotCode = db.Lots.Where(l => l.Id == it.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "—",
                    RawName = db.Lots.Where(l => l.Id == it.LotId).Join(db.Products, l => l.ProductId, p => p.Id, (l, p) => p.ProductNameAr).FirstOrDefault() ?? "—",
                    ProductId = it.ProductId,
                    ProductName = db.Products.Where(p => p.Id == it.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                    PackId = it.PackagingTypeId,
                    PackName = db.PackagingTypes.Where(p => p.Id == it.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault() ?? "-",
                    UnitDisplay = db.Products.AsNoTracking().Where(pp => pp.Id == it.ProductId).Select(pp => pp.UnitOfMeasure).FirstOrDefault() ?? "—",
                    CartonWeight = db.Products.AsNoTracking().Where(pp => pp.Id == it.ProductId).Select(pp => pp.CartonWeightKg).FirstOrDefault(),
                    QtyKg = it.PlannedQtyKg,
                    Cartons = it.PlannedCartons,
                    DateValue = it.ScheduledDate,
                    ShiftId = it.SuggestedShiftId ?? 1,
                    ShiftName = db.Shifts.Where(s => s.Id == (it.SuggestedShiftId ?? 1)).Select(s => s.ShiftNameAr).FirstOrDefault() ?? "-",
                    LineId = it.SuggestedLineId ?? 1,
                    LineName = db.ProductionLines.AsNoTracking().Where(x => x.Id == (it.SuggestedLineId ?? 1)).Select(x => x.LineNameAr).FirstOrDefault() ?? "-",
                    Priority = it.PriorityNo,
                    // §1.50.66 — استعادة التتبع + بناء SourceModes من وحدة السحب المحفوظة
                    SourceUnit = it.SourceUnit ?? "كجم",
                    SourceQtyInUnit = it.SourceQtyInUnit,
                    SourceUnitWeightKg = it.SourceUnitWeightKg,
                    SourceQtyKg = it.SourceQtyKg,
                    SourceModes = new List<string> { it.SourceUnit ?? "كجم", "كجم" }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList(),
                    SourceMode = it.SourceUnit ?? "كجم"
                });
            }
            // نطاق الخطة: عميل واحد ← يُخفى عمود العميل (محفوظ في رأس النموذج) | عدة عملاء ← يظهر العمود
            // (الحارس يمنع الفتح التلقائي للنافذة أثناء الاسترجاع البرمجي)
            _programmaticScope = true;
            try
            {
                var custIds = plan.Items.Where(i => i.CustomerId != null).Select(i => i.CustomerId).Distinct().ToList();
                if (custIds.Count <= 1)
                {
                    SingleRadio.IsChecked = true;
                    if (custIds.Count == 1)
                    {
                        RefreshCustomerList(); // نضمن وجود القائمة قبل ضبط القيمة
                        SingleCustBox.SelectedValue = custIds[0];
                    }
                }
                else MultiRadio.IsChecked = true;
            }
            finally { _programmaticScope = false; }

            Renumber();
            RowsGrid.Items.Refresh();
            SetLocked(plan.IsApproved);
            SetStatusUI(plan.IsApproved ? "Approved" : plan.Status == "UnderApproval" ? "UnderApproval" : plan.Status == "RevisionRequired" ? "RevisionRequired" : "Draft");
            UpdateCapacityBar();
            if (!plan.IsApproved) EnsureEmptyRow();
            _savedPlanFingerprint = PlanFingerprint();   // §1.50.71 P1: خط الأساس بعد كل تحميل (ما في الشاشة = ما في القاعدة)
            // §توحيد الواجهات: عند فتح خطة من البحث، ابدأ العرض من أعلى النموذج ليكون متماسكاً غير منقسم
            // §لم يعد هناك تمرير رأسي — الشاشة كلها معروضة فلا حاجة للعودة للأعلى
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Open"); }
    }

    private void RefreshPlansList()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            // §كفاءة (v1.50.20): تجميع واحد بدل 3N استعلامات — إحصاءات كل الخطط في
            // استعلام GROUP BY وحيد، ثم ربطها بالخطط في الذاكرة (قوائم الخطط تُحمَّل
            // في كل فتح للنافذة المنبثقة، وأصبح المقياس استعلامان مهما كبر عدد الخطط).
            var stats = db.ProductionPlanItems.AsNoTracking()
                .GroupBy(i => i.PlanId)
                .Select(g => new
                {
                    PlanId = g.Key,
                    Customers = g.Select(x => x.CustomerId).Distinct().Count(),
                    Items = g.Count(),
                    Qty = g.Sum(x => x.PlannedQtyKg)
                })
                .ToDictionary(x => x.PlanId);
            var list = db.ProductionPlans.AsNoTracking().OrderByDescending(p => p.Id).ToList();
            _planIds = list.Select(p => p.Id).ToList();
            _plans_all = list.Select(p =>
            {
                stats.TryGetValue(p.Id, out var s);
                return new Views.PlanSearchWindow.PlanSearchItem
                {
                    Id = p.Id,
                    DocNo = p.DocumentNumber,
                    Title = p.PlanTitle,
                    Period = $"{p.StartDate:dd/MM/yyyy} إلى {p.EndDate:dd/MM/yyyy}",
                    Customers = s?.Customers ?? 0,
                    Items = s?.Items ?? 0,
                    Qty = s?.Qty ?? 0,
                    StatusAr = p.IsApproved ? "معتمدة 🟢" : p.Status == "UnderApproval" ? "بانتظار الاعتماد ⏳" : p.Status == "RevisionRequired" ? "معادة للتعديل ↩️" : "مسودة 📝"
                };
            }).ToList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.List"); }
    }

    /// <summary>
    /// §شاشة الخطط الموحّدة: البحث عن الخطط المحفوظة في <b>نافذة منبثقة</b>
    /// (بدل المستطيل الدائم الذي كان يأكل ارتفاع الشاشة في كل لحظة).
    /// النقر المزدوج على خطة في المنبثق يُنزل بجميع بنودها إلى هذه الواجهة عبر <see cref="OpenPlan"/>.
    /// </summary>
    private void OpenPlansSearch()
    {
        try
        {
            RefreshPlansList();
            if (_plans_all.Count == 0)
            {
                AppContainer.Get<DialogService>().Info("لا توجد خطط محفوظة بعد — أنشئ خطة ثم احفظها.");
                return;
            }
            var win = new Views.PlanSearchWindow(_plans_all) { Owner = Window.GetWindow(this) };
            if (win.ShowDialog() == true && win.SelectedPlanId is int id) OpenPlan(id);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Search"); }
    }

    // ══════════ تعديل البند المحفوظ ══════════

    /// <summary>
    /// §B108 — تعديل بند خطة **محفوظ** (تاريخ/كمية/وردية/صنف/عبوة) مع إعادة فحص الطاقة في الخدمة.
    ///
    /// كان هذا الأمر معلّقاً على لوحة «بنود اليوم» المحذوفة، وهي وحدها التي كانت تحمل
    /// معرّف البند. صار الآن على **الجدول الرئيسي** مباشرةً عبر <c>PlanRowUi.ItemId</c>:
    /// نفس الوظيفة، في المكان الذي ينظر إليه الموظف أصلاً، وبنقرة مزدوجة على الصف.
    ///
    /// يعمل على الخطة المعتمدة أيضاً — وهذا مقصود: الجدول نفسه يُقفل بعد الاعتماد،
    /// و<c>UpdatePlanItem</c> هو المسار الوحيد الذي يفرض حراس التعديل بعد التنفيذ
    /// (صلاحية «إلغاء/استثناء» للبنود المنفَّذة، وحدود فترة الخطة، وفحص الطاقة).
    /// </summary>
    private void EditRow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_locked) { AppContainer.Get<DialogService>().Error("الخطة معتمدة ومقفلة — لا تعديل إلا بعد فك الاعتماد."); return; }
            if (RowsGrid.SelectedItem is not PlanRowUi row)
            { AppContainer.Get<DialogService>().Error("اختر بنداً من الجدول أولاً."); return; }

            // §الصف غير المحفوظ يُعدَّل في الجدول مباشرةً — لا معرف له في القاعدة بعد
            if (row.ItemId <= 0)
            {
                AppContainer.Get<DialogService>().Info(
                    "هذا البند لم يُحفظ بعد — عدّله مباشرةً في الجدول (الكراتين/التاريخ) ثم احفظ الخطة.");
                return;
            }

            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var rawId = row.LotId != null
                ? db.Lots.AsNoTracking().Where(l => l.Id == row.LotId).Select(l => (int?)l.ProductId).FirstOrDefault()
                : db.Products.AsNoTracking().Where(p => p.Id == row.ProductId).Select(p => p.SourceProductId).FirstOrDefault();
            var products = rawId == null ? new List<DatesErp.Core.Domain.Entities.Product>()
                : scope.ServiceProvider.GetRequiredService<IPlanningService>().GetFinishedProductsForRaw(rawId.Value);
            if (products.Count == 0) { AppContainer.Get<DialogService>().Error("لا توجد أصناف تامة مرتبطة بهذا الصنف الخام."); return; }
            var packs = db.PackagingTypes.Where(p => p.IsActive).ToList();
            var fields = new List<Views.FieldDef>
            {
                new() { Key = "date", LabelAr = "التاريخ الجديد (dd/MM/yyyy)", Default = row.Date },
                new() { Key = "qty", LabelAr = "الكمية الجديدة (كجم)", Default = row.QtyKg.ToString("0.###") },
                new() { Key = "shift", LabelAr = "رقم الوردية", Default = row.ShiftId.ToString() },
                new() { Key = "product", LabelAr = "الصنف التام", Kind = "combo", Options = products.Select(p => p.ProductNameAr).ToArray(), Default = row.ProductName },
                new() { Key = "pack", LabelAr = "العبوة", Kind = "combo", Options = packs.Select(p => p.PackageNameAr).ToArray(), Default = row.PackName }
            };
            var dlg = new Views.EntityFormDialog($"تعديل البند — {row.CustomerName} / {row.ProductName}", fields) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;

            double? qty = null;
            if (double.TryParse(dlg.Values["qty"]?.ToString(), out var q) && q > 0) qty = q;
            int? shift = null;
            if (int.TryParse(dlg.Values["shift"]?.ToString(), out var sh) && sh > 0) shift = sh;
            // تغيير الصنف/العبوة: يُرسل المعرف الجديد فقط إن غيّر المستخدم الاختيار فعلياً
            int? newProductId = null; int? newPackId = null;
            var prodSel = dlg.Values["product"]?.ToString();
            if (!string.IsNullOrEmpty(prodSel) && prodSel != row.ProductName)
                newProductId = products.FirstOrDefault(p => p.ProductNameAr == prodSel)?.Id;
            var packSel = dlg.Values["pack"]?.ToString();
            if (!string.IsNullOrEmpty(packSel) && packSel != row.PackName)
                newPackId = packs.FirstOrDefault(p => p.PackageNameAr == packSel)?.Id;

            var svc = (IPlanProgressService)scope.ServiceProvider.GetService(typeof(IPlanProgressService));
            var r = svc.UpdatePlanItem(row.ItemId, dlg.Values["date"]?.ToString(), qty, shift, null, newProductId, newPackId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);

            // §إعادة تحميل الخطة من القاعدة: التعديل تم في الخدمة، والجدول يجب أن يعكس
            // ما حُفظ فعلاً (الكراتين المشتقة قد تختلف عمّا أدخله المستخدم) لا ما ظنه.
            if (_currentPlanId.HasValue && _currentPlanId.Value > 0) OpenPlan(_currentPlanId.Value);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.EditRow"); }
    }

    /// <summary>
    /// §B108 — نقر مزدوج على صف الجدول الرئيسي يفتح تعديل البند المحفوظ.
    ///
    /// يتجاهل النقر على خلية قابلة للتحرير (الكراتين/التاريخ) كي لا يسرق النافذةُ
    /// التحريرَ المباشر داخل الجدول، ويتجاهل الصف غير المحفوظ (ItemId = 0).
    /// </summary>
    private void RowsGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RowsGrid.SelectedItem is not PlanRowUi row || row.ItemId <= 0) return;
        // خلية قيد التحرير ← النقر المزدوج للتحرير لا لفتح النافذة
        if (RowsGrid.CurrentColumn != null && !RowsGrid.CurrentColumn.IsReadOnly) return;
        EditRow_Click(sender, e);
    }

    // ══════════ الطباعة والتصدير ══════════

    /// <summary>§طباعة الخطة بنموذج نظامنا: A4 عمودي (§v1.50.25) — ترويسة الشركة + بطاقة الخطة +
    /// ملخص العملاء + بنود مرتبة بالتاريخ (فاصل لكل يوم) + الإجماليات + تذييل الاعتماد —
    /// معاينة إلزامية قبل الطباعة (تكبير/تصغير + تصدير PDF).</summary>
    private void Print()
    {
        try
        {
            if (_currentPlanId == null || _currentPlanId.Value == 0) { AppContainer.Get<DialogService>().Error("احفظ الخطة أولاً قبل الطباعة."); return; }
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var model = Views.PlanningPrintModel.Load(db, _currentPlanId.Value);
            if (model == null) { AppContainer.Get<DialogService>().Error("تعذر تحميل بيانات الخطة للطباعة."); return; }
            var doc = Views.PlanningPrintDocument.Build(model);
            var preview = new Views.PrintPreviewWindow(doc, $"خطة الإنتاج {model.PlanNumber} — {model.Title}")
            { Owner = Window.GetWindow(this) };
            preview.ShowDialog();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.Print"); }
    }

    private void Export()
    {
        var report = new ReportResult
        {
            TitleAr = $"خطة الإنتاج — {TitleBox.Text}",
            Columns = new List<string> { "م", "العميل", "الشحنة", "الدفعة", "الصنف", "العبوة", "الكراتين", "الوزن (كجم)", "التاريخ", "الوردية" },
            Rows = _rows.Select(r => new object[] { r.No, r.CustomerName, r.ShipmentNo, r.LotCode, r.ProductName, r.PackName, r.Cartons, r.QtyKg, r.Date, r.ShiftName }).ToList()
        };
        AppContainer.Get<ExportPrintService>().ExportExcel(report);
    }

    /// <summary>§بحث وفلترة لحظية على كل الأعمدة.</summary>
    // §حُذف PlansSearch_Changed مع حذف مربع البحث الدائم: البحث صار في نافذة منبثقة (OpenPlansSearch).

    // ══════════ 1.50.60 تحسينات عامة 7-ج/7-هـ/7-ب/7-د ══════════
    private void DuplicateRow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_locked) return;
            var src = RowsGrid.SelectedItem as PlanRowUi ?? _rows.LastOrDefault(r => r.LotId != null || r.ProductId != 0);
            if (src == null) { AppContainer.Get<DialogService>().Error("اختر صفاً لتكراره أولاً."); return; }
            var dup = new PlanRowUi
            {
                CustomerId = src.CustomerId, CustomerName = src.CustomerName,
                ShipmentId = src.ShipmentId, ShipmentNo = src.ShipmentNo,
                LotId = src.LotId, LotCode = src.LotCode, RawProductId = src.RawProductId, RawName = src.RawName,
                ProductId = src.ProductId, ProductName = src.ProductName,
                PackId = src.PackId, PackName = src.PackName,
                UnitDisplay = src.UnitDisplay, CartonWeight = src.CartonWeight,
                QtyKg = src.QtyKg, Cartons = src.Cartons,
                DateValue = src.DateValue, ShiftId = src.ShiftId, ShiftName = src.ShiftName,
                LineId = src.LineId, LineName = src.LineName,
                SourceUnit = src.SourceUnit, SourceQtyInUnit = src.SourceQtyInUnit,
                SourceUnitWeightKg = src.SourceUnitWeightKg, SourceQtyKg = src.SourceQtyKg
            };
            _rows.Add(dup);
            RowsGrid.SelectedItem = dup;
            RowsGrid.ScrollIntoView(dup);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.DuplicateRow"); }
    }

    private void RowsGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        try
        {
            if (_locked) return;
            if (e.Key == System.Windows.Input.Key.F2) { AddEmptyRow(); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.F10) { Save_Click(null, null); e.Handled = true; }
            else if (e.Key == System.Windows.Input.Key.Enter)
            {
                // Enter يمين مثل Excel
                if (RowsGrid.CurrentColumn != null)
                {
                    int idx = RowsGrid.Columns.IndexOf(RowsGrid.CurrentColumn);
                    if (idx < RowsGrid.Columns.Count - 1)
                    {
                        RowsGrid.CurrentCell = new DataGridCellInfo(RowsGrid.SelectedItem, RowsGrid.Columns[idx + 1]);
                        RowsGrid.BeginEdit();
                        e.Handled = true;
                    }
                    else
                    {
                        // آخر عمود → الصف التالي
                        int rIdx = _rows.IndexOf(RowsGrid.SelectedItem as PlanRowUi);
                        if (rIdx >= 0 && rIdx < _rows.Count - 1)
                        {
                            RowsGrid.SelectedIndex = rIdx + 1;
                            RowsGrid.CurrentCell = new DataGridCellInfo(_rows[rIdx + 1], RowsGrid.Columns[1]);
                            RowsGrid.BeginEdit();
                        }
                        else
                        {
                            AddEmptyRow();
                            RowsGrid.SelectedIndex = _rows.Count - 1;
                        }
                        e.Handled = true;
                    }
                }
            }
            else if (e.Key == System.Windows.Input.Key.Down)
            {
                int rIdx = _rows.IndexOf(RowsGrid.SelectedItem as PlanRowUi);
                if (rIdx == _rows.Count - 1) { AddEmptyRow(); }
            }
        }
        catch { }
    }

        private void AutoSaveDraft()
        {
            // §1.50.69 FIX: إلغاء الحفظ التلقائي نهائياً — حذف أي مسودات قديمة
            try { if (System.IO.File.Exists(AutoSavePath)) System.IO.File.Delete(AutoSavePath); } catch { }
            return;
        }

    private void TryRestoreAutoSave()
    {
        // §1.50.69 FIX: إلغاء استعادة مسودة تلقائية نهائياً + حذف الملف
        try { if (System.IO.File.Exists(AutoSavePath)) System.IO.File.Delete(AutoSavePath); } catch { }
        return;
    }

    private class AutoSaveRow
    {
        public int? CustomerId { get; set; } public string CustomerName { get; set; }
        public int? LotId { get; set; } public string LotCode { get; set; }
        public int ProductId { get; set; } public string ProductName { get; set; }
        public int? PackId { get; set; } public string PackName { get; set; }
        public double QtyKg { get; set; } public int Cartons { get; set; }
        public string Date { get; set; } public int? ShiftId { get; set; } public int? LineId { get; set; }
    }

    private void ImportExcel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_locked) { AppContainer.Get<DialogService>().Error("الخطة معتمدة ومقفلة."); return; }
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Excel (*.xlsx;*.xls)|*.xlsx;*.xls|CSV (*.csv)|*.csv", Title = "اختر ملف بنود الخطة" };
            if (dlg.ShowDialog() != true) return;
            int added = 0;
            if (dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                var lines = System.IO.File.ReadAllLines(dlg.FileName);
                foreach (var line in lines.Skip(1))
                {
                    var parts = line.Split(",");
                    if (parts.Length < 2) continue;
                    // توقع: LotCode, ProductCode, Cartons, Date
                    var lotCode = parts[0].Trim();
                    var prodCode = parts.Length > 1 ? parts[1].Trim() : "";
                    int cartons = parts.Length > 2 && int.TryParse(parts[2].Trim(), out var c) ? c : 0;
                    string dateStr = parts.Length > 3 ? parts[3].Trim() : "";
                    if (string.IsNullOrWhiteSpace(lotCode)) continue;
                    // ابحث عن الدفعة
                    using var scope = AppContainer.NewScope();
                    var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                    var lot = db.Lots.AsNoTracking().FirstOrDefault(l => l.LotCode == lotCode);
                    if (lot == null) continue;
                    var prod = !string.IsNullOrWhiteSpace(prodCode) ? db.Products.AsNoTracking().FirstOrDefault(p => p.ProductCode == prodCode) : null;
                    var row = new PlanRowUi
                    {
                        LotId = lot.Id, LotCode = lot.LotCode,
                        CustomerId = lot.CustomerId, CustomerName = db.Customers.Where(c => c.Id == lot.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "—",
                        ProductId = prod?.Id ?? 0, ProductName = prod?.ProductNameAr ?? "—",
                        Cartons = cartons > 0 ? cartons : 1,
                        QtyKg = prod != null && prod.CartonWeightKg > 0 ? cartons * prod.CartonWeightKg : 0,
                        DateValue = DatesErp.Core.Common.UiFormat.TryParseDate(dateStr, out var d) ? d : StartBox.SelectedDate
                    };
                    _rows.Add(row); added++;
                }
            }
            else
            {
                using var wb = new ClosedXML.Excel.XLWorkbook(dlg.FileName);
                var ws = wb.Worksheets.First();
                var rows = ws.RowsUsed().Skip(1);
                foreach (var r in rows)
                {
                    var lotCode = r.Cell(1).GetString().Trim();
                    var prodCode = r.Cell(2).GetString().Trim();
                    var cartonsStr = r.Cell(3).GetString().Trim();
                    var dateStr = r.Cell(4).GetString().Trim();
                    if (string.IsNullOrWhiteSpace(lotCode)) continue;
                    int.TryParse(cartonsStr, out var cartons);
                    using var scope = AppContainer.NewScope();
                    var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                    var lot = db.Lots.AsNoTracking().FirstOrDefault(l => l.LotCode == lotCode);
                    if (lot == null) continue;
                    var prod = !string.IsNullOrWhiteSpace(prodCode) ? db.Products.AsNoTracking().FirstOrDefault(p => p.ProductCode == prodCode) : null;
                    var row = new PlanRowUi
                    {
                        LotId = lot.Id, LotCode = lot.LotCode,
                        CustomerId = lot.CustomerId, CustomerName = db.Customers.Where(c => c.Id == lot.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "—",
                        ProductId = prod?.Id ?? 0, ProductName = prod?.ProductNameAr ?? "—",
                        Cartons = cartons > 0 ? cartons : 1,
                        QtyKg = prod != null && prod.CartonWeightKg > 0 ? cartons * prod.CartonWeightKg : 0,
                        DateValue = DatesErp.Core.Common.UiFormat.TryParseDate(dateStr, out var d) ? d : StartBox.SelectedDate
                    };
                    _rows.Add(row); added++;
                }
            }
            AppContainer.Get<DialogService>().Info($"تم استيراد {added} بند من Excel.");
            EnsureEmptyRow();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Planning.ImportExcel"); }
    }
}
