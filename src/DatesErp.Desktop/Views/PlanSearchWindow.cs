using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace DatesErp.Desktop.Views;

/// <summary>
/// §شاشة الخطط الموحّدة: البحث عن الخطط المحفوظة في <b>نافذة منبثقة</b>.
///
/// كانت قائمة الخطط مستطيلاً دائماً أسفل الشاشة يستهلك ارتفاعاً في كل لحظة،
/// رغم أن الحاجة إليه عارضة. الآن:
/// • الشاشة الرئيسية <b>شاشة واحدة</b> بلا مستطيل «الخطط السابقة».
/// • البحث/الاختيار في هذه النافذة، تُفتح من زر الشريط أو <c>F9</c>.
/// • <b>النقر المزدوج</b> على خطة يُنزل بجميع بنودها إلى الواجهة الرئيسية.
/// </summary>
public class PlanSearchWindow : Window
{
    /// <summary>صف خطة في نتائج البحث. نوع صريح بدل الأنواع المجهولة ليسهل ربط الشبكة وتمريره بين الشاشتين.</summary>
    public class PlanSearchItem
    {
        public int Id { get; set; }
        public string DocNo { get; set; }
        public string Title { get; set; }
        public string Period { get; set; }
        public int Customers { get; set; }
        public int Items { get; set; }
        public double Qty { get; set; }
        public string StatusAr { get; set; }
    }

    private readonly List<PlanSearchItem> _all;
    private readonly ObservableCollection<PlanSearchItem> _view = new();
    private readonly TextBox _search = new();
    private readonly DataGrid _grid = new();
    private readonly TextBlock _status = new();
    // §الفلترة: شرائح حالة (الكل + كل حالة موجودة في البيانات) تُدمج مع البحث النصي.
    private readonly StackPanel _filterRow = new();
    private string _statusFilter = "الكل";
    // §الترتيب: النقر على رأس أي عمود يرتّب به (نقر ثانٍ يعكس الاتجاه) مع سهم في الرأس.
    private string _sortPath = nameof(PlanSearchItem.Id);
    private bool _sortDesc = true;
    private readonly Dictionary<DataGridColumn, string> _baseHeaders = new();

    /// <summary>الخطة التي اختارها المستخدم (نقر مزدوج أو Enter) — تُقرأ بعد إغلاق النافذة بنتيجة صحيحة.</summary>
    public int? SelectedPlanId { get; private set; }

    public PlanSearchWindow(IEnumerable<PlanSearchItem> plans)
    {
        _all = plans?.ToList() ?? new List<PlanSearchItem>();

        Title = "الخطط المحفوظة — بحث واختيار";
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 980; Height = 560;
        MinWidth = 720; MinHeight = 400;
        Background = new SolidColorBrush(Color.FromRgb(0xF1,0xF5,0xF9));
        ShowInTaskbar = false;

        var root = new DockPanel { Margin = new Thickness(10) };

        // ── ترويسة ──
        var head = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x28, 0x3C)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 8)
        };
        head.Child = new TextBlock
        {
            Text = "📋 الخطط المحفوظة — ابحث ثم انقر الخطة نقراً مزدوجاً لإنزال بنودها للشاشة",
            Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap
        };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        // ── شريط البحث ──
        var searchBar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        searchBar.Children.Add(new TextBlock
        {
            Text = "🔍 بحث:", FontWeight = FontWeights.Bold, FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        });
        _search.FontSize = 13.5; _search.Padding = new Thickness(6, 4, 6, 4);
        _search.ToolTip = "بحث فوري في رقم الخطة وعنوانها وفترتها وحالتها";
        _search.TextChanged += (_, _) => ApplyFilter();
        _search.KeyDown += (_, e) => { if (e.Key == Key.Enter) Confirm(); };
        DockPanel.SetDock(_search, Dock.Left);
        searchBar.Children.Add(_search);
        DockPanel.SetDock(searchBar, Dock.Top);
        root.Children.Add(searchBar);

        // ── صف فلتر الحالة (شرائح) ──
        _filterRow.Margin = new Thickness(0, 0, 0, 6);
        var filterLabel = new TextBlock
        {
            Text = "الحالة:",
            FontWeight = FontWeights.Bold, FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        };
        DockPanel.SetDock(filterLabel, Dock.Left);
        _filterRow.Children.Add(filterLabel);
        DockPanel.SetDock(_filterRow, Dock.Top);
        root.Children.Add(_filterRow);

        // ── عدّاد النتائج + تلميح الاستخدام ──
        _status.FontSize = 11.5; _status.Foreground = Brushes.DimGray; _status.Margin = new Thickness(0, 0, 0, 6);
        var foot = new StackPanel();
        foot.Children.Add(_status);
        foot.Children.Add(new TextBlock
        {
            Text = "نقرة مزدوجة = إنزال الخطة بكل بنودها في الواجهة الرئيسية · Enter = نفس الأثر · Esc = إغلاق",
            FontSize = 11.5, Foreground = Brushes.DimGray
        });
        DockPanel.SetDock(foot, Dock.Bottom);
        root.Children.Add(foot);

        // ── شبكة الخطط ──
        BuildGrid();
        root.Children.Add(_grid);

        Content = root;
        Loaded += (_, _) =>
        {
            BuildStatusFilter();
            ApplyFilter();
            _search.Focus();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; } };
    }

    private void BuildGrid()
    {
        _grid.AutoGenerateColumns = false;
        _grid.IsReadOnly = true;
        _grid.FontSize = 13;
        _grid.RowHeight = 30;
        _grid.ColumnHeaderHeight = 36;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _grid.HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE4, 0xD8));
        _grid.AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(0xFB, 0xFA, 0xF5));
        _grid.RowBackground = Brushes.White;
        _grid.Background = Brushes.White;
        _grid.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0xD4, 0xC4));
        _grid.SelectionMode = DataGridSelectionMode.Single;
        _grid.MouseDoubleClick += (_, _) => Confirm();
        _grid.KeyDown += (_, e) => { if (e.Key == Key.Enter) Confirm(); };
        _grid.Sorting += OnColumnSort;
        _grid.ItemsSource = _view;

        void Col(string header, string path, double width, bool star = false)
        {
            var column = new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path),
                SortMemberPath = path,
                Width = star ? new DataGridLength(width, DataGridLengthUnitType.Star) : new DataGridLength(width)
            };
            _baseHeaders[column] = header;
            _grid.Columns.Add(column);
        }

        Col("رقم الخطة", nameof(PlanSearchItem.DocNo), 120);
        Col("عنوان الخطة", nameof(PlanSearchItem.Title), 2, star: true);
        Col("الفترة", nameof(PlanSearchItem.Period), 190);
        Col("العملاء", nameof(PlanSearchItem.Customers), 70);
        Col("البنود", nameof(PlanSearchItem.Items), 65);
        Col("الكمية (كجم)", nameof(PlanSearchItem.Qty), 110);
        Col("الحالة", nameof(PlanSearchItem.StatusAr), 130);
    }

    /// <summary>شرائح فلتر الحالة: «الكل» + كل حالة موجودة فعلاً في الخطط المحمّلة.</summary>
    private void BuildStatusFilter()
    {
        var statuses = _all.Select(p => p.StatusAr ?? "").Where(s => s.Length > 0).Distinct().ToList();
        if (statuses.Count == 0) return;

        void Chip(string label)
        {
            var radio = new RadioButton
            {
                Content = label, GroupName = "planStatusFilter",
                IsChecked = label == _statusFilter,
                FontSize = 12.5, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)),
                Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = label == "الكل" ? "إظهار جميع الخطط بلا فلترة حالة" : $"إظهار الخطط ذات الحالة: {label}"
            };
            radio.Checked += (_, _) => { _statusFilter = label; ApplyFilter(); };
            _filterRow.Children.Add(radio);
        }

        Chip("الكل");
        foreach (var s in statuses) Chip(s);
    }

    /// <summary>§الترتيب: النقر على رأس عمود يرتّب به؛ النقر عليه ثانيةً يعكس الاتجاه. سهم ▲/▼ يدل على العمود النشط.</summary>
    private void OnColumnSort(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        string path = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(path)) return;
        if (path == _sortPath) _sortDesc = !_sortDesc;
        else { _sortPath = path; _sortDesc = false; }
        ApplyFilter();
    }

    private static bool IsNumericPath(string path) =>
        path == nameof(PlanSearchItem.Id) || path == nameof(PlanSearchItem.Customers) ||
        path == nameof(PlanSearchItem.Items) || path == nameof(PlanSearchItem.Qty);

    private void SortFiltered(List<PlanSearchItem> rows)
    {
        if (IsNumericPath(_sortPath))
        {
            Func<PlanSearchItem, double> num = _sortPath switch
            {
                nameof(PlanSearchItem.Id) => p => p.Id,
                nameof(PlanSearchItem.Customers) => p => p.Customers,
                nameof(PlanSearchItem.Items) => p => p.Items,
                _ => p => p.Qty
            };
            rows = _sortDesc ? rows.OrderByDescending(num).ToList() : rows.OrderBy(num).ToList();
        }
        else
        {
            Func<PlanSearchItem, string> text = _sortPath switch
            {
                nameof(PlanSearchItem.DocNo) => p => p.DocNo ?? "",
                nameof(PlanSearchItem.Title) => p => p.Title ?? "",
                nameof(PlanSearchItem.Period) => p => p.Period ?? "",
                _ => p => p.StatusAr ?? ""
            };
            rows = _sortDesc ? rows.OrderByDescending(text, StringComparer.CurrentCulture).ToList()
                             : rows.OrderBy(text, StringComparer.CurrentCulture).ToList();
        }
        rows.ForEach(p => _view.Add(p));
    }

    /// <summary>أسهم الاتجاه على رؤوس الأعمدة بعد كل فرز.</summary>
    private void UpdateHeaderGlyphs()
    {
        foreach (var column in _grid.Columns)
        {
            if (!_baseHeaders.TryGetValue(column, out var baseHeader)) continue;
            column.Header = column.SortMemberPath == _sortPath
                ? baseHeader + (_sortDesc ? " ▼" : " ▲")
                : baseHeader;
        }
    }

    private void ApplyFilter()
    {
        string term = _search.Text?.Trim() ?? "";
        var filtered = _all.Where(p =>
            (_statusFilter == "الكل" || (p.StatusAr ?? "") == _statusFilter) &&
            (term.Length == 0 ||
                (p.DocNo ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (p.Title ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (p.Period ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (p.StatusAr ?? "").Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();

        _view.Clear();
        SortFiltered(filtered);
        UpdateHeaderGlyphs();

        string filterNote = _statusFilter == "الكل" ? "" : $" · الفلتر: {_statusFilter}";
        _status.Text = _all.Count == 0
            ? "لا توجد خطط محفوظة بعد."
            : filtered.Count == _all.Count
                ? $"عدد الخطط: {_all.Count}"
                : $"النتائج: {filtered.Count} من {_all.Count}{filterNote}";
    }

    private void Confirm()
    {
        if (_grid.SelectedItem is PlanSearchItem item)
        {
            SelectedPlanId = item.Id;
            DialogResult = true;
        }
    }
}
