using System.Windows;
using System.Windows.Controls;
using DatesErp.Desktop.Services;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>§7 — حقل في نافذة البحث الموحدة (نص/قائمة/تاريخ).</summary>
public class SearchFieldDef
{
    public string Key { get; set; }
    public string LabelAr { get; set; }
    public string Kind { get; set; } = "text"; // text | combo | date
    public string[] Options { get; set; }
}

/// <summary>نتيجة البحث الموحدة: أعمدة + صفوف بمعرفاتها.</summary>
public class SearchResult
{
    public List<string> Columns { get; set; } = new();
    public List<(int id, object[] cells)> Rows { get; set; } = new();
}

/// <summary>
/// §7/§8/§9 — نافذة البحث الموحدة لكل شاشات النظام:
/// حقول بحث حسب نوع الشاشة، بحث بدون شروط يعرض كل المستندات المتاحة حسب الصلاحية،
/// النتائج في جدول موحد واضح، ونقرتان متتاليتان على المستند تعيده إلى الواجهة الرئيسية.
/// </summary>
public class DocSearchWindow : Window
{
    /// <summary>معرف المستند الذي اختاره المستخدم بالنقر المزدوج أو زر فتح.</summary>
    public int? SelectedId { get; private set; }

    private readonly List<(SearchFieldDef def, FrameworkElement input)> _fields = new();
    private readonly DataGrid _grid = new() { Height = 380, RowHeight = 32 };
    private readonly TextBlock _state = new() { FontSize = 12, Margin = new Thickness(0, 6, 0, 0), Foreground = System.Windows.Media.Brushes.Gray };
    private readonly Func<Dictionary<string, string>, SearchResult> _search;
    private List<(int id, object[] cells)> _rows = new();

    public DocSearchWindow(string title, List<SearchFieldDef> filters, Func<Dictionary<string, string>, SearchResult> search)
    {
        _search = search;
        Title = $"🔍 بحث — {title}";
        Width = 1000; MinWidth = 760; MinHeight = 420; SizeToContent = SizeToContent.Height;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#F1F5F9");

        var filtersPanel = new WrapPanel();
        foreach (var f in filters)
        {
            FrameworkElement input = f.Kind switch
            {
                "combo" => BuildCombo(f),
                "date" => new DatePicker { Width = 130 },
                _ => new TextBox { Width = 160 }
            };
            _fields.Add((f, input));
            var sp = new StackPanel { Margin = new Thickness(0, 0, 14, 6) };
            sp.Children.Add(new TextBlock { Text = f.LabelAr + ":", FontWeight = FontWeights.Bold, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 2) });
            sp.Children.Add(input);
            filtersPanel.Children.Add(sp);
        }

        // §B84/K1: Enter يبحث وEscape يغلق.
        // §B110: حشوات مضمّنة مزالة (الثيم الموحد) + الفتح إجراء رئيسي كحلي + نمط للمحايدين
        var searchBtn = new Button { Content = "🔍 بحث", IsDefault = true };
        searchBtn.Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton");
        searchBtn.Click += (_, _) => RunSearch();
        var allBtn = new Button { Content = "📋 عرض الكل", Margin = new Thickness(6, 0, 0, 0) };
        allBtn.Style = (Style)System.Windows.Application.Current.FindResource("ErpButton");
        allBtn.Click += (_, _) => { foreach (var (_, inp) in _fields) ClearInput(inp); RunSearch(); };
        var openBtn = new Button { Content = "📂 فتح المحدد", Margin = new Thickness(6, 0, 0, 0) };
        openBtn.Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton");
        openBtn.Click += (_, _) => OpenSelected();
        var closeBtn = new Button { Content = "✖ إغلاق", Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
        closeBtn.Style = (Style)System.Windows.Application.Current.FindResource("ErpButton");
        closeBtn.Click += (_, _) => Close();

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        btns.Children.Add(searchBtn); btns.Children.Add(allBtn); btns.Children.Add(openBtn); btns.Children.Add(closeBtn);

        _grid.MouseDoubleClick += (_, _) => OpenSelected();

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = "أدخل شروط البحث ثم اضغط بحث — أو اضغط عرض الكل لعرض جميع المستندات المتاحة حسب صلاحيتك. نقرتان متتاليتان على أي مستند تفتحانه في الواجهة الرئيسية.",
            FontSize = 11.5, Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        });
        panel.Children.Add(filtersPanel);
        panel.Children.Add(btns);
        panel.Children.Add(_state);
        panel.Children.Add(_grid);
        Content = panel;

        Loaded += (_, _) => RunSearch(); // §7: بحث بدون شروط = كل المستندات المتاحة
    }

    private static ComboBox BuildCombo(SearchFieldDef f)
    {
        var cb = new ComboBox { Width = 160 };
        cb.Items.Add("— الكل —");
        if (f.Options != null) foreach (var o in f.Options) cb.Items.Add(o);
        cb.SelectedIndex = 0;
        return cb;
    }

    private static void ClearInput(FrameworkElement inp)
    {
        switch (inp)
        {
            case TextBox t: t.Text = ""; break;
            case ComboBox c: c.SelectedIndex = 0; break;
            case DatePicker d: d.SelectedDate = null; break;
        }
    }

    private Dictionary<string, string> Collect()
    {
        var values = new Dictionary<string, string>();
        foreach (var (def, inp) in _fields)
        {
            values[def.Key] = inp switch
            {
                TextBox t => t.Text?.Trim() ?? "",
                ComboBox { SelectedIndex: > 0 } c => c.SelectedItem?.ToString() ?? "",
                DatePicker d => d.SelectedDate?.ToString(DatesErp.Core.Common.UiFormat.DatePattern) ?? "",
                _ => ""
            };
        }
        return values;
    }

    private void RunSearch()
    {
        try
        {
            var result = _search(Collect());
            // §B84/M4: سقف 500 صف — البحث التلقائي عند الفتح كان يعرض الكل بلا حد (ثقل مستقبلي).
            _rows = result.Rows.Count > 500 ? result.Rows.Take(500).ToList() : result.Rows;
            var dt = new System.Data.DataTable();
            foreach (var c in result.Columns) dt.Columns.Add(c);
            foreach (var (_, cells) in _rows) dt.Rows.Add(cells);
            _grid.AutoGenerateColumns = true;
            _grid.ItemsSource = dt.DefaultView;
            _state.Text = _rows.Count == 0
                ? "لا توجد نتائج مطابقة — عدّل شروط البحث أو اضغط عرض الكل."
                : $"نتائج البحث: {_rows.Count} مستنداً" +
                  (result.Rows.Count > 500 ? $" (يُعرض أول 500 من {result.Rows.Count} — ضيّق الشروط)" : "") +
                  " — نقرتان متتاليتان على أي صف تفتحانه في الواجهة الرئيسية.";
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "DocSearch.Run"); }
    }

    private void OpenSelected()
    {
        if (_rows.Count == 0) { AppContainer.Get<DialogService>().Error("لا توجد نتائج — نفذ البحث أولاً."); return; }
        int idx = _grid.SelectedIndex;
        if (idx < 0 || idx >= _rows.Count) { AppContainer.Get<DialogService>().Error("اختر مستنداً من النتائج ثم اضغط فتح (أو انقر عليه نقرتين متتاليتين)."); return; }
        SelectedId = _rows[idx].id;
        DialogResult = true;
        Close();
    }
}
