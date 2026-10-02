using DatesErp.Core.Common;

namespace DatesErp.Core.Domain.Entities;

/// <summary>§7 — خطة الإنتاج.</summary>
public class ProductionPlan : WorkflowDocument
{
    /// <summary>§B75: نطاق التخطيط يُحفظ في الرأس ويُستعاد عند الفتح (Multi | Single).</summary>
    public string ScopeMode { get; set; } = "Multi";
    /// <summary>§B75: عميل الخطة المحددة إن كان النطاق «عميل محدد».</summary>
    public int? SingleCustomerId { get; set; }
    public string PlanTitle { get; set; }
    public string PlanType { get; set; } = "Daily"; // Daily | Period
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public int? ShiftId { get; set; }
    public int? LineId { get; set; }
    public bool IsClosed { get; set; }
    public DateTime? ClosedDate { get; set; }
    /// <summary>§B79: المستخدم الذي أقفل الخطة.</summary>
    public int? ClosedBy { get; set; }

    public List<ProductionPlanItem> Items { get; set; } = new();
}

/// <summary>Required, persisted setup for NEW sequenced plans; legacy plans have no such row.</summary>
public class ProductionPlanContext : AuditableEntity
{
    public int PlanId { get; set; }
    /// <summary>Single | SelectedMulti | Fair. The actual customer/shift IDs live in child tables.</summary>
    public string CustomerMode { get; set; } = "Single";
    public int LineId { get; set; }
}

public class ProductionPlanScopeCustomer : BaseEntity
{
    public int PlanId { get; set; }
    public int CustomerId { get; set; }
}

public class ProductionPlanSelectedShift : BaseEntity
{
    public int PlanId { get; set; }
    public int ShiftId { get; set; }
    public int SequenceNo { get; set; }
}

/// <summary>One independently completed work card per operational date and selected shift.</summary>
public class ProductionPlanWorkSlot : AuditableEntity
{
    public int PlanId { get; set; }
    public DateTime WorkDate { get; set; }
    public int ShiftId { get; set; }
    public int SequenceNo { get; set; }
    public string State { get; set; } = "Pending"; // Pending | Editing | Ready | NoProductionApproved | NeedsReview
    public DateTime? CompletedAt { get; set; }
    public int? CompletedBy { get; set; }
    public string ExceptionReason { get; set; }
}

/// <summary>Human-readable revision snapshot; never delete this when replacing unissued lines.</summary>
public class ProductionPlanRevision : BaseEntity
{
    public int PlanId { get; set; }
    public int? WorkSlotId { get; set; }
    public int RevisionNo { get; set; }
    public DateTime ChangedAt { get; set; }
    public int? ChangedBy { get; set; }
    public string Action { get; set; }
    public string Reason { get; set; }
    public string BeforeJson { get; set; }
    public string AfterJson { get; set; }
}

/// <summary>Approved input kilograms per finished carton, scoped to product/raw/packaging.
/// A zero PackagingTypeId denotes a generic rule; no implicit 1:1 or YieldFactor fallback.</summary>
public class ProductRawPlanningRule : AuditableEntity
{
    public int ProductId { get; set; }
    public int RawProductId { get; set; }
    public int PackagingTypeId { get; set; }
    public decimal RawKgPerCarton { get; set; }
    public bool IsActive { get; set; } = true;
    public int? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string ApprovalReason { get; set; }
}

public class ProductionPlanItem : BaseEntity
{
    public int PlanId { get; set; }
    /// <summary>Only new sequenced plans use this link; null for every historical item.</summary>
    public int? WorkSlotId { get; set; }
    /// <summary>Packaging specification frozen for sequenced plans; zero for historic rows.</summary>
    public int MoldsCount { get; set; }
    public decimal MoldWeightKg { get; set; }
    public string SourceType { get; set; } = "Manual"; // Manual | FromReceiving
    public int? LotId { get; set; }
    public int? ShipmentId { get; set; } // §المرجع الكامل: الشحنة التي منها الدفعة
    public int? CustomerId { get; set; }
    public int ProductId { get; set; }
    public int? PackagingTypeId { get; set; }
    public double PlannedQtyKg { get; set; }
    public int PlannedCartons { get; set; }
    public DateTime? ScheduledDate { get; set; }
    public int? SuggestedShiftId { get; set; }
    public int? SuggestedLineId { get; set; }
    public int PriorityNo { get; set; }
    public string Status { get; set; } = DocStatuses.Draft;
    // §الخطة الطويلة: تقدم مستقل لكل بند (عميل/صنف/يوم/وردية)
    public double ProducedQtyKg { get; set; }
    public double AcceptedQtyKg { get; set; }
    public double DeliveredQtyKg { get; set; }
    public string ExecutionStatus { get; set; } = "NotStarted"; // NotStarted | InProgress | Partial | Completed
    /// <summary>
    /// §إصلاح حرج: البند أُقفل نهائياً على ما أُنتج.
    /// كان الإقفال يكتب PlannedQtyKg = ProducedQtyKg فيمحو أساس الخطة، فتظهر كل خطة
    /// بنسبة إنجاز 100% في التقارير ويضيع ما كان مخططاً أصلاً. الآن يُحرَّر الحجز
    /// بهذا العلم ويظل المخطط محفوظاً للمقارنة.
    /// </summary>
    public bool IsClosed { get; set; }
    /// <summary>§ما حُرر من الحجز عند الإقفال المبكر (المخطط − المنتَج) — للتدقيق.</summary>
    public double ReleasedQtyKg { get; set; }

    // ═══ §تتبع سحب الخام من الشحنة (تشغيل الإصدار الجديد) ═══
    /// <summary>وحدة السحب كما دخلت بها الشحنة (سلة/كرتون/كجم) — من ShipmentItem.ReceiptUnit.</summary>
    public string SourceUnit { get; set; }
    /// <summary>الكمية المسحوبة من الشحنة بوحدة الاستلام (عدد السلال/الكراتين).</summary>
    public double SourceQtyInUnit { get; set; }
    /// <summary>وزن وحدة الاستلام (كجم) وقت سحب البند — يُجمَّد لتتبع مستقر.</summary>
    public double SourceUnitWeightKg { get; set; }
    /// <summary>الوزن المكافئ = SourceQtyInUnit × SourceUnitWeightKg.</summary>
    public double SourceQtyKg { get; set; }
}

/// <summary>§7 — أمر الإنتاج.</summary>
public class ProductionOrder : WorkflowDocument
{
    public string SourceType { get; set; } = "Manual"; // Manual | FromPlan
    public int? SourcePlanId { get; set; }
    public int? CustomerId { get; set; }
    public DateTime? ProductionDate { get; set; }
    public int? ShiftId { get; set; }
    public int? LineId { get; set; }
    public bool IsClosed { get; set; }
    public DateTime? ClosedDate { get; set; }
    /// <summary>§B95 — سبب إغلاق الأمر بعجز (تسوية موثقة): فارغ عند الإغلاق باكتمال الإنتاج.</summary>
    public string CloseReason { get; set; }

    public List<ProductionOrderItem> Items { get; set; } = new();
    public List<ProductionOrderMaterial> Materials { get; set; } = new();
}

public class ProductionOrderItem : BaseEntity
{
    public int OrderId { get; set; }
    public int? PlanItemId { get; set; }
    public int? LotId { get; set; }
    public int? ShipmentId { get; set; } // يُنقل كما هو من سطر الخطة
    public int? CustomerId { get; set; } // ملكية السطر محفوظة لكل سطر
    public int ProductId { get; set; }
    public int? PackagingTypeId { get; set; }
    public double PlannedQtyKg { get; set; }
    public int PlannedCartons { get; set; }
    public double ProducedQtyKg { get; set; }
    public int ProducedCartons { get; set; }
    public string Status { get; set; } = DocStatuses.Draft;
    /// <summary>§إصلاح حرج: بند الأمر أُقفل نهائياً — يُحرر الحجز دون محو المخطط.</summary>
    public bool IsClosed { get; set; }
    /// <summary>§نظام الوحدات: وزن الكرتون المعتمد وقت إنشاء الأمر — يجمّد التعريف اللاحق للعبوة
    /// بأثر رجعي (إن تغير وزن الكرتون غداً لا تتغير عمليات الأمس).</summary>
    public double CartonWeightKg { get; set; }
    /// <summary>
    /// §حفظ التعريف تاريخياً كاملاً: القاعدة توجب حفظ عدد القوالب ووزن القالب وقت العملية
    /// لا وزن الكرتون وحده — لأن تعريف المنتج قد يتغير لاحقاً.
    /// (decimal لا double: قاعدة CI تمنع إضافة double جديد في الكيانات.)
    /// </summary>
    public int MoldsCount { get; set; }
    public decimal MoldWeightKg { get; set; }
}

/// <summary>§7 — المواد المحتسبة/المصروفة لأمر الإنتاج.</summary>
public class ProductionOrderMaterial : BaseEntity
{
    public int OrderId { get; set; }
    /// <summary>المادة المساعدة القديمة (AuxiliaryMaterial) — للتوافق.</summary>
    public int MaterialId { get; set; }
    /// <summary>§1.50.63 — الصنف المساعد الجديد (ProductId) — أساسي في النظام الجديد.</summary>
    public int? AuxiliaryProductId { get; set; }
    public double CalculatedQty { get; set; }
    public double ActualIssuedQty { get; set; }
    public double ConsumedQty { get; set; }
    public double WastedQty { get; set; }
    public double ReturnedQty { get; set; }
    public string UnitOfMeasure { get; set; }
    public bool IsAutoCalculated { get; set; } = true;
    public string Status { get; set; } = DocStatuses.Draft; // Draft | Issued | Consumed
    /// <summary>§1.50.63 — الكمية المطلوبة (واضحة للمستخدم).</summary>
    // §1.50.63 — Required/Remaining تُحسب في الخدمة من CalculatedQty و ActualIssuedQty بلا حقل جديد (قاعدة CI تمنع double جديد)
}

/// <summary>§7 — جلسة تنفيذ الإنتاج.</summary>
public class ProductionExecution : WorkflowDocument
{
    public int OrderId { get; set; }
    public int? LineId { get; set; }
    public int? ShiftId { get; set; }
    public DateTime? StartDateTime { get; set; }
    public DateTime? EndDateTime { get; set; }
    public double ActualQtyKg { get; set; }
    public int ActualCartons { get; set; }
    public double WastageQtyKg { get; set; }

    /// <summary>§نموذج الإقفال اليومي: ماذا خرج من الصالة.</summary>
    public double HashfKg { get; set; }
    public double NawaKg { get; set; }
    /// <summary>كم الخام استُهلك (صُرف عند اعتماد الأمر).</summary>
    public double ConsumedRawKg { get; set; }
    /// <summary>المتبقي في صالة الإنتاج = الخام المستهلك − المخرجات.</summary>
    public double RemainingInHallKg { get; set; }
    /// <summary>ترحيل المتبقي في الصالة إلى خطة اليوم التالي.</summary>
    public bool CarryToNextDay { get; set; }
    /// <summary>أُرسل للجودة — الفحص متوقع بعد يومَي تبريد.</summary>
    public bool QualitySent { get; set; }
    public DateTime? ExpectedQualityDate { get; set; }
    /// <summary>هل أُقفل يوم الإنتاج لهذا الأمر (لا إقفالين على نفس الأمر).</summary>
    public bool IsDayClosed { get; set; }
    public string ClosingNotes { get; set; }

    public List<ExecutionDowntime> Downtimes { get; set; } = new();

    /// <summary>
    /// §لا ثوابت: المخرجات الثانوية الفعلية لهذه الجلسة بأصنافها من جدول ByProducts.
    /// العمودان HashfKg/NawaKg أعلاه بقيا للبيانات السابقة فقط — الجديد يُسجَّل هنا.
    /// </summary>
    public List<ExecutionByProduct> ByProducts { get; set; } = new();
}

/// <summary>§مخرج ثانوي مسجَّل لإقفال يوم إنتاج — الصنف من جدول ByProducts لا من الكود.</summary>
public class ExecutionByProduct : BaseEntity
{
    public int ExecutionId { get; set; }
    public int ByProductId { get; set; }
    /// <summary>§decimal لا double — قاعدة CI: لا حقول double جديدة في الكيانات.</summary>
    public decimal Qty { get; set; }
}

/// <summary>§الإقفال اليومي: بند توقف (كم ساعة ولماذا) — «توقفنا عدد كذا ساعات بسبب كذا».</summary>
public class ExecutionDowntime : BaseEntity
{
    /// <summary>إن كان التوقف على جلسة/إقفال أمر.</summary>
    public int? ExecutionId { get; set; }
    /// <summary>إن كان التوقف على مستند إقفال خطة.</summary>
    public int? ClosingId { get; set; }
    public double Hours { get; set; }
    public string ReasonAr { get; set; }
    /// <summary>
    /// §السجل الزمني للتوقف (قالب الطباعة المرجعي): وقت التوقف ووقت الاستئناف.
    /// نصّان حرّان (HH:mm) لأن الدقة المطلوبة دقيقة لا لحظة — ويُطبعان كما أُدخلا.
    /// </summary>
    public string StartTime { get; set; }
    public string EndTime { get; set; }
}

/// <summary>
/// §مستند إقفال الخطة: تسليم الخطة كاملة بأصنافها أو صنف واحد أو جزء من صنف،
/// لكل صنف: كم خاماً استُلم، كم كرتوناً أُنتج تاماً، المخرجات الثانوية، والمتبقي
/// الذي يُعاد لمخزن الخام بنفس العميل والدفعة. يعمل للخطة اليومية والفترية.
/// </summary>
public class PlanClosing : WorkflowDocument
{
    public int PlanId { get; set; }
    public DateTime? ClosingDate { get; set; }
    public bool QualitySent { get; set; }
    /// <summary>§B57: الكمية المرسلة للفحص يختارها المستخدم عند الإقفال (كجم).</summary>
    public double SentToQualityKg { get; set; }
    public DateTime? ExpectedQualityDate { get; set; }
    public string ClosingNotes { get; set; }
    /// <summary>§B10 تقدير النظام للكراتين الفارغة (المستهلك ÷ وزن كرتون الخام).</summary>
    public double EmptyCartonsEstimated { get; set; }
    /// <summary>§B10 الكراتين الفارغة الفعلية المؤكَدة من المشرف (فارغ = التقدير).</summary>
    public double? EmptyCartonsActual { get; set; }
    public List<PlanClosingItem> Items { get; set; } = new();
    public List<ExecutionDowntime> Downtimes { get; set; } = new();
}

/// <summary>§بند إقفال الخطة: حساب كامل لصنف واحد (أو جزء منه) عند الإقفال.</summary>
public class PlanClosingItem : BaseEntity
{
    public int ClosingId { get; set; }
    public int PlanItemId { get; set; }
    public int? OrderId { get; set; }
    public int? LotId { get; set; }
    public int? CustomerId { get; set; }
    public int ProductId { get; set; }
    /// <summary>كم خاماً استُلم/استُهلك لهذا البند.</summary>
    public double ConsumedRawKg { get; set; }
    /// <summary>كم أُنتج تاماً (كجم).</summary>
    public double ProducedKg { get; set; }
    /// <summary>كم كرتوناً أُنتج.</summary>
    public int ProducedCartons { get; set; }
    /// <summary>§المخرجات الثانوية الديناميكية لهذا البند (أصنافها من إعدادات الأصناف).</summary>
    public List<PlanClosingByProduct> ByProducts { get; set; } = new();
    /// <summary>§نظام الوحدات: وزن الكرتون وقت الإقفال — لا يتغير بتعريف العبوة لاحقاً.</summary>
    public double CartonWeightKg { get; set; }
    /// <summary>§تعريف التعبئة كاملاً وقت الإقفال (قوالب × وزن قالب) — تاريخي لا يتغير.</summary>
    public int MoldsCount { get; set; }
    public decimal MoldWeightKg { get; set; }
    public double HashfKg { get; set; }
    public double NawaKg { get; set; }
    public double WastageKg { get; set; }
    /// <summary>المتبقي المُعاد لمخزن الخام بنفس العميل والدفعة.</summary>
    public double ReturnedToRawKg { get; set; }
}
