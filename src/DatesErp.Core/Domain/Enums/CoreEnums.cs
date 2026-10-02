namespace DatesErp.Core.Domain.Enums;

/// <summary>§10 — الصلاحيات التي تُدار مركزياً حسب الدور.</summary>
[Flags]
public enum PermissionFlags
{
    None = 0,
    View = 1,
    Create = 2,
    Edit = 4,
    Delete = 8,
    Approve = 16,
    Post = 32,
    Print = 64,
    Export = 128,
    Cancel = 256,
    All = View | Create | Edit | Delete | Approve | Post | Print | Export | Cancel
}

/// <summary>§10 — الأدوار المركزية الافتراضية.</summary>
public static class SystemRoles
{
    public const string Administrator = "Administrator";
    public const string Management = "Management";
    public const string Finance = "Finance";
    public const string Warehouse = "Warehouse";
    public const string Production = "Production";
    public const string Quality = "Quality";
    public const string Sales = "Sales";

    public static readonly (string Code, string Arabic)[] All =
    {
        (Administrator, "مدير النظام"),
        (Management, "الإدارة"),
        (Finance, "المالية"),
        (Warehouse, "المخازن"),
        (Production, "الإنتاج"),
        (Quality, "الجودة"),
        (Sales, "المبيعات")
    };
}

/// <summary>نوع حركة المخزون — §9 كل حركة مرتبطة بمستندها.</summary>
public enum MovementType
{
    Inbound = 1,   // وارد
    Outbound = 2,  // صادر
    Transfer = 3,  // تحويل بين مخازن
    Adjustment = 4 // تسوية جرد
}

/// <summary>نوع المستند المرجعي لحركة المخزون — التتبع الكامل §9.</summary>
public enum ReferenceDocType
{
    ShipmentReceipt = 1,     // استلام تمور
    MaterialIssue = 2,       // صرف مواد
    ProductionExecution = 3, // تنفيذ إنتاج
    FinishedGoodsReceipt = 4,// استلام إنتاج تام
    CustomerDelivery = 5,    // تسليم عميل
    Adjustment = 6,          // تسوية
    Return = 7,              // مرتجع
    CartonReturn = 8,        // §كرتون فارغ متولد من التفريغ
    CartonSale = 9,          // §بيع كرتون فارغ
    CartonCount = 10,        // §عدّ فعلي للكرتون
    MaterialReturn = 11,     // §مرتجع مواد مساعدة بعد التسوية الفعلية
    // §المعالجة والتعقيم — تُضاف في النهاية بقيم صريحة. إعادة ترقيم القيم القائمة
    // كانت ستفسد كل الحركات التاريخية في القاعدة (يقرؤها EF كأرقام لا كأسماء).
    TreatmentStart = 12,     // نقل إلى مستودع المعالجة
    TreatmentRelease = 13    // إفراج من المعالجة إلى الخام الجاهز
}

/// <summary>§7 — مراحل سير العمل التي لا يجوز تجاوزها.</summary>
public enum WorkflowStage
{
    Receiving = 1,
    LotApproved = 2,
    Planning = 3,
    ProductionOrder = 4,
    MaterialIssue = 5,
    Execution = 6,
    Quality = 7,
    OrderClosed = 8,
    FinishedGoodsReceipt = 9,
    CustomerDelivery = 10
}

/// <summary>طريقة المصادقة إلى SQL Server — §13.</summary>
public enum SqlAuthMode
{
    WindowsAuthentication = 1,
    SqlAuthentication = 2
}
