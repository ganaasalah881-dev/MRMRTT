using System.Windows;
using System.Windows.Controls;
using DatesErp.Application.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §B70: شاشة «متغيرات المخازن» العامة ضمن إعدادات النظام — بداية إعادة التصميم من الصفر.
/// كل متغير تبويب، وكل تبويب يحمل الأزرار الخمسة الإلزامية: إضافة · حفظ · تعديل · بحث · حذف.
/// المجموعات حالياً: رقم المجموعة + الاسم فقط.
/// </summary>
public partial class WarehouseVariablesView : UserControl
{
    private int _editGroupId;
    private int _editUnitId;

    public WarehouseVariablesView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshGroups("");
    }

    private static DatesErpDbContext Db()
        => AppContainer.NewScope().ServiceProvider.GetRequiredService<DatesErpDbContext>();

    private static MasterDataService Svc()
        => AppContainer.NewScope().ServiceProvider.GetRequiredService<MasterDataService>();

    // ═══════════════ المجموعات ═══════════════

    private void RefreshGroups(string term)
    {
        try
        {
            using var db = Db();
            var q = db.ItemGroups.AsNoTracking().OrderBy(g => g.GroupCode).AsEnumerable();
            if (!string.IsNullOrWhiteSpace(term))
                q = q.Where(g => (g.GroupNameAr ?? "").Contains(term) || (g.GroupCode ?? "").Contains(term));
            GroupsGrid.ItemsSource = q.Select(g => new
            {
                g.Id,
                Code = g.GroupCode,
                Name = g.GroupNameAr,
                Status = g.IsActive ? "نشطة 🟢" : "موقوفة ⚪"
            }).ToList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "WhVars.Groups"); }
    }

    private void GroupAdd_Click(object sender, RoutedEventArgs e)
    {
        _editGroupId = 0;
        GroupCodeBox.Text = "";
        GroupNameBox.Text = "";
        GroupNameBox.Focus();
    }

    private void GroupSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var r = Svc().SaveGroupMinimal(_editGroupId > 0 ? _editGroupId : null, GroupCodeBox.Text, GroupNameBox.Text);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            _editGroupId = 0; GroupCodeBox.Text = ""; GroupNameBox.Text = "";
            RefreshGroups(GroupSearchBox.Text);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "WhVars.GroupSave"); }
    }

    private void GroupEdit_Click(object sender, RoutedEventArgs e) => LoadSelectedGroup();

    private void LoadSelectedGroup()
    {
        if (GroupsGrid.SelectedItem?.GetType().GetProperty("Id")?.GetValue(GroupsGrid.SelectedItem) is not int id)
        { AppContainer.Get<DialogService>().Error("اختر مجموعة من الجدول أولاً."); return; }
        using var db = Db();
        var g = db.ItemGroups.AsNoTracking().FirstOrDefault(x => x.Id == id);
        if (g == null) return;
        _editGroupId = id;
        GroupCodeBox.Text = g.GroupCode;
        GroupNameBox.Text = g.GroupNameAr;
    }

    private void GroupsGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => LoadSelectedGroup();

    private void GroupSearch_Click(object sender, RoutedEventArgs e) => RefreshGroups(GroupSearchBox.Text);
    private void GroupSearch_Changed(object sender, TextChangedEventArgs e) => RefreshGroups(GroupSearchBox.Text);
    private void GroupShowAll_Click(object sender, RoutedEventArgs e) { GroupSearchBox.Text = ""; RefreshGroups(""); }

    private void GroupDelete_Click(object sender, RoutedEventArgs e)
    {
        if (GroupsGrid.SelectedItem?.GetType().GetProperty("Id")?.GetValue(GroupsGrid.SelectedItem) is not int id)
        { AppContainer.Get<DialogService>().Error("اختر مجموعة من الجدول أولاً."); return; }
        if (!AppContainer.Get<DialogService>().Confirm("حذف المجموعة المحددة؟ (إن كانت مستخدمة ستُوقَف بدل الحذف)")) return;
        try
        {
            var r = Svc().DeleteOrDisableItemGroup(id);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            RefreshGroups(GroupSearchBox.Text);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "WhVars.GroupDelete"); }
    }

    // ═══════════════ الوحدات: اسم فقط ═══════════════

    private void RefreshUnits(string term)
    {
        try
        {
            using var db = Db();
            var q = db.UnitsOfMeasure.AsNoTracking().OrderBy(u => u.Id).AsEnumerable();
            if (!string.IsNullOrWhiteSpace(term))
                q = q.Where(u => (u.UnitNameAr ?? "").Contains(term));
            UnitsGrid.ItemsSource = q.Select(u => new
            {
                u.Id,
                Name = u.UnitNameAr,
                Status = u.IsActive ? "نشطة 🟢" : "موقوفة ⚪"
            }).ToList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "WhVars.Units"); }
    }

    private void UnitAdd_Click(object sender, RoutedEventArgs e)
    {
        _editUnitId = 0;
        UnitNameBox.Text = "";
        UnitNameBox.Focus();
    }

    private void UnitSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var r = Svc().SaveUnitMinimal(_editUnitId > 0 ? _editUnitId : null, UnitNameBox.Text);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            _editUnitId = 0; UnitNameBox.Text = "";
            RefreshUnits(UnitSearchBox.Text);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "WhVars.UnitSave"); }
    }

    private void UnitEdit_Click(object sender, RoutedEventArgs e) => LoadSelectedUnit();

    private void LoadSelectedUnit()
    {
        if (UnitsGrid.SelectedItem?.GetType().GetProperty("Id")?.GetValue(UnitsGrid.SelectedItem) is not int id)
        { AppContainer.Get<DialogService>().Error("اختر وحدة من الجدول أولاً."); return; }
        using var db = Db();
        var u = db.UnitsOfMeasure.AsNoTracking().FirstOrDefault(x => x.Id == id);
        if (u == null) return;
        _editUnitId = id;
        UnitNameBox.Text = u.UnitNameAr;
    }

    private void UnitsGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => LoadSelectedUnit();

    private void UnitSearch_Click(object sender, RoutedEventArgs e) => RefreshUnits(UnitSearchBox.Text);
    private void UnitSearch_Changed(object sender, TextChangedEventArgs e) => RefreshUnits(UnitSearchBox.Text);
    private void UnitShowAll_Click(object sender, RoutedEventArgs e) { UnitSearchBox.Text = ""; RefreshUnits(""); }

    private void UnitDelete_Click(object sender, RoutedEventArgs e)
    {
        if (UnitsGrid.SelectedItem?.GetType().GetProperty("Id")?.GetValue(UnitsGrid.SelectedItem) is not int id)
        { AppContainer.Get<DialogService>().Error("اختر وحدة من الجدول أولاً."); return; }
        if (!AppContainer.Get<DialogService>().Confirm("حذف الوحدة المحددة؟ (إن كانت مستخدمة ستُوقَف بدل الحذف)")) return;
        try
        {
            var r = Svc().DeleteOrDisableUnit(id);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            RefreshUnits(UnitSearchBox.Text);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "WhVars.UnitDelete"); }
    }
}
