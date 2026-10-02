using DatesErp.Core.Interfaces.Services;
using Microsoft.Data.SqlClient;
using System.Net.Sockets;

namespace DatesErp.Infrastructure.Connection;

/// <summary>
/// §12 — اختبار الاتصال خطوة بخطوة مع نتيجة تفصيلية:
/// Server ✓ / SQL Server ✓ / Database ✓ / Login ✓ / Connection ✓
/// </summary>
public class ConnectionTester
{
    public ConnectionTestResult Test(string server, string database, string authMode, string uid = null, string password = null, bool trustServerCertificate = false)
    {
        var r = new ConnectionTestResult();
        try
        {
            // التحقق والتركيب قبل أي محاولة اتصال، دون دمج القيم كنص إعدادات.
            var builder = SqlConnectionSettings.CreateBuilder(server, database, authMode,
                uid, password, trustServerCertificate, timeout: 6);
            var cs = builder.ConnectionString;

            // 1) الخادم قابلاً للوصول.
            // §Named Instance (مثل .\SQLEXPRESS01) يستعمل منفذاً ديناميكياً يحلّه SQL Browser
            // عبر UDP 1434، لا المنفذ 1433 الثابت. لذا فحص TCP على 1433 يُفشل الاتصال السليم
            // خطأً. لذلك: نفحص TCP فقط لـ Default Instance بلا منفذ صريح؛ أما Named Instance
            // أو منفذ صريح فنتجاوز فحص TCP ونترك SqlConnection (الذي يستعمل SQL Browser) يحسم.
            var host = server.Split('\\')[0].Split(',')[0];
            bool isNamedInstance = server.Contains('\\');
            bool hasExplicitPort = server.Contains(',');
            int port = 1433;
            if (hasExplicitPort) int.TryParse(server.Split(',')[1], out port);

            if (!isNamedInstance && !hasExplicitPort)
            {
                try
                {
                    using var tcp = new TcpClient();
                    r.ServerReachable = tcp.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(4));
                }
                catch { r.ServerReachable = false; }
                if (!r.ServerReachable)
                {
                    r.Message = $"تعذر الوصول إلى الخادم {host}:{port}. تأكد من اتصال الشبكة وجدار الحماية (§15: منفذ TCP 1433).";
                    return r;
                }
            }
            else
            {
                // Named Instance أو منفذ صريح: نفترض الوصول ونترك الاتصال الفعلي يحسم
                r.ServerReachable = true;
            }

            // 2) بناء سلسلة الاتصال وفتحها — يغطي: استجابة SQL Server + صحة الدخول
            // غيّر القاعدة كخاصية فقط: قد تحتوي كلمة المرور نفسها على Database=... .
            builder.InitialCatalog = "master";
            using var connNoDb = new SqlConnection(builder.ConnectionString);
            connNoDb.Open();
            r.SqlServerResponding = true;
            r.LoginOk = true;
            r.ServerVersion = connNoDb.ServerVersion;

            // 3) وجود قاعدة البيانات
            using (var cmd = new SqlCommand("SELECT COUNT(*) FROM sys.databases WHERE name=@db", connNoDb))
            {
                cmd.Parameters.AddWithValue("@db", database);
                r.DatabaseExists = (int)cmd.ExecuteScalar() > 0;
            }
            if (!r.DatabaseExists)
            {
                r.Message = $"قاعدة البيانات {database} غير موجودة على الخادم. شغّل مثبّت السيرفر DateERP_Server_Setup لإنشائها.";
                return r;
            }

            // 4) اتصال فعلي بقاعدة البيانات نفسها
            using var conn = new SqlConnection(cs);
            conn.Open();
            using var cmd2 = new SqlCommand("SELECT 1", conn);
            r.ConnectionOk = Equals(cmd2.ExecuteScalar(), 1);
            r.Message = r.AllOk ? "الاتصال ناجح." : "اكتمل الفحص مع ملاحظات.";
        }
        catch (SqlException ex) when (ex.Number == 18456)
        {
            r.Message = "فشل تسجيل الدخول: اسم المستخدم أو كلمة المرور غير صحيحة.";
        }
        catch (Exception ex)
        {
            r.Message = "تعذر الاتصال بخادم قاعدة البيانات.\nتأكد من اتصال الشبكة وتشغيل الخادم.\n(" + ex.Message + ")";
        }
        return r;
    }
}
