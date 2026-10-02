// ═══════════════════════════════════════════════════════════════════════════
//  DateERP — مشغّل قبول كامل الدورة
//
//  لماذا هذه الأداة موجودة:
//    التقارير كانت تُكتب عن الكود والاختبارات، والمستخدم يدخل الشاشة فلا يجد
//    ما وُعد به. و25 استدعاء اختبار كانت تسلك StartExecution/CompleteExecution
//    وهما ميتان — فالاختبارات خضراء والمسار الحقيقي غير مُختبَر.
//
//  ما تفعله:
//    تسلك نفس الدوال التي تناديها الشاشات فعلياً — لا المسار الميت — خطوة بخطوة،
//    وتتحقق من الأثر في قاعدة البيانات بعد كل خطوة (لا تكتفي بـ OpResult.Ok).
//
//  الاستعمال:
//    dotnet run --project tools/AcceptanceRunner            ← قاعدة مؤقتة في الذاكرة
//    dotnet run --project tools/AcceptanceRunner -- --db /tmp/x.db   ← قاعدة ملف
//
//  رمز الخروج: 0 = كل الخطوات نجحت · 1 = فشل خطوة أو أكثر
// ═══════════════════════════════════════════════════════════════════════════

using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Session;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DateERP.Acceptance;

public static class Program
{
    private static int _pass, _fail;
    private static readonly List<string> _failures = new();

    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string dbPath = null;
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--db") dbPath = args[i + 1];

        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  DateERP — مشغّل قبول كامل الدورة                                            ║");
        Console.WriteLine("║  يسلك مسار الشاشات الفعلي ويتحقق من الأثر في قاعدة البيانات بعد كل خطوة      ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        SqliteConnection keepAlive = null;
        var services = new ServiceCollection();
        if (dbPath == null)
        {
            keepAlive = new SqliteConnection("Data Source=:memory:");
            keepAlive.Open();
            var conn = keepAlive;
            services.AddDatesErpInfrastructure(o => o.UseSqlite(conn));
        }
        else
        {
            services.AddDatesErpInfrastructure(o => o.UseSqlite($"Data Source={dbPath}"));
        }
        services.AddScoped<IAuditService, AuditService>()
                .AddScoped<IAuthService, AuthService>()
                .AddScoped<IReceivingService, ReceivingService>()
                .AddScoped<IPlanningService, PlanningService>()
                .AddScoped<IProductionOrderService, ProductionOrderService>()
                .AddScoped<IExecutionService, ExecutionService>()
                .AddScoped<IQualityService, QualityService>()
                .AddScoped<IInspectionService, InspectionService>()
                .AddScoped<IFinishedGoodsService, FinishedGoodsService>()
                .AddScoped<ICustomerDeliveryService, CustomerDeliveryService>()
                .AddScoped<IInventoryService, InventoryService>()
                .AddScoped<IAdminService, AdminService>()
                .AddScoped<IReportService, ReportService>()
                .AddScoped<IBackupService, BackupService>()
                .AddScoped<MasterDataService>()
                .AddScoped<ICapacityService, DatesErp.Application.Services.CapacityService>()
                .AddScoped<IShiftService, DatesErp.Application.Services.ShiftService>()
                .AddScoped<IPlanProgressService, DatesErp.Application.Services.PlanProgressService>()
                .AddScoped<ITraceabilityService, DatesErp.Application.Services.TraceabilityService>()
                .AddScoped<MachineRegistry>();
        var sp = services.BuildServiceProvider();

        try
        {
            Run(sp);
        }
        catch (Exception ex)
        {
            Fail("استثناء غير متوقع أوقف المشغّل", ex.Message);
        }

        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════════════════════════════════════════");
        Console.WriteLine($"  النتيجة: {_pass} نجحت · {_fail} فشلت · من {_pass + _fail} خطوة");
        if (_fail > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  ✗ الخطوات الفاشلة:");
            foreach (var f in _failures) Console.WriteLine("     • " + f);
            Console.WriteLine();
            Console.WriteLine("  هذه إخفاقات حقيقية في المسار الذي يسلكه المستخدم — لا تُسلَّم نسخة قبل إصلاحها.");
        }
        else
        {
            Console.WriteLine("  ✓ كامل الدورة يعمل على المسار الفعلي.");
        }
        Console.WriteLine("══════════════════════════════════════════════════════════════════════════════");

        keepAlive?.Dispose();
        return _fail == 0 ? 0 : 1;
    }

    // ───────────────────────── الدورة ─────────────────────────

    private static void Run(ServiceProvider sp)
    {
        // ═══ 1) الإقلاع ═══
        Section("1. الإقلاع وإنشاء القاعدة");
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            Check("إنشاء مخطط القاعدة", () => { db.Database.EnsureCreated(); return db.Database.CanConnect(); });
            Check("بذر البيانات الأولية", () => { DbSeeder.Seed(db); return true; });
            // §يُحاكي Bootstrapper: كتالوج الصلاحيات كان يُبذر عند فتح شاشته فقط (عطل أُصلح في B37)
            Check("كتالوج الصلاحيات (كما يفعل الإقلاع)", () =>
            {
                new PermissionService(db, new SessionContext()).EnsureCatalog();
                int n = db.PermissionResources.Count();
                return Report($"موارد الصلاحيات = {n} (الكتالوج 21)", n >= 20);
            });
            Check("جداول القاعدة ≥ 60", () =>
            {
                int n = db.Model.GetEntityTypes().Count(t => !string.IsNullOrEmpty(t.GetTableName()));
                return Report($"عدد الجداول = {n}", n >= 60);
            });
        }

        // ═══ 2) تسجيل الدخول ═══
        Section("2. تسجيل الدخول");
        using (var scope = sp.CreateScope())
        {
            var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
            Check("دخول المدير", () =>
            {
                var r = auth.Login("admin", DbSeeder.InitialAdminPassword);
                return Report(r.Success ? "تم الدخول" : r.Message, r.Success);
            });
            // (الجلسة تُقرأ في الخطوة التالية مباشرة)
            Check("الجلسة تحمل صلاحيات", () =>
            {
                var s = sp.GetRequiredService<SessionContext>();
                return Report($"صلاحيات = {s.Can("quality", "Create")}", s.Can("quality", "Create"));
            });
        }

        // ═══ 3) الإعدادات: نتائج الفحص وتحويلات الوحدات ═══
        Section("3. الإعدادات — نتائج الفحص وتحويلات الوحدات (§لا ثوابت في الكود)");
        int kgId = 0, ctnId = 0, typeOk = 0, typeBy = 0;
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();
            kgId = db.UnitsOfMeasure.Single(u => u.UnitNameAr == "كجم").Id;
            ctnId = db.UnitsOfMeasure.Single(u => u.UnitNameAr == "كرتون").Id;

            Check("أنواع نتائج الفحص مبذورة من القاعدة", () =>
            {
                var t = insp.GetResultTypes();
                return Report($"{t.Count} نوع: {string.Join("، ", t.Select(x => x.NameAr).Take(4))}…", t.Count >= 3);
            });
            typeOk = insp.GetResultTypes().First(t => t.ResultKind == InspectionResultType.KindAccepted).ResultTypeId;
            typeBy = insp.GetResultTypes().First(t => t.ResultKind == InspectionResultType.KindByProduct).ResultTypeId;

            Check("إضافة نوع نتيجة جديد من الإعدادات", () =>
            {
                var r = insp.SaveResultType(null, null, "تمر مطحون (اختبار)", "مخرج ثانوي", kgId,
                    false, true, true, false, 90, true);
                return Report(r.Ok ? r.Message.Split('\n')[0] : r.Message, r.Ok);
            });
            Check("تعريف تحويل وحدات: 1 كرتون = 7.5 كجم", () =>
            {
                var r = insp.SaveConversion(null, ctnId, kgId, 7.5m, true);
                return Report(r.Ok ? r.Message : r.Message, r.Ok);
            });
        }

        // ═══ 4) البيانات الأساسية ═══
        Section("4. البيانات الأساسية — عميل · خام · منتج تام بتعريف تحويل رسمي");
        int custId = 0, rawId = 0, finId = 0;
        using (var scope = sp.CreateScope())
        {
            var m = scope.ServiceProvider.GetRequiredService<MasterDataService>();
            Check("إنشاء عميل", () => { var r = m.SaveCustomer(null, "ACC-1", "شركة القبول", "جملة", "777", "-", true); custId = r.Id; return Report(r.Message.Split('\n')[0], r.Ok); });
            Check("إنشاء صنف خام (001)", () => { var r = m.SaveProductFull(null, "ACC-R", "سكري خام", "001", "Raw", "كجم", 20, 0, 0, null); rawId = r.Id; return Report(r.Ok ? "Id=" + r.Id : r.Message, r.Ok); });
            Check("إنشاء منتج تام (002) مرتبط بالخام", () =>
            {
                var r = m.SaveProductFull(null, "ACC-F", "سكري تام", "002", "Finished", "كرتون", 7.5, 1, 0.5, new() { (1, null, 5000) }, sourceProductId: rawId);
                finId = r.Id; return Report(r.Ok ? "Id=" + r.Id : r.Message, r.Ok);
            });
            Check("تعريف التحويل محفوظ في بطاقة المنتج", () =>
            {
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                var src = db.Products.AsNoTracking().Single(p => p.Id == finId).SourceProductId;
                return Report($"SourceProductId = {src}", src == rawId);
            });
        }

        // ═══ 5) الاستلام ═══
        Section("5. الاستلام — خام من المورد إلى المخزن");
        int lotId = 0;
        using (var scope = sp.CreateScope())
        {
            var s = scope.ServiceProvider.GetRequiredService<IReceivingService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            Check("سند استلام 5,000 كجم", () =>
            {
                var r = s.SaveShipment(custId, null, null, new List<ShipmentItemDto>
                { new() { TreatmentRequired = false, ProductId = rawId, QtyKg = 5000, PackageCount = 250, UnitWeightKg = 20 } });
                if (!r.Ok) return Report(r.Message, false);
                return Report("Id=" + r.Id, s.ApproveShipment(r.Id).Ok);
            });
            lotId = db.Lots.AsNoTracking().OrderByDescending(l => l.Id).Select(l => l.Id).FirstOrDefault();
            Check("نشأت دفعة خام", () => Report("LotId=" + lotId, lotId > 0));
            Check("الرصيد في المخزن = 5,000 كجم", () =>
            {
                double bal = db.InventoryTransactions.AsNoTracking().Where(b => b.ProductId == rawId).Sum(b => b.QtyKg);
                return Report($"الرصيد = {bal:N1}", Math.Abs(bal - 5000) < 0.01);
            });
        }

        // ═══ 6) الخطة ═══
        Section("6. خطة الإنتاج");
        int planId = 0, planItemId = 0;
        using (var scope = sp.CreateScope())
        {
            var p = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            Check("حفظ الخطة", () =>
            {
                var r = p.SavePlan("خطة القبول", "Daily", DateTime.Today.ToString("yyyy-MM-dd"), DateTime.Today.ToString("yyyy-MM-dd"), 1, 1, new List<PlanItemDto>
                { new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = custId, ProductId = finId, PlannedQtyKg = 3000, PriorityNo = 1 } });
                planId = r.Id; return Report(r.Ok ? r.DocumentNumber : r.Message, r.Ok);
            });
            planItemId = db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == planId).Select(i => i.Id).FirstOrDefault();
            Check("اعتماد الخطة", () => Report("", p.ApprovePlan(planId).Ok));
        }

        // ═══ 7) أمر الإنتاج — مسار OrdersView ═══
        Section("7. أمر الإنتاج — IssueTodayOrders ← ApproveOrder ← StartOrder (مسار الشاشة الفعلي)");
        int orderId = 0;
        using (var scope = sp.CreateScope())
        {
            var o = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            Check("حفظ الأمر", () =>
            {
                var r = o.IssueTodayOrders();
                if (r.Ok) { using var lookup = sp.CreateScope(); r.Id = lookup.ServiceProvider.GetRequiredService<DatesErpDbContext>().ProductionOrders.AsNoTracking().Single(x => x.SourcePlanId == planId).Id; }
                orderId = r.Id; return Report(r.Ok ? r.DocumentNumber : r.Message, r.Ok);
            });
            Check("اعتماد الأمر (صرف المواد المساعدة فقط)", () => { var r = o.ApproveOrder(orderId); return Report(r.Ok ? "" : r.Message, r.Ok); });
            Check("بدء الإنتاج ← StartOrder", () => { var r = o.StartOrder(orderId); return Report(r.Ok ? "" : r.Message, r.Ok); });
            Check("جلسة تنفيذ بحالة InProgress", () =>
            {
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                bool ok = db.ProductionExecutions.AsNoTracking().Any(e => e.OrderId == orderId && e.Status == "InProgress");
                return Report("Status=InProgress", ok);
            });
        }

        // ═══ 8) إقفال يوم الإنتاج — مسار OrdersView ═══
        Section("8. إقفال يوم الإنتاج ← CloseProductionDay مع مخرجات ثانوية ديناميكية");
        using (var scope = sp.CreateScope())
        {
            var e = scope.ServiceProvider.GetRequiredService<IExecutionService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            int byId = db.ByProducts.AsNoTracking().OrderBy(b => b.Id).Select(b => b.Id).FirstOrDefault();
            Check("الإقفال: 2,850 كجم / 380 كرتون + 130 كجم مخرج ثانوي + 20 فاقد = 3,000 خام", () =>
            {
                var r = e.CloseProductionDay(orderId, 2850, 380, 0, 0, 20, false,
                    new List<DowntimeDto> { new() { Hours = 1, ReasonAr = "صيانة" } }, true, "إقفال القبول",
                    new List<ByProductQtyDto> { new() { ByProductId = byId, QtyKg = 130 } });
                return Report(r.Ok ? r.Message.Split('\n')[0] : r.Message, r.Ok);
            });
            Check("المخرج الثانوي سُجّل ديناميكياً", () =>
            {
                var execIds = db.ProductionExecutions.AsNoTracking().Where(e => e.OrderId == orderId).Select(e => e.Id).ToList();
                double q = db.ExecutionByProducts.AsNoTracking().Where(x => execIds.Contains(x.ExecutionId)).Sum(x => (double)x.Qty);
                // §ومسار الخطة: PlanClosingByProducts (كانت تُكتب يتيمة بـ ClosingId=0 قبل إصلاح العلاقة)
                var itemIds = db.PlanClosingItems.AsNoTracking().Where(i => i.OrderId == orderId).Select(i => i.Id).ToList();
                double q2 = db.PlanClosingByProducts.AsNoTracking().Where(x => itemIds.Contains(x.ClosingId)).Sum(x => x.QtyKg);
                if (q2 > 0) Report($"عبر مسار الخطة = {q2:N1} كجم", true);
                return Report($"الكمية = {q:N1} كجم", Math.Abs(q - 130) < 0.01);
            });
            Check("أُرسل للجودة تلقائياً", () =>
            {
                var ex = db.ProductionExecutions.AsNoTracking().FirstOrDefault(x => x.OrderId == orderId);
                return Report($"QualitySent={ex?.QualitySent} · IsDayClosed={ex?.IsDayClosed}", ex?.QualitySent == true && ex?.IsDayClosed == true);
            });
        }

        // ═══ 9) الفحص — مسار QualityView ═══
        Section("9. فحص الجودة — بيانات آلياً ← نتائج بوحدات مختلفة ← حفظ ← اعتماد");
        int checkId = 0;
        using (var scope = sp.CreateScope())
        {
            var insp = scope.ServiceProvider.GetRequiredService<IInspectionService>();
            var q = scope.ServiceProvider.GetRequiredService<IQualityService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

            Check("GetOrderContext يملأ بيانات الفحص آلياً", () =>
            {
                var c = insp.GetOrderContext(orderId);
                bool ok = !string.IsNullOrWhiteSpace(c.OrderNo) && !string.IsNullOrWhiteSpace(c.CustomerName)
                          && !string.IsNullOrWhiteSpace(c.RawItemName) && !string.IsNullOrWhiteSpace(c.FinishedProductName)
                          && !string.IsNullOrWhiteSpace(c.ShiftName);
                return Report($"أمر={c.OrderNo} · عميل={c.CustomerName} · خام={c.RawItemName} · تام={c.FinishedProductName} · وردية={c.ShiftName}", ok);
            });

            var results = new List<InspectionResultDto>
            {
                new() { ResultTypeId = typeOk, Qty = 380, UnitId = ctnId, ProductId = finId, LotId = lotId },
                new() { ResultTypeId = typeBy, Qty = 130, UnitId = kgId,  ProductId = finId, LotId = lotId },
            };
            Check("ValidateResults يمرّ على وحدات مختلفة", () => { insp.ValidateResults(results, orderId, finId); return Report("مقبول", true); });
            Check("Compute يفصل الإجماليات لكل وحدة (لا خلط)", () =>
            {
                var t = insp.Compute(results);
                bool ok = t.ByUnit.Count == 2 && !t.SingleUnit && t.Warnings.Count > 0;
                return Report($"وحدتان: {string.Join(" | ", t.ByUnit.Select(u => $"{u.UnitLabel}: مفحوص {u.Checked:N0}"))} · تحذير={t.Warnings.Count}", ok);
            });
            Check("ComputeConvertedTotal عبر التحويل المعرَّف = 2,980 كجم", () =>
            {
                var v = insp.ComputeConvertedTotal(results, kgId, out var why);
                return Report(v == null ? why : $"{v:N1} كجم", v != null && Math.Abs(v.Value - 2980) < 0.01);
            });
            Check("SaveCheck يحفظ الفحص", () =>
            {
                var r = q.SaveCheck(orderId, null, "2026-09-03", "نهائي — بعد التبريد",
                    new List<QualityItemDto> { /* §سحب: معادلة الفحص الجديدة — المفحوص = مقبول + مرفوض (المخرجات الثانوية نتائج مستقلة) */ new() { ProductId = finId, LotId = lotId, CheckedQtyKg = 2850, AcceptedQtyKg = 2850, RejectedQtyKg = 0 } },
                    null, new QualityLabDto { Decision = "Passed" });
                checkId = r.Id; return Report(r.Ok ? r.DocumentNumber : r.Message, r.Ok);
            });
            Check("حفظ النتائج الديناميكية بوحدتها", () =>
            {
                foreach (var d in results)
                    db.InspectionResults.Add(new InspectionResult
                    {
                        CheckId = checkId, ProductId = d.ProductId, LotId = d.LotId,
                        ResultTypeId = d.ResultTypeId, Qty = (decimal)d.Qty, UnitId = d.UnitId,
                        UnitLabel = insp.UnitName(d.UnitId.Value)
                    });
                db.SaveChanges();
                int n = db.InspectionResults.AsNoTracking().Count(x => x.CheckId == checkId);
                return Report($"{n} نتيجة", n == 2);
            });
            Check("القراءة تُعيد الكمية بوحدتها (§عيب الدورة السابق)", () =>
            {
                var s = db.InspectionResults.AsNoTracking().Where(x => x.CheckId == checkId).ToList();
                var acc = s.Single(x => x.ResultTypeId == typeOk);
                return Report($"المقبول = {acc.Qty:N0} {acc.UnitLabel} (لا 2850)", Math.Abs((double)acc.Qty - 380) < 0.01 && acc.UnitId == ctnId);
            });
            Check("اعتماد الفحص", () => { var r = q.ApproveCheck(checkId); return Report(r.Ok ? "" : r.Message, r.Ok); });
        }

        // ═══ 10) تسليم التام ═══
        Section("10. تسليم الإنتاج التام ← SaveReceipt ← Issue ← Receive");
        int receiptId = 0;
        using (var scope = sp.CreateScope())
        {
            var f = scope.ServiceProvider.GetRequiredService<IFinishedGoodsService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            Check("سند استلام تام (380 كرتون / 2,850 كجم)", () =>
            {
                var r = f.SaveReceipt(orderId, checkId, "2026-09-04", new List<FinishedGoodsItemDto>
                { new() { ProductId = finId, LotId = lotId, PackageCount = 380, NetWeightKg = 2850 } });
                receiptId = r.Id; return Report(r.Ok ? r.DocumentNumber : r.Message, r.Ok);
            });
            Check("الإصدار للمخزن", () => { var r = f.Issue(receiptId); return Report(r.Ok ? "" : r.Message, r.Ok); });
            Check("الاستلام المخزني (يؤثر على الأرصدة)", () =>
            {
                var r = f.Receive(receiptId, new Dictionary<int, double> { [0] = 2850 });
                return Report(r.Ok ? "" : r.Message, r.Ok);
            });
            Check("رصيد المنتج التام في المخزن > 0", () =>
            {
                double bal = db.InventoryTransactions.AsNoTracking().Where(b => b.ProductId == finId).Sum(b => b.QtyKg);
                return Report($"الرصيد = {bal:N1} كجم", bal > 0);
            });
        }

        // ═══ 11) تسليم العميل ═══
        Section("11. تسليم العميل ← Save ← Approve");
        using (var scope = sp.CreateScope())
        {
            var d = scope.ServiceProvider.GetRequiredService<ICustomerDeliveryService>();
            Check("سند تسليم 1,500 كجم", () =>
            {
                var r = d.Save(custId, "2026-09-05", orderId, new List<CustomerDeliveryItemDto>
                { new() { ProductId = finId, LotId = lotId, PackageCount = 200, QtyKg = 1500 } });
                if (!r.Ok) return Report(r.Message, false);
                var ap = d.Approve(r.Id);
                return Report(ap.Ok ? r.DocumentNumber : ap.Message, ap.Ok);
            });
        }

        // ═══ 12) الأثر النهائي ═══
        Section("12. التحقق من الأثر النهائي في القاعدة");
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            Check("الخام نقص بعد الاستهلاك", () =>
            {
                double bal = db.InventoryTransactions.AsNoTracking().Where(b => b.ProductId == rawId).Sum(b => b.QtyKg);
                return Report($"المتبقي من الخام = {bal:N1} كجم", bal < 5000);
            });
            Check("بنود الخطة محدَّثة (منتَج/مقبول)", () =>
            {
                var pi = db.ProductionPlanItems.AsNoTracking().Single(i => i.Id == planItemId);
                return Report($"ProducedKg={pi.ProducedQtyKg:N0} · AcceptedQtyKg={pi.AcceptedQtyKg:N0} · Status={pi.ExecutionStatus}",
                              pi.ProducedQtyKg > 0);
            });
            Check("التقرير اليومي يُبنى بلا استثناء", () =>
            {
                var rep = scope.ServiceProvider.GetRequiredService<IReportService>();
                var r = rep.Run("daily_production", new Dictionary<string, string>());
                return Report($"الأعمدة={r.Columns.Count} · الصفوف={r.Rows.Count} · الملخص={r.Summary.Count}", r.Rows.Count > 0);
            });
            // ═══ 13) الفحص الذاتي — نتحقق أنه يكتشف العطل فعلاً، لا أنه يقول «كل شيء سليم» دائماً ═══
            Section("13. الفحص الذاتي — §يكتشف العطل فعلاً أم يمجامل؟");
            var diag = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            Check("DiagnosticCore على قاعدة سليمة → كل الفحوصات ناجحة", () =>
            {
                var f = DiagnosticCore.CheckDatabase(diag).Concat(DiagnosticCore.CheckSeedData(diag)).ToList();
                var bad = f.Where(x => !x.Ok).ToList();
                return Report($"{f.Count} فحصاً · فاشل={bad.Count}" + (bad.Count > 0 ? " · " + string.Join("، ", bad.Select(b => b.Name)) : ""),
                              f.Count >= 10 && bad.Count == 0);
            });
            Check("DiagnosticCore يكتشف عموداً محذوفاً (اختبار سلبي)", () =>
            {
                // §لو قال «سليم» بعد حذف عمود فهو يمجامل ولا يفحص
                diag.Database.ExecuteSqlRaw("ALTER TABLE Customers RENAME TO Customers_bak");
                try
                {
                    var f = DiagnosticCore.CheckDatabase(diag);
                    var missing = f.FirstOrDefault(x => x.Name.Contains("جداول النموذج"));
                    bool caught = missing != null && !missing.Ok && missing.Detail.Contains("Customers");
                    return Report(missing == null ? "لا فحص للجداول" : missing.Detail, caught);
                }
                finally { diag.Database.ExecuteSqlRaw("ALTER TABLE Customers_bak RENAME TO Customers"); }
            });
            Check("القاعدة أُعيدت كما كانت", () =>
            {
                var f = DiagnosticCore.CheckDatabase(diag);
                return Report(string.Join(" · ", f.Select(x => x.Name + "=" + (x.Ok ? "✓" : "✗"))), f.All(x => x.Ok));
            });

            Check("أعمدة التقرير تُبنى من تعريف الأصناف (إثبات لا بحث عن اسم)", () =>
            {
                var rep = scope.ServiceProvider.GetRequiredService<IReportService>();
                int before = rep.Run("daily_production", new Dictionary<string, string>()).Columns.Count;
                // §الإثبات: إضافة مخرج ثانوي جديد تُضيف عموداً — فلو كانت الأعمدة مثبّتة لما تغيّرت
                db.ByProducts.Add(new ByProduct { ByProductCode = "BP-ACC", ByProductNameAr = "مخرج قبول جديد", UnitOfMeasure = "سلة" });
                db.SaveChanges();
                var after = rep.Run("daily_production", new Dictionary<string, string>());
                bool grew = after.Columns.Count == before + 1;
                bool hasNew = after.Columns.Any(c => c.Contains("مخرج قبول جديد"));
                return Report($"الأعمدة {before} ← {after.Columns.Count} · العمود الجديد موجود={hasNew}", grew && hasNew);
            });
        }
    }

    // ───────────────────────── أدوات الإخراج ─────────────────────────

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("── " + title + " " + new string('─', Math.Max(0, 74 - title.Length)));
    }

    private static string _current = "";

    /// <summary>خطوة قبول: تُنفَّذ، ويُسجَّل نجاحها أو فشلها مرة واحدة.</summary>
    private static void Check(string name, Func<bool> act)
    {
        _current = name;
        bool ok;
        try { ok = act(); }
        catch (Exception ex)
        {
            Fail(name, ex.GetType().Name + ": " + (ex.InnerException?.Message ?? ex.Message));
            return;
        }
        if (ok) Pass(name); else Fail(name, "");
    }

    /// <summary>تطبع تفصيل الخطوة وتعيد النتيجة — لا تعدّ (العدّ في Check وحده).</summary>
    private static bool Report(string detail, bool ok)
    {
        if (!string.IsNullOrWhiteSpace(detail)) Console.WriteLine("         ↳ " + detail);
        return ok;
    }

    private static void Pass(string name) { _pass++; Console.WriteLine($"  ✅ {name}"); }
    private static void Fail(string name, string why)
    {
        _fail++;
        Console.WriteLine($"  ❌ {name}" + (string.IsNullOrWhiteSpace(why) ? "" : "  ←  " + why));
        _failures.Add(name + (string.IsNullOrWhiteSpace(why) ? "" : " — " + why));
    }
}
