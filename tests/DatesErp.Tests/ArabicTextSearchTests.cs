using DatesErp.Core.Common;
using Xunit;

namespace DatesErp.Tests;

/// <summary>
/// §B106.2 — تطبيع البحث العربي: صور الحرف الواحد يجب أن تتساوى قبل المقارنة،
/// وإلا فشل البحث عن صف يراه المستخدم أمامه («البحث لا يعمل»).
/// </summary>
public class ArabicTextSearchTests
{
    [Theory]
    [InlineData("أحمد", "احمد")]          // همزة على الألف
    [InlineData("إذن", "اذن")]            // همزة تحت الألف
    [InlineData("آمنة", "امنه")]          // مدّة + تاء مربوطة
    [InlineData("خطة", "خطه")]            // تاء مربوطة
    [InlineData("مصطفى", "مصطفي")]        // ألف مقصورة
    [InlineData("مُفوتَر", "مفوتر")]        // تشكيل
    [InlineData("مســلَّم", "مسلم")]        // تطويل + شدة
    [InlineData("٢٠٢٦", "2026")]          // أرقام عربية-هندية
    public void Normalize_Unifies_Arabic_Variants(string written, string typed)
        => Assert.Equal(ArabicText.Normalize(typed), ArabicText.Normalize(written));

    [Theory]
    [InlineData("خطة الإنتاج اليومية", "خطه الانتاج")]
    [InlineData("PLN-٢٠٢٦-0001", "pln-2026")]
    [InlineData("تمر خام - خلاص", "خلاص")]
    public void Normalized_Cell_Contains_Normalized_Term(string cell, string term)
        => Assert.Contains(ArabicText.Normalize(term), ArabicText.Normalize(cell));

    [Fact]
    public void Empty_Term_Is_Empty_After_Normalize()
    {
        Assert.Equal("", ArabicText.Normalize(null));
        Assert.Equal("", ArabicText.Normalize("   "));
    }

    [Fact]
    public void Unrelated_Text_Does_Not_Match()
        => Assert.DoesNotContain(ArabicText.Normalize("تسليم"), ArabicText.Normalize("خطة الإنتاج"));
}
