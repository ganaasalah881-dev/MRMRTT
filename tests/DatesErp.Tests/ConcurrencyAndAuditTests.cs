using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>§5 — التزامن التفاؤلي: لا يُسمح لكتابة مستخدم أن تمسح كتابة مستخدم آخر بصمت.</summary>
public class ConcurrencyTests
{
    [Fact]
    public void Two_Users_Editing_Same_Record_Conflict_Is_Detected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();

        int customerId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            customerId = db.Customers.First().Id;
        }

        // المستخدم (أ) يفتح السجل
        using var scopeA = host.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var custA = dbA.Customers.Single(c => c.Id == customerId);

        // المستخدم (ب) يعدل نفس السجل ويحفظ أولاً
        using (var scopeB = host.Services.CreateScope())
        {
            var dbB = scopeB.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var custB = dbB.Customers.Single(c => c.Id == customerId);
            custB.CustomerName = "تعديل المستخدم ب";
            dbB.SaveChanges(); // نجح — وغيّر رمز التزامن
        }

        // المستخدم (أ) يحاول الحفظ فوقه → يجب أن يُرفض
        custA.CustomerName = "تعديل المستخدم أ";
        Assert.Throws<DbUpdateConcurrencyException>(() => dbA.SaveChanges());
    }

    [Fact]
    public void Conflict_Exception_Message_Is_User_Friendly_Arabic()
    {
        var ex = new ConcurrencyConflictException();
        Assert.Contains("مستخدم آخر", ex.Message);
        Assert.Contains("إعادة تحميل", ex.Message);
        Assert.DoesNotContain("StackTrace", ex.Message);
        Assert.DoesNotContain("DbUpdateConcurrencyException", ex.Message);
    }
}

/// <summary>§26/§10 — التدقيق والصلاحيات.</summary>
public class AuditAndRbacTests
{
    [Fact]
    public void Audit_Records_Login_And_Operations_With_Machine()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();

        var receiving = host.Get<IReceivingService>();
        var r = receiving.SaveShipment(1, null, null, new List<ShipmentItemDto>
        {
            new() { TreatmentRequired = false, ProductId = 1, PackageCount = 10, UnitWeightKg = 20, QtyKg = 200 }
        });
        Assert.True(r.Ok, r.Message);

        var db = host.Get<DatesErpDbContext>();
        Assert.Contains(db.AuditLogs, a => a.ActionType == "Login");
        Assert.Contains(db.AuditLogs, a => a.ActionType == "Create" && a.DocumentType == nameof(Core.Domain.Entities.Shipment));
        Assert.All(db.AuditLogs, a => Assert.False(string.IsNullOrEmpty(a.MachineName)));
    }

    [Fact]
    public void Role_Permissions_Matrix_Is_Central_And_Enforced()
    {
        using var host = new TestHost();
        var db = host.Get<DatesErpDbContext>();
        // الأدوار السبعة المركزية موجودة في القاعدة وليس على الجهاز (§10)
        var roles = db.Roles.Select(r => r.RoleCode).ToList();
        foreach (var expected in new[] { "Administrator", "Management", "Finance", "Warehouse", "Production", "Quality", "Sales" })
            Assert.Contains(expected, roles);
        // لكل دور صلاحيات على الوحدات
        Assert.True(db.RolePermissions.Count() >= 7 * 10);

        // مستخدم الجودة: يرى الجودة فقط للتعديل
        var auth = host.Get<IAuthService>();
        var login = auth.Login("quality", DbSeeder.InitialAdminPassword);
        Assert.True(login.Success);
        var session = host.Get<Infrastructure.Session.SessionContext>();
        Assert.True(session.Can("quality", "Create"));
        Assert.False(session.Can("planning", "Create"));
        Assert.False(session.Can("delivery", "Approve"));
    }

    [Fact]
    public void Failed_Logins_Lock_Account_After_5_Attempts()
    {
        using var host = new TestHost();
        var auth = host.Get<IAuthService>();
        for (int i = 0; i < 5; i++)
            auth.Login("admin", "خاطئة");
        var r = auth.Login("admin", DbSeeder.InitialAdminPassword);
        Assert.False(r.Success);
        Assert.Contains("مقفل", r.Message);
    }

    [Fact]
    public void Machine_Registry_Records_Connected_Devices()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var reg = host.Get<DatesErp.Application.Services.MachineRegistry>();
        reg.Heartbeat("1.0.0");
        var db = host.Get<DatesErpDbContext>();
        Assert.Single(db.ClientMachines);
        Assert.NotNull(db.ClientMachines.Single().MachineId);
    }
}
