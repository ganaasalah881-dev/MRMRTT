namespace DatesErp.Core.Interfaces.Services;

/// <summary>الخطط المعتمدة المجدولة هي نموذج قراءة، وليست نموذج إدخال أو تعديل أمر.</summary>
public sealed class TodayProductionDto
{
    public DateTime Day { get; init; }
    public string Message { get; init; }
    public IReadOnlyList<TodayProductionRowDto> Rows { get; init; } = Array.Empty<TodayProductionRowDto>();
    public bool CanIssue { get; init; }
}

public sealed class TodayProductionRowDto : System.ComponentModel.INotifyPropertyChanged
{
    public int PlanId { get; init; }
    public int PlanItemId { get; init; }
    public string PlanNumber { get; init; }
    public int? CustomerId { get; init; }
    public string CustomerName { get; init; }
    public int ProductId { get; init; }
    public string ProductName { get; init; }
    public int PlannedCartons { get; init; }
    public double PlannedKg { get; init; }
    public string Unit { get; init; } = "كرتون";
    public int? ShiftId { get; init; }
    public string ShiftName { get; init; }
    public int? LineId { get; init; }
    public string LineName { get; init; }
    public int? OrderId { get; init; }
    public string OrderNumber { get; init; }
    /// <summary>تاريخ الجدولة المنقول من بند الخطة؛ يعرض أيضاً عند استعراض الفترات السابقة والقادمة.</summary>
    public string ScheduledDate { get; init; }
    public bool IsToday { get; init; }
    public string Status { get; init; }
    public bool IsPending { get; init; }
    /// <summary>§v1.50.24: يوم هذا الأمر أُقفل (سُجل فعليه) — يغادر قائمة «أمر إنتاج اليوم».</summary>
    public bool DayClosed { get; init; }
    // اختيار الإصدار فقط؛ الأصناف والكميات المنقولة من الخطة للعرض ولا تُعدّل من هذه الشاشة.
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { _isSelected = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected))); } }
    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
}

/// <summary>§v1.50.34 — مجموعة يوم معتمدة بلا أمر بعد (زر «إضافة أمر من الخطة»).</summary>
public class TodayPendingGroupDto
{
    public int PlanId { get; init; }
    public string PlanNumber { get; init; }
    /// <summary>تاريخ البند المجدول؛ قد يكون سابقاً أو اليوم أو مستقبلياً في «إضافة من الخطة».</summary>
    public string ScheduledDate { get; init; }
    public int? CustomerId { get; init; }
    public string CustomerName { get; init; }
    public int? ShiftId { get; init; }
    public string ShiftName { get; init; }
    public int? LineId { get; init; }
    public string LineName { get; init; }
    public int ItemsCount { get; init; }
    public int Cartons { get; init; }
    public double PlannedKg { get; init; }
}

/// <summary>§v1.50.34 — صف نتيجة بحث أوامر الإنتاج.</summary>
public class OrderSearchRowDto
{
    public int OrderId { get; init; }
    public string DocumentNumber { get; init; }
    public string ProductionDate { get; init; }
    public string CustomerName { get; init; }
    public string StatusAr { get; init; }
}
