using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §المعالجة والتعقيم — **المرحلة 3: ربط التخطيط والإنتاج ومنع الصرف.**
///
/// المرحلة 2 بنت المحرك، وهذه تصله بالأبواب: التخطيط لا يعرض ولا يعتمد خاماً
/// تحت المعالجة، وأمر الإنتاج لا يصرفه، و<c>ConsumeLot</c> شبكة الأمان الأخيرة.
///
/// **الترتيب الإلزامي المطبَّق:** المواضع 7-11 (التخطيط والأوامر) قبل 12
/// (<c>ConsumeLot</c>) — لولاه لخطط المستخدم بنجاح ثم رُفض عند الإقفال، وذلك
/// أسوأ من غياب المنع.
/// </summary>
public class RawTreatmentPhase3Tests
{
    private const double BasketKg = 20;

    private sealed class Ctx
    {
        public TestHost Host;
        public int LotId;
        public IRawTreatmentService Trt;
        public IPlanningService Plan;
        public DatesErpDbContext Db;
    }

    private static Ctx Setup(bool requiresTreatment = true, double baskets = 5000)
    {
        var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();

        var raw = db.Products.First(p => p.ProductCode == "001-001");
        raw.RequiresTreatment = requiresTreatment;
        db.SaveChanges();

        var rec = host.Get<IReceivingService>();
        var r = rec.SaveShipment(1, "2026-09-01", "2026-09-01", new List<ShipmentItemDto>
        {
            new() { TreatmentRequired = false, ProductId = raw.Id, PackagingTypeId = 3, PackageCount = (int)baskets,
                    UnitWeightKg = BasketKg, QtyKg = baskets * BasketKg, ReceiptUnit = "سلة" }
        });
        Assert.True(r.Ok, r.Message);
        Assert.True(rec.ApproveShipment(r.Id).Ok);
        // Fixture تاريخي: هذه الاختبارات تحافظ على محرك السجلات السابقة، لا عقد الاستلام الجديد.
        foreach (var i in db.ShipmentItems.Where(i => i.ShipmentId == r.Id)) i.TreatmentRequired = null;
        foreach (var l in db.Lots.Where(l => l.ShipmentId == r.Id)) l.TreatmentReadyQtyKg = 0;
        db.SaveChanges();

        return new Ctx
        {
            Host = host,
            LotId = db.Lots.OrderBy(l => l.Id).Last().Id,
            Trt = host.Get<IRawTreatmentService>(),
            Plan = host.Get<IPlanningService>(),
            Db = db
        };
    }

    private static int Start(Ctx c, double baskets, double hours, double daysAgo = 0)
    {
        var r = c.Trt.Start(new TreatmentStartDto
        {
            LotId = c.LotId, QtyKg = baskets * BasketKg, PackageCount = (int)baskets,
            DurationHours = hours, StartedAt = DateTime.Now.AddDays(-daysAgo)
        });
        Assert.True(r.Ok, r.Message);
        return r.Id;
    }

    private static OpResult MakePlan(Ctx c, double kg, string date) =>
        c.Plan.SavePlan("خطة", "Daily", date, date, 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = c.LotId, CustomerId = 1, ProductId = 3,
                    PlannedQtyKg = kg, PlannedCartons = (int)(kg / 7.5), ScheduledDate = date,
                    SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 }
        });

    // ═══════════ الموضع 7: GetAvailableLots ═══════════

    /// <summary>الشاشة لا تعرض الخام الداخل في المعالجة ضمن «المتبقي».</summary>
    [Fact]
    public void Site7_AvailableLots_Excludes_UnderTreatment()
    {
        var c = Setup();
        using (c.Host)
        {
            var before = c.Plan.GetAvailableLots().Single(l => l.LotId == c.LotId);
            Assert.Equal(100000, before.RemainingKg, 1);

            Start(c, 1000, 168);

            var after = c.Plan.GetAvailableLots().Single(l => l.LotId == c.LotId);
            Assert.Equal(80000, after.RemainingKg, 1);
            Assert.Equal(20000, after.UnderTreatmentKg, 1);
            Assert.True(after.RequiresTreatment);
        }
    }

    /// <summary>
    /// **السلوك القديم محفوظ حرفياً**: استدعاء بلا تاريخ لا يكسر أي شاشة قائمة،
    /// وصنف بلا اشتراط معالجة لا يتأثر إطلاقاً.
    /// </summary>
    [Fact]
    public void Site7_Legacy_Call_And_Untreated_Product_Unaffected()
    {
        var c = Setup(requiresTreatment: false);
        using (c.Host)
        {
            Start(c, 1000, 168); // يمكن معالجته اختيارياً حتى لو لم يُشترط

            var dto = c.Plan.GetAvailableLots().Single(l => l.LotId == c.LotId);
            Assert.False(dto.RequiresTreatment);
            // المتاح للتاريخ = المتبقي كما كان — لا تقييد
            Assert.Equal(dto.RemainingKg, dto.AvailableForDateKg, 1);
        }
    }

    /// <summary>المتاح حسب تاريخ الخطة: يزداد كلما نضجت معالجة (البند 4).</summary>
    [Fact]
    public void Site7_AvailableForDate_Grows_As_Treatments_Mature()
    {
        var c = Setup();
        using (c.Host)
        {
            Start(c, 500, 168);   // ينضج بعد 7 أيام
            Start(c, 500, 240);   // ينضج بعد 10 أيام
            var today = DateTime.Now.Date;

            double At(DateTime d) => c.Plan.GetAvailableLots(null, d)
                .Single(l => l.LotId == c.LotId).AvailableForDateKg;

            Assert.Equal(0, At(today.AddDays(3)), 1);
            Assert.Equal(10000, At(today.AddDays(8)), 1);
            Assert.Equal(20000, At(today.AddDays(11)), 1);
        }
    }

    // ═══════════ حارس الاعتماد ═══════════

    /// <summary>
    /// اعتماد خطة على خام لن يجهز في تاريخها **يُرفض**، والرسالة تذكر الرقم
    /// والسبب وأقرب موعد ممكن — لا «الكمية غير كافية» وحدها.
    /// </summary>
    [Fact]
    public void Approve_Rejected_When_Raw_Not_Ready_By_Plan_Date()
    {
        var c = Setup();
        using (c.Host)
        {
            Start(c, 1000, 240);   // 20,000 كجم تنضج بعد 10 أيام

            // خطة بعد 3 أيام على 15,000 كجم: لا شيء جاهز ولا شيء ينضج قبلها
            var p = MakePlan(c, 15000, DateTime.Now.AddDays(3).ToString("dd/MM/yyyy"));
            Assert.True(p.Ok, p.Message);

            var appr = c.Plan.ApprovePlan(p.Id);
            Assert.False(appr.Ok);
            Assert.Contains("لن يكون جاهزاً", appr.Message);
            Assert.Contains("أقرب موعد", appr.Message);

            // الخطة بقيت غير معتمدة — لا اعتماد صامت
            Assert.False(c.Db.ProductionPlans.AsNoTracking().First(x => x.Id == p.Id).IsApproved);
        }
    }

    /// <summary>وبعد نضج المعالجة والإفراج عنها، الاعتماد يمر.</summary>
    [Fact]
    public void Approve_Succeeds_After_Treatment_Released()
    {
        var c = Setup();
        using (c.Host)
        {
            int t = Start(c, 1000, 168, daysAgo: 8);
            Assert.True(c.Trt.Release(t, 1000 * BasketKg).Ok);

            var p = MakePlan(c, 15000, DateTime.Now.AddDays(3).ToString("dd/MM/yyyy"));
            Assert.True(p.Ok, p.Message);
            var appr = c.Plan.ApprovePlan(p.Id);
            Assert.True(appr.Ok, appr.Message);
        }
    }

    /// <summary>
    /// الخطة على صنف لا يشترط معالجة تُعتمد كما كانت دائماً — **لا انحدار**
    /// على المسار القائم الذي لا علاقة له بالتعقيم.
    /// </summary>
    [Fact]
    public void Approve_Unaffected_For_Product_Without_Treatment_Flag()
    {
        var c = Setup(requiresTreatment: false);
        using (c.Host)
        {
            var p = MakePlan(c, 15000, DateTime.Now.AddDays(3).ToString("dd/MM/yyyy"));
            Assert.True(p.Ok, p.Message);
            var appr = c.Plan.ApprovePlan(p.Id);
            Assert.True(appr.Ok, appr.Message);
        }
    }

    /// <summary>
    /// الطلب يُجمَّع لكل (دفعة، يوم): بندان صغيران لا يتسللان معاً فوق المتاح.
    /// </summary>
    [Fact]
    public void Approve_Aggregates_Demand_Per_Lot_And_Day()
    {
        var c = Setup();
        using (c.Host)
        {
            int t = Start(c, 500, 168, daysAgo: 8);
            Assert.True(c.Trt.Release(t, 500 * BasketKg).Ok);   // 10,000 كجم جاهزة فقط

            string day = DateTime.Now.AddDays(2).ToString("dd/MM/yyyy");
            var p = c.Plan.SavePlan("خطة مجزأة", "Daily", day, day, 1, 1, new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = c.LotId, CustomerId = 1, ProductId = 3,
                        PlannedQtyKg = 6000, PlannedCartons = 800, ScheduledDate = day,
                        SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 },
                new() { SourceType = "FromReceiving", LotId = c.LotId, CustomerId = 1, ProductId = 3,
                        PlannedQtyKg = 6000, PlannedCartons = 800, ScheduledDate = day,
                        SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 2 }
            });
            Assert.True(p.Ok, p.Message);

            // 6,000 + 6,000 = 12,000 > 10,000 الجاهزة ⟵ يُرفض رغم أن كل بند وحده يمر
            var appr = c.Plan.ApprovePlan(p.Id);
            Assert.False(appr.Ok);
            Assert.Contains("لن يكون جاهزاً", appr.Message);
        }
    }

    // ═══════════ الموضع 12: شبكة الأمان الأخيرة ═══════════

    /// <summary>
    /// <c>ConsumeLot</c> يرفض صرف ما لم تكتمل معالجته — حتى لو التفّ مسار على
    /// حراس التخطيط. يُختبر عبر مسار الصرف الحقيقي: إقفال يوم الإنتاج.
    /// </summary>
    [Fact]
    public void Site12_ConsumeLot_Blocks_Untreated_Stock()
    {
        var c = Setup();
        using (c.Host)
        {
            int orderId = ManualOrder(c, 5000);

            // لا شيء أُفرج عنه بعد ⟵ صرف الخام عند الإقفال يجب أن يُرفض
            var closed = c.Host.Get<IExecutionService>().CloseProductionDay(
                orderId, 5000, 666, 0, 0, 0, false, null, false, consumedRawKg: 5000);
            Assert.False(closed.Ok);
            Assert.Contains("لم تكتمل معالجتها", closed.Message);

            // ولا يُخصم شيء: الرفض ذرّي لا جزئي
            var lot = c.Db.Lots.AsNoTracking().First(l => l.Id == c.LotId);
            Assert.Equal(100000, lot.InStockQtyKg, 1);
            Assert.Equal(0, lot.ProducedQtyKg, 1);
        }
    }

    /// <summary>وبعد الإفراج عن كمية كافية، الصرف يمر ويُخصم من الجاهز.</summary>
    [Fact]
    public void Site12_ConsumeLot_Allows_Released_Stock()
    {
        var c = Setup();
        using (c.Host)
        {
            int t = Start(c, 1000, 168, daysAgo: 8);
            Assert.True(c.Trt.Release(t, 1000 * BasketKg).Ok);   // 20,000 كجم جاهزة

            int orderId = ManualOrder(c, 15000);
            var closed = c.Host.Get<IExecutionService>().CloseProductionDay(
                orderId, 15000, 2000, 0, 0, 0, false, null, false, consumedRawKg: 15000);
            Assert.True(closed.Ok, closed.Message);

            var lot = c.Db.Lots.AsNoTracking().First(l => l.Id == c.LotId);
            Assert.Equal(85000, lot.InStockQtyKg, 1);
            Assert.Equal(15000, lot.ProducedQtyKg, 1);
        }
    }

    /// <summary>
    /// صنف لا يشترط معالجة: الصرف يعمل كما كان دائماً — **لا انحدار** على
    /// المسار القائم.
    /// </summary>
    [Fact]
    public void Site12_Unaffected_When_Product_Does_Not_Require_Treatment()
    {
        var c = Setup(requiresTreatment: false);
        using (c.Host)
        {
            int orderId = ManualOrder(c, 5000);
            var closed = c.Host.Get<IExecutionService>().CloseProductionDay(
                orderId, 5000, 666, 0, 0, 0, false, null, false, consumedRawKg: 5000);
            Assert.True(closed.Ok, closed.Message);
        }
    }

    /// <summary>أمر إنتاج يدوي معتمد على الدفعة — مسار الصرف الحقيقي.</summary>
    private static int ManualOrder(Ctx c, double kg)
    {
        var orders = c.Host.Get<IProductionOrderService>();
        var o = LegacyOrderFixture.ImportApproved(c.Host, null, 1, DateTime.Now.ToString("dd/MM/yyyy"), 1, 1,
            new List<OrderItemDto>
            {
                new() { LotId = c.LotId, ProductId = 3, PlannedQtyKg = kg, PlannedCartons = (int)(kg / 7.5) }
            });
        Assert.True(o.Ok, o.Message);
        Assert.True(c.Db.ProductionOrders.Single(x => x.Id == o.Id).IsApproved); // Deliberately old snapshot exercises the final consumption guard.
        return o.Id;
    }
}
