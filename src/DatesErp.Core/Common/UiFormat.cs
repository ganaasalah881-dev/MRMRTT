using System.Globalization;

namespace DatesErp.Core.Common;

/// <summary>
/// §معيار الواجهات الموحد — التنسيقات المركزية لكل النظام:
/// • الأرقام دائماً إنجليزية (0-9) في كل الشاشات والجداول والتقارير.
/// • التاريخ بصيغة موحدة 28/08/2026 والوقت 14:30 في الإدخال والبحث والطباعة.
/// • ألوان الحالات موحدة (مسودة/معتمد/مجدول/قيد التنفيذ/متوقف/مكتمل/مغلق/ملغي).
/// كل شاشة تستخدم هذه الدوال — ممنوع تنسيق يدوي مختلف.
/// </summary>
public static class UiFormat
{
    public const string DatePattern = "dd/MM/yyyy";
    public const string TimePattern = "HH:mm";
    public const string DateTimePattern = "dd/MM/yyyy HH:mm";
    public const string DateTimeSecPattern = "dd/MM/yyyy HH:mm:ss";

    /// <summary>ثقافة محايدة لضمان الأرقام الإنجليزية دائماً مهما كانت لغة الجهاز.</summary>
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    /// <summary>ثقافة التحليل: تفهم 28/08/2026 و2026-08-28 معاً (المرونة في الإدخال، التوحيد في الإخراج).</summary>
    private static readonly CultureInfo ParseCulture = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>تحليل تاريخ من الواجهات/الاختبارات — يقبل الصيغة الموحدة 28/08/2026 وصيغة ISO معاً.</summary>
    public static bool TryParseDate(string s, out DateTime d)
    {
        if (!string.IsNullOrWhiteSpace(s))
        {
            if (DateTime.TryParse(s, ParseCulture, DateTimeStyles.None, out d)) return true;
            if (DateTime.TryParse(s, Inv, DateTimeStyles.None, out d)) return true;
        }
        d = default;
        return false;
    }

    /// <summary>تاريخ موحد 28/08/2026 — أو «-» إن لم يوجد.</summary>
    public static string D(DateTime? d) => d?.ToString(DatePattern, Inv) ?? "-";
    public static string D(DateTime d) => d.ToString(DatePattern, Inv);

    /// <summary>تاريخ ووقت موحدان 28/08/2026 14:30.</summary>
    public static string DT(DateTime? d) => d?.ToString(DateTimePattern, Inv) ?? "-";
    public static string DT(DateTime d) => d.ToString(DateTimePattern, Inv);

    /// <summary>وقت موحد 14:30.</summary>
    public static string T(DateTime? d) => d?.ToString(TimePattern, Inv) ?? "-";

    /// <summary>رقم بأرقام إنجليزية وفاصل آلاف (10,025.5).</summary>
    public static string N(double v, int decimals = 1) => v.ToString("N" + decimals, Inv);
    public static string N0(double v) => v.ToString("N0", Inv);
    public static string N(long v) => v.ToString("N0", Inv);

    /// <summary>نسبة مئوية 85%.</summary>
    public static string Pct(double pct) => $"{pct.ToString("N0", Inv)}%";

    /// <summary>هل النص يحتوي أرقاماً عربية هندية؟ (فحص الجودة للواجهات).</summary>
    public static bool ContainsArabicDigits(string s)
        => s != null && s.Any(c => c >= '٠' && c <= '٩');

    /// <summary>لون الحالة الموحد (Hex) — نفس اللون في كل الشاشات والتقارير.</summary>
    public static string StatusHex(string status) => status switch
    {
        DocStatuses.Draft => "#9E9E9E",
        DocStatuses.Submitted => "#78909C",
        DocStatuses.Approved => "#1565C0",
        DocStatuses.Issued => "#0277BD",
        DocStatuses.Scheduled => "#00838F",
        DocStatuses.InProgress => "#EF6C00",
        DocStatuses.Stopped => "#C62828",
        DocStatuses.Completed => "#2E7D32",
        DocStatuses.Closed => "#1B5E20",
        DocStatuses.Cancelled => "#757575",
        _ => "#9E9E9E"
    };

    /// <summary>اسم الحالة العربي الموحد.</summary>
    public static string StatusAr(string status) => DocStatuses.ToArabic(status);

    // ═══ رسائل النظام الموحدة (§24) ═══
    public const string MsgSaved = "تم حفظ المستند بنجاح.";
    public const string MsgSaveFailed = "تعذر حفظ المستند. يرجى مراجعة البيانات المطلوبة.";
    public const string MsgSelectFirst = "اختر مستنداً من نتائج البحث أولاً.";
    public const string MsgNoPermission = "لا تملك صلاحية تنفيذ هذه العملية.";
    public const string MsgLocked = "المستند مقفل — لا يمكن التعديل في حالته الحالية.";
}
