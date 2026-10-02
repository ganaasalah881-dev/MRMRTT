using DatesErp.Application.Services;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §47 — الترقيم في الخادم لحظة الحفظ فقط: لا تُقترح الشاشات أي رقم مسبقاً،
/// فالمستخدمان اللذان يضيفان معاً لا يحصلان على الرقم نفسه.
/// الكود الفارغ يُولَّد فريداً في الخادم، والصريح يبقى كما هو.
/// </summary>
public class ServerSideNumberingTests
{
    [Fact]
    public void Blank_Codes_Are_Generated_Unique_At_Save()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var m = host.Get<MasterDataService>();

        // عميلان بكود فارغ (مستخدمان في اللحظة نفسها) → كودان مختلفان مولّدان في الخادم
        var c1 = m.SaveCustomer(null, "", "عميل بلا كود — أول", "جملة", "777", "-", true);
        var c2 = m.SaveCustomer(null, null, "عميل بلا كود — ثانٍ", "جملة", "777", "-", true);
        Assert.True(c1.Ok, c1.Message);
        Assert.True(c2.Ok, c2.Message);
        Assert.False(string.IsNullOrWhiteSpace(c1.DocumentNumber));
        Assert.False(string.IsNullOrWhiteSpace(c2.DocumentNumber));
        Assert.NotEqual(c1.DocumentNumber, c2.DocumentNumber);

        // مورد ومخزن وموظف — نفس القاعدة
        var s = m.SaveSupplier(null, "", "مورد بلا كود", "777", true);
        Assert.True(s.Ok, s.Message);
        Assert.False(string.IsNullOrWhiteSpace(s.DocumentNumber));

        var w = m.SaveWarehouse(null, "", "مخزن بلا كود", "Raw", true);
        Assert.True(w.Ok, w.Message);
        Assert.False(string.IsNullOrWhiteSpace(w.DocumentNumber));

        var e = m.SaveEmployee(null, "", "موظف بلا رقم", "عامل", "الإنتاج", "777", true);
        Assert.True(e.Ok, e.Message);
        Assert.StartsWith("EMP", e.DocumentNumber);
    }

    [Fact]
    public void Blank_Product_Code_Is_Generated_With_Group_Prefix()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var m = host.Get<MasterDataService>();

        var p1 = m.SaveProductFull(null, "", "صنف بلا رقم — أول", "001", "Raw", "كجم", 20, 0, 0, null);
        var p2 = m.SaveProductFull(null, "", "صنف بلا رقم — ثانٍ", "001", "Raw", "كجم", 20, 0, 0, null);
        Assert.True(p1.Ok, p1.Message);
        Assert.True(p2.Ok, p2.Message);
        Assert.StartsWith("001-", p1.DocumentNumber);
        Assert.StartsWith("001-", p2.DocumentNumber);
        Assert.NotEqual(p1.DocumentNumber, p2.DocumentNumber);

        // الكود الصريح يبقى كما هو (سلوك قائم لا يتغير)
        var p3 = m.SaveProductFull(null, "001-EXPLICIT", "صنف بكود صريح", "001", "Raw", "كجم", 20, 0, 0, null);
        Assert.True(p3.Ok, p3.Message);
        Assert.Equal("001-EXPLICIT", p3.DocumentNumber);
    }
}
