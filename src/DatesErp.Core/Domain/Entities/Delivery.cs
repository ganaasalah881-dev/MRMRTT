using DatesErp.Core.Common;

namespace DatesErp.Core.Domain.Entities;

/// <summary>§7 — أمر تسليم الإنتاج التام من الإنتاج إلى مخزن التام (استلام الإنتاج التام).</summary>
public class FinishedGoodsReceipt : WorkflowDocument
{
    public int OrderId { get; set; }
    public int? QualityCheckId { get; set; }
    /// <summary>§B96 — أمر تسليم الإنتاج المصدر (فارغ = سند مباشر من الأمر — المسار القديم).</summary>
    public int? DeliveryId { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public int? PackagingOfficerId { get; set; }
    public int? WarehouseKeeperId { get; set; }
    public int WarehouseId { get; set; } // مخزن التام
    public string ReceiptStatus { get; set; } // None | Partial | Full
    // The RCV scheme generates an integer sequence (shorter than this limit);
    // old prefixed numbers are preserved when they fit. The value is NULL
    // until a draft is actually received/numbered.
    public const int ReceiptNumberMaxLength = 100;
    public string? ReceiptNumber { get; set; }
    public int ReceiveCount { get; set; } // عدد سندات الاستلام المنفذة على هذا الأمر (سندات المتابعة)

    public List<FinishedGoodsReceiptItem> Items { get; set; } = new();
}

public class FinishedGoodsReceiptItem : BaseEntity
{
    public int ReceiptId { get; set; }
    public int ProductId { get; set; }
    public int? LotId { get; set; }
    public int? PackagingTypeId { get; set; }
    public int PackageCount { get; set; }
    public double NetWeightKg { get; set; }
    public double ReceivedQtyKg { get; set; } // ما استلمه أمين المخزن فعلياً
    /// <summary>الكراتين المرحلة تراكميًا لهذا البند؛ العكس يستعملها كما قُيدت فعلاً.</summary>
    public int ReceivedPackageCount { get; set; }
    /// <summary>§B96 — عميل البند (فارغ = عميل الأمر — توافق مع السندات القديمة).</summary>
    public int? CustomerId { get; set; }
    /// <summary>§B96 — بند أمر التسليم المربوط (للمربوط فقط — يُحكم المتبقي والسقف).</summary>
    public int? DeliveryItemId { get; set; }
    /// <summary>§نظام الوحدات: وزن الكرتون وقت الاستلام — لا يتغير بتعريف العبوة لاحقاً.</summary>
    public double CartonWeightKg { get; set; }
}

/// <summary>§7 — تسليم الإنتاج للعميل.</summary>
public class CustomerDelivery : WorkflowDocument
{
    public int CustomerId { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public int? OrderId { get; set; }
    public string DeliveryType { get; set; } = "FinishedGoods";
    public double TotalQtyKg { get; set; }
    public int TotalCartons { get; set; }
    // §9 — الفوترة على المسلَّم فعلياً فقط، مع منع تكرار الفوترة
    public double InvoicedQtyKg { get; set; }
    public double BillableQtyKg => Math.Max(0, TotalQtyKg - InvoicedQtyKg);

    public List<CustomerDeliveryItem> Items { get; set; } = new();
}

public class CustomerDeliveryItem : BaseEntity
{
    public int DeliveryId { get; set; }
    /// <summary>
    /// The exact plan credited on approval. Kept on cancellation for audit/reapproval;
    /// NULL for legacy rows and deliveries that were not linked to a plan.
    /// Never infer a new value from the mutable order list during reversal.
    /// </summary>
    public int? DeliveredPlanId { get; set; }
    public int ProductId { get; set; }
    public int? LotId { get; set; }
    public int? PackagingTypeId { get; set; }
    public int PackageCount { get; set; }
    public double QtyKg { get; set; }
    /// <summary>§القاعدة 7: وزن الكرتون وقت التسليم — يجمّد التعريف اللاحق للعبوة بأثر رجعي.</summary>
    public double CartonWeightKg { get; set; }
}

/// <summary>§B96 — المصدر التشغيلي الجديد لأمر تسليم الإنتاج هو الإنتاج الفعلي؛ تبقى قيم المصادر القديمة لقراءة المستندات التاريخية.</summary>
public static class DeliverySources
{
    public const string FromCheck = "FromCheck";
    public const string FromPlan = "FromPlan";
    public const string FromClosing = "FromClosing";
    /// <summary>المسار الرسمي الجديد: أمر تسليم الإنتاج يُنشأ من جلسة الإنتاج الفعلي فقط.</summary>
    public const string FromActual = "FromActual";

    public static bool IsBypass(string sourceType) => sourceType == FromPlan || sourceType == FromClosing;

    public static string ToArabic(string sourceType) => sourceType switch
    {
        FromCheck => "محضر فحص",
        FromPlan => "خطة إنتاج",
        FromClosing => "إقفال خطة",
        FromActual => "الإنتاج الفعلي",
        _ => sourceType ?? "—"
    };
}

/// <summary>
/// §B96 — أمر تسليم إنتاج: يحرره مدير الإنتاج داخل إدارة الإنتاج.
/// بند = أمر + صنف + دفعة + عميل + كمية — فيستوعب عميلاً أو عدة عملاء وصنفاً أو أكثر.
/// مسودة ← مُصدَر ← (مستلم جزئياً/مكتمل عبر سندات الاستلام المخزنية).
/// </summary>
public class ProductionDelivery : WorkflowDocument
{
    public DateTime? DeliveryDate { get; set; }
    public string SourceType { get; set; }
    public int SourceId { get; set; }
    /// <summary>سبب تجاوز الفحص (إجباري مكتوب لمصدرَي الخطة والإقفال — فارغ لمحضر الفحص).</summary>
    public string BypassReason { get; set; }
    /// <summary>حالة الاستلام: None | Partial | Full — تُحدَّث من سندات الاستلام.</summary>
    public string ReceiptStatus { get; set; } = "None";

    public List<ProductionDeliveryItem> Items { get; set; } = new();
}

/// <summary>§B96 — بند أمر تسليم الإنتاج: الكمية المأمور بها وما استلمه المخزن فعلاً.</summary>
public class ProductionDeliveryItem : BaseEntity
{
    public int DeliveryId { get; set; }
    /// <summary>الأمر المصدر (للتتبع والسقف الفيزيائي الموحد عبر كل المصادر).</summary>
    public int? OrderId { get; set; }
    public int ProductId { get; set; }
    public int? LotId { get; set; }
    /// <summary>عميل البند — جوهر تعدد العملاء: التام يُقيَّد به لا بعميل الترويسة.</summary>
    public int? CustomerId { get; set; }
    public int? PackagingTypeId { get; set; }
    public int PackageCount { get; set; }
    public double QtyKg { get; set; }
    public double ReceivedQtyKg { get; set; }
}
