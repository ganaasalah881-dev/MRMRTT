using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B110 — مصفوفة SQL Server الحقيقية في الاختبارات (المزود المركزي للإنتاج).
/// قبل هذه الإضافة كانت الحزمة كلها تعمل على SQLite فقط، وفروق المزودين أحرقتنا سابقاً.
///
/// التفعيل: ضبط متغير البيئة DATESERP_SQL_CONNECTION على سلسلة اتصال بقاعدة قابلة
/// للإنشاء والحذف (مستخدم بصلاحيات إنشاء قواعد). بدونه تُصبح الاختبارات نجاحاً فارغاً
/// كي تبقى الحزم المحلية خضراء — وتُشغَّل فعلياً في مهمة CI المخصصة (خدمة mssql).
/// كل تشغيل ينشئ قاعدة فريدة باسم عشوائي ويسقطها عند النهاية — بلا أثر متبقٍ.
/// </summary>
public sealed class SqlServerFixture : IDisposable
{
    public bool Enabled { get; }
    public string Connection { get; } = "";
    private readonly string _dbName = "DateErpCi_" + Guid.NewGuid().ToString("N");

    public SqlServerFixture()
    {
        var template = Environment.GetEnvironmentVariable("DATESERP_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(template)) { Enabled = false; return; }
        Enabled = true;

        var master = WithDatabase(template, "master");
        using var conn = new SqlConnection(master);
        // الخادم (حاوية CI خصوصاً) قد يستغرق ثوانٍ في الإقلاع — إعادة محاولة حتى 90 ثانية
        Exception? last = null;
        for (int i = 0; i < 45; i++)
        {
            try { conn.Open(); last = null; break; }
            catch (Exception ex) { last = ex; Thread.Sleep(2000); }
        }
        if (last != null) throw new InvalidOperationException("SQL Server test instance unreachable: " + last.Message, last);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"IF DB_ID(N'{_dbName}') IS NULL CREATE DATABASE [{_dbName}]";
            cmd.ExecuteNonQuery();
        }
        Connection = WithDatabase(template, _dbName);
    }

    private static string WithDatabase(string connectionString, string database)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database };
        return builder.ConnectionString;
    }

    public void Dispose()
    {
        if (!Enabled) return;
        try
        {
            using var conn = new SqlConnection(WithDatabase(Connection, "master"));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"IF DB_ID(N'{_dbName}') IS NOT NULL BEGIN " +
                              $"ALTER DATABASE [{_dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                              $"DROP DATABASE [{_dbName}]; END";
            cmd.ExecuteNonQuery();
        }
        catch { /* التنظيف لا يفشل الاختبارات */ }
    }
}

public class SqlServerProviderTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _f;
    public SqlServerProviderTests(SqlServerFixture f) => _f = f;

    [Fact]
    public void EnsureCreated_Seed_AndLogin_Work_OnRealSqlServer()
    {
        if (!_f.Enabled) return;
        using var host = new TestHost(sqlConnection: _f.Connection);
        var session = host.LoginAsAdmin();
        Assert.True(session.UserId > 0);
        Assert.Contains("Administrator", session.Roles);
    }

    [Fact]
    public void RowVersion_OptimisticConcurrency_Throws_OnRealSqlServer()
    {
        if (!_f.Enabled) return;
        using var host = new TestHost(sqlConnection: _f.Connection);
        host.LoginAsAdmin();

        using var s1 = host.Services.CreateScope();
        var db1 = s1.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var c1 = db1.Customers.OrderBy(c => c.Id).First();

        using (var s2 = host.Services.CreateScope())
        {
            var db2 = s2.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var c2 = db2.Customers.First(c => c.Id == c1.Id);
            c2.Phone = "999000";
            db2.SaveChanges(); // يفوز التعديل الثاني برمزيته الجديدة
        }

        c1.Phone = "888000";
        Assert.Throws<DbUpdateConcurrencyException>(() => db1.SaveChanges());
    }

    [Fact]
    public void Audit_Written_WithoutCredentialKeys_OnRealSqlServer()
    {
        if (!_f.Enabled) return;
        using var host = new TestHost(sqlConnection: _f.Connection);
        host.LoginAsAdmin();

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var master = scope.ServiceProvider.GetRequiredService<DatesErp.Application.Services.MasterDataService>();
        var user = db.Users.First(u => u.UserName == "production");

        var r = master.ResetUserPassword(user.Id, "TempPass@789");
        Assert.True(r.Ok, r.Message);

        var userAudit = db.AuditLogs.AsEnumerable()
            .Where(a => a.DocumentType == nameof(DatesErp.Core.Domain.Entities.AppUser))
            .ToList();
        Assert.NotEmpty(userAudit);
        Assert.All(userAudit, a =>
        {
            Assert.DoesNotContain("PasswordHash", (a.OldValue ?? "") + (a.NewValue ?? ""));
            Assert.DoesNotContain("PasswordSalt", (a.OldValue ?? "") + (a.NewValue ?? ""));
        });
    }

    [Fact]
    public void DecimalColumns_AreRealDecimal_WithNumericOrdering()
    {
        if (!_f.Enabled) return;
        using var host = new TestHost(sqlConnection: _f.Connection);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        // النوع في قاعدة البيانات: decimal لا نص
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        foreach (var (table, col) in new[] { ("InspectionResults", "Qty"), ("UnitConversions", "Factor") })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='{table}' AND COLUMN_NAME='{col}'";
            Assert.Equal("decimal", (string)cmd.ExecuteScalar()!);
        }

        // الدليل السلوكي: الترتيب عددي لا نصي (النص يعيد 10.25 قبل 9.5 لأن '1' < '9')
        db.UnitConversions.Add(new DatesErp.Core.Domain.Entities.UnitConversion { FromUnitId = 1, ToUnitId = 2, Factor = 9.5m });
        db.UnitConversions.Add(new DatesErp.Core.Domain.Entities.UnitConversion { FromUnitId = 2, ToUnitId = 1, Factor = 10.25m });
        db.SaveChanges();

        using var order = conn.CreateCommand();
        order.CommandText = "SELECT TOP 1 Factor FROM UnitConversions " +
                            "WHERE Factor IN (9.5, 10.25) ORDER BY Factor ASC";
        var first = Convert.ToDecimal(order.ExecuteScalar());
        Assert.Equal(9.5m, first); // لو كان العمود نصياً لظهر 10.25 أولاً
    }

    [Fact]
    public void Numbering_GeneratesSequentialUniqueNumbers_OnRealSqlServer()
    {
        if (!_f.Enabled) return;
        using var host = new TestHost(sqlConnection: _f.Connection);
        host.LoginAsAdmin();

        using var scope = host.Services.CreateScope();
        var numbering = scope.ServiceProvider.GetRequiredService<INumberingService>();
        var first = numbering.Next("SHIP");
        var second = numbering.Next("SHIP");
        Assert.NotEqual(first, second);
        Assert.StartsWith("REC-", first);
    }
}
