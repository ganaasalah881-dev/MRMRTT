using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §v1.50.28 — السلسلة المعتمدة كاملة: خطة 3,000 ← أمر ← تسليم فعلي 2,700 ←
/// الفحص ينزل من التسليم بلا إعادة اختيار الصنف ← صفات الجودة (سليم 2,550 + منسم 150)
/// بمجموع = الكمية المستلمة حكماً ← المعايير المعتمدة ← الاعتماد.
/// الصفات ليست أصنافاً: لا يُنشأ صنف جديد إطلاقاً.
/// </summary>
public class QualityDeliveryFlowTests
{
    private static string D(int offset) => DateTime.Today.AddDays(offset).ToString("yyyy-MM-dd");

    /// <summary>شحنة معتمدة بدفعة كبيرة (40,000 كجم).</summary>
    private static int SeedBigShipment(TestHost host)
    {
        host.LoginAsAdmin();
        var rcv = host.Get<IReceivingService>();
        var s = rcv.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 2000, UnitWeightKg = 20, QtyKg = 40000 } });
        Assert.True(s.Ok, s.Message);
        Assert.True(rcv.ApproveShipment(s.Id).Ok);
        return host.Get<DatesErpDbContext>().Lots.OrderBy(l => l.Id).Last().Id;
    }

    /// <summary>خطة معتمدة + أمر معتمد لخطة 3,000 كرتون من الدفعة.</summary>
    private static (int planId, int orderId, int lotId) SeedPlanAndOrder(TestHost host, int lotId, double plannedKg, int plannedCtn)
    {
        string day = D(-1);
        var planSvc = host.Get<IPlanningService>();
        var p = planSvc.SavePlan("خطة الفحص", "Daily", day, day, 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3, PackagingTypeId = 1,
                 PlannedQtyKg = plannedKg, PlannedCartons = plannedCtn, ScheduledDate = day, SuggestedShiftId = 1, SuggestedLineId = 1 } });
        Assert.True(p.Ok, p.Message);
        Assert.True(planSvc.ApprovePlan(p.Id).Ok);
        var db = host.Get<DatesErpDbContext>();
        var planItems = db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == p.Id).ToList();
        var o = LegacyOrderFixture.ImportApproved(host, p.Id, 1, day, 1, 1, new List<OrderItemDto>
        { new() { PlanItemId = planItems[0].Id, LotId = lotId, CustomerId = 1, ProductId = 3, PackagingTypeId = 1,
                 PlannedQtyKg = plannedKg, PlannedCartons = plannedCtn } });
        Assert.True(o.Ok, o.Message);
        return (p.Id, o.Id, lotId);
    }

    private static (int saleem, int monsam) GradeIds(TestHost host)
    {
        var db = host.Get<DatesErpDbContext>();
        return (db.InspectionResultTypes.AsNoTracking().Single(t => t.Code == "RT-SALEEM").Id,
                db.InspectionResultTypes.AsNoTracking().Single(t => t.Code == "RT-MONSAM").Id);
    }

    [Fact]
    public void Full_Chain_Plan3000_Delivery2700_Quality2550Plus150_Then_Approve()
    {
        using var host = new TestHost();
        int lotId = SeedBigShipment(host);
        var (_, orderId, _) = SeedPlanAndOrder(host, lotId, 15000, 3000);
        var close = host.Get<IExecutionService>()
            .CloseProductionDay(orderId, 13500, 2700, 0, 0, 0, false, new List<DowntimeDto>(), true);
        Assert.True(close.Ok, close.Message);
        var db = host.Get<DatesErpDbContext>();
        int productsBefore = db.Products.AsNoTracking().Count();

        var insp = host.Get<IInspectionService>();
        var sources = insp.GetDeliverySources();
        var src = Assert.Single(sources, s2 => s2.OrderId == orderId);
        var item = Assert.Single(src.Items);
        Assert.Equal(2700, item.ProducedCartons);                       // نازلة من التسليم تلقائياً
        Assert.Contains(item.AllowedGrades, g => g.NameAr == "تمر سليم");
        Assert.Contains(item.AllowedGrades, g => g.NameAr == "تمر منسم");

        var (saleem, monsam) = GradeIds(host);
        var r = insp.SaveDeliveryCheck(new QualityDeliveryCheckDto
        {
            OrderId = orderId,
            Rows =
            {
                new() { ProductId = item.ProductId, ResultTypeId = saleem, Cartons = 2550 },
                new() { ProductId = item.ProductId, ResultTypeId = monsam, Cartons = 150 },
            },
            Standards = { },
        });
        Assert.True(r.Ok, r.Message);

        var check = db.QualityChecks.AsNoTracking().Single(c => c.Id == r.Id);
        Assert.Equal(DocStatuses.Completed, check.Status);
        Assert.Equal(2700, check.TotalCheckedCartons);
        Assert.Equal(2700, check.AcceptedCartons);                      // سليم + منسم كلاهما مقبول
        Assert.Equal(2, db.InspectionResults.AsNoTracking().Count(x => x.CheckId == check.Id));
        // الصفات ليست أصنافاً: لا صنف جديد أُنشئ
        Assert.Equal(productsBefore, db.Products.AsNoTracking().Count());

        Assert.True(host.Get<IQualityService>().ApproveCheck(check.Id).Ok);
        Assert.True(db.QualityChecks.AsNoTracking().Single(c => c.Id == check.Id).IsApproved);
    }

    [Fact]
    public void Grades_Exceeding_Received_Qty_Are_Rejected()
    {
        using var host = new TestHost();
        int lotId = SeedBigShipment(host);
        var (_, orderId, _) = SeedPlanAndOrder(host, lotId, 15000, 3000);
        Assert.True(host.Get<IExecutionService>()
            .CloseProductionDay(orderId, 13500, 2700, 0, 0, 0, false, new List<DowntimeDto>(), true).Ok);
        var insp = host.Get<IInspectionService>();
        int pid = Assert.Single(insp.GetDeliverySources(), s2 => s2.OrderId == orderId).Items[0].ProductId;
        var (saleem, monsam) = GradeIds(host);
        var r = insp.SaveDeliveryCheck(new QualityDeliveryCheckDto
        {
            OrderId = orderId,
            Rows =
            {
                new() { ProductId = pid, ResultTypeId = saleem, Cartons = 2550 },
                new() { ProductId = pid, ResultTypeId = monsam, Cartons = 200 },   // 2750 ≠ 2700
            },
        });
        Assert.False(r.Ok);
        Assert.Contains("≠ الكمية المستلمة", r.Message);
    }

    [Fact]
    public void Grades_Less_Than_Received_Qty_Are_Rejected_Too()
    {
        using var host = new TestHost();
        int lotId = SeedBigShipment(host);
        var (_, orderId, _) = SeedPlanAndOrder(host, lotId, 15000, 3000);
        Assert.True(host.Get<IExecutionService>()
            .CloseProductionDay(orderId, 13500, 2700, 0, 0, 0, false, new List<DowntimeDto>(), true).Ok);
        var insp = host.Get<IInspectionService>();
        int pid = Assert.Single(insp.GetDeliverySources(), s2 => s2.OrderId == orderId).Items[0].ProductId;
        var (saleem, _) = GradeIds(host);
        var r = insp.SaveDeliveryCheck(new QualityDeliveryCheckDto
        {
            OrderId = orderId,
            Rows = { new() { ProductId = pid, ResultTypeId = saleem, Cartons = 2600 } },   // أقل من 2700
        });
        Assert.False(r.Ok);
        Assert.Contains("≠ الكمية المستلمة", r.Message);
    }

    /// <summary>§v1.50.29: عدة عملاء في أمر واحد — كل عميل سطر مستقل (عميل×صنف×عبوة) ويُحاسَب وحده.</summary>
    [Fact]
    public void Multi_Customer_Delivery_Shows_Per_Customer_Rows_With_Weight_And_Package()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var master = host.Get<MasterDataService>();
        var custB = master.SaveCustomer(null, "CQ29", "عميل الجودة الثاني", "جملة", "700", "-", true);
        Assert.True(custB.Ok, custB.Message);
        var rcv = host.Get<IReceivingService>();
        int lotA = SeedBigShipment(host);
        var s2 = rcv.SaveShipment(custB.Id, "2026-08-11", "2026-08-11", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 1500, UnitWeightKg = 20, QtyKg = 30000 } });
        Assert.True(s2.Ok, s2.Message);
        Assert.True(rcv.ApproveShipment(s2.Id).Ok);
        int lotB = host.Get<DatesErpDbContext>().Lots.OrderBy(l => l.Id).Last().Id;

        string day = D(-1);
        var planSvc = host.Get<IPlanningService>();
        var p = planSvc.SavePlan("خطة عميلين للفحص", "Daily", day, day, 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lotA, CustomerId = 1, ProductId = 3, PackagingTypeId = 1,
                    PlannedQtyKg = 10000, PlannedCartons = 2000, ScheduledDate = day, SuggestedShiftId = 1, SuggestedLineId = 1 },
            new() { SourceType = "FromReceiving", LotId = lotB, CustomerId = custB.Id, ProductId = 3, PackagingTypeId = 1,
                    PlannedQtyKg = 5000, PlannedCartons = 1000, ScheduledDate = day, SuggestedShiftId = 1, SuggestedLineId = 1 },
        });
        Assert.True(p.Ok, p.Message);
        Assert.True(planSvc.ApprovePlan(p.Id).Ok);
        var db = host.Get<DatesErpDbContext>();
        var planItems = db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == p.Id).OrderBy(i => i.Id).ToList();
        var o = LegacyOrderFixture.ImportApproved(host, p.Id, null, day, 1, 1, new List<OrderItemDto>
        {
            new() { PlanItemId = planItems[0].Id, LotId = lotA, CustomerId = 1, ProductId = 3, PackagingTypeId = 1, PlannedQtyKg = 10000, PlannedCartons = 2000 },
            new() { PlanItemId = planItems[1].Id, LotId = lotB, CustomerId = custB.Id, ProductId = 3, PackagingTypeId = 1, PlannedQtyKg = 5000, PlannedCartons = 1000 },
        });
        Assert.True(o.Ok, o.Message);
        var orderItems = db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == o.Id).OrderBy(i => i.Id).ToList();
        var close = host.Get<IExecutionService>().CloseProductionDay(o.Id, 15000, 3000, 0, 0, 0, false,
            new List<DowntimeDto>(), true, null, null, 0,
            new List<CloseItemQtyDto>
            {
                new() { OrderItemId = orderItems[0].Id, ProducedCartons = 2000, ProducedKg = 10000 },
                new() { OrderItemId = orderItems[1].Id, ProducedCartons = 1000, ProducedKg = 5000 },
            });
        Assert.True(close.Ok, close.Message);

        var insp = host.Get<IInspectionService>();
        var src = Assert.Single(insp.GetDeliverySources(), s3 => s3.OrderId == o.Id);
        Assert.Equal(2, src.Items.Count);
        Assert.Equal(3000, src.TotalProducedCartons);
        // العميل والوزن والعبوة نازلة مع كل سطر — بلا إعادة اختيار
        var row1 = src.Items.First(i2 => i2.ProducedCartons == 2000);
        var row2 = src.Items.First(i2 => i2.ProducedCartons == 1000);
        Assert.NotNull(row1.CustomerName);
        Assert.NotEqual(row1.CustomerName, row2.CustomerName);
        Assert.True(row1.CartonWeightKg > 0);
        Assert.NotNull(row1.PackageName);

        var (saleem, monsam) = GradeIds(host);
        var bad = insp.SaveDeliveryCheck(new QualityDeliveryCheckDto
        {
            OrderId = o.Id,
            Rows =
            {
                new() { ProductId = row1.ProductId, LotId = row1.LotId, ResultTypeId = saleem, Cartons = 1900 },  // 1900+100=2000 ✓
                new() { ProductId = row1.ProductId, LotId = row1.LotId, ResultTypeId = monsam, Cartons = 100 },
                new() { ProductId = row2.ProductId, LotId = row2.LotId, ResultTypeId = saleem, Cartons = 1000 },  // 1000 ✓
            },
        });
        Assert.True(bad.Ok, bad.Message);
        var check = db.QualityChecks.AsNoTracking().Single(c => c.Id == bad.Id);
        Assert.Equal(3000, check.TotalCheckedCartons);
        var results = db.InspectionResults.AsNoTracking().Where(x => x.CheckId == check.Id).ToList();
        Assert.Equal(3, results.Count);
        // نتيجة كل عميل مرتبطة بدفعته (العميل الثاني بدفعته الخاصة)
        Assert.Contains(results, x => x.LotId == lotB && x.Qty == 1000m);
        Assert.Contains(results, x => x.LotId == lotA && x.Qty == 1900m);
    }

    [Fact]
    public void Multi_Item_Delivery_Brings_All_Items_And_Enforces_Per_Item_Equality()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var rcv = host.Get<IReceivingService>();
        int lotA = SeedBigShipment(host);
        var s2 = rcv.SaveShipment(1, "2026-08-11", "2026-08-11", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 2000, UnitWeightKg = 20, QtyKg = 40000 } });
        Assert.True(s2.Ok, s2.Message);
        Assert.True(rcv.ApproveShipment(s2.Id).Ok);
        int lotB = host.Get<DatesErpDbContext>().Lots.OrderBy(l => l.Id).Last().Id;

        string day = D(-1);
        var planSvc = host.Get<IPlanningService>();
        var p = planSvc.SavePlan("خطة صنفين", "Daily", day, day, 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lotA, CustomerId = 1, ProductId = 3, PackagingTypeId = 1,
                    PlannedQtyKg = 7500, PlannedCartons = 1500, ScheduledDate = day, SuggestedShiftId = 1, SuggestedLineId = 1 },
            new() { SourceType = "FromReceiving", LotId = lotB, CustomerId = 1, ProductId = 3, PackagingTypeId = 1,
                    PlannedQtyKg = 7500, PlannedCartons = 1500, ScheduledDate = day, SuggestedShiftId = 1, SuggestedLineId = 1 },
        });
        Assert.True(p.Ok, p.Message);
        Assert.True(planSvc.ApprovePlan(p.Id).Ok);
        var db = host.Get<DatesErpDbContext>();
        var planItems = db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == p.Id).OrderBy(i => i.Id).ToList();
        var o = LegacyOrderFixture.ImportApproved(host, p.Id, 1, day, 1, 1, new List<OrderItemDto>
        {
            new() { PlanItemId = planItems[0].Id, LotId = lotA, CustomerId = 1, ProductId = 3, PackagingTypeId = 1, PlannedQtyKg = 7500, PlannedCartons = 1500 },
            new() { PlanItemId = planItems[1].Id, LotId = lotB, CustomerId = 1, ProductId = 3, PackagingTypeId = 1, PlannedQtyKg = 7500, PlannedCartons = 1500 },
        });
        Assert.True(o.Ok, o.Message);
        var orderItems = db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == o.Id).OrderBy(i => i.Id).ToList();
        var close = host.Get<IExecutionService>().CloseProductionDay(o.Id, 10000, 2000, 0, 0, 0, false,
            new List<DowntimeDto>(), true, null, null, 0,
            new List<CloseItemQtyDto>
            {
                new() { OrderItemId = orderItems[0].Id, ProducedCartons = 1000, ProducedKg = 5000 },
                new() { OrderItemId = orderItems[1].Id, ProducedCartons = 1000, ProducedKg = 5000 },
            });
        Assert.True(close.Ok, close.Message);

        var insp = host.Get<IInspectionService>();
        var src = Assert.Single(insp.GetDeliverySources(), s3 => s3.OrderId == o.Id);
        Assert.Equal(2, src.Items.Count);                                // كل الأصناف تنزل
        var (saleem, monsam) = GradeIds(host);
        var bad = insp.SaveDeliveryCheck(new QualityDeliveryCheckDto
        {
            OrderId = o.Id,
            Rows =
            {
                new() { ProductId = src.Items[0].ProductId, LotId = src.Items[0].LotId, ResultTypeId = saleem, Cartons = 1000 },
                new() { ProductId = src.Items[1].ProductId, LotId = src.Items[1].LotId, ResultTypeId = saleem, Cartons = 700 },  // 700 ≠ 1000
            },
        });
        Assert.False(bad.Ok);
        Assert.Contains("≠ الكمية المستلمة", bad.Message);

        var good = insp.SaveDeliveryCheck(new QualityDeliveryCheckDto
        {
            OrderId = o.Id,
            Rows =
            {
                new() { ProductId = src.Items[0].ProductId, LotId = src.Items[0].LotId, ResultTypeId = saleem, Cartons = 1000 },
                new() { ProductId = src.Items[1].ProductId, LotId = src.Items[1].LotId, ResultTypeId = saleem, Cartons = 850 },
                new() { ProductId = src.Items[1].ProductId, LotId = src.Items[1].LotId, ResultTypeId = monsam, Cartons = 150 },
            },
        });
        Assert.True(good.Ok, good.Message);
        var check = db.QualityChecks.AsNoTracking().Single(c => c.Id == good.Id);
        Assert.Equal(2000, check.TotalCheckedCartons);
        Assert.Equal(3, db.InspectionResults.AsNoTracking().Count(x => x.CheckId == check.Id));
    }
}
