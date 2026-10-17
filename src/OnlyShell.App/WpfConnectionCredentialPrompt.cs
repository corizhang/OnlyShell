using System.Windows;
using OnlyShell.Core.Hosts;
using OnlyShell.Presentation.Services;

namespace OnlyShell.App;

public sealed class WpfConnectionCredentialPrompt : IConnectionCredentialPrompt
{
    public Task<ConnectionCredential?> PromptForSecretAsync(HostProfile host, string? savedSecret)
    {
        var dialog = new CredentialPromptWindow(host, savedSecret) { Owner = Application.Current.MainWindow };
        return Task.FromResult(dialog.ShowDialog() == true && dialog.Password is { } password
            ? new ConnectionCredential(password, dialog.RememberOnThisDevice)
            : null);
    }
}
