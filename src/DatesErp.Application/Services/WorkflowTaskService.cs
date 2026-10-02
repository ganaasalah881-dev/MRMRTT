using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §3 — محرك التوجيه: يولّد المهام ويوصلها لمن يملك القدرة، ويسجّل كل انتقال.
///
/// ### المبدأ الحاكم
/// **لا سطر في هذا الملف يفحص دوراً أو مسمى وظيفياً.** التوجيه بالقدرة وحدها
/// (`مورد.عملية`)، ومالكوها يُقرَّرون في قاعدة البيانات من شاشة الصلاحيات.
/// نتيجة مباشرة: التفويض الزمني واستثناءات المستخدم تعمل على المهام **تلقائياً**،
/// لأننا نستهلك نفس محرك الصلاحيات القائم لا محركاً موازياً.
///
/// ### الذرية
/// <see cref="Raise"/> **لا تفتح معاملة ولا تحفظ** — تُستدعى داخل معاملة المستند
/// المولِّد فيُحفظان معاً أو يفشلان معاً. لا مهمة يتيمة ولا انتقال صامت (§12).
/// أما دوال التنفيذ فتفتح معاملتها لأنها عمليات مستقلة.
/// </summary>
public class WorkflowTaskService : ServiceBase, IWorkflowTaskService
{
    private readonly IAuditService _audit;

    public WorkflowTaskService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering, IAuditService audit)
        : base(db, session, numbering)
    {
        _audit = audit;
    }

    // ══════════════════════════ التوليد ══════════════════════════

    /// <summary>
    /// توليد مهمة مع منع تكرار مضمون. إن وُجدت مهمة بنفس <c>CorrelationKey</c> تُعاد كما هي
    /// **دون إنشاء ثانية ودون خطأ** — فإعادة تشغيل الترحيل أو النقر المزدوج لا يضاعف شيئاً.
    /// </summary>
    public WorkflowTask Raise(NewTaskRequest r)
    {
        if (r == null) throw new ArgumentNullException(nameof(r));
        if (string.IsNullOrWhiteSpace(r.RequiredCapability))
            throw new DomainException("لا يمكن توليد مهمة بلا قدرة مطلوبة — التوجيه بالقدرة وحدها.");
        if (!WorkflowCapabilities.IsDefined(r.RequiredCapability))
            throw new DomainException($"قدرة غير معرّفة في الكتالوج: {r.RequiredCapability}");
        if (string.IsNullOrWhiteSpace(r.DocumentType) || r.DocumentId <= 0)
            throw new DomainException("المهمة يجب أن تشير إلى مستند — المهمة مؤشِّر لا نسخة.");

        var key = string.IsNullOrWhiteSpace(r.CorrelationKey) ? BuildCorrelationKey(r) : r.CorrelationKey;

        // منع التكرار: الفحص هنا للسلوك اللطيف، والقيد الفريد في القاعدة هو الضمان الحقيقي
        var existing = Db.WorkflowTasks.FirstOrDefault(t => t.CorrelationKey == key);
        if (existing != null) return existing;

        var task = new WorkflowTask
        {
            TaskNumber = Numbering.Next("TASK"),
            Stage = r.Stage,
            TaskType = r.TaskType,
            DocumentType = r.DocumentType,
            DocumentId = r.DocumentId,
            DocumentNumber = r.DocumentNumber,
            PlanItemId = r.PlanItemId,
            BusinessDate = r.BusinessDate,
            RequiredCapability = r.RequiredCapability,
            FromUserId = Session?.UserId > 0 ? Session.UserId : null,
            FromCapability = r.FromCapability,
            Title = r.Title ?? WorkflowTaskTypes.ToArabic(r.TaskType),
            SummaryJson = r.SummaryJson,
            State = WorkflowTaskStates.Open,
            Priority = r.Priority,
            DueDate = r.DueDate,
            OpenedDate = DateTime.Now,
            ParentTaskId = r.ParentTaskId,
            CorrelationKey = key
        };
        Db.WorkflowTasks.Add(task);
        AddHistory(task, null, WorkflowTaskStates.Open, r.FromCapability, "تولّدت المهمة");
        return task;
    }

    /// <summary>
    /// مفتاح منع التكرار: (نوع المستند + معرّفه + نوع المهمة + يوم العمل).
    /// إدراج يوم العمل ضروري: خطة واحدة تولّد مهمة لكل يوم، فبدونه تُبتلع كل الأيام في مهمة واحدة.
    /// </summary>
    private static string BuildCorrelationKey(NewTaskRequest r)
    {
        var day = r.BusinessDate?.ToString("yyyyMMdd") ?? "-";
        var item = r.PlanItemId?.ToString() ?? "-";
        return $"{r.DocumentType}:{r.DocumentId}:{r.TaskType}:{day}:{item}";
    }

    // ══════════════════════════ الاستعلام ══════════════════════════

    /// <summary>
    /// مهام المستخدم = ما يملك قدرته + ما أُحيل إليه صراحةً، **ناقص** ما أُحيل لغيره.
    /// الإحالة الصريحة تخصّ المهمة بشخص، فلا يجوز أن تبقى معروضة لبقية مالكي القدرة.
    /// </summary>
    public List<WorkflowTask> GetMyTasks(int userId, bool includeDone = false)
    {
        var caps = GetUserCapabilities(userId);
        var q = Db.WorkflowTasks.AsNoTracking().AsQueryable();
        if (!includeDone) q = q.Where(t => t.State == WorkflowTaskStates.Open || t.State == WorkflowTaskStates.InProgress);

        return q.ToList()
            .Where(t => t.AssignedUserId == userId
                        || (t.AssignedUserId == null && caps.Contains(t.RequiredCapability)))
            .OrderBy(t => t.Priority)
            .ThenBy(t => t.DueDate ?? DateTime.MaxValue)
            .ThenBy(t => t.Id)
            .ToList();
    }

    /// <summary>
    /// عدّادات «مهامي» — الاستطلاع الدوري كل 60 ثانية (قرار Q5).
    /// **لا تُحمَّل تفاصيل المهام**: أعمدة العدّ فقط، ومصفاة على المستخدم الحالي وحده.
    /// </summary>
    public TaskCounters GetMyCounters(int userId)
    {
        var caps = GetUserCapabilities(userId);
        var today = DateTime.Now.Date;

        var rows = Db.WorkflowTasks.AsNoTracking()
            .Where(t => t.State == WorkflowTaskStates.Open || t.State == WorkflowTaskStates.InProgress)
            .Select(t => new { t.State, t.Priority, t.DueDate, t.RequiredCapability, t.AssignedUserId })
            .ToList()
            .Where(t => t.AssignedUserId == userId || (t.AssignedUserId == null && caps.Contains(t.RequiredCapability)))
            .ToList();

        return new TaskCounters
        {
            Live = rows.Count,
            Open = rows.Count(t => t.State == WorkflowTaskStates.Open),
            InProgress = rows.Count(t => t.State == WorkflowTaskStates.InProgress),
            Overdue = rows.Count(t => t.DueDate.HasValue && t.DueDate.Value.Date < today),
            Urgent = rows.Count(t => t.Priority == WorkflowTaskPriority.Urgent),
            DueToday = rows.Count(t => t.DueDate.HasValue && t.DueDate.Value.Date == today)
        };
    }

    /// <summary>كل المهام — للإشراف. يتطلب <c>tasks.view.all</c> ويُفرض هنا في الخادم لا في الشاشة.</summary>
    public List<WorkflowTask> GetAllTasks(bool includeDone = false)
    {
        RequireCapability(WorkflowCapabilities.TasksViewAll);
        var q = Db.WorkflowTasks.AsNoTracking().AsQueryable();
        if (!includeDone) q = q.Where(t => t.State == WorkflowTaskStates.Open || t.State == WorkflowTaskStates.InProgress);
        return q.OrderBy(t => t.Priority).ThenBy(t => t.DueDate ?? DateTime.MaxValue).ThenBy(t => t.Id).ToList();
    }

    public WorkflowTask GetById(int taskId)
        => Db.WorkflowTasks.AsNoTracking().FirstOrDefault(t => t.Id == taskId);

    public List<WorkflowTaskHistory> GetHistory(int taskId)
        => Db.WorkflowTaskHistories.AsNoTracking().Where(h => h.TaskId == taskId).OrderBy(h => h.Id).ToList();

    public List<WorkflowTask> GetForDocument(string documentType, int documentId)
        => Db.WorkflowTasks.AsNoTracking()
            .Where(t => t.DocumentType == documentType && t.DocumentId == documentId)
            .OrderBy(t => t.Id).ToList();

    // ══════════════════════════ التنفيذ ══════════════════════════

    public OpResult Claim(int taskId, int userId) => RunOp(() =>
    {
        var t = Load(taskId);
        RequireCapability(t.RequiredCapability);
        if (!WorkflowTaskStates.IsLive(t.State)) return OpResult.Fail(ClosedMessage(t));
        if (t.ClaimedByUserId.HasValue && t.ClaimedByUserId != userId)
            return OpResult.Fail($"المهمة يعمل عليها {UserName(t.ClaimedByUserId)} حالياً.");

        var from = t.State;
        t.ClaimedByUserId = userId;
        t.State = WorkflowTaskStates.InProgress;
        AddHistory(t, from, t.State, t.RequiredCapability, "التقاط المهمة للعمل عليها");
        return OpResult.Success("تم التقاط المهمة — هي الآن باسمك حتى تنفّذها أو تتخلى عنها.");
    });

    public OpResult Release(int taskId, int userId) => RunOp(() =>
    {
        var t = Load(taskId);
        if (!WorkflowTaskStates.IsLive(t.State)) return OpResult.Fail(ClosedMessage(t));
        if (t.ClaimedByUserId != userId) return OpResult.Fail("لم تلتقط هذه المهمة أصلاً.");

        var from = t.State;
        t.ClaimedByUserId = null;
        t.State = WorkflowTaskStates.Open;
        AddHistory(t, from, t.State, t.RequiredCapability, "التخلي عن المهمة");
        return OpResult.Success("عادت المهمة مفتوحة لكل من يملك القدرة.");
    });

    public OpResult Complete(int taskId, string actionResult, string notes = null)
        => Close(taskId, WorkflowTaskStates.Done, actionResult ?? "Executed", notes, requireNotes: false);

    public OpResult Reject(int taskId, string reason)
        => Close(taskId, WorkflowTaskStates.Rejected, "Rejected", reason, requireNotes: true);

    public OpResult Return(int taskId, string reason)
        => Close(taskId, WorkflowTaskStates.Returned, "Returned", reason, requireNotes: true);

    /// <summary>
    /// الإغلاق الموحد. يفرض ثلاثة أشياء دائماً: امتلاك القدرة · عدم التنفيذ المزدوج ·
    /// إلزامية السبب عند الرفض والإرجاع (وإلا صار الرفض قراراً بلا أثر مكتوب).
    /// </summary>
    private OpResult Close(int taskId, string toState, string actionResult, string notes, bool requireNotes) => RunOp(() =>
    {
        if (requireNotes && string.IsNullOrWhiteSpace(notes))
            return OpResult.Fail("السبب إلزامي — لا رفض ولا إعادة بلا سبب مكتوب.");

        var t = Load(taskId);
        RequireCapability(t.RequiredCapability);
        if (!WorkflowTaskStates.IsLive(t.State)) return OpResult.Fail(ClosedMessage(t));

        var from = t.State;
        t.State = toState;
        t.ActionResult = actionResult;
        t.ActionNotes = notes;
        t.ActedDate = DateTime.Now;
        t.ActedByUserId = Session?.UserId > 0 ? Session.UserId : null;
        AddHistory(t, from, toState, t.RequiredCapability, notes);

        _audit.Log("WorkflowTask", "Execute", "WorkflowTask", t.TaskNumber, t.Id,
            new { State = from },
            new { State = toState, Result = actionResult, Notes = notes });

        return OpResult.Success($"تم تنفيذ المهمة: {WorkflowTaskStates.ToArabic(toState)}.", t.Id, t.TaskNumber);
    });

    /// <summary>إلغاء مهام مستند أُلغي — وإلا بقيت مهام حية تشير إلى مستند ميت.</summary>
    public int CancelForDocument(string documentType, int documentId, string reason)
    {
        var tasks = Db.WorkflowTasks
            .Where(t => t.DocumentType == documentType && t.DocumentId == documentId
                        && (t.State == WorkflowTaskStates.Open || t.State == WorkflowTaskStates.InProgress))
            .ToList();
        foreach (var t in tasks)
        {
            var from = t.State;
            t.State = WorkflowTaskStates.Cancelled;
            t.ActionNotes = reason;
            t.ActedDate = DateTime.Now;
            AddHistory(t, from, WorkflowTaskStates.Cancelled, null, reason);
        }
        return tasks.Count;
    }

    /// <summary>
    /// إحالة صريحة. **لا تُحيل لمن لا يملك القدرة** — وإلا صارت الإحالة ثغرة تلتف على
    /// نظام الصلاحيات وتوصل عملاً لمن لا يحق له تنفيذه.
    /// </summary>
    public OpResult Reassign(int taskId, int toUserId, string reason) => RunOp(() =>
    {
        RequireCapability(WorkflowCapabilities.TasksReassign);
        var t = Load(taskId);
        if (!WorkflowTaskStates.IsLive(t.State)) return OpResult.Fail(ClosedMessage(t));

        if (!UserHasCapability(toUserId, t.RequiredCapability))
            return OpResult.Fail(
                $"لا يمكن إحالة المهمة إلى {UserName(toUserId)} — لا يملك القدرة المطلوبة " +
                $"«{WorkflowCapabilities.NameOf(t.RequiredCapability)}». امنحها له من شاشة الصلاحيات أولاً.");

        t.AssignedUserId = toUserId;
        t.ClaimedByUserId = null;
        t.State = WorkflowTaskStates.Open;
        AddHistory(t, t.State, WorkflowTaskStates.Open, WorkflowCapabilities.TasksReassign,
            $"إحالة إلى {UserName(toUserId)}: {reason}");

        _audit.Log("WorkflowTask", "Reassign", "WorkflowTask", t.TaskNumber, t.Id, null,
            new { ToUserId = toUserId, Reason = reason });

        return OpResult.Success($"أُحيلت المهمة إلى {UserName(toUserId)}.");
    });

    // ══════════════════════════ ملّاك القدرة ══════════════════════════

    /// <summary>
    /// من يملك هذه القدرة الآن؟ الحساب من نفس مصدر الصلاحيات القائم:
    /// أدوار المستخدم ⟵ استثناءاته الصريحة ⟵ التفويضات السارية اليوم.
    /// </summary>
    public List<int> GetCapabilityHolders(string capability)
    {
        var def = WorkflowCapabilities.Resolve(capability);
        var holders = new List<int>();
        foreach (var userId in Db.Users.Where(u => u.IsActive).Select(u => u.Id).ToList())
            if (HasResourceOp(userId, def.Resource, def.Operation)) holders.Add(userId);
        return holders;
    }

    public bool UserHasCapability(int userId, string capability)
    {
        if (!WorkflowCapabilities.IsDefined(capability)) return false;
        var def = WorkflowCapabilities.Resolve(capability);
        return HasResourceOp(userId, def.Resource, def.Operation);
    }

    /// <summary>كل القدرات التي يملكها المستخدم — تُحسب مرة واحدة لكل استعلام مهام.</summary>
    private HashSet<string> GetUserCapabilities(int userId)
    {
        var effective = EffectiveOf(userId);
        var caps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in WorkflowCapabilities.All)
            if (effective.TryGetValue((c.Resource, c.Operation), out var ok) && ok) caps.Add(c.Code);
        return caps;
    }

    private bool HasResourceOp(int userId, string resource, string operation)
        => EffectiveOf(userId).TryGetValue((resource, operation), out var ok) && ok;

    /// <summary>
    /// الصلاحية الفعلية للمستخدم شاملة التفويض — نفس منطق <c>AuthService</c> بالضبط،
    /// كي لا يختلف ما تراه المهام عمّا يراه باقي النظام.
    /// </summary>
    private Dictionary<(string module, string action), bool> EffectiveOf(int userId)
    {
        var permSvc = new PermissionService(Db, Session);
        var roleIds = Db.UserRoles.AsNoTracking().Where(ur => ur.UserId == userId && ur.IsActive)
            .Select(ur => ur.RoleId).ToList();
        var cache = permSvc.BuildEffectiveCache(userId, roleIds);

        var today = DateTime.Now.Date;
        var delegs = Db.Delegations.AsNoTracking()
            .Where(d => d.IsActive && d.ToUserId == userId && d.StartDate <= today && d.EndDate >= today
                     && Db.Users.Any(u => u.Id == d.FromUserId && u.IsActive && !u.IsLocked)).ToList(); // §فحص التفويض — الموقوف لا يفوّض
        foreach (var dg in delegs)
        {
            var fromRoles = Db.UserRoles.AsNoTracking().Where(ur => ur.UserId == dg.FromUserId && ur.IsActive)
                .Select(ur => ur.RoleId).ToList();
            foreach (var kv in permSvc.BuildEffectiveCache(dg.FromUserId, fromRoles))
            {
                if (dg.ScopeModule != null && kv.Key.module != dg.ScopeModule) continue;
                cache[kv.Key] = (cache.TryGetValue(kv.Key, out var v) && v) || kv.Value;
            }
        }
        return cache;
    }

    /// <summary>فرض القدرة على الجلسة الحالية — الخادم يرفض، لا الشاشة فقط.</summary>
    private void RequireCapability(string capability)
    {
        var def = WorkflowCapabilities.Resolve(capability);
        if (Session == null || !Session.Can(def.Resource, def.Operation))
            throw new PermissionDeniedException(
                $"{WorkflowCapabilities.NameOf(capability)} ({def.Resource}/{def.Operation})");
    }

    // ══════════════════════════ مساعدات ══════════════════════════

    private WorkflowTask Load(int taskId)
        => Db.WorkflowTasks.FirstOrDefault(t => t.Id == taskId)
           ?? throw new DomainException("المهمة غير موجودة.");

    /// <summary>رسالة التنفيذ المزدوج: تسمّي من نفّذها ومتى بدل «حدث خطأ».</summary>
    private string ClosedMessage(WorkflowTask t)
        => t.ActedDate.HasValue
            ? $"نُفِّذت هذه المهمة بواسطة {UserName(t.ActedByUserId)} في {t.ActedDate:dd/MM/yyyy HH:mm} — {WorkflowTaskStates.ToArabic(t.State)}."
            : $"المهمة لم تعد مفتوحة — حالتها: {WorkflowTaskStates.ToArabic(t.State)}.";

    private string UserName(int? userId)
        => userId == null ? "—"
           : Db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.FullName).FirstOrDefault() ?? $"#{userId}";

    private void AddHistory(WorkflowTask t, string from, string to, string capability, string notes)
        => Db.WorkflowTaskHistories.Add(new WorkflowTaskHistory
        {
            // Task (لا TaskId) — المهمة الجديدة بلا مفتاح بعد؛ EF يملؤه عند الحفظ
            Task = t,
            TaskId = t.Id,
            FromState = from,
            ToState = to,
            ByUserId = Session?.UserId > 0 ? Session.UserId : null,
            ByUserName = Session?.UserName ?? "system",
            ByCapability = capability,
            At = DateTime.Now,
            Notes = notes
        });
}
