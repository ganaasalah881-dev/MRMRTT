using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §أمر الإنتاج — عكس الصرف عند الإلغاء/إلغاء الاعتماد.
///
/// الثغرة المُصلحة: ReverseConsumption كانت تُعيد PlannedQtyKg إلى رصيد الدفعة
/// ومخزن الخام، بافتراض أن الاعتماد خصم الخام. لكن ApproveOrder **لا يخصم الخام
/// إطلاقاً** — قاعدة توازن الإنتاج تصرفه عند الإقفال بالمستهلك الفعلي (ConsumeLot).
/// فكان كل إلغاء يخلق مخزوناً من العدم، ويتضاعف بتكرار العملية.
/// </summary>
public class OrderReversalAuditTests
{
    private const int Cust1 = 1;
    private const int RawKhalas = 1;   // 001-001 خام
    private const int FinKhalas = 3;   // 002-001 كرتون 7.5 كجم
    private const int Shift1 = 1, Line1 = 1;

    private static T Svc<T>(TestHost host) => (T)host.Services.CreateScope().ServiceProvider.GetService(typeof(T));
    private static DatesErpDbContext FreshDb(TestHost host)
        => new DatesErpDbContext(new DbContextOptionsBuilder<DatesErpDbContext>().UseSqlite(host.Connection).Options);

    private static int ReceiveLot(TestHost host, double qtyKg)
    {
        var receiving = Svc<IReceivingService>(host);
        var s = receiving.SaveShipment(Cust1, null, null, new List<ShipmentItemDto>
        {
            new() { TreatmentRequired = false, ProductId = RawKhalas, PackagingTypeId = 3,
                    PackageCount = (int)(qtyKg / 20), UnitWeightKg = 20, QtyKg = qtyKg }
        });
        Assert.True(s.Ok, s.Message);
        Assert.True(receiving.ApproveShipment(s.Id).Ok);
        using var db = FreshDb(host);
        return db.Lots.OrderBy(l => l.Id).Last().Id;
    }

    /// <summary>أمر معتمد جاهز للتنفيذ على دفعة محددة.</summary>
    private static (int orderId, int lotId) ApprovedOrder(TestHost host, int cartons)
    {
        int lot = ReceiveLot(host, 100000);
        var orders = Svc<IProductionOrderService>(host);
        var o = TestOrderFixture.PlanThenOrder(host, Cust1, "01/10/2026", Shift1, Line1,
            new List<OrderItemDto>
            {
                new() { LotId = lot, CustomerId = Cust1, ProductId = FinKhalas,
                        PlannedCartons = cartons, PlannedQtyKg = cartons * 7.5 }
            });
        Assert.True(o.Ok, o.Message);
        Assert.True(orders.ApproveOrder(o.Id).Ok);
        return (o.Id, lot);
    }

    private static (double inStock, double produced) LotState(TestHost host, int lotId)
    {
        using var db = FreshDb(host);
        var l = db.Lots.First(x => x.Id == lotId);
        return (l.InStockQtyKg, l.ProducedQtyKg);
    }

    // ═══════════════════ الثغرة: مخزون من العدم ═══════════════════

    /// <summary>
    /// الاعتماد لا يخصم الخام (قاعدة توازن الإنتاج) — فالإلغاء يجب ألا يضيف شيئاً.
    /// قبل الإصلاح كان رصيد الدفعة يرتفع بالمخطط كاملاً من العدم.
    /// </summary>
    [Fact]
    public void CancelOrder_Does_Not_Fabricate_Raw_Stock()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (orderId, lot) = ApprovedOrder(host, 1000);

        var before = LotState(host, lot);
        Assert.True(Svc<IProductionOrderService>(host).CancelOrder(orderId, "إلغاء اختبار").Ok);
        var after = LotState(host, lot);

        Assert.Equal(before.inStock, after.inStock, 1);
        Assert.Equal(before.produced, after.produced, 1);
    }

    /// <summary>إلغاء الاعتماد كذلك لا يضيف خاماً لم يُخصم.</summary>
    [Fact]
    public void UnapproveOrder_Does_Not_Fabricate_Raw_Stock()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (orderId, lot) = ApprovedOrder(host, 1000);
        var orders = Svc<IProductionOrderService>(host);

        var before = LotState(host, lot);
        Assert.True(orders.UnapproveOrder(orderId).Ok);
        var after = LotState(host, lot);

        Assert.Equal(before.inStock, after.inStock, 1);
        Assert.Equal(before.produced, after.produced, 1);
    }

    /// <summary>
    /// التكرار كان يضاعف الوهم: اعتماد ⟵ إلغاء اعتماد ⟵ اعتماد ⟵ إلغاء اعتماد.
    /// كل دورة كانت تضيف المخطط مرة أخرى بلا سقف.
    /// </summary>
    [Fact]
    public void Repeated_Approve_Unapprove_Keeps_Stock_Constant()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (orderId, lot) = ApprovedOrder(host, 1000);
        var orders = Svc<IProductionOrderService>(host);

        double baseline = LotState(host, lot).inStock;

        for (int i = 0; i < 3; i++)
        {
            Assert.True(orders.UnapproveOrder(orderId).Ok);
            Assert.True(orders.ApproveOrder(orderId).Ok);
            Assert.Equal(baseline, LotState(host, lot).inStock, 1);
        }
    }

    /// <summary>الرصيد لا يتجاوز المستلم أصلاً — الفحص الذي يرصد الخلق من العدم.</summary>
    [Fact]
    public void Stock_Never_Exceeds_Received_After_Cancellation()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (orderId, lot) = ApprovedOrder(host, 2000);

        Assert.True(Svc<IProductionOrderService>(host).CancelOrder(orderId, "إلغاء").Ok);

        using var db = FreshDb(host);
        var l = db.Lots.First(x => x.Id == lot);
        Assert.True(l.InStockQtyKg <= l.InitialQtyKg + 0.001,
            $"الرصيد {l.InStockQtyKg:N1} تجاوز المستلم {l.InitialQtyKg:N1} — خُلق مخزون من العدم");
    }

    // ═══════════════════ حارس الإلغاء بعد التنفيذ ═══════════════════

    /// <summary>
    /// أمر أُقفل يومه جزئياً تبقى حالته Scheduled (لا Completed إلا باكتمال كل البنود)،
    /// فكان يمرّ من حارس الحالة ويُلغى رغم استهلاك خامه فعلياً — ويُحذف قيد الاستهلاك
    /// من دفتر الحركة. الحارس الآن على وجود جلسة تنفيذ لا على الحالة وحدها.
    /// </summary>
    [Fact]
    public void CancelOrder_Rejected_When_Execution_Sessions_Exist()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (orderId, lot) = ApprovedOrder(host, 2000);   // 15,000 كجم مخطط

        // إقفال يوم جزئي: 1,000 كرتون من 2,000 ⟵ الأمر يبقى غير مكتمل
        var exec = Svc<IExecutionService>(host);
        var close = exec.CloseProductionDay(orderId, 7500, 1000, 0, 0, 0,
            false, null, false, null, consumedRawKg: 7500);
        Assert.True(close.Ok, close.Message);

        var stateAfterClose = LotState(host, lot);

        var cancel = Svc<IProductionOrderService>(host).CancelOrder(orderId, "محاولة إلغاء");
        Assert.False(cancel.Ok);
        Assert.Contains("بعد بدء", cancel.Message); // جلسة الإقفال تمنع إلغاء الأمر ولو كان جزئياً

        // ولم يتغير شيء على الدفعة نتيجة المحاولة المرفوضة.
        var stateAfterReject = LotState(host, lot);
        Assert.Equal(stateAfterClose.inStock, stateAfterReject.inStock, 1);
        Assert.Equal(stateAfterClose.produced, stateAfterReject.produced, 1);
    }

    /// <summary>الاستهلاك الفعلي عند الإقفال يخصم الخام — وهذا هو المسار الصحيح الوحيد.</summary>
    [Fact]
    public void Raw_Is_Deducted_At_Closing_Not_At_Approval()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (orderId, lot) = ApprovedOrder(host, 1000);

        // بعد الاعتماد: لا خصم
        Assert.Equal(100000, LotState(host, lot).inStock, 1);

        var close = Svc<IExecutionService>(host).CloseProductionDay(
            orderId, 7500, 1000, 0, 0, 0, false, null, false, null, consumedRawKg: 5000);
        Assert.True(close.Ok, close.Message);

        // بعد الإقفال: يُخصم المستهلك الفعلي (5,000) لا المخطط (7,500)
        var after = LotState(host, lot);
        Assert.Equal(95000, after.inStock, 1);
        Assert.Equal(5000, after.produced, 1);
    }

    // ═══════════════════ المواد المساعدة تبقى تُعكس ═══════════════════

    /// <summary>
    /// المواد المساعدة **تُصرف فعلاً** عند الاعتماد، فعكسها عند الإلغاء سلوك صحيح
    /// يجب ألا يسقط مع الإصلاح.
    /// </summary>
    [Fact]
    public void Auxiliary_Materials_Are_Still_Reversed_On_Cancel()
    {
        using var host = new TestHost();
        host.LoginAsAdmin();
        var (orderId, _) = ApprovedOrder(host, 1000);

        using (var db = FreshDb(host))
        {
            var issued = db.ProductionOrderMaterials.Where(m => m.OrderId == orderId).ToList();
            if (issued.Count == 0 || issued.All(m => m.ActualIssuedQty <= 0))
                return; // لا مواد معرَّفة لهذا الصنف في البذر — لا شيء يُقاس
        }

        Assert.True(Svc<IProductionOrderService>(host).CancelOrder(orderId, "إلغاء").Ok);

        using (var db = FreshDb(host))
        {
            var mats = db.ProductionOrderMaterials.Where(m => m.OrderId == orderId).ToList();
            Assert.All(mats, m => Assert.Equal(0, m.ActualIssuedQty, 1));
            Assert.All(mats, m => Assert.Equal(DocStatuses.Draft, m.Status));
        }
    }
}
