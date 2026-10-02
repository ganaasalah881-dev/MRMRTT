namespace DatesErp.Core.Common;

/// <summary>
/// ═══════════════════════════ §قاعدة وحدات التقرير (قاعدة ثابتة) ═══════════════════════════
///
/// <b>القاعدة المعتمدة من صاحب النظام:</b>
/// 1) بعد خطط الإنتاج تكون <b>وحدة التداول الرئيسية هي الكرتون (الكرتون التام)</b> — وهو ما يهم العميل.
///    فكل صف يخص منتجاً تاماً يجب أن يذكر صراحةً: <b>اسم الصنف + عدد الكراتين + الوزن (كجم)</b>.
/// 2) كل <b>دخول إلى المخازن</b> يجب أن يذكر: <b>كم دخل + بوزن كم</b> (الكمية والوزن معاً).
/// 3) صف التقرير يجب أن يكون <b>مرتباً</b>: الوحدة ثم الكمية ثم الوزن، بترتيب ثابت لا يتغير بين التقارير.
///
/// كل تقرير يعرض منتجاً تاماً أو دخولاً مخزنياً <b>يستخدم هذه الدوال حصراً</b> — ممنوع صياغة
/// يدوية مختلفة، تماماً كما تُلزم <see cref="UiFormat"/> كل الشاشات بتنسيق واحد للأرقام والتواريخ.
/// </summary>
public static class ReportUnits
{
    /// <summary>الوحدة الرئيسية للتداول بعد خطط الإنتاج (الكرتون التام) — معرّفة في مكان واحد.</summary>
    public const string PrimaryTradeUnitAr = "كرتون";

    /// <summary>
    /// القاعدة 1: «عدد الكراتين بوزن كم» — صيغة موحدة لوزن المنتج التام.
    /// مثال: <c>272 كرتون × 25.0 كجم = 6,800.0 كجم</c>
    /// </summary>
    public static string CartonWithWeight(long cartons, double unitWeightKg, double totalKg)
        => $"{UiFormat.N(cartons)} {PrimaryTradeUnitAr} × {UiFormat.N(unitWeightKg)} كجم = {UiFormat.N(totalKg)} كجم";

    /// <summary>
    /// القاعدة 1 (بدون وزن الوحدة عند عدم توفره): «عدد الكراتين (الوزن كجم)».
    /// مثال: <c>272 كرتون (6,800.0 كجم)</c>
    /// </summary>
    public static string CartonWithWeight(long cartons, double totalKg)
        => $"{UiFormat.N(cartons)} {PrimaryTradeUnitAr} ({UiFormat.N(totalKg)} كجم)";

    /// <summary>
    /// القاعدة 1: سطر المنتج التام كاملاً مرتّباً — <b>اسم الصنف ← الكراتين بوزنها</b>.
    /// مثال: <c>تمر فاخر — 272 كرتون × 25.0 كجم = 6,800.0 كجم</c>
    /// </summary>
    public static string FinishedLine(string productName, long cartons, double unitWeightKg, double totalKg)
        => $"{productName} — {CartonWithWeight(cartons, unitWeightKg, totalKg)}";

    /// <summary>القاعدة 1: سطر المنتج التام عند عدم توفر وزن الوحدة.</summary>
    public static string FinishedLine(string productName, long cartons, double totalKg)
        => $"{productName} — {CartonWithWeight(cartons, totalKg)}";

    /// <summary>
    /// القاعدة 2: دخول المخازن — «كم دخل بوزن كم». يُستعمل لكل حركة إدخال مخزني
    /// (خرج تام، استلام شحنة، مردود، تسوية موجبة).
    /// مثال: <c>دخل 6,800.0 كجم (272 كرتون)</c>
    /// </summary>
    public static string WarehouseEntry(double kg, long cartons)
        => $"دخل {UiFormat.N(kg)} كجم ({UiFormat.N(cartons)} {PrimaryTradeUnitAr})";

    /// <summary>
    /// القاعدة 2: دخول المخازن بوحدة صريحة (قبل خطط الإنتاج قد تكون الوحدة سلة أو كيساً لا كرتوناً).
    /// مثال: <c>دخل 20,000.0 كجم (800 سلة)</c>
    /// </summary>
    public static string WarehouseEntry(double kg, long units, string unitAr)
        => $"دخل {UiFormat.N(kg)} كجم ({UiFormat.N(units)} {unitAr})";

    /// <summary>القاعدة 2: دخول المخازن دون عدد وحدات (مواد مساعدة/خام بلا عبوات).</summary>
    public static string WarehouseEntry(double kg)
        => $"دخل {UiFormat.N(kg)} كجم";

    /// <summary>
    /// القاعدة 2: رصيد المخزن — «كم باقٍ بوزن كم» بصيغة مطابقة لدخول المخازن.
    /// مثال: <c>رصيد 1,000.0 كجم (40 كرتون)</c>
    /// </summary>
    public static string WarehouseStock(double kg, long cartons)
        => $"رصيد {UiFormat.N(kg)} كجم ({UiFormat.N(cartons)} {PrimaryTradeUnitAr})";

    /// <summary>
    /// القاعدة 1: كمية بأسلوب العميل — «الكرتون» أولاً ثم الوزن بين قوسين.
    /// تُستعمل في أعمدة الكميات عندما يكون الكرتون هو وحدة التداول.
    /// مثال: <c>272 كرتون · 6,800.0 كجم</c>
    /// </summary>
    public static string CartonThenWeight(long cartons, double kg)
        => $"{UiFormat.N(cartons)} {PrimaryTradeUnitAr} · {UiFormat.N(kg)} كجم";

    /// <summary>
    /// القاعدة 3: صف التقرير المرتّب — يجمع الوحدة والعدد والوزن بترتيب ثابت:
    /// الكمية ← الوحدة ← الوزن المكافئ، مع حذف الأجزاء الغائبة (لا «× 0» ولا فراغات مزدوجة).
    /// </summary>
    public static string TidyLine(params string[] parts)
        => string.Join(" · ", parts.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
}
