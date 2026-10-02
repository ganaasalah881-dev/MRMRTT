using DatesErp.Core.Common;
using DatesErp.Core.Domain.Enums;

namespace DatesErp.Core.Domain.Entities;

/// <summary>§9 — الرصيد الجاري لكل (مخزن، صنف، دفعة، عميل).</summary>
public class StockBalance : AuditableEntity
{
    public int WarehouseId { get; set; }
    public int? ProductId { get; set; }
    public int? MaterialId { get; set; }
    public int? LotId { get; set; }
    public int? CustomerId { get; set; }
    public int? PackagingTypeId { get; set; }
    public double QtyKg { get; set; }
    public int PackageCount { get; set; }
}

/// <summary>§9 — حركة مخزون مرتبطة دائماً بالمستند الذي أنشأها (لا حركة بدون مستند).</summary>
public class InventoryTransaction : AuditableEntity
{
    public string TxnNumber { get; set; }
    public DateTime TxnDate { get; set; } = DateTime.Now;
    public int WarehouseId { get; set; }
    public int? ProductId { get; set; }
    public int? MaterialId { get; set; }
    public int? LotId { get; set; }
    public int? CustomerId { get; set; }
    public int? OrderId { get; set; }
    public int? PackagingTypeId { get; set; }
    public MovementType MovementType { get; set; }
    public double QtyKg { get; set; }
    public int PackageCount { get; set; }
    public ReferenceDocType ReferenceDocType { get; set; }
    public string ReferenceDocNumber { get; set; }
    public bool IsApproved { get; set; }
    public string Notes { get; set; }
    /// <summary>§26/§9 — الجهاز الذي أنشأ الحركة.</summary>
    public string MachineName { get; set; }
}

// ═══════════════ §B10 — دورة الكرتون الفارغ: عدّ وبيع ═══════════════

/// <summary>سند عدّ فعلي للكرتون الفارغ — الفرق يُقيّد تسوية آلياً.</summary>
public class CartonCountDoc : WorkflowDocument
{
    public DateTime CountDate { get; set; } = DateTime.Now;
    public int WarehouseId { get; set; }
    // §CS0108: حُذفت Notes المكررة — WorkflowDocument.Notes هي المعتمدة
    public List<CartonCountItem> Items { get; set; } = new();
}

public class CartonCountItem : BaseEntity
{
    public int DocId { get; set; }
    public int ProductId { get; set; }
    public int BookCartons { get; set; }
    public int CountedCartons { get; set; }
    public int DiffCartons => CountedCartons - BookCartons;
}

/// <summary>سند بيع كرتون فارغ — يخصم الرصيد ويمنع البيع فوقه.</summary>
public class CartonSaleDoc : WorkflowDocument
{
    public DateTime SaleDate { get; set; } = DateTime.Now;
    public int? CustomerId { get; set; }
    public int WarehouseId { get; set; }
    public double PricePerCarton { get; set; }
    public double TotalAmount { get; set; }
    // §CS0108: حُذفت Notes المكررة — WorkflowDocument.Notes هي المعتمدة
    public List<CartonSaleItem> Items { get; set; } = new();
}

public class CartonSaleItem : BaseEntity
{
    public int DocId { get; set; }
    public int ProductId { get; set; }
    public int Cartons { get; set; }
    public double Amount { get; set; }
}
