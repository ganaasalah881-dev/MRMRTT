using System.Data;
using System.Globalization;
using System.Text.Json;
using DatesErp.Core.Common;
using DatesErp.Core.Domain.Entities;
using DatesErp.Core.Exceptions;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Application.Services;

/// <summary>
/// New, opt-in production-plan workflow. Existing plans (without a context row) remain
/// readable on the historic workflow; a new plan has a frozen customer/shift selection
/// and one independently validated card per business day and selected shift.
/// </summary>
public sealed class SequencedPlanningService : ServiceBase
{
    public SequencedPlanningService(DatesErpDbContext db, ICurrentSession session, INumberingService numbering)
        : base(db, session, numbering) { }

    protected override IsolationLevel TransactionIsolation => IsolationLevel.Serializable;

    public OpResult Create(SequencedPlanSetup setup)
    {
        Require("planning", "Create");
        if (setup == null) return OpResult.Fail("حدد نطاق خطة الإنتاج أولاً.");
        var customers = (setup.CustomerIds ?? new()).Distinct().ToList();
        var shifts = setup.ShiftIds ?? new();
        if (setup.CustomerMode is not ("Single" or "SelectedMulti" or "Fair")
            || customers.Count == 0 || (setup.CustomerMode == "Single" ? customers.Count != 1 : customers.Count < 2))
            return OpResult.Fail("اختر عميلًا واحدًا أو مجموعة من عميلين على الأقل وفق نطاق الخطة.");
        if (shifts.Count is < 1 or > 2 || shifts.Distinct().Count() != shifts.Count)
            return OpResult.Fail("اختر وردية واحدة أو ورديتين مختلفتين بالترتيب من الإعداد.");
        if (setup.StartDate == default || setup.EndDate.Date < setup.StartDate.Date
            || (setup.EndDate.Date - setup.StartDate.Date).TotalDays > 365)
            return OpResult.Fail("اختر يومًا أو فترة صحيحة لا تتجاوز 366 يومًا.");
        if (string.IsNullOrWhiteSpace(setup.Title)) return OpResult.Fail("أدخل عنوان الخطة.");
        return RunOp(() =>
        {
            if (Db.Customers.AsNoTracking().Count(c => customers.Contains(c.Id) && c.IsActive) != customers.Count)
                throw new DomainException("أحد العملاء المختارين غير موجود أو موقوف.");
            var selected = Db.Shifts.AsNoTracking().Where(s => shifts.Contains(s.Id) && s.IsActive).ToList();
            var firstTwo = Db.Shifts.AsNoTracking().Where(s => s.IsActive).ToList()
                .OrderBy(s => TimeSpan.TryParse(s.StartTime, CultureInfo.InvariantCulture, out var start)
                    ? start : TimeSpan.MaxValue).Take(2).Select(s => s.Id).ToList();
            if (selected.Count != shifts.Count || shifts.Any(id => !firstTwo.Contains(id))
                || (shifts.Count == 2 && !shifts.SequenceEqual(firstTwo))
                || selected.Any(s => !TimeSpan.TryParse(s.StartTime, CultureInfo.InvariantCulture, out _)
                    || CapacityPolicy.EffectiveHours(s.EffectiveProductiveHours, s.TotalHours, s.PlannedDowntimeHours) <= 0))
                throw new DomainException("اختر الوردية الأولى و/أو الثانية حسب وقت البدء من التعريفات النشطة وبترتيبهما التشغيلي.");
            var line = Db.ProductionLines.AsNoTracking().FirstOrDefault(l => l.Id == setup.LineId && l.IsActive);
            if (line == null || line.CapacityPerShift <= 0)
                throw new DomainException("عرّف خط إنتاج نشطًا بسقف كيلوجرامات للوردية قبل إعداد الخطة.");
            var plan = new ProductionPlan
            {
                DocumentNumber = Numbering.Next("PLAN"),
                PlanTitle = setup.Title.Trim(),
                PlanType = setup.StartDate.Date == setup.EndDate.Date ? "Daily" : "Period",
                ScopeMode = setup.CustomerMode == "Single" ? "Single" : "Multi",
                SingleCustomerId = setup.CustomerMode == "Single" ? customers[0] : null,
                StartDate = setup.StartDate.Date, EndDate = setup.EndDate.Date,
                ShiftId = shifts[0], LineId = line.Id, Status = DocStatuses.Draft,
                CreatedBy = Session?.UserId
            };
            Db.ProductionPlans.Add(plan);
            Db.SaveChanges(); // the document number and all child rows remain inside this transaction
            Db.ProductionPlanContexts.Add(new ProductionPlanContext
                { PlanId = plan.Id, CustomerMode = setup.CustomerMode, LineId = line.Id });
            Db.ProductionPlanScopeCustomers.AddRange(customers.Select(id => new ProductionPlanScopeCustomer
                { PlanId = plan.Id, CustomerId = id }));
            Db.ProductionPlanSelectedShifts.AddRange(shifts.Select((id, index) => new ProductionPlanSelectedShift
                { PlanId = plan.Id, ShiftId = id, SequenceNo = index }));
            int sequence = 0;
            for (var day = setup.StartDate.Date; day <= setup.EndDate.Date; day = day.AddDays(1))
                foreach (var shiftId in shifts)
                    Db.ProductionPlanWorkSlots.Add(new ProductionPlanWorkSlot
                    {
                        PlanId = plan.Id, WorkDate = day, ShiftId = shiftId,
                        SequenceNo = sequence, State = sequence++ == 0 ? "Editing" : "Pending"
                    });
            Db.SaveChanges();
            AddRevision(plan.Id, null, "Setup", "إعداد الخطة قبل اختيار الأصناف", null, setup);
            return OpResult.Success("تم تثبيت نطاق العملاء والفترة والورديات. ابدأ باليوم والوردية الأولى.", plan.Id, plan.DocumentNumber);
        });
    }

    /// <summary>Managers define an explicit raw-input rule. YieldFactor is NEVER an implicit recipe.</summary>
    public OpResult SaveRawRule(int productId, int rawProductId, int packagingTypeId, decimal rawKgPerCarton, string reason)
    {
        Require("products", "Edit");
        Require("products", "Approve"); // editing master data alone is NOT recipe approval
        if (rawKgPerCarton <= 0 || rawKgPerCarton > 100000 || packagingTypeId < 0)
            return OpResult.Fail("عرّف كمية خام موجبة ومعقولة بالكيلو لكل كرتون.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length is < 10 or > 500)
            return OpResult.Fail("أدخل سبب اعتماد/تغيير وصفة الخام (10–500 حرف).");
        return RunOp(() =>
        {
            var product = Db.Products.AsNoTracking().FirstOrDefault(p => p.Id == productId && p.IsActive && p.ItemType == "Finished");
            if (product == null || product.SourceProductId != rawProductId
                || !Db.Products.AsNoTracking().Any(p => p.Id == rawProductId && p.IsActive && p.ItemType == "Raw"))
                throw new DomainException("الصنف التام يجب أن يكون مرتبطًا بهذا الصنف الخام النشط في بطاقة الأصناف.");
            if (packagingTypeId > 0 && !Db.PackagingTypes.AsNoTracking().Any(p => p.Id == packagingTypeId && p.IsActive))
                throw new DomainException("العبوة المحددة غير نشطة أو غير موجودة.");
            var rule = Db.ProductRawPlanningRules.FirstOrDefault(r => r.ProductId == productId
                && r.RawProductId == rawProductId && r.PackagingTypeId == packagingTypeId);
            decimal? previousRate = rule?.RawKgPerCarton;
            if (rule == null)
            {
                rule = new ProductRawPlanningRule { ProductId = productId, RawProductId = rawProductId,
                    PackagingTypeId = packagingTypeId };
                Db.ProductRawPlanningRules.Add(rule);
            }
            rule.RawKgPerCarton = rawKgPerCarton;
            rule.IsActive = true;
            rule.ApprovedBy = Session?.UserId;
            rule.ApprovedAt = Db.BusinessNow;
            rule.ApprovalReason = reason.Trim();
            Db.SaveChanges();
            // Existing unapproved slots referring to this recipe must be checked again
            // before approval; executed/approved plans are historical and never rewritten.
            var affected = Db.ProductionPlanItems.AsNoTracking()
                .Where(i => i.ProductId == productId && i.LotId != null
                    && (packagingTypeId == 0 || i.PackagingTypeId == packagingTypeId))
                .Select(i => i.WorkSlotId).Where(id => id != null).Distinct().ToList();
            var affectedIds = affected.Select(id => id!.Value).ToHashSet();
            var firstAffected = Db.ProductionPlanWorkSlots.AsNoTracking()
                .Where(s => affected.Contains(s.Id) && s.State == "Ready")
                .Join(Db.ProductionPlans.AsNoTracking(), s => s.PlanId, p => p.Id, (s, p) => new { s, p })
                .Where(x => !x.p.IsApproved && !x.p.IsClosed && x.p.Status != DocStatuses.Cancelled)
                .ToList().GroupBy(x => x.s.PlanId)
                .ToDictionary(g => g.Key, g => g.Min(x => x.s.SequenceNo));
            foreach (var (affectedPlanId, firstSequence) in firstAffected)
            {
                // A changed recipe in an earlier card also invalidates later cards
                // of OTHER products: both may draw the same lot/line capacity.
                foreach (var slot in Db.ProductionPlanWorkSlots.Where(s => s.PlanId == affectedPlanId
                    && s.SequenceNo >= firstSequence && s.State == "Ready"))
                {
                    slot.State = "NeedsReview";
                    slot.CompletedAt = null; slot.CompletedBy = null;
                    bool direct = affectedIds.Contains(slot.Id);
                    AddRevision(slot.PlanId, slot.Id, direct ? "RecipeChanged" : "NeedsReview",
                        reason.Trim(), direct ? (object)new { Rate = previousRate, State = "Ready" } : new { State = "Ready" },
                        direct ? (object)new { Rate = rawKgPerCarton, State = slot.State } : new { State = slot.State });
                }
            }
            return OpResult.Success("تم اعتماد سحب الخام للكرتون؛ ستراجع البطاقات المتأثرة قبل الاعتماد.", rule.Id);
        });
    }

    public IReadOnlyList<ProductionPlanWorkSlot> GetSlots(int planId)
    {
        Require("planning", "View");
        return Db.ProductionPlanWorkSlots.AsNoTracking().Where(s => s.PlanId == planId)
            .OrderBy(s => s.SequenceNo).ToList();
    }

    public IReadOnlyList<SequencedLotOption> GetEligibleLots(int planId, int slotId)
    {
        Require("planning", "View");
        var slot = Db.ProductionPlanWorkSlots.AsNoTracking().FirstOrDefault(s => s.Id == slotId && s.PlanId == planId)
            ?? throw new DomainException("بطاقة اليوم/الوردية غير موجودة.");
        var customers = Db.ProductionPlanScopeCustomers.AsNoTracking().Where(c => c.PlanId == planId)
            .Select(c => c.CustomerId).ToList();
        var customerNames = Db.Customers.AsNoTracking().Where(c => customers.Contains(c.Id))
            .ToDictionary(c => c.Id, c => c.CustomerName);
        // Filter at SQL Server, not after loading every factory/customer lot into UI.
        var lots = Db.Lots.AsNoTracking().Where(l => l.Status == DocStatuses.Approved && l.InStockQtyKg > 0
            && ((l.CustomerId != null && customers.Contains(l.CustomerId.Value))
                || (l.CustomerId == null && l.ShipmentId != null
                    && Db.Shipments.AsNoTracking().Any(s => s.Id == l.ShipmentId && customers.Contains(s.CustomerId)))))
            .ToList();
        var rawIds = lots.Select(l => l.ProductId).Distinct().ToList();
        var products = Db.Products.AsNoTracking().Where(p => p.IsActive && p.ItemType == "Finished"
            && p.SourceProductId != null && rawIds.Contains(p.SourceProductId.Value)).ToList();
        var packIds = products.Where(p => p.DefaultPackagingTypeId != null)
            .Select(p => p.DefaultPackagingTypeId!.Value)
            .Concat(Db.ProductRawPlanningRules.AsNoTracking().Where(r => r.IsActive && r.PackagingTypeId > 0)
                .Select(r => r.PackagingTypeId))
            .Distinct().ToList();
        var packs = Db.PackagingTypes.AsNoTracking().Where(p => packIds.Contains(p.Id) && p.IsActive)
            .ToDictionary(p => p.Id);
        var configuredPacks = Db.ProductRawPlanningRules.AsNoTracking().Where(r => r.IsActive && r.PackagingTypeId > 0)
            .Select(r => new { r.ProductId, r.RawProductId, r.PackagingTypeId }).ToList();
        var shipmentIds = lots.Where(l => l.ShipmentId != null).Select(l => l.ShipmentId!.Value).Distinct().ToList();
        var shipments = Db.Shipments.AsNoTracking().Where(s => shipmentIds.Contains(s.Id))
            .ToDictionary(s => s.Id);
        var result = new List<SequencedLotOption>();
        foreach (var lot in lots)
        {
            int? owner = lot.CustomerId ?? (lot.ShipmentId is int shipId && shipments.TryGetValue(shipId, out var sh) ? sh.CustomerId : null);
            if (owner is not int customerId || !customers.Contains(customerId)) continue;
            foreach (var product in products.Where(p => p.SourceProductId == lot.ProductId))
            foreach (var packId in configuredPacks.Where(x => x.ProductId == product.Id && x.RawProductId == lot.ProductId)
                .Select(x => x.PackagingTypeId).Append(product.DefaultPackagingTypeId ?? 0).Where(id => id > 0).Distinct())
            {
                if (!packs.TryGetValue(packId, out var pack)) continue;
                var quote = Quote(planId, slot.Id, new SequencedPlanItemInput
                    { LotId = lot.Id, ProductId = product.Id, PackagingTypeId = packId, Cartons = 1 });
                var (molds, moldWeight) = UnitsPolicy.PackagingDefinition(Db, product.Id, packId);
                result.Add(new SequencedLotOption
                {
                    LotId = lot.Id, CustomerId = customerId,
                    CustomerName = customerNames.TryGetValue(customerId, out var name) ? name : $"#{customerId}",
                    LotCode = lot.LotCode, ArrivalDate = lot.ShipmentId is int sid && shipments.TryGetValue(sid, out var ship)
                        ? ship.ArrivalDate : null,
                    ProductId = product.Id, ProductName = product.ProductNameAr,
                    PackagingTypeId = packId, PackagingName = pack.PackageNameAr,
                    CartonWeightKg = quote.CartonWeightKg,
                    MoldsCount = molds, MoldWeightKg = double.IsFinite(moldWeight) && moldWeight >= 0 && moldWeight <= 100000
                        ? Convert.ToDecimal(moldWeight) : 0m,
                    RawKgPerCarton = quote.RawKgPerCarton,
                    MaximumCartons = quote.MaximumCartons,
                    TimeMaximumCartons = quote.TimeMaximumCartons,
                    LineMaximumCartons = quote.LineMaximumCartons,
                    Reason = quote.Error ?? quote.LimitingReason
                });
            }
        }
        return result.OrderBy(o => o.ArrivalDate ?? DateTime.MaxValue).ThenBy(o => o.CustomerId).ToList();
    }

    /// <summary>FIFO, equal first-pass line/time share across eligible customers;
    /// proposal only. A customer with no released raw material is reported, not invented.</summary>
    public SequencedDistributionProposal SuggestFairSlot(int planId, int slotId)
    {
        Require("planning", "View");
        var context = Db.ProductionPlanContexts.AsNoTracking().FirstOrDefault(c => c.PlanId == planId);
        if (context?.CustomerMode != "Fair")
            throw new DomainException("اقتراح التوزيع متاح لنطاق «توزيع عادل» فقط.");
        if (Db.ProductionPlanItems.AsNoTracking().Any(i => i.WorkSlotId == slotId))
            throw new DomainException("احفظ/امسح البنود الموجودة قبل توليد اقتراح بديل؛ لن تمحى البنود آليًا.");
        var proposal = new SequencedDistributionProposal();
        var eligible = GetEligibleLots(planId, slotId).Where(o => o.MaximumCartons > 0).ToList();
        var selected = Db.ProductionPlanScopeCustomers.AsNoTracking().Where(c => c.PlanId == planId)
            .OrderBy(c => c.Id).Select(c => c.CustomerId).ToList();
        foreach (var (customer, index) in selected.Select((id, index) => (id, index)))
        {
            int remaining = selected.Count - index;
            var offer = eligible.FirstOrDefault(o => o.CustomerId == customer);
            if (offer == null)
            {
                proposal.Warnings.Add($"العميل #{customer}: لا يوجد خام مؤكد ووصفة وطاقة صالحة لهذه الوردية.");
                continue;
            }
            var input = new SequencedPlanItemInput { LotId = offer.LotId, ProductId = offer.ProductId,
                PackagingTypeId = offer.PackagingTypeId, Cartons = 1 };
            var quote = Quote(planId, slotId, input, proposal.Items);
            long shared = Math.Min(quote.RawMaximumCartons,
                Math.Min(quote.LineMaximumCartons / remaining,
                    Math.Min(quote.TimeMaximumCartons / remaining, quote.ProductShiftMaximumCartons / remaining)));
            if (!quote.IsValid || shared <= 0)
            {
                proposal.Warnings.Add($"العميل {offer.CustomerName}: {quote.Error ?? "لا توجد حصة تشغيل متبقية"}.");
                continue;
            }
            input.Cartons = (int)Math.Min(int.MaxValue, shared);
            proposal.Items.Add(input);
        }
        return proposal;
    }

    /// <summary>Maximum for this item given every OTHER draft row in the same card.
    /// A null otherRows reads the saved card; an empty list deliberately replaces it.</summary>
    public SequencedQuantityQuote Quote(int planId, int slotId, SequencedPlanItemInput input,
        IReadOnlyList<SequencedPlanItemInput> otherRows = null)
    {
        Require("planning", "View");
        var result = new SequencedQuantityQuote { LotId = input?.LotId ?? 0, ProductId = input?.ProductId ?? 0 };
        try
        {
            if (input == null || input.LotId <= 0 || input.ProductId <= 0 || input.PackagingTypeId <= 0)
                throw new DomainException("حدد دفعة وصنفًا تامًا وعبوة معرفة أولًا.");
            var context = Db.ProductionPlanContexts.AsNoTracking().FirstOrDefault(c => c.PlanId == planId)
                ?? throw new DomainException("هذه الخطة ليست ضمن مسار التخطيط المتسلسل.");
            var slot = Db.ProductionPlanWorkSlots.AsNoTracking().FirstOrDefault(s => s.Id == slotId && s.PlanId == planId)
                ?? throw new DomainException("بطاقة اليوم/الوردية غير موجودة.");
            if (!Db.ProductionPlanSelectedShifts.AsNoTracking().Any(s => s.PlanId == planId && s.ShiftId == slot.ShiftId)
                || !Db.ProductionLines.AsNoTracking().Any(l => l.Id == context.LineId && l.IsActive))
                throw new DomainException("الوردية أو خط التشغيل ليسا ضمن إعداد الخطة.");
            var lot = Db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == input.LotId && l.Status == DocStatuses.Approved)
                ?? throw new DomainException("الدفعة ليست معتمدة أو غير موجودة.");
            var owner = lot.CustomerId ?? (lot.ShipmentId is int shipmentId
                ? Db.Shipments.AsNoTracking().Where(s => s.Id == shipmentId).Select(s => s.CustomerId).FirstOrDefault() : null);
            if (owner == null || !Db.ProductionPlanScopeCustomers.AsNoTracking()
                .Any(c => c.PlanId == planId && c.CustomerId == owner.Value))
                throw new DomainException("الدفعة لا تخص أحد العملاء المختارين في إعداد الخطة.");
            var product = Db.Products.AsNoTracking().FirstOrDefault(p => p.Id == input.ProductId && p.IsActive && p.ItemType == "Finished")
                ?? throw new DomainException("اختر صنفًا تامًا نشطًا.");
            if (product.SourceProductId != lot.ProductId)
                throw new DomainException("المنتج التام غير مسموح من خام هذه الدفعة.");
            if (product.UnitOfMeasure != UnitsPolicy.UnitCarton)
                throw new DomainException("وحدة المنتج التام ليست كرتونًا؛ صحّح وحدة الصنف قبل احتساب الطاقة والوزن.");
            var pack = Db.PackagingTypes.AsNoTracking().FirstOrDefault(p => p.Id == input.PackagingTypeId && p.IsActive)
                ?? throw new DomainException("العبوة غير موجودة أو موقوفة.");
            double weight = UnitsPolicy.CartonWeight(Db, input.ProductId, input.PackagingTypeId);
            if (!double.IsFinite(weight) || weight <= 0)
                throw new DomainException("وزن الكرتون غير معرف في العبوة/بطاقة الصنف.");
            // Reject conflicting product/pack weights rather than silently using a
            // different number in the UI and the persisted/issued calculation.
            if (product.CartonWeightKg > 0 && pack.UnitWeightKg > 0
                && Math.Abs(product.CartonWeightKg - pack.UnitWeightKg) > Math.Max(0.01, product.CartonWeightKg * 0.001))
                throw new DomainException("وزن كرتون العبوة لا يطابق بطاقة الصنف؛ صحّح التعريف قبل التخطيط.");
            if (pack.UnitWeightKg > 0 && pack.MoldsCount > 0 && pack.MoldWeightKg > 0
                && Math.Abs(pack.UnitWeightKg - pack.MoldsCount * pack.MoldWeightKg) > Math.Max(0.01, pack.UnitWeightKg * 0.001))
                throw new DomainException("وزن العبوة لا يساوي عدد القوالب × وزن القالب في تعريف العبوة؛ صحّح المواصفات.");
            result.CartonWeightKg = weight;
            var (moldCount, moldKg) = UnitsPolicy.PackagingDefinition(Db, input.ProductId, input.PackagingTypeId);
            if (moldCount < 0 || moldCount > 1000000 || !double.IsFinite(moldKg)
                || moldKg < 0 || moldKg > 100000)
                throw new DomainException("مواصفات القوالب لهذا الصنف/العبوة غير صالحة.");
            var shift = Db.Shifts.AsNoTracking().FirstOrDefault(s => s.Id == slot.ShiftId && s.IsActive)
                ?? throw new DomainException("الوردية المختارة متوقفة.");
            if (!TimeSpan.TryParse(shift.StartTime, CultureInfo.InvariantCulture, out var start))
                throw new DomainException("وقت بدء الوردية غير صالح.");
            var when = slot.WorkDate.Date.Add(start);
            var shipmentItem = lot.ShipmentItemId is int shipmentItemId
                ? Db.ShipmentItems.AsNoTracking().FirstOrDefault(i => i.Id == shipmentItemId) : null;
            if (shipmentItem?.TreatmentRequired == true && shipmentItem.TreatmentUntilDate > when
                && lot.InStockQtyKg - lot.UnderTreatmentQtyKg <= 0.001)
                throw new DomainException($"الدفعة تحت المعالجة إلى {shipmentItem.TreatmentUntilDate:dd/MM/yyyy HH:mm}؛ لا خام مفرجًا عنه لبداية هذه الوردية.");
            var inProgress = Db.RawTreatments.AsNoTracking().Where(t => t.LotId == lot.Id && t.Status == TreatmentStatuses.InProgress)
                .ToList();
            if (inProgress.Any(t => t.ExpectedReadyAt > when && t.ReleasedQtyKg <= 0)
                && lot.InStockQtyKg - lot.UnderTreatmentQtyKg <= 0.001)
                throw new DomainException("الخام المتوقع جاهزيته لاحقًا ليس خامًا مفرجًا عنه لبداية الوردية.");

            var existingSlot = Db.ProductionPlanItems.AsNoTracking().Where(i => i.WorkSlotId == slotId)
                .Select(i => new SequencedPlanItemInput { ItemId = i.Id, LotId = i.LotId ?? 0,
                    ProductId = i.ProductId, PackagingTypeId = i.PackagingTypeId ?? 0, Cartons = i.PlannedCartons }).ToList();
            var peers = otherRows ?? existingSlot.Where(i => i.ItemId != input.ItemId).ToList();
            if (peers.Any(p => p.LotId <= 0 || p.ProductId <= 0 || p.Cartons <= 0))
                throw new DomainException("أكمل بيانات جميع البنود الأخرى في الوردية أولًا.");
            // Capacity is per ONE (day, shift, line). Other cards use other slots;
            // their raw reservation is subtracted separately below. Evaluating an
            // entire 366-day period for every picker row is wasteful and could let
            // an unrelated future card hide this card's actual remaining hours.
            var combined = new List<PlanItemDto>();
            foreach (var peer in peers)
            {
                var peerWeight = UnitsPolicy.CartonWeight(Db, peer.ProductId, peer.PackagingTypeId);
                combined.Add(new PlanItemDto { ProductId = peer.ProductId, PackagingTypeId = peer.PackagingTypeId,
                    PlannedCartons = peer.Cartons, PlannedQtyKg = peer.Cartons * peerWeight,
                    ScheduledDate = slot.WorkDate.ToString("dd/MM/yyyy"), SuggestedShiftId = slot.ShiftId,
                    SuggestedLineId = context.LineId });
            }
            combined.Add(new PlanItemDto { ProductId = input.ProductId, PackagingTypeId = input.PackagingTypeId,
                PlannedCartons = 1, PlannedQtyKg = weight, ScheduledDate = slot.WorkDate.ToString("dd/MM/yyyy"),
                SuggestedShiftId = slot.ShiftId, SuggestedLineId = context.LineId });
            var scheduled = slot.WorkDate.ToString("dd/MM/yyyy");
            var evaluation = new PlanningCapacityEvaluator(Db).Evaluate(combined, planId,
                scheduled, scheduled, slot.ShiftId, context.LineId);
            var capacityRow = evaluation.Rows.Last();
            if (capacityRow.Error != null || evaluation.Error != null)
                throw new DomainException(capacityRow.Error ?? evaluation.Error!);
            result.RatePerHour = capacityRow.Rate;
            result.TimeMaximumCartons = Math.Max(0, capacityRow.MaximumCartons);
            var existingOthers = ActivePlanItemsExcept(planId, slotId, slot.WorkDate, slot.ShiftId, context.LineId);
            // Other cards of this plan are also booked in the same resource, if present.
            var standalone = StandaloneOrderItems(slot.WorkDate, slot.ShiftId, context.LineId,
                existingOthers.Select(i => i.Id).ToHashSet());
            var line = Db.ProductionLines.AsNoTracking().First(l => l.Id == context.LineId);
            if (line.CapacityPerShift <= 0) throw new DomainException("حد الخط بالكيلو غير معرف.");
            double peerKg = peers.Sum(p => p.Cartons * UnitsPolicy.CartonWeight(Db, p.ProductId, p.PackagingTypeId));
            double lineUsed = existingOthers.Sum(i => i.PlannedQtyKg) + standalone.Sum(i => i.PlannedQtyKg) + peerKg;
            result.LineMaximumCartons = Math.Max(0, (long)Math.Floor((line.CapacityPerShift - lineUsed) / weight + 1e-7));
            long sameProduct = UsedProductCartons(input.ProductId, slot.WorkDate, slot.ShiftId, slotId)
                + peers.Where(i => i.ProductId == input.ProductId).Sum(i => (long)i.Cartons);
            var (_, shiftCap, _) = CapacityPolicy.Resolve(Db, input.ProductId, slot.ShiftId, input.PackagingTypeId);
            result.ProductShiftMaximumCartons = Math.Max(0, shiftCap - sameProduct);
            // Time/line caps are still explainable when the raw recipe is missing;
            // the FINAL cap must remain unavailable until an approved raw draw exists.
            var rule = Db.ProductRawPlanningRules.AsNoTracking().Where(r => r.IsActive && r.ProductId == input.ProductId
                    && r.RawProductId == lot.ProductId && (r.PackagingTypeId == input.PackagingTypeId || r.PackagingTypeId == 0))
                .OrderByDescending(r => r.PackagingTypeId).FirstOrDefault();
            if (rule == null || rule.RawKgPerCarton <= 0 || rule.ApprovedBy == null)
                throw new DomainException("سحب الخام للكرتون غير معتمد لهذا الصنف/العبوة؛ عرّفه من إعداد الوصفات أولًا. سقف الوقت/الخط استرشادي فقط وليس سقفًا نهائيًا.");
            result.RawKgPerCarton = rule.RawKgPerCarton;
            double rawUsedHere = 0;
            foreach (var peer in peers.Where(p => p.LotId == lot.Id))
            {
                var peerRule = Db.ProductRawPlanningRules.AsNoTracking().Where(r => r.IsActive && r.ProductId == peer.ProductId
                    && r.RawProductId == lot.ProductId && (r.PackagingTypeId == peer.PackagingTypeId || r.PackagingTypeId == 0))
                    .OrderByDescending(r => r.PackagingTypeId).FirstOrDefault();
                if (peerRule == null || peerRule.RawKgPerCarton <= 0)
                    throw new DomainException("وصفة خام بند آخر من نفس الدفعة مفقودة؛ أكملها أولًا.");
                rawUsedHere += peer.Cartons * (double)peerRule.RawKgPerCarton;
            }
            double availableRaw = AvailableRawForPlan(lot, planId, slotId) - rawUsedHere;
            result.RawMaximumCartons = Math.Max(0, (long)Math.Floor(availableRaw / (double)rule.RawKgPerCarton + 1e-7));
            result.MaximumCartons = Math.Max(0, new[] { result.TimeMaximumCartons, result.LineMaximumCartons,
                result.ProductShiftMaximumCartons, result.RawMaximumCartons }.Min());
            result.LimitingReason = result.MaximumCartons == result.RawMaximumCartons ? "رصيد الخام المفرج عنه"
                : result.MaximumCartons == result.LineMaximumCartons ? "سقف وزن خط الإنتاج"
                : result.MaximumCartons == result.ProductShiftMaximumCartons ? "طاقة الصنف للوردية"
                : "الساعات المتاحة بعد الإشغال";
            if (result.MaximumCartons < 1)
                result.Error = "لا توجد كمية صالحة في هذه الوردية؛ راجع " + result.LimitingReason + ".";
        }
        catch (Exception ex) when (ex is DomainException or InvalidOperationException or OverflowException)
        { result.Error = ex.Message; }
        return result;
    }

    public OpResult SaveSlot(int planId, int slotId, IReadOnlyList<SequencedPlanItemInput> items, string reason = null)
    {
        Require("planning", "Edit");
        items ??= Array.Empty<SequencedPlanItemInput>();
        return RunOp(() =>
        {
            var (plan, slot) = EditableSlot(planId, slotId);
            if (items.Count > 200) throw new DomainException("لا تتجاوز 200 بند في وردية واحدة.");
            if (items.Any(i => i.Cartons <= 0)) throw new DomainException("يجب أن تكون كمية كل بند عدد كراتين صحيحًا موجبًا.");
            var existing = Db.ProductionPlanItems.Where(i => i.WorkSlotId == slot.Id).ToList();
            if (existing.Count > 0 && string.IsNullOrWhiteSpace(reason))
                throw new DomainException("سبب تعديل بطاقة محفوظة إلزامي لحفظ تاريخ التغييرات.");
            if (items.Where(i => i.ItemId != null).Select(i => i.ItemId).Distinct().Count()
                != items.Count(i => i.ItemId != null))
                throw new DomainException("لا يُسمح بتكرار بند محفوظ مرتين.");
            var quotes = new List<SequencedQuantityQuote>(items.Count);
            for (int index = 0; index < items.Count; index++)
            {
                var input = items[index];
                if (input.ItemId != null && existing.All(i => i.Id != input.ItemId))
                    throw new DomainException("معرّف بند سابق لا يخص هذه الوردية.");
                // Exclude exactly ONE row by position: API callers may repeat the
                // same object instance, and reference equality would hide BOTH.
                var peers = items.Where((_, i) => i != index).ToList();
                var quote = Quote(planId, slotId, input, peers);
                if (!quote.IsValid || input.Cartons > quote.MaximumCartons)
                    throw new DomainException((quote.Error ?? $"الكمية تتجاوز الحد {quote.MaximumCartons:N0}")
                        + $" (الصنف #{input.ProductId}، الدفعة #{input.LotId}).");
                quotes.Add(quote);
            }
            var oldSnapshot = Snapshot(existing);
            var savedIds = items.Where(i => i.ItemId != null).Select(i => i.ItemId!.Value).ToHashSet();
            foreach (var stale in existing.Where(i => !savedIds.Contains(i.Id))) Db.ProductionPlanItems.Remove(stale);
            var context = Db.ProductionPlanContexts.First(c => c.PlanId == planId);
            int priority = 1;
            for (int index = 0; index < items.Count; index++)
            {
                var input = items[index];
                var lot = Db.Lots.AsNoTracking().First(l => l.Id == input.LotId);
                int? customer = lot.CustomerId ?? (lot.ShipmentId is int shId
                    ? Db.Shipments.AsNoTracking().Where(s => s.Id == shId).Select(s => s.CustomerId).FirstOrDefault() : null);
                var quote = quotes[index];
                var item = input.ItemId is int id ? existing.Single(i => i.Id == id) : new ProductionPlanItem();
                if (input.ItemId == null) Db.ProductionPlanItems.Add(item);
                item.PlanId = planId; item.WorkSlotId = slotId; item.SourceType = "FromReceiving";
                item.LotId = lot.Id; item.ShipmentId = lot.ShipmentId; item.CustomerId = customer;
                item.ProductId = input.ProductId; item.PackagingTypeId = input.PackagingTypeId;
                item.PlannedCartons = input.Cartons; item.PlannedQtyKg = input.Cartons * quote.CartonWeightKg;
                var (molds, moldWeight) = UnitsPolicy.PackagingDefinition(Db, input.ProductId, input.PackagingTypeId);
                item.MoldsCount = molds; item.MoldWeightKg = Convert.ToDecimal(moldWeight);
                item.SourceUnit = "كجم"; item.SourceUnitWeightKg = 1;
                // Preserve the approved decimal recipe exactly, including fractional
                // kilograms: rounding to 3 places could turn a valid draw into ZERO.
                item.SourceQtyKg = (double)(input.Cartons * quote.RawKgPerCarton);
                item.SourceQtyInUnit = item.SourceQtyKg;
                item.ScheduledDate = slot.WorkDate.Date; item.SuggestedShiftId = slot.ShiftId;
                item.SuggestedLineId = context.LineId; item.PriorityNo = priority++;
                item.Status = DocStatuses.Draft;
            }
            var invalidated = new List<int>();
            if (slot.State is "Ready" or "NeedsReview")
            {
                slot.State = "Editing"; slot.CompletedAt = null; slot.CompletedBy = null;
                foreach (var later in Db.ProductionPlanWorkSlots.Where(s => s.PlanId == planId && s.SequenceNo > slot.SequenceNo
                    && s.State == "Ready"))
                {
                    later.State = "NeedsReview"; later.CompletedAt = null; later.CompletedBy = null;
                    invalidated.Add(later.Id);
                }
            }
            Db.SaveChanges();
            RecomputeLotReservations(existing.Select(i => i.LotId).Concat(items.Select(i => (int?)i.LotId))
                .Where(id => id != null).Select(id => id!.Value).Distinct());
            Db.SaveChanges();
            var after = Db.ProductionPlanItems.AsNoTracking().Where(i => i.WorkSlotId == slotId).OrderBy(i => i.PriorityNo).ToList();
            AddRevision(planId, slotId, "SaveSlot", reason ?? "حفظ بنود الوردية لأول مرة", oldSnapshot, Snapshot(after));
            foreach (var laterId in invalidated)
                AddRevision(planId, laterId, "NeedsReview", "تغيرت وردية سابقة: " + reason,
                    new { State = "Ready" }, new { State = "NeedsReview" });
            return OpResult.Success("حُفظت بنود الوردية ضمن نطاق الخطة؛ أكملها بعد مراجعة سقوفها.", planId, plan.DocumentNumber);
        });
    }

    public OpResult CompleteSlot(int planId, int slotId)
    {
        Require("planning", "Edit");
        return RunOp(() =>
        {
            var (plan, slot) = EditableSlot(planId, slotId);
            var items = Db.ProductionPlanItems.AsNoTracking().Where(i => i.WorkSlotId == slotId)
                .OrderBy(i => i.PriorityNo).Select(i => new SequencedPlanItemInput { ItemId = i.Id,
                    LotId = i.LotId ?? 0, ProductId = i.ProductId, PackagingTypeId = i.PackagingTypeId ?? 0,
                    Cartons = i.PlannedCartons }).ToList();
            if (items.Count == 0) throw new DomainException("أضف بند إنتاج صالحًا قبل إكمال الوردية.");
            foreach (var item in items)
            {
                var quote = Quote(planId, slotId, item, items.Where(x => x != item).ToList());
                if (!quote.IsValid || item.Cartons > quote.MaximumCartons)
                    throw new DomainException("لا يمكن إكمال الوردية: " + (quote.Error ?? $"الحد الحالي {quote.MaximumCartons:N0} كرتون."));
            }
            slot.State = "Ready"; slot.CompletedBy = Session?.UserId; slot.CompletedAt = Db.BusinessNow;
            OpenNextSlot(planId, slot.SequenceNo);
            AddRevision(planId, slotId, "Ready", "اكتملت مراجعة بنود الوردية", null, new { slot.State, slot.CompletedAt });
            return OpResult.Success("اكتملت خطة هذه الوردية؛ يمكن الانتقال إلى البطاقة التالية.", planId, plan.DocumentNumber);
        });
    }

    public OpResult ApproveNoProduction(int planId, int slotId, string reason)
    {
        Require("planning", "Approve");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
            return OpResult.Fail("سبب عدم التشغيل إلزامي — 10 أحرف على الأقل.");
        return RunOp(() =>
        {
            var (plan, slot) = EditableSlot(planId, slotId);
            if (Db.ProductionPlanItems.Any(i => i.WorkSlotId == slotId))
                throw new DomainException("احذف بنود هذه الوردية غير الصادرة رسميًا قبل توثيق عدم التشغيل.");
            slot.State = "NoProductionApproved"; slot.ExceptionReason = reason.Trim();
            slot.CompletedBy = Session?.UserId; slot.CompletedAt = Db.BusinessNow;
            OpenNextSlot(planId, slot.SequenceNo);
            AddRevision(planId, slotId, "NoProduction", reason.Trim(), null,
                new { slot.State, slot.ExceptionReason, slot.CompletedBy });
            return OpResult.Success("وُثق عدم تشغيل هذه الوردية بموافقة صاحب الصلاحية.", planId, plan.DocumentNumber);
        });
    }

    public OpResult CancelUnstarted(int planId, string reason)
    {
        Require("planning", "Cancel");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
            return OpResult.Fail("سبب إلغاء إعداد الخطة إلزامي.");
        return RunOp(() =>
        {
            var plan = Db.ProductionPlans.Include(p => p.Items).FirstOrDefault(p => p.Id == planId)
                ?? throw new DomainException("الخطة غير موجودة.");
            if (!Db.ProductionPlanContexts.Any(c => c.PlanId == planId) || plan.IsApproved
                || plan.Status != DocStatuses.Draft || plan.Items.Count != 0)
                throw new DomainException("إلغاء الإعداد مسموح لمسودة متسلسلة بلا بنود قبل الاعتماد فقط.");
            plan.Status = DocStatuses.Cancelled; plan.StatusReason = reason.Trim();
            AddRevision(planId, null, "Cancel", reason.Trim(), null, new { plan.Status });
            return OpResult.Success("أُلغي إعداد الخطة دون حذف تاريخها.", planId, plan.DocumentNumber);
        });
    }

    private (ProductionPlan plan, ProductionPlanWorkSlot slot) EditableSlot(int planId, int slotId)
    {
        var plan = Db.ProductionPlans.FirstOrDefault(p => p.Id == planId)
            ?? throw new DomainException("الخطة غير موجودة.");
        if (!Db.ProductionPlanContexts.Any(c => c.PlanId == planId))
            throw new DomainException("هذه الخطة تاريخية؛ استخدم مسارها الأصلي.");
        if (plan.IsApproved || plan.IsClosed || plan.Status == DocStatuses.Cancelled || plan.Status == "UnderApproval")
            throw new DomainException("لا يمكن تعديل خطة معتمدة أو منتظرة للاعتماد أو مقفلة.");
        var slot = Db.ProductionPlanWorkSlots.FirstOrDefault(s => s.Id == slotId && s.PlanId == planId)
            ?? throw new DomainException("بطاقة اليوم والوردية غير موجودة.");
        if (slot.State == "NoProductionApproved")
            throw new DomainException("وردية عدم التشغيل معتمدة؛ يلزم إجراء تصحيح مصرح به بدل تعديلها مباشرة.");
        if (Db.ProductionPlanWorkSlots.Any(s => s.PlanId == planId && s.SequenceNo < slot.SequenceNo
            && s.State != "Ready" && s.State != "NoProductionApproved"))
            throw new DomainException("أكمل اليوم والوردية السابقة بالترتيب قبل الانتقال.");
        var ids = Db.ProductionPlanItems.Where(i => i.WorkSlotId == slotId).Select(i => i.Id).ToList();
        if (Db.ProductionOrders.Any(o => o.SourcePlanId == planId)
            || Db.ProductionOrderItems.Any(i => i.PlanItemId != null && ids.Contains(i.PlanItemId.Value)))
            throw new DomainException("صدر أمر إنتاج (ولو أُلغي لاحقًا) يشير إلى بنود هذه الخطة؛ لا تُمحَ أو تُستبدل المراجع التاريخية.");
        return (plan, slot);
    }

    private void OpenNextSlot(int planId, int sequenceNo)
    {
        var next = Db.ProductionPlanWorkSlots.Where(s => s.PlanId == planId && s.SequenceNo > sequenceNo)
            .OrderBy(s => s.SequenceNo).FirstOrDefault();
        if (next != null && next.State == "Pending") next.State = "Editing";
    }

    private List<ProductionPlanItem> ActivePlanItemsExcept(int planId, int slotId, DateTime day, int shiftId, int lineId)
    {
        return Db.ProductionPlanItems.AsNoTracking()
            .Where(i => i.ScheduledDate != null && i.ScheduledDate.Value.Date == day.Date
                && !i.IsClosed && i.WorkSlotId != slotId)
            .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
            .Where(x => x.p.Status != DocStatuses.Cancelled && x.p.Status != DocStatuses.Closed && !x.p.IsClosed
                && (x.i.SuggestedShiftId ?? x.p.ShiftId) == shiftId
                && (x.i.SuggestedLineId ?? x.p.LineId) == lineId)
            .Select(x => x.i).ToList();
    }

    private List<ProductionOrderItem> StandaloneOrderItems(DateTime day, int shiftId, int lineId,
        IReadOnlySet<int> countedPlanItemIds)
    {
        var orders = Db.ProductionOrderItems.AsNoTracking().Where(i => !i.IsClosed)
            .Join(Db.ProductionOrders.AsNoTracking(), i => i.OrderId, o => o.Id, (i, o) => new { i, o })
            .Where(x => x.o.Status != DocStatuses.Cancelled && x.o.Status != DocStatuses.Closed
                && x.o.ProductionDate != null && x.o.ProductionDate.Value.Date == day.Date
                && x.o.ShiftId == shiftId && (x.o.LineId ?? 1) == lineId)
            .Select(x => x.i).ToList();
        // Active linked plan items in this resource have been counted once above;
        // orders whose plan item was closed/cancelled/rescheduled are NOT invisible.
        return orders.Where(i => i.PlanItemId is not int id || !countedPlanItemIds.Contains(id)).ToList();
    }

    private long UsedProductCartons(int productId, DateTime day, int shiftId, int currentSlotId)
    {
        // ProductShiftCapacity is a shift-wide ceiling, including other lines.
        // Line-capacity hours/weight are independently checked in Quote.
        var plans = Db.ProductionPlanItems.AsNoTracking().Where(i => i.ProductId == productId
                && i.ScheduledDate != null && i.ScheduledDate.Value.Date == day.Date
                && i.WorkSlotId != currentSlotId && !i.IsClosed)
            .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
            .Where(x => !x.p.IsClosed && x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled
                && (x.i.SuggestedShiftId ?? x.p.ShiftId) == shiftId)
            .Select(x => x.i).ToList();
        var counted = plans.Select(p => p.Id).ToHashSet();
        var orders = Db.ProductionOrderItems.AsNoTracking().Where(i => i.ProductId == productId && !i.IsClosed)
            .Join(Db.ProductionOrders.AsNoTracking(), i => i.OrderId, o => o.Id, (i, o) => new { i, o })
            .Where(x => x.o.Status != DocStatuses.Cancelled && x.o.Status != DocStatuses.Closed
                && x.o.ProductionDate != null && x.o.ProductionDate.Value.Date == day.Date && x.o.ShiftId == shiftId)
            .Select(x => x.i).ToList();
        return plans.Sum(i => (long)i.PlannedCartons)
            + orders.Where(i => i.PlanItemId is not int id || !counted.Contains(id))
                .Sum(i => (long)i.PlannedCartons);
    }

    private static double UncoveredOrderRawKg(DatesErpDbContext db, Lot lot)
    {
        var counted = db.ProductionPlanItems.AsNoTracking().Where(i => i.LotId == lot.Id && !i.IsClosed)
            .Join(db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
            .Where(x => !x.p.IsClosed && x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled)
            .Select(x => x.i.Id).ToHashSet();
        var orders = db.ProductionOrderItems.AsNoTracking().Where(i => i.LotId == lot.Id && !i.IsClosed)
            .Join(db.ProductionOrders.AsNoTracking(), i => i.OrderId, o => o.Id, (i, o) => new { i, o })
            .Where(x => x.o.Status != DocStatuses.Cancelled && x.o.Status != DocStatuses.Closed)
            .Select(x => x.i).ToList();
        double required = 0;
        foreach (var item in orders.Where(i => i.PlanItemId is not int id || !counted.Contains(id)))
        {
            double remainingKg = Math.Max(0, item.PlannedQtyKg - item.ProducedQtyKg);
            var recipe = db.ProductRawPlanningRules.AsNoTracking().Where(r => r.IsActive && r.ProductId == item.ProductId
                    && r.RawProductId == lot.ProductId && (r.PackagingTypeId == item.PackagingTypeId || r.PackagingTypeId == 0))
                .OrderByDescending(r => r.PackagingTypeId).FirstOrDefault();
            // Independent historical orders have no raw-draw snapshot. Never assume
            // their finished-product weight is the smaller or the larger draw.
            // Take the larger of the two known commitments, without rewriting them.
            double recipeKg = recipe != null ? Math.Max(0, item.PlannedCartons - item.ProducedCartons)
                * (double)recipe.RawKgPerCarton : 0;
            required += Math.Max(remainingKg, recipeKg);
        }
        return required;
    }

    private double AvailableRawForPlan(Lot lot, int planId, int currentSlotId)
    {
        double bookedOtherPlans = Db.ProductionPlanItems.AsNoTracking().Where(i => i.LotId == lot.Id && !i.IsClosed && i.PlanId != planId)
            .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
            .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
            .Select(x => x.i).RemainingKg().Sum();
        double bookedOtherSlots = Db.ProductionPlanItems.AsNoTracking().Where(i => i.LotId == lot.Id
            && i.PlanId == planId && i.WorkSlotId != currentSlotId && !i.IsClosed).RemainingKg().Sum();
        double independentOrders = UncoveredOrderRawKg(Db, lot);
        return Math.Max(0, lot.InStockQtyKg - lot.UnderTreatmentQtyKg - bookedOtherPlans
            - bookedOtherSlots - independentOrders);
    }

    private void RecomputeLotReservations(IEnumerable<int> lotIds)
    {
        foreach (var lotId in lotIds)
        {
            var lot = Db.Lots.FirstOrDefault(l => l.Id == lotId);
            if (lot == null) continue;
            lot.ReservedQtyKg = Math.Max(0, Db.ProductionPlanItems.AsNoTracking()
                .Where(i => i.LotId == lotId && !i.IsClosed)
                .Join(Db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
                .Where(x => x.p.Status != DocStatuses.Cancelled && x.p.Status != DocStatuses.Closed && !x.p.IsClosed)
                .Select(x => x.i).RemainingKg().Sum());
        }
    }

    private static object Snapshot(IEnumerable<ProductionPlanItem> items) => items.Select(i => new
    {
        i.Id, i.WorkSlotId, i.CustomerId, i.LotId, i.ProductId, i.PackagingTypeId,
        i.ScheduledDate, i.SuggestedShiftId, i.SuggestedLineId, i.PlannedCartons,
        i.PlannedQtyKg, i.SourceQtyKg, i.MoldsCount, i.MoldWeightKg, i.Status
    }).ToList();

    private void AddRevision(int planId, int? slotId, string action, string reason, object before, object after)
    {
        int persisted = Db.ProductionPlanRevisions.Where(r => r.PlanId == planId)
            .Select(r => (int?)r.RevisionNo).Max() ?? 0;
        int pending = Db.ChangeTracker.Entries<ProductionPlanRevision>()
            .Where(e => e.State == EntityState.Added && e.Entity.PlanId == planId)
            .Select(e => e.Entity.RevisionNo).DefaultIfEmpty(0).Max();
        int next = Math.Max(persisted, pending) + 1;
        Db.ProductionPlanRevisions.Add(new ProductionPlanRevision
        {
            PlanId = planId, WorkSlotId = slotId, RevisionNo = next,
            ChangedAt = Db.BusinessNow, ChangedBy = Session?.UserId,
            Action = action, Reason = reason,
            BeforeJson = before == null ? null : JsonSerializer.Serialize(before),
            AfterJson = after == null ? null : JsonSerializer.Serialize(after)
        });
    }

    internal static void LogWorkflow(DatesErpDbContext db, ProductionPlan plan, int? userId,
        string action, string previousStatus, string reason)
    {
        if (!db.ProductionPlanContexts.AsNoTracking().Any(c => c.PlanId == plan.Id)) return;
        int persisted = db.ProductionPlanRevisions.Where(r => r.PlanId == plan.Id)
            .Select(r => (int?)r.RevisionNo).Max() ?? 0;
        int pending = db.ChangeTracker.Entries<ProductionPlanRevision>()
            .Where(e => e.State == EntityState.Added && e.Entity.PlanId == plan.Id)
            .Select(e => e.Entity.RevisionNo).DefaultIfEmpty(0).Max();
        db.ProductionPlanRevisions.Add(new ProductionPlanRevision
        {
            PlanId = plan.Id, RevisionNo = Math.Max(persisted, pending) + 1,
            ChangedAt = db.BusinessNow, ChangedBy = userId, Action = action,
            Reason = reason, BeforeJson = JsonSerializer.Serialize(new { Status = previousStatus }),
            AfterJson = JsonSerializer.Serialize(new { plan.Status, plan.IsApproved, plan.ApprovedBy })
        });
    }

    /// <summary>Hooked into submission AND approval. Never infer a missing context on old plans.</summary>
    internal static string ValidateWorkflow(DatesErpDbContext db, ProductionPlan plan)
    {
        var context = db.ProductionPlanContexts.AsNoTracking().FirstOrDefault(c => c.PlanId == plan.Id);
        if (context == null) return null;
        var slots = db.ProductionPlanWorkSlots.AsNoTracking().Where(s => s.PlanId == plan.Id)
            .OrderBy(s => s.SequenceNo).ToList();
        var shifts = db.ProductionPlanSelectedShifts.AsNoTracking().Where(s => s.PlanId == plan.Id)
            .OrderBy(s => s.SequenceNo).Select(s => s.ShiftId).ToList();
        if (plan.StartDate == null || plan.EndDate == null || shifts.Count is < 1 or > 2
            || context.LineId != plan.LineId || (plan.EndDate.Value.Date - plan.StartDate.Value.Date).TotalDays is < 0 or > 365)
            return "تغير إعداد الفترة/الورديات/الخط المحفوظ؛ لا يمكن اعتماد الخطة.";
        int expected = ((plan.EndDate.Value.Date - plan.StartDate.Value.Date).Days + 1) * shifts.Count;
        if (slots.Count != expected || slots.Where((s, index) => s.SequenceNo != index
            || s.WorkDate.Date != plan.StartDate.Value.Date.AddDays(index / shifts.Count)
            || s.ShiftId != shifts[index % shifts.Count]).Any())
            return "إحدى بطاقات الأيام أو الورديات غير موجودة أو تغير ترتيبها؛ راجع إعداد الخطة.";
        if (slots.Any(s => s.State != "Ready" && s.State != "NoProductionApproved"))
            return "أكمل جميع بطاقات أيام الفترة والورديات قبل الإرسال أو الاعتماد.";
        if (slots.Any(s => s.State == "NoProductionApproved"
            && (string.IsNullOrWhiteSpace(s.ExceptionReason) || s.CompletedBy == null)))
            return "وردية عدم التشغيل تفتقر إلى السبب أو موافقة المستخدم المفوض.";
        var selectedCustomers = db.ProductionPlanScopeCustomers.AsNoTracking().Where(c => c.PlanId == plan.Id)
            .Select(c => c.CustomerId).ToHashSet();
        if (selectedCustomers.Count == 0) return "مجموعة عملاء سياق الخطة فارغة.";
        var slotMap = slots.ToDictionary(s => s.Id);
        var shiftIds = db.ProductionPlanSelectedShifts.AsNoTracking().Where(s => s.PlanId == plan.Id)
            .Select(s => s.ShiftId).ToHashSet();
        foreach (var slot in slots.Where(s => s.State == "Ready"))
            if (!plan.Items.Any(i => i.WorkSlotId == slot.Id && !i.IsClosed))
                return $"الوردية {slot.ShiftId} يوم {slot.WorkDate:dd/MM/yyyy} بلا بنود صالحة.";
        foreach (var item in plan.Items)
        {
            if (item.WorkSlotId is not int id || !slotMap.TryGetValue(id, out var slot)
                || slot.State != "Ready" || item.ScheduledDate?.Date != slot.WorkDate.Date
                || item.SuggestedShiftId != slot.ShiftId || !shiftIds.Contains(slot.ShiftId)
                || item.SuggestedLineId != context.LineId
                || item.CustomerId is not int cust || !selectedCustomers.Contains(cust))
                return "بند خطة يخالف يوم/وردية/عميل الإعداد المثبت؛ راجع البنود.";
            var lot = item.LotId is int lotId ? db.Lots.AsNoTracking().FirstOrDefault(l => l.Id == lotId) : null;
            if (lot == null || lot.Status != DocStatuses.Approved)
                return "دفعة خام في الخطة غير معتمدة أو غير موجودة.";
            var owner = lot.CustomerId ?? (lot.ShipmentId is int shipmentId
                ? db.Shipments.AsNoTracking().Where(s => s.Id == shipmentId).Select(s => s.CustomerId).FirstOrDefault() : null);
            if (item.CustomerId != owner) return "ملكية دفعة خام في الخطة تغيرت؛ راجع العميل والدفعة.";
            var product = db.Products.AsNoTracking().FirstOrDefault(p => p.Id == item.ProductId);
            var pack = db.PackagingTypes.AsNoTracking().FirstOrDefault(p => p.Id == item.PackagingTypeId);
            if (product == null || !product.IsActive || product.ItemType != "Finished"
                || product.UnitOfMeasure != UnitsPolicy.UnitCarton
                || product.SourceProductId != lot.ProductId || pack == null || !pack.IsActive)
                return "تغير تعريف المنتج/العبوة/ربط الخام لأحد البنود؛ راجع البطاقة.";
            var weight = UnitsPolicy.CartonWeight(db, item.ProductId, item.PackagingTypeId);
            var (molds, moldKg) = UnitsPolicy.PackagingDefinition(db, item.ProductId, item.PackagingTypeId);
            if (weight <= 0 || Math.Abs(item.PlannedQtyKg - item.PlannedCartons * weight) > 0.001
                || item.MoldsCount != molds || Math.Abs((double)item.MoldWeightKg - moldKg) > 0.0001)
                return "تغير وزن العبوة أو مواصفات قوالب البند؛ أعد حفظ البطاقة قبل الاعتماد.";
            var rate = db.ProductRawPlanningRules.AsNoTracking().Where(r => r.IsActive && r.ProductId == item.ProductId
                    && r.RawProductId == lot.ProductId && (r.PackagingTypeId == item.PackagingTypeId || r.PackagingTypeId == 0))
                .OrderByDescending(r => r.PackagingTypeId).FirstOrDefault();
            if (rate == null || rate.ApprovedBy == null || Math.Abs(item.SourceQtyKg
                - item.PlannedCartons * (double)rate.RawKgPerCarton) > 0.01)
                return "تغير تعريف سحب الخام لأحد البنود؛ يلزم إعادة مراجعته قبل الاعتماد.";
            var shift = db.Shifts.AsNoTracking().FirstOrDefault(s => s.Id == slot.ShiftId && s.IsActive);
            if (shift == null || !TimeSpan.TryParse(shift.StartTime, CultureInfo.InvariantCulture, out var start))
                return "وقت الوردية غير صالح أو ورديتها موقوفة.";
            var readyAt = slot.WorkDate.Date.Add(start);
            if (lot.ShipmentItemId is int si && db.ShipmentItems.AsNoTracking()
                    .Any(s => s.Id == si && s.TreatmentRequired == true && s.TreatmentUntilDate > readyAt)
                && lot.InStockQtyKg - lot.UnderTreatmentQtyKg <= 0.001)
                return "الخام غير جاهز عند بدء إحدى الورديات؛ أعِد جدولة البند أو عالج الخام.";
        }
        // Verify released/raw stock cumulatively by lot, without using the same plan's reservation twice.
        foreach (var group in plan.Items.Where(i => !i.IsClosed && i.LotId != null).GroupBy(i => i.LotId!.Value))
        {
            var lot = db.Lots.AsNoTracking().First(l => l.Id == group.Key);
            double otherPlans = db.ProductionPlanItems.AsNoTracking().Where(i => i.LotId == lot.Id && i.PlanId != plan.Id && !i.IsClosed)
                .Join(db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
                .Where(x => x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && !x.p.IsClosed)
                .Select(x => x.i).RemainingKg().Sum();
            double required = group.Sum(PlanRawReservation.PlannedKg);
            double independentOrders = UncoveredOrderRawKg(db, lot);
            if (required > Math.Max(0, lot.InStockQtyKg - lot.UnderTreatmentQtyKg - otherPlans - independentOrders) + 0.001)
                return $"خام الدفعة {lot.LotCode} المفرج عنه غير كافٍ لكميات الخطة؛ أعد حساب البطاقات.";
        }
        return null;
    }
}
