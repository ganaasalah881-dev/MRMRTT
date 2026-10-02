using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Same real-service scenario on SQLite and isolated SQL Server. No Windows execution claim.</summary>
public static class TodayOrdersScenarios
{
    public static void Run(IServiceProvider services, Action<bool, string> check, bool concurrency = false)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var plans = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var orders = scope.ServiceProvider.GetRequiredService<IProductionOrderService>();
        var shifts = scope.ServiceProvider.GetRequiredService<IShiftService>();
        var day = db.BusinessNow.Date; string D(DateTime x) => x.ToString("dd/MM/yyyy");
        void Ok(OpResult x, string label) => check(x.Ok, label + (x.Ok ? "" : ": " + x.Message));
        check(orders.GetTodayProduction().Rows.Count == 0, "يوم بلا خطة: قائمة فارغة لا كتالوج أصناف");
        check(!orders.IssueTodayOrders().Ok && !db.ProductionOrders.Any(), "رفض الإصدار عند غياب خطة اليوم");
        Ok(shifts.SaveShift(1, "وردية صباحية", "06:00", "14:00", 8, 0, 8), "تعريف ساعات الوردية الفعلية");
        var raw = master.SaveProductFull(null, "TOD-RAW", "سكري خام", "001", "Raw", "كجم", 20, 0, 0, null!); Ok(raw, "Master: إنشاء سكري خام");
        var a8 = master.SaveProductFull(null, "TOD-8", "سكري تام 8 كجم", "002", "Finished", "كرتون", 8, 1, 8, new() { (1, null!, 5000) }, raw.Id);
        var a4 = master.SaveProductFull(null, "TOD-4", "سكري تام 4 كجم", "002", "Finished", "كرتون", 4, 1, 4, new() { (1, null!, 5000) }, raw.Id);
        var third = master.SaveProductFull(null, "TOD-THIRD", "صنف ثالث غير مخطط", "002", "Finished", "كرتون", 4, 1, 4, new() { (1, null!, 5000) }, raw.Id);
        check(a8.Ok && a4.Ok && third.Ok, "Master: صنفا المثال وصنف ثالث غير موجود في الخطة");
        var receipt = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var rec = receipt.SaveShipment(1, null!, null!, new() { new() { ProductId = raw.Id, QtyKg = 100000, PackageCount = 5000, UnitWeightKg = 20, TreatmentRequired = false } });
        Ok(rec, "استلام خام العميل بقرار معالجة صريح"); Ok(receipt.ApproveShipment(rec.Id), "اعتماد الاستلام قبل التخطيط");
        var lot = db.Lots.AsNoTracking().Single(l => l.ShipmentId == rec.Id).Id;
        PlanItemDto Item(int product, int cartons, DateTime date, bool withLot = false, int customer = 1, int shift = 1, int line = 1) => new()
        {
            ProductId = product, SelectedRawProductId = raw.Id, SourceType = withLot ? "FromReceiving" : "Manual",
            CustomerId = customer, LotId = withLot ? lot : null, ShipmentId = withLot ? rec.Id : null,
            PlannedCartons = cartons, PlannedQtyKg = cartons * (product == a8.Id ? 8 : 4),
            ScheduledDate = D(date), SuggestedShiftId = shift, SuggestedLineId = line
        };
        var main = plans.SavePlan("خطة المثال الإلزامي", "Daily", D(day), D(day), 1, 1,
            new() { Item(a8.Id, 3000, day, true), Item(a4.Id, 2000, day, true) }); Ok(main, "حفظ مسودة خطة 3000 + 2000");
        check(orders.GetTodayProduction().Rows.Count == 0, "المسودة لا تظهر كخطة معتمدة");
        var yesterday = plans.SavePlan("أمس", "Daily", D(day.AddDays(-1)), D(day.AddDays(-1)), 1, 1, new() { Item(third.Id, 10, day.AddDays(-1)) });
        var tomorrow = plans.SavePlan("غد", "Daily", D(day.AddDays(1)), D(day.AddDays(1)), 1, 1, new() { Item(third.Id, 10, day.AddDays(1)) });
        Ok(yesterday, "إنشاء خطة أمس للاختبار السلبي"); Ok(tomorrow, "إنشاء خطة غد للاختبار السلبي");
        Ok(plans.ApprovePlan(yesterday.Id), "اعتماد خطة أمس"); Ok(plans.ApprovePlan(tomorrow.Id), "اعتماد خطة غد");
        check(orders.GetTodayProduction().Rows.Count == 0, "خطة أمس وغد لا تسربان صنفاً إلى شاشة اليوم");
        List<OrderItemDto> Dtos(int planId) => db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == planId).OrderBy(i => i.Id).ToList().Select(i => new OrderItemDto
        { PlanItemId = i.Id, ProductId = i.ProductId, CustomerId = i.CustomerId, ShipmentId = i.ShipmentId, LotId = i.LotId,
            PackagingTypeId = i.PackagingTypeId, PlannedQtyKg = i.PlannedQtyKg, PlannedCartons = i.PlannedCartons }).ToList();
        OpResult Save(List<OrderItemDto> items) => orders.SaveOrder("FromPlan", main.Id, 1, D(day), 1, 1, items);
        check(!Save(Dtos(main.Id)).Ok, "الخلفية ترفض الأمر من مسودة غير معتمدة");
        check(!orders.SaveOrder("FromPlan", yesterday.Id, 1, D(day.AddDays(-1)), 1, 1, Dtos(yesterday.Id)).Ok, "رفض تاريخ أمر أمس");
        check(!orders.SaveOrder("FromPlan", tomorrow.Id, 1, D(day), 1, 1, Dtos(tomorrow.Id)).Ok, "رفض تزوير تاريخ اليوم لبنود الغد");
        check(!orders.SaveOrder("Manual", null, 1, D(day), 1, 1, Dtos(main.Id)).Ok, "رفض الأمر اليدوي حتى لمدير النظام");
        Ok(plans.ApprovePlan(main.Id), "اعتماد خطة اليوم عبر الخدمة الرسمية");
        var sheet = orders.GetTodayProduction();
        check(sheet.Day == day && sheet.Rows.Count == 2 && sheet.Rows.All(r => r.PlanId == main.Id), "فتح أمر إنتاج اليوم: الصنفان فقط من الخطة المعتمدة");
        check(sheet.Rows.Single(r => r.ProductId == a8.Id).PlannedCartons == 3000 && sheet.Rows.Single(r => r.ProductId == a4.Id).PlannedCartons == 2000,
            "الكمية المعروضة بالضبط: سكري8=3000، سكري4=2000");
        check(sheet.Rows.All(r => r.CustomerId == 1 && !string.IsNullOrWhiteSpace(r.CustomerName) && r.Unit == "كرتون" && r.ShiftId == 1), "العميل والوحدة والوردية من مراجع الخطة");
        int movements = db.InventoryTransactions.Count();
        orders.GetTodayProduction(); orders.GetTodayProduction();
        check(!db.ProductionOrders.Any() && db.InventoryTransactions.Count() == movements, "فتح الشاشة وتحديثها لا يصدر أوامر ولا يصرف مخزوناً");
        var bad = Dtos(main.Id); bad.Add(new() { ProductId = third.Id, PlannedCartons = 10, PlannedQtyKg = 40 });
        check(!Save(bad).Ok, "محاولة إضافة صنف ثالث مرفوضة في الخلفية");
        foreach (int qty in new[] { 3500, 2500 })
        {
            bad = Dtos(main.Id); bad[0].PlannedCartons = qty; bad[0].PlannedQtyKg = qty * 8;
            check(!Save(bad).Ok, $"رفض تغيير 3000 إلى {qty} حتى مع توافق الكيلو والكرتون");
        }
        bad = Dtos(main.Id); bad[0].ProductId = third.Id;
        check(!Save(bad).Ok, "رفض اختيار صنف Master غير موجود في بند الخطة");
        check(!Save(Dtos(main.Id).Take(1).ToList()).Ok, "رفض إسقاط بند من مجموعة الخطة");
        bad = Dtos(main.Id); bad[1] = bad[0]; check(!Save(bad).Ok, "رفض تكرار معرف بند الخطة");
        bad = Dtos(main.Id); bad[0].CustomerId = 2; check(!Save(bad).Ok, "رفض تزوير عميل البند");
        check(!orders.SaveOrder("FromPlan", main.Id, 1, D(day), 2, 1, Dtos(main.Id)).Ok, "رفض إعادة اختيار وردية غير الواردة في الخطة");
        check(!db.ProductionOrders.Any(), "كل محاولات التجاوز السابقة بلا حفظ جزئي");
        // IssueOrdersFromPlan is a supported production-ORDER path; only the
        // legacy production-DELIVERY sources are read-only. Do not issue here:
        // issuing would consume the rows the next IssueTodayOrders assertion needs.
        check(orders.GetScheduledProduction().Rows.Count > 0 && !db.ProductionOrders.Any(),
            "عرض الجدول لا يصدر أوامر ولا يصرف مخزوناً");
        check(!scope.ServiceProvider.GetRequiredService<IDayRunService>().IssueSelected(main.Id, D(day), new()).Ok, "مسار تحديد/تجزئة تشغيل اليوم القديم موقوف");
        Ok(orders.IssueTodayOrders(), "إصدار اليوم: نسخ جميع البنود المعتمدة كما هي");
        var order = db.ProductionOrders.AsNoTracking().Single();
        var stored = db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == order.Id).OrderBy(i => i.Id).ToList();
        check(stored.Count == 2 && stored[0].PlannedCartons == 3000 && stored[1].PlannedCartons == 2000 && stored.All(i => i.CustomerId == 1), "قاعدة البيانات: بندان فقط وكميات 3000 و2000 وهوية العميل محفوظة");
        check(db.InventoryTransactions.Count() == movements, "الإصدار لم يصرف مخزوناً ولم يبدأ التنفيذ");
        Ok(orders.IssueTodayOrders(), "تكرار الإصدار آمن");
        check(db.ProductionOrders.Count() == 1 && orders.GetTodayProduction().Rows.Count == 2, "لا أمر مكرر ولا اختفاء للبنود بعد إصدارها");
        check(!Save(Dtos(main.Id)).Ok, "الاستدعاء المباشر لا يكرر أمراً لبنود صادرة");
        check(!orders.UpdateOrderItems(order.Id, new() { new() { Id = stored[0].Id, PlannedCartons = 2500, PlannedQtyKg = 20000 } }).Ok, "تعديل كمية أمر محفوظ إلى أقل من الخطة مرفوض");
        check(!orders.UpdateOrderHeader(order.Id, D(day.AddDays(1)), 2, 1).Ok, "إعادة جدولة أمر محفوظ مرفوضة");
        check(!scope.ServiceProvider.GetRequiredService<IPlanProgressService>().UpdatePlanItem(stored[0].PlanItemId!.Value, newQtyKg: 20000).Ok,
            "الخطة المرتبطة بأمر لا تتغير بصمت عبر تعديل التقدم");
        var edit = db.ProductionOrderItems.Single(i => i.Id == stored[0].Id); edit.PlannedCartons = 2500; edit.PlannedQtyKg = 20000; db.SaveChanges();
        check(!orders.ApproveOrder(order.Id).Ok, "إفساد كمية الأمر مباشرة في القاعدة يُكتشف عند الاعتماد");
        edit = db.ProductionOrderItems.Single(i => i.Id == stored[0].Id); edit.PlannedCartons = 3000; edit.PlannedQtyKg = 24000; db.SaveChanges();
        Ok(orders.ApproveOrder(order.Id), "اعتماد الأمر الصحيح مع إبقاء دورة صرف المواد");
        edit = db.ProductionOrderItems.Single(i => i.Id == stored[0].Id); edit.ProductId = third.Id; db.SaveChanges();
        check(!orders.StartOrder(order.Id).Ok, "تبديل صنف محفوظ يُكتشف عند بدء التنفيذ");
        edit = db.ProductionOrderItems.Single(i => i.Id == stored[0].Id); edit.ProductId = a8.Id; db.SaveChanges();
        Ok(orders.StartOrder(order.Id), "بدء التنفيذ الصحيح من أمر مطابق للخطة");
        var actual = scope.ServiceProvider.GetRequiredService<IExecutionService>().CloseProductionDay(order.Id, 29200, 4600, 0, 0, 0, false, new(), true,
            consumedRawKg: 20000, itemQtys: new() { new() { OrderItemId = stored[0].Id, ProducedKg = 21600, ProducedCartons = 2700 }, new() { OrderItemId = stored[1].Id, ProducedKg = 7600, ProducedCartons = 1900 } });
        Ok(actual, "تسجيل إنتاج فعلي مستقل وإرساله للجودة مع المحافظة على قاعدة زيادة الوزن بالماء");
        check(db.ProductionOrderItems.AsNoTracking().Where(i => i.OrderId == order.Id).Sum(i => i.PlannedCartons) == 5000
            && db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == main.Id).Sum(i => i.PlannedCartons) == 5000,
            "الفعلي الأقل لا يغيّر كمية الأمر أو أصل خطة5000");
        check(orders.GetTodayProduction().Rows.Count == 2 && orders.GetTodayProduction().Rows.Sum(r => r.PlannedCartons) == 5000, "بعد التنفيذ تبقى كمية الخطة الأصلية ظاهرة");

        var customer2 = master.SaveCustomer(null, "TOD-C2", "عميل الاختبار الثاني", "Customer", "", "", true); Ok(customer2, "تعريف عميل ثانٍ");
        var line2 = new ProductionLine { LineCode = "TOD-L2", LineNameAr = "خط الاختبار الثاني", IsActive = true }; db.ProductionLines.Add(line2); db.SaveChanges();
        var period = plans.SavePlan("خطة فترة متعددة العملاء", "Period", D(day.AddDays(-1)), D(day.AddDays(2)), 1, line2.Id,
            new() { Item(a8.Id, 100, day, customer: 1, line: line2.Id), Item(a8.Id, 200, day, customer: customer2.Id, line: line2.Id),
                Item(third.Id, 30, day.AddDays(2), customer: customer2.Id, line: line2.Id) }); Ok(period, "حفظ خطة فترة بعميلين وصنف مستقبلي");
        Ok(plans.ApprovePlan(period.Id), "اعتماد خطة الفترة");
        sheet = orders.GetTodayProduction();
        check(sheet.Rows.Count == 4 && sheet.Rows.Count(r => r.PlanId == period.Id) == 2 && sheet.Rows.All(r => r.ProductId != third.Id),
            "كل خطط اليوم اليومية والفترية تظهر معاً، دون بند الفترة المستقبلي");
        check(sheet.Rows.Where(r => r.PlanId == period.Id).Select(r => r.CustomerId).Distinct().Count() == 2,
            "الصنف المشترك لا يدمج ملكية العميلين");
        Ok(orders.IssueTodayOrders(), "إصدار بقية بنود اليوم لجميع العملاء تلقائياً");
        check(db.ProductionOrders.Count() == 3 && db.ProductionOrders.All(o => o.ProductionDate == day), "أمر لكل عميل وفتحة مع حفظ الارتباط؛ جميع التواريخ اليوم");
        check(!db.ProductionOrderItems.Any(i => i.ProductId == third.Id), "لم يُصدر أمر لصنف مستقبلي أو خارج خطة اليوم");

        // Historical data compatibility: a genuinely unspecified shift is never silently set to shift 1.
        var legacy = new ProductionPlan { DocumentNumber = "TOD-LEGACY", PlanTitle = "خطة قديمة بلا وردية محددة", StartDate = day, EndDate = day,
            IsApproved = true, Status = DocStatuses.Approved, Items = new() { new() { ProductId = a4.Id, PlannedCartons = 1, PlannedQtyKg = 4, ScheduledDate = day } } };
        db.ProductionPlans.Add(legacy); db.SaveChanges();
        check(orders.GetTodayProduction().Rows.Single(r => r.PlanId == legacy.Id).ShiftId == null, "الوردية غير المحددة في خطة قديمة تبقى غير محددة في العرض");
        Ok(orders.IssueTodayOrders(), "نقل خطة قديمة بلا اختراع وردية أو خط");
        check(db.ProductionOrders.AsNoTracking().Single(o => o.SourcePlanId == legacy.Id).ShiftId == null, "لا وردية افتراضية في أمر الإنتاج");
        if (concurrency)
        {
            var simultaneous = new ProductionPlan { DocumentNumber = "TOD-CONCURRENT", PlanTitle = "اختبار إصدار متزامن", StartDate = day, EndDate = day,
                IsApproved = true, Status = DocStatuses.Approved, Items = new() { new() { ProductId = a4.Id, PlannedCartons = 2, PlannedQtyKg = 8, ScheduledDate = day } } };
            db.ProductionPlans.Add(simultaneous); db.SaveChanges();
            using var gate = new Barrier(2);
            var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
            { using var s = services.CreateScope(); var svc = s.ServiceProvider.GetRequiredService<IProductionOrderService>(); gate.SignalAndWait(); return svc.IssueTodayOrders(); })).ToArray();
            Task.WaitAll(tasks);
            check(tasks.Any(t => t.Result.Ok), "SQL Server: إصدار اليوم المتزامن نجحت منه محاولة واحدة على الأقل");
            check(db.ProductionOrderItems.AsNoTracking().Count(i => i.PlanItemId == simultaneous.Items[0].Id) == 1,
                "SQL Server: لم يتكرر بند الخطة بين جهازين متزامنين");
        }
    }
}
