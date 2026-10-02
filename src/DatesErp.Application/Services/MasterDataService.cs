using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>§23 — إدارة البيانات الأساسية: جديد/تعديل/حذف مع الصلاحيات والتدقيق.</summary>
public partial class MasterDataService : ServiceBase
{
    public MasterDataService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    /// <summary>§تتبع الصنف: «تمور» مجموعة/فئة فقط — لا تصلح اسماً لصنف خام أو تام.</summary>
    private static bool IsGenericCategoryName(string name)
    {
        var n = (name ?? "").Trim();
        return n == "تمور" || n == "تمر" || n == "تمور تامة" || n == "تمر تام";
    }

    /// <summary>§تدقيق عمليات إدارة المستخدمين — إلحاقي في AuditLog المركزي.</summary>
    private void AuditUser(string action, string actionType, string screen, string targetName, int targetId)
    {
        Db.AuditLogs.Add(new AuditLog
        {
            UserId = Session?.UserId > 0 ? Session.UserId : null,
            UserName = Session?.UserName ?? "system",
            MachineName = System.Environment.MachineName,
            ActionDate = DateTime.Now,
            ScreenName = screen,
            ActionType = actionType,
            DocumentType = "User",
            DocumentNumber = targetName,
            RecordId = targetId,
            NewValue = action
        });
    }

    // A reused service can retain a tracked user across a login, reset or lockout
    // written by another scope/device. Refresh the row and its concurrency token
    // before password checks and account administration (never reuse stale hashes).
    private AppUser LoadUserFresh(int userId)
    {
        var user = Db.Users.FirstOrDefault(x => x.Id == userId);
        if (user == null) return null;
        var entry = Db.Entry(user);
        entry.Reload();
        return entry.State == EntityState.Detached ? null : user;
    }

    // ─────────────── العملاء ───────────────
    public OpResult SaveCustomer(int? id, string code, string name, string type, string phone, string contact, bool isActive, int priorityNo = 0)
    {
        Require("customers", id == null ? "Create" : "Edit");
        if (string.IsNullOrWhiteSpace(name)) return OpResult.Fail("أدخل اسم العميل.");
        code = code?.Trim();
        return RunOp(() =>
        {
            var c = id == null ? new Customer() : Db.Customers.First(x => x.Id == id);
            if (!string.IsNullOrWhiteSpace(code) && Db.Customers.Any(x => x.CustomerCode == code && x.Id != c.Id))
                throw new DomainException("هذا الرقم محجوز — اختر رقماً آخر.");
            // §47 — لا اقتراح مسبق في الشاشة: الفراغ يُولَّد له كود فريد هنا داخل معاملة الحفظ
            if (string.IsNullOrWhiteSpace(code))
                code = (id != null && !string.IsNullOrWhiteSpace(c.CustomerCode))
                    ? c.CustomerCode
                    : NextMasterCode("C", x => Db.Customers.Any(z => z.CustomerCode == x));
            c.CustomerCode = code;
            c.CustomerName = name;
            c.CustomerType = type;
            c.Phone = phone;
            c.ContactPerson = contact;
            c.IsActive = isActive;
            c.PriorityNo = priorityNo;   // §B77 أولوية التوزيع
            if (id == null) Db.Customers.Add(c);
            Db.SaveChanges();
            return OpResult.Success(id == null ? $"تم إنشاء العميل — الكود: {c.CustomerCode}." : $"تم حفظ التعديلات — الكود: {c.CustomerCode}.", c.Id, c.CustomerCode);
        });
    }

    public OpResult DeleteCustomer(int id)
    {
        Require("customers", "Delete");
        return RunOp(() =>
        {
            var c = Db.Customers.FirstOrDefault(x => x.Id == id);
            if (c == null) throw new DomainException("العميل غير موجود.");
            if (Db.Shipments.Any(s => s.CustomerId == id) || Db.CustomerDeliveries.Any(d => d.CustomerId == id))
            {
                c.IsActive = false; // مرتبط بعمليات — يُعطَّل بدل الحذف حفاظاً على التتبع (§9)
                Db.SaveChanges();
                return OpResult.Success("العميل مرتبط بعمليات سابقة — تم تعطيله بدل الحذف.");
            }
            Db.Customers.Remove(c);
            Db.SaveChanges();
            return OpResult.Success("تم حذف العميل.");
        });
    }

    // ─────────────── الموردون ───────────────
    public OpResult SaveSupplier(int? id, string code, string name, string phone, bool isActive)
    {
        Require("suppliers", id == null ? "Create" : "Edit");
        if (string.IsNullOrWhiteSpace(name)) return OpResult.Fail("أدخل اسم المورد.");
        code = code?.Trim();
        return RunOp(() =>
        {
            var c = id == null ? new Supplier() : Db.Suppliers.First(x => x.Id == id);
            if (!string.IsNullOrWhiteSpace(code) && Db.Suppliers.Any(x => x.SupplierCode == code && x.Id != c.Id))
                throw new DomainException("هذا الرقم محجوز — اختر رقماً آخر.");
            // §47 — الترقيم عند الحفظ في الخادم فقط
            if (string.IsNullOrWhiteSpace(code))
                code = (id != null && !string.IsNullOrWhiteSpace(c.SupplierCode))
                    ? c.SupplierCode
                    : NextMasterCode("S", x => Db.Suppliers.Any(z => z.SupplierCode == x));
            c.SupplierCode = code;
            c.SupplierName = name;
            c.Phone = phone;
            c.IsActive = isActive;
            if (id == null) Db.Suppliers.Add(c);
            Db.SaveChanges();
            return OpResult.Success(id == null ? $"تم إنشاء المورد — الكود: {c.SupplierCode}." : $"تم حفظ التعديلات — الكود: {c.SupplierCode}.", c.Id, c.SupplierCode);
        });
    }

    public OpResult DeleteSupplier(int id)
    {
        Require("suppliers", "Delete");
        return RunOp(() =>
        {
            var c = Db.Suppliers.FirstOrDefault(x => x.Id == id);
            if (c == null) throw new DomainException("المورد غير موجود.");
            c.IsActive = false;
            Db.SaveChanges();
            return OpResult.Success("تم تعطيل المورد.");
        });
    }

    // ─────────────── الأصناف ───────────────
    public OpResult SaveProduct(int? id, string code, string name, string groupCode, string itemType, string unit, double cartonWeight, double hourlyRate, bool isActive, double? yieldFactor = null)
    {
        Require("products", id == null ? "Create" : "Edit");
        if (string.IsNullOrWhiteSpace(name)) return OpResult.Fail("أدخل اسم الصنف.");
        if ((itemType == "Raw" || itemType == "Finished") && IsGenericCategoryName(name))
            return OpResult.Fail("«تمور» اسم مجموعة/فئة فقط ولا يصلح بديلاً عن الصنف الفعلي — أدخل اسم الصنف الحقيقي (سكري، خلاص، صقعي، برحي...).");
        code = code?.Trim();
        return RunOp(() =>
        {
            var p = id == null ? new Product() : Db.Products.First(x => x.Id == id);
            if (!string.IsNullOrWhiteSpace(code) && Db.Products.Any(x => x.ProductCode == code && x.Id != p.Id))
                throw new DomainException("هذا الرقم محجوز — اختر رقماً آخر.");
            // §47 — الترقيم في الخادم لحظة الحفظ فقط (لا اقتراح مسبقاً في العميل)
            if (string.IsNullOrWhiteSpace(code))
                code = (id != null && !string.IsNullOrWhiteSpace(p.ProductCode))
                    ? p.ProductCode
                    : NextProductCodeInternal(string.IsNullOrWhiteSpace(groupCode) ? "001" : groupCode.Trim());
            p.ProductCode = code;
            p.ProductNameAr = name;
            p.GroupCode = groupCode;
            p.ItemType = itemType;
            p.UnitOfMeasure = unit;
            p.CartonWeightKg = cartonWeight;
            p.HourlyProductionRate = hourlyRate > 0 ? hourlyRate : p.HourlyProductionRate;
            if (yieldFactor != null) p.YieldFactor = yieldFactor; // §B85/H3
            p.IsActive = isActive;
            if (id == null) Db.Products.Add(p);
            Db.SaveChanges();
            return OpResult.Success(id == null ? $"تم إنشاء الصنف — الكود: {p.ProductCode}." : $"تم حفظ التعديلات — الكود: {p.ProductCode}.", p.Id, p.ProductCode);
        });
    }

    public OpResult DeleteProduct(int id)
    {
        Require("products", "Delete");
        return RunOp(() =>
        {
            var p = Db.Products.FirstOrDefault(x => x.Id == id);
            if (p == null) throw new DomainException("الصنف غير موجود.");
            // §تتبع الصنف: أي عملية مرتبطة بالصنف تمنع حذفه — يُعطَّل حفاظاً على الرحلة الكاملة
            bool used = Db.ShipmentItems.Any(i => i.ProductId == id)
                        || Db.Lots.Any(l => l.ProductId == id)
                        || Db.ProductionPlanItems.Any(i => i.ProductId == id)
                        || Db.ProductionOrderItems.Any(i => i.ProductId == id)
                        || Db.QualityCheckItems.Any(i => i.ProductId == id)
                        || Db.FinishedGoodsReceiptItems.Any(i => i.ProductId == id)
                        || Db.CustomerDeliveryItems.Any(i => i.ProductId == id)
                        || Db.StockBalances.Any(b => b.ProductId == id && b.QtyKg != 0);
            if (used)
            {
                p.IsActive = false;
                Db.SaveChanges();
                return OpResult.Success("الصنف مستخدم في عمليات — تم تعطيله بدل الحذف حفاظاً على التتبع.");
            }
            Db.Products.Remove(p);
            Db.SaveChanges();
            return OpResult.Success("تم حذف الصنف.");
        });
    }

    // ─────────────── المخازن ───────────────
    public OpResult SaveWarehouse(int? id, string code, string name, string type, bool isActive)
    {
        Require("inventory", id == null ? "Create" : "Edit");
        if (string.IsNullOrWhiteSpace(name)) return OpResult.Fail("أدخل اسم المخزن.");
        code = code?.Trim();
        return RunOp(() =>
        {
            var w = id == null ? new Warehouse() : Db.Warehouses.First(x => x.Id == id);
            if (!string.IsNullOrWhiteSpace(code) && Db.Warehouses.Any(x => x.WarehouseCode == code && x.Id != w.Id))
                throw new DomainException("هذا الرقم محجوز — اختر رقماً آخر.");
            // §47 — الترقيم عند الحفظ في الخادم فقط
            if (string.IsNullOrWhiteSpace(code))
                code = (id != null && !string.IsNullOrWhiteSpace(w.WarehouseCode))
                    ? w.WarehouseCode
                    : NextMasterCode("W", x => Db.Warehouses.Any(z => z.WarehouseCode == x));
            w.WarehouseCode = code;
            w.WarehouseNameAr = name;
            w.WarehouseType = type;
            w.IsActive = isActive;
            if (id == null) Db.Warehouses.Add(w);
            Db.SaveChanges();
            return OpResult.Success(id == null ? $"تم إنشاء المخزن — الكود: {w.WarehouseCode}." : $"تم حفظ التعديلات — الكود: {w.WarehouseCode}.", w.Id, w.WarehouseCode);
        });
    }

    public OpResult DeleteWarehouse(int id)
    {
        Require("inventory", "Delete");
        return RunOp(() =>
        {
            var w = Db.Warehouses.FirstOrDefault(x => x.Id == id);
            if (w == null) throw new DomainException("المخزن غير موجود.");
            if (Db.StockBalances.Any(b => b.WarehouseId == id && (b.QtyKg != 0 || b.PackageCount != 0)))
                throw new DomainException("لا يمكن حذف مخزن فيه أرصدة — قم بتصفيتها أولاً.");
            if (Db.InventoryTransactions.Any(t => t.WarehouseId == id))
            {
                w.IsActive = false;
                Db.SaveChanges();
                return OpResult.Success("المخزن له حركات سابقة — تم تعطيله بدل الحذف.");
            }
            Db.Warehouses.Remove(w);
            Db.SaveChanges();
            return OpResult.Success("تم حذف المخزن.");
        });
    }
}

/// <summary>§أمر التطوير: حفظ الصنف مع طاقته الإنتاجية لكل وردية (المصدر المركزي).</summary>
public partial class MasterDataService
{
    public OpResult SaveProductFull(int? id, string code, string name, string groupCode, string itemType,
        string unit, double cartonWeight, int moldsCount, double moldWeight,
        List<(int shiftId, int? packagingTypeId, int maxCartons)> capacities, int? sourceProductId = null,
        int? sourcePackagingTypeId = null, double? yieldFactor = null, bool? requiresTreatment = null)
    {
        Require("products", id == null ? "Create" : "Edit");
        if (string.IsNullOrWhiteSpace(name)) return OpResult.Fail("أدخل اسم الصنف.");
        if ((itemType == "Raw" || itemType == "Finished") && IsGenericCategoryName(name))
            return OpResult.Fail("«تمور» اسم مجموعة/فئة فقط ولا يصلح بديلاً عن الصنف الفعلي — أدخل اسم الصنف الحقيقي (سكري، خلاص، صقعي، برحي...).");
        code = code?.Trim();

        return RunOp(() =>
        {
            var p = id == null ? new Product() : Db.Products.FirstOrDefault(x => x.Id == id);
            if (id != null && p == null) throw new DomainException("الصنف غير موجود.");
            if (!string.IsNullOrWhiteSpace(code) && Db.Products.Any(x => x.ProductCode == code && x.Id != p.Id))
                throw new DomainException("هذا الرقم محجوز — اختر رقماً آخر.");

            // §نظام الوحدات: ثلاث مجموعات فقط — والمجموعة تحدد النوع والوحدة القياسية
            if (itemType != "Raw" && itemType != "Finished" && itemType != "ByProduct" && itemType != "Pack")
                throw new DomainException("نوع الصنف غير معتمد — المسموح: خام (001) | تام (002) | ثانوي (003) | كرتون مرتجع (004).");
            string stdGroup = itemType == "Raw" ? UnitsPolicy.GroupRaw : itemType == "Finished" ? UnitsPolicy.GroupFinished : itemType == "Pack" ? "004" : UnitsPolicy.GroupByProduct;
            // §B52: إن اختيرت مجموعة قائمة نشطة (قياسية أو منشأة حديثاً) فهي التي تُحفظ على الصنف —
            // المجموعة تحدد استخدام الصنف؛ وإن مُرِّر كود غير موجود يبقى الكود القياسي للنوع.
            if (!string.IsNullOrWhiteSpace(groupCode) && Db.ItemGroups.Any(g => g.GroupCode == groupCode.Trim() && g.IsActive))
                stdGroup = groupCode.Trim();
            // §قاعدة الوحدات: لا تُفرض الوحدات داخل الكود — الوحدة تأتي من تعريف الصنف
            // في هذه الشاشة المركزية. ومجموعة الصنف وتصنيفه هما اللذان يحددان نوعه لا اسم
            // الوحدة؛ فـ«كرتون» قد تكون مجرد عبوة خام عند الاستلام. الفراغ فقط يأخذ
            // افتراض المجموعة تيسيراً لا فرضاً.
            string stdUnit = string.IsNullOrWhiteSpace(unit)
                ? (UnitsPolicy.DefaultUnitFor(itemType) ?? "كجم")
                : unit.Trim();

            // §47 — الترقيم في الخادم لحظة الحفظ فقط: لا يقترح العميل رقماً مسبقاً،
            // فيستحيل أن يحصل مستخدمان ينشئان معاً على الرقم نفسه.
            // (الفهرس الفريد على ProductCode + إعادة المحاولة التلقائية شبكة أمان إضافية.)
            if (string.IsNullOrWhiteSpace(code))
                code = (id != null && !string.IsNullOrWhiteSpace(p.ProductCode))
                    ? p.ProductCode                       // تعديل — يبقى رقمه القائم
                    : NextProductCodeInternal(stdGroup);  // جديد — يُولَّد داخل المعاملة
            p.ProductCode = code;
            p.ProductNameAr = name;
            p.GroupCode = stdGroup;
            p.ItemType = itemType;
            p.UnitOfMeasure = stdUnit;
            p.CartonWeightKg = cartonWeight;
            p.MoldsCount = moldsCount;
            p.MoldWeightKg = moldWeight;
            if (yieldFactor != null) p.YieldFactor = yieldFactor; // §B85/H3: يُحفظ عند تمريره فقط — الفراغ يُبقي القديم
            // §B109: علم «يحتاج معالجة» يُحفظ من بطاقة الصنف. اختياري (null = لا تغيير)
            // كي لا تُصفّر النداءاتُ القديمة علماً مضبوطاً؛ فالحقل كان معرَّفاً في الكيان
            // منذ B107 بلا أي عنصر واجهة يضبطه — فبقي false أبداً في شاشة الاستلام.
            if (requiresTreatment != null) p.RequiresTreatment = requiresTreatment.Value;
            // §إصلاح: كان الحفظ يعيد التفعيل دائماً، فلا سبيل لإيقاف صنف من بطاقته
            if (id == null) p.IsActive = true;
            // §تتبع الصنف: التعريف الرسمي للتحويل — الخام الذي يُنتج منه هذا المنتج
            // (تمرير قيمة يحدّث التعريف؛ تمرير فارغ عند التعديل يبقي التعريف القائم)
            if (sourceProductId != null) p.SourceProductId = sourceProductId;
            else if (id == null) p.SourceProductId = null;
            if (stdGroup == "004") p.SourcePackagingTypeId = sourcePackagingTypeId;
            if (id == null) Db.Products.Add(p);
            Db.SaveChanges();

            // تحقق أن الصنف المصدر صنفاً خاماً قائماً
            if (p.SourceProductId is int srcId)
            {
                var src = Db.Products.FirstOrDefault(x => x.Id == srcId);
                if (src == null) throw new DomainException("الصنف المصدر (الخام) المحدد غير موجود.");
                if (src.ItemType != "Raw") throw new DomainException($"الصنف المصدر «{src.ProductNameAr}» ليس صنفاً خاماً — التحويل الرسمي يكون من خام إلى منتج تام.");
            }

            // §الطاقة الإنتاجية حسب الوردية والعبوة: المُدخل = الطاقة القصوى، والمعدل يُشتق آلياً
            if (itemType == "Finished" && capacities != null)
            {
                foreach (var (shiftId, packagingTypeId, maxCartons) in capacities)
                {
                    var shift = Db.Shifts.FirstOrDefault(s => s.Id == shiftId);
                    if (shift == null) continue;
                    double hours = CapacityPolicy.EffectiveHours(shift.EffectiveProductiveHours, shift.TotalHours);
                    double rate = CapacityPolicy.DeriveRate(maxCartons, hours);
                    var cap = Db.ProductShiftCapacities.FirstOrDefault(c =>
                        c.ProductId == p.Id && c.ShiftId == shiftId && c.PackagingTypeId == packagingTypeId);
                    if (cap == null)
                    {
                        cap = new ProductShiftCapacity { ProductId = p.Id, ShiftId = shiftId, PackagingTypeId = packagingTypeId, IsActive = true };
                        Db.ProductShiftCapacities.Add(cap);
                    }
                    cap.HourlyProductionRate = rate;
                    cap.ShiftCapacity = maxCartons;
                }
                Db.SaveChanges();
            }
            return OpResult.Success(id == null ? $"تم إنشاء الصنف — الكود: {p.ProductCode}." : $"تم حفظ الصنف وطاقاته — الكود: {p.ProductCode}.", p.Id, p.ProductCode);
        });
    }

    /// <summary>
    /// §B80 — العبوات تُقرأ من شاشة الوحدات: كل وحدة قياس نشطة يقابلها نوع عبوة بالاسم نفسه.
    /// تُنشأ العبوة الناقصة تلقائياً (بلا وزن — الوزن يُدخل في الاستلام أو بطاقة الصنف)،
    /// ولا تُحذف عبوة قائمة أبداً (مستنداتها التاريخية تبقى سليمة).
    /// </summary>
    public OpResult SyncPackagingFromUnits()
    {
        return RunOp(() =>
        {
            var unitNames = Db.UnitsOfMeasure.AsNoTracking().Where(u => u.IsActive)
                .Select(u => u.UnitNameAr).ToList()
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct()
                .ToList();
            var existing = Db.PackagingTypes.AsNoTracking().Select(p => p.PackageNameAr).ToList();
            int created = 0;
            foreach (var nm in unitNames)
            {
                if (existing.Contains(nm)) continue;
                Db.PackagingTypes.Add(new Core.Domain.Entities.PackagingType
                {
                    PackageNameAr = nm,
                    UnitWeightKg = 0,
                    IsActive = true
                });
                created++;
            }
            if (created > 0) Db.SaveChanges();
            return OpResult.Success(created > 0
                ? $"تمت مزامنة العبوات مع شاشة الوحدات — أُضيفت {created} عبوة جديدة بأسماء الوحدات."
                : "العبوات متزامنة مع شاشة الوحدات — لا جديد.");
        });
    }

    public OpResult DeleteProductById(int id)
    {
        Require("products", "Delete");
        var p = Db.Products.FirstOrDefault(x => x.Id == id);
        if (p == null) return OpResult.Fail("الصنف غير موجود.");
        if (Db.StockBalances.Any(b => b.ProductId == id && (b.QtyKg != 0 || b.PackageCount != 0)))
        {
            p.IsActive = false;
            Db.SaveChanges();
            return OpResult.Success("الصنف له أرصدة — تم إيقافه بدل الحذف.");
        }
        // §تتبع الصنف: أي عمليات مرتبطة (استلام/خطط/أوامر/فحص/تسليم) تمنع الحذف — إيقاف بدل الحذف
        if (Db.ProductionPlanItems.Any(i => i.ProductId == id) || Db.ProductionOrderItems.Any(i => i.ProductId == id)
            || Db.ShipmentItems.Any(i => i.ProductId == id) || Db.Lots.Any(l => l.ProductId == id)
            || Db.QualityCheckItems.Any(i => i.ProductId == id) || Db.FinishedGoodsReceiptItems.Any(i => i.ProductId == id)
            || Db.CustomerDeliveryItems.Any(i => i.ProductId == id))
        {
            p.IsActive = false;
            Db.SaveChanges();
            return OpResult.Success("الصنف مستخدم في عمليات (استلام/خطط/أوامر/فحص/تسليم) — تم إيقافه بدل الحذف حفاظاً على التتبع.");
        }
        return RunOp(() =>
        {
            Db.Products.Remove(p);
            Db.SaveChanges();
            return OpResult.Success("تم حذف الصنف.");
        });
    }
}


/// <summary>§إدارة الموظفين: كل موظف له رقم (كود) يدخل به للنظام — الرقم فريد ولا يتكرر.</summary>
public partial class MasterDataService
{
    public OpResult SaveEmployee(int? id, string code, string fullName, string jobTitle, string department, string phone, bool isActive)
    {
        // §فحص الموظفين — المورد الحقيقي employees (شاشته محكومة به) بدل admin العام:
        // من مُنح employees/Edit من شاشة الصلاحيات يستطيع الحفظ فعلياً، ومن حُجب عنه لا يرى ولا يعدّل.
        Require("employees", id == null ? "Create" : "Edit"); // §فحص الموظفين: المورد الحقيقي لا admin العام
        if (string.IsNullOrWhiteSpace(fullName)) return OpResult.Fail("أدخل اسم الموظف.");
        code = code?.Trim();
        return RunOp(() =>
        {
            var e = id == null ? new Employee() : Db.Employees.First(x => x.Id == id);
            // §47 — رقم الدخول اختياري: الفراغ يُولَّد في الخادم لحظة الحفظ (EMP###)
            // بدل اقتراحه مسبقاً في الشاشة، منعاً لتعارض مستخدمين يضيفان معاً.
            if (string.IsNullOrWhiteSpace(code))
                code = (id != null && !string.IsNullOrWhiteSpace(e.EmployeeCode))
                    ? e.EmployeeCode
                    : NextMasterCode("EMP", x => Db.Employees.Any(z => z.EmployeeCode == x));
            if (Db.Employees.Any(x => x.EmployeeCode == code && x.Id != e.Id))
                throw new DomainException("هذا الرقم محجوز — اختر رقماً آخر.");
            e.EmployeeCode = code;
            e.FullName = fullName;
            e.JobTitle = jobTitle;
            e.Department = department;
            e.Phone = phone;
            e.IsActive = isActive;
            if (id == null) Db.Employees.Add(e);
            // §R1 — تعطيل الموظف يوقف حساب الدخول المرتبط تلقائياً (المصيران مرتبطان)،
            // بحارس الإغلاق الكلي: لا يُوقف حساب آخر مدير صلاحيات عبر بوابة الموظفين.
            string linkedNote = "";
            if (id != null && !isActive)
            {
                var linked = Db.Users.Where(x => x.EmployeeId == id.Value && x.IsActive).ToList();
                foreach (var lu in linked)
                {
                    try { new PermissionService(Db, Session).GuardLastPermissionAdmin(lu.Id, null); }
                    catch (DomainException gex) { throw new DomainException($"لا يمكن تعطيل الموظف — حسابه المرتبط ({lu.UserName}): {gex.Message}"); }
                }
                foreach (var lu in linked)
                {
                    lu.IsActive = false;
                    AuditUser("Deactivate", "Deactivate", "Users", $"{lu.UserName} (إيقاف تلقائي مع الموظف {e.EmployeeCode})", lu.Id);
                }
                if (linked.Count > 0) { Db.SaveChanges(); linkedNote = $" — وأُوقف حساب الدخول المرتبط ({linked.Count})."; }
            }
            Db.SaveChanges();
            return OpResult.Success(id == null ? $"تم إنشاء الموظف — رقمه: {e.EmployeeCode}." : $"تم حفظ التعديلات — الرقم: {e.EmployeeCode}{linkedNote}.", e.Id, e.EmployeeCode);
        });
    }

    public OpResult DeleteEmployee(int id)
    {
        Require("employees", "Delete"); // §فحص الموظفين
        var e = Db.Employees.FirstOrDefault(x => x.Id == id);
        if (e == null) return OpResult.Fail("الموظف غير موجود.");
        if (Db.Users.Any(u => u.EmployeeId == id) || Db.Shipments.Any(s => s.ReceivedBy == id))
        {
            e.IsActive = false;
            // §R1 — التعطيل بدل الحذف يشمل حسابات الدخول المرتبطة (بحارس آخر مدير صلاحيات)
            var linked = Db.Users.Where(x => x.EmployeeId == id && x.IsActive).ToList();
            foreach (var lu in linked)
            {
                try { new PermissionService(Db, Session).GuardLastPermissionAdmin(lu.Id, null); }
                catch (DomainException gex) { return OpResult.Fail($"لا يمكن تعطيل الموظف — حسابه المرتبط ({lu.UserName}): {gex.Message}"); }
            }
            foreach (var lu in linked)
            {
                lu.IsActive = false;
                AuditUser("Deactivate", "Deactivate", "Users", $"{lu.UserName} (إيقاف تلقائي مع الموظف {e.EmployeeCode})", lu.Id);
            }
            Db.SaveChanges();
            return OpResult.Success(linked.Count > 0
                ? $"الموظف مرتبط بحسابات أو عمليات — تم تعطيله بدل الحذف، وأُوقفت حسابات الدخول المرتبطة ({linked.Count})."
                : "الموظف مرتبط بحسابات أو عمليات — تم تعطيله بدل الحذف.");
        }
        Db.Employees.Remove(e);
        Db.SaveChanges();
        return OpResult.Success("تم حذف الموظف.");
    }

    // ═══════════════ §إدارة المواد المساعدة: مجموعات/مواد/معادلات/مواصفات — بلا ثوابت ═══════════════

    public OpResult SaveAuxGroup(int? id, string code, string name, bool active = true)
    {
        Require("materials", "Edit");
        if (string.IsNullOrWhiteSpace(name)) return OpResult.Fail("أدخل اسم المجموعة.");
        var g = id == null ? new AuxGroup() : Db.AuxGroups.FirstOrDefault(x => x.Id == id);
        if (g == null) return OpResult.Fail("المجموعة غير موجودة.");
        g.GroupCode = string.IsNullOrWhiteSpace(code) ? ("AG-" + Guid.NewGuid().ToString("N")[..5].ToUpper()) : code.Trim();
        g.GroupNameAr = name.Trim();
        g.IsActive = active;
        if (id == null) Db.AuxGroups.Add(g);
        Db.SaveChanges();
        return OpResult.Success("تم حفظ المجموعة.", g.Id, g.GroupCode);
    }

    public OpResult SaveAuxMaterial(int? id, string code, string name, string groupCode, string unit,
        string quality = null, double defaultCost = 0, double lastCost = 0, bool active = true)
    {
        Require("materials", id == null ? "Create" : "Edit");
        if (string.IsNullOrWhiteSpace(name)) return OpResult.Fail("أدخل اسم المادة.");
        var m = id == null ? new AuxiliaryMaterial() : Db.AuxiliaryMaterials.FirstOrDefault(x => x.Id == id);
        if (m == null) return OpResult.Fail("المادة غير موجودة.");
        m.MaterialCode = string.IsNullOrWhiteSpace(code) ? ("AUX-" + Guid.NewGuid().ToString("N")[..5].ToUpper()) : code.Trim();
        m.MaterialNameAr = name.Trim();
        m.GroupCode = groupCode;
        m.UnitOfMeasure = string.IsNullOrWhiteSpace(unit) ? "قطعة" : unit.Trim();
        m.QualityGrade = quality;
        m.DefaultCost = defaultCost;
        if (lastCost > 0) m.LastCost = lastCost;
        m.IsActive = active;
        if (id == null) Db.AuxiliaryMaterials.Add(m);
        Db.SaveChanges();
        return OpResult.Success("تم حفظ المادة المساعدة.", m.Id, m.MaterialCode);
    }

    public OpResult SaveFormulaEx(int? id, int productId, int materialId, double qtyPerUnit, string mode,
        bool optional = false, int? customerId = null, int? packagingTypeId = null, bool active = true)
    {
        Require("materials", "Edit");
        var f = id == null ? new ConsumptionFormula() : Db.ConsumptionFormulas.FirstOrDefault(x => x.Id == id);
        if (f == null) return OpResult.Fail("المعادلة غير موجودة.");
        f.ProductId = productId; f.MaterialId = materialId; f.QtyPerUnit = qtyPerUnit;
        f.Mode = string.IsNullOrWhiteSpace(mode) ? "PerCarton" : mode;
        f.IsOptional = optional; f.CustomerId = customerId; f.PackagingTypeId = packagingTypeId; f.IsActive = active;
        if (id == null) Db.ConsumptionFormulas.Add(f);
        Db.SaveChanges();
        return OpResult.Success("تم حفظ معادلة الاستهلاك.", f.Id);
    }

    public OpResult SaveAuxSpec(int? id, int customerId, int materialId, string brand, double unitCost,
        int? productId = null, int? packagingTypeId = null, int priority = 1, bool active = true)
    {
        Require("materials", "Edit");
        var x = id == null ? new AuxCustomerSpec() : Db.AuxCustomerSpecs.FirstOrDefault(v => v.Id == id);
        if (x == null) return OpResult.Fail("المواصفة غير موجودة.");
        x.CustomerId = customerId; x.MaterialId = materialId; x.BrandName = brand; x.UnitCost = unitCost;
        x.ProductId = productId; x.PackagingTypeId = packagingTypeId; x.Priority = priority; x.IsActive = active;
        if (id == null) Db.AuxCustomerSpecs.Add(x);
        Db.SaveChanges();
        return OpResult.Success("تم حفظ مواصفة العميل — كرتون ماركة مستقلة.", x.Id);
    }

    // ═══════════════ §تفويض زمني: مدير → موظف لفترة محددة ═══════════════
    public OpResult SaveDelegation(int? id, int fromUserId, int toUserId, DateTime start, DateTime end,
        string scopeModule = null, bool active = true)
    {
        Require("users", "Edit");
        if (fromUserId == toUserId) return OpResult.Fail("لا يمكن تفويض المستخدم لنفسه.");
        if (end < start) return OpResult.Fail("تاريخ النهاية قبل البداية.");
        // §B84/V5: النطاق كان نصاً حراً يقبل أكواداً وهمية — الآن يجب أن يكون فارغاً (كل الوحدات)
        // أو كود وحدة حقيقياً من كتالوج الصلاحيات (الواجهة تعرضه قائمة منسدلة).
        if (!string.IsNullOrWhiteSpace(scopeModule) && !PermissionService.ResourceCatalog.Any(r => r.Code == scopeModule.Trim()))
            return OpResult.Fail($"كود الوحدة «{scopeModule}» غير معروف — اختر النطاق من قائمة الوحدات.");
        // §فحص التفويض — الطرفان يجب أن يكونا موجودين ونشطين: تفويض من حساب معطّل كان
        // يمرر صلاحياته للمفوَّض إليه عند الدخول (ثغرة)، وإلى حساب معطّل بلا معنى.
        var fromUser = Db.Users.AsNoTracking().FirstOrDefault(u => u.Id == fromUserId);
        var toUser = Db.Users.AsNoTracking().FirstOrDefault(u => u.Id == toUserId);
        if (fromUser == null || toUser == null) return OpResult.Fail("المفوِّض أو المفوَّض إليه غير موجود.");
        if (!fromUser.IsActive || fromUser.IsLocked)
            return OpResult.Fail($"لا يمكن التفويض من حساب غير نشط أو مقفل ({fromUser.UserName}) — صلاحيات الحساب الموقوف لا تنتقل.");
        if (!toUser.IsActive)
            return OpResult.Fail($"لا يمكن التفويض إلى حساب غير نشط ({toUser.UserName}).");
        var d = id == null ? new Delegation() : Db.Delegations.FirstOrDefault(x => x.Id == id);
        if (d == null) return OpResult.Fail("التفويض غير موجود.");
        string oldV = id == null ? "—" : $"من #{d.FromUserId} إلى #{d.ToUserId} ({d.StartDate:dd/MM/yyyy}–{d.EndDate:dd/MM/yyyy}) نطاق {d.ScopeModule ?? "الكل"} {(d.IsActive ? "نشط" : "موقوف")}";
        d.FromUserId = fromUserId; d.ToUserId = toUserId;
        d.StartDate = start.Date; d.EndDate = end.Date;
        d.ScopeModule = string.IsNullOrWhiteSpace(scopeModule) ? null : scopeModule;
        d.IsActive = active;
        if (id == null) Db.Delegations.Add(d);
        Db.SaveChanges();
        // §فحص التفويض — تدقيق إلحاقي (كان إنشاء/تعديل التفويض يمر بلا أي أثر في السجل المركزي)
        AuditUser(id == null ? "CreateDelegation" : "EditDelegation", id == null ? "CreateDelegation" : "EditDelegation", "Delegations",
            $"{fromUser.UserName} → {toUser.UserName} ({start:dd/MM/yyyy}–{end:dd/MM/yyyy}) نطاق {(d.ScopeModule ?? "كل الوحدات")} — {(active ? "نشط" : "موقوف")} | قبل: {oldV}", d.Id);
        Db.SaveChanges();
        return OpResult.Success("تم حفظ التفويض — يسري عند تسجيل دخول المفوَّض إليه.", d.Id);
    }

    // ═══════════════ §إدارة الوحدات والعبوات ═══════════════
    // ═══════════ §المجموعات والفئات ═══════════

    /// <summary>
    /// §إضافة/تعديل مجموعة أصناف. كانت المجموعات الأربع تُبذر فقط ولا توجد أي دالة لحفظها.
    /// ملاحظة: المجموعة بنيوية — نوع الصنف هو الذي يحدد المجموعة والوحدة القياسية
    /// (UnitsPolicy)، فالمجموعة الجديدة تُستخدم للتصنيف ولربط الأصناف يدوياً.
    /// </summary>
    public OpResult SaveItemGroup(int? id, string code, string name, string groupType, string defaultUnit, bool active = true)
    {
        Require("products", id == null ? "Create" : "Edit");
        if (string.IsNullOrWhiteSpace(name)) return OpResult.Fail("أدخل اسم المجموعة.");
        name = name.Trim();
        code = (code ?? "").Trim();

        return RunOp(() =>
        {
            var g = id == null ? new ItemGroup() : Db.ItemGroups.FirstOrDefault(x => x.Id == id);
            if (id != null && g == null) throw new DomainException("المجموعة غير موجودة.");
            // §B52: منع تكرار اسم المجموعة
            if (Db.ItemGroups.Any(x => x.GroupNameAr == name && x.Id != g.Id))
                throw new DomainException("توجد مجموعة أخرى بنفس الاسم — التكرار ممنوع.");
            // §B52: الكود اختياري — يُولَّد تلقائياً (005، 006، ...) إن تُرك فارغاً
            if (string.IsNullOrWhiteSpace(code)) code = NextGroupCodeInternal();
            if (Db.ItemGroups.Any(x => x.GroupCode == code && x.Id != g.Id))
                throw new DomainException("هذا الكود محجوز لمجموعة أخرى.");
            g.GroupCode = code;
            g.GroupNameAr = name;
            g.GroupType = string.IsNullOrWhiteSpace(groupType) ? "Raw" : groupType;
            // §B52: الوحدات للأصناف لا للمجموعات — إضافة مجموعة لا تحتوي حقل وحدة،
            // فلا تُفرض وحدة افتراضية منها؛ الوحدة تُختار في بطاقة الصنف من قاموس الوحدات.
            g.DefaultUnit = string.IsNullOrWhiteSpace(defaultUnit) ? "" : defaultUnit.Trim();
            g.IsActive = active;
            if (id == null) Db.ItemGroups.Add(g);
            Db.SaveChanges();
            return OpResult.Success($"تم حفظ المجموعة — الكود: {g.GroupCode}.", g.Id);
        });
    }

    /// <summary>
    /// §47 — كود تسلسلي فريد للبيانات الأساسية (عملاء/موردين/مخازن/موظفين):
    /// يُولَّد داخل معاملة الحفظ نفسها — لا تقترح الشاشات أي رقم مسبقاً،
    /// فلا يتعارض مستخدمان يضيفان في اللحظة نفسها.
    /// </summary>
    private static string NextMasterCode(string prefix, Func<string, bool> exists)
    {
        int n = 0;
        string code;
        do { code = prefix + (++n); } while (exists(code));
        return code;
    }

    /// <summary>§B52: التالي في تسلسل أكواد المجموعات (005 بعد 004...).</summary>
    private string NextGroupCodeInternal()
    {
        int max = Db.ItemGroups.AsNoTracking().Select(g => g.GroupCode).ToList()
            .Select(c => int.TryParse(c, out var n) ? n : 0).DefaultIfEmpty(0).Max();
        return (max + 1).ToString("D3");
    }

    /// <summary>
    /// §47 — رقم الصنف التالي (مجموعة-###) يُولَّد داخل معاملة الحفظ نفسها،
    /// ويتقدم حتى رقم غير مستخدم. التعارض المتزامن نادر، والفهرس الفريد
    /// مع إعادة المحاولة في ServiceBase يحسمانه نهائياً.
    /// </summary>
    private string NextProductCodeInternal(string group)
    {
        int max = Db.Products.AsNoTracking()
            .Where(p => p.GroupCode == group && p.ProductCode != null && p.ProductCode.StartsWith(group + "-"))
            .Select(p => p.ProductCode).ToList()
            .Select(c => int.TryParse(c.Substring(group.Length + 1), out var n) ? n : 0)
            .DefaultIfEmpty(0).Max();
        string code;
        do { code = $"{group}-{++max:D3}"; }
        while (Db.Products.Any(x => x.ProductCode == code));
        return code;
    }

    /// <summary>§B52: تفعيل/إيقاف مجموعة — لا حذف؛ المجموعة المستخدمة تُوقَف وتبقى أصنافها.</summary>
    public OpResult ToggleItemGroup(int id)
    {
        Require("products", "Edit");
        return RunOp(() =>
        {
            var g = Db.ItemGroups.FirstOrDefault(x => x.Id == id) ?? throw new DomainException("المجموعة غير موجودة.");
            g.IsActive = !g.IsActive;
            Db.SaveChanges();
            return OpResult.Success(g.IsActive
                ? $"تم تفعيل المجموعة «{g.GroupNameAr}»."
                : $"تم إيقاف المجموعة «{g.GroupNameAr}» — أصنافها ومعاملاتها محفوظة ولا تُحذف.");
        });
    }

    /// <summary>§إضافة/تعديل فئة صنف — تصنيف حر لا يفرضه النظام ولا يؤثر على الوحدات.</summary>
    public OpResult SaveItemCategory(int? id, string code, string name, bool active = true)
    {
        Require("products", id == null ? "Create" : "Edit");
        if (string.IsNullOrWhiteSpace(name)) return OpResult.Fail("أدخل اسم الفئة.");

        return RunOp(() =>
        {
            var c = id == null ? new ItemCategory() : Db.ItemCategories.FirstOrDefault(x => x.Id == id);
            if (id != null && c == null) throw new DomainException("الفئة غير موجودة.");
            c.CategoryCode = string.IsNullOrWhiteSpace(code)
                ? (c.CategoryCode ?? "CAT-" + Guid.NewGuid().ToString("N")[..4].ToUpper())
                : code.Trim();
            if (Db.ItemCategories.Any(x => x.CategoryCode == c.CategoryCode && x.Id != c.Id))
                throw new DomainException("هذا الكود محجوز لفئة أخرى.");
            c.CategoryNameAr = name.Trim();
            c.IsActive = active;
            if (id == null) Db.ItemCategories.Add(c);
            Db.SaveChanges();
            return OpResult.Success("تم حفظ الفئة.", c.Id);
        });
    }

    /// <summary>§ربط صنف بفئة (أو فك ربطه بتمرير null) — بلا أي أثر على المجموعة أو الوحدة.</summary>
    public OpResult SetProductCategory(int productId, int? categoryId)
    {
        Require("products", "Edit");
        return RunOp(() =>
        {
            var p = Db.Products.FirstOrDefault(x => x.Id == productId) ?? throw new DomainException("الصنف غير موجود.");
            if (categoryId != null && !Db.ItemCategories.Any(c => c.Id == categoryId && c.IsActive))
                throw new DomainException("الفئة غير موجودة أو موقوفة.");
            p.CategoryId = categoryId;
            Db.SaveChanges();
            return OpResult.Success(categoryId == null ? "تم فك ربط الصنف بالفئة." : "تم ربط الصنف بالفئة.");
        });
    }

    public OpResult SaveUnit(int? id, string name, bool active = true)
    {
        Require("products", "Edit");
        if (string.IsNullOrWhiteSpace(name)) return OpResult.Fail("أدخل اسم الوحدة.");
        name = name.Trim();

        return RunOp(() =>
        {
            var u = id == null ? new UnitOfMeasure() : Db.UnitsOfMeasure.FirstOrDefault(x => x.Id == id);
            if (u == null) return OpResult.Fail("الوحدة غير موجودة.");
            // §B52: منع تكرار اسم الوحدة
            if (Db.UnitsOfMeasure.Any(x => x.UnitNameAr == name && x.Id != u.Id))
                throw new DomainException("توجد وحدة أخرى بنفس الاسم — التكرار ممنوع.");
            u.UnitNameAr = name;
            u.UnitCode = (u.UnitCode ?? ("U-" + Guid.NewGuid().ToString("N")[..4].ToUpper()));
            u.IsActive = active;
            if (id == null) Db.UnitsOfMeasure.Add(u);
            Db.SaveChanges();
            return OpResult.Success($"تم حفظ الوحدة «{name}».", u.Id);
        });
    }

    /// <summary>§B74: خامات نشطة — تصنيف الصنف من شاشة الأصناف هو الفلتر الوحيد.</summary>
    public List<Product> GetRawItems()
        => Db.Products.AsNoTracking().Where(p => p.IsActive && p.ItemType == "Raw").OrderBy(p => p.ProductCode).ToList();

    /// <summary>§B74: منتجات تامة نشطة.</summary>
    public List<Product> GetFinishedItems()
        => Db.Products.AsNoTracking().Where(p => p.IsActive && p.ItemType == "Finished").OrderBy(p => p.ProductCode).ToList();

    /// <summary>§B74: ما يجوز تسليمه: تام + ثانوي نشط.</summary>
    public List<Product> GetDeliverableItems()
        => Db.Products.AsNoTracking().Where(p => p.IsActive && (p.ItemType == "Finished" || p.ItemType == "ByProduct")).OrderBy(p => p.ProductCode).ToList();

    /// <summary>§B70: مجموعة برقم واسم فقط — قاعدة إعادة التصميم من الصفر.</summary>
    public OpResult SaveGroupMinimal(int? id, string code, string name)
    {
        Require("settings", id == null ? "Create" : "Edit");
        if (string.IsNullOrWhiteSpace(name)) return OpResult.Fail("أدخل اسم المجموعة.");
        name = name.Trim();
        code = (code ?? "").Trim();
        return RunOp(() =>
        {
            var g = id == null ? new ItemGroup() : Db.ItemGroups.FirstOrDefault(x => x.Id == id);
            if (id != null && g == null) throw new DomainException("المجموعة غير موجودة.");
            if (Db.ItemGroups.Any(x => x.GroupNameAr == name && x.Id != g.Id))
                throw new DomainException("توجد مجموعة بنفس الاسم — التكرار ممنوع.");
            if (string.IsNullOrWhiteSpace(code)) code = NextGroupCodeInternal();
            if (Db.ItemGroups.Any(x => x.GroupCode == code && x.Id != g.Id))
                throw new DomainException("رقم المجموعة محجوز لمجموعة أخرى.");
            g.GroupCode = code;
            g.GroupNameAr = name;
            if (id == null)
            {
                // §المجموعات تحمل رقماً واسماً فقط — لا نوع ولا وحدة يفرضان منها
                g.GroupType = "";
                g.DefaultUnit = "";
                g.IsActive = true;
                Db.ItemGroups.Add(g);
            }
            Db.SaveChanges();
            return OpResult.Success($"تم حفظ المجموعة {g.GroupCode} — {g.GroupNameAr}.", g.Id);
        });
    }

    /// <summary>§B71: وحدة باسم فقط — لا ترقيم ولا شيء آخر.</summary>
    public OpResult SaveUnitMinimal(int? id, string name)
    {
        Require("settings", id == null ? "Create" : "Edit");
        if (string.IsNullOrWhiteSpace(name)) return OpResult.Fail("أدخل اسم الوحدة.");
        name = name.Trim();
        return RunOp(() =>
        {
            var u = id == null ? new UnitOfMeasure() : Db.UnitsOfMeasure.FirstOrDefault(x => x.Id == id);
            if (id != null && u == null) throw new DomainException("الوحدة غير موجودة.");
            if (Db.UnitsOfMeasure.Any(x => x.UnitNameAr == name && x.Id != u.Id))
                throw new DomainException("توجد وحدة بنفس الاسم — التكرار ممنوع.");
            u.UnitNameAr = name;
            if (id == null) { u.IsActive = true; Db.UnitsOfMeasure.Add(u); }
            Db.SaveChanges();
            return OpResult.Success($"تم حفظ الوحدة «{name}».", u.Id);
        });
    }

    /// <summary>§B71: حذف الوحدة؛ إن كانت مستخدمة تُوقَف بدل الحذف.</summary>
    public OpResult DeleteOrDisableUnit(int id)
    {
        Require("settings", "Delete");
        return RunOp(() =>
        {
            var u = Db.UnitsOfMeasure.FirstOrDefault(x => x.Id == id) ?? throw new DomainException("الوحدة غير موجودة.");
            bool used = Db.Products.Any(p => p.UnitOfMeasure == u.UnitNameAr)
                || Db.ByProducts.Any(b => b.UnitOfMeasure == u.UnitNameAr)
                || Db.InspectionResultTypes.Any(t => t.UnitId == u.Id);
            if (used)
            {
                u.IsActive = false;
                Db.SaveChanges();
                return OpResult.Success($"الوحدة «{u.UnitNameAr}» مستخدمة — أُوقِفت بدل الحذف.");
            }
            Db.UnitsOfMeasure.Remove(u);
            Db.SaveChanges();
            return OpResult.Success($"تم حذف الوحدة «{u.UnitNameAr}».");
        });
    }

    /// <summary>§B70: حذف المجموعة؛ إن كانت مستخدمة في أصناف تُوقَف بدل الحذف.</summary>
    public OpResult DeleteOrDisableItemGroup(int id)
    {
        Require("settings", "Delete");
        return RunOp(() =>
        {
            var g = Db.ItemGroups.FirstOrDefault(x => x.Id == id) ?? throw new DomainException("المجموعة غير موجودة.");
            if (Db.Products.Any(p => p.GroupCode == g.GroupCode))
            {
                g.IsActive = false;
                Db.SaveChanges();
                return OpResult.Success($"المجموعة «{g.GroupNameAr}» مستخدمة في أصناف — أُوقِفت بدل الحذف حفاظاً على البيانات.");
            }
            Db.ItemGroups.Remove(g);
            Db.SaveChanges();
            return OpResult.Success($"تم حذف المجموعة «{g.GroupNameAr}».");
        });
    }

    /// <summary>§B57: إضافة/تعديل مخرج ثانوي باسم يختاره المستخدم — بلا ثوابت، مع منع التكرار.</summary>
    public OpResult SaveByProduct(int? id, string name, string unit)
    {
        Require("products", "Edit");
        if (string.IsNullOrWhiteSpace(name)) return OpResult.Fail("أدخل اسم المخرج الثانوي.");
        name = name.Trim();
        return RunOp(() =>
        {
            var b = id == null ? new DatesErp.Core.Domain.Entities.ByProduct() : Db.ByProducts.FirstOrDefault(x => x.Id == id);
            if (b == null) return OpResult.Fail("المخرج غير موجود.");
            if (Db.ByProducts.Any(x => x.ByProductNameAr == name && x.Id != b.Id))
                throw new DomainException("يوجد مخرج ثانوي آخر بنفس الاسم — التكرار ممنوع.");
            b.ByProductNameAr = name;
            b.ByProductCode = b.ByProductCode ?? ("BP-" + Guid.NewGuid().ToString("N")[..6].ToUpper());
            b.UnitOfMeasure = string.IsNullOrWhiteSpace(unit) ? "كجم" : unit.Trim();
            b.IsActive = true;
            if (id == null) Db.ByProducts.Add(b);
            Db.SaveChanges();
            return OpResult.Success($"تم حفظ المخرج الثانوي «{name}».", b.Id);
        });
    }

    /// <summary>
    /// §B110 — تفعيل/إيقاف مخرج ثانوي عبر طبقة الخدمات.
    /// قبل هذا الإصلاح كانت شاشة «المجموعات والأصناف» تقلب الحالة بالكتابة المباشرة
    /// على القاعدة — بلا صلاحية وبلا تدقيق ولا منع تكرار.
    /// </summary>
    public OpResult ToggleByProduct(int id)
    {
        Require("products", "Edit");
        return RunOp(() =>
        {
            var b = Db.ByProducts.FirstOrDefault(x => x.Id == id) ?? throw new DomainException("المخرج الثانوي غير موجود.");
            b.IsActive = !b.IsActive;
            Db.SaveChanges();
            return OpResult.Success(b.IsActive
                ? $"تم تفعيل المخرج الثانوي «{b.ByProductNameAr}»."
                : $"تم إيقاف المخرج الثانوي «{b.ByProductNameAr}» — السجلات السابقة محفوظة.");
        });
    }

    /// <summary>
    /// §B110 — حفظ بيانات الشركة (الهوية) عبر طبقة الخدمات مع صلاحية وتدقيق.
    /// قبل هذا الإصلاح كانت شاشة «معلومات النظام» تكتب مباشرة على السياق.
    /// </summary>
    public OpResult SaveCompanyInfo(string nameAr, string nameEn, string address, string phone,
        string email, string taxNumber, string footerNote, byte[] logoBytes, bool logoChanged)
    {
        Require("settings", "Edit");
        if (string.IsNullOrWhiteSpace(nameAr)) return OpResult.Fail("أدخل اسم الشركة (عربي).");
        return RunOp(() =>
        {
            var c = Db.CompanyInfos.OrderBy(x => x.Id).FirstOrDefault();
            if (c == null) { c = new CompanyInfo(); Db.CompanyInfos.Add(c); }
            c.CompanyNameAr = nameAr.Trim();
            c.CompanyNameEn = nameEn?.Trim();
            c.Address = address?.Trim();
            c.Phone = phone?.Trim();
            c.Email = email?.Trim();
            c.TaxNumber = taxNumber?.Trim();
            c.ReportFooterNote = footerNote?.Trim();
            if (logoChanged) c.LogoBytes = logoBytes is { Length: > 0 } ? logoBytes : null;
            Db.SaveChanges();
            return OpResult.Success("تم حفظ بيانات الشركة.");
        });
    }

    /// <summary>§B52: تفعيل/إيقاف وحدة — الوحدة المستخدمة تُوقَف ولا تُحذف.</summary>
    public OpResult ToggleUnit(int id)
    {
        Require("products", "Edit");
        return RunOp(() =>
        {
            var u = Db.UnitsOfMeasure.FirstOrDefault(x => x.Id == id) ?? throw new DomainException("الوحدة غير موجودة.");
            u.IsActive = !u.IsActive;
            Db.SaveChanges();
            return OpResult.Success(u.IsActive
                ? $"تم تفعيل الوحدة «{u.UnitNameAr}» — تظهر في قواميس الوحدات بكل الشاشات."
                : $"تم إيقاف الوحدة «{u.UnitNameAr}» — الأصناف والنتائج التي تستخدمها محفوظة، وتختفي من القوائم المستقبلية فقط.");
        });
    }

    public OpResult SavePackaging(int? id, string code, string name, double unitWeight, int unitsPerPackage = 1,
        int molds = 0, double moldWeight = 0, bool active = true)
    {
        Require("products", "Edit");
        if (string.IsNullOrWhiteSpace(name)) return OpResult.Fail("أدخل اسم العبوة.");
        if (unitWeight <= 0) return OpResult.Fail("وزن العبوة يجب أن يكون أكبر من صفر.");
        var pk = id == null ? new PackagingType() : Db.PackagingTypes.FirstOrDefault(x => x.Id == id);
        if (pk == null) return OpResult.Fail("العبوة غير موجودة.");
        pk.PackageCode = string.IsNullOrWhiteSpace(code) ? ("PK-" + Guid.NewGuid().ToString("N")[..4].ToUpper()) : code.Trim();
        pk.PackageNameAr = name.Trim();
        pk.UnitWeightKg = unitWeight;
        pk.UnitsPerPackage = unitsPerPackage;
        pk.MoldsCount = molds;
        pk.MoldWeightKg = moldWeight;
        pk.IsActive = active;
        if (id == null) Db.PackagingTypes.Add(pk);
        Db.SaveChanges();
        return OpResult.Success("تم حفظ العبوة.", pk.Id);
    }

    // ═══════════════ §إدارة المستخدمين: توقيف/تفعيل، تصفير كلمة السر، فك القفل ═══════════════
    public OpResult ToggleUserActive(int userId)
    {
        Require("users", "Edit");
        var u = LoadUserFresh(userId);
        if (u == null) return OpResult.Fail("المستخدم غير موجود.");
        if (Session.UserId == userId) return OpResult.Fail("لا يمكنك توقيف حسابك الحالي.");

        // §إصلاح حرج: كان هذا المسار يقلب IsActive مباشرة فيتجاوز GuardLastPermissionAdmin،
        // والحارس لم يكن يُستدعى إلا من شاشة الصلاحيات — فأمكن قفل النظام كلياً من شاشة المستخدمين.
        if (u.IsActive)
        {
            try { new PermissionService(Db, Session).GuardLastPermissionAdmin(userId, null); }
            catch (DatesErp.Core.Exceptions.DomainException ex) { return OpResult.Fail(ex.Message); }
        }

        u.IsActive = !u.IsActive;
        AuditUser(u.IsActive ? "Activate" : "Deactivate", u.IsActive ? "Activate" : "Deactivate",
            "Users", u.UserName, u.Id);
        Db.SaveChanges();
        return OpResult.Success(u.IsActive ? "تم تفعيل المستخدم." : "تم توقيف المستخدم — لن يستطيع تسجيل الدخول.");
    }

    public OpResult UnlockUser(int userId)
    {
        Require("users", "Edit");
        var u = LoadUserFresh(userId);
        if (u == null) return OpResult.Fail("المستخدم غير موجود.");
        u.IsLocked = false;
        u.FailedLoginCount = 0;
        u.LockoutDate = null;
        AuditUser("UnlockUser", "UnlockUser", "Users", u.UserName, u.Id); // §R5 — كان بلا قيد تدقيق
        Db.SaveChanges();
        return OpResult.Success("تم فك قفل الحساب وتصفير محاولات الدخول.");
    }

    /// <summary>§كلمة السر تُخزن مجزأة ولا يمكن عرضها أبداً — المدير يصفّرها لجديدة.</summary>
    public OpResult ResetUserPassword(int userId, string newPlain)
    {
        Require("users", "Edit");
        var u = LoadUserFresh(userId);
        if (u == null) return OpResult.Fail("المستخدم غير موجود.");
        // §الكلمة المفقودة — الفراغ = توليد كلمة مؤقتة قوية تلقائياً تُعرض مرة واحدة في الرسالة؛
        // كلمة الموظف الأصلية لا يمكن معرفتها أبداً (تجزئة PBKDF2 غير عكوسة) — وهذا مقصود.
        bool generated = string.IsNullOrWhiteSpace(newPlain);
        if (generated) newPlain = GenerateTempPassword();
        int min = 8;
        var minSetting = Db.SystemSettings.AsNoTracking().FirstOrDefault(x => x.SettingKey == "PasswordMinLength");
        if (minSetting != null && int.TryParse(minSetting.SettingValue, out var parsedMin) && parsedMin > 0) min = parsedMin;
        var policyError = ValidatePasswordPolicy(newPlain, min);
        if (policyError != null) return OpResult.Fail(policyError);
        if (PasswordHistoryContains(u.PasswordHistory, newPlain))
            return OpResult.Fail("لا يجوز إعادة استخدام كلمات المرور السابقة لهذا الحساب (§سجل آخر 5 كلمات).");
        var (hash, salt) = DatesErp.Infrastructure.Security.PasswordHasher.Hash(newPlain);
        u.PasswordHistory = PushPasswordHistory(u.PasswordHistory, u.PasswordHash, u.PasswordSalt); // §R4
        u.PasswordHash = hash;
        u.PasswordSalt = salt;
        u.MustChangePassword = true; // يُجبر على تغييرها عند أول دخول
        u.IsLocked = false;
        u.FailedLoginCount = 0;
        u.LockoutDate = null;
        u.PasswordChangedDate = DateTime.Now;
        AuditUser("ResetPassword", "ResetPassword", "Users", u.UserName, u.Id);
        Db.SaveChanges();
        return OpResult.Success(generated
            ? $"تم تصفير كلمة السر. الكلمة المؤقتة: {newPlain}\nأعطها للموظف — سيُجبر على تغييرها بنفسه عند أول دخول، ولن يستطيع أحد معرفتها بعده."
            : "تم تصفير كلمة السر — يُجبر المستخدم على تغييرها عند أول دخول.");
    }

    /// <summary>§الكلمة المفقودة — كلمة مؤقتة مولَّدة عشوائياً (حروف+أرقام، 12 رمزاً) تجتاز السياسة دائماً.</summary>
    public static string GenerateTempPassword()
    {
        const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz";
        const string digits = "23456789";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(10);
        var body = new string(bytes.Select(b => (b % 2 == 0 ? letters : digits)[b % (b % 2 == 0 ? letters.Length : digits.Length)]).ToArray());
        return "Dt" + body;
    }

    /// <summary>§R4 — سجل آخر 5 كلمات (تجزئة|ملح): فحص وإعادة إدراج.</summary>
    private static bool PasswordHistoryContains(string history, string plain)
    {
        if (string.IsNullOrEmpty(history) || string.IsNullOrEmpty(plain)) return false;
        foreach (var entry in history.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('|');
            if (parts.Length == 2 && DatesErp.Infrastructure.Security.PasswordHasher.Verify(plain, parts[0], parts[1])) return true;
        }
        return false;
    }
    private static string PushPasswordHistory(string history, string hash, string salt)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt)) return history;
        var list = (history ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        list.Insert(0, $"{hash}|{salt}");
        return string.Join(";", list.Take(5));
    }

    // ═══════════ §إصلاح حرج: تغيير كلمة المرور ═══════════
    //
    // قبل الإصلاح لم تكن توجد أي آلية لتغيير كلمة المرور في النظام كله:
    // MustChangePassword يُرفع عند البذر وعند التصفير، ويُعاد في نتيجة الدخول،
    // لكن لا LoginWindow ولا MainWindow ولا أي خدمة تتيح تغييرها — فالمستخدم
    // يبقى موسوماً «يجب تغيير كلمة المرور» إلى الأبد ولا سبيل لإرضاء الشرط.

    /// <summary>
    /// §سياسة كلمة المرور (القرار #47): طول أدنى + تعقيد + منع التكرار.
    /// الطول الأدنى قابل للضبط من SystemSettings (PasswordMinLength).
    /// </summary>
    public static string ValidatePasswordPolicy(string plain, int minLength = 8)
    {
        if (string.IsNullOrWhiteSpace(plain)) return "أدخل كلمة المرور.";
        if (plain.Length < minLength)
            return $"كلمة المرور يجب ألا تقل عن {minLength} رموز.";
        bool hasLetter = plain.Any(char.IsLetter);
        bool hasDigit = plain.Any(char.IsDigit);
        if (!hasLetter || !hasDigit)
            return "كلمة المرور يجب أن تحتوي على حروف وأرقام معاً.";
        if (plain.Equals("Admin@123", StringComparison.Ordinal))
            return "لا يجوز استخدام كلمة المرور الافتراضية للنظام.";
        return null;
    }

    /// <summary>تغيير المستخدم لكلمة مروره بنفسه — يتحقق من القديمة ويُرضي MustChangePassword.</summary>
    public OpResult ChangePassword(int userId, string oldPlain, string newPlain, string confirmPlain)
    {
        if (userId <= 0) return OpResult.Fail("لا توجد جلسة مستخدم.");
        if (newPlain != confirmPlain) return OpResult.Fail("كلمتا المرور الجديدتان غير متطابقتين.");

        var u = LoadUserFresh(userId);
        if (u == null) return OpResult.Fail("المستخدم غير موجود.");
        if (!DatesErp.Infrastructure.Security.PasswordHasher.Verify(oldPlain ?? "", u.PasswordHash, u.PasswordSalt))
        {
            u.FailedLoginCount++;
            // §بدون LockoutDate لا يعمل فك القفل التلقائي — فالقفل يصبح دائماً
            if (u.FailedLoginCount >= 5) { u.IsLocked = true; u.LockoutDate = DateTime.Now; }
            AuditUser("ChangePasswordFailed", "ChangePasswordFailed", "Users", u.UserName, u.Id);
            Db.SaveChanges();
            return OpResult.Fail(u.IsLocked
                ? "كلمة المرور الحالية غير صحيحة — قُفل الحساب بعد 5 محاولات."
                : "كلمة المرور الحالية غير صحيحة.");
        }

        int min = 8;
        var setting = Db.SystemSettings.AsNoTracking().FirstOrDefault(s => s.SettingKey == "PasswordMinLength");
        if (setting != null && int.TryParse(setting.SettingValue, out var parsed) && parsed > 0) min = parsed;

        var policyError = ValidatePasswordPolicy(newPlain, min);
        if (policyError != null) return OpResult.Fail(policyError);

        if (DatesErp.Infrastructure.Security.PasswordHasher.Verify(newPlain, u.PasswordHash, u.PasswordSalt))
            return OpResult.Fail("كلمة المرور الجديدة يجب أن تختلف عن الحالية.");
        if (PasswordHistoryContains(u.PasswordHistory, newPlain))
            return OpResult.Fail("لا يجوز إعادة استخدام كلمات المرور السابقة (§سجل آخر 5 كلمات).");

        var (hash, salt) = DatesErp.Infrastructure.Security.PasswordHasher.Hash(newPlain);
        u.PasswordHistory = PushPasswordHistory(u.PasswordHistory, u.PasswordHash, u.PasswordSalt); // §R4
        u.PasswordHash = hash;
        u.PasswordSalt = salt;
        u.MustChangePassword = false;   // ← الشرط أُرضي أخيراً
        u.PasswordChangedDate = DateTime.Now;
        u.FailedLoginCount = 0;
        u.IsLocked = false;
        AuditUser("ChangePassword", "ChangePassword", "Users", u.UserName, u.Id);
        Db.SaveChanges();
        return OpResult.Success("تم تغيير كلمة المرور بنجاح.");
    }
}
