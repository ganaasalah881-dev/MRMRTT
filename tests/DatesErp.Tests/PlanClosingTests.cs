using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// اختبارات نموذج «إقفال الخطة اليومية» (بديل جلسة التنفيذ) وقاعدتَي:
/// • لا خطة فوق خطة — لا إصدار خطتين لنفس اليوم والوردية.
/// • جودة التمور — فحص بعد يومَي تبريد: التسليم للتام مسموح عند الإقفال،
///   وتسليم العميل ينتظر اعتماد الفحص.
/// السيناريو: 20 ألف كجم خام ← 2500 كرتون + حشف 200 + نوى + متبقي في الصالة يرحَّل.
/// </summary>
public class PlanClosingTests
{
    private static ServiceProvider Svcs(TestHost host) => host.Services;

    /// <summary>بناء السلسلة الكاملة: استلام ← خطة معتمدة ← أمر تشغيل معتمد (خصم الخام).</summary>
    private static (int orderId, int planId, int lotId) BuildChain(TestHost host, double qtyKg, string planDate, int shiftId = 1)
    {
        host.SetBusinessDate(planDate);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var receiving = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();

        // رصيد مواد مساعدة (كرتون فارغ + ملصقات) يكفي اعتماد الأمر
        var whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        if (!db.StockBalances.Any(b => b.WarehouseId == whAux))
        {
            db.StockBalances.AddRange(
                new StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 100000 },
                new StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 100000 });
            db.SaveChanges();
        }

        var s = receiving.SaveShipment(1, null, null, new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 1, PackageCount = 1, UnitWeightKg = qtyKg, QtyKg = qtyKg } });
        if (!s.Ok) throw new InvalidOperationException(s.Message);
        var ap = receiving.ApproveShipment(s.Id);
        if (!ap.Ok) throw new InvalidOperationException(ap.Message);
        int lotId = db.Lots.OrderBy(l => l.Id).Last().Id;

        int cartons = (int)Math.Ceiling(qtyKg / 5); // عبوة 5 كجم
        var plan = planning.SavePlan("خطة الإقفال", "Daily", planDate, planDate, shiftId, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3, PackagingTypeId = 1,
                    PlannedQtyKg = qtyKg, PlannedCartons = cartons, ScheduledDate = planDate,
                    SuggestedShiftId = shiftId, SuggestedLineId = 1, PriorityNo = 1 }
        });
        if (!plan.Ok) throw new InvalidOperationException(plan.Message);
        var apr = planning.ApprovePlan(plan.Id);
        if (!apr.Ok) throw new InvalidOperationException(apr.Message);

        int planItemId = db.ProductionPlanItems.Single(i => i.PlanId == plan.Id).Id;
        var order = orders.SaveOrder("FromPlan", plan.Id, 1, planDate, shiftId, 1, new List<OrderItemDto>
        {
            new() { PlanItemId = planItemId, LotId = lotId, CustomerId = 1, ProductId = 3, PackagingTypeId = 1,
                    PlannedQtyKg = qtyKg, PlannedCartons = cartons }
        });
        if (!order.Ok) throw new InvalidOperationException(order.Message);
        var aord = orders.ApproveOrder(order.Id); // §صرف الخام من المخازن عند الاعتماد
        if (!aord.Ok) throw new InvalidOperationException(aord.Message);
        return (order.Id, plan.Id, lotId);
    }

    [Fact]
    public void CloseDay_FullScenario_Outputs_Downtimes_Quality()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        string day = DateTime.Today.AddDays(1).ToString("dd/MM/yyyy");
        var (orderId, planId, lotId) = BuildChain(host, 20000, day);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();

        // 20 ألف كجم خام ← 2500 كرتون × 7 كجم = 17500 + حشف 200 + نوى 100 + هالك 50 = متبقي 2150
        var r = exec.CloseProductionDay(orderId,
            producedKg: 17500, producedCartons: 3500,
            hashfKg: 200, nawaKg: 100, wastageKg: 50,
            carryToNextDay: true,
            downtimes: new List<DowntimeDto>
            {
                new() { Hours = 1.5, ReasonAr = "انقطاع كهرباء" },
                new() { Hours = 0.5, ReasonAr = "صيانة الخط" }
            },
            sendToQuality: true, notes: "إقفال يوم تجريبي");

        Assert.True(r.Ok, r.Message);
        Assert.Contains("أُقفل يوم الإنتاج", r.Message);

        var exe = db.ProductionExecutions.Include(e => e.Downtimes).Single(e => e.OrderId == orderId);
        Assert.True(exe.IsDayClosed);
        Assert.Equal(20000, exe.ConsumedRawKg, 1);           // كم خاماً استلمنا/استهلكنا
        Assert.Equal(2150, exe.RemainingInHallKg, 1);        // المتبقي في الصالة
        Assert.True(exe.CarryToNextDay);                     // رُحِّل لليوم التالي
        Assert.Equal(200, exe.HashfKg, 1);
        Assert.Equal(100, exe.NawaKg, 1);
        Assert.Equal(2, exe.Downtimes.Count);                // توقفنا كذا ساعة بسبب كذا وكذا
        Assert.True(exe.QualitySent);
        Assert.Equal(DateTime.Today.AddDays(2), exe.ExpectedQualityDate); // فحص بعد يومَي تبريد

        // الفحص المعلّق أُنشئ للجودة (غير معتمد — بانتظار نتيجة التبريد)
        var check = db.QualityChecks.Single(c => c.OrderId == orderId);
        Assert.False(check.IsApproved);
        Assert.Equal(DateTime.Today.AddDays(2), check.ExpectedCheckDate);

        // مزامنة المنتَج لبند الخطة + إقفال الخطة تلقائياً لاكتمال إنتاجها
        Assert.Equal(17500, db.ProductionPlanItems.Single(i => i.PlanId == planId).ProducedQtyKg, 1);

        // لا إقفالين على نفس الأمر
        var again = exec.CloseProductionDay(orderId, 100, 0, 0, 0, 0, false, null, false);
        Assert.False(again.Ok);
        Assert.Contains("مقفل مسبقاً", again.Message);
    }

    [Fact]
    public void CloseDay_Guards_OverProduction_Negative_OutputsExceed()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        string day = DateTime.Today.AddDays(1).ToString("dd/MM/yyyy");
        var (orderId, _, _) = BuildChain(host, 5000, day);

        using var scope = host.Services.CreateScope();
        var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();

        // §قاعدة توازن الإنتاج: زيادة الوزن عن الخام مقبولة (ماء التشغيل لا يُسجَّل)،
        // والفرق يظهر في تقرير توازن الإنتاج إجراءً رقابياً — لا رفض.
        var neg = exec.CloseProductionDay(orderId, -5, 0, 0, 0, 0, false, null, false);
        Assert.False(neg.Ok);   // الكميات السالبة ما زالت مرفوضة

        // §زيادة المخرجات عن الخام مقبولة — الفرق يظهر في تقرير توازن الإنتاج إجراءً رقابياً
        var exceed = exec.CloseProductionDay(orderId, 4000, 0, 1500, 0, 0, false, null, false);
        Assert.True(exceed.Ok, "زيادة الوزن من ماء التشغيل لا تُرفض: " + exceed.Message);
    }

    [Fact]
    public void QualityTwoDays_DeliveryToFinishedAllowed_CustomerDeliveryWaitsApproval()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        string day = DateTime.Today.AddDays(1).ToString("dd/MM/yyyy");
        var (orderId, _, lotId) = BuildChain(host, 20000, day);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();
        var fg = scope.ServiceProvider.GetRequiredService<IFinishedGoodsService>();
        var delivery = scope.ServiceProvider.GetRequiredService<ICustomerDeliveryService>();
        var quality = scope.ServiceProvider.GetRequiredService<IQualityService>();

        // الإقفال مع الإرسال للجودة (الفحص غير معتمد بعد — فترة تبريد يومان)
        // The order is packaged in 5-kg cartons: 3,750 cartons = 18,750 kg.
        var close = exec.CloseProductionDay(orderId, 18750, 3750, 200, 0, 0, false, null, sendToQuality: true);
        Assert.True(close.Ok, close.Message);
        int executionId = db.ProductionExecutions.Single(e => e.OrderId == orderId).Id;

        // ✅ الجودة سمحت بالتسليم للتام رغم أن الفحص لم يُعتمد بعد
        var rcpt = TestProductionDocumentFlow.SaveReceiptFromActual(host, orderId, null, day, new List<FinishedGoodsItemDto>
        { new() { ProductId = 3, LotId = lotId, PackageCount = 0, NetWeightKg = 18750 } });
        Assert.True(rcpt.Ok, rcpt.Message);
        Assert.Contains("أمر التسليم", rcpt.Message);
        Assert.True(fg.Issue(rcpt.Id).Ok);
        int rcptItemId = db.FinishedGoodsReceiptItems.Single(i => i.ReceiptId == rcpt.Id).Id;
        var recv = fg.Receive(rcpt.Id, new Dictionary<int, double> { [rcptItemId] = 18750 });
        Assert.True(recv.Ok, recv.Message);

        // ⛔ تسليم العميل مرفوض قبل اعتماد الفحص (العيب لا يظهر إلا بعد أن يبرد المنتج)
        var dlv = delivery.Save(1, day, orderId, new List<CustomerDeliveryItemDto>
        { new() { ProductId = 3, LotId = lotId, PackagingTypeId = 1, PackageCount = 100, QtyKg = 500 } });
        Assert.True(dlv.Ok, dlv.Message);
        var blocked = delivery.Approve(dlv.Id);
        Assert.False(blocked.Ok);
        Assert.Contains("فحص جودة", blocked.Message); // cooling itself is advisory; approval is mandatory

        // 🔬 بعد يومين: الجودة تسجل النتيجة (تغطية كاملة للإنتاج 18750) وتعتمدها
        host.LoginAsAdmin(); // helper switched to warehouse for the FGR posting
        var checkId = db.QualityChecks.Single(c => c.OrderId == orderId).Id;
        var saved = quality.SaveCheck(orderId, executionId, DateTime.Today.AddDays(2).ToString("dd/MM/yyyy"), "نهائي",
            new List<QualityItemDto> { new() { ProductId = 3, LotId = lotId, CheckedQtyKg = 18750, AcceptedQtyKg = 18700, RejectedQtyKg = 50 } });
        Assert.True(saved.Ok, saved.Message);
        Assert.True(quality.ApproveCheck(checkId).Ok);

        // ✅ الآن يُعتمد تسليم العميل
        var approved = delivery.Approve(dlv.Id);
        Assert.True(approved.Ok, approved.Message);
    }

    [Fact]
    public void PlanOverlap_Allowed_By_Capacity_Not_Blanket_Ban()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var receiving = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();

        // رصيد يكفي أكثر من خطة
        var s = receiving.SaveShipment(1, null, null, new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 1, PackageCount = 1, UnitWeightKg = 8000, QtyKg = 8000 } });
        Assert.True(receiving.ApproveShipment(s.Id).Ok);
        int lotId = db.Lots.OrderBy(l => l.Id).Last().Id;
        string day = DateTime.Today.AddDays(3).ToString("dd/MM/yyyy");

        PlanItemDto Item() => new()
        {
            SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3, PackagingTypeId = 1,
            PlannedQtyKg = 1000, PlannedCartons = 200, ScheduledDate = day, SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1
        };

        var p1 = planning.SavePlan("الخطة الأولى", "Daily", day, day, 1, 1, new List<PlanItemDto> { Item() });
        Assert.True(p1.Ok, p1.Message);
        Assert.True(planning.ApprovePlan(p1.Id).Ok);

        // §منع حسب الطاقة لا منع أعمى: نفس اليوم والوردية مسموح ما دامت الطاقة المتبقية تكفي
        var p2 = planning.SavePlan("الخطة الثانية", "Daily", day, day, 1, 1, new List<PlanItemDto> { Item() });
        Assert.True(p2.Ok, p2.Message);
        var ap2 = planning.ApprovePlan(p2.Id);
        Assert.True(ap2.Ok, ap2.Message); // تُعتمد — الطاقة المتبقية تكفي (200 كرتون فقط)

        // يوم آخر ← مسموح — §B80: بنود الخطة بتواريخ داخل فترتها (تاريخ كل إنتاج مفروض)
        string day2 = DateTime.Today.AddDays(4).ToString("dd/MM/yyyy");
        PlanItemDto Item2() => new()
        {
            SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3, PackagingTypeId = 1,
            PlannedQtyKg = 1000, PlannedCartons = 200, ScheduledDate = day2, SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1
        };
        var p3 = planning.SavePlan("خطة اليوم الآخر", "Daily", day2, day2, 1, 1, new List<PlanItemDto> { Item2() });
        Assert.True(planning.ApprovePlan(p3.Id).Ok);

        // نفس اليوم وردية مختلفة ← مسموح
        var p4 = planning.SavePlan("خطة الوردية الأخرى", "Daily", day, day, 2, 1, new List<PlanItemDto> { Item() });
        Assert.True(planning.ApprovePlan(p4.Id).Ok);
    }

    [Fact]
    public void AutoClose_PlanClosedWhenAllProduced_ReservationsReleased()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        string day = DateTime.Today.AddDays(1).ToString("dd/MM/yyyy");
        var (orderId, planId, lotId) = BuildChain(host, 10000, day);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();

        Assert.Equal(10000, db.Lots.AsNoTracking().Single(l => l.Id == lotId).ReservedQtyKg, 1);

        // اكتمال الإنتاج لا يغلق الخطة؛ يجب إصدار أمر التسليم الفعلي أيضاً.
        var r = exec.CloseProductionDay(orderId, 10000, 2000, 0, 0, 0, false, null, false);
        Assert.True(r.Ok, r.Message);
        Assert.False(db.ProductionPlans.AsNoTracking().Single(p => p.Id == planId).IsClosed);
        var closure = host.Services.CreateScope().ServiceProvider.GetRequiredService<IPlanClosureService>();
        Assert.False(closure.ClosePlanFinal(planId).Ok);
        foreach (var oid in db.ProductionOrders.AsNoTracking().Where(o => o.SourcePlanId == planId).Select(o => o.Id).ToList())
            Assert.True(host.Services.CreateScope().ServiceProvider.GetRequiredService<IProductionOrderService>().CloseOrder(oid).Ok);
        var delivery = host.Services.CreateScope().ServiceProvider.GetRequiredService<IProductionDeliveryService>();
        int executionId = db.ProductionExecutions.Single(e => e.OrderId == orderId).Id;
        var draft = delivery.CreateDeliveryFromActual(executionId, day);
        Assert.True(draft.Ok, draft.Message);
        Assert.True(delivery.IssueDelivery(draft.Id).Ok);
        var cl = closure.ClosePlanFinal(planId);
        Assert.True(cl.Ok, cl.Message);
        Assert.True(db.ProductionPlans.AsNoTracking().Single(p => p.Id == planId).IsClosed);
        Assert.Equal(0, db.Lots.AsNoTracking().Single(l => l.Id == lotId).ReservedQtyKg, 1);
    }
}
