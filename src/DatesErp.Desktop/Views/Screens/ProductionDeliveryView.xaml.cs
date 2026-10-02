using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>التسجيل الفعلي وأمر تسليم الإنتاج مساران متتابعان في إدارة الإنتاج؛ الاستلام المخزني لا يُنشأ من هنا تلقائياً.</summary>
public partial class ProductionDeliveryView : UserControl
{
    public static int? PendingOrderId { get; set; }
    public ObservableCollection<ActualByProductDefinitionDto> ByProductDefinitions { get; } = new();
    private readonly ObservableCollection<ActualProductionRow> _items = new();
    private readonly ObservableCollection<ActualSecondaryRow> _secondary = new();
    private readonly ObservableCollection<DeliveryEditRow> _deliveryItems = new();
    private bool _saving;
    private readonly Func<List<ActualDeliveryOrderDto>> _loadOrders;
    private readonly Func<List<ActualByProductDefinitionDto>> _loadDefinitions;
    private readonly Func<ActualProductionDto, OpResult> _saveActual;
    private List<ActualByProductDefinitionDto> _activeDefinitions = new();
    public ProductionDeliveryView() : this(null, null, null) { }
    public ProductionDeliveryView(Func<List<ActualDeliveryOrderDto>> loadOrders,
        Func<List<ActualByProductDefinitionDto>> loadDefinitions, Func<ActualProductionDto, OpResult> saveActual)
    {
        _loadOrders = loadOrders; _loadDefinitions = loadDefinitions; _saveActual = saveActual;
        InitializeComponent(); ItemsGrid.ItemsSource = _items; SecondaryGrid.ItemsSource = _secondary; DeliveryItemsGrid.ItemsSource = _deliveryItems;
        // §v1.50.38: الإحصاءات الحية في شريط السياق — تتحدث مع كل كتابة في عمود الفعلي.
        _items.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (ActualProductionRow r in e.NewItems) r.PropertyChanged += Row_Changed;
            UpdateStats();
        };
        Loaded += (_, _) => LoadOrders(PendingOrderId, PendingOrderId.HasValue);
    }
    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("تسليم الإنتاج — تسجيل الفعلي"); chrome.SetScreenCode("MRPMPS1021");
        chrome.SetToolbar(new Views.ErpToolbar()
            .WithPrint((_, _) => Print(), "طباعة التنفيذ المحفوظ من قاعدة البيانات")
            .WithList((_, _) => LoadOrders((OrderBox.SelectedItem as ActualDeliveryOrderDto)?.OrderId, false), "تحديث أوامر اليوم")
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard")));
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard");
    }
    public static void OpenForOrder(int orderId, Window source)
    {
        ErrorLog.WriteInfo($"ProductionDeliveryView.OpenForOrder OrderId={orderId} Source={source?.GetType().Name ?? "<null>"}");
        PendingOrderId = orderId;
        var main = System.Windows.Application.Current.MainWindow as MainWindow;
        var taskOwner = source?.Owner as TaskWindow;
        if (source != null && source != main) source.Close();
        taskOwner?.Close();
        main?.OpenScreen("proddelivery");
    }
    private T WithService<T>(Func<IProductionDeliveryService, T> action)
    {
        using var scope = AppContainer.NewScope();
        return action(scope.ServiceProvider.GetRequiredService<IProductionDeliveryService>());
    }
    private void LoadOrders(int? selected, bool includeSelectedOrder)
    {
        try
        {
            _activeDefinitions = _loadDefinitions?.Invoke() ?? WithService(s => s.GetActualByProducts());
            ByProductDefinitions.Clear(); foreach (var b in _activeDefinitions) ByProductDefinitions.Add(b);
            // الإصدار الحالي من IProductionDeliveryService يعرّض API بدون معامل.
            // لا نغيّر توقيع الخدمة؛ نحمّل القائمة عبر الاستدعاء الموجود فعلياً.
            var orders = _loadOrders?.Invoke() ?? WithService(s => s.GetActualDeliveryOrders(selected));
            OrderBox.ItemsSource = orders;
            OrderBox.SelectedItem = selected.HasValue ? orders.FirstOrDefault(o => o.OrderId == selected.Value) : null;
            PendingOrderId = null;
            if (orders.Count() == 0) StatusLabel.Text = "لا توجد أوامر مطابقة لخطة اليوم المعتمدة. لا تُضاف أصناف أو خطط من هذه الشاشة.";
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ActualDelivery.Load"); }
    }
    private void Order_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ItemsGrid == null) return;
        ByProductDefinitions.Clear(); foreach (var b in _activeDefinitions) ByProductDefinitions.Add(b);
        _items.Clear(); _secondary.Clear(); RawBox.Text = ""; DowntimeBox.Text = "0"; ReasonBox.Text = ""; NotesBox.Text = "";
        var order = OrderBox.SelectedItem as ActualDeliveryOrderDto;
        SaveButton.IsEnabled = order?.CanRecord == true;
        CreateDeliveryButton.IsEnabled = order?.CanCreateDelivery == true;
        DeliveryOrderPanel.Visibility = order?.ProductionDeliveryId > 0 ? Visibility.Visible : Visibility.Collapsed;
        _deliveryItems.Clear();
        if (order?.ProductionDeliveryId > 0) LoadDelivery(order.ProductionDeliveryId);
        // §v1.50.24: لوحة الإدخال تظهر فقط للأمر القابل للتسجيل — بدل لوحة ضخمة معطّلة.
        // والمساحة المتبقية تعرض إرشاداً واضحاً بدل حقول ميتة.
        ActualFieldsOuter.Visibility = order?.CanRecord == true ? Visibility.Visible : Visibility.Collapsed;
        EmptyGuide.Visibility = order?.CanRecord == true || order?.Recorded == true ? Visibility.Collapsed : Visibility.Visible;
        ItemsGrid.IsReadOnly = order?.CanRecord != true;
        // §v1.50.38: شرائح السياق بلا رموز تعبيرية — الهوية البصرية من الثيم لا من النص
        CustChip.Text = $"العميل: {order?.Customer ?? "—"}";
        PlanChip.Text = $"الخطة: {order?.PlanNumber ?? "—"}";
        ShiftChip.Text = $"الوردية: {order?.Shift ?? "—"}";
        StatusLabel.Text = string.IsNullOrWhiteSpace(order?.Status) ? "اختر أمراً من الأعلى لتظهر بنوده." : order.Status;
        if (order == null) return;
        foreach (var line in order.Items) _items.Add(new ActualProductionRow(line, order.Recorded));
        if (order.Items.Count == 0) StatusLabel.Text += "\nتنبيه: هذا الأمر بلا بنود خطة — راجع أمر الإنتاج نفسه قبل التسجيل.";
        if (!order.Recorded) { _secondary.Add(new ActualSecondaryRow()); return; }
        RawBox.Text = order.ConsumedRawKg.ToString(CultureInfo.CurrentCulture);
        DowntimeBox.Text = order.DowntimeHours.ToString(CultureInfo.CurrentCulture); ReasonBox.Text = order.DowntimeReason; NotesBox.Text = order.Notes;
        foreach (var definition in order.RecordedByProductDefinitions)
            if (!ByProductDefinitions.Any(d => d.Id == definition.Id)) ByProductDefinitions.Add(definition);
        foreach (var b in order.ByProducts)
            _secondary.Add(new ActualSecondaryRow { Definition = ByProductDefinitions.FirstOrDefault(d => d.Id == b.ByProductId)
                ?? new ActualByProductDefinitionDto { Id = b.ByProductId, Name = $"مخرج محفوظ #{b.ByProductId} (موقوف)", Unit = "راجع التعريف" },
                Quantity = b.QtyKg.ToString(CultureInfo.CurrentCulture) });
        StatusLabel.Text += $"\nأمر تسليم الإنتاج: {order.ProductionDeliveryNumber ?? "لم يُنشأ بعد"}  |  الحالة: {order.ProductionDeliveryStatus ?? "—"}  |  الفحص: {order.QualityNumber ?? "—"}";
    }
    // §v1.50.38: أي كتابة في عمود «المنتج فعليًا» تعيد حساب شريط الإحصاءات فوراً.
    private void Row_Changed(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActualProductionRow.Actual)) UpdateStats();
    }
    private void UpdateStats()
    {
        if (StatPlanned == null) return;
        var planned = 0; var actual = 0;
        foreach (var i in _items) { planned += i.Planned; if (i.TryQuantity(out var q)) actual += q; }
        StatPlanned.Text = planned.ToString("N0", CultureInfo.CurrentCulture);
        StatActual.Text = actual.ToString("N0", CultureInfo.CurrentCulture);
        StatItems.Text = _items.Count.ToString(CultureInfo.CurrentCulture);
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ErrorLog.WriteInfo($"ActualDelivery.Save_Click STEP=ENTER Saving={_saving}");
            if (_saving)
            {
                ErrorLog.WriteInfo("ActualDelivery.Save_Click STEP=EXIT Reason=AlreadySaving");
                return;
            }

            var selectedItem = OrderBox.SelectedItem;
            ErrorLog.WriteInfo($"ActualDelivery.Save_Click STEP=READ_ORDER_SELECTED SelectedItemType={selectedItem?.GetType().FullName ?? "<null>"}");
            if (selectedItem is not ActualDeliveryOrderDto order)
            {
                ErrorLog.WriteInfo("ActualDelivery.Save_Click STEP=EXIT Reason=SelectedItemIsNotActualDeliveryOrderDto");
                return;
            }

            ErrorLog.WriteInfo($"ActualDelivery.Save_Click STEP=READ_ORDER_ID OrderId={order.OrderId}");
            ErrorLog.WriteInfo($"ActualDelivery.Save_Click STEP=LOAD_ORDER OrderId={order.OrderId} CanRecord={order.CanRecord} Recorded={order.Recorded} Items={order.Items?.Count ?? 0}");
            // §v1.50.24: لا صمت أبداً — سبب تعذّر الحفظ يظهر في بانر الحالة.
            if (!order.CanRecord)
            {
                ErrorLog.WriteInfo($"ActualDelivery.Save_Click STEP=EXIT Reason=OrderCannotRecord OrderId={order.OrderId} Status={order.Status}");
                StatusLabel.Text = "تنبيه — لا يمكن الحفظ: " + (order.Status ?? "الأمر غير قابل للتسجيل.");
                return;
            }

            ErrorLog.WriteInfo($"ActualDelivery.Save_Click STEP=COMMIT_GRID_EDITS OrderId={order.OrderId}");
            ItemsGrid.CommitEdit(DataGridEditingUnit.Cell, true); ItemsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            SecondaryGrid.CommitEdit(DataGridEditingUnit.Cell, true); SecondaryGrid.CommitEdit(DataGridEditingUnit.Row, true);
            ErrorLog.WriteInfo($"ActualDelivery.Save_Click STEP=READ_ACTUAL_ROWS OrderId={order.OrderId} Items={_items.Count} SecondaryItems={_secondary.Count} RawText={RawBox.Text} DowntimeText={DowntimeBox.Text}");
            if (_items.Any(i => !i.TryQuantity(out _))) throw new ArgumentException("أدخل الفعلي لكل بند، كراتين صحيحة لا تتجاوز المخطط. الصفر يُدخل صراحةً.");
            if (!ActualProductionRow.TryNonnegative(RawBox.Text, out var raw) || raw <= 0) throw new ArgumentException("أدخل الخام المستهلك فعليًا — كجم، دون استنتاج من الخطة.");
            if (!ActualProductionRow.TryNonnegative(DowntimeBox.Text, out var hours)) throw new ArgumentException("أدخل ساعات توقف صحيحة غير سالبة.");

            ErrorLog.WriteInfo($"ActualDelivery.Save_Click STEP=BUILD_DTO OrderId={order.OrderId}");
            var input = new ActualProductionDto { OrderId = order.OrderId, ConsumedRawKg = raw, DowntimeHours = hours, DowntimeReason = ReasonBox.Text, Notes = NotesBox.Text };
            foreach (var i in _items) { i.TryQuantity(out var q); input.Items.Add(new() { OrderItemId = i.Source.OrderItemId, ActualCartons = q }); }
            foreach (var b in _secondary)
            {
                if (b.Definition == null && string.IsNullOrWhiteSpace(b.Quantity)) continue;
                if (b.Definition == null || !ActualProductionRow.TryNonnegative(b.Quantity, out var q) || q <= 0) throw new ArgumentException("اختر المخرج الثانوي وأدخل كميته الموجبة، أو احذف السطر غير المستخدم.");
                input.ByProducts.Add(new() { ByProductId = b.Definition.Id, QtyKg = q });
            }
            ErrorLog.WriteInfo($"ActualDelivery.Save_Click STEP=DTO_BUILT OrderId={input.OrderId} Items={input.Items.Count} ByProducts={input.ByProducts.Count} ConsumedRawKg={input.ConsumedRawKg} DowntimeHours={input.DowntimeHours}");

            _saving = true; SaveButton.IsEnabled = false;
            ErrorLog.WriteInfo($"ActualDelivery.Save_Click STEP=CALL_SAVE_ACTUAL_PRODUCTION OrderId={input.OrderId}");
            var r = _saveActual?.Invoke(input) ?? WithService(s => s.SaveActualProduction(input));
            ErrorLog.WriteInfo($"ActualDelivery.Save_Click STEP=RETURN_SAVE_ACTUAL_PRODUCTION OrderId={input.OrderId} Ok={r.Ok} ResultId={r.Id} Message={r.Message}");
            if (!r.Ok) { StatusLabel.Text = r.Message; return; }
            string msg = r.Message;
            int exeId = r.Id;
            if (exeId > 0)
            {
                try
                {
                    var delRes = WithService(s => s.CreateDeliveryFromActual(exeId, DateTime.Now.ToString("dd/MM/yyyy"), input.Notes));
                    if (delRes.Ok)
                    {
                        msg += "\n" + delRes.Message;
                    }
                }
                catch (Exception ex)
                {
                    ErrorLog.WriteInfo($"ActualDelivery.Save_Click AutoCreateDeliveryFailed: {ex.Message}");
                }
            }
            MessageBox.Show(msg, "تأكيد حفظ تسجيل الفعلي وإقفال اليوم", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusLabel.Text = msg;
            LoadOrders(null, false);
        }
        catch (ArgumentException ex)
        {
            WriteSaveExceptionTrace(ex);
            StatusLabel.Text = ex.Message;
        }
        catch (Exception ex)
        {
            WriteSaveExceptionTrace(ex);
            StatusLabel.Text = $"ActualDelivery.Save: {ex.GetType().FullName}: {ex.Message}";
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = (OrderBox.SelectedItem as ActualDeliveryOrderDto)?.CanRecord == true;
            CreateDeliveryButton.IsEnabled = (OrderBox.SelectedItem as ActualDeliveryOrderDto)?.CanCreateDelivery == true;
        }
    }

    private static void WriteSaveExceptionTrace(Exception ex)
    {
        var parts = new List<string>();
        var current = ex;
        var level = 0;
        while (current != null)
        {
            parts.Add($"InnerExceptionLevel={level} Type={current.GetType().FullName} Message={current.Message} StackTrace={current.StackTrace ?? "<null>"}");
            current = current.InnerException;
            level++;
        }
        ErrorLog.WriteInfo($"ActualDelivery.Save_Click EXCEPTION\n{string.Join(Environment.NewLine, parts)}");
    }
    private void LoadDelivery(int deliveryId)
    {
        try
        {
            _deliveryItems.Clear();
            var card = WithService(s => s.GetDelivery(deliveryId));
            if (card == null) return;
            DeliveryStatusLabel.Text = $"{card.DocumentNumber} — {card.StatusAr} — المصدر: {card.SourceNumber} ({card.SourceTypeAr})";
            SaveDeliveryButton.IsEnabled = card.Status == "Draft";
            IssueDeliveryButton.IsEnabled = card.Status == "Draft";
            foreach (var line in card.Lines)
                _deliveryItems.Add(new DeliveryEditRow
                {
                    ItemId = line.Id, ProductId = line.ProductId, LotId = line.LotId,
                    CustomerId = line.CustomerId, PackagingTypeId = line.PackagingTypeId,
                    Product = line.ProductName, Customer = line.CustomerName ?? "—",
                    ActualKg = line.QtyKg + line.ReceivedQtyKg,
                    RemainingKg = line.RemainingQtyKg, QtyKg = line.QtyKg,
                    PackageCount = line.PackageCount
                });
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ActualDelivery.LoadDelivery"); }
    }

    private void SaveDelivery_Click(object sender, RoutedEventArgs e)
    {
        if (OrderBox.SelectedItem is not ActualDeliveryOrderDto { ProductionDeliveryId: > 0 } order) return;
        try
        {
            DeliveryItemsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            DeliveryItemsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var items = _deliveryItems.Where(x => x.QtyKg > 0.001).Select(x => new ProductionDeliveryItemDto
            {
                OrderId = order.OrderId,
                ProductId = x.ProductId, LotId = x.LotId, CustomerId = x.CustomerId,
                PackagingTypeId = x.PackagingTypeId, PackageCount = x.PackageCount, QtyKg = x.QtyKg
            }).ToList();
            var result = WithService(s => s.UpdateDelivery(order.ProductionDeliveryId,
                DateTime.Now.ToString("dd/MM/yyyy"), items, order.Notes));
            if (!result.Ok) { DeliveryStatusLabel.Text = result.Message; return; }
            LoadOrders(order.OrderId, true);
            StatusLabel.Text = result.Message;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ActualDelivery.UpdateDelivery"); }
    }

    private void IssueDelivery_Click(object sender, RoutedEventArgs e)
    {
        if (OrderBox.SelectedItem is not ActualDeliveryOrderDto { ProductionDeliveryId: > 0 } order) return;
        try
        {
            var result = WithService(s => s.IssueDelivery(order.ProductionDeliveryId));
            if (!result.Ok) { DeliveryStatusLabel.Text = result.Message; return; }
            LoadOrders(order.OrderId, true);
            StatusLabel.Text = result.Message;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ActualDelivery.IssueDelivery"); }
    }

    private void CreateDelivery_Click(object sender, RoutedEventArgs e)
    {
        if (OrderBox.SelectedItem is not ActualDeliveryOrderDto { Recorded: true, CanCreateDelivery: true } order || order.ExecutionId <= 0)
        {
            StatusLabel.Text = "احفظ الإنتاج الفعلي أولاً، ثم أنشئ أمر التسليم من التنفيذ المحفوظ.";
            return;
        }
        try
        {
            var result = WithService(s => s.CreateDeliveryFromActual(order.ExecutionId,
                DateTime.Now.ToString("dd/MM/yyyy"), order.Notes));
            if (!result.Ok) { StatusLabel.Text = result.Message; return; }
            LoadOrders(order.OrderId, true);
            StatusLabel.Text = result.Message + "\nأمر التسليم مسودة قابلة للتعديل قبل تحريرها للمخزن.";
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ActualDelivery.CreateDelivery"); }
    }

    // §v1.50.32: سطر الإضافة الفارغ يهدأ ويتوضح — لا تظليل صارخ يُقرأ كخطأ برمجي.
    private void Secondary_LoadingRow(object sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is not ActualSecondaryRow) // سطر «+» الفارغ فقط — الصفوف الحقيقية كما هي
        {
            e.Row.Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF1, 0xE7));
            e.Row.ToolTip = "سطر فارغ للإضافة: اختر المخرج من القائمة وأدخل كميته الموجبة — الصفوف غير المستخدمة تُحذف بـ Delete.";
        }
    }
    private void ItemsGrid_PreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (e.EditingElement is TextBox tb)
        {
            tb.Focus();
            tb.SelectAll();
        }
    }
    // §v1.50.26: النقرة الواحدة تبدأ التحرير — الكمية تستقبل الكتابة فوراً بلا نقرتين.
    private void Grid_SingleClickEdit(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || grid.IsReadOnly) return;
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null and not DataGridCell) dep = VisualTreeHelper.GetParent(dep);
        if (dep is DataGridCell { IsReadOnly: false, IsEditing: false } cell)
        {
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            grid.CurrentCell = new DataGridCellInfo(cell);
            grid.BeginEdit();
        }
    }
    private void Print()
    {
        if (OrderBox.SelectedItem is not ActualDeliveryOrderDto { Recorded: true } order)
        {
            StatusLabel.Text = "الطباعة من تنفيذ محفوظ فقط، وليست من مدخلات الشاشة.";
            return;
        }
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErp.Infrastructure.Persistence.DatesErpDbContext>();
            var model = Printing.StoredPrintModels.Execution(db, order.OrderId);
            new PrintPreviewWindow(PhasePrint.Build(model), $"{model.DocTitle} {model.DocNo}") { Owner = Window.GetWindow(this) }.ShowDialog();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "ActualDelivery.Print"); }
    }
}
