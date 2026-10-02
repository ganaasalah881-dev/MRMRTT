using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §قاعدة الوحدات الدائمة: الخام بالكيلو وينتهي عند الخطط · باقي المراحل بالكرتون
/// (والكيلو وزن مكافئ يُشتق من تعريف العبوة) · المخرجات الثانوية بالكيلو.
///
/// الجودة كانت المرحلة الوحيدة التي لا تربط الكرتون بالكيلو: تفحص كلاً منهما على
/// حدة (لا سالب، والمعادلة مفحوص = مقبول + مرفوض) لكن لا تتحقق أن الرقمين يصفان
/// نفس الكمية. ومحضر الفحص مصدر سقف التسليم، فيتسرب التناقض إلى التام وسند العميل.
/// </summary>
public class QualityUnitsRuleTests
{
    /// <summary>أمر منتَج فعلاً: 500 كجم = 67 كرتون (وزن الكرتون في البذر 7.5 كجم).</summary>
    private static (int orderId, int lot) ProducedOrder(TestHost host)
    {
        var db = host.Get<DatesErpDbContext>();
        FullWorkflowTests.SeedQuickOrder(host, db, out var oid, out var lot);
        var c = host.Get<IExecutionService>()
            .CloseProductionDay(oid, 500, 67, 0, 0, 0, false, new List<DowntimeDto>(), false);
        Assert.True(c.Ok, c.Message);
        return (oid, lot);
    }

    // ════════ الكرتون والكيلو يجب أن يصفا نفس الكمية ════════

    /// <summary>
    /// 67 كرتون مقبولة تساوي ~502 كجم. تسجيل 300 كجم معها تناقض صريح
    /// كان يُقبل بصمت قبل الإصلاح.
    /// </summary>
    [Fact]
    public void Check_Rejects_Cartons_Kg_Mismatch()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot) = ProducedOrder(host);

        var r = host.Get<IQualityService>().SaveCheck(oid, null, "2026-08-23", "نهائي",
            new List<QualityItemDto>
            {
                new() { ProductId = 3, LotId = lot,
                        CheckedQtyKg = 300, AcceptedQtyKg = 300, RejectedQtyKg = 0,
                        CheckedCartons = 67, AcceptedCartons = 67, RejectedCartons = 0 }
            });

        Assert.False(r.Ok);
        Assert.Contains("لا تطابق عدد الكراتين", r.Message);
    }

    /// <summary>والمتطابق يمرّ — الإصلاح لا يمنع الصحيح.</summary>
    [Fact]
    public void Check_Accepts_Consistent_Cartons_And_Kg()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot) = ProducedOrder(host);

        var r = host.Get<IQualityService>().SaveCheck(oid, null, "2026-08-23", "نهائي",
            new List<QualityItemDto>
            {
                new() { ProductId = 3, LotId = lot,
                        CheckedQtyKg = 500, AcceptedQtyKg = 500, RejectedQtyKg = 0,
                        CheckedCartons = 67, AcceptedCartons = 67, RejectedCartons = 0 }
            });
        Assert.True(r.Ok, r.Message);
    }

    /// <summary>التناقض في المرفوض وحده يُرصد أيضاً — لا يكفي فحص الإجمالي.</summary>
    [Fact]
    public void Rejected_Quantity_Mismatch_Is_Caught()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot) = ProducedOrder(host);

        var r = host.Get<IQualityService>().SaveCheck(oid, null, "2026-08-23", "نهائي",
            new List<QualityItemDto>
            {
                // المرفوض: 10 كراتين = ~75 كجم، لا 5 كجم
                new() { ProductId = 3, LotId = lot,
                        CheckedQtyKg = 432.5, AcceptedQtyKg = 427.5, RejectedQtyKg = 5,
                        CheckedCartons = 67, AcceptedCartons = 57, RejectedCartons = 10 }
            });

        Assert.False(r.Ok);
        Assert.Contains("المرفوض", r.Message);
    }

    // ════════ اشتقاق الوحدة الأساسية عند إدخال الكيلو وحده ════════

    /// <summary>
    /// إدخال بالكيلو فقط (توافقاً مع الإدخالات القديمة) يجب أن يشتق الكراتين،
    /// فلا يبقى محضر التام بلا وحدته الأساسية ولا يتعطل سقف الكرتون في التسليم.
    /// </summary>
    [Fact]
    public void Cartons_Are_Derived_When_Only_Kg_Entered()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot) = ProducedOrder(host);

        var r = host.Get<IQualityService>().SaveCheck(oid, null, "2026-08-23", "نهائي",
            new List<QualityItemDto>
            {
                new() { ProductId = 3, LotId = lot,
                        CheckedQtyKg = 500, AcceptedQtyKg = 450, RejectedQtyKg = 50 }
            });
        Assert.True(r.Ok, r.Message);

        var db = host.Get<DatesErpDbContext>();
        var item = db.QualityCheckItems.AsNoTracking().Single(i => i.CheckId == r.Id);
        Assert.True(item.AcceptedCartons > 0, "لم تُشتق الكراتين المقبولة من الكيلو");
        Assert.True(item.CheckedCartons > 0, "لم تُشتق الكراتين المفحوصة من الكيلو");
        // 450 كجم ÷ 7.5 = 60 كرتون
        Assert.Equal(60, item.AcceptedCartons, 1);
    }

    // ════════ الثانوية بالكيلو لا بالكرتون ════════

    /// <summary>المخرجات الثانوية تُسجَّل بالكيلو — ولا سالب.</summary>
    [Fact]
    public void ByProducts_Are_Recorded_In_Kg()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot) = ProducedOrder(host);
        var db = host.Get<DatesErpDbContext>();
        int bp = db.Products.AsNoTracking().Where(b => b.ItemType == "ByProduct").OrderBy(b => b.Id).Select(b => b.Id).First();

        var r = host.Get<IQualityService>().SaveCheck(oid, null, "2026-08-23", "نهائي",
            new List<QualityItemDto>
            {
                new() { ProductId = 3, LotId = lot,
                        CheckedQtyKg = 500, AcceptedQtyKg = 500, RejectedQtyKg = 0,
                        CheckedCartons = 67, AcceptedCartons = 67, RejectedCartons = 0 }
            },
            byProducts: new List<(int, double)> { (bp, 25.5) });
        Assert.True(r.Ok, r.Message);

        var rec = db.QualityByProductRecords.AsNoTracking().Single(x => x.CheckId == r.Id);
        Assert.Equal(25.5, rec.QtyKg, 2);
    }

    /// <summary>كمية ثانوية سالبة مرفوضة.</summary>
    [Fact]
    public void Negative_ByProduct_Is_Rejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot) = ProducedOrder(host);
        int bp = host.Get<DatesErpDbContext>().Products.AsNoTracking().Where(b => b.ItemType == "ByProduct")
            .OrderBy(b => b.Id).Select(b => b.Id).First();

        var r = host.Get<IQualityService>().SaveCheck(oid, null, "2026-08-23", "نهائي",
            new List<QualityItemDto>
            {
                new() { ProductId = 3, LotId = lot,
                        CheckedQtyKg = 500, AcceptedQtyKg = 500, RejectedQtyKg = 0,
                        CheckedCartons = 67, AcceptedCartons = 67, RejectedCartons = 0 }
            },
            byProducts: new List<(int, double)> { (bp, -5) });
        Assert.False(r.Ok);
    }

    // ════════ الخام لا يدخل الجودة ════════

    /// <summary>الخام (001) ينتهي عند الخطط — لا يُفحص كمنتج تام.</summary>
    [Fact]
    public void Raw_Product_Cannot_Be_Quality_Checked()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (oid, lot) = ProducedOrder(host);

        var r = host.Get<IQualityService>().SaveCheck(oid, null, "2026-08-23", "نهائي",
            new List<QualityItemDto>
            { new() { ProductId = 1, LotId = lot, CheckedQtyKg = 100, AcceptedQtyKg = 100 } });

        Assert.False(r.Ok);
    }
}
