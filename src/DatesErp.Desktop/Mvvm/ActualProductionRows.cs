#nullable enable annotations
using System.ComponentModel;
using System.Globalization;
using DatesErp.Core.Interfaces.Services;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>Shared actual WPF row model. Text stays text until validated: blank/NaN/fractional cartons never become zero silently.</summary>
public sealed class ActualProductionRow : INotifyPropertyChanged, IDataErrorInfo
{
    public ActualDeliveryItemDto Source { get; }
    public ActualProductionRow(ActualDeliveryItemDto source, bool recorded) { Source = source; _actual = recorded ? source.ActualCartons.ToString(CultureInfo.InvariantCulture) : ""; }
    public string Customer => Source.Customer;
    public string Product => Source.Product;
    public string Unit => Source.Unit;
    public int Planned => Source.PlannedCartons;
    private string _actual;
    public string Actual { get => _actual; set { _actual = value; Changed(nameof(Actual)); Changed(nameof(Difference)); Changed(nameof(DiffState)); Changed(nameof(Error)); } }
    /// <summary>§v1.50.32: حالة الفرق للتلوين الصريح — مطابق/نقص/غير صالح.</summary>
    public string DiffState { get { if (!TryQuantity(out var q)) return "invalid"; return q == Planned ? "match" : "gap"; } }
    public bool TryQuantity(out int quantity) => int.TryParse(Normalize(Actual), NumberStyles.Integer, CultureInfo.CurrentCulture, out quantity) && quantity >= 0 && quantity <= Planned;
    public string Difference => TryQuantity(out var q) ? (Planned - q).ToString(CultureInfo.CurrentCulture) : "—";
    public string Error => TryQuantity(out _) ? "" : "أدخل كراتين فعلية صحيحة بين صفر والمخطط؛ الخانة لا تُملأ تلقائيًا.";
    public string this[string name] => name == nameof(Actual) ? Error : "";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private static string Normalize(string text) => new string((text ?? "").Trim().Select(c =>
        c >= '٠' && c <= '٩' ? (char)('0' + c - '٠') : c >= '۰' && c <= '۹' ? (char)('0' + c - '۰')
            : c == '٫' ? CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator[0] : c).ToArray());
    public static bool TryNonnegative(string text, out double value) => double.TryParse(Normalize(text), NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value) && value >= 0;
}
public sealed class DeliveryEditRow : INotifyPropertyChanged
{
    public int ItemId { get; init; }
    public int ProductId { get; init; }
    public int? LotId { get; init; }
    public int? CustomerId { get; init; }
    public int? PackagingTypeId { get; init; }
    public string Product { get; init; }
    public string Customer { get; init; }
    public double ActualKg { get; init; }
    public double RemainingKg { get; init; }
    private double _qtyKg;
    public double QtyKg { get => _qtyKg; set { _qtyKg = value; PropertyChanged?.Invoke(this, new(nameof(QtyKg))); } }
    private int _packageCount;
    public int PackageCount { get => _packageCount; set { _packageCount = value; PropertyChanged?.Invoke(this, new(nameof(PackageCount))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}
public sealed class ActualSecondaryRow : INotifyPropertyChanged
{
    private ActualByProductDefinitionDto _definition;
    public ActualByProductDefinitionDto Definition { get => _definition; set { _definition = value; PropertyChanged?.Invoke(this, new(nameof(Unit))); } }
    public string Unit => Definition?.Unit ?? "";
    public string Quantity { get; set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
}
