using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §اختبارات انحدار — إدارة النظام (المستخدمون والصلاحيات).
///
/// خللان حرجان أُصلحا:
///  1) لم تكن توجد أي آلية لتغيير كلمة المرور في النظام كله — MustChangePassword يُرفع
///     ولا أحد يتصرف به، فالمستخدم يبقى موسوماً إلى الأبد بلا سبيل لإرضاء الشرط.
///  2) MasterDataService.ToggleUserActive كان يقلب IsActive مباشرة فيتجاوز
///     GuardLastPermissionAdmin — فأمكن قفل النظام كلياً من شاشة المستخدمين.
/// </summary>
public class SystemAdminSecurityTests
{
    private static MasterDataService Master(TestHost host)
        => host.Services.CreateScope().ServiceProvider.GetRequiredService<MasterDataService>();

    private static int AdminId(TestHost host) => Fresh(host).Users.AsNoTracking().First(u => u.UserName == "admin").Id;

    /// <summary>سياق جديد على نفس الاتصال — يتجاوز كاش متتبّع التغييرات.</summary>
    private static DatesErpDbContext Fresh(TestHost host)
        => new DatesErpDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<DatesErpDbContext>()
            .UseSqlite(host.Connection).Options);

    private static DatesErp.Core.Domain.Entities.AppUser User(TestHost host, string name)
    {
        using var db = Fresh(host);
        return db.Users.AsNoTracking().First(u => u.UserName == name);
    }

    // ═══════════ 1) تغيير كلمة المرور ═══════════

    [Fact]
    public void ChangePassword_With_Correct_Old_Clears_MustChangePassword()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int uid = AdminId(host);

        // كل المستخدمين المبدئيين موسومون بوجوب التغيير
        Assert.True(host.Get<DatesErpDbContext>().Users.All(u => u.MustChangePassword));

        var r = Master(host).ChangePassword(uid, DbSeeder.InitialAdminPassword, "N3wPass!2026", "N3wPass!2026");
        Assert.True(r.Ok, r.Message);

        var u = User(host, "admin");
        Assert.False(u.MustChangePassword);          // ← الشرط أُرضي أخيراً
        Assert.NotNull(u.PasswordChangedDate);
        Assert.False(u.IsLocked);

        // والدخول بالكلمة الجديدة ينجح
        var auth = host.Services.CreateScope().ServiceProvider.GetRequiredService<IAuthService>();
        Assert.True(auth.Login("admin", "N3wPass!2026").Success);
        Assert.False(auth.Login("admin", DbSeeder.InitialAdminPassword).Success);
    }

    [Fact]
    public void ChangePassword_With_Wrong_Old_Is_Rejected_And_Counts_Toward_Lockout()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int uid = AdminId(host);
        var svc = Master(host);

        for (int i = 0; i < 5; i++)
        {
            var r = svc.ChangePassword(uid, "wrong-pass-" + i, "N3wPass!2026", "N3wPass!2026");
            Assert.False(r.Ok);
            Assert.Contains("غير صحيحة", r.Message ?? "");
        }

        var u = User(host, "admin");
        Assert.True(u.IsLocked, "خمس محاولات خاطئة يجب أن تقفل الحساب");
        Assert.NotNull(u.LockoutDate);
    }

    [Theory]
    [InlineData("short1", "أقصر من الحد الأدنى")]
    [InlineData("alllettersonly", "بلا أرقام")]
    [InlineData("12345678", "بلا حروف")]
    [InlineData("Admin@123", "كلمة المرور الافتراضية")]
    public void ChangePassword_Enforces_Policy(string candidate, string reason)
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int uid = AdminId(host);

        var r = Master(host).ChangePassword(uid, DbSeeder.InitialAdminPassword, candidate, candidate);
        Assert.False(r.Ok, reason);
        Assert.True(User(host, "admin").MustChangePassword, "لم تتغير كلمة المرور فيجب أن يبقى الوسم");
    }

    [Fact]
    public void ChangePassword_Rejects_Same_As_Current_And_Mismatched_Confirmation()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int uid = AdminId(host);
        var svc = Master(host);

        // نغيّرها أولاً إلى كلمة مطابقة للسياسة، ثم نحاول إعادة استخدامها
        Assert.True(svc.ChangePassword(uid, DbSeeder.InitialAdminPassword, "First!2026a", "First!2026a").Ok);
        var same = svc.ChangePassword(uid, "First!2026a", "First!2026a", "First!2026a");
        Assert.False(same.Ok);
        Assert.Contains("تختلف عن الحالية", same.Message ?? "");

        var mismatch = svc.ChangePassword(uid, "First!2026a", "N3wPass!2026", "Other!2026");
        Assert.False(mismatch.Ok);
        Assert.Contains("غير متطابقتين", mismatch.Message ?? "");
    }

    [Fact]
    public void ResetPassword_Enforces_The_Same_Policy()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int uid = AdminId(host);

        Assert.False(Master(host).ResetUserPassword(uid, "weak").Ok);
        Assert.True(Master(host).ResetUserPassword(uid, "Reset!2026x").Ok);
        Assert.True(User(host, "admin").MustChangePassword);
    }

    // ═══════════ 2) حارس آخر مدير صلاحيات ═══════════

    /// <summary>يجعل الجلسة تعمل كمستخدم الإنتاج مع صلاحية إدارة المستخدمين.</summary>
    private static void ActAsProduction(TestHost host)
    {
        var session = host.Services.GetRequiredService<DatesErp.Infrastructure.Session.SessionContext>();
        session.UserId = User(host, "production").Id;
        session.UserName = "production";
        session.PermissionCache[("users", "Edit")] = true;
    }

    [Fact]
    public void ToggleUserActive_Cannot_Deactivate_Last_Permission_Admin()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int uid = AdminId(host);

        // admin هو الوحيد الذي يملك ManagePermissions بعد التهيئة.
        // نتصرّف كمستخدم آخر لأن توقيف الحساب الحالي ممنوع سابقاً لهذا الفحص بحكم التصميم.
        ActAsProduction(host);

        var r = Master(host).ToggleUserActive(uid);
        Assert.False(r.Ok);
        Assert.Contains("آخر مستخدم يملك صلاحية إدارة الصلاحيات", r.Message ?? "");
        Assert.True(User(host, "admin").IsActive);
    }

    [Fact]
    public void ToggleUserActive_Allows_Deactivation_When_Another_Admin_Remains()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int adminId = AdminId(host);

        // نمنح مستخدم الإنتاج صلاحية إدارة الصلاحيات كاستثناء ← لم يعد admin الأخير
        var session = host.Services.GetRequiredService<DatesErp.Infrastructure.Session.SessionContext>();
        var perms = new PermissionService(Fresh(host), session);
        int prodId = User(host, "production").Id;
        perms.SetUserPermission(prodId, "permissions", "ManagePermissions", true);

        ActAsProduction(host);
        var r = Master(host).ToggleUserActive(adminId);
        Assert.True(r.Ok, r.Message);
        Assert.False(User(host, "admin").IsActive);
    }

    // ═══════════ 3) فك القفل التلقائي ═══════════

    [Fact]
    public void Locked_Account_Auto_Unlocks_After_The_Configured_Period()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using (var db = Fresh(host))
        {
            var u = db.Users.First(x => x.UserName == "admin");
            u.IsLocked = true;
            u.LockoutDate = DateTime.Now.AddMinutes(-31);   // انقضت المدة الافتراضية (30 دقيقة)
            db.SaveChanges();
        }

        var auth = host.Services.CreateScope().ServiceProvider.GetRequiredService<IAuthService>();
        var r = auth.Login("admin", DbSeeder.InitialAdminPassword);
        Assert.True(r.Success, r.Message);
        Assert.False(User(host, "admin").IsLocked);
    }

    [Fact]
    public void Locked_Account_Stays_Locked_Before_The_Period_Elapses()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using (var db = Fresh(host))
        {
            var u = db.Users.First(x => x.UserName == "admin");
            u.IsLocked = true;
            u.LockoutDate = DateTime.Now.AddMinutes(-5);
            db.SaveChanges();
        }

        var auth = host.Services.CreateScope().ServiceProvider.GetRequiredService<IAuthService>();
        var r = auth.Login("admin", DbSeeder.InitialAdminPassword);
        Assert.False(r.Success);
        Assert.Contains("مقفل", r.Message ?? "");
    }

    // ═══════════ 4) كتالوج الصلاحيات ═══════════

    [Fact]
    public void Administrator_Role_Covers_Every_Resource_And_Operation()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var db = Fresh(host);
        var perms = new PermissionService(db, host.Services.GetRequiredService<DatesErp.Infrastructure.Session.SessionContext>());
        perms.EnsureCatalog();

        int adminRoleId = db.Roles.Single(r => r.RoleCode == "Administrator").Id;
        var set = perms.GetRoleSet(adminRoleId);

        Assert.Equal(
            PermissionService.ResourceCatalog.Length * PermissionService.OperationCatalog.Length,
            set.Count);
        Assert.Contains(("users", "ManagePermissions"), set);
        Assert.Contains(("planning", "Approve"), set);
        Assert.Contains(("delivery", "Cancel"), set);
    }

    [Fact]
    public void User_Exception_Overrides_Role_Permission()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var db = Fresh(host);
        var session = host.Services.GetRequiredService<DatesErp.Infrastructure.Session.SessionContext>();
        var perms = new PermissionService(db, session);
        perms.EnsureCatalog();

        int prodId = db.Users.First(u => u.UserName == "production").Id;
        var roleIds = db.UserRoles.Where(ur => ur.UserId == prodId && ur.IsActive).Select(ur => ur.RoleId).ToList();

        // دور الإنتاج لا يملك إدارة الصلاحيات
        var before = perms.BuildEffectiveCache(prodId, roleIds);
        Assert.False(before.TryGetValue(("permissions", "ManagePermissions"), out var b) && b);

        // الاستثناء على المستخدم يعلو على الدور
        perms.SetUserPermission(prodId, "permissions", "ManagePermissions", true);
        var after = perms.BuildEffectiveCache(prodId, roleIds);
        Assert.True(after[("permissions", "ManagePermissions")]);
    }
}

/// <summary>
/// §اختبارات انحدار — المجموعات والفئات والوحدات في إدارة الأصناف.
///
/// قبل الإصلاح: لم توجد أي دالة لحفظ مجموعة أصناف (ItemGroup) — المجموعات الأربع
/// كانت تُبذر فقط. ولم يكن للمستخدم أي تصنيف حر: النظام يفرض المجموعة والوحدة
/// من نوع الصنف (وهذا صحيح بنيوياً)، لكن لم تكن هناك طبقة تصنيف فوقها.
/// </summary>
public class ItemGroupsAndCategoriesTests
{
    private static MasterDataService Master(TestHost host)
        => host.Services.CreateScope().ServiceProvider.GetRequiredService<MasterDataService>();

    private static DatesErpDbContext Fresh(TestHost host)
        => new DatesErpDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<DatesErpDbContext>()
            .UseSqlite(host.Connection).Options);

    [Fact]
    public void ItemGroup_Can_Be_Added()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int before = Fresh(host).ItemGroups.Count();

        var r = Master(host).SaveItemGroup(null, "005", "تمور التصدير", "Finished", "كرتون");
        Assert.True(r.Ok, r.Message);
        Assert.Equal(before + 1, Fresh(host).ItemGroups.Count());
        Assert.Contains(Fresh(host).ItemGroups.ToList(), g => g.GroupCode == "005" && g.GroupNameAr == "تمور التصدير");
    }

    [Fact]
    public void ItemGroup_Duplicate_Code_Is_Rejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        Assert.True(Master(host).SaveItemGroup(null, "006", "فئة جديدة", "Raw", "كجم").Ok);
        var dup = Master(host).SaveItemGroup(null, "006", "نفس الكود", "Raw", "كجم");
        Assert.False(dup.Ok);
        Assert.Contains("محجوز", dup.Message ?? "");
    }

    [Fact]
    public void ItemCategory_Is_Free_And_Does_Not_Change_Group_Or_Unit()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();

        var cat = Master(host).SaveItemCategory(null, "SUKKARI", "سكري");
        Assert.True(cat.Ok, cat.Message);

        // صنف تام — النظام يفرض 002/كرتون (وهذا صحيح بنيوياً)
        var prod = Master(host).SaveProductFull(null, "002-900", "سكري فاخر", "002", "Finished", "كرتون",
            10, 5, 2, new List<(int, int?, int)>(), null, null);
        Assert.True(prod.Ok, prod.Message);
        int pid = prod.Id;

        using (var db = Fresh(host))
        {
            var p = db.Products.AsNoTracking().First(x => x.Id == pid);
            Assert.Equal("002", p.GroupCode);
            Assert.Equal("كرتون", p.UnitOfMeasure);
            Assert.Null(p.CategoryId);
        }

        // ربط الفئة الحرة — لا تغيّر المجموعة ولا الوحدة
        var link = Master(host).SetProductCategory(pid, cat.Id);
        Assert.True(link.Ok, link.Message);

        using (var db = Fresh(host))
        {
            var p = db.Products.AsNoTracking().First(x => x.Id == pid);
            Assert.Equal(cat.Id, p.CategoryId);
            Assert.Equal("002", p.GroupCode);        // لم تتغير
            Assert.Equal("كرتون", p.UnitOfMeasure);  // لم تتغير
        }
    }

    [Fact]
    public void Category_Link_Rejects_Unknown_Or_Inactive_Category()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var prod = Master(host).SaveProductFull(null, "002-901", "خلاص فاخر", "002", "Finished", "كرتون",
            10, 5, 2, new List<(int, int?, int)>(), null, null);
        Assert.True(prod.Ok, prod.Message);

        var bad = Master(host).SetProductCategory(prod.Id, 99999);
        Assert.False(bad.Ok);
        Assert.Contains("غير موجودة", bad.Message ?? "");
    }

    [Fact]
    public void Unit_Can_Be_Added_Freely()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        // §B52: أسماء جديدة تُضاف بحرية، أما اسم موجود («لفة» مبذورة) فيُرفض تكراره
        foreach (var u in new[] { "كيس", "قفة", "طبق", "عبوة معدنية" })
            Assert.True(Master(host).SaveUnit(null, u, true).Ok, u);
        Assert.False(Master(host).SaveUnit(null, "لفة", true).Ok);

        var names = Fresh(host).UnitsOfMeasure.Select(u => u.UnitNameAr).ToList();
        Assert.Contains("كيس", names);
        Assert.Contains("قفة", names);
        Assert.Contains("طبق", names);
        Assert.Contains("عبوة معدنية", names);
    }
}

/// <summary>
/// §لمسات مؤسسية — انتهاء صلاحية كلمة المرور (قرار #47) والوعي الأمني بآخر دخول.
/// قبل الإضافة: PasswordChangedDate كان يُسجَّل لكن لا أحد يفحصه، فالمدة لا تنتهي أبداً.
/// </summary>
public class PasswordExpiryTests
{
    private static DatesErpDbContext Fresh(TestHost host)
        => new DatesErpDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<DatesErpDbContext>()
            .UseSqlite(host.Connection).Options);

    private static IAuthService Auth(TestHost host)
        => host.Services.CreateScope().ServiceProvider.GetRequiredService<IAuthService>();

    [Fact]
    public void Fresh_Password_Is_Not_Expired()
    {
        using var host = new TestHost();
        var db = host.Get<DatesErpDbContext>();
        var u = db.Users.First(x => x.UserName == "admin");
        u.PasswordChangedDate = DateTime.Now;      // غُيّرت الآن
        u.MustChangePassword = false;
        db.SaveChanges();

        var r = Auth(host).Login("admin", DbSeeder.InitialAdminPassword);
        Assert.True(r.Success, r.Message);
        Assert.False(r.PasswordExpired);
        Assert.True(r.PasswordAgeDays >= 0 && r.PasswordAgeDays <= 1);
        Assert.False(r.MustChangePassword);
    }

    [Fact]
    public void Password_Older_Than_MaxAge_Forces_Change()
    {
        using var host = new TestHost();
        var db = host.Get<DatesErpDbContext>();
        var u = db.Users.First(x => x.UserName == "admin");
        u.PasswordChangedDate = DateTime.Now.AddDays(-120);   // أقدم من 90 يوماً
        u.MustChangePassword = false;
        db.SaveChanges();

        var r = Auth(host).Login("admin", DbSeeder.InitialAdminPassword);
        Assert.True(r.Success, r.Message);
        Assert.True(r.PasswordExpired, "كلمة مرور عمرها 120 يوماً يجب أن تُعتبر منتهية");
        Assert.True(r.MustChangePassword, "انتهاء المدة يجب أن يفرض التغيير");
        Assert.True(r.PasswordAgeDays >= 119);
    }

    [Fact]
    public void Seeded_Account_Without_ChangeDate_Is_Treated_As_Expired()
    {
        using var host = new TestHost();
        // الحساب المبذوق لم تُغيَّر كلمته قط — لا يجوز أن يبقى صالحاً للأبد
        var r = Auth(host).Login("admin", DbSeeder.InitialAdminPassword);
        Assert.True(r.Success, r.Message);
        Assert.True(r.PasswordExpired);
        Assert.Equal(-1, r.PasswordAgeDays);
    }

    [Fact]
    public void MaxAge_Is_Configurable()
    {
        using var host = new TestHost();
        var db = host.Get<DatesErpDbContext>();
        db.SystemSettings.Add(new DatesErp.Core.Domain.Entities.SystemSetting
        { SettingKey = "PasswordMaxAgeDays", SettingValue = "365" });
        var u = db.Users.First(x => x.UserName == "admin");
        u.PasswordChangedDate = DateTime.Now.AddDays(-120);
        u.MustChangePassword = false;
        db.SaveChanges();

        var r = Auth(host).Login("admin", DbSeeder.InitialAdminPassword);
        Assert.True(r.Success, r.Message);
        Assert.False(r.PasswordExpired, "بعد ضبط المدة على 365 يوماً، 120 يوماً ليست منتهية");
    }

    [Fact]
    public void Login_Reports_Previous_Login_Date()
    {
        using var host = new TestHost();
        var db = host.Get<DatesErpDbContext>();
        var u = db.Users.First(x => x.UserName == "admin");
        u.PasswordChangedDate = DateTime.Now;
        u.MustChangePassword = false;
        var when = new DateTime(2026, 8, 1, 9, 30, 0);
        u.LastLoginDate = when;
        db.SaveChanges();

        var r = Auth(host).Login("admin", DbSeeder.InitialAdminPassword);
        Assert.True(r.Success, r.Message);
        Assert.Equal(when, r.LastLoginDate);      // السابق، لا الحالي
    }
}
