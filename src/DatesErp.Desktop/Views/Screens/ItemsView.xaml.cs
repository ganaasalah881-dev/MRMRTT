using System.Windows;
using System.Windows.Controls;
using DatesErp.Application.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §B72: شاشة الأصناف المعاد بناؤها من الصفر.
/// خام (001): رقم المجموعة · رقم الصنف الآلي · الاسم · الوحدة فقط.
/// تام (002): + عدد القوالب · وزن القالب · وزن الكرتون الآلي · الصنف الخام المصدر.
/// الأزرار الخمسة: إضافة · حفظ · تعديل · بحث · حذف.
/// </summary>
public partial class ItemsView : UserControl
{
    private int _editId;

    private sealed class GroupOpt
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public override string ToString() => $"{Code} — {Name}";
    }

    private sealed class SrcOpt
    {
        public int? Id { get; set; }
        public string Name { get; set; }
        public override string ToString() => Name;
    }

    public ItemsView()
    {
        InitializeComponent();
        Loaded += (_, _) => { LoadLookups(); Refresh(""); };
    }

    /// <summary>§الفحص الشامل: كانت إدارة المجموعات والفئات بلا نقطة دخول منذ إعادة بناء بطاقة الصنف —
    /// النافذة موجودة لكن لا شيء يفتحها. الزر هنا يعيد الوصول إلى SaveItemGroup/SaveItemCategory.</summary>
    private void ManageGroups_Click(object sender, RoutedEventArgs e)
    {
        new Views.GroupsAndCategoriesWindow { Owner = Window.GetWindow(this) }.ShowDialog();
        LoadLookups(); Refresh("");
    }

    private static DatesErpDbContext Db()
        => AppContainer.NewScope().ServiceProvider.GetRequiredService<DatesErpDbContext>();

    private static MasterDataService Svc()
        => AppContainer.NewScope().ServiceProvider.GetRequiredService<MasterDataService>();

    private void LoadLookups()
    {
        try
        {
            using var db = Db();
            var keepGroup = (GroupBox.SelectedItem as GroupOpt)?.Code;
            GroupBox.ItemsSource = db.ItemGroups.AsNoTracking().Where(g => g.IsActive)
                .OrderBy(g => g.GroupCode).Select(g => new GroupOpt { Code = g.GroupCode, Name = g.GroupNameAr }).ToList();
            if (keepGroup != null)
                for (int i = 0; i < GroupBox.Items.Count; i++)
                    if ((GroupBox.Items[i] as GroupOpt)?.Code == keepGroup) { GroupBox.SelectedIndex = i; break; }

            UnitBox.ItemsSource = db.UnitsOfMeasure.AsNoTracking().Where(u => u.IsActive)
                .OrderBy(u => u.UnitNameAr).Select(u => u.UnitNameAr).ToList();

            var keepSrc = (SourceBox.SelectedItem as SrcOpt)?.Id;
            SourceBox.Items.Clear();
            SourceBox.Items.Add(new SrcOpt { Id = null, Name = "— بدون —" });
            foreach (var r in db.Products.AsNoTracking().Where(p => (p.ItemType == "Raw" || p.GroupCode == "001") && p.IsActive).OrderBy(p => p.ProductCode))
                SourceBox.Items.Add(new SrcOpt { Id = r.Id, Name = $"{r.ProductNameAr} ({r.ProductCode})" });
            SourceBox.SelectedIndex = 0;
            if (keepSrc != null)
                for (int i = 0; i < SourceBox.Items.Count; i++)
                    if ((SourceBox.Items[i] as SrcOpt)?.Id == keepSrc) { SourceBox.SelectedIndex = i; break; }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Items.Lookups"); }
    }

    private void Group_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CodeBox == null) return;
        var g = GroupBox.SelectedItem as GroupOpt;
        if (g == null) return;
        bool finished = g.Code == "002";
        FinishedFields.Visibility = finished ? Visibility.Visible : Visibility.Collapsed;
        // §47 — لا يُعرض رقم مقترح عند الجديد: الترقيم يُولَّد في الخادم لحظة الحفظ
        // منعاً لتعارض مستخدمين ينشئان معاً. الرقم الحقيقي يظهر في رسالة الحفظ والجدول.
        if (_editId == 0) CodeBox.Text = "";
        if (finished) MoldInputs_Changed(null, null);
    }

    private void MoldInputs_Changed(object sender, TextChangedEventArgs e)
    {
        if (CartonWBox == null || MoldsBox == null || MoldWBox == null) return;
        if (double.TryParse(MoldsBox.Text, out var m) && double.TryParse(MoldWBox.Text, out var mw) && m > 0 && mw > 0)
            CartonWBox.Text = (m * mw).ToString("0.###");
    }

    private void Refresh(string term)
    {
        try
        {
            using var db = Db();
            // §B80: ToList إلزامية — الاستعلامات الفرعية (اسم الصنف الخام) داخل Select
            // كانت تنفذ والقارئ الأول مفتوح، فتنهار على SQL Server (بلا MARS) برسالة
            // «open DataReader» — سبب «رسالة الخطأ عند الدخول/البحث» والجدول الفارغ.
            var all = db.Products.AsNoTracking().OrderBy(p => p.ProductCode).ToList().AsEnumerable();
            if (!string.IsNullOrWhiteSpace(term))
                all = all.Where(p => (p.ProductNameAr ?? "").Contains(term) || (p.ProductCode ?? "").Contains(term));
            // أسماء الأصناف الخام تُجلب مرة واحدة (قاموس) بدل استعلام لكل صف
            var nameById = db.Products.AsNoTracking().Select(x => new { x.Id, x.ProductNameAr }).ToList()
                .ToDictionary(x => x.Id, x => x.ProductNameAr);
            ItemsGrid.ItemsSource = all.Select(p => new
            {
                p.Id,
                Code = p.ProductCode,
                Name = p.ProductNameAr,
                Group = p.GroupCode,
                Unit = p.UnitOfMeasure,
                Treat = p.RequiresTreatment ? "نعم 🧪" : "لا",
                CartonW = p.CartonWeightKg > 0 ? p.CartonWeightKg.ToString("0.###") : "—",
                Source = p.SourceProductId != null && nameById.TryGetValue(p.SourceProductId.Value, out var srcName)
                    ? srcName ?? "—"
                    : "—",
                Status = p.IsActive ? "نشط 🟢" : "موقوف ⚪"
            }).ToList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Items.Grid"); }
    }

    private void Add_Click(object sender, RoutedEventArgs e) => ResetForNewItem();

    /// <summary>§B80/§47: تهيئة النموذج لصنف جديد — حقول فارغة (بلا قوالب مفروضة) وبلا رقم مسبق؛ الرقم يُولَّد عند الحفظ.</summary>
    private void ResetForNewItem()
    {
        _editId = 0;
        NameBox.Text = "";
        MoldsBox.Text = "";
        MoldWBox.Text = "";
        CartonWBox.Text = "";
        YieldBox.Text = "";
        UnitBox.SelectedIndex = -1;
        if (SourceBox.Items.Count > 0) SourceBox.SelectedIndex = 0;
        CodeBox.Text = ""; // §47 — الرقم يُولَّد في الخادم عند الحفظ لا مسبقاً في الشاشة
        NameBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var g = GroupBox.SelectedItem as GroupOpt;
            if (g == null) { AppContainer.Get<DialogService>().Error("اختر المجموعة أولاً."); return; }
            if (string.IsNullOrWhiteSpace(NameBox.Text)) { AppContainer.Get<DialogService>().Error("أدخل اسم الصنف."); return; }
            if (string.IsNullOrWhiteSpace(UnitBox.Text as string)) { AppContainer.Get<DialogService>().Error("اختر الوحدة من قائمة الوحدات."); return; }
            bool finished = g.Code == "002";
            int molds = 0; double mw = 0, cw = 0;
            if (finished)
            {
                int.TryParse(MoldsBox.Text, out molds);
                double.TryParse(MoldWBox.Text, out mw);
                double.TryParse(CartonWBox.Text, out cw);
                // §1.50.67 FIX متوسط: تحذير فوري عند تناقض وزن الكرتون مع وزن العبوة الافتراضية — سبب خطأ سكري فاخر 0.5 vs 2 كجم
                if (cw > 0)
                {
                    try
                    {
                        using var dbChk = Db();
                        var prodId = _editId > 0 ? _editId : 0;
                        var linkedPacks = dbChk.ProductShiftCapacities.AsNoTracking()
                            .Where(c => c.ProductId == prodId)
                            .Select(c => c.PackagingTypeId).Where(id => id != null).Distinct().ToList();
                        var packWeights = dbChk.PackagingTypes.AsNoTracking()
                            .Where(p => linkedPacks.Contains(p.Id) && p.UnitWeightKg > 0)
                            .Select(p => new { p.PackageNameAr, p.UnitWeightKg }).ToList();
                        // أيضاً العبوة الافتراضية للصنف
                        var defPackId = dbChk.Products.AsNoTracking().Where(p => p.Id == prodId).Select(p => p.DefaultPackagingTypeId).FirstOrDefault();
                        if (defPackId != null)
                        {
                            var defW = dbChk.PackagingTypes.AsNoTracking().Where(p => p.Id == defPackId).Select(p => new { p.PackageNameAr, p.UnitWeightKg }).FirstOrDefault();
                            if (defW != null) packWeights.Add(defW);
                        }
                        foreach (var pw in packWeights)
                        {
                            double diff = Math.Abs(cw - pw.UnitWeightKg);
                            double tol = Math.Max(0.5, cw * 0.05);
                            if (diff > tol)
                            {
                                if (!AppContainer.Get<DialogService>().Confirm($"⚠️ تنبيه: وزن الكرتون المدخل ({cw:N1} كجم) يختلف عن وزن العبوة «{pw.PackageNameAr}» ({pw.UnitWeightKg:N1} كجم) المرتبطة بهذا الصنف.\nهذا التناقض هو سبب خطأ «كمية الكيلو لا تطابق عدد الكراتين» في خطط الإنتاج.\n\nهل تريد المتابعة بالحفظ على أي حال؟"))
                                    return;
                                break;
                            }
                        }
                    }
                    catch { }
                }
            }
            int? srcId = finished ? (SourceBox.SelectedItem as SrcOpt)?.Id : null;
            if (finished && srcId == null) { AppContainer.Get<DialogService>().Error("اختر الصنف المصدر (الخام) لربط الصنف التام قبل الحفظ."); return; }
            // §B85/H3: معامل الإنتاجية اختياري — موجب فقط (خارج÷داخل)، والفراغ يُبقي القديم
            double? yf = null;
            if (finished && !string.IsNullOrWhiteSpace(YieldBox.Text))
            {
                if (!double.TryParse(YieldBox.Text, out var yfv) || yfv <= 0)
                { AppContainer.Get<DialogService>().Error("معامل الإنتاجية يجب أن يكون رقماً موجباً (مثال: 1.05) أو يُترك فارغاً."); return; }
                yf = yfv;
            }
            // §47 — لا ترقيم في العميل: الفراغ يُولِّد له الخادم رقماً فريداً داخل معاملة الحفظ
            var r = Svc().SaveProductFull(_editId > 0 ? _editId : null, CodeBox.Text.Trim(), NameBox.Text.Trim(),
                g.Code, finished ? "Finished" : "Raw", UnitBox.Text as string, cw, molds, mw, null, srcId, null, yf);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            LoadLookups();   // §v1.50.33: قائمة «الصنف الخام المصدر» تتحدث فوراً — الصنف الخام الجديد يظهر بلا إغلاق الشاشة
            Refresh(SearchBox.Text);
            // §B80: بعد الحفظ تتهيأ الشاشة لصنف جديد — لا تبقى بيانات الصنف المحفوظ في الحقول
            ResetForNewItem();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Items.Save"); }
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => LoadSelected();

    private void LoadSelected()
    {
        if (ItemsGrid.SelectedItem?.GetType().GetProperty("Id")?.GetValue(ItemsGrid.SelectedItem) is not int id)
        { AppContainer.Get<DialogService>().Error("اختر صنفاً من الجدول أولاً."); return; }
        using var db = Db();
        var p = db.Products.AsNoTracking().FirstOrDefault(x => x.Id == id);
        if (p == null) return;
        _editId = id;
        for (int i = 0; i < GroupBox.Items.Count; i++)
            if ((GroupBox.Items[i] as GroupOpt)?.Code == p.GroupCode) { GroupBox.SelectedIndex = i; break; }
        CodeBox.Text = p.ProductCode;
        NameBox.Text = p.ProductNameAr;
        UnitBox.SelectedItem = p.UnitOfMeasure;
        _ = p.RequiresTreatment; // §v1.50.33: خانة «يحتاج معالجة» أُزيلت من النموذج — القيمة المحفوظة تبقى كما هي (لا يمررها الحفظ)
        if (p.GroupCode == "002")
        {
            MoldsBox.Text = p.MoldsCount.ToString();
            MoldWBox.Text = p.MoldWeightKg.ToString();
            CartonWBox.Text = p.CartonWeightKg.ToString("0.###");
            YieldBox.Text = p.YieldFactor != null ? p.YieldFactor.Value.ToString("0.###") : "";
            for (int i = 0; i < SourceBox.Items.Count; i++)
                if ((SourceBox.Items[i] as SrcOpt)?.Id == p.SourceProductId) { SourceBox.SelectedIndex = i; break; }
        }
    }

    private void ItemsGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => LoadSelected();

    private void Search_Click(object sender, RoutedEventArgs e) => Refresh(SearchBox.Text);
    private void Search_Changed(object sender, TextChangedEventArgs e) => Refresh(SearchBox.Text);
    private void ShowAll_Click(object sender, RoutedEventArgs e) { SearchBox.Text = ""; Refresh(""); }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemsGrid.SelectedItem?.GetType().GetProperty("Id")?.GetValue(ItemsGrid.SelectedItem) is not int id)
        { AppContainer.Get<DialogService>().Error("اختر صنفاً من الجدول أولاً."); return; }
        if (!AppContainer.Get<DialogService>().Confirm("حذف الصنف المحدد؟ (إن كان مستخدماً في عمليات سيُوقَف بدل الحذف)")) return;
        try
        {
            var r = Svc().DeleteProductById(id);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            if (_editId == id) _editId = 0;
            Refresh(SearchBox.Text);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Items.Delete"); }
    }
}
