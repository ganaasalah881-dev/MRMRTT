using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;

namespace DatesErp.Core.Interfaces.Services;

/// <summary>
/// §3 — محرك التوجيه: توليد المهام وتنفيذها والاستعلام عنها.
///
/// **قاعدة حاكمة:** لا دالة هنا تأخذ اسم دور ولا مسمى وظيفي. التوجيه بالقدرة وحدها.
/// </summary>
public interface IWorkflowTaskService
{
    // ═══ التوليد ═══

    /// <summary>
    /// توليد مهمة — أو إرجاع القائمة إن كان <c>CorrelationKey</c> مستخدماً (منع التكرار §12).
    /// **تُستدعى داخل معاملة المستند نفسها** فلا تنشأ مهمة يتيمة ولا انتقال صامت.
    /// </summary>
    WorkflowTask Raise(NewTaskRequest request);

    // ═══ الاستعلام ═══

    /// <summary>مهام المستخدم: ما يملك قدرته + ما أُحيل إليه صراحةً. لا يرى ما لا يملك قدرته.</summary>
    List<WorkflowTask> GetMyTasks(int userId, bool includeDone = false);

    /// <summary>عدّاد «مهامي» — استعلام خفيف للاستطلاع الدوري كل 60 ثانية (قرار Q5).</summary>
    TaskCounters GetMyCounters(int userId);

    /// <summary>كل المهام — يتطلب قدرة <c>tasks.view.all</c>.</summary>
    List<WorkflowTask> GetAllTasks(bool includeDone = false);

    WorkflowTask GetById(int taskId);
    List<WorkflowTaskHistory> GetHistory(int taskId);

    /// <summary>مهام مستند بعينه — لعرض «أين وصل هذا المستند».</summary>
    List<WorkflowTask> GetForDocument(string documentType, int documentId);

    // ═══ التنفيذ ═══

    /// <summary>التقاط المهمة للعمل عليها — يمنع ازدواج العمل. لا يمنع غيره من التنفيذ.</summary>
    OpResult Claim(int taskId, int userId);

    /// <summary>التخلي عن مهمة ملتقَطة فتعود مفتوحة للجميع.</summary>
    OpResult Release(int taskId, int userId);

    /// <summary>
    /// إغلاق المهمة بنتيجة. يفحص أن المنفِّذ يملك <c>RequiredCapability</c>، ويرفض التنفيذ المزدوج
    /// برسالة تسمي من نفّذها ومتى.
    /// </summary>
    OpResult Complete(int taskId, string actionResult, string notes = null);

    /// <summary>رفض — **السبب إلزامي**.</summary>
    OpResult Reject(int taskId, string reason);

    /// <summary>إعادة للمنشئ للتعديل — **السبب إلزامي**.</summary>
    OpResult Return(int taskId, string reason);

    /// <summary>إلغاء مهام مستند أُلغي — لا تبقى مهام معلقة لمستند ميت.</summary>
    int CancelForDocument(string documentType, int documentId, string reason);

    /// <summary>إحالة صريحة لشخص — يتطلب <c>tasks.reassign</c>، ولا تُحيل لمن لا يملك القدرة.</summary>
    OpResult Reassign(int taskId, int toUserId, string reason);

    // ═══ ملّاك القدرة ═══

    /// <summary>من يملك هذه القدرة الآن؟ (أدوار + استثناءات + تفويضات سارية)</summary>
    List<int> GetCapabilityHolders(string capability);

    /// <summary>هل يملك هذا المستخدم هذه القدرة الآن؟</summary>
    bool UserHasCapability(int userId, string capability);
}

/// <summary>طلب توليد مهمة.</summary>
public class NewTaskRequest
{
    public WorkflowStage Stage { get; set; }
    public string TaskType { get; set; }
    public string DocumentType { get; set; }
    public int DocumentId { get; set; }
    public string DocumentNumber { get; set; }

    /// <summary>القدرة المطلوبة — إلزامية، ويجب أن تكون معرّفة في <see cref="WorkflowCapabilities"/>.</summary>
    public string RequiredCapability { get; set; }

    public string Title { get; set; }
    public string SummaryJson { get; set; }
    public int? PlanItemId { get; set; }
    public DateTime? BusinessDate { get; set; }
    public DateTime? DueDate { get; set; }
    public int Priority { get; set; } = WorkflowTaskPriority.Normal;
    public int? ParentTaskId { get; set; }

    /// <summary>القدرة التي نُفِّذت فأنتجت هذه المهمة — للتتبع.</summary>
    public string FromCapability { get; set; }

    /// <summary>
    /// تجاوز مفتاح منع التكرار المحسوب تلقائياً. اتركه فارغاً في الحالة العادية.
    /// </summary>
    public string CorrelationKey { get; set; }
}

/// <summary>عدّادات «مهامي» — للاستطلاع الدوري الخفيف (Q5).</summary>
public class TaskCounters
{
    /// <summary>كل ما ينتظر عملاً (مفتوحة + قيد التنفيذ).</summary>
    public int Live { get; set; }
    public int Open { get; set; }
    public int InProgress { get; set; }
    public int Overdue { get; set; }
    public int Urgent { get; set; }
    public int DueToday { get; set; }
}
