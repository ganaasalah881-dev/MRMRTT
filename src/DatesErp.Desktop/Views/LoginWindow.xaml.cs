using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DatesErp.Application.Services;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;

namespace DatesErp.Desktop.Views;

/// <summary>§11 — شاشة تسجيل الدخول.</summary>
public partial class LoginWindow : Window
{
    public bool LoggedIn { get; private set; }
    // حفظ اسم المستخدم في مساحة هذا الإصدار فقط؛ لا نقرأ/نهاجر remember.txt من تثبيتات سابقة.
    private static string RememberPath => Path.Combine(
        DatesErp.Infrastructure.Connection.AppConfig.ConfigDirectory, "remember.txt");

    public LoginWindow()
    {
        InitializeComponent();
        // §ختم البناء في شاشة الدخول للتحقق من وصول التحديث
        try { TitleBarText.Text += " — إصدار " + Services.BuildInfo.Stamp; } catch { }
        try
        {
            if (File.Exists(RememberPath))
            {
                UserNameBox.Text = File.ReadAllText(RememberPath).Trim();
                RememberMe.IsChecked = true;
            }
        }
        catch { }

        // §الهوية البصرية: اسم الشركة وشعارها وبياناتها من الإعدادات
        try
        {
            CompanyNameText.Text = CompanyIdentity.NameAr;
            TitleBarText.Text = $"{CompanyIdentity.NameAr} — تسجيل الدخول — إصدار {Services.BuildInfo.Stamp}";
            var meta = string.Join("  |  ", new[] { CompanyIdentity.Address, CompanyIdentity.Phone }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            CompanyMetaText.Text = meta;
            var logo = CompanyIdentity.GetLogo(128);
            if (logo != null)
            {
                LogoImage.Source = logo;
                LogoImage.Visibility = Visibility.Visible;
                DefaultLogo.Visibility = Visibility.Collapsed;
            }
        }
        catch { /* الهوية اختيارية — لا تعطل الدخول */ }

        Loaded += (_, _) =>
        {
            // §B84/B2: كان التركيز معكوساً (فارغ ← كلمة المرور!) — الصحيح: فارغ ← الاسم، مملوء ← كلمة المرور.
            if (string.IsNullOrEmpty(UserNameBox.Text)) UserNameBox.Focus();
            else PasswordBox.Focus();
        };

        // §v1.50.30: على أي قاعدة يتحقق الدخول؟ — يعرف المستخدم فوراً لماذا قد "تُخلف" كلمته
        // (SQL المركزية مختلفة عن المحلية mfgsystem_local.db: كلمتان مختلفتان لقاعدتين مختلفتين).
        try
        {
            var cfg = DatesErp.Infrastructure.Connection.AppConfig.Load();
            DbHintText.Text = cfg is { AuthMode: "Local" }
                ? "قاعدة الدخول: المحلية mfgsystem_local.db (وضع محلي مؤقت — كلمة المرور قد تختلف عن قاعدة SQL المركزية)"
                : $"قاعدة الدخول: SQL Server ({cfg?.Server ?? "؟"})";
        }
        catch { }
    }

    // §B84/B5: أزرار شريط العنوان الزخرفية كانت ميتة (✕ لا تغلق!) — الآن وظيفية حقيقية.
    private void FakeMin_Click(object sender, MouseButtonEventArgs e) => WindowState = WindowState.Minimized;
    private void FakeMax_Click(object sender, MouseButtonEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void FakeClose_Click(object sender, MouseButtonEventArgs e) => Close();

    private string EffectivePassword() => ShowPassChk.IsChecked == true ? PasswordText.Text : PasswordBox.Password;
    private void PasswordBox_Changed(object sender, RoutedEventArgs e)
    { if (ShowPassChk.IsChecked == true) PasswordText.Text = PasswordBox.Password; }
    private void PasswordText_Changed(object sender, TextChangedEventArgs e)
    { if (ShowPassChk.IsChecked == true) PasswordBox.Password = PasswordText.Text; }
    private void ShowPass_Toggled(object sender, RoutedEventArgs e)
    {
        bool show = ShowPassChk.IsChecked == true;
        if (show) { PasswordText.Text = PasswordBox.Password; PasswordBox.Visibility = Visibility.Collapsed; PasswordText.Visibility = Visibility.Visible; }
        else { PasswordBox.Password = PasswordText.Text; PasswordText.Visibility = Visibility.Collapsed; PasswordBox.Visibility = Visibility.Visible; }
    }

    /// <summary>§v1.50.30: فك قفل المدير من الشاشة نفسها — بدل «راجع مسؤول النظام» حين يكون هو المسؤول.</summary>
    private void EmergencyUnlock_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "سيتم فك قفل حسابات المديرين وإعادة كلمة المرور إلى المؤقتة:\n\nAdmin@123\n\n" +
                "وسيلزم تغييرها إجبارياً عند أول دخول. هذا الإجراء يعمل على هذا الجهاز فقط،\n" +
                "ويتطلب وجود ملف reset_admin.flag في مجلد الإعدادات (حماية من العبث).\nمتابعة؟",
                "فك قفل حساب المدير — طوارئ", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var auth = scope.ServiceProvider.GetService(typeof(IAuthService)) as IAuthService;
            var r = auth.EmergencyUnlockAdmins();
            MsgText.Text = r.Ok ? "✅ " + r.Message + "\nسجّل الدخول الآن باسم المستخدم نفسه وكلمة Admin@123." : "⛔ " + r.Message;
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "Login.EmergencyUnlock");
            MsgText.Text = "تعذر تنفيذ الاستعادة — راجع سجل التشخيص.";
        }
    }

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Login_Click(sender, e);
    }

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        LoginBtn.IsEnabled = false;
        MsgText.Text = "";
        try
        {
            using var scope = AppContainer.NewScope();
            var auth = scope.ServiceProvider.GetService(typeof(IAuthService)) as IAuthService;
            var result = auth.Login(UserNameBox.Text.Trim(), EffectivePassword());
            if (!result.Success)
            {
                MsgText.Text = result.Message;
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RememberPath));
                if (RememberMe.IsChecked == true) File.WriteAllText(RememberPath, UserNameBox.Text.Trim());
                else if (File.Exists(RememberPath)) File.Delete(RememberPath);
            }
            catch { }

            // §27 — تسجيل الجهاز في جدول ClientMachines
            var registry = scope.ServiceProvider.GetService(typeof(MachineRegistry)) as MachineRegistry;
            registry?.Heartbeat("1.0.0");

            // §لمسة مؤسسية: إظهار آخر دخول — وعي أمني يكشف الدخول غير المصرح به
            if (result.LastLoginDate != null)
                MsgText.Text = $"آخر دخول: {result.LastLoginDate:dd/MM/yyyy HH:mm}";
            if (result.PasswordExpired)
                MsgText.Text = (MsgText.Text ?? "") +
                    $"\n⚠ انتهت صلاحية كلمة المرور ({(result.PasswordAgeDays < 0 ? "لم تُغيَّر من قبل" : result.PasswordAgeDays + " يوماً")}) — يجب تغييرها.";

            // §إصلاح حرج: MustChangePassword كان يُعاد في نتيجة الدخول ولا أحد يتصرف به —
            // فالمستخدم يبقى موسوماً إلى الأبد بلا سبيل لإرضاء الشرط. الآن يُفرض التغيير.
            if (result.MustChangePassword)
            {
                var cp = new ChangePasswordWindow(result.UserId, forced: true, hideOld: !result.PasswordExpired) { Owner = this };
                if (cp.ShowDialog() != true)
                {
                    MsgText.Text = "لم تُغيَّر كلمة المرور — لا يمكن دخول النظام قبل تغييرها.";
                    return;
                }
            }

            LoggedIn = true;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "Login");
            MsgText.Text = "تعذر الاتصال بالخادم.\nتأكد من اتصال الشبكة وتشغيل الخادم.";
        }
        finally
        {
            LoginBtn.IsEnabled = true;
        }
    }
}
