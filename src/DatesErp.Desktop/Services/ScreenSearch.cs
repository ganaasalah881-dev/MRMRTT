using System.Windows.Controls;

namespace DatesErp.Desktop.Services;

/// <summary>
/// §البحث والفلترة الموحدة لكل الشاشات: مربع بحث يفلتر صفوف الشبكة بأي نص
/// يظهر في أي عمود (اسم، كود، رقم مستند، تاريخ، حالة...) — لحظياً ومع كل تحديث.
///
/// §B106.2 — أُضيف **تطبيع العربية**. كانت المقارنة حرفية، فيفشل البحث في حالات
/// يراها المستخدم مطابقة تماماً:
///   • الهمزات:      «احمد» لا تجد «أحمد» · «اذن» لا تجد «إذن»
///   • التاء المربوطة: «خطه» لا تجد «خطة» · «دفعه» لا تجد «دفعة»
///   • الألف المقصورة: «مصطفي» لا تجد «مصطفى»
///   • التشكيل:       «مُفوتر» لا تجد بالبحث عن «مفوتر»
///   • الأرقام:       «2026» لا تجد «٢٠٢٦» المعروضة في الشبكة
/// وهذه أسباب شكوى «البحث لا يعمل» رغم أن الصف ظاهر أمام المستخدم.
/// </summary>
public static class ScreenSearch
{
    /// <summary>فلترة قائمة الصفوف حسب نص البحث وعرض النتيجة في الشبكة.</summary>
    public static void Apply<T>(TextBox searchBox, DataGrid grid, List<T> all)
    {
        if (grid == null || all == null) return;
        string term = Normalize(searchBox?.Text);
        grid.ItemsSource = string.IsNullOrEmpty(term)
            ? all
            : all.Where(x => Matches(x, term)).ToList();
    }

    /// <summary>§يفوّض إلى DatesErp.Core.Common.ArabicText — موضعها Core كي تغطيها الاختبارات.</summary>
    public static string Normalize(string s) => DatesErp.Core.Common.ArabicText.Normalize(s);

    private static bool Matches(object row, string term)
    {
        if (row == null) return false;
        foreach (var p in row.GetType().GetProperties())
        {
            try
            {
                var v = p.GetValue(row);
                if (v == null) continue;
                if (Normalize(v.ToString()).Contains(term)) return true;
            }
            catch { /* خاصية غير قابلة للقراءة — نتجاوزها */ }
        }
        return false;
    }
}
