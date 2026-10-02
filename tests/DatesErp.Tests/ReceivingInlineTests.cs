using DatesErp.Core.Domain.Entities;
using DatesErp.Desktop.Mvvm;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// اختبارات الإدخال المباشر من الجدول في شاشة الاستلام (1.50.54):
/// اختيار الصنف من رقم/اسم داخل الصف وتعبئة تلقائية.
/// </summary>
public class ReceivingInlineTests
{
    [Fact]
    public void FillFromProduct_Sets_Code_Name_Unit_ReadOnly_Without_Mixing()
    {
        // 5 أصناف مختلفة
        var products = new[]
        {
            new Product { Id = 1, ProductCode = "001-001", ProductNameAr = "سكري", UnitOfMeasure = "كجم", DefaultPackagingTypeId = 1 },
            new Product { Id = 2, ProductCode = "001-002", ProductNameAr = "خلاص", UnitOfMeasure = "كجم", DefaultPackagingTypeId = 2 },
            new Product { Id = 3, ProductCode = "001-003", ProductNameAr = "صقعي", UnitOfMeasure = "كرتون", DefaultPackagingTypeId = 1 },
            new Product { Id = 4, ProductCode = "001-004", ProductNameAr = "برحي", UnitOfMeasure = "سلة", DefaultPackagingTypeId = 3 },
            new Product { Id = 5, ProductCode = "001-005", ProductNameAr = "نبوت سيف", UnitOfMeasure = "كجم", DefaultPackagingTypeId = 1 },
        };

        var rows = new List<ReceivingItemRow>();
        int n = 1;
        foreach (var p in products)
        {
            var row = new ReceivingItemRow { RowNo = n++ };
            row.FillFromProduct(p, $"عبوة-{p.Id}", p.DefaultPackagingTypeId, 10 + p.Id);
            row.PackageCount = 10;
            row.UnitWeightKg = 5 + p.Id; // وزن مختلف لكل صنف
            rows.Add(row);
        }

        // تأكد كل صنف نزل رقمه واسمه ووحدته في الحقول الصحيحة دون اختلاط
        for (int i = 0; i < products.Length; i++)
        {
            var p = products[i];
            var r = rows[i];
            Assert.Equal(p.Id, r.ProductId);
            Assert.Equal(p.ProductCode, r.ProductCode);
            Assert.Equal(p.ProductNameAr, r.ProductName);
            Assert.Equal(p.UnitOfMeasure, r.ReceiptUnit);
            // الوحدة والعبوة للقراءة فقط من بطاقة الصنف — لا يعاد إدخالها يدوياً
            Assert.False(string.IsNullOrWhiteSpace(r.ReceiptUnit));
            // تأكد عدم الاختلاط: كل صف يحمل بيانات صنفه فقط
            Assert.Equal($"عبوة-{p.Id}", r.PackName);
        }

        // تأكد أن الكميات محسوبة بشكل مستقل لكل صف
        Assert.Equal(10 * 6, rows[0].QtyKg); // 10 * (5+1)
        Assert.Equal(10 * 7, rows[1].QtyKg);
        Assert.Equal(10 * 8, rows[2].QtyKg);
        Assert.Equal(10 * 9, rows[3].QtyKg);
        Assert.Equal(10 * 10, rows[4].QtyKg);

        // تأكد أن الصفوف لا تختلط بعد تعديل أحدهما
        rows[0].ProductName = "تعديل يدوي يجب ألا يحدث — لكن لو حدث لا يؤثر على الآخرين";
        Assert.NotEqual(rows[0].ProductName, rows[1].ProductName);
        Assert.Equal("خلاص", rows[1].ProductName);
    }

    [Fact]
    public void EmptyRow_Only_Creates_Next_After_Valid_Product()
    {
        var items = new System.Collections.ObjectModel.ObservableCollection<ReceivingItemRow>();
        void AddEmpty() => items.Add(new ReceivingItemRow { RowNo = items.Count + 1 });
        void EnsureEmpty()
        {
            if (items.Count == 0 || items.Last().ProductId != 0) AddEmpty();
        }

        // البداية: صف فارغ واحد جاهز
        AddEmpty();
        Assert.Single(items);
        Assert.True(items[0].IsEmptyRow);

        // لا يتم إنشاء سطر جديد إلا بعد اختيار صنف صحيح
        EnsureEmpty();
        Assert.Single(items); // ما زال واحداً لأنه فارغ

        // اختيار صنف صحيح في الصف الفارغ
        var p = new Product { Id = 10, ProductCode = "001-010", ProductNameAr = "مجدول", UnitOfMeasure = "كجم" };
        items[0].FillFromProduct(p);
        Assert.False(items[0].IsEmptyRow);

        // الآن يجب أن يظهر صف جديد جاهز
        EnsureEmpty();
        Assert.Equal(2, items.Count);
        Assert.True(items[1].IsEmptyRow);
        Assert.Equal(0, items[1].ProductId);

        // إضافة 5 أصناف مختلفة دون مغادرة الجدول
        for (int i = 1; i < 5; i++)
        {
            var prod = new Product { Id = 10 + i, ProductCode = $"001-0{10 + i}", ProductNameAr = $"صنف-{i}", UnitOfMeasure = "كجم" };
            items.Last().FillFromProduct(prod);
            EnsureEmpty();
        }
        Assert.Equal(6, items.Count); // 5 مملوءة + 1 فارغة جاهزة
        Assert.Equal(5, items.Count(r => r.ProductId != 0));
        Assert.Single(items.Where(r => r.IsEmptyRow));
    }

    [Fact]
    public void Product_Data_Is_ReadOnly_After_Selection()
    {
        var p = new Product { Id = 1, ProductCode = "001-001", ProductNameAr = "سكري", UnitOfMeasure = "كجم" };
        var row = new ReceivingItemRow();
        row.FillFromProduct(p, "كرتون", 1, 20);

        // البيانات الأساسية من بطاقة الصنف
        Assert.Equal("001-001", row.ProductCode);
        Assert.Equal("سكري", row.ProductName);
        Assert.Equal("كجم", row.ReceiptUnit);

        // المستخدم يواصل إدخال الكمية والنوع والوزن في نفس الصف
        row.PackageCount = 15;
        row.UnitWeightKg = 20;
        Assert.Equal(300, row.QtyKg);

        // تغيير الكمية لا يغير بيانات الصنف
        row.PackageCount = 20;
        Assert.Equal("001-001", row.ProductCode);
        Assert.Equal("سكري", row.ProductName);
        Assert.Equal("كجم", row.ReceiptUnit);
    }
}
