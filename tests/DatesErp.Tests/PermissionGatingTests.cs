using DatesErp.Application.Services;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §الإصلاح الأمني — حراسة بوابة الصلاحيات.
///
/// الثغرة المُصلَحة: كانت قائمة الوحدات مكرّرة يدوياً في ثلاثة مواضع مستقلة
/// (كتالوج شاشة الصلاحيات · بذور الأدوار · بوابة فتح الشاشات)، فسقطت
/// «products» و«cartons» و«employees» من الأخيرَين. النتيجة: أي مستخدم — ولو
/// «مشاهدة» — يفتح «طاقات الأصناف» و«الكرتون» و«الموظفون وأرقام الدخول»
/// رغم أن الخادم يفرض الصلاحية عليها.
///
/// هذه الاختبارات تحرس التطابق البنيوي بين المواضع الثلاثة، لا الحالة الراهنة فقط:
/// أي وحدة تُضاف مستقبلاً وتُنسى في أحدها ⟵ يسقط البناء.
/// </summary>
public class PermissionGatingTests
{
    private static DatesErpDbContext Db(TestHost host)
        => new(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);

    // ═══════════ 1) التطابق البنيوي — الحارس ضد عودة الثغرة ═══════════

    /// <summary>
    /// الوحدات الثلاث التي كانت الثغرة: يجب أن تبقى في المصدر الواحد إلى الأبد.
    /// </summary>
    [Theory]
    [InlineData("products")]   // طاقات الأصناف — صلاحية حساسة (تغيير الطاقة الإنتاجية)
    [InlineData("cartons")]    // الكرتون الفارغ — عدّ وبيع يؤثران على المخزون
    [InlineData("employees")]  // الموظفون وأرقام الدخول — كشف بيانات دخول
    public void Previously_Ungated_Modules_Are_Now_In_Catalog(string module)
    {
        Assert.Contains(module, PermissionModules.Codes);
        Assert.Contains(module, PermissionModules.ScreenGated);
    }

    /// <summary>
    /// كل وحدة في الكتالوج تُزرع فعلياً لكل دور — وإلا صارت صلاحية شكلية
    /// تُعرض في الشاشة ولا يقابلها صف في القاعدة.
    /// </summary>
    [Fact]
    public void Every_Catalog_Module_Is_Seeded_For_Every_Role()
    {
        using var host = new TestHost();
        using var db = Db(host);

        var roles = db.Roles.AsNoTracking().ToList();
        var seeded = db.RolePermissions.AsNoTracking().ToList();

        foreach (var role in roles)
        {
            var modulesForRole = seeded.Where(p => p.RoleId == role.Id)
                                       .Select(p => p.ModuleCode).ToHashSet();
            foreach (var code in PermissionModules.Codes)
                Assert.True(modulesForRole.Contains(code),
                    $"الوحدة «{code}» غير مزروعة للدور «{role.RoleCode}» — ستُفتح شاشتها بلا صلاحية فعلية.");
        }
    }

    /// <summary>
    /// كل مورد في النموذج الهرمي (مورد×عملية) مشتق من المصدر الواحد — لا زيادة ولا نقصان.
    /// </summary>
    [Fact]
    public void Resource_Catalog_Matches_Single_Source()
    {
        var catalog = PermissionService.ResourceCatalog.Select(r => r.Code).OrderBy(x => x).ToArray();
        var source = PermissionModules.Codes.OrderBy(x => x).ToArray();
        Assert.Equal(source, catalog);
    }

    /// <summary>
    /// كل مورد يُبذر في جدول الموارد فعلياً عند التهيئة.
    /// </summary>
    [Fact]
    public void Every_Module_Is_Persisted_As_Permission_Resource()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var db = Db(host);
        new PermissionService(db, host.Services.GetRequiredService<ICurrentSession>()).EnsureCatalog();

        var stored = db.PermissionResources.AsNoTracking().Select(r => r.Code).ToHashSet();
        foreach (var code in PermissionModules.Codes)
            Assert.True(stored.Contains(code), $"المورد «{code}» غير مسجّل في جدول الموارد.");
    }

    // ═══════════ 2) الفرض الفعلي في الخادم — لا تجاوز بالاستدعاء المباشر ═══════════

    /// <summary>
    /// §21 من الأمر: المستخدم بلا صلاحية يُرفض من الخادم مباشرة — لا من الواجهة فقط.
    /// يُستدعى هنا على مستوى الخدمة تماماً كما لو أُرسل طلب مباشر متجاوزاً الشاشة.
    /// </summary>
    [Fact]
    public void Server_Rejects_Sensitive_Operations_Without_Permission()
    {
        using var host = new TestHost();
        var session = host.Services.GetRequiredService<SessionContext>();

        // جلسة بلا أي صلاحية — تحاكي طلباً مباشراً من مستخدم غير مخوَّل
        session.UserId = 999;
        session.UserName = "intruder";
        session.PermissionCache.Clear();

        using var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var capacity = scope.ServiceProvider.GetRequiredService<ICapacityService>();

        // تغيير الطاقة الإنتاجية — كانت شاشته مفتوحة بلا بوابة (الثغرة)
        Assert.Throws<PermissionDeniedException>(() =>
            capacity.SetCapacity(1, 1, 1000));

        Assert.Throws<PermissionDeniedException>(() =>
            capacity.SaveHourlyRate(1, 500));

        // إدارة الموظفين وأرقام الدخول
        Assert.Throws<PermissionDeniedException>(() =>
            master.SaveEmployee(null, "X1", "دخيل", "—", "—", "—", true));

        Assert.Throws<PermissionDeniedException>(() =>
            master.DeleteEmployee(1));
    }

    /// <summary>مستخدم «عرض فقط» لا يستطيع التعديل ولو استدعى الخدمة مباشرة.</summary>
    [Fact]
    public void ViewOnly_Session_Cannot_Mutate_Even_Bypassing_Ui()
    {
        using var host = new TestHost();
        var session = host.Services.GetRequiredService<SessionContext>();
        session.UserId = 998;
        session.UserName = "viewer";
        session.PermissionCache.Clear();
        // عرض فقط على الأصناف — بلا تعديل
        session.PermissionCache[("products", "View")] = true;

        using var scope = host.Services.CreateScope();
        var capacity = scope.ServiceProvider.GetRequiredService<ICapacityService>();
        Assert.Throws<PermissionDeniedException>(() =>
            capacity.SetCapacity(1, 1, 5000));
    }

    // ═══════════ 3) الأدوار المطلوبة في §20 من الأمر ═══════════

    /// <summary>
    /// §20: مدير الإنتاج يملك صلاحيات الإنتاج ولا يملك إدارة النظام.
    /// </summary>
    [Fact]
    public void ProductionRole_Has_Production_But_Not_SystemAdmin()
    {
        using var host = new TestHost();
        var session = LoginAsRole(host, "production");

        Assert.True(session.Can("planning", "Create"));
        Assert.True(session.Can("planning", "Edit"));
        Assert.True(session.Can("planning", "Approve"));
        Assert.True(session.Can("production", "Edit"));
        Assert.True(session.Can("execution", "Edit"));

        // لا إدارة نظام ولا صلاحيات
        Assert.False(session.Can("users", "Edit"));
        Assert.False(session.Can("permissions", "ManagePermissions"));
        Assert.False(session.Can("backup", "Edit"));
    }

    /// <summary>§20: الجودة تملك الجودة فقط — ولا تعتمد خططاً ولا تسلّم.</summary>
    [Fact]
    public void QualityRole_Has_Quality_Only()
    {
        using var host = new TestHost();
        var session = LoginAsRole(host, "quality");

        Assert.True(session.Can("quality", "Approve"));
        Assert.True(session.Can("quality", "Create"));

        Assert.False(session.Can("planning", "Approve"));
        Assert.False(session.Can("delivery", "Create"));
        Assert.False(session.Can("users", "Edit"));
        Assert.False(session.Can("permissions", "ManagePermissions"));
    }

    /// <summary>§20: المخازن تملك المخازن والتسليم — ولا تعتمد خطة إنتاج.</summary>
    [Fact]
    public void WarehouseRole_Has_Inventory_And_Delivery_Only()
    {
        using var host = new TestHost();
        var session = LoginAsRole(host, "warehouse");

        Assert.True(session.Can("receiving", "Create"));
        Assert.True(session.Can("inventory", "Edit"));
        Assert.True(session.Can("delivery", "Approve"));
        // الكرتون الفارغ من عمل المخزن — كان بلا بوابة قبل الإصلاح
        Assert.True(session.Can("cartons", "Edit"));

        Assert.False(session.Can("planning", "Approve"));
        Assert.False(session.Can("permissions", "ManagePermissions"));
    }

    /// <summary>§20: مدير النظام يملك كل شيء — من الجداول لا من مفتاح سري.</summary>
    [Fact]
    public void AdminRole_Has_Every_Module_And_Operation()
    {
        using var host = new TestHost();
        var session = host.LoginAsAdmin();

        foreach (var code in PermissionModules.Codes)
            Assert.True(session.Can(code, "View"), $"المدير لا يملك عرض «{code}».");

        Assert.True(session.Can("products", "Edit"));
        Assert.True(session.Can("cartons", "Edit"));
        Assert.True(session.Can("employees", "View"));
    }

    /// <summary>
    /// مشكلة زر حفظ الخطة: Production هو دور التخطيط التشغيلي، وManagement هو دور
    /// الإدارة العاملة في التصميم الحالي؛ وEdit لا يساوي Approve.
    /// لا تُمنح Edit للجودة أو للمخازن، لذلك تبقى بوابة الشريط هي الحكم.
    /// </summary>
    [Fact]
    public void Planning_Edit_Is_Granted_To_Designated_Workflow_Roles_Only()
    {
        using var host = new TestHost();

        var production = host.LoginAs("production");
        Assert.True(production.Can("planning", "Edit"));
        Assert.True(production.Can("planning", "Approve"));

        var quality = host.LoginAs("quality");
        Assert.False(quality.Can("planning", "Edit"));
        Assert.False(quality.Can("planning", "Approve"));

        var warehouse = host.LoginAs("warehouse");
        Assert.False(warehouse.Can("planning", "Edit"));
        Assert.False(warehouse.Can("planning", "Approve"));

        using var db = Db(host);
        var managementRole = db.Roles.Single(r => r.RoleCode == "Management");
        var planning = db.PermissionResources.Single(r => r.Code == "planning");
        var edit = db.PermissionOperations.Single(o => o.Code == "Edit");
        Assert.True(db.RoleResourcePermissions.Any(x => x.RoleId == managementRole.Id
            && x.ResourceId == planning.Id && x.OperationId == edit.Id && x.IsAllowed));
    }

    /// <summary>
    /// يحاكي قاعدة قديمة فيها صف planning/Edit مرفوض في RoleResourcePermissions.
    /// الترحيل يصلح Production وManagement فقط، ويحترم Approve كعملية مستقلة.
    /// </summary>
    [Fact]
    public void Existing_Matrix_Is_Upgraded_For_Designated_Planning_Edit_Roles()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var db = Db(host);
        var svc = new PermissionService(db, host.Services.GetRequiredService<ICurrentSession>());

        var productionRole = db.Roles.Single(r => r.RoleCode == "Production");
        var managementRole = db.Roles.Single(r => r.RoleCode == "Management");
        var qualityRole = db.Roles.Single(r => r.RoleCode == "Quality");
        var planning = db.PermissionResources.Single(r => r.Code == "planning");
        var edit = db.PermissionOperations.Single(o => o.Code == "Edit");
        var approve = db.PermissionOperations.Single(o => o.Code == "Approve");
        var productionEdit = db.RoleResourcePermissions.Single(x => x.RoleId == productionRole.Id
            && x.ResourceId == planning.Id && x.OperationId == edit.Id);
        productionEdit.IsAllowed = false;
        var managementEdit = db.RoleResourcePermissions.Single(x => x.RoleId == managementRole.Id
            && x.ResourceId == planning.Id && x.OperationId == edit.Id);
        managementEdit.IsAllowed = false;
        var marker = db.SystemSettings.SingleOrDefault(x => x.SettingKey == "PermissionUpgrade_PlanningEdit_ProductionManagement");
        if (marker != null) db.SystemSettings.Remove(marker);
        db.SaveChanges();

        svc.EnsureCatalog();

        Assert.True(db.RoleResourcePermissions.Single(x => x.RoleId == productionRole.Id
            && x.ResourceId == planning.Id && x.OperationId == edit.Id).IsAllowed);
        Assert.True(db.RoleResourcePermissions.Single(x => x.RoleId == managementRole.Id
            && x.ResourceId == planning.Id && x.OperationId == edit.Id).IsAllowed);
        Assert.True(db.RoleResourcePermissions.Single(x => x.RoleId == productionRole.Id
            && x.ResourceId == planning.Id && x.OperationId == approve.Id).IsAllowed);
        Assert.False(db.RoleResourcePermissions.Single(x => x.RoleId == qualityRole.Id
            && x.ResourceId == planning.Id && x.OperationId == edit.Id).IsAllowed);
    }

    /// <summary>حتى مع تجاوز الواجهة، مستخدم بلا planning/Edit لا يستطيع تحديث خطة.</summary>
    [Fact]
    public void Planning_Update_Is_Server_Denied_Without_Edit_Permission()
    {
        using var host = new TestHost();
        var session = host.LoginAs("quality");
        Assert.False(session.Can("planning", "Edit"));

        var planning = host.Get<IPlanningService>();
        Assert.Throws<PermissionDeniedException>(() => planning.UpdatePlan(
            1, "خطة غير مصرح بها", "Daily", "2026-09-18", "2026-09-18", 1, 1,
            new List<PlanItemDto>()));
    }

    // ═══════════ 4) مسار الترقية — قاعدة قائمة لا تُقفل على مستخدميها ═══════════

    /// <summary>
    /// قاعدة مُرقّاة: الوحدات التي دخلت البوابة حديثاً لم يكن لها أي صف صلاحية.
    /// بعد الترحيل يجب أن يملك كل دور نشط «عرض» عليها — فلا ينكسر عمل قائم —
    /// دون منح تعديل أو حذف لأحد.
    /// </summary>
    [Fact]
    public void Upgrade_Backfill_Grants_View_Without_Granting_Mutation()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var db = Db(host);
        var svc = new PermissionService(db, host.Services.GetRequiredService<ICurrentSession>());
        svc.EnsureCatalog();

        // محاكاة قاعدة قديمة: نحذف كل صفوف الوحدات المستجدة لدور غير المدير
        var role = db.Roles.Single(r => r.RoleCode == "Quality");
        var newly = db.PermissionResources
            .Where(r => r.Code == "products" || r.Code == "cartons" || r.Code == "employees")
            .Select(r => r.Id).ToList();
        db.RoleResourcePermissions.RemoveRange(
            db.RoleResourcePermissions.Where(x => x.RoleId == role.Id && newly.Contains(x.ResourceId)));
        db.SaveChanges();

        svc.BackfillNewlyGatedModules();

        var set = svc.GetRoleSet(role.Id);
        Assert.Contains(("products", "View"), set);
        Assert.Contains(("cartons", "View"), set);
        Assert.Contains(("employees", "View"), set);
        // الترحيل يمنح العرض فقط — لا تعديل ولا حذف
        Assert.DoesNotContain(("cartons", "Delete"), set);
        Assert.DoesNotContain(("employees", "Edit"), set);
    }

    /// <summary>الترحيل لا يلمس ضبطاً سابقاً للإدارة — ولا يعيد منح ما سُحب عمداً.</summary>
    [Fact]
    public void Upgrade_Backfill_Does_Not_Override_Explicit_Deny()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var db = Db(host);
        var svc = new PermissionService(db, host.Services.GetRequiredService<ICurrentSession>());
        svc.EnsureCatalog();

        var role = db.Roles.Single(r => r.RoleCode == "Sales");
        svc.SetRolePermission(role.Id, "cartons", "View", false); // سحب صريح

        svc.BackfillNewlyGatedModules();

        Assert.DoesNotContain(("cartons", "View"), svc.GetRoleSet(role.Id));
    }

    // ═══════════ 5) §7 — تمييز الموروث عن الاستثناء ═══════════

    /// <summary>
    /// §7: المستخدم قد يكون «موروثاً مسموحاً» ثم يُمنع صراحةً. الشاشة كانت تعرض مربعاً واحداً
    /// فيستحيل تمييز «ممنوع صراحةً» عن «غير موروث». هنا نثبت أن الطبقة تفصل المصدرين.
    /// </summary>
    [Fact]
    public void Inherited_And_Exception_Are_Reported_Separately()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var db = Db(host);
        var svc = new PermissionService(db, host.Services.GetRequiredService<ICurrentSession>());
        svc.EnsureCatalog();

        var user = db.Users.Single(u => u.UserName == "quality");
        var roleIds = db.UserRoles.Where(ur => ur.UserId == user.Id && ur.IsActive).Select(ur => ur.RoleId).ToList();

        var inherited = svc.GetInheritedSet(roleIds);
        Assert.Contains(("quality", "View"), inherited);       // موروث من دوره
        Assert.Empty(svc.GetUserExceptions(user.Id));           // ولا استثناء بعد

        // منع صريح رغم الوراثة
        svc.SetUserPermission(user.Id, "quality", "View", false);
        var exc = svc.GetUserExceptions(user.Id);
        Assert.False(exc[("quality", "View")]);                 // الاستثناء = منع
        Assert.Contains(("quality", "View"), svc.GetInheritedSet(roleIds)); // الوراثة لم تتغير
        Assert.False(svc.BuildEffectiveCache(user.Id, roleIds)[("quality", "View")]); // المحصلة: ممنوع
    }

    /// <summary>
    /// §7: إلغاء الاستثناء يعيد المستخدم إلى وراثة دوره تماماً — عملية كانت مستحيلة
    /// بالمربع المسطّح (كل إلغاء تحديد كان يُكتب «منعاً صريحاً» دائماً).
    /// </summary>
    [Fact]
    public void Clearing_Exception_Restores_Role_Inheritance()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var db = Db(host);
        var svc = new PermissionService(db, host.Services.GetRequiredService<ICurrentSession>());
        svc.EnsureCatalog();

        var user = db.Users.Single(u => u.UserName == "quality");
        var roleIds = db.UserRoles.Where(ur => ur.UserId == user.Id && ur.IsActive).Select(ur => ur.RoleId).ToList();

        svc.SetUserPermission(user.Id, "quality", "View", false);
        Assert.False(svc.BuildEffectiveCache(user.Id, roleIds)[("quality", "View")]);

        svc.ClearUserPermission(user.Id, "quality", "View");

        Assert.Empty(svc.GetUserExceptions(user.Id));
        Assert.True(svc.BuildEffectiveCache(user.Id, roleIds)[("quality", "View")]); // عاد للوراثة
    }

    /// <summary>إلغاء استثناء غير موجود لا يرمي ولا يكتب شيئاً (idempotent).</summary>
    [Fact]
    public void Clearing_Missing_Exception_Is_NoOp()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var db = Db(host);
        var svc = new PermissionService(db, host.Services.GetRequiredService<ICurrentSession>());
        svc.EnsureCatalog();
        var user = db.Users.Single(u => u.UserName == "quality");

        svc.ClearUserPermission(user.Id, "quality", "View");

        Assert.Empty(svc.GetUserExceptions(user.Id));
    }

    private static SessionContext LoginAsRole(TestHost host, string userName)
    {
        var session = host.Services.GetRequiredService<SessionContext>();
        var auth = host.Services.GetRequiredService<IAuthService>();
        var r = auth.Login(userName, DbSeeder.InitialAdminPassword);
        Assert.True(r.Success, $"فشل الدخول بـ {userName}: {r.Message}");
        return session;
    }
}
