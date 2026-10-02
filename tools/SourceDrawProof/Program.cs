using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
// يتحقق من تحصيل «سحب الخام من الشحنة» أثناء تعديل/إضافة بند خطة:
// سياق الوحدة/الكمية من سطر الاستلام، التحويل كيلو↔وحدات، حارس المتاح،
// والاحتفاظ بالتتبع (كمية + وحدة + وزن + شحنة + دفعة) في البند.
var dbFile = args.FirstOrDefault() ?? "/home/user/.cache/op-logs/source_draw_acceptance.db";
if (File.Exists(dbFile)) File.Delete(dbFile);
int count = 0;
void Check(bool ok, string text) { if (!ok) throw new Exception("FAIL: " + text); count++; Console.WriteLine("PASS: " + text); }
bool Eq(double a, double b) => Math.Abs(a - b) < .01;
void Ok(OpResult r, string label) => Check(r.Ok, label + (r.Ok ? "" : ": " + r.Message));

var services = new ServiceCollection()
    .AddSingleton(TimeProvider.System)
    .AddDatesErpInfrastructure(o => o.UseSqlite($"Data Source={dbFile}"))
    .AddScoped<IAuditService, AuditService>().AddScoped<IAuthService, AuthService>()
    .AddScoped<IReceivingService, ReceivingService>().AddScoped<IPlanningService, PlanningService>()
    .AddScoped<IProductionOrderService, ProductionOrderService>()
    .AddScoped<ICapacityService, CapacityService>().AddScoped<IShiftService, ShiftService>()
    .AddScoped<IReportService, ReportService>()
    .AddScoped<MasterDataService>()
    .BuildServiceProvider();
using (var s = services.CreateScope())
{
    var db = s.ServiceProvider.GetRequiredService<DatesErpDbContext>();
    db.Database.EnsureCreated(); DbSeeder.Seed(db);
    Check(s.ServiceProvider.GetRequiredService<IAuthService>().Login("admin", DbSeeder.InitialAdminPassword).Success, "Admin login");
}
int finished = 0, rawId = 0, lotId = 0;
using (var s = services.CreateScope())
{
    var sp = s.ServiceProvider; var db = sp.GetRequiredService<DatesErpDbContext>();
    var master = sp.GetRequiredService<MasterDataService>();
    Ok(sp.GetRequiredService<IShiftService>().SaveShift(1, "وردية الصباح", "06:00", "14:00", 8, 0, 8), "Shift");
    rawId = master.SaveProductFull(null, "SDW-RAW", "سكري خام", "001", "Raw", "كجم", 20, 0, 0, null!).Id;
    finished = master.SaveProductFull(null, "SDW-8", "سكري", "002", "Finished", "كرتون", 8, 1, 8, new() { (1, null!, 9000) }, rawId).Id;

    var receiving = sp.GetRequiredService<IReceivingService>();
    var ship = receiving.SaveShipment(1, null, null, new() { new ShipmentItemDto
        { ProductId = rawId, QtyKg = 25000, PackageCount = 1000, UnitWeightKg = 25, ReceiptUnit = "سلة", TreatmentRequired = false } });
    Ok(ship, "استلام: 1000 سلة / 25000 كجم");
    Ok(receiving.ApproveShipment(ship.Id), "اعتماد الاستلام");
    lotId = db.Lots.AsNoTracking().Single(l => l.ShipmentId == ship.Id).Id;
    Check(lotId > 0, "الدفعة أُنشئت من سطر الاستلام");
}

using (var s = services.CreateScope())
{
    var sp = s.ServiceProvider; var db = sp.GetRequiredService<DatesErpDbContext>();
    var svc = sp.GetRequiredService<IPlanningService>();
    var day = db.BusinessNow.Date; string D(DateTime x) => x.ToString("dd/MM/yyyy");

    // 1) سياق كمية الشحنة/الدفعة — من سطر الاستلام لا من اسم الوحدة.
    var ctx = svc.GetShipmentQuantityContext(lotId);
    Check(ctx.ReceiptUnit == "سلة", "وحدة الاستلام قُرئت من سطر الاستلام: " + ctx.ReceiptUnit);
    Check(Eq(ctx.UnitWeightKg, 25), "وزن الوحدة = 25 كجم");
    Check(Eq(ctx.ReceivedUnits, 1000), "المستلم 1000 سلة");
    Check(Eq(ctx.ReceivedKg, 25000), "المستلم 25000 كجم");
    Check(Eq(ctx.AvailableKg, 25000), "المتاح 25000 كجم");
    Check(Eq(ctx.AvailableUnits, 1000), "المتاح 1000 وحدة");
    Check(Eq(ctx.UsedKg, 0), "المستهلك 0 قبل التخطيط");

    // خطط الإنتاج التام تستهدف كميات صغيرة حتى لا تُستنزف دفعة 25000 كجم.
    PlanItemDto Item(double unitQty = 0, double kg = 0, string unit = null) => new()
    {
        SourceType = "FromReceiving", LotId = lotId, ShipmentId = ctx.ShipmentId, CustomerId = 1,
        ProductId = finished, SelectedRawProductId = rawId,
        PlannedCartons = 125, PlannedQtyKg = 1000, ScheduledDate = D(day), SuggestedShiftId = 1, SuggestedLineId = 1,
        SourceUnit = unit, SourceQtyInUnit = unitQty, SourceQtyKg = kg
    };

    // 0) مسار التحكم: بند بلا سحب خام — يجب أن يُحفظ كما كان، ولا يتأثر بإعادة التهيكل.
    var control = svc.SavePlan("خطة تحكم بلا سحب", "Daily", D(day), D(day), 1, 1, new() { Item() });
    Ok(control, "خطة تحكم (بلا سحب خام) تُحفظ");
    var ctrlItem = db.ProductionPlanItems.AsNoTracking().Single(i => i.PlanId == control.Id);
    Check(Eq(ctrlItem.SourceQtyKg, 0) && Eq(ctrlItem.SourceQtyInUnit, 0) && ctrlItem.SourceUnit == null,
        "بلا سحب: حقول السحب صفرية/فارغة (لا أثر على المسار القائم)");

    // 2) سحب بالوحدة (500 سلة) → الوزن المكافئ 12500 كجم، ويُخزَّن التتبع كاملاً.
    var plan1 = svc.SavePlan("خطة سحب 500 سلة", "Daily", D(day), D(day), 1, 1, new() { Item(unitQty: 500, unit: "سلة") });
    Ok(plan1, "خطة 1: سحب 500 سلة");
    var pi1 = db.ProductionPlanItems.AsNoTracking().Single(i => i.PlanId == plan1.Id);
    Check(pi1.SourceUnit == "سلة", "حُفظت وحدة السحب: " + pi1.SourceUnit);
    Check(Eq(pi1.SourceQtyInUnit, 500), "حُفظت كمية السحب 500");
    Check(Eq(pi1.SourceUnitWeightKg, 25), "حُفظ وزن الوحدة 25");
    Check(Eq(pi1.SourceQtyKg, 12500), "حُفظ الوزن المكافئ 12500 كجم");
    Console.WriteLine($"  → التتبع: {pi1.SourceQtyInUnit:N0} {pi1.SourceUnit} من الدفعة {lotId} = {pi1.SourceQtyKg:N0} كجم");

    // 3) سحب بالوزن (12500 كجم) → يُحسب مكافئه بالوحدات (500).
    var plan2 = svc.SavePlan("خطة سحب 12500 كجم", "Daily", D(day), D(day), 1, 1, new() { Item(kg: 12500) });
    Ok(plan2, "خطة 2: سحب بالكيلو");
    var pi2 = db.ProductionPlanItems.AsNoTracking().Single(i => i.PlanId == plan2.Id);
    Check(Eq(pi2.SourceQtyKg, 12500), "خطة 2: حُفظ الكيلو 12500");
    Check(Eq(pi2.SourceQtyInUnit, 500), "خطة 2: اُشتقّت الوحدات = 500 سلة");
    Check(Eq(pi2.SourceUnitWeightKg, 25), "خطة 2: وزن الوحدة 25");

    // 4) الوحدة الممرَّرة لا تفرض طبيعة الصنف — وحدة سطر الاستلام تسبق الاسم.
    var plan3 = svc.SavePlan("خطة وحدة مُمرَّرة مغايرة", "Daily", D(day), D(day), 1, 1, new() { Item(unitQty: 300, unit: "كرتون") });
    Ok(plan3, "خطة 3: تمرير وحدة مغايرة");
    Check(db.ProductionPlanItems.AsNoTracking().Single(i => i.PlanId == plan3.Id).SourceUnit == "سلة",
        "الوحدة المحفوظة من سطر الاستلام (سلة) لا من الاسم المُمرَّر");

    // 5) حارس المتاح: سحب 1500 سلة (37500 كجم) > المتاح (بعد 3 خطط = 22000) → رفض صارخ.
    var overflowRes = svc.SavePlan("خطة تجاوز", "Daily", D(day), D(day), 1, 1, new() { Item(unitQty: 1500) });
    Check(!overflowRes.Ok && overflowRes.Message.Contains("تجاوز المتاح"),
        "حارس المتاح رفض التجاوز — " + (overflowRes.Message?.Split('\n')[0] ?? overflowRes.Message));

    // 6) بعد الخطط: المتاح = 25000 − (4 × 1000 حجز) = 21000 كجم = 840 سلة.
    var after = svc.GetShipmentQuantityContext(lotId);
    Check(Eq(after.AvailableKg, 21000), "المتاح بعد الخطط 21000 كجم");
    Check(Eq(after.AvailableUnits, 840), "المتاح بعد الخطط 840 سلة");
    Check(Eq(after.UsedKg, 4000), "المستهلك/المحجوز 4000 كجم");
    Console.WriteLine($"  → سياق بعد التخطيط: متاح {after.AvailableKg:N0} كجم / {after.AvailableUnits:N0} {after.ReceiptUnit}؛ مستهلك {after.UsedKg:N0} كجم");
    Console.WriteLine("السحوبات الموثقة (كمية + وحدة + وزن + شحنة + دفعة) قابلة للتعقب.");

    // ═══════════ §المعالجة — منع الإنتاج قبل تاريخ اكتمالها، وإتاحته بعده ═══════════
    int lotT;
    using (var sc = services.CreateScope())
    {
        var spX = sc.ServiceProvider; var dbt = spX.GetRequiredService<DatesErpDbContext>();
        var receiving = spX.GetRequiredService<IReceivingService>();
        var shipT = receiving.SaveShipment(1, null, null, new() { new ShipmentItemDto
            { ProductId = rawId, QtyKg = 25000, PackageCount = 1000, UnitWeightKg = 25, ReceiptUnit = "سلة", TreatmentRequired = false } });
        Ok(receiving.ApproveShipment(shipT.Id), "اعتماد استلام (لاختبار المعالجة)");
        lotT = dbt.Lots.AsNoTracking().Single(l => l.ShipmentId == shipT.Id).Id;
        // نسجّل كامل الكمية تحت المعالجة + دورة معالجة تكتمل بعد 3 أيام.
        var lt = dbt.Lots.Single(l => l.Id == lotT);
        lt.UnderTreatmentQtyKg = lt.InStockQtyKg;
        dbt.RawTreatments.Add(new RawTreatment { LotId = lotT, QtyKg = lt.InStockQtyKg, PackageCount = 1000,
            StartedAt = dbt.BusinessNow, ExpectedReadyAt = dbt.BusinessNow.AddDays(3), Status = "InProgress" });
        dbt.SaveChanges();
    }
    using (var sc = services.CreateScope())
    {
        var spT = sc.ServiceProvider; var dbT = spT.GetRequiredService<DatesErpDbContext>();
        var svcT = spT.GetRequiredService<IPlanningService>();
        var dayT = dbT.BusinessNow.Date.AddDays(1); string fmt(DateTime x) => x.ToString("dd/MM/yyyy");
        var ctxT = svcT.GetShipmentQuantityContext(lotT);
        Check(Eq(ctxT.AvailableKg, 0) && ctxT.AvailableUnits < 0.001, "تحت المعالجة: المتاح الآن 0 (لم يُفرج بعد)");
        PlanItemDto TItem(string date) => new()
        {
            SourceType = "FromReceiving", LotId = lotT, ShipmentId = ctxT.ShipmentId, CustomerId = 1,
            ProductId = finished, SelectedRawProductId = rawId,
            PlannedCartons = 100, PlannedQtyKg = 800, ScheduledDate = date, SuggestedShiftId = 1, SuggestedLineId = 1,
            SourceUnit = "سلة", SourceQtyKg = 800
        };
        // قبل اكتمال المعالجة → يُمنع (المتاح 0 حتى وإن كان التاريخ قريباً).
        var before = svcT.SavePlan("خطة قيد معالجة (قبل التاريخ)", "Daily", fmt(dayT), fmt(dayT), 1, 1, new() { TItem(fmt(dayT)) });
        Check(!before.Ok && before.Message.Contains("تجاوز المتاح"), "قبل تاريخ المعالجة: الإنتاج مرفوض (المتاح 0) — " + (before.Message?.Split('\n')[0]));
        // بعد اكتمال المعالجة → يُسمح (تُضاف الكمية الناضجة للمتاح حسب التاريخ).
        var afterT = svcT.SavePlan("خطة بعد المعالجة", "Daily", fmt(dayT), fmt(dayT.AddDays(4)), 1, 1, new() { TItem(fmt(dayT.AddDays(4))) });
        Ok(afterT, "بعد تاريخ المعالجة: الإنتاج مسموح (تُحتسب الناضجة)");
        Console.WriteLine("  → ربط المعالجة: قبل التاريخ يُمنع، بعده يُتاح للكمية الناضجة.");
    }
}

// ═══════════ §تتبع الشحنة — رحلة كاملة عبر 3 أوامر إنتاج بمخرجات مختلفة حتى نهاية الرصيد ═══════════
using (var sc = services.CreateScope())
{
    var sp = sc.ServiceProvider; var db = sp.GetRequiredService<DatesErpDbContext>();
    var receiving = sp.GetRequiredService<IReceivingService>();
    var shipTr = receiving.SaveShipment(1, null, null, new() { new ShipmentItemDto
        { ProductId = rawId, QtyKg = 20000, PackageCount = 800, UnitWeightKg = 25, ReceiptUnit = "سلة", TreatmentRequired = false } });
    Ok(receiving.ApproveShipment(shipTr.Id), "تتبع: اعتماد استلام 800 سلة / 20000 كجم");
    var lotTr = db.Lots.AsNoTracking().Single(l => l.ShipmentId == shipTr.Id);

    // أنواع مخرجات الجودة (سليم/منسم/مخلفات) — قابلة للتعريف.
    var rtOk = db.InspectionResultTypes.Add(new InspectionResultType { Code = "RT-SL", NameAr = "تام سليم", ResultKind = "Accepted", IsFinishedGood = true, EntersInventory = true, UnitLabel = "كرتون", IsActive = true }).Entity;
    var rtBy = db.InspectionResultTypes.Add(new InspectionResultType { Code = "RT-MN", NameAr = "منسم", ResultKind = "Accepted", IsFinishedGood = true, EntersInventory = true, UnitLabel = "كرتون", IsActive = true }).Entity;
    var rtWs = db.InspectionResultTypes.Add(new InspectionResultType { Code = "RT-WS", NameAr = "مخلفات", ResultKind = "ByProduct", IsByProduct = true, CountsAsLoss = true, UnitLabel = "كجم", IsActive = true }).Entity;
    db.SaveChanges();

    // بنود سحب موثّقة (وحدة+كمية+وزن) عبر 3 أوامر بمخرجات مختلفة — ثم نهاية الرصيد.
    var draw = new (double units, double kg, int cartons, double producedKg, int producedCartons, int customer)[]
    {
        (300, 7500, 250, 2500, 250, 1),
        (200, 5000, 200, 1900, 190, 2),
        (100, 2500, 100, 950, 95, 3)
    };
    var orderIds = new List<int>();
    for (int i = 0; i < draw.Length; i++)
    {
        var d = draw[i];
        var plan = new ProductionPlan { DocumentNumber = $"PLN-TRACE-{i + 1}", PlanTitle = $"خطة تتبع {i + 1}", Status = "Approved", IsApproved = true };
        db.ProductionPlans.Add(plan); db.SaveChanges();
        var pi = new ProductionPlanItem
        {
            PlanId = plan.Id, SourceType = "FromReceiving", LotId = lotTr.Id, ShipmentId = shipTr.Id, CustomerId = d.customer,
            ProductId = finished, PackagingTypeId = null, PlannedQtyKg = d.kg, PlannedCartons = d.cartons,
            ScheduledDate = DateTime.Today.AddDays(i).Date, SuggestedShiftId = 1, SuggestedLineId = 1,
            SourceUnit = "سلة", SourceQtyInUnit = d.units, SourceUnitWeightKg = 25, SourceQtyKg = d.kg,
            Status = "Approved"
        };
        db.ProductionPlanItems.Add(pi); db.SaveChanges();
        var order = new ProductionOrder { DocumentNumber = $"OP-T{i + 1}", SourceType = "FromPlan", SourcePlanId = plan.Id,
            CustomerId = d.customer, ProductionDate = DateTime.Today.AddDays(i).Date, ShiftId = 1, LineId = 1,
            Status = "Approved", IsApproved = true };
        db.ProductionOrders.Add(order); db.SaveChanges();
        db.ProductionOrderItems.Add(new ProductionOrderItem
        {
            OrderId = order.Id, PlanItemId = pi.Id, LotId = lotTr.Id, ShipmentId = shipTr.Id, CustomerId = d.customer,
            ProductId = finished, PlannedQtyKg = d.kg, PlannedCartons = d.cartons,
            ProducedQtyKg = d.producedKg, ProducedCartons = d.producedCartons, Status = "Approved", CartonWeightKg = 8, MoldsCount = 1
        });
        orderIds.Add(order.Id);
    }
    db.SaveChanges();

    // مخرجات جودة معتمدة لأمرين (سليم + منسم + مخلفات) بمختلف القيم.
    foreach (var (orderIdx, okCtn, mnCtn, wsKg) in new[] { (0, 2500, 100, 200), (1, 1900, 80, 150) })
    {
        var qc = new QualityCheck { OrderId = orderIds[orderIdx], DocumentNumber = $"QC-T{orderIdx + 1}", CheckDate = DateTime.Today, Status = "Approved", IsApproved = true,
            TotalCheckedKg = 2500, AcceptedKg = 2500, RejectedKg = 0, TotalCheckedCartons = okCtn + mnCtn, AcceptedCartons = okCtn, RejectedCartons = mnCtn };
        db.QualityChecks.Add(qc); db.SaveChanges();
        db.InspectionResults.Add(new InspectionResult { CheckId = qc.Id, ProductId = finished, ResultTypeId = rtOk.Id, Qty = okCtn, UnitLabel = "كرتون" });
        db.InspectionResults.Add(new InspectionResult { CheckId = qc.Id, ProductId = finished, ResultTypeId = rtBy.Id, Qty = mnCtn, UnitLabel = "كرتون" });
        db.InspectionResults.Add(new InspectionResult { CheckId = qc.Id, ProductId = finished, ResultTypeId = rtWs.Id, Qty = wsKg, UnitLabel = "كجم" });
        db.SaveChanges();
    }

    // نهاية الرصيد: استُهلك كامل الكمية — الدفعة مستنفدة.
    var lotRaw = db.Lots.Single(l => l.Id == lotTr.Id);
    lotRaw.InStockQtyKg = 0; lotRaw.ProducedQtyKg = 5350; lotRaw.DeliveredQtyKg = 0;
    db.SaveChanges();

    // التقرير: رحلة الشحنة كاملة.
    var reports = sp.GetRequiredService<IReportService>();
    var rr = reports.Run("shipment_full", new Dictionary<string, string> { ["shipment"] = shipTr.Id.ToString() });
    int header = rr.Rows.Count(x => Convert.ToString(x[0]) == "🚢 الشحنة");
    int orderRows = rr.Rows.Count(x => Convert.ToString(x[0]).StartsWith("📝 2."));
    int outRows = rr.Rows.Count(x => Convert.ToString(x[0]).StartsWith("🏭 مخرجات الأمر"));
    var remainRow = rr.Rows.FirstOrDefault(x => Convert.ToString(x[0]).Contains("المتبقي الآن"));
    bool exhaustedShown = remainRow != null && Convert.ToString(remainRow[9]).Contains("مستنفدة");
    bool qualityShown = rr.Rows.Any(x => Convert.ToString(x[0]).StartsWith("🏭 مخرجات الأمر") && Convert.ToString(x[9]).Contains("مخرجات الجودة المعتمدة"));
    bool unitShown = rr.Rows.Any(x => Convert.ToString(x[0]) == "📥 1. الدخول" && Convert.ToString(x[9]).Contains("سلة"));
    Check(header == 1, "التقرير يعرض ترويسة الشحنة");
    Check(orderRows == 3, $"التقرير يعرض أوامر الإنتاج كسجلات مستقلة (3): {orderRows}");
    Check(outRows == 3, $"كل أمر يعرض مخرجاته كسجل مستقل (3): {outRows}");
    Check(qualityShown, "المخرجات تُقرأ من نتائج الجودة الفعلية (سليم/منسم/مخلفات)");
    Check(unitShown, "وحدة الاستلام الأصلية محفوظة (سلة) — تُعرض مع الوزن المكافئ");
    Check(exhaustedShown, "نهاية الشحنة: تُظهر 'مستنفدة' عند نفاد الرصيد الفعلي");
    Check(rr.Summary.TryGetValue("كم دخلت (إجمالي الاستلام)", out var dk) && dk.Replace(",", "").Contains("20000"), "ملخص الدخول = 20000 كجم");
    // §قاعدة الوحدات المعتمدة: بعد خطط الإنتاج الكرتون هو وحدة التداول، ودخول المخازن بكم وبوزن.
    Check(rr.StageColumn == "المرحلة", "الشكل الموحد: التقرير يعرّف عمود المرحلة (تلوين الصفوف)");
    Check(!string.IsNullOrWhiteSpace(rr.Equation), "الشكل الموحد: التقرير يعرّف معادلة الإقفال (شريط أسفل الجدول)");
    bool cartonRule = rr.Rows.Any(x => Convert.ToString(x[9]).Contains("كرتون ×"));
    Check(cartonRule, "قاعدة الوحدات: المنتج التام يُعرض بالكرتون بوزنه (كرتون × وزن = إجمالي)");
    bool entryRule = rr.Rows.Any(x => Convert.ToString(x[9]).Contains("دخل ") && Convert.ToString(x[9]).Contains("كجم"));
    Check(entryRule, "قاعدة المخازن: الدخول يُعرض «كم دخل وبوزن كم»");
    Check(ReportUnits.CartonWithWeight(272, 25, 6800).Contains("272 كرتون × 25.0 كجم = 6,800.0 كجم"),
        "قاعدة الوحدات: صيغة «الكرتون بوزنه» موحّدة في كل التقارير");
    Check(ReportUnits.WarehouseEntry(6800, 272).Contains("دخل 6,800.0 كجم (272 كرتون)"),
        "قاعدة المخازن: صيغة «دخل ... كجم» موحّدة في كل التقارير");
    Check(ReportUnits.TidyLine("أ", "", "ب") == "أ · ب", "قاعدة الترتيب: الصف يُبنى مرتّباً بلا فراغات مكررة");
    Console.WriteLine($"  → الشكل الموحد وقاعدة الوحدات: مُطبَّقان على التقرير.");
    Console.WriteLine($"  → تتبع الشحنة عبر 3 أوامر بمخرجات مختلفة: رحلة كاملة حتى نهاية الرصيد.");
}
Console.WriteLine($"DONE: {count} passed; FILE database: {dbFile}");
