using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B74 — شاشة الأصناف مصدر Master Data رسمي واحد، والربط بالـ ProductId في كل النظام.
/// سيناريو الاختبار الإلزامي: إضافة ← ظهور في المناسب دون غير المناسب ← تعديل اسم
/// ← انتشار الاسم ← تعطيل ≠ حذف ← منع حذف المستخدم ← دورة كاملة بـ ProductId موحد.
/// </summary>
public class MasterLinkageTests
{
    private static MasterDataService Master(TestHost h)
        => h.Services.CreateScope().ServiceProvider.GetRequiredService<MasterDataService>();
    private static DatesErpDbContext Db(TestHost h)
        => new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(h.Connection).Options);

    [Fact]
    public void New_Raw_Item_Shows_In_Receiving_List_Only()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var r = Master(host).SaveProductFull(null, "001-500", "خام ربط جديد", "001", "Raw", "كجم", 0, 0, 0, null);
        Assert.True(r.Ok, r.Message);

        Assert.Contains(Master(host).GetRawItems(), p => p.Id == r.Id);          // الاستلام
        Assert.DoesNotContain(Master(host).GetFinishedItems(), p => p.Id == r.Id);
        Assert.DoesNotContain(Master(host).GetDeliverableItems(), p => p.Id == r.Id); // لا يسلم خام
    }

    [Fact]
    public void New_Finished_Item_Shows_In_Production_And_Delivery_Not_In_Receiving()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var r = Master(host).SaveProductFull(null, "002-500", "تام ربط جديد", "002", "Finished", "كرتون", 10, 5, 2, null);
        Assert.True(r.Ok, r.Message);

        Assert.DoesNotContain(Master(host).GetRawItems(), p => p.Id == r.Id);
        Assert.Contains(Master(host).GetFinishedItems(), p => p.Id == r.Id);     // الإنتاج/الخطط
        Assert.Contains(Master(host).GetDeliverableItems(), p => p.Id == r.Id);  // التسليم
    }

    [Fact]
    public void Renaming_In_Master_Propagates_To_Live_Reads_Everywhere()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var scope = host.Services.CreateScope();
        var master = Master(host);
        var rcv = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var cust = master.SaveCustomer(null, "R1", "عميل الربط", "جملة", "1", "-", true);
        var item = master.SaveProductFull(null, "001-501", "خام قبل التعديل", "001", "Raw", "كجم", 0, 0, 0, null);
        var sh = rcv.SaveShipment(cust.Id, null, null, new System.Collections.Generic.List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = item.Id, QtyKg = 1000, PackageCount = 50, UnitWeightKg = 20 } });
        rcv.ApproveShipment(sh.Id);

        // تعديل الاسم من شاشة الأصناف فقط
        Assert.True(master.SaveProductFull(item.Id, "001-501", "خام بعد التعديل", "001", "Raw", "كجم", 0, 0, 0, null).Ok);

        using var db = Db(host);
        // الاستلام/الدفعات تقرأ الاسم حياً من Products بالـ ProductId
        var nameInLots = db.Lots.AsNoTracking().Where(l => l.ProductId == item.Id)
            .Select(l => db.Products.Where(p => p.Id == l.ProductId).Select(p => p.ProductNameAr).FirstOrDefault()).First();
        Assert.Equal("خام بعد التعديل", nameInLots);
    }

    [Fact]
    public void Disabling_Hides_From_New_Lists_But_History_Remains()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var scope = host.Services.CreateScope();
        var master = Master(host);
        var rcv = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var cust = master.SaveCustomer(null, "R2", "عميل التعطيل", "جملة", "2", "-", true);
        var item = master.SaveProductFull(null, "001-502", "خام التعطيل", "001", "Raw", "كجم", 0, 0, 0, null);
        var sh = rcv.SaveShipment(cust.Id, null, null, new System.Collections.Generic.List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = item.Id, QtyKg = 800, PackageCount = 40, UnitWeightKg = 20 } });
        rcv.ApproveShipment(sh.Id);

        Assert.True(master.DeleteProductById(item.Id).Ok);   // مستخدم ⇒ تعطيل لا حذف
        using (var db = Db(host))
        {
            var p = db.Products.Single(x => x.Id == item.Id);
            Assert.False(p.IsActive);                        // إيقاف ≠ حذف
        }
        Assert.DoesNotContain(Master(host).GetRawItems(), p => p.Id == item.Id);  // لا قوائم جديدة
        using var db2 = Db(host);
        Assert.Contains(db2.ShipmentItems.AsNoTracking().ToList(), si => si.ProductId == item.Id); // التاريخ باقٍ
    }

    [Fact]
    public void Deleting_Free_Item_Removes_It_Actually()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var item = Master(host).SaveProductFull(null, "001-503", "خام حر", "001", "Raw", "كجم", 0, 0, 0, null);
        Assert.True(Master(host).DeleteProductById(item.Id).Ok);
        using var db = Db(host);
        Assert.DoesNotContain(db.Products.AsNoTracking().ToList(), p => p.Id == item.Id);
    }

    [Fact]
    public void Full_Cycle_Shares_One_ProductId()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var scope = host.Services.CreateScope();
        var master = Master(host);
        var rcv = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var plan = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
        var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();
        var fg = scope.ServiceProvider.GetRequiredService<IFinishedGoodsService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var cust = master.SaveCustomer(null, "R3", "عميل الدورة", "جملة", "3", "-", true);
        var raw = master.SaveProductFull(null, "001-504", "خام الدورة", "001", "Raw", "كجم", 0, 0, 0, null);
        var fin = master.SaveProductFull(null, "002-504", "تام الدورة", "002", "Finished", "كرتون", 10, 5, 2, null, raw.Id);
        var sh = rcv.SaveShipment(cust.Id, null, null, new System.Collections.Generic.List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = raw.Id, QtyKg = 20000, PackageCount = 1000, UnitWeightKg = 20 } });
        rcv.ApproveShipment(sh.Id);
        var lot = db.Lots.Single(l => l.ShipmentId == sh.Id);
        Assert.Equal(raw.Id, lot.ProductId);                                  // الاستلام

        TestCapacityFixture.DefineMissingRates(host);
        var pl = plan.SavePlan("ربط", "Daily", "2026-09-01", "2026-09-01", 1, 1,
            new System.Collections.Generic.List<PlanItemDto>
            { new() { SourceType = "FromReceiving", LotId = lot.Id, CustomerId = cust.Id, ProductId = fin.Id, PlannedCartons = 500, PlannedQtyKg = 5000 } });
        Assert.True(pl.Ok, pl.Message);
        Assert.True(plan.ApprovePlan(pl.Id).Ok);                              // اعتماد الخطة
        var planItem = db.ProductionPlanItems.Single(i => i.PlanId == pl.Id);
        Assert.Equal(fin.Id, planItem.ProductId);                             // الخطة
        host.SetBusinessDate("2026-09-01"); // Arrange production on its scheduled business day, not the machine date.

        var or = orders.SaveOrder("FromPlan", pl.Id, cust.Id, "2026-09-01", 1, 1, TestOrderFixture.Items(host, pl.Id, cust.Id, "2026-09-01"));
        Assert.True(or.Ok, or.Message);
        var orderItem = db.ProductionOrderItems.AsNoTracking().First(i => i.OrderId == or.Id);
        Assert.Equal(fin.Id, orderItem.ProductId);                            // أمر التشغيل

        Assert.True(orders.ApproveOrder(or.Id).Ok);                           // اعتماد الأمر
        var ex = orders.StartOrder(or.Id);
        Assert.True(ex.Ok, ex.Message);
        var cl = exec.CloseProductionDay(or.Id, 5000, 500, 0, 0, 0, false,
            new System.Collections.Generic.List<DowntimeDto>(), true, null);
        Assert.True(cl.Ok, cl.Message);
        var closedItem = db.ProductionOrderItems.AsNoTracking().First(i => i.OrderId == or.Id && i.ProductId == fin.Id);
        Assert.Equal(5000, closedItem.ProducedQtyKg, 1);                      // الإقفال كتب على نفس المعرف

        var fgr = TestProductionDocumentFlow.SaveReceiptFromActual(host, or.Id, null, "2026-09-01", new System.Collections.Generic.List<FinishedGoodsItemDto>
        { new() { ProductId = fin.Id, LotId = lot.Id, PackageCount = 100, NetWeightKg = 1000 } });
        Assert.True(fgr.Ok, fgr.Message);
        var fgItem = db.FinishedGoodsReceiptItems.AsNoTracking().First(i => i.ReceiptId == fgr.Id);
        Assert.Equal(fin.Id, fgItem.ProductId);                               // التسليم/التام

        // المخزون: حركة واحدة على الأقل بنفس المعرف
        Assert.Contains(db.InventoryTransactions.AsNoTracking().ToList(), t => t.ProductId == fin.Id || t.ProductId == raw.Id);
    }
}
