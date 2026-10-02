using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;

using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §3 — المرحلة 1: محرك التوجيه بالمهام (بلا واجهة).
///
/// ما تحرسه هذه الاختبارات هو **العقود الحاكمة** التي لو انكسر أحدها انهار التصميم كله:
///   1. لا مهمة بلا قدرة معرّفة — ولا قدرة تشير إلى مورد/عملية غير موجودين.
///   2. منع التكرار مضمون بالقاعدة لا بالنية.
///   3. التوجيه بالقدرة وحدها — ولا شيء في المسار يفحص دوراً أو مسمى.
///   4. لا تنفيذ مزدوج، ولا رفض بلا سبب، ولا إحالة لمن لا يملك القدرة.
/// </summary>
public class WorkflowTaskTests
{
    private static IWorkflowTaskService Svc(TestHost host)
        => host.Services.GetRequiredService<IWorkflowTaskService>();

    private static NewTaskRequest PlanApprovalRequest(int planId = 1, string docNo = "PLN-0001") => new()
    {
        Stage = WorkflowStage.Planning,
        TaskType = WorkflowTaskTypes.PlanApproval,
        DocumentType = WorkflowDocTypes.ProductionPlan,
        DocumentId = planId,
        DocumentNumber = docNo,
        RequiredCapability = WorkflowCapabilities.PlanningApprove,
        Title = "خطة إنتاج بانتظار الاعتماد"
    };

    // ═══════════ 1) سلامة كتالوج القدرات ═══════════

    /// <summary>
    /// كل قدرة تترجم إلى (مورد، عملية) **موجودين فعلاً** في كتالوج الصلاحيات.
    /// لولا هذا الحارس لأمكن تعريف قدرة لا يستطيع أحد امتلاكها أبداً — فتُولَّد
    /// مهام لا تصل إلى أحد وتتوقف الدورة بصمت.
    /// </summary>
    [Fact]
    public void Every_Capability_Maps_To_Real_Resource_And_Operation()
    {
        var modules = PermissionModules.Codes.ToHashSet();
        var ops = PermissionService.OperationCatalog.Select(o => o.Code).ToHashSet();

        foreach (var c in WorkflowCapabilities.All)
        {
            Assert.True(modules.Contains(c.Resource), $"القدرة {c.Code} تشير إلى مورد غير موجود: {c.Resource}");
            Assert.True(ops.Contains(c.Operation), $"القدرة {c.Code} تشير إلى عملية غير موجودة: {c.Operation}");
        }
    }

    /// <summary>قرار Q3: لا قدرة فوترة في الكتالوج — الفوترة خارج نظام التمور.</summary>
    [Fact]
    public void No_Invoicing_Capability_Exists()
    {
        Assert.DoesNotContain(WorkflowCapabilities.Codes, c => c.Contains("invoice", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(WorkflowTaskTypes).GetFields()
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()), t => t.Contains("Invoic"));
    }

    [Fact]
    public void Unknown_Capability_Is_Rejected_Loudly()
    {
        Assert.Throws<ArgumentException>(() => WorkflowCapabilities.Resolve("nope.nope"));
        Assert.False(WorkflowCapabilities.IsDefined("nope.nope"));
    }

    // ═══════════ 2) التوليد ومنع التكرار ═══════════

    [Fact]
    public void Raise_Creates_Task_With_Number_And_History()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);

        var t = svc.Raise(PlanApprovalRequest());
        host.Get<DatesErpDbContext>().SaveChanges();

        Assert.True(t.Id > 0);
        Assert.False(string.IsNullOrWhiteSpace(t.TaskNumber));
        Assert.Equal(WorkflowTaskStates.Open, t.State);
        Assert.Equal(WorkflowCapabilities.PlanningApprove, t.RequiredCapability);

        var hist = svc.GetHistory(t.Id);
        Assert.Single(hist);
        Assert.Equal(WorkflowTaskStates.Open, hist[0].ToState);
        Assert.Equal(t.Id, hist[0].TaskId); // السطر ليس يتيماً رغم كتابته قبل الحفظ
    }

    /// <summary>
    /// نفس الحدث مرتين ⟵ مهمة واحدة، بلا استثناء وبلا صف ثانٍ.
    /// هذا هو ما يجعل «ترحيل الخطط القائمة» (Q7) آمناً عند إعادة الضغط.
    /// </summary>
    [Fact]
    public void Raise_Twice_For_Same_Event_Yields_One_Task()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);
        var db = host.Get<DatesErpDbContext>();

        var a = svc.Raise(PlanApprovalRequest());
        db.SaveChanges();
        var b = svc.Raise(PlanApprovalRequest());
        db.SaveChanges();

        Assert.Equal(a.Id, b.Id);
        Assert.Equal(1, db.WorkflowTasks.Count(t => t.DocumentId == 1));
    }

    /// <summary>خطة واحدة تولّد مهمة لكل يوم — فيوم العمل جزء من مفتاح منع التكرار.</summary>
    [Fact]
    public void Tasks_For_Different_Business_Days_Are_Distinct()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);
        var db = host.Get<DatesErpDbContext>();

        for (int d = 0; d < 3; d++)
        {
            var r = PlanApprovalRequest();
            r.TaskType = WorkflowTaskTypes.DailyRun;
            r.RequiredCapability = WorkflowCapabilities.ProductionOrderIssue;
            r.BusinessDate = new DateTime(2026, 3, 1).AddDays(d);
            svc.Raise(r);
        }
        db.SaveChanges();

        Assert.Equal(3, db.WorkflowTasks.Count(t => t.TaskType == WorkflowTaskTypes.DailyRun));
    }

    [Fact]
    public void Raise_Without_Capability_Throws()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var r = PlanApprovalRequest();
        r.RequiredCapability = null;
        Assert.Throws<DomainException>(() => Svc(host).Raise(r));
    }

    [Fact]
    public void Raise_Without_Document_Throws()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var r = PlanApprovalRequest();
        r.DocumentId = 0;
        Assert.Throws<DomainException>(() => Svc(host).Raise(r));
    }

    // ═══════════ 3) التوجيه بالقدرة — جوهر التصميم ═══════════

    /// <summary>
    /// المهمة تصل لمن يملك القدرة، ولا تصل لمن لا يملكها — **بلا أي فحص لدور**.
    /// دور الجودة لا يملك `planning.approve` فلا يرى مهمة اعتماد الخطة.
    /// </summary>
    [Fact]
    public void Task_Reaches_Capability_Holders_Only()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);
        var db = host.Get<DatesErpDbContext>();

        svc.Raise(PlanApprovalRequest());
        db.SaveChanges();

        int adminId = db.Users.Single(u => u.UserName == "admin").Id;
        int qualityId = db.Users.Single(u => u.UserName == "quality").Id;

        Assert.NotEmpty(svc.GetMyTasks(adminId));
        Assert.Empty(svc.GetMyTasks(qualityId));
    }

    /// <summary>
    /// **الاختبار الأهم:** منح القدرة من شاشة الصلاحيات — بلا سطر كود ولا دور جديد —
    /// يجعل المهمة تصل فوراً. هذا هو معنى «التوسع الوظيفي بصفر تعديل برمجي».
    /// </summary>
    [Fact]
    public void Granting_Capability_Routes_Task_Without_Any_Code_Change()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);
        var db = host.Get<DatesErpDbContext>();

        svc.Raise(PlanApprovalRequest());
        db.SaveChanges();

        int qualityId = db.Users.Single(u => u.UserName == "quality").Id;
        Assert.Empty(svc.GetMyTasks(qualityId));

        // منح القدرة كاستثناء شخصي — تماماً كما تفعل شاشة الصلاحيات
        var def = WorkflowCapabilities.Resolve(WorkflowCapabilities.PlanningApprove);
        new PermissionService(db, host.Services.GetRequiredService<ICurrentSession>())
            .SetUserPermission(qualityId, def.Resource, def.Operation, true);

        Assert.Single(svc.GetMyTasks(qualityId));
    }

    /// <summary>التفويض الزمني القائم يعمل على المهام تلقائياً — لأننا نستهلك محرك الصلاحيات نفسه.</summary>
    [Fact]
    public void Active_Delegation_Delivers_Task_To_Delegate()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);
        var db = host.Get<DatesErpDbContext>();

        svc.Raise(PlanApprovalRequest());
        db.SaveChanges();

        int adminId = db.Users.Single(u => u.UserName == "admin").Id;
        int qualityId = db.Users.Single(u => u.UserName == "quality").Id;
        Assert.Empty(svc.GetMyTasks(qualityId));

        db.Delegations.Add(new Delegation
        {
            FromUserId = adminId,
            ToUserId = qualityId,
            StartDate = DateTime.Now.Date.AddDays(-1),
            EndDate = DateTime.Now.Date.AddDays(1),
            IsActive = true
        });
        db.SaveChanges();

        Assert.Single(svc.GetMyTasks(qualityId));
    }

    /// <summary>تفويض منتهٍ لا يوصل شيئاً — وإلا صار التفويض ثغرة دائمة.</summary>
    [Fact]
    public void Expired_Delegation_Delivers_Nothing()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);
        var db = host.Get<DatesErpDbContext>();

        svc.Raise(PlanApprovalRequest());
        db.SaveChanges();

        int adminId = db.Users.Single(u => u.UserName == "admin").Id;
        int qualityId = db.Users.Single(u => u.UserName == "quality").Id;
        db.Delegations.Add(new Delegation
        {
            FromUserId = adminId,
            ToUserId = qualityId,
            StartDate = DateTime.Now.Date.AddDays(-30),
            EndDate = DateTime.Now.Date.AddDays(-2),
            IsActive = true
        });
        db.SaveChanges();

        Assert.Empty(svc.GetMyTasks(qualityId));
    }

    // ═══════════ 4) التنفيذ ═══════════

    [Fact]
    public void Claim_Then_Complete_Closes_Task_And_Records_History()
    {
        using var host = new TestHost();
        var session = host.LoginAsAdmin();
        var svc = Svc(host);
        var db = host.Get<DatesErpDbContext>();

        var t = svc.Raise(PlanApprovalRequest());
        db.SaveChanges();

        Assert.True(svc.Claim(t.Id, session.UserId).Ok);
        Assert.Equal(WorkflowTaskStates.InProgress, svc.GetById(t.Id).State);

        Assert.True(svc.Complete(t.Id, "Approved", "معتمدة").Ok);
        var done = svc.GetById(t.Id);
        Assert.Equal(WorkflowTaskStates.Done, done.State);
        Assert.Equal("Approved", done.ActionResult);
        Assert.NotNull(done.ActedDate);
        Assert.Equal(3, svc.GetHistory(t.Id).Count); // تولّد + التقاط + تنفيذ
    }

    /// <summary>التنفيذ المزدوج مرفوض برسالة تسمّي المنفِّذ والوقت — لا «حدث خطأ».</summary>
    [Fact]
    public void Double_Execution_Is_Refused_With_Who_And_When()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);
        var db = host.Get<DatesErpDbContext>();

        var t = svc.Raise(PlanApprovalRequest());
        db.SaveChanges();
        svc.Complete(t.Id, "Approved");

        var second = svc.Complete(t.Id, "Approved");
        Assert.False(second.Ok);
        Assert.Contains("نُفِّذت", second.Message);
        Assert.Contains("مدير النظام", second.Message);
    }

    [Fact]
    public void Reject_Without_Reason_Is_Refused()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);
        var db = host.Get<DatesErpDbContext>();

        var t = svc.Raise(PlanApprovalRequest());
        db.SaveChanges();

        Assert.False(svc.Reject(t.Id, "   ").Ok);
        Assert.Equal(WorkflowTaskStates.Open, svc.GetById(t.Id).State);

        Assert.True(svc.Reject(t.Id, "الكميات تتجاوز الطاقة").Ok);
        Assert.Equal(WorkflowTaskStates.Rejected, svc.GetById(t.Id).State);
    }

    /// <summary>من لا يملك القدرة لا ينفّذ — يُفرض في الخادم لا في الشاشة.</summary>
    [Fact]
    public void Execution_Without_Capability_Is_Denied_By_Server()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);
        var db = host.Get<DatesErpDbContext>();
        var t = svc.Raise(PlanApprovalRequest());
        db.SaveChanges();

        // تبديل الجلسة إلى مستخدم الجودة (لا يملك planning.approve)
        var auth = host.Services.GetRequiredService<IAuthService>();
        Assert.True(auth.Login("quality", DbSeeder.InitialAdminPassword).Success);

        Assert.Throws<PermissionDeniedException>(() => svc.Complete(t.Id, "Approved"));
        Assert.Equal(WorkflowTaskStates.Open, svc.GetById(t.Id).State);
    }

    /// <summary>الإحالة لا تلتف على الصلاحيات: لا تُحيل لمن لا يملك القدرة.</summary>
    [Fact]
    public void Reassign_To_User_Without_Capability_Is_Refused()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);
        var db = host.Get<DatesErpDbContext>();

        var t = svc.Raise(PlanApprovalRequest());
        db.SaveChanges();
        int qualityId = db.Users.Single(u => u.UserName == "quality").Id;

        var r = svc.Reassign(t.Id, qualityId, "إجازة");
        Assert.False(r.Ok);
        Assert.Contains("لا يملك القدرة", r.Message);
        Assert.Null(svc.GetById(t.Id).AssignedUserId);
    }

    /// <summary>بعد الإحالة الصريحة تخصّ المهمة شخصاً واحداً، فتختفي عن بقية مالكي القدرة.</summary>
    [Fact]
    public void Explicit_Assignment_Hides_Task_From_Other_Holders()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);
        var db = host.Get<DatesErpDbContext>();

        var t = svc.Raise(PlanApprovalRequest());
        db.SaveChanges();

        int adminId = db.Users.Single(u => u.UserName == "admin").Id;
        int qualityId = db.Users.Single(u => u.UserName == "quality").Id;
        var def = WorkflowCapabilities.Resolve(WorkflowCapabilities.PlanningApprove);
        new PermissionService(db, host.Services.GetRequiredService<ICurrentSession>())
            .SetUserPermission(qualityId, def.Resource, def.Operation, true);

        Assert.True(svc.Reassign(t.Id, qualityId, "إجازة المدير").Ok);

        Assert.Single(svc.GetMyTasks(qualityId));
        Assert.Empty(svc.GetMyTasks(adminId));
    }

    [Fact]
    public void Cancelling_Document_Cancels_Its_Live_Tasks()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);
        var db = host.Get<DatesErpDbContext>();

        var t = svc.Raise(PlanApprovalRequest());
        db.SaveChanges();

        int n = svc.CancelForDocument(WorkflowDocTypes.ProductionPlan, 1, "أُلغيت الخطة");
        db.SaveChanges();

        Assert.Equal(1, n);
        Assert.Equal(WorkflowTaskStates.Cancelled, svc.GetById(t.Id).State);
    }

    // ═══════════ 5) العدّادات (Q5) ═══════════

    [Fact]
    public void Counters_Report_Live_Overdue_And_Urgent()
    {
        using var host = new TestHost();
        var session = host.LoginAsAdmin();
        var svc = Svc(host);
        var db = host.Get<DatesErpDbContext>();

        var overdue = PlanApprovalRequest(1, "PLN-0001");
        overdue.DueDate = DateTime.Now.Date.AddDays(-3);
        overdue.Priority = WorkflowTaskPriority.Urgent;
        svc.Raise(overdue);

        var today = PlanApprovalRequest(2, "PLN-0002");
        today.DueDate = DateTime.Now.Date;
        svc.Raise(today);
        db.SaveChanges();

        var c = svc.GetMyCounters(session.UserId);
        Assert.Equal(2, c.Live);
        Assert.Equal(2, c.Open);
        Assert.Equal(1, c.Overdue);
        Assert.Equal(1, c.Urgent);
        Assert.Equal(1, c.DueToday);
    }

    /// <summary>المهام المنفَّذة لا تُعدّ — العدّاد يعكس ما ينتظر عملاً فقط.</summary>
    [Fact]
    public void Completed_Tasks_Leave_The_Counter()
    {
        using var host = new TestHost();
        var session = host.LoginAsAdmin();
        var svc = Svc(host);
        var db = host.Get<DatesErpDbContext>();

        var t = svc.Raise(PlanApprovalRequest());
        db.SaveChanges();
        Assert.Equal(1, svc.GetMyCounters(session.UserId).Live);

        svc.Complete(t.Id, "Approved");
        Assert.Equal(0, svc.GetMyCounters(session.UserId).Live);
    }

    // ═══════════ 6) المرحلة 2 — ما تعتمد عليه شاشة «مهامي» ═══════════

    /// <summary>
    /// كل دور نشط يملك «عرض مهامه» بعد الترحيل، ولا يملك «عرض الكل» الإشرافية.
    /// لولا الأول لحُجبت شاشة «مهامي» عن كل مستخدمي القاعدة المُرقّاة؛ ولولا الثاني
    /// لصار كل مستخدم مشرفاً على مهام الجميع.
    /// </summary>
    [Fact]
    public void Every_Role_Sees_Own_Tasks_But_Not_Everyones()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var svc = new PermissionService(db, host.Services.GetRequiredService<ICurrentSession>());
        svc.EnsureCatalog();

        foreach (var role in db.Roles.Where(r => r.IsActive).ToList())
        {
            var set = svc.GetRoleSet(role.Id);
            Assert.Contains((PermissionModules.Tasks, "View"), set);
            if (role.RoleCode is not ("Administrator" or "Management"))
                Assert.DoesNotContain((PermissionModules.Tasks, "ViewAll"), set);
        }
    }

    /// <summary>مهام مستند بعينه — أساس زر «أين وصل هذا المستند».</summary>
    [Fact]
    public void GetForDocument_Returns_Tasks_Of_That_Document_Only()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);
        var db = host.Get<DatesErpDbContext>();

        svc.Raise(PlanApprovalRequest(1, "PLN-0001"));
        svc.Raise(PlanApprovalRequest(2, "PLN-0002"));
        db.SaveChanges();

        var forOne = svc.GetForDocument(WorkflowDocTypes.ProductionPlan, 1);
        Assert.Single(forOne);
        Assert.Equal("PLN-0001", forOne[0].DocumentNumber);
    }

    /// <summary>لقطة الملخص تُحفظ وتُقرأ كما هي — البطاقة تعرضها بلا استعلام إضافي.</summary>
    [Fact]
    public void Summary_Snapshot_Round_Trips()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);
        var db = host.Get<DatesErpDbContext>();

        var r = PlanApprovalRequest();
        r.SummaryJson = "{\"العملاء\":3,\"الكمية\":\"12,500 كجم\"}";
        var t = svc.Raise(r);
        db.SaveChanges();

        Assert.Equal(r.SummaryJson, svc.GetById(t.Id).SummaryJson);
    }

    /// <summary>المهمة الملتقَطة تظهر في تبويب «قيد العمل» لصاحبها.</summary>
    [Fact]
    public void Claimed_Task_Moves_To_InProgress_For_Owner()
    {
        using var host = new TestHost();
        var session = host.LoginAsAdmin();
        var svc = Svc(host);
        var db = host.Get<DatesErpDbContext>();

        var t = svc.Raise(PlanApprovalRequest());
        db.SaveChanges();
        svc.Claim(t.Id, session.UserId);

        var mine = svc.GetMyTasks(session.UserId);
        Assert.Single(mine, x => x.Id == t.Id && x.State == WorkflowTaskStates.InProgress);

        var c = svc.GetMyCounters(session.UserId);
        Assert.Equal(1, c.InProgress);
        Assert.Equal(0, c.Open);
    }

    /// <summary>«كل المهام» صلاحية إشرافية يفرضها الخادم.</summary>
    [Fact]
    public void GetAllTasks_Requires_ViewAll_Capability()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Svc(host);
        var db = host.Get<DatesErpDbContext>();
        svc.Raise(PlanApprovalRequest());
        db.SaveChanges();

        Assert.Single(svc.GetAllTasks());

        var auth = host.Services.GetRequiredService<IAuthService>();
        Assert.True(auth.Login("quality", DbSeeder.InitialAdminPassword).Success);
        Assert.Throws<PermissionDeniedException>(() => svc.GetAllTasks());
    }
}
