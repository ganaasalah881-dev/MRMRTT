namespace DatesErp.Desktop.Services;

/// <summary>
/// ختم البناء: يظهر في عنوان النافذة (والنافذة الرئيسية وشاشة الدخول) لنتمكن من
/// معرفة أي بناء يعمل فعلاً على جهاز المستخدم (حل إشكالية «التحديث لا يصل»).
/// §1.50.71: يُقرأ تلقائياً من إصدار التجميع (csproj) — لا يُحدَّث يدوياً بعد الآن
/// فلا يعود ينزاح عن النسخة الفعلية (كان مثبّتاً على 1.50.37 بينما النظام 1.50.71).
/// </summary>
public static class BuildInfo
{
    public static string Stamp
        => typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}
