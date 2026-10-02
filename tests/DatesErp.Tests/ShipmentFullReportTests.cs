using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §تقرير الشحنة الشامل — كشف حساب تمور العميل الذي يطلبه كل العملاء:
/// متى دخلت وكم دخلت (صنف × عبوة)، كم أمر إنتاج عُمِل لها ومخرجات كل أمر،
/// المتبقي بعد كل أمر، ماذا خرج تاماً، المخرجات الثانوية، المتبقي،
/// والمقارنة النهائية: كم دخلت مقابل كم خرجت.
/// </summary>
public class ShipmentFullReportTests
{
    private static ReportResult BuildScenarioAndRun(TestHost host, Dictionary<string, string> p)
    {
        using var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var receiving = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
        var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();
        var quality = scope.ServiceProvider.GetRequiredService<IQualityService>();
        var fg = scope.ServiceProvider.GetRequiredService<IFinishedGoodsService>();
        var dlv = scope.ServiceProvider.GetRequiredService<ICustomerDeliveryService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var c = master.SaveCustomer(null, "TSH", "شركة الشحنة", "جملة", "777", "-", true);
        var rs = master.SaveProductFull(null, "SH-R1", "سكري", "001", "Raw", "كجم", 20, 0, 0, null);
        var rk = master.SaveProductFull(null, "SH-R2", "خلاص", "001", "Raw", "كجم", 20, 0, 0, null);
        var fs = master.SaveProductFull(null, "SH-F1", "سكري تام", "002", "Finished", "كرتون", 7.5, 1, 0.5, null, sourceProductId: rs.Id);
        var fk = master.SaveProductFull(null, "SH-F2", "خلاص تام", "002", "Finished", "كرتون", 7.5, 1, 0.5, null, sourceProductId: rk.Id);
        Assert.True(c.Ok && rs.Ok && rk.Ok && fs.Ok && fk.Ok);

        // الدخول: 10,000 سكري + 8,000 خلاص
        var s = receiving.SaveShipment(c.Id, "10/08/2026", "12/08/2026", new List<ShipmentItemDto>
        {
            new() { TreatmentRequired = false, ProductId = rs.Id, QtyKg = 10000, PackageCount = 500, UnitWeightKg = 20, ReceiptUnit = "سلة" },
            new() { TreatmentRequired = false, ProductId = rk.Id, QtyKg = 8000, PackageCount = 400, UnitWeightKg = 20, ReceiptUnit = "كرتون" }
        }, containerNumber: "CXLU-100");
        Assert.True(s.Ok && receiving.ApproveShipment(s.Id).Ok);
        int lotSuk = db.Lots.Single(l => l.ShipmentId == s.Id && l.ProductId == rs.Id).Id;
        int lotKha = db.Lots.Single(l => l.ShipmentId == s.Id && l.ProductId == rk.Id).Id;

        // الخطة: 7,500 سكري + 6,000 خلاص
        string day = "15/08/2026";
        TestCapacityFixture.DefineMissingRates(host);
        var plan = planning.SavePlan("خطة الشحنة", "Daily", day, day, 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lotSuk, CustomerId = c.Id, ProductId = fs.Id, PlannedQtyKg = 7500, PriorityNo = 1 },
            new() { SourceType = "FromReceiving", LotId = lotKha, CustomerId = c.Id, ProductId = fk.Id, PlannedQtyKg = 6000, PriorityNo = 2 }
        });
        Assert.True(plan.Ok, plan.Message);
        Assert.True(planning.ApprovePlan(plan.Id).Ok);
        var itemIds = db.ProductionPlanItems.Where(i => i.PlanId == plan.Id).OrderBy(i => i.PriorityNo).Select(i => i.Id).ToList();

        // الأمر واعتباره (صرف الخام)
        host.SetBusinessDate(day); // Arrange production on its scheduled business day, not the machine date.
        var order = orders.SaveOrder("FromPlan", plan.Id, c.Id, day, 1, 1, TestOrderFixture.Items(host, plan.Id, c.Id, day));
        Assert.True(order.Ok, order.Message);
        Assert.True(orders.ApproveOrder(order.Id).Ok);

        // الإقفال: المنتَج 12,900 + ثانوية 600 = 13,500 المصروف
        var close = exec.CloseProductionDay(order.Id, 12900, 1720, 350, 250, 0, false, new List<DowntimeDto>(), sendToQuality: true);
        Assert.True(close.Ok, close.Message);
        int execId = db.ProductionExecutions.Single(e => e.OrderId == order.Id).Id;

        // الفحص والاعتماد
        var qc = quality.SaveCheck(order.Id, execId, "17/08/2026", "نهائي", new List<QualityItemDto>
        {
            new() { ProductId = fs.Id, AcceptedQtyKg = 7300, RejectedQtyKg = 200 },
            new() { ProductId = fk.Id, AcceptedQtyKg = 5300, RejectedQtyKg = 100 }
        });
        Assert.True(qc.Ok, qc.Message);
        Assert.True(quality.ApproveCheck(qc.Id).Ok);

        // خروج التام إلى المخزن
        var rcpt = TestProductionDocumentFlow.SaveReceiptFromActual(host, order.Id, qc.Id, "17/08/2026", new List<FinishedGoodsItemDto>
        {
            new() { ProductId = fs.Id, LotId = lotSuk, NetWeightKg = 7300, PackageCount = 973 },
            new() { ProductId = fk.Id, LotId = lotKha, NetWeightKg = 5300, PackageCount = 707 }
        });
        Assert.True(rcpt.Ok, rcpt.Message);
        Assert.True(fg.Issue(rcpt.Id).Ok);
        Assert.True(fg.Receive(rcpt.Id, null).Ok);

        // التسليم للعميل
        var d = dlv.Save(c.Id, "18/08/2026", order.Id, new List<CustomerDeliveryItemDto>
        {
            new() { ProductId = fs.Id, LotId = lotSuk, QtyKg = 1000 },
            new() { ProductId = fk.Id, LotId = lotKha, QtyKg = 500 }
        });
        Assert.True(d.Ok, d.Message);
        Assert.True(dlv.Approve(d.Id).Ok);

        var svc = scope.ServiceProvider.GetRequiredService<IReportService>();
        var r = svc.Run("shipment_full", p);
        Assert.NotNull(r);
        return r;
    }

    [Fact]
    public void Shipment_Full_Statement_Shows_Complete_Story()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var r = BuildScenarioAndRun(host, new Dictionary<string, string>());

        // الدخول: الصنفان بكميتيهما مع العبوة الأصلية
        // §وحدة الاستلام الأصلية محفوظة كما وردت — لا تُفرض في الكود
        Assert.Contains(r.Rows, row => row[0].ToString().Contains("الدخول") && row[7].ToString() == "10,000.0" && row[10 - 1].ToString().Contains("سلة"));
        Assert.Contains(r.Rows, row => row[0].ToString().Contains("الدخول") && row[7].ToString() == "8,000.0");

        // أوامر الإنتاج ومخرجاتها والمتبقي بعد الأمر
        Assert.Contains(r.Rows, row => row[0].ToString().Contains("أمر إنتاج"));
        Assert.Contains(r.Rows, row => row[0].ToString().Contains("مخرجات الأمر"));
        Assert.Contains(r.Rows, row => row[0].ToString().Contains("المتبقي بعد الأمر") && row[7].ToString() == "2,500.0");  // سكري: 10,000 − 7,500
        Assert.Contains(r.Rows, row => row[0].ToString().Contains("المتبقي بعد الأمر") && row[7].ToString() == "2,000.0");  // خلاص: 8,000 − 6,000

        // خرج تاماً + تسليم
        Assert.Contains(r.Rows, row => row[0].ToString().Contains("خرج تام") && row[4].ToString() == "سكري تام");
        Assert.Contains(r.Rows, row => row[0].ToString().Contains("خرج تام") && row[4].ToString() == "خلاص تام");
        Assert.Contains(r.Rows, row => row[0].ToString().Contains("تسليم"));

        // المتبقي الآن: خام + تام
        Assert.Contains(r.Rows, row => row[0].ToString().Contains("المتبقي الآن") && row[7].ToString() == "2,500.0");
        Assert.Contains(r.Rows, row => row[0].ToString().Contains("متبقي تام"));

        // المقارنة النهائية: كم دخلت مقابل كم خرجت
        Assert.Contains("18,000", r.Summary["كم دخلت (إجمالي الاستلام)"]);
        Assert.Contains("13,500", r.Summary["المصروف للإنتاج"]);
        Assert.Contains("12,900", r.Summary["المنتج التام"]);
        // §العنوان صار ديناميكياً — لا أسماء مخرجات مثبّتة في التقرير
        var byKey = r.Summary.Keys.FirstOrDefault(k => k.Contains("المخرجات الثانوية"));
        Assert.False(string.IsNullOrEmpty(byKey), "لا مفتاح مخرجات ثانوية: " + string.Join(" | ", r.Summary.Keys));
        Assert.Contains("600", r.Summary[byKey]);
        Assert.Contains("1,500", r.Summary["المسلَّم للعملاء"]);
        Assert.Contains("4,500", r.Summary["المتبقي خاماً في المخازن"]);
        Assert.Contains("11,100", r.Summary["المتبقي تاماً في المخازن"]);
        Assert.Contains("95.6", r.Summary["نسبة المردود الصناعي (منتج ÷ مصروف)"]);

        // المعادلة الحسابية ظاهرة في التقرير
        Assert.Contains("=", r.Summary["المعادلة"]);

        // روابط التنقل موجودة: ترويسة الشحنة تفتح الاستلام، وصف الأمر يفتح الأمر
        Assert.NotNull(r.RowLinks);
        Assert.Contains(r.RowLinks, l => l != null && l.DocType == "receiving");
        Assert.Contains(r.RowLinks, l => l != null && l.DocType == "orders");
        Assert.Contains(r.RowLinks, l => l != null && l.DocType == "finishedgoods");
        Assert.Contains(r.RowLinks, l => l != null && l.DocType == "delivery");
    }

    [Fact]
    public void Shipment_Full_Filter_By_Shipment_And_No_Orders_Yet()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var r = BuildScenarioAndRun(host, new Dictionary<string, string>());

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<IReportService>();

        // فلتر الشحنة المحددة يعمل
        int shipId = db.Shipments.Single().Id;
        var byShip = svc.Run("shipment_full", new Dictionary<string, string> { ["shipment"] = shipId.ToString() });
        Assert.NotNull(byShip);
        Assert.True(byShip.Rows.Count > 0);

        // شحنة بلا أوامر: تظهر عبارة «لم يُعمل لهذه الدفعة أي أمر إنتاج بعد»
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var receiving = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var rsId = db.Products.Single(p => p.ProductCode == "SH-R1").Id;
        var custId = db.Customers.Single(c => c.CustomerCode == "TSH").Id;
        var s2 = receiving.SaveShipment(custId, null, null, new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = rsId, QtyKg = 5000, PackageCount = 250, UnitWeightKg = 20 } });
        Assert.True(s2.Ok && receiving.ApproveShipment(s2.Id).Ok);
        var empty = svc.Run("shipment_full", new Dictionary<string, string> { ["shipment"] = s2.Id.ToString() });
        Assert.Contains(empty.Rows, row => row[9].ToString().Contains("لم يُعمل"));
        Assert.Contains("5,000", empty.Summary["كم دخلت (إجمالي الاستلام)"]);
    }
}
