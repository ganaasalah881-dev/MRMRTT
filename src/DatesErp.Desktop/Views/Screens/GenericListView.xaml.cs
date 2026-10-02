using System.Data;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Desktop.Views;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §24 — شاشة قائمة عامة: شبكة بيانات + بحث + طباعة/تصدير.
/// §23 — تدعم أزرار جديد/تعديل/حذف للشاشات القابلة للتعديل (تراجع لا يحذف محفوظاً).
/// </summary>
public partial class GenericListView : UserControl
{
    public class CrudConfig
    {
        public string EntityTitle { get; set; }
        public List<FieldDef> Fields { get; set; }
        /// <summary>الحفظ — القيم تشمل "__id" عند التعديل.</summary>
        public Func<Dictionary<string, object>, (bool ok, string msg)> Save { get; set; }
        /// <summary>تحميل قيم السجل للتعديل.</summary>
        public Func<int, Dictionary<string, object>> LoadForEdit { get; set; }
        public Func<int, (bool ok, string msg)> Delete { get; set; }
    }

    private string _title;
    private Func<DatesErpDbContext, (List<string> columns, List<object[]> rows)> _loader;
    private CrudConfig _crud;
    private bool _idFirstColumn;
    private List<string> _columns = new();
    private List<object[]> _rows = new();
    private Views.ErpChrome _chrome;
    /// <summary>§B89 — حارس التعبئة: يمنع اشتعال SelectionChanged أثناء تعبئة الفلاتر برمجياً.</summary>
    private bool _populating;
    private const string AllFields = "كل الحقول";
    private const string AllValues = "الكل";
    public string Module { get; private set; } = "البيانات الأساسية";
    public string ScreenCode { get; private set; } = "MRPMAS1099";

    /// <summary>ربط الإطار الكلاسيكي الموحّد بالشاشة.</summary>
    public void AttachChrome(Views.ErpChrome chrome, string module, string screenCode)
    {
        _chrome = chrome;
        Module = module; ScreenCode = screenCode;
        chrome.SetModule(module);
        chrome.SetScreenCode(screenCode);
        chrome.SetToolbar(BuildToolbar());
        chrome.SetBody(this);
    }

    /// <summary>§1 — شريط الأدوات القياسي الموحد: جديد/بحث/تعديل/تراجع/طباعة ثم الإضافات حسب طبيعة الشاشة.</summary>
    public Views.ErpToolbar BuildToolbar()
    {
        var tb = new Views.ErpToolbar();
        if (_crud != null) tb.WithNew(New_Click);
        tb.WithSearch(SearchBtn_Click);
        if (_crud != null)
        {
            tb.WithEdit(Edit_Click);
            tb.WithUndo(Undo_Click);
        }
        tb.WithPrint((_, _) => AppContainer.Get<ExportPrintService>().Print(AsReport()));
        if (_crud != null) tb.WithDelete(Delete_Click);
        tb.WithExcel((_, _) => AppContainer.Get<ExportPrintService>().ExportExcel(AsReport()))
          .WithRefresh((_, _) => Refresh());
        tb.WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard"));
        return tb;
    }

    private GenericListView(string title, Func<DatesErpDbContext, (List<string>, List<object[]>)> loader)
    {
        InitializeComponent();
        _title = title;
        _loader = loader;
        // §2/§27 — الشاشة تفتح فارغة نظيفة: لا عرض تلقائي للبيانات — جديد للإنشاء وبحث للاستعراض
        Loaded += (_, _) => SetEmptyState();
    }

    private void SetEmptyState()
    {
        Grid.ItemsSource = null;
        StateText.Text = _crud != null
            ? "الشاشة جاهزة — اضغط «جديد» لإنشاء سجل جديد، أو «بحث / عرض الكل» لاستعراض السجلات المحفوظة."
            : "الشاشة جاهزة — اضغط «بحث» أو «عرض الكل» لاستعراض البيانات حسب صلاحيتك.";
        _chrome?.SetCount(0);
        // §B89: تصفير الفلاتر مع الحالة الفارغة
        _populating = true;
        try
        {
            FieldBox.ItemsSource = null;
            ValueBox.ItemsSource = null;
            CountText.Text = "";
        }
        finally { _populating = false; }
    }

    public GenericListView WithCrud(CrudConfig crud)
    {
        _crud = crud;
        _idFirstColumn = true;
        return this;
    }

    private void Refresh()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            (_columns, _rows) = _loader(db);
            PopulateFilterBoxes();
            ApplyFilter();
            string action = _crud != null ? "تفتحانه للتعديل" : "لعرض تفاصيله";
            StateText.Text = $"نتائج البحث: {_rows.Count} سجلاً — نقرتان متتاليتان على أي سجل {action}.";
        }
        catch (Exception ex)
        {
            AppContainer.Get<DialogService>().HandleException(ex, "List:" + _title);
        }
    }

    /// <summary>§B89 — تعبئة الفلاتر الاحترافية من أعمدة النتائج الفعلية (تُستدعى بعد كل بحث).</summary>
    private void PopulateFilterBoxes()
    {
        _populating = true;
        try
        {
            var displayCols = _idFirstColumn ? _columns.Skip(1).ToList() : _columns;
            var fields = new List<string> { AllFields };
            fields.AddRange(displayCols);
            FieldBox.ItemsSource = fields;
            FieldBox.SelectedIndex = 0;
            ValueBox.ItemsSource = new List<string> { AllValues };
            ValueBox.SelectedIndex = 0;
        }
        finally { _populating = false; }
    }

    /// <summary>§B89 — قيم العمود المحدد (مميزة ومرتبة، بحد أقصى 200 قيمة).</summary>
    private void PopulateValueBox()
    {
        _populating = true;
        try
        {
            var values = new List<string> { AllValues };
            int col = SelectedDisplayColumn();
            if (col >= 0)
            {
                int raw = _idFirstColumn ? col + 1 : col;
                values.AddRange(_rows
                    .Select(r => raw < r.Length ? r[raw]?.ToString() ?? "" : "")
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct()
                    .OrderBy(s => s, StringComparer.CurrentCulture)
                    .Take(200));
            }
            ValueBox.ItemsSource = values;
            ValueBox.SelectedIndex = 0;
        }
        finally { _populating = false; }
    }

    /// <summary>§B89 — فهرس عمود العرض المحدد في الفلتر، أو -1 لكل الحقول.</summary>
    private int SelectedDisplayColumn()
    {
        if (FieldBox.SelectedIndex <= 0) return -1;
        return FieldBox.SelectedIndex - 1;
    }

    private void FieldBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_populating) return;
        PopulateValueBox();
        if (_columns.Count > 0) ApplyFilter();
    }

    private void ValueBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_populating) return;
        if (_columns.Count > 0) ApplyFilter();
    }

    private void ApplyFilter()
    {
        string term = SearchBox.Text?.Trim().ToLower() ?? "";
        int col = SelectedDisplayColumn();
        string wanted = ValueBox.SelectedItem as string;
        bool useValue = !string.IsNullOrEmpty(wanted) && wanted != AllValues && col >= 0;
        var filtered = _rows.Where(r =>
        {
            // مرشح القيمة: مطابقة تامة على العمود المحدد
            if (useValue)
            {
                int raw = _idFirstColumn ? col + 1 : col;
                string cell = raw < r.Length ? r[raw]?.ToString() ?? "" : "";
                if (!string.Equals(cell, wanted, StringComparison.CurrentCulture)) return false;
            }
            // البحث النصي: كل الحقول أو العمود المحدد
            if (string.IsNullOrEmpty(term)) return true;
            if (col < 0)
                return r.Any(c => c?.ToString().ToLower().Contains(term) == true);
            int ri = _idFirstColumn ? col + 1 : col;
            string one = ri < r.Length ? r[ri]?.ToString()?.ToLower() ?? "" : "";
            return one.Contains(term);
        }).ToList();

        var dt = new DataTable();
        var displayCols = _idFirstColumn ? _columns.Skip(1).ToList() : _columns;
        foreach (var c in displayCols) dt.Columns.Add(c);
        foreach (var r in filtered)
        {
            var src = _idFirstColumn ? r.Skip(1).ToArray() : r;
            var arr = new object[displayCols.Count];
            for (int i = 0; i < displayCols.Count; i++) arr[i] = i < src.Length ? src[i] : null;
            dt.Rows.Add(arr);
        }
        Grid.Tag = filtered; // الاحتفاظ بالصفوف الأصلية (مع المعرف) للتعديل/الحذف
        Grid.AutoGenerateColumns = true;
        Grid.ItemsSource = dt.DefaultView;
        EmptyHint.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _chrome?.SetCount(filtered.Count);
        // §B89: عدّاد حي — المعروض من الإجمالي
            CountText.Text = filtered.Count == _rows.Count
                ? $"النتائج: {_rows.Count} سجلاً"
                : $"النتائج: {filtered.Count} من {_rows.Count} سجلاً";
        }

        // §44 — تصدير موحّد: نفس الصفوف المعروضة بمرشحاتها الحالية إلى CSV يفتحه Excel
        private void ExportCsv_Click(object sender, RoutedEventArgs e)
        {
            if (!CsvExport.ExportGrid(Grid, "تصدير_القائمة"))
                AppContainer.Get<DialogService>().Toast("لا توجد صفوف للتصدير حالياً", "warn");
            else
                AppContainer.Get<DialogService>().Toast("تم تصدير الصفوف المعروضة إلى ملف CSV");
        }

    private int? SelectedRowId()
    {
        if (!_idFirstColumn || Grid.SelectedIndex < 0) return null;
        if (Grid.Tag is List<object[]> filtered && Grid.SelectedIndex < filtered.Count)
            return filtered[Grid.SelectedIndex][0] as int? ?? Convert.ToInt32(filtered[Grid.SelectedIndex][0]);
        return null;
    }

    // ═══════════════════════ أزرار جديد/تعديل/حذف (§23) ═══════════════════════

    private void New_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new EntityFormDialog($"{_crud.EntityTitle} — جديد", _crud.Fields);
            if (dlg.ShowDialog() != true) return;
            var (ok, msg) = _crud.Save(dlg.Values);
            if (!ok) AppContainer.Get<DialogService>().Error(msg);
            else { AppContainer.Get<DialogService>().Info(msg); Refresh(); }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "New:" + _title); }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var id = SelectedRowId();
            if (id == null) { AppContainer.Get<DialogService>().Error("اختر سجلاً من الجدول لتعديله."); return; }
            var values = _crud.LoadForEdit(id.Value);
            values["__id"] = id.Value;
            var dlg = new EntityFormDialog($"{_crud.EntityTitle} — تعديل", _crud.Fields, values);
            if (dlg.ShowDialog() != true) return;
            var (ok, msg) = _crud.Save(dlg.Values);
            if (!ok) AppContainer.Get<DialogService>().Error(msg);
            else { AppContainer.Get<DialogService>().Info(msg); Refresh(); }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Edit:" + _title); }
    }

    /// <summary>
    /// §B89 — النقر المزدوج يدرّب دائماً: قوائم التعديل تفتح نافذة التعديل،
    /// والقوائم القرائية (الأرصدة/الحركات/الدفعات/السجل...) تفتح بطاقة تفاصيل السجل كاملة.
    /// </summary>
    private void Grid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Grid.SelectedIndex < 0) return;
        if (_crud != null) { Edit_Click(sender, e); return; }
        try
        {
            if (Grid.Tag is not List<object[]> filtered || Grid.SelectedIndex >= filtered.Count) return;
            var row = filtered[Grid.SelectedIndex];
            var displayCols = _idFirstColumn ? _columns.Skip(1).ToList() : _columns;
            var src = _idFirstColumn ? row.Skip(1).ToArray() : row;
            var pairs = new List<(string label, string value)>();
            for (int i = 0; i < displayCols.Count; i++)
                pairs.Add((displayCols[i], i < src.Length ? src[i]?.ToString() ?? "—" : "—"));
            var dlg = new RecordDetailsDialog(_title, pairs) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Details:" + _title); }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var id = SelectedRowId();
            if (id == null) { AppContainer.Get<DialogService>().Error("اختر سجلاً من الجدول لحذفه."); return; }
            if (!AppContainer.Get<DialogService>().Confirm($"هل تريد حذف هذا السجل من {_crud.EntityTitle}؟")) return;
            var (ok, msg) = _crud.Delete(id.Value);
            if (!ok) AppContainer.Get<DialogService>().Error(msg);
            else { AppContainer.Get<DialogService>().Info(msg); Refresh(); }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Delete:" + _title); }
    }

    // ═══════════════════════ الطباعة والتصدير ═══════════════════════

    private ReportResult AsReport()
    {
        var displayCols = _idFirstColumn ? _columns.Skip(1).ToList() : _columns;
        var displayRows = _rows.Select(r => _idFirstColumn ? r.Skip(1).ToArray() : r).ToList();
        return new ReportResult { TitleAr = _title, Columns = displayCols, Rows = displayRows };
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) { if (ClearBtn != null) ClearBtn.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible; if (_columns.Count > 0) ApplyFilter(); }
    private void SearchBtn_Click(object sender, RoutedEventArgs e) => Refresh();
    private void ShowAll_Click(object sender, RoutedEventArgs e) { SearchBox.Text = ""; Refresh(); }
    /// <summary>§13 — التراجع: يمسح البحث والتح دون حذف أي بيانات محفوظة.</summary>
    private void Undo_Click(object sender, RoutedEventArgs e) { SearchBox.Text = ""; SetEmptyState(); }
    private void Clear_Click(object sender, RoutedEventArgs e) { SearchBox.Text = ""; if (_columns.Count > 0) ApplyFilter(); ClearBtn.Visibility = Visibility.Collapsed; }

    private void Grid_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.F2: if (_crud != null) New_Click(sender, e); break;
            case System.Windows.Input.Key.F3: if (_crud != null) Edit_Click(sender, e); break;
            case System.Windows.Input.Key.F8: if (_crud != null) Delete_Click(sender, e); break;
            case System.Windows.Input.Key.F5: Refresh(); break;
            case System.Windows.Input.Key.F9: SearchBox.Focus(); break;
        }
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
    private void Print_Click(object sender, RoutedEventArgs e) => AppContainer.Get<ExportPrintService>().Print(AsReport());
    private void Pdf_Click(object sender, RoutedEventArgs e) => AppContainer.Get<ExportPrintService>().ExportPdf(AsReport());
    private void Excel_Click(object sender, RoutedEventArgs e) => AppContainer.Get<ExportPrintService>().ExportExcel(AsReport());

    // ═══════════════════════ شاشات القوائم ═══════════════════════

    private static (bool, string) Wrap(Func<OpResult> f)
    {
        var r = f();
        return (r.Ok, r.Message);
    }

    public static GenericListView ForCustomers()
    {
        var view = new GenericListView("العملاء", db => (
        new() { "#", "الكود", "الاسم", "النوع", "الهاتف", "الشخص المسؤول", "الأولوية", "الحالة" },
        db.Customers.ToList().Select(c => new object[] { c.Id, c.CustomerCode, c.CustomerName, c.CustomerType, c.Phone, c.ContactPerson, c.PriorityNo, c.IsActive ? "نشط" : "موقوف" }).ToList()));
        return view.WithCrud(new CrudConfig
        {
            EntityTitle = "العميل",
            Fields = new()
            {
                new FieldDef { Key = "code", LabelAr = "الكود (اختياري — يُولَّد تلقائياً عند الحفظ)" }, // §47
                new FieldDef { Key = "name", LabelAr = "اسم العميل" },
                new FieldDef { Key = "type", LabelAr = "النوع", Kind = "combo", Options = new[] { "تجار جملة", "تجزئة", "تصدير", "أخرى" } },
                new FieldDef { Key = "phone", LabelAr = "الهاتف" },
                new FieldDef { Key = "contact", LabelAr = "الشخص المسؤول" },
                new FieldDef { Key = "prio", LabelAr = "الأولوية (1 أولاً، 0 بلا)", Default = "0" },
                new FieldDef { Key = "active", LabelAr = "نشط", Kind = "check", Default = "true" }
            },
            Save = v =>
            {
                using var scope = AppContainer.NewScope();
                var svc = scope.ServiceProvider.GetRequiredService<Application.Services.MasterDataService>();
                int? id = v.TryGetValue("__id", out var x) && x != null ? Convert.ToInt32(x) : null;
                int.TryParse(v.TryGetValue("prio", out var pr) ? pr?.ToString() : "0", out var prio);
                return Wrap(() => svc.SaveCustomer(id, v["code"]?.ToString(), v["name"]?.ToString(),
                    v["type"]?.ToString(), v["phone"]?.ToString(), v["contact"]?.ToString(), v["active"] is bool b && b, prio));
            },
            LoadForEdit = id =>
            {
                using var scope = AppContainer.NewScope();
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                var c = db.Customers.First(x => x.Id == id);
                return new Dictionary<string, object>
                {
                    ["code"] = c.CustomerCode, ["name"] = c.CustomerName, ["type"] = c.CustomerType,
                    ["phone"] = c.Phone, ["contact"] = c.ContactPerson, ["prio"] = c.PriorityNo, ["active"] = c.IsActive
                };
            },
            Delete = id =>
            {
                using var scope = AppContainer.NewScope();
                var svc = scope.ServiceProvider.GetRequiredService<Application.Services.MasterDataService>();
                return Wrap(() => svc.DeleteCustomer(id));
            }
        });
    }

    public static GenericListView ForSuppliers()
    {
        var view = new GenericListView("الموردون", db => (
        new() { "#", "الكود", "الاسم", "الهاتف", "الحالة" },
        db.Suppliers.ToList().Select(s => new object[] { s.Id, s.SupplierCode, s.SupplierName, s.Phone, s.IsActive ? "نشط" : "موقوف" }).ToList()));
        return view.WithCrud(new CrudConfig
        {
            EntityTitle = "المورد",
            Fields = new()
            {
                new FieldDef { Key = "code", LabelAr = "الكود (اختياري — يُولَّد تلقائياً عند الحفظ)" }, // §47
                new FieldDef { Key = "name", LabelAr = "اسم المورد" },
                new FieldDef { Key = "phone", LabelAr = "الهاتف" },
                new FieldDef { Key = "active", LabelAr = "نشط", Kind = "check", Default = "true" }
            },
            Save = v =>
            {
                using var scope = AppContainer.NewScope();
                var svc = scope.ServiceProvider.GetRequiredService<Application.Services.MasterDataService>();
                int? id = v.TryGetValue("__id", out var x) && x != null ? Convert.ToInt32(x) : null;
                return Wrap(() => svc.SaveSupplier(id, v["code"]?.ToString(), v["name"]?.ToString(), v["phone"]?.ToString(), v["active"] is bool b && b));
            },
            LoadForEdit = id =>
            {
                using var scope = AppContainer.NewScope();
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                var s = db.Suppliers.First(x => x.Id == id);
                return new Dictionary<string, object> { ["code"] = s.SupplierCode, ["name"] = s.SupplierName, ["phone"] = s.Phone, ["active"] = s.IsActive };
            },
            Delete = id =>
            {
                using var scope = AppContainer.NewScope();
                var svc = scope.ServiceProvider.GetRequiredService<Application.Services.MasterDataService>();
                return Wrap(() => svc.DeleteSupplier(id));
            }
        });
    }

    /// <summary>§شاشة الموظفين: كل موظف يأخذ رقماً للدخول — الرقم فريد (هذا الرقم محجوز).</summary>
    public static GenericListView ForEmployees()
    {
        var view = new GenericListView("الموظفون", db => (
        new() { "#", "رقم الموظف", "الاسم", "الوظيفة", "الإدارة", "الهاتف", "الحالة" },
        db.Employees.ToList().Select(e => new object[] { e.Id, e.EmployeeCode, e.FullName, e.JobTitle, e.Department, e.Phone, e.IsActive ? "نشط" : "موقوف" }).ToList()));
        return view.WithCrud(new CrudConfig
        {
            EntityTitle = "الموظف",
            Fields = new()
            {
                new FieldDef { Key = "code", LabelAr = "رقم الموظف (رقم الدخول — اختياري، يُولَّد تلقائياً عند الحفظ)" }, // §47
                new FieldDef { Key = "name", LabelAr = "الاسم الكامل" },
                new FieldDef { Key = "job", LabelAr = "الوظيفة" },
                new FieldDef { Key = "dept", LabelAr = "الإدارة", Kind = "combo", Options = new[] { "الإدارة العامة", "الإنتاج", "المخازن", "الجودة", "المبيعات", "المالية" } },
                new FieldDef { Key = "phone", LabelAr = "الهاتف" },
                new FieldDef { Key = "active", LabelAr = "نشط", Kind = "check", Default = "true" }
            },
            Save = v =>
            {
                using var scope = AppContainer.NewScope();
                var svc = scope.ServiceProvider.GetRequiredService<Application.Services.MasterDataService>();
                int? id = v.TryGetValue("__id", out var x) && x != null ? Convert.ToInt32(x) : null;
                return Wrap(() => svc.SaveEmployee(id, v["code"]?.ToString(), v["name"]?.ToString(), v["job"]?.ToString(),
                    v["dept"]?.ToString(), v["phone"]?.ToString(), v["active"] is bool b && b));
            },
            LoadForEdit = id =>
            {
                using var scope = AppContainer.NewScope();
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                var e = db.Employees.First(x => x.Id == id);
                return new Dictionary<string, object> { ["code"] = e.EmployeeCode, ["name"] = e.FullName, ["job"] = e.JobTitle, ["dept"] = e.Department, ["phone"] = e.Phone, ["active"] = e.IsActive };
            },
            Delete = id =>
            {
                using var scope = AppContainer.NewScope();
                var svc = scope.ServiceProvider.GetRequiredService<Application.Services.MasterDataService>();
                return Wrap(() => svc.DeleteEmployee(id));
            }
        });
    }

    public static GenericListView ForWarehouses()
    {
        var view = new GenericListView("المخازن", db => (
        new() { "#", "الكود", "الاسم", "النوع", "الحالة" },
        db.Warehouses.ToList().Select(w => new object[] { w.Id, w.WarehouseCode, w.WarehouseNameAr, w.WarehouseType, w.IsActive ? "نشط" : "موقوف" }).ToList()));
        return view.WithCrud(new CrudConfig
        {
            EntityTitle = "المخزن",
            Fields = new()
            {
                new FieldDef { Key = "code", LabelAr = "الكود (اختياري — يُولَّد تلقائياً عند الحفظ)" }, // §47
                new FieldDef { Key = "name", LabelAr = "اسم المخزن" },
                new FieldDef { Key = "type", LabelAr = "النوع", Kind = "combo", Options = new[] { "Raw", "Finished", "Auxiliary" } },
                new FieldDef { Key = "active", LabelAr = "نشط", Kind = "check", Default = "true" }
            },
            Save = v =>
            {
                using var scope = AppContainer.NewScope();
                var svc = scope.ServiceProvider.GetRequiredService<Application.Services.MasterDataService>();
                int? id = v.TryGetValue("__id", out var x) && x != null ? Convert.ToInt32(x) : null;
                return Wrap(() => svc.SaveWarehouse(id, v["code"]?.ToString(), v["name"]?.ToString(), v["type"]?.ToString(), v["active"] is bool b && b));
            },
            LoadForEdit = id =>
            {
                using var scope = AppContainer.NewScope();
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                var w = db.Warehouses.First(x => x.Id == id);
                return new Dictionary<string, object> { ["code"] = w.WarehouseCode, ["name"] = w.WarehouseNameAr, ["type"] = w.WarehouseType, ["active"] = w.IsActive };
            },
            Delete = id =>
            {
                using var scope = AppContainer.NewScope();
                var svc = scope.ServiceProvider.GetRequiredService<Application.Services.MasterDataService>();
                return Wrap(() => svc.DeleteWarehouse(id));
            }
        });
    }

    public static GenericListView ForLots() => new("الدفعات Lots — (المستلم / المصروف للإنتاج / المتبقي / المسلَّم)", db => (
        new() { "الدفعة", "الصنف", "العميل", "المستلم (كجم)", "المصروف للإنتاج (كجم)", "المتبقي (كجم)", "المحجوز للخطط (كجم)", "تحت المعالجة (كجم)", "جاهز للإنتاج (كجم)", "المتاح (كجم)", "المسلَّم (كجم)" },
        db.Lots.ToList().Select(l => new object[]
        {
            l.LotCode,
            db.Products.Where(p => p.Id == l.ProductId).Select(p => p.ProductNameAr).FirstOrDefault(),
            db.Customers.Where(c => c.Id == l.CustomerId).Select(c => c.CustomerName).FirstOrDefault(),
            l.InitialQtyKg, l.ProducedQtyKg, l.InStockQtyKg, l.ReservedQtyKg,
            // §المعالجة والتعقيم: يفسّران نقص «المتاح» بدل أن يبدو خطأً في النظام
            l.UnderTreatmentQtyKg, l.TreatmentReadyQtyKg,
            l.AvailableQtyKg, l.DeliveredQtyKg
        }).ToList()));

    public static GenericListView ForExecutions() => new("متابعة التنفيذ", db => (
        new() { "الجلسة", "الأمر", "البداية", "النهاية", "الكمية (كجم)", "الكراتين", "الحالة" },
        db.ProductionExecutions.ToList().Select(e => new object[]
        {
            e.DocumentNumber,
            db.ProductionOrders.Where(o => o.Id == e.OrderId).Select(o => o.DocumentNumber).FirstOrDefault(),
            e.StartDateTime?.ToString("dd/MM/yyyy HH:mm"), e.EndDateTime?.ToString("dd/MM/yyyy HH:mm"),
            e.ActualQtyKg, e.ActualCartons, e.Status
        }).ToList()));

    public static GenericListView ForWastage() => new("الهالك والأصناف الثانوية (بالكيلو)", db => (
        new() { "الفحص", "الصنف الثانوي", "الوحدة", "الكمية (كجم)" },
        db.QualityByProductRecords.ToList().Select(b => new object[]
        {
            db.QualityChecks.Where(c => c.Id == b.CheckId).Select(c => c.DocumentNumber).FirstOrDefault(),
            db.ByProducts.Where(x => x.Id == b.ByProductId).Select(x => x.ByProductNameAr).FirstOrDefault(),
            db.ByProducts.Where(x => x.Id == b.ByProductId).Select(x => x.UnitOfMeasure).FirstOrDefault(),
            b.QtyKg
        }).ToList()));

    // §v1.50.33: الاسم يُستكمل من العبوة حين لا يكون الصنف/المادة (صفوف العبوات كانت تظهر فارغة)،
    // وأُضيفت الوحدة وتاريخ الدفعة (التشغيلة) — والبحث أصبح ذا معنى لأن كل خلية لها قيمة.
    public static GenericListView ForBalances() => new("أرصدة المخزون", db => (
        new() { "المخزن", "الصنف/المادة", "الوحدة", "الدفعة", "تاريخ الدفعة", "العميل", "الرصيد (كجم)", "العبوات" },
        db.StockBalances.Where(b => b.QtyKg != 0 || b.PackageCount != 0).ToList().Select(b => // §1.50.66 — كل عبوة منفصلة
        {
            var product = b.ProductId != null ? db.Products.Where(p => p.Id == b.ProductId).Select(p => new { p.ProductNameAr, p.UnitOfMeasure }).FirstOrDefault() : null;
            var material = b.MaterialId != null ? db.AuxiliaryMaterials.Where(m => m.Id == b.MaterialId).Select(m => new { m.MaterialNameAr, m.UnitOfMeasure }).FirstOrDefault() : null;
            var package = b.PackagingTypeId != null ? db.PackagingTypes.Where(x => x.Id == b.PackagingTypeId).Select(x => new { x.PackageNameAr }).FirstOrDefault() : null;
            var lot = b.LotId != null ? db.Lots.Where(l => l.Id == b.LotId).Select(l => new { l.LotCode, l.LotDate, l.CustomerId }).FirstOrDefault() : null;
            var customerId = b.CustomerId ?? lot?.CustomerId;
            var customer = customerId != null ? db.Customers.Where(c => c.Id == customerId).Select(c => c.CustomerName).FirstOrDefault() : null;
            return new object[]
            {
                db.Warehouses.Where(w => w.Id == b.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault(),
                product?.ProductNameAr ?? material?.MaterialNameAr ?? package?.PackageNameAr ?? "—",
                product?.UnitOfMeasure ?? material?.UnitOfMeasure ?? "كجم",
                lot?.LotCode ?? "—",
                lot?.LotDate?.ToString("dd/MM/yyyy") ?? "—",
                customer ?? "—",
                b.QtyKg, b.PackageCount
            };
        }).ToList()));

    // §v1.50.33: عمودا «المستخدم/الجهاز» أُزيلا (مطلوب المستخدم — متاحان في سجل التدقيق)،
    // وأُضيفت «الوحدة» و«العميل صاحب الشحنة» (من الحركة أو من دفعتها).
    public static GenericListView ForMovements() => new("حركات المخزون (تتبع كامل)", db => MovementRows(db, false));

    public static GenericListView ForTransfers() => new("التحويلات المخزنية", db => MovementRows(db, true));

    private static (List<string> columns, List<object[]> rows) MovementRows(DatesErpDbContext db, bool transfersOnly)
    {
        var query = db.InventoryTransactions.AsQueryable();
        if (transfersOnly) query = query.Where(t => t.MovementType == Core.Domain.Enums.MovementType.Transfer);
        var rows = query.OrderByDescending(t => t.TxnDate).Take(1000).ToList().Select(t =>
        {
            var product = t.ProductId != null ? db.Products.Where(p => p.Id == t.ProductId).Select(p => new { p.ProductNameAr, p.UnitOfMeasure }).FirstOrDefault() : null;
            var material = t.MaterialId != null ? db.AuxiliaryMaterials.Where(m => m.Id == t.MaterialId).Select(m => new { m.MaterialNameAr, m.UnitOfMeasure }).FirstOrDefault() : null;
            var lot = t.LotId != null ? db.Lots.Where(l => l.Id == t.LotId).Select(l => new { l.CustomerId }).FirstOrDefault() : null;
            var customerId = t.CustomerId ?? lot?.CustomerId;
            var customer = customerId != null ? db.Customers.Where(c => c.Id == customerId).Select(c => c.CustomerName).FirstOrDefault() : null;
            return new object[]
            {
                t.TxnNumber, t.TxnDate.ToString("dd/MM/yyyy HH:mm"),
                db.Warehouses.Where(w => w.Id == t.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault(),
                product?.ProductNameAr ?? material?.MaterialNameAr ?? "—",
                product?.UnitOfMeasure ?? material?.UnitOfMeasure ?? "كجم",
                db.Lots.Where(l => l.Id == t.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "—",
                customer ?? "—",
                t.MovementType == Core.Domain.Enums.MovementType.Inbound ? "وارد"
                    : t.MovementType == Core.Domain.Enums.MovementType.Transfer ? "تحويل" : "صادر",
                t.QtyKg, $"{t.ReferenceDocType}: {t.ReferenceDocNumber}"
            };
        }).ToList();
        return (new() { "الحركة", "التاريخ", "المخزن", "الصنف", "الوحدة", "الدفعة", "العميل صاحب الشحنة", "النوع", "الكمية (كجم)", "المستند المرجعي" }, rows);
    }

    public static GenericListView ForMachines() => new("الأجهزة المتصلة بالنظام", db => (
        new() { "معرف الجهاز", "اسم الجهاز", "مستخدم ويندوز", "إصدار التطبيق", "آخر دخول", "آخر ظهور", "الحالة" },
        db.ClientMachines.OrderByDescending(m => m.LastSeen).ToList().Select(m => new object[]
        {
            m.MachineId, m.MachineName, m.WindowsUser, m.ApplicationVersion,
            m.LastLogin?.ToString("dd/MM/yyyy HH:mm"), m.LastSeen?.ToString("dd/MM/yyyy HH:mm"),
            m.IsActive ? "نشط" : "غير نشط"
        }).ToList()));

    public static GenericListView ForAudit() => new("سجل التدقيق", db => (
        new() { "التاريخ", "المستخدم", "الجهاز", "الإجراء", "الشاشة", "المستند", "رقم السجل" },
        db.AuditLogs.OrderByDescending(a => a.ActionDate).Take(1000).ToList().Select(a => new object[]
        {
            a.ActionDate.ToString("dd/MM/yyyy HH:mm:ss"), a.UserName, a.MachineName, a.ActionType, a.ScreenName, a.DocumentNumber, a.RecordId
        }).ToList()));
}

/// <summary>
/// §B89 — بطاقة تفاصيل السجل (قراءة فقط): النقر المزدوج على أي صف في القوائم القرائية
/// يعرض كل حقول السجل عمودياً — لا بيانات مخفية خلف عرض الشبكة.
/// </summary>
public class RecordDetailsDialog : Window
{
    public RecordDetailsDialog(string title, List<(string label, string value)> pairs)
    {
        Title = title + " — تفاصيل السجل";
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height - 60;
        Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xF1,0xF5,0xF9));

        var card = new Border
        {
            Background = System.Windows.Media.Brushes.White,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x7F, 0x9D, 0xB9)),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14),
            Margin = new Thickness(12)
        };
        var rows = new StackPanel();
        rows.Children.Add(new TextBlock
        {
            Text = "🧾 " + title + " — تفاصيل السجل",
            FontSize = 13, FontWeight = FontWeights.Bold,
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x0A, 0x24, 0x6A)),
            Margin = new Thickness(0, 0, 0, 8)
        });
        bool zebra = false;
        foreach (var (label, value) in pairs)
        {
            var line = new DockPanel
            {
                Margin = new Thickness(0, 0, 0, 2),
                Background = zebra
                    ? new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xF8,0xFA,0xFC))
                    : System.Windows.Media.Brushes.Transparent
            };
            zebra = !zebra;
            line.Children.Add(new TextBlock
            {
                Text = label + ":", FontWeight = FontWeights.Bold, FontSize = 12,
                Width = 150, Margin = new Thickness(4, 3, 4, 3),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x1F, 0x29, 0x37))
            });
            var val = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
                FontSize = 12, Margin = new Thickness(4, 3, 4, 3),
                TextWrapping = TextWrapping.Wrap
            };
            line.Children.Add(val);
            rows.Children.Add(line);
        }
        var close = new Button
        {
            Content = "إغلاق",
            Style = (Style)TryFindResource("ErpButton"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        };
        close.Click += (_, _) => Close();
        rows.Children.Add(close);
        card.Child = new ScrollViewer
        {
            Content = rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = SystemParameters.WorkArea.Height - 160
        };
        Content = card;
    }
}
