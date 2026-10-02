using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
namespace DatesErp.Application.Services;

/// <summary>Compatibility boundary only. The selectable/partial day-run route is retired.</summary>
public class DayRunService : ServiceBase, IDayRunService
{
    public DayRunService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering, IProductionOrderService orders)
        : base(db, session, numbering) { }
    public DayRunContextDto GetDayRun(int planId, string date)
    {
        Require("production", "View");
        throw new DomainException(ProductionOrderService.TodayOrdersOnlyMessage);
    }
    public OpResult IssueSelected(int planId, string date, List<DayRunIssueLineDto> lines)
    {
        Require("production", "Create");
        return OpResult.Fail(ProductionOrderService.TodayOrdersOnlyMessage);
    }
}
