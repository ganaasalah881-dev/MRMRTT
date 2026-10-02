using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;
using DatesErp.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views.Screens;

public sealed class ThemePreviewRow
{
    public string Item { get; set; }
    public string Status { get; set; }
    public int Qty { get; set; }
}

/// <summary>إدارة المظهر المركزي مع معاينة حية وتراجع آمن قبل الحفظ.</summary>
public partial class ThemeSettingsView : UserControl
{
    private readonly List<ThemeColorField> _colorFields;
    private ThemeCatalogDto _catalog;
    private ThemeProfileDto _working;
    private ThemeProfileDto _original;
    private bool _loading;

    public ThemeSettingsView()
    {
        InitializeComponent();
        _colorFields = new List<ThemeColorField>
        {
            PrimaryField, SecondaryField, BackgroundField, WindowBackgroundField, TableBackgroundField, TableHeaderField,
            TextField, SecondaryTextField, ButtonField, ButtonHoverField, ButtonPressedField, ButtonTextField, BorderField,
            SuccessField, WarningField, ErrorField, InfoField, SelectedRowField, HoverRowField, AlternateRowField,
            CardField, InputField, TitleBarField
        };
        foreach (var field in _colorFields) field.ValueChanged += Field_Changed;
        Loaded += (_, _) => LoadThemeCatalog();
    }

    public void AttachChrome(ErpChrome chrome)
    {
        chrome.SetModule("settings");
        chrome.SetScreenCode("MRPSYS1004");
        chrome.SetToolbar(new ErpToolbar()
            .WithSave((_, _) => Save_Click(null, null), "💾 حفظ الثيم")
            .WithUndo((_, _) => Cancel_Click(null, null), "↩ إلغاء")
            .WithExit((_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard")));
        chrome.SetBody(this);
        chrome.CloseRequested += (_, _) => (Window.GetWindow(this) as MainWindow)?.OpenScreen("dashboard");
    }

    private void LoadThemeCatalog()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            _catalog = scope.ServiceProvider.GetRequiredService<IThemeSettingsService>().Load();
            _original = ThemeManager.Snapshot();
            _working = _original.Clone();
            ProfilesBox.ItemsSource = _catalog.Profiles;
            ProfilesBox.SelectedItem = _catalog.Profiles.FirstOrDefault(x => string.Equals(x.Name, _catalog.ActiveName, StringComparison.OrdinalIgnoreCase))
                                      ?? _catalog.Profiles.FirstOrDefault();
            LoadControls(_working);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Theme.Load"); }
    }

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProfilesBox.SelectedItem is not ThemeProfileDto profile) return;
        _working = profile.Clone();
        LoadControls(_working);
        ThemeManager.Apply(_working);
    }

    private void LoadControls(ThemeProfileDto profile)
    {
        _loading = true;
        try
        {
            foreach (var field in _colorFields)
                field.Value = typeof(ThemeProfileDto).GetProperty(field.KeyName)?.GetValue(profile)?.ToString() ?? "#000000";
            SelectTag(ModeBox, profile.Mode);
            SelectText(BodyFontBox, profile.BodyFontFamily);
            SelectText(HeadingFontBox, profile.HeadingFontFamily);
            Set(BaseFontBox, profile.BaseFontSize); Set(PageTitleBox, profile.PageTitleFontSize); Set(SectionTitleBox, profile.SectionTitleFontSize);
            Set(TableFontBox, profile.TableFontSize); Set(ButtonFontBox, profile.ButtonFontSize);
            HeadingBoldCheck.IsChecked = profile.HeadingBold; BodyBoldCheck.IsChecked = profile.BodyBold;
            Set(BorderThicknessBox, profile.BorderThickness); Set(CornerRadiusBox, profile.CornerRadius); Set(PaddingBox, profile.Padding);
            SelectTag(InputStyleBox, profile.InputStyle); UnderlineCheck.IsChecked = profile.ShowFieldUnderlines;
            TextBoxBorderCheck.IsChecked = profile.ShowTextBoxBorders; ButtonBorderCheck.IsChecked = profile.ShowButtonBorders;
            SelectTag(ButtonStyleBox, profile.ButtonStyle); Set(ButtonWidthBox, profile.ButtonWidth); Set(ButtonHeightBox, profile.ButtonHeight); Set(IconSizeBox, profile.IconSize);
            SelectTag(IconPositionBox, profile.IconPosition); Set(RowHeightBox, profile.RowHeight); SelectTag(AlignmentBox, profile.TableTextAlignment);
            TableBorderCheck.IsChecked = profile.ShowTableBorders; ColumnDividerCheck.IsChecked = profile.ShowColumnDividers; PaginationCheck.IsChecked = profile.ShowPagination;
            Set(TitleBarHeightBox, profile.TitleBarHeight); Set(SidebarWidthBox, profile.SidebarWidth); Set(ContentWidthBox, profile.ContentWidth); SelectTag(SidebarStyleBox, profile.SidebarStyle); SidebarCheck.IsChecked = profile.ShowSidebar;
            ShadowCheck.IsChecked = profile.CardShadowEnabled; Set(ShadowDepthBox, profile.ShadowDepth); Set(ShadowOpacityBox, profile.ShadowOpacity);
        }
        finally { _loading = false; }
    }

    private void Field_Changed(object sender, EventArgs e) => PreviewCore(sender);

    private void Preview_Changed(object sender, RoutedEventArgs e) => PreviewCore(sender);

    private void PreviewCore(object sender)
    {
        if (_loading || _working == null) return;
        if (ReferenceEquals(sender, ModeBox)) { ApplyModePreset(); return; }
        ReadControls(_working);
        ThemeManager.Apply(_working);
    }

    private void ReadControls(ThemeProfileDto profile)
    {
        foreach (var field in _colorFields)
        {
            var property = typeof(ThemeProfileDto).GetProperty(field.KeyName);
            property?.SetValue(profile, field.Value);
        }
        profile.Mode = Tag(ModeBox, profile.Mode);
        profile.BodyFontFamily = Text(BodyFontBox, profile.BodyFontFamily);
        profile.HeadingFontFamily = Text(HeadingFontBox, profile.HeadingFontFamily);
        profile.BaseFontSize = Number(BaseFontBox, profile.BaseFontSize); profile.PageTitleFontSize = Number(PageTitleBox, profile.PageTitleFontSize);
        profile.SectionTitleFontSize = Number(SectionTitleBox, profile.SectionTitleFontSize); profile.TableFontSize = Number(TableFontBox, profile.TableFontSize);
        profile.ButtonFontSize = Number(ButtonFontBox, profile.ButtonFontSize); profile.HeadingBold = HeadingBoldCheck.IsChecked == true; profile.BodyBold = BodyBoldCheck.IsChecked == true;
        profile.BorderThickness = Number(BorderThicknessBox, profile.BorderThickness); profile.CornerRadius = Number(CornerRadiusBox, profile.CornerRadius); profile.Padding = Number(PaddingBox, profile.Padding);
        profile.InputStyle = Tag(InputStyleBox, profile.InputStyle); profile.ShowFieldUnderlines = UnderlineCheck.IsChecked == true; profile.ShowTextBoxBorders = TextBoxBorderCheck.IsChecked == true; profile.ShowButtonBorders = ButtonBorderCheck.IsChecked == true;
        profile.ButtonStyle = Tag(ButtonStyleBox, profile.ButtonStyle); profile.ButtonWidth = Number(ButtonWidthBox, profile.ButtonWidth); profile.ButtonHeight = Number(ButtonHeightBox, profile.ButtonHeight); profile.IconSize = Number(IconSizeBox, profile.IconSize);
        profile.IconPosition = Tag(IconPositionBox, profile.IconPosition); profile.RowHeight = Number(RowHeightBox, profile.RowHeight); profile.TableTextAlignment = Tag(AlignmentBox, profile.TableTextAlignment);
        profile.ShowTableBorders = TableBorderCheck.IsChecked == true; profile.ShowColumnDividers = ColumnDividerCheck.IsChecked == true; profile.ShowPagination = PaginationCheck.IsChecked == true;
        profile.TitleBarHeight = Number(TitleBarHeightBox, profile.TitleBarHeight); profile.SidebarWidth = Number(SidebarWidthBox, profile.SidebarWidth); profile.ContentWidth = Number(ContentWidthBox, profile.ContentWidth); profile.SidebarStyle = Tag(SidebarStyleBox, profile.SidebarStyle); profile.ShowSidebar = SidebarCheck.IsChecked == true;
        profile.CardShadowEnabled = ShadowCheck.IsChecked == true; profile.ShadowDepth = Number(ShadowDepthBox, profile.ShadowDepth); profile.ShadowOpacity = Number(ShadowOpacityBox, profile.ShadowOpacity);
    }

    private void ApplyModePreset()
    {
        if (_loading || ModeBox.SelectedItem is not ComboBoxItem item) return;
        var mode = item.Tag?.ToString();
        if (mode == "Dark")
        {
            var dark = ThemeProfileDefaults.Dark(); dark.Name = _working.Name; _working = dark;
        }
        else if (mode == "Light")
        {
            var light = ThemeProfileDefaults.Light(); light.Name = _working.Name; _working = light;
        }
        LoadControls(_working);
        ThemeManager.Apply(_working);
    }

    private void NewTheme_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InputDialog("ثيم جديد", "أدخل اسم الثيم الجديد:", "مخصص");
        dialog.Owner = Window.GetWindow(this);
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value)) return;
        var name = dialog.Value.Trim();
        if (_catalog.Profiles.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
        { AppContainer.Get<DialogService>().Error("يوجد ثيم بهذا الاسم."); return; }
        var clone = _working.Clone(); clone.Name = name;
        _catalog.Profiles.Add(clone);
        ProfilesBox.ItemsSource = null; ProfilesBox.ItemsSource = _catalog.Profiles; ProfilesBox.SelectedItem = clone;
        _working = clone; LoadControls(_working); ThemeManager.Apply(_working);
    }

    private void DeleteTheme_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesBox.SelectedItem is not ThemeProfileDto profile || profile.Name == "الافتراضي") return;
        if (!AppContainer.Get<DialogService>().Confirm($"حذف الثيم «{profile.Name}»؟")) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var result = scope.ServiceProvider.GetRequiredService<IThemeSettingsService>().Delete(profile.Name);
            if (!result.Ok) { AppContainer.Get<DialogService>().Error(result.Message); return; }
            LoadThemeCatalog();
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Theme.Delete"); }
    }

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        var defaults = ThemeProfileDefaults.Light(); defaults.Name = _working?.Name ?? "مخصص"; _working = defaults;
        LoadControls(_working); ThemeManager.Apply(_working);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_working == null) return;
        ReadControls(_working); ThemeManager.Apply(_working);
        AppContainer.Get<DialogService>().Toast("تم تطبيق المعاينة على النظام.");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_working == null) return;
        try
        {
            ReadControls(_working);
            using var scope = AppContainer.NewScope();
            var result = scope.ServiceProvider.GetRequiredService<IThemeSettingsService>().Save(_working, true);
            if (!result.Ok) { AppContainer.Get<DialogService>().Error(result.Message); return; }
            _original = _working.Clone();
            _catalog = scope.ServiceProvider.GetRequiredService<IThemeSettingsService>().Load();
            ProfilesBox.ItemsSource = _catalog.Profiles;
            ProfilesBox.SelectedItem = _catalog.Profiles.FirstOrDefault(x => string.Equals(x.Name, _working.Name, StringComparison.OrdinalIgnoreCase));
            AppContainer.Get<DialogService>().Toast("تم حفظ الثيم وتفعيله للنظام.");
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Theme.Save"); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_original == null) return;
        _working = _original.Clone(); LoadControls(_working); ThemeManager.Apply(_original);
        AppContainer.Get<DialogService>().Toast("تم إلغاء التعديلات غير المحفوظة.");
    }

    private static void Set(TextBox box, double value) => box.Text = value.ToString("0.##", CultureInfo.InvariantCulture);
    private static double Number(TextBox box, double fallback) => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    private static string Text(ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;
    private static string Tag(ComboBox box, string fallback) => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;
    private static void SelectText(ComboBox box, string value) => box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase));
    private static void SelectTag(ComboBox box, string value) => box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(x => string.Equals(x.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase));
}
