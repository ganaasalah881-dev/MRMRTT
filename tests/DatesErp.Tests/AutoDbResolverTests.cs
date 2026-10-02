using System.IO;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §الحل التلقائي للاتصال (v1.50.23): عند فشل الخادم المضبوط يجرب النظام نسخ SQL
/// المحلية المسجّلة ويضبط الصحيحة تلقائياً، ويعرض الوضع المحلي المدمج عند غياب
/// الخادم — بدل رسالة «تعذر الاتصال» العمياء.
/// </summary>
public class AutoDbResolverTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public void Discovery_Is_Guarded_By_Platform_Check_And_Reads_The_Registry()
    {
        // قراءة السجل محمية بفحص المنصة (لا استثناء على لينكس) وتغطي مسارَي 64/32 بت
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src/DatesErp.Desktop/Services/AutoDbResolver.cs"));
        Assert.Matches(@"if \(!OperatingSystem\.IsWindows\(\)\) return names;", src);
        Assert.Contains("Instance Names\\SQL", src);
        Assert.Contains("WOW6432Node", src);
        Assert.Contains("RegistryView.Registry64", src);
        Assert.Contains("RegistryView.Registry32", src);
    }

    [Fact]
    public void Bootstrapper_Uses_The_Auto_Resolver_Before_Building_The_Container()
    {
        string boot = File.ReadAllText(Path.Combine(RepoRoot(), "src/DatesErp.Desktop/Services/Bootstrapper.cs"));
        int resolver = boot.IndexOf("EnsureSqlServerOrFallback", StringComparison.Ordinal);
        int build = boot.IndexOf("AppContainer.Build()", StringComparison.Ordinal);
        Assert.True(resolver >= 0, "الحل التلقائي غير مستدعى من الإقلاع");
        Assert.True(build > resolver, "الحل التلقائي يجب أن يسبق بناء الحاوية");
        Assert.Contains("app.Shutdown()", boot[resolver..(resolver + 700)]);
    }

    [Fact]
    public void Fallback_Is_Opt_In_And_Scoped_To_This_Edition()
    {
        // الوضع المحلي خيار بموافقة المستخدم (نعم/لا) وليس تحوّلاً صامتاً،
        // وقاعدة بياناته في مجلد الهوية المنفصلة MfgSystem حصراً.
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src/DatesErp.Desktop/Services/AutoDbResolver.cs"));
        Assert.Contains("MessageBoxButton.YesNo", src);
        Assert.Contains("هل تريد التشغيل الآن بالوضع المحلي المدمج؟", src);
        Assert.Contains("cfg.AuthMode = \"Local\";", src);
        string container = File.ReadAllText(Path.Combine(RepoRoot(), "src/DatesErp.Desktop/Services/AppContainer.cs"));
        Assert.Contains("AppConfig.LocalDatabasePath", container);
        string config = File.ReadAllText(Path.Combine(RepoRoot(), "src/DatesErp.Infrastructure/Connection/AppConfig.cs"));
        Assert.Contains("LocalDatabaseFileName", config);
        Assert.Contains("MfgSystem", config);
        Assert.Contains("1.50.74", config);
        Assert.DoesNotContain("\"DateERP\"", config);
    }
}
