using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// اختبار قائمة الأصناف التامة في نافذة اختيار الأصناف — السبب الجذري لشكوى
/// «لا تظهر الأصناف الخاصة بالعميل في الشاشة المنبثقة»: الفلتر القديم كان يشترط
/// المجموعة 002 حرفياً، بينما في قواعد المصانع قد تُسجل الأصناف التامة بلا مجموعة.
/// الفلتر الجديد مطابق لفلتر v1.59: (المجموعة 002 أو بدون مجموعة) ونشط.
/// </summary>
public class FinishedProductsFilterTests
{
    [Fact]
    public void Filter_Includes_002_And_NoGroup_Active_Only()
    {
        using var host = new TestHost();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        // أصناف إضافية تحاكي واقع قاعدة المصنع
        db.Products.AddRange(
            new Product { ProductCode = "FG-NOGRP", ProductNameAr = "صنف تام بلا مجموعة", GroupCode = null, ItemType = "Finished" },
            new Product { ProductCode = "FG-EMPTY", ProductNameAr = "صنف تام بمجموعة فارغة", GroupCode = "", ItemType = "Finished" },
            new Product { ProductCode = "002-INACT", ProductNameAr = "صنف تام موقوف", GroupCode = "002", ItemType = "Finished", IsActive = false },
            new Product { ProductCode = "003-AUX", ProductNameAr = "مادة مساعدة", GroupCode = "003", ItemType = "Auxiliary" });
        db.SaveChanges();

        var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var list = svc.GetFinishedProducts();

        // يشمل: أصناف 002 النشطة + بلا مجموعة + المجموعة الفارغة
        Assert.Contains(list, p => p.ProductCode == "002-001");
        Assert.Contains(list, p => p.ProductCode == "002-002");
        Assert.Contains(list, p => p.ProductCode == "FG-NOGRP");
        Assert.Contains(list, p => p.ProductCode == "FG-EMPTY");

        // يستبعد: الموقوف والخام والثانوية والمساعدة
        Assert.DoesNotContain(list, p => p.ProductCode == "002-INACT");
        Assert.DoesNotContain(list, p => p.ProductCode == "003-AUX");
        Assert.DoesNotContain(list, p => p.ProductCode == "001-001");
        Assert.DoesNotContain(list, p => p.ProductCode == "004-001");

        // لا يمكن أن تعود القائمة فارغة مع وجود أصناف صالحة — نهاية مشكلة «القائمة الفارغة»
        Assert.NotEmpty(list);
    }

    [Fact]
    public void Filter_OnSeededDb_ReturnsFinishedGoods_ForLotEditor()
    {
        using var host = new TestHost();
        using var scope = host.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var list = svc.GetFinishedProducts();
        // قاعدة البذور فيها صنفان تامان (002-001 و002-002)
        Assert.True(list.Count >= 2);
    }
}
