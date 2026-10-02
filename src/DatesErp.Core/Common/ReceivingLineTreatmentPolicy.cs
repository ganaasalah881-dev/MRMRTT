using DatesErp.Core.Exceptions;

namespace DatesErp.Core.Common;

/// <summary>العقد الحالي المشترك بين الجدول والخدمة، مستقل تماماً عن بطاقة الصنف والدرجات القديمة.</summary>
public static class ReceivingLineTreatmentPolicy
{
    public static DateTime? Validate(bool? required, DateTime? untilDate, DateTime receiptDate)
    {
        if (required == null)
            throw new DomainException("اختر المعالجة: نعم أو لا لكل بند؛ لا يجوز ترك الاختيار فارغاً.", "TREATMENT_CHOICE_REQUIRED");
        if (!required.Value) return null;
        if (untilDate == null)
            throw new DomainException("المعالجة = نعم: حقل حتى تاريخ إلزامي.", "TREATMENT_DATE_REQUIRED");
        if (untilDate.Value.Date < receiptDate.Date)
            throw new DomainException("حتى تاريخ لا يجوز أن يسبق تاريخ الاستلام.", "TREATMENT_DATE_BEFORE_RECEIPT");
        return untilDate.Value.Date;
    }
}
