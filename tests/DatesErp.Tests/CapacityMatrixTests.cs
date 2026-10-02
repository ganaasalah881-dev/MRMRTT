using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B73 — شاشة طاقات الأصناف: السيناريو المعتمد من المستخدم حرفياً.
/// تمر سكري · 100/ساعة · وردية 8س → 800 · وردية 7س → 700 · اليوم 1,500.
/// إضافة وردية ثالثة تظهر وتدخل الحساب؛ تغيير ساعات وردية يعيد الحساب؛ التخطيط يستهلك المصدر نفسه.
/// </summary>
public class CapacityMatrixTests
{
    private static ICapacityService Caps(TestHost h)
        => h.Services.CreateScope().ServiceProvider.GetRequiredService<ICapacityService>();
    private static IShiftService Shifts(TestHost h)
        => h.Services.CreateScope().ServiceProvider.GetRequiredService<IShiftService>();

    private static (int item, int s1, int s2) Setup(TestHost host)
    {
        var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var shifts = scope.ServiceProvider.GetRequiredService<IShiftService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var item = master.SaveProductFull(null, "002-777", "تمر سكري", "002", "Finished", "كرتون", 10, 5, 2, null);
        Assert.True(item.Ok, item.Message);
        // وردية أولى 8 فعلية · ثانية 7 فعلية (من شاشة الورديات)
        var rows = db.Shifts.AsNoTracking().OrderBy(s => s.Id).ToList();
        Assert.True(shifts.SaveShift(rows[0].Id, rows[0].ShiftNameAr, "06:00", "14:00", 8, 0, 8).Ok);
        Assert.True(shifts.SaveShift(rows[1].Id, rows[1].ShiftNameAr, "14:00", "21:00", 7, 0, 7).Ok);
        foreach (var extra in rows.Skip(2))
        {
            var e = db.Shifts.First(x => x.Id == extra.Id);
            e.IsActive = false;   // خارج الحساب: الورديات النشطة فقط تدخل طاقة اليوم
            db.SaveChanges();
        }
        return (item.Id, rows[0].Id, rows[1].Id);
    }

    [Fact]
    public void Hourly_Times_Effective_Hours_Per_Shift_And_Day_Total()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (item, s1, s2) = Setup(host);
        Assert.True(Caps(host).SaveHourlyRate(item, 100).Ok);

        Assert.Equal(800, Caps(host).GetCapacity(item, s1).capacity);   // 100 × 8
        Assert.Equal(700, Caps(host).GetCapacity(item, s2).capacity);   // 100 × 7
        Assert.Equal(1500, Caps(host).GetDayCapacity(item), 1);         // 800 + 700
    }

    [Fact]
    public void Added_Shift_Appears_And_Enters_Day_Capacity()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (item, s1, s2) = Setup(host);
        Assert.True(Caps(host).SaveHourlyRate(item, 100).Ok);
        Assert.Equal(1500, Caps(host).GetDayCapacity(item), 1);

        // وردية ثالثة من شاشة الورديات: 6 ساعات فعلية
        var r = Shifts(host).SaveShift(null, "وردية رابعة تجريبية", "05:00", "11:00", 6, 0, 6);
        Assert.True(r.Ok, r.Message);
        var newShiftId = host.Services.CreateScope().ServiceProvider.GetRequiredService<DatesErpDbContext>()
            .Shifts.AsNoTracking().Single(x => x.ShiftNameAr == "وردية رابعة تجريبية").Id;
        Assert.Equal(600, Caps(host).GetCapacity(item, newShiftId).capacity);
        Assert.Equal(2100, Caps(host).GetDayCapacity(item), 1);
    }

    [Fact]
    public void Changing_Shift_Hours_Recomputes_Capacities()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (item, s1, s2) = Setup(host);
        Assert.True(Caps(host).SaveHourlyRate(item, 100).Ok);
        var shiftsSvc = Shifts(host);
        using (var db = host.Services.CreateScope().ServiceProvider.GetRequiredService<DatesErpDbContext>())
        {
            var sh = db.Shifts.AsNoTracking().First(s => s.Id == s1);
            Assert.True(shiftsSvc.SaveShift(s1, sh.ShiftNameAr, "06:00", "15:00", 9, 0, 9).Ok);
        }
        Assert.Equal(900, Caps(host).GetCapacity(item, s1).capacity);   // 100 × 9
        Assert.Equal(1600, Caps(host).GetDayCapacity(item), 1);
    }

    [Fact]
    public void Planned_Downtime_Reduces_Effective_Hours_Used_In_Capacity()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (item, s1, _) = Setup(host);
        Assert.True(Caps(host).SaveHourlyRate(item, 100).Ok);
        var shiftsSvc = Shifts(host);
        using (var db = host.Services.CreateScope().ServiceProvider.GetRequiredService<DatesErpDbContext>())
        {
            var sh = db.Shifts.AsNoTracking().First(s => s.Id == s1);
            // إجمالي 8 − توقف مخطط 2 = 6 فعلية
            Assert.True(shiftsSvc.SaveShift(s1, sh.ShiftNameAr, "06:00", "14:00", 8, 2, 6).Ok);
        }
        Assert.Equal(600, Caps(host).GetCapacity(item, s1).capacity);
    }

    [Fact]
    public void Hourly_Rate_Validated_And_Negative_Rejected()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var master = host.Services.CreateScope().ServiceProvider.GetRequiredService<MasterDataService>();
        var item = master.SaveProductFull(null, "002-778", "صنف التحقق", "002", "Finished", "كرتون", 10, 5, 2, null);
        Assert.True(item.Ok);
        Assert.False(Caps(host).SaveHourlyRate(item.Id, 0).Ok);
        Assert.False(Caps(host).SaveHourlyRate(item.Id, -5).Ok);
        Assert.False(Caps(host).SaveHourlyRate(item.Id, double.NaN).Ok);
    }

    [Fact]
    public void Planning_Uses_The_Same_Computed_Capacity()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var scope = host.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var rcv = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var plan = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var cust = master.SaveCustomer(null, "CC", "عميل الطاقة", "جملة", "9", "-", true);
        var raw = master.SaveProductFull(null, "001-777", "خام سكري", "001", "Raw", "كجم", 20, 0, 0, null);
        var fin = master.SaveProductFull(null, "002-779", "سكري تخطيط", "002", "Finished", "كرتون", 10, 5, 2, null, raw.Id);
        var sh = rcv.SaveShipment(cust.Id, null, null, new System.Collections.Generic.List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = raw.Id, QtyKg = 100000, PackageCount = 5000, UnitWeightKg = 20 } });
        rcv.ApproveShipment(sh.Id);
        var lot = db.Lots.Single(l => l.ShipmentId == sh.Id).Id;

        var rows = db.Shifts.AsNoTracking().OrderBy(s => s.Id).ToList();
        Shifts(host).SaveShift(rows[0].Id, rows[0].ShiftNameAr, "06:00", "14:00", 8, 0, 8);
        Assert.True(Caps(host).SaveHourlyRate(fin.Id, 100).Ok);   // طاقة الوردية = 800

        var over = plan.SavePlan("تجاوز", "Daily", "2026-09-01", "2026-09-01", rows[0].Id, 1,
            new System.Collections.Generic.List<PlanItemDto>
            { new() { SourceType = "FromReceiving", LotId = lot, CustomerId = cust.Id, ProductId = fin.Id, PlannedCartons = 900, PlannedQtyKg = 9000 } });
        Assert.False(over.Ok);                                     // 900 > 800 مرفوضة
        var ok = plan.SavePlan("ضمن", "Daily", "2026-09-01", "2026-09-01", rows[0].Id, 1,
            new System.Collections.Generic.List<PlanItemDto>
            { new() { SourceType = "FromReceiving", LotId = lot, CustomerId = cust.Id, ProductId = fin.Id, PlannedCartons = 800, PlannedQtyKg = 8000 } });
        Assert.True(ok.Ok, ok.Message);                            // 800 = الطاقة المحسوبة
    }

    [Fact]
    public void Screen_Carries_Five_Buttons_And_Readonly_Computed_Columns()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DateERP.sln"))) dir = dir.Parent;
        var xaml = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "src/DatesErp.Desktop/Views/Screens/ItemsCapacitiesView.xaml"));
        foreach (var label in new[] { "➕ إضافة", "💾 حفظ", "✏️ تعديل", "🔍 بحث", "🗑️ حذف" })
            Assert.Contains("Content=\"" + label + "\"", xaml);
        Assert.Contains("الإنتاج بالساعة", xaml);
        // عمود طاقة اليوم يُبنى ديناميكياً في الكود الخلفي (أعمدة الورديات ديناميكية)
        var cs = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "src/DatesErp.Desktop/Views/Screens/ItemsCapacitiesView.xaml.cs"));
        Assert.Contains("طاقة اليوم كاملة", cs);
    }
}
