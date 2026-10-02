using ClosedXML.Excel;
using Microsoft.Data.Sqlite;

namespace DatesErp.Tests;

public class DependencyCompatibilityTests
{
    [Fact]
    public void Native_SQLite_Version_In_Use_Is_Not_The_Old_Vulnerable_Binary()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "select sqlite_version()";
        var version = Version.Parse((string)cmd.ExecuteScalar()!);
        // CVE-2025-6965: fixed in SQLite 3.50.2. Verify the loaded native binary, not just NuGet metadata.
        Assert.True(version >= new Version(3, 50, 2), $"Loaded SQLite: {version}");
    }

    [Fact]
    public void Patched_Packaging_Preserves_Arabic_Excel_Export_And_Numeric_Cells()
    {
        using var stream = new MemoryStream();
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.Worksheets.Add("تقرير الإنتاج");
            sheet.RightToLeft = true;
            sheet.Cell(1, 1).Value = "الصنف";
            sheet.Cell(1, 2).Value = "الكمية";
            sheet.Cell(2, 1).Value = "تمر سكري";
            sheet.Cell(2, 2).Value = 1250.5;
            sheet.Cell(2, 2).Style.NumberFormat.Format = "#,##0.00";
            workbook.SaveAs(stream);
        }
        stream.Position = 0;
        using var restored = new XLWorkbook(stream);
        var result = restored.Worksheet("تقرير الإنتاج");
        Assert.True(result.RightToLeft);
        Assert.Equal("تمر سكري", result.Cell(2, 1).GetString());
        Assert.Equal(1250.5, result.Cell(2, 2).GetDouble());
        Assert.Equal("#,##0.00", result.Cell(2, 2).Style.NumberFormat.Format);
    }
    [Fact]
    public void Windows_Rid_Legacy_Uri_Dependency_Is_Pinned_To_Patched_Version()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "DateERP.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var project = System.Xml.Linq.XDocument.Load(Path.Combine(root!.FullName,
            "src/DatesErp.Infrastructure/DatesErp.Infrastructure.csproj"));
        var uri = Assert.Single(project.Descendants("PackageReference"),
            e => (string?)e.Attribute("Include") == "System.Private.Uri");
        Assert.True(Version.Parse((string)uri.Attribute("Version")!) >= new Version(4, 3, 2));
    }

    [Fact]
    public void Legacy_Uri_Pin_Does_Not_Replace_Modern_Runtime_Or_Break_Arabic_Paths()
    {
        Assert.True(typeof(Uri).Assembly.GetName().Version! >= new Version(8, 0));
        var uri = new Uri("https://example.test/استلام/15-09-2026");
        Assert.Equal("/استلام/15-09-2026", Uri.UnescapeDataString(uri.AbsolutePath));
        Assert.True(uri.IsAbsoluteUri);
        Assert.Equal("https", uri.Scheme);
    }

}
