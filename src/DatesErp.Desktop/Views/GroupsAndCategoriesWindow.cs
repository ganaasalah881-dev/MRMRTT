using System.Windows;
using System.Windows.Controls;
using DatesErp.Application.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views;

/// <summary>
/// §B52 — نافذة المجموعات والفئات والمخرجات الثانوية (مستقلة عن شاشة الأصناف).
///
/// ما تغيّر عن السابق:
///  • **أُزيل حقل الوحدة من إضافة المجموعة** — الوحدات للأصناف وتُختار من قاموس الوحدات،
///    والمجموعة تحدد نوع الاستخدام فقط (خام/تام/ثانوي/تعبئة).
///  • أُضيفت أزرار حفظ/تعديل/بحث/تفعيل-إيقاف للمجموعات (كانت إضافة فقط).
///  • الكود اختياري: يُولَّد تلقائياً (005، 006...) إن تُرك فارغاً.
///  • منع تكرار اسم المجموعة والفئة، والتعطيل بدل الحذف للمستخدم.
///  • نُقلت إدارة المخرجات الثانوية إلى هنا من شاشة الأصناف (تبويب ثالث).
/// </summary>
public class GroupsAndCategoriesWindow : Window
{
    private int _editGroupId;
    private int _editCatId;

    public GroupsAndCategoriesWindow()
    {
        Title = "المجموعات والفئات";
        Width = 980; Height = 640; MinWidth = 800; MinHeight = 520;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // §B84/B8: إغلاق بـ Escape.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; }
        };

        // ── المجموعات (بلا حقل وحدة) ──
        var gCode = new TextBox { Width = 80 };
        var gName = new TextBox { Width = 220 };
        var gType = new ComboBox { Width = 170 };
        gType.Items.Add(new ComboBoxItem { Content = "خام (استلام)", Tag = "Raw" });
        gType.Items.Add(new ComboBoxItem { Content = "منتج تام (كرتون بيع)", Tag = "Finished" });
        gType.Items.Add(new ComboBoxItem { Content = "مخرج ثانوي", Tag = "ByProduct" });
        gType.Items.Add(new ComboBoxItem { Content = "تعبئة/مرتجع", Tag = "Pack" });
        gType.SelectedIndex = 0;
        var gSearch = new TextBox { Width = 160 };
        var gGrid = new DataGrid { Height = 240, IsReadOnly = true, RowHeight = 28, AutoGenerateColumns = false };
        gGrid.Columns.Add(new DataGridTextColumn { Header = "الكود", Binding = new System.Windows.Data.Binding("Code"), Width = 70 });
        gGrid.Columns.Add(new DataGridTextColumn { Header = "المجموعة", Binding = new System.Windows.Data.Binding("Name"), Width = 220 });
        gGrid.Columns.Add(new DataGridTextColumn { Header = "نوع الاستخدام", Binding = new System.Windows.Data.Binding("Type"), Width = 150 });
        gGrid.Columns.Add(new DataGridTextColumn { Header = "عدد الأصناف", Binding = new System.Windows.Data.Binding("Count"), Width = 90 });
        gGrid.Columns.Add(new DataGridTextColumn { Header = "الحالة", Binding = new System.Windows.Data.Binding("Status"), Width = 90 });
        var saveGroup = new Button { Content = "💾 حفظ المجموعة", Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton") };
        var newGroup = new Button { Content = "➕ جديدة", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), Margin = new Thickness(6, 0, 0, 0) };
        var editGroup = new Button { Content = "✏️ تعديل المحدد", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), Margin = new Thickness(6, 0, 0, 0) };
        var toggleGroup = new Button { Content = "🔁 تفعيل/إيقاف", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), Margin = new Thickness(6, 0, 0, 0) };

        // ── الفئات ──
        var cName = new TextBox { Width = 220 };
        var cSearch = new TextBox { Width = 160 };
        var cGrid = new DataGrid { Height = 240, IsReadOnly = true, RowHeight = 28, AutoGenerateColumns = false };
        cGrid.Columns.Add(new DataGridTextColumn { Header = "الكود", Binding = new System.Windows.Data.Binding("Code"), Width = 100 });
        cGrid.Columns.Add(new DataGridTextColumn { Header = "الفئة", Binding = new System.Windows.Data.Binding("Name"), Width = 240 });
        cGrid.Columns.Add(new DataGridTextColumn { Header = "عدد الأصناف", Binding = new System.Windows.Data.Binding("Count"), Width = 100 });
        cGrid.Columns.Add(new DataGridTextColumn { Header = "الحالة", Binding = new System.Windows.Data.Binding("Status"), Width = 90 });
        var saveCat = new Button { Content = "💾 حفظ الفئة", Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton") };
        var newCat = new Button { Content = "➕ جديدة", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), Margin = new Thickness(6, 0, 0, 0) };
        var editCat = new Button { Content = "✏️ تعديل المحدد", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), Margin = new Thickness(6, 0, 0, 0) };
        var toggleCat = new Button { Content = "🔁 تفعيل/إيقاف", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), Margin = new Thickness(6, 0, 0, 0) };

        // ── المخرجات الثانوية ──
        var bpName = new TextBox { Width = 180 };
        var bpUnit = new ComboBox { Width = 130, DisplayMemberPath = "UnitNameAr", SelectedValuePath = "Id" };
        var bpGrid = new DataGrid { Height = 240, IsReadOnly = true, RowHeight = 28, AutoGenerateColumns = false };
        bpGrid.Columns.Add(new DataGridTextColumn { Header = "الكود", Binding = new System.Windows.Data.Binding("Code"), Width = 110 });
        bpGrid.Columns.Add(new DataGridTextColumn { Header = "المخرج الثانوي", Binding = new System.Windows.Data.Binding("Name"), Width = 220 });
        bpGrid.Columns.Add(new DataGridTextColumn { Header = "الوحدة", Binding = new System.Windows.Data.Binding("Unit"), Width = 90 });
        bpGrid.Columns.Add(new DataGridTextColumn { Header = "الحالة", Binding = new System.Windows.Data.Binding("Status"), Width = 90 });
        var addBp = new Button { Content = "➕ إضافة مخرج", Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton") };
        var toggleBp = new Button { Content = "🔁 تفعيل/إيقاف", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), Margin = new Thickness(6, 0, 0, 0) };

        void Refresh()
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var term = (gSearch.Text ?? "").Trim();
            gGrid.ItemsSource = db.ItemGroups.AsNoTracking().OrderBy(g => g.GroupCode)
                .Where(g => term == "" || (g.GroupNameAr ?? "").Contains(term) || (g.GroupCode ?? "").Contains(term))
                .Select(g => new
                {
                    g.Id,
                    Code = g.GroupCode,
                    Name = g.GroupNameAr,
                    Type = g.GroupType == "Raw" ? "خام" : g.GroupType == "Finished" ? "منتج تام" : g.GroupType == "ByProduct" ? "مخرج ثانوي" : "تعبئة/مرتجع",
                    Count = db.Products.Count(p => p.GroupCode == g.GroupCode),
                    Status = g.IsActive ? "نشطة 🟢" : "موقوفة ⚪"
                }).ToList();
            var cterm = (cSearch.Text ?? "").Trim();
            cGrid.ItemsSource = db.ItemCategories.AsNoTracking().OrderBy(c => c.CategoryNameAr)
                .Where(c => cterm == "" || (c.CategoryNameAr ?? "").Contains(cterm))
                .Select(c => new
                {
                    c.Id,
                    Code = c.CategoryCode,
                    Name = c.CategoryNameAr,
                    Count = db.Products.Count(p => p.CategoryId == c.Id),
                    Status = c.IsActive ? "نشطة 🟢" : "موقوفة ⚪"
                }).ToList();
            bpGrid.ItemsSource = db.ByProducts.AsNoTracking().OrderBy(b => b.Id).Select(b => new
            { b.Id, Code = b.ByProductCode, Name = b.ByProductNameAr, Unit = b.UnitOfMeasure, Status = b.IsActive ? "نشط 🟢" : "موقوف ⚪" }).ToList();
            bpUnit.ItemsSource = db.UnitsOfMeasure.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.UnitNameAr).ToList();
        }

        string SelId(DataGrid g) => g.SelectedItem?.GetType().GetProperty("Id")?.GetValue(g.SelectedItem)?.ToString();

        saveGroup.Click += (_, _) =>
        {
            try
            {
                using var scope = AppContainer.NewScope();
                var svc = scope.ServiceProvider.GetRequiredService<MasterDataService>();
                var tag = (gType.SelectedItem as ComboBoxItem)?.Tag as string;
                var r = svc.SaveItemGroup(_editGroupId > 0 ? _editGroupId : null, gCode.Text, gName.Text, tag, null);
                if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
                _editGroupId = 0; gCode.Text = ""; gName.Text = "";
                Refresh();
            }
            catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Groups.Save"); }
        };
        newGroup.Click += (_, _) => { _editGroupId = 0; gCode.Text = ""; gName.Text = ""; };
        editGroup.Click += (_, _) =>
        {
            if (SelId(gGrid) is not string sid || !int.TryParse(sid, out var id)) { AppContainer.Get<DialogService>().Error("اختر مجموعة من الجدول."); return; }
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var g = db.ItemGroups.AsNoTracking().FirstOrDefault(x => x.Id == id);
            if (g == null) return;
            _editGroupId = id; gCode.Text = g.GroupCode; gName.Text = g.GroupNameAr;
            for (int i = 0; i < gType.Items.Count; i++)
                if ((gType.Items[i] as ComboBoxItem)?.Tag as string == g.GroupType) { gType.SelectedIndex = i; break; }
        };
        toggleGroup.Click += (_, _) =>
        {
            if (SelId(gGrid) is not string sid || !int.TryParse(sid, out var id)) { AppContainer.Get<DialogService>().Error("اختر مجموعة من الجدول."); return; }
            using var scope = AppContainer.NewScope();
            var r = scope.ServiceProvider.GetRequiredService<MasterDataService>().ToggleItemGroup(id);
            if (!r.Ok) AppContainer.Get<DialogService>().Error(r.Message);
            Refresh();
        };
        gSearch.TextChanged += (_, _) => Refresh();

        saveCat.Click += (_, _) =>
        {
            try
            {
                using var scope = AppContainer.NewScope();
                var svc = scope.ServiceProvider.GetRequiredService<MasterDataService>();
                var r = svc.SaveItemCategory(_editCatId > 0 ? _editCatId : null, null, cName.Text);
                if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
                _editCatId = 0; cName.Text = "";
                Refresh();
            }
            catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Categories.Save"); }
        };
        newCat.Click += (_, _) => { _editCatId = 0; cName.Text = ""; };
        editCat.Click += (_, _) =>
        {
            if (SelId(cGrid) is not string sid || !int.TryParse(sid, out var id)) { AppContainer.Get<DialogService>().Error("اختر فئة من الجدول."); return; }
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var c = db.ItemCategories.AsNoTracking().FirstOrDefault(x => x.Id == id);
            if (c == null) return;
            _editCatId = id; cName.Text = c.CategoryNameAr;
        };
        toggleCat.Click += (_, _) =>
        {
            if (SelId(cGrid) is not string sid || !int.TryParse(sid, out var id)) { AppContainer.Get<DialogService>().Error("اختر فئة من الجدول."); return; }
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var c = db.ItemCategories.FirstOrDefault(x => x.Id == id);
            if (c == null) return;
            var svc = scope.ServiceProvider.GetRequiredService<MasterDataService>();
            var r = svc.SaveItemCategory(id, null, c.CategoryNameAr, !c.IsActive);
            if (!r.Ok) AppContainer.Get<DialogService>().Error(r.Message);
            Refresh();
        };
        cSearch.TextChanged += (_, _) => Refresh();

        addBp.Click += (_, _) =>
        {
            var name = bpName.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name)) { AppContainer.Get<DialogService>().Error("أدخل اسم المخرج الثانوي."); return; }
            try
            {
                // §B110 — الحفظ عبر طبقة الخدمات (صلاحية + تدقيق + منع تكرار) بدل الكتابة المباشرة
                using var scope = AppContainer.NewScope();
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                string unit = bpUnit.SelectedValue is int bu
                    ? db.UnitsOfMeasure.AsNoTracking().Where(u => u.Id == bu).Select(u => u.UnitNameAr).FirstOrDefault() ?? "كجم"
                    : "كجم";
                var svc = scope.ServiceProvider.GetRequiredService<MasterDataService>();
                var r = svc.SaveByProduct(null, name, unit);
                if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
                bpName.Text = "";
                Refresh();
                AppContainer.Get<DialogService>().Info($"أُضيف المخرج الثانوي «{name}» — يظهر تلقائياً في شاشة الإقفال والتقارير.");
            }
            catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ByProducts.Add"); }
        };
        toggleBp.Click += (_, _) =>
        {
            if (SelId(bpGrid) is not string sid || !int.TryParse(sid, out var id)) { AppContainer.Get<DialogService>().Error("اختر مخرجاً من الجدول."); return; }
            try
            {
                // §B110 — قلب الحالة عبر الخدمة (صلاحية + تدقيق) بدل الكتابة المباشرة
                using var scope = AppContainer.NewScope();
                var svc = scope.ServiceProvider.GetRequiredService<MasterDataService>();
                var r = svc.ToggleByProduct(id);
                if (!r.Ok) AppContainer.Get<DialogService>().Error(r.Message);
                Refresh();
            }
            catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ByProducts.Toggle"); }
        };

        var groupsPanel = new StackPanel { Margin = new Thickness(14) };
        groupsPanel.Children.Add(new TextBlock
        {
            Text = "🗂️ المجموعات — بنيوية: تحدد نوع استخدام الصنف (خام/تام/ثانوي/تعبئة) ولا تحمل وحدة؛ الوحدة تُختار في بطاقة الصنف من قاموس الوحدات. الكود اختياري يُولَّد تلقائياً.",
            FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        });
        var grow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        grow.Children.Add(Field("الكود (تلقائي):", gCode));
        grow.Children.Add(Field("الاسم *:", gName));
        grow.Children.Add(Field("نوع الاستخدام:", gType));
        grow.Children.Add(Field("🔍 بحث:", gSearch));
        grow.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { saveGroup, newGroup, editGroup, toggleGroup }, Margin = new Thickness(8, 0, 0, 0) });
        groupsPanel.Children.Add(grow);
        groupsPanel.Children.Add(gGrid);

        var catsPanel = new StackPanel { Margin = new Thickness(14) };
        catsPanel.Children.Add(new TextBlock
        {
            Text = "🏷️ الفئات — تصنيف حر (سكري، خلاص، برحي، تصدير...) يربط الأصناف ولا يفرضه النظام ولا يؤثر على الوحدات أو الطاقة.",
            FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        });
        var crow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        crow.Children.Add(Field("الاسم *:", cName));
        crow.Children.Add(Field("🔍 بحث:", cSearch));
        crow.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { saveCat, newCat, editCat, toggleCat }, Margin = new Thickness(8, 0, 0, 0) });
        catsPanel.Children.Add(crow);
        catsPanel.Children.Add(cGrid);

        var bpPanel = new StackPanel { Margin = new Thickness(14) };
        bpPanel.Children.Add(new TextBlock
        {
            Text = "🌿 المخرجات الثانوية — تُهيأ هنا وتظهر تلقائياً في الإقفال والتقارير؛ وحدتها من قاموس الوحدات لا مفروضة.",
            FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        });
        var bprow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        bprow.Children.Add(Field("اسم المخرج الجديد *:", bpName));
        bprow.Children.Add(Field("وحدته (من القاموس):", bpUnit));
        bprow.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { addBp, toggleBp }, Margin = new Thickness(8, 0, 0, 0) });
        bpPanel.Children.Add(bprow);
        bpPanel.Children.Add(bpGrid);

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "🗂️ المجموعات", Content = groupsPanel });
        tabs.Items.Add(new TabItem { Header = "🏷️ الفئات", Content = catsPanel });
        tabs.Items.Add(new TabItem { Header = "🌿 المخرجات الثانوية", Content = bpPanel });
        Content = tabs;
        Loaded += (_, _) => Refresh();
    }

    private static StackPanel Field(string label, UIElement el)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        sp.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.Bold, FontSize = 11.5 });
        sp.Children.Add(el);
        return sp;
    }
}
