using System.Windows;
using OnlyShell.Core.Hosts;
using OnlyShell.Presentation.Services;

namespace OnlyShell.App;

public sealed class WpfHostEditorDialogService : IHostEditorDialogService
{
    public Task<HostProfile?> EditAsync(HostProfile? current, IReadOnlyList<string>? existingGroups = null)
    {
        var dialog = new HostEditorWindow(current, existingGroups) { Owner = Application.Current.MainWindow };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Result : null);
    }

    public Task<bool> ConfirmDeleteAsync(HostProfile profile)
    {
        var result = MessageBox.Show(
            Application.Current.MainWindow,
            $"确定删除主机“{profile.Name}”吗？关联的本机已保存密码也会一并删除；私钥文件不会受影响。",
            "删除主机配置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    public Task ShowErrorAsync(string message)
    {
        MessageBox.Show(Application.Current.MainWindow, message, "OnlyShell", MessageBoxButton.OK, MessageBoxImage.Error);
        return Task.CompletedTask;
    }
}
