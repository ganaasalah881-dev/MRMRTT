using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §اختبارات انحدار — بوابة الجودة المركزية.
///
/// الخلل في B18 (أُثبت بالتشغيل قبل الإصلاح): دفعة قرار جودتها «مرفوض تماماً»
/// اعتُمدت ودخلت مخزن التام وسُلّمت للعميل، لأن:
///   • ApproveCheck لا يفحص Decision
///   • DeliveryView.Save تمرر orderId = null
///   • بوابة التسليم كانت داخل if (OrderId is int) فلا تُنفَّذ أبداً
/// </summary>
public class QualityGateTests
{
    private static T Svc<T>(TestHost host) => (T)host.Services.CreateScope().ServiceProvider.GetService(typeof(T));

    /// <summary>يبني الدورة حتى مخزن التام، ويعيد معرّفات الأمر والدفعة والعميل.</summary>
    private static (int orderId, int lotId, int custId) BuildThroughFinishedGoods(TestHost host, string decision)
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
            new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 500, UnitWeightKg = 20, QtyKg = 10000 } },
            null, "QG-1");
        Assert.True(s.Ok, s.Message);
        Assert.True(receiving.ApproveShipment(s.Id).Ok);
        int lot = db.Lots.OrderBy(l => l.Id).First().Id;

        var planning = Svc<IPlanningService>(host);
        var plan = planning.SavePlan("خطة الجودة", "Period", "2026-08-20", "2026-08-20", 1, 1,
            new List<PlanItemDto> { new() { SourceType="FromReceiving", LotId=lot, CustomerId=cust, ProductId=3,
                PackagingTypeId=2, PlannedCartons=100, PlannedQtyKg=1000, ScheduledDate="2026-08-20",
                SuggestedShiftId=1, SuggestedLineId=1, PriorityNo=1 } });
        Assert.True(plan.Ok, plan.Message);
        Assert.True(planning.ApprovePlan(plan.Id).Ok);
        int planItemId = db.ProductionPlanItems.First(i => i.PlanId == plan.Id).Id;

        var orders = Svc<IProductionOrderService>(host);
        host.SetBusinessDate("2026-08-20"); // Arrange production on its scheduled business day, not the machine date.
        var or = orders.SaveOrder("FromPlan", plan.Id, cust, "2026-08-20", 1, 1,
            new List<OrderItemDto> { new() { PlanItemId=planItemId, LotId=lot, CustomerId=cust, ProductId=3,
                PackagingTypeId=2, PlannedCartons=100, PlannedQtyKg=1000 } });
        Assert.True(or.Ok, or.Message);
        Assert.True(orders.ApproveOrder(or.Id).Ok);

        var exec = Svc<IExecutionService>(host);
        Svc<IProductionOrderService>(host).StartOrder(or.Id);
        var ec = exec.CloseProductionDay(or.Id, 1000, 100, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        Assert.True(ec.Ok, ec.Message);
        int execId = Svc<DatesErpDbContext>(host).ProductionExecutions.Single(x => x.OrderId == or.Id).Id;

        var q = Svc<IQualityService>(host);
        var qc = q.SaveCheck(or.Id, execId, "2026-08-20", "نهائي",
            new List<QualityItemDto> { new() { ProductId = 3, LotId = lot, AcceptedQtyKg = 1000, RejectedQtyKg = 0, CheckedQtyKg = 1000 } },
            null, new QualityLabDto { Decision = decision, MoisturePct = decision == "Rejected" ? 30 : 15, SampleCartons = 10 });
        Assert.True(qc.Ok, qc.Message);
        Assert.True(q.ApproveCheck(qc.Id).Ok);

        var fg = Svc<IFinishedGoodsService>(host);
        var sr = TestProductionDocumentFlow.SaveReceiptFromActual(host, or.Id, qc.Id, "2026-08-21", new List<FinishedGoodsItemDto>
            { new() { ProductId = 3, LotId = lot, PackagingTypeId = 2, PackageCount = 100, NetWeightKg = 1000 } });
        Assert.True(sr.Ok, sr.Message);
        Assert.True(fg.Issue(sr.Id).Ok);
        Assert.True(fg.Receive(sr.Id, null).Ok);

        return (or.Id, lot, cust);
    }

    [Fact]
    public void Rejected_Quality_Blocks_Customer_Delivery()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (orderId, lot, cust) = BuildThroughFinishedGoods(host, "Rejected");

        var cd = Svc<ICustomerDeliveryService>(host);
        // المسار نفسه الذي كانت تسلكه الواجهة: orderId = null
        var saved = cd.Save(cust, "2026-08-22", null, new List<CustomerDeliveryItemDto>
            { new() { ProductId = 3, LotId = lot, PackagingTypeId = 2, PackageCount = 100, QtyKg = 1000 } });
        Assert.True(saved.Ok, saved.Message);

        var ap = cd.Approve(saved.Id);
        Assert.False(ap.Ok, "دفعة قرارها «مرفوض تماماً» يجب ألا تُسلَّم للعميل.");
        Assert.Contains("مرفوض تماماً", ap.Message ?? "");

        // والرصيد لم يُخصم
        using var chk = new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);
        int wfg = chk.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
        Assert.Equal(1000, chk.StockBalances.First(b => b.WarehouseId == wfg && b.ProductId == 3).QtyKg, 1);
    }

    [Fact]
    public void Quarantined_Quality_Blocks_Customer_Delivery()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (orderId, lot, cust) = BuildThroughFinishedGoods(host, "Quarantine");

        var cd = Svc<ICustomerDeliveryService>(host);
        var saved = cd.Save(cust, "2026-08-22", null, new List<CustomerDeliveryItemDto>
            { new() { ProductId = 3, LotId = lot, PackagingTypeId = 2, PackageCount = 100, QtyKg = 1000 } });
        var ap = cd.Approve(saved.Id);
        Assert.False(ap.Ok, "البضاعة المحجوزة يجب ألا تُسلَّم للعميل.");
        Assert.Contains("حجز وتحريز", ap.Message ?? "");
    }

    [Fact]
    public void Passed_Quality_Allows_Customer_Delivery()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (orderId, lot, cust) = BuildThroughFinishedGoods(host, "Passed");

        var cd = Svc<ICustomerDeliveryService>(host);
        var saved = cd.Save(cust, "2026-08-22", null, new List<CustomerDeliveryItemDto>
            { new() { ProductId = 3, LotId = lot, PackagingTypeId = 2, PackageCount = 100, QtyKg = 1000 } });
        Assert.True(saved.Ok, saved.Message);

        var ap = cd.Approve(saved.Id);
        Assert.True(ap.Ok, ap.Message);

        using var chk = new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);
        int wfg = chk.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
        Assert.Equal(0, chk.StockBalances.First(b => b.WarehouseId == wfg && b.ProductId == 3).QtyKg, 1);
    }

    [Fact]
    public void Gate_Cannot_Be_Bypassed_By_Null_OrderId()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (orderId, lot, cust) = BuildThroughFinishedGoods(host, "Rejected");

        // البوابة تُشتق الأمر من الدفعة — فتمرير null لم يعد ثغرة
        var db = host.Get<DatesErpDbContext>();
        var (ok, reason) = QualityGate.CustomerDeliveryAllowed(db, null, lot, 3);
        Assert.False(ok);
        Assert.Contains("مرفوض تماماً", reason ?? "");

        var (ok2, _) = QualityGate.CustomerDeliveryAllowed(db, orderId, lot, 3);
        Assert.False(ok2);
    }
}

/// <summary>
/// §اختبارات انحدار — إلغاء السندات بقيد عكسي لا بحذف دفتر الأستاذ.
/// كان Unapprove يحذف InventoryTransactions (FinishedGoods بـ StartsWith فيحذف
/// حركات سندات أخرى عند تصادم البادئات، وCustomerDelivery بـ ==).
/// </summary>
public class UnapproveUsesReversalEntriesTests
{
    private static T Svc<T>(TestHost host) => (T)host.Services.CreateScope().ServiceProvider.GetService(typeof(T));

    [Fact]
    public void Unapproved_Customer_Delivery_Keeps_Ledger_And_Adds_Reversal()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        int whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new DatesErp.Core.Domain.Entities.StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 500000 },
            new DatesErp.Core.Domain.Entities.StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 500000 });
        db.SaveChanges();
        int cust = db.Customers.First().Id;

        var receiving = Svc<IReceivingService>(host);
        var s = receiving.SaveShipment(cust, "2026-08-10", "2026-08-10",
            new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 500, UnitWeightKg = 20, QtyKg = 10000 } }, null, "REV-1");
        receiving.ApproveShipment(s.Id);
        int lot = db.Lots.OrderBy(l => l.Id).First().Id;

        var planning = Svc<IPlanningService>(host);
        var plan = planning.SavePlan("خطة", "Period", "2026-08-20", "2026-08-20", 1, 1, new List<PlanItemDto>
            { new() { SourceType="FromReceiving", LotId=lot, CustomerId=cust, ProductId=3, PackagingTypeId=2, PlannedCartons=100, PlannedQtyKg=1000, ScheduledDate="2026-08-20", SuggestedShiftId=1, SuggestedLineId=1, PriorityNo=1 } });
        planning.ApprovePlan(plan.Id);
        int planItemId = db.ProductionPlanItems.First(i => i.PlanId == plan.Id).Id;

        var orders = Svc<IProductionOrderService>(host);
        host.SetBusinessDate("2026-08-20");
        var or = orders.SaveOrder("FromPlan", plan.Id, cust, "2026-08-20", 1, 1, new List<OrderItemDto>
            { new() { PlanItemId=planItemId, LotId=lot, CustomerId=cust, ProductId=3, PackagingTypeId=2, PlannedCartons=100, PlannedQtyKg=1000 } });
        Assert.True(or.Ok, or.Message);
        Assert.True(orders.ApproveOrder(or.Id).Ok);
        var exec = Svc<IExecutionService>(host);
        Svc<IProductionOrderService>(host).StartOrder(or.Id);
        exec.CloseProductionDay(or.Id, 1000, 100, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        int execId = Svc<DatesErpDbContext>(host).ProductionExecutions.Single(x => x.OrderId == or.Id).Id;
        var q = Svc<IQualityService>(host);
        var qc = q.SaveCheck(or.Id, execId, "2026-08-20", "نهائي",
            new List<QualityItemDto> { new() { ProductId=3, LotId=lot, AcceptedQtyKg=1000, RejectedQtyKg=0, CheckedQtyKg=1000 } },
            null, new QualityLabDto { Decision = "Passed", SampleCartons = 10 });
        q.ApproveCheck(qc.Id);
        var fg = Svc<IFinishedGoodsService>(host);
        var sr = TestProductionDocumentFlow.SaveReceiptFromActual(host, or.Id, qc.Id, "2026-08-21", new List<FinishedGoodsItemDto>
            { new() { ProductId=3, LotId=lot, PackagingTypeId=2, PackageCount=100, NetWeightKg=1000 } });
        fg.Issue(sr.Id); fg.Receive(sr.Id, null);

        var cd = Svc<ICustomerDeliveryService>(host);
        var dlv = cd.Save(cust, "2026-08-22", or.Id, new List<CustomerDeliveryItemDto>
            { new() { ProductId=3, LotId=lot, PackagingTypeId=2, PackageCount=100, QtyKg=1000 } });
        Assert.True(cd.Approve(dlv.Id).Ok);

        using (var chk = new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options))
        {
            int wfg = chk.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
            Assert.Equal(0, chk.StockBalances.First(b => b.WarehouseId == wfg && b.ProductId == 3).QtyKg, 1);
        }

        // The FG helper leaves the shared fixture logged in as warehouse;
        // customer-delivery reversal requires the delivery/Cancel permission.
        host.LoginAsAdmin();
        Assert.True(cd.Unapprove(dlv.Id).Ok);

        using (var chk = new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options))
        {
            int wfg = chk.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
            // الرصيد عاد
            Assert.Equal(1000, chk.StockBalances.First(b => b.WarehouseId == wfg && b.ProductId == 3).QtyKg, 1);

            // دفتر الأستاذ لم يُحذف — بل أُضيف قيد عكسي
            var txns = chk.InventoryTransactions.Where(t =>
                t.ReferenceDocType == DatesErp.Core.Domain.Enums.ReferenceDocType.CustomerDelivery).ToList();
            Assert.Equal(2, txns.Count);
            Assert.Contains(txns, t => t.ReferenceDocNumber.EndsWith("#REV1"));
            Assert.Equal(0, txns.Sum(t => t.QtyKg), 1);   // صادر + وارد = صفر
        }
    }
}
