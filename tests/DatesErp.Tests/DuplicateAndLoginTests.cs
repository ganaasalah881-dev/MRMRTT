using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// اختبارات: فحص الرقم المحجوز عند إضافة/تعديل موظف أو صنف أو عميل أو مستخدم،
/// والدخول بالرقم (رقم الدخول أو رقم الموظف) بدلاً من الاسم فقط،
/// وظهور رقم المستند تلقائياً عند الإنشاء.
/// </summary>
public class DuplicateAndLoginTests
{
    [Fact]
    public void DuplicateCodes_Rejected_With_Reserved_Message()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<Application.Services.MasterDataService>();

        // عميل جديد ثم محاولة بنفس الرقم
        Assert.True(svc.SaveCustomer(null, "C777", "عميل تجريبي", "جملة", null, null, true).Ok);
        var dup = svc.SaveCustomer(null, "C777", "عميل آخر", "تجزئة", null, null, true);
        Assert.False(dup.Ok);
        Assert.Contains("هذا الرقم محجوز", dup.Message);

        // التعديل إلى رقم مستخدم مسبقاً مرفوض أيضاً
        int cust2 = svc.SaveCustomer(null, "C888", "عميل ثانٍ", "جملة", null, null, true).Id;
        var editDup = svc.SaveCustomer(cust2, "C777", "عميل ثانٍ", "جملة", null, null, true);
        Assert.False(editDup.Ok);
        Assert.Contains("هذا الرقم محجوز", editDup.Message);

        // المورد والصنف والموظف والمخزن — نفس الفحص
        Assert.True(svc.SaveSupplier(null, "S777", "مورد", "777", true).Ok);
        Assert.Contains("هذا الرقم محجوز", svc.SaveSupplier(null, "S777", "مورد آخر", null, true).Message);

        Assert.True(svc.SaveEmployee(null, "EMP77", "موظف تجريبي", "محاسب", "المالية", null, true).Ok);
        var dupEmp = svc.SaveEmployee(null, "EMP77", "موظف آخر", "أمين مخزن", "المخازن", null, true);
        Assert.False(dupEmp.Ok);
        Assert.Contains("هذا الرقم محجوز", dupEmp.Message);

        // رسالة النجاح تُظهر الرقم
        var created = svc.SaveEmployee(null, "EMP78", "موظف جديد", "مدير", "الإدارة العامة", null, true);
        Assert.True(created.Ok);
        Assert.Contains("EMP78", created.Message);
    }

    [Fact]
    public void Login_ByUserNumber_And_ByEmployeeNumber_Works()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var admin = scope.ServiceProvider.GetRequiredService<IAdminService>();
        var session = scope.ServiceProvider.GetRequiredService<DatesErp.Infrastructure.Session.SessionContext>();

        // موظف مخصص جديد (لا يشاركه أي مستخدم آخر) + مستخدم برقم دخول صريح
        var masters = scope.ServiceProvider.GetRequiredService<Application.Services.MasterDataService>();
        var empR = masters.SaveEmployee(null, "EMP99", "موظف الدخول الرقمي", "محاسب", "المالية", null, true);
        Assert.True(empR.Ok, empR.Message);

        var r = admin.SaveUser(null, "555", "tester77", "مستخدم تجريبي", "Pass@123", new List<int>(), true);
        Assert.True(r.Ok, r.Message);
        Assert.Contains("555", r.Message); // رقم الدخول يظهر عند الإنشاء

        // رقم محجوز يرفض
        var dup = admin.SaveUser(null, "555", "tester78", "آخر", "Pass@123", new List<int>(), true);
        Assert.False(dup.Ok);
        Assert.Contains("هذا الرقم محجوز", dup.Message);

        // الدخول بالرقم
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        var byNumber = auth.Login("555", "Pass@123");
        Assert.True(byNumber.Success, byNumber.Message);

        // الدخول برقم الموظف المرتبط بهذا المستخدم
        var u = db.Users.FirstOrDefault(x => x.UserName == "tester77");
        u.EmployeeId = empR.Id;
        db.SaveChanges();
        var byEmp = auth.Login("EMP99", "Pass@123");
        Assert.True(byEmp.Success, byEmp.Message);

        // الاسم ما زال يعمل أيضاً
        var byName = auth.Login("tester77", "Pass@123");
        Assert.True(byName.Success, byName.Message);
    }

    [Fact]
    public void DocumentNumber_Appears_In_Success_Message()
    {
        var r = OpResult.Success("تم الحفظ.", 5, "PLN-0099");
        Assert.Contains("PLN-0099", r.Message);
        Assert.Contains("رقم المستند", r.Message);

        var noDoc = OpResult.Success("تم الحفظ.");
        Assert.DoesNotContain("رقم المستند", noDoc.Message);
    }
}
