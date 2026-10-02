using DatesErp.Core.Common;
using DatesErp.Core.Domain.Enums;

namespace DatesErp.Core.Domain.Entities;

/// <summary>
/// §3 — المهمة: الكيان المحوري الوحيد الجديد في طبقة التوجيه.
///
/// ### المبدأ
/// **المهمة مؤشِّر لا نسخة.** تشير إلى المستند الأصلي (<see cref="DocumentType"/> +
/// <see cref="DocumentId"/>) ولا تنسخ بياناته — فلا تكرار إدخال ولا تعارض بيانات.
/// ما يُخزَّن هنا هو ما يلزم لعرض البطاقة والتوجيه فقط.
///
/// ### التوجيه
/// المهمة تحمل <see cref="RequiredCapability"/> — **قدرة، لا اسم دور ولا معرّف موظف**.
/// من يملك القدرة (بأدواره أو استثناءاته أو تفويض سارٍ) يراها. الهيكل الإداري يتغير
/// كما يشاء والمهمة تصل دائماً، بصفر تعديل برمجي.
/// <see cref="AssignedUserId"/> استثناء للإحالة الصريحة فقط، وليس المسار الطبيعي.
/// </summary>
public class WorkflowTask : AuditableEntity
{
    /// <summary>رقم مهمة متسلسل — للتدقيق والطباعة والإحالة الشفهية.</summary>
    public string TaskNumber { get; set; }

    /// <summary>المرحلة — تُستخدم الـ enum الموجود أصلاً، لا enum جديد.</summary>
    public WorkflowStage Stage { get; set; }

    /// <summary>نوع المهمة — انظر <see cref="WorkflowTaskTypes"/>.</summary>
    public string TaskType { get; set; }

    /// <summary>نوع المستند الأصلي — انظر <see cref="WorkflowDocTypes"/>.</summary>
    public string DocumentType { get; set; }

    /// <summary>مفتاح المستند الأصلي. المهمة تشير إليه ولا تخزّن بياناته.</summary>
    public int DocumentId { get; set; }

    /// <summary>لقطة رقم المستند — للعرض السريع في البطاقة بلا Join.</summary>
    public string DocumentNumber { get; set; }

    /// <summary>بند الخطة، لمهام اليوم الواحد من خطة طويلة.</summary>
    public int? PlanItemId { get; set; }

    /// <summary>اليوم الذي تخص المهمة تشغيله — جوهر الخطة الطويلة يوماً بيوم.</summary>
    public DateTime? BusinessDate { get; set; }

    /// <summary>
    /// **جوهر التوجيه:** القدرة المطلوبة لتنفيذ هذه المهمة (`مورد.عملية`).
    /// لا اسم دور ولا مسمى وظيفي — §2.
    /// </summary>
    public string RequiredCapability { get; set; }

    /// <summary>من «التقطها» ليعمل عليها — اختياري، يمنع ازدواج العمل.</summary>
    public int? ClaimedByUserId { get; set; }

    /// <summary>شخص محدد — **فقط** عند إحالة صريحة بـ `tasks.reassign`.</summary>
    public int? AssignedUserId { get; set; }

    /// <summary>«من أرسلها لي» — المستخدم الذي أطلق الحدث المولِّد.</summary>
    public int? FromUserId { get; set; }

    /// <summary>القدرة التي نُفِّذت فأنتجت هذه المهمة — للتتبع.</summary>
    public string FromCapability { get; set; }

    public string Title { get; set; }

    /// <summary>لقطة ملخص للعرض في البطاقة بلا استعلامات إضافية.</summary>
    public string SummaryJson { get; set; }

    /// <summary>الحالة — انظر <see cref="WorkflowTaskStates"/>.</summary>
    public string State { get; set; } = WorkflowTaskStates.Open;

    /// <summary>1 عاجل · 2 عادي · 3 مؤجل.</summary>
    public int Priority { get; set; } = WorkflowTaskPriority.Normal;

    public DateTime? DueDate { get; set; }

    /// <summary>محسوب — لا يُخزَّن: مستحقة ومضى موعدها وما زالت مفتوحة.</summary>
    public bool IsOverdue =>
        DueDate.HasValue && DueDate.Value.Date < DateTime.Now.Date && WorkflowTaskStates.IsLive(State);

    public DateTime? OpenedDate { get; set; }
    public DateTime? ActedDate { get; set; }
    public int? ActedByUserId { get; set; }

    /// <summary>نتيجة الإجراء: Approved · Rejected · Returned · Executed · Received …</summary>
    public string ActionResult { get; set; }

    /// <summary>ملاحظات — **إلزامية عند الرفض والإرجاع**.</summary>
    public string ActionNotes { get; set; }

    /// <summary>ربط مهمة اليوم بمهمة الخطة الأم.</summary>
    public int? ParentTaskId { get; set; }

    /// <summary>
    /// **مفتاح منع التكرار** — فريد على مستوى قاعدة البيانات (§12).
    /// يُبنى من (نوع المستند + معرّفه + نوع المهمة + يوم العمل)، فيستحيل توليد
    /// مهمتين لنفس الحدث مهما تكرر النقر أو أُعيد تشغيل الترحيل.
    /// </summary>
    public string CorrelationKey { get; set; }
}

/// <summary>§3-2 — الخط الزمني المقروء للمهمة: سطر لكل تغيير حالة.</summary>
/// <remarks>
/// لا يستبدل <see cref="AuditLog"/>: ذاك السجل القانوني للنظام كله، وهذا ما يُعرض
/// للمستخدم داخل نافذة المهمة.
/// </remarks>
public class WorkflowTaskHistory : BaseEntity
{
    public int TaskId { get; set; }

    /// <summary>
    /// مرجع الملاحة. ضروري لا تجميلي: أول سطر تاريخ يُكتب **قبل حفظ المهمة** (داخل معاملة
    /// المستند المولِّد)، فلا يوجد مفتاح بعد. ضبط هذا المرجع يجعل EF يملأ <see cref="TaskId"/>
    /// تلقائياً عند الحفظ — وبدونه يُكتب السطر بمفتاح صفر ويصير يتيماً.
    /// </summary>
    public WorkflowTask Task { get; set; }
    public string FromState { get; set; }
    public string ToState { get; set; }
    public int? ByUserId { get; set; }
    public string ByUserName { get; set; }

    /// <summary>القدرة التي خوّلت هذا الإجراء — لا الدور.</summary>
    public string ByCapability { get; set; }

    public DateTime At { get; set; } = DateTime.Now;
    public string Notes { get; set; }
}

/// <summary>حالات المهمة — قيم ثابتة، لا نصوص حرة (§4-4).</summary>
public static class WorkflowTaskStates
{
    /// <summary>مفتوحة بانتظار من يملك القدرة.</summary>
    public const string Open = "Open";
    /// <summary>التقطها أحدهم ويعمل عليها.</summary>
    public const string InProgress = "InProgress";
    /// <summary>نُفِّذت.</summary>
    public const string Done = "Done";
    /// <summary>رُفضت بسبب مسجَّل.</summary>
    public const string Rejected = "Rejected";
    /// <summary>أُعيدت للمنشئ للتعديل.</summary>
    public const string Returned = "Returned";
    /// <summary>أُلغيت (أُلغي المستند الأصلي).</summary>
    public const string Cancelled = "Cancelled";
    /// <summary>تجاوزها حدث لاحق فلم تعد ذات معنى.</summary>
    public const string Superseded = "Superseded";

    /// <summary>هل المهمة ما زالت تنتظر عملاً؟ (أساس العدّادات وحساب التأخير)</summary>
    public static bool IsLive(string state) => state is Open or InProgress;

    public static string ToArabic(string state) => state switch
    {
        Open => "مفتوحة",
        InProgress => "قيد التنفيذ",
        Done => "منفَّذة",
        Rejected => "مرفوضة",
        Returned => "أُعيدت للتعديل",
        Cancelled => "ملغاة",
        Superseded => "تجاوزها حدث لاحق",
        _ => state ?? "-"
    };
}

/// <summary>أنواع المهام — لا «Invoicing»: الفوترة خارج النطاق (قرار Q3).</summary>
public static class WorkflowTaskTypes
{
    public const string PlanApproval = "PlanApproval";
    public const string PlanExecution = "PlanExecution";
    public const string DailyRun = "DailyRun";
    public const string QualityCheck = "QualityCheck";
    public const string WarehouseReceipt = "WarehouseReceipt";
    public const string CustomerDelivery = "CustomerDelivery";

    public static string ToArabic(string type) => type switch
    {
        PlanApproval => "اعتماد خطة إنتاج",
        PlanExecution => "تنفيذ خطة",
        DailyRun => "تشغيل يوم إنتاج",
        QualityCheck => "فحص جودة",
        WarehouseReceipt => "استلام مخزني",
        CustomerDelivery => "تسليم للعميل",
        _ => type ?? "-"
    };
}

/// <summary>أنواع المستندات التي تشير إليها المهام.</summary>
public static class WorkflowDocTypes
{
    public const string ProductionPlan = "ProductionPlan";
    public const string ProductionPlanItem = "ProductionPlanItem";
    public const string ProductionOrder = "ProductionOrder";
    public const string QualityCheck = "QualityCheck";
    public const string ProductionDelivery = "ProductionDelivery";
    public const string FinishedGoodsReceipt = "FinishedGoodsReceipt";
    public const string CustomerDelivery = "CustomerDelivery";
}

/// <summary>أولويات المهمة.</summary>
public static class WorkflowTaskPriority
{
    public const int Urgent = 1;
    public const int Normal = 2;
    public const int Deferred = 3;

    public static string ToArabic(int p) => p switch
    {
        Urgent => "عاجل",
        Normal => "عادي",
        Deferred => "مؤجل",
        _ => "عادي"
    };
}
