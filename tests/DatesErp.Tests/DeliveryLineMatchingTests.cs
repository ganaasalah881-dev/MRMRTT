using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B96 — مطابقة بند التسليم بسطر المصدر.
///
/// ثغرتان في SaveDelivery:
///  (أ) حين يُترك عميل البند فارغاً كان الشرط `l.CustomerId == (it.CustomerId ?? l.CustomerId)`
///      يتحقق لكل السطور — يقارن الحقل بنفسه. فيُلتقط **أول** سطر بالصدفة، ويُقيَّد
///      التام لعميل غير صاحبه حين يشترك عميلان في نفس الصنف والدفعة.
///  (ب) RemainingQtyKg يُحسب مرة واحدة قبل الحلقة من المحفوظ، فبندان في نفس المستند
///      على سطر واحد كانا يُقاسان كلاهما على المتبقي الكامل ويتجاوزانه معاً.
/// </summary>
public class DeliveryLineMatchingTests
{
    /// <summary>دفعة واحدة، صنف واحد، عميلان — الحالة التي تكشف الالتباس.</summary>
    private static (int orderId, int lot, int custB) SeedSharedLotTwoCustomers(TestHost host)
    {
        var db = host.Get<DatesErpDbContext>();
        int whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 9000 },
            new StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 9000 });
        db.SaveChanges();

        var rcv = host.Get<IReceivingService>();
        var s = rcv.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 200, UnitWeightKg = 20, QtyKg = 4000 } });
        Assert.True(rcv.ApproveShipment(s.Id).Ok);
        int lot = db.Lots.OrderBy(l => l.Id).Last().Id;

        var custB = host.Get<MasterDataService>()
            .SaveCustomer(null, "DLM-C2", "عميل ثانٍ", "جملة", "777", "-", true);

        // بندان: نفس الصنف ونفس الدفعة، عميلان مختلفان
        var orders = host.Get<IProductionOrderService>();
        var o = LegacyOrderFixture.ImportApproved(host, null, null, "2026-08-21", 1, 1, new List<OrderItemDto>
        {
            new() { LotId = lot, CustomerId = 1,        ProductId = 3, PlannedQtyKg = 500, PlannedCartons = 67 },
            new() { LotId = lot, CustomerId = custB.Id, ProductId = 3, PlannedQtyKg = 500, PlannedCartons = 67 }
        });
        Assert.True(o.Ok, o.Message);
        Assert.True(db.ProductionOrders.Single(x => x.Id == o.Id).IsApproved); // Historical approval, not new issuance.

        var c = host.Get<IExecutionService>()
            .CloseProductionDay(o.Id, 1000, 134, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        Assert.True(c.Ok, c.Message);
        return (o.Id, lot, custB.Id);
    }

    // ═════════ (أ) التباس العميل ═════════

    /// <summary>
    /// صنف ودفعة مشتركان بين عميلين وبند بلا عميل ⟵ يجب الرفض بطلب التحديد،
    /// لا الالتقاط الصامت لأول سطر.
    /// </summary>
    [Fact]
    public void Ambiguous_Customer_Is_Rejected_Not_Silently_Guessed()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot, _) = SeedSharedLotTwoCustomers(host);
        int executionId = host.Get<DatesErpDbContext>().ProductionExecutions.Single(e => e.OrderId == oid).Id;
        var del = host.Get<IProductionDeliveryService>();
        Assert.Equal(2, del.GetSourceContext(DeliverySources.FromActual, executionId).Lines.Count);
        var r = del.SaveDelivery(DeliverySources.FromActual, executionId, "2026-08-24",
            new List<ProductionDeliveryItemDto>
            { new() { OrderId = oid, ProductId = 3, LotId = lot, CustomerId = null,
                PackageCount = 67, QtyKg = 500 } });
        Assert.False(r.Ok);
        Assert.Contains("حدد عميل البند", r.Message);
        Assert.Empty(host.Get<DatesErpDbContext>().ProductionDeliveries);
    }

    [Fact]
    public void Explicit_Customer_Is_Honoured_On_Shared_Lot()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot, custB) = SeedSharedLotTwoCustomers(host);
        var db = host.Get<DatesErpDbContext>();
        int executionId = db.ProductionExecutions.Single(e => e.OrderId == oid).Id;
        var r = host.Get<IProductionDeliveryService>().SaveDelivery(DeliverySources.FromActual,
            executionId, "2026-08-24", new List<ProductionDeliveryItemDto>
            { new() { OrderId = oid, ProductId = 3, LotId = lot, CustomerId = custB,
                PackageCount = 67, QtyKg = 500 } });
        Assert.True(r.Ok, r.Message);
        var item = db.ProductionDeliveryItems.AsNoTracking().Single(i => i.DeliveryId == r.Id);
        Assert.Equal(custB, item.CustomerId);
    }

    // ═════════ (ب) تجميع الاستهلاك داخل المستند ═════════

    /// <summary>
    /// بندان في نفس المستند على نفس سطر المصدر: مجموعهما يتجاوز المتبقي ⟵ يجب الرفض.
    /// قبل الإصلاح كان كلٌّ منهما يُقاس وحده على المتبقي الكامل فيمرّان معاً.
    /// </summary>
    [Fact]
    public void Two_Lines_Same_Source_Cannot_Exceed_Remaining_Together()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrder(host, db, out var oid, out var lot);
        Assert.True(host.Get<IExecutionService>()
            .CloseProductionDay(oid, 500, 67, 0, 0, 0, false, new List<DowntimeDto>(), false).Ok);

        int executionId = db.ProductionExecutions.Single(e => e.OrderId == oid).Id;

        var del = host.Get<IProductionDeliveryService>();
        var r = del.SaveDelivery(DeliverySources.FromActual, executionId, "2026-08-24",
            new List<ProductionDeliveryItemDto>
            {
                new() { OrderId = oid, ProductId = 3, LotId = lot, CustomerId = 1, PackageCount = 54, QtyKg = 400 },
                new() { OrderId = oid, ProductId = 3, LotId = lot, CustomerId = 1, PackageCount = 54, QtyKg = 400 }  // المجموع 800 > 500
            });

        Assert.False(r.Ok);
        Assert.Contains("المتبقي", r.Message);

        // ولم يُحفظ أي مستند: المعاملة تراجعت كاملة.
        Assert.False(db.ProductionDeliveries.AsNoTracking().Any(d => d.SourceId == executionId));
    }

    /// <summary>وبندان مجموعهما ضمن المتبقي يمرّان — الإصلاح لا يمنع الصحيح.</summary>
    [Fact]
    public void Two_Lines_Within_Remaining_Are_Accepted()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrder(host, db, out var oid, out var lot);
        Assert.True(host.Get<IExecutionService>()
            .CloseProductionDay(oid, 500, 67, 0, 0, 0, false, new List<DowntimeDto>(), false).Ok);

        int executionId = db.ProductionExecutions.Single(e => e.OrderId == oid).Id;

        var r = host.Get<IProductionDeliveryService>().SaveDelivery(
            DeliverySources.FromActual, executionId, "2026-08-24",
            new List<ProductionDeliveryItemDto>
            {
                new() { OrderId = oid, ProductId = 3, LotId = lot, CustomerId = 1, PackageCount = 27, QtyKg = 200 },
                new() { OrderId = oid, ProductId = 3, LotId = lot, CustomerId = 1, PackageCount = 40, QtyKg = 300 }  // المجموع 500 = المتبقي
            });
        Assert.True(r.Ok, r.Message);
        Assert.Equal(500, db.ProductionDeliveryItems.AsNoTracking()
            .Where(i => i.DeliveryId == r.Id).Sum(i => i.QtyKg), 1);
    }
}
