using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// §B100 — متاح العملاء (قراءة فقط): لكل عميل عبر الخطط المعتمدة، بما فيها
/// الخطط التي أُقفلت بعد إصدار تسليم إنتاجها؛ فالإقفال لا يعني شحن البضاعة للعميل.
/// المخطط/المنتج/في الفحص/المقبول/المسلَّم + «المقبول المتبقي = مقبول − مسلَّم».
/// النوافذ: بطاقة في «مهامي» + تفاصيل (أيام/دفعات مخزن التام/سجل التسليم).
/// </summary>
public class CustomerAvailabilityService : ServiceBase, ICustomerAvailabilityService
{
    public CustomerAvailabilityService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering)
    {
    }

    public List<CustomerAvailabilityDto> GetBoardSummary()
    {
        var today = DateTime.Today;
        var items = ActivePlanItemsByCustomer();
        if (items.Count == 0) return new List<CustomerAvailabilityDto>();

        return items
            .OrderByDescending(g => g.Value.Any(i => i.ScheduledDate != null && i.ScheduledDate.Value.Date < today
                && i.DeliveredQtyKg + 0.001 < i.PlannedQtyKg))
            .ThenByDescending(g => Math.Max(0, g.Value.Sum(i => i.AcceptedQtyKg) - g.Value.Sum(i => i.DeliveredQtyKg)))
            .Select(g => BuildSummary(g.Key, g.Value, today))
            .ToList();
    }

    public CustomerAvailabilityDetailDto GetCustomerAvailability(int customerId)
    {
        var today = DateTime.Today;
        var customer = Db.Customers.AsNoTracking().FirstOrDefault(c => c.Id == customerId)
                       ?? throw new Core.Exceptions.DomainException("العميل غير موجود.");
        // إقفال خطة الإنتاج بعد إصدار تسليم التام لا يخفي التزام الشحن
        // للعميل ولا يحول المقبول غير المشحون إلى صفر في التقرير.
        var planIds = Db.ProductionPlans.AsNoTracking()
            .Where(p => p.IsApproved && p.Status != DocStatuses.Cancelled).Select(p => p.Id).ToList();
        var items = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.CustomerId == customerId && planIds.Contains(i.PlanId))
            .ToList();

        double planned = items.Sum(i => i.PlannedQtyKg);
        double produced = items.Sum(i => i.ProducedQtyKg);
        double accepted = items.Sum(i => i.AcceptedQtyKg);
        double delivered = items.Sum(i => i.DeliveredQtyKg);

        var detail = new CustomerAvailabilityDetailDto
        {
            CustomerId = customerId,
            CustomerName = customer.CustomerName,
            PlannedKg = planned,
            ProducedKg = produced,
            InInspectionKg = Math.Max(0, produced - accepted),
            AcceptedKg = accepted,
            DeliveredKg = delivered,
            DeliverableKg = Math.Max(0, accepted - delivered),
            Overdue = items.Any(i => i.ScheduledDate != null && i.ScheduledDate.Value.Date < today
                && i.DeliveredQtyKg + 0.001 < i.PlannedQtyKg)
        };

        // ── بالأيام (نفس منطق سجل التنفيذ — لكل يوم العميل) ──
        detail.Days = items.Where(i => i.ScheduledDate != null)
            .GroupBy(i => i.ScheduledDate.Value.Date)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                double p = g.Sum(i => i.PlannedQtyKg);
                double pr = g.Sum(i => i.ProducedQtyKg);
                double ac = g.Sum(i => i.AcceptedQtyKg);
                double dl = g.Sum(i => i.DeliveredQtyKg);
                bool isPast = g.Key < today;
                string status;
                if (p <= 0) status = "—";
                else if (dl + 0.001 >= p) status = "مكتمل ✅";
                else if (pr + 0.001 >= p) status = "مُنتَج — بانتظار التسليم 🟡";
                else if (pr > 0) status = isPast ? "جزئي 🟠" : "قيد التنفيذ 🔵";
                else status = isPast ? "متعثر ⏰" : "لم يبدأ ⚪";
                return new CustomerAvailabilityDayDto
                {
                    Date = g.Key.ToString("dd/MM/yyyy"),
                    PlannedKg = p,
                    ProducedKg = pr,
                    AcceptedKg = ac,
                    DeliveredKg = dl,
                    Overdue = isPast && !(dl + 0.001 >= p && p > 0),
                    StatusAr = status
                };
            }).ToList();

        // ── الدفعات المتاحة فعلياً في مخزن التام (بالحركة، لا بالمحاسبة) ──
        int wfg = WarehouseId("WFG");
        var products = Db.Products.AsNoTracking().ToDictionary(x => x.Id, x => x.ProductNameAr);
        var packs = Db.PackagingTypes.AsNoTracking().ToDictionary(x => x.Id, x => x.PackageNameAr);
        var lots = Db.Lots.AsNoTracking().ToDictionary(x => x.Id, x => x.LotCode);
        detail.Stocks = Db.StockBalances.AsNoTracking()
            .Where(b => b.WarehouseId == wfg && b.CustomerId == customerId && b.QtyKg > 0.001)
            .OrderBy(b => b.ProductId).ThenBy(b => b.LotId ?? 0)
            .ToList()   // §سحب: الإسقاط في الذاكرة — TryGetValue/out و?. غير قابلة للترجمة في شجرة EF
            .Select(b => new CustomerLotStockDto
            {
                ProductName = products.TryGetValue(b.ProductId ?? 0, out var pn) ? pn : $"#{b.ProductId}",
                PackName = b.PackagingTypeId != null && packs.TryGetValue(b.PackagingTypeId.Value, out var pk) ? pk : "—",
                LotCode = b.LotId != null && lots.TryGetValue(b.LotId.Value, out var lc) ? lc : "—",
                QtyKg = b.QtyKg
            }).ToList();

        // ── سجل التسليم (الأحدث أولاً) ──
        detail.Deliveries = Db.CustomerDeliveries.AsNoTracking()
            .Where(d => d.CustomerId == customerId)
            .OrderByDescending(d => d.Id).Take(15)
            .ToList()   // §سحب: الإسقاط في الذاكرة (?. غير مترجم في EF)
            .Select(d => new CustomerDeliveryHistoryDto
            {
                Date = d.DeliveryDate?.ToString("dd/MM/yyyy") ?? "—",
                DocumentNumber = d.DocumentNumber,
                QtyKg = d.TotalQtyKg,
                Cartons = d.TotalCartons,
                StatusAr = d.IsApproved ? "مُعتمد ✅" : DocStatuses.ToArabic(d.Status)
            }).ToList();

        return detail;
    }

    private CustomerAvailabilityDto BuildSummary(int customerId, List<ProductionPlanItem> items, DateTime today)
    {
        double planned = items.Sum(i => i.PlannedQtyKg);
        double produced = items.Sum(i => i.ProducedQtyKg);
        double accepted = items.Sum(i => i.AcceptedQtyKg);
        double delivered = items.Sum(i => i.DeliveredQtyKg);
        bool overdue = items.Any(i => i.ScheduledDate != null && i.ScheduledDate.Value.Date < today
            && i.DeliveredQtyKg + 0.001 < i.PlannedQtyKg);
        string status =
            planned > 0 && delivered + 0.001 >= planned ? "مكتمل ✅"
            : Math.Max(0, accepted - delivered) > 0.001 ? "قابل للتسليم ⚡"
            : Math.Max(0, produced - accepted) > 0.001 ? "في الفحص 🔍"
            : produced > 0.001 ? "مُنتَج — بانتظار الفحص 🏭"
            : "لم يبدأ ⚪";
        return new CustomerAvailabilityDto
        {
            CustomerId = customerId,
            CustomerName = Db.Customers.AsNoTracking().Where(c => c.Id == customerId).Select(c => c.CustomerName).FirstOrDefault() ?? $"#{customerId}",
            PlannedKg = planned,
            ProducedKg = produced,
            InInspectionKg = Math.Max(0, produced - accepted),
            AcceptedKg = accepted,
            DeliveredKg = delivered,
            DeliverableKg = Math.Max(0, accepted - delivered),
            Overdue = overdue,
            StatusAr = overdue ? "⏰ متعثر — " + status : status
        };
    }

    /// <summary>بنود العملاء عبر الخطط المعتمدة؛ انتهاء الإنتاج لا ينهي شحن العميل.</summary>
    private Dictionary<int, List<ProductionPlanItem>> ActivePlanItemsByCustomer()
    {
        var planIds = Db.ProductionPlans.AsNoTracking()
            .Where(p => p.IsApproved && p.Status != DocStatuses.Cancelled).Select(p => p.Id).ToList();
        if (planIds.Count == 0) return new Dictionary<int, List<ProductionPlanItem>>();
        var items = Db.ProductionPlanItems.AsNoTracking()
            .Where(i => planIds.Contains(i.PlanId) && i.CustomerId != null)
            .ToList();
        return items.GroupBy(i => i.CustomerId.Value).ToDictionary(g => g.Key, g => g.ToList());
    }
}
