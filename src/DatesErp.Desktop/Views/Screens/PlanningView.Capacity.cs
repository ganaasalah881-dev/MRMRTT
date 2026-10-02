using System.Windows.Media;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
namespace DatesErp.Desktop.Views.Screens;
public partial class PlanningView
{
    private bool _capacityValid, _updatingCapacity;
    internal static PlanItemDto CapacityItem(PlanRowUi row) => new()
    {
        ProductId = row.ProductId, PackagingTypeId = row.PackId, LotId = row.LotId, SelectedRawProductId = row.RawProductId, PlannedCartons = row.Cartons, PlannedQtyKg = row.QtyKg,
        ScheduledDate = row.Date, SuggestedShiftId = row.ShiftId, SuggestedLineId = row.LineId
    };
    private PlanCapacityResult EvaluateDraft(List<PlanItemDto> items)
    {
        using var scope = AppContainer.NewScope();
        var service = scope.ServiceProvider.GetRequiredService<IPlanningService>();
        return service.EvaluateCapacity(items, _currentPlanId > 0 ? _currentPlanId : null,
            StartBox.SelectedDate?.ToString("dd/MM/yyyy"), EndBox.SelectedDate?.ToString("dd/MM/yyyy"), SelectedShiftId(), SelectedLineId());
    }
    private string CheckRowQuantity(PlanRowUi row, int quantity)
    {
        try
        {
            if (quantity == 0) return null;
            var candidate = CapacityItem(row); candidate.PlannedCartons = quantity; candidate.PlannedQtyKg = quantity * row.CartonWeight;
            // §1.50.67 FIX: استثناء الصفوف الفارغة وغير المكتملة من فحص الطاقة — فقط المكتملة (كراتين>0)
            var validOthers = _rows.Where(r => r != row && (r.LotId != null || r.ProductId != 0) && r.Cartons > 0).Select(CapacityItem).ToList();
            validOthers.Add(candidate);
            var check = EvaluateDraft(validOthers);
            return check.Rows.LastOrDefault()?.Error ?? check.Error;
        }
        catch (Exception ex) { ErrorLog.Write(ex, "Planning.QuantityGuard"); return "تعذر التحقق من الطاقة؛ لم تُقبل الكمية."; }
    }
    private void UpdateCapacityBar()
    {
        // §شرائح السياق (تاريخ الإنتاج المحدد/الفترة) تُحدَّث هنا لأن هذا المسار يُنادى عند
        // كل تغيير للتواريخ أو الوردية أو البنود أو فتح خطة محفوظة — نقطة تحديث واحدة.
        UpdateDateChips();
        if (_updatingCapacity || CapacitySummary == null) return;
        _updatingCapacity = true;
        try
        {
            // §1.50.67 FIX: زر الحفظ يتشفر بعد تعديل الأصناف دون حفظ — السبب صف فارغ placeholder
            // و §1.50.67 FIX2: عند الضغط على صنف جديد يتشفر الحفظ — لأن صف جديد ProductId!=0 وكرتون 0 كان يُحتسب خطأ.
            // الآن: valid = كل البنود التي لها هوية (Lot/Product)، complete = التي لها كراتين>0 وبلا QuantityError
            // الحفظ يبقى مفعلاً إذا وجد complete واحد على الأقل، والصفوف غير المكتملة تُتجاهل ولا تعطل الحفظ.
            var allValid = _rows.Where(r => r.LotId != null || r.ProductId != 0).ToList();
            var completeRows = allValid.Where(r => r.Cartons > 0 && int.TryParse(r.CartonsText, out var n) && n > 0).ToList();
            var result = EvaluateDraft(completeRows.Select(CapacityItem).ToList());
            // أخطاء الكمية فقط من البنود المكتملة (مismatch كجم/كرتون) — الصفوف قيد الإدخال (0 كرتون) لا تعطل الحفظ
            string inputError = completeRows.FirstOrDefault(r => r.QuantityError != null)?.QuantityError;
            // إذا كان هناك بند مكتمل بكراتين>0 لكن به خطأ طاقة، يظهر في result.Error
            _capacityValid = result.IsValid && inputError == null && completeRows.Count > 0;
            // §التخطيط الأفقي: شريط التقدّم يترجم الطاقة إلى مؤشر بصري فوري (أخضر ضمن الطاقة، أحمر عند التجاوز)
            double usedHours = result.Slots.Sum(s => s.UsedHours);
            double totalHours = result.Slots.Sum(s => s.TotalHours);
            if (CapacityProgress != null)
            {
                CapacityProgress.Maximum = totalHours > 0 ? totalHours : 1;
                CapacityProgress.Value = Math.Min(usedHours, CapacityProgress.Maximum);
                CapacityProgress.Foreground = _capacityValid ? Brushes.SeaGreen : Brushes.IndianRed;
                CapacityProgress.ToolTip = totalHours > 0
                    ? $"المجدول {usedHours:N3} من أصل {totalHours:N3} ساعة متاحة"
                    : "لا طاقة معرّفة للوردية المختارة";
            }
            // §v1.50.35: سطر واحد مضغوط — تفاصيل الأيام في التلميح لا تغطي الشاشة في الفترة الأسبوعية.
            int overloaded = result.Slots.Count(s => s.TotalHours > 0 && s.UsedHours > s.TotalHours + 0.001);
            CapacitySummary.Text = result.Summary
                + (result.Slots.Count > 1 ? $"  ·  {result.Slots.Count} يوماً" : "")
                + (overloaded > 0 ? $"  ·  ⚠ تجاوز في {overloaded} يوماً" : "");
            CapacitySummary.ToolTip = result.Slots.Count == 0 ? result.Summary
                : string.Join("\n", result.Slots.Select(s => $"{s.Label}: مستخدم {s.UsedHours:N1} / {s.TotalHours:N1} س"));
            RemainingBadge.Text = inputError ?? result.Error ?? "ضمن الطاقة — الحساب تراكمي لجميع الأصناف";
            RemainingBadge.Foreground = _capacityValid ? Brushes.DarkGreen : Brushes.Firebrick;
            // §1.50.67 FIX: تعيين الطاقة فقط للبنود المكتملة — كان يعين حسب index في _rows فيخطئ مع placeholder
            for (int i = 0; i < completeRows.Count && i < result.Rows.Count; i++) completeRows[i].Capacity = result.Rows[i];
            // مسح طاقة الصفوف الفارغة وغير المكتملة
            foreach (var empty in _rows.Where(r => r.LotId == null && r.ProductId == 0)) empty.Capacity = null;
            foreach (var inc in allValid.Where(r => r.Cartons <= 0)) inc.Capacity = null;
        }
        catch (Exception ex) { ErrorLog.Write(ex, "Planning.CapacityBar"); _capacityValid = false; RemainingBadge.Text = "تعذر التحقق من الطاقة؛ راجع البيانات ثم أعد المحاولة."; }
        finally
        {
            _updatingCapacity = false;
            // §FIX 1.50.73: الطاقة تُعرض وتُراجع عند الحفظ، لكنها لا تعطل الزر.
            // التفعيل الوحيد هنا هو حالة القفل؛ Save_Click يعطي رسالة التحقق الواضحة.
            if (SaveActionBtn != null) SaveActionBtn.IsEnabled = !_locked;
            if (_toolbar?.SaveBtn != null) _toolbar.SaveBtn.IsEnabled = !_locked;
        }
    }
}
