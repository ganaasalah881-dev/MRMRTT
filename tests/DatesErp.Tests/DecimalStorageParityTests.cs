using DatesErp.Core.Domain.Entities;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B110 — تكافؤ تخزين decimal بين المزودين:
/// • على SQL Server: نوع حقيقي decimal(18,4) بلا محوّل — الفرز والمجاميع عددية صحيحة.
/// • على SQLite: المحوّل النصي المعتمد سابقاً يبقى للتوافق مع القواعد المحلية القائمة.
/// قبل الإصلاح كان المحوّل النصي مطبقاً على كل المزودين (حتى الإنتاج).
/// </summary>
public class DecimalStorageParityTests
{
    private const string SqlServerConn =
        "Server=localhost;Database=ModelOnly;User Id=model;Password=model;Encrypt=Mandatory;TrustServerCertificate=true";

    [Fact]
    public void SqlServer_Model_StoresRealDecimal_WithPrecision184_AndNoConverter()
    {
        var options = new DbContextOptionsBuilder<DatesErpDbContext>()
            .UseSqlServer(SqlServerConn)
            .Options;
        using var db = new DatesErpDbContext(options);

        var qty = db.Model.FindEntityType(typeof(InspectionResult))!.FindProperty(nameof(InspectionResult.Qty))!;
        Assert.Equal("decimal(18,4)", qty.GetColumnType());
        Assert.Null(qty.GetValueConverter()); // لا محوّل نصي على SQL Server

        var factor = db.Model.FindEntityType(typeof(UnitConversion))!.FindProperty(nameof(UnitConversion.Factor))!;
        Assert.Equal("decimal(18,4)", factor.GetColumnType());
        Assert.Null(factor.GetValueConverter());
    }

    [Fact]
    public void Sqlite_Model_KeepsTextConverter_ForExistingLocalDatabases()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<DatesErpDbContext>()
            .UseSqlite(conn)
            .Options;
        using var db = new DatesErpDbContext(options);

        var qty = db.Model.FindEntityType(typeof(InspectionResult))!.FindProperty(nameof(InspectionResult.Qty))!;
        Assert.NotNull(qty.GetValueConverter()); // SQLite يحتفظ بالمحوّل النصي
        Assert.Equal(typeof(string), qty.GetValueConverter()!.ProviderClrType);
    }

    [Fact]
    public void Sqlite_DecimalValues_RoundTripThroughTextConverter()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<DatesErpDbContext>()
            .UseSqlite(conn)
            .Options;
        using var db = new DatesErpDbContext(options);
        db.Database.EnsureCreated();

        db.UnitConversions.Add(new UnitConversion { FromUnitId = 1, ToUnitId = 2, Factor = 7.5m, IsActive = true });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var loaded = db.UnitConversions.First();
        Assert.Equal(7.5m, loaded.Factor);
    }
}
