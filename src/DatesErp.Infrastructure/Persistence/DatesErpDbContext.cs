using DatesErp.Core.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DatesErp.Infrastructure.Persistence;

/// <summary>
/// سياق قاعدة البيانات المركزي — §3 كل البيانات مركزية في SQL Server على السيرفر.
/// §5 التزامن تفاؤلي عبر rowversion، §26 تدقيق تلقائي داخل نفس المعاملة.
/// </summary>
public class DatesErpDbContext : DbContext
{
    private readonly TimeProvider _clock;
    public DatesErpDbContext(DbContextOptions<DatesErpDbContext> options, TimeProvider clock = null) : base(options)
    {
        _clock = clock ?? TimeProvider.System;
    }

    // SQL Server هو مرجع الوقت المشترك، وليس ساعة جهاز الموظف. SQLite يستخدم ساعة المضيف.
    public DateTime BusinessNow => IsSqlServer
        ? Database.SqlQuery<DateTime>($"SELECT GETDATE() AS [Value]").Single()
        : _clock.GetLocalNow().DateTime;

    public bool IsSqlServer => Database.IsSqlServer();

    // ── البيانات الأساسية ──
    public DbSet<CompanyInfo> CompanyInfos => Set<CompanyInfo>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<ItemGroup> ItemGroups => Set<ItemGroup>();
    public DbSet<ItemCategory> ItemCategories => Set<ItemCategory>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<PackagingType> PackagingTypes => Set<PackagingType>();
    public DbSet<AuxiliaryMaterial> AuxiliaryMaterials => Set<AuxiliaryMaterial>();
    public DbSet<ConsumptionFormula> ConsumptionFormulas => Set<ConsumptionFormula>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<ProductShiftCapacity> ProductShiftCapacities => Set<ProductShiftCapacity>();
    public DbSet<ProductionLine> ProductionLines => Set<ProductionLine>();
    public DbSet<Employee> Employees => Set<Employee>();

    // ── الاستلام والدفعات ──
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<ShipmentItem> ShipmentItems => Set<ShipmentItem>();
    /// <summary>§B107 — أجزاء بند الاستلام بدرجات إصابة مختلفة (5/7/10 أيام) بلا صنف جديد.</summary>
    public DbSet<ShipmentItemTreatmentPart> ShipmentItemTreatmentParts => Set<ShipmentItemTreatmentPart>();
    public DbSet<Lot> Lots => Set<Lot>();

    // ── المعالجة والتعقيم ──
    public DbSet<RawTreatment> RawTreatments => Set<RawTreatment>();
    public DbSet<TreatmentType> TreatmentTypes => Set<TreatmentType>();

    // ── التخطيط والإنتاج ──
    public DbSet<ProductionPlan> ProductionPlans => Set<ProductionPlan>();
    public DbSet<ProductionPlanItem> ProductionPlanItems => Set<ProductionPlanItem>();
    public DbSet<ProductionPlanContext> ProductionPlanContexts => Set<ProductionPlanContext>();
    public DbSet<ProductionPlanScopeCustomer> ProductionPlanScopeCustomers => Set<ProductionPlanScopeCustomer>();
    public DbSet<ProductionPlanSelectedShift> ProductionPlanSelectedShifts => Set<ProductionPlanSelectedShift>();
    public DbSet<ProductionPlanWorkSlot> ProductionPlanWorkSlots => Set<ProductionPlanWorkSlot>();
    public DbSet<ProductionPlanRevision> ProductionPlanRevisions => Set<ProductionPlanRevision>();
    public DbSet<ProductRawPlanningRule> ProductRawPlanningRules => Set<ProductRawPlanningRule>();
    public DbSet<ProductionOrder> ProductionOrders => Set<ProductionOrder>();
    public DbSet<ProductionOrderItem> ProductionOrderItems => Set<ProductionOrderItem>();
    public DbSet<ProductionOrderMaterial> ProductionOrderMaterials => Set<ProductionOrderMaterial>();
    public DbSet<ProductionExecution> ProductionExecutions => Set<ProductionExecution>();
    public DbSet<ExecutionDowntime> ExecutionDowntimes => Set<ExecutionDowntime>();
    public DbSet<PlanClosing> PlanClosings => Set<PlanClosing>();
    public DbSet<PlanClosingItem> PlanClosingItems => Set<PlanClosingItem>();

    // ── الجودة ──
    public DbSet<QualityCheck> QualityChecks => Set<QualityCheck>();
    public DbSet<QualityCheckItem> QualityCheckItems => Set<QualityCheckItem>();
    public DbSet<QualityCorrection> QualityCorrections => Set<QualityCorrection>();
    public DbSet<ProductionDelivery> ProductionDeliveries => Set<ProductionDelivery>();
    public DbSet<ProductionDeliveryItem> ProductionDeliveryItems => Set<ProductionDeliveryItem>();
    public DbSet<ByProduct> ByProducts => Set<ByProduct>();
    public DbSet<QualityByProductRecord> QualityByProductRecords => Set<QualityByProductRecord>();
    public DbSet<QualityStandard> QualityStandards => Set<QualityStandard>();
    public DbSet<QualityStandardRecord> QualityStandardRecords => Set<QualityStandardRecord>();
    public DbSet<PlanClosingByProduct> PlanClosingByProducts => Set<PlanClosingByProduct>();
    public DbSet<ExecutionByProduct> ExecutionByProducts => Set<ExecutionByProduct>();
    // §الفحص الديناميكي: أنواع النتائج قابلة للتعريف + ملفات الأصناف + النتائج الفعلية + تحويلات الوحدات
    public DbSet<InspectionResultType> InspectionResultTypes => Set<InspectionResultType>();
    public DbSet<ItemInspectionProfile> ItemInspectionProfiles => Set<ItemInspectionProfile>();
    public DbSet<InspectionResult> InspectionResults => Set<InspectionResult>();
    public DbSet<UnitConversion> UnitConversions => Set<UnitConversion>();
    public DbSet<PermissionResource> PermissionResources => Set<PermissionResource>();
    public DbSet<PermissionOperation> PermissionOperations => Set<PermissionOperation>();
    public DbSet<RoleResourcePermission> RoleResourcePermissions => Set<RoleResourcePermission>();
    public DbSet<UserResourcePermission> UserResourcePermissions => Set<UserResourcePermission>();
    public DbSet<PermissionAuditLog> PermissionAuditLogs => Set<PermissionAuditLog>();
    public DbSet<CartonCountDoc> CartonCountDocs => Set<CartonCountDoc>();
    public DbSet<CartonCountItem> CartonCountItems => Set<CartonCountItem>();
    public DbSet<CartonSaleDoc> CartonSaleDocs => Set<CartonSaleDoc>();
    public DbSet<CartonSaleItem> CartonSaleItems => Set<CartonSaleItem>();
    public DbSet<AuxGroup> AuxGroups => Set<AuxGroup>();
    public DbSet<AuxCustomerSpec> AuxCustomerSpecs => Set<AuxCustomerSpec>();
    // §1.50.63 — الأصناف المساعدة الجديد
    public DbSet<AuxiliaryProductConfig> AuxiliaryProductConfigs => Set<AuxiliaryProductConfig>();
    public DbSet<ProductAuxiliaryRequirement> ProductAuxiliaryRequirements => Set<ProductAuxiliaryRequirement>();
    public DbSet<AuxiliaryIssueTransaction> AuxiliaryIssueTransactions => Set<AuxiliaryIssueTransaction>();
    public DbSet<AuxiliaryReturnTransaction> AuxiliaryReturnTransactions => Set<AuxiliaryReturnTransaction>();
    public DbSet<Delegation> Delegations => Set<Delegation>();
    public DbSet<UnitOfMeasure> UnitsOfMeasure => Set<UnitOfMeasure>();

    // ── التسليم ──
    public DbSet<FinishedGoodsReceipt> FinishedGoodsReceipts => Set<FinishedGoodsReceipt>();
    public DbSet<FinishedGoodsReceiptItem> FinishedGoodsReceiptItems => Set<FinishedGoodsReceiptItem>();
    public DbSet<CustomerDelivery> CustomerDeliveries => Set<CustomerDelivery>();
    public DbSet<CustomerDeliveryItem> CustomerDeliveryItems => Set<CustomerDeliveryItem>();

    // ── المخزون ──
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<InventoryTransaction> InventoryTransactions => Set<InventoryTransaction>();

    // ── الأمان والإدارة ──
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ClientMachine> ClientMachines => Set<ClientMachine>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<DbVersion> DbVersions => Set<DbVersion>();
    public DbSet<NumberingScheme> NumberingSchemes => Set<NumberingScheme>();

    // ── طبقة التوجيه بالمهام (§3) ──
    public DbSet<WorkflowTask> WorkflowTasks => Set<WorkflowTask>();
    public DbSet<WorkflowTaskHistory> WorkflowTaskHistories => Set<WorkflowTaskHistory>();

    /// <summary>
    /// §B110 — دقة موحدة 18,4 لكل حقول decimal على المزودين.
    /// • على SQL Server: تُخزَّن <c>decimal(18,4)</c> حقيقية — الفرز والمجاميع والمقارنات
    ///   في جانب القاعدة عددية صحيحة (قبل هذا الإصلاح كانت تُخزن نصاً على كل المزودين،
    ///   فيصبح <c>'10' &lt; '9'</c> وتفقد الفهارس معناها).
    /// • على SQLite فقط: المحوّل النصي (أدناه) — التوافق مع القواعد المحلية القائمة.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);
        configurationBuilder.Properties<decimal?>().HavePrecision(18, 4);

        if (!Database.IsSqlServer())
        {
            configurationBuilder.Properties<decimal>().HaveConversion<DecimalToStringConverter>();
            configurationBuilder.Properties<decimal?>().HaveConversion<DecimalToStringConverter>();
        }
    }

    /// <summary>محوّل decimal ↔ نص بدقة 18,4 — ثابت بين SQLite وSQL Server.</summary>
    private sealed class DecimalToStringConverter : ValueConverter<decimal, string>
    {
        public DecimalToStringConverter()
            : base(v => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
                   v => decimal.Parse(v, System.Globalization.CultureInfo.InvariantCulture)) { }
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // §5/§45 — التزامن التفاؤلي: كل الكيانات تحمل نسخة صف (انتقلت إلى BaseEntity)
        foreach (var et in b.Model.GetEntityTypes())
        {
            {
                var rv = et.FindProperty(nameof(Core.Common.BaseEntity.RowVersion));
                if (rv != null)
                {
                    // rowversion أصلي في SQL Server: رمز التزامن + توليد تلقائي عند الإضافة والتحديث
                    rv.IsConcurrencyToken = true;
                    if (Database.IsSqlServer())
                        rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAddOrUpdate;
                    else
                    {
                        // Raw/import DbContext instances have no audit interceptor.
                        // SQLite must still create a non-NULL token on INSERT;
                        // normal service writes/updates refresh it in the interceptor.
                        rv.SetDefaultValueSql("randomblob(16)");
                        rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAdd;
                    }
                }
            }
        }

        // ── طبقة التوجيه بالمهام (§3) ──
        // CorrelationKey فريد على مستوى القاعدة: هو الضمان الحقيقي لمنع تكرار المهام،
        // لا الفحص في الكود — فجهازان متزامنان لا يستطيعان توليد مهمتين لنفس الحدث.
        b.Entity<WorkflowTask>().HasIndex(t => t.CorrelationKey).IsUnique();
        b.Entity<WorkflowTask>().HasIndex(t => t.TaskNumber).IsUnique();
        // فهرس الاستطلاع الدوري (Q5): استعلام العدّادات كل 60 ثانية يمر من هنا
        b.Entity<WorkflowTask>().HasIndex(t => new { t.RequiredCapability, t.State });
        b.Entity<WorkflowTask>().HasIndex(t => new { t.AssignedUserId, t.State });
        b.Entity<WorkflowTask>().HasIndex(t => new { t.DocumentType, t.DocumentId });
        b.Entity<WorkflowTask>().HasIndex(t => t.BusinessDate);
        b.Entity<WorkflowTask>().Ignore(t => t.IsOverdue); // محسوبة، لا تُخزَّن
        b.Entity<WorkflowTask>()
            .HasMany<WorkflowTaskHistory>().WithOne(h => h.Task).HasForeignKey(h => h.TaskId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<WorkflowTaskHistory>().HasIndex(h => h.TaskId);

        // ── المعالجة والتعقيم ──
        b.Entity<RawTreatment>().HasIndex(t => t.TreatmentNo).IsUnique();
        b.Entity<RawTreatment>().HasIndex(t => t.ReceivingItemId).IsUnique()
            .HasFilter("[ReceivingItemId] IS NOT NULL");
        b.Entity<ShipmentItem>().Property(i => i.TreatmentCompletedAt).IsConcurrencyToken();
        // فهرس «المتاح حسب تاريخ الإنتاج»: التخطيط يجمع المعالجات الجارية
        // بـ ExpectedReadyAt <= D لكل دفعة — هذا مسار الاستعلام الساخن.
        b.Entity<RawTreatment>().HasIndex(t => new { t.LotId, t.Status });
        b.Entity<RawTreatment>().HasIndex(t => new { t.Status, t.ExpectedReadyAt });
        b.Entity<TreatmentType>().HasIndex(t => t.TypeCode).IsUnique();
        // محسوبة، لا تُخزَّن — تصريح واضح رغم أن EF يتجاهل ما لا setter له
        b.Entity<RawTreatment>().Ignore(t => t.RemainingQtyKg);
        b.Entity<RawTreatment>().Ignore(t => t.IsReadyByTime);
        b.Entity<RawTreatment>().Ignore(t => t.IsOverdue);
        b.Entity<Lot>().Ignore(l => l.AvailableQtyKg);

        // ── علاقات الاستلام ──
        b.Entity<Shipment>().HasMany(s => s.Items).WithOne().HasForeignKey(i => i.ShipmentId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Shipment>().HasMany(s => s.Lots).WithOne().HasForeignKey(l => l.ShipmentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Shipment>().HasIndex(s => s.DocumentNumber).IsUnique();
        // §B107 — أجزاء درجات الإصابة داخل البند الواحد (بلا صنف جديد)
        b.Entity<ShipmentItem>().HasMany(i => i.TreatmentParts).WithOne()
            .HasForeignKey(p => p.ShipmentItemId).OnDelete(DeleteBehavior.Cascade);

        // ── التخطيط ──
        b.Entity<ProductionPlan>().HasMany(p => p.Items).WithOne().HasForeignKey(i => i.PlanId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProductionPlan>().HasIndex(p => p.DocumentNumber).IsUnique();
        b.Entity<ProductionPlanItem>().HasIndex(i => new { i.ScheduledDate, i.SuggestedShiftId, i.SuggestedLineId });
        b.Entity<ProductionPlanItem>().HasIndex(i => i.WorkSlotId);
        b.Entity<ProductionPlanItem>().Property(i => i.MoldWeightKg).HasPrecision(18, 4);
        // A missing context denotes a historical plan. No backfill guesses its customer/shift scope.
        b.Entity<ProductionPlanContext>().HasIndex(c => c.PlanId).IsUnique();
        b.Entity<ProductionPlanScopeCustomer>().HasIndex(c => new { c.PlanId, c.CustomerId }).IsUnique();
        b.Entity<ProductionPlanSelectedShift>().HasIndex(s => new { s.PlanId, s.ShiftId }).IsUnique();
        b.Entity<ProductionPlanSelectedShift>().HasIndex(s => new { s.PlanId, s.SequenceNo }).IsUnique();
        b.Entity<ProductionPlanWorkSlot>().HasIndex(s => new { s.PlanId, s.WorkDate, s.ShiftId }).IsUnique();
        b.Entity<ProductionPlanWorkSlot>().HasIndex(s => new { s.PlanId, s.SequenceNo }).IsUnique();
        b.Entity<ProductionPlanRevision>().HasIndex(r => new { r.PlanId, r.RevisionNo }).IsUnique();
        b.Entity<ProductRawPlanningRule>().HasIndex(r => new { r.ProductId, r.RawProductId, r.PackagingTypeId }).IsUnique();
        b.Entity<ProductRawPlanningRule>().Property(r => r.RawKgPerCarton).HasPrecision(18, 4);
        b.Entity<ProductRawPlanningRule>().Property(r => r.ApprovalReason).HasMaxLength(500);
        b.Entity<ProductionPlanItem>().HasOne<ProductionPlanWorkSlot>().WithMany()
            .HasForeignKey(i => i.WorkSlotId).OnDelete(DeleteBehavior.Restrict);

        // ── أوامر الإنتاج ──
        b.Entity<ProductionOrder>().HasMany(o => o.Items).WithOne().HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProductionOrder>().HasMany(o => o.Materials).WithOne().HasForeignKey(m => m.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProductionOrder>().HasIndex(o => o.DocumentNumber).IsUnique();

        // ── التنفيذ والإقفال اليومي ──
        b.Entity<ProductionExecution>().HasMany(x => x.Downtimes).WithOne().HasForeignKey(d => d.ExecutionId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProductionExecution>().HasIndex(x => x.DocumentNumber).IsUnique();
        b.Entity<PlanClosing>().HasMany(c => c.Items).WithOne().HasForeignKey(i => i.ClosingId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PlanClosing>().HasMany(c => c.Downtimes).WithOne().HasForeignKey(d => d.ClosingId).OnDelete(DeleteBehavior.Cascade);
        // §عطل مُصلح: PlanClosingItem.ByProducts كانت تُحفظ بلا إعداد علاقة، فيبقى ClosingId = 0
        // وتُكتب الصفوف يتيمة لا يربطها شيء بأي إقفال. الاسم «ClosingId» تاريخي ومعناه
        // «بند الإقفال» — أُبقي الاسم حتى لا ينكسر عمود في قواعد المستخدمين القائمة.
        b.Entity<PlanClosingItem>().HasMany(i => i.ByProducts).WithOne().HasForeignKey(x => x.ClosingId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PlanClosing>().HasIndex(c => c.DocumentNumber).IsUnique();

        // ── الجودة ──
        b.Entity<QualityCheck>().HasMany(q => q.Items).WithOne().HasForeignKey(i => i.CheckId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<QualityCheck>().HasIndex(q => q.DocumentNumber).IsUnique();
        // §الفحص الديناميكي: نتائج الفحص تُحذف مع استمارتها — لا نتائج يتيمة
        b.Entity<QualityCheck>().HasMany(q => q.Results).WithOne().HasForeignKey(r => r.CheckId).OnDelete(DeleteBehavior.Cascade);
        // §مخرجات الجلسة تُحذف معها — لا سجلات يتيمة
        b.Entity<ProductionExecution>().HasMany(e => e.ByProducts).WithOne().HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<InspectionResultType>().HasIndex(t => t.Code);
        b.Entity<InspectionResult>().HasIndex(r => r.CheckId);
        b.Entity<InspectionResult>().HasIndex(r => r.OrderItemId);
        b.Entity<QualityCheckItem>().HasIndex(i => new { i.CheckId, i.LotId, i.ProductId, i.PackagingTypeId });
        b.Entity<QualityCheckItem>().HasIndex(i => i.OrderItemId);
        b.Entity<ItemInspectionProfile>().HasIndex(p => p.ProductId);

        // ── التسليم ──
        // §B102 — إصلاح علة جذرية (كانت في خطي TASNEE1212 معاً): علاقة أمر تسليم الإنتاج
        // ببنوده لم تُعرَّف قط — EF أنشأ مفتاحاً ظلماً (ProductionDeliveryId) وبقي DeliveryId = 0،
        // فكانت البنود تُحفظ يتيمة ويظهر الأمر «محفوظاً» بينما هو فارغ. الآن FK صريح.
        b.Entity<ProductionDelivery>().HasMany(d => d.Items).WithOne().HasForeignKey(i => i.DeliveryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<ProductionDelivery>().HasIndex(d => d.DocumentNumber).IsUnique();
        b.Entity<ProductionDeliveryItem>().HasIndex(i => i.DeliveryId);
        b.Entity<FinishedGoodsReceipt>().HasMany(r => r.Items).WithOne().HasForeignKey(i => i.ReceiptId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<FinishedGoodsReceipt>().HasIndex(r => r.DocumentNumber).IsUnique();
        // RCV is a separate numbered document from FGR; voucher numbers must not
        // collide, or historical ledger reversal cannot identify its owner.
        // Draft receipts have no number; SQL Server index keys cannot be nvarchar(max).
        b.Entity<FinishedGoodsReceipt>().Property(r => r.ReceiptNumber)
            .HasMaxLength(FinishedGoodsReceipt.ReceiptNumberMaxLength).IsRequired(false);
        b.Entity<FinishedGoodsReceipt>().HasIndex(r => r.ReceiptNumber).IsUnique()
            .HasFilter("[ReceiptNumber] IS NOT NULL");
        b.Entity<CustomerDelivery>().HasMany(d => d.Items).WithOne().HasForeignKey(i => i.DeliveryId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<CustomerDelivery>().HasIndex(d => d.DocumentNumber).IsUnique();

        // ── المخزون §9 — §1.50.66 سلامة المخزون: PackagingTypeId جزء من مفتاح الرصيد
        b.Entity<StockBalance>().HasIndex(x => new { x.WarehouseId, x.ProductId, x.MaterialId, x.LotId, x.CustomerId, x.PackagingTypeId })
            .IsUnique()
            .HasFilter(null); // لا فلتر — المفتاح الكامل يمنع التكرار حتى مع NULLs عبر معالجة في التطبيق، والقيد الفريد يطبق على SQL Server
        // فهرس إضافي للبحث السريع بدون تفرد
        b.Entity<StockBalance>().HasIndex(x => new { x.WarehouseId, x.ProductId, x.CustomerId, x.PackagingTypeId });
        b.Entity<InventoryTransaction>().HasIndex(x => x.TxnNumber).IsUnique();
        b.Entity<InventoryTransaction>().HasIndex(x => new { x.ReferenceDocType, x.ReferenceDocNumber });
        b.Entity<InventoryTransaction>().HasIndex(x => new { x.WarehouseId, x.ProductId, x.CustomerId, x.PackagingTypeId });

        // ── الأمان ──
        b.Entity<AppUser>().HasIndex(u => u.UserName).IsUnique();
        b.Entity<AppUser>().HasMany(u => u.UserRoles).WithOne().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Role>().HasIndex(r => r.RoleCode).IsUnique();
        b.Entity<Role>().HasMany(r => r.Permissions).WithOne().HasForeignKey(p => p.RoleId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<RolePermission>().HasIndex(p => new { p.RoleId, p.ModuleCode }).IsUnique();
        b.Entity<AuditLog>().HasIndex(a => a.ActionDate);
        b.Entity<ClientMachine>().HasIndex(m => m.MachineId).IsUnique();

        // ── الإعدادات ──
        b.Entity<SystemSetting>().HasIndex(s => s.SettingKey).IsUnique();
        b.Entity<NumberingScheme>().HasIndex(n => n.SchemeCode).IsUnique();
        b.Entity<Product>().HasIndex(p => p.ProductCode).IsUnique();
        b.Entity<Customer>().HasIndex(c => c.CustomerCode).IsUnique();
        b.Entity<AuxiliaryMaterial>().HasIndex(m => m.MaterialCode).IsUnique();
        b.Entity<Lot>().HasIndex(l => l.LotCode).IsUnique();

        // جميع الأعمدة النصية والثنائية (كالشعار) اختيارية — قاعدة بيانات عملية تحتمل القيم الفارغة
        foreach (var et in b.Model.GetEntityTypes())
            foreach (var prop in et.GetProperties())
                if ((prop.ClrType == typeof(string) || prop.ClrType == typeof(byte[])) && prop.Name != "RowVersion")
                    prop.IsNullable = true;
    }
}
