using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §نظام الوحدات والمجموعات — المعيار الرسمي المفروض مركزياً (قاعدة بيانات + Backend + API + واجهات):
/// • 001 المواد الخام: الاستلام فقط — وحدة الاستلام مرنة (كرتون/سلة/كجم) والكمية القياسية للمخزون = كجم.
/// • 002 المنتجات التامة: الإنتاج والتسليم — الوحدة الأساسية كرتونة، والوزن المكافئ بالكيلو
///   يُحسب من تعريف العبوة/المنتج (عدد القوالب × وزن القالب أو وزن الكرتون) — لا وزن ثابت افتراضي.
/// • 003 المخرجات الثانوية: وحدتها من تعريف الصنف (كجم/كرتون/حبة...) — §لا وحدة مفروضة في الكود.
/// المجموعة مرتبطة بنوع العملية وليست رقماً شكلياً: أي عملية بصنف من مجموعة غير مسموحة تُرفض هنا.
/// </summary>
public static class UnitsPolicy
{
    public const string GroupRaw = "001";
    public const string GroupFinished = "002";
    public const string GroupByProduct = "003";
    /// <summary>§كرتون/وعاء مرتجع — أُضيفت لاحقاً وكانت مجهولة لدى هذا المعيار.</summary>
    public const string GroupPack = "004";

    /// <summary>نوع الصنف المسموح لكل مجموعة — المرجع المركزي للتصنيف.</summary>
    public static string ItemTypeOfGroup(string groupCode) => groupCode switch
    {
        GroupRaw => "Raw",
        GroupFinished => "Finished",
        GroupByProduct => "ByProduct",
        // §إصلاح: كانت 004 تُرجع null رغم أن SaveProductFull يعتمدها نوعاً رابعاً
        GroupPack => "Pack",
        _ => null
    };

    // ═══════════════════════════════════════════════════════════════════════
    // §قاعدة الوحدات الإلزامية على مستوى النظام — مصدر واحد للحقيقة
    //
    //   المواد الداخلة / الخام (001)      = KG
    //   الإنتاج التام (002)                = CARTON (بوزن كرتون من تعريف المنتج/العبوة)
    //   المخرجات الثانوية (003)            = KG
    //
    // لا يجوز لصنف أن يحمل وحدة تخالف نوعه. والمخرج الثانوي الذي يُراد عدّه
    // بالكرتون يُعرَّف منتجاً تاماً (002) فيخضع لقاعدة الإنتاج التام.
    //
    // §تاريخ: في B33 رُفع الإلزام استجابةً لطلب «لا تفرض الوحدات داخل الكود»،
    // فأصبح أي صنف يقبل أي وحدة. القاعدة المعتمدة لاحقاً نسخت ذلك وأعادته —
    // وهذا الموضع هو المكان الوحيد الذي يُقرر فيه الأمر، لا الشاشات.
    // ═══════════════════════════════════════════════════════════════════════

    public const string UnitKg = "كجم";
    public const string UnitCarton = "كرتون";

    /// <summary>الوحدة الإلزامية لنوع الصنف — null تعني «بلا إلزام» (كرتون/وعاء مرتجع).</summary>
    public static string MandatedUnitFor(string itemType) => itemType switch
    {
        "Raw" => UnitKg,
        "Finished" => UnitCarton,
        "ByProduct" => UnitKg,
        _ => null
    };

    /// <summary>اسم النوع بالعربية لرسائل الرفض.</summary>
    public static string ItemTypeNameAr(string itemType) => itemType switch
    {
        "Raw" => "مادة خام (001)",
        "Finished" => "منتج تام (002)",
        "ByProduct" => "مخرج ثانوي (003)",
        "Pack" => "كرتون/وعاء مرتجع (004)",
        _ => itemType ?? "—"
    };

    /// <summary>
    /// §الوحدة الافتراضية للمجموعة — تيسير عند الفراغ فقط، لا إلزام.
    /// القاعدة المعتمدة: لا تُفرض الوحدات داخل الكود؛ الوحدة تأتي من تعريف الصنف
    /// في شاشة الأصناف المركزية، ومجموعة الصنف وتصنيفه هما ما يحدد نوعه لا اسم الوحدة.
    /// </summary>
    public static string DefaultUnitFor(string itemType) => MandatedUnitFor(itemType);

    /// <summary>
    /// §لا وزن كرتون ثابت: إن سُوّيت كراتين بلا وزن كرتون معرَّف للصنف/العبوة فالعملية مرفوضة.
    /// كان النظام يفترض 7.5 كجم بصمت — وهذا وزن ثابت مقنّع ترفضه القاعدة.
    /// </summary>
    public static void RequireCartonWeight(DatesErpDbContext db, int productId, int? packagingTypeId,
        int cartons, string contextAr)
    {
        if (cartons <= 0) return;
        double w = CartonWeight(db, productId, packagingTypeId);
        if (w > 0) return;
        string name = db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => p.ProductNameAr).FirstOrDefault() ?? $"#{productId}";
        throw new DomainException(
            $"⛔ {contextAr}: سُجّلت {cartons:N0} كرتون للصنف «{name}» بلا وزن كرتون معرَّف.\n" +
            "عرّف وزن الكرتون (أو عدد القوالب × وزن القالب) في بطاقة الصنف أو العبوة.\n" +
            "النظام لا يفترض وزناً ثابتاً للكرتون — فأي حساب سيكون تخميناً.",
            "CARTON_WEIGHT_UNDEFINED");
    }

    /// <summary>§منع الخلط بين المجموعات: يفرض أن الصنف من النوع/المجموعة المطلوبة للعملية.</summary>
    public static void RequireItemType(DatesErpDbContext db, int productId, string requiredItemType, string operationAr)
    {
        var product = db.Products.AsNoTracking().FirstOrDefault(p => p.Id == productId)
                      ?? throw new DomainException("الصنف غير موجود في بطاقة الأصناف.");
        if (string.Equals(product.ItemType, requiredItemType, StringComparison.OrdinalIgnoreCase)) return;

        string typeAr = product.ItemType switch { "Raw" => "مادة خام (001)", "Finished" => "منتج تام (002)", "ByProduct" => "مخرج ثانوي (003)", "Pack" => "كرتون/وعاء مرتجع (004)", _ => product.ItemType };
        string requiredAr = requiredItemType switch { "Raw" => "المواد الخام (001)", "Finished" => "المنتجات التامة (002)", "ByProduct" => "المخرجات الثانوية (003)", "Pack" => "الكراتين والأوعية المرتجعة (004)", _ => requiredItemType };
        throw new DomainException(
            $"⛔ {operationAr}: الصنف «{product.ProductNameAr}» من {typeAr} — وغير مسموح هنا.\n" +
            $"هذه العملية تقبل فقط أصناف {requiredAr}.\n" +
            $"المجموعات معيار ملزم: 001 للاستلام الخام | 002 للإنتاج التام والتسليم | 003 للمخرجات الثانوية (كجم).",
            "WRONG_GROUP");
    }

    /// <summary>
    /// §وزن الكرتون المعتمد لصنف/عبوة: العبوة المحددة أولاً، ثم وزن كرتون المنتج،
    /// ثم عدد القوالب × وزن القالب (مثال: 5 قوالب × 2 كجم = 10 كجم/كرتون).
    /// </summary>
    public static double CartonWeight(DatesErpDbContext db, int productId, int? packagingTypeId)
    {
        if (packagingTypeId != null)
        {
            var packW = db.PackagingTypes.AsNoTracking().Where(p => p.Id == packagingTypeId).Select(p => p.UnitWeightKg).FirstOrDefault();
            if (packW > 0) return packW;
        }
        var prod = db.Products.AsNoTracking().FirstOrDefault(p => p.Id == productId);
        if (prod == null) return 0;
        if (prod.CartonWeightKg > 0) return prod.CartonWeightKg;
        if (prod.MoldsCount > 0 && prod.MoldWeightKg > 0) return prod.MoldsCount * prod.MoldWeightKg;
        return 0;
    }

    /// <summary>
    /// §حفظ تعريف التعبئة وقت العملية — بالأولوية نفسها المتبعة في وزن الكرتون
    /// (العبوة المحددة أولاً ثم بطاقة الصنف). يُستدعى عند إنشاء بند أمر أو إقفال
    /// حتى لا يؤدي تعديل تعريف المنتج لاحقاً إلى تغيير نتائج قديمة.
    /// </summary>
    public static (int MoldsCount, double MoldWeightKg) PackagingDefinition(
        DatesErpDbContext db, int productId, int? packagingTypeId)
    {
        if (packagingTypeId != null)
        {
            var pack = db.PackagingTypes.AsNoTracking().FirstOrDefault(p => p.Id == packagingTypeId);
            if (pack != null && pack.MoldsCount > 0 && pack.MoldWeightKg > 0)
                return (pack.MoldsCount, pack.MoldWeightKg);
        }
        var prod = db.Products.AsNoTracking().FirstOrDefault(p => p.Id == productId);
        return (prod?.MoldsCount ?? 0, prod?.MoldWeightKg ?? 0);
    }

    /// <summary>
    /// §القاعدة 5 — الإنتاج الأساسي كرتونة والوزن المكافئ يُحسب من وزن الكرتون:
    /// إن أُعطيت الكراتين بلا وزن ← الكيلو = كراتين × وزن الكرتون.
    /// When both values are supplied, reject material mismatches instead of silently
    /// rewriting the user's weight or accepting a corrupt approved plan snapshot.
    /// A small tolerance remains for rounding when the measured weight is real.
    /// </summary>
    public static double EnsureCartonKgConsistency(DatesErpDbContext db, int productId, int? packagingTypeId,
        double qtyKg, int cartons, string contextAr)
    {
        if (cartons <= 0) return qtyKg;
        double weight = CartonWeight(db, productId, packagingTypeId);
        if (weight <= 0) return qtyKg; // لا تعريف وزن بعد — لا حساب

        double computed = Math.Round(cartons * weight, 1);
        if (qtyKg <= 0) return computed;

        double tolerance = Math.Max(1.0, qtyKg * 0.02);
        if (Math.Abs(qtyKg - computed) > tolerance)
            throw new DomainException($"{contextAr}: لا تطابق عدد الكراتين مع كمية الكيلو " +
                $"({cartons} كرتون × {weight:N3} كجم = {computed:N3} كجم؛ المدخل {qtyKg:N3} كجم). " +
                "صحح الكمية أو تعريف وزن الكرتون قبل الحفظ.", "CARTON_KG_MISMATCH");
        return qtyKg;
    }

    /// <summary>
    /// §قاعدة الكرتون عند التسليم: الكراتين هي الوحدة الأساسية والكجم وزن مكافئ يُشتق منها.
    /// يُستخدم حيث الكراتين هي المُدخَل الموثوق (تسليم العميل) — فيُشتق الكجم بدل تصالب مدخلين.
    /// </summary>
    public static double DeriveKgFromCartons(DatesErpDbContext db, int productId, int? packagingTypeId,
        double qtyKg, int cartons)
    {
        if (cartons <= 0) return qtyKg;
        double weight = CartonWeight(db, productId, packagingTypeId);
        return weight > 0 ? Math.Round(cartons * weight, 1) : qtyKg;
    }

    // ═══════════════ §أمر وحدة القياس الرسمية — ثوابت العرض المركزية ═══════════════
    public const string RawReferenceUnit = UnitKg;
    public const string FinishedOfficialUnit = UnitCarton;
    public const string ByProductOfficialUnit = UnitKg;
    public const string RawQtyHeader = "الكمية (كجم)";
    public const string FinishedQtyHeader = "الكمية (كرتون)";
    public const string FinishedWeightHeader = "الوزن (كجم)";
    public const string BalanceCtnHeader = "الرصيد (كرتون)";
    public const string BalanceKgHeader = "الرصيد (كجم)";
    public const string PackagesHeader = "العبوات";

    /// <summary>تحويل أي وحدة استلام إلى الكجم المرجعي: العدد × وزن الوحدة.</summary>
    public static double KgOf(double packageCount, double unitWeightKg) => packageCount * unitWeightKg;
    /// <summary>المكافئ بالكرتون من الكجم وفق وزن عبوة المنتج الفعلي (لا قيمة ثابتة).</summary>
    public static double CartonsOf(double kg, double cartonWeightKg) => cartonWeightKg > 0 ? kg / cartonWeightKg : 0;
    /// <summary>الوزن الكلي بالكرتون: عدد الكراتين × وزن العبوة الفعلي.</summary>
    public static double KgOfCartons(double cartons, double cartonWeightKg) => cartons * cartonWeightKg;
}
