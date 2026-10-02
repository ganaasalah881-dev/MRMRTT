using DatesErp.Infrastructure.Connection;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;
using System.Windows;
namespace DatesErp.Desktop.Services;

/// <summary>
/// §v1.50.23 — حل الاتصال تلقائياً قبل الإقلاع بدل رسالة الفشل العمياء:
/// 1) يجرب الخادم المضبوط في config.json.
/// 2) إن لم يستجب: يكتشف نسخ SQL Server المثبتة محلياً (من السجل) ويجربها واحدة
///    واحدة — وعند النجاح يُعيد ضبط config.json على الخادم الصحيح تلقائياً.
/// 3) إن لم يوجد خادم يعمل: يشرح السبب بدقة (غير مثبت / خدمة متوقفة) ويعرض
///    التشغيل بالوضع المحلي المدمج (SQLite في مجلد بيانات هذه النسخة وحدها).
/// </summary>
public static class AutoDbResolver
{
    /// <summary>أسماء نسخ SQL Server المحلية من سجل ويندوز (مثل .\SQLEXPRESS أو .\SQLEXPRESS01).</summary>
    public static List<string> GetLocalSqlInstanceNames()
    {
        var names = new List<string>();
        if (!OperatingSystem.IsWindows()) return names;
        try
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
            {
                foreach (var path in new[]
                {
                    @"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL",
                    @"SOFTWARE\WOW6432Node\Microsoft\Microsoft SQL Server\Instance Names\SQL"
                })
                using (var key = baseKey.OpenSubKey(path))
                {
                    if (key == null) continue;
                    foreach (var valueName in key.GetValueNames())
                    {
                        var instance = valueName.Trim();
                        if (instance.Length == 0) continue;
                        names.Add(instance.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase)
                            ? "."
                            : @".\" + instance);
                    }
                }
            }
        }
        catch { /* تعذر قراءة السجل — نعتمد الخادم المضبوط فقط */ }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool Probe(string server, string database)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(server)) return false;
        try
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = database,
                IntegratedSecurity = true,
                Encrypt = SqlConnectionEncryptOption.Mandatory,
                TrustServerCertificate = true,
                ConnectTimeout = 4
            };
            using var connection = new SqlConnection(builder.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.ExecuteScalar();
            return true;
        }
        catch { return false; }
    }

    /// <summary>يعيد true إن صار هناك إعداد قابل للتشغيل (خادم SQL صحيح — أو الوضع المحلي بموافقة المستخدم).</summary>
    public static bool EnsureSqlServerOrFallback()
    {
        var cfg = AppConfig.Load();
        if (cfg == null || cfg.AuthMode == "Local") return true;

        // 1) الخادم المضبوط يستجيب؟ لا حاجة لأي شيء.
        if (Probe(cfg.Server, cfg.Database)) return true;

        // 2) اكتشاف نسخ SQL المحلية وتجربتها حتى يجاد واحدة تستجيب — ثم ضبط الإعداد بها.
        var found = GetLocalSqlInstanceNames();
        foreach (var server in found.Where(s => !string.Equals(s, cfg.Server, StringComparison.OrdinalIgnoreCase)))
        {
            if (!Probe(server, cfg.Database)) continue;
            cfg.Server = server;
            cfg.AppVersion = typeof(AutoDbResolver).Assembly.GetName().Version?.ToString(3) ?? cfg.AppVersion;
            cfg.Save();
            ErrorLog.WriteInfo("AutoDbResolver: ضُبط الخادم تلقائياً على " + server);
            return true;
        }

        // 3) لا خادم يعمل — شرح دقيق للسبب ثم عرض الوضع المحلي المدمج.
        var detail = found.Count == 0
            ? "لا يوجد SQL Server مثبت على هذا الجهاز (لم أجد أي نسخة مسجّلة)."
            : "نسخ SQL موجودة لكنها لا تستجيب: " + string.Join(" ، ", found) +
              "\n(غالباً خدمة SQL Server متوقفة — شغّلها من SQL Server Configuration Manager ← Services)";
        var choice = MessageBox.Show(
            detail + "\n\nهل تريد التشغيل الآن بالوضع المحلي المدمج؟ (قاعدة خاصة بهذه النسخة على هذا الجهاز — تُنشأ تلقائياً)",
            "اتصال قاعدة البيانات", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (choice != MessageBoxResult.Yes) return false;
        cfg.AuthMode = "Local";
        cfg.Database = AppConfig.LocalDatabaseFileName;
        cfg.AppVersion = typeof(AutoDbResolver).Assembly.GetName().Version?.ToString(3) ?? cfg.AppVersion;
        cfg.Save();
        ErrorLog.WriteInfo("AutoDbResolver: التحول إلى الوضع المحلي المدمج بموافقة المستخدم.");
        return true;
    }
}
