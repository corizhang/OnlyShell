using System.Windows;
using OnlyShell.Core.Hosts;
using OnlyShell.Core.Security;

namespace OnlyShell.App;

public sealed class WpfHostKeyConfirmationService : IHostKeyConfirmationService
{
    public bool Confirm(HostProfile host, string fingerprint, string? previouslyTrustedFingerprint)
    {
        var dispatcher = Application.Current.Dispatcher;
        return dispatcher.Invoke(() =>
        {
            var dialog = new HostKeyPromptWindow(host, fingerprint, previouslyTrustedFingerprint)
            {
                Owner = Application.Current.MainWindow
            };
            return dialog.ShowDialog() == true;
        });
    }
}
