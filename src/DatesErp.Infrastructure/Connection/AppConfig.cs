using System.Text.Json;

namespace DatesErp.Infrastructure.Connection;

/// <summary>
/// §12/§13 — إعداد الاتصال يُحفظ في مساحة الإصدار المستقلة:
/// %LocalAppData%\MfgSystem\1.50.74\config.json.
/// ولا تُحفظ أي كلمة مرور بنص صريح: كلمات مرور SQL (إن لزم) تُشفَّر بـ DPAPI على ويندوز.
/// </summary>
public class AppConfig
{
    public string Server { get; set; }
    public string Database { get; set; } = "MfgSystemDB";
    public string AuthMode { get; set; } = "Windows"; // Windows | Sql
    public string SqlUid { get; set; }
    public string EncryptedSqlPassword { get; set; }
    /// <summary>استثناء صريح لشهادة تطوير ذاتية التوقيع؛ يظل false للإنتاج وللإعدادات القديمة.</summary>
    public bool TrustServerCertificate { get; set; }
    public string AppVersion { get; set; } = "1.0.0";

    /// <summary>هوية الإصدار الثابتة لمسارات إعداداته وبياناته المحلية.</summary>
    public const string ProductFolderName = "MfgSystem";
    public const string DataVersionFolderName = "1.50.74";
    public const string LocalDatabaseFileName = "mfgsystem_local.db";

    /// <summary>مساحة خاصة بالإصدار 1.50.74، لا تقرأ أو تهاجر إعدادات/قاعدة أي إصدار أقدم.</summary>
    public static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductFolderName, DataVersionFolderName);

    public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");
    public static string LocalDatabasePath => Path.Combine(ConfigDirectory, LocalDatabaseFileName);

    public static bool Exists() => File.Exists(ConfigPath);

    public static AppConfig Load()
    {
        if (!Exists()) return null;
        try { return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)); }
        catch { return null; }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDirectory);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>بناء سلسلة الاتصال — §14 يدعم اسم الخادم أو Instance أو IP.</summary>
    public string BuildSqlServerConnectionString()
    {
        return SqlConnectionSettings.CreateBuilder(Server, Database, AuthMode, SqlUid,
            AuthMode == "Sql" ? Protect.Unprotect(EncryptedSqlPassword) : null,
            TrustServerCertificate).ConnectionString;
    }
}

/// <summary>§13 — تشفير/فك تشفير كلمات المرور عبر DPAPI (ويندوز فقط) — لا نص صريح أبداً.</summary>
public static class Protect
{
    public static string ProtectText(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        if (!OperatingSystem.IsWindows())
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plain)); // بيئة الاختبار فقط
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(plain);
            var enc = System.Security.Cryptography.ProtectedData.Protect(bytes, null,
                System.Security.Cryptography.DataProtectionScope.LocalMachine);
            return Convert.ToBase64String(enc);
        }
        catch { return null; }
    }

    public static string Unprotect(string protectedB64)
    {
        if (string.IsNullOrEmpty(protectedB64)) return null;
        if (!OperatingSystem.IsWindows())
        {
            try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedB64)); }
            catch { return null; }
        }
        try
        {
            var enc = Convert.FromBase64String(protectedB64);
            var dec = System.Security.Cryptography.ProtectedData.Unprotect(enc, null,
                System.Security.Cryptography.DataProtectionScope.LocalMachine);
            return System.Text.Encoding.UTF8.GetString(dec);
        }
        catch { return null; }
    }
}
