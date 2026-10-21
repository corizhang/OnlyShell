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

    public Task<bool> ConfirmCloseTerminalAsync(string terminalName)
    {
        return ConfirmAsync(
            "TERMINAL / CONFIRM_CLOSE",
            "关闭终端？",
            $"确定关闭终端“{terminalName}”吗？未保存的终端输出将无法恢复。",
            "关闭终端",
            destructive: true);
    }

    public Task<bool> ConfirmForgetCredentialAsync(HostProfile profile) => ConfirmAsync(
        "HOST / FORGET_CREDENTIAL",
        "忘记已保存的密码？",
        $"将从此 Windows 用户的凭据管理器中删除主机“{profile.Name}”的登录密码。操作后，下次连接时需要重新输入。",
        "忘记密码",
        destructive: true);

    private static Task<bool> ConfirmAsync(string eyebrow, string heading, string message, string confirmLabel, bool destructive)
    {
        var dialog = new ConfirmationDialogWindow(eyebrow, heading, message, confirmLabel, destructive)
        {
            Owner = Application.Current.MainWindow
        };
        return Task.FromResult(dialog.ShowDialog() == true);
    }

    public Task ShowErrorAsync(string message)
    {
        MessageBox.Show(Application.Current.MainWindow, message, "OnlyShell", MessageBoxButton.OK, MessageBoxImage.Error);
        return Task.CompletedTask;
    }
}
