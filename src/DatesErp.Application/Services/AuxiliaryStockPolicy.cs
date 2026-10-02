using DatesErp.Core.Exceptions;

namespace DatesErp.Application.Services;

/// <summary>One inventory identity and unit policy for modern ProductId-based auxiliaries.</summary>
internal static class AuxiliaryStockPolicy
{
    internal static bool IsKg(string unit) => unit?.Contains("كجم") == true
        || unit?.Contains("kg", StringComparison.OrdinalIgnoreCase) == true;

    internal static (double Kg, int Packages) Movement(double qty, string unit)
    {
        if (double.IsNaN(qty) || double.IsInfinity(qty) || qty < 0)
            throw new DomainException("كمية الصنف المساعد غير صالحة.", "AUX_INVALID_QTY");
        if (IsKg(unit)) return (qty, 0);
        if (qty > int.MaxValue || Math.Abs(qty - Math.Round(qty)) > 0.001)
            throw new DomainException("الصنف المساعد بوحدة عدد يجب صرفه وإرجاعه بعدد صحيح.", "AUX_UNIT_INTEGRAL");
        return (0, (int)Math.Round(qty));
    }

    internal static double Available(DatesErp.Core.Domain.Entities.StockBalance balance, string unit) =>
        balance == null ? 0 : IsKg(unit) ? balance.QtyKg : balance.PackageCount;
}
