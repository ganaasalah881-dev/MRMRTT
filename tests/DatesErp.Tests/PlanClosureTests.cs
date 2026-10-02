using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>§B79 — الاختبارات الإلزامية العشرة لشاشة «إقفال خطة الإنتاج».</summary>
public class PlanClosureTests
{
    private static T S<T>(TestHost h) => h.Services.CreateScope().ServiceProvider.GetRequiredService<T>();
    private static DatesErpDbContext Db(TestHost h)
        => new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(h.Connection).Options);

    private sealed class Env
    {
        public int PlanId; public int LotA; public int LotB; public int FinId; public int Fin2Id;
        public int CustA; public int CustB; public int Shift1; public int Shift2;
    }

    private static Env Setup(TestHost host, Func<Env, List<PlanItemDto>> schedule)
    {
        host.LoginAsAdmin();
        var scope = host.Services.CreateScope();
        var master = S<MasterDataService>(host);
        var rcv = S<IReceivingService>(host);
        var plan = S<IPlanningService>(host);
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var a = master.SaveCustomer(null, "CA", "شركة أ", "جملة", "1", "-", true);
        var b = master.SaveCustomer(null, "CB", "شركة ب", "جملة", "2", "-", true);
        var raw = master.SaveProductFull(null, "001-900", "خام الإقفال", "001", "Raw", "كجم", 0, 0, 0, null);
        var fin = master.SaveProductFull(null, "002-900", "سكري", "002", "Finished", "كرتون", 10, 5, 2, null, raw.Id);
        var fin2 = master.SaveProductFull(null, "002-901", "خلاص", "002", "Finished", "كرتون", 10, 5, 2, null, raw.Id);
        var shA = rcv.SaveShipment(a.Id, null, null, new System.Collections.Generic.List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = raw.Id, QtyKg = 300000, PackageCount = 15000, UnitWeightKg = 20 } });
        rcv.ApproveShipment(shA.Id);
        var shB = rcv.SaveShipment(b.Id, null, null, new System.Collections.Generic.List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = raw.Id, QtyKg = 300000, PackageCount = 15000, UnitWeightKg = 20 } });
        rcv.ApproveShipment(shB.Id);
        var lotA = db.Lots.Single(l => l.ShipmentId == shA.Id).Id;
        var lotB = db.Lots.Single(l => l.ShipmentId == shB.Id).Id;
        TestCapacityFixture.DefineMissingRates(host);
        var shifts = db.Shifts.AsNoTracking().OrderBy(s => s.Id).ToList();
        var env = new Env { LotA = lotA, LotB = lotB, FinId = fin.Id, Fin2Id = fin2.Id, CustA = a.Id, CustB = b.Id,
            Shift1 = shifts[0].Id, Shift2 = shifts[1].Id };
        var pl = plan.SavePlan("إقفال", "Period", "2026-09-01", "2026-09-30", 1, 1, schedule(env));
        Assert.True(pl.Ok, pl.Message); Assert.True(plan.ApprovePlan(pl.Id).Ok);
        env.PlanId = pl.Id; return env;
    }

    private static int AddOrder(TestHost host, Env e, int cust, int lot, int fin, string date, int shift, int qty = 1000)
    {
        host.SetBusinessDate(date);
        var orders = S<IProductionOrderService>(host);
        var db = host.Services.CreateScope().ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var piId = db.ProductionPlanItems.AsNoTracking().Single(i => i.PlanId == e.PlanId && i.ProductId == fin && i.CustomerId == cust && i.LotId == lot
            && i.ScheduledDate == DateTime.Parse(date, System.Globalization.CultureInfo.InvariantCulture) && i.SuggestedShiftId == shift).Id;
        var r = orders.SaveOrder("FromPlan", e.PlanId, cust, date, shift, 1,
            new System.Collections.Generic.List<OrderItemDto>
            { new() { PlanItemId = piId, LotId = lot, ProductId = fin, PlannedQtyKg = qty, PlannedCartons = qty / 10 } });
        Assert.True(r.Ok, r.Message);
        return r.Id;
    }

    private static void CloseOrderFully(TestHost host, int orderId, int qty = 1000)
    {
        Assert.True(S<IProductionOrderService>(host).ApproveOrder(orderId).Ok);
        Assert.True(S<IProductionOrderService>(host).StartOrder(orderId).Ok);
        var c = S<IExecutionService>(host).CloseProductionDay(orderId, qty, qty / 10, 0, 0, 0, false,
            new System.Collections.Generic.List<DowntimeDto>(), false, "تجربة", null);
        Assert.True(c.Ok, c.Message);
        var cr = S<IProductionOrderService>(host).CloseOrder(orderId); Assert.True(cr.Ok, cr.Message);
        IssueActualDelivery(host, orderId);
    }

    private static void IssueActualDelivery(TestHost host, int orderId)
    {
        var db = host.Get<DatesErpDbContext>();
        var executionId = db.ProductionExecutions.AsNoTracking()
            .Where(x => x.OrderId == orderId && x.IsDayClosed).Select(x => x.Id).First();
        var service = S<IProductionDeliveryService>(host);
        var draft = service.CreateDeliveryFromActual(executionId);
        Assert.True(draft.Ok, draft.Message);
        var issued = service.IssueDelivery(draft.Id);
        Assert.True(issued.Ok, issued.Message);
    }

    [Fact]
    public void T01_Single_Closed_Order_Allows_Plan_Closure()
    {
        using var host = new TestHost();
        var e = Setup(host, e => new List<PlanItemDto>
        {
            new() { SourceType="FromReceiving", CustomerId=e.CustA, LotId=e.LotA, ProductId=e.FinId, ScheduledDate="2026-09-01", SuggestedShiftId=e.Shift1, SuggestedLineId=1, PlannedQtyKg=1000, PlannedCartons=1000/10 }
        });
        var o = AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1);
        CloseOrderFully(host, o);
        var info = S<IPlanClosureService>(host).GetInfo(e.PlanId);
        Assert.True(info.IsClosed); // إقفال آلي بعد تحرير آخر بند للتسليم
        Assert.True(S<IPlanClosureService>(host).ClosePlanFinal(e.PlanId).Ok); // إقفال مكرر لا يغيّر القيود
        Assert.Equal("مقفلة", S<IPlanClosureService>(host).GetInfo(e.PlanId).StatusAr);
    }

    [Fact]
    public void T02_Four_Closed_One_InProgress_Blocks()
    {
        using var host = new TestHost();
        var e = Setup(host, e => new List<PlanItemDto>
        {
            new() { SourceType="FromReceiving", CustomerId=e.CustA, LotId=e.LotA, ProductId=e.FinId, ScheduledDate="2026-09-01", SuggestedShiftId=e.Shift1, SuggestedLineId=1, PlannedQtyKg=250, PlannedCartons=250/10 },
            new() { SourceType="FromReceiving", CustomerId=e.CustA, LotId=e.LotA, ProductId=e.FinId, ScheduledDate="2026-09-02", SuggestedShiftId=e.Shift1, SuggestedLineId=1, PlannedQtyKg=250, PlannedCartons=250/10 },
            new() { SourceType="FromReceiving", CustomerId=e.CustA, LotId=e.LotA, ProductId=e.Fin2Id, ScheduledDate="2026-09-03", SuggestedShiftId=e.Shift1, SuggestedLineId=1, PlannedQtyKg=250, PlannedCartons=250/10 },
            new() { SourceType="FromReceiving", CustomerId=e.CustB, LotId=e.LotB, ProductId=e.Fin2Id, ScheduledDate="2026-09-04", SuggestedShiftId=e.Shift1, SuggestedLineId=1, PlannedQtyKg=250, PlannedCartons=250/10 },
            new() { SourceType="FromReceiving", CustomerId=e.CustB, LotId=e.LotB, ProductId=e.FinId, ScheduledDate="2026-09-05", SuggestedShiftId=e.Shift2, SuggestedLineId=1, PlannedQtyKg=250, PlannedCartons=250/10 }
        });
        var firstOrder = AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1, 250);
        CloseOrderFully(host, firstOrder, 250);
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-02", e.Shift1, 250), 250);
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.Fin2Id, "2026-09-03", e.Shift1, 250), 250);
        CloseOrderFully(host, AddOrder(host, e, e.CustB, e.LotB, e.Fin2Id, "2026-09-04", e.Shift1, 250), 250);
        var db = host.Get<DatesErpDbContext>();
        int firstExec = db.ProductionExecutions.Single(x => x.OrderId == firstOrder).Id;
        int firstDelivery = db.ProductionDeliveries.Single(d => d.SourceId == firstExec).Id;
        Assert.False(db.ProductionPlans.Single(p => p.Id == e.PlanId).IsClosed);
        Assert.True(S<IProductionDeliveryService>(host).CancelDelivery(firstDelivery).Ok);
        Assert.False(db.ProductionPlans.Single(p => p.Id == e.PlanId).IsClosed);
        var open = AddOrder(host, e, e.CustB, e.LotB, e.FinId, "2026-09-05", e.Shift2, 250);
        Assert.True(S<IProductionOrderService>(host).ApproveOrder(open).Ok);
        Assert.True(S<IProductionOrderService>(host).StartOrder(open).Ok);   // قيد الإنتاج
        var svc = S<IPlanClosureService>(host);
        var r = svc.ClosePlanFinal(e.PlanId);
        Assert.False(r.Ok);
        Assert.Contains("أمر تسليم", r.Message);
        Assert.Contains(svc.GetInfo(e.PlanId).Blockers, b => b.Contains("قيد الإنتاج"));
    }

    [Fact]
    public void T03_MultiCustomer_All_Processed_Then_Summaries()
    {
        using var host = new TestHost();
        var e = Setup(host, e => new List<PlanItemDto>
        {
            new() { SourceType="FromReceiving", CustomerId=e.CustA, LotId=e.LotA, ProductId=e.FinId, ScheduledDate="2026-09-01", SuggestedShiftId=e.Shift1, SuggestedLineId=1, PlannedQtyKg=1000, PlannedCartons=1000/10 },
            new() { SourceType="FromReceiving", CustomerId=e.CustB, LotId=e.LotB, ProductId=e.Fin2Id, ScheduledDate="2026-09-02", SuggestedShiftId=e.Shift2, SuggestedLineId=1, PlannedQtyKg=1000, PlannedCartons=1000/10 }
        });
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1));
        CloseOrderFully(host, AddOrder(host, e, e.CustB, e.LotB, e.Fin2Id, "2026-09-02", e.Shift2));
        var svc = S<IPlanClosureService>(host);
        var clRes = svc.ClosePlanFinal(e.PlanId); Assert.True(clRes.Ok, clRes.Message);
        var info = svc.GetInfo(e.PlanId);
        Assert.Equal(2, info.Customers.Count);   // ملخص مستقل لكل عميل
    }

    [Fact]
    public void T04_MultiProduct_All_Completed_Product_Summaries()
    {
        using var host = new TestHost();
        var e = Setup(host, e => new List<PlanItemDto>
        {
            new() { SourceType="FromReceiving", CustomerId=e.CustA, LotId=e.LotA, ProductId=e.FinId, ScheduledDate="2026-09-01", SuggestedShiftId=e.Shift1, SuggestedLineId=1, PlannedQtyKg=1000, PlannedCartons=1000/10 },
            new() { SourceType="FromReceiving", CustomerId=e.CustA, LotId=e.LotA, ProductId=e.Fin2Id, ScheduledDate="2026-09-02", SuggestedShiftId=e.Shift1, SuggestedLineId=1, PlannedQtyKg=1000, PlannedCartons=1000/10 }
        });
        var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var fin2 = e.Fin2Id;
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1));
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, fin2, "2026-09-02", e.Shift1));
        var svc = S<IPlanClosureService>(host);
        var clRes = svc.ClosePlanFinal(e.PlanId); Assert.True(clRes.Ok, clRes.Message);
        Assert.Equal(2, svc.GetInfo(e.PlanId).Products.Count);
    }

    [Fact]
    public void T05_Monthly_Plan_One_Day_Closed_Not_Enough()
    {
        using var host = new TestHost();
        var e = Setup(host, e => new List<PlanItemDto>
        {
            new() { SourceType="FromReceiving", CustomerId=e.CustA, LotId=e.LotA, ProductId=e.FinId, ScheduledDate="2026-09-01", SuggestedShiftId=e.Shift1, SuggestedLineId=1, PlannedQtyKg=500, PlannedCartons=500/10 },
            new() { SourceType="FromReceiving", CustomerId=e.CustB, LotId=e.LotB, ProductId=e.FinId, ScheduledDate="2026-09-20", SuggestedShiftId=e.Shift1, SuggestedLineId=1, PlannedQtyKg=500, PlannedCartons=500/10 }
        });   // خطة حتى 30/09
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1, 500), 500);
        var later = AddOrder(host, e, e.CustB, e.LotB, e.FinId, "2026-09-20", e.Shift1, 500);   // أمر لاحق مفتوح
        var svc = S<IPlanClosureService>(host);
        Assert.False(svc.ClosePlanFinal(e.PlanId).Ok);   // يوم واحد مقفل لا يكفي
        Assert.Contains("مفتوح", svc.GetInfo(e.PlanId).Blockers.Single(x => x.Contains("مفتوح")));
    }

    [Fact]
    public void T06_MultiShift_Open_Shift_Blocks()
    {
        using var host = new TestHost();
        var e = Setup(host, e => new List<PlanItemDto>
        {
            new() { SourceType="FromReceiving", CustomerId=e.CustA, LotId=e.LotA, ProductId=e.FinId, ScheduledDate="2026-09-01", SuggestedShiftId=e.Shift1, SuggestedLineId=1, PlannedQtyKg=500, PlannedCartons=500/10 },
            new() { SourceType="FromReceiving", CustomerId=e.CustA, LotId=e.LotA, ProductId=e.FinId, ScheduledDate="2026-09-01", SuggestedShiftId=e.Shift2, SuggestedLineId=1, PlannedQtyKg=500, PlannedCartons=500/10 }
        });
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1, 500), 500);
        var o2 = AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift2, 500);  // نفس اليوم وردية ثانية مفتوحة
        var svc = S<IPlanClosureService>(host);
        Assert.False(svc.ClosePlanFinal(e.PlanId).Ok);
    }

    [Fact]
    public void T07_Cancelled_Order_Does_Not_Block_And_Is_Shown()
    {
        using var host = new TestHost();
        var e = Setup(host, e => new List<PlanItemDto>
        {
            new() { SourceType="FromReceiving", CustomerId=e.CustA, LotId=e.LotA, ProductId=e.FinId, ScheduledDate="2026-09-01", SuggestedShiftId=e.Shift1, SuggestedLineId=1, PlannedQtyKg=1000, PlannedCartons=1000/10 },
            new() { SourceType="FromReceiving", CustomerId=e.CustB, LotId=e.LotB, ProductId=e.Fin2Id, ScheduledDate="2026-09-03", SuggestedShiftId=e.Shift2, SuggestedLineId=1, PlannedQtyKg=1000, PlannedCartons=1000/10 }
        });
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1));
        var cancelled = AddOrder(host, e, e.CustB, e.LotB, e.Fin2Id, "2026-09-03", e.Shift2);
        Assert.True(S<IProductionOrderService>(host).CancelOrder(cancelled, "تجربة إلغاء").Ok);
        var svc = S<IPlanClosureService>(host);
        var info = svc.GetInfo(e.PlanId);
        Assert.True(info.CanClose);
        Assert.Contains(info.Orders, o => o.IsCancelled && o.StateAr == "ملغى");
        var clRes = svc.ClosePlanFinal(e.PlanId); Assert.True(clRes.Ok, clRes.Message);
    }

    // §43: إلغاء بلا سبب موثق ليس تسوية — بند الخطة الذي لم يُنتج يبقى مانع إقفال.
    [Fact]
    public void T08_Undocumented_Cancellation_Blocks_Closure()
    {
        using var host = new TestHost();
        var e = Setup(host, e => new List<PlanItemDto>
        {
            new() { SourceType="FromReceiving", CustomerId=e.CustA, LotId=e.LotA, ProductId=e.FinId, ScheduledDate="2026-09-01", SuggestedShiftId=e.Shift1, SuggestedLineId=1, PlannedQtyKg=1000, PlannedCartons=1000/10 },
            new() { SourceType="FromReceiving", CustomerId=e.CustB, LotId=e.LotB, ProductId=e.Fin2Id, ScheduledDate="2026-09-03", SuggestedShiftId=e.Shift2, SuggestedLineId=1, PlannedQtyKg=1000, PlannedCartons=1000/10 }
        });
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1));
        var cancelled = AddOrder(host, e, e.CustB, e.LotB, e.Fin2Id, "2026-09-03", e.Shift2);
        Assert.True(S<IProductionOrderService>(host).CancelOrder(cancelled).Ok); // بلا سبب ⟵ غير موثق
        var svc = S<IPlanClosureService>(host);
        var info = svc.GetInfo(e.PlanId);
        Assert.False(info.CanClose);
        Assert.Contains(info.Blockers, b => b.Contains("بلا سبب موثق"));
        Assert.False(svc.ClosePlanFinal(e.PlanId).Ok);
        // الحالة الموثقة (بسبب إلغاء) لا تمنع الإقفال — يغطيها T07.
    }

    // §v1.50.24: إعادة إقفال خطة مغلقة ليست خطأً بل رسالة ودّية — وُجدت كتلة جسده
    // بلا ترويسة (إعلان الاختبار مفقود في المصدر المنسوخ) فاستُعيد هنا.
    [Fact]
    public void T09_AlreadyClosed_ReClose_Is_Polite()
    {
        using var host = new TestHost();
        var e = Setup(host, e => new List<PlanItemDto>
        {
            new() { SourceType="FromReceiving", CustomerId=e.CustA, LotId=e.LotA, ProductId=e.FinId, ScheduledDate="2026-09-01", SuggestedShiftId=e.Shift1, SuggestedLineId=1, PlannedQtyKg=1000, PlannedCartons=1000/10 }
        });
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1));
        var svc = S<IPlanClosureService>(host);
        var clRes = svc.ClosePlanFinal(e.PlanId); Assert.True(clRes.Ok, clRes.Message);
        // §v1.50.24: إقفال اليوم المقفل ثانيةً رسالة ودّية لا خطأ — وما بعدها لا يغيّر شيئاً.
        var second = svc.ClosePlanFinal(e.PlanId);
        Assert.True(second.Ok, second.Message);
        Assert.Contains("مقفلة سابقاً", second.Message);
    }

    [Fact]
    public void T10_Reopen_Recalculates_Status()
    {
        using var host = new TestHost();
        var e = Setup(host, e => new List<PlanItemDto>
        {
            new() { SourceType="FromReceiving", CustomerId=e.CustA, LotId=e.LotA, ProductId=e.FinId, ScheduledDate="2026-09-01", SuggestedShiftId=e.Shift1, SuggestedLineId=1, PlannedQtyKg=1000, PlannedCartons=1000/10 }
        });
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1));
        var svc = S<IPlanClosureService>(host);
        var clRes = svc.ClosePlanFinal(e.PlanId); Assert.True(clRes.Ok, clRes.Message);
        var re = svc.ReopenPlan(e.PlanId, "تصحيح كمية");
        Assert.True(re.Ok, re.Message);
        var info = svc.GetInfo(e.PlanId);
        Assert.False(info.IsClosed);
        Assert.Equal("مكتملة", info.StatusAr);   // أُعيد الاحتساب من الأوامر
    }

    [Fact]
    public void T11_Editing_Closed_Plan_Rejected()
    {
        using var host = new TestHost();
        var e = Setup(host, e => new List<PlanItemDto>
        {
            new() { SourceType="FromReceiving", CustomerId=e.CustA, LotId=e.LotA, ProductId=e.FinId, ScheduledDate="2026-09-01", SuggestedShiftId=e.Shift1, SuggestedLineId=1, PlannedQtyKg=1000, PlannedCartons=1000/10 }
        });
        CloseOrderFully(host, AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1));
        var svc = S<IPlanClosureService>(host);
        var clRes = svc.ClosePlanFinal(e.PlanId); Assert.True(clRes.Ok, clRes.Message);
        var plan = S<IPlanningService>(host);
        var up = plan.UpdatePlan(e.PlanId, "تعديل", "Daily", "2026-09-01", "2026-09-30", 1, 1,
            new System.Collections.Generic.List<PlanItemDto>
            { new() { SourceType = "FromReceiving", LotId = e.LotA, CustomerId = e.CustA, ProductId = e.FinId, PlannedCartons = 10, PlannedQtyKg = 100 } });
        Assert.False(up.Ok);   // خطة مقفلة لا تُعدَّل
    }

    // ══════════ §B83: حالات المواصفة الإضافية (3، 7، 8) ══════════

    /// <summary>الحالة 3 — أمر مكتمل الإنتاج لكن غير مقفل: يمنع الإقفال حتى يُقفل الأمر رسمياً.</summary>
    [Fact]
    public void T12_Completed_But_Not_Closed_Order_Blocks()
    {
        using var host = new TestHost();
        var e = Setup(host, e => new List<PlanItemDto>
        {
            new() { SourceType="FromReceiving", CustomerId=e.CustA, LotId=e.LotA, ProductId=e.FinId, ScheduledDate="2026-09-01", SuggestedShiftId=e.Shift1, SuggestedLineId=1, PlannedQtyKg=1000, PlannedCartons=1000/10 }
        });
        var o = AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1);
        Assert.True(S<IProductionOrderService>(host).ApproveOrder(o).Ok);
        Assert.True(S<IProductionOrderService>(host).StartOrder(o).Ok);
        // إنتاج كامل ← الأمر «مكتمل» — لكن الإقفال الرسمي لم يتم
        var c = S<IExecutionService>(host).CloseProductionDay(o, 1000, 100, 0, 0, 0, false,
            new System.Collections.Generic.List<DowntimeDto>(), false, "إنتاج كامل", null);
        Assert.True(c.Ok, c.Message);

        var svc = S<IPlanClosureService>(host);
        var info = svc.GetInfo(e.PlanId);
        Assert.Equal(1, info.ClosedOrders); // إقفال يوم الإنتاج يقفل أمر الإنتاج آلياً
        Assert.True(info.CanClose); // قابلية تجهيز الخطة لا تتجاوز بوابة أمر التسليم
        var blocked = svc.ClosePlanFinal(e.PlanId);
        Assert.False(blocked.Ok);
        Assert.Contains("تسليم", blocked.Message);

        // إقفال الأمر وحده لا يُغلق الخطة؛ يجب إصدار أمر تسليم فعلي لكامل البند.
        Assert.True(S<IProductionOrderService>(host).CloseOrder(o).Ok);
        Assert.False(svc.ClosePlanFinal(e.PlanId).Ok);
        IssueActualDelivery(host, o);
        Assert.True(svc.GetInfo(e.PlanId).IsClosed);
    }

    /// <summary>§B95 — الحالة 7: عجز 200 من 1000 معالج بتسوية موثقة (سبب إغلاق + توقف مسجل + عودة المتبقي للخام): يسمح بالإقفال.</summary>
    [Fact]
    public void T13_Settled_Shortfall_With_Reason_Allows_Closure()
    {
        using var host = new TestHost();
        var e = Setup(host, e => new List<PlanItemDto>
        {
            new() { SourceType="FromReceiving", CustomerId=e.CustA, LotId=e.LotA, ProductId=e.FinId, ScheduledDate="2026-09-01", SuggestedShiftId=e.Shift1, SuggestedLineId=1, PlannedQtyKg=1000, PlannedCartons=1000/10 }
        });
        var o = AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1);
        Assert.True(S<IProductionOrderService>(host).ApproveOrder(o).Ok);
        Assert.True(S<IProductionOrderService>(host).StartOrder(o).Ok);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        double lotBefore = db.Lots.AsNoTracking().Single(l => l.Id == e.LotA).InStockQtyKg;

        // إنتاج 800 من 1000 + توقف معتمد ساعة — المتبقي يعود للخام بحركة موثقة
        var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();
        var r = exec.CloseProductionDay(o, 800, 80, 0, 0, 0, false,
            new System.Collections.Generic.List<DowntimeDto>
            { new() { Hours = 1, ReasonAr = "عطل معتمد في خط الإنتاج" } }, false, "عجز معالج");
        Assert.True(r.Ok, r.Message);

        // §B85/H1: صُرف الخام (1,000) وعاد المتبقي (200) — الفرق «معالج» لا مفقود
        double lotAfter = db.Lots.AsNoTracking().Single(l => l.Id == e.LotA).InStockQtyKg;
        Assert.Equal(lotBefore - 800, lotAfter, 1);

        // الأمر يُقفل بتسوية موثقة (سبب العجز) ثم الخطة تُقفل والفرق يظهر كفروقات معالجة
        var co = S<IProductionOrderService>(host).CloseOrder(o, "عجز معالج: عطل معتمد في خط الإنتاج");
        Assert.True(co.Ok, co.Message);
        using var chk = Db(host);
        Assert.Equal("عجز معالج: عطل معتمد في خط الإنتاج",
            chk.ProductionOrders.AsNoTracking().Single(x => x.Id == o).CloseReason);
        var svc = S<IPlanClosureService>(host);
        var info = svc.GetInfo(e.PlanId);
        Assert.True(info.CanClose, string.Join(" | ", info.Blockers));
        Assert.Equal(200, info.SettledVariance, 1);
        Assert.False(svc.ClosePlanFinal(e.PlanId).Ok); // التسوية لا تحل محل تحرير المخرجات الفعلية
        IssueActualDelivery(host, o);
        Assert.True(svc.GetInfo(e.PlanId).IsClosed);
    }

    /// <summary>الحالة 8 — عجز 200 غير معالج: الأمر لا يُقفل والخطة لا تُقفل.</summary>
    [Fact]
    public void T14_Unsettled_Shortfall_Blocks_Closure()
    {
        using var host = new TestHost();
        var e = Setup(host, e => new List<PlanItemDto>
        {
            new() { SourceType="FromReceiving", CustomerId=e.CustA, LotId=e.LotA, ProductId=e.FinId, ScheduledDate="2026-09-01", SuggestedShiftId=e.Shift1, SuggestedLineId=1, PlannedQtyKg=1000, PlannedCartons=1000/10 }
        });
        var o = AddOrder(host, e, e.CustA, e.LotA, e.FinId, "2026-09-01", e.Shift1);
        Assert.True(S<IProductionOrderService>(host).ApproveOrder(o).Ok);
        Assert.True(S<IProductionOrderService>(host).StartOrder(o).Ok);
        // إنتاج 800 من 1000 — والـ200 الباقية غير معالجة (لا إرجاع ولا توثيق)
        var c = S<IExecutionService>(host).CloseProductionDay(o, 800, 80, 0, 0, 0, false,
            new System.Collections.Generic.List<DowntimeDto>(), false, "جزئي", null);
        Assert.True(c.Ok, c.Message);

        var closeOrder = S<IProductionOrderService>(host).CloseOrder(o);
        Assert.False(closeOrder.Ok);   // INCOMPLETE_ORDER — العجز غير معالج

        var svc = S<IPlanClosureService>(host);
        var info = svc.GetInfo(e.PlanId);
        Assert.False(info.CanClose);
        Assert.Equal(0, info.SettledVariance, 1);
        var blocked = svc.ClosePlanFinal(e.PlanId);
        Assert.False(blocked.Ok);
        Assert.Contains("أمر", blocked.Message);   // الأمر المخالف مذكور بالاسم
    }
}
