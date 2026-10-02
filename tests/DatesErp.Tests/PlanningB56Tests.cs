using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B56 — شاشة الخطط: اختيار الصنف التام من مجموعة التام فقط، والواجهة لا تنهار عند ضغط النافذة.
/// </summary>
public class PlanningB56Tests
{
    private static IPlanningService Plan(TestHost host)
        => host.Services.CreateScope().ServiceProvider.GetRequiredService<IPlanningService>();

    [Fact]
    public void Finished_Product_Choice_Lists_Finished_Only_Including_Custom_Finished_Group()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var master = host.Services.CreateScope().ServiceProvider.GetRequiredService<MasterDataService>();

        // مجموعة تامة مخصصة (B52) + صنف تابع لها
        Assert.True(master.SaveItemGroup(null, "005", "تمور تصدير فاخرة", "Finished", null).Ok);
        var customFin = master.SaveProductFull(null, "005-001", "خلاص تصدير", "005", "Finished", "كرتون", 10, 5, 2, null);
        Assert.True(customFin.Ok, customFin.Message);
        // خام وثانوي وتام قياسي
        var raw = master.SaveProductFull(null, "001-700", "خام تخطيط", "001", "Raw", "كجم", 0, 0, 0, null);
        var fin = master.SaveProductFull(null, "002-700", "سكري تخطيط", "002", "Finished", "كرتون", 10, 5, 2, null);
        var byp = master.SaveProductFull(null, "003-700", "عجينة تخطيط", "003", "ByProduct", "كجم", 0, 0, 0, null);
        Assert.True(raw.Ok && fin.Ok && byp.Ok);

        var list = Plan(host).GetFinishedProducts();
        var ids = list.Select(p => p.Id).ToList();
        Assert.Contains(fin.Id, ids);          // التام القياسي
        Assert.Contains(customFin.Id, ids);    // والتام بمجموعة مخصصة
        Assert.DoesNotContain(raw.Id, ids);    // لا الخام
        Assert.DoesNotContain(byp.Id, ids);    // ولا الثانوي
        Assert.All(list, p => Assert.Equal("Finished", p.ItemType));
    }

    [Fact]
    public void Planning_Screen_Horizontal_Specs_And_Filling_Items_Grid()
    {
        // §B61: المحددات صفوف أفقية ملتفة بلا سكرولر علوي يقتطع، وجدول البنود يملأ بلا ارتفاع ثابت.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        var xaml = File.ReadAllText(Path.Combine(dir!.FullName, "src/DatesErp.Desktop/Views/Screens/PlanningView.xaml"));
        // §B66: صفحة واحدة قابلة للتمرير — لا شيء يختفي على أي حجم نافذة
        Assert.Contains("<ScrollViewer", xaml);
        Assert.DoesNotContain("MaxHeight=\"252\"", xaml);
        int rg = xaml.IndexOf("x:Name=\"RowsGrid\"", StringComparison.Ordinal);
        Assert.True(rg >= 0, "جدول البنود مفقود.");
        string tag = xaml.Substring(rg, xaml.IndexOf('>', rg) - rg);
        Assert.DoesNotContain(" Height=", tag);
        Assert.Contains("MinHeight=\"260\"", xaml);          // ارتفاع مضمون لجدول البنود
        Assert.True(xaml.IndexOf("<WrapPanel", StringComparison.Ordinal) < rg,
            "المحددات يجب أن تسبق الجدول كصفوف أفقية ملتفة.");
    }

    [Fact]
    public void GetAvailableLots_Translates_On_Server_Providers()
    {
        // §B64: كان يستخدم AvailableQtyKg (غير مخزّنة) داخل الاستعلام فينفجر على SQL Server.
        using var host = new TestHost();
        host.LoginAsAdmin();
        var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var rcv = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var cust = master.SaveCustomer(null, "AL-1", "عميل الدفعات", "جملة", "777", "-", true);
        var raw = master.SaveProductFull(null, "001-AL", "خام الدفعات", "001", "Raw", "كجم", 20, 0, 0, null);
        var sh = rcv.SaveShipment(cust.Id, null, null, new System.Collections.Generic.List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = raw.Id, QtyKg = 9000, PackageCount = 450, UnitWeightKg = 20 } });
        rcv.ApproveShipment(sh.Id);

        var plan = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var lots = plan.GetAvailableLots(null);   // كان يرمي InvalidOperationException قبل الإصلاح
        var lot = Assert.Single(lots);
        Assert.Equal(9000, lot.RemainingKg, 1);
    }

    [Fact]
    public void Single_Customer_Filter_Shows_His_Lots_Only_Including_Legacy()
    {
        // §B67: عميل محدد ← دفعاته فقط (حتى دفعة قديمة بلا CustomerId تورث عميلها من السند)؛
        // عدة عملاء ← كل الدفعات.
        using var host = new TestHost();
        host.LoginAsAdmin();
        var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var rcv = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var plan = scope.ServiceProvider.GetRequiredService<IPlanningService>();

        var a = master.SaveCustomer(null, "CA", "عميل ألف", "جملة", "1", "-", true);
        var b = master.SaveCustomer(null, "CB", "عميل باء", "جملة", "2", "-", true);
        var raw = master.SaveProductFull(null, "001-FL", "خام الفلترة", "001", "Raw", "كجم", 20, 0, 0, null);
        var shA = rcv.SaveShipment(a.Id, null, null, new System.Collections.Generic.List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = raw.Id, QtyKg = 5000, PackageCount = 250, UnitWeightKg = 20 } });
        rcv.ApproveShipment(shA.Id);
        var shB = rcv.SaveShipment(b.Id, null, null, new System.Collections.Generic.List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = raw.Id, QtyKg = 7000, PackageCount = 350, UnitWeightKg = 20 } });
        rcv.ApproveShipment(shB.Id);

        // محاكاة بيانات قديمة: دفعة عميل ألف بلا CustomerId
        var lotA = db.Lots.Single(l => l.ShipmentId == shA.Id);
        lotA.CustomerId = null;
        db.SaveChanges();

        var forA = plan.GetAvailableLots(a.Id);
        var lot = Assert.Single(forA);               // دفعة ألف تظهر رغم نقص العميل (عبر السند)
        Assert.Equal(5000, lot.RemainingKg, 1);
        Assert.DoesNotContain(plan.GetAvailableLots(b.Id), l => l.LotId == lotA.Id); // ولا تسرب لغيره
        var forB = plan.GetAvailableLots(b.Id);
        Assert.Single(forB);                          // دفعة باء لباء فقط
        Assert.Equal(2, plan.GetAvailableLots(null).Count); // عدة عملاء ← الكل
    }

    [Fact]
    public void Exhausted_Items_Are_Not_Plannable_From_That_Lot()
    {
        // §B68: صنف خُصم كل متاح الدفعة لخططه لا يظهر ضمن الأصناف القابلة للتخطيط منها.
        using var host = new TestHost();
        host.LoginAsAdmin();
        var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var rcv = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var planSvc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var cust = master.SaveCustomer(null, "CX", "عميل س", "جملة", "3", "-", true);
        var raw = master.SaveProductFull(null, "001-EX", "خام النفاد", "001", "Raw", "كجم", 20, 0, 0, null);
        var f1 = master.SaveProductFull(null, "002-EX1", "تام مستهلك", "002", "Finished", "كرتون", 10, 5, 2, null, raw.Id);
        var f2 = master.SaveProductFull(null, "002-EX2", "تام متاح", "002", "Finished", "كرتون", 10, 5, 2, null, raw.Id);
        var sh = rcv.SaveShipment(cust.Id, null, null, new System.Collections.Generic.List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = raw.Id, QtyKg = 4000, PackageCount = 200, UnitWeightKg = 20 } });
        rcv.ApproveShipment(sh.Id);
        var lot = db.Lots.Single(l => l.ShipmentId == sh.Id).Id;

        // خطة تستهلك كل الدفعة لصالح f1
        TestCapacityFixture.DefineMissingRates(host);
        var res = planSvc.SavePlan("استهلاك", "Daily", "2026-09-01", "2026-09-01", 1, 1,
            new System.Collections.Generic.List<PlanItemDto>
            { new() { SourceType = "FromReceiving", LotId = lot, CustomerId = cust.Id, ProductId = f1.Id, PlannedCartons = 400, PlannedQtyKg = 4000 } });
        Assert.True(res.Ok, res.Message);

        var plannable = planSvc.GetPlannableProducts(lot).Select(p => p.Id).ToList();
        Assert.DoesNotContain(f1.Id, plannable);   // نفذ رصيده من الدفعة ← لا يظهر
        Assert.Contains(f2.Id, plannable);          // ما زال متاحاً ← يظهر
    }

    [Fact]
    public void Planning_Is_One_Screen_And_Plans_Search_Is_A_Popup()
    {
        // §شاشة الخطط الموحّدة: أُلغي مستطيل «الخطط السابقة» الدائم الذي كان يأكل ارتفاع الشاشة،
        // وصار البحث في نافذة منبثقة تُنزل الخطة بجميع بنودها للواجهة الرئيسية بالنقر المزدوج.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        string xaml = File.ReadAllText(Path.Combine(dir!.FullName, "src/DatesErp.Desktop/Views/Screens/PlanningView.xaml"));
        string cs = File.ReadAllText(Path.Combine(dir.FullName, "src/DatesErp.Desktop/Views/Screens/PlanningView.xaml.cs"));
        string popup = File.ReadAllText(Path.Combine(dir.FullName, "src/DatesErp.Desktop/Views/PlanSearchWindow.cs"));

        // شاشة واحدة: لا شبكة خطط ولا مربع بحث دائم داخل الواجهة
        Assert.DoesNotContain("PlansGrid", xaml);
        Assert.DoesNotContain("PlansSearchBox", xaml);
        Assert.DoesNotContain("PlansGrid", cs);
        Assert.DoesNotContain("PlansSearchBox", cs);

        // والبحث صار نافذة منبثقة
        Assert.Contains("OpenPlansSearch", cs);
        Assert.Contains("new Views.PlanSearchWindow(", cs);
        Assert.Contains("ShowDialog()", cs);
        // والنقر المزدوج في المنبثق يُنزل الخطة ببنودها في الواجهة الرئيسية
        Assert.Contains("OpenPlan(id)", cs);
        Assert.Contains("class PlanSearchWindow", popup);
        Assert.Contains("MouseDoubleClick", popup);
        Assert.Contains("SelectedPlanId", popup);
        Assert.Contains("DialogResult = true", popup);
    }

    [Fact]
    public void Plans_List_Loads_In_Two_Queries_Regardless_Of_Plan_Count()
    {
        // §كفاءة (v1.50.20): كانت قائمة الخطط تجري 3 استعلامات فرعية لكل خطة (عملاء/بنود/وزن)
        // — صارت إحصاءات GROUP BY واحدة + استعلام الخطط = استعلامان مهما كبر العدد.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        string cs = File.ReadAllText(Path.Combine(dir!.FullName, "src/DatesErp.Desktop/Views/Screens/PlanningView.xaml.cs"));
        int i = cs.IndexOf("private void RefreshPlansList", StringComparison.Ordinal);
        Assert.True(i >= 0, "RefreshPlansList مفقودة");
        int end = cs.IndexOf("catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, \"Planning.List\")", i, StringComparison.Ordinal);
        string body = cs[i..(end > i ? end : cs.Length)];
        Assert.Contains("GroupBy(i => i.PlanId)", body);
        Assert.Contains("ToDictionary(x => x.PlanId)", body);
        Assert.Contains("AsNoTracking", body);
        Assert.DoesNotContain("db.ProductionPlanItems.Where(i => i.PlanId == p.Id", body);
    }

}
