using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Views.Screens;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Provider-independent, persisted acceptance for the CURRENT separated flow:
/// plan/order -> actual execution + pending QC -> issued production delivery ->
/// issued/received FG document -> approved QC -> bounded customer delivery.
/// Recording an actual never grants warehouse permissions or stocks WFG itself.
/// </summary>
public static class ActualDeliveryScenarios
{
    public static void Run(IServiceProvider services, Action<bool, string> check, bool concurrency = false)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<DatesErpDbContext>();
        var master = sp.GetRequiredService<MasterDataService>();
        var receiving = sp.GetRequiredService<IReceivingService>();
        var planning = sp.GetRequiredService<IPlanningService>();
        var orders = sp.GetRequiredService<IProductionOrderService>();
        var actual = sp.GetRequiredService<IProductionDeliveryService>();
        var fg = sp.GetRequiredService<IFinishedGoodsService>();
        var quality = sp.GetRequiredService<IQualityService>();
        var customerDelivery = sp.GetRequiredService<ICustomerDeliveryService>();
        var reports = sp.GetRequiredService<IReportService>();
        string day = db.BusinessNow.ToString("dd/MM/yyyy");
        void Ok(OpResult result, string label) =>
            check(result.Ok, label + (result.Ok ? "" : ": " + result.Message));
        static bool Near(double a, double b) => Math.Abs(a - b) < 0.001;

        check(actual.GetActualDeliveryOrders().Count == 0, "لا إنتاج فعلي مُتاح قبل إصدار أمر من خطة معتمدة");
        var raw = master.SaveProductFull(null, "ACPT-RAW", "سكري خام للقبول", "001", "Raw", "كجم", 20, 0, 0, null);
        Ok(raw, "تعريف الخام في بطاقة الأصناف");
        var product = master.SaveProductFull(null, "ACPT-FG8", "سكري 8 كجم للقبول", "002", "Finished", "كرتون",
            8, 1, 8, new() { (1, null, 500) }, raw.Id);
        Ok(product, "تعريف التام والتحويل والطاقة");
        var secondary = master.SaveProductFull(null, "003-ACPT", "مخرج قبول ديناميكي", "003", "ByProduct", "كجم", 0, 0, 0, null);
        Ok(secondary, "تعريف المخرج الثانوي في بطاقة الأصناف");
        check(actual.GetActualByProducts().Any(b => b.Id == secondary.Id && b.Unit == "كجم"),
            "قائمة الشاشة تقرأ المخرج الثانوي من الأصناف الحالية");
        var pack = new PackagingType { PackageCode = "ACPT-8", PackageNameAr = "عبوة قبول 8 كجم", UnitWeightKg = 8 };
        db.PackagingTypes.Add(pack); db.SaveChanges();
        int fgId = db.Warehouses.Single(w => w.WarehouseCode == "WFG").Id;
        int cust = db.Customers.OrderBy(c => c.Id).First().Id;

        var shipment = receiving.SaveShipment(cust, day, day,
            new() { new() { TreatmentRequired = false, ProductId = raw.Id, QtyKg = 1000,
                PackageCount = 50, UnitWeightKg = 20 } });
        Ok(shipment, "حفظ استلام 1000 كجم خام");
        Ok(receiving.ApproveShipment(shipment.Id), "اعتماد الاستلام");
        int lotId = db.Lots.AsNoTracking().Single(l => l.ShipmentId == shipment.Id).Id;

        int CreateOrder(string tag, int cartons, double rawDrawKg, bool start = true)
        {
            var p = planning.SavePlan("خطة قبول " + tag, "Daily", day, day, 1, 1, new()
            {
                new() { SourceType = "FromReceiving", LotId = lotId, CustomerId = cust,
                    ProductId = product.Id, PackagingTypeId = pack.Id, PlannedCartons = cartons,
                    PlannedQtyKg = cartons * 8, SourceQtyKg = rawDrawKg,
                    ScheduledDate = day, SuggestedShiftId = 1, SuggestedLineId = 1 }
            });
            Ok(p, "حفظ الخطة " + tag);
            Ok(planning.ApprovePlan(p.Id), "اعتماد الخطة " + tag);
            Ok(orders.IssueTodayOrders(), "إصدار أمر اليوم " + tag);
            int orderId = db.ProductionOrders.AsNoTracking().Single(o => o.SourcePlanId == p.Id).Id;
            Ok(orders.ApproveOrder(orderId), "اعتماد أمر الإنتاج " + tag);
            if (start) Ok(orders.StartOrder(orderId), "بدء الإنتاج " + tag);
            return orderId;
        }
        int orderId = CreateOrder("الأصلية", 100, 740);
        var source = actual.GetActualDeliveryOrders().Single(o => o.OrderId == orderId);
        check(source.CanRecord && source.Items.Count == 1 && source.Items[0].PlannedCartons == 100,
            "سياق الفعلي يعرض الأمر المخطط كما هو دون إعادة اختيار صنف أو دفعة");
        var ui = new ActualProductionRow(source.Items[0], false);
        check(ui.Actual == "" && ui.Difference == "—", "صف الإدخال يبدأ فارغاً لا يفرض كمية مخططة");
        foreach (var invalid in new[] { "", "NaN", "Infinity", "-1", "101", "90.5" })
        {
            ui.Actual = invalid;
            check(!ui.TryQuantity(out _) && ui.Error.Length > 0, "إدخال غير صالح يُرفض: " + invalid);
        }
        ui.Actual = "90";
        check(ui.TryQuantity(out var valid) && valid == 90 && ui.Difference == "10", "حساب فرق 10 كراتين دون تعديل الخطة");

        ActualProductionDto Input(int id, int cartons, double rawKg) => new()
        {
            OrderId = id, ConsumedRawKg = rawKg,
            Items = db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == id)
                .Select(i => new ActualProductionItemDto { OrderItemId = i.Id, ActualCartons = cartons }).ToList(),
            DowntimeHours = 1, DowntimeReason = "عطل ماكينة موثق",
            ByProducts = new() { new() { ByProductId = secondary.Id, QtyKg = 20 } }
        };
        string Snapshot() => System.Text.Json.JsonSerializer.Serialize(new
        {
            lots = db.Lots.AsNoTracking().OrderBy(l => l.Id).Select(l => new { l.Id, l.InStockQtyKg, l.ProducedQtyKg, l.ReservedQtyKg }).ToList(),
            orders = db.ProductionOrderItems.AsNoTracking().OrderBy(i => i.Id).Select(i => new { i.Id, i.ProducedQtyKg, i.ProducedCartons }).ToList(),
            executions = db.ProductionExecutions.Count(), quality = db.QualityChecks.Count(),
            receipts = db.FinishedGoodsReceipts.Count(), ledger = db.InventoryTransactions.Count(),
            finished = db.StockBalances.AsNoTracking().Where(b => b.WarehouseId == fgId)
                .OrderBy(b => b.Id).Select(b => new { b.Id, b.QtyKg, b.PackageCount }).ToList()
        });
        void Rejected(Action<ActualProductionDto> mutate, string label)
        {
            var dto = Input(orderId, 90, 700); mutate(dto);
            string before = Snapshot(); var result = actual.SaveActualProduction(dto);
            check(!result.Ok && Snapshot() == before, "رفض بلا ترحيل جزئي: " + label + " (" + result.Message + ")");
        }
        Rejected(i => i.ConsumedRawKg = 0, "عدم إدخال الخام الفعلي");
        Rejected(i => i.ConsumedRawKg = double.NaN, "خام غير محدود");
        Rejected(i => i.Items[0].ActualCartons = 101, "تجاوز إنتاج الخطة");
        Rejected(i => i.Items[0].ActualCartons = -1, "إنتاج سالب");
        Rejected(i => i.Items.Clear(), "إسقاط بند الأمر");
        Rejected(i => i.Items[0].OrderItemId = int.MaxValue, "انتحال معرف بند آخر");
        Rejected(i => i.ByProducts[0].ByProductId = int.MaxValue, "مخرج غير معرف");
        Rejected(i => i.DowntimeHours = 25, "توقف أطول من يوم");
        var definition = db.Products.Single(p => p.Id == secondary.Id);
        definition.IsActive = false; db.SaveChanges();
        Rejected(_ => { }, "مخرج ثانوي موقوف");
        definition = db.Products.Single(p => p.Id == secondary.Id);
        definition.IsActive = true; db.SaveChanges();

        // A role without finishedgoods may record actual production, but cannot
        // stock WFG: the issued delivery and warehouse receipt are later steps.
        var noWarehouse = new ProductionDeliveryService(db, new ProductionOnlySession(),
            sp.GetRequiredService<INumberingService>(), sp.GetRequiredService<IAuditService>());
        var actualResult = noWarehouse.SaveActualProduction(Input(orderId, 90, 700));
        Ok(actualResult, "تسجيل الفعلي بصلاحيات الإنتاج دون امتيازات المخزن");
        db.ChangeTracker.Clear();
        int executionId = db.ProductionExecutions.AsNoTracking().Single(e => e.OrderId == orderId).Id;
        var exe = db.ProductionExecutions.AsNoTracking().Single(e => e.Id == executionId);
        check(exe.IsDayClosed && exe.Status == DocStatuses.Completed && Near(exe.ConsumedRawKg, 700)
            && exe.ActualCartons == 90 && Near(exe.ActualQtyKg, 720),
            "جلسة 90 كرتون / 720 كجم / 700 خام محفوظة كما أدخلت دون معامل ماء مفروض");
        check(Near(db.Lots.AsNoTracking().Single(l => l.Id == lotId).InStockQtyKg, 300)
            && db.InventoryTransactions.AsNoTracking().Any(t => t.OrderId == orderId && t.ReferenceDocType == ReferenceDocType.ProductionExecution),
            "الصرف الفعلي من الخام موثق مرة واحدة");
        check(!db.FinishedGoodsReceipts.Any(r => r.OrderId == orderId)
            && !db.StockBalances.Any(b => b.WarehouseId == fgId && b.ProductId == product.Id),
            "لا سند مخزن ولا رصيد قابل للبيع عند تسجيل الإنتاج وحده");
        var qc = db.QualityChecks.AsNoTracking().Single(q => q.ExecutionId == executionId);
        check(!qc.IsApproved && qc.AcceptedKg == 0, "محضر الجودة المبدئي لا يمنح قبولاً وهمياً");
        check(db.ExecutionByProducts.AsNoTracking().Any(b => b.ExecutionId == executionId && b.ByProductId == secondary.Id && b.Qty == 20),
            "المخرج الثانوي من بطاقة الأصناف، بالكيلو، لا من أسماء ثابتة");
        var beforeRepeat = Snapshot();
        check(!actual.SaveActualProduction(Input(orderId, 90, 700)).Ok && beforeRepeat == Snapshot(),
            "إعادة النقر لا تضاعف الخام أو الجلسة أو محضر الجودة");
        var daily = reports.Run("daily_production", new());
        check(daily.Rows.Any(r => r.Any(c => c?.ToString() == exe.DocumentNumber)),
            "تقرير الإنتاج اليومي يقرأ الفرق 10 من تنفيذ محفوظ لا من مسودة الشاشة");

        var draft = actual.CreateDeliveryFromActual(executionId, day);
        Ok(draft, "إنشاء أمر تسليم من الفعلي المكتمل");
        Ok(actual.IssueDelivery(draft.Id), "تحرير أمر التسليم للإنتاج التام");
        var card = actual.GetDelivery(draft.Id);
        var line = card.Lines.Single();
        check(line.ProductId == product.Id && line.LotId == lotId && line.CustomerId == cust
            && line.PackagingTypeId == pack.Id && line.PackageCount == 90 && Near(line.QtyKg, 720),
            "بند أمر التسليم يحتفظ بهوية المنتج والعميل والدفعة وعبوة 8 و90 كرتوناً");
        var badReceipt = fg.SaveReceipt(orderId, qc.Id, day, new() { new()
        {
            DeliveryItemId = line.Id, ProductId = product.Id, LotId = lotId,
            PackagingTypeId = pack.Id, PackageCount = 91, NetWeightKg = 720
        } }, draft.Id);
        check(!badReceipt.Ok && !db.FinishedGoodsReceipts.Any(), "عبوة/كراتين غير مطابقة للمصدر لا تُرحّل إلى المخزن");
        var receipt = fg.SaveReceipt(orderId, qc.Id, day, new() { new()
        {
            DeliveryItemId = line.Id, ProductId = product.Id, LotId = lotId,
            CustomerId = cust, PackagingTypeId = pack.Id, PackageCount = 90, NetWeightKg = 720
        } }, draft.Id);
        Ok(receipt, "حفظ سند مخزن التام بهوية بند التسليم");
        Ok(fg.Issue(receipt.Id), "إصدار السند للمخزن دون ترحيل رصيد");
        check(!db.StockBalances.Any(b => b.WarehouseId == fgId && b.ProductId == product.Id),
            "الإصدار وحده لا يصنع رصيداً");
        Ok(fg.Receive(receipt.Id, null), "استلام كامل صريح — null وليس خريطة فارغة");
        var fgStock = db.StockBalances.AsNoTracking().Single(b => b.WarehouseId == fgId && b.ProductId == product.Id);
        check(fgStock.LotId == lotId && fgStock.CustomerId == cust && fgStock.PackagingTypeId == pack.Id
            && Near(fgStock.QtyKg, 720) && fgStock.PackageCount == 90,
            "رصيد WFG وحركة المخزن: 90 كرتوناً/720 كجم لنفس هوية المصدر");
        check(db.InventoryTransactions.AsNoTracking().Any(t => t.WarehouseId == fgId && t.OrderId == orderId
            && t.PackagingTypeId == pack.Id && t.PackageCount == 90
            && t.ReferenceDocType == ReferenceDocType.FinishedGoodsReceipt),
            "دفتر حركات WFG مطابق للعبوة والدفعة والكرتون");
        check(!QualityGate.CustomerDeliveryAllowed(db, orderId, lotId, product.Id).ok,
            "رصيد المخزن وحده لا يكفي للشحن قبل اعتماد الفحص");
        var over = customerDelivery.Save(cust, day, orderId, new() { new()
        { ProductId = product.Id, LotId = lotId, PackagingTypeId = pack.Id, PackageCount = 90, QtyKg = 720 } });
        Ok(over, "إنشاء مسودة عميل دون تحريرها");
        check(!customerDelivery.Approve(over.Id).Ok, "بوابة الجودة تمنع اعتماد العميل قبل نتيجة معتمدة");
        var inspected = quality.SaveCheck(orderId, executionId, day, "نهائي", new() { new()
        {
            ProductId = product.Id, LotId = lotId, PackagingTypeId = pack.Id,
            CheckedCartons = 90, AcceptedCartons = 80, RejectedCartons = 10,
            CheckedQtyKg = 720, AcceptedQtyKg = 640, RejectedQtyKg = 80
        } }, null, new QualityLabDto { Decision = "Passed", SampleCartons = 10 });
        Ok(inspected, "إدخال نتيجة 80 كرتوناً مقبولاً و10 مرفوضة دون تعديل إنتاج 90");
        Ok(quality.ApproveCheck(inspected.Id), "اعتماد محضر الجودة بعد النتائج الكاملة");
        check(!customerDelivery.Approve(over.Id).Ok, "720 كجم مرفوضة عند سقف قبول 640 كجم");
        var allowed = customerDelivery.Save(cust, day, orderId, new() { new()
        { ProductId = product.Id, LotId = lotId, PackagingTypeId = pack.Id, PackageCount = 80, QtyKg = 640 } });
        Ok(allowed, "إنشاء تسليم العميل بحد المقبول فقط");
        Ok(customerDelivery.Approve(allowed.Id), "تسليم 640 كجم مقبولة دون تسريب المرفوضة");
        fgStock = db.StockBalances.AsNoTracking().Single(b => b.WarehouseId == fgId && b.ProductId == product.Id);
        check(Near(fgStock.QtyKg, 80) && fgStock.PackageCount == 10,
            "الرصيد المتبقي 10 كراتين/80 كجم مرفوضة غير قابلة للشحن");
        check(!customerDelivery.Approve(over.Id).Ok, "المرفوضة لا تصبح متاحة بسبب شحنة عميل أخرى");

        if (!concurrency) return;
        // SQL Server run: two independent DbContexts try to record the same
        // unexecuted order. Serializable writes must permit exactly one winner.
        int raceId = CreateOrder("تزامن الجهازين", 10, 60);
        var raceInput = Input(raceId, 10, 60);
        raceInput.ByProducts.Clear(); raceInput.DowntimeHours = 0; raceInput.DowntimeReason = null;
        using var gate = new Barrier(2);
        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            using var s = services.CreateScope();
            var svc = s.ServiceProvider.GetRequiredService<IProductionDeliveryService>();
            gate.SignalAndWait();
            try { return svc.SaveActualProduction(raceInput); }
            catch (Exception ex) { return OpResult.Fail(ex.Message); }
        })).ToArray();
        Task.WaitAll(tasks);
        check(tasks.Count(t => t.Result.Ok) == 1, "SQL Server: واحد فقط من تسجيلين متزامنين نجح");
        check(db.ProductionExecutions.AsNoTracking().Count(e => e.OrderId == raceId && e.IsDayClosed) == 1
            && db.InventoryTransactions.AsNoTracking().Count(t => t.OrderId == raceId
                && t.ReferenceDocType == ReferenceDocType.ProductionExecution) == 1
            && !db.FinishedGoodsReceipts.Any(r => r.OrderId == raceId),
            "SQL Server: لا ازدواج تنفيذ/صرف ولا استلام تام تلقائي بعد السباق");

        // The source and a pending warehouse receipt are touched on different
        // workstations. Cancellation wins => receipt invalidated, no new stock.
        // Receipt wins => source is Completed, cancellation must fail.
        int raceExecutionId = db.ProductionExecutions.AsNoTracking().Single(e => e.OrderId == raceId).Id;
        var raceDelivery = actual.CreateDeliveryFromActual(raceExecutionId, day);
        Ok(raceDelivery, "SQL Server: إنشاء أمر تسليم للتنافس بين الإلغاء والاستلام");
        Ok(actual.IssueDelivery(raceDelivery.Id), "SQL Server: تحرير مصدر الاستلام");
        var raceLine = actual.GetDelivery(raceDelivery.Id).Lines.Single();
        var raceReceipt = fg.SaveReceipt(raceId, null, day, new() { new()
        {
            DeliveryItemId = raceLine.Id, ProductId = product.Id, LotId = lotId,
            CustomerId = cust, PackagingTypeId = pack.Id, PackageCount = 10, NetWeightKg = 80
        } }, raceDelivery.Id);
        Ok(raceReceipt, "SQL Server: سند استلام معلّق بعد تحرير مصدره");
        Ok(fg.Issue(raceReceipt.Id), "SQL Server: إصدار السند بلا مخزون");
        double balanceBeforeRace = db.StockBalances.AsNoTracking()
            .Where(b => b.WarehouseId == fgId && b.ProductId == product.Id && b.LotId == lotId
                && b.CustomerId == cust && b.PackagingTypeId == pack.Id)
            .Sum(b => b.QtyKg);
        using var receiptGate = new Barrier(2);
        var cancelTask = Task.Run(() =>
        {
            using var s = services.CreateScope();
            receiptGate.SignalAndWait();
            try { return s.ServiceProvider.GetRequiredService<IProductionDeliveryService>().CancelDelivery(raceDelivery.Id); }
            catch (Exception ex) { return OpResult.Fail(ex.Message); }
        });
        var receiveTask = Task.Run(() =>
        {
            using var s = services.CreateScope();
            receiptGate.SignalAndWait();
            try { return s.ServiceProvider.GetRequiredService<IFinishedGoodsService>().Receive(raceReceipt.Id, null); }
            catch (Exception ex) { return OpResult.Fail(ex.Message); }
        });
        Task.WaitAll(cancelTask, receiveTask);
        check(cancelTask.Result.Ok != receiveTask.Result.Ok,
            "SQL Server: فائز واحد فقط في سباق إلغاء المصدر وترحيل سند مخزنه");
        var currentDelivery = db.ProductionDeliveries.AsNoTracking().Single(d => d.Id == raceDelivery.Id);
        var currentReceipt = db.FinishedGoodsReceipts.AsNoTracking().Single(r => r.Id == raceReceipt.Id);
        double balanceAfterRace = db.StockBalances.AsNoTracking()
            .Where(b => b.WarehouseId == fgId && b.ProductId == product.Id && b.LotId == lotId
                && b.CustomerId == cust && b.PackagingTypeId == pack.Id)
            .Sum(b => b.QtyKg);
        check(cancelTask.Result.Ok
                ? currentDelivery.Status == DocStatuses.Cancelled && currentReceipt.Status == DocStatuses.Cancelled
                  && Near(balanceAfterRace, balanceBeforeRace)
                : currentDelivery.Status == DocStatuses.Completed && currentReceipt.ReceiptStatus == "Full"
                  && Near(balanceAfterRace, balanceBeforeRace + 80),
            "SQL Server: حالة المصدر والسند ورصيد WFG متوافقة مع الفائز لا ترحيل بعد إلغاء");
    }

    private sealed class ProductionOnlySession : ICurrentSession
    {
        public int UserId => 1;
        public string UserName { get; set; } = "acceptance-production-only";
        public string MachineName => "acceptance";
        public bool IsInRole(string role) => false;
        public bool Can(string module, string action) => module != "finishedgoods";
        public Dictionary<(string module, string action), bool> PermissionCache => new();
        public HashSet<string> Roles => new();
        public DateTime CacheBuiltAt { get; set; } = DateTime.Now;
    }
}
