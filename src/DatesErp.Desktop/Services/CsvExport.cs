using System.Text;

namespace DatesErp.Desktop.Services;

/// <summary>§44 — سياسة الأرقام المركزية: كجم N2، كراتين N0، نسب N1، أسعار N2.</summary>
public static class UiFormat
{
    public static string Kg(double v) => v.ToString("N2");
    public static string Cartons(double v) => Math.Round(v).ToString("N0");
    public static string Rate(double v) => v.ToString("N1");
    public static string Price(double v) => v.ToString("N2");
}

/// <summary>
/// §44 — تصدير CSV متوافق مع Excel (BOM عربي) لأي جدول معروض،
/// يُستخدم كزر موحّد حين لا تملك الشاشة مصدراً خاصاً للتصدير.
/// </summary>
public static class CsvExport
{
    public static bool ExportGrid(System.Windows.Controls.DataGrid grid, string title)
    {
        if (grid == null || grid.Items.Count == 0) return false;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "ملف CSV (يفتح في Excel)|*.csv",
            FileName = MakeSafe(title) + ".csv"
        };
        if (dlg.ShowDialog() != true) return true;

        var sb = new StringBuilder();
        var headers = new List<string>();
        foreach (var col in grid.Columns)
            headers.Add(Csv(col.Header?.ToString() ?? ""));
        if (headers.Count > 0) sb.AppendLine(string.Join(",", headers));

        foreach (var item in grid.Items)
        {
            var cells = new List<string>();
            if (item is System.Data.DataRowView drv)
            {
                foreach (var col in grid.Columns)
                {
                    var path = ((col as System.Windows.Controls.DataGridBoundColumn)?.Binding as System.Windows.Data.Binding)?.Path?.Path;
                    object v = path != null && drv.Row.Table.Columns.Contains(path) ? drv.Row[path] : null;
                    cells.Add(Csv(v?.ToString() ?? ""));
                }
            }
            else
            {
                foreach (var col in grid.Columns)
                {
                    var path = ((col as System.Windows.Controls.DataGridBoundColumn)?.Binding as System.Windows.Data.Binding)?.Path?.Path;
                    object v = path != null ? item?.GetType().GetProperty(path)?.GetValue(item) : null;
                    cells.Add(Csv(v?.ToString() ?? ""));
                }
            }
            sb.AppendLine(string.Join(",", cells));
        }

        System.IO.File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        return true;
    }

    private static string Csv(string s)
        => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static string MakeSafe(string s)
    {
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }
}
