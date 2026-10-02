using DatesErp.Core.Domain.Entities;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §ترقية البيانات المرجعية على القواعد القائمة (B50).
///
/// سبب هذه الاختبارات: البذر الأولي <see cref="DbSeeder.Seed"/> يتوقف من أول سطر إن وُجد مفتاح
/// "Seeded"، وأنواع نتائج الفحص تُبذر فقط إن كان الجدول فارغاً. فقاعدة أُنشئت قبل «قاعدة المصنع»
/// (B48) تبقى على التصنيف القديم — المنسم مخرج ثانوي، ولا «تمر سليم» ولا «عجينة» — ونسخ ملفات
/// النظام الجديدة وحدها لا يغيّر شيئاً داخل القاعدة القائمة. هذه الاختبارات تبني قاعدة بالحالة
/// القديمة فعلاً ثم تتحقق أن الترقية تصلحها بلا حذف أو تعديل لبيانات عرّفها المستخدم.
/// </summary>
public class ReferenceDataUpgradeTests
{
    private static DatesErpDbContext Db(TestHost host)
        => new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);

    /// <summary>
    /// يعيد القاعدة إلى حالة ما قبل B48: المنسم مخرج ثانوي بالكجم، ولا «تمر سليم» ولا «عجينة»،
    /// ولا عجينة في بطاقة الأصناف الثانوية. (TestHost يبذر الحالة الجديدة، فنرجعها يدوياً.)
    /// </summary>
    private static void RevertToPreB48State(TestHost host)
    {
        using var db = Db(host);
        int? uKg = db.UnitsOfMeasure.Where(u => u.UnitNameAr == "كجم").Select(u => (int?)u.Id).First();

        var saleem = db.InspectionResultTypes.FirstOrDefault(t => t.Code == "RT-SALEEM");
        if (saleem != null) db.InspectionResultTypes.Remove(saleem);
        var ajeena = db.InspectionResultTypes.FirstOrDefault(t => t.Code == "RT-3AJEENAH");
        if (ajeena != null) db.InspectionResultTypes.Remove(ajeena);

        var monsam = db.InspectionResultTypes.Single(t => t.Code == "RT-MONSAM");
        monsam.NameAr = "منسم";                                  // الاسم القديم قبل B48
        monsam.ResultKind = InspectionResultType.KindByProduct;  // كان مخرجاً ثانوياً
        monsam.IsByProduct = true;
        monsam.IsFinishedGood = false;
        monsam.UnitId = uKg;
        monsam.UnitLabel = "كجم";

        var bpAjeena = db.ByProducts.FirstOrDefault(b => b.ByProductCode == "BP-3AJEENAH");
        if (bpAjeena != null) db.ByProducts.Remove(bpAjeena);

        db.SaveChanges();
    }

    [Fact]
    public void PreB48Db_MonsamIsReclassifiedToFinishedGood()
    {
        using var host = new TestHost();
        RevertToPreB48State(host);

        using (var db = Db(host))
        {
            var changes = DbSeeder.UpgradeReferenceData(db);
            Assert.NotEmpty(changes);
        }

        using var check = Db(host);
        var monsam = check.InspectionResultTypes.Single(t => t.Code == "RT-MONSAM");
        Assert.Equal(InspectionResultType.KindAccepted, monsam.ResultKind);
        Assert.True(monsam.IsFinishedGood, "المنسم منتج تام وفق قاعدة المصنع، لا مخرج ثانوي.");
        Assert.False(monsam.IsByProduct);
        Assert.True(monsam.EntersInventory);
        Assert.Equal("كرتون", monsam.UnitLabel);
        Assert.Equal("منسم", monsam.NameAr); // الاسم لا يُفرض — تعديل المستخدم يبقى
    }

    [Fact]
    public void PreB48Db_MissingSaleemAndAajeenaAreAdded()
    {
        using var host = new TestHost();
        RevertToPreB48State(host);

        using (var db = Db(host)) DbSeeder.UpgradeReferenceData(db);

        using var check = Db(host);
        var saleem = check.InspectionResultTypes.Single(t => t.Code == "RT-SALEEM");
        Assert.Equal(InspectionResultType.KindAccepted, saleem.ResultKind);
        Assert.True(saleem.IsFinishedGood);
        Assert.Equal("كرتون", saleem.UnitLabel);

        var ajeena = check.InspectionResultTypes.Single(t => t.Code == "RT-3AJEENAH");
        Assert.Equal(InspectionResultType.KindByProduct, ajeena.ResultKind);
        Assert.True(ajeena.IsByProduct);
        Assert.Equal("كجم", ajeena.UnitLabel);
    }

    [Fact]
    public void PreB48Db_AajeenaIsAddedToTheByProductsMaster()
    {
        // شاشة الإقفال والتقارير تبني أعمدة المخرجات الثانوية من بطاقة ByProducts وحدها،
        // فبدون هذا الصف لا يمكن إدخال العجينة في الإقفال أصلاً.
        using var host = new TestHost();
        RevertToPreB48State(host);

        using (var db = Db(host))
        {
            Assert.DoesNotContain(db.ByProducts.ToList(), b => b.ByProductNameAr == "عجينة");
            DbSeeder.UpgradeReferenceData(db);
        }

        using var check = Db(host);
        var bp = check.ByProducts.Single(b => b.ByProductNameAr == "عجينة");
        Assert.Equal("كجم", bp.UnitOfMeasure);
        Assert.True(bp.IsActive);
    }

    [Fact]
    public void Upgrade_RunsOnce_AndSecondRunChangesNothing()
    {
        using var host = new TestHost();
        RevertToPreB48State(host);

        List<string> first;
        using (var db = Db(host)) first = DbSeeder.UpgradeReferenceData(db);
        Assert.NotEmpty(first);

        List<string> second;
        using (var db = Db(host)) second = DbSeeder.UpgradeReferenceData(db);
        Assert.Empty(second);

        using var check = Db(host);
        Assert.Equal(1, check.InspectionResultTypes.Count(t => t.Code == "RT-SALEEM"));
        Assert.Equal(1, check.InspectionResultTypes.Count(t => t.Code == "RT-3AJEENAH"));
        Assert.Equal(1, check.ByProducts.Count(b => b.ByProductNameAr == "عجينة"));
    }

    [Fact]
    public void UserModifiedMonsam_IsNotOverwritten()
    {
        // قاعدة «لا ثوابت في الشاشات»: إن غيّر المستخدم تصنيف المنسم بنفسه فالنظام لا يفرض عليه.
        using var host = new TestHost();
        RevertToPreB48State(host);

        using (var db = Db(host))
        {
            var monsam = db.InspectionResultTypes.Single(t => t.Code == "RT-MONSAM");
            monsam.ResultKind = InspectionResultType.KindRejected; // تصنيف اختاره المستخدم
            monsam.IsByProduct = false;
            monsam.IsFinishedGood = false;
            monsam.NameAr = "منسم مرتجع";
            db.SaveChanges();
        }

        using (var db = Db(host)) DbSeeder.UpgradeReferenceData(db);

        using var check = Db(host);
        var after = check.InspectionResultTypes.Single(t => t.Code == "RT-MONSAM");
        Assert.Equal(InspectionResultType.KindRejected, after.ResultKind);
        Assert.False(after.IsFinishedGood);
        Assert.Equal("منسم مرتجع", after.NameAr);
    }

    [Fact]
    public void UserDefinedTypesAndDeletedOnes_AreRespected()
    {
        using var host = new TestHost();
        RevertToPreB48State(host);

        using (var db = Db(host))
        {
            // نوع عرّفه المستخدم بنفسه — يجب أن يبقى كما هو
            db.InspectionResultTypes.Add(new InspectionResultType
            {
                Code = "RT-CUSTOM", NameAr = "فرز خاص بالمصنع", ResultKind = InspectionResultType.KindByProduct,
                UnitLabel = "كجم", IsByProduct = true, EntersInventory = true, SortNo = 99
            });
            db.SaveChanges();
        }

        using (var db = Db(host)) DbSeeder.UpgradeReferenceData(db);

        using var check = Db(host);
        var custom = check.InspectionResultTypes.Single(t => t.Code == "RT-CUSTOM");
        Assert.Equal("فرز خاص بالمصنع", custom.NameAr);
        Assert.Equal(99, custom.SortNo);
        Assert.True(custom.IsByProduct);
    }

    [Fact]
    public void FreshDb_AlreadyCompliant_UpgradeAddsNothing()
    {
        // قاعدة جديدة بُذرت بقاعدة المصنع: الترقية لا تضيف ولا تكرر.
        using var host = new TestHost();

        List<string> changes;
        using (var db = Db(host)) changes = DbSeeder.UpgradeReferenceData(db);
        Assert.Empty(changes);

        using var check = Db(host);
        Assert.Equal(1, check.InspectionResultTypes.Count(t => t.Code == "RT-SALEEM"));
        Assert.Equal(1, check.InspectionResultTypes.Count(t => t.Code == "RT-3AJEENAH"));
        Assert.Equal(1, check.ByProducts.Count(b => b.ByProductNameAr == "عجينة"));
    }

    [Fact]
    public void Seed_Itself_CarriesTheMandatedRule()
    {
        // البذر الأولي للقاعدة الجديدة يجب أن يحمل القاعدة مباشرةً — لا يعتمد على الترقية.
        using var host = new TestHost();
        using var db = Db(host);

        var saleem = db.InspectionResultTypes.Single(t => t.Code == "RT-SALEEM");
        var monsam = db.InspectionResultTypes.Single(t => t.Code == "RT-MONSAM");
        Assert.True(saleem.IsFinishedGood);
        Assert.True(monsam.IsFinishedGood);
        Assert.False(monsam.IsByProduct);
        Assert.Contains(db.ByProducts.ToList(), b => b.ByProductNameAr == "عجينة");
        Assert.DoesNotContain(db.InspectionResultTypes.ToList(),
            t => t.IsByProduct && (t.NameAr.Contains("منسم")));
    }
}
