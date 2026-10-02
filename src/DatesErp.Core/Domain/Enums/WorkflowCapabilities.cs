namespace DatesErp.Core.Domain.Enums;

/// <summary>
/// §2 — كتالوج قدرات دورة العمل: **المصدر الواحد للحقيقة**.
///
/// ### المبدأ الحاكم (توجيه المستخدم، غير قابل للتفاوض)
/// النظام **لا يعرف أي مسمى وظيفي ولا أي موظف**. التوجيه بالقدرة وحدها.
/// لا يوجد في هذا الملف — ولا في أي ملف آخر — سطر يقول «هذه القدرة لمدير الإنتاج».
/// من يملك القدرة يُقرَّر في **قاعدة البيانات** من شاشة الصلاحيات، بصفر تعديل برمجي.
///
/// ### لماذا لا نبني نموذج صلاحيات جديداً
/// النظام يملك النموذج الصحيح أصلاً: <c>PermissionResource × PermissionOperation</c>
/// ومعه صلاحيات الأدوار واستثناءات المستخدمين والتفويض الزمني. فالقدرة هنا ليست
/// كياناً جديداً، بل **اسم مقروء لزوج (مورد، عملية) قائم**. النتيجة: التفويض
/// (<c>Delegation</c>) واستثناءات المستخدم تعمل على المهام تلقائياً بلا سطر إضافي.
///
/// ### إضافة قدرة جديدة
/// سطر واحد في <see cref="All"/>. ويحرس اختبارٌ آليٌّ أن مورد كل قدرة وعمليتها
/// موجودان فعلاً في كتالوج الصلاحيات — فلا تُولد قدرة لا يستطيع أحد امتلاكها.
/// </summary>
public static class WorkflowCapabilities
{
    // ── التخطيط ──
    public const string PlanningCreate = "planning.create";
    public const string PlanningSubmit = "planning.submit";
    public const string PlanningApprove = "planning.approve";
    public const string PlanningReject = "planning.reject";
    public const string PlanningClose = "planning.close";

    // ── الإنتاج ──
    public const string ProductionOrderIssue = "production.order.issue";
    public const string ProductionExecute = "production.execute";
    public const string ProductionDayClose = "production.day.close";

    // ── الجودة ──
    public const string QualityInspect = "quality.inspect";
    public const string QualityApprove = "quality.approve";
    public const string QualityRelease = "quality.release";

    // ── المخازن والتسليم ──
    public const string WarehouseReceive = "warehouse.receive";
    public const string DeliveryIssue = "delivery.issue";

    // ── المهام نفسها ──
    public const string TasksViewAll = "tasks.view.all";
    public const string TasksReassign = "tasks.reassign";

    /// <summary>
    /// تعريف قدرة: اسمها المقروء + الزوج (مورد، عملية) الذي تُترجم إليه في نموذج الصلاحيات القائم.
    /// </summary>
    public readonly record struct CapabilityDef(string Code, string NameAr, string Resource, string Operation);

    /// <summary>
    /// كل قدرات الدورة. عمود «من يملكها» **غير موجود عمداً** — يعيش في قاعدة البيانات.
    ///
    /// ملاحظة على الترجمة: عدة قدرات قد تشترك في نفس الزوج (مورد، عملية) عندما لا
    /// يميّز نموذج الصلاحيات بينها بعد — مثل <c>planning.submit</c> و<c>planning.create</c>.
    /// هذا مقصود ومقبول: القدرة تبقى **اسم الدورة**، والفصل الدقيق يتم بإضافة عملية
    /// جديدة إلى كتالوج الصلاحيات لاحقاً بلا تغيير في محرك التوجيه.
    ///
    /// **الفوترة غير موجودة هنا** — قرار Q3: دورة نظام التمور تنتهي عند اعتماد سند
    /// تسليم العميل، والبيع والفوترة في نظام مالي مستقل.
    /// </summary>
    public static readonly CapabilityDef[] All =
    {
        new(PlanningCreate,        "إنشاء خطة إنتاج",          PermissionModules.Planning,   "Create"),
        new(PlanningSubmit,        "إرسال الخطة للاعتماد",      PermissionModules.Planning,   "Post"),
        new(PlanningApprove,       "اعتماد الخطة",             PermissionModules.Planning,   "Approve"),
        new(PlanningReject,        "رفض الخطة / إعادتها",       PermissionModules.Planning,   "Cancel"),
        new(PlanningClose,         "إقفال الخطة",              PermissionModules.Planning,   "Reopen"),

        new(ProductionOrderIssue,  "إصدار أمر التشغيل",         PermissionModules.Production, "Create"),
        new(ProductionExecute,     "تسجيل التنفيذ والتوقفات",    PermissionModules.Execution,  "Edit"),
        new(ProductionDayClose,    "إقفال يوم الإنتاج",         PermissionModules.Execution,  "Approve"),

        new(QualityInspect,        "إجراء الفحص",              PermissionModules.Quality,    "Create"),
        new(QualityApprove,        "اعتماد نتيجة الفحص",        PermissionModules.Quality,    "Approve"),
        new(QualityRelease,        "الإفراج عن محجوز",          PermissionModules.Quality,    "Reopen"),

        new(WarehouseReceive,      "استلام الإنتاج التام",       PermissionModules.FinishedGoods, "Create"),
        new(DeliveryIssue,         "إصدار سند تسليم العميل",     PermissionModules.Delivery,   "Approve"),

        // «tasks/View» = يرى مهامه هو (تُمنح للجميع). «tasks/ViewAll» = يرى مهام الجميع (إشراف).
        new(TasksViewAll,          "رؤية كل المهام (إشراف)",     PermissionModules.Tasks,      "ViewAll"),
        new(TasksReassign,         "إعادة توجيه مهمة",          PermissionModules.Tasks,      "Edit")
    };

    public static readonly string[] Codes = All.Select(c => c.Code).ToArray();

    /// <summary>ترجمة القدرة إلى الزوج (مورد، عملية). ترمي إن كانت القدرة غير معرّفة — لا صمت.</summary>
    public static CapabilityDef Resolve(string capability)
    {
        foreach (var c in All) if (c.Code == capability) return c;
        throw new ArgumentException($"قدرة غير معرّفة في الكتالوج: {capability}", nameof(capability));
    }

    public static bool IsDefined(string capability)
    {
        foreach (var c in All) if (c.Code == capability) return true;
        return false;
    }

    public static string NameOf(string capability)
    {
        foreach (var c in All) if (c.Code == capability) return c.NameAr;
        return capability;
    }
}
