using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>§الورديات: الاسم والأوقات والساعات والتوقفات فقط — لا إعداد لطاقة الأصناف هنا.</summary>
public partial class ShiftsView : UserControl
{
    private int _editId;

    public ShiftsView()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
    }

    public void AttachChrome(Views.ErpChrome chrome)
    {
        chrome.SetModule("الورديات — إعداد الوقت المتاح");
        chrome.SetScreenCode("MRPMAS1005");
        // §1 — الأزرار الأساسية الموحدة: جديد/حفظ/بحث/تعديل/تراجع
        var tb = new Views.ErpToolbar()
            .WithNew((_, _) => New_Click(null, null), "وردية جديدة (F2)")
            .WithSave((_, _) => Save_Click(null, null), "حفظ الوردية (F10)")
            .WithSearch((_, _) => FillGrid(), "بحث / عرض الورديات المعرفة (F9)")
            .WithEdit((_, _) => ShiftsGrid_DoubleClick(null, null))
            .WithUndo((_, _) => New_Click(null, null), "تراجع: مسح النموذج — لا يحذف الورديات المحفوظة")
            .WithRefresh((_, _) => FillGrid())
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard"));
        chrome.SetToolbar(tb);
        chrome.SetBody(this);
    }

    private void Load()
    {
        // §2 — الشاشة تفتح فارغة في وضع «وردية جديدة» — الجدول يظهر عند البحث أو بعد الحفظ
        New_Click(null, null);
    }

    private void FillGrid()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            ShiftsGrid.ItemsSource = db.Shifts.OrderBy(s => s.Id).ToList().Select(s => new
            {
                Id = s.Id,
                Name = s.ShiftNameAr,
                Start = s.StartTime,
                End = s.EndTime,
                Total = s.TotalHours,
                Down = s.PlannedDowntimeHours,
                Eff = s.EffectiveProductiveHours
            }).ToList();
            ShiftsHint.Text = "الورديات المعرفة (نقر مزدوج للتعديل) — عند تغيير الساعات يُعاد حساب طاقة كل صنف تلقائياً";
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Shifts.Grid"); }
    }

    private void Hours_Changed(object sender, TextChangedEventArgs e)
    {
        if (EffBox == null) return;
        double.TryParse(TotalBox.Text, out var t);
        double.TryParse(DownBox.Text, out var d);
        if (t > 0) EffBox.Text = Math.Max(0, t - d).ToString();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            double.TryParse(TotalBox.Text, out var total);
            double.TryParse(DownBox.Text, out var down);
            double.TryParse(EffBox.Text, out var eff);
            using var scope = AppContainer.NewScope();
            var svc = (IShiftService)scope.ServiceProvider.GetService(typeof(IShiftService));
            var r = svc.SaveShift(_editId > 0 ? _editId : null, NameBox.Text, StartBox.Text, EndBox.Text, total, down, eff);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }
            // §4 — رسالة نجاح واضحة والبيانات تبقى أمام المستخدم
            AppContainer.Get<DialogService>().Info(r.Message);
            FillGrid();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Shifts.Save"); }
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        _editId = 0;
        FormTitle.Text = "⏰ إضافة/تعديل وردية — الوردية تحدد الوقت المتاح فقط (لا طاقة للأصناف هنا)";
        NameBox.Text = "";
        StartBox.Text = "06:00"; EndBox.Text = "14:00";
        TotalBox.Text = "8"; DownBox.Text = "0"; EffBox.Text = "8";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_editId == 0) { AppContainer.Get<DialogService>().Error("اختر وردية من الجدول أولاً (نقر مزدوج)."); return; }
        if (!AppContainer.Get<DialogService>().Confirm("حذف الوردية المحددة؟")) return;
        using var scope = AppContainer.NewScope();
        var svc = (IShiftService)scope.ServiceProvider.GetService(typeof(IShiftService));
        var r = svc.DeleteShift(_editId);
        if (!r.Ok) AppContainer.Get<DialogService>().Error(r.Message);
        else { AppContainer.Get<DialogService>().Info(r.Message); New_Click(sender, e); FillGrid(); }
    }

    private void ShiftsGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ShiftsGrid.SelectedItem == null) return;
        var sel = ShiftsGrid.SelectedItem;
        int id = (int)sel.GetType().GetProperty("Id").GetValue(sel);
        using var scope = AppContainer.NewScope();
        var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
        var s = db.Shifts.FirstOrDefault(x => x.Id == id);
        if (s == null) return;
        _editId = s.Id;
        FormTitle.Text = $"✏️ تعديل الوردية: {s.ShiftNameAr} — تغيير الساعات يعيد حساب طاقة كل صنف تلقائياً";
        NameBox.Text = s.ShiftNameAr;
        StartBox.Text = s.StartTime; EndBox.Text = s.EndTime;
        TotalBox.Text = s.TotalHours.ToString();
        DownBox.Text = s.PlannedDowntimeHours.ToString();
        EffBox.Text = s.EffectiveProductiveHours.ToString();
    }
}
