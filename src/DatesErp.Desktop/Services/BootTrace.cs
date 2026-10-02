using System;
using System.IO;
using DatesErp.Infrastructure.Connection;

namespace DatesErp.Desktop.Services;

/// <summary>
/// أثر الإقلاع يُكتب في مساحة بيانات الإصدار القابلة للكتابة، لا بجانب الملف التنفيذي
/// (قد يكون تحت Program Files). يبدأ قبل واجهة WPF لتسجيل تعثرات بدء التشغيل المبكرة.
/// </summary>
public static class BootTrace
{
    private static readonly object _lock = new();
    private static string _path;

    private static string Path
    {
        get
        {
            if (_path == null)
            {
                try { _path = System.IO.Path.Combine(AppConfig.ConfigDirectory, "logs", "boot_trace.txt"); }
                catch
                {
                    _path = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        AppConfig.ProductFolderName, AppConfig.DataVersionFolderName, "logs", "boot_trace.txt");
                }
            }
            return _path;
        }
    }

    public static void Step(string message)
    {
        try
        {
            lock (_lock)
            {
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(Path, $"[{DateTime.Now:dd/MM/yyyy HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch { /* لا نفشل أبداً بسبب فشل التسجيل نفسه */ }
    }

    public static void Fail(string stage, Exception ex)
    {
        try
        {
            lock (_lock)
            {
                var txt = $"[{DateTime.Now:dd/MM/yyyy HH:mm:ss.fff}] فشل في مرحلة [{stage}]: " +
                          (ex?.GetType().Name ?? "?") + ": " + (ex?.Message ?? "?") + Environment.NewLine +
                          (ex?.InnerException != null ? "   السبب الداخلي: " + ex.InnerException.Message + Environment.NewLine : "") +
                          (ex?.StackTrace ?? "") + Environment.NewLine +
                          new string('-', 70) + Environment.NewLine;
                var directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(Path, txt);
            }
        }
        catch { }
    }

    /// <summary>مسار الملف — لعرضه للمستخدم.</summary>
    public static string FilePath => Path;
}
