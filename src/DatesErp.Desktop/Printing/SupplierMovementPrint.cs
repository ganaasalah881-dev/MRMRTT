using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using DatesErp.Application.Services;
using DatesErp.Desktop.Services;

namespace DatesErp.Desktop.Printing;

/// <summary>§B103 — نماذج الأسطر/المجموعات معرّفة في طبقة التطبيق (SupplierMovementService) لتُختبر Headless.</summary>

/// <summary>بيانات ترويسة/تذييل التقرير.</summary>
public sealed record SmMeta(DateTime From, DateTime To, string PrintedBy, DateTime PrintedAt);

public static class SupplierMovementPrint
{
    // ── ألوان التصميم المرجعي (لقطة التقرير المعتمدة) ──
    private static readonly Brush Ink = Brush("#000000");
    private static readonly Brush HeaderBg = Brush("#D9EAF5");   // ترويسة الأعمدة زرقاء فاتحة
    private static readonly Brush TotalBg = Brush("#FBF3D3");   // سطر الإجمالي أصفر فاتح
    private static readonly Brush TotalInk = Brush("#C00000");  // أرقام الإجمالي حمراء
    private static readonly Brush Muted = Brush("#404040");
    private static readonly Pen Grid = new(Brush("#000000"), 0.7);
    private static readonly Pen Box = new(Brush("#000000"), 1.2);
    static SupplierMovementPrint() { Grid.Freeze(); Box.Freeze(); }

    private static Brush Brush(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }

    // أعمدة الجدول (يمين ← يسار) وأوزانها العرضية المستقاة من التصميم المرجعي
    private static readonly string[] L1 = { "رقم الصنف", "اسم الصنف", "الوحدة", "الكمية الواردة", "", "", "الكمية المرتجعة", "", "صافي المبيعات", "", "الكمية المتبقية" };
    private static readonly string[] L2 = { "", "", "", "الرصيد الإفتتاح", "المشتريات", "توريد مخزني", "مردود المشتريات", "صرف مخزني", "المبيعات", "مردود المبيعات", "" };
    private static readonly double[] W = { 0.75, 2.1, 0.65, 0.85, 0.85, 0.85, 0.95, 0.85, 0.8, 0.95, 0.95 };
    private const double RowH = 19, HeadH = 21, GroupH = 21;

    public static string Fmt(double v) => v == Math.Floor(v) ? v.ToString("N0", CultureInfo.InvariantCulture) : v.ToString("N1", CultureInfo.InvariantCulture);

    /// <summary>يبني المستند الكامل (صفحة أو أكثر) بنفس تخطيط التصميم المرجعي.</summary>
    public static FixedDocument Build(SmMeta meta, List<SmGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(groups);
        double w = PrintLayout.A4Height, h = PrintLayout.A4Width; // A4 عرضي كالتصميم المرجعي
        double m = 30, contentW = w - m * 2;
        double[] cw = PrintLayout.ColumnWidths(contentW, W.Length, W);

        // ── وحدات الرسم المسطحة: تعريف مورد / سطر صنف / سطر إجمالي ──
        var units = new List<(byte kind, SmGroup g, SmRow r)>();
        foreach (var g in groups)
        {
            units.Add((0, g, null));
            foreach (var r in g.Rows) units.Add((1, g, r));
            units.Add((2, g, null));
        }

        var doc = new FixedDocument();
        doc.DocumentPaginator.PageSize = new Size(w, h);
        double bottom = h - 46;
        int ui = 0, drawn = 0;
        SmGroup carry = null; // مجموعة مستمرة من صفحة سابقة (يعاد سطر تعريفها)
        int totalUnits = units.Count;

        // عدد الصفحات أولاً لترقيم «س/ص» كما في التصميم المرجعي
        int probe = CountPages(groups, m, bottom);

        while (ui < totalUnits || drawn == 0)
        {
            drawn++;
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                double y = DrawTop(dc, meta, w, m, contentW);
                y = DrawColHeader(dc, y, m, cw);
                if (carry != null) y = DrawGroupRow(dc, y, m, cw, carry);
                bool wrote = false;
                while (ui < totalUnits)
                {
                    var (kind, g, r) = units[ui];
                    double hh = kind == 0 ? GroupH : RowH;
                    if (y + hh > bottom) break;
                    if (kind == 0) y = DrawGroupRow(dc, y, m, cw, g);
                    else if (kind == 1) y = DrawDataRow(dc, y, m, cw, r);
                    else y = DrawTotalRow(dc, y, m, cw, g);
                    carry = g; ui++; wrote = true;
                    // سطر الإجمالي لا يُفصل عن آخر سطر في مجموعته بصرياً: إن لم يتسع
                    // ننقله للصفحة التالية مع إعادة سطر تعريف المجموعة (كالتقرير الورقي).
                    if (ui < totalUnits && units[ui].kind == 2 && y + RowH > bottom) break;
                }
                if (!wrote && carry != null) carry = null; // صفحة بيضاء حمايةً من الحلقات
                if (totalUnits == 0)
                {
                    var msg = PrintRenderer.Text("لا توجد حركات موردين ضمن الفترة المحددة", contentW - 20, 13, true, Muted, TextAlignment.Center);
                    dc.DrawRectangle(Brushes.White, Grid, new Rect(m, y, contentW, 40));
                    dc.DrawText(msg, new Point(m + 10, y + 10));
                    ui = totalUnits = 1; // إنهاء بعد صفحة الرسالة
                }
                DrawFooter(dc, meta, w, h, m, contentW, doc.Pages.Count + 1, probe);
            }
            var page = new FixedPage { Width = w, Height = h, Background = Brushes.White, FlowDirection = FlowDirection.LeftToRight };
            page.Children.Add(new SmPage(visual, w, h));
            page.Measure(new Size(w, h)); page.Arrange(new Rect(0, 0, w, h)); page.UpdateLayout();
            var content = new PageContent(); ((IAddChild)content).AddChild(page); doc.Pages.Add(content);
            if (ui >= totalUnits) break;
            // المجموعة تستمر في الصفحة التالية إلا إذا كانت الصفحة انتهت عند سطر إجماليها
            carry = units[ui - 1].kind == 2 ? null : units[ui - 1].g;
        }
        return doc;
    }

    /// <summary>عدّ الصفحات قبل الرسم (الترقيم «1/2» في التذييل يتطلب معرفة المجموع) — بنفس قواعد الرسم حرفياً.</summary>
    private static int CountPages(List<SmGroup> groups, double m, double bottom)
    {
        if (groups.Count == 0) return 1;
        double top = m + 140 + HeadH * 2;
        double y = top;
        int count = 1;
        foreach (var g in groups)
        {
            if (y + GroupH > bottom) { count++; y = top + GroupH; }
            y += GroupH;
            foreach (var _ in g.Rows)
            {
                if (y + RowH > bottom) { count++; y = top + GroupH; }
                y += RowH;
            }
            if (y + RowH > bottom) { count++; y = top + GroupH; }
            y += RowH;
        }
        return count;
    }

    /// <summary>صندوق الشركة المدوّر + العنوان المسطّر + سطر الفترة — أعلى كل صفحة.</summary>
    private static double DrawTop(DrawingContext dc, SmMeta meta, double w, double m, double contentW)
    {
        double y = m;
        dc.DrawRoundedRectangle(Brushes.White, Box, new Rect(m, y, contentW, 62), 10, 10);
        var co = PrintRenderer.Text("الشركة / المصنع : " + CompanyIdentity.NameAr, contentW * 0.55, 12.5, true, Ink);
        var ad = PrintRenderer.Text("العنوان : " + (CompanyIdentity.Address ?? "-"), contentW * 0.55, 10, false, Muted);
        dc.DrawText(co, new Point(w - m - contentW * 0.55 - 10, y + 8));
        dc.DrawText(ad, new Point(w - m - contentW * 0.55 - 10, y + 8 + co.Height + 2));
        var t1 = PrintRenderer.Text("Tele No : " + (CompanyIdentity.Phone ?? ""), contentW * 0.3, 9.5, false, Ink, TextAlignment.Left);
        var t2 = PrintRenderer.Text("Fax No :", contentW * 0.3, 9.5, false, Ink, TextAlignment.Left);
        var t3 = PrintRenderer.Text("P.O.Box :", contentW * 0.3, 9.5, false, Ink, TextAlignment.Left);
        dc.DrawText(t1, new Point(m + 10, y + 8));
        dc.DrawText(t2, new Point(m + 10, y + 8 + t1.Height + 1));
        dc.DrawText(t3, new Point(m + 10, y + 8 + t1.Height + t2.Height + 2));
        y += 62 + 16;
        var title = PrintRenderer.Text("تقرير حركة الموردين حسب الاصناف تحليلي كميات", contentW, 14, true, Ink, TextAlignment.Center);
        double cx = m + contentW / 2;
        dc.DrawText(title, new Point(m, y));
        dc.DrawLine(new Pen(Ink, 1), new Point(cx - title.Width / 2, y + title.Height + 2), new Point(cx + title.Width / 2, y + title.Height + 2));
        y += title.Height + 10;
        var period = PrintRenderer.Text($"من تاريخ : {meta.From:dd/MM/yyyy} الى تاريخ : {meta.To:dd/MM/yyyy}", contentW, 10.5, true, Ink, TextAlignment.Center);
        dc.DrawText(period, new Point(m, y));
        y += period.Height + 12;
        return y;
    }

    /// <summary>ترويسة الأعمدة بصفّين والخلفيات الزرقاء الفاتحة والمجموعات المدمجة.</summary>
    private static double DrawColHeader(DrawingContext dc, double y, double m, double[] cw)
    {
        double x = m + cw.Sum();
        // الصف الأول: المجموعات المدمجة (colspan) والخلايا الممتدة (rowspan)
        int c = 0;
        while (c < cw.Length)
        {
            int span = L1[c] switch { "الكمية الواردة" => 3, "الكمية المرتجعة" => 2, "صافي المبيعات" => 2, _ => 1 };
            double gw = 0; for (int k = 0; k < span; k++) gw += cw[c + k];
            x -= gw;
            var cell = new Rect(x, y, gw, span == 1 ? HeadH * 2 : HeadH);
            dc.DrawRectangle(HeaderBg, Grid, cell);
            if (L1[c].Length > 0)
                dc.DrawText(PrintRenderer.Text(L1[c], gw - 8, 10.5, true, Ink, TextAlignment.Center), new Point(x + 4, y + (span == 1 ? HeadH - 7 : 5)));
            c += span;
        }
        // الصف الثاني: عناوين الأعمدة الفرعية
        x = m + cw.Sum(); c = 0;
        while (c < cw.Length)
        {
            int span = L1[c] switch { "الكمية الواردة" => 3, "الكمية المرتجعة" => 2, "صافي المبيعات" => 2, _ => 1 };
            if (span > 1)
            {
                for (int k = 0; k < span; k++)
                {
                    x -= cw[c + k];
                    var cell = new Rect(x, y + HeadH, cw[c + k], HeadH);
                    dc.DrawRectangle(HeaderBg, Grid, cell);
                    dc.DrawText(PrintRenderer.Text(L2[c + k], cw[c + k] - 6, 9.5, true, Ink, TextAlignment.Center), new Point(x + 3, y + HeadH + 5));
                }
            }
            else x -= cw[c];
            c += span;
        }
        return y + HeadH * 2;
    }

    private static double DrawGroupRow(DrawingContext dc, double y, double m, double[] cw, SmGroup g)
    {
        double total = cw.Sum();
        dc.DrawRectangle(Brushes.White, Grid, new Rect(m, y, total, GroupH));
        var no = PrintRenderer.Text("رقم المورد : " + g.Code, cw[0] + cw[1] - 10, 10.5, true, Ink, TextAlignment.Right);
        dc.DrawText(no, new Point(m + total - cw[0] - cw[1] + 5, y + 5));
        var nm = PrintRenderer.Text("اسم المورد : " + g.Name, cw[2] + cw[3] + cw[4] + cw[5] - 10, 10.5, true, Ink, TextAlignment.Right);
        dc.DrawText(nm, new Point(m + total - cw[0] - cw[1] - cw[2] - cw[3] - cw[4] - cw[5] + 5, y + 5));
        return y + GroupH;
    }

    private static double DrawDataRow(DrawingContext dc, double y, double m, double[] cw, SmRow r)
    {
        double x = m + cw.Sum();
        string[] cells = { r.Code, r.Name, r.Unit, Fmt(r.Open), Fmt(r.Purch), Fmt(r.SupIn), Fmt(r.PurchRet), Fmt(r.Issue), Fmt(r.Sales), Fmt(r.SalesRet), Fmt(r.Remain) };
        for (int c = 0; c < cells.Length; c++)
        {
            x -= cw[c];
            var cell = new Rect(x, y, cw[c], RowH);
            dc.DrawRectangle(Brushes.White, Grid, cell);
            var align = c == 1 ? TextAlignment.Right : TextAlignment.Center;
            dc.DrawText(PrintRenderer.Text(cells[c], cw[c] - 8, 10, false, Ink, align), new Point(x + 4, y + 4));
        }
        return y + RowH;
    }

    private static double DrawTotalRow(DrawingContext dc, double y, double m, double[] cw, SmGroup g)
    {
        double x = m + cw.Sum();
        for (int c = 0; c < cw.Length; c++)
        {
            x -= cw[c];
            var cell = new Rect(x, y, cw[c], RowH);
            dc.DrawRectangle(TotalBg, Grid, cell);
            string v = c switch
            {
                2 => "الإجمالي",
                3 => Fmt(g.Totals[0]),
                4 => Fmt(g.Totals[1]),
                5 => Fmt(g.Totals[2]),
                6 => Fmt(g.Totals[3]),
                7 => Fmt(g.Totals[4]),
                8 => Fmt(g.Totals[5]),
                9 => Fmt(g.Totals[6]),
                10 => Fmt(g.Totals[7]),
                _ => ""
            };
            if (v.Length > 0)
                dc.DrawText(PrintRenderer.Text(v, cw[c] - 8, 10, true, TotalInk, TextAlignment.Center), new Point(x + 4, y + 4));
        }
        return y + RowH;
    }

    /// <summary>التذييل الثلاثي كالتصميم المرجعي: طبع بواسطة / رقم الصفحة / تاريخ التقرير.</summary>
    private static void DrawFooter(DrawingContext dc, SmMeta meta, double w, double h, double m, double contentW, int pageNo, int pageCount)
    {
        double fy = h - 34;
        dc.DrawLine(new Pen(Ink, 1), new Point(m, fy), new Point(w - m, fy));
        dc.DrawText(PrintRenderer.Text("طبع بواسطة : " + meta.PrintedBy, contentW / 3 - 8, 9.5, false, Ink, TextAlignment.Right), new Point(m + 2 * contentW / 3, fy + 8));
        dc.DrawText(PrintRenderer.Text($"{pageNo}/{pageCount}", contentW / 3 - 8, 9.5, false, Ink, TextAlignment.Center), new Point(m + contentW / 3, fy + 8));
        dc.DrawText(PrintRenderer.Text($"تاريخ التقرير : {meta.PrintedAt:dd/MM/yyyy , hh:mm:ss tt}", contentW / 3 - 8, 9.5, false, Ink, TextAlignment.Left), new Point(m, fy + 8));
    }

    private sealed class SmPage : FrameworkElement
    {
        private readonly DrawingVisual _visual;
        public SmPage(DrawingVisual visual, double width, double height) { _visual = visual; Width = width; Height = height; AddVisualChild(visual); }
        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => index == 0 ? _visual : throw new ArgumentOutOfRangeException(nameof(index));
    }
}
