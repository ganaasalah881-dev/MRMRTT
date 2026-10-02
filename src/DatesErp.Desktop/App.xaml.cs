using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using DatesErp.Desktop.Services;
using DatesErp.Desktop.Views;

namespace DatesErp.Desktop;

/// <summary>
/// نقطة انطلاق تطبيق Date ERP — سطح مكتب WPF متعدد المستخدمين عبر شبكة LAN
/// التدفق: Splash ← فحص الإعداد ← فحص الخادم وقاعدة البيانات ← تسجيل الدخول ← لوحة التحكم
/// </summary>
public partial class App : System.Windows.Application
{
    // §أقرب نقطة ممكنة: المُنشئ الثابت يعمل قبل أي شيء آخر — نسجل الوصول إليه فوراً
    static App()
    {
        BootTrace.Step("=== بداية تشغيل جديدة ===");
        BootTrace.Step("App static ctor: تم تحميل فئة التطبيق.");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            BootTrace.Fail("AppDomain.UnhandledException", ex);
            ErrorLog.Write(ex, "AppDomain");
            try
            {
                MessageBox.Show(
                    "حدث خطأ جسيم أثناء تشغيل النظام:\n" + Describe(ex) +
                    "\n\nالتفاصيل في ملف أثر الإقلاع لمجلد بيانات هذا الإصدار:\n" + BootTrace.FilePath +
                    "\nأرسل محتوى هذا الملف للدعم الفني.",
                    "نظام Date ERP — فشل البدء", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        };
    }

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        BootTrace.Step("OnStartup: دخول نقطة البداية.");
        try
        {
            base.OnStartup(e);
            BootTrace.Step("OnStartup: base.OnStartup اكتمل.");

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                ErrorLog.Write(args.Exception, "Task");
                args.SetObserved();
            };

            BootTrace.Step("OnStartup: ضبط الثقافة والاتجاه...");
            // §21/§22 — الأرقام إنجليزية (0-9) والتواريخ 28/08/2026 في كل التطبيق:
            // الثقافة الحالية تتحكم بتنسيق الأرقام/التواريخ في كل النصوص والاستيفاء،
            // بينما النصوص نفسها عربية ثابتة والاتجاه يمين-يسار.
            var latin = new CultureInfo("en-GB"); // أرقام لاتينية + تاريخ يوم/شهر/سنة
            CultureInfo.DefaultThreadCurrentCulture = latin;
            CultureInfo.DefaultThreadCurrentUICulture = latin;
            FrameworkElement.FlowDirectionProperty.OverrideMetadata(typeof(FrameworkElement),
                new FrameworkPropertyMetadata(FlowDirection.RightToLeft));
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
                new FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage("en-GB")));
            BootTrace.Step("OnStartup: الثقافة والاتجاه تم ضبطهما.");

            BootTrace.Step("OnStartup: إنشاء Bootstrapper...");
            var boot = new Bootstrapper();
            BootTrace.Step("OnStartup: استدعاء boot.Run...");
            boot.Run(this);
            BootTrace.Step("OnStartup: boot.Run انتهى (اكتمل الإقلاع أو خرج التطبيق).");
        }
        catch (Exception ex)
        {
            BootTrace.Fail("OnStartup", ex);
            ErrorLog.Write(ex, "Startup");
            try
            {
                MessageBox.Show(
                    "تعذر بدء تشغيل النظام:\n" + Describe(ex) +
                    "\n\nالتفاصيل في ملف أثر الإقلاع لمجلد بيانات هذا الإصدار:\n" + BootTrace.FilePath +
                    "\nأرسل محتوى هذا الملف للدعم الفني.",
                    "نظام Date ERP — فشل البدء", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
            Shutdown(-1);
        }
    }

    /// <summary>وصف موجز للاستثناء مع سببه الداخلي — للتشخيص السريع عند فشل البدء.</summary>
    private static string Describe(Exception ex)
    {
        if (ex == null) return "خطأ غير معروف.";
        var msg = ex.GetType().Name + ": " + ex.Message;
        if (ex is System.Reflection.ReflectionTypeLoadException rtle && rtle.LoaderExceptions.Length > 0)
            msg += "\nالسبب: " + (rtle.LoaderExceptions[0]?.Message ?? "-");
        if (ex.InnerException != null)
            msg += "\nالسبب: " + ex.InnerException.Message;
        return msg;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write(e.Exception, "UI");
        BootTrace.Fail("UI (Dispatcher)", e.Exception);
        // §28: لا نعرض StackTrace للمستخدم — رسالة عربية واضحة فقط
        MessageBox.Show(
            "حدث خطأ أثناء تنفيذ العملية.\n\nتم تسجيل الخطأ.\nيرجى مراجعة مسؤول النظام.",
            "نظام Date ERP", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
