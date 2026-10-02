namespace DatesErp.Core.Interfaces.Services;

public class ReceivingTreatmentStateDto
{
    public int ShipmentItemId { get; set; }
    public bool? TreatmentRequired { get; set; }
    public DateTime? UntilDate { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string StateAr { get; set; }
    public bool HasLegacyTreatment { get; set; }
}

/// <summary>اختيارات البنود المعلقة للسند اللاحق، تُحرر في الجدول نفسه قبل الحفظ.</summary>
public class ReceivingTreatmentChoiceDto
{
    public int ShipmentItemId { get; set; }
    public bool? TreatmentRequired { get; set; }
    public DateTime? UntilDate { get; set; }
}
