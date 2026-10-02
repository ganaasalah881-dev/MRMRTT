namespace DatesErp.Core.Interfaces.Services;

/// <summary>§45 — صف فروقات مطابقة المخزون: الرصيد مقابل دفتر الحركات.</summary>
public record IntegrityRow(string Subject, double Balance, double Ledger, double Diff);

/// <summary>§45 — فحص ذاتي لمطابقة الأرصدة مع مجموع حركاتها منذ الصفر.</summary>
public interface IInventoryIntegrityService
{
    List<IntegrityRow> Check(double tolerance = 0.001);
}
