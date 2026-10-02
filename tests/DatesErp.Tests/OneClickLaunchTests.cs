using System.IO;
using DatesErp.Infrastructure.Connection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// Production-install isolation for the MRMRTT 1.50.74 baseline.
/// These checks protect the versioned launcher, user data root, and fresh local database.
/// </summary>
public class OneClickLaunchTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Read(string rel) => File.ReadAllText(Path.Combine(RepoRoot(), rel));

    [Fact]
    public void AppConfig_Uses_A_Version_Scoped_User_Data_And_Local_Database()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MfgSystem", "1.50.74");
        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(AppConfig.ConfigDirectory));
        Assert.Equal(Path.Combine(expected, "config.json"), AppConfig.ConfigPath);
        Assert.Equal(Path.Combine(expected, "mfgsystem_local.db"), AppConfig.LocalDatabasePath);
        Assert.Equal("mfgsystem_local.db", AppConfig.LocalDatabaseFileName);
    }

    [Fact]
    public void Desktop_Writes_And_Remembered_State_Use_Only_This_Release_Data_Root()
    {
        string[] paths =
        {
            "src/DatesErp.Desktop/Services/ErrorLog.cs",
            "src/DatesErp.Desktop/Services/SelfDiagnostic.cs",
            "src/DatesErp.Desktop/Services/BootTrace.cs",
            "src/DatesErp.Desktop/Views/LoginWindow.xaml.cs",
            "src/DatesErp.Desktop/Views/Screens/PlanningView.xaml.cs",
            "src/DatesErp.Desktop/Views/Screens/ReceivingView.xaml.cs",
            "src/DatesErp.Desktop/Services/ScreenStateStore.cs",
            "src/DatesErp.Application/Services/ExecutionCloseTrace.cs",
            "src/DatesErp.Application/Services/AuthService.cs"
        };
        foreach (var path in paths)
        {
            var source = Read(path);
            Assert.Contains("AppConfig.ConfigDirectory", source);
            Assert.DoesNotContain("\"DateERP\"", source);
        }

        string login = Read("src/DatesErp.Desktop/Views/LoginWindow.xaml.cs");
        Assert.DoesNotContain("LegacyRememberPath", login);
        Assert.DoesNotContain("CommonApplicationData", login);
    }

    [Fact]
    public void Backup_Defaults_Are_Scoped_To_This_Release()
    {
        string autoBackup = Read("src/DatesErp.Desktop/Services/AutoBackup.cs");
        Assert.Contains("AppConfig.ProductFolderName", autoBackup);
        Assert.Contains("AppConfig.DataVersionFolderName", autoBackup);
        Assert.DoesNotContain("DateERP_Backups", autoBackup);
        Assert.DoesNotContain("C:\\SQLBackups\\DateERP", autoBackup);

        string backupView = Read("src/DatesErp.Desktop/Views/Screens/BackupView.xaml");
        Assert.Contains("C:\\MfgSystem_1.50.74_Backups", backupView);
        Assert.DoesNotContain("C:\\DateERP_Backups", backupView);
    }

    [Fact]
    public void Installer_Verifies_The_Build_And_Uses_Version_Specific_Paths()
    {
        string bat = Read("Installer/2-تنصيب.bat");
        Assert.Contains("APPVERSION=1.50.74", bat);
        Assert.Contains("VERSION.txt", bat);
        Assert.Contains("SHA256.txt", bat);
        Assert.Contains("Get-FileHash", bat);
        Assert.Contains("COPIED_SHA", bat);
        Assert.Contains("%ProgramFiles%\\%APPNAME%\\%APPVERSION%", bat);
        Assert.Contains("%LocalAppData%\\Programs\\%APPNAME%\\%APPVERSION%", bat);
        Assert.Contains("%LocalAppData%\\%APPNAME%\\%APPVERSION%", bat);
        Assert.Contains("MFGSYSTEM_INSTALL_DIR", bat);
        Assert.Contains("MFGSYSTEM_PACKAGE_DIR", bat);
        Assert.Contains("StartsWith($dst", bat);
        Assert.Contains("تشغيل_1.50.74.bat", bat);
        Assert.DoesNotContain("config.backup", bat);
        Assert.DoesNotContain("DateERP_Publish", bat);
        Assert.DoesNotContain("C:\\DateERP", bat);
    }

    [Fact]
    public void Launcher_Refuses_To_Run_A_Mismatched_Or_Unversioned_Executable()
    {
        string launcher = Read("Installer/تشغيل_1.50.74.bat");
        Assert.Contains("VERSION.txt", launcher);
        Assert.Contains("APPVERSION=1.50.74", launcher);
        Assert.Contains("MfgSystem.exe", launcher);
        Assert.Contains("INSTALLED_VERSION", launcher);
        Assert.Contains("start \"\" /D", launcher);
    }

    [Fact]
    public void Uninstaller_Only_Removes_This_Version_And_Leaves_Data_By_Default()
    {
        string bat = Read("Installer/إلغاء_التنصيب.bat");
        Assert.Contains("APPVERSION=1.50.74", bat);
        Assert.Contains("%APPNAME%\\%APPVERSION%", bat);
        Assert.Contains("تبقى افتراضياً", bat);
        Assert.Contains("/d K", bat);
        Assert.Contains("MFGSYSTEM_INSTALL_DIR", bat);
        Assert.Contains("/KEEP-DATA", bat);
        Assert.DoesNotContain("DateERP_Publish", bat);
        Assert.DoesNotContain("C:\\DateERP", bat);
    }

    [Fact]
    public void First_Launch_Creates_The_Version_Scoped_Local_Database_And_Schema()
    {
        string boot = Read("src/DatesErp.Desktop/Services/Bootstrapper.cs");
        Assert.Contains("db.Database.EnsureCreated();", boot);
        Assert.Contains("DbSeeder.Seed(db);", boot);
        Assert.Contains("SchemaMigrator.Migrate(db)", boot);
        Assert.Contains("AuthMode = \"Local\"", boot);
        Assert.Contains("AppConfig.LocalDatabaseFileName", boot);

        string container = Read("src/DatesErp.Desktop/Services/AppContainer.cs");
        Assert.Contains("AppConfig.LocalDatabasePath", container);
        Assert.DoesNotContain("DateERP_Publish", container);
        Assert.DoesNotContain("C:\\DateERP", container);
    }
}
