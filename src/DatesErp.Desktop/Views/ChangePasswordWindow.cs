using System.Windows;
using System.Windows.Controls;
using DatesErp.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DatesErp.Desktop.Views;

/// <summary>
/// §إصلاح حرج — نافذة تغيير كلمة المرور.
///
/// قبل الإصلاح لم تكن توجد أي آلية لتغيير كلمة المرور في النظام كله:
/// MustChangePassword يُرفع عند البذر وعند التصفير ويُعاد في نتيجة الدخول،
/// لكن لا نافذة ولا خدمة تتيح تغييرها — فالمستخدم يبقى موسوماً
/// «يجب تغيير كلمة المرور» إلى الأبد ولا سبيل لإرضاء الشرط.
/// </summary>
public class ChangePasswordWindow : Window
{
    private readonly PasswordBox _old = new() { Width = 260, Margin = new Thickness(0, 2, 0, 8) };
    private readonly PasswordBox _new = new() { Width = 260, Margin = new Thickness(0, 2, 0, 8) };
    private readonly PasswordBox _confirm = new() { Width = 260, Margin = new Thickness(0, 2, 0, 8) };
    private readonly TextBlock _msg = new()
    {
        Foreground = System.Windows.Media.Brushes.Firebrick,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 0)
    };

    private readonly int _userId;
    private readonly bool _forced;

    /// <summary>
    /// </summary>
    /// <param name="userId">المستخدم صاحب الجلسة.</param>
    /// <param name="forced">true عند أول دخول بعد MustChangePassword — لا يمكن الإلغاء.</param>
    /// <param name="hideOld">true عند التغيير الإجباري الأول (كلمة المرور الحالية هي الافتراضية المعروفة).</param>
    public ChangePasswordWindow(int userId, bool forced = false, bool hideOld = false)
    {
        _userId = userId;
        _forced = forced;

        Title = forced ? "يجب تغيير كلمة المرور قبل المتابعة" : "تغيير كلمة المرور";
        Width = 430;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        FlowDirection = FlowDirection.RightToLeft;

        var panel = new StackPanel { Margin = new Thickness(18) };

        if (forced)
            panel.Children.Add(new TextBlock
            {
                Text = "حسابك موسوم بـ«يجب تغيير كلمة المرور». لا يمكن استخدام النظام قبل تغييرها.",
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });

        var oldRow = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        oldRow.Children.Add(new TextBlock { Text = "كلمة المرور الحالية:", FontWeight = FontWeights.Bold });
        oldRow.Children.Add(_old);
        if (!hideOld) panel.Children.Add(oldRow);

        var newRow = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        newRow.Children.Add(new TextBlock { Text = "كلمة المرور الجديدة:", FontWeight = FontWeights.Bold });
        newRow.Children.Add(_new);
        panel.Children.Add(newRow);

        var confRow = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        confRow.Children.Add(new TextBlock { Text = "تأكيد كلمة المرور الجديدة:", FontWeight = FontWeights.Bold });
        confRow.Children.Add(_confirm);
        panel.Children.Add(confRow);

        panel.Children.Add(new TextBlock
        {
            Text = "الحد الأدنى 8 رموز، حروف وأرقام معاً، ولا يجوز استخدام كلمة المرور الافتراضية.",
            FontSize = 11,
            Foreground = System.Windows.Media.Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });
        panel.Children.Add(_msg);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        // §B84/K1: Enter من أي حقل + Escape للإلغاء (بدل Enter في حقل التأكيد فقط).
        // §B110: كانا بلا نمط (رمادي افتراضي) — رئيسي كحلي + إلغاء محايد بالثيم الموحد
        var ok = new Button { Content = "💾 تغيير كلمة المرور", Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton"), IsDefault = true };
        ok.Click += (_, _) => Submit();
        buttons.Children.Add(ok);
        if (!forced)
        {
            var cancel = new Button { Content = "إلغاء", Style = (Style)System.Windows.Application.Current.FindResource("ErpButton"), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            cancel.Click += (_, _) => { DialogResult = false; Close(); };
            buttons.Children.Add(cancel);
        }
        panel.Children.Add(buttons);

        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) => (hideOld ? _new : _old).Focus();
        // §B84/K1: حُذف معالج Enter اليدوي (IsDefault يغني عنه، وبقاؤه كان سيُرسل مرتين).
    }

    private void Submit()
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<DatesErp.Application.Services.MasterDataService>();
            var r = svc.ChangePassword(_userId, _old.Password, _new.Password, _confirm.Password);
            if (!r.Ok) { _msg.Text = r.Message; return; }

            _msg.Foreground = System.Windows.Media.Brushes.DarkGreen;
            _msg.Text = r.Message;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex, "ChangePassword");
            _msg.Text = "تعذر تغيير كلمة المرور. راجع سجل الأخطاء.";
        }
    }
}
