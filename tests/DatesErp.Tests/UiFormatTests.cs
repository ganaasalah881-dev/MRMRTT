using DatesErp.Core.Common;
using Xunit;

namespace DatesErp.Tests;

/// <summary>§معيار الواجهات: الأرقام إنجليزية والتاريخ موحّد مهما كانت ثقافة الجهاز.</summary>
public class UiFormatTests
{
    [Fact]
    public void Dates_Use_Unified_dd_MM_yyyy_Format()
    {
        var d = new DateTime(2026, 8, 28, 14, 30, 0);
        Assert.Equal("28/08/2026", UiFormat.D(d));
        Assert.Equal("28/08/2026 14:30", UiFormat.DT(d));
        Assert.Equal("14:30", UiFormat.T(d));
        Assert.Equal("-", UiFormat.D((DateTime?)null));
    }

    [Fact]
    public void Numbers_Are_English_Digits_With_Thousands_Separator()
    {
        Assert.Equal("10,025.5", UiFormat.N(10025.5));
        Assert.Equal("18,000", UiFormat.N0(18000));
        Assert.Equal("85%", UiFormat.Pct(85));
        Assert.False(UiFormat.ContainsArabicDigits(UiFormat.N(1234567.89)));
        Assert.True(UiFormat.ContainsArabicDigits("١٠٠٢٥"));
    }
    [Fact]
    public void Status_Colors_And_Names_Are_Unified()
    {
        Assert.Equal("#EF6C00", UiFormat.StatusHex(DocStatuses.InProgress));
        Assert.Equal("#2E7D32", UiFormat.StatusHex(DocStatuses.Completed));
        Assert.Equal("قيد التنفيذ", UiFormat.StatusAr(DocStatuses.InProgress));
        Assert.Equal("مجدول", UiFormat.StatusAr(DocStatuses.Scheduled));
        Assert.Equal("متوقف", UiFormat.StatusAr(DocStatuses.Stopped));
    }

    [Fact]
    public void Formatting_Is_Culture_Independent()
    {
        // حتى على جهاز بلغته العربية تبقى الأرقام إنجليزية والتنسيق موحداً
        var old = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ar-YE");
            Assert.Equal("28/08/2026", UiFormat.D(new DateTime(2026, 8, 28)));
            Assert.Equal("5,000.0", UiFormat.N(5000.0));
            Assert.False(UiFormat.ContainsArabicDigits(UiFormat.N(999999.0)));
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = old; }
    }
}
