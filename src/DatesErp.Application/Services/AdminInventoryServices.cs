using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>§10/§27 — إدارة المستخدمين والأدوار والصلاحيات والأجهزة مركزياً.</summary>
public class AdminService : ServiceBase, IAdminService
{
    public AdminService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    [System.Obsolete("GenericListView يقرأ من DbContext مباشرة — هذه الدالة غير مستخدمة.")]
    public List<AppUser> GetUsers()
    {
        Require("users", "View");
        return Db.Users.Include(u => u.UserRoles).OrderBy(u => u.Id).ToList();
    }

    public OpResult SaveUser(int? id, string userCode, string userName, string fullName, string password, List<int> roleIds, bool isActive)
    {
        Require("users", id == null ? "Create" : "Edit");
        // §فحص المستخدمين والصلاحيات — سياسة كلمة المرور تُفرض هنا أيضاً (كانت مفروضة في
        // التصفير/التغيير فقط فأمكن إنشاء مستخدم بكلمة هزيلة مثل «123» من نموذج الحفظ).
        if (!string.IsNullOrEmpty(password))
        {
            int minLen = 8;
            var minSetting = Db.SystemSettings.AsNoTracking().FirstOrDefault(x => x.SettingKey == "PasswordMinLength");
            if (minSetting != null && int.TryParse(minSetting.SettingValue, out var parsedMin) && parsedMin > 0) minLen = parsedMin;
            var policyError = MasterDataService.ValidatePasswordPolicy(password, minLen);
            if (policyError != null) return OpResult.Fail(policyError);
        }
        // §فحص المستخدمين والصلاحيات — حارس الإغلاق الكلي: لا تعطيل ولا تجريد من الأدوار
        // لآخر من يملك إدارة الصلاحيات عبر هذا المسار (كان الحارس في ToggleUserActive وحده).
        bool losingActivity = id != null && !isActive;
        bool hadManage = id != null && UserManagesPermissions(id.Value);
        bool willManage = roleIds != null && RoleIdsGrantManagePermissions(roleIds);
        if ((losingActivity || (hadManage && !willManage)) )
        {
            try { new PermissionService(Db, Session).GuardLastPermissionAdmin(losingActivity ? id : null, null); }
            catch (DomainException ex) { return OpResult.Fail(ex.Message); }
            if (!losingActivity && hadManage && !willManage)
            {
                // تجريد من الأدوار: نفحص بقاء مدير صلاحيات آخر بعد استبدال أدوار هذا المستخدم
                var remaining = Db.Users.AsNoTracking().Where(u => u.IsActive && !u.IsLocked && u.Id != id.Value).Select(u => u.Id).ToList();
                bool anyOther = remaining.Any(UserManagesPermissions);
                if (!anyOther) return OpResult.Fail("مرفوض: لا يمكن تجريد آخر مستخدم يملك صلاحية إدارة الصلاحيات من أدواره — سيُغلق النظام كلياً.");
            }
        }
        return RunOp(() =>
        {
            var user = id == null ? new AppUser() : Db.Users.Include(u => u.UserRoles).FirstOrDefault(u => u.Id == id);
            if (user == null) throw new DomainException("المستخدم غير موجود.");
            if (Db.Users.Any(u => u.UserName == userName && u.Id != user.Id))
                throw new DomainException("اسم المستخدم موجود مسبقاً.");
            // §الدخول بالرقم: رقم الدخول فريد — هذا الرقم محجوز
            if (!string.IsNullOrWhiteSpace(userCode) && Db.Users.Any(u => u.UserCode == userCode && u.Id != user.Id))
                throw new DomainException("هذا الرقم محجوز — اختر رقم دخول آخر.");
            if (string.IsNullOrWhiteSpace(userCode))
            {
                int max = Db.Users.AsNoTracking().Select(u => u.UserCode).ToList()
                    .Select(c => int.TryParse(c, out var n) ? n : 0).DefaultIfEmpty(1000).Max();
                userCode = (max + 1).ToString();
            }

            user.UserCode = userCode;
            user.UserName = userName;
            user.FullName = fullName;
            user.IsActive = isActive;
            if (!string.IsNullOrEmpty(password))
            {
                var (h, s) = PasswordHasher.Hash(password);
                user.PasswordHash = h;
                user.PasswordSalt = s;
                user.MustChangePassword = true;
            }
            if (id == null) Db.Users.Add(user);
            Db.SaveChanges();

            Db.UserRoles.RemoveRange(Db.UserRoles.Where(r => r.UserId == user.Id));
            foreach (var rid in roleIds.Distinct())
                Db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = rid });
            Db.SaveChanges();
            // §فحص المستخدمين والصلاحيات — تدقيق إلحاقي: أخطر عملية إدارية كانت بلا أثر في السجل
            Db.AuditLogs.Add(new AuditLog
            {
                UserId = Session?.UserId > 0 ? Session.UserId : null,
                UserName = Session?.UserName ?? "system",
                MachineName = System.Environment.MachineName,
                ActionDate = DateTime.Now,
                ScreenName = "Users",
                ActionType = id == null ? "CreateUser" : "EditUser",
                DocumentType = "Users",
                DocumentNumber = user.UserName,
                RecordId = user.Id,
                NewValue = $"أدوار: {string.Join(",", roleIds.Distinct())}{(password != null && password.Length > 0 ? " + كلمة مرور جديدة" : "")}{(isActive ? "" : " — معطل")}"
            });
            Db.SaveChanges();
            return OpResult.Success(id == null
                ? $"تم إنشاء المستخدم — رقم الدخول: {user.UserCode}."
                : $"تم حفظ التعديلات — رقم الدخول: {user.UserCode}.", user.Id, user.UserCode);
        });
    }

    /// <summary>هل يملك المستخدم (عبر أدواره أو استثناءاته) إدارة الصلاحيات فعلياً؟</summary>
    private bool UserManagesPermissions(int userId)
    {
        var roleIds = Db.UserRoles.AsNoTracking().Where(ur => ur.UserId == userId && ur.IsActive).Select(ur => ur.RoleId).ToList();
        var cache = new PermissionService(Db, Session).BuildEffectiveCache(userId, roleIds);
        return cache.TryGetValue(("permissions", "ManagePermissions"), out var v) && v;
    }
    private bool RoleIdsGrantManagePermissions(List<int> roleIds)
    {
        var res = Db.PermissionResources.AsNoTracking().FirstOrDefault(r => r.Code == "permissions")?.Id;
        var op = Db.PermissionOperations.AsNoTracking().FirstOrDefault(o => o.Code == "ManagePermissions")?.Id;
        if (res == null || op == null) return false;
        return Db.RoleResourcePermissions.AsNoTracking().Any(x => roleIds.Contains(x.RoleId) && x.ResourceId == res && x.OperationId == op && x.IsAllowed);
    }

    public OpResult DeleteUser(int id)
    {
        Require("users", "Delete");
        if (id == Session?.UserId) return OpResult.Fail("لا يمكنك حذف حسابك الحالي.");
        var user = Db.Users.FirstOrDefault(u => u.Id == id);
        if (user == null) return OpResult.Fail("المستخدم غير موجود.");
        return RunOp(() =>
        {
            user.IsActive = false; // تعطيل بدل الحذف حفاظاً على سلسلة التدقيق
            Db.SaveChanges();
            return OpResult.Success("تم تعطيل المستخدم.");
        });
    }

    public List<Role> GetRolesWithPermissions()
    {
        Require("users", "View");
        return Db.Roles.Include(r => r.Permissions).OrderBy(r => r.Id).ToList();
    }

    [System.Obsolete("استخدم PermissionService.SetRolePermission — النموذج الهرمي هو المعتمد.")]
    public OpResult SaveRolePermissions(int roleId, Dictionary<string, int> moduleMasks)
    {
        Require("users", "Edit");
        return RunOp(() =>
        {
            foreach (var kv in moduleMasks)
            {
                var p = Db.RolePermissions.FirstOrDefault(x => x.RoleId == roleId && x.ModuleCode == kv.Key);
                if (p == null) Db.RolePermissions.Add(new RolePermission { RoleId = roleId, ModuleCode = kv.Key, PermissionMask = kv.Value });
                else p.PermissionMask = kv.Value;
            }
            Db.SaveChanges();
            return OpResult.Success("تم حفظ مصفوفة الصلاحيات.");
        });
    }

    public List<ClientMachine> GetMachines()
    {
        Require("settings", "View");
        return Db.ClientMachines.OrderByDescending(m => m.LastSeen).ToList();
    }
}

/// <summary>§9 — الاستعلام عن الأرصدة والحركات مع بيانات التتبع الكاملة.</summary>
public class InventoryService : ServiceBase, IInventoryService
{
    public InventoryService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    public List<StockBalanceDto> GetBalances(int? warehouseId = null, int? productId = null, int? packagingTypeId = null, int? customerId = null)
    {
        Require("inventory", "View");
        var q = Db.StockBalances.AsQueryable();
        if (warehouseId != null) q = q.Where(b => b.WarehouseId == warehouseId);
        if (productId != null) q = q.Where(b => b.ProductId == productId);
        if (packagingTypeId != null) q = q.Where(b => b.PackagingTypeId == packagingTypeId);
        if (customerId != null) q = q.Where(b => b.CustomerId == customerId);
        return q.Select(b => new StockBalanceDto
        {
            WarehouseId = b.WarehouseId,
            WarehouseName = Db.Warehouses.Where(w => w.Id == b.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault(),
            ProductId = b.ProductId,
            ItemName = b.ProductId != null
                ? Db.Products.Where(p => p.Id == b.ProductId).Select(p => p.ProductNameAr).FirstOrDefault()
                : Db.AuxiliaryMaterials.Where(m => m.Id == b.MaterialId).Select(m => m.MaterialNameAr).FirstOrDefault(),
            LotCode = Db.Lots.Where(l => l.Id == b.LotId).Select(l => l.LotCode).FirstOrDefault(),
            CustomerName = Db.Customers.Where(c => c.Id == b.CustomerId).Select(c => c.CustomerName).FirstOrDefault(),
            PackagingTypeName = Db.PackagingTypes.Where(p => p.Id == b.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault(),
            PackagingTypeId = b.PackagingTypeId,
            CustomerId = b.CustomerId,
            LotId = b.LotId,
            QtyKg = b.QtyKg,
            PackageCount = b.PackageCount
        }).Where(b => b.QtyKg != 0 || b.PackageCount != 0).ToList();
    }

    public List<InventoryTransactionDto> GetTransactions(DateTime? from = null, DateTime? to = null, int? warehouseId = null)
    {
        Require("inventory", "View");
        var q = Db.InventoryTransactions.AsQueryable();
        if (from != null) q = q.Where(t => t.TxnDate >= from);
        if (to != null) q = q.Where(t => t.TxnDate <= to.Value.AddDays(1));
        if (warehouseId != null) q = q.Where(t => t.WarehouseId == warehouseId);
        return q.OrderByDescending(t => t.TxnDate).Take(2000).Select(t => new InventoryTransactionDto
        {
            TxnNumber = t.TxnNumber,
            TxnDate = t.TxnDate.ToString("dd/MM/yyyy HH:mm"),
            WarehouseName = Db.Warehouses.Where(w => w.Id == t.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault(),
            ItemName = t.ProductId != null
                ? Db.Products.Where(p => p.Id == t.ProductId).Select(p => p.ProductNameAr).FirstOrDefault()
                : Db.AuxiliaryMaterials.Where(m => m.Id == t.MaterialId).Select(m => m.MaterialNameAr).FirstOrDefault(),
            LotCode = Db.Lots.Where(l => l.Id == t.LotId).Select(l => l.LotCode).FirstOrDefault(),
            MovementTypeAr = t.MovementType == Core.Domain.Enums.MovementType.Inbound ? "وارد"
                : t.MovementType == Core.Domain.Enums.MovementType.Outbound ? "صادر" : "تحويل",
            QtyKg = t.QtyKg,
            ReferenceDoc = t.ReferenceDocNumber,
            CreatedByUser = Db.Users.Where(u => u.Id == t.CreatedBy).Select(u => u.FullName).FirstOrDefault(),
            MachineName = t.MachineName
        }).ToList();
    }
}

/// <summary>§27 — تسجيل أجهزة العملاء عند كل دخول.</summary>
public class MachineRegistry
{
    private readonly DatesErpDbContext _db;
    public MachineRegistry(DatesErpDbContext db) => _db = db;

    public void Heartbeat(string appVersion)
    {
        try
        {
            var machineId = $"{Environment.MachineName}-{Environment.UserName}";
            var m = _db.ClientMachines.FirstOrDefault(x => x.MachineId == machineId);
            if (m == null)
            {
                m = new ClientMachine { MachineId = machineId, MachineName = Environment.MachineName, WindowsUser = Environment.UserName };
                _db.ClientMachines.Add(m);
            }
            m.ApplicationVersion = appVersion;
            m.LastSeen = DateTime.Now;
            m.LastLogin ??= DateTime.Now;
            m.LastLogin = DateTime.Now;
            m.IsActive = true;
            _db.SaveChanges();
        }
        catch { /* تسجيل الأجهزة لا يعطل الدخول */ }
    }
}
