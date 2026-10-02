using System.Windows;
using DatesErp.Core.Exceptions;

namespace DatesErp.Desktop.Services;

/// <summary>§28 — حوارات موحدة: لا تظهر مكدسات الاستثناءات للمستخدم أبداً.</summary>
public class DialogService
{
    public void Info(string msg, string title = "نظام Date ERP")
        => MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Error(string msg, string title = "نظام Date ERP")
        => MessageBox.Show(msg, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string msg, string title = "تأكيد العملية")
        => MessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>§44 — نجاح غير حاجب: Toast بدل نافذة معلومات تقطع تدفق الإدخال.</summary>
    public void Toast(string msg, string kind = "ok") => UiToast.Show(msg, kind);

    /// <summary>§44 — تأكيد فعل هدّام بعرض أثره على المخزون/المستندات قبل الإتمام.</summary>
    public bool ConfirmImpact(string msg, params string[] impacts)
        => Confirm(msg + "\n\nأثر هذه العملية:\n- " + string.Join("\n- ", impacts), "تأكيد عملية مؤثرة");

    /// <summary>ترجمة أي استثناء إلى رسالة عربية آمنة + تسجيل التفاصيل في السجل.</summary>
    public void HandleException(Exception ex, string operation)
    {
        ErrorLog.Write(ex, operation);
        string msg = ex switch
        {
            DomainException d => d.Message,
            Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException => new ConcurrencyConflictException().Message,
            _ => "حدث خطأ أثناء تنفيذ العملية.\nالتفاصيل: " + (ex.InnerException?.Message ?? ex.Message) + "\nتم تسجيل الخطأ في سجل النظام."
        };
        Error(msg);
    }
}
