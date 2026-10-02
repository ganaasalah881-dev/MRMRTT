using System.IO;

namespace DatesErp.Tests;

/// <summary>Guard the mandatory pre-migration SQL Server backup on Express and other editions.</summary>
public class PreMigrationBackupExpressTests
{
    private static string Read(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "DateERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, relativePath));
    }

    [Fact]
    public void PreMigration_Backup_Is_Express_Compatible_And_Still_Gates_Migration()
    {
        string source = Read("src/DatesErp.Desktop/Services/AutoBackup.cs");
        const string signature = "public static bool EnsurePreMigration(DatesErpDbContext db)";
        int methodStart = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "The mandatory pre-migration backup method must exist.");
        string method = source[methodStart..];

        // An exact SQL contract prevents a future unsupported option from entering this backup path.
        string backupCommand = Assert.Single(method.Split('\n'), line =>
            line.Contains("ExecuteSqlRaw($\"BACKUP DATABASE", StringComparison.Ordinal)).Trim();
        Assert.Equal(
            "db.Database.ExecuteSqlRaw($\"BACKUP DATABASE [{dbName}] TO DISK = N'{sqlFile}' WITH INIT, CHECKSUM\");",
            backupCommand);
        Assert.DoesNotContain("COMPRESSION", backupCommand);

        const string verify = "db.Database.ExecuteSqlRaw($\"RESTORE VERIFYONLY FROM DISK = N'{sqlFile}'\");";
        int backup = method.IndexOf(backupCommand, StringComparison.Ordinal);
        int verifyAt = method.IndexOf(verify, StringComparison.Ordinal);
        Assert.True(verifyAt > backup, "The actual backup must precede RESTORE VERIFYONLY.");
        Assert.Contains("فشل إنشاء النسخة الاحتياطية الفعلية قبل الترحيل", method);
        Assert.Contains("return false;", method);

        string bootstrap = Read("src/DatesErp.Desktop/Services/Bootstrapper.cs");
        int gate = bootstrap.IndexOf("if (!AutoBackup.EnsurePreMigration(db))", StringComparison.Ordinal);
        int migration = bootstrap.IndexOf("SchemaMigrator.Migrate(db)", StringComparison.Ordinal);
        Assert.True(gate >= 0 && migration > gate, "Do not migrate before the verified backup gate.");
        string failureBranch = bootstrap[gate..migration];
        Assert.Contains("app.Shutdown();", failureBranch);
        Assert.Contains("return;", failureBranch);
    }
}
