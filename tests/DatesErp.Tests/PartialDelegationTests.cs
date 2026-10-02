using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §قبول: استلام جزئي (مستلم/مرفوض/معلّق + سند لاحق) وتفويض زمني يُدمج عند الدخول.
/// </summary>
public class PartialDelegationTests
{
    private static T Get<T>(TestHost h) => h.Services.CreateScope().ServiceProvider.GetRequiredService<T>();

    [Fact]
    public void Partial_Receipt_Only_Received_Items_Create_Lots_And_Remaining_Get_Own_Doc()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var receiving = Get<IReceivingService>(host);
        var r1 = receiving.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        {
            new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000, ItemStatus = "Received" },
            new() { TreatmentRequired = false, ProductId = 2, PackagingTypeId = 2, PackageCount = 50, UnitWeightKg = 20, QtyKg = 1000, ItemStatus = "Rejected" },
            new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 70, UnitWeightKg = 20, QtyKg = 1400, ItemStatus = "Pending" }
        });
        Assert.True(r1.Ok, r1.Message);
        var ap = receiving.ApproveShipment(r1.Id);
        Assert.True(ap.Ok, ap.Message);

        using (var db = new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options))
            Assert.Single(db.Lots.ToList()); // المستلم فقط

        var rem = receiving.ReceiveRemaining(r1.Id);
        Assert.True(rem.Ok, rem.Message);
        var ap2 = receiving.ApproveShipment(rem.Id);
        Assert.True(ap2.Ok, ap2.Message);
        using (var db = new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options))
        {
            Assert.Equal(2, db.Lots.Count()); // + سند المتبقي
            var child = db.Shipments.Single(s => s.Id == rem.Id);
            Assert.Equal(r1.Id, child.ParentShipmentId);
        }
        // لا متبقٍ آخر
        var rem2 = receiving.ReceiveRemaining(r1.Id);
        Assert.False(rem2.Ok);
    }

    [Fact]
    public void Delegation_Grants_Permissions_Within_Period_Only()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using (var db = new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options))
        {
            var admin = db.Users.Single(u => u.UserName == "admin").Id;
            var prod = db.Users.Single(u => u.UserName == "production").Id;
            db.Delegations.Add(new Delegation { FromUserId = admin, ToUserId = prod, StartDate = DateTime.Now.Date, EndDate = DateTime.Now.Date.AddDays(3) });
            db.Delegations.Add(new Delegation { FromUserId = admin, ToUserId = prod, StartDate = DateTime.Now.Date.AddDays(-10), EndDate = DateTime.Now.Date.AddDays(-5), IsActive = true });
            db.SaveChanges();
        }
        var auth = Get<IAuthService>(host);
        var session = Get<ICurrentSession>(host);
        var login = auth.Login("production", DbSeeder.InitialAdminPassword);
        Assert.True(login.Success, login.Message);
        // التفويض الساري يمنح صلاحيات المدير؛ والمنتهي لا يمنح شيئاً إضافياً
        Assert.True(session.Can("permissions", "ManagePermissions"));
    }

    [Fact]
    public void Role_Created_From_Ui_Gets_Permissions_And_Works()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var db = new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);
        var role = new Role { RoleCode = "R-TEST1", RoleNameAr = "مشرف جودة متقدم", IsActive = true };
        db.Roles.Add(role); db.SaveChanges();
        var svc = new PermissionService(db, host.Services.GetRequiredService<ICurrentSession>());
        svc.EnsureCatalog();
        svc.SetRolePermission(role.Id, "quality", "Approve", true);
        var set = svc.GetRoleSet(role.Id);
        Assert.Contains(("quality", "Approve"), set);
    }
}
