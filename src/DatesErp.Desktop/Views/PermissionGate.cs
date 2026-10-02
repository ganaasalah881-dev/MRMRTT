using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Session;

namespace DatesErp.Desktop.Views;

/// <summary>
/// §12 — البوابة المركزية لفحص الصلاحية في الواجهة.
///
/// كان الفحص مكرراً يدوياً داخل الشاشات (دالة Can خاصة في كل شاشة تقريباً)، فسقط في أغلبها
/// فظهرت أزرار «جديد/تعديل/حذف/اعتماد» لمن لا يملكها ثم يرفضها الخادم برسالة خطأ.
/// الآن: نقطة واحدة يقرأ منها <see cref="ErpToolbar"/> وأي شاشة تحتاج فحصاً موضعياً.
///
/// سياسة الفشل: إن لم تكن الجلسة جاهزة (بدء التشغيل) نسمح — لأن الخادم يفرض الصلاحية على أي حال،
/// والإقفال هنا كان سيمنع بناء الشاشات قبل تسجيل الدخول.
/// </summary>
public static class PermissionGate
{
    public static bool Can(string module, string action)
    {
        if (string.IsNullOrWhiteSpace(module) || string.IsNullOrWhiteSpace(action)) return true;
        try { return AppContainer.Get<SessionContext>().Can(module, action); }
        catch { return true; }
    }

    /// <summary>هل يملك المستخدم أياً من هذه العمليات على الوحدة؟</summary>
    public static bool CanAny(string module, params string[] actions)
    {
        foreach (var a in actions) if (Can(module, a)) return true;
        return false;
    }
}
