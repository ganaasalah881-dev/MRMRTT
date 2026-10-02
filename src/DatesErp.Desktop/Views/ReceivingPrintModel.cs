using Microsoft.EntityFrameworkCore;
using DatesErp.Infrastructure.Persistence;
namespace DatesErp.Desktop.Views;

public class ReceivingPrintModel
{
    public string CompanyNameAr { get; set; } = "شركة التمور";
    public string CompanyNameEn { get; set; } = "";
    public string Address { get; set; } = "";
    public string Phone { get; set; } = "";
    public byte[] LogoBytes { get; set; }

    public string DocumentNumber { get; set; } = "";
    public bool IsApproved { get; set; }
    public string StatusAr { get; set; }
    public DateTime CapturedAt { get; set; } = DateTime.Now;
    public string CustomerName { get; set; } = "-";
    public string ContainerNumber { get; set; } = "-";
    public string VesselName { get; set; } = "";
    public DateTime? ArrivalDate { get; set; }
    public DateTime? ReceivedDate { get; set; }
    public string EmployeeName { get; set; } = "-";
    public string WarehouseName { get; set; } = "-";
    public string Notes { get; set; } = "";

    public class ItemRow
    {
        public int RowNo { get; set; }
        public string ProductCode { get; set; } = "-";
        public string ProductName { get; set; } = "-";
        public string ReceiptUnit { get; set; } = "-";
        public string PackName { get; set; } = "-";
        public int PackageCount { get; set; }
        public double UnitWeightKg { get; set; }
        public double QtyKg { get; set; }
        public bool? TreatmentRequired { get; set; }
        public DateTime? TreatmentUntilDate { get; set; }
        public DateTime? TreatmentCompletedAt { get; set; }
        public string TreatmentState(DateTime now) => TreatmentRequired == null ? "غير محدد (سجل سابق)"
            : TreatmentRequired == false ? "لا يحتاج معالجة"
            : TreatmentUntilDate == null ? "تاريخ المعالجة غير محدد"
            : now.Date < TreatmentUntilDate.Value.Date ? "قيد المعالجة — لم يحل الموعد"
            : TreatmentCompletedAt != null && TreatmentCompletedAt.Value.Date < TreatmentUntilDate.Value.Date ? "تعارض تاريخ الإتمام — راجع السجل"
            : TreatmentCompletedAt != null ? $"مكتملة: {TreatmentCompletedAt:dd/MM/yyyy HH:mm}"
            : "حل الموعد — راجع حالة الاستلام والمزامنة";
    }

    public List<ItemRow> Items { get; set; } = new();

    /// <summary>§تحميل بيانات السند كاملة من قاعدة البيانات (بما فيها أسماء العرض) جاهزة للطباعة.</summary>
    public static ReceivingPrintModel Load(DatesErpDbContext db, int shipmentId)
    {
        using var tx = db.Database.CurrentTransaction == null ? db.Database.BeginTransaction(System.Data.IsolationLevel.Serializable) : null;
        var ship = db.Shipments.AsNoTracking().Include(s => s.Items).FirstOrDefault(s => s.Id == shipmentId);
        if (ship == null) return null;

        var co = db.CompanyInfos.AsNoTracking().OrderBy(x => x.Id).FirstOrDefault();
        var model = new ReceivingPrintModel
        {
            DocumentNumber = ship.DocumentNumber,
            CapturedAt = db.BusinessNow,
            IsApproved = ship.IsApproved,
            StatusAr = Printing.PrintStatus.Of(ship),
            CustomerName = db.Customers.Where(c => c.Id == ship.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "-",
            ContainerNumber = string.IsNullOrWhiteSpace(ship.ContainerNumber) ? "-" : ship.ContainerNumber,
            VesselName = ship.VesselName ?? "",
            ArrivalDate = ship.ArrivalDate,
            ReceivedDate = ship.ReceivedDate,
            WarehouseName = db.Warehouses.Where(w => w.Id == (ship.ReceivingWarehouseId ?? 0)).Select(w => w.WarehouseNameAr).FirstOrDefault()
                             ?? db.Warehouses.Where(w => w.WarehouseCode == "WRM").Select(w => w.WarehouseNameAr).FirstOrDefault() ?? "مخزن الخام",
            Notes = ship.Notes ?? ""
        };
        if (co != null)
        {
            model.CompanyNameAr = co.CompanyNameAr ?? model.CompanyNameAr;
            model.CompanyNameEn = co.CompanyNameEn ?? "";
            model.Address = co.Address ?? "";
            model.Phone = co.Phone ?? "";
            model.LogoBytes = co.LogoBytes;
        }
        var emp = ship.ReceivedBy != null
            ? db.Employees.Where(e => e.Id == ship.ReceivedBy).Select(e => e.FullName).FirstOrDefault()
            : null;
        model.EmployeeName = emp ?? "-";

        int n = 1;
        foreach (var it in ship.Items.OrderBy(i => i.Id))
        {
            model.Items.Add(new ItemRow
            {
                RowNo = n++,
                ProductCode = db.Products.Where(p => p.Id == it.ProductId).Select(p => p.ProductCode).FirstOrDefault() ?? "-",
                ProductName = db.Products.Where(p => p.Id == it.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                ReceiptUnit = it.ReceiptUnit ?? "-",
                PackName = it.PackagingTypeId != null
                    ? db.PackagingTypes.Where(p => p.Id == it.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault() ?? "-"
                    : "-",
                PackageCount = it.PackageCount,
                UnitWeightKg = it.UnitWeightKg,
                QtyKg = it.TotalWeightKg,
                TreatmentRequired = it.TreatmentRequired, TreatmentUntilDate = it.TreatmentUntilDate, TreatmentCompletedAt = it.TreatmentCompletedAt
            });
        }
        tx?.Commit();
        return model;
    }
}
