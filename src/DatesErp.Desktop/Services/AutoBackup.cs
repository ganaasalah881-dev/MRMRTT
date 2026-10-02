using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Connection;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Services;

/// <summary>
/// §44 — النسخ الاحتياطي التلقائي: مرة واحدة كل يوم عند أول إقلاع بعد الدخول،
/// ونسخة إجبارية قبل أي ترحيل مخطط. لا يعتمد على انضباط المستخدم اليدوي.
/// </summary>
public static class AutoBackup
{
    public const string KeyLast = "LastAutoBackupDate";
    public const string KeyFolder = "BackupFolder";
    private const string BackupScope = AppConfig.ProductFolderName + "_" + AppConfig.DataVersionFolderName;

    public static string DefaultFolder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), BackupScope, "Backups");

    // مجلدات الإصدار الجديدة منفصلة عن مسارات نسخ DateERP القديمة.
    private static readonly string[] FallbackFolders = new[]
    {
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), BackupScope, "Backups"),
        @"C:\SQLBackups\MfgSystem_1.50.74",
        @"C:\Temp\MfgSystem_1.50.74\Backups",
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), BackupScope, "Backups"),
        DefaultFolder
    };

    private static string GetSqlServerDefaultBackupPath(DatesErpDbContext db)
    {
        try
        {
            // محاولة قراءة مسار النسخ الافتراضي من SQL Server
            var conn = db.Database.GetDbConnection();
            bool wasClosed = conn.State == System.Data.ConnectionState.Closed;
            if (wasClosed) conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT SERVERPROPERTY('InstanceDefaultBackupPath') as p";
            var result = cmd.ExecuteScalar() as string;
            if (wasClosed) conn.Close();
            if (!string.IsNullOrWhiteSpace(result)) return result;
        }
        catch { }
        return null;
    }

    private static string ResolveWritableBackupFolder(DatesErpDbContext db, string preferred = null)
    {
        var candidates = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrWhiteSpace(preferred)) candidates.Add(preferred);
        string sqlDefault = GetSqlServerDefaultBackupPath(db);
        if (!string.IsNullOrWhiteSpace(sqlDefault)) candidates.Add(System.IO.Path.Combine(sqlDefault, BackupScope));
        candidates.AddRange(FallbackFolders);

        foreach (var folder in candidates.Distinct())
        {
            try
            {
                System.IO.Directory.CreateDirectory(folder);
                // اختبار كتابة سريعة
                string testFile = System.IO.Path.Combine(folder, $"_test_write_{Guid.NewGuid():N}.tmp");
                System.IO.File.WriteAllText(testFile, "test");
                System.IO.File.Delete(testFile);
                return folder;
            }
            catch { continue; }
        }
        return DefaultFolder;
    }

    /// <summary>يُستدعى مرة واحدة عند تحميل النافذة الرئيسية (بعد الدخول وتوفّر الجلسة).</summary>
    public static void RunDaily()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            if (!db.Database.IsSqlServer()) return;
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            // §1.50.67 FIX EF1002: إضافة OrderBy لتجنب تحذير FirstOrDefault بدون ترتيب
            string last = db.SystemSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefault(s => s.SettingKey == KeyLast)?.SettingValue;
            if (last == today) return;

            string folderSetting = db.SystemSettings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefault(s => s.SettingKey == KeyFolder)?.SettingValue;
            string folder = string.IsNullOrWhiteSpace(folderSetting) ? ResolveWritableBackupFolder(db) : folderSetting;

            var backup = scope.ServiceProvider.GetRequiredService<IBackupService>();
            var res = backup.FullBackup(folder);
            if (res.Ok)
            {
                scope.ServiceProvider.GetRequiredService<ISystemSettingsService>().Set(KeyLast, today);
                UiToast.Show("تم النسخ الاحتياطي اليومي تلقائياً والتحقق منه في:\n" + folder);
            }
            else
            {
                ErrorLog.WriteInfo("AutoBackup: تعذّر النسخ اليومي — " + res.Message);
                UiToast.Show("تعذّر النسخ الاحتياطي اليومي التلقائي.\n" + res.Message, "warn");
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "AutoBackup.RunDaily");
        }
    }

    /// <summary>نسخة موثّقة قبل الترحيل؛ لا تشغيل ولا ترحيل عند فشل التحقق من النسخة المركزية.</summary>
    public static bool EnsurePreMigration(DatesErpDbContext db)
    {
        string attemptedFile = null;
        bool backupCreated = false;
        try
        {
            if (!db.Database.IsSqlServer()) return true;
            string folder = ResolveWritableBackupFolder(db, DefaultFolder);
            System.IO.Directory.CreateDirectory(folder);
            // Multiple desktops may start in the same second. NEVER overwrite
            // another client's backup; verify this exact unique file on SQL Server.
            string file = System.IO.Path.Combine(folder,
                $"{AppConfig.ProductFolderName}_{AppConfig.DataVersionFolderName}_PreMigration_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.bak");
            attemptedFile = file;
            string dbName = db.Database.GetDbConnection().Database.Replace("]", "]]", StringComparison.Ordinal);
            string sqlFile = file.Replace("'", "''", StringComparison.Ordinal);
            // BACKUP/RESTORE require a database identifier and DISK target in SQL syntax,
            // so these two statements cannot parameterize them. Escape the closing bracket
            // and apostrophe above before composing the command text.
#pragma warning disable EF1002
            db.Database.ExecuteSqlRaw($"BACKUP DATABASE [{dbName}] TO DISK = N'{sqlFile}' WITH INIT, CHECKSUM");
            backupCreated = true;
            db.Database.ExecuteSqlRaw($"RESTORE VERIFYONLY FROM DISK = N'{sqlFile}'");
#pragma warning restore EF1002
            ErrorLog.WriteInfo("AutoBackup: نسخة ما قبل الترحيل جاهزة — " + file);
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, $"AutoBackup.PreMigration — file={attemptedFile}");
            string stage = backupCreated
                ? "فشل التحقق من النسخة الاحتياطية قبل الترحيل (RESTORE VERIFYONLY)"
                : "فشل إنشاء النسخة الاحتياطية الفعلية قبل الترحيل (BACKUP DATABASE)";
            string msg = $"{stage}.\nملف النسخة: {attemptedFile ?? "لم يُحدّد"}\nالسبب: {ex.Message}";
            if (ex.Message.Contains("Access is denied") || ex.Message.Contains("Operating system error 5"))
                msg += "\nتحقّق من صلاحيات كتابة خدمة SQL Server في مجلد النسخ الاحتياطي على الخادم (خطأ 5).";
            System.Windows.MessageBox.Show(
                msg + "\n\nلن يبدأ النظام أو يغيّر المخطط قبل نجاح النسخ والتحقق. راجع السجل ثم أعد المحاولة.",
                "نسخ احتياطي قبل الترحيل", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return false;
        }
    }

}
