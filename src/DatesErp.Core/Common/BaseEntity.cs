namespace DatesErp.Core.Common;

/// <summary>كيان أساسي مع معرف رقمي.</summary>
public abstract class BaseEntity
{
    public int Id { get; set; }

    /// <summary>
    /// §5/§45 — Optimistic Concurrency لكل الكيانات: يُربط في SQL Server بعمود rowversion.
    /// أي تعديل متزامن على نفس السجل يُطلق DbUpdateConcurrencyException
    /// وتُعرض رسالة «تم تعديل هذا السجل بواسطة مستخدم آخر».
    /// </summary>
    public byte[] RowVersion { get; set; }
}

/// <summary>
/// §5/§26 — كيان قابل للتدقيق مع طابع زمني ومنشئ + نسخة صف للتزامن التفاؤلي.
/// </summary>
public abstract class AuditableEntity : BaseEntity
{
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public int? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public int? ModifiedBy { get; set; }
}

/// <summary>مستند سير عمل: حالة + اعتماد + رقم مستند.</summary>
public abstract class WorkflowDocument : AuditableEntity
{
    public string DocumentNumber { get; set; }
    public string Status { get; set; } = DocStatuses.Draft;
    public bool IsApproved { get; set; }
    public int? ApprovedBy { get; set; }
    public DateTime? ApprovedDate { get; set; }
    public bool IsPosted { get; set; }
    public DateTime? PostedDate { get; set; }
    public string Notes { get; set; }
    /// <summary>§B102 (سحب B97/B98): سبب الحالة — لافتة الإرجاع/التوقف تُعرض في مركز المهام ونوافذ التفاصيل، وتُمسح عند الاعتماد/الاستئناف.</summary>
    public string StatusReason { get; set; }
}

/// <summary>حالات المستندات الموحدة في النظام.</summary>
public static class DocStatuses
{
    public const string Draft = "Draft";
    public const string Submitted = "Submitted";
    public const string Approved = "Approved";
    public const string Issued = "Issued";
    /// <summary>§أوامر الإنتاج: معتمد وله تاريخ ووردية — جاهز للبدء في موعده.</summary>
    public const string Scheduled = "Scheduled";
    public const string InProgress = "InProgress";
    /// <summary>§أوامر الإنتاج: توقف مؤقت أثناء التنفيذ (عطل/نقص مواد...) — يُستأنف.</summary>
    public const string Stopped = "Stopped";
    public const string Completed = "Completed";
    public const string Closed = "Closed";
    public const string Cancelled = "Cancelled";
    /// <summary>§B85/M9: أُنتج واستُلم تاماً لكن لم يُسلَّم للعميل بعد (كانت قيمة حرة تظهر إنجليزية).</summary>
    public const string PendingDelivery = "PendingDelivery";

    public static string ToArabic(string status) => status switch
    {
        Draft => "مسودة",
        Submitted => "مُرسل",
        Approved => "معتمد",
        Issued => "مُصدر",
        Scheduled => "مجدول",
        InProgress => "قيد التنفيذ",
        Stopped => "متوقف",
        Completed => "مكتمل",
        Closed => "مُغلق",
        Cancelled => "ملغي",
        PendingDelivery => "بانتظار التسليم",
        _ => status ?? "-"
    };
}
