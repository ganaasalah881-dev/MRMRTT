using DatesErp.Infrastructure.Connection;
using Xunit;
using Microsoft.Data.SqlClient;

namespace DatesErp.Tests;

/// <summary>
/// §إصلاح شاشة إعداد الاتصال (B49):
/// 1) سبب NullReferenceException الجذري: AuthModeChanged كان ينفذ أثناء InitializeComponent
///    قبل إنشاء UidLabel/UidBox/PwdLabel/PwdBox. أُصلح بحارس _ready + OnInitialized + فحوص null.
/// 2) Named Instance (.\SQLEXPRESS01) كان يفشل خطأً في فحص TCP على 1433 لأن Named Instance
///    يستعمل منفذاً ديناميكياً عبر SQL Browser. أُصلح بتجاوز فحص TCP للـ Named Instance.
/// 3) سلسلة الاتصال تُبنى صحيحة لـ Windows و Sql auth.
/// </summary>
public class ConnectionSetupTests
{
    // ── سلسلة الاتصال ──

    [Fact]
    public void ConnectionString_WindowsAuth_UsesIntegratedSecurity()
    {
        var cfg = new AppConfig { Server = @".\SQLEXPRESS01", Database = "DateFactory", AuthMode = "Windows" };
        var cs = cfg.BuildSqlServerConnectionString();
        var parsed = new SqlConnectionStringBuilder(cs);
        Assert.True(parsed.IntegratedSecurity);
        Assert.Empty(parsed.UserID);
        Assert.Equal(@".\SQLEXPRESS01", parsed.DataSource);
        Assert.Equal("DateFactory", parsed.InitialCatalog);
    }

    [Fact]
    public void ConnectionString_SqlAuth_UsesUserIdAndPassword()
    {
        var cfg = new AppConfig
        {
            Server = @".\SQLEXPRESS01",
            Database = "DateFactory",
            AuthMode = "Sql",
            SqlUid = "sa",
            EncryptedSqlPassword = Protect.ProtectText("P@ssw0rd")
        };
        var cs = cfg.BuildSqlServerConnectionString();
        var parsed = new SqlConnectionStringBuilder(cs);
        Assert.Equal("sa", parsed.UserID);
        Assert.False(parsed.IntegratedSecurity);
        Assert.Equal("P@ssw0rd", parsed.Password);
    }

    [Fact]
    public void SqlPassword_Is_Not_Stored_As_Plaintext()
    {
        var cfg = new AppConfig { AuthMode = "Sql", SqlUid = "sa", EncryptedSqlPassword = Protect.ProtectText("Secret123") };
        var json = System.Text.Json.JsonSerializer.Serialize(cfg);
        Assert.DoesNotContain("Secret123", json);   // لا نص صريح في الملف المحفوظ
        Assert.Contains("EncryptedSqlPassword", json);
    }

    // ── Named Instance: تجاوز فحص TCP ──

    [Fact]
    public void NamedInstance_DoesNot_FailOnTcpPreCheck()
    {
        // §.\SQLEXPRESS01 named instance: لا منفذ 1433 ثابت، بل ديناميكي عبر SQL Browser.
        // على لينكس بلا SQL Server سيفشل الاتصال الفعلي، لكن يجب ألا يفشل بفحص TCP
        // برسالة "تعذر الوصول إلى الخادم" — بل برسالة اتصال حقيقية.
        var tester = new ConnectionTester();
        var r = tester.Test(@".\SQLEXPRESS01", "DateFactory", "Windows");
        // Named instance: ServerReachable يُفترض true (تجاوز فحص TCP) — هذا هو المسلك المُختبَر
        Assert.True(r.ServerReachable, "Named Instance يجب أن يتجاوز فحص TCP على 1433");

        // §B106 — حُذف Assert.False(r.ConnectionOk).
        // كان يفترض **غياب SQL Server عن جهاز التشغيل** (كُتب لبيئة لينكس)، فينكسر على
        // أي جهاز ويندوز فيه SQLEXPRESS01 فعلاً — إذ ينجح الاتصال ويصير ConnectionOk=true.
        // نجاح الاتصال أو فشله ليس موضوع هذا الاختبار ولا يخضع لسيطرته؛ موضوعه أن
        // النسخة المسمّاة **لا تسقط في فحص TCP على 1433**. وهذا ما تؤكده الفحوص الباقية:
        // ServerReachable = true، ولا رسالة «تعذر الوصول إلى الخادم» مهما كانت النتيجة.
        Assert.DoesNotContain("تعذر الوصول إلى الخادم", r.Message);
    }

    [Fact]
    public void DefaultInstance_Unreachable_FailsOnTcpPreCheck()
    {
        // Default instance بلا منفذ صريح: فحص TCP على 1433.
        // منفذ مغلق مؤكد على localhost → فشل TCP سريع (connection refused).
        var tester = new ConnectionTester();
        var r = tester.Test("127.0.0.1", "DateFactory", "Windows");
        Assert.False(r.ServerReachable);
        Assert.Contains("تعذر الوصول إلى الخادم", r.Message);
    }

    [Fact]
    public void ExplicitPort_SkipsTcpPreCheck_On1433()
    {
        // منفذ صريح (,9999): نتجاوز فحص TCP الافتراضي على 1433 ونترك الاتصال الفعلي يحسم.
        // ServerReachable يُفترض true لأننا تجاوزنا الفحص، لكن الاتصال الفعلي سيفشل.
        var tester = new ConnectionTester();
        var r = tester.Test("127.0.0.1,9999", "DateFactory", "Windows");
        Assert.True(r.ServerReachable, "منفذ صريح: يُتجاوز فحص TCP الافتراضي على 1433");
        Assert.False(r.ConnectionOk);   // لكن الاتصال الفعلي يفشل (بلا SQL Server)
    }
}
