using Microsoft.EntityFrameworkCore;
using DatesErp.Core.Common;
using DatesErp.Infrastructure.Persistence;
namespace DatesErp.Desktop.Views;

public class PlanningPrintModel
{
    public string CompanyNameAr { get; set; } = "شركة التمور";
    public string CompanyNameEn { get; set; } = "";
    public string Address { get; set; } = "";
    public string Phone { get; set; } = "";
    public byte[] LogoBytes { get; set; }

    public string PlanNumber { get; set; } = "";
    public string Title { get; set; } = "";
    public string PlanTypeAr { get; set; } = "";
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public bool IsApproved { get; set; }
    public string StatusAr { get; set; }
    public string ShiftName { get; set; } = "-";
    public string LineName { get; set; } = "-";
    public string Notes { get; set; } = "";
    public string CreatedByName { get; set; } = "-";

    public class ItemRow
    {
        public int RowNo { get; set; }
        public int? CustomerId { get; set; }
        public string CustomerName { get; set; } = "-";
        public string ShipmentNo { get; set; } = "-";
        public string LotCode { get; set; } = "-";
        public string RawName { get; set; } = "-";
        public string ProductName { get; set; } = "-";
        public string PackName { get; set; } = "-";
        public int Cartons { get; set; }
        public double QtyKg { get; set; }
        public string Date { get; set; } = "-";
        public string LineName { get; set; } = "-";
        public string ShiftName { get; set; } = "-";
    }

    public List<ItemRow> Items { get; set; } = new();

    public static PlanningPrintModel Load(DatesErpDbContext db, int planId)
    {
        using var tx = db.Database.CurrentTransaction == null ? db.Database.BeginTransaction(System.Data.IsolationLevel.Serializable) : null;
        var plan = db.ProductionPlans.AsNoTracking().Include(p => p.Items).FirstOrDefault(p => p.Id == planId);
        if (plan == null) return null;
        var co = db.CompanyInfos.AsNoTracking().OrderBy(x => x.Id).FirstOrDefault();

        var shiftId = plan.ShiftId;
        var lineId = plan.LineId;

        var m = new PlanningPrintModel
        {
            PlanNumber = plan.DocumentNumber,
            Title = plan.PlanTitle ?? "",
            StartDate = plan.StartDate,
            EndDate = plan.EndDate,
            IsApproved = plan.IsApproved,
            StatusAr = Printing.PrintStatus.Of(plan),
            CreatedByName = db.Users.Where(u => u.Id == plan.CreatedBy).Select(u => u.FullName).FirstOrDefault() ?? "—",
            ShiftName = db.Shifts.Where(s => s.Id == (shiftId ?? 0)).Select(s => s.ShiftNameAr).FirstOrDefault() ?? "-",
            LineName = db.ProductionLines.Where(l => l.Id == (lineId ?? 0)).Select(l => l.LineNameAr).FirstOrDefault() ?? "-",
            Notes = plan.Notes ?? "",
            // §إصلاح: كانت "Monthly" تسقط في _ فتُطبع الخطة الشهرية بعنوان «فترة محددة».
            // المطابقة الآن مع PlanClosureService.cs:41 — مصطلح واحد في كل المستندات.
            PlanTypeAr = plan.PlanType switch
            {
                "Daily" => "يومية", "Weekly" => "أسبوعية", "Monthly" => "شهرية", _ => "فترية"
            }
        };
        if (co != null)
        {
            m.CompanyNameAr = co.CompanyNameAr ?? m.CompanyNameAr;
            m.CompanyNameEn = co.CompanyNameEn ?? "";
            m.Address = co.Address ?? "";
            m.Phone = co.Phone ?? "";
            m.LogoBytes = co.LogoBytes;
        }

        int n = 1;
        foreach (var it in plan.Items.OrderBy(i => i.ScheduledDate ?? DateTime.MinValue).ThenBy(i => i.PriorityNo))
        {
            string Cust(int? cid) => db.Customers.Where(c => c.Id == (cid ?? 0)).Select(c => c.CustomerName).FirstOrDefault();
            string Prod(int pid) => db.Products.Where(p => p.Id == pid).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-";
            string LotCode(int? lid) => db.Lots.Where(l => l.Id == (lid ?? 0)).Select(l => l.LotCode).FirstOrDefault() ?? "-";
            int? lotShip = it.ShipmentId ?? db.Lots.Where(l => l.Id == (it.LotId ?? 0)).Select(l => l.ShipmentId).FirstOrDefault();

            m.Items.Add(new ItemRow
            {
                RowNo = n++,
                CustomerId = it.CustomerId,
                CustomerName = Cust(it.CustomerId) ?? "-",
                ShipmentNo = db.Shipments.Where(s => s.Id == (lotShip ?? 0)).Select(s => s.DocumentNumber).FirstOrDefault() ?? "-",
                LotCode = LotCode(it.LotId),
                RawName = db.Lots.Where(l => l.Id == (it.LotId ?? 0)).Select(l => l.ProductId).FirstOrDefault() is int rp
                            ? db.Products.Where(p => p.Id == rp).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-" : "-",
                ProductName = Prod(it.ProductId),
                PackName = it.PackagingTypeId != null
                    ? db.PackagingTypes.Where(p_ => p_.Id == it.PackagingTypeId).Select(p_ => p_.PackageNameAr).FirstOrDefault() ?? "-"
                    : "-",
                Cartons = it.PlannedCartons,
                QtyKg = it.PlannedQtyKg,
                Date = UiFormat.D(it.ScheduledDate),
                LineName = db.ProductionLines.Where(l => l.Id == (it.SuggestedLineId ?? plan.LineId ?? 0)).Select(l => l.LineNameAr).FirstOrDefault() ?? "—",
                ShiftName = db.Shifts.Where(s => s.Id == (it.SuggestedShiftId ?? plan.ShiftId ?? 0)).Select(s => s.ShiftNameAr).FirstOrDefault() ?? "-"
            });
        }
        tx?.Commit();
        return m;
    }
}
