using DatesErp.Core.Common;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §v1.50.30 — استعادة طوارئ حساب المدير: فك القفل وإعادة كلمة المرور المؤقتة Admin@123
/// مع فرض تغييرها عند أول دخول. الهدف: حاملو دور Administrator النشطون فقط؛ وإن فُقدت
/// الأدوار (قاعدة تالفة) فالمسار الاحتياطي حساب admin بالاسم — لا تُمس بقية الحسابات أبداً.
/// يتطلب وصولاً فعلياً لجهاز المستخدم — لذلك هو إجراء طوارئ آمن محلياً.
/// </summary>
public static class AdminRecovery
{
    public const string TempPassword = "Admin@123";

    /// <summary>يفك قفل حسابات المديرين ويعيد كلمتهم المؤقتة. يُعيد عدد الحسابات المعالجة.</summary>
    public static int ResetAdminAccounts(DatesErpDbContext db)
    {
        var adminRoleIds = db.Roles.Where(r => r.RoleCode == SystemRoles.Administrator).Select(r => r.Id).ToList();
        var adminUserIds = db.UserRoles.Where(ur => adminRoleIds.Contains(ur.RoleId) && ur.IsActive)
            .Select(ur => ur.UserId).Distinct().ToList();
        var targets = db.Users.Where(u => u.IsActive && adminUserIds.Contains(u.Id)).ToList();
        if (targets.Count == 0)
            targets = db.Users.Where(u => u.IsActive && u.UserName == "admin").ToList();
        int resetCount = 0;
        foreach (var u in targets)
        {
            var (h, s) = PasswordHasher.Hash(TempPassword);
            u.PasswordHash = h;
            u.PasswordSalt = s;
            u.MustChangePassword = true;
            u.IsLocked = false;
            u.FailedLoginCount = 0;
            u.LockoutDate = null;
            resetCount++;
        }
        if (resetCount > 0) db.SaveChanges();
        return resetCount;
    }
}
