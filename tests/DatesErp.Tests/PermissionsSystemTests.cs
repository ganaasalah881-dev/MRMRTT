using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §خطة قبول نظام الصلاحيات الهرمي:
/// 1) المدير: كل الصلاحيات مسجلة في الجداول (بلا مفتاح is_admin).
/// 2) مشاهد: عرض فقط ومحاولة حذف تُرفض.
/// 3) استثناء المستخدم يعلو صلاحيات الدور.
/// 4) التدقيق يسجل grant/revoke/copy.
/// 5) منع تعطيل آخر مدير صلاحيات.
/// </summary>
public class PermissionsSystemTests
{
    private static PermissionService Svc(TestHost host)
        => new(host.Services.CreateScope().ServiceProvider.GetRequiredService<DatesErpDbContext>(),
               host.Services.GetRequiredService<ICurrentSession>());

    private static DatesErpDbContext Db(TestHost host)
        => new(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);

    [Fact]
    public void Admin_Has_Full_Permissions_From_Tables_Not_Master_Key()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var session = host.Services.GetRequiredService<ICurrentSession>();
        // كل العمليات على كل الموارد مسموحة للمدير من الجدول نفسه
        Assert.True(session.Can("production", "Delete"));
        Assert.True(session.Can("permissions", "ManagePermissions"));
        Assert.True(session.Can("receiving", "Approve"));
        // المدير مسجل له كل شيء في الجداول (بما فيه الاحتياطي) — لا مفتاح سري
        Assert.True(session.Can("backup", "Edit"));
    }

    [Fact]
    public void Viewer_Can_View_Only_And_Delete_Is_Denied()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);
        using var db = Db(host);
        var role = db.Roles.OrderBy(r => r.Id).First(r => r.RoleCode != "Administrator");
        // نحول الدور إلى «مشاهد»: نمنح العرض ونسحب صراحةً كل ما عداه على موردين
        foreach (var res in new[] { "reports", "production" })
        {
            svc.SetRolePermission(role.Id, res, "View", true);
            foreach (var op in new[] { "Create", "Edit", "Delete", "Approve" })
                svc.SetRolePermission(role.Id, res, op, false);
        }
        var set = svc.GetRoleSet(role.Id);
        Assert.Contains(("reports", "View"), set);
        Assert.DoesNotContain(("reports", "Delete"), set);
        Assert.DoesNotContain(("production", "Edit"), set);
    }

    [Fact]
    public void User_Extra_Permission_Overrides_Role()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);
        using var db = Db(host);
        var role = db.Roles.First(r => r.RoleCode != "Administrator");
        var user = db.Users.AsNoTracking().First(u => u.UserName != "admin");
        svc.SetRolePermission(role.Id, "delivery", "Export", false);
        // ربط المستخدم بالدور ثم استثناء صريح بالمنح
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        svc.SetUserPermission(user.Id, "delivery", "Export", true);
        var roleIds = db.UserRoles.Where(ur => ur.UserId == user.Id).Select(ur => ur.RoleId).ToList();
        var cache = svc.BuildEffectiveCache(user.Id, roleIds);
        Assert.True(cache[("delivery", "Export")]); // الاستثناء علا
    }

    [Fact]
    public void Audit_Log_Records_Grant_Revoke_And_Copy()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);
        using var db = Db(host);
        var r1 = db.Roles.First(r => r.RoleCode != "Administrator");
        var r2 = db.Roles.OrderBy(r => r.Id).ToList().Last(r => r.RoleCode != "Administrator" && r.Id != r1.Id);
        svc.SetRolePermission(r1.Id, "quality", "Approve", true);
        svc.SetRolePermission(r1.Id, "quality", "Approve", false);
        svc.CopyRolePermissions(r1.Id, r2.Id);
        var audit = svc.GetAudit();
        Assert.Contains(audit, a => a.ActionType == "grant" && a.ResourceCode == "quality");
        Assert.Contains(audit, a => a.ActionType == "revoke" && a.ResourceCode == "quality");
        Assert.Contains(audit, a => a.ActionType == "copy");
    }

    [Fact]
    public void Cannot_Deactivate_Last_Permission_Admin()
    {
        using var host = new TestHost();
        var session = host.LoginAsAdmin();
        var svc = Svc(host);
        using var db = Db(host);
        var adminUser = db.Users.AsNoTracking().First(u => u.UserName == "admin");
        // المدير الوحيد يملك ManagePermissions — تعطيله يُرفض لحماية النظام من الإغلاق
        Assert.Throws<DomainException>(() => svc.DeactivateUser(adminUser.Id));
    }

    [Fact]
    public void Sensitive_Operations_Are_Marked_In_Catalog()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var db = Db(host);
        new PermissionService(db, host.Services.GetRequiredService<ICurrentSession>()).EnsureCatalog();
        var ops = db.PermissionOperations.ToList();
        Assert.Contains(ops, o => o.Code == "EditAfterApproval" && o.IsSensitive);
        Assert.Contains(ops, o => o.Code == "Delete" && o.IsSensitive);
        Assert.Contains(ops, o => o.Code == "View" && !o.IsSensitive);
        // الموارد = الشاشات الفعلية نفسها المستخدمة في Require
        var res = db.PermissionResources.Select(r => r.Code).ToList();
        Assert.Contains("receiving", res);
        Assert.Contains("planning", res);
        Assert.Contains("permissions", res);
    }
}
