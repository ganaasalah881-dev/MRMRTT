using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace DatesErp.Tests;

/// <summary>
/// §فحص شاشة «تسليم بضاعة تامة» (أمر المطور — فحص بلا تعديل):
/// اختبارات توصيفية (Characterization) توثّق السلوك الحالي كما هو — بما فيه الثقوب —
/// بالأسماء الصريحة HOLE-*. بعد الموافقة على الحلول تُحدَّث التوقعات للسلوك المصحح.
/// الدورة: إنتاج ← جودة ← دخول المخزون ← تسليم ← خصم ← تقارير.
/// </summary>
public class DeliveryAuditProbeTests
{
    private readonly ITestOutputHelper _o;
    public DeliveryAuditProbeTests(ITestOutputHelper o) { _o = o; }

    private sealed class Chain
    {
        public int LotId, OrderId, PlanItemId, CheckId;
        public double Kg;
    }

    private static T Svc<T>(TestHost h) => h.Get<T>();
    private static DatesErpDbContext Db(TestHost h) => h.Get<DatesErpDbContext>();

    /// <summary>الدورة الكاملة: استلام خام ← خطة ← أمر ← تشغيل ← إقفال يوم ← فحص معتمد ← دخول مخزن التام باسم العميل.</summary>
    private static Chain BuildChain(TestHost host, int custId, int rawProductId, int finProductId, double kg, bool approveQc = true)
    {
        host.LoginAsAdmin(); // the prior chain's warehouse login does not grant planning/quality rights
        var receiving = Svc<IReceivingService>(host);
        var sh = receiving.SaveShipment(custId, "2026-09-01", "2026-09-01", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = rawProductId, PackageCount = (int)(kg / 20), UnitWeightKg = 20, QtyKg = kg } });
        Assert.True(sh.Ok, sh.Message);
        Assert.True(receiving.ApproveShipment(sh.Id).Ok);
        var db = Db(host);
        int lotId = db.Lots.Where(l => l.ShipmentId == sh.Id).OrderBy(l => l.Id).Last().Id;

        var planning = Svc<IPlanningService>(host);
        var p = planning.SavePlan($"خطة فحص {finProductId}", "Daily", "2026-09-10", "2026-09-10", 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = custId, ProductId = finProductId, PackagingTypeId = 1,
                    PlannedCartons = (int)(kg / 5), PlannedQtyKg = kg, ScheduledDate = "2026-09-10", SuggestedShiftId = 1, SuggestedLineId = 1 }
        });
        Assert.True(p.Ok, p.Message);
        Assert.True(planning.ApprovePlan(p.Id).Ok);
        int planItemId = db.ProductionPlanItems.Single(i => i.PlanId == p.Id).Id;

        var orders = Svc<IProductionOrderService>(host);
        host.SetBusinessDate("2026-09-10"); // Arrange production on its scheduled business day, not the machine date.
        var o = orders.SaveOrder("FromPlan", p.Id, custId, "2026-09-10", 1, 1, new List<OrderItemDto>
        { new() { PlanItemId = planItemId, LotId = lotId, CustomerId = custId, ProductId = finProductId, PackagingTypeId = 1,
                  PlannedQtyKg = kg, PlannedCartons = (int)(kg / 5) } });
        Assert.True(o.Ok, o.Message);
        Assert.True(orders.ApproveOrder(o.Id).Ok);
        Assert.True(orders.StartOrder(o.Id).Ok);
        var exec = Svc<IExecutionService>(host);
        var cd = exec.CloseProductionDay(o.Id, kg, (int)(kg / 5), 0, 0, 0, false, new List<DowntimeDto>(), true);
        Assert.True(cd.Ok, cd.Message);

        var qcSvc = Svc<IQualityService>(host);
        int qcExec = db.ProductionExecutions.Where(e => e.OrderId == o.Id).OrderBy(e => e.Id).Last().Id;
        var qc = qcSvc.SaveCheck(o.Id, qcExec, "2026-09-12", "نهائي", new List<QualityItemDto>
        { new() { ProductId = finProductId, LotId = lotId, CheckedQtyKg = kg, AcceptedQtyKg = kg, RejectedQtyKg = 0,
                  CheckedCartons = kg / 5, AcceptedCartons = kg / 5, RejectedCartons = 0 } }, null, new QualityLabDto { Decision = "Passed" });
        Assert.True(qc.Ok, qc.Message);
        if (approveQc) Assert.True(qcSvc.ApproveCheck(qc.Id).Ok);

        var fg = Svc<IFinishedGoodsService>(host);
        var rc = TestProductionDocumentFlow.SaveReceiptFromActual(host, o.Id, qc.Id, "2026-09-12", new List<FinishedGoodsItemDto>
        { new() { ProductId = finProductId, LotId = lotId, PackagingTypeId = 1, PackageCount = (int)(kg / 5), NetWeightKg = kg, CustomerId = custId } });
        Assert.True(rc.Ok, rc.Message);
        Assert.True(fg.Issue(rc.Id).Ok);
        Assert.True(fg.Receive(rc.Id, null).Ok);
        return new Chain { LotId = lotId, OrderId = o.Id, PlanItemId = planItemId, CheckId = qc.Id, Kg = kg };
    }

    private static int Save(TestHost host, int cust, params (int prod, int? lot, int? pack, double kg, int ctn)[] items)
    {
        var svc = Svc<ICustomerDeliveryService>(host);
        var r = svc.Save(cust, "2026-09-13", null, items.Select(i => new CustomerDeliveryItemDto
        { ProductId = i.prod, LotId = i.lot, PackagingTypeId = i.pack, QtyKg = i.kg, PackageCount = i.ctn }).ToList());
        Assert.True(r.Ok, r.Message);
        return r.Id;
    }

    private static (int cust1, int cust2) Customers(TestHost host)
    {
        var db = Db(host);
        int c1 = db.Customers.OrderBy(c => c.Id).First().Id;
        var second = db.Customers.OrderBy(c => c.Id).Skip(1).FirstOrDefault();
        if (second == null)
        {
            second = new Customer { CustomerCode = "AUD-C2", CustomerName = "عميل الفحص الثاني", IsActive = true, RowVersion = Guid.NewGuid().ToByteArray() };
            db.Customers.Add(second);
            db.SaveChanges();
        }
        return (c1, second.Id);
    }

    private static double Balance(TestHost host, int cust, int prod)
    {
        var db = Db(host);
        int wfg = db.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
        return db.StockBalances.Where(b => b.WarehouseId == wfg && b.CustomerId == cust && b.ProductId == prod).Sum(b => b.QtyKg);
    }

    // ═══ س1: عميل واحد — دورة كاملة وخصم صحيح وانعكاس على الدفعة والخطة والحركات ═══
    [Fact]
    public void S1_SingleCustomer_FullCycle_Deducts_And_Reflects()
    {
        using var host = new TestHost(); host.LoginAsAdmin();
        var (c1, _) = Customers(host);
        var ch = BuildChain(host, c1, 1, 3, 1000);
        Assert.Equal(1000, Balance(host, c1, 3), 1); // دخل المخزون باسم العميل

        int dlv = Save(host, c1, (3, ch.LotId, 1, 1000, 200));
        var ap = Svc<ICustomerDeliveryService>(host).Approve(dlv);
        Assert.True(ap.Ok, ap.Message);

        Assert.Equal(0, Balance(host, c1, 3), 1);                                  // خصم المخزون
        Assert.Equal(1000, Db(host).Lots.Single(l => l.Id == ch.LotId).DeliveredQtyKg, 1); // الدفعة
        Assert.Equal(1000, Db(host).ProductionPlanItems.Single(i => i.Id == ch.PlanItemId).DeliveredQtyKg, 1); // الخطة
        var txns = Db(host).InventoryTransactions.Count(t => t.MovementType == Core.Domain.Enums.MovementType.Outbound
            && t.ReferenceDocType == Core.Domain.Enums.ReferenceDocType.CustomerDelivery);
        Assert.True(txns >= 1);                                                     // حركة دفتر الأستاذ
        _o.WriteLine($"س1 ✔ خصم وانعكاس سليم — حركات تسليم: {txns}");
    }

    // ═══ س2: عدة أصناف في سند واحد ═══
    [Fact]
    public void S2_MultiItems_OneDoc_BothDeducted()
    {
        using var host = new TestHost(); host.LoginAsAdmin();
        var (c1, _) = Customers(host);
        var a = BuildChain(host, c1, 1, 3, 1000);
        var b = BuildChain(host, c1, 2, 4, 600);
        int dlv = Save(host, c1, (3, a.LotId, 1, 1000, 200), (4, b.LotId, 1, 600, 120));
        var ap = Svc<ICustomerDeliveryService>(host).Approve(dlv);
        Assert.True(ap.Ok, ap.Message);
        Assert.Equal(0, Balance(host, c1, 3), 1);
        Assert.Equal(0, Balance(host, c1, 4), 1);
        _o.WriteLine("س2 ✔ صنفان في سند واحد — خصما معاً");
    }

    // ═══ س3: تسليم جزئي ثم استكمال ═══
    [Fact]
    public void S3_Partial_Then_Complete()
    {
        using var host = new TestHost(); host.LoginAsAdmin();
        var (c1, _) = Customers(host);
        var ch = BuildChain(host, c1, 1, 3, 1000);

        int d1 = Save(host, c1, (3, ch.LotId, 1, 400, 80));
        Assert.True(Svc<ICustomerDeliveryService>(host).Approve(d1).Ok);
        Assert.Equal(600, Balance(host, c1, 3), 1); // المتبقي الحقيقي ظاهر

        int d2 = Save(host, c1, (3, ch.LotId, 1, 600, 120));
        Assert.True(Svc<ICustomerDeliveryService>(host).Approve(d2).Ok);
        Assert.Equal(0, Balance(host, c1, 3), 1);
        Assert.Equal(1000, Db(host).Lots.Single(l => l.Id == ch.LotId).DeliveredQtyKg, 1);
        _o.WriteLine("س3 ✔ جزئي 400 ثم 600 — المتبقي حقيقي والاستكمال سليم");
    }

    // ═══ س4: عدة عملاء — لا خلط: دفعة عميل لا تُسلَّم لآخر ═══
    [Fact]
    public void S4_CrossCustomer_Blocked()
    {
        using var host = new TestHost(); host.LoginAsAdmin();
        var (c1, c2) = Customers(host);
        var ch = BuildChain(host, c2, 1, 3, 1000); // البضاعة للعميل 2

        // محاولة الحفظ باسم العميل 1 ← رفض CROSS_CUSTOMER
        var svc = Svc<ICustomerDeliveryService>(host);
        var bad = svc.Save(c1, "2026-09-13", null, new List<CustomerDeliveryItemDto>
        { new() { ProductId = 3, LotId = ch.LotId, PackagingTypeId = 1, QtyKg = 1000, PackageCount = 200 } });
        Assert.False(bad.Ok);
        Assert.Contains("تخص عميلاً مختلفاً", bad.Message);   // CROSS_CUSTOMER
        Assert.Equal(1000, Balance(host, c2, 3), 1); // رصيد العميل 2 لم يُمس
        Assert.Equal(0, Balance(host, c1, 3), 1);    // ولا شيء باسم العميل 1
        _o.WriteLine("س4 ✔ دفعة عميل لعميل آخر — مرفوضة")
;    }

    // ═══ س5: كمية أكبر من الرصيد وأكبر من المعتمد ═══
    [Fact]
    public void S5_OverBalance_And_OverApproved_Rejected()
    {
        using var host = new TestHost(); host.LoginAsAdmin();
        var (c1, _) = Customers(host);
        var ch = BuildChain(host, c1, 1, 3, 1000);
        var svc = Svc<ICustomerDeliveryService>(host);

        int d1 = Save(host, c1, (3, ch.LotId, 1, 1500, 300)); // فوق الرصيد (الحفظ مسودة — الرفض عند الاعتماد)
        var over = svc.Approve(d1);
        Assert.False(over.Ok);
        Assert.Contains("رصيد", over.Message);
        _o.WriteLine($"س5 ✔ فوق الرصيد مرفوض: {over.Message.Split('\n')[0]}");
    }

    // ═══ س6: P1 مُصلَّح — صفر وسالب مرفوضان عند الحفظ ═══
    [Fact]
    public void S6_Zero_And_Negative_Qty_Rejected()
    {
        using var host = new TestHost(); host.LoginAsAdmin();
        var (c1, _) = Customers(host);
        var ch = BuildChain(host, c1, 1, 3, 1000);
        var svc = Svc<ICustomerDeliveryService>(host);

        var rz = svc.Save(c1, "2026-09-13", null, new List<CustomerDeliveryItemDto>
        { new() { ProductId = 3, LotId = ch.LotId, PackagingTypeId = 1, QtyKg = 0, PackageCount = 0 } });
        Assert.False(rz.Ok);
        Assert.Contains("أكبر من صفر", rz.Message);

        var rn = svc.Save(c1, "2026-09-13", null, new List<CustomerDeliveryItemDto>
        { new() { ProductId = 3, LotId = ch.LotId, PackagingTypeId = 1, QtyKg = -100, PackageCount = -20 } });
        Assert.False(rn.Ok);

        Assert.Equal(1000, Balance(host, c1, 3), 1); // الرصيد لم يُمس
        Assert.Equal(0, Db(host).Lots.Single(l => l.Id == ch.LotId).DeliveredQtyKg, 1);
        _o.WriteLine("س6 ✔ (بعد P1) صفر وسالب مرفوضان والرصيد سليم");
    }

    // ═══ س7: P2 مُصلَّح — تعديل المسودة يحدّث نفس السند + حذف المسودة + المعتمد محمي ═══
    [Fact]
    public void S7_Update_Same_Doc_And_DeleteDraft()
    {
        using var host = new TestHost(); host.LoginAsAdmin();
        var (c1, _) = Customers(host);
        var ch = BuildChain(host, c1, 1, 3, 1000);
        var svc = Svc<ICustomerDeliveryService>(host);

        int first = Save(host, c1, (3, ch.LotId, 1, 1000, 200));
        // تعديل المسودة: نفس السند بكمية محدثة
        var up = svc.Update(first, c1, "2026-09-13", null, new List<CustomerDeliveryItemDto>
        { new() { ProductId = 3, LotId = ch.LotId, PackagingTypeId = 1, QtyKg = 800, PackageCount = 160 } });
        Assert.True(up.Ok, up.Message);
        Assert.Equal(first, up.Id);
        Assert.Equal(800, Db(host).CustomerDeliveryItems.Where(i => i.DeliveryId == first).Sum(i => i.QtyKg), 1);
        Assert.Equal(1, Db(host).CustomerDeliveries.Count()); // لا تكرار

        // الاعتماد ثم محاولة تعديل ← رفض
        Assert.True(svc.Approve(first).Ok);
        var upApproved = svc.Update(first, c1, "2026-09-13", null, new List<CustomerDeliveryItemDto>
        { new() { ProductId = 3, LotId = ch.LotId, PackagingTypeId = 1, QtyKg = 500, PackageCount = 100 } });
        Assert.False(upApproved.Ok);
        host.LoginAsAdmin(); // deleting drafts requires delivery/Delete
        var delApproved = svc.DeleteDraft(first);
        Assert.False(delApproved.Ok); // المعتمد لا يُحذف

        // مسودة جديدة ← حذف ← تختفي
        int second = Save(host, c1, (3, ch.LotId, 1, 100, 20));
        var del = svc.DeleteDraft(second);
        Assert.True(del.Ok, del.Message);
        Assert.Equal(1, Db(host).CustomerDeliveries.Count());
        _o.WriteLine("س7 ✔ (بعد P2) تعديل بلا تكرار + حذف مسودة + حماية المعتمد");
    }

    // ═══ س10: P6 — بند بلا دفعة والصنف في دفعتين ← الدفعة إلزامية ═══
    [Fact]
    public void S10_MultiLot_Without_Lot_Rejected_Explicit_Lot_Works()
    {
        using var host = new TestHost(); host.LoginAsAdmin();
        var (c1, _) = Customers(host);
        var a = BuildChain(host, c1, 1, 3, 1000); // دفعة أولى
        var b = BuildChain(host, c1, 1, 3, 500);  // دفعة ثانية لنفس الصنف والعميل
        var svc = Svc<ICustomerDeliveryService>(host);

        // بلا دفعة والصنف في دفعتين ← رفض LOT_REQUIRED
        int d = Save(host, c1, (3, null, 1, 300, 60));
        var r = svc.Approve(d);
        Assert.False(r.Ok);
        Assert.Contains("حدّد الدفعة", r.Message);

        // بدفعة صريحة ← يمر
        int d2 = Save(host, c1, (3, b.LotId, 1, 300, 60));
        Assert.True(svc.Approve(d2).Ok);
        Assert.Equal(1200, Balance(host, c1, 3), 1); // 1500 − 300
        _o.WriteLine("س10 ✔ (بعد P6) بلا دفعة مرفوض عند التعدد وبدفعة صريحة يمر");
    }

    // ═══ س8: منتج لم يُفحص/يعتمد ← منع التسليم ═══
    [Fact]
    public void S8_Unapproved_QC_Blocks_Delivery()
    {
        using var host = new TestHost(); host.LoginAsAdmin();
        var (c1, _) = Customers(host);
        var ch = BuildChain(host, c1, 1, 3, 1000, approveQc: false); // فحص غير معتمد
        int d = Save(host, c1, (3, ch.LotId, 1, 1000, 200));
        var r = Svc<ICustomerDeliveryService>(host).Approve(d);
        Assert.False(r.Ok);
        Assert.Contains("فحص جودة معتمد", r.Message);
        _o.WriteLine($"س8 ✔ غير المعتمد ممنوع: {r.Message.Split('\n')[0]}");
    }

    // ═══ س9: سجل التدقيق — مُنفَّذ تلقائياً (مُثبت) ═══
    [Fact]
    public void S9_AuditLog_Recorded_Automatically()
    {
        using var host = new TestHost(); host.LoginAsAdmin();
        var (c1, _) = Customers(host);
        var ch = BuildChain(host, c1, 1, 3, 1000);
        int d = Save(host, c1, (3, ch.LotId, 1, 1000, 200));
        var svc = Svc<ICustomerDeliveryService>(host);
        Assert.True(svc.Approve(d).Ok);
        var docNo = Db(host).CustomerDeliveries.Single(x => x.Id == d).DocumentNumber;

        // §فحص — نتيجة مثبَّتة بالتشغيل: التدقيق تلقائي عبر AuditSaveChangesInterceptor (حفظ + اعتماد = قيدان)
        var entries = Db(host).AuditLogs.Where(a => a.DocumentNumber == docNo).ToList();
        _o.WriteLine($"س9 ✔ قيود التدقيق للسند {docNo} = {entries.Count} — مستخدم: {entries.FirstOrDefault()?.UserName} · إجراء: {entries.FirstOrDefault()?.ActionType}");
        Assert.True(entries.Count >= 2);
        Assert.All(entries, a => Assert.False(string.IsNullOrWhiteSpace(a.UserName)));
    }
}
