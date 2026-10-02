using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DatesErp.Core.Common;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Screens;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>لوحة المؤشرات الرئيسية — مطابقة للشاشة المعتمدة: مركز المهام + الإدارات السبع.</summary>
public partial class DashboardView : UserControl
{
    private readonly string _deptFilter;

    public DashboardView(string deptFilter = null)
    {
        InitializeComponent();
        _deptFilter = deptFilter;
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        try
        {
            DashDate.Text = "التاريخ: " + DateTime.Now.ToString("dd/MM/yyyy");
            var session0 = AppContainer.Get<Infrastructure.Session.SessionContext>();
            DashUser.Text = session0.UserName ?? "—";
            FootUser.Text = session0.UserName ?? "—";
            // §B84/D1: تسمية الاتصال كانت ثابتة "المركزية" حتى في الوضع المحلي — الآن حية.
            try
            {
                using var scope0 = AppContainer.NewScope();
                var db0 = (DatesErpDbContext)scope0.ServiceProvider.GetService(typeof(DatesErpDbContext));
                FootDb.Text = db0 != null && db0.Database.IsSqlServer()
                    ? "قاعدة البيانات: SQL Server المركزية" : "قاعدة البيانات: محلية (SQLite)";
            }
            catch { FootDb.Text = "قاعدة البيانات: محلية"; }
            TaskUserChip.Text = $"المستخدم: {session0.UserName ?? "—"}";

            // أزرار الإدارات في شريط الأدوات
            if (DeptToolbar.Children.Count == 0)
            {
                foreach (var dept in ScreenCatalog.Departments)
                {
                    var b = new Button { Content = $"{dept.Icon} {dept.Title}", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), Margin = new Thickness(2, 0, 2, 0) };
                    var did = dept.Id;
                    b.Click += (_, _) => ((MainWindow)Window.GetWindow(this))?.OpenDeptDashboardPublic(did);
                    DeptToolbar.Children.Add(b);
                }
                var home = new Button { Content = "🏠 الرئيسية", Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton"), Margin = new Thickness(2, 0, 2, 0) };
                home.Click += (_, _) => ((MainWindow)Window.GetWindow(this))?.OpenScreen("dashboard");
                DeptToolbar.Children.Add(home);
            }

            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var session = AppContainer.Get<Infrastructure.Session.SessionContext>();
            bool isAdmin = session.Roles.Contains("Administrator") || session.Roles.Contains("Management");
            bool isProd = isAdmin || session.Roles.Contains("Production");
            bool isWh = isAdmin || session.Roles.Contains("Warehouse");

            // ── عدّادات مركز المهام حسب الدور ──
            var pendingPlans = db.ProductionPlans.Where(p => !p.IsApproved && !p.IsClosed && p.Status != DocStatuses.Cancelled).ToList();
            var approvedToday = db.ProductionPlans.Where(p => p.IsApproved && p.ApprovedDate != null && p.ApprovedDate.Value.Date == DateTime.Now.Date).Count();
            var activeExecs = db.ProductionExecutions.Where(e => e.Status == DocStatuses.InProgress).Count();
            var dueOrders = db.ProductionOrders.Where(o => o.IsApproved && !o.IsClosed).Count();
            var awaitingReceipts = db.FinishedGoodsReceipts.Where(r => r.Status == DocStatuses.Issued && r.ReceiptStatus != "Full").ToList();

            TasksPanel.Children.Clear();
            if (isAdmin)
                TasksPanel.Children.Add(TaskCounter("خطط بانتظار الاعتماد", pendingPlans.Count, "🔔", "#FEF2F2", "#FCA5A5", "#DC2626"));
            if (isProd)
            {
                TasksPanel.Children.Add(TaskCounter("أوامر تشغيل جاهزة", dueOrders, "⚡", "#F0FDF4", "#86EFAC", "#16A34A"));
                TasksPanel.Children.Add(TaskCounter("جلسات تشغيل جارية", activeExecs, "🏭", "#EFF6FF", "#BFDBFE", "#2563EB"));
            }
            if (isAdmin)
                TasksPanel.Children.Add(TaskCounter("خطط اعتمدت اليوم", approvedToday, "🟢", "#F0FDF4", "#86EFAC", "#16A34A"));
            if (isWh)
                TasksPanel.Children.Add(TaskCounter("🧾 أوامر تسليم بانتظار سند الاستلام", awaitingReceipts.Count, "🏬", "#FFF7ED", "#FDBA74", "#EA580C"));

            // ── §لمسة مؤسسية: تنبيهات تشغيلية حرجة (لا تظهر إلا إن وُجدت) ──
            var alerts = new List<(string text, string bg, string border, string fg)>();

            // 1) خام تحت حد إعادة الطلب
            var lowRaw = db.Products.AsNoTracking()
                .Where(p => p.ItemType == "Raw" && p.IsActive && p.ReorderLevel > 0)
                .ToList()
                .Where(p => db.StockBalances.AsNoTracking()
                    .Where(b => b.ProductId == p.Id).Sum(b => (double?)b.QtyKg) is double q
                    ? q < p.ReorderLevel : true)
                .Select(p => p.ProductNameAr).ToList();
            if (lowRaw.Count > 0)
                alerts.Add(($"⛔ خام تحت حد إعادة الطلب ({lowRaw.Count}): {string.Join("، ", lowRaw.Take(4))}{(lowRaw.Count > 4 ? "…" : "")}",
                    "#FEF2F2", "#FCA5A5", "#991B1B"));

            // 2) أوامر إنتاج تجاوزت تاريخها ولم تُغلق
            var overdue = db.ProductionOrders.AsNoTracking()
                .Where(o => o.IsApproved && !o.IsClosed && o.ProductionDate != null && o.ProductionDate < DateTime.Today).Count();
            if (overdue > 0)
                alerts.Add(($"⏰ {overdue} أمر إنتاج تجاوز تاريخ إنتاجه ولم يُغلق", "#FFF7ED", "#FDBA74", "#9A3412"));

            // 3) تسليمات معتمدة لم تُفوتر
            var unbilled = db.CustomerDeliveries.AsNoTracking()
                .Where(d => d.IsApproved && d.InvoicedQtyKg < d.TotalQtyKg).Count();
            if (unbilled > 0)
                alerts.Add(($"🧾 {unbilled} سند تسليم معتمد لم يُفوتر بالكامل", "#FFFBEB", "#FDE68A", "#92400E"));

            // 4) فحوصات جودة بقرار مرفوض أو محجوز
            var held = db.QualityChecks.AsNoTracking()
                .Where(c => c.IsApproved && (c.Decision == "Rejected" || c.Decision == "Quarantine")).Count();
            if (held > 0)
                alerts.Add(($"🚫 {held} فحص جودة بقرار مرفوض أو حجز — ممنوع تسليمه للعميل", "#FEF2F2", "#FCA5A5", "#991B1B"));

            if (alerts.Count > 0)
            {
                TasksPanel.Children.Add(new TextBlock
                {
                    Text = "⚠ تنبيهات تشغيلية",
                    FontWeight = FontWeights.ExtraBold,
                    FontSize = 12.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x1B, 0x1B)),
                    Margin = new Thickness(0, 10, 0, 4)
                });
                foreach (var a in alerts)
                    TasksPanel.Children.Add(AlertCard(a.text, a.bg, a.border, a.fg));
            }

            // ── جدول الخطط بانتظار الاعتماد (اعتماد فوري من اللوحة) ──
            PendingGridBox.Visibility = isAdmin && pendingPlans.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (isAdmin && pendingPlans.Count > 0)
            {
                PendingGrid.ItemsSource = pendingPlans.OrderByDescending(p => p.Id).Select(p => new
                {
                    Id = p.Id,
                    الرقم = p.DocumentNumber,
                    العنوان_والفترة = $"{p.PlanTitle} ({p.StartDate:dd/MM/yyyy} إلى {p.EndDate:dd/MM/yyyy})",
                    الأصناف = db.ProductionPlanItems.Count(i => i.PlanId == p.Id),
                    إجمالي_الوزن = $"{db.ProductionPlanItems.Where(i => i.PlanId == p.Id).Sum(i => i.PlannedQtyKg):N1} كجم",
                    الكراتين = db.ProductionPlanItems.Where(i => i.PlanId == p.Id).Sum(i => i.PlannedCartons),
                    الحالة = p.Status == "UnderApproval" || p.Status == DocStatuses.Submitted ? "بانتظار اعتمادك ⏳" : "مسودة قيد الإعداد 📝"
                }).ToList();
            }

            // ── جدول أوامر التسليم بانتظار سند الاستلام ──
            WhGridBox.Visibility = isWh && awaitingReceipts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (isWh && awaitingReceipts.Count > 0)
            {
                WhGrid.ItemsSource = awaitingReceipts.OrderByDescending(r => r.Id).Select(r => new
                {
                    Id = r.Id,
                    رقم_أمر_التسليم = r.DocumentNumber,
                    أمر_الإنتاج = db.ProductionOrders.Where(o => o.Id == r.OrderId).Select(o => o.DocumentNumber).FirstOrDefault(),
                    تاريخ_التسليم = r.DeliveryDate?.ToString("dd/MM/yyyy"),
                    الإجمالي = $"{db.FinishedGoodsReceiptItems.Where(i => i.ReceiptId == r.Id).Sum(i => i.NetWeightKg):N1} كجم",
                    المستلم_سابقاً = $"{db.FinishedGoodsReceiptItems.Where(i => i.ReceiptId == r.Id).Sum(i => i.ReceivedQtyKg):N1} كجم",
                    المتبقي = $"{db.FinishedGoodsReceiptItems.Where(i => i.ReceiptId == r.Id).Sum(i => i.NetWeightKg - i.ReceivedQtyKg):N1} كجم",
                    الحالة = r.ReceiptStatus == "Partial" ? "استلام جزئي سابق" : "بانتظار أول سند"
                }).ToList();
            }

            // ── محتوى الإدارات: شبكة شاشات الإدارة المحددة أو بطاقات الإدارات ──
            DeptArea.Children.Clear();
            if (_deptFilter != null)
            {
                DeptArea.Children.Add(BuildDeptScreenGrid(_deptFilter));
            }
            else
            {
                DeptArea.Children.Add(new TextBlock
                {
                    Text = "📂 إدارات وأقسام النظام — اختر الإدارة لفتح شاشاتها ونوافذها الفرعية",
                    FontSize = 12, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)),
                    Margin = new Thickness(0, 0, 0, 10)
                });
                var tiles = new WrapPanel();
                foreach (var dept in ScreenCatalog.Departments)
                {
                    var count = ScreenCatalog.All.Count(s => s.Group == dept.Title);
                    var tile = new Border
                    {
                        Background = Brushes.White,
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0x7F, 0x9D, 0xB9)),
                        BorderThickness = new Thickness(1.5),
                        Padding = new Thickness(16, 14, 16, 14),
                        Width = 300,
                        Margin = new Thickness(0, 0, 12, 12),
                        Cursor = Cursors.Hand
                    };
                    var tileIcon = new Border
                    {
                        Width = 44, Height = 44, Background = new SolidColorBrush(Color.FromRgb(0xDC, 0xE6, 0xF1)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0x7F, 0x9D, 0xB9)), BorderThickness = new Thickness(1),
                        Child = new TextBlock { Text = dept.Icon, FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                    };
                    DockPanel.SetDock(tileIcon, Dock.Right);
                    var tileArrow = new TextBlock { Text = "⬅", FontSize = 14, FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)),
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8,0,0,0) };
                    DockPanel.SetDock(tileArrow, Dock.Left);
                    var tileInfo = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
                    tileInfo.Children.Add(new TextBlock { Text = dept.Title, FontSize = 13.5, FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)) });
                    tileInfo.Children.Add(new TextBlock { Text = $"عدد الشاشات: {count} شاشات فرعية", FontSize = 11,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), Margin = new Thickness(0, 2, 0, 0) });
                    var tilePanel = new DockPanel();
                    tilePanel.Children.Add(tileIcon);
                    tilePanel.Children.Add(tileArrow);
                    tilePanel.Children.Add(tileInfo);
                    tile.Child = tilePanel;
                    var deptId = dept.Id;
                    tile.MouseLeftButtonUp += (_, _) => ((MainWindow)Window.GetWindow(this))?.OpenDeptDashboardPublic(deptId);
                    tile.MouseEnter += (_, _) => tile.Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xEE, 0xF7));
                    tile.MouseLeave += (_, _) => tile.Background = Brushes.White;
                    tiles.Children.Add(tile);
                }
                DeptArea.Children.Add(tiles);
            }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Dashboard"); }
    }

    private UIElement BuildDeptScreenGrid(string deptId)
    {
        var dept = ScreenCatalog.Departments.FirstOrDefault(d => d.Id == deptId);
        var color = (Color)ColorConverter.ConvertFromString(dept?.Color ?? "#14532D");
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var deptHead = new StackPanel { Orientation = Orientation.Horizontal };
        deptHead.Children.Add(new TextBlock { Text = dept.Icon, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        deptHead.Children.Add(new TextBlock { Text = "  " + dept.Title, FontSize = 14.5, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new Border
        {
            BorderThickness = new Thickness(5, 0, 0, 0),
            BorderBrush = new SolidColorBrush(color),
            Background = Brushes.White,
            Padding = new Thickness(12, 10, 12, 10),
            Child = deptHead
        });

        var grid = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var s in ScreenCatalog.All.Where(x => x.Group == dept.Title && x.Code != "dashboard"))
        {
            var card = new Border
            {
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 9, 12, 9),
                Width = 230,
                Margin = new Thickness(0, 0, 10, 10),
                Cursor = Cursors.Hand
            };
            var cardIcon = new TextBlock { Text = s.Icon + "  ", FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(cardIcon, Dock.Right);
            var cardOpen = new TextBlock { Text = "فتح ⬅", FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x24, 0x6A)), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(cardOpen, Dock.Left);
            var cardTitle = new TextBlock { Text = s.Title, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B)), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis };
            var cardPanel = new DockPanel();
            cardPanel.Children.Add(cardIcon);
            cardPanel.Children.Add(cardOpen);
            cardPanel.Children.Add(cardTitle);
            card.Child = cardPanel;
            var code = s.Code;
            card.MouseLeftButtonUp += (_, _) => ((MainWindow)Window.GetWindow(this))?.OpenScreen(code);
            card.MouseEnter += (_, _) => { card.BorderBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x53, 0x2D)); card.Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)); };
            card.MouseLeave += (_, _) => { card.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)); card.Background = Brushes.White; };
            grid.Children.Add(card);
        }
        head.Children.Add(grid);
        return head;
    }

    /// <summary>§بطاقة تنبيه — شريط ملوّن بنص التحذير.</summary>
    private UIElement AlertCard(string text, string bg, string border, string fg)
    {
        var b = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg)),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(border)),
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 5)
        };
        b.Child = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg))
        };
        return b;
    }

    private UIElement TaskCounter(string label, int count, string icon, string bg, string border, string fg)
    {
        var b = new Border
        {
            Background = (Brush)new BrushConverter().ConvertFromString(bg),
            BorderBrush = (Brush)new BrushConverter().ConvertFromString(border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8),
            Width = 220,
            Margin = new Thickness(0, 0, 8, 8)
        };
        var taskIcon = new TextBlock { Text = icon, FontSize = 22, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(taskIcon, Dock.Left);
        var taskInfo = new StackPanel();
        taskInfo.Children.Add(new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap });
        taskInfo.Children.Add(new TextBlock { Text = count.ToString(), FontSize = 18, FontWeight = FontWeights.Bold,
            Foreground = (Brush)new BrushConverter().ConvertFromString(fg) });
        var taskPanel = new DockPanel();
        taskPanel.Children.Add(taskIcon);
        taskPanel.Children.Add(taskInfo);
        b.Child = taskPanel;
        return b;
    }

    private void ApprovePlan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PendingGrid.SelectedItem == null) { AppContainer.Get<DialogService>().Error("اختر خطة من الجدول."); return; }
            var idProp = PendingGrid.SelectedItem.GetType().GetProperty("Id");
            int id = (int)idProp.GetValue(PendingGrid.SelectedItem);
            if (!AppContainer.Get<DialogService>().Confirm("هل تريد اعتماد خطة الإنتاج هذه رسمياً ونقلها فوراً للتنفيذ؟")) return;

            using var scope = AppContainer.NewScope();
            var svc = (IPlanningService)scope.ServiceProvider.GetService(typeof(IPlanningService));
            var r = svc.ApprovePlan(id);
            if (!r.Ok) AppContainer.Get<DialogService>().Error(r.Message);
            else { AppContainer.Get<DialogService>().Info(r.Message); Load(); }
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Dashboard.ApprovePlan"); }
    }

    private void OpenReceipt_Click(object sender, RoutedEventArgs e)
    {
        ((MainWindow)Window.GetWindow(this))?.OpenScreen("finishedgoods");
    }

    /// <summary>§النقر المزدوج على خطة بانتظار الاعتماد يفتحها في شاشة التخطيط (لا شاشة التسليم).</summary>
    private void OpenPlan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (PendingGrid.SelectedItem == null) return;
            var idProp = PendingGrid.SelectedItem.GetType().GetProperty("Id");
            if (idProp?.GetValue(PendingGrid.SelectedItem) is int id)
                ((MainWindow)Window.GetWindow(this))?.OpenPlanById(id);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Dashboard.OpenPlan"); }
    }
}
