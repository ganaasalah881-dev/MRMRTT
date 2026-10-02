using System.Windows;
using System.Windows.Controls;
using DatesErp.Application.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views;

/// <summary>§إدارة المواد المساعدة بلا ثوابت: مجموعات + مواد بوحدات حرة + معادلات بأوضاع + مواصفات عملاء.</summary>
public partial class AuxAdminWindow : Window
{
    public AuxAdminWindow()
    {
        InitializeComponent();
        FModeBox.Items.Add("PerCarton — لكل كرتون منتج");
        FModeBox.Items.Add("PerHour — لكل ساعة (اختياري غير مفروض)");
        FModeBox.Items.Add("Actual — إدخال فعلي عند الإقفال (ديزل/وقود)");
        FModeBox.Items.Add("Unused — غير مستخدمة");
        FModeBox.SelectedIndex = 0;
        LoadUnits();
        Loaded += (_, _) => RefreshAll();
    }

    private MasterDataService Svc()
    {
        var scope = AppContainer.NewScope();
        return scope.ServiceProvider.GetRequiredService<MasterDataService>();
    }

    private void LoadUnits()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErp.Infrastructure.Persistence.DatesErpDbContext>();
            foreach (var u in db.UnitsOfMeasure.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Id).Select(x => x.UnitNameAr).ToList())
                if (!MatUnitBox.Items.Contains(u)) MatUnitBox.Items.Add(u);
        }
        catch { }
    }

    private void RefreshAll()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            GrpGrid.ItemsSource = db.AuxGroups.AsNoTracking().OrderBy(g => g.Id)
                .Select(g => new { g.Id, Code = g.GroupCode, Name = g.GroupNameAr, Status = g.IsActive ? "نشطة 🟢" : "موقوفة ⚪" }).ToList();
            MatGrpBox.ItemsSource = db.AuxGroups.AsNoTracking().Where(g => g.IsActive).ToList();
            MatGrid.ItemsSource = db.AuxiliaryMaterials.AsNoTracking().OrderBy(m => m.Id)
                .Select(m => new
                {
                    m.Id, Code = m.MaterialCode, Name = m.MaterialNameAr,
                    Group = db.AuxGroups.Where(g => g.GroupCode == m.GroupCode).Select(g => g.GroupNameAr).FirstOrDefault() ?? m.MaterialCategory,
                    m.UnitOfMeasure, m.QualityGrade, m.LastCost, Status = m.IsActive ? "نشطة 🟢" : "موقوفة ⚪"
                }).ToList();
            FProdBox.ItemsSource = db.Products.AsNoTracking().Where(p => p.GroupCode == "002" && p.IsActive).ToList();
            FMatBox.ItemsSource = db.AuxiliaryMaterials.AsNoTracking().Where(m => m.IsActive).ToList();
            FormulaGrid.ItemsSource = db.ConsumptionFormulas.AsNoTracking().OrderBy(f => f.Id)
                .Select(f => new
                {
                    f.Id,
                    Product = db.Products.Where(p => p.Id == f.ProductId).Select(p => p.ProductNameAr).FirstOrDefault(),
                    Material = db.AuxiliaryMaterials.Where(m => m.Id == f.MaterialId).Select(m => m.MaterialNameAr).FirstOrDefault(),
                    Mode = f.Mode == "PerCarton" ? "لكل كرتون" : f.Mode == "PerHour" ? "لكل ساعة" : f.Mode == "Actual" ? "إدخال فعلي" : "معطلة",
                    f.QtyPerUnit,
                    Kind = f.IsOptional ? "اختيارية" : "مطلوبة",
                    Status = f.IsActive ? "نشطة 🟢" : "موقوفة ⚪"
                }).ToList();
            SpecCustBox.ItemsSource = db.Customers.AsNoTracking().Where(c => c.IsActive).ToList();
            SpecMatBox.ItemsSource = db.AuxiliaryMaterials.AsNoTracking().Where(m => m.IsActive && m.GroupCode == "AG-CART").ToList();
            SpecGrid.ItemsSource = db.AuxCustomerSpecs.AsNoTracking().OrderBy(x => x.Id)
                .Select(x => new
                {
                    x.Id,
                    Customer = db.Customers.Where(c => c.Id == x.CustomerId).Select(c => c.CustomerName).FirstOrDefault(),
                    Material = db.AuxiliaryMaterials.Where(m => m.Id == x.MaterialId).Select(m => m.MaterialNameAr).FirstOrDefault(),
                    x.BrandName, x.UnitCost, x.Priority, Status = x.IsActive ? "نشطة 🟢" : "موقوفة ⚪"
                }).ToList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "AuxAdmin.Load"); }
    }

    private void SaveGroup_Click(object sender, RoutedEventArgs e)
    {
        var r = Svc().SaveAuxGroup(null, null, GrpNameBox.Text);
        if (r.Ok) { GrpNameBox.Text = ""; RefreshAll(); } else AppContainer.Get<DialogService>().Error(r.Message);
    }

    private void SaveMat_Click(object sender, RoutedEventArgs e)
    {
        double.TryParse(MatCostBox.Text, out var cost);
        var r = Svc().SaveAuxMaterial(null, null, MatNameBox.Text, (MatGrpBox.SelectedValue as string) ?? "AG-PACK",
            MatUnitBox.Text, MatQualityBox.Text, cost);
        if (r.Ok) { MatNameBox.Text = ""; RefreshAll(); } else AppContainer.Get<DialogService>().Error(r.Message);
    }

    private string ModeCode() => FModeBox.SelectedIndex switch { 1 => "PerHour", 2 => "Actual", 3 => "Unused", _ => "PerCarton" };

    private void SaveFormula_Click(object sender, RoutedEventArgs e)
    {
        if (FProdBox.SelectedValue is not int pid || FMatBox.SelectedValue is not int mid)
        { AppContainer.Get<DialogService>().Error("اختر المنتج والمادة."); return; }
        double.TryParse(FQtyBox.Text, out var q);
        var r = Svc().SaveFormulaEx(null, pid, mid, q, ModeCode(), FOptChk.IsChecked == true);
        if (r.Ok) RefreshAll(); else AppContainer.Get<DialogService>().Error(r.Message);
    }

    private void SaveSpec_Click(object sender, RoutedEventArgs e)
    {
        if (SpecCustBox.SelectedValue is not int cid) { AppContainer.Get<DialogService>().Error("اختر العميل."); return; }
        if (SpecMatBox.SelectedValue is not int mid) { AppContainer.Get<DialogService>().Error("اختر مادة الماركة."); return; }
        double.TryParse(SpecCostBox.Text, out var cost);
        var r = Svc().SaveAuxSpec(null, cid, mid, SpecBrandBox.Text, cost);
        if (r.Ok) { SpecBrandBox.Text = ""; RefreshAll(); } else AppContainer.Get<DialogService>().Error(r.Message);
    }
}
