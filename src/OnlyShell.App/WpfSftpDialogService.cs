using System.Windows;
using OnlyShell.Core.Files;
using OnlyShell.Presentation.Services;

namespace OnlyShell.App;

public sealed class WpfSftpDialogService : ISftpDialogService
{
    public Task<string?> PromptForNameAsync(string title, string prompt, string defaultValue = "")
    {
        var dialog = new SftpInputWindow(title, prompt, defaultValue) { Owner = Application.Current.MainWindow };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.Value : null);
    }

    public Task<bool> ConfirmDeleteAsync(RemoteFileEntry entry) => Task.FromResult(MessageBox.Show(
        Application.Current.MainWindow,
        $"确定删除远程{(entry.IsDirectory ? "目录" : "文件")}“{entry.Name}”吗？",
        "SFTP / DELETE", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);

    public Task<bool> ConfirmOverwriteAsync(string remotePath) => Task.FromResult(MessageBox.Show(
        Application.Current.MainWindow,
        $"远程路径已存在：\n{remotePath}\n\n是否覆盖？",
        "SFTP / OVERWRITE", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);

    public Task<bool> ConfirmLocalOverwriteAsync(string localPath) => Task.FromResult(MessageBox.Show(
        Application.Current.MainWindow,
        $"本地文件已存在：\n{localPath}\n\n是否覆盖？",
        "SFTP / OVERWRITE", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);

    public Task ShowErrorAsync(string message)
    {
        MessageBox.Show(Application.Current.MainWindow, message, "SFTP", MessageBoxButton.OK, MessageBoxImage.Error);
        return Task.CompletedTask;
    }
}
