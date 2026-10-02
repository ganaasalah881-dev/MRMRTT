using DatesErp.Core.Common;
using DatesErp.Core.Domain.Enums;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace DatesErp.Application.Services;

/// <summary>Shared, read-only time budget. All products consume one day/shift/line slot.
/// The write services repeat this check in a serializable transaction.</summary>
public sealed class PlanningCapacityEvaluator
{
    private readonly DatesErpDbContext _db;
    public PlanningCapacityEvaluator(DatesErpDbContext db) => _db = db;
    public PlanCapacityResult Evaluate(IReadOnlyList<PlanItemDto> items, int? excludePlanId = null,
        string startDate = null, string endDate = null, int? defaultShiftId = null, int? defaultLineId = null,
        bool ordersOfExcludedPlanCoveredByDraft = false)
    {
        var result = new PlanCapacityResult();
        var shifts = _db.Shifts.AsNoTracking().ToDictionary(s => s.Id);
        var lines = _db.ProductionLines.AsNoTracking().ToDictionary(s => s.Id);
        var rates = new Dictionary<(int, int, int?), double>();
        double Rate(int product, int shift, int? pack)
        {
            var key = (product, shift, pack);
            if (!rates.TryGetValue(key, out var rate)) rates[key] = rate = CapacityPolicy.RateFor(_db, product, shift, pack);
            return rate;
        }
        var slots = new Dictionary<(DateTime, int, int), PlanCapacitySlot>();
        PlanCapacitySlot Slot(DateTime day, int shift, int line)
        {
            var key = (day.Date, shift, line);
            if (!slots.TryGetValue(key, out var slot))
            {
                double hours = shifts.TryGetValue(shift, out var sh) && sh.IsActive
                    ? CapacityPolicy.EffectiveHours(sh.EffectiveProductiveHours, sh.TotalHours, sh.PlannedDowntimeHours) : 0;
                if (!lines.TryGetValue(line, out var ln) || !ln.IsActive) hours = 0;
                slots[key] = slot = new() { Day = day.Date, ShiftId = shift, LineId = line, TotalHours = hours };
            }
            return slot;
        }
        bool hasFrom = UiFormat.TryParseDate(startDate, out var from), hasTo = UiFormat.TryParseDate(endDate, out var to);
        if (hasFrom && hasTo && (to < from || (to - from).TotalDays > 3660))
        { result.Error = "فترة الخطة غير صالحة أو تتجاوز 3660 يوماً."; return result; }
        if (hasFrom && hasTo && defaultShiftId > 0 && defaultLineId > 0)
            for (var day = from.Date; day <= to.Date; day = day.AddDays(1)) Slot(day, defaultShiftId.Value, defaultLineId.Value);
        var rowSlots = new List<PlanCapacitySlot>();
        var occupiedRates = new Dictionary<PlanCapacitySlot, HashSet<double>>();
        for (int index = 0; index < items.Count; index++)
        {
            var item = items[index]; var row = new PlanCapacityRow { Index = index, Quantity = item.PlannedCartons };
            result.Rows.Add(row);
            int shift = item.SuggestedShiftId ?? defaultShiftId ?? 0, line = item.SuggestedLineId ?? defaultLineId ?? 1;
            if (!UiFormat.TryParseDate(item.ScheduledDate, out var day) || shift <= 0 ||
                (hasFrom && day.Date < from.Date) || (hasTo && day.Date > to.Date))
            { row.Error = "حدّد تاريخ إنتاج داخل الفترة ووردية فعلية لكل بند."; rowSlots.Add(null); continue; }
            var slot = Slot(day, shift, line); rowSlots.Add(slot);
            row.Rate = Rate(item.ProductId, shift, item.PackagingTypeId);
            if (!double.IsFinite(row.Rate) || row.Rate <= 0 || !double.IsFinite(slot.TotalHours) || slot.TotalHours <= 0)
            { row.Error = "الطاقة غير معرّفة: يلزم معدل إنتاج موجب ووردية وخط نشطان بساعات فعلية. عرّف الطاقة من شاشة الأصناف."; continue; }
            if (row.Quantity <= 0 && item.PlannedQtyKg > 0 && double.IsFinite(item.PlannedQtyKg))
            {
                double weight = UnitsPolicy.CartonWeight(_db, item.ProductId, item.PackagingTypeId);
                if (weight > 0 && item.PlannedQtyKg / weight <= int.MaxValue) row.Quantity = (int)Math.Ceiling(item.PlannedQtyKg / weight);
            }
            if (row.Quantity <= 0 || item.PlannedCartons < 0)
            { row.Error = "الكمية المطلوبة يجب أن تكون عدداً صحيحاً موجباً من الكراتين."; continue; }
            row.ProductCapacity = row.Rate * slot.TotalHours;
            row.RequiredHours = row.Quantity / row.Rate;
            row.UsagePercent = row.RequiredHours / slot.TotalHours * 100;
            slot.DraftHours += row.RequiredHours;
        }
        if (slots.Count > 0)
        {
            var min = slots.Values.Min(s => s.Day); var max = slots.Values.Max(s => s.Day).AddDays(1);
            var others = _db.ProductionPlanItems.AsNoTracking()
                .Join(_db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id, (i, p) => new { i, p })
                .Where(x => (excludePlanId == null || x.p.Id != excludePlanId) && !x.i.IsClosed && !x.p.IsClosed
                    && x.p.Status != DocStatuses.Closed && x.p.Status != DocStatuses.Cancelled && x.i.ScheduledDate >= min && x.i.ScheduledDate < max)
                .Select(x => new { x.i.Id, x.i.ProductId, x.i.PackagingTypeId, x.i.PlannedCartons, x.i.PlannedQtyKg,
                    Day = x.i.ScheduledDate.Value, Shift = x.i.SuggestedShiftId ?? x.p.ShiftId ?? 0, Line = x.i.SuggestedLineId ?? x.p.LineId ?? 1 }).ToList();
            void CountOccupied(int product, int? pack, int plannedCartons, double kg, DateTime day, int shift, int line)
            {
                if (!slots.TryGetValue((day.Date, shift, line), out var slot)) return;
                double rate = Rate(product, shift, pack), cartons = plannedCartons;
                if (cartons <= 0 && kg > 0)
                {
                    double weight = UnitsPolicy.CartonWeight(_db, product, pack);
                    if (weight > 0) cartons = Math.Ceiling(kg / weight);
                }
                if (cartons <= 0 || rate <= 0 || !double.IsFinite(rate))
                { result.Error ??= $"إشغال خطة/أمر قائم في {slot.Label} بلا كمية/معدل صالح؛ أصلح التعريف قبل إضافة حمل جديد."; return; }
                slot.OtherHours += cartons / rate; // never round occupied time down
                if (!occupiedRates.TryGetValue(slot, out var set)) occupiedRates[slot] = set = new();
                set.Add(rate);
            }
            foreach (var other in others)
                CountOccupied(other.ProductId, other.PackagingTypeId, other.PlannedCartons,
                    other.PlannedQtyKg, other.Day, other.Shift, other.Line);
            // A linked order is covered only when its plan item is counted IN THIS
            // date/shift/line. Rescheduled orders or orphaned/closed plan items still
            // use real capacity; they must not disappear from occupancy.
            var covered = others.Select(x => (x.Id, x.Day.Date, x.Shift, x.Line)).ToHashSet();
            // ONLY the order-issuance validator opts in: it passes the complete
            // approved source plan as 'items'. Those orders are already in DraftHours.
            // The normal plan editor and empty-slot occupancy must never opt in.
            var draftSourceSlots = ordersOfExcludedPlanCoveredByDraft && excludePlanId is int sourceId
                ? _db.ProductionPlanItems.AsNoTracking()
                    .Where(i => i.PlanId == sourceId && !i.IsClosed && i.ScheduledDate != null)
                    .Join(_db.ProductionPlans.AsNoTracking(), i => i.PlanId, p => p.Id,
                        (i, p) => new { i.Id, Day = i.ScheduledDate!.Value,
                            Shift = i.SuggestedShiftId ?? p.ShiftId ?? 0,
                            Line = i.SuggestedLineId ?? p.LineId ?? 1 })
                    .ToList().Select(i => (i.Id, i.Day.Date, i.Shift, i.Line)).ToHashSet()
                : new HashSet<(int, DateTime, int, int)>();
            var activeOrders = _db.ProductionOrderItems.AsNoTracking()
                .Where(i => !i.IsClosed)
                .Join(_db.ProductionOrders.AsNoTracking(), i => i.OrderId, o => o.Id, (i, o) => new { i, o })
                .Where(x => x.o.Status != DocStatuses.Cancelled && x.o.Status != DocStatuses.Closed
                    && x.o.ProductionDate >= min && x.o.ProductionDate < max)
                .Select(x => new { x.i.PlanItemId, x.i.ProductId, x.i.PackagingTypeId, x.i.PlannedCartons,
                    x.i.PlannedQtyKg, Day = x.o.ProductionDate!.Value, Shift = x.o.ShiftId ?? 0,
                    Line = x.o.LineId ?? 1 }).ToList();
            foreach (var other in activeOrders)
                if (other.PlanItemId is not int linked
                    || (!covered.Contains((linked, other.Day.Date, other.Shift, other.Line))
                        && !draftSourceSlots.Contains((linked, other.Day.Date, other.Shift, other.Line))))
                    CountOccupied(other.ProductId, other.PackagingTypeId, other.PlannedCartons,
                        other.PlannedQtyKg, other.Day, other.Shift, other.Line);
            for (int index = 0; index < result.Rows.Count; index++)
            {
                var row = result.Rows[index]; var slot = rowSlots[index];
                if (slot == null || row.Error != null) continue;
                double available = Math.Max(0, slot.TotalHours - slot.OtherHours - (slot.DraftHours - row.RequiredHours));
                row.MaximumCartons = (long)Math.Min(int.MaxValue, Math.Floor(available * row.Rate + 1e-7));
                row.RemainingPercent = Math.Max(0, slot.TotalHours - slot.UsedHours) / slot.TotalHours * 100;
                if (row.Quantity > row.MaximumCartons)
                    row.Error = $"الكمية المطلوبة أكبر من الطاقة الإنتاجية المتاحة للصنف في هذه الوردية — {slot.Label}.\nالطاقة المتاحة: {row.MaximumCartons:N0} كرتون | الحد الأقصى المسموح: {row.MaximumCartons:N0} كرتون | المطلوب: {row.Quantity:N0} كرتون | الزيادة: {row.ExcessCartons:N0} كرتون | الاستخدام: {slot.UsedHours / slot.TotalHours * 100:N2}%.";
            }
            foreach (var slot in slots.Values)
            {
                var displayRates = items.Select(i => Rate(i.ProductId, slot.ShiftId, i.PackagingTypeId))
                    .Concat(occupiedRates.TryGetValue(slot, out var set) ? set : Enumerable.Empty<double>()).Distinct().ToList();
                slot.DisplayRate = displayRates.Count == 1 && displayRates[0] > 0 ? displayRates[0] : 0;
                if (slot.UsedHours > slot.TotalHours + 1e-9) result.Error ??= $"تجاوز الطاقة في {slot.Label}.";
            }
        }
        result.Error = result.Rows.LastOrDefault(r => r.Error != null)?.Error ?? result.Error;
        result.Slots.AddRange(slots.Values.OrderBy(s => s.Day).ThenBy(s => s.ShiftId).ThenBy(s => s.LineId));
        return result;
    }
}
