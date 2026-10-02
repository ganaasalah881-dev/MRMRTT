using System.Data;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §B73: شاشة «طاقات الأصناف» — جدول ديناميكي:
/// الصنف | الإنتاج بالساعة | الوردية: ساعات | طاقة | ... | طاقة اليوم.
/// الأصناف من شاشة الأصناف بالمعرّف (لا كتابة يدوية)، والورديات من شاشة الورديات حيةً.
/// يُعدَّل «الإنتاج بالساعة» فقط؛ الباقي محسوب: المعدل × الساعات الفعلية (إجمالي − توقف مخطط).
/// </summary>
public partial class ItemsCapacitiesView : UserControl
{
    private const string HourlyCol = "الإنتاج بالساعة";
    private const string IdCol = "Id";
    private DataTable _table;
    private readonly List<(int id, string hoursCol, string capCol)> _shiftCols = new();

    public ItemsCapacitiesView()
    {
        InitializeComponent();
        CapsGrid.CellEditEnding += CapsGrid_CellEditEnding;
        Loaded += (_, _) => BuildMatrix("");
    }

    private static DatesErpDbContext Db()
        => AppContainer.NewScope().ServiceProvider.GetRequiredService<DatesErpDbContext>();

    private static ICapacityService Caps()
        => AppContainer.NewScope().ServiceProvider.GetRequiredService<ICapacityService>();

    /// <summary>بناء المصفوفة حيةً من الأصناف والورديات — أعمدة الورديات ديناميكية بعددها الفعلي.</summary>
    private void BuildMatrix(string term)
    {
        try
        {
            using var db = Db();
            var shifts = db.Shifts.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Id).ToList();
            // §B80: طاقات الإنتاج لأصناف الإنتاج التام فقط (مجموعة 002/Finished) —
            // الخام والمخرجات الثانوية لا تُنتَج فلا طاقة إنتاج لها في هذه الشاشة.
            var products = db.Products.AsNoTracking()
                .Where(p => p.IsActive && p.ItemType == "Finished")
                .OrderBy(p => p.ProductCode).ToList().AsEnumerable();
            if (!string.IsNullOrWhiteSpace(term))
                products = products.Where(p => (p.ProductNameAr ?? "").Contains(term) || (p.ProductCode ?? "").Contains(term));

            _table = new DataTable();
            _table.Columns.Add(IdCol, typeof(int));
            _table.Columns.Add("رقم الصنف", typeof(string));
            _table.Columns.Add("الصنف", typeof(string));
            _table.Columns.Add("الوحدة", typeof(string));
            _table.Columns.Add(HourlyCol, typeof(string));
            _shiftCols.Clear();
            foreach (var s in shifts)
            {
                double hours = CapacityPolicy.EffectiveHours(s.EffectiveProductiveHours, s.TotalHours);
                string hc = $"⏱ {s.ShiftNameAr} (ساعات)";
                string cc = $"⚡ طاقة {s.ShiftNameAr}";
                _table.Columns.Add(hc, typeof(string));
                _table.Columns.Add(cc, typeof(string));
                _shiftCols.Add((s.Id, hc, cc));
            }
            _table.Columns.Add("📅 طاقة اليوم كاملة", typeof(string));

            foreach (var p in products)
            {
                var row = _table.NewRow();
                FillRow(row, p.Id, p.ProductCode, p.ProductNameAr, p.UnitOfMeasure, p.HourlyProductionRate, shifts);
                _table.Rows.Add(row);
            }

            CapsGrid.AutoGenerateColumns = false;
            CapsGrid.Columns.Clear();
            CapsGrid.Columns.Add(new DataGridTextColumn { Header = "رقم الصنف", Binding = new System.Windows.Data.Binding("رقم الصنف"), Width = 90, IsReadOnly = true });
            CapsGrid.Columns.Add(new DataGridTextColumn { Header = "الصنف", Binding = new System.Windows.Data.Binding("الصنف"), Width = 160, IsReadOnly = true });
            CapsGrid.Columns.Add(new DataGridTextColumn { Header = "الوحدة", Binding = new System.Windows.Data.Binding("الوحدة"), Width = 70, IsReadOnly = true });
            CapsGrid.Columns.Add(new DataGridTextColumn { Header = HourlyCol, Binding = new System.Windows.Data.Binding(HourlyCol) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.LostFocus }, Width = 110 });
            foreach (var (id, hc, cc) in _shiftCols)
            {
                CapsGrid.Columns.Add(new DataGridTextColumn { Header = hc, Binding = new System.Windows.Data.Binding(hc), Width = 105, IsReadOnly = true });
                CapsGrid.Columns.Add(new DataGridTextColumn { Header = cc, Binding = new System.Windows.Data.Binding(cc), Width = 105, IsReadOnly = true });
            }
            CapsGrid.Columns.Add(new DataGridTextColumn { Header = "📅 طاقة اليوم كاملة", Binding = new System.Windows.Data.Binding("📅 طاقة اليوم كاملة"), Width = 130, IsReadOnly = true });
            CapsGrid.ItemsSource = _table.DefaultView;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Caps.Matrix"); }
    }

    private void FillRow(DataRow row, int id, string code, string name, string unit, double hourly, List<DatesErp.Core.Domain.Entities.Shift> shifts)
    {
        row[IdCol] = id;
        row["رقم الصنف"] = code;
        row["الصنف"] = name;
        row["الوحدة"] = unit ?? "—";
        row[HourlyCol] = hourly > 0 ? hourly.ToString("0.###") : "";
        double day = 0;
        foreach (var s in shifts)
        {
            double hours = CapacityPolicy.EffectiveHours(s.EffectiveProductiveHours, s.TotalHours);
            var (hc, cc) = (_shiftCols.FirstOrDefault(x => x.id == s.Id).hoursCol, _shiftCols.FirstOrDefault(x => x.id == s.Id).capCol);
            if (hc == null) continue;
            double cap = hourly > 0 ? hourly * hours : 0;
            row[hc] = hours.ToString("0.#");
            row[cc] = cap > 0 ? cap.ToString("0.#") : "—";
            day += cap;
        }
        row["📅 طاقة اليوم كاملة"] = day > 0 ? day.ToString("0.#") : "—";
    }

    /// <summary>تعديل «الإنتاج بالساعة» فقط — يُعاد حساب الصف ويُحفظ؛ أي قيمة غير صالحة تُرفض.</summary>
    private void CapsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Cancel) return;
        if (e.Column?.Header?.ToString() != HourlyCol) return;
        if (e.Row?.Item is not System.Data.DataRowView rv) return;
        var row = rv.Row;
        // ننتظر التزام التحرير حتى تصل القيمة الجديدة إلى الصف
        Dispatcher.BeginInvoke(new Action(() =>
        {
            int id = (int)row[IdCol];
            string txt = (row[HourlyCol] as string ?? "").Trim();
            if (!double.TryParse(txt, out var rate) || rate <= 0)
            {
                AppContainer.Get<DialogService>().Error("الإنتاج بالساعة يجب أن يكون رقماً أكبر من صفر — لم يُحفظ التعديل.");
                BuildMatrix(SearchBox.Text);
                return;
            }
            try
            {
                var r = Caps().SaveHourlyRate(id, rate);
                if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); BuildMatrix(SearchBox.Text); return; }
                using var db = Db();
                var shifts = db.Shifts.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Id).ToList();
                var p = db.Products.AsNoTracking().First(x => x.Id == id);
                FillRow(row, id, p.ProductCode, p.ProductNameAr, p.UnitOfMeasure, rate, shifts);
                AppContainer.Get<DialogService>().Info(r.Message);
            }
            catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Caps.SaveHourly"); }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// §B80: زر الإضافة يعمل فعلاً — نافذة استيراد صنف: اختيار صنف من أصناف الإنتاج التام
    /// (تُقرأ حية من شاشة الأصناف) + إدخال الإنتاج بالساعة — يُحفظ فوراً ويظهر الصف.
    /// </summary>
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var db = Db();
            var products = db.Products.AsNoTracking()
                .Where(p => p.IsActive && p.ItemType == "Finished")
                .OrderBy(p => p.ProductCode).ToList();
            if (products.Count == 0)
            { AppContainer.Get<DialogService>().Error("لا توجد أصناف إنتاج تام نشطة — أضف الأصناف التامة (مجموعة 002) من شاشة الأصناف أولاً."); return; }

            var win = new Window
            {
                Title = "➕ إضافة طاقة صنف — أصناف الإنتاج التام فقط",
                FlowDirection = FlowDirection.RightToLeft,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                SizeToContent = SizeToContent.WidthAndHeight,
                Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#F1F5F9")
            };
            var productBox = new ComboBox { Width = 320, MinHeight = 28 };
            foreach (var p in products) productBox.Items.Add($"{p.ProductCode} — {p.ProductNameAr} (الوحدة: {p.UnitOfMeasure})");
            productBox.SelectedIndex = 0;
            var rateBox = new TextBox { Width = 140, MinHeight = 28 };
            // §B110: الحفظ إجراء رئيسي كحلي + نمط للإلغاء + حشوات الثيم الموحد
            var saveBtn = new Button { Content = "💾 حفظ الطاقة", Margin = new Thickness(0, 0, 8, 0) };
            saveBtn.Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton");
            var cancelBtn = new Button { Content = "إلغاء" };
            cancelBtn.Style = (Style)System.Windows.Application.Current.FindResource("ErpButton");
            bool saved = false; int savedId = 0;
            saveBtn.Click += (_, _) =>
            {
                var p = products[productBox.SelectedIndex];
                if (!double.TryParse(rateBox.Text.Trim(), out var rate) || rate <= 0)
                { AppContainer.Get<DialogService>().Error("أدخل الإنتاج بالساعة رقماً أكبر من صفر."); return; }
                var r = Caps().SaveHourlyRate(p.Id, rate);
                if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
                saved = true; savedId = p.Id;
                win.Close();
            };
            cancelBtn.Click += (_, _) => win.Close();

            var sp = new StackPanel { Margin = new Thickness(14) };
            sp.Children.Add(new TextBlock { Text = "الصنف (من شاشة الأصناف — إنتاج تام فقط):", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(productBox);
            sp.Children.Add(new TextBlock { Text = "الإنتاج بالساعة (بوحدته):", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 4) });
            sp.Children.Add(rateBox);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            btns.Children.Add(saveBtn); btns.Children.Add(cancelBtn);
            sp.Children.Add(btns);
            win.Content = sp;
            win.ShowDialog();

            BuildMatrix(SearchBox.Text);
            if (saved)
            {
                // حدد الصف المحفوظ في الجدول ليراه المستخدم مباشرة
                foreach (System.Data.DataRowView rv in CapsGrid.Items)
                    if ((int)rv.Row[IdCol] == savedId) { CapsGrid.SelectedItem = rv; CapsGrid.ScrollIntoView(rv); break; }
                AppContainer.Get<DialogService>().Info("تم حفظ الإنتاج بالساعة — طاقة الورديات وطاقة اليوم محسوبة في الجدول.");
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Caps.Add"); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CapsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        AppContainer.Get<DialogService>().Info("كل تعديل صالح للإنتاج بالساعة يُحفظ فور إدخاله — الحسابات محدثة.");
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        // §B80: زر التعديل يعمل — التركيز على الجدول أولاً ثم بدء التحرير عبر الـ Dispatcher
        // (BeginEdit المباشر كان يفشل بصمت لأن لوحة المفاتيح ليست على الجدول).
        var rv = (CapsGrid.SelectedItem ?? CapsGrid.CurrentItem) as System.Data.DataRowView;
        if (rv == null) { AppContainer.Get<DialogService>().Error("اختر صنفاً من الجدول ثم اضغط تعديل — التحرير يتم في خلية «الإنتاج بالساعة»."); return; }
        CapsGrid.Focus();
        CapsGrid.ScrollIntoView(rv);
        CapsGrid.SelectedItem = rv;
        CapsGrid.CurrentCell = new DataGridCellInfo(rv, CapsGrid.Columns[3]);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            CapsGrid.Focus();
            CapsGrid.CurrentCell = new DataGridCellInfo(rv, CapsGrid.Columns[3]);
            CapsGrid.BeginEdit();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Search_Click(object sender, RoutedEventArgs e) => BuildMatrix(SearchBox.Text);
    private void Search_Changed(object sender, TextChangedEventArgs e) => BuildMatrix(SearchBox.Text);
    private void ShowAll_Click(object sender, RoutedEventArgs e) { SearchBox.Text = ""; BuildMatrix(""); }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        // §B80: الاعتماد على الصف الحالي (موضع التحرير/النقر) لا SelectedItem القديم —
        // مع ذكر رقم الصنف واسمه في رسالة التأكيد فلا يُحذف صنف غير المقصود أبداً.
        var rv = (CapsGrid.CurrentItem ?? CapsGrid.SelectedItem) as System.Data.DataRowView;
        if (rv == null)
        { AppContainer.Get<DialogService>().Error("اختر صنفاً من الجدول أولاً (انقر على صفه)."); return; }
        int id = (int)rv.Row[IdCol];
        string code = rv.Row["رقم الصنف"] as string ?? "";
        string name = rv.Row["الصنف"] as string ?? "";
        if (!AppContainer.Get<DialogService>().Confirm($"إزالة الإنتاج بالساعة للصنف:\n{code} — {name}\n\nستصير طاقاته غير معرَّفة في التخطيط. هل تريد المتابعة؟")) return;
        try
        {
            var r = Caps().ClearHourlyRate(id);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            BuildMatrix(SearchBox.Text);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Caps.Clear"); }
    }
}
