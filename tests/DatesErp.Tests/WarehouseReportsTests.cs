using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §تقارير المخزون الجديدة: حركة المخازن، حركة الصنف برصيد جارٍ على مستوى المخزن،
/// وتقرير المخزن الشامل — مع أزرار التنقل (+) إلى المستندات الأصلية.
/// </summary>
public class WarehouseReportsTests
{
    private sealed record Env(int Customer, int RawSuk, int RawKha, int FinSuk, int FinKha, int LotSuk, int LotKha);

    /// <summary>دورة: استلام ← خطة ← أمر واعتماد (صرف خام) ← إقفال ← فحص ← استلام تام ← تسليم.</summary>
    private static Env BuildCycle(TestHost host)
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

        var c = master.SaveCustomer(null, "TWH", "شركة المخازن", "جملة", "777", "-", true);
        var rs = master.SaveProductFull(null, "WH-R1", "سكري", "001", "Raw", "كجم", 20, 0, 0, null);
        var rk = master.SaveProductFull(null, "WH-R2", "خلاص", "001", "Raw", "كجم", 20, 0, 0, null);
        var fs = master.SaveProductFull(null, "WH-F1", "سكري تام", "002", "Finished", "كرتون", 7.5, 1, 0.5, null, sourceProductId: rs.Id);
        var fk = master.SaveProductFull(null, "WH-F2", "خلاص تام", "002", "Finished", "كرتون", 7.5, 1, 0.5, null, sourceProductId: rk.Id);
        Assert.True(c.Ok && rs.Ok && rk.Ok && fs.Ok && fk.Ok);

        var s = receiving.SaveShipment(c.Id, null, null, new List<ShipmentItemDto>
        {
            new() { TreatmentRequired = false, ProductId = rs.Id, QtyKg = 10000, PackageCount = 500, UnitWeightKg = 20 },
            new() { TreatmentRequired = false, ProductId = rk.Id, QtyKg = 8000, PackageCount = 400, UnitWeightKg = 20 }
        });
        Assert.True(s.Ok && receiving.ApproveShipment(s.Id).Ok);
        int lotSuk = db.Lots.Single(l => l.ShipmentId == s.Id && l.ProductId == rs.Id).Id;
        int lotKha = db.Lots.Single(l => l.ShipmentId == s.Id && l.ProductId == rk.Id).Id;

        string day = DateTime.Today.AddDays(1).ToString("dd/MM/yyyy");
        TestCapacityFixture.DefineMissingRates(host);
        var plan = planning.SavePlan("خطة المخازن", "Daily", day, day, 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lotSuk, CustomerId = c.Id, ProductId = fs.Id, PlannedQtyKg = 7500, PriorityNo = 1 },
            new() { SourceType = "FromReceiving", LotId = lotKha, CustomerId = c.Id, ProductId = fk.Id, PlannedQtyKg = 6000, PriorityNo = 2 }
        });
        Assert.True(plan.Ok, plan.Message);
        Assert.True(planning.ApprovePlan(plan.Id).Ok);
        var itemIds = db.ProductionPlanItems.Where(i => i.PlanId == plan.Id).OrderBy(i => i.PriorityNo).Select(i => i.Id).ToList();

        host.SetBusinessDate(day);
        var order = orders.SaveOrder("FromPlan", plan.Id, c.Id, day, 1, 1, TestOrderFixture.Items(host, plan.Id, c.Id, day));
        Assert.True(order.Ok, order.Message);
        Assert.True(orders.ApproveOrder(order.Id).Ok); // صرف الخام: حركتا منصرف من مخزن الخام

        var close = exec.CloseProductionDay(order.Id, 13500, 1800, 0, 0, 0, false, new List<DowntimeDto>(), sendToQuality: true);
        Assert.True(close.Ok, close.Message);
        int execId = db.ProductionExecutions.Single(e => e.OrderId == order.Id).Id;

        var qc = quality.SaveCheck(order.Id, execId, day, "نهائي", new List<QualityItemDto>
        {
            new() { ProductId = fs.Id, AcceptedQtyKg = 7300, RejectedQtyKg = 200 },
            new() { ProductId = fk.Id, AcceptedQtyKg = 5800, RejectedQtyKg = 200 }
        });
        Assert.True(qc.Ok, qc.Message);
        Assert.True(quality.ApproveCheck(qc.Id).Ok);

        var rcpt = TestProductionDocumentFlow.SaveReceiptFromActual(host, order.Id, qc.Id, day, new List<FinishedGoodsItemDto>
        {
            new() { ProductId = fs.Id, LotId = lotSuk, NetWeightKg = 7300, PackageCount = 973 },
            new() { ProductId = fk.Id, LotId = lotKha, NetWeightKg = 5800, PackageCount = 773 }
        });
        Assert.True(rcpt.Ok, rcpt.Message);
        Assert.True(fg.Issue(rcpt.Id).Ok);
        Assert.True(fg.Receive(rcpt.Id, null).Ok); // حركتا وارد إلى مخزن التام

        var d = dlv.Save(c.Id, day, order.Id, new List<CustomerDeliveryItemDto>
        {
            new() { ProductId = fs.Id, LotId = lotSuk, QtyKg = 1000 },
            new() { ProductId = fk.Id, LotId = lotKha, QtyKg = 500 }
        });
        Assert.True(d.Ok, d.Message);
        Assert.True(dlv.Approve(d.Id).Ok); // حركتا منصرف من مخزن التام

        return new Env(c.Id, rs.Id, rk.Id, fs.Id, fk.Id, lotSuk, lotKha);
    }

    private static ReportResult Run(TestHost host, string code, Dictionary<string, string> p = null)
    {
        using var scope = host.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IReportService>();
        var r = svc.Run(code, p ?? new Dictionary<string, string>());
        Assert.NotNull(r);
        return r;
    }

    [Fact]
    public void Warehouse_Movements_With_Filters_And_DrillLinks()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        BuildCycle(host);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        int wrm = db.Warehouses.Single(w => w.WarehouseCode == "WRM").Id;

        // كل الحركات
        var all = Run(host, "warehouse_movements");
        Assert.True(all.RowLinks != null && all.RowLinks.Count == all.Rows.Count);
        Assert.Contains(all.Rows, row => row[2].ToString() == "⬆ وارد" && row[1].ToString().Contains("الخام"));
        Assert.Contains(all.Rows, row => row[2].ToString() == "⬇ منصرف" && row[1].ToString().Contains("التام"));

        // فلتر مخزن الخام: واردان (استلام الصنفين) + منصرفان (صرف الأمر)
        var rawOnly = Run(host, "warehouse_movements", new Dictionary<string, string> { ["warehouse"] = wrm.ToString() });
        Assert.Equal(4, rawOnly.Rows.Count);
        Assert.All(rawOnly.Rows, row => Assert.Contains("الخام", row[1].ToString()));

        // فلتر نوع الحركة: الوارد فقط
        var inOnly = Run(host, "warehouse_movements", new Dictionary<string, string> { ["mtype"] = "in" });
        Assert.All(inOnly.Rows, row => Assert.Equal("⬆ وارد", row[2].ToString()));

        // روابط التنقل: صف الاستلام الأول يفتح سند الاستلام
        int shipIdx = -1;
        for (int i = 0; i < all.Rows.Count; i++)
            if (all.Rows[i][11].ToString().Contains("ShipmentReceipt")) { shipIdx = i; break; }
        Assert.True(shipIdx >= 0);
        Assert.Equal("receiving", all.RowLinks[shipIdx].DocType);
    }

    [Fact]
    public void Item_Movements_Ledger_Shows_Running_Balance_Per_Warehouse()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var env = BuildCycle(host);

        // الخام: وارد 10,000 ثم منصرف 7,500 ← رصيد 2,500
        var raw = Run(host, "item_movements", new Dictionary<string, string> { ["product"] = env.RawSuk.ToString() });
        Assert.Equal(2, raw.Rows.Count);
        Assert.Equal("+10,000.0", raw.Rows[0][4].ToString());
        Assert.Equal("10,000.0", raw.Rows[0][6].ToString());
        Assert.Equal("−7,500.0", raw.Rows[1][4].ToString());
        Assert.Equal("2,500.0", raw.Rows[1][6].ToString());
        Assert.Contains("2,500", raw.Summary["الرصيد النهائي (كجم)"]);

        // التام: وارد 7,300 ثم تسليم 1,000 ← رصيد 6,300
        var fin = Run(host, "item_movements", new Dictionary<string, string> { ["product"] = env.FinSuk.ToString() });
        Assert.Equal(2, fin.Rows.Count);
        Assert.Equal("7,300.0", fin.Rows[0][6].ToString());
        Assert.Equal("6,300.0", fin.Rows[1][6].ToString());

        // على مستوى مخزن محدد (الخام) — حركة التام لا تظهر
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        int wrm = db.Warehouses.Single(w => w.WarehouseCode == "WRM").Id;
        var finInWrm = Run(host, "item_movements", new Dictionary<string, string> { ["product"] = env.FinSuk.ToString(), ["warehouse"] = wrm.ToString() });
        Assert.Empty(finInWrm.Rows);

        // بدون اختيار صنف: تنبيه واضح
        var empty = Run(host, "item_movements");
        Assert.Empty(empty.Rows);
        Assert.Contains("اختر صنفاً", empty.Summary["تنبيه"]);
    }

    [Fact]
    public void Warehouse_Full_Shows_Current_Balances_By_Item_Lot_Customer()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var env = BuildCycle(host);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        int wfg = db.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;

        var r = Run(host, "warehouse_full", new Dictionary<string, string> { ["warehouse"] = wfg.ToString() });
        Assert.Equal(2, r.Rows.Count); // سكري تام + خلاص تام
        Assert.All(r.Rows, row => Assert.Contains("التام", row[0].ToString()));
        Assert.All(r.Rows, row => Assert.Equal("شركة المخازن", row[4].ToString())); // الملكية لكل عميل
        // إجمالي مخزن التام: 6,300 + 5,300
        string key = r.Summary.Keys.First(k => k.Contains("التام"));
        Assert.Contains("11,600", r.Summary[key]);

        // فلتر الصنف: سكري تام فقط
        var suk = Run(host, "warehouse_full", new Dictionary<string, string> { ["product"] = env.FinSuk.ToString() });
        Assert.Single(suk.Rows);
        Assert.Equal("6,300.0", suk.Rows[0][5].ToString());
        Assert.Equal("6,300.0", suk.Rows[0][9].ToString()); // مخزن التام لا يرث حجز الدفعة الخام
    }
}
