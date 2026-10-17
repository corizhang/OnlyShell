using CommunityToolkit.Mvvm.ComponentModel;
using OnlyShell.Core.Hosts;
using System.Windows.Threading;
using VirtualTerminal;

namespace OnlyShell.Presentation.ViewModels;

public sealed partial class TerminalTabViewModel : ObservableObject, IAsyncDisposable
{
    public HostProfile Host { get; }
    public string TabTitle => $"{Host.Name}  ×";

    [ObservableProperty]
    private string status = "CONNECTED";

    [ObservableProperty]
    private SecureShellSession session;

    private readonly DispatcherTimer _connectionMonitor;

    public TerminalTabViewModel(HostProfile host, SecureShellSession session)
    {
        Host = host;
        Session = session;
        _connectionMonitor = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _connectionMonitor.Tick += (_, _) =>
        {
            var currentStatus = Session.IsConnected ? "CONNECTED" : "CONNECTION LOST";
            if (!string.Equals(Status, currentStatus, StringComparison.Ordinal))
                Status = currentStatus;
        };
        _connectionMonitor.Start();
    }

    public async Task ReplaceSessionAsync(SecureShellSession session)
    {
        var previous = Session;
        Session = session;
        Status = "CONNECTED";
        try { await previous.DisconnectAsync(); }
        catch { /* A lost transport may already be disposed. */ }
        previous.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Status = "DISCONNECTING";
        _connectionMonitor.Stop();
        try { await Session.DisconnectAsync(); }
        catch { /* The remote transport may already have closed. */ }
        Session.Dispose();
        Status = "DISCONNECTED";
    }
}
