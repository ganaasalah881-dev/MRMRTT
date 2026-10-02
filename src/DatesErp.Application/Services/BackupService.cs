// §EF1002: أوامر BACKUP/RESTORE/ALTER DATABASE لا تقبل معلمات للمعرّفات والمسارات في T-SQL،
// لذا تُعقَّم يدوياً في SafeDbName() وSafePath() أعلاه. الكبت موثّق ومقصود.
#pragma warning disable EF1002
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §29/§30 — النسخ الاحتياطي والاستعادة من السيرفر نفسه (وليس من الأجهزة):
/// نسخ كامل يومي + تحقق من صلاحية الاستعادة (RESTORE VERIFYONLY) قبل اعتبار النسخة ناجحة.
/// </summary>
public class BackupService : ServiceBase, IBackupService
{
    private readonly string _dbName;

    public BackupService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering, string dbName = null)
        : base(db, session, numbering)
    {
        _dbName = dbName ?? Db.Database.GetDbConnection().Database ?? "MfgSystemDB";
    }

    // ═══ §معالجة EF1002: تعقيم المعرّفات والمسارات قبل الحقن في T-SQL ═══
    // المعرّفات (اسم القاعدة) لا يمكن تمريرها كمعاملات في T-SQL، لذا تُعقَّم:
    // إزالة ] و [ تمنع إغلاق القوس وحقن جملة جديدة.
    private string SafeDbName()
    {
        var n = (_dbName ?? "MfgSystemDB").Replace("]", "").Replace("[", "").Trim();
        if (string.IsNullOrWhiteSpace(n)) n = "MfgSystemDB";
        return n;
    }

    // المسار: تُرفض المحارف الخطرة وتُهرَّب الاقتباسات المفردة.
    private static string SafePath(string path)
    {
        var p = (path ?? "").Trim();
        if (p.IndexOf('\0') >= 0) throw new DatesErp.Core.Exceptions.DomainException("مسار غير صالح.");
        foreach (var bad in new[] { ";", "--", "/*", "*/", "xp_", "EXEC ", "EXECUTE " })
            if (p.IndexOf(bad, StringComparison.OrdinalIgnoreCase) >= 0)
                throw new DatesErp.Core.Exceptions.DomainException($"المسار يحتوي محارف غير مسموحة: {bad}");
        return p.Replace("'", "''");
    }

    public OpResult FullBackup(string folderPath)
    {
        Require("backup", "Post");
        try
        {
            if (!System.IO.Directory.Exists(folderPath))
                System.IO.Directory.CreateDirectory(folderPath);
            var file = System.IO.Path.Combine(folderPath, $"MfgSystemDB_Full_{DateTime.Now:yyyyMMdd_HHmmss}.bak");
            Db.Database.ExecuteSqlRaw($"BACKUP DATABASE [{SafeDbName()}] TO DISK = N'{SafePath(file)}' WITH INIT, COMPRESSION, CHECKSUM, STATS = 10");
            // §30 — لا تُعتبر النسخة ناجحة إلا بعد التحقق من صلاحيتها للاستعادة
            Db.Database.ExecuteSqlRaw($"RESTORE VERIFYONLY FROM DISK = N'{SafePath(file)}'");
            return OpResult.Success($"تم إنشاء نسخة احتياطية كاملة والتحقق منها:\n{file}");
        }
        catch (Exception ex)
        {
            return OpResult.Fail("تعذر إنشاء النسخة الاحتياطية. تأكد من صلاحية الوصول للمجلد على السيرفر.\n(" + ex.Message + ")");
        }
    }

    public OpResult VerifyBackup(string backupFile)
    {
        Require("backup", "View");
        try
        {
            Db.Database.ExecuteSqlRaw($"RESTORE VERIFYONLY FROM DISK = N'{SafePath(backupFile)}'");
            return OpResult.Success("النسخة الاحتياطية صالحة وقابلة للاستعادة.");
        }
        catch (Exception ex)
        {
            return OpResult.Fail("النسخة الاحتياطية غير صالحة!\n(" + ex.Message + ")");
        }
    }

    public OpResult Restore(string backupFile)
    {
        Require("backup", "Post");
        if (!Session.IsInRole("Administrator") && !Session.IsInRole("Management"))
            return OpResult.Fail("الاستعادة صلاحية إدارية عليا فقط.");
        try
        {
            Db.Database.ExecuteSqlRaw($"ALTER DATABASE [{SafeDbName()}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
            Db.Database.ExecuteSqlRaw($"RESTORE DATABASE [{SafeDbName()}] FROM DISK = N'{SafePath(backupFile)}' WITH REPLACE");
            Db.Database.ExecuteSqlRaw($"ALTER DATABASE [{SafeDbName()}] SET MULTI_USER");
            return OpResult.Success("تمت استعادة قاعدة البيانات بنجاح.");
        }
        catch (Exception ex)
        {
            try { Db.Database.ExecuteSqlRaw($"ALTER DATABASE [{SafeDbName()}] SET MULTI_USER"); } catch { }
            return OpResult.Fail("تعذرت الاستعادة.\n(" + ex.Message + ")");
        }
    }

    public List<string> ListBackups(string folderPath)
    {
        try
        {
            return System.IO.Directory.Exists(folderPath)
                ? System.IO.Directory.GetFiles(folderPath, "*.bak").OrderByDescending(f => f).Take(50).ToList()
                : new List<string>();
        }
        catch { return new List<string>(); }
    }
}
