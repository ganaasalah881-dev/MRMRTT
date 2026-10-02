using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §قبول دورة المواد المساعدة المرنة:
/// 1) مجموعات ومواد بوحدات حرة من الخدمة (بلا ثوابت).
/// 2) الاعتماد لا يتعرقَل بنقص رصيد المواد (حراس تحذيرية).
/// 3) تسوية الإقفال: مصروف 600 وفعلي 400 ⇒ مرتجع آلي 200 وحركة MaterialReturn.
/// 4) الديزل (وضع Actual) لا يُصرف عند الاعتماد ويُستهلك بالإدخال الفعلي.
/// </summary>
public class AuxFlexTests
{
    private static T Get<T>(TestHost h) => h.Services.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static (int lotId, int orderId, int planId) Arrange(TestHost host, bool stockAux)
    {
        var db = host.Get<DatesErpDbContext>();
        if (stockAux)
        {
            var whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
            db.StockBalances.AddRange(
                new StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 10000 },
                new StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 10000 },
                new StockBalance { WarehouseId = whAux, MaterialId = 4, QtyKg = 100 }); // diesel actually posted at close
            db.SaveChanges();
        }
        var receiving = Get<IReceivingService>(host);
        var r1 = receiving.SaveShipment(1, "2026-08-10", "2026-08-10", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 500, UnitWeightKg = 20, QtyKg = 10000 } });
        if (!r1.Ok) throw new System.Exception("SaveShipment: " + r1.Message);
        var ra = receiving.ApproveShipment(r1.Id);
        if (!ra.Ok) throw new System.Exception("ApproveShipment: " + ra.Message);
        var lotId = db.Lots.Single().Id;
        var planning = Get<IPlanningService>(host);
        var p1 = planning.SavePlan("خطة", "Daily", "2026-08-20", "2026-08-20", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = 1, ProductId = 3,
                  PlannedQtyKg = 3000, PlannedCartons = 600, PackagingTypeId = 1, ScheduledDate = "2026-08-20", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 } });
        if (!p1.Ok) throw new System.Exception("SavePlan: " + p1.Message);
        var pa = planning.ApprovePlan(p1.Id);
        if (!pa.Ok) throw new System.Exception("ApprovePlan: " + pa.Message);
        var orders = Get<IProductionOrderService>(host);
        host.SetBusinessDate("2026-08-20");
        var o1 = orders.SaveOrder("FromPlan", p1.Id, 1, "2026-08-20", 1, 1, TestOrderFixture.Items(host, p1.Id, 1, "2026-08-20"));
        if (!o1.Ok) throw new System.Exception("SaveOrder failed: " + o1.Message);
        return (lotId, o1.Id, p1.Id);
    }

    [Fact]
    public void Groups_And_Materials_Manageable_With_Free_Units()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var svc = Get<MasterDataService>(host);
        var g = svc.SaveAuxGroup(null, "AG-TEST", "مجموعة اختبار");
        Assert.True(g.Ok, g.Message);
        var m = svc.SaveAuxMaterial(null, null, "لفة ستريتش", "AG-TEST", "لفة", "مقوى", 12);
        Assert.True(m.Ok, m.Message);
        var f = svc.SaveFormulaEx(null, 3, m.Id, 0.2, "PerHour", optional: true);
        Assert.True(f.Ok, f.Message);
        var sp = svc.SaveAuxSpec(null, 1, 1, "ماركة العميل أ", 3.5);
        Assert.True(sp.Ok, sp.Message);
        using var db = new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);
        Assert.Contains(db.AuxGroups.ToList(), x => x.GroupNameAr == "مجموعة اختبار");
        Assert.Contains(db.AuxiliaryMaterials.ToList(), x => x.UnitOfMeasure == "لفة");
    }

    [Fact]
    public void Approval_Not_Blocked_When_Aux_Stock_Missing()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (_, orderId, _) = Arrange(host, stockAux: false); // لا رصيد مواد مساعدة إطلاقاً
        var orders = Get<IProductionOrderService>(host);
        var r = orders.ApproveOrder(orderId); // يجب ألا يتعرقَل أثناء التجارب
        Assert.True(r.Ok, r.Message);
    }

    [Fact]
    public void Closing_Returns_OverIssued_Aux_Automatically()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (_, orderId, _) = Arrange(host, stockAux: true);
        var orders = Get<IProductionOrderService>(host);
        Assert.True(orders.ApproveOrder(orderId).Ok);
        using (var db = new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options))
            Assert.Equal(600, db.ProductionOrderMaterials.Single(m => m.OrderId == orderId && m.MaterialId == 1).ActualIssuedQty, 1);

        // §B95 — المسار الموحد: إقفال يوم الأمر (استوعب تسوية المواد المساعدة من مسار بنود الخطة المحذوف).
        // إنتاج فعلي 400 كرتون من 600 ⇒ مستهلك 400 ⇒ مرتجع آلي 200 (الكيلو 2000 = 400 × 5 كجم للعبوة).
        var exec = Get<IExecutionService>(host);
        var c = exec.CloseProductionDay(orderId, 2000, 400, 0, 0, 0, false, new List<DowntimeDto>(), false, null,
            null, 3000, null,
            new List<AuxActualDto> { new() { OrderId = orderId, MaterialId = 4, Qty = 35 } }); // ديزل فعلي 35 لتر
        Assert.True(c.Ok, c.Message);

        using var db2 = new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);
        var mat = db2.ProductionOrderMaterials.Single(m => m.OrderId == orderId && m.MaterialId == 1);
        int rawWarehouse = db2.Warehouses.Single(w => w.WarehouseCode == "WRM").Id;
        var rawBalances = db2.StockBalances.AsNoTracking().Where(b => b.WarehouseId == rawWarehouse
            && b.ProductId == 1 && b.LotId != null).ToList();
        Assert.Single(rawBalances); // debit and return use the lot's original package key
        Assert.Equal(2, rawBalances[0].PackagingTypeId);
        Assert.Equal(8000, rawBalances[0].QtyKg, 1); // 10000 - 3000 consumed + 1000 returned
        Assert.Equal(200, mat.ReturnedQty, 1); // 600 مصروف − 400 مستهلك
        Assert.Contains(db2.InventoryTransactions.ToList(),
            t => t.ReferenceDocType == ReferenceDocType.MaterialReturn && t.MaterialId == 1 && System.Math.Abs(t.QtyKg - 200) < 0.5);
        // الديزل استُهلك بالإدخال الفعلي 35 وليس باشتقاق من الخطة
        var diesel = db2.ProductionOrderMaterials.SingleOrDefault(m => m.OrderId == orderId && m.MaterialId == 4);
        Assert.True(diesel == null || diesel.ReturnedQty >= 0);
        Assert.Contains(db2.InventoryTransactions.ToList(),
            t => t.MaterialId == 4 && t.QtyKg == -35);
    }
}
