using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §تقارير المعالجة والتعقيم — النقطة 6 من متطلبات الدورة (التتبع والتقارير مع المتأخرات).
///
/// تُبنى البيانات عبر **الخدمات الحقيقية** (استلام ⟵ اعتماد ⟵ بدء معالجة ⟵ إفراج/رفض)
/// لا بحقن صفوف، كي يُختبر التقرير على بيانات ينتجها النظام فعلاً.
///
/// السيناريو: 5,000 سلة × 20 كجم؛ منها معالجة متأخرة، ومعالجة أُفرج عنها جزئياً،
/// ومعالجة فيها رفض — فتظهر الحالات الثلاث في التقارير.
/// </summary>
public class TreatmentReportsTests
{
    private const double BasketKg = 20;

    private sealed class Sc
    {
        public TestHost Host;
        public DatesErpDbContext Db;
        public IRawTreatmentService Trt;
        public IReportService Rep;
        public int LotId;
    }

    private static Sc Setup()
    {
        var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();

        var raw = db.Products.First(p => p.ProductCode == "001-001");
        raw.RequiresTreatment = true;
        db.SaveChanges();

        var receiving = host.Get<IReceivingService>();
        var r = receiving.SaveShipment(1, "2026-09-01", "2026-09-01", new List<ShipmentItemDto>
        {
            new() { TreatmentRequired = false, ProductId = raw.Id, PackagingTypeId = 3, PackageCount = 5000,
                    UnitWeightKg = BasketKg, QtyKg = 5000 * BasketKg, ReceiptUnit = "سلة" }
        });
        Assert.True(r.Ok, r.Message);
        Assert.True(receiving.ApproveShipment(r.Id).Ok);
        // Fixture تاريخي: هذه الاختبارات تحافظ على محرك السجلات السابقة، لا عقد الاستلام الجديد.
        foreach (var i in db.ShipmentItems.Where(i => i.ShipmentId == r.Id)) i.TreatmentRequired = null;
        foreach (var l in db.Lots.Where(l => l.ShipmentId == r.Id)) l.TreatmentReadyQtyKg = 0;
        db.SaveChanges();

        return new Sc
        {
            Host = host,
            Db = db,
            Trt = host.Get<IRawTreatmentService>(),
            Rep = host.Get<IReportService>(),
            LotId = db.Lots.OrderBy(l => l.Id).Last().Id
        };
    }

    private static int StartAgo(Sc s, double baskets, double durationHours, double daysAgo)
    {
        var r = s.Trt.Start(new TreatmentStartDto
        {
            LotId = s.LotId,
            QtyKg = baskets * BasketKg,
            PackageCount = (int)baskets,
            DurationHours = durationHours,
            StartedAt = DateTime.Now.AddDays(-daysAgo)
        });
        Assert.True(r.Ok, r.Message);
        return r.Id;
    }

    private static Dictionary<string, string> NoParams() => new();

    // ═══════════════════════════ التسجيل في الفهرس ═══════════════════════════

    [Fact]
    public void Treatment_Reports_Are_Registered_And_Runnable()
    {
        var sc = Setup();
        using (sc.Host)
        {
            var codes = sc.Rep.GetReports().Select(x => x.Code).ToList();
            Assert.Contains("treatment_log", codes);
            Assert.Contains("treatment_overdue", codes);
            Assert.Contains("treatment_performance", codes);

            foreach (var c in new[] { "treatment_log", "treatment_overdue", "treatment_performance" })
            {
                var r = sc.Rep.Run(c, NoParams());
                Assert.NotNull(r);
                Assert.True(r.Columns.Count > 0, c);
            }
        }
    }

    /// <summary>الفئة موحدة كي تتجمع التقارير الثلاثة معاً في شجرة الشاشة.</summary>
    [Fact]
    public void Treatment_Reports_Share_One_Category()
    {
        var sc = Setup();
        using (sc.Host)
        {
            var cats = sc.Rep.GetReports()
                .Where(x => x.Code.StartsWith("treatment_"))
                .Select(x => x.Category).Distinct().ToList();
            Assert.Single(cats);
            Assert.Equal("المعالجة والتعقيم", cats[0]);
        }
    }

    // ═══════════════════════════ سجل المعالجة ═══════════════════════════

    [Fact]
    public void Log_Lists_Every_Treatment_With_Lot_And_Shipment_Chain()
    {
        var sc = Setup();
        using (sc.Host)
        {
            StartAgo(sc, 500, 7 * 24, 0);
            StartAgo(sc, 500, 10 * 24, 0);

            var r = sc.Rep.Run("treatment_log", NoParams());
            Assert.Equal(2, r.Rows.Count);

            // سلسلة التتبع: الشحنة والدفعة والعميل حاضرة في كل صف — مطلب النقطة 6.
            int iShip = r.Columns.IndexOf("الشحنة");
            int iLot = r.Columns.IndexOf("الدفعة");
            int iCust = r.Columns.IndexOf("العميل");
            foreach (var row in r.Rows)
            {
                Assert.NotEqual("—", row[iShip]?.ToString());
                Assert.NotEqual("—", row[iLot]?.ToString());
                Assert.NotEqual("—", row[iCust]?.ToString());
            }
        }
    }

    /// <summary>معادلة الاتساق: لا ازدواجية ولا اختفاء (مطلب المستخدم رقم 3).</summary>
    [Fact]
    public void Log_Balance_Equation_Holds_After_Partial_Release_And_Reject()
    {
        var sc = Setup();
        using (sc.Host)
        {
            int t1 = StartAgo(sc, 500, 7 * 24, 8);   // نضجت
            int t2 = StartAgo(sc, 500, 10 * 24, 11); // نضجت

            Assert.True(sc.Trt.Release(t1, 200 * BasketKg).Ok);       // إفراج جزئي
            Assert.True(sc.Trt.Reject(t2, 100 * BasketKg, "تلف").Ok); // رفض جزئي

            var r = sc.Rep.Run("treatment_log", NoParams());

            double total = 1000 * BasketKg;   // 20,000
            double released = 200 * BasketKg; // 4,000
            double rejected = 100 * BasketKg; // 2,000
            double remaining = total - released - rejected; // 14,000

            Assert.Equal(total.ToString("N1"), r.Summary["إجمالي الكمية المُعالَجة (كجم)"]);
            Assert.Equal(released.ToString("N1"), r.Summary["أُفرج عنه (كجم)"]);
            Assert.Equal(rejected.ToString("N1"), r.Summary["مرفوض (كجم)"]);
            Assert.Equal(remaining.ToString("N1"), r.Summary["ما زال داخل الدورة (كجم)"]);
            Assert.Equal("10.00%", r.Summary["نسبة الرفض"]); // 2,000 / 20,000
        }
    }

    /// <summary>
    /// «تحت المعالجة» وحدها تُخفي أن المدة انقضت والبضاعة تنتظر قراراً بشرياً.
    /// التقرير يفرّق بين الثلاث: جارية · بلغت مدتها · متأخرة.
    /// </summary>
    [Fact]
    public void Log_Distinguishes_Running_From_Matured_And_Overdue()
    {
        var sc = Setup();
        using (sc.Host)
        {
            StartAgo(sc, 100, 10 * 24, 1); // ما زالت جارية
            StartAgo(sc, 100, 7 * 24, 9);  // انقضت مدتها ولم يُفرج عنها

            var r = sc.Rep.Run("treatment_log", NoParams());
            int iStatus = r.Columns.IndexOf("الحالة");
            var statuses = r.Rows.Select(x => x[iStatus]?.ToString()).ToList();

            Assert.Contains(statuses, s => s.Contains("تحت المعالجة"));
            Assert.Contains(statuses, s => s.Contains("متأخرة"));
            // لا يجوز أن يظهر الصفّان بالحالة نفسها — وإلا ضاع التمييز.
            Assert.Equal(2, statuses.Distinct().Count());
        }
    }

    [Fact]
    public void Log_Filters_By_Status()
    {
        var sc = Setup();
        using (sc.Host)
        {
            int t1 = StartAgo(sc, 500, 7 * 24, 8);
            StartAgo(sc, 500, 10 * 24, 1);
            Assert.True(sc.Trt.Release(t1, 500 * BasketKg).Ok); // اكتملت

            var released = sc.Rep.Run("treatment_log",
                new Dictionary<string, string> { ["tstatus"] = TreatmentStatuses.Released });
            Assert.Single(released.Rows);

            var running = sc.Rep.Run("treatment_log",
                new Dictionary<string, string> { ["tstatus"] = TreatmentStatuses.InProgress });
            Assert.Single(running.Rows);
        }
    }

    // ═══════════════════════════ المتأخرات ═══════════════════════════

    [Fact]
    public void Overdue_Shows_Only_Late_Unreleased_Treatments()
    {
        var sc = Setup();
        using (sc.Host)
        {
            StartAgo(sc, 500, 7 * 24, 9);   // متأخرة بيومين
            StartAgo(sc, 500, 10 * 24, 1);  // ما زالت في مدتها

            var r = sc.Rep.Run("treatment_overdue", NoParams());
            Assert.Single(r.Rows);
            Assert.Equal("1", r.Summary["عدد المعالجات المتأخرة"]);
            // الكمية المحتجزة عن الإنتاج = المتبقي داخل الدورة المتأخرة.
            Assert.Equal((500 * BasketKg).ToString("N1"), r.Summary["كمية محتجزة عن الإنتاج (كجم)"]);
        }
    }

    /// <summary>الإفراج يُخرج العملية من قائمة المتأخرات — وإلا تراكمت بلا معنى.</summary>
    [Fact]
    public void Overdue_Clears_After_Release()
    {
        var sc = Setup();
        using (sc.Host)
        {
            int t = StartAgo(sc, 500, 7 * 24, 9);
            Assert.Single(sc.Rep.Run("treatment_overdue", NoParams()).Rows);

            Assert.True(sc.Trt.Release(t, 500 * BasketKg).Ok);

            var after = sc.Rep.Run("treatment_overdue", NoParams());
            Assert.Empty(after.Rows);
            Assert.Equal("لا توجد معالجات متأخرة ✅", after.Summary["الحالة"]);
        }
    }

    /// <summary>الأقدم تأخراً أولاً — هذا تقرير عمل يومي لا كشف أرشيفي.</summary>
    [Fact]
    public void Overdue_Is_Sorted_Worst_First()
    {
        var sc = Setup();
        using (sc.Host)
        {
            StartAgo(sc, 100, 24, 3);   // متأخرة بيومين
            StartAgo(sc, 100, 24, 11);  // متأخرة بعشرة أيام — الأسوأ

            var r = sc.Rep.Run("treatment_overdue", NoParams());
            Assert.Equal(2, r.Rows.Count);

            int iLate = r.Columns.IndexOf("التأخير");
            // الصف الأول يجب أن يكون الأطول تأخيراً (بالأيام).
            double First(int i) => double.Parse(r.Rows[i][iLate].ToString().Split(' ')[0]);
            Assert.True(First(0) > First(1), "المتأخرات غير مرتبة بالأسوأ أولاً");
            Assert.Equal("10.0 يوم", r.Summary["أقصى تأخير"]);
        }
    }

    // ═══════════════════════════ أداء المدد ═══════════════════════════

    [Fact]
    public void Performance_Compares_Planned_Vs_Actual_Duration()
    {
        var sc = Setup();
        using (sc.Host)
        {
            // خُطط لها 7 أيام، وبُدئت قبل 9 أيام وأُفرج عنها الآن ⟵ الفعلي 9 أيام.
            int t = StartAgo(sc, 500, 7 * 24, 9);
            Assert.True(sc.Trt.Release(t, 500 * BasketKg).Ok);

            var r = sc.Rep.Run("treatment_performance", NoParams());
            Assert.Single(r.Rows);

            int iPlan = r.Columns.IndexOf("متوسط المدة المخططة");
            int iAct = r.Columns.IndexOf("متوسط المدة الفعلية");
            int iDev = r.Columns.IndexOf("الانحراف");

            Assert.Equal("7.0 يوم", r.Rows[0][iPlan]);
            Assert.Equal("9.0 يوم", r.Rows[0][iAct]);
            // تأخّر عن المخطط ⟵ انحراف موجب.
            Assert.StartsWith("+", r.Rows[0][iDev].ToString());
        }
    }

    [Fact]
    public void Performance_Reports_Rejection_Rate()
    {
        var sc = Setup();
        using (sc.Host)
        {
            int t = StartAgo(sc, 1000, 7 * 24, 8);
            Assert.True(sc.Trt.Reject(t, 250 * BasketKg, "إصابة حشرية").Ok);

            var r = sc.Rep.Run("treatment_performance", NoParams());
            Assert.Equal("25.00%", r.Summary["نسبة الرفض الإجمالية"]);
        }
    }

    /// <summary>قاعدة بلا معالجات: تقرير فارغ نظيف لا استثناء ولا قسمة على صفر.</summary>
    [Fact]
    public void Treatment_Reports_Are_Clean_On_Empty_Data()
    {
        var sc = Setup();
        using (sc.Host)
        {
            foreach (var c in new[] { "treatment_log", "treatment_overdue", "treatment_performance" })
            {
                var r = sc.Rep.Run(c, NoParams());
                Assert.NotNull(r);
                Assert.Empty(r.Rows);
                Assert.True(r.Columns.Count > 0, c);
            }
        }
    }
}
