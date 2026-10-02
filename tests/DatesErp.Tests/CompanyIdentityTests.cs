using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// اختبارات الهوية البصرية: بيانات الشركة والشعار المخزون في قاعدة البيانات —
/// تقرأ منها شاشات الدخول والنوافذ وكل النماذج والتقارير، مع ترحيل عمود الشعار
/// تلقائياً لقواعد البيانات القديمة.
/// </summary>
public class CompanyIdentityTests
{
    [Fact]
    public void CompanyInfo_NameAndLogo_RoundTrip()
    {
        using var host = new TestHost();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var c = db.CompanyInfos.OrderBy(x => x.Id).First();
        c.CompanyNameAr = "شركة الاختبار للتمور";
        c.ReportFooterNote = "تذييل التقارير الموحد";
        c.LogoBytes = new byte[] { 1, 2, 3, 4 };
        db.SaveChanges();

        var read = db.CompanyInfos.AsNoTracking().OrderBy(x => x.Id).First();
        Assert.Equal("شركة الاختبار للتمور", read.CompanyNameAr);
        Assert.Equal("تذييل التقارير الموحد", read.ReportFooterNote);
        Assert.Equal(4, read.LogoBytes.Length);
    }

    [Fact]
    public void LegacyDb_WithoutLogoColumn_MigratorAddsIt_AndWriteWorks()
    {
        using var host = new TestHost();

        // محاكاة قاعدة قديمة بلا عمود الشعار
        using (var cmd = host.Connection.CreateCommand())
        {
            cmd.CommandText = "ALTER TABLE CompanyInfos DROP COLUMN LogoBytes";
            cmd.ExecuteNonQuery();
        }

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var report = SchemaMigrator.Migrate(db);
        Assert.Contains(report, r => r.Contains("CompanyInfos.LogoBytes"));

        // الكتابة بعد الترحيل تعمل مباشرة
        var c = db.CompanyInfos.OrderBy(x => x.Id).First();
        c.LogoBytes = new byte[] { 9, 8, 7 };
        db.SaveChanges();
        Assert.Equal(3, db.CompanyInfos.AsNoTracking().OrderBy(x => x.Id).First().LogoBytes.Length);
    }
}
