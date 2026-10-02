using DatesErp.Core.Interfaces.Services;

namespace DatesErp.Infrastructure.Session;

/// <summary>جلسة المستخدم الحالي — تُعبأ بعد تسجيل الدخول وتُحقن في كل الخدمات.</summary>
public class SessionContext : ICurrentSession
{
    public int UserId { get; set; }
    public string UserName { get; set; }
    public string MachineName { get; set; } = Environment.MachineName;

    public HashSet<string> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<(string module, string action), bool> PermissionCache { get; set; } = new();
    /// <summary>§R2 — طزاجة المصفوفة: Require يعيد بناءها إذا تجاوز عمرها دقيقة.</summary>
    public DateTime CacheBuiltAt { get; set; } = DateTime.MinValue;

    public bool IsInRole(string roleCode) => Roles.Contains(roleCode);

    public bool Can(string module, string action)
        => PermissionCache.TryGetValue((module, action), out var v) && v;
}
