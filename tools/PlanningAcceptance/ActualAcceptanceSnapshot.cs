using System.Text.Json;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Fresh database reads immediately after actual recording, before explicit delivery, receipt and QC approval.</summary>
public static class ActualAcceptanceSnapshot
{
    public static void Write(IServiceProvider services, string path)
    {
        using var scope = services.CreateScope(); var sp = scope.ServiceProvider; var db = sp.GetRequiredService<DatesErpDbContext>();
        var execution = db.ProductionExecutions.AsNoTracking().Single(e => e.IsDayClosed);
        var order = db.ProductionOrders.AsNoTracking().Single(o => o.Id == execution.OrderId);
        var items = db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == order.Id).ToList();
        var plan = db.ProductionPlans.AsNoTracking().Include(p => p.Items).Single(p => p.Id == order.SourcePlanId);
        var quality = db.QualityChecks.AsNoTracking().Include(q => q.Items).Single(q => q.ExecutionId == execution.Id);
        // The current flow must NOT create a warehouse receipt from actual recording.
        var receiptExists = db.FinishedGoodsReceipts.AsNoTracking().Any(r => r.OrderId == order.Id);
        var report = sp.GetRequiredService<IReportService>().Run("daily_production", new());
        var result = new
        {
            provider = db.Database.ProviderName, database = db.Database.GetDbConnection().Database,
            checkpoint = "After actual execution and raw consumption; BEFORE issuing delivery, stocking finished goods or approving QC", windowsWpfExecuted = false,
            plan = new { plan.DocumentNumber, plan.Status, items = plan.Items.Select(i => new { i.Id, i.ProductId, i.CustomerId, i.PlannedCartons, i.PlannedQtyKg, i.ProducedQtyKg }) },
            order = new { order.Id, order.DocumentNumber, order.CustomerId, order.Status, order.ProductionDate },
            items = items.Select(i => new { i.Id, i.ProductId, i.LotId, i.CustomerId, i.PlannedCartons, i.ProducedCartons, difference = i.PlannedCartons - i.ProducedCartons, i.PlannedQtyKg, i.ProducedQtyKg }),
            execution = new { execution.Id, execution.DocumentNumber, execution.ActualCartons, execution.ActualQtyKg, execution.ConsumedRawKg, execution.RemainingInHallKg, execution.IsDayClosed, execution.QualitySent, execution.ClosingNotes },
            downtime = db.ExecutionDowntimes.AsNoTracking().Where(d => d.ExecutionId == execution.Id).Select(d => new { d.Hours, d.ReasonAr }).ToList(),
            secondary = db.ExecutionByProducts.AsNoTracking().Where(b => b.ExecutionId == execution.Id).ToList().Select(b => new { b.ByProductId, b.Qty, definition = db.Products.AsNoTracking().Where(d => d.Id == b.ByProductId && d.ItemType == "ByProduct").Select(d => new { d.ProductNameAr, d.UnitOfMeasure }).Single() }),
            quality = new { quality.Id, quality.DocumentNumber, quality.Status, quality.IsApproved, quality.TotalCheckedCartons, quality.TotalCheckedKg, quality.AcceptedKg, quality.ExpectedCheckDate, items = quality.Items.Select(i => new { i.ProductId, i.LotId, i.CheckedCartons, i.CheckedQtyKg, i.AcceptedQtyKg, i.Notes }) },
            receipt = new { exists = receiptExists, note = "Warehouse receipt must be created and issued separately after the production delivery." },
            stock = db.StockBalances.AsNoTracking().Where(b => b.CustomerId == order.CustomerId).Select(b => new { b.WarehouseId, b.ProductId, b.LotId, b.CustomerId, b.QtyKg, b.PackageCount }).ToList(),
            movements = db.InventoryTransactions.AsNoTracking().Where(t => t.OrderId == order.Id).Select(t => new { t.TxnNumber, t.ReferenceDocType, t.ReferenceDocNumber, t.ProductId, t.LotId, t.CustomerId, t.QtyKg, t.PackageCount }).ToList(),
            report = new { report.TitleAr, report.Columns, report.Rows, report.Summary }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
