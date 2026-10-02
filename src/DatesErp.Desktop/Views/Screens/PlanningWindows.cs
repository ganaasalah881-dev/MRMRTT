using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>سطر محرر الدفعات داخل النافذة المنبثقة (مطابق لنموذج v1.59).</summary>
/// <summary>
/// النافذة المنبثقة لاختيار أصناف وشحنات العملاء — مطابقة لنموذج v1.59:
/// كل دفعة صف قابل للتحرير: الصنف التام (002) ← العبوة والقوالب ← الكراتين ← الخام المطلوب (يُحسب)
/// مع تحديد متعدد، فلتر بحث، تحديد الكل، إدراج فردي لكل صف، وإنزال كل المحدد دفعة واحدة.
/// </summary>
public class LotsEditorWindow : Window
{
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = false, Height = 380, CanUserAddRows = false };
    private readonly TextBlock _capacityBar = new() { TextWrapping = TextWrapping.Wrap, FontSize = 15, FontWeight = FontWeights.Bold, Margin = new Thickness(8) };
    private readonly TextBlock _capacityError = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Firebrick, Margin = new Thickness(8) };
    private readonly Func<List<PlanItemDto>, PlanCapacityResult> _evaluate;
    private readonly int _lineId;
    private bool _refreshing;
    private readonly TextBox _filterBox = new() { Width = 220 };
    private readonly ComboBox _productFilter = new()
    {
        Width = 190,
        MinHeight = 24,
        DisplayMemberPath = nameof(ProductOption.Name),
        SelectedValuePath = nameof(ProductOption.Id)
    };
    private readonly CheckBox _checkAll = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly List<LotEditorRow> _rows;
    private readonly List<LotEditorRow> _all;

    /// <summary>البنود الجاهزة للإنزال إلى الخطة.</summary>
    public List<LotEditorRow> Inserted { get; } = new();

    /// <summary>§B80: فترة الخطة — تاريخ كل بند إلزامي داخلها.</summary>
    private readonly DateTime? _planFrom;
    private readonly DateTime? _planTo;

    public LotsEditorWindow(List<LotEditorRow> rows, string title, bool singleCustomer,
        DateTime? planFrom = null, DateTime? planTo = null,
        List<DatesErp.Core.Domain.Entities.Shift> shifts = null, int defaultShiftId = 0,
        Func<List<PlanItemDto>, PlanCapacityResult> evaluate = null, int lineId = 1)
    {
        _evaluate = evaluate;
        _lineId = lineId;
        _planFrom = planFrom;
        _planTo = planTo;
        // §B80: تاريخ افتراضي لكل بند = بداية فترة الخطة (قابل للتعديل لكل بند على حدة)
        foreach (var r in rows)
            if (r.DateValue == null)
            {
                if (!string.IsNullOrWhiteSpace(r.PresetDate) && DatesErp.Core.Common.UiFormat.TryParseDate(r.PresetDate, out var pd))
                    r.DateValue = pd;
                else r.DateValue = planFrom ?? DateTime.Today;
            }
        // §B92: الاختيار اليدوي للوردية — خيارات كل بند من الورديات النشطة (بالساعات الفعالة)،
        // والافتراضي وردية الشاشة؛ بنود المحرك تحتفظ بورديتها المجدولة (قابلة للتجاوز يدوياً).
        var shiftOpts = (shifts ?? new List<DatesErp.Core.Domain.Entities.Shift>())
            .Select(s => new ShiftOption { Id = s.Id, Name = $"{s.ShiftNameAr} ({s.EffectiveProductiveHours:0.#}س)" }).ToList();
        foreach (var r in rows)
        {
            if (r.AllShifts.Count == 0 && shiftOpts.Count > 0) r.AllShifts = shiftOpts;
            if (r.ShiftId == null)
                r.ShiftId = r.AllShifts.Any(x => x.Id == defaultShiftId) ? defaultShiftId
                    : (r.AllShifts.FirstOrDefault()?.Id);
        }
        _all = rows;
        _rows = rows;
        Title = title;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = Math.Min(1480, SystemParameters.WorkArea.Width - 20);
        SizeToContent = SizeToContent.Height;
        // §لا تخرج النافذة عن نطاق الشاشة أبداً
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        MaxWidth = SystemParameters.WorkArea.Width - 20;
        Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#F1F5F9");

        BuildGrid(singleCustomer);
        _grid.ItemsSource = _rows;
        _checkAll.Checked += (_, _) => { foreach (var r in _rows) r.IsChecked = true; };
        _checkAll.Unchecked += (_, _) => { foreach (var r in _rows) r.IsChecked = false; };
        _filterBox.TextChanged += (_, _) => ApplyFilter();
        // §فلتر الصنف التام — قائمة فريدة من أصناف كل البنود المعروضة (بدون تكرار).
        var prodOpts = rows.SelectMany(r => r.AllProducts ?? new List<ProductOption>())
            .GroupBy(p => p.Id).Select(g => g.First()).OrderBy(p => p.Name).ToList();
        _productFilter.Items.Add(new ProductOption { Id = 0, Name = "كل الأصناف التامة" });
        foreach (var p in prodOpts) _productFilter.Items.Add(p);
        _productFilter.SelectedIndex = 0;
        _productFilter.SelectionChanged += (_, _) => ApplyFilter();

        // §B110: الزرّان المكرران (رأس/تذييل) نفس الفعل — لفظ واحد موجز + إجراء رئيسي كحلي
        var insertAllBtn = new Button
        {
            Content = "📥 إنزال المحدد للخطة",
            Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton"),
            Margin = new Thickness(0, 0, 8, 0),
            // §B84/K1: Enter يُنزل المحدد وEscape يغلق.
            IsDefault = true
        };
        insertAllBtn.Click += (_, _) => InsertChecked();
        var closeBtn = new Button { Content = "إغلاق", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), IsCancel = true };
        closeBtn.Click += (_, _) => Close();

        var headerBar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var rightStack = new StackPanel { Orientation = Orientation.Horizontal };
        rightStack.Children.Add(new TextBlock { Text = "🔎 فلترة الأصناف:", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        rightStack.Children.Add(_productFilter);
        rightStack.Children.Add(new TextBlock { Text = "بحث حر:", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) });
        rightStack.Children.Add(_filterBox);
        DockPanel.SetDock(rightStack, Dock.Right);
        headerBar.Children.Add(rightStack);
        headerBar.Children.Add(insertAllBtn);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        var insertAllBtn2 = new Button
        {
            Content = "📥 إنزال المحدد للخطة",
            Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        insertAllBtn2.Click += (_, _) => InsertChecked();
        footer.Children.Add(insertAllBtn2);
        // §B92: قرار الإدارة بعين مفتوحة — حمولة أيام الفترة من البنود المحددة حالياً قبل الإنزال
        var dayLoadBtn = new Button
        {
            Content = "📅 حمولة الأيام المحددة",
            Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        dayLoadBtn.Click += (_, _) => ShowDayLoad();
        footer.Children.Add(dayLoadBtn);
        footer.Children.Add(closeBtn);

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new Border { Background = System.Windows.Media.Brushes.Honeydew, Child = _capacityBar });
        panel.Children.Add(_capacityError);
        panel.Children.Add(headerBar);
        panel.Children.Add(_grid);
        panel.Children.Add(footer);
        Content = panel;
        foreach (var row in _all)
        {
            row.QuantityGuard = quantity => CheckQuantity(row, quantity);
            row.PropertyChanged += SelectionChanged;
        }
        Closed += (_, _) => { foreach (var row in _all) { row.QuantityGuard = null; row.PropertyChanged -= SelectionChanged; } };
        RefreshCapacity();
    }

    public static PlanItemDto CapacityItem(LotEditorRow row, int lineId, int? quantity = null) => new()
    {
        ProductId = row.ProductId ?? 0, PackagingTypeId = row.PackId, LotId = row.LotId, SelectedRawProductId = row.RawProductId,
        PlannedCartons = quantity ?? (int.TryParse(row.CartonsText, out var c) ? c : 0),
        ScheduledDate = row.DateValue?.ToString("dd/MM/yyyy"), SuggestedShiftId = row.ShiftId, SuggestedLineId = lineId
    };

    private PlanCapacityResult Evaluate(List<LotEditorRow> rows)
        => _evaluate?.Invoke(rows.Select(r => CapacityItem(r, _lineId)).ToList())
            ?? new PlanCapacityResult { Error = "تعذر الاتصال بخدمة الطاقة؛ الإدراج موقوف." };

    private string CheckQuantity(LotEditorRow row, int quantity)
    {
        try
        {
            var items = _all.Where(r => r.IsChecked && r != row).Select(r => CapacityItem(r, _lineId)).ToList();
            if (quantity == 0) return null; // allow clearing; zero can never be inserted
            items.Add(CapacityItem(row, _lineId, quantity));
            var check = _evaluate?.Invoke(items);
            var candidate = check?.Rows.LastOrDefault();
            row.Capacity = candidate;
            string error = check == null ? "خدمة الطاقة غير متاحة." : candidate?.Error ?? check.Error;
            if (error != null) _capacityError.Text = "رُفض الإدخال: " + error;
            return error;
        }
        catch (Exception ex) { DatesErp.Desktop.Services.ErrorLog.Write(ex, "Planning.PickerCapacity"); return "تعذر التحقق من الطاقة؛ أعد المحاولة."; }
    }

    private void SelectionChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LotEditorRow.CartonsText) or nameof(LotEditorRow.IsChecked)
            or nameof(LotEditorRow.ProductId) or nameof(LotEditorRow.PackId) or nameof(LotEditorRow.DateValue)
            or nameof(LotEditorRow.ShiftId) or nameof(LotEditorRow.QuantityError) or nameof(LotEditorRow.RawProductId)) RefreshCapacity(e.PropertyName == nameof(LotEditorRow.QuantityError) ? null : sender as LotEditorRow);
    }

    private void RefreshCapacity(LotEditorRow focus = null)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var selected = _all.Where(r => r.IsChecked).ToList(); // filters NEVER change consumption
            var result = Evaluate(selected);
            _capacityBar.Text = BuildBar(selected, result);
            // لا تعرض خطأ ناتجاً من بنود الخطة الحالية قبل أن يحدد المستخدم أي دفعة؛
            // الرسالة الخضراء في هذه الحالة إرشادية وليست رفضاً للإدراج.
            _capacityError.Text = selected.Count == 0
                ? ""
                : _all.FirstOrDefault(r => r.QuantityError != null)?.QuantityError ?? result.Error ?? "";
            int offset = result.Rows.Count - selected.Count; // existing parent draft precedes this selection
            for (int i = 0; i < selected.Count && offset >= 0; i++) selected[i].Capacity = result.Rows[offset + i];
            if (focus != null && !focus.IsChecked && focus.ProductId != null && _evaluate != null)
            {
                var inquiry = selected.Select(r => CapacityItem(r, _lineId)).Append(CapacityItem(focus, _lineId, 1)).ToList();
                var projection = _evaluate(inquiry).Rows.LastOrDefault();
                if (projection != null)
                {
                    projection.Quantity = 0; projection.RequiredHours = 0; projection.UsagePercent = 0;
                    projection.RemainingPercent = projection.ProductCapacity > 0 ? projection.MaximumCartons / projection.ProductCapacity * 100 : 0;
                    if (projection.Rate > 0 && projection.ProductCapacity > 0) projection.Error = null;
                    focus.Capacity = projection;
                }
            }
        }
        catch (Exception ex)
        { DatesErp.Desktop.Services.ErrorLog.Write(ex, "Planning.PickerCapacity"); _capacityError.Text = "تعذر التحقق من الطاقة؛ الإدراج موقوف حتى إعادة التحقق."; }
        finally { _refreshing = false; }
    }

    /// <summary>
    /// §شريط الطاقة — يُحسب فعلياً لكل صنف تام بحسب عبوته (المعدل من «طاقات الأصناف») وورديته
    /// (ساعاتها الفعلية: وردية النهار أطول من الليل إذا كانت معرّفة كذلك). وزن العبوة يُظهر
    /// سياق الخام المطلوب، ولا يُشتق منه المعدل.
    /// </summary>
    private string BuildBar(List<LotEditorRow> selected, PlanCapacityResult result)
    {
        if (selected.Count == 0)
            return "⚡ الطاقة — لم تحدد بنوداً بعد: علّم بمربعات الاختيار، وتُحسب لكل صنف بحسب عبوته وورديته.";
        int offset = result.Rows.Count - selected.Count;
        var lines = new List<string>();
        double totalCartons = 0;
        foreach (var row in selected)
        {
            int idx = selected.IndexOf(row) + offset;
            if (idx < 0 || idx >= result.Rows.Count) continue;
            var rc = result.Rows[idx];
            if (row.ProductId == null || rc.Rate <= 0 || rc.ProductCapacity <= 0) continue;
            string name = row.AllProducts?.FirstOrDefault(p => p.Id == row.ProductId)?.Name ?? "صنف " + row.ProductId;
            var slot = result.Slots.FirstOrDefault(s => s.Day == row.DateValue?.Date
                && s.ShiftId == (row.ShiftId ?? 0) && s.LineId == _lineId);
            double hours = slot?.TotalHours ?? 0;
            totalCartons += rc.ProductCapacity;
            lines.Add($"• {name} — عبوة {row.PackWeight:0.#} كجم | معدل {rc.Rate:N0} كرتون/س × {hours:0.#} س = {rc.ProductCapacity:N0} كرتون");
        }
        string head = $"⚡ طاقة الوردية (تُحسب بوزن العبوة وساعات الوردية): مجمل {totalCartons:N0} كرتون | الاستخدام {result.UsagePercent:N1}%";
        return lines.Count == 0
            ? head + "\n(لم تُعرَّف طاقة بعض الأصناف في «طاقات الأصناف» — حدّدها لعرض أرقام الطاقة.)"
            : head + "\n" + string.Join("\n", lines);
    }

    private void BuildGrid(bool singleCustomer)
    {
        // §إصلاح «زر المربع لا يقبل التحديد»: عمود القالب يحتوي مربع اختيار حقيقي
        // يستجيب لنقرة واحدة مباشرة (عكس DataGridCheckBoxColumn الذي يتطلب دخول وضع التحرير)
        var chkCol = new DataGridTemplateColumn { Header = "اختيار", Width = 55 };
        var chkFactory = new FrameworkElementFactory(typeof(CheckBox));
        chkFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        chkFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        chkFactory.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new System.Windows.Data.Binding("IsChecked")
            {
                Mode = System.Windows.Data.BindingMode.TwoWay,
                UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
            });
        chkCol.CellTemplate = new DataTemplate { VisualTree = chkFactory };
        _grid.Columns.Add(chkCol);

        // §إصلاح احترافي: كانت الحقول مكدَّسة في عمود واحد بعرض 190px (عميل + دفعة + خام)
        // فتُقتطع ولا يظهر الصنف للمستخدم. الآن عمود مستقل لكل حقل، بعرض نجمي يملأ المتاح.
        // ═══════════ شاشة إدخال سريعة (إعادة تصميم) — الصف فقط: خام ← طريقة ← متاح ← تام
        // ← مواصفات تلقائية ← إنتاج ← خام مطلوب ← كفاية خام ← تاريخ ← وردية ═══════════
        if (!singleCustomer)
            _grid.Columns.Add(TextCol("العميل المالك 👤", "CustomerName", new DataGridLength(1.0, DataGridLengthUnitType.Star), 130));
        // §رقم الشحنة يُعرض بدل الدفعة — الدفعة تبقى في التلميح (ToolTip) للتتبع.
        _grid.Columns.Add(ShipmentNoCol());
        var rawCol = new DataGridTemplateColumn { Header = "الصنف الخام *", Width = 180 };
        var rawCombo = new FrameworkElementFactory(typeof(ComboBox));
        rawCombo.SetBinding(ComboBox.ItemsSourceProperty, new System.Windows.Data.Binding("RawOptions"));
        rawCombo.SetValue(ComboBox.DisplayMemberPathProperty, "Name"); rawCombo.SetValue(ComboBox.SelectedValuePathProperty, "Id");
        rawCombo.SetBinding(ComboBox.SelectedValueProperty, new System.Windows.Data.Binding("RawProductId") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true });
        rawCombo.SetBinding(ComboBox.IsEnabledProperty, new System.Windows.Data.Binding("CanChangeRaw"));
        rawCol.CellTemplate = new DataTemplate { VisualTree = rawCombo }; _grid.Columns.Add(rawCol);

        // طريقة السحب — حسب وحدة الاستلام الفعلية (سلة/كرتون/كجم) + كجم
        var modeCol = new DataGridTemplateColumn { Header = "طريقة السحب", Width = 140 };
        var modeCombo = new FrameworkElementFactory(typeof(ComboBox));
        modeCombo.SetValue(ComboBox.ItemsSourceProperty, new System.Windows.Data.Binding("SourceModes"));
        modeCombo.SetValue(ComboBox.DisplayMemberPathProperty, null);
        modeCombo.SetBinding(ComboBox.SelectedValueProperty, new System.Windows.Data.Binding("SourceMode") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
        modeCol.CellTemplate = new DataTemplate { VisualTree = modeCombo };
        _grid.Columns.Add(modeCol);

        // المتاح — يتغير حسب طريقة السحب (وحدات + كجم، أو كجم فقط)
        _grid.Columns.Add(TextCol("المتاح", "AvailableDisplay", new DataGridLength(1.6, DataGridLengthUnitType.Star), 170));

        // §عدد أيام الشحنة بالمستودع — يبقى (الأقدم أولوية الإنتاج)
        _grid.Columns.Add(TextCol("أيام بالمخزن ⏳", "DaysInStockText", new DataGridLength(0.6, DataGridLengthUnitType.Star), 85));

        // الخام المطلوب معلومة تشغيلية أساسية؛ وضعه بجوار بيانات الشحنة يضمن ظهوره
        // في نافذة الاختيار قبل الأعمدة التفصيلية، مع بقائه محسوباً للقراءة فقط.
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "الخام المطلوب (كجم)", Width = 130, IsReadOnly = true,
            Binding = new System.Windows.Data.Binding("RawRequiredKg") { StringFormat = "N1" }
        });

        var prodCol = new DataGridTemplateColumn { Header = "الصنف التام (002) *", Width = 190 };
        var prodCombo = new FrameworkElementFactory(typeof(ComboBox));
        prodCombo.SetValue(ComboBox.ItemsSourceProperty, new System.Windows.Data.Binding("AllProducts"));
        prodCombo.SetValue(ComboBox.DisplayMemberPathProperty, "Name");
        prodCombo.SetValue(ComboBox.SelectedValuePathProperty, "Id");
        prodCombo.SetValue(ComboBox.SelectedValueProperty, new System.Windows.Data.Binding("ProductId") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
        prodCol.CellTemplate = new DataTemplate { VisualTree = prodCombo };
        _grid.Columns.Add(prodCol);

        // §v1.50.35 — طاقة الصنف بجوار الصنف التام + مواصفات البطاقة (وزن/قوالب)
        _grid.Columns.Add(new DataGridTextColumn { Header = "طاقة الصنف", Width = 130, IsReadOnly = true, Binding = new System.Windows.Data.Binding("ProductCapacityDisplay") });
        _grid.Columns.Add(new DataGridTextColumn { Header = "وزن العبوة (كجم)", Width = 120, IsReadOnly = true, Binding = new System.Windows.Data.Binding("PackWeight") { StringFormat = "N1" } });
        _grid.Columns.Add(new DataGridTextColumn { Header = "عدد القوالب", Width = 105, IsReadOnly = true, Binding = new System.Windows.Data.Binding("MoldsCount") });

        // الإنتاج (كرتون) — مدخل وحيد
        var ctnCol = new DataGridTemplateColumn { Header = "الإنتاج (كرتون)", Width = 110 };
        var ctnBox = new FrameworkElementFactory(typeof(TextBox));
        ctnBox.SetValue(TextBox.TextProperty, new System.Windows.Data.Binding("CartonsText") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true });
        ctnBox.SetValue(TextBox.TextAlignmentProperty, TextAlignment.Center);
        ctnBox.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        ctnCol.CellTemplate = new DataTemplate { VisualTree = ctnBox };
        _grid.Columns.Add(ctnCol);

        // كفاية الخام — تُحسب تلقائياً بعد اختيار الصنف والكمية.
        _grid.Columns.Add(TextCol("حالة الخام", "RawStatusText", new DataGridLength(1.5, DataGridLengthUnitType.Star), 150));
        // §حالة المعالجة — تُظهر أحمر عندما يكون الخام قيد المعالجة ولا يمكن إنتاجه قبل التاريخ.
        _grid.Columns.Add(TreatmentCol());

        // تاريخ الإنتاج + الوردية — إلزاميان داخل فترة الخطة
        var dateCol = new DataGridTemplateColumn { Header = "تاريخ الإنتاج 📅", Width = 125 };
        var datePick = new FrameworkElementFactory(typeof(DatePicker));
        datePick.SetValue(FrameworkElement.WidthProperty, 118.0);
        datePick.SetBinding(DatePicker.SelectedDateProperty, new System.Windows.Data.Binding("DateValue") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
        dateCol.CellTemplate = new DataTemplate { VisualTree = datePick };
        _grid.Columns.Add(dateCol);
        var shiftCol = new DataGridTemplateColumn { Header = "الوردية 🕐 *", Width = 165 };
        var shiftCombo = new FrameworkElementFactory(typeof(ComboBox));
        shiftCombo.SetValue(ComboBox.ItemsSourceProperty, new System.Windows.Data.Binding("AllShifts"));
        shiftCombo.SetValue(ComboBox.DisplayMemberPathProperty, "Name");
        shiftCombo.SetValue(ComboBox.SelectedValuePathProperty, "Id");
        shiftCombo.SetValue(ComboBox.SelectedValueProperty, new System.Windows.Data.Binding("ShiftId") { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
        shiftCol.CellTemplate = new DataTemplate { VisualTree = shiftCombo };
        _grid.Columns.Add(shiftCol);

        var actCol = new DataGridTemplateColumn { Header = "إجراء", Width = 80 };
        var btnFactory = new FrameworkElementFactory(typeof(Button));
        btnFactory.SetValue(Button.ContentProperty, "➕ إدراج");
        btnFactory.SetValue(Button.StyleProperty, System.Windows.Application.Current.FindResource("ErpButton"));
        btnFactory.AddHandler(Button.ClickEvent, new RoutedEventHandler((s, e) =>
        {
            if ((s as Button)?.DataContext is LotEditorRow row) InsertSingle(row);
        }));
        actCol.CellTemplate = new DataTemplate { VisualTree = btnFactory };
        _grid.Columns.Add(actCol);
    }

    /// <summary>عمود حالة المعالجة — أحمر عندما يكون الخام قيد المعالجة (لا يُنتج قبل التاريخ).</summary>
    private DataGridTemplateColumn TreatmentCol()
    {
        var col = new DataGridTemplateColumn { Header = "حالة المعالجة 🧪", Width = 205 };
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("TreatmentDisplay"));
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(TextBlock.ToolTipProperty, new System.Windows.Data.Binding("TreatmentBlockReason"));
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.SeaGreen));
        style.Triggers.Add(new DataTrigger { Binding = new System.Windows.Data.Binding("TreatmentBlocked"), Value = true,
            Setters = { new Setter(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.Firebrick) } });
        text.SetValue(TextBlock.StyleProperty, style);
        col.CellTemplate = new DataTemplate { VisualTree = text };
        return col;
    }

    /// <summary>عمود رقم الشحنة — يُعرض رقم السند، والدُفعة تظهر في التلميح (للتتبع).</summary>
    private DataGridTemplateColumn ShipmentNoCol()
    {
        var col = new DataGridTemplateColumn { Header = "رقم الشحنة 📦", Width = 150 };
        var cell = new FrameworkElementFactory(typeof(TextBlock));
        cell.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("ShipmentNo"));
        cell.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        cell.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        cell.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var tip = new System.Windows.Data.MultiBinding { StringFormat = "الشحنة: {0}\nالدفعة: {1}" };
        tip.Bindings.Add(new System.Windows.Data.Binding("ShipmentNo"));
        tip.Bindings.Add(new System.Windows.Data.Binding("LotCode"));
        cell.SetValue(TextBlock.ToolTipProperty, tip);
        col.CellTemplate = new DataTemplate { VisualTree = cell };
        return col;
    }

    /// <summary>
    /// §عمود نصي مستقل: عرض نجمي بحد أدنى، واقتطاع بنقاط، وToolTip يُظهر النص كاملاً.
    /// </summary>
    private static DataGridTextColumn TextCol(string header, string path, DataGridLength width, double minWidth)
    {
        var col = new DataGridTextColumn
        {
            Header = header,
            Width = width,
            MinWidth = minWidth,
            IsReadOnly = true,
            Binding = new System.Windows.Data.Binding(path)
        };
        // DataGridTextColumn يستخدم ElementStyle لا CellTemplate
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.ToolTipProperty, new System.Windows.Data.Binding(path)));
        col.ElementStyle = style;
        return col;
    }

    private static DataTemplate MakeTextTemplate(string line1, string line2, string line3)
    {
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        var t1 = new FrameworkElementFactory(typeof(TextBlock));
        t1.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(line1));
        t1.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
        stack.AppendChild(t1);
        if (line2 != null)
        {
            var t2 = new FrameworkElementFactory(typeof(TextBlock));
            t2.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(line2));
            t2.SetValue(TextBlock.ForegroundProperty, System.Windows.Media.Brushes.Navy);
            stack.AppendChild(t2);
        }
        if (line3 != null)
        {
            var t3 = new FrameworkElementFactory(typeof(TextBlock));
            t3.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(line3));
            t3.SetValue(TextBlock.ForegroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x92, 0x40, 0x0E)));
            stack.AppendChild(t3);
        }
        return new DataTemplate { VisualTree = stack };
    }

    private void ApplyFilter()
    {
        var term = (_filterBox.Text ?? "").Trim().ToLower();
        int? filterProduct = _productFilter.SelectedItem is ProductOption pf && pf.Id > 0 ? pf.Id : (int?)null;
        IEnumerable<LotEditorRow> rows = _all;
        // §فلتر الصنف التام المحدد.
        if (filterProduct != null)
            rows = rows.Where(r => r.AllProducts != null && r.AllProducts.Any(p => p.Id == filterProduct.Value));
        // §البحث الحر — يطابق اسم العميل/الشحنة/الدفعة/الخام/الصنف التام المختار.
        if (!string.IsNullOrEmpty(term))
            rows = rows.Where(r =>
                (r.CustomerName ?? "").ToLower().Contains(term) ||
                (r.LotCode ?? "").ToLower().Contains(term) ||
                (r.ShipmentNo ?? "").ToLower().Contains(term) ||
                (r.RawName ?? "").ToLower().Contains(term) ||
                (ProductNameOf(r) ?? "").ToLower().Contains(term));
        _grid.ItemsSource = rows.ToList();
    }

    /// <summary>§اسم الصنف التام المختار في الصف، أو أسماء كل أصنافه الممكنة (للفلترة الحرة).</summary>
    private static string ProductNameOf(LotEditorRow r)
        => r.AllProducts != null
            ? string.Join(" ، ", r.AllProducts.Select(p => p.Name))
            : "";

    /// <summary>
    /// §B92 — حمولة الأيام: إجماليات البنود المحددة حالياً (المرئية بعد الفلترة) مجمعة بالتاريخ —
    /// الإدارة ترى توزيعها اليدوي على أيام الفترة قبل الإنزال، لا بعده.
    /// </summary>
    private void ShowDayLoad()
    {
        var checkedRows = _all.Where(r => r.IsChecked).ToList();
        if (checkedRows.Count == 0)
        { MessageBox.Show("لم تحدد أي بنود بعد — علّم بمربعات الاختيار أولاً لعرض حمولة الأيام.", "حمولة الأيام", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var groups = checkedRows
            .GroupBy(r => r.DateValue?.Date)
            .OrderBy(g => g.Key)
            .Select(g => new object[]
            {
                g.Key?.ToString("dd/MM/yyyy") ?? "—",
                g.Count(),
                g.Sum(r => int.TryParse(r.CartonsText, out var c) ? c : 0),
                Math.Round(g.Sum(r => r.ComputedKg), 1),
                string.Join("، ", g.Select(r => r.ShiftName).Distinct())
            }).ToList();
        var dlg = new DetailListWindow("📅 حمولة الأيام المحددة",
            $"إجماليات {checkedRows.Count} بنداً محدداً موزعة على {groups.Count} أيام — راجع ثم أنزل.",
            new List<string> { "اليوم", "البنود", "الكراتين", "الوزن (كجم)", "الورديات" }, groups)
        { Owner = this };
        dlg.ShowDialog();
    }

    private void InsertSingle(LotEditorRow row)
    {
        string err = ValidateRow(row);
        if (err != null) { MessageBox.Show(err, "إنزال البند", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var capacity = Evaluate(new List<LotEditorRow> { row });
        if (!capacity.IsValid) { _capacityError.Text = capacity.Error; return; }
        Inserted.Clear();
        Inserted.Add(row);
        DialogResult = true;
        Close();
    }

    private void InsertChecked()
    {
        var checkedRows = _all.Where(r => r.IsChecked).ToList();
        if (checkedRows.Count == 0)
        { MessageBox.Show("لم تقم بتحديد أي دفعات للإدراج — علّم بمربعات الاختيار أولاً.", "إنزال الدفعات", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        var capacity = Evaluate(checkedRows);
        if (!capacity.IsValid) { _capacityError.Text = capacity.Error; return; }
        Inserted.Clear();
        var skippedReasons = new List<string>();
        foreach (var row in checkedRows)
        {
            string err = ValidateRow(row);
            if (err != null) { skippedReasons.Add(err); continue; }
            Inserted.Add(row);
        }
        if (Inserted.Count == 0)
        {
            MessageBox.Show(
                "لم ينزل أي بند — أسباب التخطي:\n• " + string.Join("\n• ", skippedReasons),
                "إنزال الدفعات", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // §B80: لا إسقاط صامت — المستخدم يرى كل بند تخطّى وسببه قبل الإنزال
        if (skippedReasons.Count > 0)
        {
            var c2 = MessageBox.Show(
                $"⚠️ سيُنزَّل ({Inserted.Count}) بنداً من ({checkedRows.Count}) — تخطّى النظام ({skippedReasons.Count}):\n• " +
                string.Join("\n• ", skippedReasons) +
                "\n\nهل تريد إنزال البنود الصالحة فقط؟ («لا» = عودة لتصحيح البنود)",
                "إنزال الدفعات", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (c2 != MessageBoxResult.Yes) return;
        }
        DialogResult = true;
        Close();
    }

    private string ValidateRow(LotEditorRow row)
    {
        if (row.LoadFinishedProducts != null)
        {
            row.ReloadFinishedProducts(); // re-read Master Items; no stale list can authorize insertion
            if (row.RawProductId == null || row.AllProducts.Count == 0) return row.LinkMessage;
            if (row.ProductId == null || !row.AllProducts.Any(p => p.Id == row.ProductId))
                return "اختر صنفاً تاماً مرتبطاً بالصنف الخام المحدد.";
        }
        if (row.QuantityError != null) return row.QuantityError;
        // §المعالجة: منع الإدراج قبل تاريخ اكتمال المعالجة (يظهر أحمر).
        if (row.TreatmentBlocked)
            return $"الشحنة {row.ShipmentNo}: {row.TreatmentBlockReason}";
        // §كفاية الخام: منع الإدراج إذا تجاوز الخام المطلوب (إنتاج × وزن العبوة) المتاح من الشحنة.
        if (row.Ctx != null && !row.RawSufficient && row.RawRequiredKg > 0)
            return $"الشحنة {row.ShipmentNo}: {row.SourceError}";
        if (row.ProductId == null) return $"الشحنة {row.ShipmentNo}: يرجى اختيار الصنف التام المراد إنتاجه أولاً.";
        if (!int.TryParse(row.CartonsText, out var ctn) || ctn <= 0) return $"الدفعة {row.LotCode}: يرجى إدخال عدد كراتين صحيح أكبر من الصفر.";
        // §B80: تاريخ إنتاج كل بند إلزامي وداخل فترة الخطة
        if (row.DateValue == null) return $"الدفعة {row.LotCode}: حدّد تاريخ الإنتاج لهذا البند.";
        if (_planFrom != null && row.DateValue.Value.Date < _planFrom.Value.Date)
            return $"الدفعة {row.LotCode}: تاريخ الإنتاج قبل بداية فترة الخطة ({_planFrom.Value.Date:dd/MM/yyyy}).";
        if (_planTo != null && row.DateValue.Value.Date > _planTo.Value.Date)
            return $"الدفعة {row.LotCode}: تاريخ الإنتاج بعد نهاية فترة الخطة ({_planTo.Value.Date:dd/MM/yyyy}).";
        // §B92: وردية كل بند إلزامية — الاختيار اليدوي بلا افتراض صامت عند الإنزال
        if (row.ShiftId == null) return $"الدفعة {row.LotCode}: اختر وردية الإنتاج لهذا البند.";
        return null;
    }
}

/// <summary>
/// ⚖ معالج التوزيع العادل — الخطوة الأولى: محددات الفترة الحرة
/// (أسبوع أو 20 يوماً أو شهر... بلا ربط بمدة ثابتة) مع اختيار إلزامي للوردية.
/// </summary>
public class FairDistributionWizardWindow : Window
{
    private readonly DatePicker _from = new();
    private readonly DatePicker _to = new();
    private readonly ComboBox _shiftBox = new();
    private readonly ComboBox _lineBox = new();
    private readonly ComboBox _productBox = new();
    private readonly TextBox _quotaBox = new();
    private readonly CheckBox _friBox = new() { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _cumulativeBox = new() { IsChecked = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _capPerCustBox = new() { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _fullDayBox = new() { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<Shift> _shifts;
    private readonly List<ProductionLine> _lines;
    private readonly List<Product> _products;

    public string FromDate { get; private set; }
    public string ToDate { get; private set; }
    public int ShiftId { get; private set; }
    public int LineId { get; private set; }
    public int? TargetProductId { get; private set; }
    public double? DailyKg { get; private set; }
    /// <summary>§B87: تخطي الجمعة (الأسبوع: السبت–الخميس) — افتراضياً نعم.</summary>
    public bool ExcludeFriday { get; private set; } = true;
    /// <summary>§1.50.58 تحسين 2: الإنجاز التراكمي من بداية الموسم.</summary>
    public bool UseCumulative { get; private set; } = false;
    /// <summary>§1.50.58 تحسين 3: سقف العميل/اليوم.</summary>
    public bool CapPerCustomerPerDay { get; private set; } = true;
    /// <summary>§1.50.59 تحسين 4: يوم كامل (كل الورديات) أم وردية واحدة فقط — يجبر اختيار الطاقة قبل الإنزال.</summary>
    public bool UseFullDay { get; private set; } = true;

    public FairDistributionWizardWindow(List<Shift> shifts, List<ProductionLine> lines, List<Product> products,
        DateTime defaultFrom, DateTime defaultTo, int currentShiftId, int currentLineId)
    {
        _shifts = shifts; _lines = lines; _products = products;
        Title = "⚖ معالج التوزيع العادل — فترة حرة ووردية إلزامية";
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 560; SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#F1F5F9");

        _from.SelectedDate = defaultFrom;
        _to.SelectedDate = defaultTo;
        _shiftBox.ItemsSource = shifts;
        _shiftBox.DisplayMemberPath = "ShiftNameAr";
        var shIdx = shifts.FindIndex(s => s.Id == currentShiftId);
        _shiftBox.SelectedIndex = shIdx >= 0 ? shIdx : (shifts.Count > 0 ? 0 : -1);
        _lineBox.ItemsSource = lines;
        _lineBox.DisplayMemberPath = "LineNameAr";
        var lnIdx = lines.FindIndex(l => l.Id == currentLineId);
        _lineBox.SelectedIndex = lnIdx >= 0 ? lnIdx : (lines.Count > 0 ? 0 : -1);

        _productBox.Items.Add("— تلقائي (دوّار الأصناف التامة) —");
        foreach (var p in products) _productBox.Items.Add($"{p.ProductNameAr} ({p.ProductCode})");
        _productBox.SelectedIndex = 0;
        _quotaBox.Text = "";

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = "يوزع المحرك الأرصدة الخام المتاحة على أيام الفترة بالتناوب العادل بين العملاء:\nالأقل إنجازاً أولاً ثم أقدم الحاويات (FIFO) — لا إغراق لسوق عميل ولا انتظار لصاحب الحاوية الواحدة.",
            FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap
        });
        var grid = new System.Windows.Controls.Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        int r = 0;
        void AddRow(string label, FrameworkElement el, string hint = null)
        {
            var lb = new TextBlock { Text = label, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) };
            System.Windows.Controls.Grid.SetRow(lb, r); System.Windows.Controls.Grid.SetColumn(lb, 0);
            el.Margin = new Thickness(0, 0, 0, 6);
            System.Windows.Controls.Grid.SetRow(el, r); System.Windows.Controls.Grid.SetColumn(el, 1);
            grid.Children.Add(lb); grid.Children.Add(el);
            r++;
            if (hint != null)
            {
                var ht = new TextBlock { Text = hint, FontSize = 10.5, Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0, -4, 0, 6), TextWrapping = TextWrapping.Wrap };
                System.Windows.Controls.Grid.SetRow(ht, r); System.Windows.Controls.Grid.SetColumn(ht, 1);
                grid.Children.Add(ht); r++;
            }
        }
        AddRow("من تاريخ *:", _from);
        AddRow("إلى تاريخ *:", _to, "الفترة حرة: أسبوع، 10 أيام، 20 يوماً، شهر أو أكثر — حسب موسمك.");
        AddRow("الوردية الأساسية * (إلزامي):", _shiftBox, "يملأ المحرك كل الورديات النشطة — يبدأ بهذه ثم يفيض للبقية بمعدل كل صنف في ورديته.");
        AddRow("خط الإنتاج:", _lineBox);
        AddRow("الصنف التام المستهدف:", _productBox, "اترك «تلقائي» ليُدوّر المحرك بين كل الأصناف التامة.");
        AddRow("الحصة اليومية (كجم):", _quotaBox, "اتركها فارغة لاستخدام طاقة الورديات تلقائياً — الآن متوسط كل الأصناف (تحسين 1.50.58).");
        AddRow("تخطي الجمعة:", _friBox, "الأسبوع: السبت–الخميس. ألغِ التحديد إن كان المصنع يعمل أيام الجمعة.");
        AddRow("نطاق اليوم:", _fullDayBox, "☑ يوم كامل = كل الورديات النشطة (مثلاً نهار+ليل = 16 ساعة) — ☐ وردية واحدة فقط = الأساسية فقط (تحسين 1.50.59 — يجبرك تختار طاقة قبل الإنزال).");
        AddRow("الإنجاز التراكمي:", _cumulativeBox, "يحسب إنتاج كل عميل من 1 يناير — العميل الأقل إنتاجاً تاريخياً يتقدم (تحسين 2).");
        AddRow("سقف العميل/اليوم:", _capPerCustBox, "يمنع دفعة كبيرة تأكل اليوم كله — كل عميل له سقف = حصة اليوم / عدد العملاء × 1.5 (تحسين 3).");
        panel.Children.Add(grid);

        // §B84/K1: Enter يقترح وEscape يلغي.
        var okBtn = new Button { Content = "⚖ اقترح التوزيع العادل", Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton"), Margin = new Thickness(0, 8, 8, 0), IsDefault = true };
        okBtn.Click += (_, _) => Run();
        var cancelBtn = new Button { Content = "إلغاء", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), Margin = new Thickness(0, 8, 0, 0), IsCancel = true };
        cancelBtn.Click += (_, _) => Close();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        bar.Children.Add(okBtn); bar.Children.Add(cancelBtn);
        panel.Children.Add(bar);
        Content = panel;
    }

    private void Run()
    {
        if (_from.SelectedDate == null || _to.SelectedDate == null || _to.SelectedDate < _from.SelectedDate)
        { MessageBox.Show("حدد فترة صحيحة: من تاريخ ≤ إلى تاريخ.", "التوزيع العادل", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (_shiftBox.SelectedIndex < 0)
        { MessageBox.Show("اختيار الوردية إلزامي — حدد الوردية أولاً.", "التوزيع العادل", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        FromDate = _from.SelectedDate.Value.ToString("dd/MM/yyyy");
        ToDate = _to.SelectedDate.Value.ToString("dd/MM/yyyy");
        ShiftId = _shifts[_shiftBox.SelectedIndex].Id;
        LineId = _lineBox.SelectedIndex >= 0 && _lineBox.SelectedIndex < _lines.Count ? _lines[_lineBox.SelectedIndex].Id : 1;
        TargetProductId = _productBox.SelectedIndex > 0 && _productBox.SelectedIndex - 1 < _products.Count
            ? _products[_productBox.SelectedIndex - 1].Id : (int?)null;
        DailyKg = double.TryParse(_quotaBox.Text?.Trim(), out var q) && q > 0 ? q : (double?)null;
        ExcludeFriday = _friBox.IsChecked != false;
        UseCumulative = _cumulativeBox.IsChecked == true;
        CapPerCustomerPerDay = _capPerCustBox.IsChecked == true;
        UseFullDay = _fullDayBox.IsChecked == true;
        DialogResult = true;
        Close();
    }
}

/// <summary>
/// ⚖ الخطوة الثانية من معالج التوزيع العادل: ملخص نصيب كل عميل —
/// وفي القلب منه «في أي يوم ننتج لهذا العميل» — قبل الانتقال للتنزيل والتعديل.
/// </summary>
public class FairSummaryWindow : Window
{
    public class SummaryRowUi
    {
        public string CustomerName { get; set; }
        public int ContainersCount { get; set; }
        public double TotalAvailableKg { get; set; }
        public double AllocatedKg { get; set; }
        public int AllocatedCartons { get; set; }
        public double ProgressRatio { get; set; }
        public string DaysText { get; set; }
        public double HistoricalKg { get; set; }
    }

    public FairSummaryWindow(FairDistributionProposal proposal)
    {
        Title = "⚖ نتيجة التوزيع العادل — نصيب كل عميل وأيام إنتاجه";
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 980; SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        MaxWidth = SystemParameters.WorkArea.Width - 20;
        Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#F1F5F9");

        var panel = new StackPanel { Margin = new Thickness(14) };
        var msg = new TextBlock
        {
            Text = proposal.Message,
            FontWeight = FontWeights.Bold, FontSize = 13,
            Foreground = proposal.TotalRemainingKg > 0.01
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x92, 0x40, 0x0E))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x15, 0x80, 0x3D)),
            Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(msg);
        panel.Children.Add(new TextBlock
        {
            Text = $"إجمالي البنود المقترحة: {proposal.Rows.Count} بنداً على {proposal.DaysUsed} يوماً — حصة يومية ≈ {proposal.DailyQuotaKg:N0} كجم.",
            Margin = new Thickness(0, 0, 0, 8)
        });
        // §B87: من أين جاءت الأرقام + ملاحظات التجاوُز الصاخبة (تُقرأ قبل المتابعة)
        if (!string.IsNullOrWhiteSpace(proposal.CapacityNote))
            panel.Children.Add(new TextBlock
            {
                Text = proposal.CapacityNote,
                FontSize = 11, Foreground = System.Windows.Media.Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap
            });
        if (proposal.SkippedNotes != null && proposal.SkippedNotes.Count > 0)
            panel.Children.Add(new TextBlock
            {
                Text = string.Join("\n", proposal.SkippedNotes.Take(6)) + (proposal.SkippedNotes.Count > 6 ? $"\n…و {proposal.SkippedNotes.Count - 6} ملاحظات أخرى." : ""),
                FontWeight = FontWeights.Bold, FontSize = 11.5,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x92, 0x40, 0x0E)),
                Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap
            });

        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, Height = 220, CanUserAddRows = false, RowHeight = 28 };
        grid.Columns.Add(new DataGridTextColumn { Header = "العميل 👤", Width = 150, Binding = new System.Windows.Data.Binding("CustomerName") });
        grid.Columns.Add(new DataGridTextColumn { Header = "الحاويات 🚢", Width = 65, Binding = new System.Windows.Data.Binding("ContainersCount") });
        grid.Columns.Add(new DataGridTextColumn { Header = "الرصيد المتاح (كجم)", Width = 110, Binding = new System.Windows.Data.Binding("TotalAvailableKg") { StringFormat = "N0" } });
        grid.Columns.Add(new DataGridTextColumn { Header = "المخصص (كجم)", Width = 95, Binding = new System.Windows.Data.Binding("AllocatedKg") { StringFormat = "N0" } });
        grid.Columns.Add(new DataGridTextColumn { Header = "الكراتين", Width = 60, Binding = new System.Windows.Data.Binding("AllocatedCartons") { StringFormat = "N0" } });
        grid.Columns.Add(new DataGridTextColumn { Header = "تاريخي كجم", Width = 90, Binding = new System.Windows.Data.Binding("HistoricalKg") { StringFormat = "N0" } });
        grid.Columns.Add(new DataGridTextColumn { Header = "نسبة الإنجاز %", Width = 85, Binding = new System.Windows.Data.Binding("ProgressRatio") { StringFormat = "N1" } });
        grid.Columns.Add(new DataGridTextColumn { Header = "📅 أيام الإنتاج", Width = 220, Binding = new System.Windows.Data.Binding("DaysText") });
        grid.ItemsSource = proposal.Customers.Select(c => new SummaryRowUi
        {
            CustomerName = c.CustomerName,
            ContainersCount = c.ContainersCount,
            TotalAvailableKg = c.TotalAvailableKg,
            AllocatedKg = c.AllocatedKg,
            AllocatedCartons = c.AllocatedCartons,
            ProgressRatio = c.ProgressRatio,
            DaysText = string.Join("، ", c.ProductionDays),
            HistoricalKg = c.HistoricalKg
        }).ToList();
        panel.Children.Add(grid);

        // §B84/K1: Enter يتابع وEscape يلغي.
        // §B110: تقصير الجملة إلى تسمية زر (الشرح للتلميح) + إجراء رئيسي كحلي
        var okBtn = new Button { Content = "⬅ متابعة للمراجعة والإنزال", ToolTip = "مراجعة البنود وتعديلها ثم الإنزال للخطة", Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton"), Margin = new Thickness(0, 10, 8, 0), IsDefault = true };
        okBtn.Click += (_, _) => { DialogResult = true; Close(); };
        var cancelBtn = new Button { Content = "إلغاء", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), Margin = new Thickness(0, 10, 0, 0), IsCancel = true };
        cancelBtn.Click += (_, _) => Close();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        bar.Children.Add(okBtn); bar.Children.Add(cancelBtn);
        panel.Children.Add(bar);
        Content = panel;
    }
}

/// <summary>
/// §نافذة تفاصيل عامة للقراءة فقط — تُفتح بالنقر المزدوج على أي مستند في سجلاته
/// (مستندات الإقفال وغيرها): عنوان + جدول تفاصيل بلا تحرير.
/// </summary>
public class DetailListWindow : Window
{
    public DetailListWindow(string title, string subtitle, List<string> columns, List<object[]> rows)
    {
        Title = title;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 980; SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        MaxWidth = SystemParameters.WorkArea.Width - 20;

        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, RowHeight = 28 };
        foreach (var col in columns)
            grid.Columns.Add(new DataGridTextColumn { Header = col, Binding = new System.Windows.Data.Binding($"[{columns.IndexOf(col)}]"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.ItemsSource = rows;
        grid.Height = Math.Min(420, Math.Max(120, rows.Count * 28 + 40));

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = title, FontSize = 14, FontWeight = FontWeights.Bold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0A, 0x24, 0x6A)),
            Margin = new Thickness(0, 0, 0, 4)
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
            panel.Children.Add(new TextBlock
            {
                Text = subtitle, FontSize = 11.5,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5F, 0x63, 0x68)),
                Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap
            });
        panel.Children.Add(grid);
        // §B84/K1: DialogResult=false الصريح حتى يعرف المستدعي أن المستخدم أغلق بلا اختيار.
        var closeBtn = new Button { Content = "إغلاق", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(22, 5, 22, 5), IsCancel = true };
        closeBtn.Click += (_, _) => Close();
        panel.Children.Add(closeBtn);
        Content = panel;
    }
}

/// <summary>
/// §B91 — 🔍 نافذة فحص الخطة: شريط الحكم (قابلة للتنفيذ/عجز) + ملخص الأرقام
/// + تبويبات الأيام والعملاء والأصناف والتحذيرات — كل رقم من المحاكاة نفسها.
/// </summary>
public class PlanCheckWindow : Window
{
    public PlanCheckWindow(PlanCheckResult r)
    {
        Title = $"🔍 فحص الخطة {r.PlanNumber} — {r.PlanTitle}";
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 1020; SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 40;
        MaxWidth = SystemParameters.WorkArea.Width - 20;
        Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#F1F5F9");

        var panel = new StackPanel { Margin = new Thickness(14) };
        var conv = new System.Windows.Media.BrushConverter();
        panel.Children.Add(new Border
        {
            Background = (System.Windows.Media.Brush)conv.ConvertFromString(r.Ok ? "#DCFCE7" : "#FEE2E2"),
            BorderBrush = (System.Windows.Media.Brush)conv.ConvertFromString(r.Ok ? "#16A34A" : "#DC2626"),
            BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8),
            Child = new TextBlock
            {
                Text = r.Verdict, FontWeight = FontWeights.Bold, FontSize = 13,
                Foreground = (System.Windows.Media.Brush)conv.ConvertFromString(r.Ok ? "#14532D" : "#7F1D1D"),
                TextWrapping = TextWrapping.Wrap
            }
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"أيام العمل: {r.WorkDays} · العملاء: {r.CustomersCount} · البنود: {r.ItemsCount} · المطلوب: {r.RequiredKg:N1} كجم · المغطى: {r.CoveredKg:N1} كجم · العجز: {r.ShortageKg:N1} كجم.",
            FontSize = 12, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(r.CapacityNote))
            panel.Children.Add(new TextBlock
            {
                Text = r.CapacityNote, FontSize = 11,
                Foreground = System.Windows.Media.Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap
            });

        var tabs = new TabControl { Height = 340 };
        tabs.Items.Add(new TabItem { Header = "📅 توزيع الأيام", Content = DaysGrid(r) });
        tabs.Items.Add(new TabItem { Header = "👥 تغطية العملاء", Content = CustomersGrid(r) });
        tabs.Items.Add(new TabItem { Header = "🏷️ تغطية الأصناف", Content = ItemsGrid(r) });
        var warnBox = new ListBox { FontSize = 12 };
        foreach (var w in r.Warnings) warnBox.Items.Add(w);
        if (r.Warnings.Count == 0) warnBox.Items.Add("لا تحذيرات — الفحص نظيف.");
        tabs.Items.Add(new TabItem { Header = $"⚠ التحذيرات ({r.Warnings.Count})", Content = warnBox });
        panel.Children.Add(tabs);

        var closeBtn = new Button { Content = "إغلاق", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(26, 5, 26, 5), IsCancel = true };
        closeBtn.Click += (_, _) => Close();
        panel.Children.Add(closeBtn);
        Content = panel;
    }

    private static DataGrid BaseGrid()
        => new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, RowHeight = 28 };

    private static DataGrid DaysGrid(PlanCheckResult r)
    {
        var g = BaseGrid();
        g.Columns.Add(new DataGridTextColumn { Header = "اليوم", Width = 100, Binding = new System.Windows.Data.Binding("Date") });
        g.Columns.Add(new DataGridTextColumn { Header = "مطلوب اليوم (كجم)", Width = 120, Binding = new System.Windows.Data.Binding("DemandKg") { StringFormat = "N1" } });
        g.Columns.Add(new DataGridTextColumn { Header = "الموزع (كجم)", Width = 110, Binding = new System.Windows.Data.Binding("AllocatedKg") { StringFormat = "N1" } });
        g.Columns.Add(new DataGridTextColumn { Header = "الساعات", Width = 90, Binding = new System.Windows.Data.Binding("HoursUsed") { StringFormat = "N1" } });
        g.Columns.Add(new DataGridTextColumn { Header = "الحمل %", Width = 70, Binding = new System.Windows.Data.Binding("LoadPct") });
        g.Columns.Add(new DataGridTextColumn { Header = "الحالة", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new System.Windows.Data.Binding("StatusAr") });
        g.ItemsSource = r.Days;
        return g;
    }

    private static DataGrid CustomersGrid(PlanCheckResult r)
    {
        var g = BaseGrid();
        g.Columns.Add(new DataGridTextColumn { Header = "العميل", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new System.Windows.Data.Binding("CustomerName") });
        g.Columns.Add(new DataGridTextColumn { Header = "المطلوب (كجم)", Width = 110, Binding = new System.Windows.Data.Binding("RequiredKg") { StringFormat = "N1" } });
        g.Columns.Add(new DataGridTextColumn { Header = "المغطى (كجم)", Width = 110, Binding = new System.Windows.Data.Binding("CoveredKg") { StringFormat = "N1" } });
        g.Columns.Add(new DataGridTextColumn { Header = "العجز (كجم)", Width = 100, Binding = new System.Windows.Data.Binding("ShortageKg") { StringFormat = "N1" } });
        g.Columns.Add(new DataGridTextColumn { Header = "الحالة", Width = 90, Binding = new System.Windows.Data.Binding("StatusAr") });
        g.ItemsSource = r.Customers;
        return g;
    }

    private static DataGrid ItemsGrid(PlanCheckResult r)
    {
        var g = BaseGrid();
        g.Columns.Add(new DataGridTextColumn { Header = "العميل", Width = 150, Binding = new System.Windows.Data.Binding("CustomerName") });
        g.Columns.Add(new DataGridTextColumn { Header = "الصنف", Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new System.Windows.Data.Binding("ProductName") });
        g.Columns.Add(new DataGridTextColumn { Header = "الدفعة", Width = 100, Binding = new System.Windows.Data.Binding("LotCode") });
        g.Columns.Add(new DataGridTextColumn { Header = "المطلوب", Width = 90, Binding = new System.Windows.Data.Binding("RequiredKg") { StringFormat = "N1" } });
        g.Columns.Add(new DataGridTextColumn { Header = "المغطى", Width = 90, Binding = new System.Windows.Data.Binding("CoveredKg") { StringFormat = "N1" } });
        g.Columns.Add(new DataGridTextColumn { Header = "طاقة/يوم", Width = 90, Binding = new System.Windows.Data.Binding("DailyCapKg") { StringFormat = "N1" } });
        g.Columns.Add(new DataGridTextColumn { Header = "أيام لازمة", Width = 80, Binding = new System.Windows.Data.Binding("DaysNeeded") { StringFormat = "N1" } });
        g.Columns.Add(new DataGridTextColumn { Header = "الحالة", Width = 90, Binding = new System.Windows.Data.Binding("StatusAr") });
        g.ItemsSource = r.Items;
        return g;
    }
}
