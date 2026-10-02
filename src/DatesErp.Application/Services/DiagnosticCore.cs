using DatesErp.Core.Domain.Entities;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §قلب الفحص الذاتي — بلا أي اعتماد على WPF، فيُختبر ويُشغَّل على أي منصة.
/// شاشة «معلومات النظام» تستدعيه، ومشغّل القبول يستدعيه للتحقق من صحته.
///
/// الغرض: يجعل «هل العطل من جهازي أم من البرنامج؟» سؤالاً له جواب مكتوب.
/// </summary>
public static class DiagnosticCore
{
    /// <summary>نتيجة فحص واحد.</summary>
    public record Finding(string Name, bool Ok, string Detail);

    /// <summary>
    /// §B81 — هوية النظام والقاعدة: «هل أنا متصل بالقاعدة الصحيحة؟» بصرف النظر عن أي شيء آخر.
    /// الخادم والقاعدة يُقرآن من الاتصال المفتوح نفسه (SELECT @@SERVERNAME / DB_NAME())
    /// لا من نص الاتصال المطلوب — فالقاعدة الفعلية هي الحجة. مع إصدار القاعدة وأعداد
    /// الجداول الرئيسية التي تطابقها عين المستخدم مع ما يراه في الشاشات.
    /// </summary>
    public static List<(string Name, string Value)> GetIdentity(DatesErpDbContext db)
    {
        var rows = new List<(string, string)>();
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        bool sqlite = !db.Database.IsSqlServer();

        if (sqlite)
        {
            rows.Add(("المزوّد", "SQLite (قاعدة محلية على هذا الجهاز)"));
            // DataSource فارغ لقواعد الذاكرة (الاختبارات) — نصرّح بها بدل سطر فارغ مضلِّل
            rows.Add(("ملف القاعدة", string.IsNullOrWhiteSpace(conn.DataSource) ? "(قاعدة ذاكرة — بلا ملف)" : conn.DataSource));
        }
        else
        {
            rows.Add(("المزوّد", "SQL Server"));
            rows.Add(("الخادم (فعلياً من الاتصال)", Scalar(conn, "SELECT @@SERVERNAME") ?? conn.DataSource ?? "—"));
            rows.Add(("قاعدة البيانات (فعلياً)", Scalar(conn, "SELECT DB_NAME()") ?? conn.Database ?? "—"));
        }

        string dbv;
        try { dbv = db.DbVersions.AsNoTracking().OrderByDescending(v => v.Id).Select(v => v.VersionNumber).FirstOrDefault() ?? "غير معروف"; }
        catch { dbv = "تعذرت القراءة"; }
        rows.Add(("إصدار قاعدة البيانات", dbv));

        rows.Add(("عدد الأصناف", Count(db, "Products")));
        rows.Add(("عدد العملاء", Count(db, "Customers")));
        rows.Add(("خطط الإنتاج", Count(db, "ProductionPlans")));
        rows.Add(("أوامر الإنتاج", Count(db, "ProductionOrders")));
        rows.Add(("دفعات الخام", Count(db, "Lots")));
        rows.Add(("المستخدمون", Count(db, "Users")));
        return rows;
    }

    private static string Scalar(System.Data.Common.DbConnection conn, string sql)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar()?.ToString();
        }
        catch { return null; }
    }

    private static string Count(DatesErpDbContext db, string table)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM [{table}]";
            return Convert.ToInt32(cmd.ExecuteScalar()).ToString("N0");
        }
        catch { return "تعذر العد"; }
    }

    /// <summary>فحوصات الاتصال والمخطط: كل جدول وعمود في النموذج موجود فعلاً في القاعدة.</summary>
    public static List<Finding> CheckDatabase(DatesErpDbContext db)
    {
        var list = new List<Finding>();
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        bool sqlite = !db.Database.IsSqlServer();

        list.Add(new Finding("الاتصال بقاعدة البيانات", Safe(() => db.Database.CanConnect(), out var can) && can,
            db.Database.IsSqlServer() ? "SQL Server" : "SQLite"));

        var entities = db.Model.GetEntityTypes().Where(e => !string.IsNullOrEmpty(e.GetTableName())).ToList();
        var missingTables = new List<string>();
        var missingCols = new List<string>();

        foreach (var e in entities)
        {
            var t = e.GetTableName();
            if (!TableExists(conn, t, sqlite)) { missingTables.Add(t); continue; }
            var have = Columns(conn, t, sqlite);
            var id = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(t, e.GetSchema());
            foreach (var p in e.GetProperties())
            {
                var c = p.GetColumnName(id);
                if (!string.IsNullOrEmpty(c) && !have.Contains(c, StringComparer.OrdinalIgnoreCase))
                    missingCols.Add($"{t}.{c}");
            }
        }

        list.Add(new Finding($"جداول النموذج موجودة ({entities.Count} جدولاً)",
            missingTables.Count == 0,
            missingTables.Count == 0 ? "كل الجداول موجودة" : "ناقصة: " + string.Join("، ", missingTables.Take(10))));

        list.Add(new Finding("أعمدة النموذج موجودة",
            missingCols.Count == 0,
            missingCols.Count == 0 ? "كل الأعمدة موجودة"
                : $"ناقصة ({missingCols.Count}): " + string.Join("، ", missingCols.Take(10))));

        return list;
    }

    /// <summary>فحوصات البيانات الأولية التي بدونها تنهار الشاشات أو تختفي الأزرار.</summary>
    public static List<Finding> CheckSeedData(DatesErpDbContext db) => new()
    {
        Min("مستخدمون", db.Users.Count(), 1, "لا مستخدمين — لن تستطيع الدخول"),
        Min("أدوار", db.Roles.Count(), 1, "لا أدوار"),
        // §الكتالوج الفعلي 21 مورداً × 12 عملية (PermissionService.ResourceCatalog) — الحد 20 لا 50
        Min("موارد الصلاحيات", db.PermissionResources.Count(), 20, "كتالوج الصلاحيات ناقص — أزرار ستختفي"),
        Min("وحدات قياس", db.UnitsOfMeasure.Count(), 2, "لا وحدات — قوائم الوحدات ستظهر فارغة"),
        Min("مخازن", db.Warehouses.Count(), 1, "لا مخازن — الاستلام سيفشل"),
        Min("ورديات", db.Shifts.Count(), 1, "لا ورديات — الخطة ستفشل"),
        Min("أنواع نتائج الفحص", db.InspectionResultTypes.Count(x => x.IsActive), 1, "لا أنواع نتائج — شاشة الفحص بلا صفوف"),
        Min("مخططات الترقيم", db.NumberingSchemes.Count(), 1, "لا مخططات ترقيم — المستندات لن تأخذ أرقاماً"),
    };

    /// <summary>
    /// §فحوصات الإعداد التشغيلي — أشياء موجودة في القاعدة لكنها تُعطّل عملية بعينها.
    /// الفرق عن CheckSeedData: تلك تعدّ الصفوف، وهذه تتحقق أن **ما يطلبه الكود بالاسم**
    /// موجود فعلاً. غيابها لا يظهر عند بدء التشغيل بل عند أول محاولة استخدام،
    /// فيبدو عطلاً عشوائياً في شاشة بريئة.
    /// </summary>
    public static List<Finding> CheckOperational(DatesErpDbContext db)
    {
        var list = new List<Finding>();

        // §المخازن تُطلب بالكود الحرفي عبر WarehouseId(code) التي ترمي DomainException.
        // WTRT مضاف لاحقاً لدورة المعالجة، فقاعدة مُرقّاة من إصدار أقدم قد تفتقده
        // فتفشل كل عمليات المعالجة برسالة «المخزن WTRT غير معرّف».
        var wh = new[]
        {
            ("WRM", "مخزن المواد الخام — الاستلام والصرف للإنتاج"),
            ("WFG", "مخزن الإنتاج التام — استلام التام والتسليم"),
            ("WAUX", "مخزن المواد المساعدة"),
            ("WTRT", "مستودع المعالجة والتعقيم — دورة المعالجة")
        };
        var haveWh = db.Warehouses.AsNoTracking().Select(w => w.WarehouseCode).ToList();
        foreach (var (code, use) in wh)
            list.Add(new Finding($"المخزن {code}",
                haveWh.Contains(code),
                haveWh.Contains(code) ? use : $"مفقود — سيتعطل: {use}"));

        // §مخططات الترقيم تُطلب بالكود عبر Numbering.Next(code).
        var schemes = new[] { "SHIP", "PLAN", "ORD", "EXE", "QC", "FGR", "RCV", "CD", "TXN", "PCL", "LOT", "TASK", "TRT" };
        var haveSch = db.NumberingSchemes.AsNoTracking().Select(x => x.SchemeCode).ToList();
        var missSch = schemes.Where(x => !haveSch.Contains(x)).ToList();
        list.Add(new Finding("مخططات ترقيم المستندات",
            missSch.Count == 0,
            missSch.Count == 0 ? $"كل المخططات موجودة ({schemes.Length})"
                : "مفقودة: " + string.Join("، ", missSch) + " — المستندات لن تأخذ أرقاماً"));

        // §بوابة الصلاحيات: مورد ناقص في القاعدة = أزرار تختفي بلا سبب ظاهر.
        var haveRes = db.PermissionResources.AsNoTracking().Select(x => x.Code).ToList();
        var missRes = Core.Domain.Enums.PermissionModules.All
            .Where(m => !haveRes.Contains(m.Code)).Select(m => m.Code).ToList();
        list.Add(new Finding($"موارد الصلاحيات ({Core.Domain.Enums.PermissionModules.All.Length} مورداً)",
            missRes.Count == 0,
            missRes.Count == 0 ? "الكتالوج مكتمل"
                : "ناقصة: " + string.Join("، ", missRes) + " — أزرار ستختفي من الشاشات"));

        // §مستخدم فعّال واحد على الأقل: القفل خارج النظام عطل لا رجعة فيه من الواجهة.
        int active = db.Users.AsNoTracking().Count(u => u.IsActive);
        list.Add(new Finding("مستخدمون فعّالون",
            active >= 1,
            active >= 1 ? $"الموجود {active}" : "لا مستخدم فعّال — لن يستطيع أحد الدخول"));

        return list;
    }

    /// <summary>
    /// §فحوصات اتساق البيانات — العطل الصامت الذي لا يرفع استثناءً ولا يمنع الحفظ،
    /// بل يعطي **أرقاماً خاطئة** يبني عليها المستخدم قراراً. هذه أخطر من العطل الظاهر:
    /// العطل الظاهر يوقف العمل، والرقم الخاطئ يمرّ ويُعتمد.
    /// كلها للقراءة فقط — تُبلّغ ولا تُصلح، فالإصلاح التلقائي للأرصدة قرار محاسبي لا تقني.
    /// </summary>
    public static List<Finding> CheckDataIntegrity(DatesErpDbContext db)
    {
        var list = new List<Finding>();

        var lots = db.Lots.AsNoTracking().ToList();

        // §1) لا كمية سالبة: الرصيد السالب يعني حركة صرف تجاوزت حارس المنع.
        var neg = lots.Where(l => l.InStockQtyKg < -0.001 || l.ReservedQtyKg < -0.001
                               || l.UnderTreatmentQtyKg < -0.001 || l.TreatmentReadyQtyKg < -0.001).ToList();
        list.Add(new Finding("لا أرصدة دفعات سالبة",
            neg.Count == 0,
            neg.Count == 0 ? $"{lots.Count} دفعة سليمة"
                : $"{neg.Count} دفعة برصيد سالب: " + string.Join("، ", neg.Take(5).Select(x => x.LotCode))));

        // §2) المحجوز + تحت المعالجة لا يتجاوز المخزون، وإلا صار AvailableQtyKg صفراً
        // بلا سبب مفهوم فتبدو الدفعة «غير متاحة» وهي مليئة.
        var over = lots.Where(l => l.ReservedQtyKg + l.UnderTreatmentQtyKg > l.InStockQtyKg + 0.001).ToList();
        list.Add(new Finding("المحجوز وتحت المعالجة ضمن المخزون",
            over.Count == 0,
            over.Count == 0 ? "كل الدفعات متسقة"
                : $"{over.Count} دفعة الالتزام فيها يتجاوز الرصيد: "
                  + string.Join("، ", over.Take(5).Select(x =>
                      $"{x.LotCode} (مخزون {x.InStockQtyKg:N0} · محجوز {x.ReservedQtyKg:N0} · معالجة {x.UnderTreatmentQtyKg:N0})"))));

        // §3) تطابق «تحت المعالجة» على الدفعة مع مجموع العمليات الجارية فعلاً.
        // اختلافهما يعني كمية محتجزة عن الإنتاج بلا عملية معالجة تفسّرها — أو العكس.
        var openByLot = db.RawTreatments.AsNoTracking()
            .Where(t => t.Status == TreatmentStatuses.InProgress)
            .ToList()
            .GroupBy(t => t.LotId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.RemainingQtyKg));
        var mismatch = new List<string>();
        foreach (var l in lots)
        {
            double open = openByLot.TryGetValue(l.Id, out var v) ? v : 0;
            if (Math.Abs(open - l.UnderTreatmentQtyKg) > 0.01)
                mismatch.Add($"{l.LotCode} (الدفعة {l.UnderTreatmentQtyKg:N1} · العمليات {open:N1})");
        }
        list.Add(new Finding("«تحت المعالجة» يطابق عمليات المعالجة الجارية",
            mismatch.Count == 0,
            mismatch.Count == 0 ? "متطابق"
                : $"{mismatch.Count} دفعة غير متطابقة: " + string.Join("، ", mismatch.Take(5))));

        // §4) لكل حركة مخزون مستند: الحركة اليتيمة تكسر التتبع من التقرير إلى مصدره.
        int orphanTxn = db.InventoryTransactions.AsNoTracking()
            .Count(t => t.ReferenceDocNumber == null || t.ReferenceDocNumber == "");
        list.Add(new Finding("كل حركة مخزون مرتبطة بمستند",
            orphanTxn == 0,
            orphanTxn == 0 ? "لا حركات يتيمة" : $"{orphanTxn} حركة بلا رقم مستند — التتبع مقطوع"));

        // §5) رصيد المستودعات لا يكون سالباً على مستوى الصف — §1.50.66 يشمل PackageCount و PackagingTypeId
        int negBal = db.StockBalances.AsNoTracking().Count(b => b.QtyKg < -0.001 || b.PackageCount < 0);
        list.Add(new Finding("لا أرصدة مخازن سالبة (كجم وعبوات)",
            negBal == 0,
            negBal == 0 ? "كل الأرصدة موجبة" : $"{negBal} رصيد سالب في StockBalances (كجم أو عبوات)"));

        // §1.50.66 — فحص التكرار: نفس المفتاح الكامل مكرر
        var dupGroups = db.StockBalances.AsNoTracking()
            .GroupBy(b => new { b.WarehouseId, b.ProductId, b.MaterialId, b.LotId, b.CustomerId, b.PackagingTypeId })
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Take(5).ToList();
        list.Add(new Finding("لا أرصدة مكررة بنفس المفتاح الكامل (Warehouse+Product+Material+Lot+Customer+Packaging)",
            dupGroups.Count == 0,
            dupGroups.Count == 0 ? "لا تكرار" : $"{dupGroups.Count} مجموعات مكررة: " + string.Join(", ", dupGroups.Select(k => $"WH{k.WarehouseId}-P{k.ProductId}-PKG{k.PackagingTypeId}"))));

        // §6) الدفعات المعلّقة على شحنة محذوفة — يتيمة تظهر في التقارير بلا مصدر.
        var shipIds = db.Shipments.AsNoTracking().Select(x => x.Id).ToHashSet();
        var orphanLots = lots.Where(l => l.ShipmentId != null && !shipIds.Contains(l.ShipmentId.Value)).ToList();
        list.Add(new Finding("كل دفعة مرتبطة بشحنة قائمة",
            orphanLots.Count == 0,
            orphanLots.Count == 0 ? "لا دفعات يتيمة"
                : $"{orphanLots.Count} دفعة تشير إلى شحنة غير موجودة: "
                  + string.Join("، ", orphanLots.Take(5).Select(x => x.LotCode))));

        return list;
    }

    /// <summary>
    /// §الفحص العميق (توسعة 1.50.52) — طبقة رابعة تكشف الأعطال الصامتة الأخطر:
    /// تطابق الدفتر مع الأرصدة، القيود المكررة، رايات الاعتماد المتناقضة، السلاسل الممزقة
    /// (مستندات حية على دفعات ملغاة)، سلامة الحسابات والأدوار، ومصفوفة الصلاحيات.
    /// كلها للقراءة فقط — تُبلّغ ولا تُصلح، كسابقتها.
    /// </summary>
    public static List<Finding> CheckDeepIntegrity(DatesErpDbContext db)
    {
        var list = new List<Finding>();

        // §D1) دفتر الحركات هو الحقيقة: مجموع القيود لكل مفتاح رصيد يجب أن يساوي الرصيد.
        // أي فرق = قيد ضاع أو رصيد عُدّل خلف ظهر الدفتر (§43).
        // §1.50.66 — المفتاح الكامل يشمل PackagingTypeId
        var ledger = db.InventoryTransactions.AsNoTracking()
            .GroupBy(t => new { t.WarehouseId, t.ProductId, t.MaterialId, t.LotId, t.CustomerId, t.PackagingTypeId })
            .Select(g => new { g.Key.WarehouseId, g.Key.ProductId, g.Key.MaterialId, g.Key.LotId, g.Key.CustomerId, g.Key.PackagingTypeId, Total = g.Sum(x => x.QtyKg) })
            .ToList();
        var balances = db.StockBalances.AsNoTracking().ToList();
        var ledgerByKey = ledger.ToDictionary(
            x => (x.WarehouseId, x.ProductId, x.MaterialId, x.LotId, x.CustomerId, x.PackagingTypeId), x => x.Total);
        var whNames = db.Warehouses.AsNoTracking().ToDictionary(w => w.Id, w => w.WarehouseCode);
        var diffs = new List<string>();
        var seen = new HashSet<(int, int?, int?, int?, int?, int?)>();
        foreach (var b in balances)
        {
            var key = (b.WarehouseId, b.ProductId, b.MaterialId, b.LotId, b.CustomerId, b.PackagingTypeId);
            seen.Add(key);
            double book = ledgerByKey.TryGetValue(key, out var v) ? v : 0;
            if (Math.Abs(book - b.QtyKg) > 0.01)
                diffs.Add($"{whNames.GetValueOrDefault(b.WarehouseId, "?" + b.WarehouseId)}·صنف{b.ProductId}·مادة{b.MaterialId}·دفعة{b.LotId}·عبوة{b.PackagingTypeId} (رصيد {b.QtyKg:N1} ≠ دفتر {book:N1})");
        }
        foreach (var l in ledger)
        {
            var key = (l.WarehouseId, l.ProductId, l.MaterialId, l.LotId, l.CustomerId, l.PackagingTypeId);
            if (!seen.Contains(key) && Math.Abs(l.Total) > 0.01)
                diffs.Add($"{whNames.GetValueOrDefault(l.WarehouseId, "?" + l.WarehouseId)}·صنف{l.ProductId}·دفعة{l.LotId}·عبوة{l.PackagingTypeId} (قيود {l.Total:N1} بلا رصيد مناظر)");
        }
        list.Add(new Finding("تطابق أرصدة المخازن مع دفتر الحركات",
            diffs.Count == 0,
            diffs.Count == 0 ? $"{balances.Count} رصيداً يطابق قيوده"
                : $"{diffs.Count} مفتاحاً مختلفاً: " + string.Join(" | ", diffs.Take(5))));

        // §D2) لا قيد مكرر: نفس (المستند، النوع، المخزن، الصنف/المادة، الدفعة، العميل، العبوة) مرتين = ازدواج محاسبي.
        // §1.50.66 — إضافة CustomerId و PackagingTypeId لمنع تكرار وهمي
        var dups = db.InventoryTransactions.AsNoTracking()
            .GroupBy(t => new { t.ReferenceDocType, t.ReferenceDocNumber, t.MovementType, t.WarehouseId, t.ProductId, t.MaterialId, t.LotId, t.CustomerId, t.PackagingTypeId })
            .Where(g => g.Count() > 1)
            .Select(g => g.Key.ReferenceDocNumber)
            .Take(5).ToList();
        list.Add(new Finding("لا قيود مكررة في دفتر الحركات",
            dups.Count == 0,
            dups.Count == 0 ? "لا تكرار" : "مستندات بقيود مكررة: " + string.Join("، ", dups)));

        // §D3) راية الاعتماد والحالة لا تتناقضان (فحص محافظ: التناقض القطعي فقط).
        var contradict = new List<string>();
        foreach (var x in db.Shipments.AsNoTracking().ToList())
            if ((x.IsApproved && x.Status == Core.Common.DocStatuses.Draft) || (!x.IsApproved && x.Status == Core.Common.DocStatuses.Approved))
                contradict.Add($"شحنة {x.DocumentNumber}");
        foreach (var x in db.ProductionPlans.AsNoTracking().ToList())
            if ((x.IsApproved && x.Status == Core.Common.DocStatuses.Draft) || (!x.IsApproved && x.Status == Core.Common.DocStatuses.Approved))
                contradict.Add($"خطة {x.DocumentNumber}");
        foreach (var x in db.ProductionOrders.AsNoTracking().ToList())
            if ((x.IsApproved && x.Status == Core.Common.DocStatuses.Draft)
                || (!x.IsApproved && x.Status is Core.Common.DocStatuses.InProgress or Core.Common.DocStatuses.Completed
                    or Core.Common.DocStatuses.Closed or Core.Common.DocStatuses.Stopped))
                contradict.Add($"أمر {x.DocumentNumber}");
        list.Add(new Finding("رايات الاعتماد تطابق حالة المستند",
            contradict.Count == 0,
            contradict.Count == 0 ? "لا تناقض" : $"{contradict.Count} مستنداً متناقضاً: " + string.Join("، ", contradict.Take(5))));

        // §D4) السلاسل الممزقة: مستند حي يشير إلى دفعة ملغاة (ثغرة v1.50.27 التاريخية).
        var cancelledLots = db.Lots.AsNoTracking().Where(l => l.Status == Core.Common.DocStatuses.Cancelled).ToList();
        var torn = new List<string>();
        if (cancelledLots.Count > 0)
        {
            var cancelledIds = cancelledLots.Select(l => l.Id).ToHashSet();
            var liveOrderRefs = db.ProductionOrderItems.AsNoTracking()
                .Where(oi => oi.LotId != null)
                .Join(db.ProductionOrders.AsNoTracking().Where(o => o.Status != Core.Common.DocStatuses.Cancelled && o.Status != Core.Common.DocStatuses.Closed),
                    oi => oi.OrderId, o => o.Id, (oi, o) => new { o.DocumentNumber, oi.LotId })
                .ToList();
            foreach (var r in liveOrderRefs.Where(r => cancelledIds.Contains(r.LotId!.Value)).Take(5))
                torn.Add($"أمر {r.DocumentNumber} على دفعة ملغاة");
            var livePlanRefs = db.ProductionPlanItems.AsNoTracking()
                .Where(pi => pi.LotId != null)
                .Join(db.ProductionPlans.AsNoTracking().Where(p => p.Status != Core.Common.DocStatuses.Cancelled),
                    pi => pi.PlanId, p => p.Id, (pi, p) => new { p.DocumentNumber, pi.LotId })
                .ToList();
            foreach (var r in livePlanRefs.Where(r => cancelledIds.Contains(r.LotId!.Value)).Take(5))
                torn.Add($"خطة {r.DocumentNumber} على دفعة ملغاة");
        }
        list.Add(new Finding("لا مستندات حية على دفعات ملغاة",
            torn.Count == 0,
            torn.Count == 0 ? "السلاسل سليمة" : string.Join("، ", torn.Distinct())));

        // §D5) مراجع يتيمة: بنود خطط/أوامر تشير إلى دفعة غير موجودة أصلاً.
        var allLotIds = db.Lots.AsNoTracking().Select(l => l.Id).ToHashSet();
        int orphanPlanRefs = db.ProductionPlanItems.AsNoTracking().AsEnumerable()
            .Count(pi => pi.LotId != null && !allLotIds.Contains(pi.LotId.Value));
        int orphanOrderRefs = db.ProductionOrderItems.AsNoTracking().AsEnumerable()
            .Count(oi => oi.LotId != null && !allLotIds.Contains(oi.LotId.Value));
        list.Add(new Finding("مراجع الدفعات في الخطط والأوامر موجودة",
            orphanPlanRefs == 0 && orphanOrderRefs == 0,
            orphanPlanRefs == 0 && orphanOrderRefs == 0 ? "لا مراجع يتيمة"
                : $"بنود خطط يتيمة {orphanPlanRefs} · بنود أوامر يتيمة {orphanOrderRefs}"));

        // §D6) سلامة الحسابات — ثلاث حراسات: مدير فعّال، أسماء فريدة، موظف مرتبط صالح.
        var adminRoleIds = db.Roles.AsNoTracking().Where(r => r.RoleCode == "Administrator").Select(r => r.Id).ToList();
        int activeAdmins = db.Users.AsNoTracking()
            .Where(u => u.IsActive)
            .Join(db.UserRoles.AsNoTracking(), u => u.Id, ur => ur.UserId, (u, ur) => ur.RoleId)
            .Count(rid => adminRoleIds.Contains(rid));
        list.Add(new Finding("يوجد مدير نظام فعّال",
            activeAdmins >= 1,
            activeAdmins >= 1 ? $"{activeAdmins} مدير فعّال" : "لا مدير فعّال — النظام مقفل إدارياً (استخدم إجراء الطوارئ)"));

        var dupNames = db.Users.AsNoTracking().GroupBy(u => u.UserName)
            .Where(g => g.Count() > 1).Select(g => g.Key).Take(5).ToList();
        list.Add(new Finding("لا أسماء مستخدمين مكررة",
            dupNames.Count == 0,
            dupNames.Count == 0 ? "كل الأسماء فريدة" : "مكررة: " + string.Join("، ", dupNames)));

        var inactiveEmp = db.Employees.AsNoTracking().Where(e => !e.IsActive).Select(e => e.Id).ToList();
        var linkedInactive = db.Users.AsNoTracking()
            .Where(u => u.IsActive && u.EmployeeId != null && inactiveEmp.Contains(u.EmployeeId.Value))
            .Select(u => u.UserName).Take(5).ToList();
        list.Add(new Finding("لا مستخدمين نشطين مرتبطين بموظفين معطلين",
            linkedInactive.Count == 0,
            linkedInactive.Count == 0 ? "الارتباطات سليمة" : "حسابات نشطة على موظف معطل: " + string.Join("، ", linkedInactive)));

        // §D7) المستند المعتمد مكتمل البنية: شحنة بلا دفعات / خطة أو أمر بلا بنود = اعتماد أجوف.
        int emptyShips = db.Shipments.AsNoTracking().Where(x => x.IsApproved).Count(x => !db.Lots.Any(l => l.ShipmentId == x.Id));
        int emptyPlans = db.ProductionPlans.AsNoTracking().Where(p => p.IsApproved).Count(p => !db.ProductionPlanItems.Any(i => i.PlanId == p.Id));
        int emptyOrders = db.ProductionOrders.AsNoTracking().Where(o => o.IsApproved).Count(o => !db.ProductionOrderItems.Any(i => i.OrderId == o.Id));
        list.Add(new Finding("المستندات المعتمدة مكتملة البنية",
            emptyShips == 0 && emptyPlans == 0 && emptyOrders == 0,
            emptyShips == 0 && emptyPlans == 0 && emptyOrders == 0 ? "لا اعتماد أجوف"
                : $"شحنات معتمدة بلا دفعات {emptyShips} · خطط بلا بنود {emptyPlans} · أوامر بلا بنود {emptyOrders}"));

        // §D8) عبوات سالبة في الأرصدة — عدّاد الكرتون انقلب تحت الصفر.
        int negPkg = db.StockBalances.AsNoTracking().Count(b => b.PackageCount < 0);
        list.Add(new Finding("لا أرصدة عبوات سالبة",
            negPkg == 0,
            negPkg == 0 ? "كل العدادات موجبة" : $"{negPkg} رصيداً بعبوات سالبة"));

        // §D9) A total row count is NOT a matrix check: extra permission rows
        // elsewhere can hide a missing admin/legacy role permission. Expect the
        // combinations that EnsureCatalog actually seeds, not an imaginary
        // full Cartesian product for every non-admin role.
        var resourceIds = db.PermissionResources.AsNoTracking().ToDictionary(r => r.Code, r => r.Id);
        var operationIds = db.PermissionOperations.AsNoTracking().Select(o => o.Id).ToList();
        var expectedPermissions = new HashSet<(int role, int resource, int operation)>();
        foreach (var rp in db.RolePermissions.AsNoTracking().ToList())
            if (rp.ModuleCode != null && resourceIds.TryGetValue(rp.ModuleCode, out int resourceId))
                foreach (var operationId in operationIds)
                    expectedPermissions.Add((rp.RoleId, resourceId, operationId));
        foreach (var adminId in db.Roles.AsNoTracking().Where(r => r.RoleCode == "Administrator").Select(r => r.Id))
            foreach (var resourceId in resourceIds.Values)
                foreach (var operationId in operationIds)
                    expectedPermissions.Add((adminId, resourceId, operationId));
        var actualPermissions = db.RoleResourcePermissions.AsNoTracking()
            .Select(x => new { x.RoleId, x.ResourceId, x.OperationId }).ToList()
            .Select(x => (x.RoleId, x.ResourceId, x.OperationId)).ToHashSet();
        var missingPermissions = expectedPermissions.Except(actualPermissions).ToList();
        bool complete = resourceIds.Count > 0 && operationIds.Count > 0 && missingPermissions.Count == 0;
        list.Add(new Finding("مصفوفة الصلاحيات مكتملة الصفوف", complete,
            complete ? $"{actualPermissions.Count:N0} صفاً — لا تركيبات إلزامية ناقصة"
                : $"ناقصة {missingPermissions.Count:N0} صفاً إلزامياً من {expectedPermissions.Count:N0}"
                    + (resourceIds.Count == 0 || operationIds.Count == 0 ? " — كتالوج الموارد/العمليات مفقود" : "")
                    + " — شغّل EnsureCatalog وافحص التعديلات اليدوية"));

        return list;
    }

    // ── أدوات ──

    private static Finding Min(string name, int actual, int min, string problem)
        => new(name, actual >= min, actual >= min ? $"الموجود {actual}" : $"{problem} (الموجود {actual} · المطلوب ≥ {min})");

    private static bool Safe(Func<bool> act, out bool value)
    {
        try { value = act(); return true; }
        catch { value = false; return false; }
    }

    private static bool TableExists(System.Data.Common.DbConnection conn, string table, bool sqlite)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sqlite
            ? $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table.Replace("'", "''")}'"
            : $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='{table.Replace("'", "''")}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static HashSet<string> Columns(System.Data.Common.DbConnection conn, string table, bool sqlite)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        if (sqlite)
        {
            cmd.CommandText = $"PRAGMA table_info('{table.Replace("'", "''")}')";
            using var r = cmd.ExecuteReader();
            while (r.Read()) set.Add(r.GetString(1));
            return set;
        }
        cmd.CommandText = $"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='{table.Replace("'", "''")}'";
        using var r2 = cmd.ExecuteReader();
        while (r2.Read()) set.Add(r2.GetString(0));
        return set;
    }
}
