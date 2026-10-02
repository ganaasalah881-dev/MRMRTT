using DatesErp.Core.Common;

namespace DatesErp.Core.Domain.Entities;

/// <summary>
/// §1.50.63 — تهيئة الأصناف المساعدة: ربط بطاقة الصنف بإعدادات الصرف.
/// الصنف نفسه معرّف في بطاقة الأصناف (ItemType=Auxiliary) ولا نكرره.
/// هنا نحدد طريقة الصرف وهل يحتاج صرف عند إصدار أمر الإنتاج.
/// </summary>
public class AuxiliaryProductConfig : BaseEntity
{
    /// <summary>الصنف المساعد — من بطاقة الأصناف (ItemType=Auxiliary أو Pack).</summary>
    public int ProductId { get; set; }
    /// <summary>المجموعة/التصنيف: أصناف مساعدة — من ItemGroup أو نص حر.</summary>
    public string GroupCode { get; set; }
    /// <summary>وحدة المخزون الأساسية — من قاموس الوحدات.</summary>
    public string BaseUnit { get; set; }
    /// <summary>وزن الوحدة إن وجد (كجم).</summary>
    public decimal UnitWeightKg { get; set; }
    /// <summary>طريقة الصرف: ByUnit بالوحدة | ByKilo بالكيلو | PerProduction حسب كمية الإنتاج.</summary>
    public string DispensingMethod { get; set; } = "ByUnit";
    /// <summary>هل يحتاج هذا الصنف إلى تشغيل/صرف عند إصدار أمر الإنتاج؟</summary>
    public bool NeedsIssueOnOrder { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public string Notes { get; set; }
}

/// <summary>
/// §1.50.63 — مكونات الإنتاج / احتياجات الصنف التام: ربط الصنف التام بالأصناف المساعدة.
/// الكمية مرتبطة بمواصفة الصنف التام وليس بالخام.
/// مثال: سكري 8 كجم → كرتون 8 كجم 1 وحدة لكل كرتون، ملصق 1، كيس 1، مادة X 0.020 كجم.
/// </summary>
public class ProductAuxiliaryRequirement : BaseEntity
{
    /// <summary>الصنف التام (Finished).</summary>
    public int FinishedProductId { get; set; }
    /// <summary>الصنف المساعد (Auxiliary/Product).</summary>
    public int AuxiliaryProductId { get; set; }
    /// <summary>الوحدة: وحدة/كجم/...</summary>
    public string Unit { get; set; }
    /// <summary>الكمية لكل كرتون (أو لكل وحدة إنتاج حسب طريقة الحساب).</summary>
    public decimal QtyPerCarton { get; set; }
    /// <summary>طريقة الحساب: PerCarton لكل كرتون | PerKg لكل كجم | PerProduction حسب كمية الإنتاج.</summary>
    public string CalculationMethod { get; set; } = "PerCarton";
    public bool IsActive { get; set; } = true;
    public string Notes { get; set; }
}

/// <summary>
/// §1.50.63 — صرف الأصناف المساعدة للإنتاج: كل حركة صرف موثقة بتفاصيل التتبع الكاملة.
/// </summary>
public class AuxiliaryIssueTransaction : BaseEntity
{
    /// <summary>رقم أمر الإنتاج.</summary>
    public int OrderId { get; set; }
    public string OrderNumber { get; set; }
    /// <summary>الصنف المساعد (ProductId) — أساسي في النظام الجديد.</summary>
    public int? AuxiliaryProductId { get; set; }
    /// <summary>المادة المساعدة (MaterialId) — للتوافق مع النظام القديم.</summary>
    public int? MaterialId { get; set; }
    /// <summary>الكمية المطلوبة لأمر الإنتاج.</summary>
    public decimal RequiredQty { get; set; }
    /// <summary>الكمية المصروفة في هذه الحركة.</summary>
    public decimal IssuedQty { get; set; }
    /// <summary>الوحدة.</summary>
    public string Unit { get; set; }
    public DateTime IssueDate { get; set; } = DateTime.Now;
    public int? UserId { get; set; }
    public string UserName { get; set; }
    /// <summary>رقم مستند الصرف.</summary>
    public string DocumentNumber { get; set; }
    /// <summary>المستودع المصدر (WAUX).</summary>
    public int? WarehouseId { get; set; }
    /// <summary>الرصيد قبل الصرف.</summary>
    public decimal BalanceBefore { get; set; }
    /// <summary>الرصيد بعد الصرف.</summary>
    public decimal BalanceAfter { get; set; }
    public string Notes { get; set; }
}

/// <summary>
/// §1.50.63 — إرجاع الأصناف المساعدة من الإنتاج إلى المخزن (عند تقليل الإنتاج أو وجود فائض غير مستخدم).
/// </summary>
public class AuxiliaryReturnTransaction : BaseEntity
{
    public int OrderId { get; set; }
    public string OrderNumber { get; set; }
    public int? AuxiliaryProductId { get; set; }
    public int? MaterialId { get; set; }
    public decimal ReturnedQty { get; set; }
    public string Unit { get; set; }
    public DateTime ReturnDate { get; set; } = DateTime.Now;
    public int? UserId { get; set; }
    public string UserName { get; set; }
    public string DocumentNumber { get; set; }
    public int? WarehouseId { get; set; }
    public decimal BalanceBefore { get; set; }
    public decimal BalanceAfter { get; set; }
    public string Reason { get; set; }
}
