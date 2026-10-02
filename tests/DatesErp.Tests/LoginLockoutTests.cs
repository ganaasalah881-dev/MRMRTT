using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §v1.50.30 — مشكلة المستخدم: هو المدير الوحيد والحساب يقفل مؤقتاً بعد محاولات
/// خاطئة والرسالة «راجع مسؤول النظام» طريق مسدود. الإصلاحات:
/// ① الوقت المعروض من الإعداد الفعلي (كان 30 ثابتة) ② الحساب المدير المفروز
/// يُخبر أنه يستطيع فك قفله بنفسه ③ EmergencyUnlockAdmins يفك القفل ويعيد
/// كلمة مؤقتة Admin@123 مع فرض التغيير ④ فك القفل التلقائي بعد انقضاء المدة.
/// </summary>
public class LoginLockoutTests
{
    private static (IAuthService auth, DatesErpDbContext db) Svc(TestHost host)
    {
        host.LoginAsAdmin();
        return (host.Get<IAuthService>(), host.Get<DatesErpDbContext>());
    }

    [Fact]
    public void Locked_Admin_Message_Tells_Him_To_Self_Unlock_And_Result_Flags_Admin()
    {
        using var host = new TestHost();
        var (auth, db) = Svc(host);
        var admin = db.Users.Include(u => u.UserRoles).First(u => u.UserName == "admin");
        admin.IsLocked = true;
        admin.LockoutDate = DateTime.Now.AddMinutes(-5);
        db.SaveChanges();

        var r = auth.Login("admin", "wrong");
        Assert.False(r.Success);
        Assert.True(r.LockedIsAdmin);
        Assert.Contains("فك قفل حساب المدير", r.Message);   // الطريق ليس مسدوداً
        Assert.DoesNotContain("راجع مسؤول النظام", r.Message);
        Assert.Matches(@"الساعة \d{2}:\d{2}", r.Message);
    }

    [Fact]
    public void Locked_NonAdmin_Message_Keeps_The_Admin_Referral()
    {
        using var host = new TestHost();
        var (auth, db) = Svc(host);
        var u = new AppUser
        {
            UserName = "locked_clerk", FullName = "موظف", IsActive = true,
            PasswordHash = "x", PasswordSalt = "y",
        };
        db.Users.Add(u);
        db.SaveChanges();
        u.IsLocked = true; u.LockoutDate = DateTime.Now.AddMinutes(-1);
        db.SaveChanges();

        var r = auth.Login("locked_clerk", "whatever");
        Assert.False(r.Success);
        Assert.False(r.LockedIsAdmin);
        Assert.Contains("راجع مسؤول النظام", r.Message);
    }

    [Fact]
    public void Emergency_Unlock_Resets_Admin_Password_And_Forces_Change()
    {
        using var host = new TestHost();
        var (auth, db) = Svc(host);
        var admin = db.Users.First(u => u.UserName == "admin");
        admin.IsLocked = true; admin.FailedLoginCount = 5; admin.LockoutDate = DateTime.Now;
        db.SaveChanges();

        var r = auth.EmergencyUnlockAdmins();
        Assert.True(r.Ok, r.Message);
        Assert.Contains("Admin@123", r.Message);
        var after = db.Users.AsNoTracking().First(x => x.Id == admin.Id);
        Assert.False(after.IsLocked);
        Assert.Equal(0, after.FailedLoginCount);
        Assert.True(after.MustChangePassword);
        // الدخول بالكلمة المؤقتة ينجح ويطلب التغيير الإجباري
        var login = auth.Login("admin", "Admin@123");
        Assert.True(login.Success, login.Message);
        Assert.True(login.MustChangePassword);
    }

    [Fact]
    public void Auto_Unlock_Honors_Configured_LockoutMinutes()
    {
        using var host = new TestHost();
        var (auth, db) = Svc(host);
        db.SystemSettings.Add(new SystemSetting { SettingKey = "LockoutMinutes", SettingValue = "60" });
        db.SaveChanges();
        var admin = db.Users.First(u => u.UserName == "admin");
        admin.IsLocked = true;
        admin.LockoutDate = DateTime.Now.AddMinutes(-45);     // ضمن مدة الـ60 → يبقى مقفلاً
        db.SaveChanges();
        var r1 = auth.Login("admin", "wrong");
        Assert.True(r1.LockedIsAdmin);
        // §الوقت المعروض من الإعداد: LockoutDate + 60 دقيقة (كان يعرض +30 دائماً)
        string expected = admin.LockoutDate!.Value.AddMinutes(60).ToString("HH:mm");
        Assert.Contains(expected, r1.Message);

        admin.LockoutDate = DateTime.Now.AddMinutes(-61);     // تجاوز المدة → فك تلقائي
        db.SaveChanges();
        var r2 = auth.Login("admin", "wrong");                // فُك القفل: الخطأ أصبح «كلمة غير صحيحة»
        Assert.False(r2.LockedIsAdmin);
        Assert.Contains("كلمة المرور غير صحيحة", r2.Message);
        Assert.Equal(1, db.Users.AsNoTracking().First(x => x.UserName == "admin").FailedLoginCount);
    }
}
