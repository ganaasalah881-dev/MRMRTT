using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// اختبار دورة العمل الكاملة متعددة العملاء (§7/§8/§9):
/// خطة لعميلين ← أمر لكل عميل ← اعتماد يخصم الخام ← تنفيذ ← جودة
/// ← تسليم إنتاج (إصدار بلا أثر ← سند استلام يؤثر) ← تسليم عميل ← التأثير على المخازن.
/// </summary>
public class FullCycleMultiCustomerTests
{
    private static T Svc<T>(TestHost host) => (T)host.Services.CreateScope().ServiceProvider.GetService(typeof(T));

    /// <summary>سياق قراءة جديد — يضمن رؤية أحدث القيم بلا كاش الكيانات.</summary>
    private static DatesErpDbContext FreshDb(TestHost host)
        => new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);

    [Fact]
    public void Multi_Customer_Full_Cycle_With_Inventory_Impacts()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();

        // تجهيز رصيد المواد المساعدة
        var whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new Core.Domain.Entities.StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 50000 },
            new Core.Domain.Entities.StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 50000 });
        db.SaveChanges();

        // 1) عميلان
        var admin = Svc<MasterDataService>(host);
        var c2 = admin.SaveCustomer(null, "C002", "مصنع النخيل الحديث", "تجار جملة", "777111222", null, true);
        Assert.True(c2.Ok, c2.Message);
        int cust2 = c2.Id;
        int cust1 = db.Customers.First().Id;

        // 2) استلام شحنتين (شحنة لكل عميل) واعتمادهما
        var receiving = Svc<IReceivingService>(host);
        var s1 = receiving.SaveShipment(cust1, "2026-08-10", "2026-08-10",
            new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 300, UnitWeightKg = 20, QtyKg = 6000 } }, null, "CXLU-001");
        Assert.True(s1.Ok, s1.Message);
        Assert.True(receiving.ApproveShipment(s1.Id).Ok);

        var s2 = receiving.SaveShipment(cust2, "2026-08-10", "2026-08-10",
            new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 200, UnitWeightKg = 20, QtyKg = 4000 } }, null, "CXLU-002");
        Assert.True(s2.Ok, s2.Message);
        Assert.True(receiving.ApproveShipment(s2.Id).Ok);

        var lot1 = db.Lots.OrderBy(l => l.Id).First();
        var lot2 = db.Lots.OrderBy(l => l.Id).Last();
        Assert.Equal(cust1, lot1.CustomerId);
        Assert.Equal(cust2, lot2.CustomerId);

        // 3) خطة واحدة متعددة العملاء (بندان من دفعتي العميلين)
        var planning = Svc<IPlanningService>(host);
        var plan = planning.SavePlan("خطة عميلين", "Period", "2026-08-20", "2026-08-22", 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lot1.Id, CustomerId = cust1, ProductId = 3, PlannedQtyKg = 3000, PlannedCartons = 400, ScheduledDate = "2026-08-20", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 },
            new() { SourceType = "FromReceiving", LotId = lot2.Id, CustomerId = cust2, ProductId = 3, PlannedQtyKg = 2000, PlannedCartons = 267, ScheduledDate = "2026-08-21", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 2 }
        });
        Assert.True(plan.Ok, plan.Message);
        Assert.True(planning.ApprovePlan(plan.Id).Ok);

        // الحجز خفض متاح الدفعتين معاً (لكل الأصناف لا صنف واحد) — قراءة بسياق جديد
        using (var rd = FreshDb(host))
        {
            Assert.Equal(3000, rd.Lots.First(l => l.Id == lot1.Id).AvailableQtyKg, 1);
            Assert.Equal(2000, rd.Lots.First(l => l.Id == lot2.Id).AvailableQtyKg, 1);
        }

        // 4) أمر لكل عميل من نفس الخطة (كما تفعل شاشة أوامر الإنتاج)
        var orders = Svc<IProductionOrderService>(host);
        host.SetBusinessDate("2026-08-20"); // Arrange production on its scheduled business day, not the machine date.
        var o1 = orders.SaveOrder("FromPlan", plan.Id, cust1, "2026-08-20", 1, 1, TestOrderFixture.Items(host, plan.Id, cust1, "2026-08-20"));
        Assert.True(o1.Ok, o1.Message);
        host.SetBusinessDate("2026-08-21"); // Arrange production on its scheduled business day, not the machine date.
        var o2 = orders.SaveOrder("FromPlan", plan.Id, cust2, "2026-08-21", 1, 1, TestOrderFixture.Items(host, plan.Id, cust2, "2026-08-21"));
        Assert.True(o2.Ok, o2.Message);

        // 5) الاعتماد يخصم الخام من الدفعتين ويصرف المواد (ذرياً)
        double wrmBefore;
        using (var rd = FreshDb(host)) wrmBefore = rd.StockBalances.Where(b => b.WarehouseId == 1).Sum(b => b.QtyKg);
        host.SetBusinessDate("2026-08-20");
        Assert.True(orders.ApproveOrder(o1.Id).Ok);
        host.SetBusinessDate("2026-08-21");
        Assert.True(orders.ApproveOrder(o2.Id).Ok);
        using (var rd = FreshDb(host))
        {
            // §قاعدة توازن الإنتاج: لا يُخصم الخام عند اعتماد الأمر — يُصرف عند الإقفال
            // بالكمية المستهلكة فعلياً، لا بوزن المنتج المخطط (لا معادلة ثابتة تربطهما).
            double wrmAfter = rd.StockBalances.Where(b => b.WarehouseId == 1).Sum(b => b.QtyKg);
            Assert.Equal(wrmBefore, wrmAfter, 1);
            Assert.Equal(6000, rd.Lots.First(l => l.Id == lot1.Id).InStockQtyKg, 1);
            Assert.Equal(4000, rd.Lots.First(l => l.Id == lot2.Id).InStockQtyKg, 1);
        }

        // 6) تنفيذ الأمرين
        var exec = Svc<IExecutionService>(host);
        var orders2 = Svc<IProductionOrderService>(host);
        host.SetBusinessDate("2026-08-20");
        var st1 = orders2.StartOrder(o1.Id); Assert.True(st1.Ok, st1.Message);
        var closeA = exec.CloseProductionDay(o1.Id, 3000, 400, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        Assert.True(closeA.Ok, closeA.Message);
        host.SetBusinessDate("2026-08-21");
        var st2 = orders2.StartOrder(o2.Id); Assert.True(st2.Ok, st2.Message);
        var closeB = exec.CloseProductionDay(o2.Id, 2000, 266, 0, 0, 0, false, new List<DowntimeDto>(), false, null);
        Assert.True(closeB.Ok, closeB.Message);
        int e1Id = Svc<DatesErpDbContext>(host).ProductionExecutions.Single(x => x.OrderId == o1.Id).Id;
        int e2Id = Svc<DatesErpDbContext>(host).ProductionExecutions.Single(x => x.OrderId == o2.Id).Id;

        // 7) جودة معتمدة لكل أمر
        var quality = Svc<IQualityService>(host);
        var q1 = quality.SaveCheck(o1.Id, e1Id, "2026-08-20", "نهائي",
            new List<QualityItemDto> { new() { ProductId = 3, LotId = lot1.Id, AcceptedQtyKg = 3000, RejectedQtyKg = 0 } },
            new List<(int, double)> { (5, 15.0) }); // حشف بالكيلو
        Assert.True(q1.Ok, q1.Message);
        Assert.True(quality.ApproveCheck(q1.Id).Ok);
        var q2 = quality.SaveCheck(o2.Id, e2Id, "2026-08-21", "نهائي",
            new List<QualityItemDto> { new() { ProductId = 3, LotId = lot2.Id, AcceptedQtyKg = 2000, RejectedQtyKg = 0 } });
        Assert.True(q2.Ok, q2.Message);
        Assert.True(quality.ApproveCheck(q2.Id).Ok);

        // 8) تسليم إنتاج الأمر الأول: الإصدار لا يؤثر على الأرصدة
        var fg = Svc<IFinishedGoodsService>(host);
        var whFg = db.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
        var f1 = TestProductionDocumentFlow.SaveReceiptFromActual(host, o1.Id, q1.Id, "2026-08-20",
            new List<FinishedGoodsItemDto> { new() { ProductId = 3, LotId = lot1.Id, PackageCount = 400, NetWeightKg = 3000 } });
        Assert.True(f1.Ok, f1.Message);
        double wfgBefore;
        using (var rd = FreshDb(host)) wfgBefore = rd.StockBalances.Where(b => b.WarehouseId == whFg).Sum(b => b.QtyKg);
        Assert.True(fg.Issue(f1.Id).Ok);
        using (var rd = FreshDb(host)) Assert.Equal(wfgBefore, rd.StockBalances.Where(b => b.WarehouseId == whFg).Sum(b => b.QtyKg), 1); // الإصدار بلا أثر

        // استلام جزئي (1500) ثم سند متابعة للمتبقي → الرصيد يُنسب لعميل الأمر
        int itemId;
        using (var rd = FreshDb(host)) itemId = rd.FinishedGoodsReceiptItems.Single(i => i.ReceiptId == f1.Id).Id;
        var r1 = fg.Receive(f1.Id, new Dictionary<int, double> { [itemId] = 1500 });
        Assert.True(r1.Ok, r1.Message);
        using (var rd = FreshDb(host))
        {
            var bal1 = rd.StockBalances.Single(b => b.WarehouseId == whFg && b.CustomerId == cust1);
            Assert.Equal(1500, bal1.QtyKg, 1);
        }
        var r1b = fg.Receive(f1.Id, null);
        Assert.True(r1b.Ok, r1b.Message);
        using (var rd = FreshDb(host))
            Assert.Equal(3000, rd.StockBalances.Single(b => b.WarehouseId == whFg && b.CustomerId == cust1).QtyKg, 1);

        // تسليم إنتاج الأمر الثاني كاملاً
        var f2 = TestProductionDocumentFlow.SaveReceiptFromActual(host, o2.Id, q2.Id, "2026-08-21",
            new List<FinishedGoodsItemDto> { new() { ProductId = 3, LotId = lot2.Id, PackageCount = 0, NetWeightKg = 2000 } });
        Assert.True(f2.Ok, f2.Message);
        Assert.True(fg.Issue(f2.Id).Ok);
        Assert.True(fg.Receive(f2.Id, null).Ok);
        using (var rd = FreshDb(host))
            Assert.Equal(2000, rd.StockBalances.Single(b => b.WarehouseId == whFg && b.CustomerId == cust2).QtyKg, 1);

        // 9) تسليم العملاء: خصم من رصيد العميل فقط
        var cd = Svc<ICustomerDeliveryService>(host);
        var d1 = cd.Save(cust1, "2026-08-22", o1.Id,
            new List<CustomerDeliveryItemDto> { new() { ProductId = 3, LotId = lot1.Id, QtyKg = 1200, PackageCount = 167 } });
        Assert.True(d1.Ok, d1.Message);
        Assert.True(cd.Approve(d1.Id).Ok);
        using (var rd = FreshDb(host))
        {
            Assert.Equal(1800, rd.StockBalances.Single(b => b.WarehouseId == whFg && b.CustomerId == cust1).QtyKg, 1);
            Assert.Equal(2000, rd.StockBalances.Single(b => b.WarehouseId == whFg && b.CustomerId == cust2).QtyKg, 1); // رصيد الآخر لم يتأثر
        }

        // محاولة تسليم دفعة العميل الثاني إلى العميل الأول → مرفوضة
        var bad = cd.Save(cust1, "2026-08-22", null,
            new List<CustomerDeliveryItemDto> { new() { ProductId = 3, LotId = lot2.Id, QtyKg = 500 } });
        Assert.False(bad.Ok);
        Assert.Contains("عميل آخر", bad.Message);

        // محاولة تسليم أكبر من رصيد العميل → مرفوضة
        var over = cd.Save(cust1, "2026-08-22", null,
            new List<CustomerDeliveryItemDto> { new() { ProductId = 3, LotId = lot1.Id, QtyKg = 99999 } });
        Assert.True(over.Ok);
        var overAp = cd.Approve(over.Id);
        Assert.False(overAp.Ok);
        Assert.Contains("رصيد العميل", overAp.Message);

        // 10) إلغاء تسليم العميل الأول يعيد الكميات لرصيده
        host.LoginAsAdmin(); // cancellation is a supervisor permission
        Assert.True(cd.Unapprove(d1.Id).Ok);
        using (var rd = FreshDb(host))
            Assert.Equal(3000, rd.StockBalances.Single(b => b.WarehouseId == whFg && b.CustomerId == cust1).QtyKg, 1);

        // 11) كل الحركات مرتبطة بمستندات (§9) والتدقيق سجل العمليات (§26)
        using (var rd = FreshDb(host))
        {
            Assert.DoesNotContain(rd.InventoryTransactions.ToList(), t => string.IsNullOrEmpty(t.ReferenceDocNumber));
            Assert.True(rd.AuditLogs.Count(a => a.ActionType == "Approve") >= 4);
        }
    }
}
