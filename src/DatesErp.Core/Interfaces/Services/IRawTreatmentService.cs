using DatesErp.Core.Domain.Entities;

namespace DatesErp.Core.Interfaces.Services;

/// <summary>
/// §المعالجة والتعقيم — دورة معالجة الخام قبل دخوله الإنتاج (المرحلة 2).
///
/// **مصدر الحقيقة للحالة هو <see cref="RawTreatment"/> على مستوى الدفعة**، ومستودع
/// WTRT أثر محاسبي تابع. السبب أن الدفعة تنقسم إلى أجزاء بمُدد مختلفة، والمستودع
/// يحمل رصيداً واحداً بلا تاريخ جاهزية فيعجز عن الإجابة عن «كم يجهز يوم كذا».
///
/// **ثابت التوازن الذي تحرسه كل عملية هنا:**
/// <c>رصيد(WRM) + رصيد(WTRT) = Lot.InStockQtyKg</c> — لا ازدواجية ولا اختفاء.
/// </summary>
public interface IRawTreatmentService
{
    /// <summary>
    /// بدء معالجة على **جزء** من دفعة: نقل الكمية من مخزن الخام إلى مستودع المعالجة.
    /// يُحسب موعد الجاهزية تلقائياً = وقت البدء + المدة.
    /// تُستدعى عدة مرات على الدفعة نفسها بمُدد مختلفة (4,000 + 500×7أيام + 500×10أيام).
    /// </summary>
    OpResult Start(TreatmentStartDto dto);

    /// <summary>
    /// الإفراج بعد اكتمال المدة — **كلي أو جزئي** (500 من 1,000). الكمية تعود من
    /// مستودع المعالجة إلى الخام وتصبح متاحة للإنتاج.
    /// </summary>
    /// <summary>§B106: وجهة الإفراج — "WRM" مخزن الخام (افتراضي) أو "WFG" مخزن التام.</summary>
    OpResult Release(int treatmentId, double qtyKg, string notes = null, string destWarehouseCode = "WRM");

    /// <summary>رفض كمية فاشلة: تخرج من مستودع المعالجة إلى الهدر ولا تعود للخام.</summary>
    OpResult Reject(int treatmentId, double qtyKg, string reason);

    /// <summary>إلغاء بدء خاطئ: عكس كامل للحركة وإرجاع الكمية إلى الخام كما كانت.</summary>
    OpResult Cancel(int treatmentId, string reason);

    /// <summary>معالجات دفعة بعينها — عمود التتبع.</summary>
    List<RawTreatment> GetByLot(int lotId);

    /// <summary>بحث للشاشة والتقارير. <paramref name="onlyOverdue"/> = المعالجات المتأخرة.</summary>
    List<TreatmentRowDto> Search(string status = null, bool onlyOverdue = false, int? productId = null);

    /// <summary>حالة الخام لدفعة: مستلم · تحت المعالجة · جاهز · مرفوض (البند 1).</summary>
    LotTreatmentStateDto GetLotState(int lotId);

    /// <summary>
    /// المتاح للإنتاج في تاريخ محدد = الجاهز الآن + ما تكتمل معالجته حتى ذلك التاريخ.
    /// أساس البند (4)، وتستهلكه المرحلة 3 في التخطيط.
    /// </summary>
    double GetAvailableForDate(int lotId, DateTime forDate);
}

/// <summary>§المعالجة — بيانات بدء عملية معالجة.</summary>
public class TreatmentStartDto
{
    public int LotId { get; set; }
    public int? TreatmentTypeId { get; set; }
    public double QtyKg { get; set; }
    public int PackageCount { get; set; }
    public DateTime? StartedAt { get; set; }

    /// <summary>المدة بالساعات — فارغة تعني المدة الافتراضية لنوع المعالجة.</summary>
    public double? DurationHours { get; set; }

    public int? ResponsibleUserId { get; set; }
    public string Notes { get; set; }
}

/// <summary>§المعالجة — سطر عرض للشاشة والتقارير.</summary>
public class TreatmentRowDto
{
    public int Id { get; set; }
    public string TreatmentNo { get; set; }
    public int LotId { get; set; }
    public string LotCode { get; set; }
    public string ProductName { get; set; }
    public string TreatmentTypeName { get; set; }
    public double QtyKg { get; set; }
    public int PackageCount { get; set; }
    public DateTime StartedAt { get; set; }
    public double DurationHours { get; set; }
    public DateTime ExpectedReadyAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public double ReleasedQtyKg { get; set; }
    public double RejectedQtyKg { get; set; }
    public double RemainingQtyKg { get; set; }
    public string Status { get; set; }
    public string StatusAr { get; set; }
    public bool IsOverdue { get; set; }
    public bool IsReadyByTime { get; set; }
    public string ResponsibleName { get; set; }
    public string Notes { get; set; }
}

/// <summary>
/// §المعالجة — حالات الخام الأربع لدفعة (البند 1).
/// **تُشتق حسابياً ولا تُخزَّن كعمود حالة**: الدفعة الواحدة تكون في ثلاث حالات معاً
/// (4,000 جاهزة + 500 تحت المعالجة + 500 مرفوضة)، فالحالة صفة كمية لا صفة دفعة.
/// وهذا يمنع تناقض البيانات جذرياً لأن عمود الحالة غير موجود أصلاً.
/// </summary>
public class LotTreatmentStateDto
{
    public int LotId { get; set; }
    public string LotCode { get; set; }
    public double InStockQtyKg { get; set; }

    /// <summary>🔵 مستلم ولم يدخل المعالجة بعد.</summary>
    public double NotTreatedQtyKg { get; set; }

    /// <summary>🟠 تحت المعالجة/التعقيم — غير متاح للإنتاج.</summary>
    public double UnderTreatmentQtyKg { get; set; }

    /// <summary>🟢 جاهز للإنتاج.</summary>
    public double ReadyQtyKg { get; set; }

    /// <summary>🔴 مرفوض.</summary>
    public double RejectedQtyKg { get; set; }

    /// <summary>المحجوز لخطط نشطة.</summary>
    public double ReservedQtyKg { get; set; }

    /// <summary>المتاح فعلاً بعد الحجز وبعد استبعاد ما تحت المعالجة.</summary>
    public double AvailableQtyKg { get; set; }

    /// <summary>هل الصنف يشترط معالجة أصلاً؟</summary>
    public bool RequiresTreatment { get; set; }
}
