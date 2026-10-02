using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ClosedXML.Excel;
using DatesErp.Core.Interfaces.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DatesErp.Desktop.Services;

/// <summary>
/// §25 + §التطوير الشامل — محرك الإخراج الاحترافي للتقارير:
/// طباعة بمعاينة (ترويسة شركة + مستخدم + إجماليات + تبريد صفوف + اتجاه تلقائي)،
/// PDF متعدد الصفوف برأس متكرر وترقيم صفحات وأعمدة متناسبة،
/// Excel منسّق (حدود/تبريد/تنسيق أرقام/صف إجماليات/تثبيت الرأس/اتجاه RTL).
/// </summary>
public class ExportPrintService
{
    private readonly DialogService _dialogs;

    public ExportPrintService(DialogService dialogs)
    {
        _dialogs = dialogs;
    }

    private static string CurrentUser()
    {
        try { return AppContainer.Get<ICurrentSession>().UserName ?? "-"; }
        catch { return "-"; }
    }

    /// <summary>§الإجماليات الاحترافية: مجموع كل عمود رقمي (null للأعمدة النصية وغير القابلة للجمع).</summary>
    public static List<double?> ComputeTotals(List<string> columns, List<object[]> rows)
    {
        var totals = new List<double?>();
        for (int c = 0; c < columns.Count; c++)
        {
            // §B84/P1: الأعمدة غير القابلة للجمع (نسب/أسعار/أرقام تعريفية) كان مجموعها مضللاً — تُستبعد.
            if (IsNonSummable(columns[c])) { totals.Add(null); continue; }
            // §B84/P1: حارس السنوات — عمود سنوي (2024، 2025...) لا يُجمع حتى لو بدا رقمياً.
            bool yearHeader = IsYearHeader(columns[c]);
            double sum = 0; bool numeric = false; bool allYearLike = true;
            foreach (var row in rows)
            {
                if (c >= row.Length) continue;
                var v = row[c];
                if (v == null || string.IsNullOrWhiteSpace(v.ToString())) continue;
                if (v is double d) { sum += d; numeric = true; if (d < 1900 || d > 2100 || d != Math.Floor(d)) allYearLike = false; }
                else if (v is int i) { sum += i; numeric = true; if (i < 1900 || i > 2100) allYearLike = false; }
                else if (double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p)) { sum += p; numeric = true; if (p < 1900 || p > 2100 || p != Math.Floor(p)) allYearLike = false; }
                else { numeric = false; break; }
            }
            totals.Add(numeric && rows.Count > 0 && !(yearHeader && allYearLike) ? sum : null);
        }
        return totals;
    }

    /// <summary>§B84/P1: ترويسات لا معنى لجمعها (نسب، متوسطات، أسعار مفردة، أرقام تعريفية).</summary>
    private static bool IsNonSummable(string header)
    {
        if (string.IsNullOrWhiteSpace(header)) return true;
        string[] keys = { "نسبة", "%", "٪", "متوسط", "معدل", "سعر", "السعر", "Price", "price",
            "رقم", "كود", "رمز", "تسلسل", "No.", "Code", "code" };
        foreach (var k in keys)
            if (header.Contains(k)) return true;
        return false;
    }

    /// <summary>§B84/P1: ترويسة عمود سنوي (يُطبَّق عليها حارس السنوات مع فحص القيم).</summary>
    private static bool IsYearHeader(string header)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        return header.Contains("سنة") || header.Contains("السنة") || header.Contains("العام")
            || header.Contains("Year") || header.Contains("year");
    }

    /// <summary>§شريط إجماليات نصي مختصر للعرض داخل الشاشة.</summary>
    public static string TotalsLine(List<string> columns, List<object[]> rows)
    {
        var totals = ComputeTotals(columns, rows);
        var parts = new List<string>();
        for (int c = 0; c < columns.Count; c++)
            // §B84/P2: توحيد الإجماليات على منزلتين عشريتين (كانت N1 هنا وN2 في الخلايا).
            if (totals[c] is double t) parts.Add($"{columns[c]}: {t:N2}");
        return parts.Count > 0 ? "الإجماليات ← " + string.Join(" | ", parts) : "";
    }

    // ═══════════════════════════ الطباعة بمعاينة ═══════════════════════════

    public void Print(ReportResult report)
    {
        try
        {
            var doc = BuildPrintDocument(report);
            var preview = new Views.PrintPreviewWindow(doc, report.TitleAr)
            { Owner = System.Windows.Application.Current.MainWindow };
            preview.ShowDialog();
        }
        catch (Exception ex) { _dialogs.HandleException(ex, "PrintPreview"); }
    }

    // ═══════════════════════════ PDF احترافي ═══════════════════════════

    public void ExportPdf(ReportResult report)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "ملف PDF|*.pdf",
            FileName = SafeFileName(report.TitleAr) + ".pdf"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            WritePdf(report, dlg.FileName);
            _dialogs.Info($"تم تصدير التقرير إلى:\n{dlg.FileName}");
        }
        catch (Exception ex) { _dialogs.HandleException(ex, "ExportPdf"); }
    }

    /// <summary>§B84/P6: كتابة PDF لمسار مباشر بلا حوار حفظ — كان التصدير مرتبطاً بالحوار
    /// فيستحيل إعادة استخدامه (معاينة/أرشفة/إرفاق). ExportPdf أعلاه أصبح غلافاً رفيعاً حولها.</summary>
    public void WritePdf(ReportResult report, string path)
        => Printing.PrintRenderer.ExportPdf(BuildPrintDocument(report), path, report.TitleAr);

    // ═══════════════════════════ Excel احترافي ═══════════════════════════

    public void ExportExcel(ReportResult report)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "ملف Excel|*.xlsx",
            FileName = SafeFileName(report.TitleAr) + ".xlsx"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet(SafeSheetName(report.TitleAr));
            ws.RightToLeft = true;

            // §ترويسة: شركة + تقرير + مستخدم/توقيت
            var coCell = ws.Cell(1, 1);
            coCell.Value = CompanyIdentity.NameAr;
            coCell.Style.Font.Bold = true;
            coCell.Style.Font.FontSize = 16;
            coCell.Style.Font.FontColor = XLColor.FromHtml("#0A246A");
            ws.Cell(2, 1).Value = report.TitleAr;
            ws.Cell(2, 1).Style.Font.Bold = true;
            var meta = ws.Cell(3, 1);
            meta.Value = $"المستخدم: {CurrentUser()} | تاريخ الإصدار: {DateTime.Now:dd/MM/yyyy HH:mm}";
            meta.Style.Font.FontColor = XLColor.Gray;
            meta.Style.Font.FontSize = 9;

            int headRow = 4;
            for (int c = 0; c < report.Columns.Count; c++)
            {
                var cell = ws.Cell(headRow, c + 1);
                cell.Value = report.Columns[c];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#0A246A");
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            }

            var totals = ComputeTotals(report.Columns, report.Rows);
            for (int r = 0; r < report.Rows.Count; r++)
            {
                for (int c = 0; c < report.Columns.Count && c < report.Rows[r].Length; c++)
                {
                    var cell = ws.Cell(headRow + 1 + r, c + 1);
                    SetCell(cell, report.Rows[r][c]);
                    if (r % 2 == 1) cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F2F2F2");
                    cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    if (totals[c] != null && report.Rows[r][c] is double or int)
                        cell.Style.NumberFormat.Format = "#,##0.###";
                }
            }

            // §صف الإجماليات
            int tr = headRow + 1 + report.Rows.Count;
            ws.Cell(tr, 1).Value = "الإجمالي";
            ws.Cell(tr, 1).Style.Font.Bold = true;
            for (int c = 0; c < report.Columns.Count; c++)
            {
                var cell = ws.Cell(tr, c + 1);
                if (totals[c] is double t) { cell.Value = t; cell.Style.NumberFormat.Format = "#,##0.##"; }
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#C9CBA3");
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            }

            int sr = tr + 2;
            foreach (var kv in report.Summary)
            {
                ws.Cell(sr, 1).Value = kv.Key;
                ws.Cell(sr, 2).Value = kv.Value;
                ws.Cell(sr, 1).Style.Font.Bold = true;
                ws.Cell(sr, 2).Style.Font.FontColor = XLColor.FromHtml("#1B4D3E");
                sr++;
            }
            if (!string.IsNullOrWhiteSpace(CompanyIdentity.ReportFooter))
            {
                sr++;
                ws.Cell(sr, 1).Value = CompanyIdentity.ReportFooter;
                ws.Cell(sr, 1).Style.Font.FontColor = XLColor.Gray;
            }
            ws.SheetView.FreezeRows(headRow);
            ws.Columns().AdjustToContents();
            wb.SaveAs(dlg.FileName);
            _dialogs.Info($"تم تصدير التقرير إلى:\n{dlg.FileName}");
        }
        catch (Exception ex)
        {
            _dialogs.HandleException(ex, "ExportExcel");
        }
    }

    private static void SetCell(IXLCell cell, object v)
    {
        switch (v)
        {
            case null: cell.Value = ""; break;
            case double d: cell.Value = d; break;
            case int i: cell.Value = i; break;
            case DateTime dt: cell.Value = dt.ToString("dd/MM/yyyy"); break;
            default: cell.Value = v.ToString(); break;
        }
    }

    // ═══════════════════════════ معاينة الطباعة ═══════════════════════════

    private FixedDocument BuildPrintDocument(ReportResult report)
    {
        var m = new Views.PhaseDocModel
        {
            DocTitle = report.TitleAr, DocNo = "تقرير", StatusAr = "نسخة تقرير",
            Columns = report.Columns.ToArray(), Rows = report.Rows.Select(r => r.ToArray()).ToList(),
            Landscape = false, // §v1.50.25: التقارير عمودية مثل باقي النماذج المعتمدة
        };
        m.Info.Add(("طبع بواسطة", CurrentUser()));
        if (!string.IsNullOrWhiteSpace(report.PeriodLabel)) m.Info.Add(("الفترة", report.PeriodLabel));
        foreach (var kv in report.Summary)
            if (kv.Key.Contains("توقيع")) m.Signatures.Add(kv.Key);
            else m.Info.Add((kv.Key, kv.Value));
        var totals = ComputeTotals(report.Columns, report.Rows);
        for (int c = 0; c < totals.Count; c++)
            if (totals[c] is double value) m.Totals.Add((report.Columns[c], value.ToString("N2")));
        return Views.PhasePrint.Build(m);
    }

    private static string FormatCell(object v) => v switch
    {
        null => "",
        double d => d.ToString("N2", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("dd/MM/yyyy HH:mm"),
        _ => v.ToString()
    };

    private static string SafeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "report" : s;
    }

    private static string SafeSheetName(string s)
    {
        foreach (var c in new[] { '\\', '/', '*', '?', ':', '[', ']' }) s = s.Replace(c, ' ');
        return s.Length > 28 ? s[..28] : (string.IsNullOrWhiteSpace(s) ? "تقرير" : s);
    }
}
