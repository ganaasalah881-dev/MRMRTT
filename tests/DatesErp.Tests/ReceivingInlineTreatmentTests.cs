using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Tests;

public class ReceivingInlineTreatmentTests
{
    private static readonly DateTime Receipt = new(2026, 9, 8);
    internal sealed class Clock : TimeProvider
    {
        public DateTime Now = Receipt.AddHours(12);
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
    private sealed class Fixture : IDisposable
    {
        public Clock Clock = new();
        public TestHost Host;
        public DatesErpDbContext Db => Host.Get<DatesErpDbContext>();
        public IReceivingService Service => Host.Get<IReceivingService>();
        public Fixture() { Host = new TestHost(Clock); Host.LoginAsAdmin(); }
        public OpResult Save(params ShipmentItemDto[] items) => Service.SaveShipment(1, "08/09/2026", "08/09/2026", items.ToList());
        public int Approve(params ShipmentItemDto[] items)
        {
            var saved = Save(items); Assert.True(saved.Ok, saved.Message);
            var approved = Service.ApproveShipment(saved.Id); Assert.True(approved.Ok, approved.Message);
            return saved.Id;
        }
        public void Dispose() => Host.Dispose();
    }
    private static ShipmentItemDto Item(bool? decision = false, DateTime? until = null) => new()
    {
        TreatmentRequired = decision, TreatmentUntilDate = until, ProductId = 1,
        PackagingTypeId = 3, PackageCount = 10, UnitWeightKg = 20, QtyKg = 200, ReceiptUnit = "سلة"
    };
    private sealed class ConsumptionProbe : ServiceBase
    {
        public ConsumptionProbe(TestHost host) : base(host.Get<DatesErpDbContext>(), host.Get<ICurrentSession>(), host.Get<INumberingService>()) { }
        public OpResult Consume(int id, double qty) => RunOp(() => { ConsumeLot(id, qty, "اختبار مسار الصرف المشترك"); return OpResult.Success(); });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void No_Is_Authoritative_Regardless_Of_Product_Flag_And_Ignores_Date(bool flag)
    {
        using var f = new Fixture();
        f.Db.Products.Single(p => p.Id == 1).RequiresTreatment = flag; f.Db.SaveChanges();
        f.Approve(Item(false, Receipt.AddDays(-5)));
        var item = f.Db.ShipmentItems.AsNoTracking().Single(); var lot = f.Db.Lots.Single();
        Assert.False(item.TreatmentRequired); Assert.Null(item.TreatmentUntilDate);
        Assert.Empty(f.Db.RawTreatments); Assert.Empty(f.Db.ShipmentItemTreatmentParts);
        Assert.Equal(200, lot.AvailableQtyKg); Assert.Equal(0, lot.UnderTreatmentQtyKg);
        Assert.True(new ConsumptionProbe(f.Host).Consume(lot.Id, 10).Ok);
    }

    [Theory]
    [InlineData(0)] [InlineData(7)] [InlineData(14)] [InlineData(30)] [InlineData(43)]
    public void Arbitrary_Calendar_Date_Not_A_Grade_Or_Approval_Time(int days)
    {
        using var f = new Fixture(); f.Db.Products.Single(p => p.Id == 1).RequiresTreatment = false; f.Db.SaveChanges();
        f.Clock.Now = Receipt.AddHours(20);
        int id = f.Approve(Item(true, Receipt.AddDays(days).AddHours(18)));
        var item = f.Db.ShipmentItems.AsNoTracking().Single(); var t = f.Db.RawTreatments.Single(); var lot = f.Db.Lots.Single();
        Assert.True(item.TreatmentRequired); Assert.Equal(Receipt.AddDays(days), item.TreatmentUntilDate);
        Assert.Equal(item.Id, t.ReceivingItemId); Assert.Equal(Receipt, t.StartedAt);
        Assert.Equal(item.TreatmentUntilDate, t.ExpectedReadyAt); Assert.Equal(days * 24, t.DurationHours);
        Assert.Null(t.TreatmentTypeId); Assert.Empty(f.Db.ShipmentItemTreatmentParts);
        Assert.Equal(days == 0 ? 200 : 0, lot.AvailableQtyKg);
        Assert.Equal(days == 0 ? 0 : 200, lot.UnderTreatmentQtyKg);
        Assert.Equal(days == 0, f.Service.GetTreatmentStates(id).Single().CompletedAt != null);
    }

    [Fact]
    public void Mixed_Same_Product_Three_Rows_Remain_Independent_And_Release_At_Their_Own_Dates()
    {
        using var f = new Fixture(); int id = f.Approve(Item(true, Receipt.AddDays(7)), Item(), Item(true, Receipt.AddDays(12)));
        var lots = f.Db.Lots.OrderBy(l => l.Id).ToList();
        Assert.Equal(new double[] { 0, 200, 0 }, lots.Select(l => l.AvailableQtyKg));
        Assert.Equal(2, f.Db.RawTreatments.Count());
        f.Clock.Now = Receipt.AddDays(7).AddTicks(-1); f.Service.ProcessDueTreatments();
        Assert.Equal(400, f.Db.Lots.Sum(l => l.UnderTreatmentQtyKg));
        f.Clock.Now = Receipt.AddDays(7); f.Service.ProcessDueTreatments();
        Assert.Equal(new double[] { 200, 200, 0 }, f.Db.Lots.OrderBy(l => l.Id).ToList().Select(l => l.AvailableQtyKg));
        var states = f.Service.GetTreatmentStates(id);
        Assert.Contains("جاهز", states[0].StateAr); Assert.Equal("لا يحتاج معالجة", states[1].StateAr); Assert.Equal("قيد المعالجة", states[2].StateAr);
        f.Clock.Now = Receipt.AddDays(12); f.Service.ProcessDueTreatments();
        Assert.Equal(0, f.Db.Lots.Sum(l => l.UnderTreatmentQtyKg));
        Assert.Equal(600, f.Db.StockBalances.Sum(b => b.QtyKg));
        Assert.Equal(30, f.Db.StockBalances.Sum(b => b.PackageCount));
        int moves = f.Db.InventoryTransactions.Count(); f.Service.ProcessDueTreatments(); f.Service.ProcessDueTreatments();
        Assert.Equal(moves, f.Db.InventoryTransactions.Count());
    }

    [Theory]
    [InlineData("missing-choice")] [InlineData("missing-date")] [InlineData("earlier-date")]
    [InlineData("legacy-parts")] [InlineData("nan")] [InlineData("negative-packages")]
    public void Invalid_Input_Rolls_Back_All_Rows(string scenario)
    {
        using var f = new Fixture(); var bad = Item(true, Receipt.AddDays(7));
        switch (scenario)
        {
            case "missing-choice": bad.TreatmentRequired = null; break;
            case "missing-date": bad.TreatmentUntilDate = null; break;
            case "earlier-date": bad.TreatmentUntilDate = Receipt.AddDays(-1); break;
            case "legacy-parts": bad.TreatmentParts.Add(new() { QtyKg = 200, DurationHours = 168 }); break;
            case "nan": bad.QtyKg = double.NaN; break;
            case "negative-packages": bad.PackageCount = -1; break;
        }
        var result = f.Save(Item(), bad); Assert.False(result.Ok, result.Message);
        Assert.Empty(f.Db.Shipments.AsNoTracking()); Assert.Empty(f.Db.ShipmentItems.AsNoTracking());
        Assert.Empty(f.Db.Lots); Assert.Empty(f.Db.RawTreatments); Assert.Empty(f.Db.InventoryTransactions);
    }

    [Theory]
    [InlineData("missing-choice")] [InlineData("missing-date")] [InlineData("earlier-date")] [InlineData("inactive-product")]
    public void Approval_Revalidates_Persisted_Draft(string mutation)
    {
        using var f = new Fixture(); var saved = f.Save(Item(true, Receipt.AddDays(7))); Assert.True(saved.Ok);
        var item = f.Db.ShipmentItems.Single();
        switch (mutation)
        {
            case "missing-choice": item.TreatmentRequired = null; break;
            case "missing-date": item.TreatmentUntilDate = null; break;
            case "earlier-date": item.TreatmentUntilDate = Receipt.AddDays(-1); break;
            case "inactive-product": f.Db.Products.Single(p => p.Id == 1).IsActive = false; break;
        }
        f.Db.SaveChanges(); Assert.False(f.Service.ApproveShipment(saved.Id).Ok);
        Assert.False(f.Db.Shipments.AsNoTracking().Single().IsApproved);
        Assert.Empty(f.Db.Lots); Assert.Empty(f.Db.StockBalances); Assert.Empty(f.Db.InventoryTransactions);
    }

    [Fact]
    public void Editing_Yes_To_No_Clears_Date_And_Failed_Edit_Preserves_Draft()
    {
        using var f = new Fixture(); var saved = f.Save(Item(true, Receipt.AddDays(7)));
        var bad = f.Service.SaveShipment(1, null, "08/09/2026", new() { Item(true) }, existingId: saved.Id);
        Assert.False(bad.Ok); Assert.Equal(Receipt.AddDays(7), f.Db.ShipmentItems.AsNoTracking().Single().TreatmentUntilDate);
        var no = f.Service.SaveShipment(1, null, "08/09/2026", new() { Item(false, Receipt.AddDays(7)) }, existingId: saved.Id);
        Assert.True(no.Ok, no.Message); Assert.Null(f.Db.ShipmentItems.AsNoTracking().Single().TreatmentUntilDate);
        Assert.True(f.Service.ApproveShipment(saved.Id).Ok); Assert.Empty(f.Db.RawTreatments);
    }

    [Fact]
    public void No_Manual_Release_Cancel_Reject_Start_Or_Edit_Can_Bypass_Date()
    {
        using var f = new Fixture(); int id = f.Approve(Item(true, Receipt.AddDays(7)));
        var raw = f.Host.Get<IRawTreatmentService>(); var t = f.Db.RawTreatments.Single(); var lot = f.Db.Lots.Single();
        Assert.False(raw.Release(t.Id, 200).Ok); Assert.False(raw.Cancel(t.Id, "تجاوز").Ok);
        Assert.False(raw.Reject(t.Id, 200, "تجاوز").Ok);
        Assert.False(raw.Start(new() { LotId = lot.Id, QtyKg = 10, DurationHours = 1 }).Ok);
        Assert.False(f.Service.UnapproveShipment(id).Ok);
        Assert.False(f.Service.SaveShipment(1, null, "08/09/2026", new() { Item() }, existingId: id).Ok);
        Assert.False(new ConsumptionProbe(f.Host).Consume(lot.Id, 1).Ok);
        // Even inconsistent ready counters cannot bypass the authoritative date/completion guard.
        lot = f.Db.Lots.Single(); lot.TreatmentReadyQtyKg = 200; f.Db.SaveChanges();
        Assert.False(new ConsumptionProbe(f.Host).Consume(lot.Id, 1).Ok);
        Assert.Equal(200, f.Db.Lots.AsNoTracking().Single().InStockQtyKg);
    }

    [Fact]
    public void Late_Approval_Completes_Immediately_Without_Shifting_Chosen_Date()
    {
        using var f = new Fixture(); var saved = f.Save(Item(true, Receipt.AddDays(7)));
        f.Clock.Now = Receipt.AddDays(10); Assert.True(f.Service.ApproveShipment(saved.Id).Ok);
        Assert.Equal(Receipt.AddDays(7), f.Db.RawTreatments.Single().ExpectedReadyAt);
        Assert.Equal(200, f.Db.Lots.Single().AvailableQtyKg);
    }

    [Fact]
    public void Actual_Receiving_Warehouse_Is_Used_On_Both_Receipt_And_Due_Return()
    {
        using var f = new Fixture(); var wh = new Warehouse { WarehouseCode = "WRM-CUSTOM", WarehouseNameAr = "مخزن آخر", IsActive = true };
        f.Db.Warehouses.Add(wh); f.Db.SaveChanges();
        var saved = f.Service.SaveShipment(1, null, "08/09/2026", new() { Item(true, Receipt.AddDays(7)) }, warehouseId: wh.Id);
        Assert.True(saved.Ok); Assert.True(f.Service.ApproveShipment(saved.Id).Ok);
        Assert.Equal(0, f.Db.StockBalances.Where(b => b.WarehouseId == wh.Id).Sum(b => b.QtyKg));
        f.Clock.Now = Receipt.AddDays(7); f.Service.ProcessDueTreatments();
        Assert.Equal(200, f.Db.StockBalances.Where(b => b.WarehouseId == wh.Id).Sum(b => b.QtyKg));
        Assert.Equal(10, f.Db.StockBalances.Where(b => b.WarehouseId == wh.Id).Sum(b => b.PackageCount));
    }

    [Fact]
    public void Approval_Failure_After_An_Earlier_Row_Rolls_Back_The_Whole_Document()
    {
        using var f = new Fixture(); var saved = f.Save(Item(), Item(true, Receipt.AddDays(7)));
        var wh = f.Db.Warehouses.Single(w => w.WarehouseCode == "WTRT"); wh.WarehouseCode = "DISABLED"; f.Db.SaveChanges();
        Assert.False(f.Service.ApproveShipment(saved.Id).Ok);
        Assert.Empty(f.Db.Lots.AsNoTracking()); Assert.Empty(f.Db.RawTreatments.AsNoTracking());
        Assert.Empty(f.Db.StockBalances.AsNoTracking()); Assert.Empty(f.Db.InventoryTransactions.AsNoTracking());
        Assert.False(f.Db.Shipments.AsNoTracking().Single().IsApproved);
    }

    [Fact]
    public void Due_Stock_Failure_Does_Not_Mark_Row_Ready_And_Retry_Is_Idempotent()
    {
        using var f = new Fixture(); f.Approve(Item(true, Receipt.AddDays(7)));
        var wh = f.Db.Warehouses.Single(w => w.WarehouseCode == "WTRT");
        var balance = f.Db.StockBalances.Single(b => b.WarehouseId == wh.Id); balance.QtyKg = 0; f.Db.SaveChanges();
        f.Clock.Now = Receipt.AddDays(7); int count = f.Db.InventoryTransactions.Count();
        Assert.Throws<DomainException>(() => f.Service.ProcessDueTreatments());
        Assert.Null(f.Db.ShipmentItems.AsNoTracking().Single().TreatmentCompletedAt);
        Assert.Equal(count, f.Db.InventoryTransactions.Count());
        f.Db.StockBalances.Single(b => b.WarehouseId == wh.Id).QtyKg = 200; f.Db.SaveChanges();
        f.Service.ProcessDueTreatments(); f.Service.ProcessDueTreatments();
        Assert.Equal(count + 2, f.Db.InventoryTransactions.Count()); Assert.Equal(200, f.Db.Lots.Single().AvailableQtyKg);
    }

    [Fact]
    public void Reopen_New_Scope_After_Downtime_Catches_Up_And_Reports_Per_Row()
    {
        using var f = new Fixture(); int id = f.Approve(Item(true, Receipt.AddDays(7)), Item());
        f.Clock.Now = Receipt.AddDays(15);
        using var scope = f.Host.Services.CreateScope(); var service = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        Assert.Contains("جاهز", service.GetTreatmentStates(id)[0].StateAr);
        var report = scope.ServiceProvider.GetRequiredService<IReportService>().Run("receiving_line_treatment", new());
        Assert.Equal(2, report.Rows.Count); Assert.Equal("نعم", report.Rows[0][8]); Assert.Equal("15/09/2026", report.Rows[0][9]);
        Assert.Contains("جاهز", report.Rows[0][10].ToString()); Assert.Equal("لا", report.Rows[1][8]); Assert.Null(report.Rows[1][9]);
        Assert.All(report.RowLinks, link => Assert.Equal(id, link.Id));
    }

    [Fact]
    public void Future_Planning_Forecast_Does_Not_Mean_Ready_Now_And_No_Double_Count_After_Consumption()
    {
        using var f = new Fixture(); f.Approve(Item(true, Receipt.AddDays(7))); var lotId = f.Db.Lots.Single().Id;
        var raw = f.Host.Get<IRawTreatmentService>(); var plan = f.Host.Get<IPlanningService>();
        Assert.Equal(0, raw.GetAvailableForDate(lotId, Receipt.AddDays(6)));
        Assert.Equal(200, raw.GetAvailableForDate(lotId, Receipt.AddDays(7)));
        Assert.Empty(plan.GetAvailableLots());
        Assert.Equal(200, plan.GetAvailableLots(forDate: Receipt.AddDays(7)).Single().AvailableForDateKg);
        Assert.False(new ConsumptionProbe(f.Host).Consume(lotId, 10).Ok);
        f.Clock.Now = Receipt.AddDays(7); f.Service.ProcessDueTreatments();
        Assert.True(new ConsumptionProbe(f.Host).Consume(lotId, 30).Ok);
        Assert.Equal(170, raw.GetAvailableForDate(lotId, Receipt.AddDays(7)));
        Assert.Equal(0, raw.GetAvailableForDate(lotId, Receipt.AddDays(6)));
        Assert.Equal(170, plan.GetAvailableLots().Single().AvailableForDateKg);
    }

    [Fact]
    public void Remaining_Receipt_Preserves_Per_Line_Choice_Date_Unit_And_Warehouse()
    {
        using var f = new Fixture(); var pending = Item(true, Receipt.AddDays(20)); pending.ItemStatus = "Pending";
        int id = f.Approve(Item(), pending); f.Clock.Now = Receipt.AddDays(1);
        var result = f.Service.ReceiveRemaining(id); Assert.True(result.Ok, result.Message);
        var item = f.Db.ShipmentItems.Single(i => i.ShipmentId == result.Id);
        Assert.True(item.TreatmentRequired); Assert.Equal(Receipt.AddDays(20), item.TreatmentUntilDate); Assert.Equal("سلة", item.ReceiptUnit);
        Assert.True(f.Service.ApproveShipment(result.Id).Ok);
        Assert.Equal(Receipt.AddDays(20), f.Db.RawTreatments.Single().ExpectedReadyAt);
    }

    [Fact]
    public void Stale_Completion_Token_Cannot_Overwrite_Another_Scopes_Release()
    {
        using var f = new Fixture(); f.Approve(Item(true, Receipt.AddDays(7)));
        using var a = f.Host.Services.CreateScope(); var dbA = a.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var stale = dbA.ShipmentItems.Single();
        f.Clock.Now = Receipt.AddDays(7); f.Service.ProcessDueTreatments();
        stale.TreatmentCompletedAt = f.Clock.Now.AddHours(1);
        Assert.Throws<DbUpdateConcurrencyException>(() => dbA.SaveChanges());
        Assert.Equal(200, f.Db.StockBalances.Sum(b => b.QtyKg));
    }

    [Fact]
    public void Migration_Adds_Nullable_Columns_Without_Inventing_Decisions_And_Is_Repeatable()
    {
        using var f = new Fixture(); f.Approve(Item()); f.Db.Lots.Single().TreatmentReadyQtyKg = 0; f.Db.SaveChanges(); f.Db.ChangeTracker.Clear();
        f.Db.Database.ExecuteSqlRaw("DROP INDEX [IX_RawTreatments_ReceivingItemId]");
        f.Db.Database.ExecuteSqlRaw("ALTER TABLE RawTreatments DROP COLUMN ReceivingItemId");
        foreach (var col in new[] { "TreatmentRequired", "TreatmentUntilDate", "TreatmentCompletedAt" })
            f.Db.Database.ExecuteSqlRaw("ALTER TABLE ShipmentItems DROP COLUMN " + col);
        var log = SchemaMigrator.Migrate(f.Db); Assert.DoesNotContain(log, s => s.Contains("خطأ") || s.Contains("تعذّر"));
        Assert.Null(f.Db.ShipmentItems.Single().TreatmentRequired); Assert.Equal(200, f.Db.Lots.Single().TreatmentReadyQtyKg);
        SchemaMigrator.Migrate(f.Db); Assert.Null(f.Db.ShipmentItems.Single().TreatmentRequired);
    }

    [Fact]
    public void Readiness_Backfill_Never_Releases_A_Current_Yes_Row_With_Missing_Tracking()
    {
        using var f = new Fixture(); f.Approve(Item(true, Receipt.AddDays(7)));
        f.Db.RawTreatments.RemoveRange(f.Db.RawTreatments); f.Db.Lots.Single().UnderTreatmentQtyKg = 0; f.Db.SaveChanges();
        SchemaMigrator.Migrate(f.Db);
        Assert.Equal(0, f.Db.Lots.Single().TreatmentReadyQtyKg); Assert.Null(f.Db.ShipmentItems.Single().TreatmentCompletedAt);
        Assert.False(new ConsumptionProbe(f.Host).Consume(f.Db.Lots.Single().Id, 1).Ok);
    }
    [Fact]
    public void Expired_Pending_Date_Is_Reselected_Per_Line_Without_Changing_Approved_Original()
    {
        using var f = new Fixture(); var a = Item(true, Receipt.AddDays(7)); a.ItemStatus = "Pending";
        var b = Item(true, Receipt.AddDays(12)); b.ItemStatus = "Pending";
        int id = f.Approve(Item(), a, b); f.Clock.Now = Receipt.AddDays(15);
        Assert.False(f.Service.ReceiveRemaining(id).Ok); // no silent extension, no partial transfer
        var pending = f.Db.ShipmentItems.Where(i => i.ShipmentId == id && i.Status == "Pending").OrderBy(i => i.Id).ToList();
        Assert.Equal(2, pending.Count);
        var choices = new List<ReceivingTreatmentChoiceDto>
        {
            new() { ShipmentItemId = pending[0].Id, TreatmentRequired = true, UntilDate = Receipt.AddDays(30) },
            new() { ShipmentItemId = pending[1].Id, TreatmentRequired = false, UntilDate = Receipt.AddDays(12) }
        };
        var result = f.Service.ReceiveRemaining(id, choices, f.Clock.Now); Assert.True(result.Ok, result.Message);
        var rows = f.Db.ShipmentItems.Where(i => i.ShipmentId == result.Id).OrderBy(i => i.Id).ToList();
        Assert.True(rows[0].TreatmentRequired); Assert.Equal(Receipt.AddDays(30), rows[0].TreatmentUntilDate);
        Assert.False(rows[1].TreatmentRequired); Assert.Null(rows[1].TreatmentUntilDate);
        Assert.Equal(Receipt.AddDays(7), f.Db.ShipmentItems.Single(i => i.Id == pending[0].Id).TreatmentUntilDate);
        Assert.True(f.Service.ApproveShipment(result.Id).Ok);
    }

    [Fact]
    public void Real_Production_Approval_Start_And_Close_Are_Blocked_Until_Due_Then_Use_Actual_Warehouse()
    {
        using var f = new Fixture(); var wh = new Warehouse { WarehouseCode = "WRM-R2", WarehouseNameAr = "استلام 2", IsActive = true };
        f.Db.Warehouses.Add(wh); f.Db.SaveChanges();
        var saved = f.Service.SaveShipment(1, null, "08/09/2026", new() { Item(true, Receipt.AddDays(7)) }, warehouseId: wh.Id);
        Assert.True(saved.Ok); Assert.True(f.Service.ApproveShipment(saved.Id).Ok); var lot = f.Db.Lots.Single();
        var orders = f.Host.Get<IProductionOrderService>();
        var planning = f.Host.Get<IPlanningService>();
        var due = Receipt.AddDays(7).ToString("dd/MM/yyyy");
        var plan = planning.SavePlan("خطة بعد انتهاء المعالجة", "Daily", due, due, 1, 1,
            new() { new() { ProductId = 3, PackagingTypeId = 1, LotId = lot.Id, CustomerId = 1,
                SourceType = "FromReceiving", PlannedQtyKg = 100, PlannedCartons = 20,
                ScheduledDate = due, SuggestedShiftId = 1, SuggestedLineId = 1 } });
        Assert.True(plan.Ok, plan.Message);
        var approvedPlan = planning.ApprovePlan(plan.Id);
        Assert.True(approvedPlan.Ok, approvedPlan.Message);
        Assert.Empty(orders.GetTodayProduction().Rows); // not today's approved schedule yet
        Assert.False(orders.IssueTodayOrders().Ok);
        Assert.Empty(f.Db.ProductionOrders);
        f.Clock.Now = Receipt.AddDays(7); f.Service.ProcessDueTreatments();
        var issued = orders.IssueTodayOrders(); Assert.True(issued.Ok, issued.Message);
        var order = f.Db.ProductionOrders.Single();
        // External corruption of the treatment gate must still block approval/start/close.
        var receiptItem = f.Db.ShipmentItems.Single(); receiptItem.TreatmentUntilDate = Receipt.AddDays(8); f.Db.SaveChanges();
        Assert.False(orders.ApproveOrder(order.Id).Ok);
        var corrupt = f.Db.ProductionOrders.Single(); corrupt.IsApproved = true; corrupt.Status = DocStatuses.Scheduled; f.Db.SaveChanges();
        Assert.False(orders.StartOrder(order.Id).Ok);
        Assert.False(f.Host.Get<IExecutionService>().CloseProductionDay(order.Id, 100, 20, 0, 0, 0, false, new(), false, consumedRawKg: 100).Ok);
        receiptItem = f.Db.ShipmentItems.Single(); receiptItem.TreatmentUntilDate = Receipt.AddDays(7); f.Db.SaveChanges();
        var freshOrder = f.Db.ProductionOrders.Single(); freshOrder.IsApproved = false; freshOrder.Status = DocStatuses.Draft; f.Db.SaveChanges();
        var approved = orders.ApproveOrder(order.Id); Assert.True(approved.Ok, approved.Message);
        Assert.True(orders.StartOrder(order.Id).Ok);
        var close = f.Host.Get<IExecutionService>().CloseProductionDay(order.Id, 100, 20, 0, 0, 0, false, new(), false, consumedRawKg: 100);
        Assert.True(close.Ok, close.Message);
        Assert.Equal(100, f.Db.StockBalances.Where(b => b.WarehouseId == wh.Id && b.LotId == lot.Id).Sum(b => b.QtyKg));
        Assert.Equal(100, f.Db.Lots.Single().InStockQtyKg);
    }

}
