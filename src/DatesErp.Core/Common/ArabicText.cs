using System.Text;

namespace DatesErp.Core.Common;

/// <summary>
/// §B106.2 — تطبيع النص العربي للبحث والمقارنة.
/// وُضع في Core لا في Desktop كي تغطيه الاختبارات (مشروع الاختبار net8.0
/// ولا يرجع إلى Desktop المستهدف net8.0-windows).
/// </summary>
public static class ArabicText
{
    /// <summary>
    /// يوحّد صور الحرف العربي والأرقام قبل المقارنة:
    /// الهمزات (أ إ آ ٱ) ← ا · التاء المربوطة ة ← ه · الألف المقصورة ى ← ي
    /// ؤ ← و · ئ ← ي · حذف التطويل والتشكيل · الأرقام العربية-الهندية ← لاتينية.
    /// بدونه يفشل البحث عن صف يراه المستخدم أمامه: «احمد» لا تجد «أحمد»،
    /// و«خطه» لا تجد «خطة»، و«2026» لا تجد «٢٠٢٦».
    /// </summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Trim())
        {
            char c = ch;
            switch (c)
            {
                case 'أ': case 'إ': case 'آ': case 'ٱ': case 'ٲ': case 'ٳ': c = 'ا'; break;
                case 'ة': c = 'ه'; break;
                case 'ى': c = 'ي'; break;
                case 'ؤ': c = 'و'; break;
                case 'ئ': c = 'ي'; break;
                case 'ـ': continue;
            }
            if (c >= '\u064B' && c <= '\u0652') continue;                      // التشكيل
            if (c >= '\u0660' && c <= '\u0669') c = (char)('0' + (c - '\u0660'));
            else if (c >= '\u06F0' && c <= '\u06F9') c = (char)('0' + (c - '\u06F0'));
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
