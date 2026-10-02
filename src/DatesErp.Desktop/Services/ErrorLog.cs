using System.IO;
using DatesErp.Infrastructure.Connection;

namespace DatesErp.Desktop.Services;

/// <summary>
/// §28 — سجل الأخطاء: التفاصيل الفنية تُكتب في ملف سجل فقط ولا تُعرض للمستخدم.
/// الموقع: مجلد بيانات الإصدار 1.50.74 تحت %LocalAppData%\MfgSystem\1.50.74\logs.
/// </summary>
public static class ErrorLog
{
    private static readonly object _lock = new();

    public static string LogDirectory => Path.Combine(AppConfig.ConfigDirectory, "logs");

    public static void Write(Exception ex, string source)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(LogDirectory);
                var line = $"[{DateTime.Now:dd/MM/yyyy HH:mm:ss}] [{source}] {ex?.GetType().Name}: {ex?.Message}\n{ex?.StackTrace}\n{new string('-', 80)}\n";
                File.AppendAllText(Path.Combine(LogDirectory, "errors.log"), line);
            }
        }
        catch
        {
            // لا نفشل أبداً بسبب فشل التسجيل نفسه
        }
    }

    public static void WriteInfo(string message)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(Path.Combine(LogDirectory, "app.log"),
                    $"[{DateTime.Now:dd/MM/yyyy HH:mm:ss}] {message}\n");
            }
        }
        catch { }
    }
}
