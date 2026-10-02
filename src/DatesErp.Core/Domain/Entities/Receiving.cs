using DatesErp.Core.Common;

namespace DatesErp.Core.Domain.Entities;

/// <summary>§7 — استلام التمور (شحنة واردة من عميل/مورد).</summary>
public class Shipment : WorkflowDocument
{
    public int CustomerId { get; set; }
    public string ContainerNumber { get; set; }
    public string VesselName { get; set; }
    public DateTime? ArrivalDate { get; set; }
    public DateTime? ReceivedDate { get; set; }
    public int? ReceivedBy { get; set; }
    public double TotalWeightKg { get; set; }
    public int TotalCartons { get; set; }
    public int ItemCount { get; set; }
    /// <summary>§استلام جزئي: سند لاحق يكمل بنوداً علّقت في سند سابق.</summary>
    public int? ParentShipmentId { get; set; }
    /// <summary>§المخازن المتعددة: مخزن الاستلام الفعلي الذي وصلت إليه الحاوية — الاعتماد يقيّد الوارد فيه.
    /// فارغ = مخزن الخام الافتراضي WRM (توافق مع البيانات القديمة).</summary>
    public int? ReceivingWarehouseId { get; set; }

    public List<ShipmentItem> Items { get; set; } = new();
    public List<Lot> Lots { get; set; } = new();
}

public class ShipmentItem : BaseEntity
{
    /// <summary>قرار البند الصريح. NULL للبيانات السابقة فقط؛ الحفظ/الاعتماد الجديد يرفضان الفراغ.</summary>
    public bool? TreatmentRequired { get; set; }
    /// <summary>التاريخ المختار، حصراً عند نعم. الجاهزية من بداية هذا اليوم.</summary>
    public DateTime? TreatmentUntilDate { get; set; }
    /// <summary>ختم الإكمال المحاسبي الآلي؛ ليس مدخلاً في DTO أو الواجهة.</summary>
    public DateTime? TreatmentCompletedAt { get; set; }

    public int ShipmentId { get; set; }
    public int ProductId { get; set; }
    public int? PackagingTypeId { get; set; }
    public int PackageCount { get; set; }
    public double UnitWeightKg { get; set; }
    public double TotalWeightKg { get; set; }
    public string Status { get; set; } = DocStatuses.Draft;
    /// <summary>§نظام الوحدات: وحدة الاستلام الأصلية كما وصلت فعلياً (كرتون/سلة/كجم...) — لا تُفقد أبداً،
    /// والكمية القياسية للمخزون الخام هي الكيلو (TotalWeightKg).</summary>
    public string ReceiptUnit { get; set; }

    /// <summary>
    /// §B107 — وجهة البند بعد الاعتماد: <see cref="ReceiptDestinations.RawStore"/> مخزن الخام
    /// (السلوك القائم والافتراضي) أو <see cref="ReceiptDestinations.Treatment"/> مستودع المعالجة
    /// والتعقيم. الاختيار **لكل بند** لا لكل سند: الحاوية الواحدة تحمل أصنافاً بعضها مصاب وبعضها سليم.
    /// فارغ = مخزن الخام (توافق كامل مع كل السندات القديمة).
    /// </summary>
    public string Destination { get; set; } = ReceiptDestinations.RawStore;

    /// <summary>
    /// §B107 — تقسيم كمية البند الواحد إلى أجزاء بدرجات إصابة مختلفة (5/7/10 أيام)
    /// **بلا صنف جديد**: الأجزاء تصير عدة صفوف <c>RawTreatment</c> على نفس الدفعة عند الاعتماد.
    /// </summary>
    public List<ShipmentItemTreatmentPart> TreatmentParts { get; set; } = new();
}

/// <summary>§B107 — وجهة بند الاستلام بعد الاعتماد. محصورة في مخزنين معلومين — لا كود حر.</summary>
public static class ReceiptDestinations
{
    /// <summary>مخزن الخام — الوجهة الافتراضية والسلوك القائم.</summary>
    public const string RawStore = "WRM";

    /// <summary>مستودع المعالجة والتعقيم — تبدأ المعالجة تلقائياً عند اعتماد السند.</summary>
    public const string Treatment = "WTRT";

    public static bool IsTreatment(string d)
        => string.Equals((d ?? "").Trim(), Treatment, StringComparison.OrdinalIgnoreCase);

    /// <summary>يوحّد أي قيمة قادمة من الواجهة/البيانات القديمة إلى أحد الرمزين.</summary>
    public static string Normalize(string d)
        => IsTreatment(d) ? Treatment : RawStore;

    public static string ToArabic(string d)
        => IsTreatment(d) ? "مستودع المعالجة والتعقيم" : "مخزن الخام";
}

/// <summary>
/// §B107 — جزء من كمية بند استلام بدرجة إصابة واحدة.
///
/// **لماذا جدول لا صنف جديد:** البند الواحد (5,000 سلة سكري) قد ينقسم إلى 4,000 سليمة
/// + 500 إصابة متوسطة (7 أيام) + 500 إصابة شديدة (10 أيام). إنشاء صنف لكل درجة كان
/// سيفجّر بطاقة الأصناف ويقطع التتبع؛ فالصنف واحد والدرجة صفة كمية على الجزء.
/// كل جزء يتحول عند الاعتماد إلى صف <c>RawTreatment</c> مستقل على **نفس الدفعة**،
/// وهو بالضبط ما يجيده المحرك القائم (مُختبَر بـ500+500).
/// </summary>
public class ShipmentItemTreatmentPart : BaseEntity
{
    public int ShipmentItemId { get; set; }

    /// <summary>درجة الإصابة — انظر <see cref="InfestationLevels"/>.</summary>
    public string InfestationLevel { get; set; } = InfestationLevels.Medium;

    /// <summary>نوع المعالجة المقابل للدرجة (TRT-INF-L/M/H) — يُشتق عند الحفظ إن لم يُحدَّد.</summary>
    public int? TreatmentTypeId { get; set; }

    public double QtyKg { get; set; }
    public int PackageCount { get; set; }

    /// <summary>مدة الدرجة المثبتة عند الحفظ (120/168/240 ساعة). فارغ في السند القديم = المدة الافتراضية؛ لا تجاوز حر من الاستلام.</summary>
    public double? DurationHours { get; set; }

    public string Notes { get; set; }
}

/// <summary>
/// §B107 — درجات الإصابة ومُددها المعتمدة: خفيفة 5 أيام · متوسطة 7 أيام · شديدة 10 أيام.
/// الرموز تطابق أنواع المعالجة المبذورة في B106 (TRT-INF-L / TRT-INF-M / TRT-INF-H).
/// </summary>
public static class InfestationLevels
{
    public const string Light = "Light";     // 5 أيام
    public const string Medium = "Medium";   // 7 أيام
    public const string High = "High";       // 10 أيام

    /// <summary>رمز نوع المعالجة المبذور المقابل للدرجة.</summary>
    public static string TypeCode(string level) => Normalize(level) switch
    {
        Light => "TRT-INF-L",
        High => "TRT-INF-H",
        _ => "TRT-INF-M"
    };

    /// <summary>المدة الافتراضية بالساعات — احتياط لو غاب نوع المعالجة من القاعدة.</summary>
    public static double DefaultHours(string level) => Normalize(level) switch
    {
        Light => 120d,
        High => 240d,
        _ => 168d
    };

    public static string Normalize(string level)
    {
        var s = (level ?? "").Trim();
        if (s.Equals(Light, StringComparison.OrdinalIgnoreCase) || s.Contains("خفيف")) return Light;
        if (s.Equals(High, StringComparison.OrdinalIgnoreCase) || s.Contains("شديد")) return High;
        return Medium;
    }

    public static string ToArabic(string level) => Normalize(level) switch
    {
        Light => "إصابة خفيفة — 5 أيام",
        High => "إصابة شديدة — 10 أيام",
        _ => "إصابة متوسطة — 7 أيام"
    };

    public static readonly string[] All = { Light, Medium, High };
}

/// <summary>§7 — الدفعة (Lot) الناتجة عن اعتماد الاستلام — أساس التتبع الكامل.</summary>
public class Lot : AuditableEntity
{
    public string LotCode { get; set; }
    public int? ShipmentId { get; set; }
    public int? ShipmentItemId { get; set; }
    public int ProductId { get; set; }
    public int? CustomerId { get; set; }
    public int? PackagingTypeId { get; set; }
    public DateTime? LotDate { get; set; }
    public double InitialQtyKg { get; set; }
    public double ProducedQtyKg { get; set; }
    public double InStockQtyKg { get; set; }
    public double DeliveredQtyKg { get; set; }
    public double WastageQtyKg { get; set; }
    public string Status { get; set; } = DocStatuses.Approved;

    /// <summary>المتاح للتخطيط = المخزون غير المحجوز لخطط نشطة.</summary>
    public double ReservedQtyKg { get; set; }

    /// <summary>
    /// §المعالجة والتعقيم — الكمية الداخلة حالياً في دورة معالجة جارية (مستودع WTRT).
    /// **لا تُخصم من <see cref="InStockQtyKg"/>**: الكمية لم تغادر المنشأة بل انتقلت بين
    /// مستودعين، وخصمها كان سيوهم بنقص في المخزون. تتغير عند البدء والإفراج والرفض فقط.
    /// ثابت التوازن: رصيد(WRM) + رصيد(WTRT) = InStockQtyKg.
    /// </summary>
    public double UnderTreatmentQtyKg { get; set; }

    /// <summary>
    /// §المعالجة والتعقيم — المفرَج عنه تراكمياً بعد اكتمال المعالجة واعتمادها.
    /// للدفعات السابقة للترقية يساويها المُرحِّل بـ<see cref="InStockQtyKg"/> — وإلا
    /// توقف الإنتاج على مخزون قائم لم يمر بمعالجة قط.
    /// </summary>
    public double TreatmentReadyQtyKg { get; set; }

    /// <summary>
    /// المتاح للتخطيط = المخزون − المحجوز لخطط نشطة − **ما هو تحت المعالجة**.
    ///
    /// §المعالجة والتعقيم — تغيّر التعريف بإضافة الحدّ الثالث. القيمة العددية **لم تتغير
    /// لأي بيانات قائمة** لأن UnderTreatmentQtyKg = 0 لكل صف قبل أول عملية معالجة.
    ///
    /// ⚠️ **خاصية محسوبة غير مخزّنة — لا تُترجم إلى SQL** (فخ §B64). كل استعلام خادمي
    /// يجب أن يطرح الأعمدة الثلاثة صراحةً بدل استدعائها، وإلا انفجر وقت التشغيل.
    /// </summary>
    public double AvailableQtyKg => Math.Max(0, InStockQtyKg - ReservedQtyKg - UnderTreatmentQtyKg);
}

/// <summary>
/// §المعالجة والتعقيم — نوع المعالجة (تعقيم حراري / تجميد / تبخير…). بيانات أساسية
/// تُدار من شاشة، لا قائمة مضمّنة في الشيفرة.
/// </summary>
public class TreatmentType : BaseEntity
{
    public string TypeCode { get; set; }
    public string TypeNameAr { get; set; }

    /// <summary>
    /// المدة الافتراضية **بالساعات** لا بالأيام: التعقيم الحراري قد يكون 6 ساعات
    /// والتجميد 10 أيام. وحدة واحدة تستوعب الاثنين بلا كسور مربكة.
    /// قابلة للتجاوز في كل عملية على حدة.
    /// </summary>
    public double DefaultDurationHours { get; set; } = 24;

    /// <summary>هل يلزم فحص جودة معتمد قبل الإفراج؟ (قرار المستخدم س4: قابل للضبط لكل نوع)</summary>
    public bool RequiresQualityCheck { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>
/// §المعالجة والتعقيم — عملية معالجة واحدة على **جزء** من دفعة.
///
/// **مصدر الحقيقة للحالة** (قرار المستخدم س1)؛ مستودع WTRT أثر محاسبي تابع.
/// السبب أن الدفعة الواحدة تنقسم إلى أجزاء بمُدد مختلفة (5,000 سلة = 4,000 جاهزة
/// + 500 لسبعة أيام + 500 لعشرة أيام)، والمستودع يحمل رصيداً واحداً بلا تاريخ
/// جاهزية فيعجز عن الإجابة عن «كم يجهز يوم 13 وكم يوم 16».
///
/// **لا صنف جديد لكل مدة**: ProductId يُنسخ من الدفعة كما هو.
/// </summary>
public class RawTreatment : AuditableEntity
{
    /// <summary>رابط فريد للبند ذي التاريخ المختار؛ NULL للعمليات التاريخية ذات الضوابط القديمة.</summary>
    public int? ReceivingItemId { get; set; }

    public string TreatmentNo { get; set; }

    /// <summary>الدفعة الأم — عمود التتبع الذي لا ينقطع حتى المنتج النهائي.</summary>
    public int LotId { get; set; }
    public int ProductId { get; set; }
    public int? TreatmentTypeId { get; set; }

    /// <summary>الكمية بالكيلو — الوحدة القياسية للمخزون الخام.</summary>
    public double QtyKg { get; set; }

    /// <summary>
    /// عدد الطرود (سلال/كراتين) كما استُلمت. يُخزَّن بجانب الكيلو لأن المستخدم يتعامل
    /// بالسلال؛ الحساب بالكيلو والعرض بوحدة الاستلام، وإلا رأى أرقاماً لا يعرفها.
    /// </summary>
    public int PackageCount { get; set; }

    public DateTime StartedAt { get; set; }
    public double DurationHours { get; set; }

    /// <summary>
    /// موعد الجاهزية المتوقع — **يُحسب تلقائياً** = StartedAt + DurationHours عند البدء.
    /// يُخزَّن ولا يُشتق عند القراءة كي يبقى ثابتاً لو عُدّلت المدة الافتراضية للنوع لاحقاً.
    /// </summary>
    public DateTime ExpectedReadyAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>المفرَج عنه تراكمياً — يخدم الإفراج الجزئي (500 من 1,000).</summary>
    public double ReleasedQtyKg { get; set; }

    public double RejectedQtyKg { get; set; }
    public int? ResponsibleUserId { get; set; }
    public string Notes { get; set; }
    public string Status { get; set; } = TreatmentStatuses.InProgress;

    /// <summary>المتبقي داخل الدورة ولم يُفرج عنه ولم يُرفض بعد.</summary>
    public double RemainingQtyKg => Math.Max(0, QtyKg - ReleasedQtyKg - RejectedQtyKg);

    /// <summary>
    /// بلغت مدتها ولم تُفرج بعد. **الجاهزية تُشتق بالمقارنة الزمنية عند القراءة ولا
    /// يغيّرها مؤقّت خلفي**: الوقت شرط ضروري لا كافٍ، والإفراج يبقى فعلاً بشرياً موثّقاً
    /// — وإلا دخلت بضاعة الإنتاج دون أن يراها أحد.
    /// </summary>
    public bool IsReadyByTime => DateTime.Now >= ExpectedReadyAt;

    /// <summary>تجاوزت موعدها ولم تُنجز — أساس تقرير «المعالجات المتأخرة».</summary>
    public bool IsOverdue => Status == TreatmentStatuses.InProgress && DateTime.Now > ExpectedReadyAt;
}

/// <summary>§المعالجة والتعقيم — حالات عملية المعالجة.</summary>
public static class TreatmentStatuses
{
    public const string InProgress = "InProgress";   // تحت المعالجة
    public const string Released = "Released";       // أُفرج عنها كاملة — جاهزة للإنتاج
    public const string Rejected = "Rejected";       // مرفوضة
    public const string Cancelled = "Cancelled";     // أُلغي البدء (خطأ إدخال)

    public static string ToArabic(string s) => s switch
    {
        InProgress => "تحت المعالجة",
        Released => "جاهزة للإنتاج",
        Rejected => "مرفوضة",
        Cancelled => "ملغاة",
        _ => s ?? "-"
    };
}
