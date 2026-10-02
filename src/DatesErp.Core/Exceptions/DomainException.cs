namespace DatesErp.Core.Exceptions;

/// <summary>
/// استثناء عمل (قاعدة تشغيلية) — رسالته عربية جاهزة للعرض على المستخدم مباشرة.
/// §8 القيود التشغيلية، §7 سير العمل، §21 انقطاع الشبكة.
/// </summary>
public class DomainException : Exception
{
    public string Code { get; }

    public DomainException(string arabicMessage, string code = "BIZ") : base(arabicMessage)
    {
        Code = code;
    }
}

/// <summary>§5 — استثناء تعارض تعديل متزامن بين مستخدمين.</summary>
public class ConcurrencyConflictException : DomainException
{
    public ConcurrencyConflictException()
        : base("تم تعديل هذا السجل بواسطة مستخدم آخر.\nيرجى إعادة تحميل البيانات والمحاولة من جديد.", "CONCURRENCY")
    {
    }
}

/// <summary>§21 — انقطاع الاتصال بالخادم أثناء عملية حساسة: لا حفظ جزئي أبداً.</summary>
public class ServerUnavailableException : DomainException
{
    public ServerUnavailableException()
        : base("تعذر الاتصال بالخادم.\nلم يتم حفظ العملية.", "NETWORK")
    {
    }
}

/// <summary>صلاحية مرفوضة.</summary>
public class PermissionDeniedException : DomainException
{
    public PermissionDeniedException(string what)
        : base($"لا تملك صلاحية: {what}", "PERMISSION")
    {
    }
}
