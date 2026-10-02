using DatesErp.Core.Common;

namespace DatesErp.Core.Domain.Entities;

/// <summary>§7 — فحص الجودة (نهائي/أثناء العملية) — على تصميم استمارة الفحص المعتمدة.</summary>
public class QualityCheck : WorkflowDocument
{
    /// <summary>الفحص اليدوي قد لا يرتبط بأمر — والاعتيادي يرتبط بأمر إنتاجه.</summary>
    public int? OrderId { get; set; }
    public int? ExecutionId { get; set; }
    public DateTime? CheckDate { get; set; }
    public string CheckType { get; set; } = "نهائي";
    public double TotalCheckedKg { get; set; }
    public double AcceptedKg { get; set; }
    public double RejectedKg { get; set; }
    /// <summary>§جودة التمور: الفحص النهائي متوقع بعد فترة التبريد (يومان) — لا يظهر العيب إلا بعد أن يبرد المنتج.</summary>
    public DateTime? ExpectedCheckDate { get; set; }
    /// <summary>§قرار الجودة: Passed مطابق ومقبول للإفراج | Quarantine حجز وتحريز مؤقت | Rejected مرفوض/عوادم.</summary>
    public string Decision { get; set; } = "Passed";
    /// <summary>§معايير الفحص المخبري والحسي — المواصفة القياسية المعتمدة للتمور.</summary>
    public double MoisturePct { get; set; }        // نسبة الرطوبة % (14–18)
    public double BrixDeg { get; set; }            // تركيز السكريات Brix° (≥ 65)
    public double SkinSeparationPct { get; set; }  // نسبة انفصال القشرة % (≤ 5)
    public double ImpuritiesPct { get; set; }      // نسبة الشوائب والأتربة % (≤ 1)
    public int SampleCartons { get; set; }         // عينة الفحص المخبري (كرتون)
    public string InspectorNotes { get; set; }     // ملاحظات وتوصيات مسؤول الجودة

    /// <summary>§B95 — اسم الفاحص (لقطة وقت الحفظ من المستخدم الحالي — يُعرض في رأس المحضر والطباعة).</summary>
    public string InspectorName { get; set; }
    /// <summary>§B95 — إجماليات الكراتين: الإنتاج التام يُقاس بالكرتون، ومعادلة الاكتمال كرتونية أولاً ثم كيلو.</summary>
    public double TotalCheckedCartons { get; set; }
    public double AcceptedCartons { get; set; }
    /// <summary>§B95 — كراتين مرفوضة (غير مطابق + مرفوض نهائي) — التفصيل الدقيق في نتائج الفحص الديناميكية.</summary>
    public double RejectedCartons { get; set; }

    public List<QualityCheckItem> Items { get; set; } = new();

    /// <summary>
    /// §النتائج الديناميكية: نوع نتيجة + كمية + وحدتها — بدل أعمدة ثابتة باسم «حشف/نوى».
    /// الأصناف والوحدات وأنواع النتائج كلها من التعريفات، فلا نتيجة مثبّتة في الكود.
    /// </summary>
    public List<InspectionResult> Results { get; set; } = new();
}

public class QualityCheckItem : BaseEntity
{
    public int CheckId { get; set; }
    /// <summary>Exact produced line, required for new order-linked checks; nullable for historical data.</summary>
    public int? OrderItemId { get; set; }
    public int? CustomerId { get; set; }
    public int ProductId { get; set; }
    public int? LotId { get; set; }
    public int? PackagingTypeId { get; set; }
    public double CheckedQtyKg { get; set; }
    public double AcceptedQtyKg { get; set; }
    public double RejectedQtyKg { get; set; }
    /// <summary>§B95 — الكراتين المفحوصة/المطابقة/المرفوضة للبند (وحدة الإنتاج التام الأساسية).</summary>
    public double CheckedCartons { get; set; }
    public double AcceptedCartons { get; set; }
    public double RejectedCartons { get; set; }
    public string Notes { get; set; }
}

/// <summary>§B95 — حالات محضر فحص الإنتاج التام: مسودة ← قيد الفحص ← مكتمل ← معتمد.</summary>
public static class QualityCheckStatuses
{
    public static string ToArabic(string status) => status switch
    {
        DocStatuses.Draft => "مسودة",
        DocStatuses.Submitted => "مُرسل للفحص",
        DocStatuses.InProgress => "قيد الفحص",
        DocStatuses.Completed => "مكتمل",
        DocStatuses.Approved => "معتمد",
        _ => DocStatuses.ToArabic(status)
    };
}

/// <summary>§B95 — سجل تصحيح معتمد على فحص معتمد: السبب إجباري ويُحفظ مع المستخدم والوقت.</summary>
public class QualityCorrection : BaseEntity
{
    public int CheckId { get; set; }
    public string Reason { get; set; }
    public int? CorrectedBy { get; set; }
    public string CorrectedByName { get; set; }
    public DateTime CorrectedDate { get; set; } = DateTime.Now;
}

/// <summary>§7/§8 — الأصناف الثانوية (حشف، نوى، مخلفات) — تُقاس بالكيلو دائماً.</summary>
public class ByProduct : BaseEntity
{
    public string ByProductCode { get; set; }
    public string ByProductNameAr { get; set; } // حشف | نوى | مخلفات فرز
    public string UnitOfMeasure { get; set; } = "كجم"; // §8: وحدات الأصناف الثانوية كيلوجرام
    public bool IsActive { get; set; } = true;
}

public class QualityByProductRecord : BaseEntity
{
    public int CheckId { get; set; }
    public int ByProductId { get; set; }
    public double QtyKg { get; set; }
    public string Notes { get; set; }
}

/// <summary>§لا ثوابت في الكود: معايير الفحص المخبري تُهيأ كلها من شاشة إعدادات الأصناف.</summary>
public class QualityStandard : BaseEntity
{
    public string Code { get; set; }
    public string NameAr { get; set; }
    public string UnitLabel { get; set; } = "%";
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }
    public double DefaultValue { get; set; }
    public int SortNo { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>قيمة معيار مسجلة في استمارة فحص (بدلاً من أعمدة ثابتة).</summary>
public class QualityStandardRecord : BaseEntity
{
    public int CheckId { get; set; }
    public int StandardId { get; set; }
    public double Value { get; set; }
    public string Notes { get; set; }
}

/// <summary>§مخرجات ثانوية فعلية لإقفال خطة — الأصناف من جدول ByProducts لا من الكود.</summary>
public class PlanClosingByProduct : BaseEntity
{
    public int ClosingId { get; set; }
    public int ByProductId { get; set; }
    public double QtyKg { get; set; }
}
