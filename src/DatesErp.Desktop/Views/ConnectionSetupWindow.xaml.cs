using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DatesErp.Infrastructure.Connection;

namespace DatesErp.Desktop.Views;

/// <summary>§12 — شاشة إعداد الاتصال بالخادم المركزي مع فحص تفصيلي.</summary>
public partial class ConnectionSetupWindow : Window
{
    public bool SavedSuccessfully { get; private set; }

    // §السبب الجذري لخطأ NullReferenceException:
    // الـComboBox في XAML يحمل SelectedIndex="0"، فيُطلق SelectionChanged أثناء
    // InitializeComponent() — أي قبل أن يُنشئ XAML العناصر UidLabel/UidBox/PwdLabel/PwdBox
    // (التي تأتي بعد الـComboBox في الشجرة). فكان AuthModeChanged يلمس عناصر ما زالت null.
    // الحل: حارس _ready يمنع المعالج قبل اكتمال التهيئة، ثم نطبّق الحالة الظاهرية مرة
    // واحدة في OnInitialized بعد اكتمال إنشاء كل العناصر.
    private bool _ready;

    public ConnectionSetupWindow()
    {
        InitializeComponent();
        var cfg = AppConfig.Load();
        if (cfg != null)
        {
            ServerBox.Text = cfg.Server ?? "";
            DatabaseBox.Text = cfg.Database ?? "";
            AuthModeBox.SelectedIndex = cfg.AuthMode == "Sql" ? 1 : 0;
            UidBox.Text = cfg.SqlUid ?? "";
            PwdBox.Password = cfg.AuthMode == "Sql" ? Protect.Unprotect(cfg.EncryptedSqlPassword) ?? "" : "";
            TrustCertificateBox.IsChecked = cfg.TrustServerCertificate;
            // §الوضع المحلي: لا خادم SQL ولا مصادقة — نُظهر الخيار المطابق
            if (cfg.AuthMode == "Local")
            {
                LocalModeHint.Visibility = Visibility.Visible;
                SqlFieldsPanel.Visibility = Visibility.Collapsed;
            }
        }
        // §بعد اكتمال InitializeComponent: كل العناصر موجودة الآن، فالتفعيل آمن
        _ready = true;
        ApplyAuthModeVisibility();
    }

    /// <summary>§يُستدعى بعد اكتمال إنشاء كل عناصر الشاشة — تطبيق الحالة الظاهرية آمن هنا.</summary>
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        _ready = true;
        ApplyAuthModeVisibility();
    }

    private void AuthModeChanged(object sender, SelectionChangedEventArgs e)
    {
        // §الحارس: لا تلمس أي عنصر قبل اكتمال التهيئة (يمنع NullReferenceException الجذري)
        if (!_ready) return;
        ApplyAuthModeVisibility();
        ConnectionInputChanged(sender, e);
    }

    /// <summary>
    /// §إظهار/إخفاء حقول SQL بحسب طريقة المصادقة — بفحوصات null صريحة على كل عنصر
    /// حتى لا ينهار إن استُدعيت قبل اكتمال أي عنصر.
    /// </summary>
    private void ApplyAuthModeVisibility()
    {
        if (AuthModeBox == null) return;                 // لم يُنشأ بعد
        bool sqlAuth = AuthModeBox.SelectedIndex == 1;   // 0=Windows, 1=Sql
        if (UidLabel != null) UidLabel.Visibility = sqlAuth ? Visibility.Visible : Visibility.Collapsed;
        if (UidBox != null) UidBox.Visibility = sqlAuth ? Visibility.Visible : Visibility.Collapsed;
        if (PwdLabel != null) PwdLabel.Visibility = sqlAuth ? Visibility.Visible : Visibility.Collapsed;
        if (PwdBox != null) PwdBox.Visibility = sqlAuth ? Visibility.Visible : Visibility.Collapsed;
        if (SqlFieldsPanel != null) SqlFieldsPanel.Visibility = sqlAuth ? Visibility.Visible : Visibility.Collapsed;
        if (LocalModeHint != null) LocalModeHint.Visibility = Visibility.Collapsed;
    }

    private Core.Interfaces.Services.ConnectionTestResult RunTest()
    {
        var tester = new ConnectionTester();
        return tester.Test(
            ServerBox.Text.Trim(),
            DatabaseBox.Text.Trim(),
            AuthModeBox.SelectedIndex == 1 ? "Sql" : "Windows",
            UidBox.Text.Trim(),
            PwdBox.Password,
            TrustCertificateBox.IsChecked == true);
    }

    // أي تعديل بعد نجاح الفحص يستلزم فحصاً جديداً قبل الحفظ.
    private void ConnectionInputChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready || SaveBtn == null || TestBtn == null) return;
        SaveBtn.IsEnabled = false;
        SaveBtn.IsDefault = false;
        TestBtn.IsDefault = true;
        if (ResultMsg != null) ResultMsg.Text = "تغيّرت الإعدادات — اختبر الاتصال قبل الحفظ.";
    }

    private void SetCheck(TextBlock el, bool ok, string label)
    {
        el.Text = $"{label}:  {(ok ? "✓" : "✗")}";
        el.Foreground = ok ? Brushes.Green : Brushes.Red;
        el.FontWeight = FontWeights.Bold;
    }

    private void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ServerBox.Text) || string.IsNullOrWhiteSpace(DatabaseBox.Text))
        {
            MessageBox.Show("أدخل اسم الخادم واسم قاعدة البيانات.", "إعداد الاتصال", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        TestBtn.IsEnabled = false;
        ResultMsg.Text = "جارٍ الفحص...";
        try
        {
            var r = RunTest();
            SetCheck(ChkServer, r.ServerReachable, "الخادم (الخادم المركزي)");
            SetCheck(ChkSql, r.SqlServerResponding, "خدمة SQL Server");
            SetCheck(ChkDb, r.DatabaseExists, "قاعدة البيانات");
            SetCheck(ChkLogin, r.LoginOk, "بيانات الدخول");
            SetCheck(ChkConn, r.ConnectionOk, "الاتصال الكامل");
            ResultMsg.Text = r.Message;
            SaveBtn.IsEnabled = r.AllOk;
            // §B84/K1: Enter يختبر أولاً، وبعد نجاح الاختبار يتحول الافتراضي لزر الحفظ.
            TestBtn.IsDefault = !r.AllOk;
            SaveBtn.IsDefault = r.AllOk;
            if (!r.AllOk)
            {
                ResultMsg.Foreground = Brushes.Red;
                // §7: تسجيل السبب الحقيقي في errors.log بدل رسالة عامة فقط
                Services.ErrorLog.WriteInfo(
                    $"[اختبار اتصال فاشل] الخادم={ServerBox.Text.Trim()} القاعدة={DatabaseBox.Text.Trim()} " +
                    $"المصادقة={(AuthModeBox.SelectedIndex == 1 ? "Sql" : "Windows")}\n" +
                    $"  الخادم قابل للوصول: {r.ServerReachable}\n" +
                    $"  SQL Server يستجيب: {r.SqlServerResponding}\n" +
                    $"  القاعدة موجودة: {r.DatabaseExists}\n" +
                    $"  الدخول سليم: {r.LoginOk}\n" +
                    $"  الاتصال الكامل: {r.ConnectionOk}\n" +
                    $"  السبب: {r.Message}");
            }
            else
            {
                ResultMsg.Foreground = Brushes.Green;
                Services.ErrorLog.WriteInfo($"[اختبار اتصال ناجح] {ServerBox.Text.Trim()} / {DatabaseBox.Text.Trim()} — {r.ServerVersion}");
            }
        }
        finally
        {
            TestBtn.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveBtn.IsEnabled) return;
        // §13: كلمة مرور SQL إن لزم تُشفَّر بـ DPAPI ولا تُخزن نصاً صريحاً
        // §الحفاظ على AppVersion القائم (لا تصفيره) — والإعداد القديم إن وُجد
        var existing = AppConfig.Load();
        var cfg = new AppConfig
        {
            Server = ServerBox.Text.Trim(),
            Database = DatabaseBox.Text.Trim(),
            AuthMode = AuthModeBox.SelectedIndex == 1 ? "Sql" : "Windows",
            SqlUid = AuthModeBox.SelectedIndex == 1 ? UidBox.Text.Trim() : null,
            EncryptedSqlPassword = AuthModeBox.SelectedIndex == 1 ? Protect.ProtectText(PwdBox.Password) : null,
            TrustServerCertificate = TrustCertificateBox.IsChecked == true,
            AppVersion = existing?.AppVersion ?? "1.0.0"
        };
        if (cfg.AuthMode == "Sql" && !string.IsNullOrEmpty(PwdBox.Password)
            && string.IsNullOrEmpty(cfg.EncryptedSqlPassword))
        {
            MessageBox.Show("تعذر تشفير كلمة المرور؛ لم يُحفظ الإعداد.", "إعداد الاتصال",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        cfg.Save();
        Services.ErrorLog.WriteInfo($"[حفظ إعداد الاتصال] {cfg.Server} / {cfg.Database} / {cfg.AuthMode}");
        SavedSuccessfully = true;
        MessageBox.Show("تم حفظ إعداد الاتصال بنجاح.\nلن تظهر هذه الشاشة مجدداً.", "إعداد الاتصال",
            MessageBoxButton.OK, MessageBoxImage.Information);
        DialogResult = true;
        Close();
    }
}
