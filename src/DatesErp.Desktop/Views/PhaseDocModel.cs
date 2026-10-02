namespace DatesErp.Desktop.Views;

public class PhaseDocModel
{
    public string CompanyNameAr { get; set; }
    public string CompanyAddress { get; set; }
    public string CompanyPhone { get; set; }
    public byte[] LogoBytes { get; set; }
    public DateTime CapturedAt { get; set; } = DateTime.Now;
    public double[] ColumnWeights { get; set; }
    public List<DatesErp.Desktop.Printing.PrintSection> ExtraSections { get; set; } = new();
    public string DocTitle { get; set; } = "";
    public string DocNo { get; set; } = "";
    public string StatusAr { get; set; } = "";
    public List<(string Label, string Value)> Info { get; set; } = new();
    public string[] Columns { get; set; } = Array.Empty<string>();
    public List<object[]> Rows { get; set; } = new();
    public List<(string Label, string Value)> Totals { get; set; } = new();
    /// <summary>§عنوان قسم الجدول الرئيسي — لكل مستند عنوانه في القالب المرجعي.</summary>
    public string MainTitle { get; set; } = "بنود المستند";
    public string SecondTitle { get; set; } = "";
    public string[] SecondColumns { get; set; } = Array.Empty<string>();
    public List<object[]> SecondRows { get; set; } = new();
    public List<string> Signatures { get; set; } = new();
    public string Notes { get; set; } = "";
    /// <summary>§المستندات الرسمية تُطبع A4 عمودياً؛ تُجعل أفقية فقط للجداول العريضة جداً.</summary>
    public bool Landscape { get; set; }
    /// <summary>§بيان يُطبع أسفل التوقيعات (رقم الأمر/الخطة + وقت الإصدار).</summary>
    public string FooterNote { get; set; } = "";
}
