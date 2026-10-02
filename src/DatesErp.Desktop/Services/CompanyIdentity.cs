using System.IO;
using System.Windows.Media.Imaging;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Services;

/// <summary>
/// §الهوية البصرية: مصدر موحّد لبيانات الشركة وشعارها — تقرأ منه شاشات الدخول
/// والنوافذ وكل النماذج والتقارير (طباعة/Excel/PDF) حتى تظهر بهوية واحدة.
/// </summary>
public static class CompanyIdentity
{
    private static string _nameAr, _footer, _phone, _address;
    private static byte[] _logoBytes;
    private static bool _loaded;
    private static readonly object _lock = new();

    /// <summary>إعادة القراءة من قاعدة البيانات (بعد حفظ بيانات الشركة من الإعدادات).</summary>
    public static void Refresh()
    {
        lock (_lock) { _loaded = false; }
        EnsureLoaded();
    }

    private static void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded) return;
            try
            {
                using var scope = AppContainer.NewScope();
                var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
                var c = db.CompanyInfos.AsNoTracking().OrderBy(x => x.Id).FirstOrDefault();
                _nameAr = c?.CompanyNameAr ?? "Date ERP";
                _footer = c?.ReportFooterNote;
                _phone = c?.Phone;
                _address = c?.Address;
                _logoBytes = c?.LogoBytes;
            }
            catch
            {
                _nameAr = "Date ERP";
            }
            _loaded = true;
        }
    }

    public static string NameAr { get { EnsureLoaded(); return _nameAr; } }
    public static string ReportFooter { get { EnsureLoaded(); return _footer; } }
    public static string Phone { get { EnsureLoaded(); return _phone; } }
    public static string Address { get { EnsureLoaded(); return _address; } }
    public static byte[] LogoBytes { get { EnsureLoaded(); return _logoBytes; } }

    /// <summary>الشعار كصورة للعرض في الواجهات — أو الشعار الافتراضي إن لم يُرفع شعار.</summary>
    public static BitmapImage GetLogo(double decodeWidth = 96)
    {
        EnsureLoaded();
        try
        {
            if (_logoBytes is { Length: > 0 })
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.StreamSource = new MemoryStream(_logoBytes);
                img.DecodePixelWidth = (int)decodeWidth;
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.EndInit();
                img.Freeze();
                return img;
            }
        }
        catch { /* شعار تالف — نستخدم الافتراضي */ }
        return null;
    }
}
