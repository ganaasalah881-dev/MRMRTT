using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §أمر فحص وحدة القياس الرسمية — القاعدة الذهبية:
/// الخام مرجعه الكجم مع حفظ وحدة الاستلام الأصلية ومعامل التحويل؛ ومن بعد الخطة
/// الكرتون هو الوحدة الرسمية للمنتج التام حتى التسليم والكجم بيان إضافي؛
/// والمخرجات الثانوية كجم في كل مراحلها. الاختبارات تبني عمليات حقيقية وتقارن
/// الوحدات المخزنة والترويسات المعروضة في التقارير بهذه القاعدة.
/// </summary>
public class UnitsGoldenRuleTests
{
    private static T Svc<T>(TestHost host) => (T)host.Services.CreateScope().ServiceProvider.GetService(typeof(T));
    private static DatesErpDbContext FreshDb(TestHost host)
        => new(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);

    // ── §1/§2/§13: استلام بأربع وحدات ← المرجع كجم واحد لا يتغير ──
    [Fact]
    public void Units_01_Receiving_FourUnits_OneKgReference()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        int cust = db.Customers.OrderBy(c => c.Id).First().Id;
        var rcv = Svc<IReceivingService>(host);
        // طن: 10 × 1000 = 10,000 كجم
        var ton = rcv.SaveShipment(cust, "2026-08-01", "2026-08-01", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 10, UnitWeightKg = 1000, QtyKg = 10000, ReceiptUnit = "طن" } }, null, "U-TON");
        // سلة: 100 × 20 = 2,000 كجم
        var basket = rcv.SaveShipment(cust, "2026-08-01", "2026-08-01", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 3, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000, ReceiptUnit = "سلة" } }, null, "U-BAS");
        // كرتون: 500 × 8 = 4,000 كجم
        var carton = rcv.SaveShipment(cust, "2026-08-01", "2026-08-01", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 500, UnitWeightKg = 8, QtyKg = 4000, ReceiptUnit = "كرتون" } }, null, "U-CTN");
        // كجم: 500 × 1
        var kgs = rcv.SaveShipment(cust, "2026-08-01", "2026-08-01", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 500, UnitWeightKg = 1, QtyKg = 500, ReceiptUnit = "كجم" } }, null, "U-KG");
        Assert.True(ton.Ok && basket.Ok && carton.Ok && kgs.Ok);
        foreach (var id in new[] { ton.Id, basket.Id, carton.Id, kgs.Id }) Assert.True(rcv.ApproveShipment(id).Ok);

        using var rd = FreshDb(host);
        // الأصل + المعامل + المكافئ محفوظان لكل بند (لا فقدان لوحدة الإدخال)
        var items = rd.Shipments.Include(x => x.Items).Where(x => x.CustomerId == cust).SelectMany(x => x.Items).ToList();
        Assert.Contains(items, i => i.ReceiptUnit == "طن" && i.UnitWeightKg == 1000 && i.PackageCount == 10 && i.TotalWeightKg == 10000);
        Assert.Contains(items, i => i.ReceiptUnit == "سلة" && i.UnitWeightKg == 20 && i.PackageCount == 100 && i.TotalWeightKg == 2000);
        Assert.Contains(items, i => i.ReceiptUnit == "كرتون" && i.UnitWeightKg == 8 && i.PackageCount == 500 && i.TotalWeightKg == 4000);
        Assert.Contains(items, i => i.ReceiptUnit == "كجم" && i.TotalWeightKg == 500);
        // الرصيد المرجعي بالخام = مجموع الكجم مهما اختلفت وحدات الاستلام
        Assert.Equal(16500, rd.StockBalances.Where(b => b.ProductId == 1).Sum(b => b.QtyKg), 1);
        Assert.Equal(16500, UnitsPolicy.KgOf(10, 1000) + UnitsPolicy.KgOf(100, 20) + UnitsPolicy.KgOf(500, 8) + UnitsPolicy.KgOf(500, 1), 1);
        // تقرير الاستلام يعرض وحدة الاستلام الأصلية بجانب الكجم
        var rep = Svc<IReportService>(host).Run("receiving_line_treatment", new Dictionary<string, string> { ["from"] = "2026-08-01", ["to"] = "2026-08-31" });
        Assert.Contains(rep.Columns, c => c == "الوحدة");
        Assert.Contains(rep.Rows, r => Equals(r[5], "طن"));
    }

    // ── §4/§6/§7/§9/§10/§11: من الإنتاج فصاعداً الكرتون رسمي والكجم إضافي ──
    [Fact]
    public void Units_02_FinishedChain_CartonOfficial_KgSupplementary()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        int cust = db.Customers.OrderBy(c => c.Id).First().Id;
        int whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new Core.Domain.Entities.StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 50000 },
            new Core.Domain.Entities.StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 50000 });
        db.SaveChanges();
        var rcv = Svc<IReceivingService>(host);
        var s = rcv.SaveShipment(cust, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 300, UnitWeightKg = 20, QtyKg = 6000, ReceiptUnit = "سلة" } }, null, "U-CH");
        Assert.True(rcv.ApproveShipment(s.Id).Ok);
        int lot = FreshDb(host).Lots.First().Id;

        // عبوة 8 كجم صريحة: وزن الصنف الافتراضي 7.5 ليس وزن هذه العبوة.
        var pack8 = new Core.Domain.Entities.PackagingType { PackageCode = "CT8-GOLD",
            PackageNameAr = "كرتون 8 كجم", UnitWeightKg = 8 };
        db.PackagingTypes.Add(pack8); db.SaveChanges();
        // خطة: خام 800 كجم ← منتج 100 كرتون × 8 كجم من تعريف العبوة.
        var planning = Svc<IPlanningService>(host);
        var p = planning.SavePlan("خطة وحدات", "Daily", "2026-08-20", "2026-08-20", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lot, CustomerId = cust, ProductId = 3, PackagingTypeId = pack8.Id, PlannedQtyKg = 800, PlannedCartons = 100, ScheduledDate = "2026-08-20", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 } });
        Assert.True(p.Ok, p.Message);
        Assert.True(planning.ApprovePlan(p.Id).Ok);
        var orders = Svc<IProductionOrderService>(host);
        host.SetBusinessDate("2026-08-20");
        var o = orders.SaveOrder("FromPlan", p.Id, cust, "2026-08-20", 1, 1, TestOrderFixture.Items(host, p.Id, cust, "2026-08-20"));
        Assert.True(orders.ApproveOrder(o.Id).Ok);
        // §5: مرجع الخطة ينتقل للأمر بلا فقدان هوية
        using (var rd = FreshDb(host))
            Assert.Equal(p.Id, rd.ProductionOrders.First(x => x.Id == o.Id).SourcePlanId);

        var exec = Svc<IExecutionService>(host);
        var close = exec.CloseProductionDay(o.Id, 800, 100, 0, 0, 0, false, new List<DowntimeDto>(), true);
        Assert.True(close.Ok, close.Message);
        var qcId = FreshDb(host).QualityChecks.Where(c => c.OrderId == o.Id).Select(c => c.Id).First();
        var exeId = FreshDb(host).ProductionExecutions.First(e => e.OrderId == o.Id).Id;

        // الجودة بالكرتون: 100 = 90 مقبول + 10 مرفوض
        var quality = Svc<IQualityService>(host);
        var chk = quality.SaveCheck(o.Id, exeId, "2026-08-20", "نهائي", new List<QualityItemDto>
        { new() { ProductId = 3, LotId = lot, PackagingTypeId = pack8.Id, CheckedQtyKg = 800, AcceptedQtyKg = 720, RejectedQtyKg = 80,
                CheckedCartons = 100, AcceptedCartons = 90, RejectedCartons = 10 } });
        Assert.True(chk.Ok, chk.Message);
        Assert.True(quality.ApproveCheck(qcId).Ok);

        // استلام التام: 90 كرتوناً (لا 720 كجم كوحدة أساسية)
        var fg = Svc<IFinishedGoodsService>(host);
        var fr = TestProductionDocumentFlow.SaveReceiptFromActual(host, o.Id, qcId, "2026-08-21", new List<FinishedGoodsItemDto>
        { new() { ProductId = 3, PackagingTypeId = pack8.Id, PackageCount = 90, NetWeightKg = 720 } });
        Assert.True(fg.Issue(fr.Id).Ok);
        Assert.True(fg.Receive(fr.Id, null).Ok);
        int whFg;
        using (var rd = FreshDb(host))
        {
            whFg = rd.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
            var bal = rd.StockBalances.First(b => b.CustomerId == cust && b.WarehouseId == whFg);
            Assert.Equal(90, bal.PackageCount);      // الرسمي: كرتون
            Assert.Equal(720, bal.QtyKg, 1);         // الإضافي: كجم
        }

        // تسليم 50 كرتوناً ← المتبقي 40 كرتوناً في التقارير بالرصيد الكرطوني أولاً
        var dlv = Svc<ICustomerDeliveryService>(host);
        var d = dlv.Save(cust, "2026-08-22", null, new List<CustomerDeliveryItemDto> { new() { ProductId = 3, PackagingTypeId = pack8.Id, PackageCount = 50, QtyKg = 400 } });
        Assert.True(dlv.Approve(d.Id).Ok);
        var rep = Svc<IReportService>(host);
        var fgr = rep.Run("finished_goods", new Dictionary<string, string>());
        Assert.Equal(UnitsPolicy.BalanceCtnHeader, fgr.Columns[3]);
        Assert.Equal(UnitsPolicy.BalanceKgHeader, fgr.Columns[4]);
        Assert.Equal(40, Convert.ToDouble(fgr.Rows.First(x => x[0] != null)[3]), 1);
        var dr = rep.Run("delivery", new Dictionary<string, string>());
        Assert.Equal(UnitsPolicy.FinishedQtyHeader, dr.Columns[3]);
        Assert.Equal(50, Convert.ToDouble(dr.Rows.First()[3]), 1);
        Assert.Equal(400, Convert.ToDouble(dr.Rows.First()[4]), 1);
        var qr = rep.Run("quality", new Dictionary<string, string>());
        Assert.Equal(100, Convert.ToDouble(qr.Rows.First()[3]), 1);   // الجودة بالكرتون رسمية
        Assert.Equal(90, Convert.ToDouble(qr.Rows.First()[4]), 1);
    }

    // ── §8/§12: المخرجات الثانوية كجم في كل المراحل — لا تُكرطن أبداً ──
    [Fact]
    public void Units_03_ByProducts_StayKg_Everywhere()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        int byId = db.Products.Where(b => b.ItemType == "ByProduct").OrderBy(b => b.Id).First().Id;
        var quality = Svc<IQualityService>(host);
        var chk = quality.SaveCheck(null, null, "2026-08-25", "نهائي",
            new List<QualityItemDto> { new() { ProductId = 3, CheckedQtyKg = 2700, AcceptedQtyKg = 2700, CheckedCartons = 360, AcceptedCartons = 360 } },
            new List<(int byProductId, double qtyKg)> { (byId, 200) });
        Assert.True(chk.Ok, chk.Message);
        Assert.True(quality.ApproveCheck(chk.Id).Ok);
        var w = Svc<IReportService>(host).Run("wastage", new Dictionary<string, string>());
        Assert.Contains("(كجم)", w.Columns[2]);                 // وحدة المخرجات الرسمية كجم
        Assert.DoesNotContain(w.Columns, c => c.Contains("كرتون"));
        Assert.Equal(200, Convert.ToDouble(w.Rows.First()[2]), 1);   // 200 كجم كما سُجلت
        Assert.Equal(200, UnitsPolicy.KgOf(1, 200), 1);
    }

    // ── §14/§15: تدقيق ترويسات كل التقارير ضد القاعدة الذهبية ──
    [Fact]
    public void Units_04_ReportHeaders_Follow_GoldenRule()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var rep = Svc<IReportService>(host);
        var raw = rep.Run("inventory", new Dictionary<string, string>());
        Assert.Contains(raw.Columns, c => c.Contains("(كجم)"));                    // الخام مرجعه كجم
        var lots = rep.Run("lots", new Dictionary<string, string>());
        Assert.Contains(lots.Columns, c => c == "المستلم (كجم)");
        var fg = rep.Run("finished_goods", new Dictionary<string, string>());
        Assert.Equal(UnitsPolicy.BalanceCtnHeader, fg.Columns[3]);                 // التام رسمي بالكرتون
        var dlv = rep.Run("delivery", new Dictionary<string, string>());
        Assert.Equal(UnitsPolicy.FinishedQtyHeader, dlv.Columns[3]);
        var cust = rep.Run("customers", new Dictionary<string, string>());
        Assert.Equal(UnitsPolicy.BalanceCtnHeader, cust.Columns[2]);
        Assert.Equal(UnitsPolicy.BalanceKgHeader, cust.Columns[3]);
        var prod = rep.Run("production", new Dictionary<string, string>());
        Assert.Equal(UnitsPolicy.FinishedQtyHeader, prod.Columns[4]);
        Assert.Equal(UnitsPolicy.FinishedWeightHeader, prod.Columns[5]);
        var mov = rep.Run("movements", new Dictionary<string, string>());
        Assert.Equal(UnitsPolicy.RawQtyHeader, mov.Columns[6]);
        Assert.Equal(UnitsPolicy.PackagesHeader, mov.Columns[7]);                  // كرتون التام ظاهر لا مدفون
        var w = rep.Run("wastage", new Dictionary<string, string>());
        Assert.DoesNotContain(w.Columns, c => c.Contains("كرتون"));                // الثانويات كجم فقط
    }

    // ── §16: معادلات التحويل من إعدادات المنتج الفعلية (8 و4 كجم/كرتون) ──
    [Fact]
    public void Units_05_CartonMath_From_Product_Settings_Not_Constants()
    {
        Assert.Equal(125, UnitsPolicy.CartonsOf(1000, 8), 3);   // 1000 كجم ← 125 كرتون 8 كجم
        Assert.Equal(250, UnitsPolicy.CartonsOf(1000, 4), 3);   // 1000 كجم ← 250 كرتون 4 كجم
        Assert.Equal(21600, UnitsPolicy.KgOfCartons(2700, 8), 1); // 2700 كرتون × 8 = 21,600 كجم
        Assert.Equal(0, UnitsPolicy.CartonsOf(1000, 0), 1);      // وزن عبوة غير معرف ← لا تحويل وهمي
        // والفعلية تحكم: الإنتاج الفعلي يُسجل كما حدث لا كتحويل آلي (هدر/فاقد وفق الكميات الفعلية)
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var prod = db.Products.First(p => p.Id == 3);
        Assert.True(prod.CartonWeightKg > 0);   // الوزن من بطاقة الصنف لا من ثابت في الكود
        Assert.Equal(UnitsPolicy.CartonsOf(800, prod.CartonWeightKg), 800 / prod.CartonWeightKg, 3);
    }
}
