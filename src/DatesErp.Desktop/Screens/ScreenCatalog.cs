namespace DatesErp.Desktop.Screens;

/// <summary>شاشة واحدة في النظام.</summary>
public record ScreenDef(string Code, string Group, string Title, string Module, string Icon = "▪", string ScreenCode = "MRPMAS1099");

/// <summary>مجموعة إدارات لوحة التحكم السبع (المعتمدة).</summary>
public record DeptGroup(string Id, string Title, string Icon, string Color);

/// <summary>§22 — كتالوج الشاشات منظماً حسب الإدارات السبع المعتمدة في النظام الأصلي.</summary>
public static class ScreenCatalog
{
    public static readonly List<DeptGroup> Departments = new()
    {
        new("system", "إدارة النظام", "⚙️", "#0F766E"),
        new("inventory", "المخازن والاستلام", "📦", "#15803D"),
        new("production", "الإنتاج", "🏭", "#1D4ED8"),
        new("quality", "الجودة", "✅", "#0284C7"),
        new("delivery", "التسليم والشحن", "🚚", "#D97706"),
        new("reports", "التقارير", "📈", "#7C3AED"),
        new("maintenance", "النسخ الاحتياطي والصيانة", "🛠️", "#BE123C")
    };

    public static readonly List<ScreenDef> All = new()
    {
        new("dashboard", "الرئيسية", "لوحة المؤشرات", "dashboard", "📊", "MRPDSH1000"),
        // §7 — «مهامي»: شاشة ما بعد الدخول. الموظف لا يبحث عن عمله.
        new("mytasks", "الرئيسية", "مهامي", "tasks", "🗂", "MRPWFT1000"),

        // ⚙️ إدارة النظام
        new("users", "إدارة النظام", "المستخدمون", "users", "👥", "MRPMAS1002"),
        new("permissions", "إدارة النظام", "الأدوار والصلاحيات", "users", "🔐", "MRPMAS1003"),
        new("matrix", "إدارة النظام", "مصفوفة الصلاحيات", "users", "🔐", "MRPMAS1004"),
        new("customers", "إدارة النظام", "العملاء", "customers", "👤", "MRPMAS1006"),
        new("suppliers", "إدارة النظام", "الموردون", "suppliers", "🚚", "MRPMAS1007"),
        new("employees", "إدارة النظام", "الموظفون وأرقام الدخول", "employees", "👥", "MRPMAS1008"),
        new("shifts", "إدارة النظام", "الورديات (الوقت المتاح)", "products", "⏰", "MRPMAS1005"),
        new("warehouses", "إدارة النظام", "المخازن وإضافاتها", "inventory", "🏬", "MRPINV1000"),
        new("machines", "إدارة النظام", "الأجهزة المتصلة", "settings", "💻", "MRPMAS1010"),
        new("whvars", "إدارة النظام", "متغيرات المخازن", "settings", "🗃️", "MRPSYS1003"),
        new("items", "إدارة النظام", "الأصناف", "products", "🏷️", "MRPMAS1001"),
        new("caps", "إدارة النظام", "طاقات الأصناف", "products", "⚡", "MRPMAS1011"),
        new("plan-closure", "الإنتاج", "إقفال خطة الإنتاج", "planning", "🔐", "MRPMPS1020"),

        // 📦 المخازن والاستلام
        new("receiving", "المخازن والاستلام", "الاستلام وسندات الاستلام", "receiving", "📥", "MRPREC1001"),
        new("lots", "المخازن والاستلام", "الدفعات وأرصدة الخام", "lots", "📊", "MRPINV1002"),
        // §المعالجة والتعقيم — بجوار الدفعات: هي دورة على الخام المستلم
        new("balances", "المخازن والاستلام", "أرصدة المخزون", "inventory", "⚖", "MRPINV1001"),
        new("movements", "المخازن والاستلام", "حركات المخزون", "inventory", "🔁", "MRPINV1003"),
        new("transfers", "المخازن والاستلام", "التحويلات المخزنية", "inventory", "↔", "MRPINV1007"),
        new("cartons", "المخازن والاستلام", "الكرتون الفارغ (عدّ/بيع)", "cartons", "📦", "MRPINV1004"),
        new("fgreceive", "المخازن والاستلام", "أوامر استلام الإنتاج", "finishedgoods", "📥", "MRPINV1006"),

        // 🏭 الإنتاج
        new("planning", "الإنتاج", "خطط الإنتاج (MPS)", "planning", "📋", "MRPMPS1001"),
        new("orders", "الإنتاج", "أوامر الإنتاج", "production", "📝", "MRPMPS1007"),
        new("aux-setup", "الإنتاج", "تهيئة الأصناف المساعدة", "auxiliary", "🧩", "MRPAUX1001"),
        new("product-bom", "الإنتاج", "مكونات الإنتاج (BOM)", "auxiliary", "📋", "MRPAUX1002"),
        new("aux-issue", "الإنتاج", "صرف الأصناف المساعدة", "auxiliary", "🧪", "MRPAUX1003"),
        new("customer-cartons", "الإنتاج", "كراتين العملاء (ماركات)", "auxiliary", "🏷️", "MRPAUX1004"),
        new("materials", "الإنتاج", "صرف المواد للأوامر (قديم)", "materials", "🧪", "MRPMPS1008"),
        new("proddelivery", "الإنتاج", "أوامر تسليم الإنتاج", "production", "📤", "MRPMPS1021"),

        // ✅ الجودة
        new("quality", "الجودة", "فحوصات الجودة", "quality", "🔍", "MRPQC1002"),
        new("wastage", "الجودة", "الهالك والأصناف الثانوية", "quality", "🗑", "MRPQC1003"),

        // 🚚 التسليم والشحن
        new("finishedgoods", "التسليم والشحن", "تسليم الإنتاج (مخزن التام)", "finishedgoods", "📦", "MRPMPS1015"),
        new("delivery", "التسليم والشحن", "تسليم العملاء", "delivery", "🚛", "MRPINV1005"),
        new("avail", "التسليم والشحن", "متاح العملاء", "delivery", "📦", "MRPDLV1002"),   // §B102.1 (إصلاح فحص): نقطة دخول ميزة B100

        // 📈 التقارير
        new("reports", "التقارير", "مركز التقارير الموحد", "reports", "📈", "MRPRPT1000"),
        new("audit", "التقارير", "سجل التدقيق", "settings", "📜", "MRPRPT1010"),
        new("shipment", "التقارير", "تقرير حركة شحنة العميل", "reports", "🚚", "MRPRPT1011"),   // §B102 (سحب B101)
        new("supmove", "التقارير", "تقرير حركة الموردين حسب الأصناف", "reports", "🧾", "MRPRPT1012"),   // §B103

        // 🛠️ النسخ الاحتياطي والصيانة
        new("backup", "النسخ الاحتياطي والصيانة", "النسخ الاحتياطي والاستعادة", "backup", "💾", "MRPSYS1001"),
        new("systeminfo", "النسخ الاحتياطي والصيانة", "معلومات النظام والإعدادات", "settings", "⚙️", "MRPSYS1002"),
        new("theme", "إدارة النظام", "تخصيص النظام والمظهر", "settings", "🎨", "MRPSYS1004")
    };

    public static IEnumerable<ScreenDef> OfDept(string deptId) => All.Where(s => s.Group == DeptTitle(deptId));

    public static string DeptTitle(string deptId) => Departments.FirstOrDefault(d => d.Id == deptId)?.Title ?? deptId;
}
