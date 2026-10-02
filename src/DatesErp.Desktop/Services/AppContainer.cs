using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure;
using DatesErp.Infrastructure.Connection;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Services;

/// <summary>حاوية الخدمات المركزية لواجهة سطح المكتب.</summary>
public static class AppContainer
{
    public static ServiceProvider Provider { get; private set; }

    /// <summary>بناء الحاوية حسب إعداد الاتصال المحفوظ (§12): SQL Server مركزي أو فشل واضح.</summary>
    public static void Build(Action<DbContextOptionsBuilder> optionsOverride = null)
    {
        var cfg = AppConfig.Load();
        var services = new ServiceCollection();

        services.AddDatesErpInfrastructure(options =>
        {
            if (optionsOverride != null) optionsOverride(options);
            else if (cfg != null && cfg.AuthMode != "Local") options.UseSqlServer(cfg.BuildSqlServerConnectionString());
            else options.UseSqlite($"Data Source={AppConfig.LocalDatabasePath}");
        });

        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IReceivingService, ReceivingService>();
        services.AddScoped<IPlanningService, PlanningService>();
        services.AddScoped<SequencedPlanningService>();
        services.AddScoped<IPlanClosureService, PlanClosureService>();
        services.AddScoped<IProductionOrderService, ProductionOrderService>();
        services.AddScoped<IExecutionService, ExecutionService>();
        services.AddScoped<IQualityService, QualityService>();
        // §الفحص الديناميكي: أنواع نتائج ووحدات قابلة للتعريف من الإعدادات
        services.AddScoped<IInspectionService, InspectionService>();
        services.AddScoped<IFinishedGoodsService, FinishedGoodsService>();
        services.AddScoped<IProductionDeliveryService, ProductionDeliveryService>();
        services.AddScoped<ICustomerDeliveryService, CustomerDeliveryService>();
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddScoped<IAdminService, AdminService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<SupplierMovementService>();
        services.AddScoped<PermissionService>();
        services.AddScoped<CartonService>();
        services.AddScoped<IBackupService, BackupService>();
        services.AddScoped<ISystemSettingsService, SystemSettingsService>(); // §44
        services.AddScoped<IThemeSettingsService, ThemeSettingsService>(); // مدير الثيم المركزي
        services.AddScoped<IInventoryIntegrityService, InventoryIntegrityService>(); // §45
        services.AddScoped<MasterDataService>();
        services.AddScoped<ICapacityService, CapacityService>();
        services.AddScoped<IShiftService, ShiftService>();
        services.AddScoped<IPlanProgressService, PlanProgressService>();
        services.AddScoped<ITraceabilityService, TraceabilityService>();
        // §3 — محرك التوجيه بالمهام
        services.AddScoped<IWorkflowTaskService, WorkflowTaskService>();
        services.AddScoped<ITaskCenterService, TaskCenterService>();            // §B102 (سحب B97) — مركز المهام
        services.AddScoped<IDayRunService, DayRunService>();                    // §B102 (سحب B98) — تشغيل اليوم
        services.AddScoped<ICustomerAvailabilityService, CustomerAvailabilityService>(); // §B102 (سحب B100) — متاح العملاء
        services.AddScoped<DeliveryPickService>(); // §أمر شاشة التسليم — قائمة الالتقاط والتوزيع FIFO (§1/§2/§5)
        services.AddScoped<CorrectionService>();   // §C1 — معالج تصحيح السلسلة (فك مترابط موثق)
        services.AddScoped<IRawTreatmentService, RawTreatmentService>();
        services.AddScoped<AuxiliaryManagementService>(); // §1.50.63 — الأصناف المساعدة
        services.AddScoped<MachineRegistry>();
        services.AddSingleton<ConnectionTester>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<ExportPrintService>();

        Provider = services.BuildServiceProvider();
    }

    public static T Get<T>() => Provider.GetRequiredService<T>();
    public static IServiceScope NewScope() => Provider.CreateScope();
}
