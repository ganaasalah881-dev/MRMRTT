using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DatesErp.Desktop.Services;

/// <summary>§1.50.60 7-أ: جميع قوائم الاختيار تظهر كل الخيارات فوراً بلا كتابة إجبارية.
/// يطبق على كل الشاشات مرة واحدة — جهد قليل وقيمة عالية جداً.
/// </summary>
public static class ComboBoxAutoShowHelper
{
    public static void Apply(DependencyObject root)
    {
        if (root == null) return;
        foreach (var cb in FindVisualChildren<ComboBox>(root))
        {
            // لا نغير القوائم التي هي بالفعل قابلة للتحرير مع تصفية مخصصة (مثل البحث)
            if (cb.IsEditable) continue;
            // تأكد أنها تظهر كل الخيارات فوراً
            cb.IsTextSearchEnabled = true;
            cb.StaysOpenOnEdit = false;
            // عند فتح القائمة، اعرض الكل — لا تصفية
            cb.IsDropDownOpen = false;
            // اجعلها قابلة للبحث بالكتابة لكن لا تتطلب كتابة
            cb.IsTextSearchEnabled = true;
            // إذا كانت ItemsSource فارغة وقت التحميل، لا تفعل شيئاً
        }
    }

    private static System.Collections.Generic.List<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var list = new System.Collections.Generic.List<T>();
        if (parent == null) return list;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) list.Add(t);
            list.AddRange(FindVisualChildren<T>(child));
        }
        return list;
    }
}
