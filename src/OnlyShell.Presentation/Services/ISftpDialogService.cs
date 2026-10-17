using OnlyShell.Core.Files;

namespace OnlyShell.Presentation.Services;

public interface ISftpDialogService
{
    Task<string?> PromptForNameAsync(string title, string prompt, string defaultValue = "");
    Task<bool> ConfirmDeleteAsync(RemoteFileEntry entry);
    Task<bool> ConfirmOverwriteAsync(string remotePath);
    Task<bool> ConfirmLocalOverwriteAsync(string localPath);
    Task ShowErrorAsync(string message);
}
