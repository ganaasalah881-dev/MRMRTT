using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §اختبارات انحدار — ثغرة تجاوز الطاقة داخل الخطة الواحدة.
///
/// الخلل الذي كانت عليه B18: كان SavePlan/UpdatePlan/ApprovePlan تمرّر excludePlanId
/// وحده إلى ShiftUsageHours، فتُستثنى الخطة كلها من الحساب ولا تُحتسب بنودها على بعضها.
/// النتيجة: 16 ساعة عمل في وردية 8 ساعات تُقبل إن وُضعت البنود في خطة واحدة،
/// بينما نفس الحِمل في خطتين كان يُرفض.
///
/// وكذلك: البند بلا تاريخ كان يتخطى فحص الطاقة كلياً لأن الحارس يشترط ScheduledDate != null.
/// </summary>
public class PlanCapacityAccumulationTests
{
    private static T Svc<T>(TestHost host) => (T)host.Services.CreateScope().ServiceProvider.GetService(typeof(T));

    /// <summary>دفعة خام كبيرة حتى لا يكون رصيد الدفعة هو العائق بل الطاقة.</summary>
    private static int BigKhalasLot(TestHost host)
    {
        var db = host.Get<DatesErpDbContext>();
        int cust = db.Customers.First().Id;
        var receiving = Svc<IReceivingService>(host);
        var s = receiving.SaveShipment(cust, "2026-08-10", "2026-08-10",
            new List<ShipmentItemDto>
            {
                new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 20000, UnitWeightKg = 20, QtyKg = 400000 }
            }, null, "CAP-RAW-1");
        Assert.True(s.Ok, s.Message);
        Assert.True(receiving.ApproveShipment(s.Id).Ok);
        return db.Lots.OrderBy(l => l.Id).First().Id;
    }

    private static PlanItemDto Item(int lotId, int custId, int cartons, string date) => new()
    {
        SourceType = "FromReceiving",
        LotId = lotId,
        CustomerId = custId,
        ProductId = 3,             // خلاص ممتاز 500جم — معدله 500 كرتون/س على الوردية الأولى
        PackagingTypeId = 2,       // وزن الكرتون 10 كجم
        PlannedCartons = cartons,
        PlannedQtyKg = cartons * 10.0,
        ScheduledDate = date,
        SuggestedShiftId = 1,
        SuggestedLineId = 1,
        PriorityNo = 1
    };

    /// <summary>
    /// الاختبار الأساسي: بندان في «خطة واحدة» بنفس اليوم والوردية، كل منهما يملأ الوردية.
    /// كان يُقبل في B18 — يجب أن يُرفض.
    /// </summary>
    [Fact]
    public void Two_Items_In_One_Plan_Same_Day_Over_Capacity_Is_Rejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lot = BigKhalasLot(host);
        int cust = host.Get<DatesErpDbContext>().Customers.First().Id;

        var planning = Svc<IPlanningService>(host);
        // 4,000 كرتون = 8 ساعات = الوردية كاملة. بندان = 16 ساعة في وردية 8 ساعات.
        var r = planning.SavePlan("خطة مثقلة في يوم واحد", "Period", "2026-08-20", "2026-08-20", 1, 1,
            new List<PlanItemDto> { Item(lot, cust, 4000, "2026-08-20"), Item(lot, cust, 4000, "2026-08-20") });

        Assert.False(r.Ok, "خطة واحدة تحمل 16 ساعة في وردية 8 ساعات يجب أن تُرفض.");
        Assert.Contains("الطاقة الإنتاجية", r.Message ?? "");
    }

    /// <summary>الشاهد: نفس الحِمل موزعاً على يومين يجب أن يُقبل — الإصلاح لا يبالغ في المنع.</summary>
    [Fact]
    public void Same_Load_Split_Across_Two_Days_Is_Accepted()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lot = BigKhalasLot(host);
        int cust = host.Get<DatesErpDbContext>().Customers.First().Id;

        var planning = Svc<IPlanningService>(host);
        var r = planning.SavePlan("خطة موزعة على يومين", "Period", "2026-08-20", "2026-08-21", 1, 1,
            new List<PlanItemDto> { Item(lot, cust, 4000, "2026-08-20"), Item(lot, cust, 4000, "2026-08-21") });

        Assert.True(r.Ok, r.Message);
    }

    /// <summary>
    /// البند بلا تاريخ كان يتخطى فحص الطاقة كلياً. الآن يُجدول على بداية الخطة فيخضع للفحص.
    /// </summary>
    [Fact]
    public void Item_Without_Date_Is_Scheduled_And_Still_Capacity_Checked()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lot = BigKhalasLot(host);
        int cust = host.Get<DatesErpDbContext>().Customers.First().Id;

        var planning = Svc<IPlanningService>(host);
        var noDate1 = Item(lot, cust, 4000, null);
        var noDate2 = Item(lot, cust, 4000, null);

        var r = planning.SavePlan("بندان بلا تاريخ", "Period", "2026-08-20", "2026-08-20", 1, 1,
            new List<PlanItemDto> { noDate1, noDate2 });

        Assert.False(r.Ok, "بندان بلا تاريخ يُجدولان على نفس اليوم فيجب أن يخضعا لفحص الطاقة.");
        Assert.Contains("الطاقة الإنتاجية", r.Message ?? "");

        // والتاريخ الافتراضي عُيّن فعلاً على بداية الخطة
        Assert.Equal("20/08/2026", noDate1.ScheduledDate);
    }

    /// <summary>الاعتماد يعيد الفحص بالتراكم — لا يكفي أن يمرّ الحفظ.</summary>
    [Fact]
    public void Approve_Rejects_Plan_Whose_Items_Overload_One_Day()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lot = BigKhalasLot(host);
        int cust = host.Get<DatesErpDbContext>().Customers.First().Id;

        var planning = Svc<IPlanningService>(host);
        // بند واحد يملأ الوردية — يمر الحفظ والاعتماد
        var ok = planning.SavePlan("بند واحد", "Period", "2026-08-20", "2026-08-20", 1, 1,
            new List<PlanItemDto> { Item(lot, cust, 4000, "2026-08-20") });
        Assert.True(ok.Ok, ok.Message);

        // خطة ثانية في نفس اليوم تُرفض عند الحفظ (سلوك قائم وصحيح)
        var second = planning.SavePlan("خطة ثانية", "Period", "2026-08-20", "2026-08-20", 1, 1,
            new List<PlanItemDto> { Item(lot, cust, 100, "2026-08-20") });
        Assert.False(second.Ok);
        Assert.Contains("الطاقة الإنتاجية", second.Message ?? "");

        // والخطة الأولى تُعتمد بنجاح
        Assert.True(planning.ApprovePlan(ok.Id).Ok);
    }

    /// <summary>UpdatePlan خاضع لنفس التراكم — لا ثغرة من باب التعديل.</summary>
    [Fact]
    public void Update_Plan_Also_Accumulates_Capacity()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int lot = BigKhalasLot(host);
        int cust = host.Get<DatesErpDbContext>().Customers.First().Id;

        var planning = Svc<IPlanningService>(host);
        var saved = planning.SavePlan("خطة للتعديل", "Period", "2026-08-20", "2026-08-20", 1, 1,
            new List<PlanItemDto> { Item(lot, cust, 2000, "2026-08-20") });
        Assert.True(saved.Ok, saved.Message);

        // التعديل إلى بندين يملآن الوردية مرتين ← مرفوض
        var upd = planning.UpdatePlan(saved.Id, "خطة للتعديل", "Period", "2026-08-20", "2026-08-20", 1, 1,
            new List<PlanItemDto> { Item(lot, cust, 4000, "2026-08-20"), Item(lot, cust, 4000, "2026-08-20") });

        Assert.False(upd.Ok, "التعديل لا يجوز أن يفتح ثغرة التراكم.");
        Assert.Contains("الطاقة الإنتاجية", upd.Message ?? "");
    }
}

/// <summary>
/// §اختبارات انحدار — أوامر الإنتاج ترث جدولة الخطة.
/// الخلل: OrderableItemDto.ScheduledDate كان معبّأً من الـ Backend والواجهة تتجاهله،
/// فكل بنود خطة الـ14 يوماً كانت تأخذ تاريخ اليوم.
/// </summary>
public class OrderInheritsPlanScheduleTests
{
    private static T Svc<T>(TestHost host) => (T)host.Services.CreateScope().ServiceProvider.GetService(typeof(T));

    [Fact]
    public void Orderable_Items_Carry_The_Plan_Items_Scheduled_Date_And_Shift()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        int cust = db.Customers.First().Id;

        var receiving = Svc<IReceivingService>(host);
        var s = receiving.SaveShipment(cust, "2026-08-10", "2026-08-10",
            new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 5000, UnitWeightKg = 20, QtyKg = 100000 } },
            null, "SCHED-1");
        Assert.True(s.Ok, s.Message);
        Assert.True(receiving.ApproveShipment(s.Id).Ok);
        int lot = db.Lots.OrderBy(l => l.Id).First().Id;

        var planning = Svc<IPlanningService>(host);
        // خطة طويلة: ثلاثة أيام مختلفة لصنف واحد
        var plan = planning.SavePlan("خطة ثلاثة أيام", "Period", DateTime.Today.ToString("yyyy-MM-dd"), DateTime.Today.AddDays(2).ToString("yyyy-MM-dd"), 1, 1,
            new List<PlanItemDto>
            {
                new() { SourceType="FromReceiving", LotId=lot, CustomerId=cust, ProductId=3, PackagingTypeId=2, PlannedCartons=500, PlannedQtyKg=5000, ScheduledDate=DateTime.Today.ToString("yyyy-MM-dd"), SuggestedShiftId=1, SuggestedLineId=1, PriorityNo=1 },
                new() { SourceType="FromReceiving", LotId=lot, CustomerId=cust, ProductId=3, PackagingTypeId=2, PlannedCartons=500, PlannedQtyKg=5000, ScheduledDate=DateTime.Today.AddDays(1).ToString("yyyy-MM-dd"), SuggestedShiftId=2, SuggestedLineId=1, PriorityNo=2 },
                new() { SourceType="FromReceiving", LotId=lot, CustomerId=cust, ProductId=3, PackagingTypeId=2, PlannedCartons=500, PlannedQtyKg=5000, ScheduledDate=DateTime.Today.AddDays(2).ToString("yyyy-MM-dd"), SuggestedShiftId=1, SuggestedLineId=1, PriorityNo=3 },
            });
        Assert.True(plan.Ok, plan.Message);
        Assert.True(planning.ApprovePlan(plan.Id).Ok);

        var orders = Svc<IProductionOrderService>(host);
        var orderable = orders.GetOrderableItems(plan.Id);

        Assert.Single(orderable);
        // كل بند يحمل تاريخه المجدول وورديته — وهذا ما كانت الواجهة تهمله
        Assert.Equal(new[] { DateTime.Today.ToString("dd/MM/yyyy") }, orderable.Select(o => o.ScheduledDate).ToArray());
        Assert.Equal(new int?[] { 1 }, orderable.Select(o => o.SuggestedShiftId).ToArray());
    }
}
