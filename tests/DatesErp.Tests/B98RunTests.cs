using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B98 — تشغيل اليوم بإدخال يدوي موجّه + دورة أمر التشغيل (بدء/إيقاف/استئناف/إقفال) + سجل التنفيذ.
/// </summary>
public class B98RunTests
{
    private static readonly DateTime Today = DateTime.Today;
    private static string D(int offset) => (Today.AddDays(offset)).ToString("yyyy-MM-dd");
    private static string DdMmYy(string iso)
    {
        var p = iso.Split('-');
        return $"{p[2]}/{p[1]}/{p[0]}";
    }

    /// <summary>
    /// خطة متعددة الأيام من دفعة خام (إيراد ← مورد) — المنتج 3 بالعبوة 1 (5 كجم/كرتون).
    /// </summary>
    private static (int planId, Dictionary<string, int> itemByDay) SeedPlan(TestHost host, (string day, int cartons)[] days)
    {
        var db = host.Get<DatesErpDbContext>();
        int whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 8000 },
            new StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 8000 });
        db.SaveChanges();

        var rcv = host.Get<IReceivingService>();
        var s = rcv.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        {
            new() { TreatmentRequired = false, ProductId = 1, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000 }
        });
        Assert.True(s.Ok, s.Message);
        Assert.True(rcv.ApproveShipment(s.Id).Ok);
        int lot = db.Lots.OrderBy(l => l.Id).Last().Id;

        var planSvc = host.Get<IPlanningService>();
        var items = days.Select(d => new PlanItemDto
        {
            SourceType = "FromReceiving", LotId = lot, CustomerId = 1,
            ProductId = 3, PackagingTypeId = 1,
            PlannedQtyKg = d.cartons * 5.0, PlannedCartons = d.cartons,
            ScheduledDate = d.day, SuggestedShiftId = 1, SuggestedLineId = 1
        }).ToList();
        var p = planSvc.SavePlan("خطة B98", days.Length > 1 ? "Period" : "Daily", days[0].day, days[^1].day, 1, 1, items);
        Assert.True(p.Ok, p.Message);
        Assert.True(planSvc.ApprovePlan(p.Id).Ok);

        var itemByDay = new Dictionary<string, int>();
        foreach (var (day, _) in days)
            itemByDay[day] = db.ProductionPlanItems.Single(i => i.PlanId == p.Id && i.ScheduledDate!.Value.Date == (DateTime.Parse(day).Date)).Id;
        return (p.Id, itemByDay);
    }

    /// <summary>إصدار يدوي لسطر واحد — يعيد رقم الأمر المنشأ.</summary>
    private static int IssueOne(TestHost host, int planId, string day, int itemId, int cartons)
    {
        // Fixtures execute the approved quantity in full; the old partial input is no longer authoritative.
        Assert.Equal(D(0), day);
        var svc = host.Get<IProductionOrderService>();
        var r = svc.IssueTodayOrders(); Assert.True(r.Ok, r.Message);
        int id = host.Get<DatesErpDbContext>().ProductionOrders.Single(o => o.SourcePlanId == planId).Id;
        var approved = svc.ApproveOrder(id); Assert.True(approved.Ok, approved.Message);
        return id;
    }

    [Fact]
    public void TodaySheet_Shows_Only_Approved_Today_And_Retires_Overdue_Selection()
    {
        using var host = new TestHost(); host.LoginAsAdmin();
        var (planId, _) = SeedPlan(host, new (string, int)[] { (D(-2), 50), (D(0), 100), (D(1), 60) });
        var today = host.Get<IProductionOrderService>().GetTodayProduction();
        var row = Assert.Single(today.Rows); Assert.Equal(100, row.PlannedCartons); Assert.Equal(500, row.PlannedKg);
        Assert.Equal(1, row.ShiftId); Assert.True(today.CanIssue);
        Assert.Throws<DatesErp.Core.Exceptions.DomainException>(() => host.Get<IDayRunService>().GetDayRun(planId, D(-2)));
    }

    [Fact]
    public void IssueToday_Creates_Linked_Full_Quantity_And_Approved_Order_Is_Scheduled()
    {
        using var host = new TestHost(); host.LoginAsAdmin();
        var (planId, byDay) = SeedPlan(host, new (string, int)[] { (D(0), 100) });
        int id = IssueOne(host, planId, D(0), byDay[D(0)], 100);
        var order = host.Get<DatesErpDbContext>().ProductionOrders.Include(o => o.Items).Single(o => o.Id == id);
        Assert.Equal(DocStatuses.Scheduled, order.Status); Assert.Equal(planId, order.SourcePlanId);
        var item = Assert.Single(order.Items); Assert.Equal(byDay[D(0)], item.PlanItemId);
        Assert.Equal(100, item.PlannedCartons); Assert.Equal(500, item.PlannedQtyKg);
        var sheet = host.Get<IProductionOrderService>().GetTodayProduction();
        Assert.False(sheet.CanIssue); Assert.Equal(100, Assert.Single(sheet.Rows).PlannedCartons);
    }

    [Fact]
    public void Retired_IssueSelected_Rejects_Even_Formerly_Allowed_Partials()
    {
        using var host = new TestHost(); host.LoginAsAdmin();
        var (planId, byDay) = SeedPlan(host, new (string, int)[] { (D(0), 100) });
        var old = host.Get<IDayRunService>();
        Assert.False(old.IssueSelected(planId, D(0), new()).Ok);
        foreach (int quantity in new[] { 0, 20, 40, 100, 101 })
            Assert.False(old.IssueSelected(planId, D(0), new() { new() { ItemId = byDay[D(0)], Cartons = quantity } }).Ok);
        Assert.Empty(host.Get<DatesErpDbContext>().ProductionOrders);
        Assert.True(host.Get<IProductionOrderService>().IssueTodayOrders().Ok);
        Assert.Equal(100, host.Get<DatesErpDbContext>().ProductionOrderItems.Single().PlannedCartons);
    }

    [Fact]
    public void Lifecycle_Start_Stop_Resume_With_Reason_OnCard()
    {
        using var host = new TestHost();
        host.LoginAs("admin");
        var (planId, byDay) = SeedPlan(host, new (string, int)[] { (D(0), 100) });
        int orderId = IssueOne(host, planId, D(0), byDay[D(0)], 100);

        host.LoginAs("production");
        var orders = host.Get<IProductionOrderService>();
        var db = host.Get<DatesErpDbContext>();

        Assert.True(orders.StartOrder(orderId).Ok);
        Assert.Equal(DocStatuses.InProgress, db.ProductionOrders.Single(o => o.Id == orderId).Status);

        var shortStop = orders.StopOrder(orderId, "عطل");
        Assert.False(shortStop.Ok); // الحارس على الخادم: لا إيقاف بلا سبب كافٍ
        var stop = orders.StopOrder(orderId, "عطل في ماكينة العجن — بانتظار الصيانة");
        Assert.True(stop.Ok);
        var stoppedOrder = db.ProductionOrders.Single(o => o.Id == orderId);
        Assert.Equal(DocStatuses.Stopped, stoppedOrder.Status);
        Assert.Equal("عطل في ماكينة العجن — بانتظار الصيانة", stoppedOrder.StatusReason);

        Assert.False(orders.StopOrder(orderId, "توقف مرة أخرى").Ok); // مكرر — ليس قيد التنفيذ

        // السبب يظهر على بطاقة المهمة (لا في الملاحظات فقط)
        var board = host.Get<ITaskCenterService>().GetBoard();
        Assert.Contains(board.InFlight, c => c.DocId == orderId && c.Reason == "عطل في ماكينة العجن — بانتظار الصيانة");

        Assert.True(orders.ResumeOrder(orderId).Ok);
        var resumed = db.ProductionOrders.Single(o => o.Id == orderId);
        Assert.Equal(DocStatuses.InProgress, resumed.Status);
        Assert.Null(resumed.StatusReason);
    }

    [Fact]
    public void CloseDay_Writes_Production_QC_And_PlanSync()
    {
        using var host = new TestHost();
        host.LoginAs("admin");
        var (planId, byDay) = SeedPlan(host, new (string, int)[] { (D(0), 100) });
        int orderId = IssueOne(host, planId, D(0), byDay[D(0)], 60); // أمر 60 كرتون = 300 كجم

        host.LoginAs("production");
        Assert.True(host.Get<IProductionOrderService>().StartOrder(orderId).Ok);
        var exe = host.Get<IExecutionService>();
        var r = exe.CloseProductionDay(orderId, 300, 60, 0, 0, 0, false, new List<DowntimeDto>(), true);
        Assert.True(r.Ok, r.Message);

        var db = host.Get<DatesErpDbContext>();
        var order = db.ProductionOrders.Include(o => o.Items).Single(o => o.Id == orderId);
        Assert.Equal(300.0, order.Items.Single().ProducedQtyKg, 1);
        // الفعلي 60 من أصل100 لا يحوّل كمية الأمر إلى60 ولا يدّعي اكتمال الخطة.
        Assert.Equal(100, order.Items.Single().PlannedCartons);
        Assert.Equal(500, order.Items.Single().PlannedQtyKg);
        Assert.Equal(DocStatuses.Completed, order.Status); // إقفال يوم الأمر واحد ونهائي؛ النقص يبقى في الخطة

        // المزامنة إلى الخطة: المنتج يُجمع على بند الخطة
        var planItem = db.ProductionPlanItems.Single(i => i.Id == byDay[D(0)]);
        Assert.True(planItem.ProducedQtyKg >= 299.9, $"بند الخطة يجمع المنتج: {planItem.ProducedQtyKg}");

        // جودة: أمر فحص (QC) مُنشأ للأمر عند الإرسال للجودة
        // §B102 — تصحيح الاختبار نفسه: كان يفحص وجود «أمر إنتاج ثانٍ» بلا علاقة بالجودة (لم يُشغَّل قط في خطه الأصلي)
        Assert.True(db.QualityChecks.Any(c => c.OrderId == orderId && c.Status == DocStatuses.Submitted),
            "أمر جودة (QC) يجب أن يُنشأ عند الإرسال للجودة");
    }

    [Fact]
    public void ExecutionLog_Shows_States_Without_Issuing_Past_Or_Future_Work()
    {
        using var host = new TestHost(); host.LoginAsAdmin();
        var (planId, byDay) = SeedPlan(host, new (string, int)[] { (D(-2), 50), (D(0), 100), (D(1), 60), (D(2), 40) });
        int id = IssueOne(host, planId, D(0), byDay[D(0)], 100);
        host.LoginAs("production"); Assert.True(host.Get<IProductionOrderService>().StartOrder(id).Ok);
        Assert.True(host.Get<IExecutionService>().CloseProductionDay(id, 125, 25, 0, 0, 0, false, new(), false).Ok);
        var log = host.Get<IPlanProgressService>().GetExecutionLog(planId); Assert.Equal(4, log.Count);
        Assert.True(log[0].Overdue); Assert.Equal(0, log[0].OrderedKg); Assert.Equal(0, log[0].ProducedKg);
        Assert.Equal(125, log[1].ProducedKg); Assert.Equal(500, log[1].OrderedKg);
        Assert.Equal("قيد التنفيذ 🔵", log[1].StatusAr);
        Assert.All(log.Skip(2), row => { Assert.Equal(0, row.OrderedKg); Assert.Equal("لم يبدأ ⚪", row.StatusAr); });
    }

    [Fact]
    public void DueDayCard_Becomes_ReadyOrderCard_After_Issue()
    {
        using var host = new TestHost();
        host.LoginAs("admin");
        var (planId, byDay) = SeedPlan(host, new (string, int)[] { (D(0), 100) });

        host.LoginAs("production");
        var board0 = host.Get<ITaskCenterService>().GetBoard();
        var due = board0.Action.Single(c => c.DocType == "Plan" && c.DocId == planId);
        Assert.Equal("TodayOrders", due.Action); // §B98 — البطاقة تفتح «تشغيل اليوم»
        Assert.False(due.Overdue);

        IssueOne(host, planId, D(0), byDay[D(0)], 100); // تشغيل كامل لليوم

        var board1 = host.Get<ITaskCenterService>().GetBoard();
        Assert.DoesNotContain(board1.Action, c => c.DocType == "Plan" && c.DocId == planId); // غادر «المطلوب اليوم»
        Assert.Contains(board1.Action, c => c.DocType == "Order" && c.Title.Contains("جاهز للبدء"));
    }
}
