using System.Windows.Input;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Connection;
using DatesErp.Infrastructure.Persistence;
using DatesErp.Infrastructure.Session;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;

namespace DatesErp.Desktop.Views.Screens;

public partial class SystemInfoView : UserControl
{
    private byte[] _pendingLogo; // null = بلا تغيير، طول صفر = إزالة الشعار
    private bool _logoChanged;

    public SystemInfoView()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        var cfg = AppConfig.Load();
        var session = AppContainer.Get<SessionContext>();

        AppVersionText.Text = $"إصدار التطبيق: {AssemblyVersion()}";
        ServerText.Text = $"الخادم: {cfg?.Server ?? "غير مُعد"}";
        DbText.Text = $"قاعدة البيانات: {cfg?.Database ?? "—"}";
        AuthText.Text = $"المصادقة: {(cfg?.AuthMode == "Sql" ? "SQL Server Authentication" : "Windows Authentication")}";
        MachineText.Text = $"اسم الجهاز: {Environment.MachineName} | مستخدم ويندوز: {Environment.UserName}";
        UserText.Text = $"مستخدم النظام الحالي: {session.UserName ?? "—"}";

        try
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var dbv = db.DbVersions.OrderByDescending(v => v.Id).FirstOrDefault();
            DbVersionText.Text = $"إصدار قاعدة البيانات: {dbv?.VersionNumber ?? "غير معروف"}";

            // §بيانات الشركة والهوية
            var c = db.CompanyInfos.OrderBy(x => x.Id).FirstOrDefault();
            if (c != null)
            {
                CompNameArBox.Text = c.CompanyNameAr;
                CompNameEnBox.Text = c.CompanyNameEn;
                CompAddressBox.Text = c.Address;
                CompPhoneBox.Text = c.Phone;
                CompEmailBox.Text = c.Email;
                CompTaxBox.Text = c.TaxNumber;
                CompFooterBox.Text = c.ReportFooterNote;
                ShowLogo(c.LogoBytes);
            }
            _logoChanged = false;
        }
        catch (Exception ex)
        {
            DbVersionText.Text = "إصدار قاعدة البيانات: تعذر القراءة (انقطع الاتصال؟)";
            ErrorLog.Write(ex, "SystemInfo");
        }
    }

    private void ShowLogo(byte[] bytes)
    {
        if (bytes is { Length: > 0 })
        {
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.StreamSource = new MemoryStream(bytes);
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                LogoPreview.Source = img;
                LogoPreview.Visibility = Visibility.Visible;
                LogoPlaceholder.Visibility = Visibility.Collapsed;
                return;
            }
            catch { }
        }
        LogoPreview.Source = null;
        LogoPreview.Visibility = Visibility.Collapsed;
        LogoPlaceholder.Visibility = Visibility.Visible;
    }

    private void UploadLogo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "اختر شعار الشركة",
            Filter = "صور|*.png;*.jpg;*.jpeg;*.bmp;*.ico|كل الملفات|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var bytes = File.ReadAllBytes(dlg.FileName);
            if (bytes.Length > 2 * 1024 * 1024)
            { AppContainer.Get<DialogService>().Error("حجم الشعار كبير — اختر صورة أقل من 2 ميجابايت."); return; }
            _pendingLogo = bytes;
            _logoChanged = true;
            ShowLogo(bytes);
        }
        catch (Exception ex)
        {
            AppContainer.Get<DialogService>().HandleException(ex, "Settings.UploadLogo");
        }
    }

    private void RemoveLogo_Click(object sender, RoutedEventArgs e)
    {
        _pendingLogo = Array.Empty<byte>();
        _logoChanged = true;
        ShowLogo(null);
    }

    private void SaveCompany_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(CompNameArBox.Text))
            { AppContainer.Get<DialogService>().Error("أدخل اسم الشركة (عربي)."); return; }

            // §B110 — الحفظ عبر طبقة الخدمات (صلاحية «الإعدادات/تعديل» + تدقيق) بدل الكتابة المباشرة
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<MasterDataService>();
            var r = svc.SaveCompanyInfo(
                CompNameArBox.Text, CompNameEnBox.Text?.Trim(), CompAddressBox.Text?.Trim(),
                CompPhoneBox.Text?.Trim(), CompEmailBox.Text?.Trim(), CompTaxBox.Text?.Trim(),
                CompFooterBox.Text?.Trim(), _pendingLogo, _logoChanged);
            if (!r.Ok) { AppContainer.Get<DialogService>().Error(r.Message); return; }

            CompanyIdentity.Refresh(); // تظهر الهوية فوراً في الشاشات والتقارير
            _logoChanged = false;
            AppContainer.Get<DialogService>().Info("تم حفظ بيانات الشركة — ستظهر في شاشة الدخول وكل النماذج والتقارير.");
        }
        catch (Exception ex)
        {
            AppContainer.Get<DialogService>().HandleException(ex, "Settings.SaveCompany");
        }
    }

    private static string AssemblyVersion()
        => typeof(SystemInfoView).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>§الفحص الذاتي — يُنفَّذ ويكتب تقريراً، ثم نُظهر الخلاصة ومسار الملف.</summary>
    private void IntegrityCheck_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var rows = AppContainer.Get<DatesErp.Core.Interfaces.Services.IInventoryIntegrityService>().Check();
            var dlg = AppContainer.Get<DialogService>();
            if (rows.Count == 0)
            {
                dlg.Toast("مطابقة المخزون: كل الأرصدة تتفق مع مجموع حركاتها ✔", "ok");
                return;
            }
            var sb = new System.Text.StringBuilder($"وُجدت فروقات في {rows.Count} بنداً (الأكبر أولاً):\n\n");
            foreach (var r in rows.Take(15))
                sb.AppendLine($"• {r.Subject}: رصيد {r.Balance:N2} — دفتر {r.Ledger:N2} — فرق {r.Diff:N2}");
            if (rows.Count > 15) sb.AppendLine($"… و{rows.Count - 15} بنداً آخر");
            dlg.Info(sb.ToString(), "فحص مطابقة المخزون");
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Integrity.Check"); }
    }

    private void RunSelfTest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            var report = Services.SelfDiagnostic.Run();
            int fail = report.Split('\n').Count(l => l.Contains("❌"));
            int pass = report.Split('\n').Count(l => l.Contains("✅"));
            SelfTestResult.Text = fail == 0
                ? $"✅ لا خلل بنيوي — {pass} فحصاً نجح."
                : $"❌ {fail} إخفاقاً من {pass + fail} فحصاً — أرسل الملف للمطوّر.";
            SelfTestResult.Foreground = fail == 0
                ? (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#15803D")
                : (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#B91C1C");
            SelfTestPath.Text = "الملف: " + Services.SelfDiagnostic.ReportPath;
        }
        catch (Exception ex)
        {
            SelfTestResult.Text = "تعذّر إكمال الفحص: " + ex.Message;
        }
        finally { Mouse.OverrideCursor = null; }
    }

    private void OpenReportFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Services.SelfDiagnostic.ReportDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Services.SelfDiagnostic.ReportDirectory,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "SelfTest.OpenFolder"); }
    }

    private void EditConnection_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ConnectionSetupWindow();
        if (dlg.ShowDialog() == true)
        {
            AppContainer.Get<DialogService>().Info("تم تحديث إعداد الاتصال. أعد تشغيل التطبيق ليأخذ الإعداد الجديد مفعوله الكامل.");
            Load();
        }
    }
}
