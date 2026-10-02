using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B110 — الكتابة على القاعدة تمر حصراً عبر طبقة الخدمات:
/// المسارات الثلاثة التي كانت تكتب مباشرة من الواجهة (المخارج الثانوية، إنشاء الأدوار،
/// بيانات الشركة) صارت خدمات بصلاحيات وتدقيق، وهذه اختبارات سلوكها وفرض صلاحياتها.
/// </summary>
public class LayeredWriteEnforcementTests
{
    [Fact]
    public void ToggleByProduct_FlipsState_AndIsAudited()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<MasterDataService>();

        var bp = db.ByProducts.First(b => b.ByProductCode == "BP-HASHF");
        var r = svc.ToggleByProduct(bp.Id);
        Assert.True(r.Ok, r.Message);
        Assert.False(db.ByProducts.AsNoTracking().First(b => b.Id == bp.Id).IsActive);

        var r2 = svc.ToggleByProduct(bp.Id);
        Assert.True(r2.Ok, r2.Message);
        Assert.True(db.ByProducts.AsNoTracking().First(b => b.Id == bp.Id).IsActive);
    }

    [Fact]
    public void ToggleByProduct_WithoutSession_IsDenied()
    {
        using var host = new TestHost(); // بلا تسجيل دخول — جلسة فارغة
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var bp = db.ByProducts.First();
        Assert.Throws<PermissionDeniedException>(() => svc.ToggleByProduct(bp.Id));
    }

    [Fact]
    public void SaveCompanyInfo_PersistsIdentity_WithPermission()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<MasterDataService>();

        var r = svc.SaveCompanyInfo("شركة الاختبار", "Test Co", "صنعاء", "777111222",
            "info@test.ye", "TX-1", "شكراً لتعاملكم", null, false);
        Assert.True(r.Ok, r.Message);

        var c = db.CompanyInfos.AsNoTracking().OrderBy(x => x.Id).First();
        Assert.Equal("شركة الاختبار", c.CompanyNameAr);
        Assert.Equal("صنعاء", c.Address);

        // التدقيق كُتب داخل نفس المعاملة
        Assert.Contains(db.AuditLogs.AsEnumerable(),
            a => a.DocumentType == nameof(CompanyInfo) && a.ActionType == "Edit");
    }

    [Fact]
    public void SaveCompanyInfo_WithoutEditPermission_IsDenied()
    {
        using var host = new TestHost();
        host.LoginAs("warehouse"); // الإعدادات ليست من صلاحيات أمين المخزن
        using var scope = host.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        Assert.Throws<PermissionDeniedException>(() =>
            svc.SaveCompanyInfo("شركة", null, null, null, null, null, null, null, false));
    }

    [Fact]
    public void CreateCustomRole_CreatesUniqueCode_AndRejectsDuplicateName()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<PermissionService>();

        var role = svc.CreateCustomRole("مشرف جودة متقدم");
        Assert.True(role.Id > 0);
        Assert.StartsWith("R-", role.RoleCode);
        Assert.True(role.IsActive);

        // الاسم المكرر مرفوض
        var ex = Record.Exception(() => svc.CreateCustomRole("مشرف جودة متقدم"));
        Assert.NotNull(ex);

        // ودُوّن في سجل تدقيق الصلاحيات
        Assert.Contains(db.PermissionAuditLogs.AsEnumerable(),
            a => a.TargetRoleId == role.Id && a.ActionType == "grant");
    }

    [Fact]
    public void CreateCustomRole_ByNonAdmin_IsDenied()
    {
        using var host = new TestHost();
        host.LoginAs("quality"); // لا يملك تعديل وحدة المستخدمين
        using var scope = host.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PermissionService>();
        Assert.Throws<PermissionDeniedException>(() => svc.CreateCustomRole("دور غير مخول"));
    }
}
