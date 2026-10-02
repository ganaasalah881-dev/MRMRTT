using System.Data;
using DatesErp.Core.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DatesErp.Infrastructure.Persistence;

/// <summary>
/// §الترحيل الآمن الشامل: يضمن أن قاعدة بيانات المستخدم (مهما كان عمرها وإصدارها)
/// تحتوي على كل الجداول وكل الأعمدة الموجودة في نموذج النظام الحالي — بلا حذف بيانات أبداً.
/// يُنفَّذ عند الإقلاع على SQLite وعلى SQL Server على حد سواء.
///
/// لماذا هذا ضروري: المستخدم يحدّث النظام بنسخ ملفات DLL فقط (لا مثبّت ولا حذف قاعدة)،
/// فأي جدول أو عمود أُضيف في إصدار لاحق يجب إنشاؤه/إلحاقه تلقائياً وإلا فشلت الشاشات
/// بأخطاء «عمود/جدول غير موجود» — وهذا بالضبط ما كان يُخفي العملاء ويعطل زر اختيار الأصناف.
/// </summary>
public static class SchemaMigrator
{
    /// <summary>ينفذ الترحيل ويعيد قائمة وصفية بما تم إنشاؤه/إضافته (أو الأخطاء إن وقعت).</summary>
    public static List<string> Migrate(DatesErpDbContext db)
    {
        var report = new List<string>();
        try
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open) conn.Open();
            bool sqlite = !db.Database.IsSqlServer();

            // Fail before ANY schema changes if existing RCV values cannot fit the
            // indexable SQL Server column or already violate its UNIQUE key.
            if (!sqlite) FinishedGoodsReceiptSchemaRepair.Preflight(conn);

            foreach (var entity in db.Model.GetEntityTypes())
            {
                var table = entity.GetTableName();
                if (string.IsNullOrEmpty(table)) continue;
                var tableId = StoreObjectIdentifier.Table(table, entity.GetSchema());

                try
                {
                    if (!TableExists(conn, table, sqlite))
                    {
                        Exec(conn, BuildCreateTable(entity, table, tableId, sqlite));
                        report.Add($"تم إنشاء الجدول المفقود: {table}");
                        // §B110 — الجدول الجديد يُنشأ بفهارسه أيضاً (كان يُخلق بلا فهارس
                        // ولا قيود تكرار، فتتباين القواعد المُرحّلة عن الجديدة بنيوياً).
                        EnsureIndexes(conn, entity, table, tableId, sqlite, report);
                        continue; // جدول جديد — كل أعمدته موجودة بالتعريف
                    }

                    // جدول قائم: تأكد من وجود كل الأعمدة
                    var existing = GetColumns(conn, table, sqlite);
                    foreach (var prop in entity.GetProperties())
                    {
                        var col = prop.GetColumnName(tableId);
                        if (string.IsNullOrEmpty(col)) continue;
                        // §45 — عمود التزامن يُضاف بخطوة مخصصة (نوع rowversion له قواعد إضافة خاصة)
                        if (string.Equals(col, "RowVersion", StringComparison.OrdinalIgnoreCase)) continue;
                        if (existing.Contains(col, StringComparer.OrdinalIgnoreCase)) continue;

                        var type = prop.GetColumnType(tableId);
                        // SQLite يقبل «ADD COLUMN» بينما SQL Server يتطلب «ADD» بدون COLUMN
                        var addSql = sqlite
                            ? $"ALTER TABLE [{table}] ADD COLUMN [{col}] {type} NULL"
                            : $"ALTER TABLE [{table}] ADD [{col}] {type} NULL";
                        Exec(conn, addSql);
                        FillNulls(conn, table, col, prop);
                        report.Add($"تمت إضافة العمود المفقود: {table}.{col}");
                    }

                    // A previously added ReceiptNumber can still be nvarchar(max):
                    // repair its actual SQL Server type and filtered UNIQUE index
                    // atomically, before generic index creation or any data sweep.
                    if (!sqlite && table == FinishedGoodsReceiptSchemaRepair.TableName)
                        FinishedGoodsReceiptSchemaRepair.Apply(conn, report);

                    // §B110 — استكمال الفهارس المفقودة على الجداول القائمة (من تعريف النموذج)
                    EnsureRowVersion(conn, table, sqlite, report);
                    EnsureIndexes(conn, entity, table, tableId, sqlite, report);

                    // §إصلاح الأعطال الشاملة: تعبئة أي قيمة فارغة (NULL) في الأعمدة غير
                    // القابلة للفراغ — هذا يحل خطأ «The data is NULL at ordinal» نهائياً،
                    // سواء كان الفراغ من عمود أُضيف حديثاً أو من بيانات قديمة.
                    // §B110 — كل تعبئة تُعلن في التقرير بدل الصمت.
                    SweepNulls(conn, table, entity, tableId, report);
                }
                catch (Exception exTable)
                {
                    report.Add($"خطأ في ترحيل {table}: {exTable.Message}");
                    // A failed RCV repair must not be followed by other schema/data
                    // changes. Bootstrapper sees the error and stops the application.
                    if (table == FinishedGoodsReceiptSchemaRepair.TableName) return report;
                }
            }
        }
        catch (Exception ex)
        {
            report.Add("خطأ عام في الترحيل: " + ex.Message);
            return report; // In particular, preflight failure is read-only and fatal.
        }

        // Restore the EXACT cartons posted for legacy receipts from the immutable
        // inventory ledger; estimating from weight would repeat the old rounding bug.
        BackfillReceiptCartons(db, report);

        // §B110 — تصحيح أعمدة decimal التي خُزنت نصاً على SQL Server في إصدارات سابقة
        RepairDecimalColumns(db, report);

        // §تتبع الصنف: الربط الآلي لتعريفات التحويل الرسمية في القواعد القديمة
        LinkProductSources(db, report);

        // §نظام الوحدات: توحيد المجموعات (001 خام | 002 تام | 003 مخرجات ثانوية)
        NormalizeGroups(db, report);

        // §B87/M6: توحيد العميل الفارغ — الصفر القديم يعني «بلا عميل» فيُرحَّل إلى NULL
        NullifyZeroCustomers(db, report);

        // §المعالجة والتعقيم — الدفعات القائمة تُعتبر جاهزة (منع توقف الإنتاج عند الترقية)
        EnsureReceivingTreatmentIndex(db, report);
        BackfillTreatmentReadiness(db, report);

        return report;
    }

    /// <summary>
    /// An old receipt can have several partial postings, each rounded separately.
    /// Backfill posted carton counts from its inbound ledger entries, never by a
    /// fresh proportional calculation. If a legacy receipt number/key is shared,
    /// leave it unresolved and report it; Unapprove fails closed for that receipt.
    /// </summary>
    private static void BackfillReceiptCartons(DatesErpDbContext db, List<string> report)
    {
        try
        {
            var candidates = db.FinishedGoodsReceiptItems
                .Where(i => i.ReceivedQtyKg > 0.001 && i.ReceivedPackageCount == 0)
                .ToList();
            if (candidates.Count == 0) return;
            var receipts = db.FinishedGoodsReceipts.AsNoTracking().ToList();
            var allItems = db.FinishedGoodsReceiptItems.AsNoTracking().ToList();
            var duplicates = receipts.Where(r => !string.IsNullOrWhiteSpace(r.ReceiptNumber))
                .GroupBy(r => r.ReceiptNumber).Where(g => g.Count() > 1)
                .Select(g => g.Key).ToHashSet();
            int filled = 0;
            foreach (var item in candidates)
            {
                var receipt = receipts.FirstOrDefault(r => r.Id == item.ReceiptId);
                if (receipt == null || string.IsNullOrWhiteSpace(receipt.ReceiptNumber)
                    || duplicates.Contains(receipt.ReceiptNumber))
                {
                    report.Add($"تتطلب بنود الاستلام #{item.Id} تسوية يدوية: رقم سند الاستلام مفقود/مكرر.");
                    continue;
                }
                var owner = item.CustomerId ?? db.ProductionOrders.AsNoTracking()
                    .Where(o => o.Id == receipt.OrderId).Select(o => o.CustomerId).FirstOrDefault();
                if (allItems.Count(i => i.ReceiptId == item.ReceiptId && i.ProductId == item.ProductId
                    && i.LotId == item.LotId && (i.CustomerId ?? owner) == owner
                    && i.PackagingTypeId == item.PackagingTypeId) > 1)
                {
                    report.Add($"تتطلب بنود الاستلام #{item.Id} تسوية يدوية: عدة بنود تحمل هوية مخزون واحدة.");
                    continue;
                }
                var prefix = receipt.ReceiptNumber + "#";
                var posted = db.InventoryTransactions.AsNoTracking().Where(t =>
                        t.ReferenceDocType == ReferenceDocType.FinishedGoodsReceipt
                        && t.MovementType == MovementType.Inbound
                        && t.ReferenceDocNumber != null && t.ReferenceDocNumber.StartsWith(prefix)
                        && t.WarehouseId == receipt.WarehouseId && t.OrderId == receipt.OrderId
                        && t.ProductId == item.ProductId && t.LotId == item.LotId
                        && t.CustomerId == owner && t.PackagingTypeId == item.PackagingTypeId)
                    .ToList();
                if (posted.Count == 0 || Math.Abs(posted.Sum(t => t.QtyKg) - item.ReceivedQtyKg) > 0.001)
                {
                    report.Add($"تتطلب بنود الاستلام #{item.Id} تسوية يدوية: الحركات الواردة لا تساوي وزن السند.");
                    continue;
                }
                var cartons = posted.Sum(t => t.PackageCount);
                if (cartons < 0)
                {
                    report.Add($"تتطلب بنود الاستلام #{item.Id} تسوية يدوية: عدد كراتين دفتر المخزون سالب.");
                    continue;
                }
                item.ReceivedPackageCount = cartons;
                if (cartons > 0) filled++;
            }
            if (filled > 0)
            {
                db.SaveChanges();
                report.Add($"استعيدت كراتين {filled} بند استلام تاريخي من حركات المخزون الفعلية.");
            }
        }
        catch (Exception ex) { report.Add("تعذر ترحيل كراتين سندات الاستلام التاريخية: " + ex.Message); }
    }

    /// <summary>
    /// §المعالجة والتعقيم — ترحيل صريح ومسجَّل للمخزون القائم (قرار المستخدم س2).
    ///
    /// **لماذا هذا إلزامي؟** الدفعات الموجودة في القاعدة لم تمر بدورة معالجة قط، لأن
    /// الدورة لم تكن موجودة. لو تُركت بـ TreatmentReadyQtyKg = 0 ثم فُعّل اشتراط
    /// المعالجة (المرحلة 3)، لتوقف الإنتاج كله لحظة الترقية على مخزون قائم ومدفوع ثمنه.
    /// هذا فخ ترقية حقيقي، نظير ما عولج في طبقة الصلاحيات.
    ///
    /// **آمن للتكرار (idempotent):** يعالج الدفعات التي لم تُرحَّل ولم تدخل معالجة قط
    /// فقط. لا يلمس صفاً بدأت عليه دورة حقيقية، فلا يمحو عمل النظام بعد التشغيل.
    /// </summary>
    private static void EnsureReceivingTreatmentIndex(DatesErpDbContext db, List<string> report)
    {
        // الفهرس مطلوب أيضاً للقواعد القائمة؛ الترحيل العام يضيف الأعمدة لا الفهارس.
        using var check = db.Database.GetDbConnection().CreateCommand();
        check.CommandText = db.IsSqlServer
            ? "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_RawTreatments_ReceivingItemId' AND object_id = OBJECT_ID('RawTreatments')"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_RawTreatments_ReceivingItemId'";
        if (Convert.ToInt32(check.ExecuteScalar()) > 0) return;
        var sql = db.IsSqlServer
            ? "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_RawTreatments_ReceivingItemId' AND object_id = OBJECT_ID('RawTreatments')) CREATE UNIQUE INDEX [IX_RawTreatments_ReceivingItemId] ON [RawTreatments] ([ReceivingItemId]) WHERE [ReceivingItemId] IS NOT NULL"
            : "CREATE UNIQUE INDEX IF NOT EXISTS [IX_RawTreatments_ReceivingItemId] ON [RawTreatments] ([ReceivingItemId]) WHERE [ReceivingItemId] IS NOT NULL";
        db.Database.ExecuteSqlRaw(sql); // فشل قيد الأمان لا يُبتلع ولا يُعلن الترحيل ناجحاً.
        report.Add("تم إنشاء فهرس المعالجة الفريد لكل بند؛ البيانات السابقة بقيت بلا تحويل ضمني إلى نعم/لا.");
    }

    private static void BackfillTreatmentReadiness(DatesErpDbContext db, List<string> report)
    {
        try
        {
            // الشرط الثلاثي هو ضمان عدم اللمس: جاهز = 0، وتحت المعالجة = 0،
            // ولا توجد أي عملية معالجة مسجّلة على الدفعة.
            var treated = db.RawTreatments.Select(t => t.LotId).Distinct().ToHashSet();
            var pending = db.Lots
                .Where(l => l.TreatmentReadyQtyKg <= 0 && l.UnderTreatmentQtyKg <= 0 && l.InStockQtyKg > 0)
                .Where(l => !db.ShipmentItems.Any(i => i.Id == l.ShipmentItemId && i.TreatmentRequired != null))
                .ToList()
                .Where(l => !treated.Contains(l.Id))
                .ToList();

            if (pending.Count == 0) return;
            foreach (var lot in pending) lot.TreatmentReadyQtyKg = lot.InStockQtyKg;
            db.SaveChanges();
            report.Add($"ترحيل المعالجة: اعتُبرت {pending.Count} دفعة قائمة جاهزة للإنتاج "
                       + "(مخزون سابق لتفعيل دورة المعالجة — لم يُمسّ أي رصيد).");
        }
        catch (Exception ex)
        {
            // الترحيل لا يُسقط الإقلاع: النظام يعمل بسلوكه السابق ويُبلَّغ بالسبب
            report.Add("تعذّر ترحيل جاهزية المعالجة: " + ex.Message);
        }
    }

    /// <summary>
    /// §نظام الوحدات — المعيار الرسمي للمجموعات:
    /// 001 مواد خام | 002 منتجات تامة | 003 مخرجات ثانوية (وحداتها كجم).
    /// القواعد القديمة: المجموعة 003 كانت «مواد مساعدة» و004 «أصناف ثانوية» —
    /// تُرحَّل الأصناف الثانوية إلى 003، والمواد المساعدة كيان مستقل خارج مجموعات الأصناف.
    /// </summary>
    private static void NormalizeGroups(DatesErpDbContext db, List<string> report)
    {
        try
        {
            bool changed = false;

            // مجموعة 003 تصبح «المخرجات الثانوية»
            var g3 = db.ItemGroups.FirstOrDefault(g => g.GroupCode == "003");
            if (g3 != null && g3.GroupType != "ByProduct")
            {
                g3.GroupNameAr = "المخرجات الثانوية";
                g3.GroupType = "ByProduct";
                g3.DefaultUnit = "كجم";
                changed = true;
                report.Add("تم توحيد المجموعة 003 = المخرجات الثانوية (كجم)");
            }

            // مجموعة 004 (أصناف ثانوية قديمة) تُعلَّم كياناتها وتنقل أصنافها إلى 003
            var g4 = db.ItemGroups.FirstOrDefault(g => g.GroupCode == "004");
            if (g4 != null && g4.GroupType == "ByProduct")
            {
                g4.GroupNameAr = "أصناف ثانوية (قديمة — رُحلت إلى 003)";
                g4.IsActive = false;
                changed = true;
            }
            foreach (var p in db.Products.Where(p => p.GroupCode == "004"))
            {
                p.GroupCode = "003";
                if (p.ItemType != "ByProduct") p.ItemType = "ByProduct";
                if (p.UnitOfMeasure != "كجم") p.UnitOfMeasure = "كجم"; // §المخرجات الثانوية كجم دائماً
                changed = true;
            }

            // مطابقة المجموعة مع نوع الصنف للسلالات الثلاث
            foreach (var p in db.Products.Where(p => p.ItemType == "Raw" && (p.GroupCode == null || p.GroupCode == "")))
            { p.GroupCode = "001"; changed = true; }
            foreach (var p in db.Products.Where(p => p.ItemType == "Finished" && (p.GroupCode == null || p.GroupCode == "")))
            { p.GroupCode = "002"; changed = true; }
            foreach (var p in db.Products.Where(p => p.ItemType == "ByProduct" && (p.GroupCode == null || p.GroupCode == "" || p.GroupCode == "004")))
            { p.GroupCode = "003"; changed = true; }

            if (changed) db.SaveChanges();
        }
        catch (Exception ex) { report.Add("خطأ في توحيد المجموعات: " + ex.Message); }
    }

    /// <summary>
    /// §تتبع الصنف: كل منتج تام بلا تعريف مصدر يُربط آلياً بالصنف الخام الذي تظهر
    /// كلمته (السلالة) في اسمه — «سكري فاخر 1كجم» ← «تمر خام - سكري».
    /// هكذا لا تكسر الترقية القواعد القديمة: التحويل يُستنتج من الأسماء ثم يُثبَّت في البطاقة.
    /// </summary>
    private static void LinkProductSources(DatesErpDbContext db, List<string> report)
    {
        try
        {
            var raws = db.Products.AsNoTracking().Where(p => p.ItemType == "Raw" && p.IsActive).ToList();
            if (raws.Count == 0) return;
            bool changed = false;
            foreach (var fin in db.Products.Where(p => p.ItemType == "Finished" && p.SourceProductId == null).ToList())
            {
                var match = raws.Select(r => (r, token: RawToken(r.ProductNameAr)))
                    .Where(x => x.token.Length >= 2 && (fin.ProductNameAr ?? "").Contains(x.token))
                    .OrderByDescending(x => x.token.Length)
                    .Select(x => x.r)
                    .FirstOrDefault();
                if (match != null)
                {
                    fin.SourceProductId = match.Id;
                    changed = true;
                    report.Add($"تم ربط تحويل رسمي آلياً: {fin.ProductNameAr} ← {match.ProductNameAr}");
                }
            }
            if (changed) db.SaveChanges();
        }
        catch (Exception ex) { report.Add("خطأ في ربط تحويلات الأصناف: " + ex.Message); }
    }

    /// <summary>كلمة السلالة في اسم الخام: «تمر خام - سكري» ← «سكري».</summary>
    private static string RawToken(string rawName)
    {
        var t = (rawName ?? "").Replace("تمر خام", "").Replace("مواد خام", "").Replace("خام", "");
        return t.Trim(' ', '-', '–', '—', ':', '.');
    }

    /// <summary>هل الجدول موجود؟</summary>
    public static bool TableExists(IDbConnection conn, string table, bool sqlite)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sqlite
            ? $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'"
            : $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME='{table}'";
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static HashSet<string> GetColumns(IDbConnection conn, string table, bool sqlite)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sqlite
            ? $"SELECT name FROM pragma_table_info('{table}')"
            : $"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='{table}'";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) set.Add(rd.GetString(0));
        return set;
    }

    /// <summary>بناء أمر CREATE TABLE لجدول مفقود من وصف نموذج EF مباشرة.</summary>
    private static string BuildCreateTable(IEntityType entity, string table, StoreObjectIdentifier tableId, bool sqlite)
    {
        var pk = entity.FindPrimaryKey();
        var colDefs = new List<string>();
        foreach (var prop in entity.GetProperties())
        {
            var col = prop.GetColumnName(tableId);
            var type = prop.GetColumnType(tableId);
            bool isPk = pk?.Properties.Any(p => p.Name == prop.Name) == true;
            bool autoInt = isPk && pk!.Properties.Count == 1
                           && prop.ClrType == typeof(int)
                           && prop.ValueGenerated == ValueGenerated.OnAdd;

            string def = $"[{col}] {type}";
            if (sqlite && autoInt)
                def += " PRIMARY KEY AUTOINCREMENT";          // SQLite: مفتاح تلقائي
            else if (!sqlite && autoInt)
                def += " IDENTITY(1,1) NOT NULL";              // SQL Server: عدّاد تلقائي
            else if (!prop.IsNullable)
                def += " NOT NULL";
            // CREATE TABLE (not just EF EnsureCreated) must also support rows
            // inserted by contexts without the audit interceptor.
            if (sqlite && col == "RowVersion") def += " DEFAULT (randomblob(16))";
            colDefs.Add(def);
        }
        // مفتاح مركّب (مثل UserRoles / RolePermissions)
        if (pk != null && pk.Properties.Count > 1)
            colDefs.Add($"CONSTRAINT [PK_{table}] PRIMARY KEY (" +
                        string.Join(", ", pk.Properties.Select(p => $"[{p.GetColumnName(tableId)}]")) + ")");
        else if (pk != null && pk.Properties.Count == 1)
        {
            var kp = pk.Properties[0];
            bool autoInt = kp.ClrType == typeof(int) && kp.ValueGenerated == ValueGenerated.OnAdd;
            if (sqlite && autoInt) { /* عولج داخل تعريف العمود */ }
            else if (!sqlite && autoInt) colDefs.Add($"CONSTRAINT [PK_{table}] PRIMARY KEY ([{kp.GetColumnName(tableId)}])");
            else colDefs.Add($"CONSTRAINT [PK_{table}] PRIMARY KEY ([{kp.GetColumnName(tableId)}])");
        }
        return $"CREATE TABLE [{table}] ({string.Join(", ", colDefs)})";
    }

    /// <summary>قيمة افتراضية لعمود غير قابل للفراغ — أو null إن لم يكن له افتراضي معروف.</summary>
    private static string DefaultFill(IProperty prop)
    {
        // §الحالة النشطة افتراضياً: أعمدة IsActive الفارغة تُملأ «نشط» (1) — لا «موقوف»،
        // حتى لا تختفي السجلات (عملاء/أصناف/موردون...) من القوائم المنسدلة
        if (prop.Name == "IsActive") return "1";

        var t = Nullable.GetUnderlyingType(prop.ClrType) ?? prop.ClrType;
        if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
            || t == typeof(double) || t == typeof(float) || t == typeof(decimal)) return "0";
        if (t == typeof(bool)) return "0";
        if (t == typeof(string)) return "''";
        if (t == typeof(DateTime)) return "'2000-01-01'";
        if (t.IsEnum) return "0";
        return null;
    }

    /// <summary>تعبئة الفراغات في عمود واحد (بعد إضافته حديثاً).</summary>
    /// <summary>§45 — عمود rowversion للتزامن التفاؤلي على كل الجداول القائمة (SQL Server فقط).</summary>
    private static void EnsureRowVersion(IDbConnection conn, string table, bool sqlite, List<string> report)
    {
        var cols = GetColumns(conn, table, sqlite);
        if (sqlite)
        {
            if (!cols.Contains("RowVersion", StringComparer.OrdinalIgnoreCase))
            {
                // ALTER TABLE in SQLite disallows non-constant randomblob()
                // defaults; application writes use the interceptor, but existing
                // rows still need valid tokens on upgrade.
                Exec(conn, $"ALTER TABLE [{table}] ADD COLUMN [RowVersion] BLOB NULL");
                report.Add($"تمت إضافة عمود التزامن: {table}.RowVersion");
            }
            int fixedRows = Exec(conn, $"UPDATE [{table}] SET [RowVersion] = randomblob(16) WHERE [RowVersion] IS NULL");
            if (fixedRows > 0) report.Add($"عُولجت {fixedRows} قيم تزامن فارغة في {table}.");
            return;
        }
        if (cols.Contains("RowVersion", StringComparer.OrdinalIgnoreCase)) return;
        // rowversion عمود NOT NULL ضمناً ويملأ الصفوف القائمة تلقائياً بقيم فريدة
        try { Exec(conn, $"ALTER TABLE [{table}] ADD [RowVersion] ROWVERSION"); }
        catch { Exec(conn, $"ALTER TABLE [{table}] ADD [RowVersion] ROWVERSION NULL"); }
        report.Add($"تمت إضافة عمود التزامن: {table}.RowVersion");
    }

    private static void FillNulls(IDbConnection conn, string table, string col, IProperty prop)
    {
        if (prop.IsNullable) return;
        var fill = DefaultFill(prop);
        if (fill == null) return;
        try { Exec(conn, $"UPDATE [{table}] SET [{col}] = {fill} WHERE [{col}] IS NULL"); }
        catch { /* الأفضل ألا يعطل الترحيل */ }
    }

    /// <summary>
    /// §إصلاح الأعطال الشاملة: يمسح كل جدول ويملأ أي قيمة فارغة (NULL) في أي عمود غير
    /// قابل للفراغ — بصرف النظر إن كان العمود مضافاً حديثاً أو قديماً وفيه بيانات فارغة.
    /// هذا يحل نهائياً خطأ: «The data is NULL at ordinal ... can't be called on NULL values».
    /// </summary>
    private static void SweepNulls(IDbConnection conn, string table, IEntityType entity, StoreObjectIdentifier tableId, List<string> report)
    {
        foreach (var prop in entity.GetProperties())
        {
            if (prop.IsNullable) continue; // القابل للفراغ يتحمله EF بلا مشكلة
            var col = prop.GetColumnName(tableId);
            if (string.IsNullOrEmpty(col)) continue;
            var fill = DefaultFill(prop);
            if (fill == null) continue;
            try
            {
                // §B110 — التعبئة لم تعد صامتة: عدد الصفوف المعبأة يُعلن في تقرير الترحيل
                var filled = Exec(conn, $"UPDATE [{table}] SET [{col}] = {fill} WHERE [{col}] IS NULL");
                if (filled > 0)
                    report.Add($"§تعبئة تلقائية: مُلئت {filled} قيمة فارغة بالافتراضي في {table}.{col}");
            }
            catch { /* لا نوقف الإقلاع على عمود واحد */ }
        }
    }

    /// <summary>
    /// §B110 — يضمن وجود كل فهارس النموذج على الجدول (فريدة/عادية/مصفّاة).
    /// قبل هذا الإصلاح كانت الجداول المُنشأة بالترحيل تنقصها الفهارس كلها:
    /// بطء في المسارات الساخنة وغياب حماية التكرار التي تعتمد عليها أرقام المستندات.
    /// الفحص بالاسم ثم بتوقيع الأعمدة (على SQLite) يمنع إنشاء فهرس مكرر باسم مختلف.
    /// </summary>
    private static void EnsureIndexes(IDbConnection conn, IEntityType entity, string table,
        StoreObjectIdentifier tableId, bool sqlite, List<string> report)
    {
        foreach (var idx in entity.GetIndexes())
        {
            var name = idx.GetDatabaseName();
            var cols = idx.Properties
                .Select(p => p.GetColumnName(tableId))
                .Where(c => !string.IsNullOrEmpty(c))
                .Select(c => c!)
                .ToList();
            if (string.IsNullOrEmpty(name) || cols.Count != idx.Properties.Count) continue;
            try
            {
                if (IndexExists(conn, table, name, cols, sqlite)) continue;
                var unique = idx.IsUnique ? "UNIQUE " : "";
                var colList = string.Join(", ", cols.Select(c => $"[{c}]"));
                var filter = idx.GetFilter();
                var where = string.IsNullOrEmpty(filter) ? "" : $" WHERE {filter}";
                Exec(conn, $"CREATE {unique}INDEX [{name}] ON [{table}] ({colList}){where}");
                report.Add($"تم إنشاء الفهرس المفقود: {table}.{name}");
            }
            catch (Exception exIdx)
            {
                report.Add($"خطأ في إنشاء فهرس {table}.{name}: {exIdx.Message}");
                // Fresh SQL Server table: don't silently proceed past the RCV
                // unique-index failure (existing tables use the atomic repair).
                if (!sqlite && table == FinishedGoodsReceiptSchemaRepair.TableName
                    && name == FinishedGoodsReceiptSchemaRepair.IndexName)
                    throw new InvalidOperationException($"فشل إنشاء فهرس سندات الاستلام {name}؛ أُوقف الترحيل.", exIdx);
            }
        }
    }

    /// <summary>هل الفهرس موجود؟ بالاسم على المزودين، وبتوقيع الأعمدة على SQLite لمنع التكرار.</summary>
    private static bool IndexExists(IDbConnection conn, string table, string name, List<string> cols, bool sqlite)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = sqlite
                ? $"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND tbl_name='{table}' AND name='{name}'"
                : $"SELECT COUNT(*) FROM sys.indexes WHERE name='{name}' AND object_id = OBJECT_ID('{table}')";
            if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) return true;
        }

        if (!sqlite) return false; // على SQL Server يكفي الاسم؛ تكرار بأسماء مختلفة أثره أداء لا صحة

        var indexNames = new List<string>();
        using (var list = conn.CreateCommand())
        {
            list.CommandText = $"SELECT name FROM pragma_index_list('{table}')";
            using var rd = list.ExecuteReader();
            while (rd.Read()) indexNames.Add(rd.GetString(0));
        }
        foreach (var idxName in indexNames)
        {
            var indexCols = new List<string>();
            using var info = conn.CreateCommand();
            info.CommandText = $"SELECT name FROM pragma_index_info('{idxName.Replace("'", "''")}') ORDER BY seqno";
            using var rd2 = info.ExecuteReader();
            while (rd2.Read()) indexCols.Add(rd2.GetString(0));
            if (indexCols.SequenceEqual(cols, StringComparer.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// §B110 — تصحيح أعمدة decimal على SQL Server التي خُزنت نصاً في إصدارات سابقة
    /// (المحوّل النصي كان مطبقاً على كل المزودين). يحوّل كل عمود إلى
    /// <c>decimal(18,4)</c> حقيقي مع الحفاظ على القيم عبر TRY_CAST — والقيم غير
    /// القابلة للتحويل تُترك فارغة ثم تعبئها خطوة التعبئة العامة إن كان العمود إلزامياً.
    /// على SQLite يبقى التخزين النصي هو التصميم المعتمد (لا يُفعل شيء).
    /// </summary>
    private static void RepairDecimalColumns(DatesErpDbContext db, List<string> report)
    {
        if (!db.IsSqlServer) return;
        var conn = db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open) conn.Open();

        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (string.IsNullOrEmpty(table)) continue;
            var tableId = StoreObjectIdentifier.Table(table, entity.GetSchema());

            foreach (var prop in entity.GetProperties())
            {
                var underlying = Nullable.GetUnderlyingType(prop.ClrType) ?? prop.ClrType;
                if (underlying != typeof(decimal)) continue;
                var col = prop.GetColumnName(tableId);
                if (string.IsNullOrEmpty(col)) continue;

                try
                {
                    string actualType;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = $"SELECT DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME='{table}' AND COLUMN_NAME='{col}'";
                        actualType = cmd.ExecuteScalar() as string;
                    }
                    if (actualType == null || actualType.StartsWith("decimal", StringComparison.OrdinalIgnoreCase)
                        || actualType.StartsWith("numeric", StringComparison.OrdinalIgnoreCase)) continue;

                    // التحويل: عمود مؤقت حقيقي ← نسخ القيم ← حذف النصي ← إعادة التسمية
                    var tmp = col + "_b110_dec";
                    Exec(conn, $"ALTER TABLE [{table}] ADD [{tmp}] decimal(18,4) NULL");
                    Exec(conn, $"UPDATE [{table}] SET [{tmp}] = TRY_CAST(NULLIF(LTRIM(RTRIM(CAST([{col}] AS nvarchar(max)))), '') AS decimal(18,4)) WHERE [{col}] IS NOT NULL");
                    Exec(conn, $"ALTER TABLE [{table}] DROP COLUMN [{col}]");
                    Exec(conn, $"EXEC sp_rename N'[dbo].[{table}].[{tmp}]', N'{col}', N'COLUMN'");
                    report.Add($"تم تصحيح نوع العمود {table}.{col} من نص إلى decimal(18,4)");
                }
                catch (Exception exCol)
                {
                    report.Add($"خطأ في تصحيح {table}.{col} إلى decimal: {exCol.Message}");
                }
            }
        }
    }

    /// <summary>
    /// §B87/M6: «بدون عميل» كانت تُخزَّن صفراً في بعض المسارات القديمة — تُرحَّل إلى NULL
    /// (الصفر ليس عميلاً حقيقياً ويكسر التقارير والتجميع). آمنة تكرارياً: الصفر فقط يتحول.
    /// </summary>
    private static void NullifyZeroCustomers(DatesErpDbContext db, List<string> report)
    {
        try
        {
            int n = 0;
            foreach (var i in db.ProductionPlanItems.Where(i => i.CustomerId == 0)) { i.CustomerId = null; n++; }
            foreach (var i in db.ProductionOrderItems.Where(i => i.CustomerId == 0)) { i.CustomerId = null; n++; }
            foreach (var o in db.ProductionOrders.Where(o => o.CustomerId == 0)) { o.CustomerId = null; n++; }
            foreach (var l in db.Lots.Where(l => l.CustomerId == 0)) { l.CustomerId = null; n++; }
            if (n > 0) { db.SaveChanges(); report.Add($"تم ترحيل {n} سجلاً من عميل-صفر إلى «بدون عميل» (NULL)"); }
        }
        catch (Exception ex) { report.Add("خطأ في ترحيل العملاء الأصفار: " + ex.Message); }
    }

    private static int Exec(IDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }
}
