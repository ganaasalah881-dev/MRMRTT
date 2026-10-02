namespace DatesErp.Core.Domain.Enums;

/// <summary>
/// §الإصلاح الأمني — المصدر الواحد للحقيقة لوحدات الصلاحيات.
///
/// كانت قائمة الوحدات مكرّرة يدوياً في ثلاثة مواضع مستقلة:
///   1) PermissionService.ResourceCatalog  — ما يُعرض في شاشة الصلاحيات
///   2) DbSeeder.modules                   — ما يُزرع فعلياً لكل دور
///   3) MainWindow.GatedModules            — ما يُفحص عند فتح الشاشة
///
/// فسقطت «products» و«cartons» و«employees» من (2) و(3) بينما هي مُطبَّقة في الخادم،
/// فصار أي مستخدم — ولو «مشاهدة» — يفتح «طاقات الأصناف» و«الكرتون» و«الموظفون وأرقام الدخول»
/// لأن البوابة لا تعرفها أصلاً. الحل: قائمة واحدة هنا، والثلاثة يقرأون منها.
///
/// إضافة وحدة جديدة = سطر واحد في <see cref="All"/> فقط — ويحرس الاختبارُ الآليُّ التطابقَ.
/// </summary>
public static class PermissionModules
{
    // ── البيانات الأساسية ──
    public const string Products = "products";
    public const string Customers = "customers";
    public const string Suppliers = "suppliers";

    // ── المخازن ──
    public const string Receiving = "receiving";
    public const string Lots = "lots";
    public const string Inventory = "inventory";
    public const string Materials = "materials";
    public const string Cartons = "cartons";

    // ── الإنتاج ──
    public const string Planning = "planning";
    public const string Production = "production";
    public const string ManualOrder = "manualorder";
    public const string Execution = "execution";

    // ── الجودة والتسليم ──
    public const string Quality = "quality";
    public const string FinishedGoods = "finishedgoods";
    public const string Delivery = "delivery";

    // ── الإدارة ──
    public const string Reports = "reports";
    public const string Users = "users";
    public const string Employees = "employees";
    public const string Permissions = "permissions";
    public const string Settings = "settings";
    public const string Backup = "backup";
    public const string Admin = "admin";

    /// <summary>§3 — «مهامي» ولوحة المهام: مورد قائم بذاته له عملياته.</summary>
    public const string Tasks = "tasks";

    /// <summary>
    /// §المعالجة والتعقيم — مورد مستقل بعملياته (Start / Release / Reject / View).
    /// **موجَّه بالقدرة لا بالمسمى الوظيفي**: من يملك القدرة ينفّذها أياً كان مسماه،
    /// وتُضاف الوظائف أو تُدمج من شاشة الصلاحيات بصفر تعديل برمجي.
    /// </summary>
    public const string Treatment = "treatment";

    /// <summary>لوحة المؤشرات — مفتوحة للجميع بعد الدخول، فلا تخضع للبوابة.</summary>
    public const string Dashboard = "dashboard";

    /// <summary>
    /// كل الوحدات الخاضعة للصلاحيات. الترتيب هو ترتيب العرض في شجرة شاشة الصلاحيات.
    /// «dashboard» ليست هنا عمداً — لا تُحجب.
    /// </summary>
    public static readonly (string Code, string NameAr, string GroupAr)[] All =
    {
        (Receiving, "الاستلام وسندات الاستلام", "المخازن"),
        (Inventory, "أرصدة المخزون والحركات", "المخازن"),
        (Cartons, "الكرتون الفارغ (تولّد/عدّ/بيع)", "المخازن"),
        (Lots, "الدفعات وأرصدة الخام", "المخازن"),
        (Treatment, "معالجة وتعقيم الخام", "المخازن"),
        (Materials, "المواد المساعدة", "المخازن"),

        (Products, "الأصناف والعبوات والطاقة", "البيانات الأساسية"),
        (Customers, "العملاء", "البيانات الأساسية"),
        (Suppliers, "الموردون", "البيانات الأساسية"),

        (Planning, "خطط الإنتاج (MPS)", "الإنتاج"),
        (Production, "أوامر الإنتاج", "الإنتاج"),
        (ManualOrder, "الأوامر اليدوية الاستثنائية (بلا خطة)", "الإنتاج"),
        (Execution, "التنفيذ والإقفال اليومي", "الإنتاج"),

        (Quality, "الفحص والجودة", "الجودة"),
        (FinishedGoods, "استلام الإنتاج التام", "الجودة"),
        (Delivery, "التسليم والفوترة", "التسليم"),

        (Reports, "مركز التقارير", "التقارير"),
        (Users, "إدارة المستخدمين", "الإدارة"),
        (Employees, "الموظفون وأرقام الدخول", "الإدارة"),
        (Permissions, "الأدوار والصلاحيات", "الإدارة"),
        (Settings, "الإعدادات والهوية", "الإدارة"),
        (Backup, "النسخ الاحتياطي والصيانة", "الإدارة"),
        (Admin, "إدارة النظام العامة", "الإدارة"),

        (Tasks, "المهام وسير العمل", "الإدارة")
    };

    /// <summary>أكواد الوحدات فقط — تستهلكها البذور وبوابة فتح الشاشات.</summary>
    public static readonly string[] Codes = All.Select(x => x.Code).ToArray();

    /// <summary>
    /// الوحدات التي تُفحص عند فتح الشاشة. «manualorder» و«admin» ليستا شاشتين
    /// (بوابتا عملية داخل شاشات أخرى) فلا معنى لفحصهما عند الفتح.
    /// </summary>
    public static readonly string[] ScreenGated =
        Codes.Where(c => c != ManualOrder && c != Admin).ToArray();
}
