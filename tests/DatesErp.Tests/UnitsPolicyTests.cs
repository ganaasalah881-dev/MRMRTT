using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §نظام الوحدات والمجموعات — المعيار الرسمي المفروض مركزياً (قاعدة البيانات + Backend + API + الواجهات):
/// 001 مواد خام (الاستلام فقط، وحدة مرنة والقياسية كجم) | 002 منتجات تامة (الإنتاج والتسليم، كرتونة + وزن مكافئ)
/// | 003 مخرجات ثانوية (كجم دائماً). ومنع تغيير تعريف العبوة بأثر رجعي.
/// </summary>
public class UnitsPolicyTests
{
    private sealed record Setup(int Customer, int RawSuk, int RawKha, int FinSuk, int FinKha, int ByProd, int LotSuk, int LotKha);

    private static Setup Seed(TestHost host)
    {
        using var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var receiving = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var c = master.SaveCustomer(null, "TU", "شركة الوحدات", "جملة", "777", "-", true);
        Assert.True(c.Ok, c.Message);
        var rs = master.SaveProductFull(null, "U-R1", "سكري خام", "001", "Raw", "كجم", 20, 0, 0, null);
        var rk = master.SaveProductFull(null, "U-R2", "خلاص خام", "001", "Raw", "كجم", 20, 0, 0, null);
        Assert.True(rs.Ok, rs.Message); Assert.True(rk.Ok, rk.Message);
        var fs = master.SaveProductFull(null, "U-F1", "سكري تام", "002", "Finished", "كرتون", 7.5, 1, 0.5, null, sourceProductId: rs.Id);
        var fk = master.SaveProductFull(null, "U-F2", "خلاص تام", "002", "Finished", "كرتون", 7.5, 1, 0.5, null, sourceProductId: rk.Id);
        Assert.True(fs.Ok, fs.Message); Assert.True(fk.Ok, fk.Message);
        var bp = master.SaveProductFull(null, "U-B1", "مخلفات فرز", "003", "ByProduct", "كجم", 0, 0, 0, null);
        Assert.True(bp.Ok, bp.Message);

        var s = receiving.SaveShipment(c.Id, null, null, new List<ShipmentItemDto>
        {
            new() { TreatmentRequired = false, ProductId = rs.Id, QtyKg = 10000, PackageCount = 500, UnitWeightKg = 20, ReceiptUnit = "سلة" },
            new() { TreatmentRequired = false, ProductId = rk.Id, QtyKg = 8000, PackageCount = 400, UnitWeightKg = 20, ReceiptUnit = "كرتون" }
        });
        Assert.True(s.Ok, s.Message);
        Assert.True(receiving.ApproveShipment(s.Id).Ok);
        var lotSuk = db.Lots.Single(l => l.ShipmentId == s.Id && l.ProductId == rs.Id).Id;
        var lotKha = db.Lots.Single(l => l.ShipmentId == s.Id && l.ProductId == rk.Id).Id;
        return new Setup(c.Id, rs.Id, rk.Id, fs.Id, fk.Id, bp.Id, lotSuk, lotKha);
    }

    // ══════════ 1) الاستلام: 001 فقط — رفض 002 و003 ══════════
    [Fact]
    public void Receiving_Accepts_Raw_With_Any_Packaging_Unit()
    {
        // §القاعدة المعتمدة: الخام قد يصل بأي عبوة — سلة/كيس/كرتون — فتُسجَّل
        // وحدة الاستلام الأصلية، ومجموعة الصنف هي التي تحدد أنه خام لا اسم الوحدة.
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var rcv = scope.ServiceProvider.GetRequiredService<IReceivingService>();

        int raw = db.Products.AsNoTracking().First(p => p.ItemType == "Raw").Id;
        int cust = db.Customers.AsNoTracking().Select(c => c.Id).First();
        var r = rcv.SaveShipment(cust, null, null, new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = raw, QtyKg = 9200, PackageCount = 460, UnitWeightKg = 20, ReceiptUnit = "كرتون" } });
        Assert.True(r.Ok, r.Message);
        Assert.Equal("كرتون", db.ShipmentItems.AsNoTracking().OrderByDescending(x => x.Id).First().ReceiptUnit);

        // والصنف يبقى خاماً (001) رغم أن عبوته كرتون
        Assert.Equal("Raw", db.Products.Single(p => p.Id == raw).ItemType);

        // وغير الخام ما زال مرفوضاً في الاستلام
        int fin = db.Products.AsNoTracking().First(p => p.ItemType == "Finished").Id;
        var bad = rcv.SaveShipment(cust, null, null, new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = fin, QtyKg = 100, PackageCount = 10, UnitWeightKg = 10 } });
        Assert.False(bad.Ok);
    }

    // ══════════ 2) الخطة والأمر: 002 فقط — رفض 001 و003 ══════════
    [Fact]
    public void Planning_And_Orders_Accept_Only_Finished_002()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var s = Seed(host);
        using var scope = host.Services.CreateScope();
        var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();

        // محاولة تخطيط صنف خام (001) كمنتج ← مرفوضة
        var raw = planning.SavePlan("خطة خاطئة", "Daily", "2026-09-01", "2026-09-01", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = s.LotSuk, CustomerId = s.Customer, ProductId = s.RawSuk, PlannedQtyKg = 1000, PriorityNo = 1 } });
        Assert.False(raw.Ok);
        Assert.Contains("002", raw.Message);

        // محاولة تخطيط مخرج ثانوي (003) ← مرفوضة
        var by = planning.SavePlan("خطة خاطئة 2", "Daily", "2026-09-01", "2026-09-01", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = s.LotSuk, CustomerId = s.Customer, ProductId = s.ByProd, PlannedQtyKg = 100, PriorityNo = 1 } });
        Assert.False(by.Ok);
        Assert.Contains("002", by.Message);

        // محاولة أمر إنتاج بصنف خام ← مرفوضة
        var rawOrder = orders.SaveOrder("Manual", null, s.Customer, "2026-09-01", 1, 1, new List<OrderItemDto>
        { new() { LotId = s.LotSuk, CustomerId = s.Customer, ProductId = s.RawSuk, PlannedQtyKg = 1000 } });
        Assert.False(rawOrder.Ok);
        Assert.Contains("لا ينشأ يدوياً", rawOrder.Message);
    }

    // ══════════ 3) التسليم واستلام التام: 002 فقط ══════════
    [Fact]
    public void Delivery_And_FinishedReceipt_Accept_Only_Finished_002()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var s = Seed(host);
        using var scope = host.Services.CreateScope();
        var delivery = scope.ServiceProvider.GetRequiredService<ICustomerDeliveryService>();

        // محاولة تسليم صنف خام للعميل ← مرفوضة
        var rawDlv = delivery.Save(s.Customer, "2026-09-01", null, new List<CustomerDeliveryItemDto>
        { new() { ProductId = s.RawSuk, LotId = s.LotSuk, QtyKg = 500 } });
        Assert.False(rawDlv.Ok);
        Assert.Contains("002", rawDlv.Message);

        // محاولة تسليم مخرج ثانوي للعميل ← مرفوضة
        var byDlv = delivery.Save(s.Customer, "2026-09-01", null, new List<CustomerDeliveryItemDto>
        { new() { ProductId = s.ByProd, QtyKg = 100 } });
        Assert.False(byDlv.Ok);
        Assert.Contains("002", byDlv.Message);
    }

    // ══════════ 4) الإنتاج الأساسي كرتونة والوزن المكافئ من وزن الكرتون (§القاعدة 4/5) ══════════
    [Fact]
    public void Finished_Production_Carton_Is_Base_And_Kg_Is_Equivalent()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var s = Seed(host);
        using var scope = host.Services.CreateScope();
        var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        host.SetBusinessDate("2026-09-01");
        TestCapacityFixture.DefineMissingRates(host);
        var planSvc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var plan = planSvc.SavePlan("اختبار وحدة الكرتون", "Daily", "2026-09-01", "2026-09-01", 1, 1,
            new List<PlanItemDto> { new() { SourceType = "FromReceiving", LotId = s.LotSuk, CustomerId = s.Customer,
                ProductId = s.FinSuk, PlannedCartons = 100, PlannedQtyKg = 0 } });
        Assert.True(plan.Ok, plan.Message); Assert.True(planSvc.ApprovePlan(plan.Id).Ok);
        Assert.Equal(750, db.ProductionPlanItems.Single(i => i.PlanId == plan.Id).PlannedQtyKg, 1);
        var mismatchItems = TestOrderFixture.Items(host, plan.Id, s.Customer, "2026-09-01");
        mismatchItems[0].PlannedQtyKg = 500;
        var mismatch = orders.SaveOrder("FromPlan", plan.Id, s.Customer, "2026-09-01", 1, 1, mismatchItems);
        Assert.False(mismatch.Ok);
        Assert.Contains("تطابق الخطة المعتمدة تماماً", mismatch.Message);
        Assert.Empty(db.ProductionOrders);
        var derived = orders.SaveOrder("FromPlan", plan.Id, s.Customer, "2026-09-01", 1, 1,
            TestOrderFixture.Items(host, plan.Id, s.Customer, "2026-09-01"));
        Assert.True(derived.Ok, derived.Message);
        var item = db.ProductionOrderItems.Single(i => i.OrderId == derived.Id);
        Assert.Equal(750, item.PlannedQtyKg, 1);   // الوزن المكافئ محسوب
        Assert.Equal(100, item.PlannedCartons);     // الكمية الأساسية كرتون — لم تستبدل
        Assert.Equal(7.5, item.CartonWeightKg, 1);  // وزن الكرتون محفوظ وقت العملية

        // قوالب مختلفة لمنتج آخر: 5 قوالب × 2 كجم = 10 كجم/كرتون (§القاعدة 4)
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var custom = master.SaveProductFull(null, "U-F9", "سكري مضغوط كبير", "002", "Finished", "كرتون", 0, 5, 2, null, sourceProductId: s.RawSuk);
        Assert.True(custom.Ok, custom.Message);
        Assert.Equal(10, UnitsPolicy.CartonWeight(db, custom.Id, null), 1);
    }

    // ══════════ 5) المخرجات الثانوية كجم فقط (§القاعدة 6) ══════════
    [Fact]
    public void ByProduct_Unit_Comes_From_Its_Definition()
    {
        // §القاعدة المعتمدة: لا تُفرض الوحدات داخل الكود. وحدة المخرج الثانوي من
        // تعريفه في شاشة الأصناف، والافتراض كجم عند الفراغ تيسيراً لا فرضاً.
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var ok = master.SaveProductFull(null, "U-B8", "نوى", "003", "ByProduct", null, 0, 0, 0, null);
        Assert.True(ok.Ok, ok.Message);
        var p = db.Products.Single(x => x.Id == ok.Id);
        Assert.Equal("كجم", p.UnitOfMeasure);
        Assert.Equal("003", p.GroupCode);

        // ووحدة معرَّفة صراحةً تُحفظ كما هي
        var custom = master.SaveProductFull(null, "U-B10", "عجينة", "003", "ByProduct", "كجم", 0, 0, 0, null);
        Assert.True(custom.Ok, custom.Message);
        Assert.Equal("كجم", db.Products.Single(x => x.Id == custom.Id).UnitOfMeasure);
    }

    // ══════════ 6) لا تغيير لوزن العبوة بأثر رجعي (§القاعدة 7) ══════════
    [Fact]
    public void Pack_Weight_Change_Does_Not_Affect_Previous_Operations()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var s = Seed(host);
        using var scope = host.Services.CreateScope();
        var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        // أمر بعبوة «كرتون 10 كجم» (العبوة رقم 2 المزروعة = 10 كجم)
        var order = TestOrderFixture.PlanThenOrder(host, s.Customer, "2026-09-01", 1, 1, new List<OrderItemDto>
        { new() { LotId = s.LotSuk, CustomerId = s.Customer, ProductId = s.FinSuk, PackagingTypeId = 2, PlannedQtyKg = 0, PlannedCartons = 200 } });
        Assert.True(order.Ok, order.Message);
        var item = db.ProductionOrderItems.Single(i => i.OrderId == order.Id);
        Assert.Equal(2000, item.PlannedQtyKg, 1);  // 200 × 10
        Assert.Equal(10, item.CartonWeightKg, 1);  // الوزن وقت العملية

        // تغيير تعريف العبوة إلى 20 كجم ← العمليات القديمة لا تتغير
        var pack = db.PackagingTypes.Single(p => p.Id == 2);
        pack.UnitWeightKg = 20;
        db.SaveChanges();

        var itemAfter = db.ProductionOrderItems.AsNoTracking().Single(i => i.OrderId == order.Id);
        Assert.Equal(2000, itemAfter.PlannedQtyKg, 1);      // لم تتغير
        Assert.Equal(10, itemAfter.CartonWeightKg, 1);      // الوزن التاريخي محفوظ
        Assert.Equal(20, UnitsPolicy.CartonWeight(db, s.FinSuk, 2), 1); // التعريف الجديد للعمليات القادمة فقط
    }

    // ══════════ 7) بطاقة الصنف: ثلاث مجموعات فقط والوحدة مفروضة ══════════
    [Fact]
    public void Product_Card_Takes_Unit_From_User_And_Group_From_Type()
    {
        // §القاعدة المعتمدة: الوحدة من تعريف الصنف، والمجموعة والتصنيف يحددان النوع.
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var fin = master.SaveProductFull(null, "U-F7", "سكري", "002", "Finished", "كرتون", 7.5, 1, 0.5, null);
        Assert.True(fin.Ok, fin.Message);
        Assert.Equal("كرتون", db.Products.Single(x => x.Id == fin.Id).UnitOfMeasure);
        Assert.Equal("002", db.Products.Single(x => x.Id == fin.Id).GroupCode);

        // والفراغ يأخذ افتراض المجموعة
        var fin2 = master.SaveProductFull(null, "U-F8", "سكري بلا وحدة", "002", "Finished", null, 7.5, 1, 0.5, null);
        Assert.True(fin2.Ok, fin2.Message);
        Assert.Equal("كرتون", db.Products.Single(x => x.Id == fin2.Id).UnitOfMeasure);

        // ونوع غير معتمد ما زال مرفوضاً — فهذا تصنيف لا وحدة
        var bad = master.SaveProductFull(null, "U-X1", "كرتون فارغ", "003", "Auxiliary", "قطعة", 0, 0, 0, null);
        Assert.False(bad.Ok);
    }

    // ══════════ 8) ترحيل القواعد القديمة إلى المجموعات الثلاث ══════════
    [Fact]
    public void Migrator_Normalizes_Legacy_Groups_To_The_Standard()
    {
        using var host = new TestHost();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        // محاكاة قاعدة قديمة: صنف ثانوي بالمجموعة 004 ووحدة كرتون
        db.Products.Add(new DatesErp.Core.Domain.Entities.Product
        {
            ProductCode = "LEG-1", ProductNameAr = "حشف قديم", GroupCode = "004",
            ItemType = "ByProduct", UnitOfMeasure = "كرتون", CartonWeightKg = 5
        });
        db.SaveChanges();

        var report = SchemaMigrator.Migrate(db);
        Assert.DoesNotContain(report, r => r.StartsWith("خطأ"));

        var legacy = db.Products.Single(p => p.ProductCode == "LEG-1");
        Assert.Equal("003", legacy.GroupCode);          // رُحّل إلى المخرجات الثانوية
        Assert.Equal("كجم", legacy.UnitOfMeasure);       // ووحدته أصبحت كجم
        var g3 = db.ItemGroups.Single(g => g.GroupCode == "003");
        Assert.Equal("ByProduct", g3.GroupType);
    }
}
