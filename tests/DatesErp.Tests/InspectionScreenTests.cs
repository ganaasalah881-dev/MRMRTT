using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §شاشة الفحص والجودة — مرنة حسب الصنف والوحدات (§شرط القبول):
///  • لا وحدة ثابتة في الكود.
///  • لا أنواع نتائج ثابتة في الكود.
///  • إضافة نوع نتيجة جديد من الإعدادات يظهر فوراً.
///  • الربط مع أمر الإنتاج محفوظ.
///  • النتائج والنسب تُحسب صح.
///  • منع جمع كميات بوحدات مختلفة دون تحويل معرَّف.
/// </summary>
public class InspectionScreenTests
{
    private sealed record Ctx(int Customer, int Raw, int Fin, int Lot, int OrderId, int Kg, int Ctn);

    /// <summary>دورة كاملة: خام ← استلام ← خطة ← أمر ← إقفال إنتاج — حتى يصبح الأمر مصدراً للفحص.</summary>
    private static Ctx BuildOrderWithProduction(TestHost host, string tag, double cartonWeight = 7.5, double produceKg = 3000)
    {
        using var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var receiving = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var planning = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
        var exec = scope.ServiceProvider.GetRequiredService<IExecutionService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var c = master.SaveCustomer(null, $"IC-{tag}", $"عميل الفحص {tag}", "جملة", "777", "-", true);
        var raw = master.SaveProductFull(null, $"{tag}-R1", $"سكري خام {tag}", "001", "Raw", "كجم", 20, 0, 0, null);
        var fin = master.SaveProductFull(null, $"{tag}-F1", $"سكري تام {tag}", "002", "Finished", "كرتون", cartonWeight, 1, 0.5, null, sourceProductId: raw.Id);
        Assert.True(c.Ok && raw.Ok && fin.Ok, $"{c.Message} {raw.Message} {fin.Message}");

        var s = receiving.SaveShipment(c.Id, null, null, new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = raw.Id, QtyKg = 5000, PackageCount = 250, UnitWeightKg = 20 } });
        Assert.True(s.Ok && receiving.ApproveShipment(s.Id).Ok);
        int lot = db.Lots.Single(l => l.ShipmentId == s.Id).Id;

        TestCapacityFixture.DefineMissingRates(host);
        var plan = planning.SavePlan($"خطة {tag}", "Daily", "2026-09-01", "2026-09-01", 1, 1, new List<PlanItemDto>
        { new() { SourceType = "FromReceiving", LotId = lot, CustomerId = c.Id, ProductId = fin.Id, PlannedQtyKg = produceKg, PriorityNo = 1 } });
        Assert.True(plan.Ok && planning.ApprovePlan(plan.Id).Ok);
        int planItemId = db.ProductionPlanItems.Single(i => i.PlanId == plan.Id).Id;
        host.SetBusinessDate("2026-09-01"); // Arrange production on its scheduled business day, not the machine date.

        var order = orders.SaveOrder("FromPlan", plan.Id, c.Id, "2026-09-01", 1, 1, TestOrderFixture.Items(host, plan.Id, c.Id, "2026-09-01"));
        Assert.True(order.Ok, order.Message);
        var approveOrder = orders.ApproveOrder(order.Id); Assert.True(approveOrder.Ok, approveOrder.Message);

        var close = exec.CloseProductionDay(order.Id, produceKg, (int)(produceKg / cartonWeight), 0, 0, 0, false, new List<DowntimeDto>(), false);
        Assert.True(close.Ok, close.Message);

        int kg = db.UnitsOfMeasure.Single(u => u.UnitNameAr == "كجم").Id;
        int ctn = db.UnitsOfMeasure.Single(u => u.UnitNameAr == "كرتون").Id;
        return new Ctx(c.Id, raw.Id, fin.Id, lot, order.Id, kg, ctn);
    }

    private static int Unit(TestHost host, string name)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        return db.UnitsOfMeasure.Single(u => u.UnitNameAr == name).Id;
    }

    // ══════════════════ 1) لا أنواع نتائج ثابتة في الكود ══════════════════

    [Fact]
    public void ResultTypes_Come_From_Database_Not_Hardcoded()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var fromService = insp.GetResultTypes();
        Assert.NotEmpty(fromService);

        // ما تُرجعه الخدمة هو بالضبط ما في الجدول — لا قائمة موازية في الكود
        var fromDb = db.InspectionResultTypes.Where(t => t.IsActive).Select(t => t.Id).OrderBy(x => x).ToList();
        Assert.Equal(fromDb, fromService.Select(t => t.ResultTypeId).OrderBy(x => x).ToList());

        // وكل نوع يحمل وحدته من القاموس
        foreach (var t in fromService.Where(t => t.UnitId != null))
            Assert.Equal(db.UnitsOfMeasure.Single(u => u.Id == t.UnitId).UnitNameAr, t.UnitLabel);
    }

    [Fact]
    public void Adding_New_ResultType_From_Settings_Appears_Immediately()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        int kg = Unit(host, "كجم");

        using var scope = host.Services.CreateScope();
        var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();

        const string newName = "تمر مطحون (نتيجة جديدة)";
        Assert.DoesNotContain(insp.GetResultTypes(), t => t.NameAr == newName);

        var r = insp.SaveResultType(null, null, newName, "مخرج ثانوي", kg,
            isFinishedGood: false, isByProduct: true, entersInventory: true, countsAsLoss: false, sortNo: 99, isActive: true);
        Assert.True(r.Ok, r.Message);

        var added = insp.GetResultTypes().Single(t => t.NameAr == newName);
        Assert.Equal("مخرج ثانوي", added.ResultKindAr);   // قَبِل التسمية العربية
        Assert.Equal(InspectionResultType.KindByProduct, added.ResultKind);
        Assert.Equal("كجم", added.UnitLabel);
        Assert.True(added.IsByProduct);
        Assert.True(added.EntersInventory);
        Assert.False(added.CountsAsLoss);

        // يظهر فوراً ضمن نتائج أي صنف (بلا تخصيص)
        Assert.Contains(insp.GetAllowedResultTypesForItem(null), a => a.NameAr == newName);
    }

    [Fact]
    public void ResultType_Rejects_Unknown_Unit_Duplicate_Name_And_BadKind()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();

        var badUnit = insp.SaveResultType(null, null, "نتيجة بوحدة وهمية", "Accepted", 99999,
            true, false, true, false, 1, true);
        Assert.False(badUnit.Ok);
        Assert.Contains("قاموس الوحدات", badUnit.Message);

        // اسم جديد يُقبل ثم يُرفض تكراره
        const string uniq = "نتيجة فريدة للاختبار";
        Assert.True(insp.SaveResultType(null, null, uniq, "ByProduct", null, false, true, true, false, 1, true).Ok);
        var dup = insp.SaveResultType(null, null, uniq, "ByProduct", null, false, true, true, false, 2, true);
        Assert.False(dup.Ok);
        Assert.Contains(uniq, dup.Message);

        // والتصنيف غير المعتمد مرفوض
        var badKind = insp.SaveResultType(null, null, "تصنيف خاطئ", "نوع غير معروف", null, false, false, true, false, 3, true);
        Assert.False(badKind.Ok);
    }

    // ══════════════════ 2) لا وحدة ثابتة في الكود ══════════════════

    [Fact]
    public void ByProduct_Unit_Comes_From_Item_Definition()
    {
        // §القاعدة المعتمدة: لا تُفرض الوحدات داخل الكود — الوحدة من تعريف الصنف في
        // شاشة الأصناف المركزية. ومجموعة الصنف (003) هي التي تحدد أنه مخرج ثانوي.
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var d = master.SaveProductFull(null, "BP-DEF", "مخرج افتراضي", "003", "ByProduct", null, 0, 0, 0, null);
        Assert.True(d.Ok, d.Message);
        var saved = db.Products.Single(p => p.Id == d.Id);
        Assert.Equal("كجم", saved.UnitOfMeasure);   // افتراض المجموعة عند الفراغ
        Assert.Equal("003", saved.GroupCode);

        // ووحدة معرَّفة صراحةً تُقبل كما هي
        var kg = master.SaveProductFull(null, "BP-KG", "عجينة", "003", "ByProduct", "كجم", 0, 0, 0, null);
        Assert.True(kg.Ok, kg.Message);
        Assert.Equal("كجم", db.Products.Single(p => p.Id == kg.Id).UnitOfMeasure);
    }

    [Fact]
    public void Same_ResultType_Can_Use_Different_Units_Per_Item()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var a = BuildOrderWithProduction(host, "U1");
        var b = BuildOrderWithProduction(host, "U2");
        int bsk = Unit(host, "سلة");

        using var scope = host.Services.CreateScope();
        var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();
        var type = insp.GetResultTypes().First(t => t.ResultKind == InspectionResultType.KindByProduct);

        // الصنف الأول: النتيجة بالسلة · الصنف الثاني: بنفس وحدة النوع
        Assert.True(insp.SetProfile(null, a.Fin, null, type.ResultTypeId, bsk, 0, false, 1, true).Ok);

        var forA = insp.GetAllowedResultTypesForItem(a.Fin).Single(x => x.ResultTypeId == type.ResultTypeId);
        var forB = insp.GetAllowedResultTypesForItem(b.Fin).Single(x => x.ResultTypeId == type.ResultTypeId);
        Assert.Equal("سلة", forA.UnitLabel);
        Assert.NotEqual(forA.UnitLabel, forB.UnitLabel);
    }

    // ══════════════════ 3) الحسابات ومنع جمع الوحدات المختلفة ══════════════════

    [Fact]
    public void Compute_Groups_By_Unit_And_Refuses_To_Mix_Without_Conversion()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = BuildOrderWithProduction(host, "M1");

        using var scope = host.Services.CreateScope();
        var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();
        var types = insp.GetResultTypes();
        int ok = types.First(t => t.ResultKind == InspectionResultType.KindAccepted).ResultTypeId;
        int by = types.First(t => t.ResultKind == InspectionResultType.KindByProduct).ResultTypeId;
        int rej = types.First(t => t.ResultKind == InspectionResultType.KindRejected).ResultTypeId;

        // 900 كرتون تام + 100 كجم مخرج ثانوي + 30 كرتون مرفوض — مثال المستخدم حرفياً
        var results = new List<InspectionResultDto>
        {
            new() { ResultTypeId = ok,  Qty = 900, UnitId = ctx.Ctn, ProductId = ctx.Fin, LotId = ctx.Lot },
            new() { ResultTypeId = by,  Qty = 100, UnitId = ctx.Kg,  ProductId = ctx.Fin, LotId = ctx.Lot },
            new() { ResultTypeId = rej, Qty = 30,  UnitId = ctx.Ctn, ProductId = ctx.Fin, LotId = ctx.Lot }
        };
        insp.ValidateResults(results, ctx.OrderId, ctx.Fin);
        var t = insp.Compute(results);

        Assert.False(t.SingleUnit);                     // وحدتان → لا إجمالي موحّد
        Assert.Equal(2, t.ByUnit.Count);

        var ctnTotal = t.ByUnit.Single(u => u.UnitLabel == "كرتون");
        Assert.Equal(930, ctnTotal.Checked);            // 900 + 30
        Assert.Equal(900, ctnTotal.Accepted);
        Assert.Equal(30, ctnTotal.Rejected);
        Assert.Equal(0, ctnTotal.ByProduct);
        Assert.Equal(Math.Round(900.0 / 930 * 100, 2), Math.Round(ctnTotal.Accepted / ctnTotal.Checked * 100, 2));

        var kgTotal = t.ByUnit.Single(u => u.UnitLabel == "كجم");
        Assert.Equal(100, kgTotal.Checked);
        Assert.Equal(100, kgTotal.ByProduct);
        Assert.Equal(0, kgTotal.Accepted);

        Assert.NotEmpty(t.Warnings);
        Assert.Contains("وحدات مختلفة", t.Warnings[0]);

        // بلا تحويل معرَّف: الإجمالي الموحّد مرفوض مع سبب صريح
        var mixed = insp.ComputeConvertedTotal(results, ctx.Kg, out var why);
        Assert.Null(mixed);
        Assert.Contains("لا يوجد تحويل معرَّف", why);

        // بعد تعريف التحويل: يُحسب
        Assert.True(insp.SaveConversion(null, ctx.Ctn, ctx.Kg, 7.5m, true).Ok);
        var total = insp.ComputeConvertedTotal(results, ctx.Kg, out var why2);
        Assert.Null(why2);
        Assert.Equal(930 * 7.5 + 100, total.Value);     // 7075
    }

    [Fact]
    public void Compute_Single_Unit_Gives_Acceptance_Percentage()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = BuildOrderWithProduction(host, "M2");
        using var scope = host.Services.CreateScope();
        var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();
        var types = insp.GetResultTypes();

        var results = new List<InspectionResultDto>
        {
            new() { ResultTypeId = types.First(t => t.ResultKind == InspectionResultType.KindAccepted).ResultTypeId, Qty = 800, UnitId = ctx.Ctn },
            new() { ResultTypeId = types.First(t => t.ResultKind == InspectionResultType.KindRejected).ResultTypeId, Qty = 200, UnitId = ctx.Ctn }
        };
        var t = insp.Compute(results);
        Assert.True(t.SingleUnit);
        Assert.Equal("كرتون", t.PrimaryUnitLabel);
        Assert.Equal(1000, t.TotalChecked);
        Assert.Equal(80, t.AcceptancePct.Value);
        Assert.Empty(t.Warnings);
    }

    // ══════════════════ 4) الربط بأمر الإنتاج ══════════════════

    [Fact]
    public void Order_Context_Auto_Fills_All_Header_Fields()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = BuildOrderWithProduction(host, "C1");
        using var scope = host.Services.CreateScope();
        var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var c = insp.GetOrderContext(ctx.OrderId);
        var order = db.ProductionOrders.Single(o => o.Id == ctx.OrderId);

        Assert.Equal(order.DocumentNumber, c.OrderNo);
        Assert.False(string.IsNullOrWhiteSpace(c.PlanNo));                     // خطة الإنتاج
        Assert.Equal("عميل الفحص C1", c.CustomerName);                          // العميل
        Assert.Equal("سكري خام C1", c.RawItemName);                             // الصنف الخام من تعريف التحويل
        Assert.Equal("سكري تام C1", c.FinishedProductName);                     // المنتج التام
        Assert.Equal(3000, c.ProducedQtyKg);                                    // الكمية المنتجة
        Assert.Equal("كرتون", c.ProducedUnitLabel);                             // الوحدة
        Assert.False(string.IsNullOrWhiteSpace(c.Date));                        // التاريخ
        Assert.False(string.IsNullOrWhiteSpace(c.ShiftName));                   // الوردية
        Assert.NotEmpty(c.Items);
        Assert.Equal(ctx.Lot, c.Items[0].LotId);
    }

    [Fact]
    public void Result_For_Product_Outside_Order_Is_Rejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = BuildOrderWithProduction(host, "L1");
        var other = BuildOrderWithProduction(host, "L2");

        using var scope = host.Services.CreateScope();
        var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();
        int ok = insp.GetResultTypes().First(t => t.ResultKind == InspectionResultType.KindAccepted).ResultTypeId;

        var ex = Assert.Throws<DomainException>(() => insp.ValidateResults(
            new List<InspectionResultDto> { new() { ResultTypeId = ok, Qty = 10, UnitId = ctx.Ctn, ProductId = other.Fin } },
            ctx.OrderId, ctx.Fin));
        Assert.Contains("ليس من بنود أمر الإنتاج", ex.Message);
    }

    [Fact]
    public void Mandatory_ResultType_Must_Be_Entered_For_Item()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = BuildOrderWithProduction(host, "MND");
        using var scope = host.Services.CreateScope();
        var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();
        var types = insp.GetResultTypes();
        int ok = types.First(t => t.ResultKind == InspectionResultType.KindAccepted).ResultTypeId;
        int by = types.First(t => t.ResultKind == InspectionResultType.KindByProduct).ResultTypeId;

        Assert.True(insp.SetProfile(null, ctx.Fin, null, by, ctx.Kg, 0, isMandatory: true, 1, true).Ok);

        var ex = Assert.Throws<DomainException>(() => insp.ValidateResults(
            new List<InspectionResultDto> { new() { ResultTypeId = ok, Qty = 10, UnitId = ctx.Ctn, ProductId = ctx.Fin } },
            ctx.OrderId, ctx.Fin));
        Assert.Contains("إجبارية", ex.Message);

        // بعد إدخالها يمرّ التحقق
        insp.ValidateResults(new List<InspectionResultDto>
        {
            new() { ResultTypeId = ok, Qty = 10, UnitId = ctx.Ctn, ProductId = ctx.Fin },
            new() { ResultTypeId = by, Qty = 5, UnitId = ctx.Kg, ProductId = ctx.Fin }
        }, ctx.OrderId, ctx.Fin);
    }

    [Fact]
    public void Results_Save_And_Reload_With_Their_Own_Units_No_Kg_Roundtrip_Loss()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = BuildOrderWithProduction(host, "RT");
        using var scope = host.Services.CreateScope();
        var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();
        var quality = scope.ServiceProvider.GetRequiredService<IQualityService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var types = insp.GetResultTypes();
        int ok = types.First(t => t.ResultKind == InspectionResultType.KindAccepted).ResultTypeId;
        int by = types.First(t => t.ResultKind == InspectionResultType.KindByProduct).ResultTypeId;

        var dtos = new List<InspectionResultDto>
        {
            new() { ResultTypeId = ok, Qty = 10, UnitId = ctx.Ctn, ProductId = ctx.Fin, LotId = ctx.Lot },
            new() { ResultTypeId = by, Qty = 25, UnitId = ctx.Kg,  ProductId = ctx.Fin, LotId = ctx.Lot }
        };
        insp.ValidateResults(dtos, ctx.OrderId, ctx.Fin);

        var r = quality.SaveCheck(ctx.OrderId, null, "2026-09-03", "نهائي",
            new List<QualityItemDto> { new() { ProductId = ctx.Fin, LotId = ctx.Lot, AcceptedQtyKg = 75, RejectedQtyKg = 0, CheckedQtyKg = 75 } },
            null, new QualityLabDto { Decision = "Passed" });
        Assert.True(r.Ok, r.Message);

        // حفظ النتائج بوحدتها كما هي
        foreach (var d in dtos)
            db.InspectionResults.Add(new InspectionResult
            {
                CheckId = r.Id, ProductId = d.ProductId, LotId = d.LotId,
                ResultTypeId = d.ResultTypeId, Qty = (decimal)d.Qty, UnitId = d.UnitId,
                UnitLabel = insp.UnitName(d.UnitId.Value)
            });
        db.SaveChanges();

        // القراءة: 10 كرتون تبقى 10 كرتون — لا 75 (عيب النسخة السابقة)
        var saved = db.InspectionResults.Where(x => x.CheckId == r.Id).ToList();
        Assert.Equal(10m, saved.Single(x => x.ResultTypeId == ok).Qty);
        Assert.Equal(ctx.Ctn, saved.Single(x => x.ResultTypeId == ok).UnitId);
        Assert.Equal(25m, saved.Single(x => x.ResultTypeId == by).Qty);
        Assert.Equal(ctx.Kg, saved.Single(x => x.ResultTypeId == by).UnitId);

        var recomputed = insp.Compute(saved.Select(s => new InspectionResultDto
        { ResultTypeId = s.ResultTypeId, Qty = (double)s.Qty, UnitId = s.UnitId }).ToList());
        Assert.Equal(10, recomputed.ByUnit.Single(u => u.UnitLabel == "كرتون").Accepted);
        Assert.Equal(25, recomputed.ByUnit.Single(u => u.UnitLabel == "كجم").ByProduct);
    }

    // ══════════════════ 5) أكثر من صنف ومنتج بنتائج مختلفة ══════════════════

    [Fact]
    public void Two_Products_With_Different_Result_Sets_And_Units()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var a = BuildOrderWithProduction(host, "P1");
        var b = BuildOrderWithProduction(host, "P2");
        int pcs = Unit(host, "حبة");

        using var scope = host.Services.CreateScope();
        var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();

        // نوع جديد وحدته العامة «كرتون»، ثم يُخصَّص للمنتج الثاني بوحدة مختلفة تماماً («حبة»)
        var newType = insp.SaveResultType(null, null, "عبوات فردية", "Accepted", a.Ctn, true, false, true, false, 50, true);
        Assert.True(newType.Ok, newType.Message);
        Assert.True(insp.SetProfile(null, b.Fin, null, newType.Id, pcs, 0, false, 1, true).Ok);

        var forA = insp.GetAllowedResultTypesForItem(a.Fin);
        var forB = insp.GetAllowedResultTypesForItem(b.Fin);

        // المنتج الثاني يحمل النوع الجديد بوحدة «حبة» — والأول بوحدة النوع العامة
        Assert.Equal("حبة", forB.Single(x => x.ResultTypeId == newType.Id).UnitLabel);
        Assert.NotEqual(
            forA.Single(x => x.ResultTypeId == newType.Id).UnitLabel,
            forB.Single(x => x.ResultTypeId == newType.Id).UnitLabel);

        // فحص فعلي للمنتجين بنتائج مختلفة في آن واحد
        int ok = insp.GetResultTypes().First(t => t.ResultKind == InspectionResultType.KindAccepted).ResultTypeId;
        var mixed = new List<InspectionResultDto>
        {
            new() { ResultTypeId = ok, Qty = 100, UnitId = a.Ctn, ProductId = a.Fin },
            new() { ResultTypeId = newType.Id, Qty = 500, UnitId = pcs, ProductId = b.Fin }
        };
        insp.ValidateResults(mixed);
        var t = insp.Compute(mixed);
        Assert.Equal(2, t.ByUnit.Count);
        Assert.Equal(100, t.ByUnit.Single(u => u.UnitLabel == "كرتون").Accepted);
        Assert.Equal(500, t.ByUnit.Single(u => u.UnitLabel == "حبة").Accepted);
        Assert.False(t.SingleUnit);
    }

    // ══════════════════ 6) حراسة بنيوية: لا أسماء نتائج في كود الواجهة ══════════════════

    [Fact]
    public void Screen_Code_Contains_No_Hardcoded_ResultType_Names_Or_Units()
    {
        string root = FindRepoRoot();
        string[] uiFiles =
        {
            Path.Combine(root, "src/DatesErp.Desktop/Views/Screens/QualityView.xaml"),
            Path.Combine(root, "src/DatesErp.Desktop/Views/Screens/QualityView.xaml.cs")
        };
        foreach (var f in uiFiles) Assert.True(File.Exists(f), "ملف مفقود: " + f);

        // أسماء نتائج كانت ستُثبَّت في الكود
        string[] resultNames = { "منسم", "حشف", "نوى", "مكسور", "مخلفات فرز" };
        foreach (var f in uiFiles)
        {
            string src = File.ReadAllText(f);
            foreach (var name in resultNames)
                Assert.DoesNotContain(name, src);
        }

        // لا وحدات مفروضة في ترويسات الأعمدة (كان: «المقبول (كرتون)» / «مكافئ (كجم)»)
        string xaml = File.ReadAllText(uiFiles[0]);
        Assert.DoesNotContain("(كرتون)", xaml);
        Assert.DoesNotContain("(كجم)", xaml);
        // ولا وزن كرتون افتراضي مثبّت (كان: PackWeight > 0 ? PackWeight : 7.5)
        Assert.DoesNotContain("7.5", File.ReadAllText(uiFiles[1]));
    }

    [Fact]
    public void Quality_Screen_Xaml_Has_No_Vertical_ScrollViewer()
    {
        string xaml = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/DatesErp.Desktop/Views/Screens/QualityView.xaml"));
        // §قاعدة التصميم: شاشة واحدة بلا صعود ونزول
        Assert.DoesNotContain("<ScrollViewer", xaml);
        // والجدول يأخذ المساحة المتبقية لا ارتفاعاً ثابتاً
        Assert.DoesNotContain("ResultsGrid\" Height=", xaml);
        Assert.Contains("SelectionUnit=\"Cell\"", xaml);   // نقر مفرد يبدأ التحرير
    }

    // §B69: نافذة الوحدات حُذفت لإعادة التصميم — يُعاد هذا الحارس مع النافذة الجديدة

    [Fact]
    public void Closing_And_Reports_Contain_No_Hardcoded_ByProduct_Names()
    {
        string root = FindRepoRoot();
        string[] files =
        {
            "src/DatesErp.Desktop/Views/Screens/OrdersWindows.cs",
            "src/DatesErp.Application/Services/ReportServiceOperations.cs",
            "src/DatesErp.Application/Services/ReportServiceProfessional.cs",
        };
        string[] names = { "منسم", "حشف", "نوى", "مخلفات فرز" };

        foreach (var rel in files)
        {
            string path = Path.Combine(root, rel);
            Assert.True(File.Exists(path), "ملف مفقود: " + path);
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string code = line.TrimStart();
                // التعليقات تشرح منطق التوافق فتذكر الأسماء — لا تُعدّ تثبيتاً في الكود
                if (code.StartsWith("//") || code.StartsWith("///") || code.StartsWith("*")) continue;
                // §الاستثناء الوحيد: مطابقة اسم العمود القديم للبيانات السابقة للديناميكية
                bool isLegacyMatch = line.Contains(".Contains(\"") &&
                    (line.Contains("HashfKg") || line.Contains("NawaKg") || line.Contains("+= h") || line.Contains("+= n")
                     || line.Contains("+=") && (line.Contains("h;") || line.Contains("n;")) || line.Contains("else if"));
                if (isLegacyMatch) continue;
                foreach (var n in names)
                    Assert.False(line.Contains(n),
                        $"{rel}:{i + 1} يحمل اسم مخرج مثبّت «{n}» — الأسماء يجب أن تأتي من جدول ByProducts.\n{line.Trim()}");
            }
        }

        // عناوين التقارير لم تعد تسمّي مخرجات بعينها
        string prof = File.ReadAllText(Path.Combine(root, files[files.Length - 1]));
        Assert.DoesNotContain("المخرجات الثانوية والهالك (حشف", prof);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
