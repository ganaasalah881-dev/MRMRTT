using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>لون حالة المطابقة للمعايير.</summary>
public class MatchStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && s.Contains("غير مطابق") ? Brushes.Red : Brushes.Green;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>قاعدة صفوف قابلة للتحرير: تغيير الحقل ينعكس فوراً على المشتقات.</summary>
public abstract class EditableRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    protected void Raise([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); return true;
    }
}

public partial class QualityView : UserControl
{
    /// <summary>
    /// §v1.50.29: صف المصفوفة — بنفس شكل خطة الإنتاج: عميل × صنف × وزن × عبوة × المستلم للفحص،
    /// ثم عمود لكل صفة جودة (GradeQtys[typeId]) وعمود الإجمالي المحسوب. الصفات ليست أصنافاً.
    /// </summary>
    private class ItemRowUi : EditableRow
    {
        public int ProductId { get; set; }
        public int? OrderItemId { get; set; }
        public int? LotId { get; set; }
        public string LotCode { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public string ProductName { get; set; } = "";
        public string CartonWeight { get; set; } = "";
        public string PackageName { get; set; } = "";
        public double ReceivedCartons { get; set; }
        public List<AllowedResultType> Grades { get; set; } = new();
        public Dictionary<int, double> GradeQtys { get; } = new();
        public double Total => GradeQtys.Values.Sum();
        public bool IsInvalid => Math.Abs(Total - ReceivedCartons) > 0.001;
        public string ValidationError => IsInvalid ? $"مجموع الصفات {Total:N0} لا يساوي المستلم {ReceivedCartons:N0}" : null;
        /// <summary>بعد تحرير خلية صفة: يُحدّث الإجمالي.</summary>
        public void CellEdited() { Raise("Item[]"); Raise(nameof(Total)); Raise(nameof(IsInvalid)); Raise(nameof(ValidationError)); }
    }

    /// <summary>صف معيار معتمد بقيمته المسجلة وحالة المطابقة.</summary>
    private class StandardUi : EditableRow
    {
        public int StandardId { get; set; }
        public string Key { get; set; }
        public string Name { get; set; }
        public string UnitLabel { get; set; } = "%";
        public double Min { get; set; } = double.MinValue;
        public double Max { get; set; } = double.MaxValue;
        public string MinText => Min == double.MinValue ? "—" : Min.ToString("0.##");
        public string MaxText => Max == double.MaxValue ? "—" : Max.ToString("0.##");
        private double? _value;
        public double? Value { get => _value; set { if (Set(ref _value, value)) Raise(nameof(StatusAr)); } }
        public bool IsInvalid => Value != null && (Value < Min || Value > Max);
        public string ValidationError => IsInvalid ? $"القيمة {Value} خارج الحدود الاسترشادية [{MinText} - {MaxText}]" : null;
        public string StatusAr => Value == null ? "غير مقاس (استرشادي)"
            : IsInvalid ? "خارج الحد ⚠ (استرشادي)" : "ضمن الحدود ✓";
    }

    private List<QualitySourceDto> _sources = new();
    private QualitySourceDto _current;
    private readonly ObservableCollection<ItemRowUi> _rows = new();
    private readonly ObservableCollection<StandardUi> _standards = new();
    private List<AllowedResultType> _gradeColumns = new();
    private bool _loading;
    public QualityView() { InitializeComponent(); ResultsGrid.ItemsSource = _rows; CriteriaGrid.ItemsSource = _standards; Loaded += (_, _) => Load(); }
    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("فحص وتأكيد جودة التمور — المواصفة القياسية");
        chrome.SetScreenCode("MRPQC1002");
        chrome.SetToolbar(new Views.ErpToolbar()
            .WithPrint((_, _) => Print_Click(this, new RoutedEventArgs()), "طباعة محضر الفحص المحفوظ")
            .WithList((_, _) => Load(), "تحديث تسليمات الإنتاج")
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard")));
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard");
    }
    private T WithInsp<T>(Func<IInspectionService, T> action)
    {
        using var scope = AppContainer.NewScope();
        return action(scope.ServiceProvider.GetRequiredService<IInspectionService>());
    }

    /// <summary>تحميل المصادر: تسليمات الإنتاج المكتملة — القابلة للفحص أولاً.</summary>
    private void Load(int? keepOrderId = null)
    {
        try
        {
            _sources = WithInsp(s => s.GetDeliverySources());
            SourceBox.ItemsSource = _sources;
            SourceBox.DisplayMemberPath = nameof(QualitySourceDto.Label);
            _loading = true;
            SourceBox.SelectedItem = _sources.FirstOrDefault(s2 => s2.OrderId == keepOrderId && !s2.CheckApproved)
                ?? _sources.FirstOrDefault(s2 => !s2.CheckApproved)
                ?? _sources.FirstOrDefault(s2 => s2.OrderId == keepOrderId)
                ?? _sources.FirstOrDefault();
            _loading = false;
            if (_sources.Count == 0)
                StatusLabel.Text = "لا توجد تسليمات إنتاج مكتملة بعد — سجّل الفعلي في «تسليم الإنتاج» وسيأتي إلى هنا تلقائياً بأصنافه.";
            Source_Changed(SourceBox, new SelectionChangedEventArgs(ComboBox.SelectionChangedEvent, new List<object>(), new List<object> { SourceBox.SelectedItem }));
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Quality.Load"); }
    }

    private void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsGrid == null || _loading) return;
        _current = SourceBox.SelectedItem as QualitySourceDto;
        _rows.Clear(); _standards.Clear();
        ItemsPanel.Children.Clear();
        ItemsPanel.Children.Add(new TextBlock { Text = "الأصناف المستلمة للفحص:", FontWeight = FontWeights.Bold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        CheckNoChip.Text = _current?.CheckId != null
            ? $"🧾 فحص {_current.CheckNumber} — {QualityCheckStatuses.ToArabic(_current.CheckStatus)}{(_current.CheckApproved ? " (معتمد — للقراءة)" : "")}"
            : "— لا فحص محفوظ بعد";
        SaveButton.IsEnabled = _current != null && !_current.CheckApproved;
        ApproveButton.IsEnabled = _current?.CheckId != null && !_current.CheckApproved;
        StatusLabel.Text = _current == null
            ? "اختر تسليم إنتاج من الأعلى — تنزل أصنافه المنتَجة تلقائياً بكمياتها."
            : $"مصدر الفحص: تسليم الإنتاج رقم {_current.OrderNumber} — إجمالي المنتَج {_current.TotalProducedCartons:N0} كرتون"
              + (_current.CheckApproved ? " — الفحص معتمد، النتائج للقراءة والطباعة." : " — النتائج تُدخل تحت مباشرة.");
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var savedResults = _current?.CheckId != null
                ? db.InspectionResults.AsNoTracking().Where(r2 => r2.CheckId == _current.CheckId).ToList()
                : new List<InspectionResult>();
            foreach (var item in _current?.Items ?? new List<QualitySourceItemDto>())
            {
                // §التدفق المعتمد: الأصناف المنتَجة كلها تنزل بكمياتها — بلا إعادة اختيار الصنف،
                // وكل عميل بصفه كما في خطة الإنتاج (عميل × صنف × وزن × عبوة).
                var chip = new Border { Background = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#EDF3EE")),
                    BorderBrush = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#CADCCF")),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11), Padding = new Thickness(9,3,9,3), Margin = new Thickness(0,0,8,0) };
                chip.Child = new TextBlock { FontWeight = FontWeights.Bold, FontSize = 12, Foreground = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString("#14532D")),
                    Text = $"{item.CustomerName} — {item.ProductName} — {item.ProducedCartons:N0}" };
                ItemsPanel.Children.Add(chip);
                var row = new ItemRowUi { ProductId = item.ProductId, OrderItemId = item.OrderItemId, LotId = item.LotId,
                    CustomerName = item.CustomerName ?? "—", ProductName = item.ProductName, LotCode = item.LotCode ?? "—",
                    CartonWeight = item.CartonWeightKg > 0 ? item.CartonWeightKg.ToString("0.##") : "—",
                    PackageName = string.IsNullOrWhiteSpace(item.PackageName) ? "—" : item.PackageName,
                    ReceivedCartons = item.ProducedCartons, Grades = item.AllowedGrades };
                var mine = savedResults.Where(r2 => r2.OrderItemId == item.OrderItemId
                    || (r2.OrderItemId == null && r2.ProductId == item.ProductId
                        && r2.LotId == item.LotId && r2.PackagingTypeId == item.PackagingTypeId
                        && _current.Items.Count(i2 => i2.ProductId == item.ProductId
                            && i2.LotId == item.LotId && i2.PackagingTypeId == item.PackagingTypeId) == 1)).ToList();
                foreach (var r2 in mine)
                    row.GradeQtys[r2.ResultTypeId] = (double)r2.Qty;
                _rows.Add(row);
            }
            // القياسات استرشادية — لا تُملأ بقيم افتراضية توحي بأنها قِيست فعلاً.
            var savedStd = _current?.CheckId != null
                ? db.QualityStandardRecords.AsNoTracking().Where(r2 => r2.CheckId == _current.CheckId).ToDictionary(r2 => r2.StandardId, r2 => r2.Value)
                : new Dictionary<int, double>();
            foreach (var st in db.QualityStandards.AsNoTracking().Where(s2 => s2.IsActive).OrderBy(s2 => s2.SortNo))
                _standards.Add(new StandardUi { StandardId = st.Id, Key = st.Code, Name = st.NameAr, UnitLabel = st.UnitLabel,
                    Min = st.MinValue ?? double.MinValue, Max = st.MaxValue ?? double.MaxValue,
                    Value = savedStd.TryGetValue(st.Id, out var v) ? v : null });
            BuildGradeColumns();
            Recalc();
            if (_current.CheckId == null)
                StatusLabel.Text += " — أدخل صفات الفحص بنفسك؛ القياسات المخبرية اختيارية/استرشادية والتبريد تحذير فقط.";
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Quality.Source"); }
    }

    /// <summary>§v1.50.29: أعمدة الشبكة = الثابتة (عميل/صنف/وزن/عبوة/المستلم/الدفعة) + عمود لكل صفة + الإجمالي.</summary>
    private void BuildGradeColumns()
    {
        _gradeColumns = _rows.SelectMany(r2 => r2.Grades).GroupBy(g => g.ResultTypeId).Select(g => g.First())
            .OrderByDescending(g => g.ResultKind == InspectionResultType.KindAccepted).ThenBy(g => g.ResultTypeId).ToList();
        ResultsGrid.Columns.Clear();

        var wrapStyle = new Style(typeof(TextBlock));
        wrapStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        wrapStyle.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(6, 4, 6, 4)));
        wrapStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        wrapStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.5));

        var boldCenterStyle = new Style(typeof(TextBlock));
        boldCenterStyle.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center));
        boldCenterStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
        boldCenterStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        boldCenterStyle.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(6, 4, 6, 4)));
        boldCenterStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.5));

        void AddCol(string header, string path, double width, Style style = null, bool ro = true)
        {
            var col = new DataGridTextColumn { Header = header, Binding = new Binding(path), IsReadOnly = ro, Width = width };
            if (style != null) col.ElementStyle = style;
            ResultsGrid.Columns.Add(col);
        }

        AddCol("العميل", nameof(ItemRowUi.CustomerName), 220, wrapStyle);
        AddCol("الصنف", nameof(ItemRowUi.ProductName), 240, wrapStyle);
        AddCol("وزن الكرتون", nameof(ItemRowUi.CartonWeight), 100, boldCenterStyle);
        AddCol("العبوة", nameof(ItemRowUi.PackageName), 120, wrapStyle);
        AddCol("المستلم للفحص", nameof(ItemRowUi.ReceivedCartons) + StringFormatN0, 120, boldCenterStyle);
        foreach (var g in _gradeColumns)
        {
            var col = new DataGridTextColumn
            {
                Header = g.NameAr,
                Binding = new Binding($"GradeQtys[{g.ResultTypeId}]") { UpdateSourceTrigger = PropertyChangedTrigger, Mode = BindingModeTwoWay },
                Width = 115,
                ElementStyle = boldCenterStyle
            };
            ResultsGrid.Columns.Add(col);
        }
        AddCol("الدفعة", nameof(ItemRowUi.LotCode), 120, wrapStyle);
        AddCol("الإجمالي", nameof(ItemRowUi.Total) + StringFormatN0, 110, boldCenterStyle);
    }
    private const string StringFormatN0 = ";{0:N0}";
    private const System.Windows.Data.UpdateSourceTrigger PropertyChangedTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged;
    private const System.Windows.Data.BindingMode BindingModeTwoWay = System.Windows.Data.BindingMode.TwoWay;

    private void Grid_PreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (e.EditingElement is TextBox tb)
        {
            tb.Focus();
            tb.SelectAll();
        }
    }

    /// <summary>بعد تحرير خلية صفة: تحديث الإجماليات ومعادلة الحفظ الحية.</summary>
    private void Results_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit)
        {
            (e.Row.Item as ItemRowUi)?.CellEdited();
            Dispatcher.BeginInvoke(new Action(Recalc));
        }
    }

    /// <summary>معادلة الحفظ الحية لكل صف: مجموع الصفات مقابل الكمية المستلمة، والنسب لكل صفة.</summary>
    private void Recalc()
    {
        foreach (var row in _rows) row.CellEdited();
        bool allOk = _rows.Count > 0 && _rows.All(IsRowBalanced);
        var lines = new List<string>();
        foreach (var row in _rows)
        {
            double sum = row.Total;
            bool ok = Math.Abs(sum - row.ReceivedCartons) <= 0.001;
            string pcts = string.Join(" · ", _gradeColumns
                .Where(g => row.GradeQtys.TryGetValue(g.ResultTypeId, out var q) && row.ReceivedCartons > 0 && q > 0)
                .Select(g => $"{g.NameAr} {row.GradeQtys[g.ResultTypeId] / row.ReceivedCartons * 100:N2}٪"));
            lines.Add($"{row.CustomerName}/{row.ProductName}: {sum:N0} / {row.ReceivedCartons:N0} {(ok ? "✓" : "✗")}"
                      + (pcts.Length > 0 ? $" ({pcts})" : ""));
        }
        var perGrade = _gradeColumns
            .Select(g => (g.NameAr, Q: _rows.Sum(r2 => r2.GradeQtys.TryGetValue(g.ResultTypeId, out var q) ? q : 0)))
            .Where(x => x.Q > 0).ToList();
        double tot = _rows.Sum(r2 => r2.Total);
        TotalsLabel.Text = "الإجمالي: "
            + (perGrade.Count > 0 ? string.Join(" · ", perGrade.Select(x => $"{x.NameAr} {x.Q:N0}")) + " — " : "")
            + $"{tot:N0} من {_current?.TotalProducedCartons ?? 0:N0}";
        EqLabel.Text = _rows.Count == 0
            ? "معادلة الحفظ: مجموع صفات كل صنف = كميته المستلمة للفحص."
            : (allOk ? "✓ مجموع الصفات يطابق الكمية المستلمة لكل صف — يُسمح بالحفظ.  "
                     : "✗ لا يُسمح بالحفظ — الصفات لا تطابق الكمية المستلمة:  ") + string.Join("  •  ", lines);
        EqLabel.Foreground = new System.Windows.Media.SolidColorBrush((Color)ColorConverter.ConvertFromString(allOk ? "#14532D" : "#B91C1C"));
    }
    private static bool IsRowBalanced(ItemRowUi row)
        => Math.Abs(row.Total - row.ReceivedCartons) <= 0.001;

    private void Grid_SingleClickEdit(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || grid.IsReadOnly) return;
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null and not DataGridCell) dep = VisualTreeHelper.GetParent(dep);
        if (dep is DataGridCell { IsReadOnly: false, IsEditing: false } cell)
        {
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            grid.CurrentCell = new DataGridCellInfo(cell);
            grid.BeginEdit();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || _current.CheckApproved) return;
        try
        {
            ResultsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            CriteriaGrid.CommitEdit(DataGridEditingUnit.Row, true);
            if (!_rows.All(IsRowBalanced))
            {
                StatusLabel.Text = "⛔ الحفظ مرفوض: " + EqLabel.Text;
                return;
            }
            // §1.50.72 P2-4: نتائج فيها مرفوضات ← القرار يجب أن يُحدد صراحةً —
            // كان الحفظ بدون اختيار أي زر قرار يسجل «مطابق» بصمت رغم المرفوضات.
            bool hasRejected = _rows.Any(r2 => r2.GradeQtys.Any(kv =>
                kv.Value > 0 && r2.Grades.Any(g => g.ResultTypeId == kv.Key && g.ResultKind == InspectionResultType.KindRejected)));
            if (hasRejected && DecisionRejected.IsChecked != true && DecisionQuarantine.IsChecked != true)
            {
                StatusLabel.Text = "⛔ هذا الفحص يحتوي كراتين مرفوضة — حدّد القرار صراحةً (مطابق / حجز / مرفوض) قبل الحفظ.";
                return;
            }
            var dto = new QualityDeliveryCheckDto
            {
                OrderId = _current.OrderId,
                CheckDate = DateTime.Now.ToString(DatesErp.Core.Common.UiFormat.DatePattern),
                Decision = DecisionRejected.IsChecked == true ? "Rejected" : DecisionQuarantine.IsChecked == true ? "Quarantine" : "Passed",
                InspectorNotes = null,
                Rows = _rows.SelectMany(r2 => r2.GradeQtys
                        .Where(kv => kv.Key > 0 && kv.Value > 0)
                        .Select(kv => new QualityGradeRowDto { ProductId = r2.ProductId, LotId = r2.LotId,
                            OrderItemId = r2.OrderItemId, ResultTypeId = kv.Key, Cartons = kv.Value }))
                    .ToList(),
                Standards = _standards.Where(s2 => s2.Value.HasValue)
                    .Select(s2 => new QualityStandardValueDto { StandardId = s2.StandardId, Value = s2.Value!.Value }).ToList(),
            };
            if (dto.Rows.Count == 0) { StatusLabel.Text = "⛔ أدخل كمية صفة واحدة على الأقل."; return; }
            var r = WithInsp(s => s.SaveDeliveryCheck(dto));
            StatusLabel.Text = r.Ok ? "✅ " + r.Message : "⛔ " + r.Message;
            if (r.Ok) Load(_current.OrderId);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Quality.Save"); }
    }

    private void Approve_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.CheckId == null || _current.CheckApproved) return;
        try
        {
            if (!AppContainer.Get<DialogService>().Confirm($"اعتماد فحص الجودة {_current.CheckNumber}؟ الاعتماد يُقفل النتائج.")) return;
            using var scope = AppContainer.NewScope();
            var qc = scope.ServiceProvider.GetRequiredService<IQualityService>();
            var r = qc.ApproveCheck(_current.CheckId.Value);
            StatusLabel.Text = r.Ok ? "✅ " + r.Message : "⛔ " + r.Message;
            if (r.Ok) Load(_current.OrderId);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Quality.Approve"); }
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.CheckId == null) { StatusLabel.Text = "الطباعة بعد حفظ الفحص — احفظ النتائج أولاً."; return; }
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var m = Printing.StoredPrintModels.Quality(db, _current.CheckId.Value);
            new PrintPreviewWindow(PhasePrint.Build(m), $"{m.DocTitle} {m.DocNo}") { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Quality.Print"); }
    }
}
