using DatesErp.Core.Common;

namespace DatesErp.Core.Domain.Entities;

/// <summary>
/// §نوع نتيجة الفحص — قابل للتعريف من الإعدادات، لا ثوابت في الكود.
/// «خرج تام مطابق / منسم / حشف / مكسور / غير مطابق / نوى / مخلفات / أي نتيجة أخرى»
/// كلها صفوف في هذا الجدول: يُضاف نوع جديد من الشاشة فيظهر في استمارة الفحص دون تعديل كود.
///
/// الوحدة ليست عموداً نصياً حراً: <see cref="UnitId"/> يشير إلى قاموس الوحدات
/// (UnitOfMeasure) و<see cref="UnitLabel"/> نسخة للعرض فقط — فالنظام يطبّق قواعد
/// الوحدة المعرّفة لهذا النوع ولا يقبل وحدة غير معرفة.
/// </summary>
public class InspectionResultType : BaseEntity
{
    public string Code { get; set; }
    public string NameAr { get; set; }

    /// <summary>
    /// تصنيف النوع — يحدد كيف يعامله النظام في الإجماليات:
    /// Accepted (مقبول للإفراج) | Rejected (مرفوض) | ByProduct (مخرج ثانوي) | Loss (فاقد).
    /// ليس اسماً ثابتاً لنتيجة بعينها: يمكن تعريف أكثر من نوع مقبول أو أكثر من مخرج ثانوي.
    /// </summary>
    public string ResultKind { get; set; } = "Accepted";

    /// <summary>قاموس الوحدات — الوحدة المسموحة/الافتراضية لهذا النوع.</summary>
    public int? UnitId { get; set; }

    /// <summary>نسخة للعرض من اسم الوحدة (تُزامَن عند الحفظ من القاموس).</summary>
    public string UnitLabel { get; set; }

    /// <summary>هل يُعتبر منتجاً تاماً (يُفرَج عنه كمخزون تام)؟</summary>
    public bool IsFinishedGood { get; set; }

    /// <summary>هل يُعتبر مخرجاً ثانوياً؟</summary>
    public bool IsByProduct { get; set; }

    /// <summary>هل يدخل المخزون عند الاعتماد؟</summary>
    public bool EntersInventory { get; set; } = true;

    /// <summary>هل يُحسب ضمن الفاقد؟</summary>
    public bool CountsAsLoss { get; set; }

    /// <summary>
    /// §B95 — درجة الرفض للأنواع المرفوضة (ResultKind = Rejected) فقط:
    /// false = «غير مطابق» (قابل للمعالجة/إعادة الفرز — يُحتفظ به بحالته ولا يُسلَّم)،
    /// true = «مرفوض» نهائي (عوادم/إتلاف — لا يدخل المخزون ولا يُسلَّم إطلاقاً).
    /// الأنواع المقبولة دائماً «مطابق» بغض النظر عن هذا العلم.
    /// </summary>
    public bool IsFinalScrap { get; set; }

    public int SortNo { get; set; }
    public bool IsActive { get; set; } = true;
    public string Notes { get; set; }

    // ── ثوابت التصنيف (ResultKind) — تصنيفات لا أسماء نتائج ──
    public const string KindAccepted = "Accepted";
    public const string KindRejected = "Rejected";
    public const string KindByProduct = "ByProduct";
    public const string KindLoss = "Loss";

    public static string KindNameAr(string kind) => kind switch
    {
        KindAccepted => "مقبول للإفراج",
        KindRejected => "مرفوض",
        KindByProduct => "مخرج ثانوي",
        KindLoss => "فاقد",
        _ => kind
    };

    // ── §B95: درجات نتيجة فحص الإنتاج التام (صفوف جدول النتيجة: مطابق/غير مطابق/مرفوض) ──
    public const string GradeConforming = "Conforming";
    public const string GradeNonConforming = "NonConforming";
    public const string GradeScrap = "Scrap";

    /// <summary>
    /// §B95 — درجة النوع في معادلة الإنتاج التام: المقبول ← مطابق، والمرفوض ينقسم
    /// إلى غير مطابق أو مرفوض نهائي حسب <see cref="IsFinalScrap"/>، والمخرج الثانوي
    /// والفاقد خارج المعادلة (يُعرضان كبند مستقل ولا يدخلان إجمالي الكراتين).
    /// </summary>
    public static string GradeOf(string resultKind, bool isFinalScrap) => resultKind switch
    {
        KindAccepted => GradeConforming,
        KindRejected => isFinalScrap ? GradeScrap : GradeNonConforming,
        _ => null
    };

    // §B106 — أُزيل «منسم» من تسمية درجة «غير مطابق».
    // كان تناقضاً مع قاعدة المصنع المطبَّقة في البذر نفسه: النوع RT-MONSAM «تمر منسم»
    // مُعرَّف KindAccepted + IsFinishedGood + EntersInventory — أي **منتج تام مقبول
    // قابل للبيع والتسليم**، لا مخرجاً ثانوياً ولا غير مطابق. فتسمية الدرجة الرافضة
    // باسمه كانت توحي لمسؤول الجودة بأن كل منسم مرفوض، وهو عكس التصنيف المعتمد.
    // (B103 صحّحت الترويسة المولَّدة وتركت هذه التسمية الثابتة — فاكتمل التصحيح هنا.)
    public static string GradeNameAr(string grade) => grade switch
    {
        GradeConforming => "مطابق / سليم",
        GradeNonConforming => "غير مطابق",
        GradeScrap => "مرفوض",
        _ => grade ?? "—"
    };
}

/// <summary>
/// §ملف تعريف نتائج الفحص لصنف (أو لمجموعة أصناف) — مصدر «أي النتائج تظهر لهذه السلعة».
/// السلسلة المطلوبة: تعريف الصنف/المنتج ← نتائج الفحص المسموحة ← الوحدة ← ظهورها تلقائياً في الشاشة.
///
/// الأولوية: ملف خاص بالصنف (<see cref="ProductId"/> محدّد) ثم ملف المجموعة
/// (<see cref="GroupCode"/> فقط) ثم الأنواع النشطة بلا ملف (تظهر للجميع).
/// </summary>
public class ItemInspectionProfile : BaseEntity
{
    /// <summary>صنف محدد — أو فارغ ليصبح الملف عاماً للمجموعة.</summary>
    public int? ProductId { get; set; }

    /// <summary>مجموعة الأصناف (001/002/003/004) — تُستخدم عندما يكون <see cref="ProductId"/> فارغاً.</summary>
    public string GroupCode { get; set; }

    public int ResultTypeId { get; set; }

    /// <summary>الوحدة المعتمدة لهذا النوع مع هذا الصنف — تتجاوز وحدة النوع العامة.</summary>
    public int? UnitId { get; set; }

    /// <summary>§decimal لا double: كميات جديدة تُحسب بدقة عشرية (قاعدة CI: لا double جديداً).</summary>
    public decimal DefaultQty { get; set; }
    public bool IsMandatory { get; set; }
    public int SortNo { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// §نتيجة فحص فعلية مسجّلة في استمارة — صف واحد = نوع نتيجة + كمية + وحدتها.
/// هذا ما يجعل الشاشة ديناميكية: لا أعمدة ثابتة باسم «حشف» أو «نوى» في أي جدول.
/// </summary>
public class InspectionResult : BaseEntity
{
    public int CheckId { get; set; }

    /// <summary>المنتج/الصنف الذي تُنسب إليه النتيجة — يحفظ الربط بالأمر والعميل.</summary>
    public int? ProductId { get; set; }
    /// <summary>Unambiguous production line and frozen lot/package/customer identity.</summary>
    public int? OrderItemId { get; set; }
    public int? CustomerId { get; set; }
    public int? PackagingTypeId { get; set; }
    public int? LotId { get; set; }
    public int ResultTypeId { get; set; }

    /// <summary>§decimal: الكمية كما أدخلها المستخدم بلا انجراف عشري.</summary>
    public decimal Qty { get; set; }

    /// <summary>الوحدة المستخدمة فعلياً — يجب أن تكون معرفة في القاموس ومسموحة لهذا النوع.</summary>
    public int? UnitId { get; set; }

    /// <summary>نسخة اسم الوحدة لحظة التسجيل (توثيق — لا يعتمد عليها في الحساب).</summary>
    public string UnitLabel { get; set; }

    public string Notes { get; set; }
}

/// <summary>
/// §تحويل وحدات معرَّف — النظام لا يجمع كميات بوحدات مختلفة في إجمالي واحد
/// إلا عبر تحويل موجود هنا (مثال: كرتون → كجم = 7.5).
/// </summary>
public class UnitConversion : BaseEntity
{
    public int FromUnitId { get; set; }
    public int ToUnitId { get; set; }

    /// <summary>1 من «From» = Factor من «To». §decimal: معامل التحويل يجب أن يكون دقيقاً.</summary>
    public decimal Factor { get; set; }

    public bool IsActive { get; set; } = true;
}
