namespace DatesErp.Core.Interfaces.Services;

/// <summary>Read-only capacity projection. Cartons are comparable only at a common rate.</summary>
public sealed class PlanCapacityResult
{
    public List<PlanCapacityRow> Rows { get; } = new();
    public List<PlanCapacitySlot> Slots { get; } = new();
    public string Error { get; set; }
    public bool IsValid => string.IsNullOrEmpty(Error);
    public double TotalHours => Slots.Sum(s => s.TotalHours);
    public double UsedHours => Slots.Sum(s => s.UsedHours);
    public double RemainingHours => Slots.Sum(s => Math.Max(0, s.TotalHours - s.UsedHours));
    public double UsagePercent => TotalHours > 0 ? UsedHours / TotalHours * 100 : 0;
    public double RemainingPercent => TotalHours > 0 ? RemainingHours / TotalHours * 100 : 0;
    public string Summary
    {
        get
        {
            var rates = Slots.Select(s => s.DisplayRate).Distinct().ToList();
            bool cartons = rates.Count == 1 && rates[0] > 0;
            double factor = cartons ? rates[0] : 1;
            string unit = cartons ? "كرتون" : "ساعة إنتاج فعلية";
            return $"الطاقة الكلية: {TotalHours * factor:N2} {unit} | المستخدم: {UsedHours * factor:N2} | المتبقي: {RemainingHours * factor:N2} | الاستخدام: {UsagePercent:N2}% | المتبقي: {RemainingPercent:N2}%";
        }
    }
}

public sealed class PlanCapacitySlot
{
    public DateTime Day { get; set; }
    public int ShiftId { get; set; }
    public int LineId { get; set; }
    public double TotalHours { get; set; }
    public double OtherHours { get; set; }
    public double DraftHours { get; set; }
    public double UsedHours => OtherHours + DraftHours;
    public double DisplayRate { get; set; }
    public string Label => $"{Day:dd/MM/yyyy} · وردية {ShiftId} · خط {LineId}";
}

public sealed class PlanCapacityRow
{
    public int Index { get; set; }
    public double Rate { get; set; }
    public int Quantity { get; set; }
    public double ProductCapacity { get; set; }
    public double RequiredHours { get; set; }
    public double UsagePercent { get; set; }
    public long MaximumCartons { get; set; }
    public long ExcessCartons => Math.Max(0L, (long)Quantity - MaximumCartons);
    public long RemainingCartons => Math.Max(0L, MaximumCartons - Quantity);
    public double RemainingPercent { get; set; }
    public string Error { get; set; }
}
