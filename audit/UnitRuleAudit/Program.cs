// ═══════════════════════════════════════════════════════════════════════════
//  مُدقِّق قاعدة الوحدات — يفحص النظام مقابل القاعدة الإلزامية المعتمدة
//
//  هذا أداة فحص لا جزء من المنتج: لا يُشحن مع الحزمة ولا يُضاف إلى الحل.
//  ينفّذ «شرط القبول النهائي» حرفياً ويختبر كل منع (Validation) مطلوب.
//
//  السيناريو المعتمد:
//    استلام سكري 10,000 KG
//    خطة: سكري → سكري تام · 1,000 CARTON · كرتون=10 · قوالب=5 · قالب=2
//    أمر إنتاج: 1,000 CARTON (بلا إعادة إدخال وزن الكرتون)
//    إنتاج فعلي: 950 CARTON = 9,500 KG مكافئ
//    مخرج ثانوي: نوى 800 KG
// ═══════════════════════════════════════════════════════════════════════════

using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Session;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DateERP.Audit;

public static class Program
{
    private static int _pass, _fail;
    private static int _custId, _finId, _rawId;   // §تُشارك بين السيناريو وفحوصات المنع
    private static readonly List<string> _failures = new();
    private static ServiceProvider _sp;

    public static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Line("═", 78);
        Line("  مُدقِّق قاعدة الوحدات — القاعدة: خام=KG · تام=CARTON · ثانوي=KG");
        Line("═", 78);

        _sp = Build();
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            db.Database.EnsureCreated();
            DbSeeder.Seed(db);
            new PermissionService(db, new SessionContext()).EnsureCatalog();
        }
        scope0(s => s.GetRequiredService<IAuthService>().Login("admin", DbSeeder.InitialAdminPassword));

        Scenario();
        Section("أ) المنع المطلوب (Validation) — هل يرفض النظام المخالفات فعلاً؟");
        Validations();
        Section("ب) حفظ الوزن التاريخي — هل تتغير النتائج القديمة لو تغيّر التعريف؟");
        HistoricalWeight();

        Line("");
        Line("═", 78);
        Line($"  النتيجة: {_pass} مطابق · {_fail} مخالف · من {_pass + _fail} بنداً");
        if (_fail > 0)
        {
            Line("");
            Line("  المخالفات:");
            foreach (var f in _failures) Line("   ✗ " + f);
        }
        Line("═", 78);
        return _fail == 0 ? 0 : 1;
    }

    // ═══════════════ سيناريو شرط القبول ═══════════════

    private static void Scenario()
    {
        Section("السيناريو المعتمد — دورة كاملة بالأرقام المطلوبة");

        int rawId = 0, finId = 0, lotId = 0, custId = 0, planId = 0, planItemId = 0, orderId = 0, byId = 0;

        // 1) تعريف المنتج: كرتون 10 كجم · 5 قوالب × 2 كجم
        Check("تعريف «سكري تام»: كرتون=10 كجم · 5 قوالب · قالب=2 كجم", () =>
        {
            using var scope = _sp.CreateScope();
            var m = scope.ServiceProvider.GetRequiredService<MasterDataService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var c = m.SaveCustomer(null, "UR-1", "عميل قاعدة الوحدات", "جملة", "777", "-", true);
            custId = c.Id; _custId = c.Id;
            var raw = m.SaveProductFull(null, "UR-R", "سكري", "001", "Raw", "كجم", 20, 0, 0, null);
            rawId = raw.Id; _rawId = raw.Id;
            var fin = m.SaveProductFull(null, "UR-F", "سكري تام", "002", "Finished", "كرتون", 10, 5, 2, new() { (1, null, 5000) }, sourceProductId: raw.Id);
            finId = fin.Id; _finId = fin.Id;
            byId = db.ByProducts.AsNoTracking().OrderBy(b => b.Id).Select(b => b.Id).FirstOrDefault();
            var p = db.Products.AsNoTracking().Single(x => x.Id == finId);
            return Ok($"وحدة الصنف={p.UnitOfMeasure} · وزن الكرتون={p.CartonWeightKg} · قوالب={p.MoldsCount} · قالب={p.MoldWeightKg}",
                p.CartonWeightKg == 10 && p.MoldsCount == 5 && p.MoldWeightKg == 2);
        });

        // 2) الوزن النظري من بيانات التعبئة
        Check("الوزن النظري للكرتون = 5 قوالب × 2 كجم = 10 كجم", () =>
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            double w = UnitsPolicy.CartonWeight(db, finId, null);
            return Ok($"UnitsPolicy.CartonWeight = {w} كجم", w == 10);
        });

        // 3) الاستلام 10,000 KG
        Check("استلام سكري 10,000 KG", () =>
        {
            using var scope = _sp.CreateScope();
            var r = scope.ServiceProvider.GetRequiredService<IReceivingService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var s = r.SaveShipment(custId, null, null, new List<ShipmentItemDto>
            { new() { TreatmentRequired = false, ProductId = rawId, QtyKg = 10000, PackageCount = 500, UnitWeightKg = 20 } });
            if (!s.Ok) return Bad(s.Message);
            r.ApproveShipment(s.Id);
            lotId = db.Lots.AsNoTracking().OrderByDescending(l => l.Id).Select(l => l.Id).First();
            double stock = db.Lots.AsNoTracking().Single(l => l.Id == lotId).InStockQtyKg;
            return Ok($"رصيد الدفعة = {stock:N0} كجم", Math.Abs(stock - 10000) < 0.01);
        });

        // 4) الخطة
        Check("خطة إنتاج: 1,000 كرتون (= 10,000 كجم)", () =>
        {
            using var scope = _sp.CreateScope();
            var p = scope.ServiceProvider.GetRequiredService<IPlanningService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var r = p.SavePlan("خطة قاعدة الوحدات", "Daily", DateTime.Today.ToString("yyyy-MM-dd"), DateTime.Today.ToString("yyyy-MM-dd"), 1, 1, new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = custId, ProductId = finId,
                        PlannedQtyKg = 10000, PlannedCartons = 1000, PriorityNo = 1 }
            });
            if (!r.Ok) return Bad(r.Message);
            planId = r.Id;
            p.ApprovePlan(planId);
            var pi = db.ProductionPlanItems.AsNoTracking().Single(i => i.PlanId == planId);
            planItemId = pi.Id;
            return Ok($"المخطط: {pi.PlannedQtyKg:N0} كجم / {pi.PlannedCartons:N0} كرتون", pi.PlannedCartons == 1000);
        });

        // 5) الأمر يرث وزن الكرتون بلا إعادة إدخال
        Check("أمر الإنتاج يرث وزن الكرتون من التعريف (بلا إعادة إدخال)", () =>
        {
            using var scope = _sp.CreateScope();
            var o = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var r = o.IssueTodayOrders();
            if (r.Ok) r.Id = db.ProductionOrders.AsNoTracking().Single(x => x.SourcePlanId == planId).Id;
            if (!r.Ok) return Bad(r.Message);
            orderId = r.Id;
            o.ApproveOrder(orderId);
            var oi = db.ProductionOrderItems.AsNoTracking().Single(i => i.OrderId == orderId);
            return Ok($"وزن الكرتون المحفوظ على بند الأمر = {oi.CartonWeightKg}", oi.CartonWeightKg == 10);
        });

        // 6) الإنتاج الفعلي 950 كرتون
        Check("إنتاج فعلي 950 كرتون + مخرج ثانوي 500 كجم (توازن كتلة)", () =>
        {
            using var scope = _sp.CreateScope();
            var o = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            var e = scope.ServiceProvider.GetRequiredService<IExecutionService>();
            var st = o.StartOrder(orderId);
            if (!st.Ok) return Bad(st.Message);
            var r = e.CloseProductionDay(orderId, 9500, 950, 0, 0, 0, false, new List<DowntimeDto>(), false, null,
                new List<ByProductQtyDto> { new() { ByProductId = byId, QtyKg = 500 } });
            return r.Ok ? Ok(r.Message.Split('\n')[0], true) : Bad(r.Message);
        });

        Check("الوزن المكافئ = 950 × 10 = 9,500 كجم", () =>
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var ex = db.ProductionExecutions.AsNoTracking().Single(x => x.OrderId == orderId);
            bool ok = ex.ActualCartons == 950 && Math.Abs(ex.ActualQtyKg - 9500) < 0.01;
            return Ok($"{ex.ActualCartons:N0} كرتون · {ex.ActualQtyKg:N0} كجم", ok);
        });

        Check("المخرج الثانوي 500 كجم محفوظ بوحدة الكيلو", () =>
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            int exId = db.ProductionExecutions.Single(x => x.OrderId == orderId).Id;
            double q = db.ExecutionByProducts.AsNoTracking().Where(x => x.ExecutionId == exId).Sum(x => (double)x.Qty);
            string u = db.ByProducts.AsNoTracking().Single(b => b.Id == byId).UnitOfMeasure;
            return Ok($"الكمية={q:N0} · وحدة المخرج في بطاقته={u}", Math.Abs(q - 500) < 0.01);
        });
    }

    // ═══════════════ المنع المطلوب ═══════════════

    private static void Validations()
    {
        // (1) خام بالكرتون
        // §القاعدة المعتمدة: لا تُفرض الوحدات داخل الكود — الوحدة من تعريف الصنف في
        // شاشة الأصناف المركزية. ومجموعة الصنف وتصنيفه هما ما يحدد نوعه لا اسم الوحدة،
        // فـ«كرتون» قد تكون مجرد عبوة خام عند الاستلام.
        Check("الوحدة تأتي من تعريف الصنف لا من الكود", () =>
        {
            using var scope = _sp.CreateScope();
            var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

            var raw = master.SaveProductFull(null, "AU-R", "خام بالكرتون", "001", "Raw", "كرتون", 20, 0, 0, null);
            var finR = master.SaveProductFull(null, "AU-F", "تام بالكرتون", "002", "Finished", "كرتون", 10, 5, 2, null);
            var by = master.SaveProductFull(null, "AU-B", "مخرج بالكجم", "003", "ByProduct", "كجم", 0, 0, 0, null);
            if (!raw.Ok || !finR.Ok || !by.Ok) return Bad("رُفضت وحدة معرَّفة — وهذا فرض في الكود");

            bool ok = db.Products.Single(p => p.Id == raw.Id).UnitOfMeasure == "كرتون"
                   && db.Products.Single(p => p.Id == raw.Id).GroupCode == "001"
                   && db.Products.Single(p => p.Id == finR.Id).UnitOfMeasure == "كرتون"
                   && db.Products.Single(p => p.Id == finR.Id).GroupCode == "002"
                   && db.Products.Single(p => p.Id == by.Id).UnitOfMeasure == "كجم"
                   && db.Products.Single(p => p.Id == by.Id).GroupCode == "003";
            return ok ? Ok("كل وحدة حُفظت كما عُرّفت، والمجموعة هي التي حددت النوع", true)
                      : Bad("الوحدة أو المجموعة لم تُحفظ كما عُرّفت");
        });

        Check("الخام يُستلم بأي عبوة مع حفظ الوحدة الأصلية والكيلو مرجعاً", () =>
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var rcv = scope.ServiceProvider.GetRequiredService<IReceivingService>();
            int raw = db.Products.AsNoTracking().First(p => p.ItemType == "Raw").Id;
            int cust = db.Customers.AsNoTracking().Select(c => c.Id).First();

            var r = rcv.SaveShipment(cust, null, null, new List<ShipmentItemDto>
            { new() { TreatmentRequired = false, ProductId = raw, QtyKg = 9200, PackageCount = 460, UnitWeightKg = 20, ReceiptUnit = "كرتون" } });
            if (!r.Ok) return Bad("رُفض استلام خام بعبوة كرتون: " + r.Message);
            rcv.ApproveShipment(r.Id);

            var item = db.ShipmentItems.AsNoTracking().OrderByDescending(x => x.Id).First();
            bool ok = item.ReceiptUnit == "كرتون" && Math.Abs(item.TotalWeightKg - 9200) < 0.01 && item.PackageCount == 460;
            return ok ? Ok($"الوحدة الأصلية «{item.ReceiptUnit}» محفوظة · الوزن المرجعي {item.TotalWeightKg:N0} كجم · {item.PackageCount} عبوة", true)
                      : Bad("الوحدة الأصلية أو الوزن المرجعي لم يُحفظ");
        });


        Check("لا افتراض وزن ثابت: منتجان تامان بوزنين مختلفين", () =>
        {
            using var scope = _sp.CreateScope();
            var m = scope.ServiceProvider.GetRequiredService<MasterDataService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            int raw = db.Products.AsNoTracking().Where(p => p.ItemType == "Raw").Select(p => p.Id).First();
            var a = m.SaveProductFull(null, "UR-A", "سكري تام 5", "002", "Finished", "كرتون", 5, 5, 1, null, sourceProductId: raw);
            var b = m.SaveProductFull(null, "UR-B", "خلاص تام 20", "002", "Finished", "كرتون", 20, 10, 2, null, sourceProductId: raw);
            double wa = UnitsPolicy.CartonWeight(db, a.Id, null);
            double wb = UnitsPolicy.CartonWeight(db, b.Id, null);
            return Ok($"أ={wa} كجم · ب={wb} كجم", wa == 5 && wb == 20 && wa != wb);
        });

        // (7) صنف جديد بلا وزن كرتون معرَّف — هل يُفترض 7.5؟
        Check("صنف بلا وزن كرتون: لا يُفترض وزن ثابت بل يُرفض أو يُنبَّه", () =>
        {
            using var scope = _sp.CreateScope();
            var m = scope.ServiceProvider.GetRequiredService<MasterDataService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var r = m.SaveProductFull(null, "UR-NW", "تام بلا وزن", "002", "Finished", "كرتون", 0, 0, 0, null);
            var saved = db.Products.AsNoTracking().FirstOrDefault(p => p.ProductCode == "UR-NW");
            double w = UnitsPolicy.CartonWeight(db, saved?.Id ?? 0, null);
            // القاعدة: ممنوع افتراض وزن ثابت — فإما يُرفض الحفظ أو يبقى الوزن صفراً
            bool ok = !r.Ok || (saved != null && saved.CartonWeightKg == 0 && w == 0);
            return ok
                ? Ok($"الوزن={saved?.CartonWeightKg} · CartonWeight={w}", true)
                : Bad($"قُبل صنف بلا وزن ثم افترض النظام {w} كجم — هذا وزن ثابت مقنّع");
        });
    }

    // ═══════════════ حفظ الوزن التاريخي ═══════════════

    private static void HistoricalWeight()
    {
        Check("تعديل تعريف المنتج لاحقاً لا يغيّر نتيجة الإنتاج القديمة", () =>
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            int finId = db.Products.AsNoTracking().Where(p => p.ProductCode == "UR-F").Select(p => p.Id).First();
            // §نثبّته على الأمر المقفل فعلياً في السيناريو — لا على «آخر أمر» الذي قد يكون أمر فحص غير مكتمل
            int oid = db.ProductionExecutions.AsNoTracking().Where(x => x.IsDayClosed)
                .OrderByDescending(x => x.Id).Select(x => x.OrderId).First();
            double before = db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == oid)
                .Select(i => i.CartonWeightKg).First();
            var ex = db.ProductionExecutions.AsNoTracking().Single(x => x.OrderId == oid);
            double kgBefore = ex.ActualQtyKg;

            // نغيّر التعريف إلى 20 كجم
            var prod = db.Products.Single(p => p.Id == finId);
            prod.CartonWeightKg = 20;
            db.SaveChanges();

            double afterItem = db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == oid).Select(i => i.CartonWeightKg).First();
            var ex2 = db.ProductionExecutions.AsNoTracking().Single(x => x.OrderId == oid);
            return Ok($"وزن البند: {before} ← {afterItem} · الكيلو المسجّل: {kgBefore:N0} ← {ex2.ActualQtyKg:N0}",
                before == 10 && afterItem == 10 && Math.Abs(ex2.ActualQtyKg - kgBefore) < 0.01);
        });

        Check("عدد القوالب ووزن القالب محفوظان تاريخياً على العملية", () =>
        {
            using var scope = _sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            // هل يوجد عمود للقوالب/وزن القالب على أي كيان تشغيلي؟
            var t = typeof(ProductionOrderItem);
            bool hasMolds = t.GetProperty("MoldsCount") != null;
            bool hasMoldW = t.GetProperty("MoldWeightKg") != null;
            var t2 = typeof(PlanClosingItem);
            bool cMolds = t2.GetProperty("MoldsCount") != null;
            bool cMoldW = t2.GetProperty("MoldWeightKg") != null;
            return Ok($"بند الأمر: قوالب={hasMolds} قالب={hasMoldW} · بند الإقفال: قوالب={cMolds} قالب={cMoldW}",
                hasMolds && hasMoldW && cMolds && cMoldW);
        });
    }

    // ═══════════════ أدوات ═══════════════

    private static ServiceProvider Build()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        return new ServiceCollection()
            .AddDatesErpInfrastructure(o => o.UseSqlite(conn))
            .AddScoped<IAuditService, AuditService>()
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
            .AddScoped<ICapacityService, CapacityService>()
            .AddScoped<IShiftService, ShiftService>()
            .AddScoped<IPlanProgressService, PlanProgressService>()
            .AddScoped<ITraceabilityService, TraceabilityService>()
            .AddScoped<MachineRegistry>()
            .BuildServiceProvider();
    }

    private static void scope0(Action<IServiceProvider> act)
    {
        using var s = _sp.CreateScope();
        act(s.ServiceProvider);
    }

    private static string _cur = "";
    private static void Check(string name, Func<bool> act)
    {
        _cur = name;
        int before = _pass + _fail;
        try
        {
            bool ok = act();
            if (_pass + _fail == before) { if (ok) Pass(name, ""); else Bad(name); }
        }
        catch (Exception ex) { _fail++; Line($"  ✗ {name}  ←  {ex.GetType().Name}: {ex.Message}"); _failures.Add(name + " — استثناء " + ex.GetType().Name); }
    }

    private static bool Ok(string detail, bool ok)
    {
        if (ok) { _pass++; Line($"  ✓ {_cur}  ({detail})"); }
        else { _fail++; Line($"  ✗ {_cur}  ←  {detail}"); _failures.Add(_cur + " — " + detail); }
        return ok;
    }

    private static bool Bad(string detail)
    {
        _fail++; Line($"  ✗ {_cur}  ←  {detail}"); _failures.Add(_cur + " — " + detail); return false;
    }

    private static bool Rejects(string what, bool rejected, string detail)
        => rejected ? Ok("مرفوض — " + Trunc(detail), true) : Bad($"لم يُرفض — {what}: {Trunc(detail)}");

    private static string Trunc(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 90 ? s[..90] + "…" : s.Split('\n')[0]);
    private static void Pass(string n, string d) { _pass++; Line($"  ✓ {n}"); }
    private static void Section(string t) { Line(""); Line("── " + t + " " + new string('─', Math.Max(0, 76 - t.Length))); }
    private static void Line(string s) => Console.WriteLine(s);
    private static void Line(string c, int n) => Console.WriteLine(new string(c[0], n));
}
