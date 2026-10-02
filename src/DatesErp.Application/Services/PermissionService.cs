using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Core.Exceptions;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §النظام الهرمي للصلاحيات: مورد (شاشة فعلية) × عملية (فعل) × حالة (تُفحص في طبقة الخدمات)،
/// مع استثناءات مستخدم فوق صلاحيات الأدوار، وسجل تدقيق إلحاقي، ونسخ/مقارنة، ومنع تعطيل آخر مدير صلاحيات.
/// البيانات الفعلية للشاشات هي الأساس — لا أسماء وهمية.
/// </summary>
public class PermissionService
{
    private readonly DatesErpDbContext _db;
    private readonly ICurrentSession _session;

    public PermissionService(DatesErpDbContext db, ICurrentSession session)
    { _db = db; _session = session; }

    // ═══ الكتالوج الفعلي: الموارد = أكواد الوحدات المستخدمة في Require عبر النظام ═══
    // §الإصلاح الأمني: صار مشتقاً من PermissionModules.All (المصدر الواحد في Core) بدل
    // نسخة يدوية ثالثة. كان «products/cartons/employees» مُعرَّفة هنا أو مُطبَّقة في الخادم
    // بينما تسقط من البذور وبوابة الشاشات — فتُفتح الشاشة بلا فحص. الآن مستحيل بنيوياً.
    public static readonly (string Code, string NameAr, string GroupAr)[] ResourceCatalog =
        PermissionModules.All;

    public static readonly (string Code, string NameAr, bool Sensitive)[] OperationCatalog =
    {
        ("View", "عرض", false),
        // §3 — «عرض الكل» عملية إشرافية منفصلة عن «عرض»: من يرى مهامه لا يرى مهام الجميع.
        // لولا فصلها لكان منح «عرض» يمنح الإشراف الكامل بلا قصد.
        ("ViewAll", "عرض الكل (إشراف)", false),
        ("Create", "إضافة", false),
        ("Edit", "تعديل", false),
        ("Delete", "حذف", true),
        ("Approve", "اعتماد", true),
        ("Post", "ترحيل", false),
        ("Print", "طباعة", false),
        ("Export", "تصدير", false),
        ("Cancel", "إلغاء/عكس", true),
        ("Reopen", "إعادة فتح مستند معتمد", true),
        ("EditAfterApproval", "تعديل بعد الاعتماد", true),
        ("BypassInspection", "تجاوز الفحص", true),
        ("ManagePermissions", "إدارة الصلاحيات", true),
        // §C1/C2 — تصحيح المستندات المعتمدة: عمليات حساسة لا تُمنح افتراضياً لأي دور؛
        // من يملكها يظهر له الزران في الاستلام، ومن لا يملك لا يراهما أصلاً.
        ("ChainCorrection", "تصحيح السلسلة (فك مترابط موثق)", true),
        ("CorrectApprovedQty", "تصحيح كمية/عميل مستند معتمد", true)
    };

    // §المعالجة والتعقيم — تعيين العمليات العامة على مورد «treatment»:
    //   View    = عرض المعالجات والتقارير
    //   Create  = بدء معالجة (نقل الكمية إلى مستودع المعالجة)
    //   Approve = الإفراج بعد اكتمال المدة (كلي أو جزئي)
    //   Cancel  = الرفض أو إلغاء بدء خاطئ
    // لم تُضف عمليات خاصة (Start/Release/Reject) عمداً: مصفوفة العمليات مشتركة بين
    // كل الموارد، فإضافة ثلاث عمليات لمورد واحد تضخّم شاشة الصلاحيات لأربعة وعشرين
    // مورداً بأعمدة لا معنى لها لأغلبها.

    // ═══ التهيئة: بذر الكتالوج + ترحيل صلاحيات الأدوار القديمة إلى النموذج الجديد ═══
    public void EnsureCatalog()
    {
        // §B84/S1: إكمال الكتالوج أولاً — القواعد القائمة لا ترى الموارد/العمليات المضافة لاحقاً
        // (البذر الأصلي يعمل فقط عندما تكون الجداول فارغة).
        EnsureCatalogUpsert();

        if (!_db.PermissionResources.Any())
        {
            int s = 1;
            foreach (var (code, name, group) in ResourceCatalog)
                _db.PermissionResources.Add(new PermissionResource { Code = code, NameAr = name, GroupAr = group, SortNo = s++ });
            s = 1;
            foreach (var (code, name, sens) in OperationCatalog)
                _db.PermissionOperations.Add(new PermissionOperation { Code = code, NameAr = name, IsSensitive = sens, SortNo = s++ });
            _db.SaveChanges();
        }

        if (!_db.RoleResourcePermissions.Any())
        {
            var resByCode = _db.PermissionResources.ToDictionary(r => r.Code, r => r.Id);
            var opByCode = _db.PermissionOperations.ToDictionary(o => o.Code, o => o.Id);
            var adminRole = _db.Roles.FirstOrDefault(r => r.RoleCode == "Administrator");
            // LINQ-to-database Any() does not see entities staged for INSERT in
            // this DbContext. Without an in-memory key set the admin backfill
            // inserts a second row for EVERY admin permission at first launch.
            var stagedKeys = new HashSet<(int role, int resource, int operation)>();
            foreach (var rp in _db.RolePermissions.ToList())
                foreach (var opc in OperationCatalog)
                {
                    var op = opc.Code;
                    bool allowed = rp.RoleId == adminRole?.Id || rp.Can(op);
                    if (rp.ModuleCode != null && resByCode.TryGetValue(rp.ModuleCode, out var rid)
                        && opByCode.TryGetValue(op, out var oid) && stagedKeys.Add((rp.RoleId, rid, oid)))
                        _db.RoleResourcePermissions.Add(new RoleResourcePermission { RoleId = rp.RoleId, ResourceId = rid, OperationId = oid, IsAllowed = allowed });
                }
            // §لا مفتاح is_admin: دور المدير العام يُسجل صلاحياته كاملة في الجداول كأي دور
            if (adminRole != null)
                foreach (var rid in resByCode.Values)
                    foreach (var oid in opByCode.Values)
                        if (stagedKeys.Add((adminRole.Id, rid, oid)))
                            _db.RoleResourcePermissions.Add(new RoleResourcePermission { RoleId = adminRole.Id, ResourceId = rid, OperationId = oid, IsAllowed = true });
            _db.SaveChanges();
        }

        // §ظهرت موارد جديدة لاحقاً؟ يُستكمل المدير العام بها حتى لا يُقفل النظام
        var admin = _db.Roles.FirstOrDefault(r => r.RoleCode == "Administrator");
        if (admin != null)
        {
            bool added = false;
            foreach (var rid in _db.PermissionResources.Select(r => r.Id).ToList())
                foreach (var oid in _db.PermissionOperations.Select(o => o.Id).ToList())
                    if (!_db.RoleResourcePermissions.Any(x => x.RoleId == admin.Id && x.ResourceId == rid && x.OperationId == oid))
                    { _db.RoleResourcePermissions.Add(new RoleResourcePermission { RoleId = admin.Id, ResourceId = rid, OperationId = oid, IsAllowed = true }); added = true; }
            if (added) _db.SaveChanges();
        }

        // §التخطيط: ترقية القواعد القائمة التي أُنشئت قبل إضافة planning/Edit إلى مصفوفة
        // المورد×العملية. هذا إصلاح لدور الإنتاج المحدد، لا فتح عام ولا تجاوزاً للبوابة.
        EnsureProductionPlanningEdit();

        // §B84/S1: منح إعادة الفتح لمن يملك الاعتماد (تفعيل صلاحية Reopen الميتة سابقاً).
        GrantReopenToApprovers();
        // §B95: منح «تعديل بعد الاعتماد» لمعتمدي الجودة — التصحيح المعتمد على المحاضر المعتمدة.
        GrantQualityCorrectionToApprovers();
        // §الإصلاح الأمني: ترحيل القواعد القائمة إلى الوحدات التي دخلت البوابة حديثاً.
        BackfillNewlyGatedModules();
        // §3: الإشراف على كل المهام لمن يدير الصلاحيات — والمصنع يوسّعه من الشاشة.
        GrantTaskOversightToSystemAdmins();
        EnsureUniquePermissionMatrix();
    }

    /// <summary>
    /// Upgrade legacy duplicate permission keys before creating the database-level
    /// uniqueness guards. The old seeder staged administrator rows then checked
    /// database Any(), which cannot see staged inserts: a second, contradictory
    /// row could keep a revoked permission effective on another device.
    /// </summary>
    private void EnsureUniquePermissionMatrix()
    {
        const string marker = "PermissionMatrixUnique_B111";
        if (_db.SystemSettings.AsNoTracking().Any(s => s.SettingKey == marker)) return;
        using var tx = _db.Database.CurrentTransaction == null
            ? _db.Database.BeginTransaction(System.Data.IsolationLevel.Serializable) : null;
        // Re-check inside the transaction for a concurrent initialisation.
        if (_db.SystemSettings.AsNoTracking().Any(s => s.SettingKey == marker)) return;

        var resources = _db.PermissionResources.AsNoTracking().ToDictionary(x => x.Id, x => x.Code);
        var operations = _db.PermissionOperations.AsNoTracking().ToDictionary(x => x.Id, x => x.Code);
        var audit = _db.PermissionAuditLogs.AsNoTracking().ToList();

        foreach (var g in _db.RoleResourcePermissions.ToList()
            .GroupBy(x => (x.RoleId, x.ResourceId, x.OperationId)).Where(g => g.Count() > 1))
        {
            var entries = g.OrderBy(x => x.Id).ToList();
            var lastExplicit = audit.Where(a => a.TargetRoleId == g.Key.RoleId
                    && a.ResourceCode == resources.GetValueOrDefault(g.Key.ResourceId)
                    && a.OperationCode == operations.GetValueOrDefault(g.Key.OperationId)
                    && a.ActionType is "grant" or "revoke")
                .OrderBy(a => a.ChangedAt).ThenBy(a => a.Id).LastOrDefault();
            // A grant/revoke audit is authoritative. Without one, preserve the
            // existing effective role permission (OR of duplicate rows).
            entries[0].IsAllowed = lastExplicit == null
                ? entries.Any(x => x.IsAllowed) : lastExplicit.ActionType == "grant";
            _db.RoleResourcePermissions.RemoveRange(entries.Skip(1));
        }
        foreach (var g in _db.UserResourcePermissions.ToList()
            .GroupBy(x => (x.UserId, x.ResourceId, x.OperationId)).Where(g => g.Count() > 1))
        {
            var entries = g.OrderBy(x => x.Id).ToList();
            var lastExplicit = audit.Where(a => a.TargetUserId == g.Key.UserId
                    && a.ResourceCode == resources.GetValueOrDefault(g.Key.ResourceId)
                    && a.OperationCode == operations.GetValueOrDefault(g.Key.OperationId)
                    && a.ActionType is "grant" or "revoke")
                .OrderBy(a => a.ChangedAt).ThenBy(a => a.Id).LastOrDefault();
            // Conflicting user overrides without an audit cannot be interpreted
            // safely; deny until an administrator explicitly grants access.
            entries[0].IsAllowed = lastExplicit == null
                ? entries.All(x => x.IsAllowed) : lastExplicit.ActionType == "grant";
            _db.UserResourcePermissions.RemoveRange(entries.Skip(1));
        }
        _db.SaveChanges();
        if (_db.Database.IsSqlServer())
        {
            _db.Database.ExecuteSqlRaw("IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_RoleResourcePermissions_Identity' AND object_id = OBJECT_ID('RoleResourcePermissions')) CREATE UNIQUE INDEX [UX_RoleResourcePermissions_Identity] ON [RoleResourcePermissions] ([RoleId], [ResourceId], [OperationId])");
            _db.Database.ExecuteSqlRaw("IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_UserResourcePermissions_Identity' AND object_id = OBJECT_ID('UserResourcePermissions')) CREATE UNIQUE INDEX [UX_UserResourcePermissions_Identity] ON [UserResourcePermissions] ([UserId], [ResourceId], [OperationId])");
        }
        else
        {
            _db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS [UX_RoleResourcePermissions_Identity] ON [RoleResourcePermissions] ([RoleId], [ResourceId], [OperationId])");
            _db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS [UX_UserResourcePermissions_Identity] ON [UserResourcePermissions] ([UserId], [ResourceId], [OperationId])");
        }
        _db.SystemSettings.Add(new SystemSetting { SettingKey = marker, SettingValue = "1" });
        _db.SaveChanges();
        tx?.Commit();
    }

    /// <summary>
    /// §التخطيط — إصلاح ترقية مصفوفة الصلاحيات للأدوار الوظيفية المقصودة فقط.
    ///
    /// DbSeeder يمنح Production صلاحية Edit على planning، وManagement يملك المصفوفة
    /// الكاملة. لكن القواعد القائمة التي كوّنت RoleResourcePermissions قبل اكتمال هذا
    /// الربط ظلّت تحمل صفاً مرفوضاً/ناقصاً، ولذلك كان BuildEffectiveCache يعيد false
    /// ويخفي ErpToolbar.WithSave رغم صحة Save_Click وF10.
    ///
    /// هذا ترحيل لمرة واحدة: يضمن planning/Edit لدوري Production وManagement فقط.
    /// لا يمنحها لـ Administrator كحل التفافي ولا لكل الأدوار، ولا يلمس Approve؛ فهما
    /// عمليتان مستقلتان. بعد وضع علامة الترحيل، أي سحب يدوي لاحق من شاشة الصلاحيات يُحترم.
    /// </summary>
    public void EnsureProductionPlanningEdit()
    {
        const string markerKey = "PermissionUpgrade_PlanningEdit_ProductionManagement";
        if (_db.SystemSettings.Any(s => s.SettingKey == markerKey)) return;

        var resource = _db.PermissionResources.FirstOrDefault(r => r.IsActive && r.Code == PermissionModules.Planning);
        var operation = _db.PermissionOperations.FirstOrDefault(o => o.Code == "Edit");
        if (resource == null || operation == null) return;

        var roleCodes = new[] { SystemRoles.Production, SystemRoles.Management };
        var roles = _db.Roles.Where(r => r.IsActive && roleCodes.Contains(r.RoleCode)).ToList();
        if (roles.Count == 0) return;

        foreach (var role in roles)
        {
            var row = _db.RoleResourcePermissions.FirstOrDefault(x => x.RoleId == role.Id
                && x.ResourceId == resource.Id && x.OperationId == operation.Id);
            if (row == null)
            {
                _db.RoleResourcePermissions.Add(new RoleResourcePermission
                {
                    RoleId = role.Id,
                    ResourceId = resource.Id,
                    OperationId = operation.Id,
                    IsAllowed = true
                });
            }
            else
            {
                // صف الرفض هنا هو أثر المصفوفة القديمة، لا استثناء مستخدم؛ أصلحه مرة واحدة.
                row.IsAllowed = true;
            }
        }

        _db.SystemSettings.Add(new SystemSetting
        {
            SettingKey = markerKey,
            SettingValue = "1.50.73",
            Category = "Permissions",
            Description = "إصلاح planning/Edit لدوري الإنتاج والإدارة في مصفوفة الصلاحيات الهرمية"
        });
        _db.SaveChanges();
    }

    /// <summary>
    /// §الإصلاح الأمني — ترحيل آمن للقواعد القائمة.
    ///
    /// «products» و«cartons» و«employees» كانت خارج بذور الأدوار وخارج بوابة فتح الشاشات،
    /// فتُفتح شاشاتها بلا فحص. بعد إدخالها البوابة، القاعدة القائمة (المُرقّاة لا الجديدة)
    /// لا تحوي لها أي صف صلاحية لأي دور ⟵ سيُحجب عن كل المستخدمين ما كانوا يعملون عليه.
    ///
    /// هذا الترحيل يمنح «عرض» فقط لكل دور نشط على هذه الوحدات إن لم يكن له صف مسجّل أصلاً —
    /// فلا ينكسر عمل قائم، ولا تُمنح صلاحية تعديل أو حذف لأحد. ما زاد عن العرض يُمنح يدوياً
    /// من شاشة الصلاحيات. idempotent: لا يكتب شيئاً بعد أول تشغيل، ولا يلمس أي صف موجود.
    /// </summary>
    public void BackfillNewlyGatedModules()
    {
        // §3: «tasks» انضمت للقائمة — كل دور يجب أن يرى مهامه هو، وإلا كانت طبقة
        // التوجيه كلها محجوبة عن مستخدمي القاعدة المُرقّاة. «ViewAll» الإشرافية لا تُمنح هنا.
        // §المعالجة والتعقيم: «treatment» مورد جديد — يُمنح «عرض» لكل دور نشط عند الترقية.
        // العمليات الفعلية (بدء/إفراج/رفض) لا تُمنح تلقائياً: العرض لا يضر، والتنفيذ قرار إدارة.
        string[] newlyGated =
        {
            PermissionModules.Products, PermissionModules.Cartons,
            PermissionModules.Employees, PermissionModules.Tasks,
            PermissionModules.Treatment
        };
        var viewOp = _db.PermissionOperations.FirstOrDefault(o => o.Code == "View");
        if (viewOp == null) return;

        var resources = _db.PermissionResources.Where(r => newlyGated.Contains(r.Code)).ToList();
        if (resources.Count == 0) return;

        var roleIds = _db.Roles.Where(r => r.IsActive).Select(r => r.Id).ToList();
        bool added = false;
        foreach (var roleId in roleIds)
            foreach (var res in resources)
            {
                // أي صف مسجّل لهذا الدور على هذا المورد = الإدارة ضبطته سابقاً ⟵ لا نلمسه
                bool hasAny = _db.RoleResourcePermissions.Any(x => x.RoleId == roleId && x.ResourceId == res.Id);
                if (hasAny) continue;
                _db.RoleResourcePermissions.Add(new RoleResourcePermission
                { RoleId = roleId, ResourceId = res.Id, OperationId = viewOp.Id, IsAllowed = true });
                added = true;
            }
        if (added) _db.SaveChanges();
    }

    /// <summary>
    /// §3 — بذرة معقولة لصلاحية الإشراف على المهام: من يملك «إدارة الصلاحيات» يملك
    /// «عرض كل المهام». لا اسم دور هنا — الشرط قدرة قائمة، لا مسمى.
    /// المصنع يمنح <c>tasks/ViewAll</c> لمن يشاء غيرهم من شاشة الصلاحيات.
    /// idempotent: لا يكتب شيئاً بعد أول تشغيل ولا يلمس صفاً موجوداً.
    /// </summary>
    public void GrantTaskOversightToSystemAdmins()
    {
        var tasksRes = _db.PermissionResources.FirstOrDefault(r => r.Code == PermissionModules.Tasks);
        var permRes = _db.PermissionResources.FirstOrDefault(r => r.Code == PermissionModules.Permissions);
        var viewAllOp = _db.PermissionOperations.FirstOrDefault(o => o.Code == "ViewAll");
        var manageOp = _db.PermissionOperations.FirstOrDefault(o => o.Code == "ManagePermissions");
        if (tasksRes == null || permRes == null || viewAllOp == null || manageOp == null) return;

        var managerRoleIds = _db.RoleResourcePermissions
            .Where(x => x.ResourceId == permRes.Id && x.OperationId == manageOp.Id && x.IsAllowed)
            .Select(x => x.RoleId).Distinct().ToList();

        bool added = false;
        foreach (var roleId in managerRoleIds)
        {
            if (_db.RoleResourcePermissions.Any(x => x.RoleId == roleId && x.ResourceId == tasksRes.Id && x.OperationId == viewAllOp.Id))
                continue;
            _db.RoleResourcePermissions.Add(new RoleResourcePermission
            { RoleId = roleId, ResourceId = tasksRes.Id, OperationId = viewAllOp.Id, IsAllowed = true });
            added = true;
        }
        if (added) _db.SaveChanges();
    }

    /// <summary>
    /// §B84/S1: إكمال ناقص الكتالوج بالقوة (idempotent): يضيف أي مورد/عملية من الكتالوج
    /// البرمجي غير موجودة في القاعدة — فيرى التحديثُ القواعدَ القائمة لا الجديدة فقط.
    /// </summary>
    public void EnsureCatalogUpsert()
    {
        var resCodes = _db.PermissionResources.Select(r => r.Code).ToList();
        int s = _db.PermissionResources.Any() ? _db.PermissionResources.Max(r => r.SortNo) + 1 : 1;
        foreach (var (code, name, group) in ResourceCatalog)
            if (!resCodes.Contains(code))
                _db.PermissionResources.Add(new PermissionResource { Code = code, NameAr = name, GroupAr = group, SortNo = s++ });
        var opCodes = _db.PermissionOperations.Select(o => o.Code).ToList();
        s = _db.PermissionOperations.Any() ? _db.PermissionOperations.Max(o => o.SortNo) + 1 : 1;
        foreach (var (code, name, sens) in OperationCatalog)
            if (!opCodes.Contains(code))
                _db.PermissionOperations.Add(new PermissionOperation { Code = code, NameAr = name, IsSensitive = sens, SortNo = s++ });
        _db.SaveChanges();
    }

    /// <summary>
    /// §B84/S1: كل دور يملك (planning + Approve) يُمنح (planning + Reopen) تلقائياً —
    /// عملياً: مدير النظام والإدارة والإنتاج. تُستدعى عند كل إقلاع (EnsureCatalog) فلا إقفال
    /// للأدوار القائمة بعد تفعيل فحص Reopen في PlanClosureService.ReopenPlan.
    /// </summary>
    public void GrantReopenToApprovers()
    {
        var planRes = _db.PermissionResources.FirstOrDefault(r => r.Code == "planning");
        var approveOp = _db.PermissionOperations.FirstOrDefault(o => o.Code == "Approve");
        var reopenOp = _db.PermissionOperations.FirstOrDefault(o => o.Code == "Reopen");
        if (planRes == null || approveOp == null || reopenOp == null) return;
        var approverRoles = _db.RoleResourcePermissions
            .Where(x => x.ResourceId == planRes.Id && x.OperationId == approveOp.Id && x.IsAllowed)
            .Select(x => x.RoleId).Distinct().ToList();
        bool added = false;
        foreach (var roleId in approverRoles)
            if (!_db.RoleResourcePermissions.Any(x => x.RoleId == roleId && x.ResourceId == planRes.Id && x.OperationId == reopenOp.Id))
            {
                _db.RoleResourcePermissions.Add(new RoleResourcePermission
                    { RoleId = roleId, ResourceId = planRes.Id, OperationId = reopenOp.Id, IsAllowed = true });
                added = true;
            }
        if (added) _db.SaveChanges();
    }

    /// <summary>
    /// §B95: منح «الجودة/تعديل بعد الاعتماد» لكل دور يملك «الجودة/اعتماد» —
    /// التصحيح المعتمد على محضر معتمد حقٌّ لمن اعتمده أصلاً، بسبب مكتوب يُسجَّل في التدقيق.
    /// تُستدعى عند كل إقلاع (EnsureCatalog) فلا إقفال للأدوار القائمة بعد تفعيل البوابة في RequestCorrection.
    /// </summary>
    public void GrantQualityCorrectionToApprovers()
    {
        var qRes = _db.PermissionResources.FirstOrDefault(r => r.Code == "quality");
        var approveOp = _db.PermissionOperations.FirstOrDefault(o => o.Code == "Approve");
        var corrOp = _db.PermissionOperations.FirstOrDefault(o => o.Code == "EditAfterApproval");
        if (qRes == null || approveOp == null || corrOp == null) return;
        var approverRoles = _db.RoleResourcePermissions
            .Where(x => x.ResourceId == qRes.Id && x.OperationId == approveOp.Id && x.IsAllowed)
            .Select(x => x.RoleId).Distinct().ToList();
        bool added = false;
        foreach (var roleId in approverRoles)
            if (!_db.RoleResourcePermissions.Any(x => x.RoleId == roleId && x.ResourceId == qRes.Id && x.OperationId == corrOp.Id))
            {
                _db.RoleResourcePermissions.Add(new RoleResourcePermission
                    { RoleId = roleId, ResourceId = qRes.Id, OperationId = corrOp.Id, IsAllowed = true });
                added = true;
            }
        if (added) _db.SaveChanges();
    }

    // ═══ الصلاحية الفعلية: أدوار المستخدم ثم استثناءاته الصريحة فوقها ═══
    public Dictionary<(string module, string action), bool> BuildEffectiveCache(int userId, List<int> roleIds)
    {
        EnsureCatalog();
        var resById = _db.PermissionResources.ToDictionary(r => r.Id, r => r.Code);
        var opById = _db.PermissionOperations.ToDictionary(o => o.Id, o => o.Code);
        var cache = new Dictionary<(string, string), bool>();

        var roleRows = _db.RoleResourcePermissions.Where(x => roleIds.Contains(x.RoleId)).ToList();
        foreach (var x in roleRows)
        {
            var key = (resById[x.ResourceId], opById[x.OperationId]);
            cache[key] = cache.TryGetValue(key, out var v) && v || x.IsAllowed;
        }
        var userRows = _db.UserResourcePermissions.Where(x => x.UserId == userId).ToList();
        foreach (var x in userRows)
            cache[(resById[x.ResourceId], opById[x.OperationId])] = x.IsAllowed; // الاستثناء يعلو
        return cache;
    }

    public HashSet<(string res, string op)> GetRoleSet(int roleId)
    {
        EnsureCatalog();
        var resById = _db.PermissionResources.ToDictionary(r => r.Id, r => r.Code);
        var opById = _db.PermissionOperations.ToDictionary(o => o.Id, o => o.Code);
        return _db.RoleResourcePermissions.Where(x => x.RoleId == roleId && x.IsAllowed).ToList()
            .Select(x => (resById[x.ResourceId], opById[x.OperationId])).ToHashSet();
    }

    /// <summary>
    /// §7 — الموروث من الأدوار فقط (بلا استثناءات المستخدم): ما يملكه المستخدم بحكم أدواره.
    /// تستخدمه شاشة الصلاحيات لعرض عمود «من الدور» منفصلاً عن عمود «استثناء».
    /// </summary>
    public HashSet<(string res, string op)> GetInheritedSet(List<int> roleIds)
    {
        EnsureCatalog();
        var resById = _db.PermissionResources.ToDictionary(r => r.Id, r => r.Code);
        var opById = _db.PermissionOperations.ToDictionary(o => o.Id, o => o.Code);
        return _db.RoleResourcePermissions.Where(x => roleIds.Contains(x.RoleId) && x.IsAllowed).ToList()
            .Select(x => (resById[x.ResourceId], opById[x.OperationId])).ToHashSet();
    }

    /// <summary>
    /// §7 — استثناءات المستخدم الصريحة بقيمتها: true = منح فوق الدور، false = منع رغم الدور.
    /// غياب المفتاح = لا استثناء (وراثة صافية) — وهي الحالة الثالثة التي كان المربع المسطّح يخفيها.
    /// </summary>
    public Dictionary<(string res, string op), bool> GetUserExceptions(int userId)
    {
        EnsureCatalog();
        var resById = _db.PermissionResources.ToDictionary(r => r.Id, r => r.Code);
        var opById = _db.PermissionOperations.ToDictionary(o => o.Id, o => o.Code);
        return _db.UserResourcePermissions.Where(x => x.UserId == userId).ToList()
            .ToDictionary(x => (resById[x.ResourceId], opById[x.OperationId]), x => x.IsAllowed);
    }

    /// <summary>
    /// §7 — إزالة الاستثناء نهائياً فيعود المستخدم إلى وراثة دوره.
    /// (كانت مستحيلة سابقاً: المربع المسطّح لا يفرّق بين «لا استثناء» و«منع صريح».)
    /// </summary>
    public void ClearUserPermission(int userId, string resCode, string opCode)
    {
        RequireManage();
        var res = _db.PermissionResources.FirstOrDefault(r => r.Code == resCode) ?? throw new DomainException("مورد غير معروف.");
        var op = _db.PermissionOperations.FirstOrDefault(o => o.Code == opCode) ?? throw new DomainException("عملية غير معروفة.");
        var row = _db.UserResourcePermissions.FirstOrDefault(x => x.UserId == userId && x.ResourceId == res.Id && x.OperationId == op.Id);
        if (row == null) return;
        string oldV = row.IsAllowed ? "استثناء: مسموح" : "استثناء: ممنوع";
        _db.UserResourcePermissions.Remove(row);
        Log(null, userId, resCode, opCode, oldV, "وراثة من الدور", "inherit");
        _db.SaveChanges();
    }

    public HashSet<(string res, string op)> GetUserSet(int userId)
    {
        EnsureCatalog();
        var resById = _db.PermissionResources.ToDictionary(r => r.Id, r => r.Code);
        var opById = _db.PermissionOperations.ToDictionary(o => o.Id, o => o.Code);
        return _db.UserResourcePermissions.Where(x => x.UserId == userId).ToList()
            .Select(x => (resById[x.ResourceId], opById[x.OperationId])).ToHashSet();
    }

    // ═══ التعديل مع التدقيق ═══
    /// <summary>
    /// §R2 — إعادة بناء مصفوفة الجلسة الحية من القاعدة: أدوار + استثناءات + تفويض ساري
    /// (بما فيه فحص نشاط المفوِّض). المستخدم المعطّل/المقفل تُصفَّر مصفوفته فوراً —
    /// فالسحب والتعطيل يسريان على الجلسات المفتوحة خلال دقيقة واحدة كحد أقصى (عبر Require).
    /// </summary>
    public void RefreshSessionCache(int userId)
    {
        _session.PermissionCache.Clear();
        var u = _db.Users.AsNoTracking().FirstOrDefault(x => x.Id == userId);
        if (u == null || !u.IsActive || u.IsLocked) { _session.CacheBuiltAt = DateTime.Now; return; }

        var roleIds = _db.UserRoles.AsNoTracking().Where(ur => ur.UserId == userId && ur.IsActive).Select(ur => ur.RoleId).ToList();
        _session.Roles.Clear();
        foreach (var code in _db.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id)).Select(r => r.RoleCode).ToList())
            _session.Roles.Add(code);
        foreach (var kv in BuildEffectiveCache(userId, roleIds))
            _session.PermissionCache[kv.Key] = kv.Value;

        var today = DateTime.Now.Date;
        var delegs = _db.Delegations.AsNoTracking().Where(d => d.IsActive && d.ToUserId == userId
            && d.StartDate <= today && d.EndDate >= today
            && _db.Users.Any(x => x.Id == d.FromUserId && x.IsActive && !x.IsLocked)).ToList();
        foreach (var dg in delegs)
        {
            var fromRoles = _db.UserRoles.AsNoTracking().Where(ur => ur.UserId == dg.FromUserId && ur.IsActive).Select(ur => ur.RoleId).ToList();
            foreach (var kv in BuildEffectiveCache(dg.FromUserId, fromRoles))
            {
                if (dg.ScopeModule != null && kv.Key.module != dg.ScopeModule) continue;
                _session.PermissionCache[kv.Key] = (_session.PermissionCache.TryGetValue(kv.Key, out var v) && v) || kv.Value;
            }
        }
        _session.CacheBuiltAt = DateTime.Now;
    }

    /// <summary>§فحص الصلاحيات — المعدِّلات محكومة في طبقة الخدمات نفسها (كان الحكم في
    /// بوابة الشاشة فقط): أي مسار برمجي يصل هنا بلا ManagePermissions يُرفض server-side.</summary>
    private void RequireManage()
    {
        if (_session == null || !_session.Can(PermissionModules.Permissions, "ManagePermissions"))
            throw new PermissionDeniedException("ManagePermissions على وحدة permissions");
    }

    public void SetRolePermission(int roleId, string resCode, string opCode, bool allowed)
    {
        RequireManage();
        var res = _db.PermissionResources.FirstOrDefault(r => r.Code == resCode) ?? throw new DomainException("مورد غير معروف.");
        var op = _db.PermissionOperations.FirstOrDefault(o => o.Code == opCode) ?? throw new DomainException("عملية غير معروفة.");
        var row = _db.RoleResourcePermissions.FirstOrDefault(x => x.RoleId == roleId && x.ResourceId == res.Id && x.OperationId == op.Id);
        string oldV = row == null ? "غير مسجلة" : row.IsAllowed ? "مسموح" : "مرفوض";
        if (row == null) { row = new RoleResourcePermission { RoleId = roleId, ResourceId = res.Id, OperationId = op.Id }; _db.RoleResourcePermissions.Add(row); }
        row.IsAllowed = allowed;
        Log(roleId, null, resCode, opCode, oldV, allowed ? "مسموح" : "مرفوض", allowed ? "grant" : "revoke");
        _db.SaveChanges();
    }

    public void SetUserPermission(int userId, string resCode, string opCode, bool allowed)
    {
        RequireManage();
        var res = _db.PermissionResources.FirstOrDefault(r => r.Code == resCode) ?? throw new DomainException("مورد غير معروف.");
        var op = _db.PermissionOperations.FirstOrDefault(o => o.Code == opCode) ?? throw new DomainException("عملية غير معروفة.");
        var row = _db.UserResourcePermissions.FirstOrDefault(x => x.UserId == userId && x.ResourceId == res.Id && x.OperationId == op.Id);
        string oldV = row == null ? "بدون استثناء" : row.IsAllowed ? "مسموح" : "مرفوض";
        if (row == null) { row = new UserResourcePermission { UserId = userId, ResourceId = res.Id, OperationId = op.Id }; _db.UserResourcePermissions.Add(row); }
        row.IsAllowed = allowed;
        Log(null, userId, resCode, opCode, oldV, allowed ? "مسموح" : "مرفوض", allowed ? "grant" : "revoke");
        _db.SaveChanges();
    }

    public void CopyRolePermissions(int srcRoleId, int dstRoleId)
    {
        RequireManage();
        if (srcRoleId == dstRoleId) throw new DomainException("لا يمكن نسخ الدور إلى نفسه.");
        var src = GetRoleSet(srcRoleId);
        var resByCode = _db.PermissionResources.ToDictionary(r => r.Code, r => r.Id);
        var opByCode = _db.PermissionOperations.ToDictionary(o => o.Code, o => o.Id);
        // With unique matrix indexes, EF may INSERT new permissions before
        // deleting old ones. Flush removals first inside ONE transaction so a
        // failed copy cannot leave the destination role half-configured.
        using var tx = _db.Database.CurrentTransaction == null ? _db.Database.BeginTransaction() : null;
        _db.RoleResourcePermissions.RemoveRange(_db.RoleResourcePermissions.Where(x => x.RoleId == dstRoleId).ToList());
        _db.SaveChanges();
        foreach (var (res, op) in src)
            _db.RoleResourcePermissions.Add(new RoleResourcePermission { RoleId = dstRoleId, ResourceId = resByCode[res], OperationId = opByCode[op], IsAllowed = true });
        Log(dstRoleId, null, "*", "*", "صلاحيات سابقة", $"نسخ من دور #{srcRoleId} ({src.Count} صلاحية)", "copy");
        _db.SaveChanges();
        tx?.Commit();
    }

    public void CopyUserPermissions(int srcUserId, int dstUserId)
    {
        RequireManage();
        if (srcUserId == dstUserId) throw new DomainException("لا يمكن نسخ المستخدم إلى نفسه.");
        var src = _db.UserResourcePermissions.Where(x => x.UserId == srcUserId).ToList();
        using var tx = _db.Database.CurrentTransaction == null ? _db.Database.BeginTransaction() : null;
        _db.UserResourcePermissions.RemoveRange(_db.UserResourcePermissions.Where(x => x.UserId == dstUserId).ToList());
        _db.SaveChanges();
        foreach (var s in src)
            _db.UserResourcePermissions.Add(new UserResourcePermission { UserId = dstUserId, ResourceId = s.ResourceId, OperationId = s.OperationId, IsAllowed = s.IsAllowed });
        Log(null, dstUserId, "*", "*", "استثناءات سابقة", $"نسخ من مستخدم #{srcUserId} ({src.Count})", "copy");
        _db.SaveChanges();
        tx?.Commit();
    }

    // ═══ منع الإغلاق الكامل: لا تعطيل لآخر من يملك إدارة الصلاحيات ═══
    public void GuardLastPermissionAdmin(int? deactivateUserId, int? deactivateRoleId)
    {
        bool UserCanManage(int uid)
        {
            var roleIds = _db.UserRoles.Where(ur => ur.UserId == uid && ur.IsActive).Select(ur => ur.RoleId).ToList();
            var cache = BuildEffectiveCache(uid, roleIds);
            return cache.TryGetValue(("permissions", "ManagePermissions"), out var v) && v;
        }
        var activeUsers = _db.Users.Where(u => u.IsActive && !u.IsLocked).ToList();
        var remaining = activeUsers.Where(u =>
            (deactivateUserId == null || u.Id != deactivateUserId) &&
            !(deactivateRoleId != null && u.UserRoles.Any(ur => ur.RoleId == deactivateRoleId && ur.IsActive)) &&
            UserCanManage(u.Id)).ToList();
        if (!remaining.Any())
            throw new DomainException("مرفوض: لا يمكن تعطيل آخر مستخدم يملك صلاحية إدارة الصلاحيات — سيُغلق النظام كلياً.");
    }

    public void DeactivateUser(int userId)
    {
        RequireManage();
        GuardLastPermissionAdmin(userId, null);
        var u = _db.Users.FirstOrDefault(x => x.Id == userId) ?? throw new DomainException("المستخدم غير موجود.");
        u.IsActive = false;
        Log(null, userId, "*", "*", "نشط", "معطل", "deactivate");
        _db.SaveChanges();
    }

    public void DeactivateRole(int roleId)
    {
        RequireManage();
        GuardLastPermissionAdmin(null, roleId);
        var r = _db.Roles.FirstOrDefault(x => x.Id == roleId) ?? throw new DomainException("الدور غير موجود.");
        r.IsActive = false;
        Log(roleId, null, "*", "*", "نشط", "معطل", "deactivate");
        _db.SaveChanges();
    }

    /// <summary>
    /// §B110 — إنشاء دور مخصص عبر طبقة الخدمات: كود فريد مضمون، منع تكرار الاسم،
    /// صلاحية مفروضة وتدقيق إلحاقي. قبل هذا الإصلاح كانت شاشة «الصلاحيات» تكتب
    /// الدور مباشرة على القاعدة بكود عشوائي وبلا تدقيق ولا فحص صلاحية.
    /// </summary>
    public Role CreateCustomRole(string nameAr)
    {
        if (_session == null || !_session.Can(PermissionModules.Users, "Edit"))
            throw new PermissionDeniedException("Edit على وحدة users");
        if (string.IsNullOrWhiteSpace(nameAr)) throw new DomainException("أدخل اسم الدور.");
        nameAr = nameAr.Trim();
        if (_db.Roles.Any(r => r.RoleNameAr == nameAr && r.IsActive))
            throw new DomainException("يوجد دور نشط بنفس الاسم — التكرار ممنوع.");
        string code;
        do { code = "R-" + Guid.NewGuid().ToString("N")[..5].ToUpper(); }
        while (_db.Roles.Any(r => r.RoleCode == code));
        var role = new Role { RoleCode = code, RoleNameAr = nameAr, IsActive = true };
        _db.Roles.Add(role);
        _db.SaveChanges();
        Log(role.Id, null, "roles", "Create", null, nameAr, "grant");
        _db.SaveChanges();
        return role;
    }

    public List<PermissionAuditLog> GetAudit()
        => _db.PermissionAuditLogs.OrderByDescending(a => a.Id).Take(500).ToList();

    private void Log(int? roleId, int? userId, string res, string op, string oldV, string newV, string action)
    {
        _db.PermissionAuditLogs.Add(new PermissionAuditLog
        {
            ChangedById = _session.UserId > 0 ? _session.UserId : null,
            ChangedByName = _session.UserName ?? "system",
            TargetRoleId = roleId,
            TargetUserId = userId,
            ResourceCode = res,
            OperationCode = op,
            OldValue = oldV,
            NewValue = newV,
            ActionType = action
        });
    }
}
