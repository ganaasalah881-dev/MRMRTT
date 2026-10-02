using System.IO;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §v1.50.31 — إعادة تنظيم مركز الصلاحيات بالشكل الكلاسيكي المعتمد (نموذج ONYX):
/// يمين شجرة الوحدات للتصفية، وسط مصفوفة «الشاشات × العمليات» بمربعات اختيار مباشرة،
/// أسفل تبويبات (مستخدمو القاعدة / صلاحيات مجموعة المستخدم / صلاحيات مستخدم)
/// وشريط تدقيق (مدخل السجل / تاريخ الإدخال) — مع بقاء النموذج الهرمي
/// (دور ← استثناء مستخدم ثلاثي) وتأكيد الحساسة والحفظ بملخص وسجل التدقيق.
/// </summary>
public class PermissionsMatrixDesignTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Read(string rel) => File.ReadAllText(Path.Combine(RepoRoot(), rel));

    [Fact]
    public void Permissions_Screen_Uses_The_Classic_Matrix_Layout()
    {
        string xaml = Read("src/DatesErp.Desktop/Views/Screens/PermissionsView.xaml");
        string cs = Read("src/DatesErp.Desktop/Views/Screens/PermissionsView.xaml.cs");

        // يمين: شجرة الوحدات + بحث يصفي الشجرة والمصفوفة
        Assert.Contains("ResTree", xaml);
        Assert.Contains("وحدات النظام", xaml);
        Assert.Contains("TreeSearchBox", xaml);
        Assert.Contains("BuildTree", cs);
        Assert.Contains("_groupFilter", cs);

        // وسط: مصفوفة الشاشات × العمليات — أعمدة مربعات تُبنى من كتالوج العمليات
        Assert.Contains("MatrixGrid", xaml);
        Assert.Contains("BuildMatrixColumns", cs);
        Assert.Contains("Cells[", cs);                       // ربط خانة لكل عملية: Cells[{op}].On
        Assert.Contains("اسم الشاشة", cs);
        Assert.Contains("SelectAll_Click", cs);
        Assert.Contains("ClearAll_Click", cs);

        // أسفل: التبويبات الثلاثة
        Assert.Contains("مستخدمو القاعدة", xaml);
        Assert.Contains("صلاحيات مجموعة المستخدم", xaml);
        Assert.Contains("صلاحيات مستخدم", xaml);
        Assert.Contains("UsersAllGrid", xaml);
        Assert.Contains("RolesList", xaml);
        Assert.Contains("UsersList", xaml);

        // شريط التدقيق السفلي
        Assert.Contains("مدخل السجل", xaml);
        Assert.Contains("تاريخ الإدخال", xaml);
        Assert.Contains("AuditUserText", cs);
    }

    [Fact]
    public void User_Exceptions_Stay_TriState_Over_Role_Inheritance()
    {
        // استثناء المستخدم ثلاثي الحالة فوق ما يرثه من دوره: منح صريح / منع صريح / وراثة
        string cs = Read("src/DatesErp.Desktop/Views/Screens/PermissionsView.xaml.cs");
        Assert.Contains("Inherited", cs);
        Assert.Contains("Exc", cs);
        Assert.Contains("GetInheritedSet", cs);
        Assert.Contains("GetUserExceptions", cs);
        Assert.Contains("ResetToInherited_Click", cs);
        // الخانة تعود للوراثة حين يطابق الاستثناء المنح الموروث (لا تُراكم صفوف مطابقة)
        Assert.Contains("if (val == Inherited.Contains(op)) Exc.Remove(op);", cs);
    }

    [Fact]
    public void Sensitive_Operations_Still_Confirm_Before_Grant()
    {
        string cs = Read("src/DatesErp.Desktop/Views/Screens/PermissionsView.xaml.cs");
        // تأكيد المنح للحساسة داخل تغيير الخانة نفسه
        Assert.Contains("حساسة على الشاشة", cs);
        Assert.Contains("OnCellChanging", cs);
        // وملخص الحفظ يُبرز الحساسة
        Assert.Contains("صلاحية حساسة", cs);
        Assert.Contains("IsSensitive", cs);
    }

    [Fact]
    public void Save_Still_Writes_Deltas_Through_The_Service()
    {
        string cs = Read("src/DatesErp.Desktop/Views/Screens/PermissionsView.xaml.cs");
        Assert.Contains("SetRolePermission", cs);
        Assert.Contains("SetUserPermission", cs);
        Assert.Contains("ClearUserPermission", cs);
        Assert.Contains("لا تغييرات للحفظ", cs);
        // النسخ والمقارنة والسجل والتعطيل الآمن باقية
        Assert.Contains("CopyRolePermissions", cs);
        Assert.Contains("Compare_Click", cs);
        Assert.Contains("GetAudit", cs);
        Assert.Contains("DeactivateRole", cs);
        Assert.Contains("DeactivateUser", cs);
    }
}

/// <summary>§v1.50.33 — تصويبات المستخدم على شاشة الصلاحيات.</summary>
public class PermissionsHeaderDropdownTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string Read(string rel) => File.ReadAllText(Path.Combine(RepoRoot(), rel));

    [Fact]
    public void Target_Is_A_Header_Dropdown_Like_The_Reference_Image()
    {
        // «اختيار الموظف من قائمة منسدلة رأس الصفحة بنفس الصورة المرجعية»
        string xaml = Read("src/DatesErp.Desktop/Views/Screens/PermissionsView.xaml");
        string cs = Read("src/DatesErp.Desktop/Views/Screens/PermissionsView.xaml.cs");
        Assert.Contains("ModeBox", xaml);
        Assert.Contains("TargetBox", xaml);
        Assert.Contains("منح/سحب الصلاحية لـ:", xaml);
        Assert.Contains("FillTargets", cs);
        // العنونة كالصورة: «رقم X — الاسم»
        Assert.Contains("رقم {r.Id} — {r.RoleNameAr}", cs);
        Assert.Contains("رقم {u.Id} — {u.FullName}", cs);
    }

    [Fact]
    public void Matrix_Is_Disabled_Until_An_Explicit_Target_Is_Chosen()
    {
        // «قمت بالتأشير على المربعات دون اختيار موظف والنظام يقبل» — انتهى ذلك: معطلة بلا هدف، ولا هدف افتراضي صامت
        string xaml = Read("src/DatesErp.Desktop/Views/Screens/PermissionsView.xaml");
        string cs = Read("src/DatesErp.Desktop/Views/Screens/PermissionsView.xaml.cs");
        Assert.Contains("x:Name=\"MatrixGrid\" IsEnabled=\"False\"", xaml);
        Assert.Contains("SetNoTarget", cs);
        Assert.Contains("RequireTarget", cs);
        Assert.DoesNotContain("RolesList.SelectedIndex = 0", cs);   // لا اختيار افتراضي صامت للهدف
        Assert.DoesNotContain("TargetBox.SelectedIndex = 0", cs);
        Assert.Contains("اختر الموظف/الدور أولاً", cs);
    }

    [Fact]
    public void Units_Tree_Is_Wider_And_Larger()
    {
        // «مربع وحدات النظام صغير ويصعب استخدامه» — عرض 310 وخط أكبر
        string xaml = Read("src/DatesErp.Desktop/Views/Screens/PermissionsView.xaml");
        Assert.Contains("Width=\"310\"", xaml);
        Assert.Contains("TreeView x:Name=\"ResTree\" FontSize=\"13\"", xaml);
    }

    [Fact]
    public void Load_Failures_Show_Details_Inside_The_Screen()
    {
        // «تظهر رسالة خطأ — حدث خطأ أثناء تنفيذ العملية» — التفاصيل الآن داخل الشاشة نفسها
        string xaml = Read("src/DatesErp.Desktop/Views/Screens/PermissionsView.xaml");
        string cs = Read("src/DatesErp.Desktop/Views/Screens/PermissionsView.xaml.cs");
        Assert.Contains("ErrorBanner", xaml);
        Assert.Contains("ErrorText", xaml);
        Assert.Contains("ShowError", cs);
        // الأدوار تُجلب داخل الاستعلام الواحد (لا خصائص تنقل بعد إغلاق النطاق)
        Assert.Contains("RoleIds = u.UserRoles", cs);
    }
}
