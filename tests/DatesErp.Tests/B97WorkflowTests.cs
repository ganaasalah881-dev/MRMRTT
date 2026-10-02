using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B97 — النظام المُوَجَّه بالمهام:
/// Four-Eyes (المعتمد ≠ المنشئ)، سبب الإرجاع الإجباري (StatusReason)،
/// مركز المهام (اشتقاق بطاقات لكل دور)، المطلوب اليوم/المتعثر، تنبيهات الإدارة (تجاوزات الفحص).
/// ملاحظة: TestHost يُعطِّل Four-Eyes افتراضياً (اختبارات قديمة تُعتمد بنفس المستخدم) —
/// الاختبار الأول فعّله صراحةً لاختبار المنع.
/// </summary>
public class B97WorkflowTests
{
    private static int SavePlan(TestHost host, string title, string start, string end,
        params (string date, double kg, int ctn)[] days)
    {
        var planning = host.Get<IPlanningService>();
        var items = days.Select(d => new PlanItemDto
        {
            SourceType = "Manual",
            CustomerId = 1,
            ProductId = 3,
            PackagingTypeId = 1,
            PlannedQtyKg = d.kg,
            PlannedCartons = d.ctn,
            ScheduledDate = d.date,
            SuggestedShiftId = 1,
            SuggestedLineId = 1
        }).ToList();
        var p = planning.SavePlan(title, days.Length > 1 ? "Period" : "Daily", start, end, 1, 1, items);
        Assert.True(p.Ok, p.Message);
        return p.Id;
    }

    // ═══ Four-Eyes ═══

    [Fact]
    public void Creator_Cannot_Approve_Their_Own_Plan_When_FourEyes_Strict()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        db.SystemSettings.Single(s => s.SettingKey == "Workflow_FourEyes").SettingValue = "Strict";
        db.SaveChanges();

        int planId = SavePlan(host, "خطة B97", "2026-09-20", "2026-09-20", ("2026-09-20", 500, 100));
        var planning = host.Get<IPlanningService>();
        Assert.True(planning.SubmitPlan(planId).Ok);

        var r = planning.ApprovePlan(planId);
        Assert.False(r.Ok);
        Assert.Contains("Four-Eyes", r.Message);
        Assert.Contains("منشئ", r.Message);
    }

    [Fact]
    public void Different_Approver_Can_Approve_And_Transition_Is_Audited()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int planId = SavePlan(host, "خطة الاعتماد", "2026-09-20", "2026-09-20", ("2026-09-20", 500, 100));
        var planning = host.Get<IPlanningService>();
        Assert.True(planning.SubmitPlan(planId).Ok);

        // مستخدم آخر مخوَّل الاعتماد (مدير الإنتاج — planning كاملة)
        host.LoginAs("production");
        var r = host.Get<IPlanningService>().ApprovePlan(planId);
        Assert.True(r.Ok, r.Message);

        var db = host.Get<DatesErpDbContext>();
        var plan = db.ProductionPlans.AsNoTracking().Single(x => x.Id == planId);
        Assert.True(plan.IsApproved);
        Assert.Equal(DocStatuses.Approved, plan.Status);
        Assert.NotNull(plan.ApprovedBy);

        var audit = db.AuditLogs.AsNoTracking().Single(a => a.DocumentNumber == plan.DocumentNumber && a.ActionType == "Approve");
        Assert.Equal("production", audit.UserName);
    }

    // ═══ سبب الإرجاع الإجباري ═══

    [Fact]
    public void Return_Requires_Written_Reason_And_Stores_StatusReason()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int planId = SavePlan(host, "خطة الإرجاع", "2026-09-20", "2026-09-20", ("2026-09-20", 500, 100));
        var planning = host.Get<IPlanningService>();
        Assert.True(planning.SubmitPlan(planId).Ok);

        host.LoginAs("production");
        var svc = host.Get<IPlanningService>();
        Assert.False(svc.ReturnPlan(planId, null).Ok);          // بلا سبب
        Assert.False(svc.ReturnPlan(planId, "قصير").Ok);        // أقل من 10 أحرف

        var reason = "الكمية تتجاوز الطاقة المتوفرة في الوردية";
        var r = svc.ReturnPlan(planId, reason);
        Assert.True(r.Ok, r.Message);

        var db = host.Get<DatesErpDbContext>();
        var plan = db.ProductionPlans.AsNoTracking().Single(x => x.Id == planId);
        Assert.Equal(DocStatuses.Draft, plan.Status);
        Assert.Equal(reason, plan.StatusReason);
        Assert.Contains(reason, plan.Notes);
    }

    [Fact]
    public void Resubmit_Clears_StatusReason_And_Plan_Goes_UnderApproval()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int planId = SavePlan(host, "خطة إعادة الإرسال", "2026-09-20", "2026-09-20", ("2026-09-20", 500, 100));
        var planning = host.Get<IPlanningService>();
        Assert.True(planning.SubmitPlan(planId).Ok);

        host.LoginAs("production");
        Assert.True(host.Get<IPlanningService>().ReturnPlan(planId, "تصحيح الكميات حسب الطاقة الفعلية").Ok);

        host.LoginAsAdmin();
        Assert.True(host.Get<IPlanningService>().SubmitPlan(planId).Ok);

        var db = host.Get<DatesErpDbContext>();
        var plan = db.ProductionPlans.AsNoTracking().Single(x => x.Id == planId);
        Assert.Null(plan.StatusReason);
        Assert.Equal("UnderApproval", plan.Status);
    }

    // ═══ مركز المهام: بطاقات الصانع ═══

    [Fact]
    public void Board_Creator_Sees_Returned_Plan_With_Reason_And_His_Drafts()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int returnedId = SavePlan(host, "الخطة المرجعة", "2026-09-20", "2026-09-20", ("2026-09-20", 500, 100));
        int draftId = SavePlan(host, "خطة مسودة ثانية", "2026-09-21", "2026-09-21", ("2026-09-21", 300, 60));
        var planning = host.Get<IPlanningService>();
        Assert.True(planning.SubmitPlan(returnedId).Ok);

        host.LoginAs("production");
        Assert.True(host.Get<IPlanningService>().ReturnPlan(returnedId, "الفترة لا تتوافق مع طاقتنا المتاحة").Ok);

        host.LoginAsAdmin();
        var board = host.Get<ITaskCenterService>().GetBoard();

        var returned = board.Action.Single(c => c.DocType == "Plan" && c.DocId == returnedId);
        Assert.Contains("عيدت للتعديل", returned.Title);   // §B102: بلا اعتماد على شكل الهمزة
        Assert.Equal("الفترة لا تتوافق مع طاقتنا المتاحة", returned.Reason);

        var draft = board.Action.Single(c => c.DocType == "Plan" && c.DocId == draftId);
        Assert.Contains("مسودة", draft.Title);
        Assert.Null(draft.Reason);
    }

    // ═══ مركز المهام: بطاقات المعتمد ═══

    [Fact]
    public void Board_Approver_Sees_UnderApproval_Plan_With_Full_Context()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int planId = SavePlan(host, "خطة بانتظار الاعتماد", "2026-09-20", "2026-09-27",
            ("2026-09-20", 500, 100), ("2026-09-21", 300, 60));
        Assert.True(host.Get<IPlanningService>().SubmitPlan(planId).Ok);

        host.LoginAs("production");
        var board = host.Get<ITaskCenterService>().GetBoard();
        var card = board.Action.Single(c => c.DocType == "Plan" && c.DocId == planId);
        Assert.Contains("بانتظار اعتمادك", card.Title);
        Assert.Contains("مدير النظام", card.Sender);
        Assert.Contains("800", card.Subtitle); // 500 + 300
        Assert.Contains("09/20", card.Due);
    }

    [Fact]
    public void DoneToday_Contains_Plans_Approved_By_The_Approver_Today()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int planId = SavePlan(host, "خطة اعتماد اليوم", "2026-09-20", "2026-09-20", ("2026-09-20", 500, 100));
        Assert.True(host.Get<IPlanningService>().SubmitPlan(planId).Ok);

        host.LoginAs("production");
        Assert.True(host.Get<IPlanningService>().ApprovePlan(planId).Ok);

        var board = host.Get<ITaskCenterService>().GetBoard();
        var done = board.DoneToday.Single(c => c.DocType == "Plan" && c.DocId == planId);
        Assert.Contains("اعتمدت اليوم", done.Title);
    }

    // ═══ مركز المهام: المطلوب اليوم / المتعثر (مدير الإنتاج) ═══

    [Fact]
    public void Board_Production_Sees_DueToday_And_Overdue_Days()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var today = DateTime.Today;
        var past = today.AddDays(-2);
        int planId = SavePlan(host, "خطة أيام", past.ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"),
            (past.ToString("yyyy-MM-dd"), 1000, 200),
            (today.ToString("yyyy-MM-dd"), 1000, 200));
        host.LoginAs("production");
        Assert.True(host.Get<IPlanningService>().ApprovePlan(planId).Ok);

        var board = host.Get<ITaskCenterService>().GetBoard();
        var todayCard = board.Action.Single(c => c.DocType == "Plan" && c.DocId == planId && !c.Overdue);
        Assert.Contains("المطلوب تشغيله اليوم", todayCard.Title);
        Assert.Contains(today.ToString("dd/MM"), todayCard.Title);

        var overdueCard = board.Action.Single(c => c.DocType == "Plan" && c.DocId == planId && c.Overdue);
        Assert.Contains("تحتاج مراجعة التخطيط", overdueCard.Title);

        // المتعثر أولوية أولى (يظهر قبل ما هو مستحق اليوم)
        Assert.True(board.Action.IndexOf(overdueCard) < board.Action.IndexOf(todayCard));
    }

    // ═══ تنبيهات الإدارة ═══

    [Fact]
    public void Board_Alerts_Shows_Overdue_Plan_Items_And_Active_Bypasses()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var yest = DateTime.Today.AddDays(-1);
        int planId = SavePlan(host, "خطة متعثرة", yest.ToString("yyyy-MM-dd"), yest.ToString("yyyy-MM-dd"),
            (yest.ToString("yyyy-MM-dd"), 500, 100));
        host.LoginAs("production");
        Assert.True(host.Get<IPlanningService>().ApprovePlan(planId).Ok);

        // تجاوز فحص نشط (B96) — يُدخَل مباشرة لاختبار اشتقاق التنبيه (قواعد إنشاء تجاوزها اختبارات B96)
        var db = host.Get<DatesErpDbContext>();
        db.ProductionDeliveries.Add(new ProductionDelivery
        {
            DocumentNumber = "PDL-B97-1",
            Status = DocStatuses.Issued,
            SourceType = "plan",
            SourceId = planId,
            BypassReason = "تسليم عاجل قبل اكتمال الفحص",
            DeliveryDate = DateTime.Today,
            ReceiptStatus = "None"
        });
        db.SaveChanges();

        var board = host.Get<ITaskCenterService>().GetBoard();
        Assert.Contains(board.Alerts, a => a.Contains("بند خطة معتمدة تجاوز موعده"));
        Assert.Contains(board.Alerts, a => a.Contains("تجاوز فحص نشط") && a.Contains("PDL-B97-1"));
    }
}
