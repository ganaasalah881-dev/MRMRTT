using DatesErp.Core.Common;
using DatesErp.Core.Domain.Enums;

namespace DatesErp.Core.Domain.Entities;

/// <summary>§10 — مستخدم النظام. كلمة المرور تُخزن hashed فقط ولا تظهر في أي ملف إعداد.</summary>
public class AppUser : AuditableEntity
{
    public string UserCode { get; set; }
    public string UserName { get; set; }
    public string FullName { get; set; }
    public string PasswordHash { get; set; }
    public string PasswordSalt { get; set; }
    public int? EmployeeId { get; set; }
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public int FailedLoginCount { get; set; }
    public bool IsLocked { get; set; }
    public DateTime? LastLoginDate { get; set; }
    /// <summary>§سياسة كلمة المرور: آخر تغيير — أساس انتهاء الصلاحية والتدقيق.</summary>
    public DateTime? PasswordChangedDate { get; set; }
    /// <summary>§تاريخ آخر قفل — أساس فك القفل التلقائي بعد انقضاء المدة.</summary>
    public DateTime? LockoutDate { get; set; }
    /// <summary>§R4 — سجل آخر 5 كلمات مرور (تجزئة|ملح) لمنع إعادة استخدامها. يُضاف تلقائياً لقواعدكم عبر SchemaMigrator.</summary>
    public string PasswordHistory { get; set; }

    public List<UserRole> UserRoles { get; set; } = new();
}

public class Role : BaseEntity
{
    public string RoleCode { get; set; }
    public string RoleNameAr { get; set; }
    public string RoleNameEn { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;
    public List<RolePermission> Permissions { get; set; } = new();
}

public class UserRole : BaseEntity
{
    public int UserId { get; set; }
    public int RoleId { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>§10 — الصلاحيات محفوظة مركزياً في قاعدة البيانات، لكل دور وعلى كل وحدة.</summary>
public class RolePermission : BaseEntity
{
    public int RoleId { get; set; }
    public string ModuleCode { get; set; } // customers, receiving, planning, production, quality, inventory, delivery, reports, admin
    public int PermissionMask { get; set; } // PermissionFlags
    public bool Can(string action) => action switch
    {
        "View" => (PermissionMask & (int)PermissionFlags.View) != 0,
        "Create" => (PermissionMask & (int)PermissionFlags.Create) != 0,
        "Edit" => (PermissionMask & (int)PermissionFlags.Edit) != 0,
        "Delete" => (PermissionMask & (int)PermissionFlags.Delete) != 0,
        "Approve" => (PermissionMask & (int)PermissionFlags.Approve) != 0,
        "Post" => (PermissionMask & (int)PermissionFlags.Post) != 0,
        "Print" => (PermissionMask & (int)PermissionFlags.Print) != 0,
        "Export" => (PermissionMask & (int)PermissionFlags.Export) != 0,
        "Cancel" => (PermissionMask & (int)PermissionFlags.Cancel) != 0,
        _ => false
    };
}

/// <summary>§26 — سجل التدقيق المركزي: من، أين، متى، ماذا، القيمة قبل/بعد.</summary>
public class AuditLog : BaseEntity
{
    public int? UserId { get; set; }
    public string UserName { get; set; }
    public string ComputerName { get; set; }
    public string MachineName { get; set; }
    public DateTime ActionDate { get; set; } = DateTime.Now;
    public string ScreenName { get; set; }
    public string ActionType { get; set; } // Create|Edit|Delete|Approve|Post|Cancel|Issue|Production|Delivery|Login|Logout
    public string DocumentType { get; set; }
    public string DocumentNumber { get; set; }
    public int? RecordId { get; set; }
    public string OldValue { get; set; }
    public string NewValue { get; set; }
}

/// <summary>§27 — تسجيل الأجهزة المتصلة بالنظام.</summary>
public class ClientMachine : BaseEntity
{
    public string MachineId { get; set; }
    public string MachineName { get; set; }
    public string WindowsUser { get; set; }
    public string ApplicationVersion { get; set; }
    public DateTime? LastLogin { get; set; }
    public DateTime? LastSeen { get; set; }
    public bool IsActive { get; set; } = true;
}

// ═══════════════ §النموذج الهرمي للصلاحيات: مورد ← عملية ← حالة ═══════════════

/// <summary>مورد = شاشة/وحدة وظيفية فعلية (الكود هو كود الوحدة المستخدم في Require).</summary>
public class PermissionResource : BaseEntity
{
    public string Code { get; set; }
    public string NameAr { get; set; }
    public string GroupAr { get; set; }
    public string Path { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortNo { get; set; }
}

/// <summary>عملية = فعل قابل للتنفيذ على المورد؛ الحساسة تُحذَّر عند منحها.</summary>
public class PermissionOperation : BaseEntity
{
    public string Code { get; set; }
    public string NameAr { get; set; }
    public bool IsSensitive { get; set; }
    public int SortNo { get; set; }
}

/// <summary>صلاحية الدور على (مورد × عملية).</summary>
public class RoleResourcePermission : BaseEntity
{
    public int RoleId { get; set; }
    public int ResourceId { get; set; }
    public int OperationId { get; set; }
    public bool IsAllowed { get; set; }
}

/// <summary>استثناءات المستخدم الإضافية — تُدمج فوق صلاحيات أدواره (تضيف أو تلغي).</summary>
public class UserResourcePermission : BaseEntity
{
    public int UserId { get; set; }
    public int ResourceId { get; set; }
    public int OperationId { get; set; }
    public bool IsAllowed { get; set; }
}

/// <summary>سجل تغييرات الصلاحيات — إلحاقي غير قابل للتعديل.</summary>
public class PermissionAuditLog : BaseEntity
{
    public int? ChangedById { get; set; }
    public string ChangedByName { get; set; }
    public int? TargetUserId { get; set; }
    public int? TargetRoleId { get; set; }
    public string ResourceCode { get; set; }
    public string OperationCode { get; set; }
    public string OldValue { get; set; }
    public string NewValue { get; set; }
    public DateTime ChangedAt { get; set; } = DateTime.Now;
    public string ActionType { get; set; } // grant | revoke | copy | deactivate
}

/// <summary>§تفويض زمني: مدير يفوّض صلاحياته مؤقتاً (إجازة/تناوب) — يُدمج في تقييم الصلاحية ضمن الفترة.</summary>
public class Delegation : BaseEntity
{
    public int FromUserId { get; set; }
    public int ToUserId { get; set; }
    public DateTime StartDate { get; set; } = DateTime.Now.Date;
    public DateTime EndDate { get; set; } = DateTime.Now.Date;
    /// <summary>نطاق التفويض: null = كل الوحدات، وإلا كود الوحدة.</summary>
    public string ScopeModule { get; set; }
    public bool IsActive { get; set; } = true;
}
