using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// فحص تدقيقي شامل لدورة العمل وسلامة الأرصدة (§9):
/// بعد كل عملية: رصيد كل مخزن = مجموع حركاته (وزناً وعبوات)، والخام والتام ينخفضان بالعمليات،
/// والإلغاءات تعكس بدقة، والأرصدة السالبة مستحيلة، والازدواج مرفوض بلا أثر.
/// </summary>
public class InventoryLedgerAuditTests
{
    private static T Svc<T>(TestHost host) => (T)host.Services.CreateScope().ServiceProvider.GetService(typeof(T));

    private static DatesErpDbContext FreshDb(TestHost host)
        => new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);

    /// <summary>تدقيق سلامة الدفتر: كل رصيد = مجموع حركاته المرجعية (وزناً وعبوات).</summary>
    private static void AssertLedgerConsistent(TestHost host)
    {
        using var db = FreshDb(host);
        var balances = db.StockBalances.ToList();
        var txns = db.InventoryTransactions.ToList();
        foreach (var b in balances)
        {
            double sumKg = txns.Where(t => t.WarehouseId == b.WarehouseId && t.ProductId == b.ProductId
                && t.MaterialId == b.MaterialId && t.LotId == b.LotId && t.CustomerId == b.CustomerId)
                .Sum(t => t.QtyKg);
            Assert.True(Math.Abs(sumKg - b.QtyKg) < 0.01,
                $"خلل دفتر: رصيد المخزن {b.WarehouseId} صنف {b.ProductId ?? b.MaterialId} = {b.QtyKg} لكن مجموع الحركات = {sumKg}");
            int sumPkg = txns.Where(t => t.WarehouseId == b.WarehouseId && t.ProductId == b.ProductId
                && t.MaterialId == b.MaterialId && t.LotId == b.LotId && t.CustomerId == b.CustomerId)
                .Sum(t => t.PackageCount);
            Assert.True(sumPkg == b.PackageCount,
                $"خلل عبوات: المخزن {b.WarehouseId} صنف {b.ProductId ?? b.MaterialId} = {b.PackageCount} عبوة لكن الحركات = {sumPkg}");
        }
        // لا حركة بلا مستند
        Assert.DoesNotContain(txns, t => string.IsNullOrEmpty(t.ReferenceDocNumber));
    }

    private static (double wrm, double wfg, double waux) WarehouseTotals(TestHost host)
    {
        using var db = FreshDb(host);
        return (
            db.StockBalances.Where(b => b.WarehouseId == 1).Sum(b => b.QtyKg),
            db.StockBalances.Where(b => b.WarehouseId == 2).Sum(b => b.QtyKg),
            db.StockBalances.Where(b => b.WarehouseId == 3).Sum(b => b.QtyKg));
    }

    [Fact]
    public void Full_Cycle_Ledger_Audit_Raw_And_Finished_Decrease_Correctly()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using (var db = FreshDb(host))
        {
            var whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
            // أرصدة افتتاحية موثقة بحركة (لا رصيد بلا مستند — §9)
            foreach (var matId in new[] { 1, 2 })
            {
                db.StockBalances.Add(new Core.Domain.Entities.StockBalance
                { WarehouseId = whAux, MaterialId = matId, QtyKg = 20000, RowVersion = Guid.NewGuid().ToByteArray() });
                db.InventoryTransactions.Add(new Core.Domain.Entities.InventoryTransaction
                {
                    TxnNumber = $"OPEN-{matId}", TxnDate = DateTime.Now, WarehouseId = whAux, MaterialId = matId,
                    MovementType = Core.Domain.Enums.MovementType.Inbound, QtyKg = 20000,
                    ReferenceDocType = Core.Domain.Enums.ReferenceDocType.Adjustment,
                    ReferenceDocNumber = "OPENING-BALANCE", IsApproved = true,
                    RowVersion = Guid.NewGuid().ToByteArray()
                });
            }
            db.SaveChanges();
        }

        // ── المرحلة 1: الاستلام يزيد مخزن الخام (وزناً وعبوات) ──
        var receiving = Svc<IReceivingService>(host);
        var s1 = receiving.SaveShipment(1, "2026-08-10", "2026-08-10",
            new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 400, UnitWeightKg = 20, QtyKg = 8000 } });
        Assert.True(s1.Ok, s1.Message);
        var (wrm0, wfg0, waux0) = WarehouseTotals(host);
        Assert.Equal(0, wrm0, 1); // قبل الاعتماد: لا أثر
        Assert.True(receiving.ApproveShipment(s1.Id).Ok);
        var (wrm1, _, _) = WarehouseTotals(host);
        Assert.Equal(8000, wrm1, 1); // الخام زاد
        using (var db = FreshDb(host))
        {
            var bal = db.StockBalances.Single(b => b.WarehouseId == 1);
            Assert.Equal(400, bal.PackageCount); // العبوات سُجلت
        }
        AssertLedgerConsistent(host);

        // ── المرحلة 2: الخطة لا تحرك الأرصدة (حجز منطقي فقط) ──
        var planning = Svc<IPlanningService>(host);
        int lotId;
        using (var db = FreshDb(host)) lotId = db.Lots.Single().Id;
        var plan = planning.SavePlan("خطة تدقيق", "Daily", "2026-08-20", "2026-08-20", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3, PlannedQtyKg = 5000, PlannedCartons = 667, ScheduledDate = "2026-08-20", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 } });
        Assert.True(plan.Ok, plan.Message);
        Assert.True(planning.ApprovePlan(plan.Id).Ok);
        var (wrm2, _, _) = WarehouseTotals(host);
        Assert.Equal(8000, wrm2, 1); // الخطة لم تمس المخزون
        AssertLedgerConsistent(host);

        // ── المرحلة 3: اعتماد الأمر يُنقص المواد المساعدة — أما الخام فيُصرف عند الإقفال ──
        var orders = Svc<IProductionOrderService>(host);
        host.SetBusinessDate("2026-08-20"); // Arrange production on its scheduled business day, not the machine date.
        var o = orders.SaveOrder("FromPlan", plan.Id, 1, "2026-08-20", 1, 1, TestOrderFixture.Items(host, plan.Id, 1, "2026-08-20"));
        Assert.True(o.Ok, o.Message);
        var wauxBefore = WarehouseTotals(host).waux;
        Assert.True(orders.ApproveOrder(o.Id).Ok);
        var (wrm3, _, waux3) = WarehouseTotals(host);
        // §قاعدة توازن الإنتاج: لا يُخصم الخام عند الاعتماد — يُصرف عند الإقفال فعلياً
        Assert.Equal(8000, wrm3, 1);
        Assert.True(waux3 < wauxBefore - 0.01);   // المواد المساعدة نقصت
        using (var db = FreshDb(host))
        {
            var lot = db.Lots.Single(l => l.Id == lotId);
            Assert.Equal(8000, lot.InStockQtyKg, 1);
        }
        AssertLedgerConsistent(host);

        // محاولة اعتماد ثانٍ → مرفوضة وبلا أثر
        var dup = orders.ApproveOrder(o.Id);
        Assert.False(dup.Ok);
        Assert.Equal(8000, WarehouseTotals(host).wrm, 1);
        AssertLedgerConsistent(host);

        // ── المرحلة 4: الإقفال يصرف الخام فعلياً · والجودة لا تحرك المخزون ──
        var exec = Svc<IExecutionService>(host);
        Svc<IProductionOrderService>(host).StartOrder(o.Id);
        var ec = exec.CloseProductionDay(o.Id, 5000, 666, 0, 0, 0, false, new List<DowntimeDto>(), false, null, null, 5000);
        Assert.True(ec.Ok, ec.Message);
        int e1Id = Svc<DatesErpDbContext>(host).ProductionExecutions.Single(x => x.OrderId == o.Id).Id;
        var quality = Svc<IQualityService>(host);
        var q = quality.SaveCheck(o.Id, e1Id, "2026-08-20", "نهائي",
            new List<QualityItemDto> { new() { ProductId = 3, LotId = lotId, AcceptedQtyKg = 5000, RejectedQtyKg = 0 } },
            new List<(int, double)> { (5, 20.0) });
        Assert.True(q.Ok, q.Message);
        Assert.True(quality.ApproveCheck(q.Id).Ok);
        var (wrm4, wfg4, _) = WarehouseTotals(host);
        Assert.Equal(3000, wrm4, 1);
        Assert.Equal(0, wfg4, 1); // لا تام قبل سند الاستلام
        AssertLedgerConsistent(host);

        // ── المرحلة 5: تسليم الإنتاج — الإصدار بلا أثر، الاستلام يزيد التام (وزناً وعبوات) ──
        var f = TestProductionDocumentFlow.SaveReceiptFromActual(host, o.Id, q.Id, "2026-08-20",
            new List<FinishedGoodsItemDto> { new() { ProductId = 3, LotId = lotId, PackageCount = 666, NetWeightKg = 5000 } });
        Assert.True(f.Ok, f.Message);
        Assert.True(Svc<IFinishedGoodsService>(host).Issue(f.Id).Ok);
        Assert.Equal(0, WarehouseTotals(host).wfg, 1); // الإصدار بلا أثر
        var received = Svc<IFinishedGoodsService>(host).Receive(f.Id, null); // explicit receive-all API
        Assert.True(received.Ok, received.Message);
        var (wrm5, wfg5, _) = WarehouseTotals(host);
        Assert.Equal(3000, wrm5, 1);
        Assert.Equal(5000, wfg5, 1); // التام زاد
        using (var db = FreshDb(host))
        {
            var bal = db.StockBalances.Single(b => b.WarehouseId == 2 && b.CustomerId == 1);
            Assert.Equal(666, bal.PackageCount); // exactly the issued production cartons
        }
        AssertLedgerConsistent(host);

        // استلام مكرر بعد الاكتمال → مرفوض وبلا أثر
        var dupR = Svc<IFinishedGoodsService>(host).Receive(f.Id, new Dictionary<int, double>());
        Assert.False(dupR.Ok);
        Assert.Equal(5000, WarehouseTotals(host).wfg, 1);
        AssertLedgerConsistent(host);

        // ── المرحلة 6: تسليم العميل يُنقص التام ──
        var d = Svc<ICustomerDeliveryService>(host).Save(1, "2026-08-21", o.Id,
            new List<CustomerDeliveryItemDto> { new() { ProductId = 3, LotId = lotId, QtyKg = 2000, PackageCount = 266 } });
        Assert.True(d.Ok, d.Message);
        // قبل الاعتماد: لا أثر
        Assert.Equal(5000, WarehouseTotals(host).wfg, 1);
        Assert.True(Svc<ICustomerDeliveryService>(host).Approve(d.Id).Ok);
        var (_, wfg6, _) = WarehouseTotals(host);
        Assert.Equal(3000, wfg6, 1); // التام نقص 2000
        using (var db = FreshDb(host))
        {
            var lot = db.Lots.Single(l => l.Id == lotId);
            Assert.Equal(2000, lot.DeliveredQtyKg, 1);
        }
        AssertLedgerConsistent(host);

        // تسليم فوق الرصيد → مرفوض وبلا أثر على الدفتر
        var over = Svc<ICustomerDeliveryService>(host).Save(1, "2026-08-21", null,
            new List<CustomerDeliveryItemDto> { new() { ProductId = 3, LotId = lotId, QtyKg = 99999 } });
        var overAp = Svc<ICustomerDeliveryService>(host).Approve(over.Id);
        Assert.False(overAp.Ok);
        Assert.Equal(3000, WarehouseTotals(host).wfg, 1);
        AssertLedgerConsistent(host);

        // ── المرحلة 7: الإلغاءات تعكس بدقة ──
        host.LoginAsAdmin(); // cancellation belongs to authorized supervisor, not warehouse
        var un1 = Svc<ICustomerDeliveryService>(host).Unapprove(d.Id); Assert.True(un1.Ok, un1.Message);
        Assert.Equal(5000, WarehouseTotals(host).wfg, 1); // عاد التام
        AssertLedgerConsistent(host);

        var un2 = Svc<IFinishedGoodsService>(host).Unapprove(f.Id); Assert.True(un2.Ok, un2.Message);
        Assert.Equal(0, WarehouseTotals(host).wfg, 1); // عاد التام إلى صفر
        AssertLedgerConsistent(host);

        // §7 — حراس التتبع: لا إلغاء لأمر له تنفيذ، ولا إلغاء لاستلام استُهلكت دفعاته
        var un3 = orders.UnapproveOrder(o.Id);
        Assert.False(un3.Ok);
        Assert.Contains("تنفيذ", un3.Message);
        var un4 = Svc<IReceivingService>(host).UnapproveShipment(s1.Id); // نطاق جديد لكل إجراء (نمط الإنتاج)
        Assert.False(un4.Ok);
        // §v1.50.27: الحارس الأقوى (خطط مرجَعة) يسبق فحص الاستهلاك — كلاهما «لا يمكن إلغاء الاستلام»
        Assert.Contains("لا يمكن إلغاء اعتماد الاستلام", un4.Message); // §v1.50.27: حارس الخطط المرجَعة يسبق فحص الاستهلاك
        // الأرصدة تبقى صحيحة ومتسقة: سند استلام التام أُلغي (التام صفر)،
        // بينما أمر الإنتاج والاستلام محميان من الإلغاء (الخام المصروف يبقى خارجاً)
        var (wrmFin, wfgFin, wauxFin) = WarehouseTotals(host);
        Assert.Equal(8000 - 5000, wrmFin, 1);
        Assert.Equal(0, wfgFin, 1);
        using (var db = FreshDb(host))
        {
            var lot = db.Lots.Single(l => l.Id == lotId);
            Assert.Equal(3000, lot.InStockQtyKg, 1); // المتبقي في الدفعة بعد صرف الأمر (محمي من الإلغاء)
            Assert.Equal(5000, lot.ProducedQtyKg, 1);
        }
        AssertLedgerConsistent(host);

        AssertLedgerConsistent(host);
    }

    [Fact]
    public void Negative_Raw_Balance_Is_Impossible()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        // استلام 1000 كجم فقط
        var receiving = Svc<IReceivingService>(host);
        var s = receiving.SaveShipment(1, null, null,
            new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 50, UnitWeightKg = 20, QtyKg = 1000 } });
        Assert.True(s.Ok);
        Assert.True(receiving.ApproveShipment(s.Id).Ok);

        // §قاعدة توازن الإنتاج: التخطيط لا يُرفض لتجاوز رصيد الخام — فالمخطط مستهدف
        // إنتاجي لا حجز خام، ولا معادلة ثابتة تربطهما (وزن الخارج يزيد لإضافة الماء).
        // القيد الفيزيائي الحقيقي: لا يُصرف من الدفعة أكثر من رصيدها — ويُفحص عند الصرف الفعلي.
        var planning = Svc<IPlanningService>(host);
        int lotId;
        using (var db = FreshDb(host)) lotId = db.Lots.Single().Id;
        var p1 = planning.SavePlan("خطة 1", "Daily", "2026-09-01", "2026-09-01", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lotId, ProductId = 3, PlannedQtyKg = 800, ScheduledDate = "2026-09-01", PriorityNo = 1 } });
        Assert.True(p1.Ok, p1.Message);
        var p2 = planning.SavePlan("خطة 2", "Daily", "2026-09-02", "2026-09-02", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lotId, ProductId = 3, PlannedQtyKg = 500, ScheduledDate = "2026-09-02", PriorityNo = 1 } });
        Assert.True(p2.Ok, "التخطيط بأكثر من رصيد الخام مشروع: " + p2.Message);

        // الصرف الفعلي فوق الرصيد ما زال مستحيلاً
        using var scope = host.Services.CreateScope();
        var db2 = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var baseSvc = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
        // Even draft plans reserve the same lot: settle the conflicting draft
        // first; never disable the legitimate reservation guard just for this test.
        var unavailable = planning.ApprovePlan(p1.Id);
        Assert.False(unavailable.Ok);
        Assert.Contains("حجوزات", unavailable.Message);
        Assert.True(planning.DeletePlan(p2.Id).Ok);
        var approve = planning.ApprovePlan(p1.Id);
        Assert.True(approve.Ok, approve.Message);
        var planItemId = db2.ProductionPlanItems.AsNoTracking().First(i => i.PlanId == p1.Id).Id;
        host.SetBusinessDate("2026-09-01"); // Arrange production on its scheduled business day, not the machine date.
        var order = baseSvc.SaveOrder("FromPlan", p1.Id, 1, "2026-09-01", 1, 1, TestOrderFixture.Items(host, p1.Id, 1, "2026-09-01"));
        Assert.True(order.Ok, order.Message);
        Assert.True(baseSvc.ApproveOrder(order.Id).Ok);
        Assert.True(baseSvc.StartOrder(order.Id).Ok);
        var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();
        var over = exec.CloseProductionDay(order.Id, 800, 0, 0, 0, 0, false, new List<DowntimeDto>(), false, null, null, 1500);
        Assert.False(over.Ok);   // صرف 1,500 من رصيد 1,000 مستحيل
        AssertLedgerConsistent(host);
    }
}
