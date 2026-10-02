using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// اختبارات إصلاح الترقيم — كانت أرقام المستندات تتكرر (خطأ UNIQUE) لأن خدمة الترقيم
/// كانت Singleton فلا تُحفظ زيادة التسلسل أبداً. الآن Scoped + حلقة ضمان عدم التكرار.
/// </summary>
public class NumberingTests
{
    private sealed class FailedPostingProbe : ServiceBase
    {
        public FailedPostingProbe(TestHost h) : base(h.Get<DatesErpDbContext>(),
            h.Get<ICurrentSession>(), h.Get<INumberingService>()) { }
        public OpResult PostThenFail() => RunOp(() =>
        {
            Numbering.Next("SHIP");
            Db.Warehouses.Add(new Warehouse { WarehouseCode = "FAILED-POST", WarehouseNameAr = "لا يجب الحفظ" });
            Db.SaveChanges();
            return OpResult.Fail("لم تنجح العملية");
        });
    }

    [Fact]
    public void Failed_OpResult_Rolls_Back_Even_After_Explicit_SaveChanges_And_Number_Reservation()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var probe = new FailedPostingProbe(host);
        Assert.False(probe.PostThenFail().Ok);
        var db = host.Get<DatesErpDbContext>();
        Assert.DoesNotContain(db.Warehouses, w => w.WarehouseCode == "FAILED-POST");
        Assert.Equal(0, db.NumberingSchemes.Single(s => s.SchemeCode == "SHIP").LastSequence);
        Assert.Equal("1", host.Get<INumberingService>().Next("SHIP"));
    }

    [Fact]
    public void Treatment_And_Plan_Template_Numbering_Check_Their_Own_Unique_Columns()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var numbering = host.Get<INumberingService>();
        var ship = host.Get<IReceivingService>().SaveShipment(1, "2026-08-10", "2026-08-10",
            new List<ShipmentItemDto> { new() { ProductId = 1, TreatmentRequired = false,
                PackageCount = 1, UnitWeightKg = 20, QtyKg = 20 } });
        Assert.True(ship.Ok, ship.Message);
        Assert.True(host.Get<IReceivingService>().ApproveShipment(ship.Id).Ok);
        int lotId = db.Lots.Single().Id;
        var trt = db.NumberingSchemes.Single(n => n.SchemeCode == "TRT");
        trt.LastSequence = 0;
        db.RawTreatments.Add(new RawTreatment { TreatmentNo = "7", LotId = lotId,
            ProductId = 1, QtyKg = 1, StartedAt = DateTime.UtcNow, DurationHours = 1 });
        db.SaveChanges();
        Assert.Equal("8", numbering.Next("TRT"));
        db.SaveChanges();
        Assert.Equal("9", numbering.Next("TRT"));
        var planNo = numbering.Next("PLAN");
        db.ProductionPlans.Add(new ProductionPlan { DocumentNumber = planNo, PlanTitle = "قالب اختبار", PlanType = "Template" });
        db.SaveChanges();
        Assert.Equal("2", numbering.Next("PLN")); // legacy alias checks same ProductionPlans.DocumentNumber column
    }

    [Fact]
    public void Next_ProducesUniqueSequentialNumbers_AndPersists()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var numbering = scope.ServiceProvider.GetRequiredService<INumberingService>();

        var n1 = numbering.Next("SHIP"); db.SaveChanges();
        var n2 = numbering.Next("SHIP"); db.SaveChanges();
        var n3 = numbering.Next("SHIP"); db.SaveChanges();

        Assert.NotEqual(n1, n2);
        Assert.NotEqual(n2, n3);
        Assert.NotEqual(n1, n3);
        // §1.50.53 — التسلسل النقي 1،2،3…
        Assert.True(int.TryParse(n1, out _), $"يجب أن يكون نقياً: {n1}");
        Assert.True(int.TryParse(n2, out _), $"يجب أن يكون نقياً: {n2}");
        Assert.Equal("1", n1);
        Assert.Equal("2", n2);
        Assert.Equal("3", n3);

        // التسلسل محفوظ — خدمة جديدة في نفس النطاق تقرأ القيمة المحدثة
        var seq = db.NumberingSchemes.First(s => s.SchemeCode == "SHIP").LastSequence;
        Assert.True(seq >= 3, $"التسلسل يجب أن يُحفظ (كان {seq})");
    }

    [Fact]
    public void Peek_Shows_Next_Without_Reserving_And_Next_Matches_Peek()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var numbering = scope.ServiceProvider.GetRequiredService<INumberingService>();

        var peek1 = numbering.Peek("PLAN");
        Assert.Equal("1", peek1);
        // Peek لا يحجز — استدعاء ثانٍ يعطي نفس الرقم
        var peek2 = numbering.Peek("PLAN");
        Assert.Equal(peek1, peek2);

        var next = numbering.Next("PLAN"); db.SaveChanges();
        Assert.Equal(peek1, next);

        var peek3 = numbering.Peek("PLAN");
        Assert.Equal("2", peek3);
    }

    [Fact]
    public void Next_SelfCorrects_When_Sequence_Is_Stale_Behind_Existing_Data()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var numbering = scope.ServiceProvider.GetRequiredService<INumberingService>();

        // محاكاة قاعدة غير متزامنة: مستندات نقية موجودة حتى 7 بينما المخطط ما زال عند 0
        var cust = db.Customers.First();
        for (int i = 1; i <= 7; i++)
        {
            db.Shipments.Add(new Shipment
            {
                DocumentNumber = i.ToString(),
                CustomerId = cust.Id,
                TotalWeightKg = 100
            });
        }
        db.SaveChanges();
        var scheme = db.NumberingSchemes.First(s => s.SchemeCode == "SHIP");
        scheme.LastSequence = 0; // تسلسل متأخر (الخلل القديم)
        db.SaveChanges();

        // الرقم التالي يجب ألا يكرر أي رقم موجود — يقفز فوقها تلقائياً
        var next = numbering.Next("SHIP");
        Assert.True(int.TryParse(next, out var seq), $"الرقم الجديد يجب أن يكون نقياً 1،2،3… حصل على {next}");
        Assert.True(seq >= 8, $"الرقم يجب أن يتجاوز الموجود (حصل على {next})");

        // ولا يصطدم عند الحفظ الفعلي
        db.Shipments.Add(new Shipment { DocumentNumber = next, CustomerId = cust.Id, TotalWeightKg = 50 });
        db.SaveChanges(); // يجب ألا يرمي UNIQUE
    }

    [Fact]
    public void Next_CreatesMissingScheme_SelfHealing()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var numbering = scope.ServiceProvider.GetRequiredService<INumberingService>();

        // مخطط غير موجود إطلاقاً — يُنشأ ذاتياً بدل الانهيار
        Assert.Null(db.NumberingSchemes.FirstOrDefault(s => s.SchemeCode == "NEWSCHEME"));
        var n1 = numbering.Next("NEWSCHEME"); db.SaveChanges();
        var n2 = numbering.Next("NEWSCHEME"); db.SaveChanges();
        Assert.NotEqual(n1, n2);
        Assert.NotNull(db.NumberingSchemes.FirstOrDefault(s => s.SchemeCode == "NEWSCHEME"));
    }

    [Fact]
    public void ApproveShipment_CreatesUniqueLotsAndTransactions_NoDuplicateErrors()
    {
        // سيناريو المستخدم حرفياً: حفظ سندات واعتمادها بلا أخطاء UNIQUE
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var receiving = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var cust = db.Customers.First();

        for (int s = 0; s < 3; s++)
        {
            var prod = db.Products.First(p => p.GroupCode == "001");
            var save = receiving.SaveShipment(cust.Id, "2026-08-25", "2026-08-25",
                new List<ShipmentItemDto>
                {
                    new() { TreatmentRequired = false, ProductId = prod.Id, PackageCount = 10, UnitWeightKg = 20, QtyKg = 200 }
                });
            Assert.True(save.Ok, save.Message);
            var approve = receiving.ApproveShipment(save.Id);
            Assert.True(approve.Ok, approve.Message);
        }

        // كل أرقام المستندات والدفعات والحركات فريدة
        var docNos = db.Shipments.Select(x => x.DocumentNumber).ToList();
        Assert.Equal(docNos.Count, docNos.Distinct().Count());
        var lotCodes = db.Lots.Select(x => x.LotCode).ToList();
        Assert.Equal(lotCodes.Count, lotCodes.Distinct().Count());
        var txnNos = db.InventoryTransactions.Select(x => x.TxnNumber).ToList();
        Assert.Equal(txnNos.Count, txnNos.Distinct().Count());
    }
}
