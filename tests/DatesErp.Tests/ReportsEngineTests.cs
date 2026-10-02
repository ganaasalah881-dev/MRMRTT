using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §مرحلة التقارير — محرك التقارير الجديد (مبني من الصفر على نموذج النظام):
/// تقارير العمليات: حركات التوريد، حركات الخطط، سجل العمليات الموحد.
/// التقارير الشاملة: كشف الصنف، كشف العميل، الإنتاج اليومي، الفحص التفصيلي.
/// </summary>
public class ReportsEngineTests
{
    private sealed record Env(int Customer, int RawSuk, int RawKha, int FinSuk, int FinKha, int LotSuk, int LotKha, int OrderId);

    /// <summary>دورة كاملة: استلام ← خطة ← أمر ← إنتاج/إقفال ← فحص ← تام ← تسليم ← فاتورة.</summary>
    private static Env BuildFullCycle(TestHost host)
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
        var progress = scope.ServiceProvider.GetRequiredService<IPlanProgressService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var c = master.SaveCustomer(null, "TR1", "شركة التقارير", "جملة", "777", "-", true);
        var rs = master.SaveProductFull(null, "R-SUK-T", "سكري", "001", "Raw", "كجم", 20, 0, 0, null);
        var rk = master.SaveProductFull(null, "R-KHA-T", "خلاص", "001", "Raw", "كجم", 20, 0, 0, null);
        var fs = master.SaveProductFull(null, "F-SUK-T", "سكري تام", "002", "Finished", "كرتون", 7.5, 1, 0.5, null, sourceProductId: rs.Id);
        var fk = master.SaveProductFull(null, "F-KHA-T", "خلاص تام", "002", "Finished", "كرتون", 7.5, 1, 0.5, null, sourceProductId: rk.Id);
        Assert.True(c.Ok && rs.Ok && rk.Ok && fs.Ok && fk.Ok);

        var s = receiving.SaveShipment(c.Id, "15/08/2026", "16/08/2026", new List<ShipmentItemDto>
        {
            new() { TreatmentRequired = false, ProductId = rs.Id, QtyKg = 10000, PackageCount = 500, UnitWeightKg = 20, ReceiptUnit = "سلة" },
            new() { TreatmentRequired = false, ProductId = rk.Id, QtyKg = 8000, PackageCount = 400, UnitWeightKg = 20, ReceiptUnit = "كرتون" }
        }, containerNumber: "CXLU-884");
        Assert.True(s.Ok && receiving.ApproveShipment(s.Id).Ok);
        int lotSuk = db.Lots.Single(l => l.ShipmentId == s.Id && l.ProductId == rs.Id).Id;
        int lotKha = db.Lots.Single(l => l.ShipmentId == s.Id && l.ProductId == rk.Id).Id;

        string day = "20/08/2026";
        TestCapacityFixture.DefineMissingRates(host);
        var plan = planning.SavePlan("خطة التقارير", "Daily", day, day, 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lotSuk, CustomerId = c.Id, ProductId = fs.Id,
                    PlannedQtyKg = 7500, PlannedCartons = 1000, ScheduledDate = day, SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 },
            new() { SourceType = "FromReceiving", LotId = lotKha, CustomerId = c.Id, ProductId = fk.Id,
                    PlannedQtyKg = 6000, PlannedCartons = 800, ScheduledDate = day, SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 2 }
        });
        Assert.True(plan.Ok, plan.Message);
        Assert.True(planning.ApprovePlan(plan.Id).Ok);
        var itemIds = db.ProductionPlanItems.Where(i => i.PlanId == plan.Id).OrderBy(i => i.PriorityNo).Select(i => i.Id).ToList();
        host.SetBusinessDate(day); // Arrange production on its scheduled business day, not the machine date.

        var order = orders.SaveOrder("FromPlan", plan.Id, c.Id, day, 1, 1, new List<OrderItemDto>
        {
            new() { PlanItemId = itemIds[0], LotId = lotSuk, CustomerId = c.Id, ProductId = fs.Id, PlannedQtyKg = 7500, PlannedCartons = 1000 },
            new() { PlanItemId = itemIds[1], LotId = lotKha, CustomerId = c.Id, ProductId = fk.Id, PlannedQtyKg = 6000, PlannedCartons = 800 }
        });
        Assert.True(order.Ok, order.Message);
        Assert.True(orders.ApproveOrder(order.Id).Ok);

        var close = exec.CloseProductionDay(order.Id, 13100, 1747, 200, 150, 50, false,
            new List<DowntimeDto> { new() { Hours = 1.5, ReasonAr = "صيانة الخط" } }, sendToQuality: true);
        Assert.True(close.Ok, close.Message);
        int execId = db.ProductionExecutions.Single(e => e.OrderId == order.Id).Id;

        var qc = quality.SaveCheck(order.Id, execId, "22/08/2026", "نهائي — بعد التبريد", new List<QualityItemDto>
        {
            new() { ProductId = fs.Id, LotId = lotSuk, AcceptedQtyKg = 7300, RejectedQtyKg = 200 },
            new() { ProductId = fk.Id, LotId = lotKha, AcceptedQtyKg = 5500, RejectedQtyKg = 100 }
        }, null, new QualityLabDto { Decision = "Passed", MoisturePct = 15.8, BrixDeg = 69.2, SampleCartons = 10 });
        Assert.True(qc.Ok, qc.Message);
        Assert.True(quality.ApproveCheck(qc.Id).Ok);

        var rcpt = TestProductionDocumentFlow.SaveReceiptFromActual(host, order.Id, qc.Id, "22/08/2026", new List<FinishedGoodsItemDto>
        {
            // The line is the carton authority; its actual production rounding
            // may differ by one from a naive kg / product-card weight.
            new() { ProductId = fs.Id, LotId = lotSuk, NetWeightKg = 7300, PackageCount = 0 },
            new() { ProductId = fk.Id, LotId = lotKha, NetWeightKg = 5500, PackageCount = 0 }
        });
        Assert.True(rcpt.Ok, rcpt.Message);
        Assert.True(fg.Issue(rcpt.Id).Ok);
        Assert.True(fg.Receive(rcpt.Id, null).Ok);

        var d = dlv.Save(c.Id, "23/08/2026", order.Id, new List<CustomerDeliveryItemDto>
        {
            new() { ProductId = fs.Id, LotId = lotSuk, QtyKg = 1000 },
            new() { ProductId = fk.Id, LotId = lotKha, QtyKg = 500 }
        });
        Assert.True(d.Ok, d.Message);
        Assert.True(dlv.Approve(d.Id).Ok);
        Assert.True(progress.MarkInvoiced(d.Id, 1500).Ok);

        return new Env(c.Id, rs.Id, rk.Id, fs.Id, fk.Id, lotSuk, lotKha, order.Id);
    }

    private static ReportResult RunReport(TestHost host, string code, Dictionary<string, string> p = null)
    {
        using var scope = host.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IReportService>();
        var r = svc.Run(code, p ?? new Dictionary<string, string>());
        Assert.NotNull(r);
        return r;
    }

    [Fact]
    public void Receiving_Detail_Shows_Unit_And_Kg_With_Filters()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var env = BuildFullCycle(host);

        var r = RunReport(host, "receiving_detail");
        Assert.Equal(2, r.Rows.Count); // بندا الاستلام: سكري + خلاص
        Assert.Contains("18,000", r.Summary["إجمالي الوزن المستلم (كجم)"]);
        // §قاعدة الاستلام: تُحفظ وحدة الاستلام الأصلية كما وردت — سلة/كرتون/غيرها
        Assert.Contains(r.Rows, row => row[5].ToString() == "سلة");
        Assert.Contains(r.Rows, row => row[5].ToString() == "كرتون");

        // فلتر الصنف: السكري فقط
        var filtered = RunReport(host, "receiving_detail", new Dictionary<string, string> { ["product"] = env.RawSuk.ToString() });
        Assert.Single(filtered.Rows);
        Assert.Contains("سكري", filtered.Rows[0][4].ToString());
    }

    [Fact]
    public void Plans_Activity_Shows_Full_Chain_Per_Item()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        BuildFullCycle(host);

        var r = RunReport(host, "plans_activity");
        Assert.Equal(2, r.Rows.Count);
        Assert.Contains("13,500", r.Summary["إجمالي المخطط (كجم)"]);
        Assert.Contains("13,100", r.Summary["إجمالي المنتَج (كجم)"]);
        // المسلَّم: 1000 سكري + 500 خلاص
        Assert.Contains("1,500", r.Summary["إجمالي المسلَّم (كجم)"]);
        // كل سطر بمرجعه: دفعة وشحنة وخطة
        Assert.All(r.Rows, row =>
        {
            Assert.NotEqual("—", row[3]); // الدفعة
            Assert.NotEqual("—", row[4]); // الشحنة
        });
    }

    [Fact]
    public void Operations_Ledger_Unifies_All_Document_Types()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        BuildFullCycle(host);

        var r = RunReport(host, "operations");
        Assert.True(r.Rows.Count >= 7, $"عمليات متوقعة ≥ 7 والفعلية {r.Rows.Count}");
        Assert.Contains(r.Rows, row => row[0].ToString().Contains("استلام"));
        Assert.Contains(r.Rows, row => row[0].ToString().Contains("خطة"));
        Assert.Contains(r.Rows, row => row[0].ToString().Contains("أمر"));
        Assert.Contains(r.Rows, row => row[0].ToString().Contains("إنتاج"));
        Assert.Contains(r.Rows, row => row[0].ToString().Contains("فحص"));
        Assert.Contains(r.Rows, row => row[0].ToString().Contains("استلام تام"));
        Assert.Contains(r.Rows, row => row[0].ToString().Contains("تسليم"));

        // فلتر نوع العملية: التسليم فقط
        var d = RunReport(host, "operations", new Dictionary<string, string> { ["optype"] = "delivery" });
        Assert.All(d.Rows, row => Assert.Contains("تسليم", row[0].ToString()));
    }

    [Fact]
    public void Item_Statement_Shows_Complete_Journey_Totals()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var env = BuildFullCycle(host);

        var r = RunReport(host, "item_statement", new Dictionary<string, string> { ["customer"] = env.Customer.ToString() });
        Assert.Equal(4, r.Rows.Count); // سكري خام/تام + خلاص خام/تام

        var suk = r.Rows.First(row => row[0].ToString() == "سكري");
        Assert.Equal("10,000.0", suk[3].ToString()); // المستلم
        Assert.Equal("7,500.0", suk[4].ToString());  // المخطط
        Assert.Equal("7,500.0", suk[5].ToString());  // المنتَج
        Assert.Equal("7,300.0", suk[6].ToString());  // المقبول
        Assert.Equal("6,300.0", suk[7].ToString());  // مخزون التام (7300 − 1000)
        Assert.Equal("1,000.0", suk[8].ToString());  // المسلَّم
    }

    [Fact]
    public void Customer_Statement_Shows_All_Stage_Totals()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var env = BuildFullCycle(host);

        var r = RunReport(host, "customer_statement", new Dictionary<string, string> { ["customer"] = env.Customer.ToString() });
        Assert.Single(r.Rows);
        var row = r.Rows[0];
        Assert.Equal("شركة التقارير", row[0]);
        Assert.Equal("18,000.0", row[1]); // المستلم
        Assert.Equal("13,500.0", row[2]); // المخطط
        Assert.Equal("13,100.0", row[3]); // المنتَج
        Assert.Equal("11,300.0", row[5]); // مخزون التام (12800 − 1500)
        Assert.Equal("1,500.0", row[6]);  // المسلَّم
        Assert.Equal("1,500.0", row[7]);  // المفوتر
    }

    [Fact]
    public void Daily_Production_Shows_Outputs_And_Downtime()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        BuildFullCycle(host);

        var r = RunReport(host, "daily_production");
        Assert.Single(r.Rows);
        var row = r.Rows[0];
        // §أعمدة المخرجات الثانوية ديناميكية (عمود لكل مخرج معرَّف في إعدادات الأصناف)،
        // فأرقام الأعمدة تتغير بإضافة مخرج جديد. لذلك يُبحث عن العمود بعنوانه لا بموقعه.
        int Col(string startsWith) =>
            r.Columns.ToList().FindIndex(c => c.StartsWith(startsWith, StringComparison.Ordinal));
        int iProduced = Col("المنتَج");
        int iHashf = Col("حشف"), iNawa = Col("نوى"), iAjeenah = Col("عجينة");
        int iWaste = Col("الفاقد"), iDown = Col("توقفات");
        Assert.True(iProduced >= 0 && iHashf >= 0 && iNawa >= 0 && iWaste >= 0 && iDown >= 0,
            "عناوين الأعمدة: " + string.Join(" | ", r.Columns));

        Assert.Equal("13,100.0", row[iProduced].ToString()); // المنتَج
        Assert.Equal("200.0", row[iHashf].ToString());       // حشف
        Assert.Equal("150.0", row[iNawa].ToString());        // نوى
        Assert.Equal("50.0", row[iWaste].ToString());        // الفاقد
        Assert.Equal("1.5", row[iDown].ToString());          // توقفات
        // §قاعدة المصنع: العجينة مخرج ثانوي معرَّف، فيظهر عموده في التقرير (صفر في هذه الدورة)
        if (iAjeenah >= 0) Assert.Equal("0.0", row[iAjeenah].ToString());
        Assert.Contains("1.5", r.Summary["إجمالي التوقفات (ساعة)"]);
        // §عناوين المخرجات صارت ديناميكية (من تعريف الأصناف) — نبحث عن المفتاح لا عن اسم ثابت
        var byKey = r.Summary.Keys.FirstOrDefault(k => k.Contains("المخرجات الثانوية") || k.StartsWith("إجمالي") && k.Contains("كجم"));
        Assert.False(string.IsNullOrEmpty(byKey), "لا مفتاح مخرجات ثانوية في الملخص: " + string.Join(" | ", r.Summary.Keys));
        Assert.False(string.IsNullOrEmpty(r.Summary[byKey]));
    }

    [Fact]
    public void Quality_Detail_Shows_Decision_And_Lab_Standards()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        BuildFullCycle(host);

        var r = RunReport(host, "quality_detail");
        Assert.Single(r.Rows);
        var row = r.Rows[0];
        Assert.Equal("15.8", row[7].ToString());  // الرطوبة
        Assert.Equal("69.2", row[8].ToString());  // السكريات
        Assert.Contains("مطابق", row[11].ToString()); // القرار
        Assert.NotEqual("—", r.Summary["نسبة القبول"]);

        // فلتر القرار: مرفوض ← لا نتائج في هذه الدورة
        var rej = RunReport(host, "quality_detail", new Dictionary<string, string> { ["decision"] = "Rejected" });
        Assert.Empty(rej.Rows);
    }
}

/// <summary>
/// §اختبارات انحدار — مركز التقارير: الفترات والفلاتر والترويسة والإجماليات.
///
/// ما كان قبل الإصلاح:
///  • 19 من 34 تقريراً بلا فلتر فترة — ومنها تقارير حركية (أوامر/إنتاج/خطط/جودة/تسليم)
///    لا تفلتر بالتاريخ لا في التعريف ولا في التنفيذ
///  • كل التقارير الـ34 بلا PeriodLabel إلا إذا مُرّرت فترة — فالمطبوع لا يُظهر مدى تغطيته
///  • 14 تقريراً بلا Summary
///  • 15 فئة بأسماء مكررة («المخزون» و«تقارير المخزون»…) تقسم المجال الواحد قسمين
/// </summary>
public class ReportsPeriodAndFilterTests
{
    private static IReportService Rep(TestHost host)
        => host.Services.CreateScope().ServiceProvider.GetRequiredService<IReportService>();

    private static DatesErpDbContext Fresh(TestHost host)
        => new DatesErpDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<DatesErpDbContext>()
            .UseSqlite(host.Connection).Options);

    private static int MakePlanInAugust(TestHost host)
    {
        // الكتابة عبر سياق المضيف نفسه — سياق جديد يتجاوز AuditSaveChangesInterceptor
        // الذي يملأ RowVersion على SQLite، فيفشل قيد NOT NULL.
        var db = host.Get<DatesErpDbContext>();
        int whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new DatesErp.Core.Domain.Entities.StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 900000 },
            new DatesErp.Core.Domain.Entities.StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 900000 });
        db.SaveChanges();
        int cust = db.Customers.First().Id;
        var receiving = host.Services.CreateScope().ServiceProvider.GetRequiredService<IReceivingService>();
        var s = receiving.SaveShipment(cust, "2026-08-10", "2026-08-10",
            new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 500, UnitWeightKg = 20, QtyKg = 10000 } }, null, "RPT-1");
        Assert.True(s.Ok, s.Message);
        Assert.True(receiving.ApproveShipment(s.Id).Ok);
        int lot = Fresh(host).Lots.OrderBy(l => l.Id).First().Id;
        var planning = host.Services.CreateScope().ServiceProvider.GetRequiredService<IPlanningService>();
        TestCapacityFixture.DefineMissingRates(host);
        var p = planning.SavePlan("خطة أغسطس", "Period", "2026-08-20", "2026-08-20", 1, 1,
            new List<PlanItemDto> { new() { SourceType="FromReceiving", LotId=lot, CustomerId=cust, ProductId=3,
                PackagingTypeId=2, PlannedCartons=100, PlannedQtyKg=1000, ScheduledDate="2026-08-20",
                SuggestedShiftId=1, SuggestedLineId=1, PriorityNo=1 } });
        Assert.True(p.Ok, p.Message);
        Assert.True(planning.ApprovePlan(p.Id).Ok);
        return p.Id;
    }

    [Fact]
    public void Every_Report_Runs_And_Rows_Match_Columns()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        MakePlanInAugust(host);
        var rep = Rep(host);
        foreach (var d in rep.GetReports())
        {
            var r = rep.Run(d.Code, new Dictionary<string, string>());
            Assert.NotNull(r);
            Assert.All(r.Rows, row => Assert.Equal(r.Columns.Count, row.Length));
        }
    }

    [Fact]
    public void Every_Report_Shows_A_Period_Label_And_A_Summary()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        MakePlanInAugust(host);
        var rep = Rep(host);
        foreach (var d in rep.GetReports())
        {
            var r = rep.Run(d.Code, new Dictionary<string, string>());
            Assert.False(string.IsNullOrWhiteSpace(r.PeriodLabel), $"{d.Code}: بلا PeriodLabel في الترويسة");
            Assert.NotEmpty(r.Summary);
        }
    }

    [Fact]
    public void Period_Filter_Actually_Discriminates_On_Plans()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        MakePlanInAugust(host);
        var rep = Rep(host);

        var all = rep.Run("plans", new Dictionary<string, string>());
        Assert.NotEmpty(all.Rows);

        var outOfRange = rep.Run("plans", new Dictionary<string, string> { ["from"] = "01/09/2026", ["to"] = "30/09/2026" });
        Assert.Empty(outOfRange.Rows);

        var inRange = rep.Run("plans", new Dictionary<string, string> { ["from"] = "01/08/2026", ["to"] = "31/08/2026" });
        Assert.Equal(all.Rows.Count, inRange.Rows.Count);
    }

    [Theory]
    [InlineData("orders")]
    [InlineData("production")]
    [InlineData("plans")]
    [InlineData("quality")]
    [InlineData("delivery")]
    public void Transaction_Reports_Declare_A_Period_Filter(string code)
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var d = Rep(host).GetReports().Single(x => x.Code == code);
        Assert.Contains(d.Parameters, p => p.Key == "from");
        Assert.Contains(d.Parameters, p => p.Key == "to");
    }

    [Fact]
    public void Categories_Are_Not_Duplicated()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var cats = Rep(host).GetReports().Select(r => r.Category).Distinct().ToList();
        // «المخزون» و«تقارير المخزون» كانا فئتين لمجال واحد — وكذلك الإنتاج والجودة
        Assert.DoesNotContain(cats, c => c.StartsWith("تقارير "));
        Assert.DoesNotContain(cats, c => c.StartsWith("التقارير "));
        Assert.Equal(1, cats.Count(c => c.Contains("المخزون")));
        Assert.Equal(1, cats.Count(c => c.Contains("الإنتاج") && !c.Contains("أوامر")));
        Assert.Equal(1, cats.Count(c => c == "الجودة"));
    }
}

/// <summary>
/// §اختبار انحدار — زر «+» للتنقل إلى المستند المصدر (نمط TAMAT555: عمود «التتبع» في كل تقرير).
/// قبل الإصلاح: 16 من 34 تقريراً (كل التقارير القديمة) بلا RowLinks إطلاقاً،
/// فزر «+» لا يظهر فيها ولا يمكن الوصول إلى المستند من التقرير.
/// </summary>
public class ReportDrillDownTests
{
    private static IReportService Rep(TestHost host)
        => host.Services.CreateScope().ServiceProvider.GetRequiredService<IReportService>();

    /// <summary>التقارير التي لها مستند مصدر واحد لكل صف — يجب أن يكون لها رابط لكل صف.</summary>
    [Theory]
    [InlineData("receiving", "receiving")]
    [InlineData("lots", "receiving")]
    [InlineData("plans", "planning")]
    [InlineData("orders", "orders")]
    [InlineData("quality", "quality")]
    [InlineData("delivery", "delivery")]
    public void Report_Rows_Carry_A_DrillDown_Link(string code, string expectedDocType)
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        int whAux = db.Warehouses.Single(w => w.WarehouseCode == "WAUX").Id;
        db.StockBalances.AddRange(
            new DatesErp.Core.Domain.Entities.StockBalance { WarehouseId = whAux, MaterialId = 1, QtyKg = 900000 },
            new DatesErp.Core.Domain.Entities.StockBalance { WarehouseId = whAux, MaterialId = 2, QtyKg = 900000 });
        db.SaveChanges();
        int cust = db.Customers.First().Id;

        var receiving = host.Services.CreateScope().ServiceProvider.GetRequiredService<IReceivingService>();
        var s = receiving.SaveShipment(cust, "2026-08-10", "2026-08-10",
            new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 500, UnitWeightKg = 20, QtyKg = 10000 } }, null, "DD-1");
        Assert.True(s.Ok, s.Message);
        Assert.True(receiving.ApproveShipment(s.Id).Ok);
        int lot = host.Get<DatesErpDbContext>().Lots.OrderBy(l => l.Id).First().Id;

        var planning = host.Services.CreateScope().ServiceProvider.GetRequiredService<IPlanningService>();
        TestCapacityFixture.DefineMissingRates(host);
        var pl = planning.SavePlan("خطة التتبع", "Period", "2026-08-20", "2026-08-20", 1, 1,
            new List<PlanItemDto> { new() { SourceType="FromReceiving", LotId=lot, CustomerId=cust, ProductId=3,
                PackagingTypeId=2, PlannedCartons=100, PlannedQtyKg=1000, ScheduledDate="2026-08-20",
                SuggestedShiftId=1, SuggestedLineId=1, PriorityNo=1 } });
        Assert.True(pl.Ok, pl.Message);
        Assert.True(planning.ApprovePlan(pl.Id).Ok);

        var rep = Rep(host);
        var r = rep.Run(code, new Dictionary<string, string>());
        Assert.NotNull(r);
        Assert.NotNull(r.RowLinks);
        if (r.Rows.Count == 0) return;   // لا مستندات من هذا النوع في هذا السيناريو
        // الرابط يوازي الصفوف تماماً — وإلا اختلّ ترتيب الأزرار
        Assert.Equal(r.Rows.Count, r.RowLinks.Count);
        Assert.All(r.RowLinks, l => Assert.Equal(expectedDocType, l.DocType));
        Assert.All(r.RowLinks, l => Assert.True(l.Id > 0, "رابط بلا معرّف مستند"));
    }

    /// <summary>كل تقرير يجب أن يُرجع قائمة روابط (ولو فارغة) لا null — حتى لا ينكسر العرض.</summary>
    [Fact]
    public void Every_Report_Returns_A_NonNull_RowLinks_List()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var rep = Rep(host);
        foreach (var d in rep.GetReports())
        {
            var r = rep.Run(d.Code, new Dictionary<string, string>());
            Assert.NotNull(r);
            Assert.NotNull(r.RowLinks);
        }
    }
}
