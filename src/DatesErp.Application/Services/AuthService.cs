using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Security;
using DatesErp.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>§11 — تسجيل الدخول وتحميل الأدوار والصلاحيات في الجلسة.</summary>
public class AuthService : IAuthService
{
    private readonly DatesErpDbContext _db;
    private readonly SessionContext _session;
    private readonly IAuditService _audit;

    public AuthService(DatesErpDbContext db, SessionContext session, IAuditService audit)
    {
        _db = db;
        _session = session;
        _audit = audit;
    }

    public LoginResult Login(string userName, string password)
    {
        // §الدخول بالرقم: يقبل رقم الدخول (UserCode) أو رقم الموظف أو اسم المستخدم
        var loginKey = (userName ?? "").Trim();
        var user = _db.Users.Include(u => u.UserRoles).FirstOrDefault(u => u.UserName == loginKey)
            ?? _db.Users.Include(u => u.UserRoles).FirstOrDefault(u => u.UserCode == loginKey)
            ?? _db.Users.Include(u => u.UserRoles).FirstOrDefault(u =>
                u.EmployeeId != null && u.EmployeeId == _db.Employees.Where(e => e.EmployeeCode == loginKey).Select(e => (int?)e.Id).FirstOrDefault());
        // A long-lived login window can hold a tracked account that was unlocked,
        // disabled or had its password reset from another machine. Always refresh
        // the authoritative state (including its rowversion) before deciding.
        if (user != null)
        {
            var entry = _db.Entry(user);
            entry.Reload();
            if (entry.State == EntityState.Detached) user = null;
        }
        // §R6 — رسالة واحدة موحدة للمجهول/المعطّل/كلمة الخطأ: لا كشف لحالة الحسابات (منع الاستنتاج)
        if (user == null || !user.IsActive)
            return new LoginResult { Success = false, Message = "رقم الدخول أو كلمة المرور غير صحيحة." };
        // §إصلاح: فك القفل التلقائي بعد انقضاء المدة — كان القفل دائماً لا يفكّه إلا المدير
        // أو ملف استعادة الطوارئ، فأي قفل عارض كان يوقف العمل حتى تدخل إداري.
        int lockoutMinutes = 30;
        var lockSetting = _db.SystemSettings.AsNoTracking().FirstOrDefault(x => x.SettingKey == "LockoutMinutes");
        if (lockSetting != null && int.TryParse(lockSetting.SettingValue, out var lm) && lm > 0) lockoutMinutes = lm;
        if (user.IsLocked && user.LockoutDate != null)
        {
            if ((DateTime.Now - user.LockoutDate.Value).TotalMinutes >= lockoutMinutes)
            {
                user.IsLocked = false;
                user.FailedLoginCount = 0;
                user.LockoutDate = null;
                _db.SaveChanges();
            }
        }
        if (user.IsLocked)
        {
            // §v1.50.30: الوقت المعروض من الإعداد الفعلي (كان 30 ثابتة مهما تغيّر الإعداد)،
            // والمدير لا يُرسل إلى «راجع مسؤول النظام» — هو المسؤول: يُفك قفله من زر الطوارئ بالأسفل.
            string when = user.LockoutDate != null
                ? user.LockoutDate.Value.AddMinutes(lockoutMinutes).ToString("HH:mm")
                : "—";
            bool isAdmin = user.UserRoles.Where(r => r.IsActive)
                .Join(_db.Roles, r => r.RoleId, ro => ro.Id, (r, ro) => ro.RoleCode)
                .Contains(SystemRoles.Administrator);
            string msg = isAdmin
                ? $"الحساب مقفل مؤقتاً بعد محاولات دخول خاطئة — يُفك تلقائياً الساعة {when}.\n" +
                  "أنت مسؤول النظام: يمكنك فك القفل فوراً من زر «فك قفل حساب المدير» في هذه الشاشة."
                : $"الحساب مقفل — يُفك تلقائياً الساعة {when} أو راجع مسؤول النظام.";
            return new LoginResult { Success = false, Message = msg, LockedIsAdmin = isAdmin };
        }
        if (!PasswordHasher.Verify(password ?? "", user.PasswordHash, user.PasswordSalt))
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= 5) { user.IsLocked = true; user.LockoutDate = DateTime.Now; }
            _db.SaveChanges();
            return new LoginResult { Success = false, Message = "رقم الدخول أو كلمة المرور غير صحيحة." };
        }

        user.FailedLoginCount = 0;
        var previousLogin = user.LastLoginDate;
        user.LastLoginDate = DateTime.Now;

        // §لمسة مؤسسية (قرار #47): انتهاء صلاحية كلمة المرور بعد مدة قابلة للضبط (افتراضي 90 يوماً).
        int maxAge = 90;
        var ageSetting = _db.SystemSettings.AsNoTracking().FirstOrDefault(x => x.SettingKey == "PasswordMaxAgeDays");
        if (ageSetting != null && int.TryParse(ageSetting.SettingValue, out var parsedAge) && parsedAge > 0)
            maxAge = parsedAge;
        int ageDays = user.PasswordChangedDate != null
            ? (int)(DateTime.Now - user.PasswordChangedDate.Value).TotalDays
            : int.MaxValue;   // لم تُغيَّر قط (حساب مبذوق) → تُعتبر منتهية
        bool expired = ageDays >= maxAge;
        if (expired) user.MustChangePassword = true;

        // تعبئة الجلسة: الأدوار + المصفوفة المركزية + التفويض الساري — مسار واحد مشترك (§R2)
        _session.UserId = user.Id;
        _session.UserName = user.UserName;
        new PermissionService(_db, _session).RefreshSessionCache(user.Id);

        _db.SaveChanges();
        _audit.Log("Login", "Login", "Users", user.UserName, user.Id);
        return new LoginResult
        {
            Success = true,
            UserId = user.Id,
            FullName = user.FullName,
            Roles = _session.Roles.ToList(),
            MustChangePassword = user.MustChangePassword,
            PasswordExpired = expired,
            PasswordAgeDays = ageDays == int.MaxValue ? -1 : ageDays,
            LastLoginDate = previousLogin,
            Message = "تم تسجيل الدخول."
        };
    }

    public void Logout()
    {
        if (_session.UserId > 0)
            _audit.Log("Logout", "Logout", "Users", _session.UserName, _session.UserId);
        _session.UserId = 0;
        _session.UserName = null;
        _session.Roles.Clear();
        _session.PermissionCache.Clear();
    }

    /// <summary>§v1.50.30: طوارئ المدير — نفس إجراء ملف reset_admin.flag لكن من زر في شاشة الدخول.</summary>
    public OpResult EmergencyUnlockAdmins()
    {
        // §R3 — الزر لا يعمل إلا بوجود ملف العلم reset_admin* في مجلد الإعدادات على هذا الجهاز
        // (نفس شرط مسار الإقلاع): تحقق فيزيائي مقصود حتى لا يعبث عابر من شاشة الدخول بكلمات المديرين.
        // لا نستخدم مجلد البرنامج كمسار احتياطي؛ العلم لا يُقرأ إلا من بيانات 1.50.74.
        string configDir = Infrastructure.Connection.AppConfig.ConfigDirectory;
        bool hasFlag = false;
        try { hasFlag = System.IO.Directory.Exists(configDir) && System.IO.Directory.GetFiles(configDir, "reset_admin*").Length > 0; } catch { }
        if (!hasFlag)
            return OpResult.Fail("طوارئ المديرين مقفلة.\nأنشئ ملفاً باسم reset_admin.flag داخل مجلد الإعدادات على هذا الجهاز ثم أعد المحاولة —\nيُسجَّل إجراء الطوارئ في سجل التدقيق كالمعتاد.");
        int n = AdminRecovery.ResetAdminAccounts(_db);
        if (n == 0) return OpResult.Fail("لا يوجد حساب مدير نشط لفك قفله.");
        _audit.Log("Login", "EmergencyUnlock", "Users", $"فك قفل {n} حساب مدير (طوارئ)", null);
        return OpResult.Success($"تم فك القفل وإعادة كلمة {n} من حسابات المديرين إلى {AdminRecovery.TempPassword} — سيلزم تغييرها عند أول دخول.");
    }
}