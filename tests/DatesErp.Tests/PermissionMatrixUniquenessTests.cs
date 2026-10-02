using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>Permissions must be a function of (principal, resource, operation), not OR-ed duplicate rows.</summary>
public class PermissionMatrixUniquenessTests
{
    [Fact]
    public void Seed_Is_Unique_And_A_Revoked_Role_Grant_Cannot_Reappear_From_A_Second_Row()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var session = host.Get<DatesErp.Infrastructure.Session.SessionContext>();
        int admin = db.Roles.Single(r => r.RoleCode == "Administrator").Id;
        int resource = db.PermissionResources.Single(r => r.Code == "planning").Id;
        int operation = db.PermissionOperations.Single(o => o.Code == "View").Id;
        Assert.Single(db.RoleResourcePermissions.AsNoTracking().Where(p => p.RoleId == admin
            && p.ResourceId == resource && p.OperationId == operation));

        var permissions = new PermissionService(db, session);
        permissions.SetRolePermission(admin, "planning", "View", false);
        Assert.False(permissions.BuildEffectiveCache(session.UserId, new List<int> { admin })[("planning", "View")]);

        // The database (not only the C# setter) must reject a conflicting row
        // from another client. Its failed insert cannot resurrect the grant.
        db.RoleResourcePermissions.Add(new RoleResourcePermission
        {
            RoleId = admin, ResourceId = resource, OperationId = operation, IsAllowed = true
        });
        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
        db.ChangeTracker.Clear();
        Assert.Single(db.RoleResourcePermissions.AsNoTracking().Where(p => p.RoleId == admin
            && p.ResourceId == resource && p.OperationId == operation && !p.IsAllowed));
    }

    [Fact]
    public void Copy_Role_Permissions_Remains_Atomic_Under_Unique_Keys()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var session = host.Get<DatesErp.Infrastructure.Session.SessionContext>();
        var p = new PermissionService(db, session);
        int source = db.Roles.Single(r => r.RoleCode == "Production").Id;
        int target = db.Roles.Single(r => r.RoleCode == "Quality").Id;
        p.CopyRolePermissions(source, target);
        var keys = db.RoleResourcePermissions.AsNoTracking().Where(r => r.RoleId == target).ToList();
        Assert.NotEmpty(keys);
        Assert.Equal(keys.Count, keys.Select(x => (x.ResourceId, x.OperationId)).Distinct().Count());
        Assert.Contains(db.PermissionAuditLogs, a => a.TargetRoleId == target && a.ActionType == "copy");
    }
}
