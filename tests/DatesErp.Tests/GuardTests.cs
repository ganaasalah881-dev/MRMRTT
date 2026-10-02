using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Xunit;

namespace DatesErp.Tests;

/// <summary>§8/§39 — القيود التشغيلية ومنع التعارض والازدواج والأرصدة السالبة.</summary>
public class GuardTests
{
    [Fact]
    public void Cannot_Issue_More_Than_Available_Stock()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrder(host, db, out var oid, out var lotId);

        // محاولة صرف مواد أكبر من الرصيد المتوفر
        var db2 = host.Get<DatesErpDbContext>();
        var bal = db2.StockBalances.First(b => b.MaterialId == 1);
        bal.QtyKg = 1; // رصيد شبه معدوم
        db2.SaveChanges();

        var orders = host.Get<IProductionOrderService>();
        var r = orders.IssueMaterials(oid, new Dictionary<int, double> { [1] = 500 });
        Assert.False(r.Ok);
        Assert.Contains("أكبر من المتوفر", r.Message);
    }

    [Fact]
    public void Cannot_Produce_More_Than_Planned()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrder(host, db, out var oid, out _);

        // §المسار الفعلي: الإقفال هو موضع الحارس — المخطط 500 فقط
        host.Get<IProductionOrderService>().StartOrder(oid);
        var r = host.Get<IExecutionService>()
            .CloseProductionDay(oid, 5000, 0, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        Assert.False(r.Ok);
    }

    [Fact]
    public void Cannot_Close_Incomplete_Order()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrder(host, db, out var oid, out _);

        var orders = host.Get<IProductionOrderService>();
        var r = orders.CloseOrder(oid); // لم يُنتج شيء بعد
        Assert.False(r.Ok);
        Assert.Contains("ناقص", r.Message);
    }

    [Fact]
    public void Cannot_Deliver_Customer_Lot_To_Another_Customer()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();

        // عميل ثانٍ
        db.Customers.Add(new Customer { CustomerCode = "C002", CustomerName = "عميل آخر" });
        db.SaveChanges();
        var otherCust = db.Customers.Single(c => c.CustomerCode == "C002").Id;

        FullWorkflowTests.SeedQuickOrder(host, db, out _, out var lotId); // الدفعة تخص العميل 1

        var cd = host.Get<ICustomerDeliveryService>();
        var r = cd.Save(otherCust, "2026-08-22", null, new List<CustomerDeliveryItemDto>
        {
            new() { ProductId = 3, LotId = lotId, QtyKg = 100 }
        });
        Assert.False(r.Ok);
        Assert.Contains("عميل آخر", r.Message);
    }

    [Fact]
    public void Cannot_Deliver_Without_A_Line_Level_Quality_Release()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrder(host, db, out var oid, out var lotId);

        // رأس قديم بلا بنود جودة لا يمنح إفراجاً حتى لو وُضع فيه IsApproved=true.
        db.QualityChecks.Add(new Core.Domain.Entities.QualityCheck
        { DocumentNumber = "QC-GUARD", OrderId = oid, IsApproved = true, Status = "Approved", TotalCheckedKg = 1 });
        db.SaveChanges();

        var cd = host.Get<ICustomerDeliveryService>();
        var s = cd.Save(1, "2026-08-22", oid, new List<CustomerDeliveryItemDto>
        {
            new() { ProductId = 3, LotId = lotId, QtyKg = 99999 }
        });
        Assert.True(s.Ok, s.Message);
        var r = cd.Approve(s.Id);
        Assert.False(r.Ok);
        Assert.Contains("فحص", r.Message);
    }

    [Fact]
    public void Double_Approval_Of_Same_Order_Is_Rejected_And_Materials_Issued_Once()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrder(host, db, out var oid, out _);

        var orders = host.Get<IProductionOrderService>();
        var r2 = orders.ApproveOrder(oid); // اعتماد ثانٍ لنفس الأمر
        Assert.False(r2.Ok);
        Assert.Contains("معتمد مسبقاً", r2.Message);

        // حركة صرف المواد عند الاعتماد وُجدت مرة واحدة فقط لكل مادة (§8 منع تكرار الصرف)
        var orderNo = db.ProductionOrders.Single(o => o.Id == oid).DocumentNumber;
        var issueCount = db.InventoryTransactions.Count(t =>
            t.ReferenceDocType == Core.Domain.Enums.ReferenceDocType.MaterialIssue
            && t.ReferenceDocNumber == orderNo);
        Assert.Equal(db.ProductionOrderMaterials.Count(m => m.OrderId == oid && m.CalculatedQty > 0), issueCount);
    }

    [Fact]
    public void Double_Delivery_Receipt_Is_Rejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrder(host, db, out var oid, out var lotId);

        // تنفيذ + جودة معتمدة
        host.Get<IProductionOrderService>().StartOrder(oid);
        var exec = host.Get<IExecutionService>();
        var ec = exec.CloseProductionDay(oid, 500, 66, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        Assert.True(ec.Ok, ec.Message);
        int execId = db.ProductionExecutions.Single(x => x.OrderId == oid).Id;
        var quality = host.Get<IQualityService>();
        var q = quality.SaveCheck(oid, execId, null, "نهائي", new List<QualityItemDto> { new() { ProductId = 3, LotId = lotId, AcceptedQtyKg = 500 } });
        quality.ApproveCheck(q.Id);

        var fg = host.Get<IFinishedGoodsService>();
        var f = TestProductionDocumentFlow.SaveReceiptFromActual(host, oid, q.Id, null, new List<FinishedGoodsItemDto> { new() { ProductId = 3, LotId = lotId, PackageCount = 0, NetWeightKg = 500 } });
        Assert.True(f.Ok, f.Message);
        Assert.True(fg.Issue(f.Id).Ok);
        // null is the explicitly requested 'receive all'; an empty map means receive nothing.
        Assert.True(fg.Receive(f.Id, null).Ok);
        var again = fg.Receive(f.Id, null);
        Assert.False(again.Ok);
        Assert.Contains("بالكامل مسبقاً", again.Message);
    }

    [Fact]
    public void Plan_Rejects_Exceeding_Lot_Available_For_All_Items()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();

        var rec = host.Get<IReceivingService>();
        var s = rec.SaveShipment(1, null, null, new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 50, UnitWeightKg = 20, QtyKg = 1000 } });
        rec.ApproveShipment(s.Id);
        var lotId = db.Lots.OrderBy(l => l.Id).Last().Id;

        var planning = host.Get<IPlanningService>();
        // بندان من نفس الدفعة مجموعهما أكبر من الرصيد → رفض
        var r = planning.SavePlan("خطة متجاوزة", "Daily", "2026-08-25", "2026-08-25", 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lotId, ProductId = 3, PlannedQtyKg = 600, PlannedCartons = 0, ScheduledDate = "2026-08-25", SuggestedShiftId = null, PriorityNo = 1 },
            new() { SourceType = "FromReceiving", LotId = lotId, ProductId = 4, PlannedQtyKg = 600, PlannedCartons = 0, ScheduledDate = "2026-08-25", SuggestedShiftId = null, PriorityNo = 2 }
        });
        Assert.False(r.Ok);
    }

    [Fact]
    public void Plan_Rejects_Exceeding_Shift_Capacity()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();

        var planning = host.Get<IPlanningService>();
        // الصنف 3 في الوردية 1: 500 كرتون/س × 8 س = 4000 كرتون كحد أقصى
        var r = planning.SavePlan("خطة متجاوزة للطاقة", "Daily", "2026-09-01", "2026-09-01", 1, 1, new List<PlanItemDto>
        {
            new() { ProductId = 3, PlannedQtyKg = 37500, PlannedCartons = 5000, ScheduledDate = "2026-09-01", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 }
        });
        Assert.False(r.Ok);
        Assert.Contains("الطاقة الإنتاجية", r.Message);
    }

    [Fact]
    public void Permission_Denied_For_Unauthorized_Role()
    {
        using var host = new TestHost();
        // دخول كمسؤول جودة — لا يملك صلاحية إنشاء خطط
        var session = host.Get<Infrastructure.Session.SessionContext>();
        var auth = host.Get<IAuthService>();
        var login = auth.Login("quality", DbSeeder.InitialAdminPassword);
        Assert.True(login.Success, login.Message);

        var planning = host.Get<IPlanningService>();
        var ex = Assert.ThrowsAny<Core.Exceptions.PermissionDeniedException>(
            () => planning.SavePlan("خ", "Daily", null, null, null, null,
                new List<PlanItemDto> { new() { ProductId = 3, PlannedQtyKg = 1 } }));
        Assert.Contains("صلاحية", ex.Message);
    }
}
