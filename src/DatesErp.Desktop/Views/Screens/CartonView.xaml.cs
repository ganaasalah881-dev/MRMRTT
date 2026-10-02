using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §B10 — شاشة الكرتون الفارغ: رصيد + دفتر حركات، عدّ فعلي يقيّد الفروق آلياً،
/// بيع موثق يخصم الرصيد ويمنع تجاوزه، وطباعة رسمية بنمط المراحل السابقة.
/// </summary>
public partial class CartonView : UserControl
{
    private sealed class CountRow : System.ComponentModel.INotifyPropertyChanged
    {
        private int _productId; private string _product = "— اضغط لاختيار الصنف —"; private int _book; private int _counted;
        public int ProductId { get => _productId; set { _productId = value; OnChanged(nameof(ProductId)); OnChanged(nameof(IsEmptyRow)); } }
        public string Product { get => _product; set { _product = value; OnChanged(nameof(Product)); } }
        public int Book { get => _book; set { _book = value; OnChanged(nameof(Book)); OnChanged(nameof(Diff)); } }
        public int Counted { get => _counted; set { _counted = value; OnChanged(nameof(Counted)); OnChanged(nameof(Diff)); OnChanged(nameof(IsInvalid)); OnChanged(nameof(ValidationError)); } }
        public int Diff => Counted - Book;
        public bool IsEmptyRow => ProductId == 0;
        public bool IsInvalid => !IsEmptyRow && (Counted < 0);
        public string ValidationError => IsInvalid ? "العدّ لا يمكن أن يكون سالباً" : null;
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
    }
    private sealed class SaleRow : System.ComponentModel.INotifyPropertyChanged
    {
        private int _productId; private string _product = "— اضغط لاختيار الصنف —"; private int _cartons; private double _amount; private double _price;
        public int ProductId { get => _productId; set { _productId = value; OnChanged(nameof(ProductId)); OnChanged(nameof(IsEmptyRow)); } }
        public string Product { get => _product; set { _product = value; OnChanged(nameof(Product)); } }
        public int Cartons { get => _cartons; set { _cartons = value; _amount = Math.Round(value * _price,2); OnChanged(nameof(Cartons)); OnChanged(nameof(Amount)); OnChanged(nameof(IsInvalid)); OnChanged(nameof(ValidationError)); } }
        public double Amount { get => _amount; set { _amount = value; OnChanged(nameof(Amount)); } }
        public double Price { get => _price; set { _price = value; _amount = Math.Round(_cartons * value,2); OnChanged(nameof(Price)); OnChanged(nameof(Amount)); } }
        public bool IsEmptyRow => ProductId == 0;
        public bool IsInvalid => !IsEmptyRow && Cartons <= 0;
        public string ValidationError => IsInvalid ? "أدخل كراتين > 0" : null;
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        private void OnChanged(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
    }
    private readonly ObservableCollection<CountRow> _countRows = new();
    private readonly ObservableCollection<SaleRow> _saleRows = new();

    public CartonView()
    {
        InitializeComponent();
        CountGrid.ItemsSource = _countRows;
        CountGrid.AutoGeneratingColumn += (_, e) => { };
        SaleGrid.ItemsSource = _saleRows;
        CountDateBox.SelectedDate = DateTime.Now;
        // §1.50.60 7-هـ: تنقل لوحة مفاتيح
        CountGrid.PreviewKeyDown += Grid_PreviewKeyDown;
        SaleGrid.PreviewKeyDown += Grid_PreviewKeyDown;
        Loaded += (_, _) => { Services.ComboBoxAutoShowHelper.Apply(this); Load(); };
    }
    private void Grid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F2) { if (sender == CountGrid) AddCountRowInline_Click(null,null); else AddSaleRowInline_Click(null,null); e.Handled=true; }
        else if (e.Key == System.Windows.Input.Key.Enter)
        {
            if (sender is DataGrid dg && dg.CurrentColumn != null)
            {
                int idx = dg.Columns.IndexOf(dg.CurrentColumn);
                if (idx < dg.Columns.Count - 1)
                {
                    dg.CurrentCell = new DataGridCellInfo(dg.SelectedItem, dg.Columns[idx+1]);
                    dg.BeginEdit(); e.Handled=true;
                }
            }
        }
    }

    private CartonService Svc()
    {
        var scope = AppContainer.NewScope();
        return scope.ServiceProvider.GetRequiredService<CartonService>();
    }

    private void Load()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var whs = db.Warehouses.AsNoTracking().Where(w => w.IsActive).ToList();
            CountWhBox.ItemsSource = whs;
            SaleWhBox.ItemsSource = whs;
            CountWhBox.SelectedValue = Svc().DefaultCartonWarehouseId();
            SaleWhBox.SelectedValue = Svc().DefaultCartonWarehouseId();
            var packs = db.Products.AsNoTracking().Where(p => p.GroupCode == "004" && p.IsActive).ToList();
            SaleCustBox.ItemsSource = db.Customers.AsNoTracking().Where(c => c.IsActive).ToList();
            SaleCustBox.Items.Insert(0, null);
            RefreshData();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Carton.Load"); }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshData();

    private void RefreshData()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var packIds = db.Products.AsNoTracking().Where(p => p.GroupCode == "004").Select(p => p.Id).ToList();
            BalGrid.ItemsSource = db.StockBalances.AsNoTracking()
                .Where(b => b.ProductId != null && packIds.Contains(b.ProductId.Value) && b.LotId == null && (b.PackageCount != 0))
                .Select(b => new
                {
                    Warehouse = db.Warehouses.Where(w => w.Id == b.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault(),
                    Product = db.Products.Where(p => p.Id == b.ProductId).Select(p => p.ProductNameAr).FirstOrDefault(),
                    Cartons = b.PackageCount
                }).ToList();
            MovGrid.ItemsSource = db.InventoryTransactions.AsNoTracking()
                .Where(t => t.ProductId != null && packIds.Contains(t.ProductId.Value))
                .OrderByDescending(t => t.Id).Take(300)
                .Select(t => new
                {
                    Date = t.TxnDate.ToString("dd/MM/yyyy"),
                    Txn = t.ReferenceDocType == ReferenceDocType.CartonReturn ? "تولّد 🔄" : t.ReferenceDocType == ReferenceDocType.CartonSale ? "بيع 💰" : "عدّ/تسوية 🔢",
                    Ref = t.ReferenceDocNumber,
                    Warehouse = db.Warehouses.Where(w => w.Id == t.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault(),
                    Inb = t.PackageCount > 0 ? t.PackageCount.ToString() : "",
                    Outb = t.PackageCount < 0 ? t.PackageCount.ToString() : ""
                }).ToList();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Carton.Refresh"); }
    }

    // ═══ العدّ — إدخال مباشر من الجدول (1.50.61) ═══
    private void AddCountRowInline_Click(object sender, RoutedEventArgs e)
    {
        _countRows.Add(new CountRow());
    }
    private void DuplicateCountRow_Click(object sender, RoutedEventArgs e)
    {
        var src = CountGrid.SelectedItem as CountRow ?? _countRows.LastOrDefault(x => !x.IsEmptyRow);
        if (src == null) return;
        _countRows.Add(new CountRow { ProductId = src.ProductId, Product = src.Product, Book = src.Book, Counted = src.Counted });
    }
    private void RemoveCountRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.Tag is CountRow row) _countRows.Remove(row);
    }
    private void CountProduct_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.Tag is CountRow row)
        {
            var dlg = new Views.ProductPickerWindow("", "004") { Owner = System.Windows.Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.SelectedProduct != null)
            {
                row.ProductId = dlg.SelectedProduct.Id;
                row.Product = dlg.SelectedProduct.ProductNameAr;
                if (CountWhBox.SelectedValue is int wid)
                    row.Book = Svc().BookCartons(row.ProductId, wid);
            }
        }
    }
    private void AddCountRow_Click(object sender, RoutedEventArgs e) => AddCountRowInline_Click(sender,e); // توافق قديم


    private void SaveCount_Click(object sender, RoutedEventArgs e)
    {
        if (CountWhBox.SelectedValue is not int wid) { AppContainer.Get<DialogService>().Error("اختر مخزن العدّ."); return; }
        if (_countRows.Count == 0) { AppContainer.Get<DialogService>().Error("أضف سطراً واحداً على الأقل."); return; }
        try
        {
            var r = Svc().CreateCountDoc(wid, CountDateBox.SelectedDate?.ToString("dd/MM/yyyy"), CountHint.Text, _countRows.Select(c => (c.ProductId, c.Counted)).ToList());
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            _countRows.Clear();
            RefreshData();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Carton.Count"); }
    }

    // ═══ البيع — إدخال مباشر (1.50.61) ═══
    private void AddSaleRowInline_Click(object sender, RoutedEventArgs e)
    {
        double.TryParse(PriceBox.Text, out var price);
        _saleRows.Add(new SaleRow { Price = price });
    }
    private void DuplicateSaleRow_Click(object sender, RoutedEventArgs e)
    {
        var src = SaleGrid.SelectedItem as SaleRow ?? _saleRows.LastOrDefault(x => !x.IsEmptyRow);
        if (src == null) return;
        double.TryParse(PriceBox.Text, out var price);
        _saleRows.Add(new SaleRow { ProductId = src.ProductId, Product = src.Product, Cartons = src.Cartons, Price = price });
    }
    private void RemoveSaleRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.Tag is SaleRow row) _saleRows.Remove(row);
    }
    private void SaleProduct_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button b && b.Tag is SaleRow row)
        {
            var dlg = new Views.ProductPickerWindow("", "004") { Owner = System.Windows.Window.GetWindow(this) };
            if (dlg.ShowDialog() == true && dlg.SelectedProduct != null)
            {
                row.ProductId = dlg.SelectedProduct.Id;
                row.Product = dlg.SelectedProduct.ProductNameAr;
                double.TryParse(PriceBox.Text, out var price);
                row.Price = price;
                SaleTotal.Text = $"الإجمالي: {_saleRows.Sum(s => s.Amount):N2}";
            }
        }
    }
    private void AddSaleRow_Click(object sender, RoutedEventArgs e) => AddSaleRowInline_Click(sender,e); // توافق


    private void SaveSale_Click(object sender, RoutedEventArgs e)
    {
        if (SaleWhBox.SelectedValue is not int wid) { AppContainer.Get<DialogService>().Error("اختر مخزن الصرف."); return; }
        if (_saleRows.Count == 0) { AppContainer.Get<DialogService>().Error("أضف سطراً واحداً على الأقل."); return; }
        double.TryParse(PriceBox.Text, out var price);
        try
        {
            var r = Svc().CreateSaleDoc((SaleCustBox.SelectedItem as Customer)?.Id, wid, price, null, _saleRows.Select(s => (s.ProductId, s.Cartons)).ToList());
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            _saleRows.Clear();
            SaleTotal.Text = "الإجمالي: 0";
            RefreshData();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Carton.Sale"); }
    }

    private void PrintSale_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var doc = db.CartonSaleDocs.Include(d => d.Items).OrderByDescending(d => d.Id).FirstOrDefault();
            if (doc == null) { AppContainer.Get<DialogService>().Error("لا يوجد سند بيع للطباعة."); return; }
            var m = new PhaseDocModel
            {
                DocTitle = "سند بيع كرتون فارغ",
                DocNo = doc.DocumentNumber,
                StatusAr = "معتمد ✅",
                Columns = new[] { "الصنف", "الكراتين", "سعر الكرتون", "الإجمالي" },
                Signatures = { "أمين المخزن", "المشتري/المندوب", "المدير المالي" }
            };
            m.Info.Add(("العميل", doc.CustomerId != null ? db.Customers.Where(c => c.Id == doc.CustomerId).Select(c => c.CustomerName).FirstOrDefault() ?? "-" : "نقدي"));
            m.Info.Add(("المخزن", db.Warehouses.Where(w => w.Id == doc.WarehouseId).Select(w => w.WarehouseNameAr).FirstOrDefault() ?? "-"));
            m.Info.Add(("التاريخ", doc.SaleDate.ToString("dd/MM/yyyy")));
            foreach (var it in doc.Items)
                m.Rows.Add(new object[]
                {
                    db.Products.Where(p => p.Id == it.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                    it.Cartons, doc.PricePerCarton, it.Amount
                });
            m.Totals.Add(("إجمالي الكراتين", doc.Items.Sum(i => i.Cartons).ToString("N0")));
            m.Totals.Add(("إجمالي القيمة", doc.TotalAmount.ToString("N2")));
            new PrintPreviewWindow(PhasePrint.Build(m), $"{m.DocTitle} {m.DocNo}")
            { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Carton.PrintSale"); }
    }
}
