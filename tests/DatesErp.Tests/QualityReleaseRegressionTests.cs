using DatesErp.Application.Services;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>QC release is an exact, approved quantity; failures must be atomic.</summary>
public class QualityReleaseRegressionTests
{
    private static (int order, int lot, int execution) Prepare(TestHost host)
    {
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrderPacked(host, db, out var order, out var lot);
        var closed = host.Get<IExecutionService>().CloseProductionDay(order, 500, 100,
            0, 0, 0, false, new List<DowntimeDto>(), false);
        Assert.True(closed.Ok, closed.Message);
        int execution = db.ProductionExecutions.Single(e => e.OrderId == order).Id;
        var receipt = TestProductionDocumentFlow.SaveReceiptFromActual(host, order, null, "2026-08-24",
            new List<FinishedGoodsItemDto>
            { new() { ProductId = 3, LotId = lot, CustomerId = 1, PackagingTypeId = 1,
                PackageCount = 100, NetWeightKg = 500 } });
        Assert.True(receipt.Ok, receipt.Message);
        var fg = host.Get<IFinishedGoodsService>();
        Assert.True(fg.Issue(receipt.Id).Ok);
        Assert.True(fg.Receive(receipt.Id, null).Ok);
        host.LoginAsAdmin();
        return (order, lot, execution);
    }

    private static QualityItemDto Item((int order, int lot, int execution) src, double accepted)
        => new() { ProductId = 3, LotId = src.lot, CustomerId = 1, PackagingTypeId = 1,
            CheckedQtyKg = 500, AcceptedQtyKg = accepted, RejectedQtyKg = 500 - accepted,
            CheckedCartons = 100, AcceptedCartons = accepted / 5,
            RejectedCartons = (500 - accepted) / 5 };

    private static int SaveAndApprove(TestHost host, (int order, int lot, int execution) src, double accepted)
    {
        host.SetBusinessDate("2026-08-24"); // cooling date 26/08: warning, never a veto
        var service = host.Get<IQualityService>();
        var result = service.SaveCheck(src.order, src.execution, "2026-08-24", "نهائي",
            new List<QualityItemDto> { Item(src, accepted) },
            lab: new QualityLabDto { Decision = QualityGate.Passed, MoisturePct = 99 });
        Assert.True(result.Ok, result.Message);
        var approved = service.ApproveCheck(result.Id);
        Assert.True(approved.Ok, approved.Message);
        Assert.Contains("استرشادية", approved.Message); // out-of-range lab/cooling is advisory
        return result.Id;
    }

    private static int SaveCustomerDelivery(TestHost h, int lot, int cartons)
    {
        var saved = h.Get<ICustomerDeliveryService>().Save(1, "2026-08-25", null,
            new List<CustomerDeliveryItemDto> { new() { ProductId = 3, LotId = lot,
                PackagingTypeId = 1, PackageCount = cartons, QtyKg = cartons * 5 } });
        Assert.True(saved.Ok, saved.Message);
        return saved.Id;
    }

    [Fact]
    public void Passed_With_Zero_Accepted_Is_Refused_While_Approved_Rejection_Blocks_Shipment()
    {
        using var h = new TestHost();
        var src = Prepare(h);
        var quality = h.Get<IQualityService>();
        int before = h.Get<DatesErpDbContext>().QualityChecks.Count();
        var invalid = quality.SaveCheck(src.order, src.execution, "2026-08-24", "نهائي",
            new List<QualityItemDto> { Item(src, 0) },
            lab: new QualityLabDto { Decision = QualityGate.Passed });
        Assert.False(invalid.Ok);
        Assert.Contains("مقبولة", invalid.Message);
        Assert.Equal(before, h.Get<DatesErpDbContext>().QualityChecks.Count());

        var rejected = quality.SaveCheck(src.order, src.execution, "2026-08-24", "نهائي",
            new List<QualityItemDto> { Item(src, 0) },
            lab: new QualityLabDto { Decision = QualityGate.Rejected });
        Assert.True(rejected.Ok, rejected.Message);
        Assert.True(quality.ApproveCheck(rejected.Id).Ok);
        var ship = SaveCustomerDelivery(h, src.lot, 1);
        var denied = h.Get<ICustomerDeliveryService>().Approve(ship);
        Assert.False(denied.Ok);
        Assert.Contains("مرفوض", denied.Message);
    }

    [Fact]
    public void Approved_Partial_Quantity_Is_A_Hard_Ceiling_Across_Shipments_And_Correction_Waits_For_Return()
    {
        using var h = new TestHost();
        var src = Prepare(h);
        int check = SaveAndApprove(h, src, 250);
        var shipTooMuch = SaveCustomerDelivery(h, src.lot, 51);
        var cd = h.Get<ICustomerDeliveryService>();
        var over = cd.Approve(shipTooMuch);
        Assert.False(over.Ok);
        Assert.Contains("المطابق المعتمد", over.Message);
        int shipped = SaveCustomerDelivery(h, src.lot, 50);
        Assert.True(cd.Approve(shipped).Ok);
        var another = cd.Approve(SaveCustomerDelivery(h, src.lot, 1));
        Assert.False(another.Ok);
        Assert.Contains("المطابق المعتمد", another.Message);
        var quality = h.Get<IQualityService>();
        var premature = quality.RequestCorrection(check, "تراجع نتيجة الفحص");
        Assert.False(premature.Ok);
        Assert.Contains("شحنة", premature.Message);
        Assert.True(h.Get<DatesErpDbContext>().QualityChecks.AsNoTracking().Single(q => q.Id == check).IsApproved);
        Assert.True(cd.Unapprove(shipped).Ok); // return/reversal, not deletion from the ledger
        Assert.True(quality.RequestCorrection(check, "بعد عكس الشحنة").Ok);
        Assert.False(h.Get<DatesErpDbContext>().QualityChecks.AsNoTracking().Single(q => q.Id == check).IsApproved);
        var blocked = cd.Approve(SaveCustomerDelivery(h, src.lot, 1));
        Assert.False(blocked.Ok);
        Assert.Contains("فحص", blocked.Message);
    }

    [Fact]
    public void Failure_After_Persisting_Qc_Header_Rolls_Back_Header_Items_And_Changes()
    {
        using var h = new TestHost();
        var src = Prepare(h);
        var db = h.Get<DatesErpDbContext>();
        int headers = db.QualityChecks.Count();
        int items = db.QualityCheckItems.Count();
        int extras = db.QualityByProductRecords.Count();
        var r = h.Get<IQualityService>().SaveCheck(src.order, src.execution, "2026-08-24", "نهائي",
            new List<QualityItemDto> { Item(src, 500) },
            byProducts: new List<(int byProductId, double qtyKg)> { (999999, 1) },
            lab: new QualityLabDto { Decision = QualityGate.Passed });
        Assert.False(r.Ok);
        using var fresh = new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>()
            .UseSqlite(h.Connection!).Options);
        Assert.Equal(headers, fresh.QualityChecks.Count());
        Assert.Equal(items, fresh.QualityCheckItems.Count());
        Assert.Equal(extras, fresh.QualityByProductRecords.Count());
        // No stale tracked entities leak into the next action after rollback.
        Assert.True(h.Get<IQualityService>().SaveCheck(src.order, src.execution, "2026-08-24", "نهائي",
            new List<QualityItemDto> { Item(src, 500) },
            lab: new QualityLabDto { Decision = QualityGate.Passed }).Ok);
    }
}
