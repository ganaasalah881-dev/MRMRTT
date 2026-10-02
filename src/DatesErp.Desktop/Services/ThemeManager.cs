using System.Windows;
using System.Windows.Media;
using DatesErp.Core.Interfaces.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Services;

/// <summary>
/// طبقة تطبيق الثيم الوحيدة في الواجهة. تعدّل موارد Application المشتركة، لذلك تتحدث
/// النوافذ الحالية والجديدة معاً دون إعادة تعريف Style في الشاشات أو لمس منطق الأعمال.
/// </summary>
public static class ThemeManager
{
    public static event EventHandler Changed;
    public static ThemeProfileDto Current { get; private set; } = ThemeProfileDefaults.Light();
    private static bool _loaded;

    private static readonly Dictionary<string, string> ColorKeys = new(StringComparer.Ordinal)
    {
        ["ThemePrimaryColor"] = nameof(ThemeProfileDto.PrimaryColor),
        ["ThemeSecondaryColor"] = nameof(ThemeProfileDto.SecondaryColor),
        ["ThemeBackgroundColor"] = nameof(ThemeProfileDto.BackgroundColor),
        ["ThemeWindowBackgroundColor"] = nameof(ThemeProfileDto.WindowBackgroundColor),
        ["ThemeTableBackgroundColor"] = nameof(ThemeProfileDto.TableBackgroundColor),
        ["ThemeTableHeaderColor"] = nameof(ThemeProfileDto.TableHeaderColor),
        ["ThemeTextColor"] = nameof(ThemeProfileDto.TextColor),
        ["ThemeSecondaryTextColor"] = nameof(ThemeProfileDto.SecondaryTextColor),
        ["ThemeButtonColor"] = nameof(ThemeProfileDto.ButtonColor),
        ["ThemeButtonHoverColor"] = nameof(ThemeProfileDto.ButtonHoverColor),
        ["ThemeButtonPressedColor"] = nameof(ThemeProfileDto.ButtonPressedColor),
        ["ThemeBorderColor"] = nameof(ThemeProfileDto.BorderColor),
        ["ThemeSuccessColor"] = nameof(ThemeProfileDto.SuccessColor),
        ["ThemeWarningColor"] = nameof(ThemeProfileDto.WarningColor),
        ["ThemeErrorColor"] = nameof(ThemeProfileDto.ErrorColor),
        ["ThemeInfoColor"] = nameof(ThemeProfileDto.InfoColor),
        ["ThemeSelectedRowColor"] = nameof(ThemeProfileDto.SelectedRowColor),
        ["ThemeHoverRowColor"] = nameof(ThemeProfileDto.HoverRowColor),
        ["ThemeAlternateRowColor"] = nameof(ThemeProfileDto.AlternateRowColor),
        ["ThemeCardBackgroundColor"] = nameof(ThemeProfileDto.CardBackgroundColor),
        ["ThemeInputBackgroundColor"] = nameof(ThemeProfileDto.InputBackgroundColor),
        ["ThemeTitleBarColor"] = nameof(ThemeProfileDto.TitleBarColor)
    };

    public static void LoadSaved()
    {
        if (_loaded) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IThemeSettingsService>().Load();
            var profile = catalog.Profiles.FirstOrDefault(x => string.Equals(x.Name, catalog.ActiveName, StringComparison.OrdinalIgnoreCase))
                          ?? ThemeProfileDefaults.Light();
            Apply(profile, false);
        }
        catch
        {
            Apply(ThemeProfileDefaults.Light(), false);
        }
        _loaded = true;
    }

    public static void Apply(ThemeProfileDto profile, bool notify = true)
    {
        if (profile == null) return;
        Current = profile.Clone();
        var visual = VisualProfile(Current);
        var resources = System.Windows.Application.Current?.Resources;
        if (resources == null) return;

        foreach (var pair in ColorKeys)
        {
            var colorText = GetColorText(visual, pair.Value);
            SetColor(resources, pair.Key, colorText);
            SetBrush(resources, pair.Key.Replace("Color", "Brush"), colorText);
        }
        // حدود الجداول/الحقول وحبرها تُشتق من خلفيتها بدلاً من لون باهت ثابت:
        // نسبة 3:1 للخطوط و4.5:1 للنص العادي في الثيمات المضمنة والمخصصة.
        var tableBg = ParseColor(visual.TableBackgroundColor, Colors.White);
        var inputBg = ParseColor(visual.InputBackgroundColor, Colors.White);
        var headerBg = ParseColor(visual.TableHeaderColor, Color.FromRgb(10, 36, 106));
        var borderColor = ParseColor(visual.BorderColor, Color.FromRgb(100, 116, 139));
        SetBrush(resources, "ThemeGridLineBrush", VisibleRule(borderColor, tableBg).ToString());
        SetBrush(resources, "ThemeInputBorderBrush", VisibleRule(borderColor, inputBg).ToString());
        SetBrush(resources, "ThemeFocusBorderBrush", VisibleRule(headerBg, inputBg).ToString());
        SetBrush(resources, "ThemeHeaderRuleBrush", VisibleRule(borderColor, headerBg).ToString());
        SetBrush(resources, "ThemeHeaderForegroundBrush", ReadableInk(Colors.White, headerBg).ToString());
        SetBrush(resources, "ThemeInputForegroundBrush", ReadableInk(ParseColor(visual.TextColor, Colors.Black), inputBg).ToString());
        SetBrush(resources, "ThemeStatusTextBrush", "#F8FAFC"); // الشريط السفلي خلفيته #064E3B في كل الثيمات.
        SetBrush(resources, "ThemeButtonTextColor", visual.ButtonTextColor);
        SetBrush(resources, "ThemeButtonTextBrush", visual.ButtonTextColor);

        // الموارد القديمة تبقى aliases حتى لا تنكسر الشاشات الموجودة في النسخة المستقرة.
        SetColor(resources, "PrimaryColor", visual.PrimaryColor);
        SetColor(resources, "PrimaryDarkColor", Darken(visual.PrimaryColor));
        SetColor(resources, "AccentColor", visual.SecondaryColor);
        SetColor(resources, "LightBgColor", visual.BackgroundColor);
        SetColor(resources, "BorderColor", visual.BorderColor);
        SetColor(resources, "DangerColor", visual.ErrorColor);
        SetColor(resources, "SuccessColor", visual.SuccessColor);
        SetColor(resources, "NavyColor", visual.TableHeaderColor);
        SetColor(resources, "ErpInfoColor", visual.InfoColor);
        SetColor(resources, "ErpSurfaceColor", visual.TableBackgroundColor);
        SetColor(resources, "ErpInkColor", visual.TextColor);
        SetColor(resources, "ErpMutedColor", visual.SecondaryTextColor);
        SetColor(resources, "ErpLineColor", visual.BorderColor);
        SetColor(resources, "ErpOkColor", visual.SuccessColor);
        SetColor(resources, "ErpWarnColor", visual.WarningColor);
        SetColor(resources, "ErpDangerColor", visual.ErrorColor);

        // Brushes المستخدمة مباشرة في بعض القوالب القديمة تُحدّث in-place حتى تتغير فوراً.
        SetBrush(resources, "PrimaryBrush", visual.PrimaryColor);
        SetBrush(resources, "PrimaryDarkBrush", Darken(visual.PrimaryColor));
        SetBrush(resources, "AccentBrush", visual.SecondaryColor);
        SetBrush(resources, "LightBgBrush", visual.BackgroundColor);
        SetBrush(resources, "BorderBrushStd", visual.BorderColor);
        SetBrush(resources, "DangerBrush", visual.ErrorColor);
        SetBrush(resources, "SuccessBrush", visual.SuccessColor);
        SetBrush(resources, "NavyBrush", visual.TableHeaderColor);
        SetBrush(resources, "ErpSurfaceBrush", visual.TableBackgroundColor);
        SetBrush(resources, "ErpInkBrush", visual.TextColor);
        SetBrush(resources, "ErpMutedBrush", visual.SecondaryTextColor);
        SetBrush(resources, "ErpLineBrush", visual.BorderColor);
        SetBrush(resources, "ErpOkBrush", visual.SuccessColor);
        SetBrush(resources, "ErpWarnBrush", visual.WarningColor);
        SetBrush(resources, "ErpDangerBrush", visual.ErrorColor);
        SetBrush(resources, "ErpInfoBrush", visual.InfoColor);

        var bodyFont = new FontFamily(string.IsNullOrWhiteSpace(visual.BodyFontFamily) ? "Segoe UI" : visual.BodyFontFamily);
        var headingFont = new FontFamily(string.IsNullOrWhiteSpace(visual.HeadingFontFamily) ? "Segoe UI" : visual.HeadingFontFamily);
        resources["ThemeBodyFontFamily"] = bodyFont;
        resources["ThemeHeadingFontFamily"] = headingFont;
        resources["BodyFont"] = bodyFont;
        resources["HeadFont"] = headingFont;
        resources["ThemeBaseFontSize"] = visual.BaseFontSize;
        resources["ThemeTableFontSize"] = visual.TableFontSize;
        resources["ThemeButtonFontSize"] = visual.ButtonFontSize;
        resources["ThemePageTitleFontSize"] = visual.PageTitleFontSize;
        resources["ThemeSectionTitleFontSize"] = visual.SectionTitleFontSize;
        resources["ThemeHeadingWeight"] = visual.HeadingBold ? FontWeights.Bold : FontWeights.Normal;
        resources["ThemeBodyWeight"] = visual.BodyBold ? FontWeights.Bold : FontWeights.Normal;
        resources["UiFontSize"] = visual.BaseFontSize;

        var controlBorder = visual.ShowTextBoxBorders
            ? (visual.InputStyle == "Flat" ? new Thickness(0) : new Thickness(Math.Max(0, visual.BorderThickness)))
            : (visual.ShowFieldUnderlines ? new Thickness(0, 0, 0, Math.Max(0, visual.BorderThickness)) : new Thickness(0));
        var buttonBorder = visual.ShowButtonBorders && visual.ButtonStyle != "Flat"
            ? new Thickness(Math.Max(0, visual.BorderThickness)) : new Thickness(0);
        resources["ThemeInputBorderThickness"] = controlBorder;
        resources["ThemeButtonBorderThickness"] = buttonBorder;
        resources["ThemeTableBorderThickness"] = visual.ShowTableBorders ? new Thickness(Math.Max(0, visual.BorderThickness)) : new Thickness(0);
        resources["ThemeColumnDividerThickness"] = visual.ShowColumnDividers ? new Thickness(0, 0, Math.Max(0, visual.BorderThickness), Math.Max(0, visual.BorderThickness)) : new Thickness(0);
        resources["ThemeCornerRadius"] = new CornerRadius(Math.Max(0, visual.CornerRadius));
        resources["ThemeButtonCornerRadius"] = new CornerRadius(visual.ButtonStyle is "Square" or "Flat" ? 0 : Math.Max(0, visual.CornerRadius));
        var inputPaddingHorizontal = Math.Max(0, visual.Padding);
        var inputPaddingVertical = Math.Max(0, visual.Padding / 1.5);
        resources["ThemeInputPadding"] = new Thickness(inputPaddingHorizontal, inputPaddingVertical,
            inputPaddingHorizontal, inputPaddingVertical);
        var buttonPaddingHorizontal = Math.Max(4, visual.Padding * 2);
        var buttonPaddingVertical = Math.Max(2, visual.Padding);
        resources["ThemeButtonPadding"] = new Thickness(buttonPaddingHorizontal, buttonPaddingVertical,
            buttonPaddingHorizontal, buttonPaddingVertical);
        resources["ThemeCardPadding"] = new Thickness(Math.Max(0, visual.Padding * 2));
        resources["ThemeRowHeight"] = Math.Max(18, visual.RowHeight);
        resources["ThemeTitleBarHeight"] = Math.Max(24, visual.TitleBarHeight);
        resources["ThemeTableHeaderHeight"] = Math.Max(24, visual.RowHeight);
        resources["ThemeButtonHeight"] = Math.Max(22, visual.ButtonHeight);
        resources["ThemeButtonWidth"] = visual.ButtonWidth > 0 ? visual.ButtonWidth : double.NaN;
        resources["ThemeButtonTextColor"] = ToBrush(visual.ButtonTextColor, Colors.White);
        resources["ThemeCardEffect"] = new System.Windows.Media.Effects.DropShadowEffect
        {
            ShadowDepth = Math.Max(0, visual.ShadowDepth), BlurRadius = 8,
            Opacity = visual.CardShadowEnabled ? Math.Clamp(visual.ShadowOpacity, 0, 1) : 0,
            Color = ParseColor(visual.BorderColor, Colors.Black)
        };
        resources["ThemeIconSize"] = Math.Max(8, visual.IconSize);
        resources["ThemeSidebarWidth"] = Math.Max(0, visual.SidebarWidth);
        resources["ThemeShowSidebar"] = visual.ShowSidebar;
        resources["ThemeTextAlignment"] = ParseAlignment(visual.TableTextAlignment);

        foreach (Window window in System.Windows.Application.Current.Windows)
            if (window is Views.MainWindow main) main.ApplyThemeLayout(visual);
        if (notify) Changed?.Invoke(null, EventArgs.Empty);
    }

    public static ThemeProfileDto Snapshot() => Current.Clone();

    private static ThemeProfileDto VisualProfile(ThemeProfileDto profile)
    {
        if (!string.Equals(profile.Mode, "Auto", StringComparison.OrdinalIgnoreCase) || !IsSystemDark()) return profile;
        var dark = ThemeProfileDefaults.Dark();
        dark.Name = profile.Name;
        dark.Mode = profile.Mode;
        return dark;
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch { return false; }
    }

    private static string GetColorText(ThemeProfileDto profile, string property)
        => typeof(ThemeProfileDto).GetProperty(property)?.GetValue(profile)?.ToString() ?? "#000000";

    private static void SetColor(ResourceDictionary resources, string key, string text)
        => resources[key] = ParseColor(text, Colors.Transparent);

    private static void SetBrush(ResourceDictionary resources, string key, string text)
    {
        var color = ParseColor(text, Colors.Transparent);
        if (resources[key] is SolidColorBrush existing && !existing.IsFrozen)
            existing.Color = color;
        else resources[key] = new SolidColorBrush(color);
    }

    private static SolidColorBrush ToBrush(string text, Color fallback) => new(ParseColor(text, fallback));

    public static Color ParseColor(string text, Color fallback)
    {
        try
        {
            var value = (Color)ColorConverter.ConvertFromString(text ?? "");
            return value;
        }
        catch { return fallback; }
    }

    private static string Darken(string text)
    {
        var c = ParseColor(text, Colors.Black);
        int r = (int)(c.R * .72), g = (int)(c.G * .72), b = (int)(c.B * .72);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static Color VisibleRule(Color requested, Color background)
    {
        if (ContrastRatio(requested, background) >= 3) return requested;
        var dark = Color.FromRgb(100, 116, 139);  // #64748B — أوضح من لون الفاصل الافتراضي على الأبيض.
        var light = Color.FromRgb(148, 163, 184); // #94A3B8 — مقروء على السطح الداكن.
        if (ContrastRatio(dark, background) >= 3) return dark;
        if (ContrastRatio(light, background) >= 3) return light;
        return ContrastRatio(Colors.Black, background) >= ContrastRatio(Colors.White, background) ? Colors.Black : Colors.White;
    }

    private static Color ReadableInk(Color requested, Color background)
    {
        if (ContrastRatio(requested, background) >= 4.5) return requested;
        return ContrastRatio(Colors.Black, background) >= ContrastRatio(Colors.White, background) ? Colors.Black : Colors.White;
    }

    private static double ContrastRatio(Color first, Color second)
    {
        static double Linear(byte channel)
        {
            double v = channel / 255.0;
            return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4);
        }
        static double Lum(Color c) => .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        var a = Lum(first); var b = Lum(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static HorizontalAlignment ParseAlignment(string value)
        => Enum.TryParse<HorizontalAlignment>(value, true, out var alignment) ? alignment : HorizontalAlignment.Right;
}
