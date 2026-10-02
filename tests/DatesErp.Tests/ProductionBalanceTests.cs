using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §توازن الإنتاج — القاعدة المعتمدة من المصنع:
///
/// في تصنيع التمور يزيد وزن الخارج عن الداخل لإضافة الماء أثناء التشغيل، والماء
/// لا يُسجَّل صنفاً ولا مدخلاً مستقلاً. لذلك:
///   • لا معادلة ثابتة تفترض أن الخام = التام + المخرجات.
///   • لا رفض للعملية عند زيادة الوزن.
///   • لا إجبار للمستخدم على تغيير أرقامه لتصفير الفرق.
///   • الفرق ونسبة الانحراف يظهران في «تقرير توازن الإنتاج» إجراءً رقابياً.
///
/// والتمر السليم والتمر المنسم كلاهما منتج تام (002) قابل للبيع والتسليم —
/// والمنسم ليس مخرجاً ثانوياً؛ الفرق اسم/تصنيف تجاري فقط.
/// </summary>
public class ProductionBalanceTests
{
    private sealed record Ctx(int Customer, int Raw, int Saleem, int Monsam, int Lot, int OrderId, int Shift);

    /// <summary>خام 9,200 كجم ← أمر ← إنتاج 10,000 كجم (سليم 6,000 + منسم 4,000).</summary>
    private static Ctx Build(TestHost host)
    {
        using var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var receiving = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        int shift = db.Shifts.AsNoTracking().OrderBy(s => s.Id).Select(s => s.Id).First();
        var c = master.SaveCustomer(null, "BAL-1", "عميل التوازن", "جملة", "777", "-", true);
        var raw = master.SaveProductFull(null, "BAL-R", "سكري خام", "001", "Raw", "كجم", 20, 0, 0, null);

        // §السليم والمنسم: منتجان تامان (002) بوحدة الكرتون — الفرق تصنيف تجاري فقط
        var saleem = master.SaveProductFull(null, "BAL-S", "تمر سليم", "002", "Finished", "كرتون", 10, 5, 2, null, sourceProductId: raw.Id);
        var monsam = master.SaveProductFull(null, "BAL-M", "تمر منسم", "002", "Finished", "كرتون", 10, 5, 2, null, sourceProductId: raw.Id);
        Assert.True(c.Ok && raw.Ok && saleem.Ok && monsam.Ok);

        var sh = receiving.SaveShipment(c.Id, null, null, new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = raw.Id, QtyKg = 9200, PackageCount = 460, UnitWeightKg = 20, ReceiptUnit = "كرتون" } });
        Assert.True(sh.Ok, sh.Message);
        receiving.ApproveShipment(sh.Id);
        int lot = db.Lots.AsNoTracking().Where(l => l.ShipmentId == sh.Id).Select(l => l.Id).First();

        TestCapacityFixture.DefineMissingRates(host);
        var plan = planning.SavePlan("خطة التوازن", "Daily", "2026-09-01", "2026-09-01", shift, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lot, CustomerId = c.Id, ProductId = saleem.Id, PlannedQtyKg = 6000, PlannedCartons = 600, SourceQtyKg = 5520, PriorityNo = 1 },
            new() { SourceType = "FromReceiving", LotId = lot, CustomerId = c.Id, ProductId = monsam.Id, PlannedQtyKg = 4000, PlannedCartons = 400, SourceQtyKg = 3680, PriorityNo = 2 }
        });
        Assert.True(plan.Ok, plan.Message);
        Assert.True(planning.ApprovePlan(plan.Id).Ok);
        // Raw reservations use the entered source draw, not the 10,000 kg
        // finished-product target. The difference is NOT a forced yield factor.
        Assert.Equal(9200, db.Lots.AsNoTracking().Single(l => l.Id == lot).ReservedQtyKg, 1);
        host.SetBusinessDate("2026-09-01"); // Arrange production on its scheduled business day, not the machine date.

        var order = orders.SaveOrder("FromPlan", plan.Id, c.Id, "2026-09-01", shift, 1, TestOrderFixture.Items(host, plan.Id, c.Id, "2026-09-01"));
        Assert.True(order.Ok, order.Message);
        var ap = orders.ApproveOrder(order.Id);
        Assert.True(ap.Ok, "اعتماد الأمر: " + ap.Message);
        Assert.True(orders.StartOrder(order.Id).Ok);

        return new Ctx(c.Id, raw.Id, saleem.Id, monsam.Id, lot, order.Id, shift);
    }

    [Fact]
    public void Weight_Gain_From_Process_Water_Is_Accepted_Not_Rejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = Build(host);

        using var scope = host.Services.CreateScope();
        var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();

        // خام 9,200 ← مخرجات 10,000 (زيادة 800 من ماء التشغيل) — يجب أن تُقبل
        var r = exec.CloseProductionDay(ctx.OrderId, 10000, 1000, 0, 0, 0, false,
            new List<DowntimeDto>(), false, "زيادة الوزن من ماء التشغيل", null, 9200);
        Assert.True(r.Ok, "زيادة الوزن من ماء التشغيل يجب ألا تُرفض: " + r.Message);
    }

    [Fact]
    public void Unspecified_Actual_Raw_Uses_Explicit_Plan_Draw_Not_Output_Target()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = Build(host);
        var close = host.Get<IExecutionService>().CloseProductionDay(ctx.OrderId, 10000, 1000,
            0, 0, 0, false, new List<DowntimeDto>(), false);
        Assert.True(close.Ok, close.Message);
        var db = host.Get<DatesErpDbContext>();
        Assert.Equal(9200, db.ProductionExecutions.AsNoTracking().Single(x => x.OrderId == ctx.OrderId).ConsumedRawKg, 1);
        Assert.Equal(0, db.Lots.AsNoTracking().Single(l => l.Id == ctx.Lot).InStockQtyKg, 1);
    }

    [Fact]
    public void User_Is_Not_Forced_To_Change_His_Numbers()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = Build(host);
        using var scope = host.Services.CreateScope();
        var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        Assert.True(exec.CloseProductionDay(ctx.OrderId, 10000, 1000, 0, 0, 0, false,
            new List<DowntimeDto>(), false, null, null, 9200).Ok);

        // الأرقام محفوظة كما أدخلها المستخدم حرفياً — لم يُخفَّض الإنتاج ولم يُزد الخام
        var e = db.ProductionExecutions.AsNoTracking().Single(x => x.OrderId == ctx.OrderId);
        Assert.Equal(10000, e.ActualQtyKg, 1);
        Assert.Equal(1000, e.ActualCartons);
        Assert.Equal(9200, e.ConsumedRawKg, 1);
    }

    [Fact]
    public void Balance_Report_Shows_Difference_And_Deviation_As_A_Control()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = Build(host);
        using var scope = host.Services.CreateScope();
        var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();
        Assert.True(exec.CloseProductionDay(ctx.OrderId, 10000, 1000, 0, 0, 0, false,
            new List<DowntimeDto>(), false, null, null, 9200).Ok);

        var reports = scope.ServiceProvider.GetRequiredService<IReportService>();
        var r = reports.Run("production_balance", new Dictionary<string, string>());
        Assert.NotNull(r);
        Assert.Single(r.Rows);

        var row = r.Rows[0];
        Assert.Equal("9,200.0", row[3].ToString());    // وزن الخام المسجل
        Assert.Equal("10,000.0", row[4].ToString());   // المنتج التام
        Assert.Equal("10,000.0", row[7].ToString());   // إجمالي المخرجات
        Assert.Equal("+800.0", row[8].ToString());     // فرق الوزن
        Assert.Contains("مراجعة رقابية", row[10].ToString());

        Assert.Contains("9,200.0", r.Summary["إجمالي وزن الخام المسجل (كجم)"]);
        Assert.Contains("+800.0", r.Summary["فرق الوزن الإجمالي (كجم)"]);
        Assert.Equal("1", r.Summary["جلسات تحتاج مراجعة رقابية"]);
        Assert.Contains("الماء", r.Summary["ملاحظة"]);
    }

    [Fact]
    public void Saleem_And_Monsam_Are_Both_Finished_Products_Not_ByProducts()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = Build(host);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        foreach (var id in new[] { ctx.Saleem, ctx.Monsam })
        {
            var p = db.Products.AsNoTracking().Single(x => x.Id == id);
            Assert.Equal("002", p.GroupCode);
            Assert.Equal("Finished", p.ItemType);
        }

        // والمنسم ليس ضمن المخرجات الثانوية (003)
        Assert.DoesNotContain(db.Products.AsNoTracking().Where(p => p.ItemType == "ByProduct").ToList(),
            p => p.ProductNameAr.Contains("منسم"));

        // وكلاهما قابل للتسليم: نفس طبيعة المنتج التام
        using var scope2 = host.Services.CreateScope();
        var del = scope2.ServiceProvider.GetRequiredService<ICustomerDeliveryService>();
        var d = del.Save(ctx.Customer, "2026-09-05", ctx.OrderId, new List<CustomerDeliveryItemDto>
        {
            new() { ProductId = ctx.Monsam, LotId = ctx.Lot, PackageCount = 100, QtyKg = 1000 }
        });
        Assert.True(d.Ok, "المنسم منتج تام قابل للتسليم: " + d.Message);
    }

    [Fact]
    public void Inspection_Records_Both_Saleem_And_Monsam_As_Finished_Grade()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();

        var types = insp.GetResultTypes();
        var saleem = types.FirstOrDefault(t => t.NameAr.Contains("سليم"));
        var monsam = types.FirstOrDefault(t => t.NameAr.Contains("منسم"));
        Assert.NotNull(saleem);
        Assert.NotNull(monsam);

        // كلاهما نتيجة «مقبول» ومنتج تام — لا مخرج ثانوي
        Assert.Equal(InspectionResultType.KindAccepted, saleem.ResultKind);
        Assert.Equal(InspectionResultType.KindAccepted, monsam.ResultKind);
        Assert.True(saleem.IsFinishedGood);
        Assert.True(monsam.IsFinishedGood);
        Assert.False(monsam.IsByProduct);
        Assert.Equal("كرتون", monsam.UnitLabel);
    }

    [Fact]
    public void No_Fixed_Balance_Equation_Exists_In_The_Code()
    {
        // §لا معادلة ثابتة تفترض أن الخام يجب أن يساوي التام + المخرجات
        string root = FindRoot();
        string exec = File.ReadAllText(Path.Combine(root, "src/DatesErp.Application/Services/ExecutionService.cs"));
        Assert.DoesNotContain("OUTPUTS_EXCEED", exec);
        Assert.DoesNotContain("أكبر من الخام المستهلك", exec);
    }

    private static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "DateERP.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
