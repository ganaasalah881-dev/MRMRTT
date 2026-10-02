using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B104 — استكمال البيانات المرجعية في القواعد القائمة: الفحص الذاتي على قاعدة المستخدم
/// (أنشئت قبل ميزة المعالجة) كشف غياب مخزن WTRT وترقيم TASK/TRT — الباذر يغطي الجديد فقط،
/// فخطوة UpgradeReferenceData (تعمل عند كل إقلاع) صارت تضمنها idempotent.
/// </summary>
public class B104BackfillTests
{
    [Fact]
    public void Existing_Db_Gets_WTRT_And_Missing_Schemes_Backfilled()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();

        // محاكاة قاعدة قديمة: احذف ما أضافته الميزات اللاحقة وأعد العلامة لهدف قديم
        db.Warehouses.Remove(db.Warehouses.Single(w => w.WarehouseCode == "WTRT"));
        db.NumberingSchemes.Remove(db.NumberingSchemes.Single(s => s.SchemeCode == "TRT"));
        db.NumberingSchemes.Remove(db.NumberingSchemes.Single(s => s.SchemeCode == "TASK"));
        var marker = db.SystemSettings.FirstOrDefault(s => s.SettingKey == "RefDataUpgrade");
        if (marker != null) marker.SettingValue = "B48";
        db.SaveChanges();

        List<string> changes;
        using (var scope = host.Services.CreateScope())
            changes = DbSeeder.UpgradeReferenceData(scope.ServiceProvider.GetRequiredService<DatesErpDbContext>());

        Assert.Contains(changes, c => c.Contains("WTRT"));
        Assert.Contains(changes, c => c.Contains("TRT"));
        Assert.Contains(changes, c => c.Contains("TASK"));

        using var verify = host.Services.CreateScope();
        var rd = verify.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        Assert.True(rd.Warehouses.Any(w => w.WarehouseCode == "WTRT"));
        Assert.Equal("Treatment", rd.Warehouses.Single(w => w.WarehouseCode == "WTRT").WarehouseType);
        Assert.True(rd.NumberingSchemes.Any(s => s.SchemeCode == "TRT"));
        Assert.True(rd.NumberingSchemes.Any(s => s.SchemeCode == "TASK"));
        Assert.Equal(DbSeeder.RefDataUpgradeTarget,
            rd.SystemSettings.Single(s => s.SettingKey == "RefDataUpgrade").SettingValue);
    }

    [Fact]
    public void Backfill_Is_Idempotent_No_Duplicates()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();

        // تشغيل أول (قاعدة جديدة أصلاً كاملة) ثم ثانٍ بعد إنزال العلامة — بلا تكرار
        using (var scope = host.Services.CreateScope())
        {
            var db0 = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            DbSeeder.UpgradeReferenceData(db0);
            var m = db0.SystemSettings.Single(s => s.SettingKey == "RefDataUpgrade");
            m.SettingValue = "B48"; // أنزل العلامة ليعاد التنفيذ
            db0.SaveChanges();
        }
        List<string> changes;
        using (var scope = host.Services.CreateScope())
            changes = DbSeeder.UpgradeReferenceData(scope.ServiceProvider.GetRequiredService<DatesErpDbContext>());

        Assert.Empty(changes); // كل شيء موجود — لا إضافات
        using var verify = host.Services.CreateScope();
        var rd = verify.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        Assert.Equal(1, rd.Warehouses.Count(w => w.WarehouseCode == "WTRT"));
        Assert.Equal(1, rd.Warehouses.Count(w => w.WarehouseCode == "WRM"));
        Assert.Equal(1, rd.NumberingSchemes.Count(s => s.SchemeCode == "TRT"));
        Assert.Equal(1, rd.NumberingSchemes.Count(s => s.SchemeCode == "SHIP"));
    }

    [Fact]
    public void Treatment_Service_Works_After_Backfill_On_Old_Db()
    {
        // الغاية النهائية: دورة المعالجة تعمل على قاعدة قديمة بعد الاستكمال
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        db.Warehouses.Remove(db.Warehouses.Single(w => w.WarehouseCode == "WTRT"));
        db.SaveChanges();

        using (var scope = host.Services.CreateScope())
            DbSeeder.UpgradeReferenceData(scope.ServiceProvider.GetRequiredService<DatesErpDbContext>());

        // WarehouseId("WTRT") كان سيرمي «غير معرّف» — الآن يعمل عبر بدء معالجة فعلية
        var receiving = host.Get<IReceivingService>();
        var sh = receiving.SaveShipment(1, "2026-09-01", "2026-09-01", new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = 1, PackageCount = 50, UnitWeightKg = 20, QtyKg = 1000 } });
        Assert.True(sh.Ok, sh.Message);
        Assert.True(receiving.ApproveShipment(sh.Id).Ok);
        // بيانات تاريخية لاختبار محرك المعالجة السابق، وليست استلاماً جديداً قابلاً لتجاوز القرار.
        foreach (var i in db.ShipmentItems.Where(i => i.ShipmentId == sh.Id)) i.TreatmentRequired = null;
        foreach (var l in db.Lots.Where(l => l.ShipmentId == sh.Id)) l.TreatmentReadyQtyKg = 0;
        db.SaveChanges();
        var lot = db.Lots.OrderBy(l => l.Id).Last();

        var trt = host.Get<IRawTreatmentService>();
        var st = trt.Start(new TreatmentStartDto { LotId = lot.Id, QtyKg = 500, PackageCount = 25, DurationHours = 24 });
        Assert.True(st.Ok, st.Message);
    }
}
