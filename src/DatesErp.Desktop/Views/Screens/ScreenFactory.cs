using System.Windows;
using System.Windows.Controls;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>مصنع الشاشات: يعيد عنصر الواجهة المناسب لكل كود شاشة داخل الإطار الكلاسيكي الموحّد.</summary>
public static class ScreenFactory
{
    private const string Company = "الشركة اليمنية لتعبئة وتصنيع التمور";

    private static UIElement MyTasksScreen()
    {
        var v = new MyTasksView();
        var c = new ErpChrome { TitleText = "[MAIN] - [نظام إدارة وتصنيع التمور - مهامي] - (" + Company + ")" };
        v.AttachChrome(c);
        return c;
    }

    private static UIElement ShiftsScreen()
    {
        var v = new ShiftsView();
        var c = new ErpChrome { TitleText = "[MAIN] - [نظام إدارة وتصنيع التمور - الورديات (الوقت المتاح فقط)] - (" + Company + ")" };
        v.AttachChrome(c);
        return c;
    }

    private static UIElement OrdersScreen()
    {
        var v = new OrdersView();
        var c = new ErpChrome { TitleText = "[MAIN] - [نظام إدارة وتصنيع التمور - أوامر الإنتاج] - (" + Company + ")" };
        v.AttachChrome(c);
        return c;
    }

    private static UIElement QualityScreen()
    {
        var v = new QualityView();
        var c = new ErpChrome { TitleText = "[MAIN] - [نظام إدارة وتصنيع التمور - استمارة فحص وتأكيد جودة التمور] - (" + Company + ")" };
        v.AttachChrome(c);
        return c;
    }

    private static UIElement FinishedGoodsScreen()
    {
        var v = new FinishedGoodsView();
        var c = new ErpChrome { TitleText = "[MAIN] - [نظام إدارة وتصنيع التمور - أمر تسليم الإنتاج التام] - (" + Company + ")" };
        v.AttachChrome(c);
        return c;
    }

    private static UIElement ProdDeliveryScreen()
    {
        var v = new ProductionDeliveryView();
        var c = new ErpChrome { TitleText = "[MAIN] - [نظام إدارة وتصنيع التمور - أوامر تسليم الإنتاج] - (" + Company + ")" };
        v.AttachChrome(c);
        return c;
    }

    private static UIElement FGReceiveScreen()
    {
        var v = new FGReceiveView();
        var c = new ErpChrome { TitleText = "[MAIN] - [نظام إدارة وتصنيع التمور - أوامر استلام الإنتاج] - (" + Company + ")" };
        v.AttachChrome(c);
        return c;
    }

    private static UIElement DeliveryScreen()
    {
        var v = new DeliveryView();
        var c = new ErpChrome { TitleText = "[MAIN] - [نظام إدارة وتصنيع التمور - إذن خروج وتسليم بضاعة للعملاء] - (" + Company + ")" };
        v.AttachChrome(c);
        return c;
    }

    private static UIElement StockTransferScreen()
    {
        return Wrap(GenericListView.ForTransfers(), "التحويلات المخزنية", "المخازن والأرصدة", "MRPINV1007");
    }

    private static UIElement ThemeSettingsScreen()
    {
        var v = new ThemeSettingsView();
        var c = new ErpChrome { TitleText = "[MAIN] - [نظام إدارة وتصنيع التمور - تخصيص النظام والمظهر] - (" + Company + ")" };
        v.AttachChrome(c);
        return c;
    }

    private static UIElement PlanningScreen()
    {
        // An existing context-less plan always opens in its original editor.
        // Every NEW plan opens the setup-first sequenced workflow instead.
        int? pending = MainWindow.PendingPlanIdToOpen;
        bool sequenced = pending == null;
        if (pending is int planId)
        {
            using var scope = AppContainer.NewScope();
            sequenced = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>()
                .ProductionPlanContexts.AsNoTracking().Any(x => x.PlanId == planId);
        }
        var c = new ErpChrome { TitleText = "[MAIN] - [نظام إدارة وتصنيع التمور - خطط الإنتاج (MPS)] - (" + Company + ")" };
        if (sequenced) new SequencedPlanningView(pending).AttachChrome(c);
        else new PlanningView().AttachChrome(c);
        return c;
    }

    /// <summary>إطار موحّد للشاشات ذاتية المحتوى (شريط أدوات أساسي: تحديث/طباعة/خروج).</summary>
    /// <summary>
    /// §إصلاح: كان زر الطباعة موصولاً بـ lambda فارغة (WithPrint((_, _) => { })) في ثماني شاشات —
    /// زر ظاهر لا يفعل شيئاً. الآن يُضاف الزر فقط إن وُجد معالج طباعة حقيقي.
    /// </summary>
    private static UIElement WrapPlain(UIElement body, string title, string module, string code,
        RoutedEventHandler print = null)
    {
        var chrome = new ErpChrome { TitleText = "[MAIN] - [نظام إدارة وتصنيع التمور - " + title + "] - (" + Company + ")" };
        chrome.SetModule(module);
        chrome.SetScreenCode(code);
        var tb = new ErpToolbar()
            .WithRefresh((_, _) => { var mw = System.Windows.Window.GetWindow(body) as MainWindow; if (mw != null) mw.ReloadCurrent(); });
        if (print != null) tb = tb.WithPrint(print);
        tb = tb.WithExit((_, _) => { var mw = System.Windows.Window.GetWindow(body) as MainWindow; mw?.OpenScreen("dashboard"); });
        chrome.SetToolbar(tb);
        chrome.SetBody(body);
        return chrome;
    }

    private static UIElement ReceivingScreen()
    {
        var v = new ReceivingView();
        var c = new ErpChrome { TitleText = "[MAIN] - [نظام إدارة وتصنيع التمور - الاستلام وسندات الاستلام] - (" + Company + ")" };
        v.AttachChrome(c);
        return c;
    }

    private static UIElement Wrap(GenericListView list, string title, string module, string screenCode)
    {
        var chrome = new ErpChrome
        {
            TitleText = $"[MAIN] - [نظام إدارة وتصنيع التمور - {title}] - ({Company})"
        };
        list.AttachChrome(chrome, module, screenCode);
        return chrome;
    }

    public static UIElement Create(string code) => code switch
    {
        "dashboard" => new DashboardView(),
        "mytasks" => MyTasksScreen(),
        "treatment" => ReceivingScreen(),

        // بيانات أساسية (قوائم عامة)
        "customers" => Wrap(GenericListView.ForCustomers(), "العملاء", "البيانات الأساسية", "MRPMAS1006"),
        "suppliers" => Wrap(GenericListView.ForSuppliers(), "الموردون", "البيانات الأساسية", "MRPMAS1007"),
        "employees" => Wrap(GenericListView.ForEmployees(), "الموظفون وأرقام الدخول", "البيانات الأساسية", "MRPMAS1008"),
        "shifts" => ShiftsScreen(),
        "warehouses" => Wrap(GenericListView.ForWarehouses(), "المخازن", "المخازن والأرصدة", "MRPINV1000"),

        // الاستلام
        "receiving" => ReceivingScreen(),
        "lots" => Wrap(GenericListView.ForLots(), "الدفعات وأرصدة الخام", "المخازن والأرصدة", "MRPINV1002"),

        // الإنتاج
        "planning" => PlanningScreen(),
        "orders" => OrdersScreen(),
        "materials" => WrapPlain(new MaterialsView(), "صرف المواد المساعدة لأوامر الإنتاج", "أوامر الإنتاج والاحتساب التلقائي للمواد", "MRPMPS1008"),
        "aux-setup" => WrapPlain(new AuxiliarySetupView(), "تهيئة الأصناف المساعدة", "الإنتاج - الأصناف المساعدة", "MRPAUX1001"),
        "product-bom" => WrapPlain(new ProductBOMView(), "مكونات الإنتاج / احتياجات الصنف التام", "الإنتاج - الأصناف المساعدة", "MRPAUX1002"),
        "aux-issue" => WrapPlain(new ProductionAuxiliaryView(), "صرف الأصناف المساعدة للإنتاج", "الإنتاج - الأصناف المساعدة", "MRPAUX1003"),
        "customer-cartons" => WrapPlain(new CustomerCartonMappingView(), "كراتين العملاء (ماركات)", "الإنتاج - الأصناف المساعدة", "MRPAUX1004"),
        "proddelivery" => ProdDeliveryScreen(),

        // الجودة
        "quality" => QualityScreen(),
        "wastage" => Wrap(GenericListView.ForWastage(), "الهالك والأصناف الثانوية", "الجودة", "MRPQC1003"),

        // المخازن والتام
        "finishedgoods" => FinishedGoodsScreen(),
        "fgreceive" => FGReceiveScreen(),
        "balances" => Wrap(GenericListView.ForBalances(), "أرصدة المخزون", "المخازن والأرصدة", "MRPINV1001"),
        "movements" => Wrap(GenericListView.ForMovements(), "حركات المخزون", "المخازن والأرصدة", "MRPINV1003"),
        "transfers" => StockTransferScreen(),

        // التسليم
        "delivery" => DeliveryScreen(),
        "avail" => WrapPlain(new CustomerAvailabilityBoardView(), "متاح العملاء", "التسليم والشحن", "MRPDLV1002"),   // §B102.1

        // التقارير
        "reports" => WrapPlain(new ReportsView(), "مركز التقارير الموحد", "التقارير", "MRPRPT1000"),
        "shipment" => WrapPlain(new ShipmentReportView(), "تقرير حركة شحنة العميل", "التقارير", "MRPRPT1011"),
        "supmove" => WrapPlain(new SupplierMovementReportView(), "تقرير حركة الموردين حسب الأصناف — تحليلي كميات", "التقارير", "MRPRPT1012"),   // §B103   // §B102 (سحب B101)   // الطباعة بزر داخلي في الشاشة

        // الإدارة
        "users" => WrapPlain(new UsersView(), "المستخدمون", "إدارة النظام", "MRPMAS1002"),
        "permissions" => WrapPlain(new PermissionsView(), "الأدوار والصلاحيات", "إدارة النظام", "MRPMAS1003"),
            "cartons" => WrapPlain(new CartonView(), "الكرتون الفارغ (عدّ/بيع)", "المخازن والاستلام", "MRPINV1004"),
        "matrix" => WrapPlain(new PermissionsView(), "مصفوفة الصلاحيات", "إدارة النظام", "MRPMAS1004"),
        "machines" => Wrap(GenericListView.ForMachines(), "الأجهزة المتصلة", "الإدارة", "MRPMAS1010"),
        "audit" => WrapPlain(new AuditFilterView(), "سجل التدقيق (فلاتر)", "الإدارة", "MRPRPT1010"),
        "backup" => WrapPlain(new BackupView(), "النسخ الاحتياطي والاستعادة", "النسخ الاحتياطي والصيانة", "MRPSYS1001"),
        "systeminfo" => WrapPlain(new SystemInfoView(), "معلومات النظام والإعدادات", "إدارة النظام", "MRPSYS1002"),
        "theme" => ThemeSettingsScreen(),
        "whvars" => WrapPlain(new WarehouseVariablesView(), "متغيرات المخازن", "إدارة النظام", "MRPSYS1003"),
        "items" => WrapPlain(new ItemsView(), "الأصناف", "إدارة النظام", "MRPMAS1001"),
        "caps" => WrapPlain(new ItemsCapacitiesView(), "طاقات الأصناف", "إدارة النظام", "MRPMAS1011"),
        "plan-closure" => WrapPlain(new PlanClosureView(), "إقفال خطة الإنتاج", "الإنتاج", "MRPMPS1020"),

        _ => new TextBlock { Text = "الشاشة غير متوفرة.", FontSize = 16, Margin = new Thickness(20) }
    };
}
