namespace DatesErp.Core.Interfaces.Services;

/// <summary>Inputs selected BEFORE adding any product. These are frozen on the created plan.</summary>
public sealed class SequencedPlanSetup
{
    public string Title { get; set; }
    public string CustomerMode { get; set; } = "Single"; // Single | SelectedMulti | Fair
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public List<int> CustomerIds { get; set; } = new();
    public List<int> ShiftIds { get; set; } = new(); // operating order, not database Id order
    public int LineId { get; set; }
}

public sealed class SequencedPlanItemInput
{
    public int? ItemId { get; set; }
    public int LotId { get; set; }
    public int ProductId { get; set; }
    public int PackagingTypeId { get; set; }
    public int Cartons { get; set; }
}

public sealed class SequencedLotOption
{
    public int LotId { get; set; }
    public int CustomerId { get; set; }
    public string CustomerName { get; set; }
    public string LotCode { get; set; }
    public DateTime? ArrivalDate { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; }
    public int PackagingTypeId { get; set; }
    public string PackagingName { get; set; }
    public double CartonWeightKg { get; set; }
    public int MoldsCount { get; set; }
    public decimal MoldWeightKg { get; set; }
    public decimal RawKgPerCarton { get; set; }
    public long MaximumCartons { get; set; }
    public long TimeMaximumCartons { get; set; }
    public long LineMaximumCartons { get; set; }
    public string Reason { get; set; }
}

public sealed class SequencedDistributionProposal
{
    /// <summary>Suggested only. The planner must review and SaveSlot; nothing is persisted here.</summary>
    public List<SequencedPlanItemInput> Items { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public sealed class SequencedQuantityQuote
{
    public int LotId { get; set; }
    public int ProductId { get; set; }
    public long TimeMaximumCartons { get; set; }
    public long LineMaximumCartons { get; set; }
    public long RawMaximumCartons { get; set; }
    public long ProductShiftMaximumCartons { get; set; }
    public long MaximumCartons { get; set; }
    public double RatePerHour { get; set; }
    public decimal RawKgPerCarton { get; set; }
    public double CartonWeightKg { get; set; }
    public string LimitingReason { get; set; }
    public string Error { get; set; }
    public bool IsValid => Error == null && MaximumCartons > 0;
}
