#nullable enable annotations
using System.Collections.ObjectModel;
using System.ComponentModel;
using DatesErp.Core.Interfaces.Services;
namespace DatesErp.Desktop.Views.Screens;

public class LotEditorRow : INotifyPropertyChanged
{
    public Func<int, string> QuantityGuard { get; set; }
    public string QuantityError { get; private set; }
    private PlanCapacityRow _capacity;
    public PlanCapacityRow Capacity { get => _capacity; set { _capacity = value; OnChange(nameof(Capacity)); OnChange(nameof(CapAlert)); OnChange(nameof(ProductCapacityDisplay)); } }
    private void GuardQuantity(int quantity)
    {
        QuantityError = quantity < 0 ? "الكمية لا يمكن أن تكون سالبة." : QuantityGuard?.Invoke(quantity);
        OnChange(nameof(QuantityError));
        if (!string.IsNullOrEmpty(QuantityError)) throw new ArgumentException(QuantityError);
    }

    public List<ProductOption> RawOptions { get; set; } = new();
    public bool CanChangeRaw => LotId == null;
    public Func<int, List<ProductOption>> LoadFinishedProducts { get; set; }
    private int? _rawProductId;
    public int? RawProductId
    {
        get => _rawProductId;
        set { if (_rawProductId == value) return; _rawProductId = value; ReloadFinishedProducts(); OnChange(nameof(RawProductId)); }
    }
    public string LinkMessage => RawProductId == null ? "اختر الصنف الخام أولاً." : AllProducts.Count == 0
        ? "لا توجد أصناف تامة مرتبطة بهذا الصنف الخام." : "";
    public void ReloadFinishedProducts()
    {
        List<ProductOption> next;
        try { next = RawProductId != null && LoadFinishedProducts != null ? LoadFinishedProducts(RawProductId.Value) : new(); }
        catch { ApplyLinkedProducts(new()); throw; }
        ApplyLinkedProducts(next);
    }
    private void ApplyLinkedProducts(List<ProductOption> next)
    {
        if (ProductId != null && !next.Any(p => p.Id == ProductId))
        { ProductId = null; CartonsText = "0"; IsChecked = false; }
        if (!AllProducts.Select(p => (p.Id, p.Name)).SequenceEqual(next.Select(p => (p.Id, p.Name)))) AllProducts = next;
        OnChange(nameof(LinkMessage));
    }

    // هوية الشحنة/الدفعة
    public int? LotId { get; set; }
    public int? ShipmentId { get; set; }
    public string ShipmentNo { get; set; }
    public string LotCode { get; set; }
    public int? CustomerId { get; set; }
    public string CustomerName { get; set; }
    public string RawName { get; set; }
    public double Available { get; set; }
    public string DaysInStockText { get; set; } = "";
    public string PresetDate { get; set; }

    // الوردية
    public List<ShiftOption> AllShifts { get; set; } = new();
    private int? _shiftId;
    public int? ShiftId
    {
        get => _shiftId;
        set { _shiftId = value; var hit = value != null ? AllShifts.FirstOrDefault(s => s.Id == value.Value) : null; if (hit != null) ShiftName = hit.Name; OnChange(nameof(ShiftId)); }
    }
    public string ShiftName { get; set; } = "—";
    private DateTime? _dateValue;
    public DateTime? DateValue
    {
        get => _dateValue;
        set { if (_dateValue == value) return; _dateValue = value; OnChange(nameof(DateValue)); Recalc(); }
    }

    // ═══ §المعالجة والتعقيم — حالة جاهزية الخام (أحمر) + منع الإنتاج قبل تاريخ المعالجة ═══
    public bool TreatmentRequired { get; set; }
    public DateTime? TreatmentUntilDate { get; set; }
    public DateTime? TreatmentReadyDate { get; set; }
    public double UnderTreatmentKg { get; set; }
    public bool TreatmentBlocked { get; private set; }
    public string TreatmentBlockReason { get; private set; }
    public string TreatmentDisplay
    {
        get
        {
            if (!TreatmentRequired) return "لا معالجة";
            bool under = UnderTreatmentKg > 0;
            if (!under) return "معالجة مكتملة ✓";
            DateTime? until = TreatmentBlockUntil();
            if (TreatmentBlocked) return $"🔴 قيد المعالجة حتى {until:dd/MM/yyyy}";
            return $"🟢 جاهز من {until:dd/MM/yyyy}";
        }
    }
    private DateTime? TreatmentBlockUntil()
    {
        DateTime? until = null;
        if (TreatmentUntilDate != null) until = TreatmentUntilDate;
        if (TreatmentReadyDate != null && (until == null || TreatmentReadyDate > until)) until = TreatmentReadyDate;
        return until?.Date;
    }
    private void RecalcTreatment()
    {
        var date = DateValue?.Date;
        var until = TreatmentBlockUntil();
        bool blocked = TreatmentRequired && UnderTreatmentKg > 0 && date.HasValue && until.HasValue
            && date.Value < until.Value;
        TreatmentBlocked = blocked;
        TreatmentBlockReason = blocked
            ? $"خام الدفعة قيد المعالجة حتى {until:dd/MM/yyyy} — لا يمكن الإنتاج قبل هذا التاريخ."
            : null;
        OnChange(nameof(TreatmentDisplay)); OnChange(nameof(TreatmentBlocked)); OnChange(nameof(TreatmentBlockReason));
    }

    // تعريف الصنف التام — §1.50.67 FIX: عرض الوحدة بوحدتين (كرتون + كجم) بدل كجم فقط
    public Dictionary<int, string> ProductUnits { get; set; } = new();
    public Dictionary<int, double> ProductCartonWeights { get; set; } = new();
    public string UnitDisplay
    {
        get
        {
            if (_productId == null) return "—";
            string u = ProductUnits.TryGetValue(_productId.Value, out var uu) && !string.IsNullOrWhiteSpace(uu) ? uu : "كرتون";
            double cw = 0;
            if (ProductCartonWeights.TryGetValue(_productId.Value, out var cc)) cw = cc;
            else if (PackWeight > 0) cw = PackWeight;
            // إذا كان وزن الكرتون معروف اعرض "كرتون (2 كجم)" بدل "كجم" فقط
            if (cw > 0) return $"{u} ({cw:N1} كجم)";
            return u;
        }
    }
    public Dictionary<int, double> PerProductAvailable { get; set; } = new();
    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (value && QuantityGuard != null && int.TryParse(CartonsText, out var q))
            { try { GuardQuantity(q); } catch (ArgumentException) { OnChange(nameof(IsChecked)); return; } }
            _isChecked = value; OnChange(nameof(IsChecked));
        }
    }

    private int? _productId;
    public int? ProductId
    {
        get => _productId;
        set { _productId = value; OnChange(nameof(ProductId)); OnChange(nameof(UnitDisplay)); RebuildPacks(); Recalc(); }
    }
    private ObservableCollection<PackOption> _packs = new();
    public ObservableCollection<PackOption> Packs { get => _packs; private set { _packs = value; OnChange(nameof(Packs)); } }
    private int? _packId;
    public int? PackId { get => _packId; set { _packId = value; OnChange(nameof(PackId)); Recalc(); } }

    // §الإنتاج (كرتون) — المدخل الوحيد. الوحدة والمواصفات تأتي تلقائياً من الصنف التام.
    private string _cartonsText = "0";
    public string CartonsText
    {
        get => _cartonsText;
        set
        {
            if (int.TryParse(value, out var q)) GuardQuantity(q);
            else { QuantityError = "أدخل عدداً صحيحاً من الكراتين."; OnChange(nameof(QuantityError)); }
            _cartonsText = value; OnChange(nameof(CartonsText)); Recalc();
            if (QuantityGuard != null && int.TryParse(value, out _)) IsChecked = q > 0;
        }
    }
    private double _computedKg;
    public double ComputedKg { get => _computedKg; private set { _computedKg = value; OnChange(nameof(ComputedKg)); } }

    // مواصفات الصنف التام (تلقائية من العبوة المختارة — لا يُدخلها المستخدم)
    public double PackWeight { get => _packWeight; private set { _packWeight = value; OnChange(nameof(PackWeight)); } }
    private double _packWeight;
    public int MoldsCount { get => _moldsCount; private set { _moldsCount = value; OnChange(nameof(MoldsCount)); } }
    private int _moldsCount;

    // الخام المطلوب + كفاية الخام (تلقائي)
    public double RawRequiredKg { get => _rawRequiredKg; private set { _rawRequiredKg = value; OnChange(nameof(RawRequiredKg)); } }
    private double _rawRequiredKg;
    public double RawShortageKg { get => _rawShortageKg; private set { _rawShortageKg = value; OnChange(nameof(RawShortageKg)); } }
    private double _rawShortageKg;
    public bool RawSufficient { get => _rawSufficient; private set { _rawSufficient = value; OnChange(nameof(RawSufficient)); } }
    private bool _rawSufficient;
    public string RawStatusText { get => _rawStatusText; private set { _rawStatusText = value; OnChange(nameof(RawStatusText)); } }
    private string _rawStatusText;

    // ═══ §سحب الخام (مصغّر) — الوحدة من سطر الاستلام، والكمية تُشتق من الإنتاج × وزن العبوة ═══
    public ShipmentQuantityContext Ctx { get; set; }
    public string ReceiptUnitText => string.IsNullOrWhiteSpace(Ctx?.ReceiptUnit) ? "—" : Ctx.ReceiptUnit;
    public double UnitWeight => Ctx?.UnitWeightKg ?? 0;
    public List<string> SourceModes { get; private set; } = new();
    private string _sourceMode;
    public string SourceMode
    {
        get => _sourceMode;
        set { if (_sourceMode == value) return; _sourceMode = value; OnChange(nameof(SourceMode)); OnChange(nameof(AvailableDisplay)); Recalc(); }
    }
    /// <summary>يبني خيارات طريقة السحب من وحدة الاستلام الفعلية (سلة/كرتون/كجم) + كجم.</summary>
    public void SetContext()
    {
        var m = new List<string>();
        string u = ReceiptUnitText;
        if (!string.IsNullOrWhiteSpace(u) && u != "—") m.Add(u);
        if (m.Count > 0 && !string.Equals(u, "كجم", StringComparison.OrdinalIgnoreCase)) m.Add("كجم");
        if (m.Count == 0) m.Add("كجم");
        SourceModes = m;
        if (string.IsNullOrEmpty(_sourceMode) || !m.Contains(_sourceMode)) _sourceMode = m[0];
        OnChange(nameof(SourceModes)); OnChange(nameof(SourceMode)); OnChange(nameof(ContextDisplay));
        OnChange(nameof(AvailableDisplay)); Recalc();
    }
    public string ContextDisplay => Ctx == null ? "—"
        : $"{ReceiptUnitText}: مستلم {Ctx.ReceivedUnits:N0} ({Ctx.ReceivedKg:N0} كجم) — متاح {Math.Floor(Ctx.AvailableUnits):N0} {ReceiptUnitText} ({Ctx.AvailableKg:N0} كجم)";
    /// <summary>المتاح يتغيّر مع طريقة السحب: بالوحدة (وحدات + كجم) أو بالكيلو فقط.</summary>
    public string AvailableDisplay
    {
        get
        {
            if (Ctx == null) return "—";
            bool units = !string.Equals(SourceMode, "كجم", StringComparison.OrdinalIgnoreCase);
            return units ? $"{Math.Floor(Ctx.AvailableUnits):N0} {ReceiptUnitText} / {Ctx.AvailableKg:N0} كجم" : $"{Ctx.AvailableKg:N0} كجم";
        }
    }

    // التتبع المحفوظ للسحب (يُشتق من الإنتاج × وزن العبوة — يُنقل إلى بند الخطة)
    public string SourceUnit { get; set; }
    public double SourceQtyInUnit { get; set; }
    public double SourceUnitWeightKg { get; set; }
    public double SourceQtyKg { get; set; }
    public string SourceError { get => _sourceError; private set { _sourceError = value; OnChange(nameof(SourceError)); } }
    private string _sourceError;

    // مراجع الحساب
    private List<ProductOption> _allProducts = new();
    public List<ProductOption> AllProducts { get => _allProducts; set { _allProducts = value; OnChange(nameof(AllProducts)); OnChange(nameof(LinkMessage)); } }
    public List<PackOption> AllPacks { get; set; } = new();
    private void RebuildPacks()
    {
        Packs = new ObservableCollection<PackOption>(ProductId == null ? new List<PackOption>() : AllPacks);
        var prod = ProductId != null ? AllProducts.FirstOrDefault(p => p.Id == ProductId) : null;
        int? prefer = prod?.DefaultPackagingTypeId;
        if (_packId != null && Packs.Any(p => p.Id == _packId)) { /* keep */ }
        else if (prefer != null && Packs.Any(p => p.Id == prefer)) _packId = prefer;
        else _packId = Packs.Count > 0 ? Packs[0].Id : (int?)null;
        OnChange(nameof(PackId));
    }

    public void Recalc()
    {
        int.TryParse(_cartonsText, out var ctn);
        var pack = AllPacks.FirstOrDefault(p => p.Id == _packId);
        var prod = _productId != null ? AllProducts.FirstOrDefault(p => p.Id == _productId) : null;
        // §v1.50.35: وزن العبوة وعدد القوالب من بطاقة الصنف التام (شاشة الأصناف)،
        // لا من أول عبوة عامة في جدول العبوات (كانت تُظهر 5.0 / 5 لكل الأصناف).
        // §1.50.66 FIX: بطاقة الصنف أولاً ثم العبوة — يمنع mismatch 0.5 vs 2 كجم
        double packW = 0;
        if (prod != null && prod.CartonWeightKg > 0) packW = prod.CartonWeightKg;
        else if (prod != null && prod.MoldsCount > 0 && prod.MoldWeightKg > 0) packW = prod.MoldsCount * prod.MoldWeightKg;
        else packW = pack?.UnitWeightKg ?? 0;

        PackWeight = packW;
        MoldsCount = (prod != null && prod.MoldsCount > 0) ? prod.MoldsCount : (pack?.MoldsCount ?? 0);
        ComputedKg = ctn > 0 && packW > 0 ? Math.Round(ctn * packW, 2) : 0;
        RawRequiredKg = ComputedKg;

        // §1.50.66 — تحقق فوري من تطابق وزن العبوة بين بطاقة الصنف والعبوة المحددة
        if (prod != null && pack != null && prod.CartonWeightKg > 0 && pack.UnitWeightKg > 0)
        {
            double diff = Math.Abs(prod.CartonWeightKg - pack.UnitWeightKg);
            double tol = Math.Max(0.5, prod.CartonWeightKg * 0.05);
            if (diff > tol)
            {
                QuantityError = $"⚠️ تنبيه: وزن الكرتون في بطاقة الصنف ({prod.CartonWeightKg:N1} كجم) يختلف عن وزن العبوة المحددة ({pack.UnitWeightKg:N1} كجم) للصنف «{prod.Name}». سيُستخدم وزن البطاقة ({packW:N1} كجم).";
                OnChange(nameof(QuantityError));
            }
            else
            {
                if (QuantityError != null && QuantityError.Contains("وزن الكرتون في بطاقة الصنف"))
                {
                    QuantityError = null;
                    OnChange(nameof(QuantityError));
                }
            }
        }

        if (Ctx == null)
        {
            // بند بلا شحنة/دفعة (إدخال يدوي) — لا تتبع خام، ولا يُرسل أي سحب إلى الخلفية.
            SourceUnit = null; SourceUnitWeightKg = 0; SourceQtyKg = 0; SourceQtyInUnit = 0;
            RawShortageKg = 0; RawSufficient = true; _sourceError = null;
            RawStatusText = RawRequiredKg > 0 ? "— (بلا شحنة)" : "—";
        }
        else
        {
            // اشتقاق التتبع: الوحدة من طريقة السحب المختارة، والكمية = الإنتاج × وزن العبوة.
            double uw = Ctx.UnitWeightKg;
            // §1.50.66 — وحدة السحب الآن selectable: سلة/كرتون/كجم
            bool byKg = string.Equals(SourceMode, "كجم", StringComparison.OrdinalIgnoreCase);
            SourceUnit = byKg ? "كجم" : Ctx.ReceiptUnit;
            SourceUnitWeightKg = byKg ? 1 : uw;
            SourceQtyKg = RawRequiredKg;
            SourceQtyInUnit = byKg ? RawRequiredKg : (uw > 0 ? Math.Round(RawRequiredKg / uw, 2) : 0);

            double availKg = Ctx.AvailableKg;
            var date = DateValue?.Date;
            var untilT = TreatmentBlockUntil();
            bool treatMatures = TreatmentRequired && UnderTreatmentKg > 0 && untilT.HasValue
                && date.HasValue && date.Value >= untilT.Value;
            // §بعد تاريخ المعالجة تُضاف الكمية التي ستكتمل إلى المتاح؛ قبلها تُحسب المتاح فقط.
            double availForRow = treatMatures ? availKg + UnderTreatmentKg : availKg;
            RawShortageKg = Math.Max(0, RawRequiredKg - availForRow);
            RawSufficient = RawRequiredKg <= 0 || availForRow <= 0 ? true : RawRequiredKg <= availForRow + 0.001;
            _sourceError = RawSufficient ? null
                : $"غير كافٍ — المطلوب {RawRequiredKg:N1} كجم / المتاح {availForRow:N1} كجم (عجز {RawShortageKg:N1} كجم)";
            RawStatusText = RawRequiredKg <= 0 ? "أدخل كمية الإنتاج"
                : RawSufficient ? "متوفر ✓" : "غير كافٍ ❌";
        }
        OnChange(nameof(PackWeight)); OnChange(nameof(MoldsCount)); OnChange(nameof(ComputedKg)); OnChange(nameof(ProductCapacityDisplay));
        OnChange(nameof(RawRequiredKg)); OnChange(nameof(RawShortageKg)); OnChange(nameof(RawSufficient));
        OnChange(nameof(RawStatusText)); OnChange(nameof(SourceError));
        RecalcTreatment();
    }

    private string _capAlert = "حدد الصنف والكمية…";
    public string CapAlert { get => QuantityError ?? Capacity?.Error ?? (Capacity == null ? "اختر الصنف والكمية" : $"الحد الأقصى {Capacity.MaximumCartons:N0} كرتون | المتبقي {Capacity.RemainingCartons:N0}"); private set { _capAlert = value; OnChange(nameof(CapAlert)); } }

    /// <summary>§v1.50.35 — طاقة الصنف بجوار الصنف التام في نافذة الاختيار.</summary>
    public string ProductCapacityDisplay
    {
        get
        {
            if (Capacity != null && Capacity.ProductCapacity > 0)
                return Capacity.ProductCapacity.ToString("N0") + " كرتون/وردية";
            var prod = _productId != null ? AllProducts.FirstOrDefault(p => p.Id == _productId) : null;
            if (prod != null && prod.HourlyRate > 0) return prod.HourlyRate.ToString("N0") + " كرتون/س";
            return "—";
        }
    }
    public bool IsOverCapacity => QuantityError != null || Capacity?.Error != null;
    public bool IsInvalid => !string.IsNullOrEmpty(QuantityError) || IsOverCapacity;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChange(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class ProductOption
{
    public int Id { get; set; }
    public string Name { get; set; }
    /// <summary>§v1.50.35 — مواصفات بطاقة الصنف التام (شاشة الأصناف) لا عبوة عامة.</summary>
    public double CartonWeightKg { get; set; }
    public int MoldsCount { get; set; }
    public double MoldWeightKg { get; set; }
    public double HourlyRate { get; set; }
    public int? DefaultPackagingTypeId { get; set; }
}
/// <summary>§B92 — خيار وردية في الاختيار اليدوي (الاسم يتضمن الساعات الفعالة لقرار الإدارة).</summary>
public class ShiftOption { public int Id { get; set; } public string Name { get; set; } }
public class PackOption { public int Id { get; set; } public string Name { get; set; } public double UnitWeightKg { get; set; } public int MoldsCount { get; set; } public double MoldWeightKg { get; set; } }


public class PlanRowUi : System.ComponentModel.INotifyPropertyChanged
{
    public Func<int, string> QuantityGuard { get; set; }
    public string QuantityError { get; private set; }
    private PlanCapacityRow _capacity;
    public PlanCapacityRow Capacity { get => _capacity; set { _capacity = value; OnChanged(nameof(Capacity)); } }
    private void GuardQuantity(int quantity)
    {
        QuantityError = quantity < 0 ? "الكمية لا يمكن أن تكون سالبة." : QuantityGuard?.Invoke(quantity);
        OnChanged(nameof(QuantityError));
        if (!string.IsNullOrEmpty(QuantityError)) throw new ArgumentException(QuantityError);
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));

    private int _no;
    public int No { get => _no; set { if (_no != value) { _no = value; OnChanged(nameof(No)); } } }

    /// <summary>
    /// §B108 — معرّف بند الخطة المحفوظ (0 = صف جديد لم يُحفظ بعد).
    /// </summary>
    public int ItemId { get; set; }

    public int? CustomerId { get; set; }
    public string CustomerName { get; set; }
    public int? ShipmentId { get; set; }
    public string ShipmentNo { get; set; }
    public int? LotId { get; set; }
    public int? RawProductId { get; set; }
    public string LotCode { get; set; }
    public string RawName { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; }
    private int? _planPackId;
    public int? PackId { get => _planPackId; set { _planPackId = value; OnChanged(nameof(PackId)); } }
    public string PackName { get; set; }
    /// <summary>§B80: وحدة الصنف التام كما في بطاقته (شاشة الأصناف) — مثل «كرتون 5كجم».</summary>
    public string UnitDisplay { get; set; }
    private double _qtyKg;
    public double QtyKg 
    { 
        get => _qtyKg; 
        set 
        { 
            if (Math.Abs(_qtyKg - value) > 0.001) 
            { 
                // §1.50.68 FIX: إذا غيّر المستخدم الكيلو يدوياً، أعد حساب الكراتين تلقائياً
                // لأن الكرتون هو الوحدة الأساسية - يمنع خطأ "لا تطابق عدد الكراتين"
                if (CartonWeight > 0 && value > 0)
                {
                    int newCartons = (int)Math.Round(value / CartonWeight);
                    if (newCartons > 0 && newCartons != _cartons)
                    {
                        _cartons = newCartons;
                        _cartonsText = newCartons.ToString();
                        OnChanged(nameof(Cartons));
                        OnChanged(nameof(CartonsText));
                    }
                }
                _qtyKg = value; 
                OnChanged(nameof(QtyKg)); 
                OnChanged(nameof(RemainingAfterKg));
                ValidateCartonKgImmediate();
            } 
        } 
    }
    /// <summary>§B58: وزن كرتون الصنف لاشتقاق الكيلو عند تعديل الكراتين داخل الجدول.</summary>
    public double CartonWeight { get; set; }
    private int _cartons;
    public int Cartons { get => _cartons; set { if (_cartons != value) { GuardQuantity(value); _cartons = value; _cartonsText = value.ToString(); OnChanged(nameof(Cartons)); OnChanged(nameof(CartonsText)); ValidateCartonKgImmediate(); } } }
    private string _cartonsText = "0";
    /// <summary>§1.50.66: تحرير الكراتين داخل الجدول يعيد حساب الوزن المكافئ آلياً + تحقق فوري.</summary>
    public string CartonsText
    {
        get => _cartonsText;
        set
        {
            if (int.TryParse(value, out var proposed)) GuardQuantity(proposed);
            else { QuantityError = "أدخل عدداً صحيحاً من الكراتين."; OnChanged(nameof(QuantityError)); }
            if (_cartonsText == value) return;
            _cartonsText = value; OnChanged(nameof(CartonsText));
            if (int.TryParse(value, out var c) && c >= 0)
            {
                _cartons = c; OnChanged(nameof(Cartons));
                if (CartonWeight > 0) { QtyKg = Math.Round(c * CartonWeight, 1); OnChanged(nameof(QtyKg)); }
                ValidateCartonKgImmediate();
            }
        }
    }

    private void ValidateCartonKgImmediate()
    {
        try
        {
            if (Cartons <= 0 || CartonWeight <= 0 || QtyKg <= 0) 
            {
                if (QuantityError != null && QuantityError.Contains("لا تطابق عدد الكراتين")) { QuantityError = null; OnChanged(nameof(QuantityError)); }
                return;
            }
            double computed = Math.Round(Cartons * CartonWeight, 1);
            double tol = Math.Max(1.0, QtyKg * 0.02);
            if (Math.Abs(QtyKg - computed) > tol)
            {
                // §1.50.68 FIX: بدل منع الحفظ، صحح تلقائياً لأن الكرتون هو الأساس
                // في الصورة: 3000 كجم و 4000 كرتون × 2.5 = 10000 كجم → نصحح الكيلو إلى 10000
                // أو إذا المستخدم أدخل كيلو، نكون قد صححنا الكراتين في setter أعلاه
                // هنا نصحح الكيلو ليطابق الكراتين (الوحدة الأساسية)
                _qtyKg = computed;
                OnChanged(nameof(QtyKg));
                OnChanged(nameof(RemainingAfterKg));
                QuantityError = null;
                OnChanged(nameof(QuantityError));
            }
            else
            {
                if (QuantityError != null && QuantityError.Contains("لا تطابق عدد الكراتين")) { QuantityError = null; OnChanged(nameof(QuantityError)); }
            }
        }
        catch { }
    }

    private string _date;
    public string Date
    {
        get => _date;
        set
        {
            if (_date == value) return;
            _date = value; OnChanged(nameof(Date));
            // §B80: المزامنة مع DatePicker — تحرير نصي أو اختيار من التقويم يحدّث الطرفين
            if (DatesErp.Core.Common.UiFormat.TryParseDate(value, out var dv)) { _dateValue = dv; OnChanged(nameof(DateValue)); }
        }
    }
    private DateTime? _dateValue;
    /// <summary>§B80: تاريخ الإنتاج كتاريخ حقيقي لعمود DatePicker — قابل للتعديل دائماً في الجدول.</summary>
    public DateTime? DateValue
    {
        get => _dateValue;
        set
        {
            if (_dateValue == value) return;
            _dateValue = value; OnChanged(nameof(DateValue));
            _date = value?.ToString("dd/MM/yyyy") ?? ""; OnChanged(nameof(Date));
        }
    }
    private int _shiftId;
    public int ShiftId { get => _shiftId; set { if (_shiftId != value) { _shiftId = value; OnChanged(nameof(ShiftId)); } } }
    public string ShiftName { get; set; }
    public string LineName { get; set; }
    private int _lineId;
    public int LineId { get => _lineId; set { if (_lineId != value) { _lineId = value; OnChanged(nameof(LineId)); } } }
    public int Priority { get; set; }

    // ═══ §تتبع سحب الخام من الشحنة — تُنقل إلى PlanItemDto وتُحفظ على البند ═══
    public string SourceUnit { get; set; }
    public double SourceQtyInUnit { get; set; }
    public double SourceUnitWeightKg { get; set; }
    private double _sourceQtyKg;
    public double SourceQtyKg { get => _sourceQtyKg; set { _sourceQtyKg = value; OnChanged(nameof(SourceQtyKg)); OnChanged(nameof(RemainingAfterKg)); OnChanged(nameof(AvailableDisplay)); OnChanged(nameof(SourceDisplay)); } }

    // §1.50.66 — وحدة السحب selectable في الجدول الرئيسي + عرض المتاح بالوحدتين
    public List<string> SourceModes { get; set; } = new();
    private string _sourceMode = "كجم";
    public string SourceMode 
    { 
        get => _sourceMode; 
        set 
        { 
            if (_sourceMode == value) return; 
            _sourceMode = value; 
            OnChanged(nameof(SourceMode)); 
            OnChanged(nameof(AvailableDisplay));
            OnChanged(nameof(SourceDisplay));
            // إعادة حساب SourceQtyInUnit حسب الوحدة الجديدة
            RecalcSourceQtyFromMode();
        } 
    }
    public string AvailableDisplay 
    { 
        get 
        {
            if (SourceQtyKg <= 0) return "—";
            bool byKg = string.Equals(SourceMode, "كجم", StringComparison.OrdinalIgnoreCase);
            if (byKg) return $"{SourceQtyKg:N0} كجم";
            double units = SourceUnitWeightKg > 0 ? SourceQtyKg / SourceUnitWeightKg : SourceQtyInUnit;
            return $"{Math.Floor(units):N0} {SourceUnit} / {SourceQtyKg:N0} كجم";
        }
    }
    public string SourceDisplay
    {
        get
        {
            if (SourceQtyKg <= 0) return "—";
            bool byKg = string.Equals(SourceMode, "كجم", StringComparison.OrdinalIgnoreCase);
            if (byKg) return $"{SourceQtyKg:N0} كجم";
            double units = SourceUnitWeightKg > 0 ? SourceQtyKg / SourceUnitWeightKg : SourceQtyInUnit;
            return $"{Math.Floor(units):N0} {SourceUnit} ({SourceQtyKg:N0} كجم)";
        }
    }
    private void RecalcSourceQtyFromMode()
    {
        if (SourceQtyKg <= 0) return;
        bool byKg = string.Equals(SourceMode, "كجم", StringComparison.OrdinalIgnoreCase);
        if (byKg)
        {
            SourceQtyInUnit = SourceQtyKg;
            SourceUnitWeightKg = 1;
        }
        else
        {
            if (SourceUnitWeightKg > 0 && SourceUnitWeightKg != 1)
            {
                SourceQtyInUnit = Math.Round(SourceQtyKg / SourceUnitWeightKg, 2);
            }
        }
        OnChanged(nameof(SourceQtyInUnit));
    }

    // §1.50.57 — المتبقي بعد التخطيط: المتاح - المجدول — يجيب سؤال المستخدم "عند إضافة صنف جديد كم ستظهر كميته"
    public double RemainingAfterKg => Math.Max(0, SourceQtyKg - QtyKg);
    public bool IsInvalid => !string.IsNullOrEmpty(QuantityError) || Cartons <= 0;
}
