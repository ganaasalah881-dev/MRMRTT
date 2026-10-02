using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §أمر الفحص الشامل لمنظومة التقارير — اختبارات تشغيلية حقيقية:
/// تنفّذ عمليات فعلية (استلام ← اعتماد ← حجز ← أمر ← تسليم ← جودة) عبر طبقة الخدمات
/// على قاعدة SQLite حقيقية، ثم تقارن مخرجات التقارير (IReportService.Run) مع
/// المستندات والأرصدة الناتجة: الربط، المجاميع، عزل العملاء (مجموع الأجزاء = الكل)،
/// المسودات لا تُعرض كنهائية، أثر التعديل/الحذف + سجل التدقيق، حراس الكمية،
/// وحسابات تقرير الموردين §B103. ما لا يمكن تشغيله خارج ويندوز (معاينة الطباعة/الأداء)
/// موثّق في Documentation/REPORTS_AUDIT_AR.md بوصفه «يُتحقق على الجهاز».
/// </summary>
public class ReportsAuditDirectiveTests
{
    private static T Svc<T>(TestHost host) => (T)host.Services.CreateScope().ServiceProvider.GetService(typeof(T));
    private static DatesErpDbContext FreshDb(TestHost host)
        => new(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);
    private static ReportResult Run(TestHost host, string code, Dictionary<string, string> prm = null)
        => Svc<IReportService>(host).Run(code, prm ?? new Dictionary<string, string>());
    private static bool Has(ReportResult r, int col, string needle)
        => r.Rows.Any(row => (row[col]?.ToString() ?? "").Contains(needle));
    private static double Sum(ReportResult r, int col) => r.Rows.Sum(row => Convert.ToDouble(row[col]));
    private static Dictionary<string, string> Period(string from, string to) => new() { ["from"] = from, ["to"] = to };
    // Ledger movements are dated when posted, not retroactively at the
    // shipment's document date. The report must be filtered by posting date.
    private static Dictionary<string, string> CurrentPostingPeriod() =>
        Period(DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd"), DateTime.Today.AddDays(1).ToString("yyyy-MM-dd"));

    private static (TestHost host, int cust1, int shipmentId, string docNo) ArrangeApprovedShipment(double kg = 6000, int packs = 300)
    {
        var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        int cust1 = db.Customers.First().Id;
        var receiving = Svc<IReceivingService>(host);
        var s = receiving.SaveShipment(cust1, "2026-08-10", "2026-08-10",
            new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = packs, UnitWeightKg = kg / packs, QtyKg = kg } }, null, "CXLU-AUD");
        Assert.True(s.Ok, s.Message);
        Assert.True(receiving.ApproveShipment(s.Id).Ok);
        var doc = FreshDb(host).Shipments.First(x => x.Id == s.Id).DocumentNumber;
        return (host, cust1, s.Id, doc);
    }

    // ── §3/§4/§12: دورة كاملة — التقرير = المستند = الرصيد ──
    [Fact]
    public void Directive_01_FullCycle_Reports_Match_Documents_And_Balances()
    {
        var (host, cust1, shipId, docNo) = ArrangeApprovedShipment();
        var custName = FreshDb(host).Customers.First(c => c.Id == cust1).CustomerName;

        // تقرير الاستلام: رقم المستند + العميل + الوزن + الحالة معتمد
        var rec = Run(host, "receiving", Period("2026-08-01", "2026-08-31"));
        Assert.True(Has(rec, 0, docNo));
        var row = rec.Rows.First(x => x[0].ToString() == docNo);
        Assert.Equal(custName, row[2]);
        Assert.Equal(6000, Convert.ToDouble(row[4]), 1);
        Assert.Equal("معتمد", row[5]);
        Assert.Equal(6000, Sum(rec, 4), 1); // مجموع التقرير = وزن المستند وحده

        // تقرير الحركات: الحركة مرتبطة بالمستند المصدر و«وارد»
        var mov = Run(host, "movements", CurrentPostingPeriod());
        Assert.True(Has(mov, 8, docNo));
        Assert.Equal("وارد", mov.Rows.First(x => x[8].ToString()!.Contains(docNo))[5]);

        // الرصيد الفعلي = وزن الاستلام (المستند ← الحركة ← الرصيد)
        using (var rd = FreshDb(host))
            Assert.Equal(6000, rd.StockBalances.Where(b => b.ProductId == 1).Sum(b => b.QtyKg), 1);
        var inv = Run(host, "inventory");
        Assert.Equal(6000, inv.Rows.Where(x => Equals(x[3], custName)).Sum(x => Convert.ToDouble(x[5])), 1); // [4] اسم العبوة، [5] كجم

        // الدفعات: المستلم = 6000 والمتبقي = 6000 وإجمالي التقرير يطابق الصفوف
        var lots = Run(host, "lots");
        Assert.Equal(6000, Sum(lots, 3), 1);
        Assert.Equal(6000, Sum(lots, 5), 1);
        Assert.Equal(6000, double.Parse(lots.Summary["إجمالي المتبقي (كجم)"], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture), 1);

        // حجز الخطة يخفض المتاح، لكنه ليس صرفاً فعلياً: الدفتر والدفعة لا ينقصان حتى الإقفال.
        var lotId = FreshDb(host).Lots.First().Id;
        var planning = Svc<IPlanningService>(host);
        var plan = planning.SavePlan("خطة الفحص", "Period", "2026-08-20", "2026-08-22", 1, 1, new List<PlanItemDto>
        {
            new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = cust1, ProductId = 3, PlannedQtyKg = 3000, PlannedCartons = 400, ScheduledDate = "2026-08-20", SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 }
        });
        Assert.True(plan.Ok, plan.Message);
        Assert.True(planning.ApprovePlan(plan.Id).Ok);
        var lots2 = Run(host, "lots");
        Assert.Equal(0, Sum(lots2, 4), 1);      // لم يُصرف خام بمجرد اعتماد الخطة
        Assert.Equal(6000, Sum(lots2, 5), 1);   // الرصيد الفعلي لم ينقص
        using (var rd = FreshDb(host))
        {
            Assert.Equal(3000, rd.Lots.First(l => l.Id == lotId).ReservedQtyKg, 1);
            Assert.Equal(3000, rd.Lots.First(l => l.Id == lotId).AvailableQtyKg, 1);
        }

        // أمر الإنتاج: المخطط في التقرير = المخطط في الأمر
        var orders = Svc<IProductionOrderService>(host);
        host.SetBusinessDate("2026-08-20");
        var o = orders.SaveOrder("FromPlan", plan.Id, cust1, "2026-08-20", 1, 1, TestOrderFixture.Items(host, plan.Id, cust1, "2026-08-20"));
        Assert.True(o.Ok, o.Message);
        Assert.True(orders.ApproveOrder(o.Id).Ok);
        var ord = Run(host, "orders", Period("2026-08-01", "2026-08-31"));
        var odoc = FreshDb(host).ProductionOrders.First(x => x.Id == o.Id).DocumentNumber;
        Assert.True(Has(ord, 0, odoc));
        Assert.Equal(3000, Convert.ToDouble(ord.Rows.First(x => x[0].ToString() == odoc)[3]), 1);
        host.Dispose();
    }

    // ── §13: عزل أربعة عملاء ومطابقة مجموع الأجزاء للكل ──
    [Fact]
    public void Directive_02_FourCustomers_Isolation_And_SumParity()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();
        var admin = Svc<MasterDataService>(host);
        var ids = new List<int> { db.Customers.First().Id };
        double[] kg = { 1000, 2000, 3000, 4000 };
        for (int i = 1; i < 4; i++)
        {
            var c = admin.SaveCustomer(null, $"C10{i}", $"عميل الفحص {i}", "تجار جملة", "77700000" + i, null, true);
            Assert.True(c.Ok, c.Message);
            ids.Add(c.Id);
        }
        var receiving = Svc<IReceivingService>(host);
        for (int i = 0; i < 4; i++)
        {
            var s = receiving.SaveShipment(ids[i], "2026-09-01", "2026-09-01",
                new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = (int)(kg[i] / 20), UnitWeightKg = 20, QtyKg = kg[i] } }, null, $"CXLU-ISO{i}");
            Assert.True(s.Ok, s.Message);
            Assert.True(receiving.ApproveShipment(s.Id).Ok);
        }
        var names = FreshDb(host).Customers.Where(c => ids.Contains(c.Id)).ToDictionary(c => c.Id, c => c.CustomerName);
        double sumOfParts = 0;
        for (int i = 0; i < 4; i++)
        {
            var one = Run(host, "receiving", new Dictionary<string, string> { ["from"] = "2026-09-01", ["to"] = "2026-09-30", ["customer"] = ids[i].ToString() });
            Assert.Equal(kg[i], Sum(one, 4), 1);
            sumOfParts += Sum(one, 4);
            // عزل العملاء: لا اسم عميل آخر داخل نتيجة هذا العميل
            foreach (var (jid, jname) in names)
                if (jid != ids[i]) Assert.DoesNotContain(jname, one.Rows.Select(r => r[2]?.ToString() ?? ""));
        }
        var all = Run(host, "receiving", Period("2026-09-01", "2026-09-30"));
        Assert.Equal(sumOfParts, Sum(all, 4), 1);      // مجموع الأجزاء = تقرير الجميع
        Assert.Equal(10000, Sum(all, 4), 1);
    }

    // ── §5/§10/§11: المسودة ليست نهائية + التعديل والحذف وسجل التدقيق + حراس الكمية ──
    [Fact]
    public void Directive_03_Drafts_Edit_Delete_Audit_And_Guards()
    {
        var (host, cust1, _, _) = ArrangeApprovedShipment();
        var receiving = Svc<IReceivingService>(host);

        // مسودة استلام: تظهر بحالتها في تقرير الاستلام ولا تلمس الحركات ولا الرصيد
        var draft = receiving.SaveShipment(cust1, "2026-08-15", "2026-08-15",
            new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 10, UnitWeightKg = 20, QtyKg = 200 } }, null, "CXLU-DFT");
        Assert.True(draft.Ok, draft.Message);
        var ddoc = FreshDb(host).Shipments.First(x => x.Id == draft.Id).DocumentNumber;
        var rec = Run(host, "receiving", Period("2026-08-01", "2026-08-31"));
        var drow = rec.Rows.First(x => x[0].ToString() == ddoc);
        Assert.NotEqual("معتمد", drow[5]);                       // لا تُعرض كمعتمد
        var mov = Run(host, "movements", Period("2026-08-01", "2026-08-31"));
        Assert.False(Has(mov, 8, ddoc));                          // بلا أثر مخزني
        var approvedOnly = new ReportResult { Rows = rec.Rows.Where(x => x[5].ToString() == "معتمد").ToList() };
        Assert.Equal(6000, Sum(approvedOnly, 4), 1);   // مجموع المعتمد وحده = المستند المعتمد

        // تسليم: مسودة ← تعديل ← حذف، والتقرير يتحدث في كل خطوة + أثر التدقيق
        var delivery = Svc<ICustomerDeliveryService>(host);
        var d1 = delivery.Save(cust1, "2026-08-18", null, new List<CustomerDeliveryItemDto> { new() { ProductId = 3, PackagingTypeId = 2, PackageCount = 10, QtyKg = 200 } });
        Assert.True(d1.Ok, d1.Message);
        var dno = FreshDb(host).CustomerDeliveries.First(x => x.Id == d1.Id).DocumentNumber;
        Assert.Equal("مسودة", Run(host, "delivery").Rows.First(x => x[0].ToString() == dno)[5]);
        var u = delivery.Update(d1.Id, cust1, "2026-08-18", null, new List<CustomerDeliveryItemDto> { new() { ProductId = 3, PackagingTypeId = 2, PackageCount = 15, QtyKg = 300 } });
        Assert.True(u.Ok, u.Message);
        var drow2 = Run(host, "delivery").Rows.First(x => x[0].ToString() == dno);
        Assert.Equal(15, Convert.ToDouble(drow2[3]), 1);   // الكرتون الوحدة الرسمية
        Assert.Equal(300, Convert.ToDouble(drow2[4]), 1);  // الكجم بيان إضافي
        Assert.True(delivery.DeleteDraft(d1.Id).Ok);
        Assert.DoesNotContain(dno, Run(host, "delivery").Rows.Select(x => x[0]?.ToString() ?? ""));
        var audit = Run(host, "audit");
        Assert.True(audit.Rows.Any(x => (x[6]?.ToString() ?? "").Contains(dno) || (x[3]?.ToString() ?? "").Contains("حذف")));

        // حارس الكمية: اعتماد تسليم بلا رصيد تام مرفوض (لا تسليم بأكثر من المتاح)
        var d2 = delivery.Save(cust1, "2026-08-19", null, new List<CustomerDeliveryItemDto> { new() { ProductId = 3, PackagingTypeId = 2, PackageCount = 5, QtyKg = 100 } });
        Assert.True(d2.Ok, d2.Message);
        var ap = delivery.Approve(d2.Id);
        Assert.False(ap.Ok);
        host.Dispose();
    }

    // ── §8: الجودة — الكميات بالصفات والحالة المعتمدة ومسار التصحيح الموحد ──
    [Fact]
    public void Directive_04_Quality_Quantities_Approval_And_CorrectionPath()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var quality = Svc<IQualityService>(host);
        var chk = quality.SaveCheck(null, null, "2026-08-25", "نهائي", new List<QualityItemDto>
        {
            new() { ProductId = 3, PackagingTypeId = 2, CheckedQtyKg = 2700, AcceptedQtyKg = 2550, RejectedQtyKg = 150,
                    CheckedCartons = 270, AcceptedCartons = 255, RejectedCartons = 15 }
        });
        Assert.True(chk.Ok, chk.Message);
        Assert.True(quality.ApproveCheck(chk.Id).Ok);
        var q = Run(host, "quality", Period("2026-08-01", "2026-08-31"));
        var row = q.Rows.First();
        Assert.Equal(270, Convert.ToDouble(row[3]), 1);    // الكرتون أولاً: وحدات رسمية
        Assert.Equal(255, Convert.ToDouble(row[4]), 1);
        Assert.Equal(15, Convert.ToDouble(row[5]), 1);
        Assert.Equal(2700, Convert.ToDouble(row[6]), 1);   // والكجم بيان إضافي
        Assert.Equal(2550, Convert.ToDouble(row[7]), 1);
        Assert.Equal(150, Convert.ToDouble(row[8]), 1);
        Assert.Equal("معتمد", row[9]);
        // المعتمد لا يُعدّل مباشرة: التصحيح بمسار مصرّح ومسجّل فقط
        var cor = quality.RequestCorrection(chk.Id, "تصحيح تجريبي ضمن الفحص الشامل");
        Assert.True(cor.Ok, cor.Message);
        Assert.Equal("قيد التنفيذ", Run(host, "quality", Period("2026-08-01", "2026-08-31")).Rows.First()[9]); // فُتح التصحيح فعلياً
    }

    // ── §B103: حسابات تقرير الموردين — ربط الأعمدة بالعمليات الفعلية وعزل الأطراف ──
    [Fact]
    public void Directive_05_SupplierMovement_Mapping_Totals_And_Isolation()
    {
        var (host, cust1, _, _) = ArrangeApprovedShipment();
        var admin = Svc<MasterDataService>(host);
        var c2 = admin.SaveCustomer(null, "C202", "مورد فحص ثان", "تجار جملة", "777333444", null, true);
        Assert.True(c2.Ok, c2.Message);
        var receiving = Svc<IReceivingService>(host);
        var s2 = receiving.SaveShipment(c2.Id, "2026-08-11", "2026-08-11",
            new List<ShipmentItemDto> { new() { TreatmentRequired = false, ProductId = 1, PackagingTypeId = 2, PackageCount = 100, UnitWeightKg = 20, QtyKg = 2000 } }, null, "CXLU-SM2");
        Assert.True(s2.Ok, s2.Message);
        Assert.True(receiving.ApproveShipment(s2.Id).Ok);

        var svc = Svc<SupplierMovementService>(host);
        var all = svc.Compute(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), null, "");
        Assert.Equal(2, all.Count);                                   // مجموعتان معزولتان
        var g6 = all.First(g => Math.Abs(g.Rows.Sum(r => r.Purch) - 6000) < 1);
        var g2 = all.First(g => Math.Abs(g.Rows.Sum(r => r.Purch) - 2000) < 1);
        Assert.NotEqual(g6.Code, g2.Code);
        Assert.Equal(6000, g6.Totals[1], 1);                          // سطر الإجمالي = مجموع الأسطر
        Assert.Equal(6000, g6.Totals[7], 1);                          // المتبقي = الوارد − المصروف ± ...
        var only1 = svc.Compute(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), cust1, "");
        Assert.Single(only1);
        Assert.Equal(6000, only1[0].Rows.Sum(r => r.Purch), 1);
        var code = g6.Rows[0].Code;
        Assert.Single(svc.Compute(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), cust1, code));  // بحث الصنف داخل الطرف
        host.Dispose();
    }

    // ── §2: الفلاتر تغيّر النتائج فعلاً (فترة/عميل/صنف) ولا تبقى بيانات قديمة ──
    [Fact]
    public void Directive_06_Filters_Change_Results()
    {
        var (host, cust1, _, docNo) = ArrangeApprovedShipment();
        var inRange = Run(host, "receiving", Period("2026-08-01", "2026-08-31"));
        Assert.True(Has(inRange, 0, docNo));
        var outRange = Run(host, "receiving", Period("2026-07-01", "2026-07-31"));
        Assert.False(Has(outRange, 0, docNo));
        var byCust = Run(host, "receiving", new Dictionary<string, string> { ["from"] = "2026-08-01", ["to"] = "2026-08-31", ["customer"] = cust1.ToString() });
        Assert.True(Has(byCust, 0, docNo));
        var byOther = Run(host, "receiving", new Dictionary<string, string> { ["from"] = "2026-08-01", ["to"] = "2026-08-31", ["customer"] = "999999" });
        Assert.False(Has(byOther, 0, docNo));
        var byProd = Run(host, "receiving", new Dictionary<string, string> { ["from"] = "2026-08-01", ["to"] = "2026-08-31", ["product"] = "1" });
        Assert.True(Has(byProd, 0, docNo));
        var byProdOther = Run(host, "receiving", new Dictionary<string, string> { ["from"] = "2026-08-01", ["to"] = "2026-08-31", ["product"] = "3" });
        Assert.False(Has(byProdOther, 0, docNo));
        // فلتر الحركة بالعميل (إضافة الفحص الشامل)
        var movCust = CurrentPostingPeriod(); movCust["customer"] = cust1.ToString();
        Assert.True(Has(Run(host, "movements", movCust), 8, docNo));
        var movOtherPeriod = CurrentPostingPeriod(); movOtherPeriod["customer"] = "999999";
        var movOther = Run(host, "movements", movOtherPeriod);
        Assert.Empty(movOther.Rows);
        host.Dispose();
    }
}
