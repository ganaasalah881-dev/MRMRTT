using DatesErp.Application.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>§B81 — الفحص الذاتي: قسم «هوية النظام والقاعدة» يجيب «هل أنا على القاعدة الصحيحة؟».</summary>
public class B81DiagnosticIdentityTests
{
    [Fact]
    public void Identity_Reports_Provider_Db_Version_And_Counts()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var id = DiagnosticCore.GetIdentity(db);
        string Val(string name) => id.FirstOrDefault(x => x.Name == name).Value;

        // المزوّد والقاعدة
        Assert.Equal("SQLite (قاعدة محلية على هذا الجهاز)", Val("المزوّد"));
        Assert.Equal("(قاعدة ذاكرة — بلا ملف)", Val("ملف القاعدة"));   // TestHost قاعدة ذاكرة

        // إصدار القاعدة — البذر الأولي 1.0.0
        Assert.Equal("1.0.0", Val("إصدار قاعدة البيانات"));

        // العدادات أرقام فعلية (البذر يملأ ورديات ووحدات ومستخدمين)
        Assert.True(int.TryParse(Val("المستخدمون").Replace(",", ""), out var users) && users >= 1);
        Assert.True(int.TryParse(Val("دفعات الخام").Replace(",", ""), out _));
        Assert.True(int.TryParse(Val("خطط الإنتاج").Replace(",", ""), out _));
        Assert.True(int.TryParse(Val("أوامر الإنتاج").Replace(",", ""), out _));
    }

    [Fact]
    public void Identity_Counts_Reflect_New_Data()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();

        int Before()
        {
            var v = DiagnosticCore.GetIdentity(db).FirstOrDefault(x => x.Name == "عدد الأصناف").Value;
            return int.Parse(v.Replace(",", ""));
        }

        int before = Before();
        var r = master.SaveProductFull(null, "001-777", "صنف الهوية", "001", "Raw", "كجم", 0, 0, 0, null);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(before + 1, Before());   // العدّ حيّ من القاعدة نفسها
    }
}
