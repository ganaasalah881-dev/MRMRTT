using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// اختبار ترقية قواعد البيانات القديمة: يحاكي قاعدة مستخدم أُنشئت بإصدار أقدم من النظام
/// (تنقصها الأعمدة الجديدة وجدول طاقات الأصناف حسب الوردية) ويتحقق أن المرحّل الشامل
/// SchemaMigrator يعيد كل شيء بلا فقد بيانات — وأن مسار شاشة الخطة (العملاء، الدفعات،
/// الطاقة، خطة اليوم) يعود للعمل بلا أخطاء. هذا هو سبب «العملاء لا يظهرون» و
/// «زر اختيار الأصناف يظهر خطأ» الذي كان يحدث على أجهزة المستخدمين.
/// </summary>
public class SchemaMigratorTests
{
    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>تحويل المخطط إلى حالة «قاعدة قديمة»: حذف الأعمدة الجديدة وجدول الطاقات.</summary>
    private static void SimulateLegacySchema(SqliteConnection conn)
    {
        Exec(conn, "DROP TABLE ProductShiftCapacities");
        Exec(conn, "ALTER TABLE Lots DROP COLUMN ReservedQtyKg");
        Exec(conn, "ALTER TABLE ProductionPlanItems DROP COLUMN ProducedQtyKg");
        Exec(conn, "ALTER TABLE ProductionPlanItems DROP COLUMN AcceptedQtyKg");
        Exec(conn, "ALTER TABLE ProductionPlanItems DROP COLUMN DeliveredQtyKg");
        Exec(conn, "ALTER TABLE ProductionPlanItems DROP COLUMN ExecutionStatus");
        Exec(conn, "ALTER TABLE Shifts DROP COLUMN PlannedDowntimeHours");
        Exec(conn, "ALTER TABLE Products DROP COLUMN MoldsCount");
        Exec(conn, "ALTER TABLE Products DROP COLUMN MoldWeightKg");
        Exec(conn, "ALTER TABLE PackagingTypes DROP COLUMN MoldsCount");
        Exec(conn, "ALTER TABLE PackagingTypes DROP COLUMN MoldWeightKg");
        Exec(conn, "ALTER TABLE CustomerDeliveries DROP COLUMN InvoicedQtyKg");
    }

    [Fact]
    public void LegacyDb_AfterMigration_RestoresEverything_AndPlanningPathWorks()
    {
        using var host = new TestHost();
        var conn = host.Connection;

        long lotsBefore = Scalar(conn, "SELECT COUNT(*) FROM Lots");
        long customersBefore = Scalar(conn, "SELECT COUNT(*) FROM Customers");

        SimulateLegacySchema(conn);

        // إثبات أن القاعدة أصبحت «قديمة»: استعلام الدفعات يفشل (هذا ما كان يظهر للمستخدم كخطأ)
        using (var broken = host.Services.CreateScope())
        {
            var dbx = broken.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var ex = Record.Exception(() => dbx.Lots.Where(l => l.InStockQtyKg > 0)
                .Select(l => l.InStockQtyKg - l.ReservedQtyKg).ToList());
            Assert.NotNull(ex);
        }

        // ── تنفيذ الترحيل الشامل ──
        List<string> report;
        using (var ms = host.Services.CreateScope())
        {
            var dbm = ms.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            report = SchemaMigrator.Migrate(dbm);
        }

        Assert.DoesNotContain(report, r => r.StartsWith("خطأ"));
        Assert.Contains(report, r => r.Contains("ProductShiftCapacities"));
        Assert.Contains(report, r => r.Contains("Lots.ReservedQtyKg"));

        // الجدول والأعمدة عادت
        Assert.Equal(1, Scalar(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ProductShiftCapacities'"));
        Assert.Equal(1, Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('Lots') WHERE name='ReservedQtyKg'"));
        Assert.Equal(1, Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('ProductionPlanItems') WHERE name='ProducedQtyKg'"));
        Assert.Equal(1, Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('Shifts') WHERE name='PlannedDowntimeHours'"));
        Assert.Equal(1, Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('Products') WHERE name='MoldsCount'"));

        // البيانات لم تُفقد
        Assert.Equal(lotsBefore, Scalar(conn, "SELECT COUNT(*) FROM Lots"));
        Assert.Equal(customersBefore, Scalar(conn, "SELECT COUNT(*) FROM Customers"));

        // ── مسار شاشة الخطة كله يعمل الآن (نفس استعلامات نافذة اختيار الأصناف) ──
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        // العملاء يظهرون
        var customers = db.Customers.AsNoTracking().Where(c => c.IsActive).ToList();
        Assert.NotEmpty(customers);

        // الدفعات تُقرأ مع المحجوز
        var lots = db.Lots.AsNoTracking().Where(l => l.InStockQtyKg > 0).ToList();
        Assert.All(lots, l => Assert.True(Math.Max(0, l.InStockQtyKg - l.ReservedQtyKg) >= 0));

        // الأصناف التامة والعبوات
        var products = db.Products.Where(p => p.GroupCode == "002" && p.IsActive).ToList();
        Assert.NotEmpty(products);
        var packs = db.PackagingTypes.Where(p => p.IsActive).ToList();
        Assert.NotEmpty(packs);

        // الطاقة حسب الوردية (جدول أعيد إنشاؤه) — تعمل وترجع معدلاً احتياطياً إن كانت فارغة
        var capacity = scope.ServiceProvider.GetRequiredService<ICapacityService>();
        var (rate, cap) = capacity.GetCapacity(products[0].Id, 1);
        Assert.True(rate >= 0);
        Assert.True(cap >= 0);

        // خطة اليوم وتقدم العملاء
        var progress = scope.ServiceProvider.GetRequiredService<IPlanProgressService>();
        var daily = progress.GetDailyPlan(DateTime.Today.ToString("dd/MM/yyyy"), null);
        Assert.NotNull(daily);
    }

    [Fact]
    public void FreshDb_Migration_IsHarmless_NoChangesReported()
    {
        using var host = new TestHost();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var report = SchemaMigrator.Migrate(db);
        Assert.DoesNotContain(report, r => r.StartsWith("خطأ"));
        Assert.DoesNotContain(report, r => r.StartsWith("تم"));
    }

    /// <summary>قاعدة بلا جدول طاقات نهائياً: يُنشأ الجدول ويمكن الكتابة فيه والقراءة منه.</summary>
    [Fact]
    public void MissingCapacityTable_IsCreated_AndUsable()
    {
        using var host = new TestHost();
        host.LoginAsAdmin(); // SetCapacity تتطلب صلاحية «تعديل الأصناف»
        var conn = host.Connection;
        Exec(conn, "DROP TABLE ProductShiftCapacities");

        using (var ms = host.Services.CreateScope())
        {
            var dbm = ms.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var report = SchemaMigrator.Migrate(dbm);
            Assert.DoesNotContain(report, r => r.StartsWith("خطأ"));
        }

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var capacity = scope.ServiceProvider.GetRequiredService<ICapacityService>();
        var product = db.Products.First(p => p.GroupCode == "002");

        var set = capacity.SetCapacity(product.Id, 1, 5000);
        Assert.True(set.Ok, set.Message);

        var (rate, cap) = capacity.GetCapacity(product.Id, 1);
        Assert.Equal(5000, cap);
        Assert.True(rate > 0);
    }

    /// <summary>
    /// يحاكي عطل المستخدم حرفياً: أعمدة موجودة لكن قيمها فارغة (NULL) بينما النموذج
    /// يتوقعها غير فارغة — كان يسبب «The data is NULL at ordinal». المرحّل يملؤها تلقائياً.
    /// </summary>
    [Fact]
    public void NullValues_InNonNullableColumns_AreRepaired_BySweep()
    {
        using var host = new TestHost();
        var conn = host.Connection;

        // محاكاة قاعدة المستخدم حرفياً: أعمدة أُضيفت لاحقاً كقابلة للفراغ (بقيمتها فارغة
        // في كل الصفوف) بينما النموذج يتوقعها غير فارغة
        Exec(conn, "ALTER TABLE Lots DROP COLUMN ReservedQtyKg");
        Exec(conn, "ALTER TABLE Lots ADD COLUMN ReservedQtyKg REAL NULL");
        Exec(conn, "ALTER TABLE Products DROP COLUMN MoldsCount");
        Exec(conn, "ALTER TABLE Products ADD COLUMN MoldsCount INTEGER NULL");
        Exec(conn, "ALTER TABLE Shifts DROP COLUMN EffectiveProductiveHours");
        Exec(conn, "ALTER TABLE Shifts ADD COLUMN EffectiveProductiveHours REAL NULL");

        using (var ms = host.Services.CreateScope())
        {
            var dbm = ms.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var report = SchemaMigrator.Migrate(dbm);
            Assert.DoesNotContain(report, r => r.StartsWith("خطأ"));
        }

        // بعد الترحيل: لا فراغات متبقية في الأعمدة غير القابلة للفراغ
        Assert.Equal(0, Scalar(conn, "SELECT COUNT(*) FROM Lots WHERE ReservedQtyKg IS NULL"));
        Assert.Equal(0, Scalar(conn, "SELECT COUNT(*) FROM Products WHERE MoldsCount IS NULL"));
        Assert.Equal(0, Scalar(conn, "SELECT COUNT(*) FROM Shifts WHERE EffectiveProductiveHours IS NULL"));

        // والقراءة عبر النموذج تعمل بلا خطأ «data is NULL at ordinal»
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var lots = db.Lots.AsNoTracking().ToList();           // قراءة آمنة حتى لو كانت فارغة
        Assert.NotNull(lots);
        Assert.NotEmpty(db.Shifts.AsNoTracking().ToList());
        Assert.NotEmpty(db.Products.AsNoTracking().ToList());
        Assert.All(db.Shifts.AsNoTracking().ToList(), sh => Assert.True(sh.EffectiveProductiveHours >= 0));
    }
}

/// <summary>
/// §تتبع الصنف: قاعدة قديمة بلا عمود SourceProductId — المرحّل يضيف العمود
/// ثم يربط تحويلات الأصناف آلياً من الأسماء (سكري فاخر ← تمر خام - سكري).
/// </summary>
public class ConversionAutoLinkTests
{
    [Fact]
    public void Migrate_AddsSourceColumn_And_AutoLinks_Conversions_FromNames()
    {
        using var host = new TestHost();

        // محاكاة قاعدة قديمة: حذف عمود التعريف الرسمي
        using (var cmd = host.Connection.CreateCommand())
        {
            cmd.CommandText = "ALTER TABLE Products DROP COLUMN SourceProductId";
            cmd.ExecuteNonQuery();
        }

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var report = SchemaMigrator.Migrate(db);
            Assert.DoesNotContain(report, r => r.StartsWith("خطأ"));
            Assert.Contains(report, r => r.Contains("Products.SourceProductId"));
            Assert.Contains(report, r => r.Contains("تحويل رسمي"));

            // الربط الآلي: كل منتج تام أصبح يعرف خامه من اسمه
            var finKhalas = db.Products.AsNoTracking().Single(p => p.ProductCode == "002-001");
            var finSukkari = db.Products.AsNoTracking().Single(p => p.ProductCode == "002-002");
            var rawKhalas = db.Products.AsNoTracking().Single(p => p.ProductCode == "001-001");
            var rawSukkari = db.Products.AsNoTracking().Single(p => p.ProductCode == "001-002");
            Assert.Equal(rawKhalas.Id, finKhalas.SourceProductId);
            Assert.Equal(rawSukkari.Id, finSukkari.SourceProductId);
        }
    }
}

/// <summary>
/// §B110 — الجداول المُنشأة أو المُرحّلة يجب أن تحمل فهارسها كاملة. قبل هذا الإصلاح
/// كان المرحّل يخلق الجداول الجديدة بلا فهارس (وبلا قيود التكرار التي تعتمد عليها
/// أرقام المستندات)، فتتباين القواعد الميدانية بنيوياً عن الجديدة. هنا نُسقط فهرسين
/// فريدين ونُثبت أن الترحيل يعيدهما.
/// </summary>
public class SchemaIndexRepairTests
{
    private static long Scalar(System.Data.IDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void Migrate_RecreatesMissingUniqueIndexes()
    {
        using var host = new TestHost();
        var conn = host.Connection;

        const string corrIdx = "IX_WorkflowTasks_CorrelationKey";
        const string numIdx = "IX_WorkflowTasks_TaskNumber";
        string Count(string name) =>
            $"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='{name}'";

        // إثبات الوجود قبل الإسقاط
        Assert.Equal(1, Scalar(conn, Count(corrIdx)));
        Assert.Equal(1, Scalar(conn, Count(numIdx)));

        // محاكاة قاعدة قديمة هُدرت فهارسها
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"DROP INDEX {corrIdx}; DROP INDEX {numIdx};";
            cmd.ExecuteNonQuery();
        }
        Assert.Equal(0, Scalar(conn, Count(corrIdx)));
        Assert.Equal(0, Scalar(conn, Count(numIdx)));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var report = SchemaMigrator.Migrate(db);

        Assert.DoesNotContain(report, r => r.StartsWith("خطأ في إنشاء فهرس"));
        Assert.Contains(report, r => r.Contains(corrIdx));
        Assert.Contains(report, r => r.Contains(numIdx));
        Assert.Equal(1, Scalar(conn, Count(corrIdx)));
        Assert.Equal(1, Scalar(conn, Count(numIdx)));
    }

    [Fact]
    public void Migrate_IsIdempotent_DoesNotDuplicateIndexes()
    {
        using var host = new TestHost();
        var conn = host.Connection;

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        SchemaMigrator.Migrate(db);

        // تشغيل ثانٍ — لا يجب أن يُعاد إنشاء شيء أو يظهر خطأ
        var second = SchemaMigrator.Migrate(db);
        Assert.DoesNotContain(second, r => r.StartsWith("خطأ في إنشاء فهرس"));
        Assert.DoesNotContain(second, r => r.Contains("تم إنشاء الفهرس المفقود"));

        Assert.Equal(1, Scalar(conn,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_WorkflowTasks_CorrelationKey'"));
    }
}
