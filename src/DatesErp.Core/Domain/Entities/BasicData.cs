using DatesErp.Core.Common;

namespace DatesErp.Core.Domain.Entities;

/// <summary>بيانات الشركة — تظهر في الترويسات والتقارير.</summary>
public class CompanyInfo : BaseEntity
{
    public string CompanyNameAr { get; set; }
    public string CompanyNameEn { get; set; }
    public string Address { get; set; }
    public string Phone { get; set; }
    public string Email { get; set; }
    public string TaxNumber { get; set; }
    public string ReportFooterNote { get; set; }
    /// <summary>§الهوية البصرية: شعار الشركة — تقرأه كل النماذج والتقارير وشاشات الدخول.</summary>
    public byte[] LogoBytes { get; set; }
}

public class Customer : AuditableEntity
{
    public string CustomerCode { get; set; }
    public string CustomerName { get; set; }
    public string CustomerType { get; set; }
    public string Address { get; set; }
    public string Phone { get; set; }
    public string Email { get; set; }
    public string ContactPerson { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>§B77: أولوية العميل في التوزيع عند تضارب الطاقة (1 أولاً). صفر = بلا أولوية.</summary>
    public int PriorityNo { get; set; } = 0;
    public string Notes { get; set; }
}

public class Supplier : AuditableEntity
{
    public string SupplierCode { get; set; }
    public string SupplierName { get; set; }
    public string Address { get; set; }
    public string Phone { get; set; }
    public bool IsActive { get; set; } = true;
    public string Notes { get; set; }
}

public class ItemGroup : BaseEntity
{
    public string GroupCode { get; set; }
    public string GroupNameAr { get; set; }
    public string GroupType { get; set; } // Raw | Finished | Auxiliary
    public string DefaultUnit { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// §فئة الصنف — تصنيف حرّ يختاره المستخدم ويربط الأصناف به، والنظام لا يفرضه.
///
/// الفرق عن ItemGroup: المجموعات الأربع (001‑004) بنيوية — فهي التي تحدد الوحدة
/// القياسية وأي العمليات تقبل الصنف (UnitsPolicy)، فلا يجوز أن تكون حرة.
/// أما الفئة فطبقة تصنيف فوقها (سكري/خلاص/برحي/تمور فاخرة/تصدير...) بلا أي أثر على الوحدات.
/// </summary>
public class ItemCategory : BaseEntity
{
    public string CategoryCode { get; set; }
    public string CategoryNameAr { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>الصنف — خام أو منتج تام أو مادة مساعدة.</summary>
public class Product : AuditableEntity
{
    public string ProductCode { get; set; }
    public string ProductNameAr { get; set; }
    public int? GroupId { get; set; }
    public string GroupCode { get; set; }
    /// <summary>§الفئة الحرة (ItemCategory) — تصنيف يختاره المستخدم ولا يفرضه النظام.</summary>
    public int? CategoryId { get; set; }
    public string ItemType { get; set; } = "Finished"; // Raw | Finished | Auxiliary | ByProduct
    /// <summary>§العبوة الافتراضية للصنف (بطاقة الصنف ← شاشة الاستلام): تُختار تلقائياً ووزنها يُعبأ — قابلة للتجاوز.</summary>
    public int? DefaultPackagingTypeId { get; set; }
    /// <summary>§B10 لأصناف 004: أي وعاء مرتجع يمثّل هذا الصنف (كرتون/سلة) — يفصل التولّد الآلي.</summary>
    public int? SourcePackagingTypeId { get; set; }
    public string UnitOfMeasure { get; set; } = "كجم";
    public string TradingUnit { get; set; } = "كرتون";
    /// <summary>
    /// §لا وزن ثابت: صفر = «غير معرَّف بعد». كان الافتراض 7.5 كجم، وهو وزن ثابت مقنّع
    /// ترفضه قاعدة الوحدات — فالكرتون 5 كجم لصنف و20 كجم لآخر.
    /// </summary>
    public double CartonWeightKg { get; set; }
    public int MoldsCount { get; set; }            // عدد القوالب (للمنتج التام)
    public double MoldWeightKg { get; set; }       // وزن القالب بالكيلو
    public double HourlyProductionRate { get; set; } // §B85/H4: صفر = طاقة غير معرَّفة (كان 500 صامتاً يمرر أي كمية) — حدّدها من «طاقات الأصناف»
    public double? YieldFactor { get; set; } // §B85/H3: معامل الإنتاجية (خارج/داخل — ماء التشغيل يجعله > 1) — فارغ = غير معرَّف فلا يُحتسب انحراف
    public double ReorderLevel { get; set; }
    public double DefaultCost { get; set; }

    /// <summary>
    /// §المعالجة والتعقيم — هل يشترط هذا الصنف معالجة قبل دخوله الإنتاج؟ (قرار المستخدم س3)
    /// **الافتراضي false**: الإلزام الشامل كان سيعطّل خطوط إنتاج لا علاقة لها بالتعقيم
    /// (التمور المجففة مثلاً)، ويدفع الناس إلى الالتفاف عليه — وذلك أسوأ من غياب الضابط.
    /// يُفعَّل لكل صنف من شاشة الأصناف بصفر تعديل برمجي.
    /// </summary>
    public bool RequiresTreatment { get; set; }

    public bool IsActive { get; set; } = true;
    public string Notes { get; set; }
    /// <summary>
    /// §تتبع الصنف: التعريف الرسمي للتحويل — الصنف الخام الذي يُنتَج منه هذا المنتج
    /// (سكري تام ← سكري خام). يُفرض في كل المراحل: لا إنتاج خلاص من دفعة سكري،
    /// ولا تسليم خلاص من مخزون سكري — إلا بتعريف رسمي في البطاقة.
    /// </summary>
    public int? SourceProductId { get; set; }
}

public class PackagingType : BaseEntity
{
    public string PackageCode { get; set; }
    public string PackageNameAr { get; set; }
    public double UnitWeightKg { get; set; }
    public int UnitsPerPackage { get; set; }
    public int MoldsCount { get; set; } = 1;      // عدد القوالب في العبوة
    public double MoldWeightKg { get; set; }      // وزن القالب بالكيلو
    public bool IsActive { get; set; } = true;
}

/// <summary>المواد المساعدة (كرتون، ملصقات، أكياس...) — تُحتسب تلقائياً لأوامر الإنتاج.</summary>
/// <summary>§مجموعات المواد المساعدة — قابلة للإدارة من الشاشة، لا ثوابت بالكود.</summary>
public class AuxGroup : BaseEntity
{
    public string GroupCode { get; set; }
    public string GroupNameAr { get; set; }
    public bool IsActive { get; set; } = true;
}

public class AuxiliaryMaterial : AuditableEntity
{
    public string MaterialCode { get; set; }
    public string MaterialNameAr { get; set; }
    public string MaterialCategory { get; set; }
    /// <summary>§المجموعة الإدارية (كراتين عملاء/تغليف/وقود...).</summary>
    public string GroupCode { get; set; }
    /// <summary>§وحدات حرة: كجم، لفّة، كرتون، حبة، قطعة، لتر...</summary>
    public string UnitOfMeasure { get; set; }
    /// <summary>§درجة الجودة (عادي/مقوى/ماركة...).</summary>
    public string QualityGrade { get; set; }
    public double DefaultCost { get; set; }
    /// <summary>§متوسط متحرك يُحدَّث عند أي وارد.</summary>
    public double LastCost { get; set; }
    public double ReorderLevel { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>§مواصفة عميل: كرتون ماركة مستقلة لكل عميل/صنف/عبوة + تكلفة — لمنع الخلط.</summary>
public class AuxCustomerSpec : BaseEntity
{
    public int CustomerId { get; set; }
    public int? ProductId { get; set; }
    public int? PackagingTypeId { get; set; }
    public int MaterialId { get; set; }
    /// <summary>§1.50.63 — ربط كراتين العميل بالصنف المساعد الجديد (Product) — يدعم النظامين القديم والجديد.</summary>
    public int? AuxiliaryProductId { get; set; }
    /// <summary>§1.50.63 — الصنف المساعد العام الذي يتم استبداله بماركة العميل (مثلاً كرتون عام 8كجم).</summary>
    public int? GenericAuxiliaryProductId { get; set; }
    public string BrandName { get; set; }
    public double UnitCost { get; set; }
    public int Priority { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}

/// <summary>معادلة استهلاك مادة مساعدة لكل وحدة إنتاج.</summary>
public class ConsumptionFormula : BaseEntity
{
    public int ProductId { get; set; }
    public int? PackagingTypeId { get; set; }
    public int MaterialId { get; set; }
    public string FormulaType { get; set; } = "PerCarton";
    /// <summary>§وضع الاستهلاك: PerCarton لكل كرتون | PerHour اختياري بالساعات | Actual إدخال فعلي (ديزل) | Unused معطل.</summary>
    public string Mode { get; set; } = "PerCarton";
    public bool IsOptional { get; set; }
    public int? CustomerId { get; set; }
    public double QtyPerUnit { get; set; }
    public string UnitOfMeasure { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Warehouse : BaseEntity
{
    public string WarehouseCode { get; set; } // WRM خام | WFG تام | WAUX مساعد
    public string WarehouseNameAr { get; set; }
    public string WarehouseType { get; set; } = "Raw";
    public bool IsActive { get; set; } = true;
}

public class Shift : BaseEntity
{
    public string ShiftCode { get; set; }
    public string ShiftNameAr { get; set; }
    public string StartTime { get; set; }
    public string EndTime { get; set; }
    public double TotalHours { get; set; } = 8;
    public double PlannedDowntimeHours { get; set; } // التوقفات المخططة
    public double EffectiveProductiveHours { get; set; } = 8;
    public bool IsActive { get; set; } = true;
}

/// <summary>§3/§4 — طاقة إنتاجية مستقلة لكل صنف ولكل وردية.</summary>
public class ProductShiftCapacity : BaseEntity
{
    public int ProductId { get; set; }
    public int ShiftId { get; set; }
    /// <summary>§الطاقة حسب العبوة/المواصفة: صفر/فارغ = طاقة عامة للصنف بأي عبوة،
    /// وإن حُددت عبوة تصبح الطاقة خاصة بها (سكري 7.5 كجم ≠ سكري 4 كجم).</summary>
    public int? PackagingTypeId { get; set; }
    public double HourlyProductionRate { get; set; }
    public int ShiftCapacity { get; set; }
    public bool IsActive { get; set; } = true;
}

public class ProductionLine : BaseEntity
{
    public string LineCode { get; set; }
    public string LineNameAr { get; set; }
    public double CapacityPerShift { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Employee : BaseEntity
{
    public string EmployeeCode { get; set; }
    public string FullName { get; set; }
    public string JobTitle { get; set; }
    public string Department { get; set; }
    public string Phone { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>§قاموس الوحدات: كرتون/حبة/كجم/لفة... تُدار من نافذة الوحدات ولا تُفرض بالكود.</summary>
public class UnitOfMeasure : BaseEntity
{
    public string UnitCode { get; set; }
    public string UnitNameAr { get; set; }
    public bool IsActive { get; set; } = true;
}
