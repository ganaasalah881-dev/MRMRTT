using System.Windows;
using System.Windows.Controls;
using DatesErp.Application.Services;
using DatesErp.Core.Domain.Entities;
using DatesErp.Desktop.Services;
using DatesErp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DatesErp.Desktop.Views;

/// <summary>§تفويض زمني: مدير يفوّض صلاحياته مؤقتاً (إجازة/تناوب) — كلياً أو لوحدة محددة.</summary>
public class DelegationWindow : Window
{
    public DelegationWindow()
    {
        Title = "التفويض الزمني للصلاحيات";
        Width = 780; Height = 560; MinWidth = 640; MinHeight = 480;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; }
        };

        var fromBox = new ComboBox { Width = 200, DisplayMemberPath = "FullName", SelectedValuePath = "Id" };
        var toBox = new ComboBox { Width = 200, DisplayMemberPath = "FullName", SelectedValuePath = "Id" };
        var startBox = new DatePicker { Width = 130, SelectedDate = DateTime.Now.Date };
        var endBox = new DatePicker { Width = 130, SelectedDate = DateTime.Now.Date.AddDays(7) };
        // §B84/V5: النطاق كان نصاً حراً يقبل أكواداً وهمية — الآن قائمة من كتالوج الوحدات الحقيقي.
        var scopeBox = new ComboBox { Width = 170, DisplayMemberPath = "Name", SelectedValuePath = "Code" };
        scopeBox.Items.Add(new { Code = "", Name = "كل الوحدات" });
        foreach (var r in PermissionService.ResourceCatalog)
            scopeBox.Items.Add(new { Code = r.Code, Name = $"{r.NameAr} ({r.Code})" });
        scopeBox.SelectedIndex = 0;
        var grid = new DataGrid { Height = 220, IsReadOnly = true, RowHeight = 28, AutoGenerateColumns = false };
        grid.Columns.Add(new DataGridTextColumn { Header = "من", Binding = new System.Windows.Data.Binding("From"), Width = 140 });
        grid.Columns.Add(new DataGridTextColumn { Header = "إلى", Binding = new System.Windows.Data.Binding("To"), Width = 140 });
        grid.Columns.Add(new DataGridTextColumn { Header = "من تاريخ", Binding = new System.Windows.Data.Binding("Start"), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "إلى تاريخ", Binding = new System.Windows.Data.Binding("End"), Width = 90 });
        grid.Columns.Add(new DataGridTextColumn { Header = "النطاق", Binding = new System.Windows.Data.Binding("Scope"), Width = 100 });
        grid.Columns.Add(new DataGridTextColumn { Header = "الحالة", Binding = new System.Windows.Data.Binding("Status"), Width = 80 });

        void Refresh()
        {
            using var scope = AppContainer.NewScope();
            var db = scope.ServiceProvider.GetRequiredService<DatesErpDbContext>();
            var users = db.Users.AsNoTracking().Where(u => u.IsActive).ToList();
            fromBox.ItemsSource = users; toBox.ItemsSource = users;
            grid.ItemsSource = db.Delegations.AsNoTracking().OrderByDescending(d => d.Id).Take(100).Select(d => new
            {
                From = db.Users.Where(u => u.Id == d.FromUserId).Select(u => u.FullName).FirstOrDefault(),
                To = db.Users.Where(u => u.Id == d.ToUserId).Select(u => u.FullName).FirstOrDefault(),
                Start = d.StartDate.ToString("dd/MM/yyyy"),
                End = d.EndDate.ToString("dd/MM/yyyy"),
                Scope = d.ScopeModule ?? "كل الوحدات",
                Status = d.IsActive && d.StartDate <= DateTime.Now.Date && d.EndDate >= DateTime.Now.Date ? "سارٍ 🟢" : d.IsActive ? "مؤجل ⏳" : "منتهٍ/موقوف ⚪"
            }).ToList();
        }

        var saveBtn = new Button { Content = "💾 حفظ التفويض", Style = (Style)System.Windows.Application.Current.FindResource("ErpPrimaryButton") };
        saveBtn.Click += (_, _) =>
        {
            if (fromBox.SelectedValue is not int fid || toBox.SelectedValue is not int tid)
            { AppContainer.Get<DialogService>().Error("اختر المفوِّض والمفوَّض إليه."); return; }
            using var scope = AppContainer.NewScope();
            var svc = scope.ServiceProvider.GetRequiredService<MasterDataService>();
            var r = svc.SaveDelegation(null, fid, tid, startBox.SelectedDate ?? DateTime.Now.Date,
                endBox.SelectedDate ?? DateTime.Now.Date, (scopeBox.SelectedValue as string) ?? "");
            if (r.Ok) { AppContainer.Get<DialogService>().Info(r.Message); Refresh(); }
            else AppContainer.Get<DialogService>().Error(r.Message);
        };

        var p = new StackPanel { Margin = new Thickness(14) };
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        void Field(string label, UIElement el)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            sp.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.Bold, FontSize = 11.5 });
            sp.Children.Add(el);
            row.Children.Add(sp);
        }
        Field("المفوِّض (المدير):", fromBox);
        Field("المفوَّض إليه:", toBox);
        Field("من:", startBox);
        Field("إلى:", endBox);
        Field("نطاق محدد (اختياري):", scopeBox);
        Field("", saveBtn);
        p.Children.Add(new TextBlock { Text = "يسري التفويض عند تسجيل دخول المفوَّض إليه ضمن الفترة، ويُسجل في التدقيق.", Foreground = System.Windows.Media.Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 0, 0, 6) });
        p.Children.Add(row);
        p.Children.Add(grid);
        Content = p;
        Loaded += (_, _) => Refresh();
    }
}
