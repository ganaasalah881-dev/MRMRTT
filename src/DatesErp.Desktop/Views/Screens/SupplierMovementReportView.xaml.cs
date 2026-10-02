using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using DatesErp.Core.Common;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Application.Services;
using DatesErp.Desktop.Printing;
using DatesErp.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views.Screens;

/// <summary>
/// §B103 — تقرير حركة الموردين حسب الأصناف (تحليلي كميات) — الشكل مطابق للتصميم
/// المرجعي المعتمد (لقطة التقرير): تجميع بالمورد، أعمدة كمية واردة/مرتجعة/صافي
/// مبيعات/متبقية، سطر إجمالي لكل مورد. الطباعة عبر SupplierMovementPrint.
/// ربط البيانات بحركات المخزون المعتمدة (لا حركة بدون مستند §9):
/// المشتريات = استلامات التمور · التوريد المخزني = وارد الإنتاج/التام/الإفراج + التسويات ·
/// مردود المشتريات = مرتجعات مواد صادرة · الصرف المخزني = صرف المواد والاستهلاك ·
/// المبيعات = تسليمات العملاء وبيع الكرتون · مردود المبيعات = مرتجعات عملاء واردة.
/// الكمية بوحدة الصنف: كجم ← الوزن، وإلا ← عدد العبوات.
/// </summary>
public partial class SupplierMovementReportView : UserControl
{
    private readonly List<int?> _partyIds = new();
    private List<SmGroup> _groups = new();
    private SmMeta _meta;

    public SupplierMovementReportView()
    {
        InitializeComponent();
        Loaded += (_, _) => Init();
    }

    private void Init()
    {
        FromBox.SelectedDate = new DateTime(DateTime.Now.Year, 1, 1);
        ToBox.SelectedDate = new DateTime(DateTime.Now.Year, 12, 31);
        LoadParties();
    }

    private void LoadParties()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<SupplierMovementService>();
            _partyIds.Clear(); _partyIds.Add(null);
            PartyBox.Items.Clear(); PartyBox.Items.Add("كل الموردين");
            foreach (var p in svc.GetParties()) { _partyIds.Add(p.Id); PartyBox.Items.Add($"{p.Code} — {p.Name}"); }
            PartyBox.SelectedIndex = 0;
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "SupplierMovement.LoadParties"); }
    }

    private int? PartyId() => PartyBox.SelectedIndex > 0 && PartyBox.SelectedIndex < _partyIds.Count
        ? _partyIds[PartyBox.SelectedIndex] : null;

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var from = (FromBox.SelectedDate ?? DateTime.Now.Date).Date;
            var to = (ToBox.SelectedDate ?? DateTime.Now.Date).Date;
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<SupplierMovementService>();
            Apply(from, to, svc.Compute(from, to, PartyId(), SearchBox.Text));
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "SupplierMovement.Run"); }
    }

    private void Apply(DateTime from, DateTime to, List<SmGroup> groups)
    {
        _groups = groups;
        _meta = new SmMeta(from, to, CurrentUser(), DateTime.Now);
        var flat = new List<object>();
        foreach (var g in groups)
            foreach (var r in g.Rows)
                flat.Add(new
                {
                    Party = $"{g.Code} {g.Name}",
                    r.Code,
                    r.Name,
                    r.Unit,
                    Open = SupplierMovementPrint.Fmt(r.Open),
                    Purch = SupplierMovementPrint.Fmt(r.Purch),
                    SupIn = SupplierMovementPrint.Fmt(r.SupIn),
                    PurchRet = SupplierMovementPrint.Fmt(r.PurchRet),
                    Issue = SupplierMovementPrint.Fmt(r.Issue),
                    Sales = SupplierMovementPrint.Fmt(r.Sales),
                    SalesRet = SupplierMovementPrint.Fmt(r.SalesRet),
                    Remain = SupplierMovementPrint.Fmt(r.Remain)
                });
        SmGrid.ItemsSource = flat;
        int rows = flat.Count;
        RowsCount.Text = rows == 0
            ? $"لا توجد حركات موردين من {DatesErp.Core.Common.UiFormat.D(from)} إلى {DatesErp.Core.Common.UiFormat.D(to)}."
            : $"{groups.Count} مورداً · {rows} سطراً · من {DatesErp.Core.Common.UiFormat.D(from)} إلى {DatesErp.Core.Common.UiFormat.D(to)}";
    }

    private static string CurrentUser()
    {
        try { return AppContainer.Get<ICurrentSession>().UserName ?? "-"; }
        catch { return "-"; }
    }

    private FixedDocument Doc()
    {
        if (_groups.Count == 0) Run_Click(this, new RoutedEventArgs());
        return SupplierMovementPrint.Build(_meta ?? new SmMeta(DateTime.Now.Date, DateTime.Now.Date, CurrentUser(), DateTime.Now), _groups);
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        try { new PrintPreviewWindow(Doc(), "تقرير حركة الموردين حسب الاصناف تحليلي كميات").Show(); }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "SupplierMovement.Print"); }
    }

    private void Pdf_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "ملف PDF|*.pdf", FileName = "تقرير-حركة-الموردين-حسب-الاصناف.pdf" };
        if (dlg.ShowDialog() != true) return;
        try { PrintRenderer.ExportPdf(Doc(), dlg.FileName, "تقرير حركة الموردين حسب الاصناف تحليلي كميات"); AppContainer.Get<DialogService>().Toast("تم حفظ PDF:\n" + dlg.FileName); }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "SupplierMovement.Pdf"); }
    }

    private void Excel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_groups.Count == 0) Run_Click(this, new RoutedEventArgs());
            var rr = new ReportResult { TitleAr = "تقرير حركة الموردين حسب الاصناف تحليلي كميات", PeriodLabel = _meta == null ? "" : $"من {DatesErp.Core.Common.UiFormat.D(_meta.From)} إلى {DatesErp.Core.Common.UiFormat.D(_meta.To)}" };
            rr.Columns.AddRange(new[] { "رقم المورد", "اسم المورد", "رقم الصنف", "اسم الصنف", "الوحدة", "الرصيد الإفتتاح", "المشتريات", "توريد مخزني", "مردود المشتريات", "صرف مخزني", "المبيعات", "مردود المبيعات", "الكمية المتبقية" });
            foreach (var g in _groups)
            {
                foreach (var r in g.Rows)
                    rr.Rows.Add(new object[] { g.Code, g.Name, r.Code, r.Name, r.Unit, r.Open, r.Purch, r.SupIn, r.PurchRet, r.Issue, r.Sales, r.SalesRet, r.Remain });
                rr.Rows.Add(new object[] { g.Code, g.Name, "الإجمالي", "", "", g.Totals[0], g.Totals[1], g.Totals[2], g.Totals[3], g.Totals[4], g.Totals[5], g.Totals[6], g.Totals[7] });
            }
            AppContainer.Get<ExportPrintService>().ExportExcel(rr);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "SupplierMovement.Excel"); }
    }
}
