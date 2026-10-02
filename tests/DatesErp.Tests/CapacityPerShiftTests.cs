using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §ربط طاقة الإنتاج بالوردية من شاشة الأصناف.
///
///   الوردية تحدد الوقت المتاح فقط · الصنف يحدد طاقته في كل وردية ·
///   الخطة تحسب الاستهلاك ولا تسمح بتجاوز الطاقة أو الساعات.
///
/// الأرقام هنا هي أمثلة أمر التطوير حرفياً (الصنف أ: 7.5 كجم · 4,000/3,000 كرتون،
/// والصنف ب: 4 قوالب × 500 جم = 2 كجم · 8,000/6,500 كرتون).
/// </summary>
public class CapacityPerShiftTests
{
    private sealed record Ctx(int Shift1, int Shift2, int ItemA, int ItemB, int Customer, int Lot);

    private static Ctx Build(TestHost host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var shifts = scope.ServiceProvider.GetRequiredService<IShiftService>();

        // وردية أولى 8 ساعات · ثانية 6 ساعات (توقفات مخططة 2)
        var s1 = db.Shifts.AsNoTracking().OrderBy(s => s.Id).First();
        var s2 = db.Shifts.AsNoTracking().OrderBy(s => s.Id).Skip(1).First();
        shifts.SaveShift(s1.Id, s1.ShiftNameAr, "06:00", "14:00", 8, 0, 8);
        shifts.SaveShift(s2.Id, s2.ShiftNameAr, "14:00", "20:00", 6, 0, 6);

        var cust = master.SaveCustomer(null, "CAP-1", "عميل الطاقة", "جملة", "777", "-", true);
        var raw = master.SaveProductFull(null, "CAP-R", "خام الطاقة", "001", "Raw", "كجم", 20, 0, 0, null);

        // الصنف أ: كرتون 7.5 كجم — طاقة 4,000 (أولى) و3,000 (ثانية)
        var a = master.SaveProductFull(null, "CAP-A", "الصنف أ", "002", "Finished", "كرتون", 7.5, 1, 7.5,
            new List<(int, int?, int)> { (s1.Id, null, 4000), (s2.Id, null, 3000) }, raw.Id);

        // الصنف ب: 4 قوالب × 500 جم = 2 كجم — طاقة 8,000 (أولى) و6,500 (ثانية)
        var b = master.SaveProductFull(null, "CAP-B", "الصنف ب", "002", "Finished", "كرتون", 2, 4, 0.5,
            new List<(int, int?, int)> { (s1.Id, null, 8000), (s2.Id, null, 6500) }, raw.Id);

        // دفعة خام كافية
        var rcv = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var sh = rcv.SaveShipment(cust.Id, null, null, new List<ShipmentItemDto>
        { new() { TreatmentRequired = false, ProductId = raw.Id, QtyKg = 200000, PackageCount = 10000, UnitWeightKg = 20 } });
        rcv.ApproveShipment(sh.Id);
        int lot = db.Lots.AsNoTracking().Where(l => l.ShipmentId == sh.Id).Select(l => l.Id).First();

        return new Ctx(s1.Id, s2.Id, a.Id, b.Id, cust.Id, lot);
    }

    [Fact]
    public void Capacity_Differs_Per_Item_And_Per_Shift()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = Build(host);
        using var scope = host.Services.CreateScope();
        var cap = scope.ServiceProvider.GetRequiredService<ICapacityService>();

        Assert.Equal(4000, cap.GetCapacity(ctx.ItemA, ctx.Shift1).capacity);
        Assert.Equal(3000, cap.GetCapacity(ctx.ItemA, ctx.Shift2).capacity);
        Assert.Equal(8000, cap.GetCapacity(ctx.ItemB, ctx.Shift1).capacity);
        Assert.Equal(6500, cap.GetCapacity(ctx.ItemB, ctx.Shift2).capacity);
    }

    [Fact]
    public void Rate_Is_Derived_Not_Entered()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = Build(host);
        using var scope = host.Services.CreateScope();
        var cap = scope.ServiceProvider.GetRequiredService<ICapacityService>();

        // 4,000 ÷ 8 = 500 · 3,000 ÷ 6 = 500 · 8,000 ÷ 8 = 1,000 · 6,500 ÷ 6 ≈ 1,083.3
        Assert.Equal(500, cap.GetCapacity(ctx.ItemA, ctx.Shift1).rate);
        Assert.Equal(500, cap.GetCapacity(ctx.ItemA, ctx.Shift2).rate);
        Assert.Equal(1000, cap.GetCapacity(ctx.ItemB, ctx.Shift1).rate);
        Assert.Equal(1083.3, cap.GetCapacity(ctx.ItemB, ctx.Shift2).rate, 1);
    }

    [Fact]
    public void Carton_Weight_Is_Derived_From_Molds()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = Build(host);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        // الصنف ب: 4 قوالب × 0.5 كجم = 2 كجم
        Assert.Equal(2, UnitsPolicy.CartonWeight(db, ctx.ItemB, null));
        Assert.Equal(7.5, UnitsPolicy.CartonWeight(db, ctx.ItemA, null));
    }

    [Fact]
    public void Changing_Shift_Hours_Recalculates_Capacity_From_Rate()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = Build(host);
        using var scope = host.Services.CreateScope();
        var shifts = scope.ServiceProvider.GetRequiredService<IShiftService>();
        var cap = scope.ServiceProvider.GetRequiredService<ICapacityService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        // المعدل 500 → 8 ساعات = 4,000 · 7 = 3,500 · 6 = 3,000 · 5 = 2,500
        foreach (var (hours, expected) in new[] { (8.0, 4000), (7.0, 3500), (6.0, 3000), (5.0, 2500) })
        {
            var s = db.Shifts.AsNoTracking().Single(x => x.Id == ctx.Shift1);
            var r = shifts.SaveShift(ctx.Shift1, s.ShiftNameAr, s.StartTime, s.EndTime, hours, 0, hours);
            Assert.True(r.Ok, r.Message);
            Assert.Equal(expected, cap.GetCapacity(ctx.ItemA, ctx.Shift1).capacity);
            Assert.Equal(500, cap.GetCapacity(ctx.ItemA, ctx.Shift1).rate);   // المعدل ثابت
        }
    }

    [Fact]
    public void Changing_One_Shift_Does_Not_Touch_Another_Items_Settings()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = Build(host);
        using var scope = host.Services.CreateScope();
        var shifts = scope.ServiceProvider.GetRequiredService<IShiftService>();
        var cap = scope.ServiceProvider.GetRequiredService<ICapacityService>();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

        var s1 = db.Shifts.AsNoTracking().Single(x => x.Id == ctx.Shift1);
        shifts.SaveShift(ctx.Shift1, s1.ShiftNameAr, s1.StartTime, s1.EndTime, 10, 0, 10);

        // الوردية الأولى تغيّرت → طاقتها فقط. الثانية وصنفها كما هما.
        Assert.Equal(5000, cap.GetCapacity(ctx.ItemA, ctx.Shift1).capacity);   // 500 × 10
        Assert.Equal(3000, cap.GetCapacity(ctx.ItemA, ctx.Shift2).capacity);   // لم تتغير
        Assert.Equal(6500, cap.GetCapacity(ctx.ItemB, ctx.Shift2).capacity);
    }

    [Fact]
    public void Plan_Over_Capacity_Is_Rejected_With_The_Difference()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = Build(host);
        using var scope = host.Services.CreateScope();
        var plan = scope.ServiceProvider.GetRequiredService<IPlanningService>();

        // الطاقة 4,000 والمطلوب 4,500 → زيادة 500.
        // §ملاحظة: SavePlan يغلّف الاستثناء في OpResult.Fail عبر RunOp — فالرفض نتيجة لا استثناء.
        var res = plan.SavePlan("خطة تجاوز", "Daily",
            "2026-09-01", "2026-09-01", ctx.Shift1, 1, new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = ctx.Lot, CustomerId = ctx.Customer,
                        ProductId = ctx.ItemA, PlannedCartons = 4500, PlannedQtyKg = 4500 * 7.5, PriorityNo = 1 }
            });
        Assert.False(res.Ok);
        Assert.Contains("أكبر من الطاقة الإنتاجية المتاحة", res.Message);
        Assert.Contains("4,000", res.Message);
        Assert.Contains("4,500", res.Message);
        Assert.Contains("500", res.Message);
    }

    [Fact]
    public void Plan_Under_Capacity_Computes_Required_And_Remaining_Hours()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = Build(host);
        using var scope = host.Services.CreateScope();
        var plan = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var prog = scope.ServiceProvider.GetRequiredService<IPlanProgressService>();

        // 2,000 كرتون ÷ 500 = 4 ساعات من 8 → متبقٍ 4
        var r = plan.SavePlan("خطة ضمن الطاقة", "Daily", "2026-09-01", "2026-09-01", ctx.Shift1, 1,
            new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = ctx.Lot, CustomerId = ctx.Customer,
                        ProductId = ctx.ItemA, PlannedCartons = 2000, PlannedQtyKg = 15000, PriorityNo = 1 }
            });
        Assert.True(r.Ok, r.Message);
        Assert.True(plan.ApprovePlan(r.Id).Ok);

        var rows = prog.GetDailyPlan("2026-09-01", r.Id).Where(x => x.ProductName == "الصنف أ").ToList();
        var row = Assert.Single(rows);
        Assert.Equal(4000, row.MaxCapacity);
        Assert.Equal(500, row.RatePerHour);
        Assert.Equal(4, row.RequiredHours, 2);
        Assert.Equal(4, row.HoursRemainingOnDay, 2);
    }

    [Fact]
    public void Two_Items_In_One_Shift_Cannot_Exceed_Total_Hours()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = Build(host);
        using var scope = host.Services.CreateScope();
        var plan = scope.ServiceProvider.GetRequiredService<IPlanningService>();

        // الصنف أ: 3,000 كرتون ÷ 500 = 6 ساعات. الصنف ب: 3,000 ÷ 1,000 = 3 ساعات. المجموع 9 > 8.
        var res = plan.SavePlan("خطة ساعتين", "Daily",
            "2026-09-01", "2026-09-01", ctx.Shift1, 1, new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = ctx.Lot, CustomerId = ctx.Customer,
                        ProductId = ctx.ItemA, PlannedCartons = 3000, PlannedQtyKg = 22500, PriorityNo = 1 },
                new() { SourceType = "FromReceiving", LotId = ctx.Lot, CustomerId = ctx.Customer,
                        ProductId = ctx.ItemB, PlannedCartons = 3000, PlannedQtyKg = 6000, PriorityNo = 2 }
            });
        Assert.False(res.Ok);
        Assert.Contains("الطاقة الإنتاجية المتاحة", res.Message);
    }

    [Fact]
    public void Two_Items_Fitting_The_Shift_Are_Accepted()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var ctx = Build(host);
        using var scope = host.Services.CreateScope();
        var plan = scope.ServiceProvider.GetRequiredService<IPlanningService>();

        // أ: 2,000 ÷ 500 = 4 ساعات. ب: 4,000 ÷ 1,000 = 4 ساعات. المجموع 8 = المتاحة.
        var r = plan.SavePlan("خطة ملء الوردية", "Daily", "2026-09-01", "2026-09-01", ctx.Shift1, 1,
            new List<PlanItemDto>
            {
                new() { SourceType = "FromReceiving", LotId = ctx.Lot, CustomerId = ctx.Customer,
                        ProductId = ctx.ItemA, PlannedCartons = 2000, PlannedQtyKg = 15000, PriorityNo = 1 },
                new() { SourceType = "FromReceiving", LotId = ctx.Lot, CustomerId = ctx.Customer,
                        ProductId = ctx.ItemB, PlannedCartons = 4000, PlannedQtyKg = 8000, PriorityNo = 2 }
            });
        Assert.True(r.Ok, r.Message);
    }

    [Fact]
    public void Undefined_Capacity_Is_Zero_Not_A_Silent_Default()
    {
        // §كان التخطيط يعوّض 500 كرتون/ساعة بصمت عند غياب التعريف
        using var host = new TestHost();
        host.LoginAsAdmin();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();

        var p = master.SaveProductFull(null, "CAP-N", "بلا طاقة", "002", "Finished", "كرتون", 7.5, 1, 7.5,
            new List<(int, int?, int)>());
        Assert.True(p.Ok, p.Message);
        // نبخّس المعدل العام للصنف حتى لا يكون مصدراً بديلاً
        var prod = db.Products.Single(x => x.Id == p.Id);
        prod.HourlyProductionRate = 0;
        db.SaveChanges();

        int shift = db.Shifts.AsNoTracking().OrderBy(s => s.Id).Select(s => s.Id).First();
        var (rate, capacity, source) = CapacityPolicy.Resolve(db, p.Id, shift);
        Assert.Equal(0, rate);
        Assert.Equal(0, capacity);
        Assert.Equal("غير معرَّف", source);
    }

    [Fact]
    public void Shift_Screen_Has_No_Item_Capacity_Setting()
    {
        // §البند 1 و11: الوردية تحدد الوقت المتاح فقط — لا طاقة أصناف فيها
        string root = FindRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "src/DatesErp.Desktop/Views/Screens/ShiftsView.xaml"));
        string cs = File.ReadAllText(Path.Combine(root, "src/DatesErp.Desktop/Views/Screens/ShiftsView.xaml.cs"));
        Assert.DoesNotContain("ProductShiftCapacit", xaml);
        Assert.DoesNotContain("ProductShiftCapacit", cs);
        Assert.DoesNotContain("SetCapacity", cs);
        Assert.Contains("لا طاقة للأصناف هنا", xaml);
    }

    [Fact]
    public void Planning_Capacity_Summary_Is_Display_Only()
    {
        // الشاشة الحالية تعرض ملخصاً لكل يوم/وردية، لا أعمدة MaxCapacity القديمة.
        // نبقي شرط العرض فقط دون الاعتماد على صياغة العناوين أو السماح بإدخال الطاقة.
        string root = FindRoot();
        var plan = System.Xml.Linq.XDocument.Load(Path.Combine(root,
            "src/DatesErp.Desktop/Views/Screens/PlanningView.xaml"));
        System.Xml.Linq.XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var name in new[] { "CapacitySummary", "RemainingBadge" })
        {
            var element = Assert.Single(plan.Descendants(), e => (string)e.Attribute(x + "Name") == name);
            Assert.Equal("TextBlock", element.Name.LocalName);
        }
        Assert.DoesNotContain(plan.Descendants().SelectMany(e => e.Attributes()), a =>
            a.Value.Contains("MaxCapacity") &&
            (a.Value.Contains("Mode=TwoWay") || a.Value.Contains("UpdateSourceTrigger")));
        string code = File.ReadAllText(Path.Combine(root,
            "src/DatesErp.Desktop/Views/Screens/PlanningView.Capacity.cs"));
        Assert.Contains("service.EvaluateCapacity(items", code);
        Assert.Contains("CapacitySummary.Text = result.Summary", code);
    }

    [Fact]
    public void No_Hardcoded_Capacity_Or_Hours_Defaults_In_Services()
    {
        // §لا معدل ولا ساعات افتراضية في الكود
        string root = FindRoot();
        string dir = Path.Combine(root, "src/DatesErp.Application/Services");
        foreach (var f in Directory.GetFiles(dir, "*.cs"))
        {
            var lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
            {
                string code = lines[i].TrimStart();
                if (code.StartsWith("//") || code.StartsWith("///")) continue;
                Assert.False(code.Contains("?? 8;"),
                    $"{Path.GetFileName(f)}:{i + 1} يفترض 8 ساعات — الساعات من الوردية\n{code}");
                Assert.False(code.Contains("= 500;") || code.Contains("?? 500") || code.Contains("* 7.2") || code.Contains(": 7.2;"),
                    $"{Path.GetFileName(f)}:{i + 1} يفترض معدلاً أو وزن كرتون افتراضياً\n{code}");
            }
        }
    }

    private static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "DateERP.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
