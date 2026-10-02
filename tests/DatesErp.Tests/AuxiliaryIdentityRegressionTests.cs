using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>Modern auxiliary BOM is keyed by ProductId, NEVER MaterialId=0.</summary>
public class AuxiliaryIdentityRegressionTests
{
    private static (int order, int lot, int auxId, int wh) CreateModernBomOrder(TestHost h)
    {
        h.LoginAsAdmin();
        var db = h.Get<DatesErpDbContext>();
        int wa = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(new StockBalance { WarehouseId = wa, MaterialId = 1, QtyKg = 5000 },
            new StockBalance { WarehouseId = wa, MaterialId = 2, QtyKg = 5000 });
        db.SaveChanges();
        var master = h.Get<MasterDataService>();
        var aux = master.SaveProductFull(null, "AUX-REV-01", "عبوة تعبئة تجريبية", "004",
            "Pack", "قطعة", 0, 0, 0, null);
        Assert.True(aux.Ok, aux.Message);
        db.StockBalances.Add(new StockBalance { WarehouseId = wa, ProductId = aux.Id,
            MaterialId = null, PackageCount = 10 });
        db.SaveChanges();
        var req = h.Get<AuxiliaryManagementService>().SaveRequirement(null, 3, aux.Id,
            "قطعة", 0.04, "PerCarton"); // 100 finished cartons -> 4 auxiliary units
        Assert.True(req.Ok, req.Message);
        var receiving = h.Get<IReceivingService>();
        var ship = receiving.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        { new() { ProductId = 1, TreatmentRequired = false, PackageCount = 100,
            UnitWeightKg = 20, QtyKg = 2000 } });
        Assert.True(ship.Ok, ship.Message);
        Assert.True(receiving.ApproveShipment(ship.Id).Ok);
        int lot = db.Lots.OrderBy(l => l.Id).Last().Id;
        h.SetBusinessDate("2026-08-20");
        var planning = h.Get<IPlanningService>();
        var plan = planning.SavePlan("مواد بمفتاح الصنف", "Daily", "2026-08-20", "2026-08-20", 1, 1,
            new List<PlanItemDto> { new() { SourceType = "FromReceiving", CustomerId = 1,
                LotId = lot, ProductId = 3, PackagingTypeId = 1, PlannedCartons = 100,
                PlannedQtyKg = 500, ScheduledDate = "2026-08-20", SuggestedShiftId = 1,
                SuggestedLineId = 1 } });
        Assert.True(plan.Ok, plan.Message);
        Assert.True(planning.ApprovePlan(plan.Id).Ok);
        var orders = h.Get<IProductionOrderService>();
        var issued = orders.IssueTodayOrders();
        Assert.True(issued.Ok, issued.Message);
        int order = db.ProductionOrders.Single(o => o.SourcePlanId == plan.Id).Id;
        return (order, lot, aux.Id, wa);
    }

    [Fact]
    public void Modern_Bom_Approval_And_Cancellation_Use_The_Same_Product_And_Package_Stock_Key()
    {
        using var h = new TestHost();
        var x = CreateModernBomOrder(h);
        var db = h.Get<DatesErpDbContext>();
        var orders = h.Get<IProductionOrderService>();
        var material = db.ProductionOrderMaterials.Single(m => m.OrderId == x.order && m.AuxiliaryProductId == x.auxId);
        Assert.Equal(4, material.CalculatedQty, 2);
        Assert.Equal(0, material.MaterialId); // old schema field is not the warehouse key
        Assert.True(orders.ApproveOrder(x.order).Ok);
        var balance = db.StockBalances.Single(b => b.WarehouseId == x.wh && b.ProductId == x.auxId);
        Assert.Equal(6, balance.PackageCount);
        Assert.Equal(0, balance.QtyKg);
        var debit = db.InventoryTransactions.AsNoTracking().Single(t => t.OrderId == x.order
            && t.ProductId == x.auxId && t.ReferenceDocType == ReferenceDocType.MaterialIssue);
        Assert.Equal(-4, debit.PackageCount);
        Assert.Null(debit.MaterialId);
        Assert.DoesNotContain(db.InventoryTransactions, t => t.OrderId == x.order && t.MaterialId == 0);
        Assert.True(orders.CancelOrder(x.order, "إلغاء قبل التنفيذ").Ok);
        Assert.Equal(10, balance.PackageCount);
        Assert.Equal(4, db.ProductionOrderMaterials.Single(m => m.Id == material.Id).ReturnedQty, 2);
        var reversals = db.InventoryTransactions.AsNoTracking().Where(t => t.OrderId == x.order
            && t.ProductId == x.auxId).ToList();
        Assert.Equal(0, reversals.Sum(t => t.PackageCount));
        Assert.All(reversals, t => Assert.Null(t.MaterialId));
    }

    [Fact]
    public void Modern_Bom_Production_Shortfall_Returns_Only_The_Unused_Units()
    {
        using var h = new TestHost();
        var x = CreateModernBomOrder(h);
        var orders = h.Get<IProductionOrderService>();
        Assert.True(orders.ApproveOrder(x.order).Ok);
        Assert.True(orders.StartOrder(x.order).Ok);
        var close = h.Get<IExecutionService>().CloseProductionDay(x.order, 250, 50, 0, 0, 0,
            false, new List<DowntimeDto>(), false, consumedRawKg: 250);
        Assert.True(close.Ok, close.Message);
        var db = h.Get<DatesErpDbContext>();
        var mat = db.ProductionOrderMaterials.Single(m => m.OrderId == x.order && m.AuxiliaryProductId == x.auxId);
        Assert.Equal(4, mat.ActualIssuedQty, 2);
        Assert.Equal(2, mat.ConsumedQty, 2);
        Assert.Equal(2, mat.ReturnedQty, 2);
        var balance = db.StockBalances.Single(b => b.WarehouseId == x.wh && b.ProductId == x.auxId);
        Assert.Equal(8, balance.PackageCount);
        Assert.Equal(0, db.InventoryTransactions.Where(t => t.OrderId == x.order && t.MaterialId == 0).Count());
        Assert.Equal(-2, db.InventoryTransactions.Where(t => t.OrderId == x.order && t.ProductId == x.auxId)
            .Sum(t => t.PackageCount));
    }
}
