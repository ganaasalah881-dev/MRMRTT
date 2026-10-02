using System;
using System.Collections.Generic;

namespace DatesErp.Core.Interfaces.Services;

/// <summary>§B79 — صف أمر إنتاج كما تعرضه شاشة إقفال الخطة.</summary>
public class OrderClosureRow
{
    public int OrderId { get; set; }
    public string OrderNumber { get; set; }
    public string CustomerName { get; set; }
    public string ProductNames { get; set; }
    public string Date { get; set; }
    public string ShiftName { get; set; }
    public double Planned { get; set; }
    public double Produced { get; set; }
    public double Closed { get; set; }
    public string StateAr { get; set; }
    public bool IsCancelled { get; set; }
}

/// <summary>§B79 — صف ملخص (عميل أو صنف) داخل شاشة إقفال الخطة.</summary>
public class ClosureSummaryRow
{
    public string Name { get; set; }
    public double Planned { get; set; }
    public double Produced { get; set; }
    /// <summary>§B100 (سحب) — المقبول جودة (من فحوصات معتمدة) — للعملاء فقط.</summary>
    public double Accepted { get; set; }
    /// <summary>§B100 (سحب) — المسلَّم للعميل فعلياً — للعملاء فقط.</summary>
    public double Delivered { get; set; }
    public double Closed { get; set; }
    public double Remaining { get; set; }
    public string StateAr { get; set; }
}

/// <summary>§B79 — كل ما تحتاجه شاشة «إقفال خطة الإنتاج» في كائن واحد.</summary>
public class PlanClosureInfo
{
    public int PlanId { get; set; }
    public string PlanNumber { get; set; }
    public string PlanTypeAr { get; set; }
    public string StartDate { get; set; }
    public string EndDate { get; set; }
    public string StatusAr { get; set; }
    public bool IsClosed { get; set; }
    public string ClosedAt { get; set; }
    public string ClosedByName { get; set; }

    public int TotalOrders { get; set; }
    public int OpenOrders { get; set; }
    public int InProgressOrders { get; set; }
    public int CompletedOrders { get; set; }
    public int ClosedOrders { get; set; }
    public int CancelledOrders { get; set; }

    public double PlannedTotal { get; set; }
    public double ProducedTotal { get; set; }
    public double ClosedTotal { get; set; }
    public double Remaining { get; set; }
    /// <summary>§B83: الفروقات المعالجة — عجز الأوامر المقفلة (سُوّي بإعادة المتبقي للمخزن/بالتوثيق).</summary>
    public double SettledVariance { get; set; }
    /// <summary>§B83: الأوامر غير المعالجة (مفتوح + قيد الإنتاج + مكتمل بلا إقفال) — هي التي تمنع الإقفال.</summary>
    public int UnprocessedOrders { get; set; }

    public bool CanClose { get; set; }
    public List<string> Blockers { get; } = new();
    public List<OrderClosureRow> Orders { get; } = new();
    public List<ClosureSummaryRow> Customers { get; } = new();
    public List<ClosureSummaryRow> Products { get; } = new();
}

/// <summary>
/// §B79 — شاشة «إقفال خطة الإنتاج»: مستوى إجمالي فوق أوامر الإنتاج.
/// لا إقفال قبل اكتمال وإقفال جميع الأوامر المطلوبة؛ والإقفال معاملة ذرية واحدة.
/// </summary>
public interface IPlanClosureService
{
    PlanClosureInfo GetInfo(int planId);
    /// <summary>إقفال رسمي؛ force باستثناء إداري يتطلب سبباً ويُسدَّل كاملاً.</summary>
    OpResult ClosePlanFinal(int planId, string reason = null, bool force = false);
    /// <summary>إعادة فتح بصلاحية خاصة مع تسجيل السبب والمستخدم والحالتين.</summary>
    OpResult ReopenPlan(int planId, string reason);
}
