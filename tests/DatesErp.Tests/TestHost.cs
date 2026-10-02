using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Session;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Tests;

/// <summary>مضيف اختبار: قاعدة SQLite معزولة + كل الخدمات الحقيقية.</summary>
public class TestHost : IDisposable
{
    private readonly FixtureClock? _fixtureClock;
    private sealed class FixtureClock : TimeProvider
    {
        internal DateTimeOffset? FixedUtc;
        public override DateTimeOffset GetUtcNow() => FixedUtc ?? DateTimeOffset.UtcNow;
    }
    /// <summary>Explicit per-fixture business date. Never changes the OS clock or SQL Server time.</summary>
    public void SetBusinessDate(string date)
    {
        if (_fixtureClock == null) throw new InvalidOperationException("This host has an externally controlled clock; advance that clock explicitly.");
        if (!DatesErp.Core.Common.UiFormat.TryParseDate(date, out var day)) throw new ArgumentException("Invalid fixture date: " + date);
        var local = DateTime.SpecifyKind(day.Date.AddHours(12), DateTimeKind.Unspecified);
        _fixtureClock.FixedUtc = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }
    public ServiceProvider Services { get; }
    public SqliteConnection? Connection { get; }

    public TestHost(TimeProvider? clock = null, string? sqlConnection = null)
    {
        if (clock == null && sqlConnection == null) clock = _fixtureClock = new FixtureClock();
        Connection = sqlConnection == null ? new SqliteConnection("Data Source=:memory:") : null!;
        Connection?.Open();

        Services = new ServiceCollection()
            .AddSingleton(clock ?? TimeProvider.System)
            .AddDatesErpInfrastructure(options => { if (sqlConnection == null) options.UseSqlite(Connection!); else options.UseSqlServer(sqlConnection); })
            .AddScoped<IAuditService, AuditService>()
            .AddScoped<IAuthService, AuthService>()
            .AddScoped<IReceivingService, ReceivingService>()
            .AddScoped<IPlanningService, PlanningService>()
            .AddScoped<SequencedPlanningService>()
                .AddScoped<IPlanClosureService, PlanClosureService>()
            .AddScoped<IProductionOrderService, ProductionOrderService>()
            .AddScoped<IExecutionService, ExecutionService>()
            .AddScoped<IQualityService, QualityService>()
            .AddScoped<IInspectionService, InspectionService>()
            .AddScoped<IFinishedGoodsService, FinishedGoodsService>()
            .AddScoped<IProductionDeliveryService, ProductionDeliveryService>()
            .AddScoped<ICustomerDeliveryService, CustomerDeliveryService>()
            .AddScoped<DatesErp.Application.Services.CartonService>()
            .AddScoped<IInventoryService, InventoryService>()
            .AddScoped<IAdminService, AdminService>()
            .AddScoped<IReportService, ReportService>()
                .AddScoped<SupplierMovementService>()
            .AddScoped<IBackupService, BackupService>()
            .AddScoped<Application.Services.MasterDataService>()
            .AddScoped<ICapacityService, DatesErp.Application.Services.CapacityService>()
            .AddScoped<IShiftService, DatesErp.Application.Services.ShiftService>()
            .AddScoped<IPlanProgressService, DatesErp.Application.Services.PlanProgressService>()
            .AddScoped<ITraceabilityService, DatesErp.Application.Services.TraceabilityService>()
            .AddScoped<IWorkflowTaskService, DatesErp.Application.Services.WorkflowTaskService>()
            .AddScoped<IRawTreatmentService, DatesErp.Application.Services.RawTreatmentService>()
            .AddScoped<ITaskCenterService, DatesErp.Application.Services.TaskCenterService>()
            .AddScoped<IDayRunService, DatesErp.Application.Services.DayRunService>()
            .AddScoped<ICustomerAvailabilityService, DatesErp.Application.Services.CustomerAvailabilityService>()
            .AddScoped<DatesErp.Application.Services.DeliveryPickService>()
            .AddScoped<DatesErp.Application.Services.CorrectionService>()
            .AddScoped<DatesErp.Application.Services.StockBalanceIntegrityService>()
            .AddScoped<DatesErp.Application.Services.AuxiliaryManagementService>()
            .AddScoped<PermissionService>()
            .AddScoped<MachineRegistry>()
            .BuildServiceProvider();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        db.Database.EnsureCreated();
        DbSeeder.Seed(db);
    }

    /// <summary>تسجيل دخول كـ مدير النظام (صلاحيات كاملة).</summary>
    public SessionContext LoginAsAdmin() => LoginAs("admin");

    /// <summary>§B102 (سحب B97) — تسجيل دخول كأي مستخدم مُبذّر (admin/production/warehouse/quality).</summary>
    public SessionContext LoginAs(string userName)
    {
        var session = Services.GetRequiredService<DatesErp.Infrastructure.Session.SessionContext>();
        var auth = Services.GetRequiredService<IAuthService>();
        var r = auth.Login(userName, DbSeeder.InitialAdminPassword);
        if (!r.Success) throw new InvalidOperationException(r.Message);
        return session;
    }

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public void Dispose()
    {
        Services.Dispose();
        Connection?.Dispose();
    }
}
