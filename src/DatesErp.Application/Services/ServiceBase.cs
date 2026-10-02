using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// قاعدة مشتركة لخدمات الأعمال:
/// §6 معاملات ذرية (Commit/Rollback كامل)، §5 ترجمة تعارض التزامن، §21 انقطاع الشبكة،
/// §10 فحص الصلاحيات، §9 قيد حركة مخزون مرتبطة بمستند مع حماية الرصيد السالب.
/// </summary>
public abstract class ServiceBase
{
    protected readonly DatesErpDbContext Db;
    protected readonly ICurrentSession Session;
    protected readonly INumberingService Numbering;

    protected ServiceBase(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
    {
        Db = db;
        Session = session;
        Numbering = numbering;
    }

    /// <summary>§10 — فحص صلاحية قبل أي عملية.</summary>
    protected void Require(string module, string action)
    {
        // §R2 — طزاجة المصفوفة: إن تجاوز عمرها دقيقة تُعاد من القاعدة — سحب صلاحية أو تعطيل
        // مستخدم أو انتهاء تفويض يسري على الجلسات الحية بلا انتظار إعادة الدخول.
        if (Session != null && Session.UserId > 0 && (DateTime.Now - Session.CacheBuiltAt).TotalSeconds > 60)
        {
            try { new PermissionService(Db, Session).RefreshSessionCache(Session.UserId); }
            catch { /* تعذر التحديث لا يوقف العمل — المصفوفة الحالية هي الحكم */ }
        }
        if (Session == null || !Session.Can(module, action))
            throw new PermissionDeniedException($"{action} على وحدة {module}");
        // تحديث المسار التشغيلي لا يعطّل الإعدادات/الإصلاح/المستخدمين إذا احتاج رصيد ما للمراجعة.
        if (module is "receiving" or "planning" or "production" or "manualorder" or "execution"
            or "quality" or "finishedgoods" or "delivery" or "reports" or "inventory")
            EnsureReceiptTreatmentsCurrent();
    }

    /// <summary>§6 — تنفيذ عملية داخل معاملة ذرية مع ترجمة موحدة للأخطاء.</summary>
    protected virtual System.Data.IsolationLevel TransactionIsolation => System.Data.IsolationLevel.ReadCommitted;

    // Only explicitly composed internal service instances join the coordinator transaction.
    protected virtual bool RetryTransactionDeadlocks => false;
    internal bool JoinParentTransaction { get; init; }

    protected T RunInTransaction<T>(Func<T> work)
    {
        if (JoinParentTransaction && Db.Database.CurrentTransaction != null) return work();
        // §B84/C1: إعادة محاولة تلقائية عند تعارض القيد الفريد (ترقيم متزامن غالباً):
        // جهازان ولّدا نفس الرقم ← الأول يُحفظ والثاني يُعاد برقم جديد بدل خطأ للمستخدم.
        // التكرار الحقيقي (كود مُدخل مكرر) يفشل بالخطأ الأصلي نفسه بعد المحاولات — لا يتغير سلوكه.
        // §أمان الإعادة: work() تعديلات قاعدة فقط تُلفّ بالكامل قبل كل إعادة (لا آثار خارجية في الخدمات).
        int attempt = 0;
        while (true)
        {
            attempt++;
            using var tx = TransactionIsolation == System.Data.IsolationLevel.Serializable
                ? Db.Database.BeginTransaction(System.Data.IsolationLevel.Serializable)
                : Db.Database.BeginTransaction();
            try
            {
                var result = work();
                // A normal OpResult.Fail is NOT a successful transaction. Some
                // callers return it after SaveChanges (e.g. reserving a document
                // number); committing that path would silently persist a failed
                // operation on every client, especially dangerous on SQL Server.
                if (result is OpResult op && !op.Ok)
                {
                    tx.Rollback();
                    Db.ChangeTracker.Clear();
                    return result;
                }
                Db.SaveChanges();
                tx.Commit();
                return result;
            }
            catch (Exception ex) when (RetryTransactionDeadlocks && IsSqlDeadlock(ex) && attempt < 3)
            {
                try { tx.Rollback(); } catch { }
                Db.ChangeTracker.Clear();
                System.Threading.Thread.Sleep(80 * attempt);
            }
            catch (DbUpdateConcurrencyException)
            {
                tx.Rollback();
                Db.ChangeTracker.Clear();
                throw new ConcurrencyConflictException(); // §5 رسالة عربية موحدة
            }
            catch (SqlException ex) when (IsConnectionError(ex))
            {
                try { tx.Rollback(); } catch { }
                throw new ServerUnavailableException(); // §21 لا حفظ جزئي عند انقطاع الشبكة
            }
            catch (Exception ex) when (attempt < 3 && IsUniqueViolation(ex))
            {
                try { tx.Rollback(); } catch { }
                Db.ChangeTracker.Clear();
                System.Threading.Thread.Sleep(80 * attempt);
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                // §الدفاعية: كيانات العملية الفاشلة لا تُسرَّب للعملية التالية
                Db.ChangeTracker.Clear();
                throw; // §28 تُعرض رسالة عامة في الواجهة والتفاصيل في سجل الأخطاء
            }
        }
    }

    protected void RunInTransaction(Action work) => RunInTransaction<object>(() => { work(); return null; });

    /// <summary>§28 — واجهة موحدة: أخطاء الأعمال تتحول إلى رسالة عربية في OpResult ولا تُعرض مكدسات استثناء.</summary>
    protected OpResult RunOp(Func<OpResult> work)
    {
        try { return RunInTransaction(work); }
        // §B102 — الصلاحيات propagate دائماً (اتساقاً مع Require خارج المعاملة):
        // رفض الصلاحية قرار أمني يظهر للاستدعاء لا نتيجة عمل تُبتلع داخلها.
        catch (Core.Exceptions.PermissionDeniedException) { throw; }
        catch (DomainException ex) { return OpResult.Fail(ex.Message); }
    }

    private static bool IsSqlDeadlock(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is SqlException sql && sql.Number == 1205) return true;
        return false;
    }

    private static bool IsConnectionError(SqlException ex)
        => ex.Class >= 20 || ex.Number is 53 or -2 or 10053 or 10054 or 10060 or 64;

    /// <summary>§B84/C1: كشف انتهاك القيد الفريد عبر المزودين (SQL Server + SQLite) بلا اعتماديات جديدة.</summary>
    private static bool IsUniqueViolation(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is SqlException sql && (sql.Number == 2627 || sql.Number == 2601)) return true;
            var t = e.GetType();
            // §SQLite: كشف بالاسم (Microsoft.Data.Sqlite غير مرجعة هنا) + كود 19 أو نص القيد
            if (t.Name == "SqliteException")
            {
                var prop = t.GetProperty("SqliteErrorCode");
                if (prop?.GetValue(e) is int code && code == 19) return true;
                if ((e.Message ?? "").Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)) return true;
            }
            if ((e.Message ?? "").Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    protected void EnsureReceiptTreatmentsCurrent()
        => new ReceivingTreatmentLifecycle(Db, Session, Numbering).Synchronize();

    /// <summary>لا اعتماد/بدء إنتاج ببند جديد محجوز، حتى لو استُدعي المسار بعيداً عن الواجهة.</summary>
    protected void GuardReceivingOrderReadiness(IEnumerable<int?> lotIds)
    {
        var ids = lotIds.Where(i => i != null).Select(i => i.Value).Distinct().ToList();
        var itemIds = Db.Lots.Where(l => ids.Contains(l.Id)).Select(l => l.ShipmentItemId).ToList();
        var now = Db.BusinessNow;
        var held = Db.ShipmentItems.AsNoTracking().FirstOrDefault(i => itemIds.Contains(i.Id) && i.TreatmentRequired == true
            && (i.TreatmentUntilDate == null || i.TreatmentUntilDate > now || i.TreatmentCompletedAt == null));
        if (held != null)
            throw new DomainException($"بند الاستلام #{held.Id} قيد المعالجة حتى {held.TreatmentUntilDate:dd/MM/yyyy}؛ لا بدء أو اعتماد إنتاج قبل إكمال المعالجة.");
    }

    protected bool LotRequiresTreatment(Lot lot)
        => Db.ShipmentItems.AsNoTracking().Where(i => i.Id == lot.ShipmentItemId)
               .Select(i => i.TreatmentRequired).FirstOrDefault()
           ?? Db.Products.AsNoTracking().Where(p => p.Id == lot.ProductId)
               .Select(p => p.RequiresTreatment).FirstOrDefault();

    protected int WarehouseId(string code)
        => Db.Warehouses.FirstOrDefault(w => w.WarehouseCode == code)?.Id
           ?? throw new DomainException($"المخزن {code} غير معرّف.");

    /// <summary>
    /// §9 — قيد حركة مخزون مرتبطة بمستند + تحديث الرصيد الجاري.
    /// §8 — منع الرصيد السالب (لا صرف أكثر من المتوفر) ومنع تكرار نفس الحركة لنفس المستند.
    /// §1.50.66 — سلامة المخزون: PackagingTypeId جزء من مفتاح الرصيد + منع سالب إلا بتسوية مصرحة + منع خلط عملاء.
    /// </summary>
    protected InventoryTransaction PostStockMovement(
        int warehouseId, MovementType movement,
        double qtyKg, int packageCount,
        ReferenceDocType refType, string refDocNumber,
        int? productId = null, int? materialId = null, int? lotId = null,
        int? customerId = null, int? orderId = null, int? packagingTypeId = null,
        string notes = null)
    {
        // §8 منع تكرار العملية: نفس المستند + نفس الصنف + نفس النوع + نفس العميل + نفس العبوة
        // §1.50.66 — إضافة CustomerId و PackagingTypeId لمنع تكرار وهمي وخلط عملاء/عبوات
        var duplicate = Db.InventoryTransactions.Any(t =>
            t.ReferenceDocType == refType && t.ReferenceDocNumber == refDocNumber &&
            t.MovementType == movement && t.WarehouseId == warehouseId &&
            t.ProductId == productId && t.MaterialId == materialId && t.LotId == lotId &&
            t.CustomerId == customerId && t.PackagingTypeId == packagingTypeId);
        if (duplicate)
            throw new DomainException("تم تنفيذ هذه الحركة مسبقاً لنفس المستند — لا يسمح بتكرار العملية.", "DUPLICATE");

        // §1.50.66 — مفتاح الرصيد الكامل: Warehouse + Product + Material + Lot + Customer + PackagingType
        var balance = Db.StockBalances.FirstOrDefault(b =>
            b.WarehouseId == warehouseId && b.ProductId == productId &&
            b.MaterialId == materialId && b.LotId == lotId && b.CustomerId == customerId &&
            b.PackagingTypeId == packagingTypeId);
        if (balance == null)
        {
            balance = new StockBalance
            {
                WarehouseId = warehouseId,
                ProductId = productId,
                MaterialId = materialId,
                LotId = lotId,
                CustomerId = customerId,
                PackagingTypeId = packagingTypeId
            };
            Db.StockBalances.Add(balance);
        }

        var delta = movement == MovementType.Inbound ? Math.Abs(qtyKg) : -Math.Abs(qtyKg);
        var pkgDelta = movement == MovementType.Inbound ? Math.Abs(packageCount) : -Math.Abs(packageCount);

        // §1.50.66 — منع الرصيد السالب إلا من خلال تسوية مصرح بها (Adjustment)
        // Ordinary production consumption is NOT a stock adjustment. Letting it
        // overdraft a missing stock key hid lot/package mismatches as negative
        // warehouse balances (and later made legitimate returns fail).
        bool isAdjustment = movement == MovementType.Adjustment || refType == ReferenceDocType.Adjustment;
        if (!isAdjustment)
        {
            if (balance.QtyKg + delta < -0.001)
                throw new DomainException(
                    $"الكمية المطلوبة أكبر من المتوفر في المخزن.\nالرصيد الحالي: {balance.QtyKg:N1} كجم — المطلوب: {Math.Abs(qtyKg):N1} كجم — المخزن: {warehouseId} — العبوة: {packagingTypeId?.ToString() ?? "بدون"} — العميل: {customerId?.ToString() ?? "عام"}",
                    "INSUFFICIENT_STOCK");
            if (balance.PackageCount + pkgDelta < 0)
                throw new DomainException(
                    $"عدد العبوات المطلوب أكبر من المتوفر.\nالرصيد الحالي: {balance.PackageCount:N0} — المطلوب: {Math.Abs(packageCount):N0} — المخزن: {warehouseId} — العبوة: {packagingTypeId?.ToString() ?? "بدون"} — العميل: {customerId?.ToString() ?? "عام"}",
                    "INSUFFICIENT_STOCK");
        }

        // §1.50.66 — منع خلط مخزون عميل بآخر: إذا كان الرصيد له عميل محدد، لا يسمح بحركة لعميل آخر على نفس المفتاح (المفتاح يضمن الفصل، لكن نحمي من null→محدد)
        // المفتاح نفسه يفصل، لكن إذا كان balance.CustomerId != null و customerId == null و العملية Outbound، فهذا خلط
        if (balance.CustomerId != null && customerId == null && movement == MovementType.Outbound && balance.QtyKg > 0)
        {
            // السماح فقط إذا كان المخزن عام (WFG قد يكون عام)، لكن إذا كان الرصيد لعميل محدد وطلب عام، نمنع
            // نتحقق: هل الرصيد لعميل محدد والطلب عام؟ هذا خلط
            if (balance.CustomerId != null && customerId == null)
            {
                // نسمح فقط إذا كان المنتج مساعد (Material) أو بدون عميل أصلاً، لكن للمنتج التام نمنع
                if (productId != null && materialId == null)
                {
                    // منتج تام — يجب أن يتطابق العميل
                    throw new DomainException($"منع خلط مخزون العملاء: الرصيد يخص العميل {balance.CustomerId} والحركة بدون عميل — حدد العميل.", "CUSTOMER_MIX");
                }
            }
        }
        if (customerId != null && balance.CustomerId != null && balance.CustomerId != customerId)
        {
            throw new DomainException($"منع خلط مخزون العملاء: الرصيد يخص العميل {balance.CustomerId} والحركة للعميل {customerId}.", "CUSTOMER_MIX");
        }

        balance.QtyKg += delta;
        balance.PackageCount += pkgDelta;

        var txn = new InventoryTransaction
        {
            TxnNumber = Numbering.Next("TXN"),
            TxnDate = DateTime.Now,
            WarehouseId = warehouseId,
            ProductId = productId,
            MaterialId = materialId,
            LotId = lotId,
            CustomerId = customerId,
            OrderId = orderId,
            PackagingTypeId = packagingTypeId,
            MovementType = movement,
            QtyKg = delta,
            PackageCount = pkgDelta,
            ReferenceDocType = refType,
            ReferenceDocNumber = refDocNumber,
            IsApproved = true,
            Notes = notes,
            MachineName = Environment.MachineName
        };
        Db.InventoryTransactions.Add(txn);
        return txn;
    }

    /// <summary>
    /// جوهر قيد الحجز المخزني: بنود الاستلام الحالية تحمل التاريخ الدقيق ورابط البند؛
    /// المحرك التاريخي يحتفظ بالساعات للعمليات السابقة. لا توجد شاشة بدء مستقلة حالياً.
    /// نسخة واحدة لحركة المصدر → WTRT تحفظ توازن المخزون والعبوات.
    ///
    /// يفترض أن المستدعي **داخل معاملة قائمة** ولا يفتح معاملة جديدة ولا يفحص صلاحية —
    /// كلاهما مسؤولية الخدمة المستدعية (الاستلام يفحص «اعتماد استلام»، والشاشة تفحص «بدء معالجة»).
    /// </summary>
    /// <param name="checkEligibility">
    /// فحص «الكمية لا تتجاوز المتاح للمعالجة». يُعطَّل عند الاستلام لأن الدفعة تُنشأ
    /// في اللحظة نفسها من كمية البند، فالكمية مضمونة بحكم مصدرها ولا مخزون سابقاً لمقارنته.
    /// </param>
    protected RawTreatment StartTreatmentCore(
        Lot lot, int? treatmentTypeId, double qtyKg, int packageCount,
        DateTime startedAt, double hours, int? responsibleUserId, string notes,
        bool checkEligibility = true, int? sourceWarehouseId = null, int? receivingItemId = null, DateTime? exactReadyAt = null)
    {
        if (lot == null) throw new DomainException("الدفعة غير موجودة.");
        if (qtyKg <= 0) throw new DomainException("الكمية يجب أن تكون أكبر من صفر.");
        if (!double.IsFinite(hours) || hours < 0 || (hours == 0 && receivingItemId == null))
            throw new DomainException("مدة المعالجة غير محددة — أدخلها أو اختر نوع معالجة له مدة افتراضية.");

        if (checkEligibility)
        {
            // §الكمية القابلة للإدخال في معالجة = المخزون − ما هو تحت المعالجة الآن − المحجوز
            // للخطط. طرح المحجوز مقصود: لو أُدخلت كمية محجوزة لخطة معتمدة إلى المعالجة
            // لتعطّلت خطة قائمة بلا إنذار — والخطة المعتمدة التزام قائم لا يُنقض ضمناً.
            double eligible = lot.InStockQtyKg - lot.UnderTreatmentQtyKg - lot.ReservedQtyKg;
            if (qtyKg > eligible + 0.001)
                throw new DomainException(
                    $"الكمية المطلوبة ({qtyKg:N1} كجم) تتجاوز المتاح للمعالجة في الدفعة {lot.LotCode}.\n"
                    + $"المخزون: {lot.InStockQtyKg:N1} — تحت المعالجة: {lot.UnderTreatmentQtyKg:N1} "
                    + $"— المحجوز لخطط: {lot.ReservedQtyKg:N1} — القابل للإدخال: {Math.Max(0, eligible):N1} كجم");
        }

        var t = new RawTreatment
        {
            TreatmentNo = Numbering.Next("TRT"),
            ReceivingItemId = receivingItemId,
            LotId = lot.Id,
            ProductId = lot.ProductId,           // §لا صنف جديد: يُنسخ من الدفعة كما هو
            TreatmentTypeId = treatmentTypeId,
            QtyKg = qtyKg,
            PackageCount = packageCount,
            StartedAt = startedAt,
            DurationHours = hours,
            ExpectedReadyAt = exactReadyAt ?? startedAt.AddHours(hours),   // §يُحسب تلقائياً
            ResponsibleUserId = responsibleUserId ?? Session?.UserId,
            Notes = notes,
            Status = TreatmentStatuses.InProgress
        };
        Db.RawTreatments.Add(t);
        Db.SaveChanges(); // للحصول على المعرف قبل قيد الحركة

        // §حركة المخزون: خروج من الخام ودخول إلى مستودع المعالجة — بنفس الكمية
        // §مخزن المصدر: مخزن الخام الافتراضي WRM، أو مخزن الاستلام الفعلي حين تبدأ
        // المعالجة من سند استلام وصل إلى مخزن خام آخر (خام 2 / ثلاجة...).
        int srcWh = sourceWarehouseId ?? WarehouseId("WRM");
        PostStockMovement(srcWh, MovementType.Outbound, qtyKg, packageCount,
            ReferenceDocType.TreatmentStart, t.TreatmentNo,
            productId: t.ProductId, lotId: lot.Id, customerId: lot.CustomerId,
            packagingTypeId: lot.PackagingTypeId, notes: $"بدء معالجة {t.TreatmentNo}");
        PostStockMovement(WarehouseId("WTRT"), MovementType.Inbound, qtyKg, packageCount,
            ReferenceDocType.TreatmentStart, t.TreatmentNo,
            productId: t.ProductId, lotId: lot.Id, customerId: lot.CustomerId,
            packagingTypeId: lot.PackagingTypeId, notes: $"بدء معالجة {t.TreatmentNo}");

        // §InStockQtyKg لا يتغير — الكمية انتقلت بين مستودعين ولم تغادر المنشأة
        lot.UnderTreatmentQtyKg += qtyKg;
        Db.SaveChanges();
        return t;
    }

    /// <summary>خصم كمية من دفعة (Lot) مع حماية السالب.</summary>
    protected void ConsumeLot(int lotId, double qtyKg, string what)
    {
        var lot = Db.Lots.FirstOrDefault(l => l.Id == lotId)
                  ?? throw new DomainException("الدفعة غير موجودة.");
        if (lot.InStockQtyKg - qtyKg < -0.001)
            throw new DomainException($"الكمية أكبر من رصيد الدفعة {lot.LotCode}.\nالمتاح: {lot.InStockQtyKg:N1} كجم", "INSUFFICIENT_LOT");

        // §المعالجة والتعقيم — **شبكة الأمان الأخيرة** (الموضع 12 في جرد AvailableQtyKg).
        // كل مسارات الصرف تمر من هنا، فحتى لو التفّ مسار جديد على حراس التخطيط
        // لا يستطيع استهلاك خام تحت المعالجة. يُفحص **بعد** حارس الرصيد أعلاه
        // كي تبقى رسالة «الرصيد لا يكفي» هي الأدق حين يكون النقص نقص رصيد فعلاً.
        GuardTreatedStock(lot, qtyKg);

        lot.InStockQtyKg -= qtyKg;
        lot.ProducedQtyKg += qtyKg;
    }

    /// <summary>
    /// §المعالجة والتعقيم — يمنع صرف كمية لم تكتمل معالجتها.
    ///
    /// القرار الجديد يؤخذ من البند؛ علم الصنف مرجع السجلات السابقة ذات القرار NULL فقط:
    /// التمور المجففة وغيرها لا تحتاج تعقيماً، والإلزام الشامل كان سيعطّل خطوطاً
    /// لا علاقة لها بالموضوع.
    ///
    /// المتاح للصرف = <c>TreatmentReadyQtyKg</c> − ما استُهلك منه سابقاً. ويُشتق
    /// المستهلك من <c>ProducedQtyKg</c> بدل عمود جديد، فلا مصدر حقيقة ثانٍ يتناقض.
    /// </summary>
    protected void GuardTreatedStock(Lot lot, double qtyKg)
    {
        var item = Db.ShipmentItems.AsNoTracking().FirstOrDefault(i => i.Id == lot.ShipmentItemId);
        if (item?.TreatmentRequired == true &&
            (item.TreatmentUntilDate == null || item.TreatmentUntilDate > Db.BusinessNow || item.TreatmentCompletedAt == null))
            throw new DomainException($"الدفعة {lot.LotCode} قيد المعالجة حتى {item.TreatmentUntilDate:dd/MM/yyyy}؛ لا صرف قبل إكمالها آلياً.");
        if (!LotRequiresTreatment(lot)) return;

        double readyLeft = lot.TreatmentReadyQtyKg - lot.ProducedQtyKg;
        if (qtyKg <= readyLeft + 0.001) return;

        throw new DomainException(
            $"⛔ لا يمكن صرف {qtyKg:N1} كجم من الدفعة {lot.LotCode}: لم تكتمل معالجتها.\n"
            + $"الجاهز للإنتاج: {Math.Max(0, readyLeft):N1} كجم — تحت المعالجة: {lot.UnderTreatmentQtyKg:N1} كجم.\n"
            + "راجع حالة البند داخل سند الاستلام؛ السجلات السابقة تحتفظ بضوابط الوقت والجودة الأصلية.",
            "TREATMENT_INCOMPLETE");
    }
}
