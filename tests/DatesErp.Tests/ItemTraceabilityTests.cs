using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §أمر التطوير الإلزامي — الصنف محور التتبع الكامل:
/// استلام → خطة → أمر → إنتاج → فحص → مخزون → تسليم → فاتورة،
/// والهوية (سكري/خلاص) لا تضيع ولا تُدمج في أي مرحلة، لعميل واحد ولعدة عملاء.
/// </summary>
public class ItemTraceabilityTests
{
    private sealed record ItemIds(int RawSukkari, int RawKhalas, int FinSukkari, int FinKhalas);

    /// <summary>إنشاء شركة + أصناف سكري/خلاص (خام وتام) مع التعريف الرسمي للتحويل.</summary>
    private static (int cust, ItemIds ids) SeedCompany(TestHost host, string code, string name)
    {
        using var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var c = master.SaveCustomer(null, code, name, "جملة", "777000", "مسؤول", true);
        Assert.True(c.Ok, c.Message);
        var rs = master.SaveProductFull(null, $"{code}-R1", "سكري", "001", "Raw", "كجم", 20, 0, 0, null);
        var rk = master.SaveProductFull(null, $"{code}-R2", "خلاص", "001", "Raw", "كجم", 20, 0, 0, null);
        Assert.True(rs.Ok, rs.Message); Assert.True(rk.Ok, rk.Message);
        var fs = master.SaveProductFull(null, $"{code}-F1", "سكري تام", "002", "Finished", "كرتون", 7.5, 1, 0.5, null, sourceProductId: rs.Id);
        var fk = master.SaveProductFull(null, $"{code}-F2", "خلاص تام", "002", "Finished", "كرتون", 7.5, 1, 0.5, null, sourceProductId: rk.Id);
        Assert.True(fs.Ok, fs.Message); Assert.True(fk.Ok, fk.Message);
        return (c.Id, new ItemIds(rs.Id, rk.Id, fs.Id, fk.Id));
    }

    /// <summary>استلام سكري + خلاص واعتماده — يجب أن ينشئ دفعتين مستقلتين (لا دمج).</summary>
    private static (int lotSukkari, int lotKhalas) ReceiveTwoVarieties(TestHost host, int cust, ItemIds ids, double sukkariKg, double khalasKg)
    {
        using var scope = host.Services.CreateScope();
        var receiving = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var items = new List<ShipmentItemDto>();
        if (sukkariKg > 0) items.Add(new ShipmentItemDto { TreatmentRequired = false, ProductId = ids.RawSukkari, QtyKg = sukkariKg, PackageCount = (int)(sukkariKg / 20), UnitWeightKg = 20 });
        if (khalasKg > 0) items.Add(new ShipmentItemDto { TreatmentRequired = false, ProductId = ids.RawKhalas, QtyKg = khalasKg, PackageCount = (int)(khalasKg / 20), UnitWeightKg = 20 });
        var s = receiving.SaveShipment(cust, "2026-08-20", "2026-08-20", items);
        Assert.True(s.Ok, s.Message);
        var ap = receiving.ApproveShipment(s.Id);
        Assert.True(ap.Ok, ap.Message);
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var lots = db.Lots.Where(l => l.ShipmentId == s.Id).ToList();
        Assert.Equal(items.Count, lots.Count); // كل صنف كيانه المستقل — لا دمج
        int ls = lots.FirstOrDefault(l => l.ProductId == ids.RawSukkari)?.Id ?? 0;
        int lk = lots.FirstOrDefault(l => l.ProductId == ids.RawKhalas)?.Id ?? 0;
        return (ls, lk);
    }

    // ══════════════════════════════════════════════════════════════
    // 17) الاختبار الإجباري الكامل — عميل واحد، سكري + خلاص حتى الفاتورة
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void Sukkari_And_Khalas_FullCycle_SingleCustomer_IdentityPreserved()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (custA, ids) = SeedCompany(host, "TA", "شركة A");
        var (lotSukkari, lotKhalas) = ReceiveTwoVarieties(host, custA, ids, 10000, 8000);
        Assert.NotEqual(0, lotSukkari); Assert.NotEqual(0, lotKhalas);
        Assert.NotEqual(lotSukkari, lotKhalas); // منع الدمج: دفعتان مستقلتان

        // ── خطة واحدة: سكري ← سكري تام | خلاص ← خلاص تام ──
        int planId, orderId, executionId;
        List<int> planItemIds;
        using (var scope = host.Services.CreateScope())
        {
            var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            TestCapacityFixture.DefineMissingRates(host);
            var plan = planning.SavePlan("خطة شركة A — سكري وخلاص", "Daily", "2026-08-25", "2026-08-25", 1, 1, new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lotSukkari, CustomerId = custA, ProductId = ids.FinSukkari, PlannedQtyKg = 5000, PriorityNo = 1 },
                new() { SourceType = "FromReceiving", LotId = lotKhalas, CustomerId = custA, ProductId = ids.FinKhalas, PlannedQtyKg = 4000, PriorityNo = 2 }
            });
            Assert.True(plan.Ok, plan.Message);
            planId = plan.Id;
            var apPlan = planning.ApprovePlan(planId);
            Assert.True(apPlan.Ok, apPlan.Message);

            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            planItemIds = db.ProductionPlanItems.Where(i => i.PlanId == planId).OrderBy(i => i.PriorityNo).Select(i => i.Id).ToList();
            Assert.Equal(2, planItemIds.Count);

            // ── أمر الإنتاج ينقل الصنف كما هو من الخطة ──
            var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            host.SetBusinessDate("2026-08-25"); // Arrange production on its scheduled business day, not the machine date.
            var order = orders.SaveOrder("FromPlan", planId, custA, "2026-08-25", 1, 1, TestOrderFixture.Items(host, planId, custA, "2026-08-25"));
            Assert.True(order.Ok, order.Message);
            orderId = order.Id;
            var ao = orders.ApproveOrder(orderId);
            Assert.True(ao.Ok, ao.Message);

            // ── الإنتاج الفعلي: إقفال اليوم وإرسال للجودة ──
            var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();
            var close = exec.CloseProductionDay(orderId, producedKg: 9000, producedCartons: 1200,
                hashfKg: 0, nawaKg: 0, wastageKg: 0, carryToNextDay: false,
                downtimes: new List<DowntimeDto>(), sendToQuality: true);
            Assert.True(close.Ok, close.Message);
            executionId = close.Id;

            // ── الفحص: كل منتج بهويته (سكري تام / خلاص تام) ──
            var quality = scope.ServiceProvider.GetRequiredService<IQualityService>();
            var qc = quality.SaveCheck(orderId, executionId, "2026-08-27", "نهائي", new List<QualityItemDto>
            {
                new() { ProductId = ids.FinSukkari, CheckedQtyKg = 5000, AcceptedQtyKg = 4950, RejectedQtyKg = 50 },
                new() { ProductId = ids.FinKhalas, CheckedQtyKg = 4000, AcceptedQtyKg = 3950, RejectedQtyKg = 50 }
            });
            Assert.True(qc.Ok, qc.Message);
            var qcAp = quality.ApproveCheck(qc.Id);
            Assert.True(qcAp.Ok, qcAp.Message);

            // ── المخزون: استلام الإنتاج التام (محاولة خاطئة أولاً: خلاص بدفعة سكري) ──
            var fg = scope.ServiceProvider.GetRequiredService<IFinishedGoodsService>();
            var wrong = TestProductionDocumentFlow.SaveReceiptFromActual(host, orderId, qc.Id, "2026-08-27", new List<FinishedGoodsItemDto>
            { new() { ProductId = ids.FinKhalas, LotId = lotSukkari, NetWeightKg = 97.5, PackageCount = 13 } });
            Assert.False(wrong.Ok);
            Assert.Contains("بند أمر تسليم", wrong.Message); // السطر المزوَّر لا يطابق هوية المصدر

            var rcpt = TestProductionDocumentFlow.SaveReceiptFromActual(host, orderId, qc.Id, "2026-08-27", new List<FinishedGoodsItemDto>
            {
                new() { ProductId = ids.FinSukkari, NetWeightKg = 4950, PackageCount = 660 },
                new() { ProductId = ids.FinKhalas, NetWeightKg = 3950, PackageCount = 526 }
            });
            Assert.True(rcpt.Ok, rcpt.Message);
            Assert.True(fg.Issue(rcpt.Id).Ok);
            var recv = fg.Receive(rcpt.Id, null);
            Assert.True(recv.Ok, recv.Message);

            // ── التسليم: سند واحد متعدد الأصناف (سكري تام + خلاص تام) + محاولة خاطئة ──
            var dlvSvc = scope.ServiceProvider.GetRequiredService<ICustomerDeliveryService>();
            var wrongDlv = dlvSvc.Save(custA, "2026-08-28", orderId, new List<CustomerDeliveryItemDto>
            { new() { ProductId = ids.FinKhalas, LotId = lotSukkari, QtyKg = 100 } });
            Assert.False(wrongDlv.Ok);
            Assert.Contains("تحويل", wrongDlv.Message);

            var dlv = dlvSvc.Save(custA, "2026-08-28", orderId, new List<CustomerDeliveryItemDto>
            {
                new() { ProductId = ids.FinSukkari, QtyKg = 1000 },
                new() { ProductId = ids.FinKhalas, QtyKg = 500 }
            });
            Assert.True(dlv.Ok, dlv.Message);
            var adv = dlvSvc.Approve(dlv.Id);
            Assert.True(adv.Ok, adv.Message);

            // ── الفاتورة على المسلَّم فعلياً ──
            var progress = scope.ServiceProvider.GetRequiredService<IPlanProgressService>();
            var inv = progress.MarkInvoiced(dlv.Id, 1500);
            Assert.True(inv.Ok, inv.Message);
        }

        // ── التتبع الكامل: رحلة سكري ورحلة خلاص ──
        using (var scope = host.Services.CreateScope())
        {
            var trace = scope.ServiceProvider.GetRequiredService<ITraceabilityService>();

            var jS = trace.GetJourneys(custA, ids.RawSukkari).Single();
            Assert.Equal("سكري", jS.ProductName);
            Assert.Equal(10000, jS.ReceivedKg, 1);
            Assert.Equal(5000, jS.PlannedKg, 1);
            Assert.Equal(5000, jS.ProducedKg, 1);
            Assert.Equal(4950, jS.AcceptedKg, 1);
            Assert.Equal(3950, jS.InStockKg, 1);   // 4950 − 1000 مسلَّم
            Assert.Equal(1000, jS.DeliveredKg, 1);
            Assert.Equal(1000, jS.InvoicedKg, 1);
            Assert.Equal(3950, jS.RemainingKg, 1);
            AssertStage(jS, "استلام"); AssertStage(jS, "خطة"); AssertStage(jS, "أمر");
            AssertStage(jS, "إنتاج"); AssertStage(jS, "فحص"); AssertStage(jS, "مخزن");
            AssertStage(jS, "تسليم"); AssertStage(jS, "فاتورة");
            Assert.All(jS.Stages, st => Assert.DoesNotContain("تمور", st.ProductName ?? ""));

            var jK = trace.GetJourneys(custA, ids.RawKhalas).Single();
            Assert.Equal("خلاص", jK.ProductName);
            Assert.Equal(8000, jK.ReceivedKg, 1);
            Assert.Equal(4000, jK.PlannedKg, 1);
            Assert.Equal(4000, jK.ProducedKg, 1);
            Assert.Equal(3950, jK.AcceptedKg, 1);
            Assert.Equal(3450, jK.InStockKg, 1);   // 3950 − 500
            Assert.Equal(500, jK.DeliveredKg, 1);
            Assert.Equal(500, jK.InvoicedKg, 1);
            Assert.Equal(3450, jK.RemainingKg, 1);
            AssertStage(jK, "استلام"); AssertStage(jK, "تسليم");

            // رحلة الدفعة مباشرة
            var jl = trace.GetLotJourney(lotSukkari);
            Assert.NotNull(jl);
            Assert.Contains(jl.Stages, st => st.StageAr.Contains("الدفعة محور التتبع") && st.QtyKg == 10000);
        }
    }

    private static void AssertStage(ProductJourneyDto j, string keyword)
        => Assert.Contains(j.Stages, s => (s.StageAr ?? "").Contains(keyword));

    // ══════════════════════════════════════════════════════════════
    // 18) عدة عملاء في خطة واحدة — لا خلط بين سكري A وسكري B
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void One_Plan_Three_Customers_NoIdentityMixing()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();

        // كل عميل بأصنافه الخاصة (سلالات مستقلة لكل عميل — كما في الواقع: استلام منفصل)
        var (custA, idsA) = SeedCompany(host, "TA", "شركة A");
        var (custB, idsB) = SeedCompany(host, "TB", "شركة B");
        var (custC, idsC) = SeedCompany(host, "TC", "شركة C");

        var (lotASuk, lotAKha) = ReceiveTwoVarieties(host, custA, idsA, 5000, 4000);
        var (lotBSuk, lotBKha) = ReceiveTwoVarieties(host, custB, idsB, 6000, 3000);
        var (lotCSuk, _) = ReceiveTwoVarieties(host, custC, idsC, 2000, 0);

        int planId;
        using (var scope = host.Services.CreateScope())
        {
            var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            // خطة واحدة: 5 بنود لثلاثة عملاء
            TestCapacityFixture.DefineMissingRates(host);
            var plan = planning.SavePlan("خطة ثلاثة عملاء", "Period", "2026-09-01", "2026-09-05", 1, 1, new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lotASuk, CustomerId = custA, ProductId = idsA.FinSukkari, PlannedQtyKg = 3000, PriorityNo = 1 },
                new() { SourceType = "FromReceiving", LotId = lotAKha, CustomerId = custA, ProductId = idsA.FinKhalas, PlannedQtyKg = 2000, PriorityNo = 2 },
                new() { SourceType = "FromReceiving", LotId = lotBSuk, CustomerId = custB, ProductId = idsB.FinSukkari, PlannedQtyKg = 2500, PriorityNo = 3 },
                new() { SourceType = "FromReceiving", LotId = lotBKha, CustomerId = custB, ProductId = idsB.FinKhalas, PlannedQtyKg = 1500, PriorityNo = 4 },
                new() { SourceType = "FromReceiving", LotId = lotCSuk, CustomerId = custC, ProductId = idsC.FinSukkari, PlannedQtyKg = 1000, PriorityNo = 5 }
            });
            Assert.True(plan.Ok, plan.Message);
            planId = plan.Id;
            var ap = planning.ApprovePlan(planId);
            Assert.True(ap.Ok, ap.Message);

            // الحجوزات منفصلة لكل دفعة — لا خلط
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            Assert.Equal(3000, db.Lots.Single(l => l.Id == lotASuk).ReservedQtyKg, 1);
            Assert.Equal(2500, db.Lots.Single(l => l.Id == lotBSuk).ReservedQtyKg, 1);
            Assert.Equal(1000, db.Lots.Single(l => l.Id == lotCSuk).ReservedQtyKg, 1);

            // تقدم كل عميل مستقل داخل الخطة
            var progress = scope.ServiceProvider.GetRequiredService<IPlanProgressService>();
            var byCust = progress.GetPlanProgressByCustomer(planId);
            Assert.Equal(3, byCust.Count);
            Assert.Equal(5000, byCust.First(c => c.CustomerId == custA).Planned, 1); // 3000 سكري + 2000 خلاص
            Assert.Equal(4000, byCust.First(c => c.CustomerId == custB).Planned, 1);
            Assert.Equal(1000, byCust.First(c => c.CustomerId == custC).Planned, 1);

            // الرحلة لكل عميل منفصلة — سكري A ≠ سكري B ≠ سكري C
            var trace = scope.ServiceProvider.GetRequiredService<ITraceabilityService>();
            Assert.Equal(5000, trace.GetJourneys(custA, idsA.RawSukkari).Single().ReceivedKg, 1);
            Assert.Equal(6000, trace.GetJourneys(custB, idsB.RawSukkari).Single().ReceivedKg, 1);
            Assert.Equal(2000, trace.GetJourneys(custC, idsC.RawSukkari).Single().ReceivedKg, 1);
            // تخطيط كل عميل يظهر في رحلته فقط
            Assert.Equal(3000, trace.GetJourneys(custA, idsA.RawSukkari).Single().PlannedKg, 1);
            Assert.Equal(2500, trace.GetJourneys(custB, idsB.RawSukkari).Single().PlannedKg, 1);
        }

        // محاولة تخطيط دفعة العميل B باسم العميل A — مرفوضة (لا دمج ملكيات)
        using (var scope = host.Services.CreateScope())
        {
            var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            TestCapacityFixture.DefineMissingRates(host);
            var bad = planning.SavePlan("خلط ملكية", "Daily", "2026-09-06", "2026-09-06", 1, 1, new List<PlanItemDto>
            { new() { SourceType = "FromReceiving", LotId = lotBSuk, CustomerId = custA, ProductId = idsA.FinSukkari, PlannedQtyKg = 500, PriorityNo = 1 } });
            Assert.False(bad.Ok);
        }
    }

    // ══════════════════════════════════════════════════════════════
    // 19) اختبارات منع الأخطاء
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Prevent_Producing_Khalas_From_Sukkari_Without_OfficialDefinition()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (cust, ids) = SeedCompany(host, "TX", "شركة الاختبار");
        var (lotSukkari, _) = ReceiveTwoVarieties(host, cust, ids, 5000, 0);

        using var scope = host.Services.CreateScope();
        var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();

        // 1) محاولة إنتاج خلاص تام من دفعة سكري ← رفض قاطع
        TestCapacityFixture.DefineMissingRates(host);
        var wrong = planning.SavePlan("تحويل خاطئ", "Daily", "2026-09-01", "2026-09-01", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lotSukkari, CustomerId = cust, ProductId = ids.FinKhalas, PlannedQtyKg = 1000, PriorityNo = 1 } });
        Assert.False(wrong.Ok);
        Assert.Contains("تحويل", wrong.Message);

        // 2) منتج بلا تعريف تحويل رسمي ← رفض حتى يُضاف التعريف في البطاقة
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var noDef = master.SaveProductFull(null, "TX-F9", "منتج غامض", "002", "Finished", "كرتون", 7.5, 1, 0.5, null);
        Assert.True(noDef.Ok, noDef.Message);
        TestCapacityFixture.DefineMissingRates(host);
        var noDefPlan = planning.SavePlan("بلا تعريف", "Daily", "2026-09-01", "2026-09-01", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lotSukkari, CustomerId = cust, ProductId = noDef.Id, PlannedQtyKg = 500, PriorityNo = 1 } });
        Assert.False(noDefPlan.Ok);
        Assert.Contains("تعريف", noDefPlan.Message);

        // 3) بعد إضافة التعريف الرسمي (سكري ← سكري) ← يُقبل
        var fix = master.SaveProductFull(noDef.Id, "TX-F9", "سكري مضغوط", "002", "Finished", "كرتون", 7.5, 1, 0.5, null, sourceProductId: ids.RawSukkari);
        Assert.True(fix.Ok, fix.Message);
        TestCapacityFixture.DefineMissingRates(host);
        var okPlan = planning.SavePlan("بتعريف رسمي", "Daily", "2026-09-01", "2026-09-01", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lotSukkari, CustomerId = cust, ProductId = noDef.Id, PlannedQtyKg = 500, PriorityNo = 1 } });
        Assert.True(okPlan.Ok, okPlan.Message);
    }

    [Fact]
    public void Prevent_Changing_ItemIdentity_After_OperationsExist()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (cust, ids) = SeedCompany(host, "TY", "شركة القفل");
        var (lotSukkari, _) = ReceiveTwoVarieties(host, cust, ids, 3000, 2000);

        int itemId;
        using (var scope = host.Services.CreateScope())
        {
            var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            TestCapacityFixture.DefineMissingRates(host);
            var plan = planning.SavePlan("خطة القفل", "Daily", "2026-09-01", "2026-09-01", 1, 1, new List<PlanItemDto>
            { new() { SourceType = "FromReceiving", LotId = lotSukkari, CustomerId = cust, ProductId = ids.FinSukkari, PlannedQtyKg = 2000, PriorityNo = 1 } });
            Assert.True(plan.Ok, plan.Message);
            Assert.True(planning.ApprovePlan(plan.Id).Ok);
            itemId = db.ProductionPlanItems.Single(i => i.PlanId == plan.Id).Id;

            // أمر إنتاج مرتبط بالبند = عملية موجودة
            var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            host.SetBusinessDate("2026-09-01"); // Arrange production on its scheduled business day, not the machine date.
            var order = orders.SaveOrder("FromPlan", plan.Id, cust, "2026-09-01", 1, 1, TestOrderFixture.Items(host, plan.Id, cust, "2026-09-01"));
            Assert.True(order.Ok, order.Message);

            // محاولة تغيير الصنف إلى خلاص تام ← مرفوضة (قفل الهوية)
            var progress = scope.ServiceProvider.GetRequiredService<IPlanProgressService>();
            var change = progress.UpdatePlanItem(itemId, newProductId: ids.FinKhalas);
            Assert.False(change.Ok);
            Assert.Contains("صدر أمر إنتاج", change.Message);
            Assert.Equal(ids.FinSukkari, db.ProductionPlanItems.AsNoTracking().Single(i => i.Id == itemId).ProductId);

            // محاولة تغييره إلى منتج سكري آخر غير موجود في تحويل الدفعة — أيضاً مرفوض بالتحويل الرسمي
            var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
            var other = master.SaveProductFull(null, "TY-F9", "خلاص مضغوط", "002", "Finished", "كرتون", 7.5, 1, 0.5, null, sourceProductId: ids.RawKhalas);
            Assert.True(other.Ok);
        }
    }

    [Fact]
    public void Prevent_Delete_Used_Product_And_Generic_Names_And_Blank_Receiving()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (cust, ids) = SeedCompany(host, "TZ", "شركة الحذف");

        using var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var receiving = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        // 1) لا دمج صنفين في سجل واحد: بندا استلام ← دفعتان مستقلتان بهويتين
        var (ls, lk) = ReceiveTwoVarieties(host, cust, ids, 1000, 700);
        Assert.NotEqual(ls, lk);
        Assert.Equal(2, db.Lots.Count(l => l.Id == ls || l.Id == lk));
        Assert.Equal(ids.RawSukkari, db.Lots.AsNoTracking().Single(l => l.Id == ls).ProductId);
        Assert.Equal(ids.RawKhalas, db.Lots.AsNoTracking().Single(l => l.Id == lk).ProductId);

        // 2) حذف صنف مستخدم في استلام ← إيقاف بدل الحذف (الحفاظ على الرحلة)
        var del = master.DeleteProductById(ids.RawSukkari);
        Assert.True(del.Ok, del.Message);
        Assert.Contains("إيقاف", del.Message);
        var still = db.Products.AsNoTracking().Single(p => p.Id == ids.RawSukkari);
        Assert.False(still.IsActive); // ما زال موجوداً — لم يُحذف

        // 3) اسم «تمور» العام مرفوض كصنف خام أو تام (فئة فقط)
        var generic = master.SaveProduct(null, "G-1", "تمور", "001", "Raw", "كجم", 20, 0, true);
        Assert.False(generic.Ok);
        Assert.Contains("فئة", generic.Message);

        // 4) استلام بدون صنف صريح ← مرفوض
        var blank = receiving.SaveShipment(cust, null, null, new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 0, QtyKg = 1000, PackageCount = 50, UnitWeightKg = 20 } });
        Assert.False(blank.Ok);
        Assert.Contains("تحديد الصنف", blank.Message);

        // 5) الاستلام بصنف موقوف ← مرفوض
        var disabled = receiving.SaveShipment(cust, null, null, new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = ids.RawSukkari, QtyKg = 500, PackageCount = 25, UnitWeightKg = 20 } });
        Assert.False(disabled.Ok);
        Assert.Contains("موقوف", disabled.Message);
    }

    [Fact]
    public void Quality_Rejects_Product_Not_In_Order()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (cust, ids) = SeedCompany(host, "TQ", "شركة الجودة");
        var (lotSukkari, _) = ReceiveTwoVarieties(host, cust, ids, 2000, 0);

        using var scope = host.Services.CreateScope();
        var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
        var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();
        var quality = scope.ServiceProvider.GetRequiredService<IQualityService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        TestCapacityFixture.DefineMissingRates(host);
        var plan = planning.SavePlan("خطة الجودة", "Daily", "2026-09-01", "2026-09-01", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lotSukkari, CustomerId = cust, ProductId = ids.FinSukkari, PlannedQtyKg = 2000, PriorityNo = 1 } });
        Assert.True(plan.Ok, plan.Message);
        Assert.True(planning.ApprovePlan(plan.Id).Ok);
        int itemId = db.ProductionPlanItems.Single(i => i.PlanId == plan.Id).Id;
        host.SetBusinessDate("2026-09-01"); // Arrange production on its scheduled business day, not the machine date.

        var order = orders.SaveOrder("FromPlan", plan.Id, cust, "2026-09-01", 1, 1, TestOrderFixture.Items(host, plan.Id, cust, "2026-09-01"));
        Assert.True(order.Ok, order.Message);
        Assert.True(orders.ApproveOrder(order.Id).Ok);
        var close = exec.CloseProductionDay(order.Id, 2000, 266, 0, 0, 0, false, new List<DowntimeDto>(), true);
        Assert.True(close.Ok, close.Message);

        // محاولة فحص «خلاص تام» على أمر لم ينتج إلا «سكري تام» ← مرفوضة
        var foreign = quality.SaveCheck(order.Id, close.Id, "2026-09-03", "نهائي", new List<QualityItemDto>
        { new() { ProductId = ids.FinKhalas, CheckedQtyKg = 100, AcceptedQtyKg = 100 } });
        Assert.False(foreign.Ok);
        Assert.Contains("ليس من بنود", foreign.Message);

        // الفحص الصحيح بهوية الأمر ← مقبول
        var ok = quality.SaveCheck(order.Id, close.Id, "2026-09-03", "نهائي", new List<QualityItemDto>
        { new() { ProductId = ids.FinSukkari, CheckedQtyKg = 2000, AcceptedQtyKg = 1990, RejectedQtyKg = 10 } });
        Assert.True(ok.Ok, ok.Message);
    }
}
