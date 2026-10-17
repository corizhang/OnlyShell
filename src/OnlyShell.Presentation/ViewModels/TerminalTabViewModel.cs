using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OnlyShell.Core.Hosts;
using VirtualTerminal;
using VirtualTerminal.Interfaces;

namespace OnlyShell.Presentation.ViewModels;

public enum TerminalSessionKind
{
    Ssh,
    Local
}

public sealed partial class TerminalTabViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DispatcherTimer _connectionMonitor;
    private ITerminalSession? _observedSession;

    public HostProfile? Host { get; }
    public TerminalSessionKind Kind { get; }
    public string? LocalShellId { get; }
    public string? LocalWorkingDirectory { get; }
    public string DisplayName => Kind == TerminalSessionKind.Local ? LocalShellLabel : Host?.Name ?? "SSH";
    public string EndpointLabel => Kind == TerminalSessionKind.Local
        ? $"LOCAL // {Environment.UserName}"
        : Host is null ? "SSH // RESTORED SESSION" : $"{Host.UserName}@{Host.HostName}:{Host.Port}";
    public string ProtocolLabel => Kind == TerminalSessionKind.Local ? "LOCAL / CONPTY / UTF-8" : "SSH / UTF-8 / XTERM";
    public string LocalShellLabel => LocalShellId switch
    {
        "pwsh" => "PowerShell 7",
        "cmd" => "Command Prompt",
        _ => "PowerShell"
    };
    public bool CanReconnect => Status != "CONNECTED" && (Kind == TerminalSessionKind.Local || Host is not null);
    public bool IsSessionMissing => Session is null;
    public bool IsConnected => Kind == TerminalSessionKind.Local
        ? Status == "CONNECTED"
        : Session is SecureShellSession ssh && ssh.IsConnected;

    [ObservableProperty]
    private string status = "DISCONNECTED";

    [ObservableProperty]
    private ITerminalSession? session;

    public TerminalTabViewModel(HostProfile host, SecureShellSession session)
        : this(host, session, TerminalSessionKind.Ssh)
    {
    }

    public TerminalTabViewModel(HostProfile? host, ITerminalSession? session, TerminalSessionKind kind, string? localShellId = null, string? localWorkingDirectory = null)
    {
        Host = host;
        Kind = kind;
        LocalShellId = localShellId;
        LocalWorkingDirectory = localWorkingDirectory;
        Session = session;
        Status = session is null ? "RESTORED / DISCONNECTED" : "CONNECTED";
        _connectionMonitor = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _connectionMonitor.Tick += (_, _) =>
        {
            var currentStatus = Session switch
            {
                SecureShellSession ssh => ssh.IsConnected ? "CONNECTED" : "CONNECTION LOST",
                _ => Status
            };
            SetStatus(currentStatus);
        };
        _connectionMonitor.Start();
        Subscribe(session);
    }

    partial void OnSessionChanged(ITerminalSession? value)
    {
        if (_observedSession is not null)
            _observedSession.Disconnected -= Session_Disconnected;
        Subscribe(value);
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(CanReconnect));
        OnPropertyChanged(nameof(IsSessionMissing));
    }

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(CanReconnect));
    }

    private void Subscribe(ITerminalSession? value)
    {
        _observedSession = value;
        if (_observedSession is not null)
            _observedSession.Disconnected += Session_Disconnected;
    }

    private void Session_Disconnected(object? sender, EventArgs e) =>
        _ = System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => SetStatus("DISCONNECTED"));

    private void SetStatus(string value)
    {
        if (string.Equals(Status, value, StringComparison.Ordinal))
            return;
        Status = value;
    }

    public async Task ReplaceSessionAsync(ITerminalSession session)
    {
        var previous = Session;
        Session = session;
        Status = "CONNECTED";
        if (previous is null)
            return;
        if (previous is SecureShellSession ssh)
        {
            try { await ssh.DisconnectAsync(); }
            catch { /* A lost transport may already be disposed. */ }
        }
        previous.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Status = "DISCONNECTING";
        _connectionMonitor.Stop();
        var current = Session;
        Session = null;
        if (current is SecureShellSession ssh)
        {
            try { await ssh.DisconnectAsync(); }
            catch { /* The remote transport may already have closed. */ }
        }
        current?.Dispose();
        Status = "DISCONNECTED";
    }
}
