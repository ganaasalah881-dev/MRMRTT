using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §الخطة الطويلة: خطة واحدة ← أيام متعددة ← وردية ← عميل ← صنف ← كمية.
/// خطة اليوم لمدير الإنتاج، استقلال حالة كل عميل، تعديل الأيام المستقبلية، الفوترة على المسلَّم.
/// </summary>
public class LongTermPlanTests
{
    private static T Svc<T>(TestHost host) => (T)host.Services.CreateScope().ServiceProvider.GetService(typeof(T));
    private static DatesErpDbContext FreshDb(TestHost host)
        => new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);

    private static (int cust2, int lot1, int lot2, int planId) SetupTwoCustomerPlan(TestHost host)
    {
        using (var db = FreshDb(host))
        {
            db.Customers.Add(new Core.Domain.Entities.Customer { CustomerCode = "C002", CustomerName = "مصنع النخيل", IsActive = true, RowVersion = Guid.NewGuid().ToByteArray() });
            // أرصدة افتتاحية للمواد المساعدة (كرتون/ملصقات) موثقة بحركات
            var whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
            foreach (var matId in new[] { 1, 2 })
            {
                db.StockBalances.Add(new Core.Domain.Entities.StockBalance { WarehouseId = whAux, MaterialId = matId, QtyKg = 200000, RowVersion = Guid.NewGuid().ToByteArray() });
                db.InventoryTransactions.Add(new Core.Domain.Entities.InventoryTransaction
                { TxnNumber = $"OPEN-LT-{matId}", TxnDate = DateTime.Now, WarehouseId = whAux, MaterialId = matId,
                  MovementType = Core.Domain.Enums.MovementType.Inbound, QtyKg = 200000,
                  ReferenceDocType = Core.Domain.Enums.ReferenceDocType.Adjustment, ReferenceDocNumber = "OPENING", IsApproved = true,
                  RowVersion = Guid.NewGuid().ToByteArray() });
            }
            db.SaveChanges();
        }
        int cust2;
        using (var db = FreshDb(host)) cust2 = db.Customers.Single(c => c.CustomerCode == "C002").Id;

        var receiving = Svc<IReceivingService>(host);
        var s1 = receiving.SaveShipment(1, null, null, new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 3000, UnitWeightKg = 20, QtyKg = 60000 } });
        Assert.True(receiving.ApproveShipment(s1.Id).Ok);
        var s2 = receiving.SaveShipment(cust2, null, null, new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 2000, UnitWeightKg = 20, QtyKg = 40000 } });
        Assert.True(receiving.ApproveShipment(s2.Id).Ok);

        int lot1, lot2;
        using (var db = FreshDb(host))
        {
            lot1 = db.Lots.OrderBy(l => l.Id).First().Id;   // للعميل 1
            lot2 = db.Lots.OrderBy(l => l.Id).Last().Id;    // للعميل 2
        }

        // خطة بعميلين: كل يوم عملاء وأصناف وكميات مختلفة لكل وردية
        // §B80: الفترة حتى 04/11 — تعديل البند المستقبلي (03 ← 04) يبقى داخل فترة الخطة المفروضة
        var planning = Svc<IPlanningService>(host);
        var plan = planning.SavePlan("خطة الأسبوعين", "Period", "2026-11-01", "2026-11-04", 1, 1, new List<PlanItemDto>
        {
            // اليوم 1: العميل 1 ← الصنف 3 ← 2000 | العميل 2 ← الصنف 3 ← 1500
            new() { SourceType = "FromReceiving", LotId = lot1, CustomerId = 1, ProductId = 3, PackagingTypeId = 1, PlannedQtyKg = 2000, PlannedCartons = 400, ScheduledDate = "2026-11-01", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 },
            new() { SourceType = "FromReceiving", LotId = lot2, CustomerId = cust2, ProductId = 3, PackagingTypeId = 1, PlannedQtyKg = 1500, PlannedCartons = 300, ScheduledDate = "2026-11-01", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 2 },
            // اليوم 2: العميل 1 ← الصنف 3 ← 1000
            new() { SourceType = "FromReceiving", LotId = lot1, CustomerId = 1, ProductId = 3, PackagingTypeId = 1, PlannedQtyKg = 1000, PlannedCartons = 200, ScheduledDate = "2026-11-02", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 },
            // اليوم 3: العميل 2 ← الصنف 3 ← 2000
            new() { SourceType = "FromReceiving", LotId = lot2, CustomerId = cust2, ProductId = 3, PackagingTypeId = 1, PlannedQtyKg = 2000, PlannedCartons = 400, ScheduledDate = "2026-11-03", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 }
        });
        Assert.True(plan.Ok, plan.Message);
        Assert.True(planning.ApprovePlan(plan.Id).Ok);
        return (cust2, lot1, lot2, plan.Id);
    }

    [Fact]
    public void Daily_Plan_Shows_Only_That_Day_With_Energy_Per_Row()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (cust2, lot1, lot2, planId) = SetupTwoCustomerPlan(host);

        var progress = Svc<IPlanProgressService>(host);

        // §5 — خطة يوم 01: بندان لعميلين مختلفين فقط
        var day1 = progress.GetDailyPlan("2026-11-01");
        Assert.Equal(2, day1.Count);
        Assert.Contains(day1, r => r.CustomerId == 1 && r.PlannedKg == 2000);
        Assert.Contains(day1, r => r.CustomerId == cust2 && r.PlannedKg == 1500);

        // §4 — لكل بند: المعدل والطاقة القصوى والساعات المطلوبة والمتبقية
        var row = day1.First(r => r.CustomerId == 1);
        Assert.True(row.RatePerHour > 0);
        Assert.True(row.MaxCapacity > 0);
        Assert.True(row.RequiredHours > 0);
        Assert.True(row.HoursRemainingOnDay >= 0);
        Assert.False(string.IsNullOrEmpty(row.ShipmentNo)); // المرجع الكامل: الشحنة
        Assert.False(string.IsNullOrEmpty(row.LotCode));    // والدفعة

        // يوم 02: بند واحد فقط للعميل 1
        var day2 = progress.GetDailyPlan("2026-11-02");
        Assert.Single(day2);
        Assert.Equal(1000, day2[0].PlannedKg, 1);

        // خطة اليوم داخل خطة محددة
        var day1OfPlan = progress.GetDailyPlan("2026-11-01", planId);
        Assert.Equal(2, day1OfPlan.Count);
    }

    [Fact]
    public void Customer_Status_Independent_And_Updates_Through_Production_And_Delivery()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (cust2, lot1, lot2, planId) = SetupTwoCustomerPlan(host);
        var progress = Svc<IPlanProgressService>(host);

        // قبل التنفيذ: لم يبدأ للعميلين
        var prog = progress.GetPlanProgressByCustomer(planId);
        Assert.Equal(2, prog.Count);
        Assert.All(prog, p => Assert.Equal("لم يبدأ ⏳", p.StatusAr));

        // تنفيذ بند اليوم 1 للعميل 1 فقط: أمر ← تنفيذ ← جودة ← تسليم
        var day1 = progress.GetDailyPlan("2026-11-01", planId);
        var rowA = day1.First(r => r.CustomerId == 1);

        var orders = Svc<IProductionOrderService>(host);
        host.SetBusinessDate("2026-11-01"); // Arrange production on its scheduled business day, not the machine date.
        var o = orders.SaveOrder("FromPlan", planId, 1, "2026-11-01", 1, 1, new List<OrderItemDto>
        { new() { PlanItemId = rowA.ItemId, LotId = rowA.ItemId > 0 ? lot1 : lot1, ShipmentId = null, CustomerId = 1, ProductId = 3, PackagingTypeId = 1, PlannedQtyKg = 2000, PlannedCartons = 400 } });
        Assert.True(o.Ok, o.Message);
        Assert.True(orders.ApproveOrder(o.Id).Ok);

        var exec = Svc<IExecutionService>(host);
        Svc<IProductionOrderService>(host).StartOrder(o.Id);
        var ec = exec.CloseProductionDay(o.Id, 2000, 400, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        Assert.True(ec.Ok, ec.Message);
        int execId = Svc<DatesErpDbContext>(host).ProductionExecutions.Single(x => x.OrderId == o.Id).Id;

        // المنتَج تزامن لبند الخطة: العميل 1 جزئي/قيد التنفيذ والعميل 2 ما زال لم يبدأ
        prog = progress.GetPlanProgressByCustomer(planId);
        var pA = prog.First(p => p.CustomerId == 1);
        var pB = prog.First(p => p.CustomerId == cust2);
        Assert.Equal(2000, pA.Produced, 1);
        Assert.NotEqual("لم يبدأ ⏳", pA.StatusAr);
        Assert.Equal("لم يبدأ ⏳", pB.StatusAr); // استقلال حالة العملاء

        // جودة معتمدة ← المقبول يتزامن
        var quality = Svc<IQualityService>(host);
        var q = quality.SaveCheck(o.Id, execId, "2026-11-01", "نهائي",
            new List<QualityItemDto> { new() { ProductId = 3, LotId = lot1, AcceptedQtyKg = 2000, RejectedQtyKg = 0 } });
        Assert.True(q.Ok, q.Message);
        Assert.True(quality.ApproveCheck(q.Id).Ok);
        prog = progress.GetPlanProgressByCustomer(planId);
        Assert.Equal(2000, prog.First(p => p.CustomerId == 1).Accepted, 1);

        // تسليم جزئي 1200 من 2000 ← المسلَّم يتزامن والحالة جزئي والمتبقي 800
        var fg = Svc<IFinishedGoodsService>(host);
        var f = TestProductionDocumentFlow.SaveReceiptFromActual(host, o.Id, q.Id, "2026-11-01", new List<FinishedGoodsItemDto> { new() { ProductId = 3, LotId = lot1, PackageCount = 0, NetWeightKg = 1200 } });
        Assert.True(f.Ok, f.Message);
        Assert.True(fg.Issue(f.Id).Ok);
        Assert.True(fg.Receive(f.Id, null).Ok);

        var cd = Svc<ICustomerDeliveryService>(host);
        var d = cd.Save(1, "2026-11-01", o.Id, new List<CustomerDeliveryItemDto> { new() { ProductId = 3, LotId = lot1, PackagingTypeId = 1, QtyKg = 1200, PackageCount = 240 } });
        Assert.True(d.Ok, d.Message);
        var approvedDelivery = cd.Approve(d.Id);
        Assert.True(approvedDelivery.Ok, approvedDelivery.Message);

        prog = progress.GetPlanProgressByCustomer(planId);
        pA = prog.First(p => p.CustomerId == 1);
        Assert.Equal(1200, pA.Delivered, 1);
        // مخطط العميل عبر كل الأيام = 2000 + 1000 = 3000، المسلَّم 1200 ← المتبقي 1800
        Assert.Equal(1800, pA.Remaining, 1);
        Assert.Equal("جزئي 🟠", pA.StatusAr); // اكتمال عميل لا يعني اكتمال الخطة

        // حالة أيام الخطة: يوم 01 جزئي، يوم 02/03 غير مكتمل
        var days = progress.GetPlanDayStatuses(planId);
        Assert.Equal("جزئي 🟠", days.First(x => x.Date == "01/11/2026").StatusAr);
        Assert.Equal("غير مكتمل ⏳", days.First(x => x.Date == "02/11/2026").StatusAr);
    }

    [Fact]
    public void Future_Days_Editable_With_Energy_Recheck_Executed_Locked_Billing_On_Delivered_Only()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (cust2, lot1, lot2, planId) = SetupTwoCustomerPlan(host);
        var progress = Svc<IPlanProgressService>(host);

        var day3 = progress.GetDailyPlan("2026-11-03", planId);
        var row = day3.Single();

        // §v1.50.35: الخطة المعتمدة مقفلة — فك الاعتماد (لا حركات لاحقة بعد) ثم التعديل
        var lockedApproved = progress.UpdatePlanItem(row.ItemId, newDate: "2026-11-04", newQtyKg: 1500);
        Assert.False(lockedApproved.Ok);
        Assert.Contains("معتمدة ومقفلة", lockedApproved.Message);
        Assert.True(Svc<IPlanningService>(host).UnapprovePlan(planId).Ok);
        progress = Svc<IPlanProgressService>(host);
        // §3 — تعديل يوم مستقبلي: نقل من 03 إلى 04 وتغيير الكمية
        var r = progress.UpdatePlanItem(row.ItemId, newDate: "2026-11-04", newQtyKg: 1500);
        Assert.True(r.Ok, r.Message);
        var moved = progress.GetDailyPlan("2026-11-04", planId);
        Assert.Single(moved);
        Assert.Equal(1500, moved[0].PlannedKg, 1);

        // تعديل بكمية تفوق طاقة اليوم الجديد → رفض مع رسالة الطاقة
        var over = progress.UpdatePlanItem(row.ItemId, newQtyKg: 500000);
        Assert.False(over.Ok);
        Assert.True(Svc<IPlanningService>(host).ApprovePlan(planId).Ok);

        // تنفيذ بند اليوم 1 للعميل 1 لجعله منفذاً
        var day1 = progress.GetDailyPlan("2026-11-01", planId);
        var rowA = day1.First(x => x.CustomerId == 1);
        var orders = Svc<IProductionOrderService>(host);
        host.SetBusinessDate("2026-11-01"); // Arrange production on its scheduled business day, not the machine date.
        var o = orders.SaveOrder("FromPlan", planId, 1, "2026-11-01", 1, 1, new List<OrderItemDto>
        { new() { PlanItemId = rowA.ItemId, LotId = lot1, CustomerId = 1, ProductId = 3, PackagingTypeId = 1, PlannedQtyKg = 2000, PlannedCartons = 400 } });
        Assert.True(o.Ok, o.Message);
        Assert.True(orders.ApproveOrder(o.Id).Ok);
        var exec = Svc<IExecutionService>(host);
        Svc<IProductionOrderService>(host).StartOrder(o.Id);
        var ec2 = exec.CloseProductionDay(o.Id, 2000, 400, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        Assert.True(ec2.Ok, ec2.Message);
        int execId = Svc<DatesErpDbContext>(host).ProductionExecutions.Single(x => x.OrderId == o.Id).Id;

        // (فحص قفل الأيام المنفذة يُنفذ آخر الاختبار بمستخدم بلا صلاحية)

        // §9 — الفوترة على المسلَّم فعلياً فقط ومنع التكرار
        var quality = Svc<IQualityService>(host);
        var q = quality.SaveCheck(o.Id, execId, "2026-11-01", "نهائي",
            new List<QualityItemDto> { new() { ProductId = 3, LotId = lot1, AcceptedQtyKg = 2000, RejectedQtyKg = 0 } });
        Assert.True(q.Ok); Assert.True(quality.ApproveCheck(q.Id).Ok);
        var fg = Svc<IFinishedGoodsService>(host);
        var f = TestProductionDocumentFlow.SaveReceiptFromActual(host, o.Id, q.Id, "2026-11-01", new List<FinishedGoodsItemDto> { new() { ProductId = 3, LotId = lot1, PackageCount = 0, NetWeightKg = 2000 } });
        Assert.True(f.Ok, f.Message);
        Assert.True(fg.Issue(f.Id).Ok);
        Assert.True(fg.Receive(f.Id, null).Ok);
        var cd = Svc<ICustomerDeliveryService>(host);
        var d = cd.Save(1, "2026-11-01", o.Id, new List<CustomerDeliveryItemDto> { new() { ProductId = 3, LotId = lot1, PackagingTypeId = 1, QtyKg = 2000, PackageCount = 400 } });
        Assert.True(d.Ok, d.Message);
        var approvedDelivery = cd.Approve(d.Id);
        Assert.True(approvedDelivery.Ok, approvedDelivery.Message);

        var billable = progress.GetBillableDeliveries(1);
        Assert.Single(billable);
        Assert.Equal(2000, billable[0].BillableQtyKg, 1);

        Assert.True(progress.MarkInvoiced(d.Id, 1200).Ok);
        billable = progress.GetBillableDeliveries(1);
        Assert.Equal(800, billable[0].BillableQtyKg, 1);

        // منع تكرار الفوترة فوق المتبقي
        var dup = progress.MarkInvoiced(d.Id, 900);
        Assert.False(dup.Ok);
        Assert.Contains("ممنوع تكرار الفوترة", dup.Message);
        Assert.True(progress.MarkInvoiced(d.Id, 800).Ok);
        billable = progress.GetBillableDeliveries(1);
        Assert.Equal(0, billable[0].BillableQtyKg, 1);

        // §3 — تعديل بند منفَّذ بمستخدم بلا صلاحية (الجودة) → مرفوض
        var auth = (IAuthService)host.Services.CreateScope().ServiceProvider.GetService(typeof(IAuthService));
        var lg = auth.Login("quality", DbSeeder.InitialAdminPassword);
        Assert.True(lg.Success, lg.Message);
        OpResult locked;
        try { locked = progress.UpdatePlanItem(rowA.ItemId, newDate: "2026-11-09"); }
        catch (DatesErp.Core.Exceptions.PermissionDeniedException px) { locked = OpResult.Fail(px.Message); }
        Assert.False(locked.Ok);
        Assert.Contains("صلاحية", locked.Message);
    }
}
