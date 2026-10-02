using System.Text.Json;

namespace DatesErp.Desktop.Services;

/// <summary>§45 — حالة الجلسة لكل مستخدم على هذا الجهاز (آخر شاشة مفتوحة) في ملف محلي مستقل.</summary>
public static class ScreenStateStore
{
    private static string Path => System.IO.Path.Combine(
        DatesErp.Infrastructure.Connection.AppConfig.ConfigDirectory, "screen_state.json");

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (System.IO.File.Exists(Path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(Path)) ?? new();
        }
        catch { /* ملف تالف ≠ عطل نظام */ }
        return new();
    }

    public static string Get(string key) => Load().TryGetValue(key, out var v) ? v : null;

    public static void Set(string key, string value)
    {
        try
        {
            var d = Load();
            d[key] = value;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            System.IO.File.WriteAllText(Path, JsonSerializer.Serialize(d));
        }
        catch { /* الحالة ترف لا يُفشل التشغيل */ }
    }
}
