using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Views.Screens;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public static class PlanningScenarios
{
    public static void Run(IServiceProvider services, Action<bool, string> check, bool testConcurrency = false)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var master = scope.ServiceProvider.GetRequiredService<MasterDataService>();
        var plan = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        var caps = scope.ServiceProvider.GetRequiredService<ICapacityService>();
        var shifts = scope.ServiceProvider.GetRequiredService<IShiftService>();
        check(shifts.SaveShift(1, "وردية الاختبار", "06:00", "14:00", 8, 0, 8).Ok, "تعريف 8 ساعات فعلية في مصدر الورديات");
        var rawA = master.SaveProductFull(null, "ACC-RA", "سكري خام", "001", "Raw", "كجم", 20, 0, 0, null!);
        var rawB = master.SaveProductFull(null, "ACC-RB", "برحي خام", "001", "Raw", "كجم", 20, 0, 0, null!);
        var rawEmpty = master.SaveProductFull(null, "ACC-RC", "خام بلا تام", "001", "Raw", "كجم", 20, 0, 0, null!);
        check(rawA.Ok && rawB.Ok && rawEmpty.Ok, "إنشاء ثلاثة أصناف خام بخدمة شاشة الأصناف الحقيقية");
        var a8 = master.SaveProductFull(null, "ACC-A8", "سكري تام 8 كجم", "002", "Finished", "كرتون", 8, 1, 8, new() { (1, null!, 5000) }, rawA.Id);
        var a4 = master.SaveProductFull(null, "ACC-A4", "سكري تام 4 كجم", "002", "Finished", "كرتون", 4, 1, 4, new() { (1, null!, 5000) }, rawA.Id);
        var other = master.SaveProductFull(null, "ACC-B", "إخلاص تام", "002", "Finished", "كرتون", 4, 1, 4, new() { (1, null!, 5000) }, rawB.Id);
        check(a8.Ok && a4.Ok && other.Ok, "إنشاء وربط أصناف التام من Master Items بلا قوائم ثابتة");
        check(db.Products.AsNoTracking().Single(p => p.Id == a8.Id).SourceProductId == rawA.Id, "حفظ SourceProductId الفعلي في قاعدة البيانات");
        var names = plan.GetFinishedProductsForRaw(rawA.Id);
        check(names.Count == 2 && names.Any(p => p.Id == a8.Id) && names.Any(p => p.Id == a4.Id) && names.All(p => p.Id != other.Id), "سكري خام يعرض سكري 8 و4 فقط؛ لا إخلاص");
        var row = new LotEditorRow
        {
            LoadFinishedProducts = raw => plan.GetFinishedProductsForRaw(raw).Select(p => new ProductOption { Id = p.Id, Name = p.ProductNameAr }).ToList(),
            ShiftId = 1, DateValue = new DateTime(2026, 10, 1)
        };
        var notifications = new List<string>(); row.PropertyChanged += (_, e) => notifications.Add(e.PropertyName ?? "");
        row.RawProductId = rawA.Id; row.ProductId = a8.Id; row.CartonsText = "100";
        check(row.AllProducts.Count == 2 && row.ProductId == a8.Id, "نموذج الواجهة الفعلي يعرض الاختيارين المرتبطين");
        row.RawProductId = rawB.Id;
        check(row.AllProducts.Count == 1 && row.AllProducts[0].Id == other.Id && row.ProductId == null && !row.IsChecked, "تغيير الخام يبدل القائمة ويمسح الصنف السابق فوراً");
        check(notifications.Contains(nameof(LotEditorRow.AllProducts)) && notifications.Contains(nameof(LotEditorRow.ProductId)), "إشعارات WPF للقائمة والاختيار أُطلقت فعلياً");
        row.RawProductId = rawEmpty.Id;
        check(row.AllProducts.Count == 0 && row.LinkMessage == "لا توجد أصناف تامة مرتبطة بهذا الصنف الخام.", "القائمة الفارغة تظهر الرسالة المطلوبة حرفياً");
        row.RawProductId = rawA.Id; row.ProductId = a8.Id;
        var relink = master.SaveProductFull(a8.Id, "ACC-A8", "سكري تام 8 كجم", "002", "Finished", "كرتون", 8, 1, 8, null!, rawB.Id);
        check(relink.Ok, "تعديل الربط عبر خدمة شاشة الأصناف"); row.ReloadFinishedProducts();
        check(row.ProductId == null && row.AllProducts.All(p => p.Id != a8.Id), "إعادة القراءة لا تحتفظ بربط Master قديم");
        check(master.SaveProductFull(a8.Id, "ACC-A8", "سكري تام 8 كجم", "002", "Finished", "كرتون", 8, 1, 8, null!, rawA.Id).Ok, "إعادة ربط صنف الاختبار بالخام الصحيح");

        var receipt = scope.ServiceProvider.GetRequiredService<IReceivingService>();
        var shipment = receipt.SaveShipment(1, null!, null!, new() { new() { ProductId = rawA.Id, QtyKg = 100000, PackageCount = 5000, UnitWeightKg = 20, TreatmentRequired = false } });
        check(shipment.Ok && receipt.ApproveShipment(shipment.Id).Ok, "استلام واعتماد خام العميل وإنشاء الدفعة الحقيقية");
        int lot = db.Lots.AsNoTracking().Single(l => l.ShipmentId == shipment.Id).Id;
        check(plan.GetPlannableProducts(lot).Select(p => p.Id).OrderBy(i => i).SequenceEqual(new[] { a8.Id, a4.Id }.OrderBy(i => i)), "مسار دفعات العميل نفسه يفلتر التام حسب خام الدفعة");
        PlanItemDto Item(int id, int quantity, int? selectedRaw = null, int? selectedLot = null, string day = "01/10/2026") => new()
        {
            ProductId = id, SelectedRawProductId = selectedRaw ?? rawA.Id, LotId = selectedLot,
            SourceType = selectedLot == null ? "Manual" : "FromReceiving", CustomerId = 1,
            PlannedCartons = quantity, PlannedQtyKg = quantity * UnitsPolicy.CartonWeight(db, id, null!),
            ScheduledDate = day, SuggestedShiftId = 1, SuggestedLineId = 1
        };
        OpResult Save(params PlanItemDto[] items) => plan.SavePlan("اختبار الربط والطاقة", "Daily", "01/10/2026", "01/10/2026", 1, 1, items.ToList());
        var wrong = Save(Item(other.Id, 1, rawA.Id, lot));
        check(!wrong.Ok && !db.ProductionPlans.Any(p => p.PlanTitle == "اختبار الربط والطاقة"), "Backend يرفض إخلاص مع سكري خام ولا يحفظ خطة جزئية");
        check(!Save(Item(a8.Id, 1, rawB.Id, lot)).Ok, "تزوير SelectedRawProductId لا يتجاوز خام الدفعة الفعلي");
        check(!Save(Item(other.Id, 1, rawA.Id)).Ok, "المسار اليدوي أيضاً يرفض الخام غير المرتبط");
        var x = new LotEditorRow { ProductId = a8.Id, ShiftId = 1, DateValue = row.DateValue };
        var y = new LotEditorRow { ProductId = a4.Id, ShiftId = 1, DateValue = row.DateValue };
        var z = new LotEditorRow { ProductId = a8.Id, ShiftId = 1, DateValue = row.DateValue };
        var selected = new List<LotEditorRow> { x, y, z };
        PlanItemDto Dto(LotEditorRow r, int? q = null) => Item(r.ProductId!.Value, q ?? int.Parse(r.CartonsText));
        foreach (var r in selected) r.QuantityGuard = q =>
        {
            if (q == 0) return null!;
            var result = plan.EvaluateCapacity(selected.Where(a => a.IsChecked && a != r).Select(a => Dto(a)).Append(Dto(r, q)).ToList());
            return result.Rows.LastOrDefault()?.Error ?? result.Error;
        };
        PlanCapacityResult Current() => plan.EvaluateCapacity(selected.Where(r => r.IsChecked).Select(r => Dto(r)).ToList());
        x.CartonsText = "3000";
        check(Math.Abs(Current().UsagePercent - 60) < 1e-7 && Math.Abs(Current().RemainingHours * 625 - 2000) < 1e-7, "5000: إضافة 3000 تستهلك 60% ويتبقى 2000");
        bool rejected = false;
        try { y.CartonsText = "3500"; } catch (ArgumentException) { rejected = true; }
        check(rejected && !y.IsChecked && y.CartonsText == "0" && y.QuantityError.Contains("2,000") && y.QuantityError.Contains("1,500"), "3500 مرفوضة فوراً في setter الواجهة؛ الحد 2000 والتجاوز1500");
        y.CartonsText = "2000"; check(Math.Abs(Current().UsagePercent - 100) < 1e-7, "قبول2000: استهلاك100%");
        rejected = false; try { z.CartonsText = "1"; } catch (ArgumentException) { rejected = true; }
        check(rejected && !z.IsChecked, "أي كرتون إضافي مرفوض قبل الحفظ");
        x.IsChecked = false; check(Math.Abs(Current().RemainingHours * 625 - 3000) < 1e-7, "حذف/إلغاء تحديد3000 يعيد الطاقة فوراً");
        y.CartonsText = "1000"; check(Math.Abs(Current().UsagePercent - 20) < 1e-7, "تعديل الكمية يحدث الاستخدام إلى20%");
        var reopen = plan.EvaluateCapacity(new() { Item(a8.Id, 3000), Item(a4.Id, 3500) });
        check(!reopen.IsValid && reopen.Rows.Last().MaximumCartons == 2000, "إعادة فتح الاختيار تحتسب مسودة الأب غير المحفوظة");
        check(!Save(Item(a8.Id, 3000), Item(a4.Id, 3500)).Ok, "Backend يرفض6500حتى إذا تم تجاوز الواجهة");
        var saved = Save(Item(a8.Id, 3000, selectedLot: lot), Item(a4.Id, 2000, selectedLot: lot));
        check(saved.Ok, "حفظ خطة صحيحة بروابط الخام و5000كرتون");
        check(!Save(Item(a8.Id, 1)).Ok, "خطة أخرى لا تحصل على طاقة إضافية في الوردية المشغولة");
        var update = plan.UpdatePlan(saved.Id, "اختبار الربط والطاقة", "Daily", "01/10/2026", "01/10/2026", 1, 1, new() { Item(a8.Id, 3000, selectedLot: lot), Item(other.Id, 100, rawA.Id, lot) });
        check(!update.Ok && db.ProductionPlanItems.AsNoTracking().Where(i => i.PlanId == saved.Id).Sum(i => i.PlannedCartons) == 5000, "تعديل الخطة بخامة خاطئة مرفوض مع rollback");
        var itemId = db.ProductionPlanItems.AsNoTracking().Single(i => i.PlanId == saved.Id && i.ProductId == a4.Id).Id;
        var progress = scope.ServiceProvider.GetRequiredService<IPlanProgressService>();
        check(!progress.UpdatePlanItem(itemId, newProductId: other.Id).Ok, "تعديل بند محفوظ لا يتجاوز الربط");
        check(!progress.UpdatePlanItem(itemId, newQtyKg: 3500 * 4).Ok, "تعديل بند محفوظ لا يتجاوز الطاقة");
        check(progress.UpdatePlanItem(itemId, newQtyKg: 1000 * 4).Ok, "خفض كمية البند المحفوظ يحرر الطاقة");
        check(caps.SetCapacity(a4.Id, 1, 2500).Ok, "تغيير معدل الصنف الثاني من مصدر الطاقة");
        var mixed = plan.EvaluateCapacity(new() { Item(a8.Id, 3000), Item(a4.Id, 1000) }, saved.Id);
        check(mixed.IsValid && Math.Abs(mixed.UsagePercent - 100) < 1e-7 && mixed.Summary.Contains("ساعة إنتاج فعلية"), "المعدلات المختلفة تستهلك4.8+3.2=8ساعات مشتركة");
        check(!plan.EvaluateCapacity(new() { Item(a8.Id, 3000), Item(a4.Id, 1001) }, saved.Id).IsValid, "معدل الصنف البطيء يمنع الكرتون1001");
        var exact = plan.EvaluateCapacity(new() { Item(a8.Id, 5000) }, saved.Id);
        check(exact.IsValid, "استثناء الخطة الحالية من الإشغال الخارجي يمنع العد المزدوج");
        if (testConcurrency)
        {
            var concurrentItems = Enumerable.Range(0, 2).Select(_ => Item(a8.Id, 3000, day: "03/10/2026")).ToArray();
            using var gate = new Barrier(2);
            var tasks = Enumerable.Range(0, 2).Select(index => Task.Run(() =>
            {
                using var concurrent = services.CreateScope();
                var service = concurrent.ServiceProvider.GetRequiredService<IPlanningService>(); gate.SignalAndWait();
                try { return service.SavePlan("اختبار متزامن", "Daily", "03/10/2026", "03/10/2026", 1, 1,
                    new() { concurrentItems[index] }).Ok; }
                catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 1205) { return false; }
            })).ToArray();
            Task.WaitAll(tasks);
            check(tasks.Count(t => t.Result) == 1, "SQL Server: محاولة حجز3000+3000متزامنة؛ تقبل واحدة فقط");
            check(db.ProductionPlanItems.AsNoTracking().Where(i => i.ScheduledDate == new DateTime(2026, 10, 3)).Sum(i => i.PlannedCartons) == 3000, "SQL Server: الرصيد المخزن بعد التزامن3000لا6000");
        }
    }
}
