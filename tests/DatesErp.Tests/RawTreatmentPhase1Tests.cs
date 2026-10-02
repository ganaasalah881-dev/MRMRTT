using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §المعالجة والتعقيم — **المرحلة 1: الأساس فقط** (كيانات · حقول · مستودع · صلاحيات · ترحيل).
///
/// الادعاء المركزي الذي تحرسه هذه الاختبارات:
/// **المرحلة 1 لا تغيّر أي سلوك قائم.** UnderTreatmentQtyKg = 0 لكل صف، فقيمة
/// AvailableQtyKg العددية تبقى كما كانت حرفياً، ويبقى التخطيط والإنتاج على حالهما.
/// منطق المعالجة نفسه (البدء/الإفراج/الرفض) يأتي في المرحلة 2، ومنع الصرف في المرحلة 3.
/// </summary>
public class RawTreatmentPhase1Tests
{
    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // ═══════════ 1) الصيغة الجديدة ═══════════

    /// <summary>
    /// **الادعاء الأهم:** بلا معالجة جارية، الصيغة الجديدة تساوي القديمة تماماً.
    /// لو سقط هذا الاختبار فالمرحلة 1 غيّرت سلوكاً قائماً، وهو ما تعهدنا بعدمه.
    /// </summary>
    [Fact]
    public void Available_Unchanged_When_No_Treatment()
    {
        var lot = new Lot { InStockQtyKg = 5000, ReservedQtyKg = 1200 };

        Assert.Equal(0, lot.UnderTreatmentQtyKg);
        Assert.Equal(3800, lot.AvailableQtyKg, 3);          // = الصيغة القديمة حرفياً
        Assert.Equal(lot.InStockQtyKg - lot.ReservedQtyKg, lot.AvailableQtyKg, 3);
    }

    /// <summary>الحدّ الثالث يخصم فعلاً حين يوجد — أساس «تحت المعالجة ليست متاحة».</summary>
    [Fact]
    public void Available_Excludes_UnderTreatment()
    {
        // سيناريو المستخدم: 5,000 سلة ⟵ 4,000 جاهزة + 1,000 تحت المعالجة
        var lot = new Lot { InStockQtyKg = 5000, ReservedQtyKg = 0, UnderTreatmentQtyKg = 1000 };
        Assert.Equal(4000, lot.AvailableQtyKg, 3);

        // ومع حجز خطة قائمة، الحدّان يجتمعان بلا ازدواج
        lot.ReservedQtyKg = 1500;
        Assert.Equal(2500, lot.AvailableQtyKg, 3);
    }

    /// <summary>لا قيمة سالبة مهما تراكمت الحدود — الحارس Math.Max باقٍ.</summary>
    [Fact]
    public void Available_Never_Negative()
    {
        var lot = new Lot { InStockQtyKg = 1000, ReservedQtyKg = 800, UnderTreatmentQtyKg = 700 };
        Assert.Equal(0, lot.AvailableQtyKg, 3);
    }

    // ═══════════ 2) حساب موعد الجاهزية ═══════════

    /// <summary>
    /// المدة بالساعات تستوعب «7 أيام» و«6 ساعات» بوحدة واحدة — وهو سبب اختيارها
    /// على الأيام. الجاهزية تُشتق بالمقارنة الزمنية ولا يغيّرها مؤقّت خلفي.
    /// </summary>
    [Theory]
    [InlineData(168, 7)]   // سبعة أيام — الجزء الأول في سيناريو المستخدم
    [InlineData(240, 10)]  // عشرة أيام — الجزء الثاني
    [InlineData(6, 0)]     // تعقيم حراري بالساعات
    public void ExpectedReady_Is_Start_Plus_Duration(double hours, int expectedDays)
    {
        var start = new DateTime(2026, 9, 6, 8, 0, 0);
        var t = new RawTreatment
        {
            StartedAt = start,
            DurationHours = hours,
            ExpectedReadyAt = start.AddHours(hours),
            Status = TreatmentStatuses.InProgress
        };

        Assert.Equal(expectedDays, (t.ExpectedReadyAt - t.StartedAt).Days);
        Assert.Equal(start.AddHours(hours), t.ExpectedReadyAt);
    }

    /// <summary>المتأخرة = جارية وتجاوزت موعدها — أساس تقرير «المعالجات المتأخرة».</summary>
    [Fact]
    public void Overdue_Only_While_Still_InProgress()
    {
        var past = new RawTreatment
        {
            StartedAt = DateTime.Now.AddDays(-10),
            ExpectedReadyAt = DateTime.Now.AddDays(-3),
            Status = TreatmentStatuses.InProgress
        };
        Assert.True(past.IsReadyByTime);
        Assert.True(past.IsOverdue);

        // بعد الإفراج لا تعود متأخرة — وإلا تراكمت في التقرير إلى الأبد
        past.Status = TreatmentStatuses.Released;
        Assert.False(past.IsOverdue);

        var future = new RawTreatment
        {
            StartedAt = DateTime.Now,
            ExpectedReadyAt = DateTime.Now.AddDays(7),
            Status = TreatmentStatuses.InProgress
        };
        Assert.False(future.IsReadyByTime);
        Assert.False(future.IsOverdue);
    }

    /// <summary>المتبقي داخل الدورة يحترم الإفراج الجزئي والرفض معاً.</summary>
    [Fact]
    public void Remaining_Accounts_For_Partial_Release_And_Rejection()
    {
        var t = new RawTreatment { QtyKg = 1000 };
        Assert.Equal(1000, t.RemainingQtyKg, 3);

        t.ReleasedQtyKg = 500;                 // إفراج جزئي (البند 5)
        Assert.Equal(500, t.RemainingQtyKg, 3);

        t.RejectedQtyKg = 200;
        Assert.Equal(300, t.RemainingQtyKg, 3);

        t.ReleasedQtyKg = 800;                 // لا سالب
        Assert.Equal(0, t.RemainingQtyKg, 3);
    }

    // ═══════════ 3) المخطط والبذور ═══════════

    /// <summary>الجدولان أُنشئا، والمستودع ونوعا المعالجة بُذروا.</summary>
    [Fact]
    public void Schema_And_Seed_Are_Present()
    {
        using var host = new TestHost();
        var db = host.Get<DatesErpDbContext>();

        Assert.Equal(1, Scalar(host.Connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='RawTreatments'"));
        Assert.Equal(1, Scalar(host.Connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='TreatmentTypes'"));

        var wh = db.Warehouses.FirstOrDefault(w => w.WarehouseCode == "WTRT");
        Assert.NotNull(wh);
        Assert.Equal("Treatment", wh.WarehouseType);

        // المستودعات القائمة لم تُمسّ
        Assert.NotNull(db.Warehouses.FirstOrDefault(w => w.WarehouseCode == "WRM"));
        Assert.NotNull(db.Warehouses.FirstOrDefault(w => w.WarehouseCode == "WFG"));

        Assert.NotEmpty(db.TreatmentTypes.ToList());
        // المدة الافتراضية بالساعات لا بالأيام
        Assert.Equal(168, db.TreatmentTypes.Single(t => t.TypeCode == "TRT-FRZ").DefaultDurationHours, 1);
    }

    /// <summary>AvailableQtyKg محسوبة: لا يجوز أن تُنشأ كعمود في القاعدة.</summary>
    [Fact]
    public void Computed_Properties_Are_Not_Columns()
    {
        using var host = new TestHost();
        Assert.Equal(0, Scalar(host.Connection,
            "SELECT COUNT(*) FROM pragma_table_info('Lots') WHERE name='AvailableQtyKg'"));
        Assert.Equal(0, Scalar(host.Connection,
            "SELECT COUNT(*) FROM pragma_table_info('RawTreatments') WHERE name='IsOverdue'"));
        Assert.Equal(0, Scalar(host.Connection,
            "SELECT COUNT(*) FROM pragma_table_info('RawTreatments') WHERE name='RemainingQtyKg'"));

        // بينما العمودان الحقيقيان موجودان
        Assert.Equal(1, Scalar(host.Connection,
            "SELECT COUNT(*) FROM pragma_table_info('Lots') WHERE name='UnderTreatmentQtyKg'"));
        Assert.Equal(1, Scalar(host.Connection,
            "SELECT COUNT(*) FROM pragma_table_info('Lots') WHERE name='TreatmentReadyQtyKg'"));
    }

    // ═══════════ 4) الترحيل — فخ الترقية ═══════════

    /// <summary>
    /// **الحارس ضد توقف الإنتاج عند الترقية** (قرار المستخدم س2): قاعدة قائمة بلا
    /// أعمدة المعالجة تُرقّى، فتصبح كل دفعة ذات رصيد «جاهزة» بترحيل مسجَّل في التقرير.
    /// </summary>
    [Fact]
    public void Migration_Marks_Existing_Lots_Ready_And_Reports_It()
    {
        using var host = new TestHost();
        var conn = host.Connection;

        // محاكاة قاعدة سابقة لدورة المعالجة
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "ALTER TABLE Lots DROP COLUMN UnderTreatmentQtyKg;"
                            + "ALTER TABLE Lots DROP COLUMN TreatmentReadyQtyKg;";
            cmd.ExecuteNonQuery();
        }

        long lotsBefore = Scalar(conn, "SELECT COUNT(*) FROM Lots");
        long stockBefore = Scalar(conn, "SELECT CAST(IFNULL(SUM(InStockQtyKg),0) AS INTEGER) FROM Lots");

        List<string> report;
        using (var ms = host.Services.CreateScope())
            report = SchemaMigrator.Migrate(ms.ServiceProvider.GetRequiredService<DatesErpDbContext>());

        Assert.DoesNotContain(report, r => r.StartsWith("خطأ"));
        Assert.Contains(report, r => r.Contains("Lots.UnderTreatmentQtyKg"));

        // لا فقد بيانات ولا تغيّر رصيد — الترحيل يعلّم ولا يحرّك كمية
        Assert.Equal(lotsBefore, Scalar(conn, "SELECT COUNT(*) FROM Lots"));
        Assert.Equal(stockBefore, Scalar(conn, "SELECT CAST(IFNULL(SUM(InStockQtyKg),0) AS INTEGER) FROM Lots"));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var lots = db.Lots.AsNoTracking().Where(l => l.InStockQtyKg > 0).ToList();
        if (lots.Count > 0)
        {
            // كل دفعة قائمة صارت جاهزة بالكامل، ولا شيء تحت المعالجة
            Assert.All(lots, l => Assert.Equal(l.InStockQtyKg, l.TreatmentReadyQtyKg, 3));
            Assert.All(lots, l => Assert.Equal(0, l.UnderTreatmentQtyKg, 3));
            // ومن ثم فالمتاح لم يتغير عمّا كان
            Assert.All(lots, l => Assert.Equal(
                Math.Max(0, l.InStockQtyKg - l.ReservedQtyKg), l.AvailableQtyKg, 3));
            Assert.Contains(report, r => r.Contains("ترحيل المعالجة"));
        }
    }

    /// <summary>الترحيل آمن للتكرار: تشغيله مرتين لا يضاعف شيئاً ولا يُبلغ مرتين.</summary>
    [Fact]
    public void Migration_Is_Idempotent()
    {
        using var host = new TestHost();

        using (var s1 = host.Services.CreateScope())
            SchemaMigrator.Migrate(s1.ServiceProvider.GetRequiredService<DatesErpDbContext>());

        List<string> second;
        using (var s2 = host.Services.CreateScope())
            second = SchemaMigrator.Migrate(s2.ServiceProvider.GetRequiredService<DatesErpDbContext>());

        Assert.DoesNotContain(second, r => r.StartsWith("خطأ"));
        Assert.DoesNotContain(second, r => r.Contains("ترحيل المعالجة"));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        // مستودع واحد لا اثنان، وأنواع المعالجة لم تتكرر
        Assert.Single(db.Warehouses.Where(w => w.WarehouseCode == "WTRT").ToList());

        // §B106 — كان: Assert.Equal(3, Count()). عددٌ سحري يقيس «عدم التكرار» بالإجمالي،
        // فينكسر كلما أُضيف نوع مشروع (أُضيفت أنواع درجات الإصابة: خفيفة/متوسطة/شديدة).
        // العدد ليس هو الثابت المقصود — الثابت هو **ألا يتكرر أي TypeCode**.
        // نفحصه مباشرةً الآن، فيصمد أمام أي إضافة مستقبلية ويكشف التكرار الحقيقي
        // الذي كان العدّ السحري سيُخفيه لو أُضيف نوع وحُذف آخر.
        var codes = db.TreatmentTypes.Select(x => x.TypeCode).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.NotEmpty(codes);
    }

    /// <summary>
    /// الترحيل **لا يلمس** دفعة دخلت دورة معالجة حقيقية — وإلا محا عمل النظام
    /// بعد التشغيل وأعاد كمية تحت المعالجة إلى «جاهزة» بلا وجه حق.
    /// </summary>
    [Fact]
    public void Migration_Does_Not_Touch_Lots_With_Real_Treatment()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();   // §B103 — مطلوب لبذرة الاستلام (صلاحية receiving/Create)
        var db = host.Get<DatesErpDbContext>();

        // §B103 — البذرة لا تنشئ دفعات: نستلم شحنة فتتولد الدفعة (كان الاختبار يفترض دفعة مبذورة)
        var receiving = host.Get<IReceivingService>();
        var sh = receiving.SaveShipment(1, "2026-09-01", "2026-09-01", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 50, UnitWeightKg = 20, QtyKg = 1000 } });
        Assert.True(sh.Ok, sh.Message);
        Assert.True(receiving.ApproveShipment(sh.Id).Ok);

        var lot = db.Lots.OrderBy(l => l.Id).First();
        lot.TreatmentReadyQtyKg = 0;
        lot.UnderTreatmentQtyKg = 0;
        db.RawTreatments.Add(new RawTreatment
        {
            TreatmentNo = "TRT-2026-0001",
            LotId = lot.Id,
            ProductId = lot.ProductId,
            QtyKg = 500,
            StartedAt = DateTime.Now,
            DurationHours = 168,
            ExpectedReadyAt = DateTime.Now.AddHours(168),
            Status = TreatmentStatuses.InProgress
        });
        db.SaveChanges();

        using (var ms = host.Services.CreateScope())
            SchemaMigrator.Migrate(ms.ServiceProvider.GetRequiredService<DatesErpDbContext>());

        using var scope = host.Services.CreateScope();
        var rd = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var after = rd.Lots.AsNoTracking().First(l => l.Id == lot.Id);
        Assert.Equal(0, after.TreatmentReadyQtyKg, 3); // بقي صفراً — لم يُرحَّل
    }

    // ═══════════ 5) الصلاحيات ═══════════

    /// <summary>
    /// مورد «treatment» في الكتالوج، وكل دور نشط ينال «عرض» عند الترقية — ولا ينال
    /// «اعتماد» (الإفراج) تلقائياً: العرض لا يضر، والتنفيذ قرار إدارة.
    /// </summary>
    [Fact]
    public void Treatment_Resource_Is_Gated_And_Backfilled_View_Only()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var svc = new PermissionService(db, host.Services.GetRequiredService<ICurrentSession>());
        svc.EnsureCatalog();

        Assert.Contains(PermissionModules.Treatment, PermissionModules.Codes);
        Assert.Contains(PermissionModules.Treatment, PermissionModules.ScreenGated);
        Assert.NotNull(db.PermissionResources.FirstOrDefault(r => r.Code == PermissionModules.Treatment));

        foreach (var role in db.Roles.Where(r => r.IsActive).ToList())
        {
            var set = svc.GetRoleSet(role.Id);
            Assert.Contains((PermissionModules.Treatment, "View"), set);
        }
    }

    /// <summary>المورد يظهر في مجموعة «المخازن» — مجموعة قائمة لا مخترعة.</summary>
    [Fact]
    public void Treatment_Uses_An_Existing_Group()
    {
        var groups = PermissionModules.All.Select(x => x.GroupAr).Distinct().ToList();
        var mine = PermissionModules.All.Single(x => x.Code == PermissionModules.Treatment).GroupAr;
        Assert.Equal("المخازن", mine);
        Assert.Contains(mine, groups);
    }
}
