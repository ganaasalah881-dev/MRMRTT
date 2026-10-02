using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DatesErp.Core.Common;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §أمر الإنتاج من الخطة — لا إعادة إدخال: العميل والصنف والمنتج والمخطط تُجلب من الخطة،
/// والمستخدم يحدد فقط كمية التنفيذ من المتبقي والتاريخ والوردية والخط.
/// المتبقي = المخطط − أوامر سابقة، والطاقة تُحسب لحظياً، والتوزيع على عدة ورديات بضغطة واحدة.
/// </summary>
internal static class OrderGridColumns
{
    internal static DataGridTextColumn IdentCol(string header, string path, double star, double minWidth)
    {
        var col = new DataGridTextColumn
        {
            Header = header,
            Width = new DataGridLength(star, DataGridLengthUnitType.Star),
            MinWidth = minWidth,
            IsReadOnly = true,
            Binding = new System.Windows.Data.Binding(path)
        };
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.ToolTipProperty, new System.Windows.Data.Binding(path)));
        col.ElementStyle = style;
        return col;
    }
}


    // §حُذف غلافَا NewOrderWindow وProductionOrderWindow في B40: لم ينشئهما أي كود —
    // OrdersView يستضيف NewOrderPanel وOrderDocumentPanel مباشرة داخل الشاشة.

/// <summary>§B80 — صف بند أمر قابل للتعديل (مسودة): الكراتين تُعدَّل والوزن المكافئ يُحسب في الخدمة.</summary>
public class OrderItemEditRow
{
    public int Id { get; set; }
    public string Customer { get; set; }
    public string Shipment { get; set; }
    public string Lot { get; set; }
    public string Raw { get; set; }
    public string Product { get; set; }
    public string Pack { get; set; }
    public double PlannedKg { get; set; }
    public int PlannedCartons { get; set; }
    /// <summary>الكراتين القابلة للتعديل — تبدأ بالمخطط.</summary>
    public int Cartons { get; set; }
    public double ProducedKg { get; set; }
    public string StatusAr { get; set; }
}

public class OrderDocumentPanel : UserControl
{
    private readonly int _orderId;
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)) };
    private readonly WrapPanel _cardPanel = new();
    private readonly ProgressBar _progress = new() { Height = 20, Maximum = 100 };
    private readonly TextBlock _progressText = new() { FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
    private readonly DataGrid _itemsGrid = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false, Height = 150, RowHeight = 28 };
    private readonly TextBox _notesBox = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 44, IsReadOnly = true };
    private List<OrderItemEditRow> _itemRows = new();
    private readonly DataGrid _materialsGrid = new() { AutoGenerateColumns = false, IsReadOnly = true, Height = 110, RowHeight = 26 };
    private readonly DataGrid _eventsGrid = new() { AutoGenerateColumns = false, IsReadOnly = true, Height = 160, RowHeight = 26 };
    private readonly StackPanel _actionsPanel = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
    private OrderCardDto _card;
    // §v1.50.34 — الشريط الكلاسيكي برأس الشاشة: حفظ/تعديل/إضافة/بحث/حذف
    private Button _editBtn, _saveBtn, _deleteBtn;
    private bool _editing;
    /// <summary>يفتح أمراً آخر (نتيجة بحث/إصدار) — تُمرَّر من الشاشة المضيفة.</summary>
    public Action<int> Navigator { get; set; }
    /// <summary>تُستدعى بعد إصدار/حذف ليحدّث المضيف قائمته.</summary>
    public Action Changed { get; set; }
    /// <summary>العودة لقائمة أوامر اليوم بعد الحذف.</summary>
    public Action ReturnToList { get; set; }
    private bool IsDraft => _card?.Status == DocStatuses.Draft;

    public OrderDocumentPanel(int orderId)
    {
        _orderId = orderId;
        FlowDirection = FlowDirection.RightToLeft;

        _itemsGrid.Columns.Add(OrderGridColumns.IdentCol("العميل المالك 👤", "Customer", 1.2, 140));
        _itemsGrid.Columns.Add(OrderGridColumns.IdentCol("الشحنة 🚢", "Shipment", 0.9, 110));
        _itemsGrid.Columns.Add(OrderGridColumns.IdentCol("الدفعة 📦", "Lot", 0.9, 110));
        _itemsGrid.Columns.Add(OrderGridColumns.IdentCol("الصنف الخام المستلم 🌴", "Raw", 1.3, 160));
        _itemsGrid.Columns.Add(OrderGridColumns.IdentCol("المنتج النهائي 🏷️", "Product", 1.3, 160));
        _itemsGrid.Columns.Add(OrderGridColumns.IdentCol("العبوة 📦", "Pack", 0.9, 110));
        _itemsGrid.Columns.Add(new DataGridTextColumn { Header = "المخطط (كجم)", Binding = new System.Windows.Data.Binding("PlannedKg"), Width = 90, IsReadOnly = true });
        _itemsGrid.Columns.Add(new DataGridTextColumn { Header = "المخطط (كرتون)", Binding = new System.Windows.Data.Binding("PlannedCartons"), Width = 95, IsReadOnly = true });
        _itemsGrid.Columns.Add(new DataGridTextColumn { Header = "المنتَج (كجم)", Binding = new System.Windows.Data.Binding("ProducedKg"), Width = 90, IsReadOnly = true });
        _itemsGrid.Columns.Add(new DataGridTextColumn { Header = "الحالة", Binding = new System.Windows.Data.Binding("StatusAr"), Width = 80, IsReadOnly = true });

        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "الصنف المساعد", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "المطلوب", Binding = new System.Windows.Data.Binding("Required"), Width = 90 });
        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "المصروف", Binding = new System.Windows.Data.Binding("Issued"), Width = 90 });
        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "المتبقي", Binding = new System.Windows.Data.Binding("Remaining"), Width = 90 });
        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "المتاح", Binding = new System.Windows.Data.Binding("Available"), Width = 90 });
        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "حالة المخزون", Binding = new System.Windows.Data.Binding("StockStatus"), Width = 90 });
        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "الوحدة", Binding = new System.Windows.Data.Binding("Unit"), Width = 80 });
        _materialsGrid.Columns.Add(new DataGridTextColumn { Header = "التفاصيل", Binding = new System.Windows.Data.Binding("Details"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        _eventsGrid.Columns.Add(new DataGridTextColumn { Header = "الوقت", Binding = new System.Windows.Data.Binding("Time"), Width = 130 });
        _eventsGrid.Columns.Add(new DataGridTextColumn { Header = "المستخدم", Binding = new System.Windows.Data.Binding("User"), Width = 140 });
        _eventsGrid.Columns.Add(new DataGridTextColumn { Header = "العملية", Binding = new System.Windows.Data.Binding("Action"), Width = 200 });
        _eventsGrid.Columns.Add(new DataGridTextColumn { Header = "التفاصيل", Binding = new System.Windows.Data.Binding("Detail"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        // §v1.50.34 — الرأس ثابت (لا يختفي بالتمرير): العنوان + الشريط الكلاسيكي + أزرار الحالة،
        // والمحتوى يُمرَّر تحته. زر الحفظ في رأس الشاشة كما طلب المستخدم.
        var head = new StackPanel { Margin = new Thickness(12, 10, 12, 2) };
        head.Children.Add(_title);
        head.Children.Add(BuildDocTools());
        head.Children.Add(_actionsPanel);
        var body = new StackPanel { Margin = new Thickness(12, 6, 12, 12) };
        body.Children.Add(_cardPanel);
        body.Children.Add(_progress);
        body.Children.Add(_progressText);
        body.Children.Add(Section("📦 بنود الأمر — الهوية كاملة: العميل/الشحنة/الدفعة/الصنف المستلم/المنتج النهائي", _itemsGrid));
        body.Children.Add(Section("🧰 المواد المساعدة المحتسبة", _materialsGrid));
        body.Children.Add(Section("📝 ملاحظات الأمر — تُعدَّل بوضع «✏️ تعديل» وتُحفظ بزر «💾 حفظ» من رأس الشاشة. كميات البنود ثابتة من الخطة المعتمدة.", _notesBox));
        body.Children.Add(Section("📋 سجل العمليات — من فعل ماذا ومتى", _eventsGrid));
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = dock;

        if (orderId > 0) Loaded += (_, _) => Refresh();
    }

    private static TextBlock Lbl(string t) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0), FontSize = 11.5 };

    private static Border Section(string header, UIElement body)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = header, FontWeight = FontWeights.Bold, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)), Margin = new Thickness(0, 8, 0, 4) });
        sp.Children.Add(body);
        return new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0xC8, 0xB0)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Margin = new Thickness(0, 6, 0, 0), Background = Brushes.White };
    }

    private bool Can(string module, string action)
    {
        try { return AppContainer.Get<SessionContext>().Can(module, action); }
        catch { return false; }
    }

    private void Refresh()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();

            _card = svc.GetOrderCard(_orderId);
            if (_card == null)
            {
                _title.Text = "أمر الإنتاج غير موجود.";
                return;
            }

            _title.Text = $"📝 أمر الإنتاج {_card.OrderNumber} — {_card.StatusAr}";
            _cardPanel.Children.Clear();
            AddCard("العميل", _card.CustomerName);
            AddCard("الصنف المستلم", _card.RawName);
            AddCard("المنتج النهائي", _card.ProductName);
            AddCard("الخطة", _card.PlanNumber);
            AddCard("الدفعة", _card.LotCode);
            AddCard("الشحنة", _card.ShipmentNumber);
            AddCard("التاريخ", _card.ProductionDate);
            AddCard("الوردية", _card.ShiftName);
            AddCard("الخط", _card.LineName);
            AddCard("وقت البداية", _card.StartTime);
            AddCard("النهاية المتوقع", _card.ExpectedEndTime);
            AddCard("المخطط في الخطة", $"{_card.PlannedInPlanKg:N1} كجم / {_card.PlannedInPlanCartons:N0} كرتون");
            AddCard("كمية الأمر", $"{_card.OrderedKg:N1} كجم / {_card.OrderedCartons:N0} كرتون");
            // §v1.50.24: حقول الفعلي/المقبول/المرفوض/المتبقي ليس لها دخل بأمر الإنتاج —
            // أُزيلت من هنا؛ مكانها الطبيعي شاشة تسليم الإنتاج وشاشة الجودة.
            AddCard("المعدل", $"{_card.RatePerHour:N0} كرتون/س — {_card.ExpectedHours:N1} س متوقعة");
            AddCard("أنشأه", $"{_card.CreatedBy} — {_card.CreatedDate}");

            _progress.Value = _card.ProgressPct;
            _progressText.Text = $"شريط التقدم: {_card.ProducedKg:N1} / {_card.OrderedKg:N1} كجم — {_card.ProgressPct:N0}%";

            // البنود بالهوية الكاملة — §B80 صفوف قابلة لتعديل الكراتين لأمر المسودة
            var items = db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == _orderId).ToList();
            _itemRows = items.Select(it => new OrderItemEditRow
            {
                Id = it.Id,
                Customer = it.CustomerId != null ? db.Customers.AsNoTracking().Where(c => c.Id == it.CustomerId).Select(c => c.CustomerName).FirstOrDefault() : "-",
                Shipment = it.ShipmentId != null ? db.Shipments.AsNoTracking().Where(s => s.Id == it.ShipmentId).Select(s => s.DocumentNumber).FirstOrDefault() : "-",
                Lot = db.Lots.AsNoTracking().Where(l => l.Id == it.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "-",
                Raw = db.Lots.AsNoTracking().Where(l => l.Id == it.LotId).Join(db.Products, l => l.ProductId, p => p.Id, (l, p) => p.ProductNameAr).FirstOrDefault() ?? "-",
                Product = db.Products.AsNoTracking().Where(p => p.Id == it.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                Pack = it.PackagingTypeId != null ? db.PackagingTypes.AsNoTracking().Where(p => p.Id == it.PackagingTypeId).Select(p => p.PackageNameAr).FirstOrDefault() : "-",
                PlannedKg = it.PlannedQtyKg,
                PlannedCartons = it.PlannedCartons,
                Cartons = it.PlannedCartons,
                ProducedKg = it.ProducedQtyKg,
                StatusAr = DocStatuses.ToArabic(it.Status)
            }).ToList();
            _itemsGrid.ItemsSource = _itemRows;

            try
            {
                var auxSvc = scope.ServiceProvider.GetRequiredService<DatesErp.Application.Services.AuxiliaryManagementService>();
                var needs = auxSvc.CalculateNeedsForOrder(_orderId);
                _materialsGrid.ItemsSource = needs.Select(n => new
                {
                    Name = n.AuxiliaryProductName,
                    Required = n.RequiredQty,
                    Issued = n.IssuedQty,
                    Remaining = n.RemainingQty,
                    Available = n.AvailableQty,
                    StockStatus = n.StockStatusAr,
                    Unit = n.Unit,
                    Details = n.CalculationDetails,
                    AuxiliaryProductId = n.AuxiliaryProductId
                }).ToList();
            }
            catch
            {
                // fallback للقديم
                _materialsGrid.ItemsSource = db.ProductionOrderMaterials.AsNoTracking().Where(m => m.OrderId == _orderId).ToList()
                    .Select(m => new
                    {
                        Name = m.AuxiliaryProductId != null ? db.Products.Where(p => p.Id == m.AuxiliaryProductId).Select(p => p.ProductNameAr).FirstOrDefault() : db.AuxiliaryMaterials.AsNoTracking().Where(a => a.Id == m.MaterialId).Select(a => a.MaterialNameAr).FirstOrDefault(),
                        Required = m.CalculatedQty,
                        Issued = m.ActualIssuedQty,
                        Remaining = m.CalculatedQty - m.ActualIssuedQty,
                        Available = 0.0,
                        StockStatus = "-",
                        Unit = m.UnitOfMeasure,
                        Details = "",
                        AuxiliaryProductId = m.AuxiliaryProductId
                    }).ToList();
            }

            _eventsGrid.ItemsSource = svc.GetOrderEvents(_orderId);
            _notesBox.Text = db.ProductionOrders.AsNoTracking().Where(o => o.Id == _orderId).Select(o => o.Notes).FirstOrDefault() ?? "";

            BuildActions();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "OrderWindow.Refresh"); }
    }

    private void AddCard(string label, string value)
    {
        var b = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF2, 0xE8)),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 4, 6, 2),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xD0, 0xBB)), BorderThickness = new Thickness(1)
        };
        b.Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = label, FontSize = 10, Foreground = Brushes.Gray },
                new TextBlock { Text = value ?? "-", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)) }
            }
        };
        _cardPanel.Children.Add(b);
    }

    private void BuildActions()
    {
        _actionsPanel.Children.Clear();
        string st = _card.Status;
        Button Btn(string text, string style, Action act, bool enabled)
        {
            // §B110: الحشوة من الثيم الموحد (12,5) بدل المضمّنة
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 0), IsEnabled = enabled };
            b.Style = (Style)System.Windows.Application.Current.FindResource(style);
            b.Click += (_, _) => { act(); };
            return b;
        }

        if (st == DocStatuses.Draft)
            _actionsPanel.Children.Add(Btn("🔒 اعتماد الأمر (صرف المواد المساعدة)", "ErpApproveButton", () => Do(s => s.ApproveOrder(_orderId)), Can("production", "Approve")));
        if (st is DocStatuses.Approved or DocStatuses.Scheduled)
        {
            _actionsPanel.Children.Add(Btn("🏭 بدء الإنتاج", "ErpPrimaryButton", () => Do(s => s.StartOrder(_orderId)), Can("execution", "Create")));
            // التسجيل من أمر متأخر لا يحتاج خطوة بدء منفصلة؛ الخدمة تبدأه ذرياً عند الحفظ.
            _actionsPanel.Children.Add(Btn("📤 تسجيل فعلي اليوم (بدء وإقفال اليوم)", "ErpPrimaryButton", CloseDay, Can("execution", "Edit")));
            _actionsPanel.Children.Add(Btn("↩ إلغاء الاعتماد", "ErpDangerButton", () => Do(s => s.UnapproveOrder(_orderId)), Can("production", "Cancel")));
        }
        if (st == DocStatuses.InProgress)
        {
            _actionsPanel.Children.Add(Btn("⏸ إيقاف مؤقت", "ErpDangerButton", () => Do(s => s.StopOrder(_orderId, null)), Can("execution", "Edit")));
            _actionsPanel.Children.Add(Btn("📤 تسجيل فعلي اليوم (إقفال اليوم)", "ErpPrimaryButton", CloseDay, Can("execution", "Edit")));
        }
        if (st == DocStatuses.Stopped)
        {
            _actionsPanel.Children.Add(Btn("▶ استئناف الإنتاج", "ErpApproveButton", () => Do(s => s.ResumeOrder(_orderId)), Can("execution", "Edit")));
            _actionsPanel.Children.Add(Btn("📤 تسجيل فعلي اليوم (إقفال اليوم)", "ErpPrimaryButton", CloseDay, Can("execution", "Edit")));
        }
        if (st is DocStatuses.Draft or DocStatuses.Approved or DocStatuses.Scheduled)
            _actionsPanel.Children.Add(Btn("✖ إلغاء الأمر", "ErpDangerButton", CancelWithReason, Can("production", "Cancel")));
        if (st is DocStatuses.Completed or DocStatuses.InProgress)
            _actionsPanel.Children.Add(Btn("🔒 إغلاق الأمر", "ErpButton", () => CloseOrderWithReason(), Can("production", "Cancel")));

        RefreshActionStates();
    }

    // ═══ §v1.50.37 — أدوات المستند فقط (بدون CRUD مكرر) ═══
    private WrapPanel BuildDocTools()
    {
        Button BarBtn(string text, string style, RoutedEventHandler click, string tip = null)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 4), ToolTip = tip };
            try { b.Style = (Style)System.Windows.Application.Current.FindResource(style); } catch { }
            b.Click += click;
            return b;
        }
        var bar = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        bar.Children.Add(BarBtn("↩ تحديث المستند", "ErpButton", (_, _) => Refresh()));
        bar.Children.Add(BarBtn("🖨 طباعة الأمر", "ErpButton", (_, _) => Print()));
        bar.Children.Add(BarBtn("تقرير التنفيذ والإقفال", "ErpButton", (_, _) => PrintExecution()));
        bar.Children.Add(BarBtn("📄 PDF", "ErpButton", (_, _) => Pdf()));
        _editBtn = BarBtn("✏️ تعديل", "ErpButton", Edit_Click);
        _saveBtn = BarBtn("💾 حفظ", "ErpApproveButton", Save_Click);
        _deleteBtn = BarBtn("🗑 حذف المسودة", "ErpDangerButton", Delete_Click);
        _editBtn.Visibility = _saveBtn.Visibility = _deleteBtn.Visibility = Visibility.Collapsed;
        return bar;
    }

    private void RefreshActionStates()
    {
        if (_editBtn == null) return;
        bool locked = _card == null || _card.Status == DocStatuses.Closed || _card.Status == DocStatuses.Cancelled;
        _editBtn.IsEnabled = !locked && Can("production", "Edit") && !_editing;
        _saveBtn.IsEnabled = _editing && Can("production", "Edit");
        _deleteBtn.IsEnabled = IsDraft && Can("production", "Delete");
    }

    public void InvokeEdit() => Edit_Click(null, null);
    public void InvokeSave() => Save_Click(null, null);
    public void InvokeDelete() => Delete_Click(null, null);
    public void InvokePrint() => Print();

    public static void PromptSearch(Window owner, Action<int> navigator)
    {
        var p = new OrderDocumentPanel(0) { Navigator = navigator };
        p.Search_Click(null, null);
    }

    public static void PromptAddFromPlan(Window owner, Action<int> navigator, Action changed)
    {
        var p = new OrderDocumentPanel(0) { Navigator = navigator, Changed = changed };
        p.AddFromPlan_Click(null, null);
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_card == null || _card.Status == DocStatuses.Closed || _card.Status == DocStatuses.Cancelled)
        { AppContainer.Get<DialogService>().Error("أوامر مغلقة أو ملغاة لا تُعدَّل."); return; }
        _editing = true;
        _notesBox.IsReadOnly = false;
        RefreshActionStates();
        AppContainer.Get<DialogService>().Info("وضع التعديل مفعّل — عدّل «ملاحظات الأمر» ثم اضغط «💾 حفظ» من رأس الشاشة. كميات البنود ثابتة من الخطة المعتمدة وراجعها جهة التخطيط.");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_editing) { AppContainer.Get<DialogService>().Info("لا تعديل مفعّل — اضغط «✏️ تعديل» أولاً."); return; }
        try
        {
            using var scope = AppContainer.NewScope();
            var r = scope.ServiceProvider.GetRequiredService<IProductionOrderService>().UpdateOrderHeader(_orderId, notes: _notesBox.Text ?? "");
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            _editing = false;
            _notesBox.IsReadOnly = true;
            Refresh();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "OrderDoc.Save"); }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!IsDraft) { AppContainer.Get<DialogService>().Error("الحذف لأمر المسودة فقط — الأمر المعتمد يُلغى ولا يُحذف."); return; }
        if (!AppContainer.Get<DialogService>().Confirm("حذف أمر الإنتاج (المسودة) نهائياً؟")) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var r = scope.ServiceProvider.GetRequiredService<IProductionOrderService>().DeleteOrder(_orderId);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            AppContainer.Get<DialogService>().Info(r.Message);
            Changed?.Invoke();
            ReturnToList?.Invoke();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "OrderDoc.Delete"); }
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        var win = new Window { Title = "بحث عن أمر إنتاج", Width = 740, Height = 470, FlowDirection = FlowDirection.RightToLeft, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this) };
        var term = new TextBox { Width = 320 };
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, Height = 310, RowHeight = 28 };
        grid.Columns.Add(new DataGridTextColumn { Header = "رقم المستند", Binding = new System.Windows.Data.Binding("DocumentNumber"), Width = 130 });
        grid.Columns.Add(new DataGridTextColumn { Header = "التاريخ", Binding = new System.Windows.Data.Binding("ProductionDate"), Width = 100 });
        grid.Columns.Add(new DataGridTextColumn { Header = "العميل", Binding = new System.Windows.Data.Binding("CustomerName"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "الحالة", Binding = new System.Windows.Data.Binding("StatusAr"), Width = 110 });
        void Run()
        {
            try
            {
                using var scope = AppContainer.NewScope();
                grid.ItemsSource = scope.ServiceProvider.GetRequiredService<IProductionOrderService>().SearchOrders(term.Text ?? "");
            }
            catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "OrderDoc.Search"); }
        }
        term.TextChanged += (_, _) => Run();
        var open = new Button { Content = "📂 فتح المحدد", Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton"), Margin = new Thickness(0, 8, 6, 0) };
        open.Click += (_, _) =>
        {
            if (grid.SelectedItem?.GetType().GetProperty("OrderId")?.GetValue(grid.SelectedItem) is int id)
            { Navigator?.Invoke(id); win.Close(); }
            else AppContainer.Get<DialogService>().Info("اختر أمراً من النتائج أولاً.");
        };
        grid.MouseDoubleClick += (_, _) => open.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var sp = new StackPanel { Margin = new Thickness(12) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "ابحث برقم المستند أو اسم العميل:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontWeight = FontWeights.Bold });
        row.Children.Add(term);
        sp.Children.Add(row);
        sp.Children.Add(grid);
        sp.Children.Add(open);
        win.Content = sp;
        win.Loaded += (_, _) => { term.Focus(); Run(); };
        win.ShowDialog();
    }

    private void AddFromPlan_Click(object sender, RoutedEventArgs e)
    {
        var win = new Window { Title = "إضافة أمر إنتاج من الخطط المعتمدة", Width = 1000, Height = 520, FlowDirection = FlowDirection.RightToLeft, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this) };
        var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, Height = 350, RowHeight = 30 };
        grid.Columns.Add(new DataGridTextColumn { Header = "التاريخ المجدول", Binding = new System.Windows.Data.Binding("ScheduledDate"), Width = 115 });
        grid.Columns.Add(new DataGridTextColumn { Header = "الخطة", Binding = new System.Windows.Data.Binding("PlanNumber"), Width = 130 });
        grid.Columns.Add(new DataGridTextColumn { Header = "العميل", Binding = new System.Windows.Data.Binding("CustomerName"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "الوردية", Binding = new System.Windows.Data.Binding("ShiftName"), Width = 130 });
        grid.Columns.Add(new DataGridTextColumn { Header = "الخط", Binding = new System.Windows.Data.Binding("LineName"), Width = 130 });
        grid.Columns.Add(new DataGridTextColumn { Header = "البنود", Binding = new System.Windows.Data.Binding("ItemsCount"), Width = 60 });
        grid.Columns.Add(new DataGridTextColumn { Header = "الكراتين", Binding = new System.Windows.Data.Binding("Cartons"), Width = 80 });
        var hint = new TextBlock { Margin = new Thickness(0, 6, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0x53, 0x69, 0x70)), TextWrapping = TextWrapping.Wrap };
        var issue = new Button { Content = "📤 إصدار الأمر للمجموعة المحددة", Style = (Style)System.Windows.Application.Current.FindResource("ErpApproveButton"), Margin = new Thickness(0, 8, 6, 0) };
        void Load()
        {
            try
            {
                using var scope = AppContainer.NewScope();
                var groups = scope.ServiceProvider.GetRequiredService<IProductionOrderService>().GetPendingPlanGroups();
                grid.ItemsSource = groups;
                hint.Text = groups.Count == 0
                    ? "لا توجد مجموعات معلّقة — تحقّق من اعتماد الخطة وتاريخ الجدولة، أو حدّث الشاشة."
                    : "كل صف = مجموعة (تاريخ مجدول سابق أو حالي أو قادم + خطة + عميل + وردية + خط) بلا أمر بعد. الإصدار بكميات الخطة الأصلية كما هي.";
            }
            catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "OrderDoc.AddLoad"); }
        }
        issue.Click += (_, _) =>
        {
            if (grid.SelectedItem is not DatesErp.Core.Interfaces.Services.TodayPendingGroupDto g)
            { AppContainer.Get<DialogService>().Info("اختر المجموعة أولاً."); return; }
            if (!AppContainer.Get<DialogService>().Confirm($"إصدار أمر إنتاج ليوم {g.ScheduledDate} لهذه المجموعة ({g.CustomerName} — {g.ItemsCount} بنداً / {g.Cartons:N0} كرتون)؟")) return;
            try
            {
                using var scope = AppContainer.NewScope();
                var r = scope.ServiceProvider.GetRequiredService<IProductionOrderService>().IssuePlanGroup(g.PlanId, g.ScheduledDate, g.CustomerId, g.ShiftId, g.LineId);
                if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
                AppContainer.Get<DialogService>().Info(r.Message);
                Changed?.Invoke();
                Navigator?.Invoke(r.Id);
                win.Close();
            }
            catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "OrderDoc.IssueGroup"); }
        };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = "الأوامر تنشأ من الخطط المعتمدة فقط — تظهر هنا الخطط المجدولة السابقة والحالية والقادمة، اختر المجموعة المطلوبة ثم أصدر أمرها:", FontWeight = FontWeights.Bold });
        sp.Children.Add(grid);
        sp.Children.Add(hint);
        sp.Children.Add(issue);
        win.Content = sp;
        win.Loaded += (_, _) => Load();
        win.ShowDialog();
    }

    private void Do(Func<IProductionOrderService, OpResult> op)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var res = op(scope.ServiceProvider.GetRequiredService<IProductionOrderService>());
            if (!res.Ok) AppContainer.Get<DialogService>().Error(res.Message);
            else AppContainer.Get<DialogService>().Info(res.Message);
            Refresh();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "OrderWindow.Action"); }
    }



    private void CancelWithReason()
    {
        if (!AppContainer.Get<DialogService>().Confirm("إلغاء أمر الإنتاج؟ إن كان معتمداً سيُعكس الصرف ويعود المتبقي للخطة.")) return;
        var dlg = new InputDialog("إلغاء أمر الإنتاج", "سبب الإلغاء (اختياري):");
        string reason = dlg.ShowDialog() == true ? dlg.Value : "";
        Do(s => s.CancelOrder(_orderId, reason));
    }

    /// <summary>§B95 — إغلاق الأمر: السبب فارغ عند الاكتمال وإجباري عند العجز (تسوية موثقة تُحفظ في الأمر).</summary>
    private void CloseOrderWithReason()
    {
        var dlg = new InputDialog("إغلاق أمر الإنتاج", "سبب الإغلاق (فارغ عند اكتمال الإنتاج — إجباري عند وجود عجز):");
        if (dlg.ShowDialog() != true) return;
        Do(s => s.CloseOrder(_orderId, dlg.Value));
    }

    private void CloseDay()
    {
        Services.ErrorLog.WriteInfo($"OrdersWindow.CloseDay CLICK OrderId={_orderId}");
        ProductionDeliveryView.OpenForOrder(_orderId, Window.GetWindow(this));
    }

    private ReportResult BuildReport()
    {
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var items = db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == _orderId).ToList();
        var report = new ReportResult
        {
            TitleAr = $"أمر إنتاج رقم: {_card.OrderNumber}",
            Columns = new List<string> { "العميل", "الدفعة", "الصنف المستلم", "المنتج النهائي", "المخطط (كجم)", "الكراتين", "المنتَج (كجم)" }
        };
        foreach (var it in items)
            report.Rows.Add(new object[]
            {
                it.CustomerId != null ? db.Customers.AsNoTracking().Where(c => c.Id == it.CustomerId).Select(c => c.CustomerName).FirstOrDefault() : "-",
                db.Lots.AsNoTracking().Where(l => l.Id == it.LotId).Select(l => l.LotCode).FirstOrDefault() ?? "-",
                db.Lots.AsNoTracking().Where(l => l.Id == it.LotId).Join(db.Products, l => l.ProductId, p => p.Id, (l, p) => p.ProductNameAr).FirstOrDefault() ?? "-",
                db.Products.AsNoTracking().Where(p => p.Id == it.ProductId).Select(p => p.ProductNameAr).FirstOrDefault() ?? "-",
                it.PlannedQtyKg, it.PlannedCartons, it.ProducedQtyKg
            });
        report.Summary["الخطة"] = _card.PlanNumber;
        report.Summary["العميل"] = _card.CustomerName;
        report.Summary["التاريخ والوردية"] = $"{_card.ProductionDate} — {_card.ShiftName}";
        report.Summary["وقت البداية / النهاية المتوقع"] = $"{_card.StartTime} / {_card.ExpectedEndTime}";
        report.Summary["الحالة"] = _card.StatusAr;
        report.Summary["توقيع مدير الإنتاج"] = "____________________";
        report.Summary["توقيع مشرف الوردية"] = "____________________";
        report.Summary["توقيع مسؤول الجودة"] = "____________________";
        return report;
    }

    private void PrintExecution()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var m = Printing.StoredPrintModels.Execution(db, _orderId);
            new PrintPreviewWindow(PhasePrint.Build(m), $"{m.DocTitle} {m.DocNo}") { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Execution.Print"); }
    }

    private void Print()
    {
        try
        {
            using var scope=AppContainer.NewScope();
            var m=Printing.StoredPrintModels.Order(scope.ServiceProvider.GetRequiredService<DatesErpDbContext>(),_orderId);
            new PrintPreviewWindow(PhasePrint.Build(m),$"{m.DocTitle} {m.DocNo}") { Owner=Window.GetWindow(this) }.ShowDialog();
        }
        catch(Exception ex) { AppContainer.Get<DialogService>().HandleException(ex,"Order.Print"); }
    }
    private void Pdf() => Print();
}
