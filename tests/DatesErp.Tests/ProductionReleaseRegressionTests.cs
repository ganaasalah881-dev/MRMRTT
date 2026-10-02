using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>Executable regressions for delivery/receipt identity, rounding and atomic rollback.</summary>
public class ProductionReleaseRegressionTests
{
    private static (int order, int lot, int delivery, int line) PrepareDelivery(TestHost h)
    {
        h.LoginAsAdmin();
        var db = h.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrder(h, db, out var order, out var lot);
        var closed = h.Get<IExecutionService>().CloseProductionDay(order, 500, 67, 0, 0, 0,
            false, new List<DowntimeDto>(), false);
        Assert.True(closed.Ok, closed.Message);
        var execution = db.ProductionExecutions.Single(e => e.OrderId == order).Id;
        var deliveryService = h.Get<IProductionDeliveryService>();
        var draft = deliveryService.CreateDeliveryFromActual(execution, "2026-08-24");
        Assert.True(draft.Ok, draft.Message);
        var issued = deliveryService.IssueDelivery(draft.Id);
        Assert.True(issued.Ok, issued.Message);
        return (order, lot, draft.Id, db.ProductionDeliveryItems.Single(i => i.DeliveryId == draft.Id).Id);
    }

    private static int MakeReceipt(TestHost h, int order, int lot, int delivery, int line, double kg)
    {
        var fg = h.Get<IFinishedGoodsService>();
        var saved = fg.SaveReceipt(order, null, "2026-08-24", new List<FinishedGoodsItemDto>
        {
            new() { ProductId = 3, LotId = lot, CustomerId = 1, DeliveryItemId = line,
                PackageCount = 0, NetWeightKg = kg }
        }, delivery);
        Assert.True(saved.Ok, saved.Message);
        Assert.True(fg.Issue(saved.Id).Ok);
        return saved.Id;
    }

    private static (double kg, int cartons) FinishedBalance(TestHost h, int lot, int? pack = null)
    {
        using var db = new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>()
            .UseSqlite(h.Connection!).Options);
        var wh = db.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
        var rows = db.StockBalances.AsNoTracking().Where(b => b.WarehouseId == wh && b.ProductId == 3
            && b.LotId == lot && b.PackagingTypeId == pack && b.CustomerId == 1).ToList();
        return (rows.Sum(b => b.QtyKg), rows.Sum(b => b.PackageCount));
    }

    [Fact]
    public void Two_Receipts_Of_One_Delivery_Post_Exactly_67_Cartons_And_Reverse_Exactly()
    {
        using var h = new TestHost();
        var x = PrepareDelivery(h);
        var fg = h.Get<IFinishedGoodsService>();
        int first = MakeReceipt(h, x.order, x.lot, x.delivery, x.line, 250);
        Assert.True(fg.Receive(first, null).Ok);
        Assert.Equal(34, FinishedBalance(h, x.lot).cartons);
        int second = MakeReceipt(h, x.order, x.lot, x.delivery, x.line, 250);
        Assert.Equal(33, h.Get<DatesErpDbContext>().FinishedGoodsReceiptItems
            .Single(i => i.ReceiptId == second).PackageCount);
        Assert.NotEqual(h.Get<DatesErpDbContext>().FinishedGoodsReceipts.Single(r => r.Id == first).ReceiptNumber,
            h.Get<DatesErpDbContext>().FinishedGoodsReceipts.Single(r => r.Id == second).ReceiptNumber);
        Assert.True(fg.Receive(second, null).Ok);
        var db = h.Get<DatesErpDbContext>();
        Assert.Equal(67, FinishedBalance(h, x.lot).cartons);
        Assert.Equal(500, FinishedBalance(h, x.lot).kg, 2);
        Assert.Equal(34, db.FinishedGoodsReceiptItems.Single(i => i.ReceiptId == first).ReceivedPackageCount);
        Assert.Equal(33, db.FinishedGoodsReceiptItems.Single(i => i.ReceiptId == second).ReceivedPackageCount);
        Assert.True(fg.Unapprove(second).Ok);
        Assert.Equal(34, FinishedBalance(h, x.lot).cartons);
        Assert.True(fg.Unapprove(first).Ok);
        Assert.Equal((0d, 0), FinishedBalance(h, x.lot));
        var movements = db.InventoryTransactions.AsNoTracking().Where(t =>
            t.ReferenceDocType == ReferenceDocType.FinishedGoodsReceipt && t.ProductId == 3).ToList();
        Assert.Equal(0, movements.Sum(m => m.PackageCount));
        Assert.Equal(0, movements.Sum(m => m.QtyKg), 2);
    }

    [Fact]
    public void One_Receipt_Split_Into_Two_Postings_Uses_Cumulative_Rounding_And_Reverses_The_Ledger()
    {
        using var h = new TestHost();
        var x = PrepareDelivery(h);
        var fg = h.Get<IFinishedGoodsService>();
        int receipt = MakeReceipt(h, x.order, x.lot, x.delivery, x.line, 500);
        int item = h.Get<DatesErpDbContext>().FinishedGoodsReceiptItems.Single(i => i.ReceiptId == receipt).Id;
        Assert.True(fg.Receive(receipt, new Dictionary<int, double> { [item] = 250 }).Ok);
        Assert.Equal(34, FinishedBalance(h, x.lot).cartons);
        Assert.True(fg.Receive(receipt, new Dictionary<int, double> { [item] = 250 }).Ok);
        Assert.Equal(67, FinishedBalance(h, x.lot).cartons);
        Assert.True(fg.Unapprove(receipt).Ok);
        Assert.Equal((0d, 0), FinishedBalance(h, x.lot));
    }

    [Fact]
    public void Failed_Partial_Map_Does_Not_Consume_Receipt_Number_Or_Alter_Stock()
    {
        using var h = new TestHost();
        var x = PrepareDelivery(h);
        var fg = h.Get<IFinishedGoodsService>();
        int receipt = MakeReceipt(h, x.order, x.lot, x.delivery, x.line, 500);
        int item = h.Get<DatesErpDbContext>().FinishedGoodsReceiptItems.Single(i => i.ReceiptId == receipt).Id;
        Assert.False(fg.Receive(receipt, new Dictionary<int, double> { [item] = 0 }).Ok);
        Assert.False(fg.Receive(receipt, new Dictionary<int, double> { [item] = 100, [item + 999] = 1 }).Ok);
        using (var db = new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(h.Connection!).Options))
        {
            var row = db.FinishedGoodsReceipts.AsNoTracking().Single(r => r.Id == receipt);
            Assert.Equal(0, row.ReceiveCount);
            Assert.Null(row.ReceiptNumber);
            Assert.Equal(0, db.FinishedGoodsReceiptItems.AsNoTracking().Single(i => i.Id == item).ReceivedQtyKg);
        }
        Assert.Equal((0d, 0), FinishedBalance(h, x.lot));
        Assert.True(fg.Receive(receipt, null).Ok);
    }

    [Fact]
    public void Cancelling_Delivery_Voids_Unposted_Receipt_Without_Stock_Or_Reissuing()
    {
        using var h = new TestHost();
        var x = PrepareDelivery(h);
        var fg = h.Get<IFinishedGoodsService>();
        int receipt = MakeReceipt(h, x.order, x.lot, x.delivery, x.line, 500);
        var cancel = h.Get<IProductionDeliveryService>().CancelDelivery(x.delivery);
        Assert.True(cancel.Ok, cancel.Message);
        Assert.False(fg.Receive(receipt, null).Ok);
        Assert.False(fg.Issue(receipt).Ok);
        Assert.Equal(DocStatuses.Cancelled, h.Get<DatesErpDbContext>().FinishedGoodsReceipts.Single(r => r.Id == receipt).Status);
        Assert.Equal((0d, 0), FinishedBalance(h, x.lot));
    }

    [Fact]
    public void Historical_Carton_Count_Is_Recovered_From_Ledger_And_Cannot_Be_Guessed_On_Cancellation()
    {
        using var h = new TestHost();
        var x = PrepareDelivery(h);
        var fg = h.Get<IFinishedGoodsService>();
        int receipt = MakeReceipt(h, x.order, x.lot, x.delivery, x.line, 500);
        int item = h.Get<DatesErpDbContext>().FinishedGoodsReceiptItems.Single(i => i.ReceiptId == receipt).Id;
        Assert.True(fg.Receive(receipt, new Dictionary<int, double> { [item] = 250 }).Ok);
        var db = h.Get<DatesErpDbContext>();
        // Emulate migration from a pre-fix database: legacy rows have the new column zero.
        db.Database.ExecuteSqlRaw("UPDATE [FinishedGoodsReceiptItems] SET [ReceivedPackageCount]=0 WHERE [Id]={0}", item);
        db.ChangeTracker.Clear();
        var denied = fg.Unapprove(receipt);
        Assert.False(denied.Ok);
        Assert.Contains("كراتين", denied.Message);
        Assert.Equal(34, FinishedBalance(h, x.lot).cartons);
        var report = SchemaMigrator.Migrate(db);
        Assert.Contains(report, line => line.Contains("استعيدت كراتين"));
        Assert.Equal(34, db.FinishedGoodsReceiptItems.AsNoTracking().Single(i => i.Id == item).ReceivedPackageCount);
        Assert.True(fg.Unapprove(receipt).Ok);
        Assert.Equal((0d, 0), FinishedBalance(h, x.lot));
    }

    [Fact]
    public void Upgrading_Old_Sqlite_Receipt_Schema_Adds_Columns_And_Backfills_Ledger_Cartons_Idempotently()
    {
        using var h = new TestHost();
        var x = PrepareDelivery(h);
        int receipt = MakeReceipt(h, x.order, x.lot, x.delivery, x.line, 500);
        int item = h.Get<DatesErpDbContext>().FinishedGoodsReceiptItems.Single(i => i.ReceiptId == receipt).Id;
        var fg = h.Get<IFinishedGoodsService>();
        Assert.True(fg.Receive(receipt, new Dictionary<int, double> { [item] = 250 }).Ok);
        var db = h.Get<DatesErpDbContext>();
        db.Database.ExecuteSqlRaw("ALTER TABLE [FinishedGoodsReceiptItems] DROP COLUMN [ReceivedPackageCount]");
        db.ChangeTracker.Clear();
        var report = SchemaMigrator.Migrate(db);
        Assert.DoesNotContain(report, line => line.StartsWith("خطأ") || line.StartsWith("تعذر"));
        Assert.Contains(report, line => line.Contains("FinishedGoodsReceiptItems.ReceivedPackageCount"));
        Assert.Contains(report, line => line.Contains("استعيدت كراتين"));
        Assert.Equal(34, db.FinishedGoodsReceiptItems.AsNoTracking().Single(i => i.Id == item).ReceivedPackageCount);
        Assert.DoesNotContain(SchemaMigrator.Migrate(db), line => line.StartsWith("خطأ") || line.StartsWith("تعذر"));
        Assert.True(fg.Unapprove(receipt).Ok);
        Assert.Equal((0d, 0), FinishedBalance(h, x.lot));
    }

    [Fact]
    public void Planned_Shortfall_Settlement_Needs_Planning_Authority_Audit_And_Actual_Delivery()
    {
        using var h = new TestHost();
        h.LoginAsAdmin();
        var db = h.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrderPacked(h, db, out var order, out _);
        int planId = db.ProductionOrders.Single(o => o.Id == order).SourcePlanId!.Value;
        var close = h.Get<IExecutionService>().CloseProductionDay(order, 250, 50, 0, 0, 0,
            false, new List<DowntimeDto>(), false, consumedRawKg: 250);
        Assert.True(close.Ok, close.Message);
        var orders = h.Get<IProductionOrderService>();
        Assert.False(orders.CloseOrder(order).Ok); // textless shortfall must never settle itself
        var session = h.Services.GetRequiredService<DatesErp.Infrastructure.Session.SessionContext>();
        session.PermissionCache[("planning", "Approve")] = false;
        Assert.Throws<PermissionDeniedException>(() => orders.CloseOrder(order, "عجز 250 كجم: توقف معتمد"));
        Assert.Null(db.ProductionOrders.AsNoTracking().Single(o => o.Id == order).CloseReason);
        session.PermissionCache[("planning", "Approve")] = true;
        var settlement = orders.CloseOrder(order, "عجز 250 كجم: توقف معتمد");
        Assert.True(settlement.Ok, settlement.Message);
        Assert.Contains(db.AuditLogs, a => a.RecordId == order && a.ScreenName == "تسوية عجز الإنتاج"
            && a.ActionType == "اعتماد تسوية عجز");
        Assert.False(db.ProductionPlans.AsNoTracking().Single(p => p.Id == planId).IsClosed);
        int execution = db.ProductionExecutions.Single(e => e.OrderId == order).Id;
        var pd = h.Get<IProductionDeliveryService>();
        var draft = pd.CreateDeliveryFromActual(execution);
        Assert.True(draft.Ok, draft.Message);
        Assert.True(pd.IssueDelivery(draft.Id).Ok);
        Assert.True(db.ProductionPlans.AsNoTracking().Single(p => p.Id == planId).IsClosed);
        var revise = orders.CloseOrder(order, "عجز 250 كجم: نص مختلف");
        Assert.False(revise.Ok);
        Assert.Contains("لا تُعدَّل", revise.Message);
    }

    [Fact]
    public void Same_Product_And_Lot_In_Two_Packages_Stay_Separate_Until_Warehouse()
    {
        using var h = new TestHost();
        h.LoginAsAdmin();
        var db = h.Get<DatesErpDbContext>();
        int whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(new StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 5000 },
            new StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 5000 });
        db.SaveChanges();
        var rcv = h.Get<IReceivingService>();
        var shipment = rcv.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
            { new() { ProductId = 1, TreatmentRequired = false, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000 } });
        Assert.True(shipment.Ok, shipment.Message);
        Assert.True(rcv.ApproveShipment(shipment.Id).Ok);
        int lot = db.Lots.OrderBy(l => l.Id).Last().Id;
        h.SetBusinessDate("2026-08-20");
        var planning = h.Get<IPlanningService>();
        var plan = planning.SavePlan("عبوتان لنفس الدفعة", "Daily", "2026-08-20", "2026-08-20", 1, 1,
            new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", CustomerId = 1, LotId = lot, ProductId = 3,
                    PackagingTypeId = 1, PlannedCartons = 50, PlannedQtyKg = 250,
                    ScheduledDate = "2026-08-20", SuggestedLineId = 1, SuggestedShiftId = 1 },
                new() { SourceType = "FromReceiving", CustomerId = 1, LotId = lot, ProductId = 3,
                    PackagingTypeId = 2, PlannedCartons = 25, PlannedQtyKg = 250,
                    ScheduledDate = "2026-08-20", SuggestedLineId = 1, SuggestedShiftId = 1 }
            });
        Assert.True(plan.Ok, plan.Message);
        Assert.True(planning.ApprovePlan(plan.Id).Ok);
        var orders = h.Get<IProductionOrderService>();
        var created = orders.IssueTodayOrders();
        Assert.True(created.Ok, created.Message);
        int order = db.ProductionOrders.Single(o => o.SourcePlanId == plan.Id).Id;
        Assert.True(orders.ApproveOrder(order).Ok);
        Assert.True(orders.StartOrder(order).Ok);
        var parts = db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == order).ToList();
        Assert.Equal(2, parts.Count);
        var closed = h.Get<IExecutionService>().CloseProductionDay(order, 500, 75, 0, 0, 0,
            false, new List<DowntimeDto>(), false, consumedRawKg: 500,
            itemQtys: parts.Select(i => new CloseItemQtyDto { OrderItemId = i.Id,
                ProducedKg = i.PlannedQtyKg, ProducedCartons = i.PlannedCartons }).ToList());
        Assert.True(closed.Ok, closed.Message);
        var execution = db.ProductionExecutions.Single(e => e.OrderId == order).Id;
        var pd = h.Get<IProductionDeliveryService>();
        var createdDelivery = pd.CreateDeliveryFromActual(execution, "2026-08-24");
        Assert.True(createdDelivery.Ok, createdDelivery.Message);
        Assert.True(pd.IssueDelivery(createdDelivery.Id).Ok);
        var deliveryLines = db.ProductionDeliveryItems.AsNoTracking().Where(i => i.DeliveryId == createdDelivery.Id).ToList();
        Assert.Equal(2, deliveryLines.Count);
        Assert.Equal(50, deliveryLines.Single(i => i.PackagingTypeId == 1).PackageCount);
        Assert.Equal(25, deliveryLines.Single(i => i.PackagingTypeId == 2).PackageCount);
        var fg = h.Get<IFinishedGoodsService>();
        var receipt = fg.SaveReceipt(order, null, "2026-08-24", deliveryLines.Select(l => new FinishedGoodsItemDto
            { ProductId = 3, LotId = lot, DeliveryItemId = l.Id, NetWeightKg = l.QtyKg,
                CustomerId = 1, PackagingTypeId = l.PackagingTypeId, PackageCount = l.PackageCount }).ToList(), createdDelivery.Id);
        Assert.True(receipt.Ok, receipt.Message);
        Assert.True(fg.Issue(receipt.Id).Ok);
        var firstRow = db.FinishedGoodsReceiptItems.Single(i => i.ReceiptId == receipt.Id && i.PackagingTypeId == 1);
        var secondRow = db.FinishedGoodsReceiptItems.Single(i => i.ReceiptId == receipt.Id && i.PackagingTypeId == 2);
        Assert.True(fg.Receive(receipt.Id, new Dictionary<int, double> { [firstRow.Id] = 250 }).Ok);
        Assert.Equal((0d, 0), FinishedBalance(h, lot, 2)); // omitted row stays unreceived
        Assert.Equal(0, db.FinishedGoodsReceiptItems.AsNoTracking().Single(i => i.Id == secondRow.Id).ReceivedQtyKg);
        Assert.True(fg.Receive(receipt.Id, new Dictionary<int, double> { [secondRow.Id] = 250 }).Ok);
        Assert.Equal((250d, 50), FinishedBalance(h, lot, 1));
        Assert.Equal((250d, 25), FinishedBalance(h, lot, 2));
        // One QC may contain two different results for the SAME product/lot.
        // Its header Passed must release only the accepted production-line identity.
        var quality = h.Get<IQualityService>();
        var result = quality.SaveCheck(order, execution, "2026-08-24", "نهائي",
            parts.Select(i => new QualityItemDto { OrderItemId = i.Id, ProductId = 3, LotId = lot,
                CustomerId = 1, PackagingTypeId = i.PackagingTypeId, CheckedQtyKg = 250,
                AcceptedQtyKg = i.PackagingTypeId == 1 ? 250 : 0,
                RejectedQtyKg = i.PackagingTypeId == 1 ? 0 : 250 }).ToList(),
            lab: new QualityLabDto { Decision = QualityGate.Passed });
        Assert.True(result.Ok, result.Message);
        Assert.True(quality.ApproveCheck(result.Id).Ok);
        var cd = h.Get<ICustomerDeliveryService>();
        var allowed = cd.Save(1, "2026-08-25", null, new List<CustomerDeliveryItemDto>
            { new() { ProductId = 3, LotId = lot, PackagingTypeId = 1, PackageCount = 50, QtyKg = 250 } });
        Assert.True(allowed.Ok, allowed.Message);
        Assert.True(cd.Approve(allowed.Id).Ok);
        var rejected = cd.Save(1, "2026-08-25", null, new List<CustomerDeliveryItemDto>
            { new() { ProductId = 3, LotId = lot, PackagingTypeId = 2, PackageCount = 25, QtyKg = 250 } });
        Assert.True(rejected.Ok, rejected.Message);
        var blocked = cd.Approve(rejected.Id);
        Assert.False(blocked.Ok);
        Assert.Contains("صفر", blocked.Message);
    }
}
