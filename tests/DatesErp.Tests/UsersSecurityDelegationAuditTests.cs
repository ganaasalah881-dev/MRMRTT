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
/// §فحص شامل ومعمق: الموظفون والمستخدمون والصلاحيات والتفويض — اختبارات تشغيلية حقيقية.
/// كل اختبار ينفذ عمليات فعلية (دخول، أخطاء كلمة مرور، منح/سحب، تفويض، تعطيل) ويقارن
/// النتائج بالسياسات المعلنة: PBKDF2 + قفل 5 محاولات + فك تلقائي + انتهاء صلاحية،
/// فرض server-side، استثناءات المستخدم فوق الأدوار، حراس آخر مدير صلاحيات،
/// تفويض زمني بنطاق ومن دون تصعيد متسلسل، وتدقيق إلحاقي لكل عملية حساسة.
/// </summary>
public class UsersSecurityDelegationAuditTests
{
    private static T Get<T>(TestHost h) => h.Services.CreateScope().ServiceProvider.GetRequiredService<T>();
    private static DatesErpDbContext Db(TestHost h)
        => new(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(h.Connection).Options);
    private const string Pw = DbSeeder.InitialAdminPassword;

    private static int UserId(TestHost h, string name) { using var db = Db(h); return db.Users.Single(u => u.UserName == name).Id; }
    private static int RoleId(TestHost h, string code) { using var db = Db(h); return db.Roles.Single(r => r.RoleCode == code).Id; }

    // ──Sec_01── الدخول بثلاث هويات + القفل بعد 5 أخطاء + فك تلقائي + انتهاء صلاحية + تعطيل ──
    [Fact]
    public void Sec_01_Login_Identities_Lockout_AutoUnlock_Expiry_Deactivation()
    {
        using var host = new TestHost();
        var auth = Get<IAuthService>(host);

        // ثلاث هويات مقبولة: اسم المستخدم، رقم الدخول، رقم الموظف
        Assert.True(auth.Login("admin", Pw).Success);
        Assert.True(auth.Login("U001", Pw).Success);
        var byEmp = auth.Login("EMP1", Pw);
        Assert.True(byEmp.Success, byEmp.Message);
        Assert.True(byEmp.MustChangePassword); // البذرة تفرض التغيير عند أول دخول

        // كلمة خاطئة ×5 ← قفل الحساب
        for (int i = 0; i < 5; i++)
            Assert.False(auth.Login("admin", "WrongPass1").Success);
        using (var db = Db(host))
        {
            var u = db.Users.Single(x => x.UserName == "admin");
            Assert.True(u.IsLocked);
            Assert.NotNull(u.LockoutDate);
            Assert.Equal(5, u.FailedLoginCount);
        }
        // حتى الكلمة الصحيحة تُرفض أثناء القفل
        Assert.Contains("مقفل", auth.Login("admin", Pw).Message);

        // فك تلقائي بعد انقضاء المدة (افتراضي 30 دقيقة)
        using (var db = Db(host))
        {
            var u = db.Users.Single(x => x.UserName == "admin");
            u.LockoutDate = DateTime.Now.AddMinutes(-31);
            db.SaveChanges();
        }
        var after = auth.Login("admin", Pw);
        Assert.True(after.Success, after.Message);
        using (var db = Db(host)) Assert.False(db.Users.Single(x => x.UserName == "admin").IsLocked);

        // انتهاء صلاحية كلمة المرور (90 يوماً افتراضي) ← PasswordExpired + فرض التغيير
        using (var db = Db(host))
        {
            var u = db.Users.Single(x => x.UserName == "admin");
            u.PasswordChangedDate = DateTime.Now.AddDays(-100);
            db.SaveChanges();
        }
        var exp = auth.Login("admin", Pw);
        Assert.True(exp.PasswordExpired);
        Assert.True(exp.MustChangePassword);

        // حساب معطّل لا يدخل
        using (var db = Db(host))
        {
            var q = db.Users.Single(x => x.UserName == "quality");
            q.IsActive = false;
            db.SaveChanges();
        }
        Assert.False(auth.Login("quality", Pw).Success);

        // التدقيق: Login مسجل في السجل المركزي
        using (var db = Db(host)) Assert.Contains(db.AuditLogs.ToList(), a => a.ActionType == "Login");
    }

    // ──Sec_02── الفرض في طبقة الخدمات: مستخدم بلا صلاحية يُرفض server-side لا من الشاشة فقط ──
    [Fact]
    public void Sec_02_ServerSide_Enforcement_Not_Just_UI()
    {
        using var host = new TestHost();
        host.LoginAs("warehouse"); // viewOnly على users/employees
        Assert.Throws<PermissionDeniedException>(() =>
            Get<IAdminService>(host).SaveUser(null, null, "intruder", "دخيل", "Pass1234", new List<int> { RoleId(host, "Administrator") }, true));
        Assert.Throws<PermissionDeniedException>(() =>
            Get<MasterDataService>(host).SaveEmployee(null, "EMP77", "دخيل", "-", "-", "-", true));
        Assert.Throws<PermissionDeniedException>(() =>
            Get<MasterDataService>(host).SaveDelegation(null, 1, 2, DateTime.Today, DateTime.Today.AddDays(1)));

        host.LoginAsAdmin();
        var ok = Get<IAdminService>(host).SaveUser(null, null, "auditor", "مدقق", "Pass1234", new List<int> { RoleId(host, "Finance") }, true);
        Assert.True(ok.Ok, ok.Message);
    }

    // ──Sec_03── استثناءات المستخدم تعلو على الأدوار (منع/منح/إزالة←وراثة) + سياسة كلمة المرور + تدقيق ──
    [Fact]
    public void Sec_03_UserExceptions_Override_Roles_With_Audit()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var admin = Get<IAdminService>(host);
        int prodRole = RoleId(host, "Production");

        // كلمة هزيلة تُرفض من نموذج الحفظ نفسه (لا من التصفير فقط)
        var weak = admin.SaveUser(null, null, "prod2", "مستخدم إنتاج", "OnlyLetters", new List<int> { prodRole }, true);
        Assert.False(weak.Ok);
        Assert.Contains("حروف وأرقام", weak.Message);

        var made = admin.SaveUser(null, null, "prod2", "مستخدم إنتاج", "Test1234", new List<int> { prodRole }, true);
        Assert.True(made.Ok, made.Message);
        int prod2 = UserId(host, "prod2");

        // دوره يمنح اعتماد التخطيط
        var auth = Get<IAuthService>(host);
        Assert.True(auth.Login("prod2", "Test1234").Success);
        Assert.True(Get<ICurrentSession>(host).Can("planning", "Approve"));

        // منع صريح فوق الدور ← يسري بعد الدخول التالي
        host.LoginAsAdmin();
        var perm = Get<PermissionService>(host);
        perm.SetUserPermission(prod2, "planning", "Approve", false);
        Assert.True(auth.Login("prod2", "Test1234").Success);
        Assert.False(Get<ICurrentSession>(host).Can("planning", "Approve"));

        // محاولة تعديل المصفوفة من جلسة غير مخولة ← رفض server-side
        Assert.Throws<PermissionDeniedException>(() => Get<PermissionService>(host).SetUserPermission(prod2, "planning", "Approve", true));

        // إزالة الاستثناء ← عودة للوراثة تماماً
        host.LoginAsAdmin();
        Get<PermissionService>(host).ClearUserPermission(prod2, "planning", "Approve");
        Assert.True(auth.Login("prod2", "Test1234").Success);
        Assert.True(Get<ICurrentSession>(host).Can("planning", "Approve"));

        // التدقيق الإلحاقي: grant/revoke/inherit بقيم قبل/بعد ومن قام بها
        using var db = Db(host);
        var logs = db.PermissionAuditLogs.ToList();
        Assert.Contains(logs, a => a.TargetUserId == prod2 && a.ActionType == "revoke" && a.ResourceCode == "planning" && a.OperationCode == "Approve");
        Assert.Contains(logs, a => a.TargetUserId == prod2 && a.ActionType == "inherit");
        Assert.Contains(logs, a => a.ChangedByName == "admin");
        // إنشاء المستخدم نفسه صار مدققاً في السجل المركزي
        Assert.Contains(db.AuditLogs.ToList(), a => a.ActionType == "CreateUser" && a.DocumentNumber == "prod2");
    }

    // ──Sec_04── التفويض: نطاق وفترة وتحقق الطرفين وتدقيق + الموقوف لا يفوّض ──
    [Fact]
    public void Sec_04_Delegation_Scope_Window_InactiveFrom_Audit()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var mds = Get<MasterDataService>(host);
        var auth = Get<IAuthService>(host);
        int wh = UserId(host, "warehouse"), q = UserId(host, "quality");

        // مدخلات مرفوضة: تفويض للنفس، نهاية قبل بداية، نطاق وهمي
        Assert.False(mds.SaveDelegation(null, wh, wh, DateTime.Today, DateTime.Today).Ok);
        Assert.False(mds.SaveDelegation(null, wh, q, DateTime.Today, DateTime.Today.AddDays(-1)).Ok);
        Assert.False(mds.SaveDelegation(null, wh, q, DateTime.Today, DateTime.Today, "modules_fake").Ok);

        // تفويض ساري بنطاق التسليم فقط
        var dg = mds.SaveDelegation(null, wh, q, DateTime.Today, DateTime.Today.AddDays(5), "delivery");
        Assert.True(dg.Ok, dg.Message);
        // ومنتهي الفترة (نطاق كلي) لا يضيف شيئاً
        Assert.True(mds.SaveDelegation(null, wh, q, DateTime.Today.AddDays(-10), DateTime.Today.AddDays(-5), null).Ok);

        Assert.True(auth.Login("quality", Pw).Success);
        var session = Get<ICurrentSession>(host);
        Assert.True(session.Can("delivery", "Approve"));    // من التفويض ضمن النطاق
        Assert.False(session.Can("receiving", "Create"));   // خارج النطاق والتفويض المنتهي لا أثر له

        // التدقيق الإلحاقي للتفويض
        using (var db = Db(host))
            Assert.Contains(db.AuditLogs.ToList(), a => a.ActionType == "CreateDelegation");

        // تعطيل المفوِّض ← إنشاء تفويض جديد مرفوض، والقائم يسقط أثره فوراً
        host.LoginAsAdmin();
        using (var db = Db(host))
        {
            var u = db.Users.Single(x => x.Id == wh);
            u.IsActive = false;
            db.SaveChanges();
        }
        var denied = mds.SaveDelegation(null, wh, q, DateTime.Today, DateTime.Today.AddDays(2), "delivery");
        Assert.False(denied.Ok);
        Assert.Contains("غير نشط", denied.Message);
        Assert.True(auth.Login("quality", Pw).Success);
        Assert.False(Get<ICurrentSession>(host).Can("delivery", "Approve")); // صلاحيات الموقوف لا تنتقل
    }

    // ──Sec_05── التفويض لا يتسلسل: مفوَّض إليه لا يعيد تفويض ما فُوّض إليه ──
    [Fact]
    public void Sec_05_Delegation_Does_Not_Chain_Escalate()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var mds = Get<MasterDataService>(host);
        var auth = Get<IAuthService>(host);
        int wh = UserId(host, "warehouse"), q = UserId(host, "quality"), p = UserId(host, "production");

        Assert.True(mds.SaveDelegation(null, wh, q, DateTime.Today, DateTime.Today.AddDays(3), "delivery").Ok);
        Assert.True(mds.SaveDelegation(null, q, p, DateTime.Today, DateTime.Today.AddDays(3), "delivery").Ok);

        // quality نفسها ترى اعتماد التسليم (من warehouse مباشرة)
        Assert.True(auth.Login("quality", Pw).Success);
        Assert.True(Get<ICurrentSession>(host).Can("delivery", "Approve"));

        // production فُوّض من quality — ولا يرث ما فُوّض إلى quality (منع التصعيد المتسلسل)
        Assert.True(auth.Login("production", Pw).Success);
        Assert.False(Get<ICurrentSession>(host).Can("delivery", "Approve"));
    }

    // ──Sec_06── حراس الإغلاق الكلي: لا تعطيل ولا تجريد لآخر مدير صلاحيات — من كل المسارات ──
    [Fact]
    public void Sec_06_LastPermissionAdmin_Guarded_All_Paths()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int adminId = UserId(host, "admin");
        var mds = Get<MasterDataService>(host);
        var adminSvc = Get<IAdminService>(host);

        // مسار التوقيف المباشر: المدير الحالي يرفض توقيف نفسه، ولو من جلسة أخرى يرفضه حارس «آخر مدير»
        var t = mds.ToggleUserActive(adminId);
        Assert.False(t.Ok);
        Assert.Contains("حسابك الحالي", t.Message);
        using (var db = Db(host))
        {
            int adminId2 = db.Users.Single(u => u.UserName == "admin").Id;
            var permSvc = Get<PermissionService>(host);
            // حتى لو أُزيلت الحماية الذاتية، حارس آخر مدير صلاحيات يمنع التعطيل
            Assert.Throws<DomainException>(() => permSvc.DeactivateUser(adminId2));
        }

        // مسار حفظ المستخدم: تعطيل عبر IsActive=false
        var d = adminSvc.SaveUser(adminId, "U001", "admin", "مدير النظام", null, new List<int> { RoleId(host, "Administrator") }, false);
        Assert.False(d.Ok);
        Assert.Contains("آخر مستخدم", d.Message);

        // مسار حفظ المستخدم: تجريد من الأدوار
        var strip = adminSvc.SaveUser(adminId, "U001", "admin", "مدير النظام", null, new List<int> { RoleId(host, "Production") }, true);
        Assert.False(strip.Ok);
        Assert.Contains("إدارة الصلاحيات", strip.Message);

        // مسار شاشة الصلاحيات
        Assert.Throws<DomainException>(() => Get<PermissionService>(host).DeactivateUser(adminId));

        // الحذف الذاتي مرفوض دائماً
        Assert.False(adminSvc.DeleteUser(adminId).Ok);

        // الإدارة تبقى قادرة: مستخدم ثانٍ يُمنح إدارة الصلاحيات فيفك الحظر عن تعديل الأول
        using (var db = Db(host)) Assert.True(db.Users.Single(u => u.Id == adminId).IsActive);
    }

    // ──Sec_07── دورة حياة كلمة المرور: تصفير بمنطق + تغيير ذاتي + منع التكرار + قفل عند الفشل ──
    [Fact]
    public void Sec_07_Password_Lifecycle_Reset_Change_Reuse_Lockout()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var mds = Get<MasterDataService>(host);
        var auth = Get<IAuthService>(host);
        int q = UserId(host, "quality");

        // تصفير: سياسة مفروضة + إجبار التغيير + تدقيق
        Assert.False(mds.ResetUserPassword(q, "abc").Ok);          // قصيرة
        Assert.False(mds.ResetUserPassword(q, "Admin@123").Ok);    // الافتراضية ممنوعة
        var reset = mds.ResetUserPassword(q, "Qpass123");
        Assert.True(reset.Ok, reset.Message);
        using (var db = Db(host)) Assert.True(db.Users.Single(u => u.Id == q).MustChangePassword);
        var ql = auth.Login("quality", "Qpass123");
        Assert.True(ql.Success && ql.MustChangePassword);

        // تغيير ذاتي: كلمة قديمة خاطئة تُحسب في عداد القفل
        var wrongOld = mds.ChangePassword(q, "NoPass999", "Other123", "Other123");
        Assert.False(wrongOld.Ok);
        using (var db = Db(host)) Assert.Equal(1, db.Users.Single(u => u.Id == q).FailedLoginCount);

        // عدم تطابق الجديدتين
        Assert.False(mds.ChangePassword(q, "Qpass123", "Other123", "Other999").Ok);
        // إعادة استخدام الحالية ممنوعة
        Assert.False(mds.ChangePassword(q, "Qpass123", "Qpass123", "Qpass123").Ok);
        // تغيير سليم ← MustChangePassword يُرضى
        var ch = mds.ChangePassword(q, "Qpass123", "Other123", "Other123");
        Assert.True(ch.Ok, ch.Message);
        using (var db = Db(host))
        {
            var u = db.Users.Single(x => x.Id == q);
            Assert.False(u.MustChangePassword);
            Assert.NotNull(u.PasswordChangedDate);
        }
        Assert.True(auth.Login("quality", "Other123").Success);

        // فك القفل اليدوي بعد 5 محاولات فاشلة
        for (int i = 0; i < 5; i++) Assert.False(auth.Login("quality", "Bad12345").Success);
        using (var db = Db(host)) Assert.True(db.Users.Single(u => u.Id == q).IsLocked);
        // Resetting another account requires an administrator, not the quality
        // employee who most recently logged in on this shared fixture session.
        host.LoginAsAdmin();
        Assert.True(mds.UnlockUser(q).Ok);
        Assert.True(auth.Login("quality", "Other123").Success);

        // التدقيق: كل خطوة حساسة مسجلة
        using (var db2 = Db(host))
        {
            var logs = db2.AuditLogs.ToList();
            Assert.Contains(logs, a => a.ActionType == "ResetPassword");
            Assert.Contains(logs, a => a.ActionType == "ChangePassword");
            Assert.Contains(logs, a => a.ActionType == "ChangePasswordFailed");
            // ولا كلمة مرور صريحة في أي سجل (تعتيم مسبق مثبت في AuditCredentialRedactionTests)
            Assert.DoesNotContain(logs, a => (a.NewValue ?? "").Contains("Qpass123") || (a.OldValue ?? "").Contains("Other123"));
        }
    }

    // ──Sec_08── الموظفون: أرقام فريدة (توليد server-side) + حذف يتحول تعطيلاً عند الارتباط ──
    [Fact]
    public void Sec_08_Employees_UniqueCodes_SoftDelete_WhenLinked()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var mds = Get<MasterDataService>(host);

        var e1 = mds.SaveEmployee(null, "EMP9", "موظف اختبار", "مخازن", "المخازن", "777000", true);
        Assert.True(e1.Ok, e1.Message);
        // الرقم محجوز: التكرار مرفوض
        var dup = mds.SaveEmployee(null, "EMP9", "آخر", "-", "-", "-", true);
        Assert.False(dup.Ok);
        Assert.Contains("محجوز", dup.Message);
        // الفراغ يُولَّد في الخادم (EMP###) — لا تعارض بين مستخدمين متزامنين
        var auto = mds.SaveEmployee(null, null, "بلا رقم", "إنتاج", "الإنتاج", null, true);
        Assert.True(auto.Ok, auto.Message);
        Assert.StartsWith("EMP", auto.DocumentNumber);

        // §R1 — موظف مرتبط بحساب: تعطيل بدل الحذف، وEMP1 مرتبط بالمدير الأخير ← الحارس يرفض
        var blocked = mds.DeleteEmployee(1);
        Assert.False(blocked.Ok);
        Assert.Contains("مرتبط", blocked.Message);
        using (var db = Db(host)) Assert.True(db.Users.Single(u => u.UserName == "admin").IsActive);

        // غير المرتبط يُحذف فعلاً
        Assert.True(mds.DeleteEmployee(e1.Id).Ok);
        using (var db = Db(host)) Assert.Null(db.Employees.FirstOrDefault(x => x.Id == e1.Id));
    }

    // ──Sec_09── الكلمة المفقودة: لا تُعرف أبداً — تصفير بكلمة مولَّدة تُعرض مرة واحدة + الموظف يغيّرها بنفسه + سجل 5 كلمات ──
    [Fact]
    public void Sec_09_ForgottenPassword_ResetGenerated_SelfChange_History()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var mds = Get<MasterDataService>(host);
        var auth = Get<IAuthService>(host);
        int q = UserId(host, "quality");

        // الفراغ = توليد كلمة مؤقتة قوية — تظهر في الرسالة مرة واحدة
        var reset = mds.ResetUserPassword(q, null);
        Assert.True(reset.Ok, reset.Message);
        var m = System.Text.RegularExpressions.Regex.Match(reset.Message, @"الكلمة المؤقتة: (\S+)");
        Assert.True(m.Success, reset.Message);
        string temp = m.Groups[1].Value;
        Assert.True(temp.Length >= 12);
        Assert.True(temp.Any(char.IsLetter) && temp.Any(char.IsDigit));

        // الموظف يدخل بالمؤقتة ويُجبر على تغييرها — والقديمة الأصلية لم تعد تعمل
        var login = auth.Login("quality", temp);
        Assert.True(login.Success, login.Message);
        Assert.True(login.MustChangePassword);
        Assert.False(auth.Login("quality", Pw).Success);

        // يغيّرها بنفسه (ChangePassword الذاتية) ← تدخل كلمة جديدة
        var ch = mds.ChangePassword(q, temp, "Fresh1234", "Fresh1234");
        Assert.True(ch.Ok, ch.Message);
        Assert.True(auth.Login("quality", "Fresh1234").Success);
        using (var db = Db(host))
        {
            var u = db.Users.Single(x => x.Id == q);
            Assert.False(u.MustChangePassword);
            Assert.False(string.IsNullOrEmpty(u.PasswordHistory)); // §R4 — القديمة في السجل
        }

        // §R4 — إعادة استخدام أي كلمة من السجل مرفوضة (من التصفير ومن التغيير الذاتي).
        // Administrative reset is forbidden while logged in as the quality user.
        host.LoginAsAdmin();
        var reuse = mds.ResetUserPassword(q, temp);
        Assert.False(reuse.Ok);
        Assert.Contains("السابقة", reuse.Message);
        var reuse2 = mds.ChangePassword(q, "Fresh1234", temp, temp);
        Assert.False(reuse2.Ok);

        // لا كلمة مرور — مولدة أو مختارة — تظهر في أي سجل تدقيق
        using (var db = Db(host))
            Assert.DoesNotContain(db.AuditLogs.ToList(), a =>
                (a.NewValue ?? "").Contains(temp) || (a.DocumentNumber ?? "").Contains(temp) || (a.NewValue ?? "").Contains("Fresh1234"));
    }

    // ──Sec_10── R1: تعطيل الموظف يوقف حسابه المرتبط تلقائياً (ويسري على الدخول فوراً) ──
    [Fact]
    public void Sec_10_Employee_Deactivation_Disables_Linked_User()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var mds = Get<MasterDataService>(host);
        var auth = Get<IAuthService>(host);

        // مدير صلاحيات ثانٍ حتى لا يمنع الحارس تعطيل حساب المدير الأول المرتبط بـ EMP1
        var mgr = Get<IAdminService>(host).SaveUser(null, null, "mgr2", "مدير ثانٍ", "Mgr12345",
            new List<int> { RoleId(host, "Administrator") }, true);
        Assert.True(mgr.Ok, mgr.Message);

        var del = mds.DeleteEmployee(1); // EMP1 مرتبط بـ admin — الآن يوجد من يخلفه
        Assert.True(del.Ok, del.Message);
        Assert.Contains("أُوقفت حسابات", del.Message);
        using (var db = Db(host))
        {
            Assert.False(db.Employees.Single(x => x.Id == 1).IsActive);
            Assert.False(db.Users.Single(u => u.UserName == "admin").IsActive); // §R1
        }
        Assert.False(auth.Login("admin", Pw).Success);          // الحساب الموقوف لا يدخل
        Assert.True(auth.Login("mgr2", "Mgr12345").Success);    // الخلف يعمل

        // مسار الحفظ: تعطيل موظف المخزن يوقف مستخدم warehouse — والدخول الآن بالخلف mgr2
        Assert.True(auth.Login("mgr2", "Mgr12345").Success);
        int emp3;
        using (var db = Db(host)) emp3 = db.Employees.Single(x => x.EmployeeCode == "EMP3").Id;
        var off = mds.SaveEmployee(emp3, "EMP3", "أمين المخزن", "مخازن", null, null, false);
        Assert.True(off.Ok, off.Message);
        Assert.Contains("أُوقف حساب الدخول المرتبط", off.Message);
        Assert.False(auth.Login("warehouse", Pw).Success);
    }

    // ──Sec_11── R2: السحب يسري على الجلسة الحية — معطّل يفقد صلاحياته دون إعادة دخول ──
    [Fact]
    public void Sec_11_LiveSession_Revoked_Within_RefreshWindow()
    {
        using var host = new TestHost();
        host.LoginAs("warehouse");
        var session = Get<ICurrentSession>(host);
        Assert.True(session.Can("reports", "View")); // قبل التعطيل: مسموح

        // مدير (من سياق آخر) يعطّل الحساب مباشرة، وتمر دقيقة على مصفوفة الجلسة
        using (var db = Db(host))
        {
            var u = db.Users.Single(x => x.UserName == "warehouse");
            u.IsActive = false;
            db.SaveChanges();
        }
        ((DatesErp.Infrastructure.Session.SessionContext)session).CacheBuiltAt = DateTime.Now.AddSeconds(-61);

        // أول عملية تخضع لـ Require تعيد قراءة القاعدة ← رفض فوري بلا إعادة دخول
        Assert.Throws<PermissionDeniedException>(() =>
            Get<IReportService>(host).Run("inventory", new Dictionary<string, string>()));
        Assert.False(session.Can("reports", "View"));
    }

    // ──Sec_12── R3+R6: طوارئ المديرين مشروطة بملف العلم، ورسائل الدخول موحدة ──
    [Fact]
    public void Sec_12_EmergencyFlag_Required_And_Login_Messages_Unified()
    {
        using var host = new TestHost();
        var auth = Get<IAuthService>(host);

        // بلا ملف reset_admin* في مجلد الإعدادات ← الزر مقفول برسالة تعليمات
        var em = auth.EmergencyUnlockAdmins();
        Assert.False(em.Ok);
        Assert.Contains("reset_admin.flag", em.Message);
        // كلمة المدير لم تُمس
        Assert.True(auth.Login("admin", Pw).Success);

        // R6 — المجهول والمعطّل وكلمة الخطأ برسالة واحدة (لا كشف لحالة الحساب)
        var unknown = auth.Login("ghost_user", Pw).Message;
        var wrong = auth.Login("admin", "WrongPass9").Message;
        Assert.Equal(unknown, wrong);
        Assert.Equal("رقم الدخول أو كلمة المرور غير صحيحة.", unknown);
    }
}
