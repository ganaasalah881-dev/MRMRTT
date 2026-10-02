using DatesErp.Application.Services;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §قاعدة الوحدات الإلزامية على مستوى النظام — حراسة انحدار.
///
///   المواد الداخلة / الخام (001)   = KG
///   الإنتاج التام (002)             = CARTON
///   المخرجات الثانوية (003)         = KG
///
/// هذه الاختبارات موجودة لأن الإلزام رُفع مرة في B33 ومضى ذلك بلا ملاحظة.
/// أي محاولة لرفعه مجدداً ستُفشلها.
/// </summary>
public class UnitRuleTests
{
    private static (TestHost host, MasterDataService master, DatesErpDbContext db) NewHost()
    {
        var host = new TestHost();
        host.LoginAsAdmin();
        var scope = host.Services.CreateScope();
        return (host, scope.ServiceProvider.GetRequiredService<MasterDataService>(),
                        scope.ServiceProvider.GetRequiredService<DatesErpDbContext>());
    }

    [Theory]
    [InlineData("001", "Raw", "كرتون")]
    [InlineData("001", "Raw", "سلة")]
    [InlineData("001", "Raw", "كيس")]
    [InlineData("002", "Finished", "كرتون")]
    [InlineData("003", "ByProduct", "كجم")]
    [InlineData("003", "ByProduct", "كرتون")]
    public void Unit_Comes_From_Item_Definition_Not_Mandated_In_Code(string group, string type, string unit)
    {
        // §القاعدة المعتمدة: لا تُفرض الوحدات داخل الكود — الوحدة من تعريف الصنف في شاشة
        // الأصناف المركزية. ومجموعة الصنف وتصنيفه هما ما يحدد نوعه لا اسم الوحدة،
        // فـ«كرتون» قد تكون مجرد عبوة خام عند الاستلام.
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var r = master.SaveProductFull(null, $"U-{type}-{unit}", "صنف بوحدته المعرَّفة", group, type, unit, 10, 0, 0, null);
        Assert.True(r.Ok, r.Message);
        var saved = db.Products.Single(p => p.Id == r.Id);
        Assert.Equal(unit, saved.UnitOfMeasure);   // الوحدة كما عرَّفها المستخدم حرفياً
        Assert.Equal(group, saved.GroupCode);      // والمجموعة هي التي تحدد النوع
    }

    [Theory]
    [InlineData("001", "Raw", "كجم")]
    [InlineData("002", "Finished", "كرتون")]
    [InlineData("003", "ByProduct", "كجم")]
    public void Item_Unit_Correct_Is_Accepted(string group, string type, string rightUnit)
    {
        var (host, master, _) = NewHost();
        using (host)
        {
            var r = master.SaveProductFull(null, $"OK-{type}", "صنف بوحدته الصحيحة", group, type, rightUnit, 10, 5, 2, null);
            Assert.True(r.Ok, r.Message);
        }
    }

    [Fact]
    public void Blank_Unit_Takes_Group_Default()
    {
        var (host, master, db) = NewHost();
        using (host)
        {
            var raw = master.SaveProductFull(null, "BL-R", "خام بلا وحدة", "001", "Raw", null, 20, 0, 0, null);
            var fin = master.SaveProductFull(null, "BL-F", "تام بلا وحدة", "002", "Finished", null, 10, 5, 2, null);
            var by = master.SaveProductFull(null, "BL-B", "ثانوي بلا وحدة", "003", "ByProduct", null, 0, 0, 0, null);
            Assert.True(raw.Ok && fin.Ok && by.Ok);
            Assert.Equal("كجم", db.Products.Single(p => p.Id == raw.Id).UnitOfMeasure);
            Assert.Equal("كرتون", db.Products.Single(p => p.Id == fin.Id).UnitOfMeasure);
            Assert.Equal("كجم", db.Products.Single(p => p.Id == by.Id).UnitOfMeasure);
        }
    }

    [Fact]
    public void No_Fixed_Carton_Weight_Across_Products()
    {
        var (host, master, db) = NewHost();
        using (host)
        {
            var a = master.SaveProductFull(null, "CW-A", "كرتون 5", "002", "Finished", "كرتون", 5, 5, 1, null);
            var b = master.SaveProductFull(null, "CW-B", "كرتون 10", "002", "Finished", "كرتون", 10, 5, 2, null);
            var c = master.SaveProductFull(null, "CW-C", "كرتون 20", "002", "Finished", "كرتون", 20, 10, 2, null);
            Assert.True(a.Ok && b.Ok && c.Ok);
            Assert.Equal(5, UnitsPolicy.CartonWeight(db, a.Id, null));
            Assert.Equal(10, UnitsPolicy.CartonWeight(db, b.Id, null));
            Assert.Equal(20, UnitsPolicy.CartonWeight(db, c.Id, null));
        }
    }

    [Fact]
    public void Carton_Weight_Is_Derived_From_Molds_When_Not_Set_Directly()
    {
        var (host, master, db) = NewHost();
        using (host)
        {
            // وزن الكرتون = 0 لكن 5 قوالب × 2 كجم ← 10 كجم
            var r = master.SaveProductFull(null, "CW-M", "بالقوالب", "002", "Finished", "كرتون", 0, 5, 2, null);
            Assert.True(r.Ok, r.Message);
            Assert.Equal(10, UnitsPolicy.CartonWeight(db, r.Id, null));
        }
    }

    [Fact]
    public void Undefined_Carton_Weight_Is_Zero_Not_A_Silent_Constant()
    {
        // §كان Product.CartonWeightKg = 7.5 افتراضاً، وRawCartonWeight يرجع 7.5 عند الغياب.
        // القاعدة تمنع الوزن الثابت — فغير المعرَّف يبقى صفراً.
        var (host, master, db) = NewHost();
        using (host)
        {
            var r = master.SaveProductFull(null, "CW-N", "بلا تعريف", "002", "Finished", "كرتون", 0, 0, 0, null);
            Assert.True(r.Ok, r.Message);
            Assert.Equal(0, db.Products.Single(p => p.Id == r.Id).CartonWeightKg);
            Assert.Equal(0, UnitsPolicy.CartonWeight(db, r.Id, null));
            Assert.Equal(0, CartonService.RawCartonWeight(db, null, r.Id));
        }
    }

    [Fact]
    public void Recording_Cartons_Without_Defined_Weight_Is_Rejected()
    {
        var (host, _, db) = NewHost();
        using (host)
        {
            var prod = db.Products.AsNoTracking().First(p => p.ItemType == "Finished");
            // وزن غير معرَّف صراحةً
            var p = db.Products.Single(x => x.Id == prod.Id);
            p.CartonWeightKg = 0; p.MoldsCount = 0; p.MoldWeightKg = 0;
            db.SaveChanges();

            var ex = Assert.Throws<DomainException>(() =>
                UnitsPolicy.RequireCartonWeight(db, prod.Id, null, 100, "اختبار"));
            Assert.Contains("بلا وزن كرتون معرَّف", ex.Message);

            // وبلا كراتين لا اعتراض (مخرجات بالكيلو لا تحتاج وزن كرتون)
            UnitsPolicy.RequireCartonWeight(db, prod.Id, null, 0, "اختبار");
        }
    }

    [Theory]
    [InlineData("كرتون")]
    [InlineData("سلة")]
    [InlineData("كيس")]
    public void Raw_Receipt_Keeps_Its_Original_Packaging_Unit(string unit)
    {
        // §القاعدة المعتمدة: الخام قد يصل بأي عبوة — سلة/كيس/كرتون/غيرها.
        // فتُسجَّل وحدة الاستلام الأصلية كما وردت، والكيلو يبقى الوزن المرجعي.
        // ومجموعة الصنف (001) هي التي تحدد أنه خام لا اسم الوحدة.
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var rcv = scope.ServiceProvider.GetRequiredService<IReceivingService>();

        int raw = db.Products.AsNoTracking().First(p => p.ItemType == "Raw").Id;
        int cust = db.Customers.AsNoTracking().Select(c => c.Id).First();
        var r = rcv.SaveShipment(cust, null, null, new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = raw, QtyKg = 9200, PackageCount = 460, UnitWeightKg = 20, ReceiptUnit = unit } });
        Assert.True(r.Ok, r.Message);
        rcv.ApproveShipment(r.Id);

        var item = db.ShipmentItems.AsNoTracking().OrderByDescending(x => x.Id).First();
        Assert.Equal(unit, item.ReceiptUnit);        // الوحدة الأصلية محفوظة
        Assert.Equal(9200, item.TotalWeightKg, 1);   // والكيلو وزن مرجعي
        Assert.Equal(460, item.PackageCount);        // وعدد العبوات
        Assert.Equal("Raw", db.Products.Single(p => p.Id == raw).ItemType);   // والمجموعة تحدد النوع
    }

    [Fact]
    public void Closing_Rejects_Cartons_Contradicting_Carton_Weight()
    {
        // §هذا التحقق كان غائباً عن الإقفال (موجوداً في الخطة والأمر والتام فقط)
        var (host, _, db) = NewHost();
        using (host)
        {
            var prod = db.Products.AsNoTracking().First(p => p.ItemType == "Finished");
            var p = db.Products.Single(x => x.Id == prod.Id);
            p.CartonWeightKg = 10; p.MoldsCount = 5; p.MoldWeightKg = 2;
            db.SaveChanges();

            // 100 كرتون مقابل 9,500 كجم ← يقتضي 95 كجم/كرتون
            var ex = Assert.Throws<DomainException>(() =>
                UnitsPolicy.EnsureCartonKgConsistency(db, prod.Id, null, 9500, 100, "اختبار"));
            Assert.Contains("لا تطابق عدد الكراتين", ex.Message);

            // ومتطابقان ← يمر
            Assert.Equal(1000, UnitsPolicy.EnsureCartonKgConsistency(db, prod.Id, null, 1000, 100, "اختبار"));
        }
    }

    [Fact]
    public void Packaging_Definition_Is_Captured_For_History()
    {
        var (host, master, db) = NewHost();
        using (host)
        {
            var r = master.SaveProductFull(null, "PD-A", "تعريف تعبئة", "002", "Finished", "كرتون", 10, 5, 2, null);
            var (molds, moldW) = UnitsPolicy.PackagingDefinition(db, r.Id, null);
            Assert.Equal(5, molds);
            Assert.Equal(2, moldW);
        }
    }
}
