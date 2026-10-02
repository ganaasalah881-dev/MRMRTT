using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §المعالجة والتعقيم — **الاختبار التنفيذي الفعلي** بسيناريو المستخدم (البند 7).
///
/// 5,000 سلة × 20 كجم = 100,000 كجم، تُقسَّم إلى ثلاثة أجزاء **بلا إنشاء صنف جديد**:
/// 4,000 سلة جاهزة فوراً · 500 سلة معالجة 7 أيام · 500 سلة معالجة 10 أيام.
///
/// نقاط التحقق السبع التي طلبها المستخدم مغطاة هنا، كل واحدة باختبار مستقل يحمل رقمها.
/// </summary>
public class RawTreatmentScenarioTests
{
    private const double BasketKg = 20;
    private const int TotalBaskets = 5000;

    /// <summary>حالة السيناريو المشتركة: دفعة مستلمة فعلياً عبر دورة الاستلام الحقيقية.</summary>
    private sealed class Scenario
    {
        public TestHost Host;
        public int LotId;
        public IRawTreatmentService Trt;
        public DatesErpDbContext Db;
        public int WhRaw, WhTrt;
    }

    /// <summary>
    /// تهيئة السيناريو عبر **الخدمات الحقيقية** (استلام ⟵ اعتماد ⟵ دفعة)، لا بحقن
    /// صفوف يدوياً — وإلا اختُبر النظام على بيانات لا ينتجها هو.
    /// </summary>
    private static Scenario Setup(bool requiresTreatment = true)
    {
        var host = new TestHost();
        host.LoginAsAdmin();
        var db = host.Get<DatesErpDbContext>();

        // الصنف الخام يشترط معالجة (قرار المستخدم س3: علم على الصنف)
        var raw = db.Products.First(p => p.ProductCode == "001-001");
        raw.RequiresTreatment = requiresTreatment;
        db.SaveChanges();

        var receiving = host.Get<IReceivingService>();
        var r = receiving.SaveShipment(1, "2026-09-01", "2026-09-01", new List<ShipmentItemDto>
        {
            new() { TreatmentRequired = false, ProductId = raw.Id, PackagingTypeId = 3, PackageCount = TotalBaskets,
                    UnitWeightKg = BasketKg, QtyKg = TotalBaskets * BasketKg, ReceiptUnit = "سلة" }
        });
        Assert.True(r.Ok, r.Message);
        Assert.True(receiving.ApproveShipment(r.Id).Ok);
        // Fixture تاريخي: هذه الاختبارات تحافظ على محرك السجلات السابقة، لا عقد الاستلام الجديد.
        foreach (var i in db.ShipmentItems.Where(i => i.ShipmentId == r.Id)) i.TreatmentRequired = null;
        foreach (var l in db.Lots.Where(l => l.ShipmentId == r.Id)) l.TreatmentReadyQtyKg = 0;
        db.SaveChanges();

        var lot = db.Lots.OrderBy(l => l.Id).Last();
        Assert.Equal(100000, lot.InStockQtyKg, 1);

        return new Scenario
        {
            Host = host,
            LotId = lot.Id,
            Trt = host.Get<IRawTreatmentService>(),
            Db = db,
            WhRaw = db.Warehouses.Single(w => w.WarehouseCode == "WRM").Id,
            WhTrt = db.Warehouses.Single(w => w.WarehouseCode == "WTRT").Id
        };
    }

    private static double Bal(Scenario s, int warehouseId) => s.Db.StockBalances
        .Where(b => b.WarehouseId == warehouseId && b.LotId == s.LotId)
        .Sum(b => b.QtyKg);

    private static Lot Reload(Scenario s)
    {
        s.Db.ChangeTracker.Clear();
        return s.Db.Lots.First(l => l.Id == s.LotId);
    }

    /// <summary>بدء معالجة بتاريخ ماضٍ — لاختبار نضج المدة بلا انتظار حقيقي.</summary>
    private static int StartAgo(Scenario s, double baskets, double durationHours, double startedDaysAgo)
    {
        var r = s.Trt.Start(new TreatmentStartDto
        {
            LotId = s.LotId,
            QtyKg = baskets * BasketKg,
            PackageCount = (int)baskets,
            DurationHours = durationHours,
            StartedAt = DateTime.Now.AddDays(-startedDaysAgo)
        });
        Assert.True(r.Ok, r.Message);
        return r.Id;
    }

    // ═══════════ نقطة التحقق 1: ما تحت المعالجة لا يُستخدم في الإنتاج ═══════════

    [Fact]
    public void P1_UnderTreatment_Is_Not_Available_For_Production()
    {
        var sc = Setup();
        using (sc.Host)
        {
            // 1,000 سلة تدخل المعالجة (500 + 500) — 4,000 تبقى جاهزة
            StartAgo(sc, 500, 168, 0);
            StartAgo(sc, 500, 240, 0);

            var lot = Reload(sc);
            Assert.Equal(20000, lot.UnderTreatmentQtyKg, 1);   // 1,000 سلة
            Assert.Equal(80000, lot.AvailableQtyKg, 1);        // 4,000 سلة فقط
            Assert.Equal(100000, lot.InStockQtyKg, 1);         // المخزون لم ينقص

            var st = sc.Trt.GetLotState(sc.LotId);
            Assert.Equal(20000, st.UnderTreatmentQtyKg, 1);
            Assert.Equal(80000, st.NotTreatedQtyKg, 1);
            Assert.Equal(0, st.ReadyQtyKg, 1);
        }
    }

    /// <summary>لا يمكن إدخال كمية أكبر من المتاح — ولا معالجة كمية محجوزة لخطة قائمة.</summary>
    [Fact]
    public void P1b_Cannot_Treat_More_Than_Eligible()
    {
        var sc = Setup();
        using (sc.Host)
        {
            var over = sc.Trt.Start(new TreatmentStartDto
            {
                LotId = sc.LotId, QtyKg = 120000, PackageCount = 6000, DurationHours = 24
            });
            Assert.False(over.Ok);
            Assert.Contains("تتجاوز المتاح", over.Message);

            // ولا يتراكم البدء فوق الرصيد
            StartAgo(sc, 4000, 24, 0);
            var second = sc.Trt.Start(new TreatmentStartDto
            {
                LotId = sc.LotId, QtyKg = 40000, PackageCount = 2000, DurationHours = 24
            });
            Assert.False(second.Ok);
        }
    }

    // ═══════════ نقطتا التحقق 2 و3: الجاهزية بعد 7 أيام وبعد 10 ═══════════

    [Fact]
    public void P2_P3_Release_Blocked_Before_Duration_Then_Allowed()
    {
        var sc = Setup();
        using (sc.Host)
        {
            // بدأتا قبل 8 أيام: الأولى (7 أيام) نضجت، والثانية (10 أيام) لم تنضج بعد
            int t7 = StartAgo(sc, 500, 168, 8);
            int t10 = StartAgo(sc, 500, 240, 8);

            // الأولى: أُفرج عنها بنجاح
            var ok = sc.Trt.Release(t7, 500 * BasketKg);
            Assert.True(ok.Ok, ok.Message);

            // الثانية: مرفوضة — لم تكتمل مدتها
            var early = sc.Trt.Release(t10, 500 * BasketKg);
            Assert.False(early.Ok);
            Assert.Contains("لم تكتمل مدة المعالجة", early.Message);

            var lot = Reload(sc);
            Assert.Equal(10000, lot.TreatmentReadyQtyKg, 1);   // 500 سلة جاهزة
            Assert.Equal(10000, lot.UnderTreatmentQtyKg, 1);   // 500 لا تزال تحت المعالجة
            Assert.Equal(90000, lot.AvailableQtyKg, 1);        // 4,500 سلة

            // موعد الجاهزية محسوب تلقائياً = البدء + المدة
            var e10 = sc.Db.RawTreatments.AsNoTracking().First(x => x.Id == t10);
            Assert.Equal(e10.StartedAt.AddHours(240), e10.ExpectedReadyAt);
        }
    }

    /// <summary>بعد مرور 11 يوماً تنضج الثانية أيضاً ويكتمل الـ5,000.</summary>
    [Fact]
    public void P3_Second_Batch_Ready_After_Ten_Days()
    {
        var sc = Setup();
        using (sc.Host)
        {
            int t7 = StartAgo(sc, 500, 168, 11);
            int t10 = StartAgo(sc, 500, 240, 11);

            Assert.True(sc.Trt.Release(t7, 500 * BasketKg).Ok);
            Assert.True(sc.Trt.Release(t10, 500 * BasketKg).Ok);

            var lot = Reload(sc);
            Assert.Equal(20000, lot.TreatmentReadyQtyKg, 1);
            Assert.Equal(0, lot.UnderTreatmentQtyKg, 1);
            Assert.Equal(100000, lot.AvailableQtyKg, 1);       // كامل الـ5,000 سلة

            Assert.All(sc.Db.RawTreatments.AsNoTracking().ToList(),
                t => Assert.Equal(TreatmentStatuses.Released, t.Status));
        }
    }

    // ═══════════ نقطة التحقق 4: المتاح حسب تاريخ الإنتاج ═══════════

    [Fact]
    public void P4_Available_Depends_On_Production_Date()
    {
        var sc = Setup();
        using (sc.Host)
        {
            // تبدآن اليوم: الأولى تنضج بعد 7 أيام، والثانية بعد 10
            StartAgo(sc, 500, 168, 0);
            StartAgo(sc, 500, 240, 0);

            var today = DateTime.Now.Date;

            // بعد 3 أيام: لا شيء نضج — 4,000 سلة فقط (الجاهز الآن صفر + غير المعالَج)
            // ملاحظة: الجاهز الآن = 0، فالمتاح للتخطيط في هذا التاريخ يعتمد على النضج وحده
            Assert.Equal(0, sc.Trt.GetAvailableForDate(sc.LotId, today.AddDays(3)), 1);

            // بعد 8 أيام: الأولى نضجت ⟵ 500 سلة
            Assert.Equal(10000, sc.Trt.GetAvailableForDate(sc.LotId, today.AddDays(8)), 1);

            // بعد 11 يوماً: الاثنتان ⟵ 1,000 سلة
            Assert.Equal(20000, sc.Trt.GetAvailableForDate(sc.LotId, today.AddDays(11)), 1);
        }
    }

    /// <summary>الصنف الذي لا يشترط معالجة لا يتأثر إطلاقاً (قرار س3).</summary>
    [Fact]
    public void P4b_Product_Without_RequiresTreatment_Is_Unaffected()
    {
        var sc = Setup(requiresTreatment: false);
        using (sc.Host)
        {
            var today = DateTime.Now.Date;
            // كامل المخزون متاح في أي تاريخ — لا اشتراط معالجة
            Assert.Equal(100000, sc.Trt.GetAvailableForDate(sc.LotId, today), 1);
            Assert.Equal(100000, sc.Trt.GetAvailableForDate(sc.LotId, today.AddDays(30)), 1);
        }
    }

    // ═══════════ نقطة التحقق 5: الحجز يمنع الاستخدام المزدوج ═══════════

    [Fact]
    public void P5_Reservation_Prevents_Double_Use_And_Blocks_Treating_Reserved()
    {
        // §B103 — السيناريو بلا شرط معالجة على الخام: غرض الاختبار تفاعل الحجز مع المعالجة،
        // وبوابة «خام يشترط معالجة» تمنع اعتماد الخطة أصلاً قبل المعالجة (سلوك صحيح بحد ذاته).
        var sc = Setup(requiresTreatment: false);
        using (sc.Host)
        {
            var planning = sc.Host.Get<IPlanningService>();
            var p = planning.SavePlan("خطة المعالجة", "Daily", "2026-09-20", "2026-09-20", 1, 1,
                new List<PlanItemDto>
                {
                    new() { SourceType = "FromReceiving", LotId = sc.LotId, CustomerId = 1, ProductId = 3,
                            PlannedQtyKg = 22500, PlannedCartons = 3000, ScheduledDate = "2026-09-20",
                            SuggestedShiftId = 1, SuggestedLineId = 1, PriorityNo = 1 }
                });
            Assert.True(p.Ok, p.Message);
            Assert.True(planning.ApprovePlan(p.Id).Ok);

            var lot = Reload(sc);
            Assert.Equal(22500, lot.ReservedQtyKg, 1);
            Assert.Equal(77500, lot.AvailableQtyKg, 1);

            // §المحجوز لخطة معتمدة لا يجوز إدخاله المعالجة: كان سيعطّل خطة قائمة بلا إنذار
            var grab = sc.Trt.Start(new TreatmentStartDto
            {
                LotId = sc.LotId, QtyKg = 90000, PackageCount = 4500, DurationHours = 24
            });
            Assert.False(grab.Ok);
            Assert.Contains("المحجوز لخطط", grab.Message);

            // بينما غير المحجوز يُقبل
            Assert.True(sc.Trt.Start(new TreatmentStartDto
            {
                LotId = sc.LotId, QtyKg = 77500, PackageCount = 3875, DurationHours = 24
            }).Ok);
        }
    }

    // ═══════════ نقطة التحقق 6: تطابق المخزون — لا نقص ولا تكرار ═══════════

    /// <summary>
    /// **الثابت الحاكم:** رصيد(WRM) + رصيد(WTRT) = InStockQtyKg بعد كل عملية.
    /// يُفحص بعد الاستلام والبدء والإفراج الجزئي والإفراج الكامل والإلغاء.
    /// </summary>
    [Fact]
    public void P6_Warehouse_Balances_Always_Equal_Lot_Stock()
    {
        var sc = Setup();
        using (sc.Host)
        {
            void AssertBalanced(string stage)
            {
                var lot = Reload(sc);
                double sum = Bal(sc, sc.WhRaw) + Bal(sc, sc.WhTrt);
                Assert.True(Math.Abs(sum - lot.InStockQtyKg) < 0.01,
                    $"اختل التوازن عند «{stage}»: WRM+WTRT = {sum:N1} بينما InStock = {lot.InStockQtyKg:N1}");
            }

            AssertBalanced("بعد الاستلام");
            Assert.Equal(100000, Bal(sc, sc.WhRaw), 1);
            Assert.Equal(0, Bal(sc, sc.WhTrt), 1);

            int t = StartAgo(sc, 1000, 168, 8);
            AssertBalanced("بعد بدء المعالجة");
            Assert.Equal(80000, Bal(sc, sc.WhRaw), 1);
            Assert.Equal(20000, Bal(sc, sc.WhTrt), 1);

            Assert.True(sc.Trt.Release(t, 500 * BasketKg).Ok);
            AssertBalanced("بعد إفراج جزئي");
            Assert.Equal(90000, Bal(sc, sc.WhRaw), 1);
            Assert.Equal(10000, Bal(sc, sc.WhTrt), 1);

            Assert.True(sc.Trt.Release(t, 500 * BasketKg).Ok);
            AssertBalanced("بعد الإفراج الكامل");
            Assert.Equal(100000, Bal(sc, sc.WhRaw), 1);
            Assert.Equal(0, Bal(sc, sc.WhTrt), 1);

            // ولا تكرار في الحركات: أربع حركات بدء/إفراج على مخزنين
            int moves = sc.Db.InventoryTransactions.Count(x =>
                x.ReferenceDocType == ReferenceDocType.TreatmentStart ||
                x.ReferenceDocType == ReferenceDocType.TreatmentRelease);
            Assert.Equal(6, moves); // بدء (2) + إفراج أول (2) + إفراج ثانٍ (2)
        }
    }

    /// <summary>الإفراج الجزئي على دفعات (البند 5): 500 من 1,000 والباقي تحت المعالجة.</summary>
    [Fact]
    public void P5b_Partial_Release_Keeps_Remainder_Under_Treatment()
    {
        var sc = Setup();
        using (sc.Host)
        {
            int t = StartAgo(sc, 1000, 168, 8);

            var r = sc.Trt.Release(t, 500 * BasketKg);
            Assert.True(r.Ok, r.Message);
            Assert.Contains("إفراج جزئي", r.Message);

            var trt = sc.Db.RawTreatments.AsNoTracking().First(x => x.Id == t);
            Assert.Equal(TreatmentStatuses.InProgress, trt.Status); // لم تكتمل
            Assert.Equal(10000, trt.ReleasedQtyKg, 1);
            Assert.Equal(10000, trt.RemainingQtyKg, 1);

            var lot = Reload(sc);
            Assert.Equal(10000, lot.TreatmentReadyQtyKg, 1);
            Assert.Equal(10000, lot.UnderTreatmentQtyKg, 1);

            // إفراج أكثر من المتبقي مرفوض
            var over = sc.Trt.Release(t, 600 * BasketKg);
            Assert.False(over.Ok);
            Assert.Contains("تتجاوز المتبقي", over.Message);
        }
    }

    /// <summary>الرفض ينقص المخزون فعلاً (إتلاف) ويظل التوازن قائماً.</summary>
    [Fact]
    public void P6b_Rejection_Reduces_Stock_And_Keeps_Balance()
    {
        var sc = Setup();
        using (sc.Host)
        {
            int t = StartAgo(sc, 1000, 168, 8);
            Assert.True(sc.Trt.Reject(t, 200 * BasketKg, "تلف حراري").Ok);

            var lot = Reload(sc);
            Assert.Equal(96000, lot.InStockQtyKg, 1);          // نقص 200 سلة فعلياً
            Assert.Equal(4000, lot.WastageQtyKg, 1);
            Assert.Equal(16000, lot.UnderTreatmentQtyKg, 1);   // 800 سلة باقية
            Assert.Equal(Bal(sc, sc.WhRaw) + Bal(sc, sc.WhTrt), lot.InStockQtyKg, 1);
        }
    }

    /// <summary>إلغاء بدء خاطئ يعيد كل شيء كما كان — ولا يُسمح به بعد أي إفراج.</summary>
    [Fact]
    public void Cancel_Reverses_Start_But_Not_After_Release()
    {
        var sc = Setup();
        using (sc.Host)
        {
            int t = StartAgo(sc, 500, 168, 8);
            Assert.True(sc.Trt.Cancel(t, "خطأ في إدخال الكمية").Ok);

            var lot = Reload(sc);
            Assert.Equal(0, lot.UnderTreatmentQtyKg, 1);
            Assert.Equal(100000, lot.InStockQtyKg, 1);
            Assert.Equal(100000, Bal(sc, sc.WhRaw), 1);
            Assert.Equal(0, Bal(sc, sc.WhTrt), 1);

            int t2 = StartAgo(sc, 500, 168, 8);
            Assert.True(sc.Trt.Release(t2, 100 * BasketKg).Ok);
            var late = sc.Trt.Cancel(t2, "متأخر");
            Assert.False(late.Ok);
            Assert.Contains("أُفرج", late.Message);
        }
    }

    // ═══════════ نقطة التحقق 7: التتبع ═══════════

    /// <summary>
    /// سلسلة التتبع: الشحنة ⟵ الاستلام ⟵ الدفعة ⟵ المعالجة ⟵ الجاهزية.
    /// كل معالجة تحمل LotId وProductId المنسوخين من الدفعة — **بلا صنف جديد**.
    /// </summary>
    [Fact]
    public void P7_Full_Traceability_Chain_Is_Intact()
    {
        var sc = Setup();
        using (sc.Host)
        {
            int t7 = StartAgo(sc, 500, 168, 11);
            StartAgo(sc, 500, 240, 11);
            Assert.True(sc.Trt.Release(t7, 500 * BasketKg).Ok);

            var lot = sc.Db.Lots.AsNoTracking().First(l => l.Id == sc.LotId);
            var treatments = sc.Trt.GetByLot(sc.LotId);
            Assert.Equal(2, treatments.Count);

            // الشحنة والدفعة والصنف: سلسلة متصلة بلا انقطاع
            Assert.NotNull(lot.ShipmentId);
            Assert.All(treatments, t =>
            {
                Assert.Equal(sc.LotId, t.LotId);
                Assert.Equal(lot.ProductId, t.ProductId);   // §لا صنف جديد لكل مدة
                Assert.False(string.IsNullOrWhiteSpace(t.TreatmentNo));
            });

            // كل حركة مخزون مرتبطة بالدفعة ورقم المعالجة
            var moves = sc.Db.InventoryTransactions.AsNoTracking()
                .Where(x => x.ReferenceDocType == ReferenceDocType.TreatmentStart
                         || x.ReferenceDocType == ReferenceDocType.TreatmentRelease)
                .ToList();
            Assert.NotEmpty(moves);
            Assert.All(moves, m =>
            {
                Assert.Equal(sc.LotId, m.LotId);
                Assert.Contains(treatments, t => m.ReferenceDocNumber == t.TreatmentNo
                    || m.ReferenceDocNumber.StartsWith(t.TreatmentNo + "/")); // pure document numbers, no fixed TRT prefix
            });
        }
    }

    /// <summary>تقرير المعالجات المتأخرة يلتقط ما تجاوز موعده ولم يُفرج.</summary>
    [Fact]
    public void Overdue_Report_Lists_Only_Late_InProgress()
    {
        var sc = Setup();
        using (sc.Host)
        {
            int late = StartAgo(sc, 500, 168, 20);   // تأخرت 13 يوماً
            StartAgo(sc, 500, 240, 0);               // جديدة، غير متأخرة

            var overdue = sc.Trt.Search(onlyOverdue: true);
            Assert.Single(overdue);
            Assert.Equal(late, overdue[0].Id);
            Assert.True(overdue[0].IsOverdue);

            // بعد الإفراج تخرج من التقرير — وإلا تراكمت فيه إلى الأبد
            Assert.True(sc.Trt.Release(late, 500 * BasketKg).Ok);
            Assert.Empty(sc.Trt.Search(onlyOverdue: true));
        }
    }
}
