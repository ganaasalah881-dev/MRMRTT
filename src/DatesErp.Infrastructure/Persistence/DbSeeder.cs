using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Infrastructure.Persistence;

/// <summary>
/// زرع البيانات الأساسية المرجعية (تُستخدم في الاختبارات وعند إنشاء قاعدة جديدة).
/// §10 مصفوفة الصلاحيات المركزية، مستخدمون بكلمات مرور مجزأة مع فرض التغيير عند أول دخول.
/// </summary>
public static class DbSeeder
{
    public const string InitialAdminPassword = "Admin@123"; // كلمة المرور الأولية — يُجبر النظام على تغييرها فور أول دخول

    public static void Seed(DatesErpDbContext db)
    {
        if (db.SystemSettings.Any(s => s.SettingKey == "Seeded")) return;

        // ── الشركة ──
        db.CompanyInfos.Add(new CompanyInfo
        {
            CompanyNameAr = "الشركة اليمنية لتعبئة وتصنيع التمور",
            CompanyNameEn = "Yemen Dates Co.",
            Address = "تعز - اليمن",
            ReportFooterNote = "نظام Date ERP — جميع الحقوق محفوظة"
        });

        // ── المجموعات والأصناف — §نظام الوحدات المعتمد: 001 خام | 002 تام | 003 مخرجات ثانوية ──
        db.ItemGroups.AddRange(
            new ItemGroup { GroupCode = "001", GroupNameAr = "المواد الخام", GroupType = "Raw", DefaultUnit = "كجم" },
            new ItemGroup { GroupCode = "002", GroupNameAr = "المنتجات التامة", GroupType = "Finished", DefaultUnit = "كرتون" },
            new ItemGroup { GroupCode = "003", GroupNameAr = "المخرجات الثانوية", GroupType = "ByProduct", DefaultUnit = "كجم" },
            new ItemGroup { GroupCode = "004", GroupNameAr = "الكرتون والتغليف المرتجع", GroupType = "Pack", DefaultUnit = "كرتون" });

        db.Products.AddRange(
            new Product { ProductCode = "001-001", ProductNameAr = "تمر خام - خلاص", GroupCode = "001", ItemType = "Raw", UnitOfMeasure = "كجم", CartonWeightKg = 20 },
            new Product { ProductCode = "001-002", ProductNameAr = "تمر خام - سكري", GroupCode = "001", ItemType = "Raw", UnitOfMeasure = "كجم", CartonWeightKg = 20 },
            // §تتبع الصنف: التعريف الرسمي للتحويل — كل منتج تام يعرف الخام الذي يُنتج منه
            new Product { ProductCode = "002-001", ProductNameAr = "خلاص ممتاز 500جم", GroupCode = "002", ItemType = "Finished", UnitOfMeasure = "كرتون", TradingUnit = "كرتون", CartonWeightKg = 7.5, MoldsCount = 1, MoldWeightKg = 0.5, HourlyProductionRate = 500, SourceProductId = 1 },
            new Product { ProductCode = "002-002", ProductNameAr = "سكري فاخر 1كجم", GroupCode = "002", ItemType = "Finished", UnitOfMeasure = "كرتون", TradingUnit = "كرتون", CartonWeightKg = 2, MoldsCount = 4, MoldWeightKg = 0.5, HourlyProductionRate = 1000, SourceProductId = 2 },
            // §المخرجات الثانوية (003) — وحدتها كجم دائماً
            new Product { ProductCode = "003-001", ProductNameAr = "حشف", GroupCode = "003", ItemType = "ByProduct", UnitOfMeasure = "كجم" },
            new Product { ProductCode = "003-002", ProductNameAr = "نوى", GroupCode = "003", ItemType = "ByProduct", UnitOfMeasure = "كجم" },
            new Product { ProductCode = "004-001", ProductNameAr = "كرتون فارغ (مستعمل)", GroupCode = "004", ItemType = "Pack", UnitOfMeasure = "كرتون", CartonWeightKg = 1 },
            new Product { ProductCode = "004-002", ProductNameAr = "سلة فارغة (مستعملة)", GroupCode = "004", ItemType = "Pack", UnitOfMeasure = "سلة", CartonWeightKg = 1 });

        db.PackagingTypes.AddRange(
            new PackagingType { PackageCode = "CT5", PackageNameAr = "كرتون 5 كجم", UnitWeightKg = 5, UnitsPerPackage = 1, MoldsCount = 5, MoldWeightKg = 1.0 },
            new PackagingType { PackageCode = "CT10", PackageNameAr = "كرتون 10 كجم", UnitWeightKg = 10, UnitsPerPackage = 1, MoldsCount = 10, MoldWeightKg = 1.0 },
            new PackagingType { PackageCode = "BK20", PackageNameAr = "سلة 20 كجم", UnitWeightKg = 20, UnitsPerPackage = 1 });

        db.UnitsOfMeasure.AddRange(
            new UnitOfMeasure { UnitCode = "KG", UnitNameAr = "كجم" },
            new UnitOfMeasure { UnitCode = "CTN", UnitNameAr = "كرتون" },
            new UnitOfMeasure { UnitCode = "PCS", UnitNameAr = "حبة" },
            new UnitOfMeasure { UnitCode = "PC", UnitNameAr = "قطعة" },
            new UnitOfMeasure { UnitCode = "ROLL", UnitNameAr = "لفة" },
            new UnitOfMeasure { UnitCode = "RUL", UnitNameAr = "رول" },
            new UnitOfMeasure { UnitCode = "LTR", UnitNameAr = "لتر" },
            new UnitOfMeasure { UnitCode = "BSK", UnitNameAr = "سلة" });
        db.AuxGroups.AddRange(
            new AuxGroup { GroupCode = "AG-CART", GroupNameAr = "كراتين العملاء" },
            new AuxGroup { GroupCode = "AG-PACK", GroupNameAr = "مواد التغليف" },
            new AuxGroup { GroupCode = "AG-FUEL", GroupNameAr = "الوقود والتشغيل" });
        db.AuxiliaryMaterials.AddRange(
            new AuxiliaryMaterial { MaterialCode = "AUX-CARTON", MaterialNameAr = "كرتون فارغ", MaterialCategory = "تعبئة", GroupCode = "AG-CART", UnitOfMeasure = "قطعة" },
            new AuxiliaryMaterial { MaterialCode = "AUX-LABEL", MaterialNameAr = "ملصقات", MaterialCategory = "تغليف", GroupCode = "AG-PACK", UnitOfMeasure = "قطعة" },
            new AuxiliaryMaterial { MaterialCode = "AUX-TAPE", MaterialNameAr = "شريط لاصق", MaterialCategory = "تغليف", GroupCode = "AG-PACK", UnitOfMeasure = "رول" },
            new AuxiliaryMaterial { MaterialCode = "AUX-DIESEL", MaterialNameAr = "ديزل المولد", MaterialCategory = "وقود", GroupCode = "AG-FUEL", UnitOfMeasure = "لتر" });

        // معادلات الاستهلاك: لكل كرتون منتج = كرتون فارغ + ملصق
        var p1 = 3; // أول صنف تام
        db.ConsumptionFormulas.AddRange(
            new ConsumptionFormula { ProductId = p1, MaterialId = 1, QtyPerUnit = 1, UnitOfMeasure = "قطعة", Mode = "PerCarton" },
            new ConsumptionFormula { ProductId = p1, MaterialId = 2, QtyPerUnit = 1, UnitOfMeasure = "قطعة", Mode = "PerCarton" },
            // §الديزل فعلي لا مفروض: يُدخل عند الإقفال ولا يُشتق من ساعات الخطة
            new ConsumptionFormula { ProductId = p1, MaterialId = 4, QtyPerUnit = 0, UnitOfMeasure = "لتر", Mode = "Actual", IsOptional = true });

        // ── المخازن ──
        db.Warehouses.AddRange(
            new Warehouse { WarehouseCode = "WRM", WarehouseNameAr = "مخزن المواد الخام", WarehouseType = "Raw" },
            new Warehouse { WarehouseCode = "WFG", WarehouseNameAr = "مخزن الإنتاج التام", WarehouseType = "Finished" },
            new Warehouse { WarehouseCode = "WAUX", WarehouseNameAr = "مخزن المواد المساعدة", WarehouseType = "Auxiliary" });

        // ── الورديات والطاقات ──
        db.Shifts.AddRange(
            new Shift { ShiftCode = "M", ShiftNameAr = "الوردية الصباحية", StartTime = "06:00", EndTime = "14:00", TotalHours = 8, EffectiveProductiveHours = 8 },
            new Shift { ShiftCode = "E", ShiftNameAr = "الوردية المسائية", StartTime = "14:00", EndTime = "22:00", TotalHours = 8, EffectiveProductiveHours = 6 },
            new Shift { ShiftCode = "N", ShiftNameAr = "الوردية الليلية", StartTime = "22:00", EndTime = "06:00", TotalHours = 8, EffectiveProductiveHours = 7 });

        db.ProductShiftCapacities.AddRange(
            // الصنف A: الأولى 4000 (500/س) | الثانية 3000 (500/س)
            new ProductShiftCapacity { ProductId = 3, ShiftId = 1, HourlyProductionRate = 500, ShiftCapacity = 4000 },
            new ProductShiftCapacity { ProductId = 3, ShiftId = 2, HourlyProductionRate = 500, ShiftCapacity = 3000 },
            new ProductShiftCapacity { ProductId = 3, ShiftId = 3, HourlyProductionRate = 500, ShiftCapacity = 2500 },
            // الصنف B: الأولى 8000 (1000/س) | الثانية 6500 (1083.3/س)
            new ProductShiftCapacity { ProductId = 4, ShiftId = 1, HourlyProductionRate = 1000, ShiftCapacity = 8000 },
            new ProductShiftCapacity { ProductId = 4, ShiftId = 2, HourlyProductionRate = 1083.3, ShiftCapacity = 6500 },
            new ProductShiftCapacity { ProductId = 4, ShiftId = 3, HourlyProductionRate = 900, ShiftCapacity = 6300 });

        db.ProductionLines.Add(new ProductionLine { LineCode = "L1", LineNameAr = "خط الإنتاج الأول", CapacityPerShift = 5000 });
        if (!db.Warehouses.Any(w => w.WarehouseCode == "WPK"))
            db.Warehouses.Add(new Warehouse { WarehouseCode = "WPK", WarehouseNameAr = "مخزن الكرتون والتغليف", WarehouseType = "Pack" });
        // §المعالجة والتعقيم — مستودع مستقل يفصل ما تحت المعالجة عن الخام المتاح.
        // بنمط «إن لم يوجد» كسابقه: البذر يمر على قواعد قائمة فلا يكرر الصف.
        if (!db.Warehouses.Any(w => w.WarehouseCode == "WTRT"))
            db.Warehouses.Add(new Warehouse { WarehouseCode = "WTRT", WarehouseNameAr = "مستودع المعالجة والتعقيم", WarehouseType = "Treatment" });
        if (!db.TreatmentTypes.Any())
            db.TreatmentTypes.AddRange(
                new TreatmentType { TypeCode = "TRT-HEAT", TypeNameAr = "تعقيم حراري", DefaultDurationHours = 6, RequiresQualityCheck = true },
                new TreatmentType { TypeCode = "TRT-FRZ", TypeNameAr = "تجميد", DefaultDurationHours = 168, RequiresQualityCheck = false },
                new TreatmentType { TypeCode = "TRT-FUM", TypeNameAr = "تبخير", DefaultDurationHours = 72, RequiresQualityCheck = true });

        // §B106 — المدة تتبع **درجة الإصابة** لا تقنية المعالجة (قرار المستخدم):
        //   خفيفة 5 أيام · متوسطة 7 أيام · شديدة 10 أيام.
        // تُبذر صفاً صفاً بنمط «إن لم يوجد» كي تصل القواعدَ القائمة أيضاً — لا داخل
        // شرط !Any() أعلاه، فذاك لا يمر إلا على قاعدة جديدة تماماً.
        foreach (var (code, name, hours) in new[]
                 {
                     ("TRT-INF-L", "إصابة خفيفة — 5 أيام",  120d),
                     ("TRT-INF-M", "إصابة متوسطة — 7 أيام", 168d),
                     ("TRT-INF-H", "إصابة شديدة — 10 أيام", 240d),
                 })
        {
            if (!db.TreatmentTypes.Any(t => t.TypeCode == code))
                db.TreatmentTypes.Add(new TreatmentType
                {
                    TypeCode = code,
                    TypeNameAr = name,
                    DefaultDurationHours = hours,
                    RequiresQualityCheck = true   // الإفراج بعد الإصابة يستوجب فحصاً معتمداً
                });
        }

        var bk = db.PackagingTypes.FirstOrDefault(x => x.PackageCode == "BK20");
        var basketEmpty = db.Products.FirstOrDefault(x => x.ProductCode == "004-002");
        if (bk != null && basketEmpty != null) basketEmpty.SourcePackagingTypeId = bk.Id;

        db.ByProducts.AddRange(
            new ByProduct { ByProductCode = "BP-HASHF", ByProductNameAr = "حشف", UnitOfMeasure = "كجم" },
            new ByProduct { ByProductCode = "BP-NAWA", ByProductNameAr = "نوى", UnitOfMeasure = "كجم" },
            // §قاعدة المصنع (B48): العجينة مخرج ثانوي — والمنسم ليس مخرجاً ثانوياً بل منتج تام.
            new ByProduct { ByProductCode = "BP-3AJEENAH", ByProductNameAr = "عجينة", UnitOfMeasure = "كجم" });

        // §لا ثوابت في الشاشات: معايير الفحص الافتراضية تُبذر مرة واحدة ثم تُدار من إعدادات الأصناف
        db.QualityStandards.AddRange(
            new QualityStandard { Code = "MOIST", NameAr = "نسبة الرطوبة", UnitLabel = "%", MinValue = 14, MaxValue = 18, DefaultValue = 16.5, SortNo = 1 },
            new QualityStandard { Code = "BRIX", NameAr = "تركيز السكريات", UnitLabel = "°", MinValue = 65, DefaultValue = 68.5, SortNo = 2 },
            new QualityStandard { Code = "SKIN", NameAr = "نسبة انفصال القشرة", UnitLabel = "%", MaxValue = 5, DefaultValue = 2, SortNo = 3 },
            new QualityStandard { Code = "IMP", NameAr = "نسبة الشوائب والأتربة", UnitLabel = "%", MaxValue = 1, DefaultValue = 0.3, SortNo = 4 });

        // §الفحص الديناميكي: أنواع نتائج الفحص تُبذر مرة واحدة ثم تُدار من «إعدادات الأصناف».
        // هذه نقطة بداية قابلة للتعديل والحذف — ليست ثوابت في الكود: الشاشة تقرأ الجدول وحده.
        // §لا تحويلات وحدات مبذورة: وزن الكرتون يختلف بين الأصناف، فالتحويل يُعرّفه المستخدم.
        if (!db.InspectionResultTypes.Any())
        {
            int? uKg = db.UnitsOfMeasure.Where(u => u.UnitNameAr == "كجم").Select(u => (int?)u.Id).FirstOrDefault();
            int? uCtn = db.UnitsOfMeasure.Where(u => u.UnitNameAr == "كرتون").Select(u => (int?)u.Id).FirstOrDefault();
            db.InspectionResultTypes.AddRange(
                new InspectionResultType { Code = "RT-OK", NameAr = "خرج تام مطابق", ResultKind = InspectionResultType.KindAccepted, UnitId = uCtn, UnitLabel = "كرتون", IsFinishedGood = true, EntersInventory = true, SortNo = 1 },
                new InspectionResultType { Code = "RT-REJ", NameAr = "غير مطابق", ResultKind = InspectionResultType.KindRejected, UnitId = uCtn, UnitLabel = "كرتون", EntersInventory = false, SortNo = 2 },
                new InspectionResultType { Code = "RT-BRK", NameAr = "مكسور", ResultKind = InspectionResultType.KindByProduct, UnitId = uKg, UnitLabel = "كجم", IsByProduct = true, EntersInventory = true, SortNo = 3 },
                new InspectionResultType { Code = "RT-HASHF", NameAr = "حشف", ResultKind = InspectionResultType.KindByProduct, UnitId = uKg, UnitLabel = "كجم", IsByProduct = true, EntersInventory = true, SortNo = 4 },
                new InspectionResultType { Code = "RT-NAWA", NameAr = "نوى", ResultKind = InspectionResultType.KindByProduct, UnitId = uKg, UnitLabel = "كجم", IsByProduct = true, EntersInventory = true, SortNo = 5 },
                // §قاعدة المصنع: التمر السليم والتمر المنسم كلاهما منتج تام (002) قابل للبيع
                // والتسليم — والمنسم ليس مخرجاً ثانوياً، فالفرق اسم/تصنيف تجاري فقط.
                new InspectionResultType { Code = "RT-SALEEM", NameAr = "تمر سليم", ResultKind = InspectionResultType.KindAccepted, UnitId = uCtn, UnitLabel = "كرتون", IsFinishedGood = true, EntersInventory = true, SortNo = 1 },
                new InspectionResultType { Code = "RT-MONSAM", NameAr = "تمر منسم", ResultKind = InspectionResultType.KindAccepted, UnitId = uCtn, UnitLabel = "كرتون", IsFinishedGood = true, EntersInventory = true, SortNo = 2 },
                new InspectionResultType { Code = "RT-3AJEENAH", NameAr = "عجينة", ResultKind = InspectionResultType.KindByProduct, UnitId = uKg, UnitLabel = "كجم", IsByProduct = true, EntersInventory = true, SortNo = 6 },
                new InspectionResultType { Code = "RT-LOSS", NameAr = "فاقد", ResultKind = InspectionResultType.KindLoss, UnitId = uKg, UnitLabel = "كجم", CountsAsLoss = true, EntersInventory = false, SortNo = 7 });
        }

        // ── عملاء وموردون ──
        db.Customers.Add(new Customer { CustomerCode = "C001", CustomerName = "مصنع التمور الحديث", CustomerType = "تجار جملة", Phone = "777000000" });
        db.Suppliers.Add(new Supplier { SupplierCode = "S001", SupplierName = "مورد التمور الرئيسي", Phone = "733000000" });
        db.Employees.AddRange(
            new Employee { EmployeeCode = "EMP1", FullName = "مدير النظام", JobTitle = "IT" },
            new Employee { EmployeeCode = "EMP2", FullName = "مدير الإنتاج", JobTitle = "إنتاج" },
            new Employee { EmployeeCode = "EMP3", FullName = "أمين المخزن", JobTitle = "مخازن" },
            new Employee { EmployeeCode = "EMP4", FullName = "مسؤول الجودة", JobTitle = "جودة" });

        // ── الأدوار والصلاحيات (§10) ──
        var roles = SystemRoles.All.Select((r, i) => new Role
        {
            RoleCode = r.Code,
            RoleNameAr = r.Arabic,
            RoleNameEn = r.Code,
            IsSystem = true
        }).ToList();
        db.Roles.AddRange(roles);
        db.SaveChanges();

        int Mask(params PermissionFlags[] flags) => flags.Sum(f => (int)f);
        var full = (int)PermissionFlags.All;
        var viewOnly = (int)PermissionFlags.View;
        var viewPrintExport = Mask(PermissionFlags.View, PermissionFlags.Print, PermissionFlags.Export);

        // §الإصلاح الأمني: كانت القائمة مكتوبة يدوياً هنا وتسقط منها products/cartons/employees،
        // فلا يُزرع لها أي صف صلاحية ⟵ تُفتح شاشاتها بلا فحص. الآن من المصدر الواحد في Core.
        string[] modules = PermissionModules.Codes;
        foreach (var role in roles)
        {
            foreach (var m in modules)
            {
                int mask = role.RoleCode switch
                {
                    SystemRoles.Administrator => full,
                    SystemRoles.Management => full,
                    // §الإصلاح الأمني: الوحدات المستجدة (cartons/products/employees) وُزّعت على الأدوار
                    // بمنطق عملها الفعلي في الخادم — لا تُترك viewOnly لمن يشغّلها فيُقفل عليه عمله.
                    SystemRoles.Warehouse => (m is "receiving" or "lots" or "inventory" or "materials" or "cartons")
                        ? full : (m is "finishedgoods" or "delivery") ? Mask(PermissionFlags.View, PermissionFlags.Create, PermissionFlags.Edit, PermissionFlags.Approve, PermissionFlags.Post, PermissionFlags.Print) : viewOnly,
                    SystemRoles.Production => (m is "planning" or "production" or "execution" or "materials")
                        ? full : (m is "products") ? Mask(PermissionFlags.View, PermissionFlags.Edit, PermissionFlags.Print) : viewOnly,
                    // الجودة تعرّف معايير الفحص وأنواع النتائج على الأصناف (InspectionService ⟵ products/Edit)
                    SystemRoles.Quality => m == "quality" ? full
                        : (m is "products") ? Mask(PermissionFlags.View, PermissionFlags.Edit, PermissionFlags.Print) : viewOnly,
                    SystemRoles.Sales => (m is "customers" or "delivery") ? full : viewPrintExport,
                    SystemRoles.Finance => (m is "reports" or "customers") ? viewPrintExport : viewOnly,
                    _ => viewOnly
                };
                db.RolePermissions.Add(new RolePermission { RoleId = role.Id, ModuleCode = m, PermissionMask = mask });
            }
        }

        // ── المستخدمون (كلمات مرور مجزأة + فرض التغيير عند أول دخول) ──
        var (h1, s1) = PasswordHasher.Hash(InitialAdminPassword);
        db.Users.AddRange(
            new AppUser { UserCode = "U001", UserName = "admin", FullName = "مدير النظام", PasswordHash = h1, PasswordSalt = s1, EmployeeId = 1, MustChangePassword = true },
            new AppUser { UserCode = "U002", UserName = "production", FullName = "مدير الإنتاج", PasswordHash = h1, PasswordSalt = s1, EmployeeId = 2, MustChangePassword = true },
            new AppUser { UserCode = "U003", UserName = "warehouse", FullName = "أمين المخزن", PasswordHash = h1, PasswordSalt = s1, EmployeeId = 3, MustChangePassword = true },
            new AppUser { UserCode = "U004", UserName = "quality", FullName = "مسؤول الجودة", PasswordHash = h1, PasswordSalt = s1, EmployeeId = 4, MustChangePassword = true });
        db.SaveChanges();

        var adminRole = db.Roles.Single(r => r.RoleCode == SystemRoles.Administrator);
        var prodRole = db.Roles.Single(r => r.RoleCode == SystemRoles.Production);
        var whRole = db.Roles.Single(r => r.RoleCode == SystemRoles.Warehouse);
        var qcRole = db.Roles.Single(r => r.RoleCode == SystemRoles.Quality);
        var users = db.Users.OrderBy(u => u.Id).ToList();
        db.UserRoles.AddRange(
            new UserRole { UserId = users[0].Id, RoleId = adminRole.Id },
            new UserRole { UserId = users[1].Id, RoleId = prodRole.Id },
            new UserRole { UserId = users[2].Id, RoleId = whRole.Id },
            new UserRole { UserId = users[3].Id, RoleId = qcRole.Id });

        // ── الترقيم ──
        db.NumberingSchemes.AddRange(
            new NumberingScheme { SchemeCode = "SHIP", SchemeName = "استلام", Prefix = "REC", LastSequence = 0 },
            new NumberingScheme { SchemeCode = "PLAN", SchemeName = "خطة", Prefix = "PLN", LastSequence = 0 },
            new NumberingScheme { SchemeCode = "ORD", SchemeName = "أمر إنتاج", Prefix = "PRD", LastSequence = 0 },
            new NumberingScheme { SchemeCode = "EXE", SchemeName = "تنفيذ", Prefix = "EXE", LastSequence = 0 },
            new NumberingScheme { SchemeCode = "QC", SchemeName = "جودة", Prefix = "QC", LastSequence = 0 },
            new NumberingScheme { SchemeCode = "FGR", SchemeName = "استلام تام", Prefix = "DLV", LastSequence = 0 },
            new NumberingScheme { SchemeCode = "RCV", SchemeName = "سند استلام", Prefix = "RCV", LastSequence = 0 },
            new NumberingScheme { SchemeCode = "CD", SchemeName = "تسليم عميل", Prefix = "CD", LastSequence = 0 },
            new NumberingScheme { SchemeCode = "TXN", SchemeName = "حركة مخزون", Prefix = "INV", LastSequence = 0 },
            new NumberingScheme { SchemeCode = "PCL", SchemeName = "إقفال خطة", Prefix = "PCL", LastSequence = 0 },
            new NumberingScheme { SchemeCode = "LOT", SchemeName = "دفعة خام", Prefix = "LOT", LastSequence = 0 },
            new NumberingScheme { SchemeCode = "TASK", SchemeName = "مهمة سير عمل", Prefix = "TSK", LastSequence = 0 },
            new NumberingScheme { SchemeCode = "TRT", SchemeName = "معالجة وتعقيم", Prefix = "TRT", LastSequence = 0 });   // §B103: كان مفقوداً — التشخيص يلتقطه والقواعد القائمة يعالجها الإصلاح الذاتي في NumberingService

        // ── إصدار قاعدة البيانات (§31) ──
        db.DbVersions.Add(new DbVersion { VersionNumber = "1.0.0", Description = "الإصدار الأولي — المخطط الكامل" });
        db.SystemSettings.Add(new SystemSetting { SettingKey = "Workflow_FourEyes", SettingValue = "Off" }); // §B102 (سحب B97): مبدأ أربع عيون اختياري — اضبطه Strict للفصل بين المنشئ والمعتمد
        db.SystemSettings.Add(new SystemSetting { SettingKey = "Seeded", SettingValue = DateTime.Now.ToString("s") });
        db.SaveChanges();
    }

    /// <summary>هدف ترقية البيانات المرجعية الحالي — يُخزَّن في SystemSettings لمنع التكرار.</summary>
    public const string RefDataUpgradeTarget = "B111";   // §B111: امسح سجل كلمات المرور التاريخية أيضاً، حتى في القواعد التي رُقِّيت إلى B110

    /// <summary>
    /// §ترقية البيانات المرجعية على القواعد القائمة — تُشغَّل عند الإقلاع بعد ترحيل المخطط.
    ///
    /// سبب وجودها: <see cref="Seed"/> تتوقف من أول سطر إن وُجد مفتاح "Seeded"، وأنواع نتائج
    /// الفحص تُبذر فقط إن كان الجدول فارغاً. فأي قاعدة أُنشئت قبل «قاعدة المصنع» (B48) تبقى على
    /// التصنيف القديم: المنسم مخرج ثانوي، ولا «تمر سليم» ولا «عجينة» — ونسخ ملفات النظام الجديدة
    /// وحده لا يغيّر شيئاً داخل القاعدة القائمة.
    ///
    /// حدودها (احتراماً لقاعدة «لا ثوابت في الشاشات»):
    ///   • تضيف الصفوف الناقصة فقط — لا تحذف صفاً عرّفه المستخدم ولا تغيّر اسماً عدّله.
    ///   • تعيد تصنيف «المنسم» فقط إن بقي على قيمته المبذورة القديمة (أي لم يلمسه المستخدم).
    ///   • لا تفعل شيئاً في جدول أفرغه المستخدم عمداً.
    ///   • تُنفَّذ مرة واحدة لكل هدف (علامة RefDataUpgrade).
    /// </summary>
    public static List<string> UpgradeReferenceData(DatesErpDbContext db)
    {
        const string MarkerKey = "RefDataUpgrade";
        var changes = new List<string>();

        var marker = db.SystemSettings.FirstOrDefault(s => s.SettingKey == MarkerKey);
        if (marker != null && marker.SettingValue == RefDataUpgradeTarget) return changes;

        int? UnitId(string unitNameAr) => db.UnitsOfMeasure
            .Where(u => u.UnitNameAr == unitNameAr).Select(u => (int?)u.Id).FirstOrDefault();

        // ── أنواع نتائج الفحص: السليم والمنسم كلاهما منتج تام، والعجينة مخرج ثانوي ──
        var types = db.InspectionResultTypes.ToList();
        if (types.Count > 0)
        {
            int? uKg = UnitId("كجم");
            int? uCtn = UnitId("كرتون");
            int nextSort = types.Max(t => t.SortNo) + 1;

            var monsam = types.FirstOrDefault(t => t.Code == "RT-MONSAM");
            if (monsam != null && monsam.IsByProduct && !monsam.IsFinishedGood
                && monsam.ResultKind == InspectionResultType.KindByProduct)
            {
                monsam.ResultKind = InspectionResultType.KindAccepted;
                monsam.IsByProduct = false;
                monsam.IsFinishedGood = true;
                monsam.EntersInventory = true;
                monsam.UnitId = uCtn;
                monsam.UnitLabel = "كرتون";
                changes.Add($"أُعيد تصنيف «{monsam.NameAr}» إلى منتج تام (كرتون) بدل مخرج ثانوي.");
            }

            if (types.All(t => t.Code != "RT-SALEEM"))
            {
                db.InspectionResultTypes.Add(new InspectionResultType
                {
                    Code = "RT-SALEEM", NameAr = "تمر سليم", ResultKind = InspectionResultType.KindAccepted,
                    UnitId = uCtn, UnitLabel = "كرتون", IsFinishedGood = true, EntersInventory = true, SortNo = nextSort++
                });
                changes.Add("أُضيف نوع نتيجة الفحص «تمر سليم» كمنتج تام (كرتون).");
            }

            if (types.All(t => t.Code != "RT-3AJEENAH"))
            {
                db.InspectionResultTypes.Add(new InspectionResultType
                {
                    Code = "RT-3AJEENAH", NameAr = "عجينة", ResultKind = InspectionResultType.KindByProduct,
                    UnitId = uKg, UnitLabel = "كجم", IsByProduct = true, EntersInventory = true, SortNo = nextSort++
                });
                changes.Add("أُضيف نوع نتيجة الفحص «عجينة» كمخرج ثانوي (كجم).");
            }
        }

        // ── بطاقة الأصناف الثانوية: شاشة الإقفال والتقارير تقرأ هذه البطاقة وحدها ──
        var byDefs = db.ByProducts.ToList();
        if (byDefs.Count > 0 && byDefs.All(b => b.ByProductCode != "BP-3AJEENAH" && b.ByProductNameAr != "عجينة"))
        {
            db.ByProducts.Add(new ByProduct { ByProductCode = "BP-3AJEENAH", ByProductNameAr = "عجينة", UnitOfMeasure = "كجم" });
            changes.Add("أُضيف «عجينة» إلى بطاقة الأصناف الثانوية.");
        }

        // ── §B104 — ضمان مرجعي idempotent: المخازن ومخططات الترقيم تُستكمل في أي قاعدة ──
        // (الفحص الذاتي على قاعدة المستخدم كشف الفجوة: قاعدة أُنشئت قبل ميزة المعالجة بلا WTRT
        //  ولا ترقيم TASK/TRT — الباذر يغطي القواعد الجديدة فقط، وهذه الخطوة تغطي القائمة.)
        foreach (var wh in new[]
        {
            ("WRM", "مخزن المواد الخام", "Raw"),
            ("WFG", "مخزن الإنتاج التام", "Finished"),
            ("WAUX", "مخزن المواد المساعدة", "Auxiliary"),
            ("WPK", "مخزن الكرتون والتغليف", "Pack"),
            ("WTRT", "مستودع المعالجة والتعقيم", "Treatment"),
        })
        {
            if (!db.Warehouses.Any(w => w.WarehouseCode == wh.Item1))
            {
                db.Warehouses.Add(new Warehouse { WarehouseCode = wh.Item1, WarehouseNameAr = wh.Item2, WarehouseType = wh.Item3 });
                changes.Add($"أُضيف المخزن الناقص: {wh.Item1} — {wh.Item2}.");
            }
        }
        foreach (var sch in new[]
        {
            ("SHIP", "استلام", "REC"), ("PLAN", "خطة", "PLN"), ("ORD", "أمر إنتاج", "PRD"),
            ("EXE", "تنفيذ", "EXE"), ("QC", "جودة", "QC"), ("FGR", "استلام تام", "DLV"),
            ("RCV", "سند استلام", "RCV"), ("CD", "تسليم عميل", "CD"), ("TXN", "حركة مخزون", "INV"),
            ("PCL", "إقفال خطة", "PCL"), ("LOT", "دفعة خام", "LOT"),
            ("TASK", "مهمة سير عمل", "TSK"), ("TRT", "معالجة وتعقيم", "TRT"),
        })
        {
            if (!db.NumberingSchemes.Any(x => x.SchemeCode == sch.Item1))
            {
                db.NumberingSchemes.Add(new NumberingScheme { SchemeCode = sch.Item1, SchemeName = sch.Item2, Prefix = sch.Item3, LastSequence = 0 });
                changes.Add($"أُضيف ترقيم المستندات الناقص: {sch.Item1} ({sch.Item3}).");
            }
        }

        // ── §B109 — استكمال وحدات القياس في القواعد القائمة ──
        // الوحدات تُزرع في Seed() وحدها، وهي محروسة بعلامة "Seeded": أي قاعدة أُنشئت
        // قبل إضافة «سلة»/«كرتون» لم تستلمها أبداً، فتظهر قائمة الوحدات في بطاقة الصنف
        // ناقصة — ومن ثَمّ يظهر عمود «الوحدة» في شاشة الخطط فارغاً أو «—»، لأنه يقرأ
        // Product.UnitOfMeasure الذي لم يُضبط أصلاً. الاستكمال هنا idempotent بالاسم.
        foreach (var u in new[]
        {
            ("KG", "كجم"), ("CTN", "كرتون"), ("PCS", "حبة"), ("PC", "قطعة"),
            ("ROLL", "لفة"), ("RUL", "رول"), ("LTR", "لتر"), ("BSK", "سلة"),
        })
        {
            if (!db.UnitsOfMeasure.Any(x => x.UnitNameAr == u.Item2))
            {
                db.UnitsOfMeasure.Add(new UnitOfMeasure { UnitCode = u.Item1, UnitNameAr = u.Item2 });
                changes.Add($"أُضيفت وحدة القياس الناقصة: {u.Item2}.");
            }
        }

        // ── §B109 — استكمال العبوات القياسية (كرتون/سلة) في القواعد القائمة ──
        foreach (var pk in new[]
        {
            ("CT5", "كرتون 5 كجم", 5.0), ("CT10", "كرتون 10 كجم", 10.0), ("BK20", "سلة 20 كجم", 20.0),
        })
        {
            if (!db.PackagingTypes.Any(x => x.PackageCode == pk.Item1))
            {
                db.PackagingTypes.Add(new PackagingType
                { PackageCode = pk.Item1, PackageNameAr = pk.Item2, UnitWeightKg = pk.Item3, UnitsPerPackage = 1 });
                changes.Add($"أُضيفت العبوة الناقصة: {pk.Item2}.");
            }
        }

        // ── §B110 — مسح حقول الاعتماد من سجلات التدقيق التاريخية ──
        // قبل هذا الإصدار كان تعديل أي مستخدم يكتب PasswordHash/PasswordSalt نصاً في
        // سجل التدقيق. الوسيط لم يعد يفعل ذلك، لكن القواعد القائمة تحوي السجلات القديمة —
        // تُنظف هنا مرة واحدة (الخطوة تعديلية فقط: تحذف المفتاحين من JSON اللقطة).
        changes.AddRange(ScrubAuditCredentials(db));

        if (marker == null)
            db.SystemSettings.Add(new SystemSetting { SettingKey = MarkerKey, SettingValue = RefDataUpgradeTarget });
        else
            marker.SettingValue = RefDataUpgradeTarget;

        db.SaveChanges();
        return changes;
    }

    /// <summary>§B110 — يحذف مفاتيح الاعتماد من لقطات التدقيق التاريخية لكيات المستخدمين.</summary>
    private static List<string> ScrubAuditCredentials(DatesErpDbContext db)
    {
        var changes = new List<string>();
        try
        {
            var userTypeName = nameof(AppUser);
            var rows = db.AuditLogs.Where(a => a.DocumentType == userTypeName
                    && ((a.OldValue != null && (a.OldValue.Contains("PasswordHash") || a.OldValue.Contains("PasswordSalt") || a.OldValue.Contains("PasswordHistory")))
                        || (a.NewValue != null && (a.NewValue.Contains("PasswordHash") || a.NewValue.Contains("PasswordSalt") || a.NewValue.Contains("PasswordHistory")))))
                .ToList();
            foreach (var row in rows)
            {
                row.OldValue = StripCredentialKeys(row.OldValue);
                row.NewValue = StripCredentialKeys(row.NewValue);
            }
            if (rows.Count > 0)
                changes.Add($"B111: تم مسح حقول الاعتماد من {rows.Count} سجل تدقيق تاريخي — لا بصمات كلمات مرور في التدقيق بعد الآن.");
        }
        catch (Exception ex)
        {
            // Never mark an unsuccessful scrub as completed. The start-up upgrade
            // must be retried after the underlying database error is corrected.
            throw new InvalidOperationException("تعذر مسح حقول الاعتماد من التدقيق التاريخي.", ex);
        }
        return changes;
    }

    /// <summary>يعيد بناء JSON اللقطة بلا مفاتيح الاعتماد؛ وإن تعذر التحليل أعاد النص كما هو.</summary>
    private static string StripCredentialKeys(string json)
    {
        if (string.IsNullOrEmpty(json)
            || (!json.Contains("PasswordHash") && !json.Contains("PasswordSalt") && !json.Contains("PasswordHistory"))) return json;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            using var ms = new System.IO.MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (AuditSaveChangesInterceptor.IsRedactedProperty(prop.Name)) continue;
                    prop.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            // Old snapshots were sometimes truncated at 4000 characters and are
            // not parseable JSON. Keeping the original text would preserve the
            // credential hashes indefinitely. Preserve the audit row and actor,
            // but deliberately redact the malformed payload in its entirety.
            return "[حُجبت لقطة تدقيق مستخدم تاريخية غير صالحة لحماية بيانات الاعتماد]";
        }
    }
}
