using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §اختبارات انحدار — الإقفال كان يمحو أساس الخطة.
///
/// الخلل في B18: ClosePlanItems كان يكتب pi.PlannedQtyKg = pi.ProducedQtyKg
/// (وكذلك على بند الأمر) «لتحرير الحجز». النتيجة:
///  • تقرير «الخطة مقابل الفعلي» يعرض 100% إنجاز دائماً حتى لو أُنتج 60%
///  • عمودا المخطط (كجم) و(كرتون) يتناقضان لأن الكراتين لم تكن تُعدَّل
///  • أساس الخطة يضيع ولا يمكن معرفة ما كان مخططاً
///  • الإقفال التلقائي يُطلق دائماً لأن الشرط يصبح صحيحاً بعد إعادة الكتابة
///
/// الإصلاح: علم IsClosed على البند يحرّر الحجز، والمخطط يبقى محفوظاً.
/// </summary>
public class ClosingPreservesPlanBaselineTests
{
    private static T Svc<T>(TestHost host) => (T)host.Services.CreateScope().ServiceProvider.GetService(typeof(T));

    private static (int planId, int planItemId, int orderId) Build(TestHost host, int cartons)
    {
        var db = host.Get<DatesErpDbContext>();
        int whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new DatesErp.Core.Domain.Entities.StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 500000 },
            new DatesErp.Core.Domain.Entities.StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 500000 });
        db.SaveChanges();

        int cust = db.Customers.First().Id;
        var receiving = Svc<IReceivingService>(host);
        var s = receiving.SaveShipment(cust, "2026-08-10", "2026-08-10",
            new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 5000, UnitWeightKg = 20, QtyKg = 100000 } },
            null, "BASE-1");
        Assert.True(s.Ok, s.Message);
        Assert.True(receiving.ApproveShipment(s.Id).Ok);
        int lot = db.Lots.OrderBy(l => l.Id).First().Id;

        var planning = Svc<IPlanningService>(host);
        var plan = planning.SavePlan("خطة الأساس", "Period", "2026-08-20", "2026-08-20", 1, 1,
            new List<PlanItemDto>
            {
                new() { SourceType="FromReceiving", LotId=lot, CustomerId=cust, ProductId=3, PackagingTypeId=2,
                        PlannedCartons=cartons, PlannedQtyKg=cartons*10.0, ScheduledDate="2026-08-20",
                        SuggestedShiftId=1, SuggestedLineId=1, PriorityNo=1 }
            });
        Assert.True(plan.Ok, plan.Message);
        Assert.True(planning.ApprovePlan(plan.Id).Ok);
        int planItemId = db.ProductionPlanItems.First(i => i.PlanId == plan.Id).Id;

        var orders = Svc<IProductionOrderService>(host);
        host.SetBusinessDate("2026-08-20"); // Arrange production on its scheduled business day, not the machine date.
        var or = orders.SaveOrder("FromPlan", plan.Id, cust, "2026-08-20", 1, 1,
            new List<OrderItemDto> { new() { PlanItemId=planItemId, LotId=lot, CustomerId=cust, ProductId=3, PackagingTypeId=2,
                                           PlannedCartons=cartons, PlannedQtyKg=cartons*10.0 } });
        Assert.True(or.Ok, or.Message);
        Assert.True(orders.ApproveOrder(or.Id).Ok);
        return (plan.Id, planItemId, or.Id);
    }

    /// <summary>الاختبار الأساسي: إقفال 60% يجب أن يُبقي المخطط 10,000 ويظهر الإنجاز 60%.</summary>
    [Fact]
    public void Partial_Closing_Keeps_Planned_Quantity_And_Shows_Real_Achievement()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, planItemId, orderId) = Build(host, 1000);   // 10,000 كجم / 1,000 كرتون

        // §B95 — المسار الواحد: إقفال يوم الأمر بما أُنتج فعلياً (60%) — الأساس يُحفظ في البندين
        var exec = Svc<IExecutionService>(host);
        var close = exec.CloseProductionDay(orderId, 6000, 600, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        Assert.True(close.Ok, close.Message);

        using var chk = new DatesErpDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<DatesErpDbContext>()
                .UseSqlite(GetConn(host)).Options);

        var pi = chk.ProductionPlanItems.First(i => i.Id == planItemId);
        // الأساس محفوظ — كان يُكتب 6000 في B18
        Assert.Equal(10000, pi.PlannedQtyKg, 1);
        Assert.Equal(1000, pi.PlannedCartons);
        Assert.Equal(6000, pi.ProducedQtyKg, 1);   // مزامنة الإنتاج فقط — لا مساس بالمخطط
        Assert.False(pi.IsClosed, "إقفال جزئي — بند الخطة يبقى مفتوحاً للمتبقي");

        var oi = chk.ProductionOrderItems.First(i => i.PlanItemId == planItemId);
        Assert.Equal(10000, oi.PlannedQtyKg, 1);
        Assert.Equal(600, oi.ProducedCartons);
        Assert.Equal(6000, oi.ProducedQtyKg, 1);
        // إقفال يوم الأمر نهائي ولا يُعاد لنفس الأمر؛ المتبقي يبقى على
        // بند الخطة لإصدار أمر لاحق أو تسوية العجز رسمياً.
        Assert.True(oi.IsClosed, "بند الأمر ينتهي مع إقفال اليوم، لا يُفتح لجلسة ثانية");
    }

    /// <summary>التقرير يجب أن يعرض 60% لا 100%.</summary>
    [Fact]
    public void Plan_Vs_Actual_Report_Shows_Real_Percentage_After_Partial_Closing()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, planItemId, orderId) = Build(host, 1000);

        var exec = Svc<IExecutionService>(host);
        Assert.True(exec.CloseProductionDay(orderId, 6000, 600, 0, 0, 0, false, new List<DowntimeDto>(), false, null).Ok);

        var rep = Svc<IReportService>(host);
        var r = rep.Run("plan_vs_actual", new Dictionary<string, string>());
        Assert.NotNull(r);
        var row = Assert.Single(r.Rows);

        int iPlannedKg = r.Columns.ToList().IndexOf("المخطط (كجم)");
        int iProduced = r.Columns.ToList().IndexOf("المنتج (كجم)");
        int iPct = r.Columns.ToList().IndexOf("إنجاز ٪");
        Assert.True(iPlannedKg >= 0 && iProduced >= 0 && iPct >= 0);

        Assert.Equal(10000, Convert.ToDouble(row[iPlannedKg]), 1);
        Assert.Equal(6000, Convert.ToDouble(row[iProduced]), 1);
        Assert.Equal(60, Convert.ToDouble(row[iPct]), 1);   // كان 100 في B18
    }

    /// <summary>تحرير الحجز ما زال يعمل — المتاح يعود بعد الإقفال الجزئي.</summary>
    [Fact]
    public void Partial_Closing_Still_Releases_The_Reservation()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (planId, planItemId, orderId) = Build(host, 1000);

        var planning = Svc<IPlanningService>(host);
        int lot = host.Get<DatesErpDbContext>().ProductionPlanItems.First(i => i.Id == planItemId).LotId!.Value;
        double reservedBefore = planning.GetProductLotRemaining(lot, 3);

        var exec = Svc<IExecutionService>(host);
        Assert.True(exec.CloseProductionDay(orderId, 6000, 600, 0, 0, 0, false, new List<DowntimeDto>(), false, null).Ok);

        double afterClosing = planning.GetProductLotRemaining(lot, 3);
        Assert.True(afterClosing > reservedBefore,
            $"الحجز يجب أن يتحرر بعد الإقفال: قبل {reservedBefore} ← بعد {afterClosing}");
    }

    private static Microsoft.Data.Sqlite.SqliteConnection GetConn(TestHost host) => host.Connection;
}
