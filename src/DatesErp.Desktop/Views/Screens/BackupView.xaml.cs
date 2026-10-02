using System.Windows;
using System.Windows.Controls;
using DatesErp.Core.Interfaces.Services;
using DatesErp.Desktop.Services;

namespace DatesErp.Desktop.Views.Screens;

public partial class BackupView : UserControl
{
    public BackupView()
    {
        InitializeComponent();
    }

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = (IBackupService)scope.ServiceProvider.GetService(typeof(IBackupService));
            var r = svc.FullBackup(FolderBox.Text.Trim());
            if (!r.Ok) AppContainer.Get<DialogService>().Error(r.Message);
            else AppContainer.Get<DialogService>().Info(r.Message);
            List_Click(sender, e);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Backup.Full"); }
    }

    private void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (BackupsList.SelectedItem is not string file) { AppContainer.Get<DialogService>().Error("اختر نسخة من القائمة."); return; }
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = (IBackupService)scope.ServiceProvider.GetService(typeof(IBackupService));
            var r = svc.VerifyBackup(file);
            if (!r.Ok) AppContainer.Get<DialogService>().Error(r.Message);
            else AppContainer.Get<DialogService>().Info(r.Message);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Backup.Verify"); }
    }

    private void List_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = (IBackupService)scope.ServiceProvider.GetService(typeof(IBackupService));
            BackupsList.ItemsSource = svc.ListBackups(FolderBox.Text.Trim());
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Backup.List"); }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (BackupsList.SelectedItem is not string file) { AppContainer.Get<DialogService>().Error("اختر نسخة من القائمة."); return; }
        if (!AppContainer.Get<DialogService>().Confirm("⚠ الاستعادة ستستبدل قاعدة البيانات الحالية بالكامل.\nهل أنت متأكد؟")) return;
        try
        {
            using var scope = AppContainer.NewScope();
            var svc = (IBackupService)scope.ServiceProvider.GetService(typeof(IBackupService));
            var r = svc.Restore(file);
            if (!r.Ok) AppContainer.Get<DialogService>().Error(r.Message);
            else AppContainer.Get<DialogService>().Info(r.Message);
        }
        catch (Exception ex) { AppContainer.Get<DialogService>().HandleException(ex, "Backup.Restore"); }
    }
}
