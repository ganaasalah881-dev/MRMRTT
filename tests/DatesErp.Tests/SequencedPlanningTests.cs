using DatesErp.Application.Services;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DatesErp.Tests;

/// <summary>SQLite integration coverage of the new planning workflow, not UI-only tests.</summary>
public class SequencedPlanningTests
{
    private static DatesErpDbContext Fresh(TestHost h) => new(new DbContextOptionsBuilder<DatesErpDbContext>()
        .UseSqlite(h.Connection).Options);
    private static T Svc<T>(TestHost h) where T : notnull => h.Services.CreateScope().ServiceProvider.GetRequiredService<T>();

    private static (int lot, int product, int pack) SeedReadyRaw(TestHost h, double kg = 10000)
    {
        using var db = Fresh(h);
        var product = db.Products.Single(p => p.ProductCode == "002-001");
        var raw = db.Products.Single(p => p.ProductCode == "001-001");
        var pack = db.PackagingTypes.Single(p => p.PackageCode == "CT5");
        product.CartonWeightKg = pack.UnitWeightKg;
        product.DefaultPackagingTypeId = pack.Id;
        product.SourceProductId = raw.Id;
        var lot = new Lot { LotCode = $"SEQ-{Guid.NewGuid():N}", ProductId = raw.Id,
            CustomerId = 1, InitialQtyKg = kg, InStockQtyKg = kg, TreatmentReadyQtyKg = kg,
            Status = DocStatuses.Approved };
        db.Lots.Add(lot);
        db.SaveChanges();
        return (lot.Id, product.Id, pack.Id);
    }

    private static SequencedPlanSetup Setup(string mode = "Single", int[] customers = null,
        int[] shifts = null, int days = 1) => new()
    {
        Title = "جدول إنتاج متسلسل", CustomerMode = mode,
        StartDate = new DateTime(2026, 12, 1), EndDate = new DateTime(2026, 12, 1).AddDays(days - 1),
        CustomerIds = (customers ?? new[] { 1 }).ToList(), ShiftIds = (shifts ?? new[] { 1, 2 }).ToList(), LineId = 1
    };
    private static SequencedPlanItemInput Input((int lot, int product, int pack) raw, int cartons, int? itemId = null)
        => new() { ItemId = itemId, LotId = raw.lot, ProductId = raw.product,
            PackagingTypeId = raw.pack, Cartons = cartons };

    [Fact]
    public void Setup_Is_Required_Stored_And_Builds_Chronological_Independent_Shift_Cards()
    {
        using var h = new TestHost(); h.LoginAsAdmin();
        var service = Svc<SequencedPlanningService>(h);
        Assert.False(service.Create(Setup(shifts: Array.Empty<int>())).Ok);
        Assert.False(service.Create(Setup(days: 0)).Ok);
        Assert.False(service.Create(Setup("Fair", new[] { 1 })).Ok);
        Assert.False(service.Create(Setup(shifts: new[] { 3 })).Ok); // third active shift is not first/second
        Assert.False(service.Create(Setup(shifts: new[] { 2, 1 })).Ok); // no reverse order
        var r = service.Create(Setup(days: 2));
        Assert.True(r.Ok, r.Message);
        using var db = Fresh(h);
        Assert.Equal(0, db.ProductionPlanItems.Count(i => i.PlanId == r.Id));
        Assert.Equal("Single", db.ProductionPlanContexts.Single(c => c.PlanId == r.Id).CustomerMode);
        Assert.Equal(new[] { (new DateTime(2026,12,1),1), (new DateTime(2026,12,1),2),
            (new DateTime(2026,12,2),1), (new DateTime(2026,12,2),2) },
            db.ProductionPlanWorkSlots.Where(s => s.PlanId == r.Id).OrderBy(s => s.SequenceNo)
                .AsEnumerable().Select(s => (s.WorkDate.Date,s.ShiftId)).ToArray());
        Assert.Equal("Pending", db.ProductionPlanWorkSlots.Where(s => s.PlanId == r.Id).OrderBy(s => s.SequenceNo).Skip(1).First().State);
    }

    [Fact]
    public void Missing_Recipe_Or_Wrong_Customer_Or_Raw_Link_Cannot_Be_Planned()
    {
        using var h = new TestHost(); h.LoginAsAdmin();
        var raw = SeedReadyRaw(h);
        var svc = Svc<SequencedPlanningService>(h);
        var create = svc.Create(Setup(shifts: new[] { 1 }));
        Assert.True(create.Ok, create.Message);
        int slot = svc.GetSlots(create.Id).Single().Id;
        var incomplete = svc.Quote(create.Id, slot, Input(raw, 1));
        Assert.Contains("سحب الخام", incomplete.Error);
        Assert.True(incomplete.TimeMaximumCartons > 0 && incomplete.LineMaximumCartons > 0);
        Assert.Equal(0, incomplete.MaximumCartons); // illustrative caps are NOT a final allowed quantity
        Assert.False(svc.SaveSlot(create.Id, slot, new[] { Input(raw, 1) }).Ok);
        var rule = svc.SaveRawRule(raw.product, 1, raw.pack, 6m, "وصفة تحويل خام معتمدة للإنتاج");
        Assert.True(rule.Ok, rule.Message);
        Assert.Equal(1000, svc.Quote(create.Id, slot, Input(raw, 1)).MaximumCartons); // line: 5,000kg / 5kg per carton; actually 1,000
    }

    [Fact]
    public void Missing_Product_Rate_Or_Wrong_Unit_Produces_No_Invented_Maximum()
    {
        using var h = new TestHost(); h.LoginAsAdmin();
        var raw = SeedReadyRaw(h);
        var svc = Svc<SequencedPlanningService>(h);
        Assert.True(svc.SaveRawRule(raw.product, 1, raw.pack, 6m, "وصفة خام معلومة لكن الطاقة غير معرفة").Ok);
        var plan = svc.Create(Setup(shifts: new[] { 1 }));
        int slot = svc.GetSlots(plan.Id).Single().Id;
        using (var db = Fresh(h))
        {
            db.Products.Single(p => p.Id == raw.product).HourlyProductionRate = 0;
            foreach (var c in db.ProductShiftCapacities.Where(c => c.ProductId == raw.product)) c.IsActive = false;
            db.SaveChanges();
        }
        var noRate = svc.Quote(plan.Id, slot, Input(raw, 1));
        Assert.NotNull(noRate.Error);
        Assert.Equal(0, noRate.MaximumCartons);
        using (var db = Fresh(h))
        {
            var p = db.Products.Single(p => p.Id == raw.product);
            p.HourlyProductionRate = 500;
            p.UnitOfMeasure = "قطعة";
            db.SaveChanges();
        }
        Assert.Contains("وحدة", svc.Quote(plan.Id, slot, Input(raw, 1)).Error);
        Assert.False(svc.SaveSlot(plan.Id, slot, new[] { Input(raw, 1) }).Ok);
    }

    [Fact]
    public void Max_Is_Min_Of_Time_Line_And_Released_Raw_And_Standalone_Orders()
    {
        using var h = new TestHost(); h.LoginAsAdmin();
        var raw = SeedReadyRaw(h, 3000);
        var svc = Svc<SequencedPlanningService>(h);
        Assert.True(svc.SaveRawRule(raw.product, 1, raw.pack, 6m, "استهلاك ستة كيلو للكرتون المنتج").Ok);
        var created = svc.Create(Setup(shifts: new[] { 1 }));
        Assert.True(created.Ok, created.Message);
        int slot = svc.GetSlots(created.Id).Single().Id;
        var quote = svc.Quote(created.Id, slot, Input(raw, 1));
        Assert.Null(quote.Error);
        Assert.Equal(4000, quote.TimeMaximumCartons);
        Assert.Equal(1000, quote.LineMaximumCartons);
        Assert.Equal(500, quote.RawMaximumCartons);
        Assert.Equal(500, quote.MaximumCartons);
        Assert.False(svc.SaveSlot(created.Id, slot, new[] { Input(raw, 501) }).Ok);
        using (var db = Fresh(h))
        {
            var order = new ProductionOrder { DocumentNumber = "SEQ-INDEPENDENT", Status = DocStatuses.Approved,
                ProductionDate = new DateTime(2026, 12, 1), ShiftId = 1, LineId = 1 };
            db.ProductionOrders.Add(order); db.SaveChanges();
            db.ProductionOrderItems.Add(new ProductionOrderItem { OrderId = order.Id,
                LotId = null, ProductId = raw.product, PackagingTypeId = raw.pack,
                PlannedCartons = 950, PlannedQtyKg = 4750 });
            db.SaveChanges();
        }
        quote = svc.Quote(created.Id, slot, Input(raw, 1));
        Assert.Equal(50, quote.LineMaximumCartons);
        Assert.Equal(50, quote.MaximumCartons);
        Assert.False(svc.SaveSlot(created.Id, slot, new[] { Input(raw, 51) }).Ok);
        Assert.True(svc.SaveSlot(created.Id, slot, new[] { Input(raw, 50) }).Ok);
    }

    [Fact]
    public void Incorrect_Empty_Setup_Can_Be_Cancelled_Without_Deleting_Its_History()
    {
        using var h = new TestHost(); h.LoginAsAdmin();
        var raw = SeedReadyRaw(h);
        var svc = Svc<SequencedPlanningService>(h);
        Assert.True(svc.SaveRawRule(raw.product, 1, raw.pack, 6m, "وصفة معتمدة لاختبار إلغاء إعداد خاطئ").Ok);
        var wrong = svc.Create(Setup(days: 2));
        Assert.True(wrong.Ok, wrong.Message);
        Assert.False(svc.CancelUnstarted(wrong.Id, "قصير").Ok); // a documented reason is mandatory
        var cancelled = svc.CancelUnstarted(wrong.Id, "اختير العميل والفترة بالخطأ قبل إضافة أي صنف");
        Assert.True(cancelled.Ok, cancelled.Message);
        using (var db = Fresh(h))
        {
            var plan = db.ProductionPlans.Single(p => p.Id == wrong.Id); // never deleted
            Assert.Equal(DocStatuses.Cancelled, plan.Status);
            Assert.Contains("اختير", plan.StatusReason);
            Assert.Equal(1, db.ProductionPlanContexts.Count(c => c.PlanId == wrong.Id)); // setup context stays
            Assert.Equal(4, db.ProductionPlanWorkSlots.Count(s => s.PlanId == wrong.Id));
            Assert.Contains(db.ProductionPlanRevisions.Where(r => r.PlanId == wrong.Id), r => r.Action == "Cancel");
        }
        // A cancelled setup cannot receive products afterwards.
        Assert.False(svc.SaveSlot(wrong.Id, svc.GetSlots(wrong.Id).First().Id, new[] { Input(raw, 1) }).Ok);
        // A plan that already holds items cannot use the "empty setup" cancellation path.
        var started = svc.Create(Setup(shifts: new[] { 1 }));
        Assert.True(started.Ok, started.Message);
        Assert.True(svc.SaveSlot(started.Id, svc.GetSlots(started.Id).Single().Id, new[] { Input(raw, 5) }).Ok);
        Assert.False(svc.CancelUnstarted(started.Id, "محاولة إلغاء خطة بدأ تخطيطها فعلا").Ok);
        Assert.Equal(DocStatuses.Draft, Fresh(h).ProductionPlans.Single(p => p.Id == started.Id).Status);
    }

    [Fact]
    public void Repeated_Input_Instance_Cannot_Hide_Another_Row_From_Raw_Limit()
    {
        using var h = new TestHost(); h.LoginAsAdmin();
        var raw = SeedReadyRaw(h, 600);
        var svc = Svc<SequencedPlanningService>(h);
        Assert.True(svc.SaveRawRule(raw.product, 1, raw.pack, 6m, "وصفة خام لتجربة حجز السطر المكرر").Ok);
        var p = svc.Create(Setup(shifts: new[] { 1 }));
        Assert.True(p.Ok, p.Message);
        int slot = svc.GetSlots(p.Id).Single().Id;
        // 600 kg at 6 kg/carton = 100 cartons. The SAME object twice = 120 cartons: must be refused.
        var sameObject = Input(raw, 60);
        var saved = svc.SaveSlot(p.Id, slot, new[] { sameObject, sameObject });
        Assert.False(saved.Ok);
        using var db = Fresh(h);
        Assert.DoesNotContain(db.ProductionPlanItems, i => i.PlanId == p.Id);
    }

    [Fact]
    public void Evening_Only_Has_One_Card_And_Does_Not_Require_An_Empty_Morning_Shift()
    {
        using var h = new TestHost(); h.LoginAsAdmin();
        var raw = SeedReadyRaw(h);
        var svc = Svc<SequencedPlanningService>(h);
        Assert.True(svc.SaveRawRule(raw.product, 1, raw.pack, 6m, "وصفة معتمدة لخطة مسائية فقط").Ok);
        var p = svc.Create(Setup(shifts: new[] { 2 }));
        Assert.True(p.Ok, p.Message);
        var slot = Assert.Single(svc.GetSlots(p.Id));
        Assert.Equal(2, slot.ShiftId);
        Assert.True(svc.SaveSlot(p.Id, slot.Id, new[] { Input(raw, 12) }).Ok);
        Assert.True(svc.CompleteSlot(p.Id, slot.Id).Ok);
        var result = Svc<IPlanningService>(h).SubmitPlan(p.Id);
        Assert.True(result.Ok, result.Message);
    }

    [Fact]
    public void Cannot_Skip_Shifts_Or_Days_And_Submission_Is_Gated()
    {
        using var h = new TestHost(); h.LoginAsAdmin();
        var raw = SeedReadyRaw(h);
        var svc = Svc<SequencedPlanningService>(h);
        Assert.True(svc.SaveRawRule(raw.product, 1, raw.pack, 6m, "استهلاك معتمد من مدير المنتجات").Ok);
        var created = svc.Create(Setup(days: 2));
        Assert.True(created.Ok, created.Message);
        var slots = svc.GetSlots(created.Id);
        Assert.Equal(4, slots.Count);
        Assert.False(svc.SaveSlot(created.Id, slots[1].Id, new[] { Input(raw, 1) }).Ok);
        Assert.True(svc.SaveSlot(created.Id, slots[0].Id, new[] { Input(raw, 10) }).Ok);
        Assert.False(Svc<IPlanningService>(h).SubmitPlan(created.Id).Ok);
        Assert.True(svc.CompleteSlot(created.Id, slots[0].Id).Ok);
        Assert.False(svc.SaveSlot(created.Id, slots[2].Id, new[] { Input(raw, 1) }).Ok);
        Assert.True(svc.SaveSlot(created.Id, slots[1].Id, new[] { Input(raw, 10) }).Ok);
        Assert.True(svc.CompleteSlot(created.Id, slots[1].Id).Ok);
        Assert.True(svc.SaveSlot(created.Id, slots[2].Id, new[] { Input(raw, 10) }).Ok);
        Assert.True(svc.CompleteSlot(created.Id, slots[2].Id).Ok);
        Assert.False(svc.CompleteSlot(created.Id, slots[3].Id).Ok); // no item, not silently skipped
        Assert.True(svc.ApproveNoProduction(created.Id, slots[3].Id, "توقف خط الإنتاج لتجهيز الصيانة").Ok);
        var sent = Svc<IPlanningService>(h).SubmitPlan(created.Id);
        Assert.True(sent.Ok, sent.Message);
        using var db = Fresh(h);
        Assert.Equal(3, db.ProductionPlanItems.Count(i => i.PlanId == created.Id));
        Assert.Equal(4, db.ProductionPlanWorkSlots.Count(i => i.PlanId == created.Id));
    }

    [Fact]
    public void Edits_Are_Historied_And_Legacy_Replace_Cannot_Wipe_Managed_Cards()
    {
        using var h = new TestHost(); h.LoginAsAdmin();
        var raw = SeedReadyRaw(h);
        var svc = Svc<SequencedPlanningService>(h);
        Assert.True(svc.SaveRawRule(raw.product, 1, raw.pack, 6m, "سحب معتمد لكميات الإنتاج اليومية").Ok);
        var created = svc.Create(Setup(shifts: new[] { 1 }));
        int slot = svc.GetSlots(created.Id).Single().Id;
        Assert.True(svc.SaveSlot(created.Id, slot, new[] { Input(raw, 10) }).Ok);
        Assert.True(svc.CompleteSlot(created.Id, slot).Ok);
        using (var db = Fresh(h))
        {
            var item = db.ProductionPlanItems.Single(i => i.WorkSlotId == slot);
            Assert.Equal(60, item.SourceQtyKg);
            Assert.Equal(50, item.PlannedQtyKg);
            Assert.False(Svc<IPlanningService>(h).UpdatePlan(created.Id, "مسح غير آمن", "Daily", "2026-12-01",
                "2026-12-01", 1, 1, new List<PlanItemDto>()).Ok);
            Assert.False(Svc<IPlanningService>(h).DeletePlan(created.Id).Ok);
            Assert.False(svc.SaveSlot(created.Id, slot, new[] { Input(raw, 9, item.Id) }).Ok);
            var edit = svc.SaveSlot(created.Id, slot, new[] { Input(raw, 9, item.Id) }, "تصحيح الكمية بعد مراجعة الخام");
            Assert.True(edit.Ok, edit.Message);
            Assert.True(svc.CompleteSlot(created.Id, slot).Ok);
        }
        using var fresh = Fresh(h);
        Assert.Equal(9, fresh.ProductionPlanItems.Single(i => i.WorkSlotId == slot).PlannedCartons);
        Assert.Contains(fresh.ProductionPlanRevisions.Where(r => r.PlanId == created.Id), r => r.Action == "SaveSlot" && r.BeforeJson!.Contains("10") && r.AfterJson!.Contains("9"));
        Assert.Equal(54, fresh.Lots.Single(l => l.Id == raw.lot).ReservedQtyKg);
    }

    [Fact]
    public void Customer_Scope_Enforced_But_Fair_Mode_Offers_Each_Selected_Owner()
    {
        using var h = new TestHost(); h.LoginAsAdmin();
        var raw = SeedReadyRaw(h);
        int second;
        using (var db = Fresh(h))
        {
            var customer = new Customer { CustomerCode = "SEQ-C2", CustomerName = "عميل التوزيع" };
            db.Customers.Add(customer); db.SaveChanges(); second = customer.Id;
            db.Lots.Add(new Lot { LotCode = "SEQ-OTHER", ProductId = 1, CustomerId = second,
                InitialQtyKg = 600, InStockQtyKg = 600, Status = DocStatuses.Approved });
            db.SaveChanges();
        }
        var svc = Svc<SequencedPlanningService>(h);
        Assert.True(svc.SaveRawRule(raw.product, 1, raw.pack, 6m, "وصفة معتمدة للتوزيع على العملاء").Ok);
        var single = svc.Create(Setup(shifts: new[] { 1 }));
        Assert.True(single.Ok, single.Message);
        int slot = svc.GetSlots(single.Id).Single().Id;
        using var check = Fresh(h);
        int otherLot = check.Lots.Single(l => l.CustomerId == second).Id;
        Assert.Contains("لا تخص", svc.Quote(single.Id, slot,
            new SequencedPlanItemInput { LotId = otherLot, ProductId = raw.product,
                PackagingTypeId = raw.pack, Cartons = 1 }).Error);
        var fair = svc.Create(Setup("Fair", new[] { 1, second }, new[] { 1 }));
        Assert.True(fair.Ok, fair.Message);
        var fairSlot = svc.GetSlots(fair.Id).Single().Id;
        var offers = svc.GetEligibleLots(fair.Id, fairSlot);
        Assert.Equal(2, offers.Select(o => o.CustomerId).Distinct().Count());
        var proposed = svc.SuggestFairSlot(fair.Id, fairSlot);
        Assert.Equal(2, proposed.Items.Count);
        Assert.Empty(proposed.Warnings);
        Assert.Equal(0, check.ProductionPlanItems.Count(i => i.PlanId == fair.Id)); // suggestion is NOT a hidden save
        Assert.True(svc.SaveSlot(fair.Id, fairSlot, proposed.Items).Ok);
        Assert.Equal(2, Fresh(h).ProductionPlanItems.Count(i => i.PlanId == fair.Id));
    }

    [Fact]
    public void Additive_Sqlite_Migration_Preserves_Legacy_Plans_And_Creates_New_Constraints()
    {
        using var h = new TestHost(); h.LoginAsAdmin();
        int historic;
        using (var db = Fresh(h))
        {
            var old = new ProductionPlan { DocumentNumber = "SEQ-HIST-1", PlanTitle = "خطة موجودة قبل التحديث",
                Status = DocStatuses.Draft, StartDate = new DateTime(2026, 11, 1), EndDate = new DateTime(2026, 11, 1) };
            db.ProductionPlans.Add(old); db.SaveChanges(); historic = old.Id;
            foreach (var table in new[] { "ProductionPlanContexts", "ProductionPlanScopeCustomers",
                "ProductionPlanSelectedShifts", "ProductionPlanRevisions", "ProductRawPlanningRules",
                "ProductionPlanWorkSlots" })
                db.Database.ExecuteSqlRaw(string.Concat("DROP TABLE [", table, "]")); // fixed fixture names ONLY; never run on a live DB
        }
        using (var db = Fresh(h))
        {
            var report = SchemaMigrator.Migrate(db);
            Assert.DoesNotContain(report, line => line.StartsWith("خطأ"));
            Assert.Single(db.ProductionPlans.Where(p => p.Id == historic));
            Assert.Empty(db.ProductionPlanContexts);
            Assert.Equal(1, db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_table_info('ProductionPlanItems') WHERE name='WorkSlotId'").Single());
            Assert.Contains(report, line => line.Contains("ProductionPlanWorkSlots"));
        }
        var svc = Svc<SequencedPlanningService>(h);
        Assert.True(svc.Create(Setup(shifts: new[] { 1 })).Ok);
        using var final = Fresh(h);
        Assert.Single(final.ProductionPlans.Where(p => p.Id == historic));
        Assert.Equal(1, final.ProductionPlanContexts.Count());
    }

    [Fact]
    public void Completed_Single_Shift_Plan_Can_Be_Submitted_And_Approved_With_Revision_History()
    {
        using var h = new TestHost(); h.LoginAsAdmin();
        var raw = SeedReadyRaw(h);
        var svc = Svc<SequencedPlanningService>(h);
        Assert.True(svc.SaveRawRule(raw.product, 1, raw.pack, 6m, "وصفة صادرة من قسم الإنتاج معتمدة").Ok);
        var created = svc.Create(Setup(shifts: new[] { 1 }));
        int slot = svc.GetSlots(created.Id).Single().Id;
        Assert.True(svc.SaveSlot(created.Id, slot, new[] { Input(raw, 10) }).Ok);
        Assert.True(svc.CompleteSlot(created.Id, slot).Ok);
        var planning = Svc<IPlanningService>(h);
        var submitted = planning.SubmitPlan(created.Id);
        Assert.True(submitted.Ok, submitted.Message);
        var approved = planning.ApprovePlan(created.Id);
        Assert.True(approved.Ok, approved.Message);
        using var db = Fresh(h);
        Assert.True(db.ProductionPlans.Single(p => p.Id == created.Id).IsApproved);
        Assert.Equal(60, db.ProductionPlanItems.Single(i => i.WorkSlotId == slot).SourceQtyKg);
        Assert.Equal(new[] { "Setup", "SaveSlot", "Ready", "Submit", "Approve" },
            db.ProductionPlanRevisions.Where(r => r.PlanId == created.Id)
                .OrderBy(r => r.RevisionNo).Select(r => r.Action).ToArray());
        var issue = Svc<IProductionOrderService>(h).IssuePlanGroup(created.Id, "01/12/2026", 1, 1, 1);
        Assert.True(issue.Ok, issue.Message);
        Assert.Equal(db.ProductionPlanItems.Single(i => i.WorkSlotId == slot).Id,
            db.ProductionOrderItems.Single(i => i.OrderId == issue.Id).PlanItemId);
        Assert.False(svc.SaveSlot(created.Id, slot, new[] { Input(raw, 9) }, "تعديل بعد إصدار أمر إنتاج").Ok);
    }

    [Fact]
    public void Conflicting_Pack_Weight_Blocks_Planning_Then_Compatible_Spec_Is_Snapshotted()
    {
        using var h = new TestHost(); h.LoginAsAdmin();
        var raw = SeedReadyRaw(h);
        using (var db = Fresh(h))
        {
            db.Products.Single(p => p.Id == raw.product).CartonWeightKg = 7.5;
            db.SaveChanges();
        }
        var svc = Svc<SequencedPlanningService>(h);
        Assert.True(svc.SaveRawRule(raw.product, 1, raw.pack, 6m, "تعريف خام لعبوة خمسة كيلوجرامات").Ok);
        var created = svc.Create(Setup(shifts: new[] { 1 }));
        int slot = svc.GetSlots(created.Id).Single().Id;
        Assert.Contains("لا يطابق", svc.Quote(created.Id, slot, Input(raw, 1)).Error);
        Assert.False(svc.SaveSlot(created.Id, slot, new[] { Input(raw, 10) }).Ok);
        using (var db = Fresh(h))
        {
            db.Products.Single(p => p.Id == raw.product).CartonWeightKg = 5;
            db.SaveChanges();
        }
        Assert.Equal(5, svc.Quote(created.Id, slot, Input(raw, 1)).CartonWeightKg);
        Assert.True(svc.SaveSlot(created.Id, slot, new[] { Input(raw, 10) }).Ok);
        using var verified = Fresh(h);
        var item = verified.ProductionPlanItems.Single(i => i.WorkSlotId == slot);
        Assert.Equal(50, item.PlannedQtyKg);
        Assert.Equal(5, item.MoldsCount);
        Assert.Equal(1m, item.MoldWeightKg);
    }

    [Fact]
    public void Fractional_Kilogram_Recipe_Never_Rounds_To_Zero_Reservation()
    {
        using var h = new TestHost(); h.LoginAsAdmin();
        var raw = SeedReadyRaw(h, 0.1);
        var svc = Svc<SequencedPlanningService>(h);
        Assert.True(svc.SaveRawRule(raw.product, 1, raw.pack, 0.0001m, "تحويل مخبري صغير موثق بالكيلوجرام").Ok);
        var p = svc.Create(Setup(shifts: new[] { 1 }));
        int slot = svc.GetSlots(p.Id).Single().Id;
        Assert.True(svc.SaveSlot(p.Id, slot, new[] { Input(raw, 1) }).Ok);
        using var db = Fresh(h);
        Assert.Equal(0.0001, db.ProductionPlanItems.Single(i => i.WorkSlotId == slot).SourceQtyKg, 8);
        Assert.Equal(0.0001, db.Lots.Single(l => l.Id == raw.lot).ReservedQtyKg, 8);
    }

    [Fact]
    public void Revising_Earlier_Completed_Shift_Invalidates_Later_Ready_Shift_With_Audit_Entry()
    {
        using var h = new TestHost(); h.LoginAsAdmin();
        var raw = SeedReadyRaw(h);
        var svc = Svc<SequencedPlanningService>(h);
        Assert.True(svc.SaveRawRule(raw.product, 1, raw.pack, 6m, "وصفة فترة الوردية الأولى والثانية").Ok);
        var p = svc.Create(Setup());
        var slots = svc.GetSlots(p.Id);
        foreach (var slot in slots)
        {
            Assert.True(svc.SaveSlot(p.Id, slot.Id, new[] { Input(raw, 10) }).Ok);
            Assert.True(svc.CompleteSlot(p.Id, slot.Id).Ok);
        }
        int firstItem;
        using (var db = Fresh(h)) firstItem = db.ProductionPlanItems.Single(i => i.WorkSlotId == slots[0].Id).Id;
        Assert.True(svc.SaveSlot(p.Id, slots[0].Id, new[] { Input(raw, 9, firstItem) },
            "مراجعة أول وردية بعد تدقيق التوزيع").Ok);
        Assert.Equal("NeedsReview", svc.GetSlots(p.Id)[1].State);
        Assert.False(Svc<IPlanningService>(h).SubmitPlan(p.Id).Ok);
        Assert.True(svc.CompleteSlot(p.Id, slots[0].Id).Ok);
        Assert.True(svc.CompleteSlot(p.Id, slots[1].Id).Ok);
        Assert.True(Svc<IPlanningService>(h).SubmitPlan(p.Id).Ok);
        using var final = Fresh(h);
        Assert.Contains(final.ProductionPlanRevisions.Where(r => r.PlanId == p.Id),
            r => r.Action == "NeedsReview" && r.WorkSlotId == slots[1].Id);
    }

    [Fact]
    public void Changing_Earlier_Recipe_Invalidates_Different_Later_Product_And_Blocks_Submission()
    {
        using var h = new TestHost(); h.LoginAsAdmin();
        var raw = SeedReadyRaw(h);
        int second;
        using (var db = Fresh(h))
        {
            // A second finished product that draws from the SAME raw lot and the same pack.
            var other = db.Products.Single(p => p.ProductCode == "002-002");
            other.SourceProductId = db.Lots.Single(l => l.Id == raw.lot).ProductId;
            other.CartonWeightKg = db.PackagingTypes.Single(p => p.Id == raw.pack).UnitWeightKg;
            other.DefaultPackagingTypeId = raw.pack;
            db.SaveChanges(); second = other.Id;
        }
        var svc = Svc<SequencedPlanningService>(h);
        Assert.True(svc.SaveRawRule(raw.product, 1, raw.pack, 6m, "وصفة المنتج الأول لسحب الخام المشترك").Ok);
        Assert.True(svc.SaveRawRule(second, 1, raw.pack, 6m, "وصفة المنتج الثاني لسحب الخام المشترك").Ok);
        var p = svc.Create(Setup());
        Assert.True(p.Ok, p.Message);
        var slots = svc.GetSlots(p.Id);
        Assert.True(svc.SaveSlot(p.Id, slots[0].Id, new[] { Input(raw, 10) }).Ok);
        Assert.True(svc.CompleteSlot(p.Id, slots[0].Id).Ok);
        var saved = svc.SaveSlot(p.Id, slots[1].Id, new[] { Input((raw.lot, second, raw.pack), 10) });
        Assert.True(saved.Ok, saved.Message);
        Assert.True(svc.CompleteSlot(p.Id, slots[1].Id).Ok);
        // Only the FIRST product's recipe changes, yet the later card of a DIFFERENT product
        // shares the lot and the line capacity, so it must be re-checked too.
        Assert.True(svc.SaveRawRule(raw.product, 1, raw.pack, 7m, "مراجعة وصفة المنتج الأول بعد قياس الفاقد").Ok);
        var after = svc.GetSlots(p.Id);
        Assert.Equal("NeedsReview", after[0].State);
        Assert.Equal("NeedsReview", after[1].State);
        Assert.False(Svc<IPlanningService>(h).SubmitPlan(p.Id).Ok);
        int firstItem = Fresh(h).ProductionPlanItems.Single(i => i.WorkSlotId == slots[0].Id).Id;
        Assert.True(svc.SaveSlot(p.Id, slots[0].Id, new[] { Input(raw, 10, firstItem) },
            "إعادة احتساب البطاقة الأولى بالوصفة الجديدة").Ok);
        Assert.True(svc.CompleteSlot(p.Id, slots[0].Id).Ok);
        Assert.False(Svc<IPlanningService>(h).SubmitPlan(p.Id).Ok); // second card still unchecked
        Assert.True(svc.CompleteSlot(p.Id, slots[1].Id).Ok);
        var sent = Svc<IPlanningService>(h).SubmitPlan(p.Id);
        Assert.True(sent.Ok, sent.Message);
        using var final = Fresh(h);
        var revisions = final.ProductionPlanRevisions.Where(r => r.PlanId == p.Id).ToList();
        Assert.Contains(revisions, r => r.Action == "RecipeChanged" && r.WorkSlotId == slots[0].Id);
        Assert.Contains(revisions, r => r.Action == "NeedsReview" && r.WorkSlotId == slots[1].Id);
    }

    [Fact]
    public void Existing_Order_Reference_Protects_Card_Even_If_Order_Header_Lost_Its_SourcePlanId()
    {
        using var h = new TestHost(); h.LoginAsAdmin();
        var raw = SeedReadyRaw(h);
        var svc = Svc<SequencedPlanningService>(h);
        Assert.True(svc.SaveRawRule(raw.product, 1, raw.pack, 6m, "اعتماد خام لدفعة مرتبطة بأمر إنتاج").Ok);
        var p = svc.Create(Setup(shifts: new[] { 1 }));
        int slot = svc.GetSlots(p.Id).Single().Id;
        Assert.True(svc.SaveSlot(p.Id, slot, new[] { Input(raw, 10) }).Ok);
        int itemId;
        using (var db = Fresh(h))
        {
            itemId = db.ProductionPlanItems.Single(i => i.WorkSlotId == slot).Id;
            var order = new ProductionOrder { DocumentNumber = "SEQ-ORPHAN-REF", Status = DocStatuses.Draft,
                SourcePlanId = null, ProductionDate = new DateTime(2026,12,1), ShiftId = 1, LineId = 1 };
            db.ProductionOrders.Add(order); db.SaveChanges();
            db.ProductionOrderItems.Add(new ProductionOrderItem { OrderId = order.Id, PlanItemId = itemId,
                ProductId = raw.product, PlannedCartons = 10, PlannedQtyKg = 50 });
            db.SaveChanges();
        }
        var attempt = svc.SaveSlot(p.Id, slot, new[] { Input(raw, 9, itemId) }, "تغيير لا ينبغي تطبيقه بعد صدور أمر");
        Assert.False(attempt.Ok);
        Assert.Equal(10, Fresh(h).ProductionPlanItems.Single(i => i.Id == itemId).PlannedCartons);
    }

    [Fact]
    public void Expected_At_16_Oclock_Is_Not_Released_Raw_For_14_Oclock_Shift()
    {
        using var h = new TestHost(); h.LoginAsAdmin();
        var raw = SeedReadyRaw(h, 1000);
        using (var db = Fresh(h))
        {
            var shipment = new Shipment { DocumentNumber = "SEQ-TREAT", CustomerId = 1,
                Status = DocStatuses.Approved, ArrivalDate = new DateTime(2026, 11, 30) };
            db.Shipments.Add(shipment); db.SaveChanges();
            var line = new ShipmentItem { ShipmentId = shipment.Id, ProductId = 1,
                TreatmentRequired = true, TreatmentUntilDate = new DateTime(2026, 12, 1, 16, 0, 0),
                Status = DocStatuses.Approved, TotalWeightKg = 1000 };
            db.ShipmentItems.Add(line); db.SaveChanges();
            var lot = db.Lots.Single(l => l.Id == raw.lot);
            lot.ShipmentId = shipment.Id; lot.ShipmentItemId = line.Id;
            lot.UnderTreatmentQtyKg = 1000; lot.TreatmentReadyQtyKg = 0;
            db.SaveChanges();
        }
        var svc = Svc<SequencedPlanningService>(h);
        Assert.True(svc.SaveRawRule(raw.product, 1, raw.pack, 6m, "استعمال خام فقط بعد الإفراج الفعلي").Ok);
        var p = svc.Create(Setup(shifts: new[] { 2 }, days: 2));
        Assert.True(p.Ok, p.Message);
        var slots = svc.GetSlots(p.Id);
        Assert.Contains("تحت المعالجة", svc.Quote(p.Id, slots[0].Id, Input(raw, 1)).Error);
        Assert.Equal(0, svc.Quote(p.Id, slots[1].Id, Input(raw, 1)).MaximumCartons);
        using (var db = Fresh(h))
        {
            var lot = db.Lots.Single(l => l.Id == raw.lot);
            lot.UnderTreatmentQtyKg = 0; lot.TreatmentReadyQtyKg = 1000;
            db.SaveChanges();
        }
        Assert.True(svc.Quote(p.Id, slots[1].Id, Input(raw, 1)).MaximumCartons > 0);
    }

    [Fact]
    public void Linked_Order_Counts_Once_While_Independent_Order_Counts_Its_Real_Slot()
    {
        using var h = new TestHost(); h.LoginAsAdmin();
        var raw = SeedReadyRaw(h);
        using (var db = Fresh(h))
        {
            var historic = new ProductionPlan { DocumentNumber = "SEQ-LINKED-PLAN", PlanTitle = "سابق",
                ShiftId = 1, LineId = 1, Status = DocStatuses.Approved,
                StartDate = new DateTime(2026, 12, 1), EndDate = new DateTime(2026, 12, 1) };
            db.ProductionPlans.Add(historic); db.SaveChanges();
            var oldItem = new ProductionPlanItem { PlanId = historic.Id, SourceType = "Manual",
                ProductId = raw.product, PackagingTypeId = raw.pack,
                ScheduledDate = new DateTime(2026, 12, 1), SuggestedShiftId = 1, SuggestedLineId = 1,
                PlannedCartons = 100, PlannedQtyKg = 500 };
            db.ProductionPlanItems.Add(oldItem); db.SaveChanges();
            var order = new ProductionOrder { DocumentNumber = "SEQ-LINKED-ORDER", Status = DocStatuses.Approved,
                SourcePlanId = historic.Id, ShiftId = 1, LineId = 1, ProductionDate = new DateTime(2026, 12, 1) };
            db.ProductionOrders.Add(order); db.SaveChanges();
            db.ProductionOrderItems.Add(new ProductionOrderItem { OrderId = order.Id, PlanItemId = oldItem.Id,
                ProductId = raw.product, PackagingTypeId = raw.pack, PlannedCartons = 100, PlannedQtyKg = 500 });
            db.SaveChanges();
        }
        var svc = Svc<SequencedPlanningService>(h);
        Assert.True(svc.SaveRawRule(raw.product, 1, raw.pack, 6m, "وصفة المنتجات على نفس الخط والوردية").Ok);
        var p = svc.Create(Setup(shifts: new[] { 1 }));
        int slot = svc.GetSlots(p.Id).Single().Id;
        var quote = svc.Quote(p.Id, slot, Input(raw, 1));
        Assert.Equal(900, quote.LineMaximumCartons); // 500kg linked plan + order: once
        Assert.Equal(900, quote.MaximumCartons);
    }

    [Fact]
    public void Rescheduled_Linked_Order_Must_Consume_Time_In_Its_Actual_Shift()
    {
        using var h = new TestHost(); h.LoginAsAdmin();
        var raw = SeedReadyRaw(h);
        using (var db = Fresh(h))
        {
            var historic = new ProductionPlan { DocumentNumber = "SEQ-MOVED-PLAN", PlanTitle = "خطة سابقة بأمر منقول",
                ShiftId = 1, LineId = 1, Status = DocStatuses.Approved,
                StartDate = new DateTime(2026, 12, 1), EndDate = new DateTime(2026, 12, 1) };
            db.ProductionPlans.Add(historic); db.SaveChanges();
            var oldItem = new ProductionPlanItem { PlanId = historic.Id, SourceType = "Manual",
                ProductId = raw.product, PackagingTypeId = raw.pack,
                ScheduledDate = new DateTime(2026, 12, 1), SuggestedShiftId = 1, SuggestedLineId = 1,
                PlannedCartons = 100, PlannedQtyKg = 500 };
            db.ProductionPlanItems.Add(oldItem); db.SaveChanges();
            // The order still points at its plan item, but operations moved it to the NEXT day.
            var order = new ProductionOrder { DocumentNumber = "SEQ-MOVED-ORDER", Status = DocStatuses.Approved,
                SourcePlanId = historic.Id, ShiftId = 1, LineId = 1, ProductionDate = new DateTime(2026, 12, 2) };
            db.ProductionOrders.Add(order); db.SaveChanges();
            db.ProductionOrderItems.Add(new ProductionOrderItem { OrderId = order.Id, PlanItemId = oldItem.Id,
                ProductId = raw.product, PackagingTypeId = raw.pack, PlannedCartons = 100, PlannedQtyKg = 500 });
            db.SaveChanges();
        }
        var svc = Svc<SequencedPlanningService>(h);
        Assert.True(svc.SaveRawRule(raw.product, 1, raw.pack, 6m, "وصفة المنتج لاختبار أمر منقول لليوم التالي").Ok);
        var p = svc.Create(Setup(shifts: new[] { 1 }, days: 2));
        Assert.True(p.Ok, p.Message);
        var slots = svc.GetSlots(p.Id);
        var firstDay = svc.Quote(p.Id, slots[0].Id, Input(raw, 1));  // the plan item occupies this day
        var secondDay = svc.Quote(p.Id, slots[1].Id, Input(raw, 1)); // the moved order occupies this day
        Assert.Null(firstDay.Error);
        Assert.Null(secondDay.Error);
        // 100 cartons at 500/hour = 0.2 h in each day, counted exactly once per real slot.
        Assert.Equal(3900, firstDay.TimeMaximumCartons);
        Assert.Equal(3900, secondDay.TimeMaximumCartons);
        Assert.Equal(900, firstDay.LineMaximumCartons);
        Assert.Equal(900, secondDay.LineMaximumCartons);

        // Order-issuance path (ValidateSourceCapacity): the WHOLE approved source plan is the
        // draft and its own linked orders are covered only in the slot where their plan item
        // really is. A moved order must still occupy its real day (it once vanished: 4,000).
        int sourcePlanId = Fresh(h).ProductionPlans.Single(x => x.DocumentNumber == "SEQ-MOVED-PLAN").Id;
        var draft = new List<PlanItemDto>
        {
            new() { ProductId = raw.product, PackagingTypeId = raw.pack, PlannedCartons = 100, PlannedQtyKg = 500,
                ScheduledDate = "01/12/2026", SuggestedShiftId = 1, SuggestedLineId = 1 },
            new() { ProductId = raw.product, PackagingTypeId = raw.pack, PlannedCartons = 1, PlannedQtyKg = 5,
                ScheduledDate = "02/12/2026", SuggestedShiftId = 1, SuggestedLineId = 1 },
        };
        PlanCapacityResult Issuance()
        {
            using var db = Fresh(h);
            return new PlanningCapacityEvaluator(db).Evaluate(draft, sourcePlanId, "01/12/2026", "02/12/2026",
                ordersOfExcludedPlanCoveredByDraft: true);
        }
        var issuance = Issuance();
        Assert.Null(issuance.Error);
        Assert.Equal(4000, issuance.Rows[0].MaximumCartons); // day 1: only the draft itself
        Assert.Equal(3900, issuance.Rows[1].MaximumCartons); // day 2: the moved order takes 0.2 h
        // An UNMOVED order of the same plan item is covered by the draft: counted once, never twice.
        using (var db = Fresh(h))
        {
            var itemX = db.ProductionPlanItems.Single(i => i.PlanId == sourcePlanId);
            var unmoved = new ProductionOrder { DocumentNumber = "SEQ-UNMOVED-ORDER", Status = DocStatuses.Approved,
                SourcePlanId = sourcePlanId, ShiftId = 1, LineId = 1, ProductionDate = new DateTime(2026, 12, 1) };
            db.ProductionOrders.Add(unmoved); db.SaveChanges();
            db.ProductionOrderItems.Add(new ProductionOrderItem { OrderId = unmoved.Id, PlanItemId = itemX.Id,
                ProductId = raw.product, PackagingTypeId = raw.pack, PlannedCartons = 100, PlannedQtyKg = 500 });
            db.SaveChanges();
        }
        issuance = Issuance();
        Assert.Equal(4000, issuance.Rows[0].MaximumCartons);
        Assert.Equal(3900, issuance.Rows[1].MaximumCartons);
    }

    [Fact]
    public void Changed_Raw_Rule_Requires_Rechecking_A_Ready_Card_Before_Approval()
    {
        using var h = new TestHost(); h.LoginAsAdmin();
        var raw = SeedReadyRaw(h);
        var svc = Svc<SequencedPlanningService>(h);
        Assert.True(svc.SaveRawRule(raw.product, 1, raw.pack, 6m, "وصفة أولى للخام حسب دفعات العميل").Ok);
        var p = svc.Create(Setup(shifts: new[] { 1 }));
        int slot = svc.GetSlots(p.Id).Single().Id;
        Assert.True(svc.SaveSlot(p.Id, slot, new[] { Input(raw, 10) }).Ok);
        Assert.True(svc.CompleteSlot(p.Id, slot).Ok);
        var revision = svc.SaveRawRule(raw.product, 1, raw.pack, 7m, "الوصفة الجديدة بعد اختبار الإشغال");
        Assert.True(revision.Ok, revision.Message);
        Assert.Equal("NeedsReview", svc.GetSlots(p.Id).Single().State);
        Assert.False(Svc<IPlanningService>(h).ApprovePlan(p.Id).Ok);
        using var db = Fresh(h);
        var item = db.ProductionPlanItems.Single(i => i.WorkSlotId == slot);
        Assert.True(svc.SaveSlot(p.Id, slot, new[] { Input(raw, 10, item.Id) }, "إعادة احتساب الخام من الوصفة المعدلة").Ok);
        Assert.True(svc.CompleteSlot(p.Id, slot).Ok);
        Assert.Equal(70, Fresh(h).ProductionPlanItems.Single(i => i.WorkSlotId == slot).SourceQtyKg);
    }
}
