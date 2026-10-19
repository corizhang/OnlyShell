using System.Windows.Threading;
using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using OnlyShell.Core.Hosts;
using OnlyShell.Terminal;
using VirtualTerminal;
using VirtualTerminal.Interfaces;

namespace OnlyShell.Presentation.ViewModels;

public enum TerminalSessionKind
{
    Ssh,
    Local,
    New
}

public sealed partial class TerminalTabViewModel : ObservableObject, IAsyncDisposable
{
    private const int MetricsHistoryLimit = 60;
    private readonly DispatcherTimer _connectionMonitor;
    private readonly DispatcherTimer _metricsTimer;
    private readonly LinuxServerMetricsCollector _metricsCollector = new();
    private readonly CancellationTokenSource _metricsCancellation = new();
    private ITerminalSession? _observedSession;
    private Task? _metricsRefreshTask;
    private bool _metricsRefreshRunning;
    private bool _metricsUnsupported;

    public HostProfile? Host { get; }
    public TerminalSessionKind Kind { get; }
    public string? LocalShellId { get; }
    public string? LocalWorkingDirectory { get; }
    public string DisplayName => Kind == TerminalSessionKind.New ? "新标签页" : Kind == TerminalSessionKind.Local ? LocalShellLabel : Host?.Name ?? "SSH";
    public string EndpointLabel => Kind == TerminalSessionKind.New ? "QUICK CONNECT // SELECT A HOST" : Kind == TerminalSessionKind.Local
        ? $"LOCAL // {Environment.UserName}"
        : Host is null ? "SSH // RESTORED SESSION" : $"{Host.UserName}@{Host.HostName}:{Host.Port}";
    public string ProtocolLabel => Kind == TerminalSessionKind.New ? "SSH / QUICK CONNECT" : Kind == TerminalSessionKind.Local ? "LOCAL / CONPTY / UTF-8" : "SSH / UTF-8 / XTERM";
    public string GitContextLabel => !IsGitRepository ? string.Empty : $"GIT // {GitBranch}{(GitHasChanges ? " *" : string.Empty)}";
    public bool IsNewTab => Kind == TerminalSessionKind.New;
    public string LocalShellLabel => LocalShellId switch
    {
        "pwsh" => "PowerShell 7",
        "cmd" => "Command Prompt",
        _ => "PowerShell"
    };
    public bool CanReconnect => !IsNewTab && Status != "CONNECTED" && (Kind == TerminalSessionKind.Local || Host is not null);
    public bool IsSessionMissing => Session is null;
    public bool IsRemoteMetricsVisible => Kind == TerminalSessionKind.Ssh && Host is not null;
    public string CpuMetricText => Metrics?.CpuPercent is { } cpu ? $"{cpu:F1}%" : "--";
    public string MemoryMetricText => Metrics is { } metrics ? $"{metrics.MemoryUsedMiB:F0} / {metrics.MemoryTotalMiB:F0} MiB  ·  {metrics.MemoryPercent:F1}%" : "--";
    public string DiskMetricText => Metrics is { } metrics ? $"/  {metrics.DiskUsedGiB:F1} / {metrics.DiskTotalGiB:F1} GiB  ·  {metrics.DiskPercent:F0}%" : "--";
    public string NetworkMetricText => Metrics is { } metrics
        ? $"↓ {FormatRate(metrics.NetworkReceiveBytesPerSecond)}  ↑ {FormatRate(metrics.NetworkTransmitBytesPerSecond)}"
        : "--";
    public bool IsConnected => Kind == TerminalSessionKind.Local
        ? Status == "CONNECTED"
        : Session is SecureShellSession ssh && ssh.IsConnected;
    public string MetricsHostLabel => Metrics?.HostName ?? Host?.HostName ?? "LINUX HOST";
    public string LoadMetricText => Metrics is { } metrics
        ? $"{metrics.LoadAverage1:F2}  {metrics.LoadAverage5:F2}  {metrics.LoadAverage15:F2}"
        : "--  --  --";
    public string UptimeProcessMetricText => Metrics is { } metrics
        ? $"{FormatUptime(metrics.UptimeSeconds)}  ·  {metrics.ProcessCount} PROC"
        : "--  ·  -- PROC";
    public string SwapMetricText => Metrics is { } metrics
        ? $"{metrics.SwapUsedMiB:F0} / {metrics.SwapTotalMiB:F0} MiB  ·  {metrics.SwapPercent:F0}%"
        : "--";
    public string SshRoundTripText => Metrics is { } metrics ? $"SSH RTT {metrics.SshRoundTripMilliseconds:F0} ms" : "SSH RTT --";
    public string MetricsUpdatedText => MetricsUpdatedAt?.ToLocalTime().ToString("HH:mm:ss") ?? "--:--:--";
    public double CpuProgress => Metrics?.CpuPercent ?? 0;
    public double MemoryProgress => Metrics?.MemoryPercent ?? 0;
    public double SwapProgress => Metrics?.SwapPercent ?? 0;
    public double DiskProgress => Metrics?.DiskPercent ?? 0;
    public ObservableCollection<ServerMetricsHistorySample> MetricsHistory { get; } = [];
    public double NetworkTotalBytesPerSecond => Metrics is { } metrics
        ? metrics.NetworkReceiveBytesPerSecond + metrics.NetworkTransmitBytesPerSecond
        : 0;
    public int MonitorWarningThreshold => int.TryParse(MonitorWarningThresholdText, out var value)
        ? Math.Clamp(value, 1, 100)
        : 90;
    public string MetricsAlertText
    {
        get
        {
            if (Metrics is not { } metrics)
                return "NO DATA";
            var threshold = MonitorWarningThreshold;
            var alerts = new List<string>(3);
            if (metrics.CpuPercent is { } cpu && cpu >= threshold) alerts.Add($"CPU {cpu:F0}%");
            if (metrics.MemoryPercent >= threshold) alerts.Add($"MEM {metrics.MemoryPercent:F0}%");
            if (metrics.DiskPercent >= threshold) alerts.Add($"DISK {metrics.DiskPercent:F0}%");
            return alerts.Count == 0 ? "NORMAL" : $"ALERT / {string.Join(" · ", alerts)}";
        }
    }
    public Brush MetricsAlertBrush => MetricsAlertText.StartsWith("ALERT", StringComparison.Ordinal)
        ? new SolidColorBrush(Color.FromRgb(255, 105, 97))
        : new SolidColorBrush(Color.FromRgb(91, 220, 144));

    [ObservableProperty]
    private string status = "DISCONNECTED";

    [ObservableProperty]
    private string gitWorkingDirectory = string.Empty;

    [ObservableProperty]
    private string gitBranch = string.Empty;

    [ObservableProperty]
    private bool gitHasChanges;

    public bool IsGitRepository => !string.IsNullOrWhiteSpace(GitBranch);

    partial void OnGitBranchChanged(string value)
    {
        OnPropertyChanged(nameof(IsGitRepository));
        OnPropertyChanged(nameof(GitContextLabel));
    }

    partial void OnGitHasChangesChanged(bool value) => OnPropertyChanged(nameof(GitContextLabel));

    public void UpdateGitContextFromTerminalTitle(string title)
    {
        const string prefix = "OnlyShellGit|";
        if (!title.StartsWith(prefix, StringComparison.Ordinal))
            return;

        var parts = title[prefix.Length..].Split('|');
        if (parts.Length != 3)
            return;

        try
        {
            GitWorkingDirectory = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
            GitBranch = parts[1];
            GitHasChanges = parts[2] == "1";
        }
        catch (FormatException)
        {
            // Ignore malformed or unrelated terminal title updates.
        }
    }

    [ObservableProperty]
    private ITerminalSession? session;

    [ObservableProperty]
    private ServerMetricsSnapshot? metrics;

    [ObservableProperty]
    private string metricsStatus = "LINUX / WAITING FOR SSH";

    [ObservableProperty]
    private DateTimeOffset? metricsUpdatedAt;

    [ObservableProperty]
    private string monitorWarningThresholdText = "90";

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
        _metricsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _metricsTimer.Tick += (_, _) => _metricsRefreshTask = RefreshMetricsAsync();
        if (IsRemoteMetricsVisible)
        {
            MetricsStatus = session is SecureShellSession ? "LINUX / CONNECTING" : "SSH / RECONNECT TO MONITOR";
            _metricsTimer.Start();
        }
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
        if (Kind == TerminalSessionKind.Ssh && value != "CONNECTED")
            MetricsStatus = "SSH / DISCONNECTED";
    }

    partial void OnMetricsChanged(ServerMetricsSnapshot? value)
    {
        if (value is not null)
        {
            MetricsUpdatedAt = DateTimeOffset.UtcNow;
            MetricsHistory.Add(new ServerMetricsHistorySample(
                MetricsUpdatedAt.Value,
                value.CpuPercent ?? 0,
                value.MemoryPercent,
                value.DiskPercent,
                value.NetworkReceiveBytesPerSecond + value.NetworkTransmitBytesPerSecond));
            while (MetricsHistory.Count > MetricsHistoryLimit)
                MetricsHistory.RemoveAt(0);
        }
        OnPropertyChanged(nameof(CpuMetricText));
        OnPropertyChanged(nameof(MemoryMetricText));
        OnPropertyChanged(nameof(DiskMetricText));
        OnPropertyChanged(nameof(NetworkMetricText));
        OnPropertyChanged(nameof(MetricsHostLabel));
        OnPropertyChanged(nameof(LoadMetricText));
        OnPropertyChanged(nameof(UptimeProcessMetricText));
        OnPropertyChanged(nameof(SwapMetricText));
        OnPropertyChanged(nameof(SshRoundTripText));
        OnPropertyChanged(nameof(CpuProgress));
        OnPropertyChanged(nameof(MemoryProgress));
        OnPropertyChanged(nameof(SwapProgress));
        OnPropertyChanged(nameof(DiskProgress));
        OnPropertyChanged(nameof(NetworkTotalBytesPerSecond));
        OnPropertyChanged(nameof(MetricsAlertText));
        OnPropertyChanged(nameof(MetricsAlertBrush));
    }

    partial void OnMonitorWarningThresholdTextChanged(string value)
    {
        OnPropertyChanged(nameof(MonitorWarningThreshold));
        OnPropertyChanged(nameof(MetricsAlertText));
        OnPropertyChanged(nameof(MetricsAlertBrush));
    }

    partial void OnMetricsUpdatedAtChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(MetricsUpdatedText));

    private async Task RefreshMetricsAsync()
    {
        if (_metricsRefreshRunning || _metricsUnsupported || _metricsCancellation.IsCancellationRequested)
            return;
        if (Session is not SecureShellSession ssh || !ssh.IsConnected || ssh.Client is null)
        {
            MetricsStatus = "SSH / DISCONNECTED";
            return;
        }

        _metricsRefreshRunning = true;
        try
        {
            MetricsStatus = Metrics is null ? "LINUX / COLLECTING" : "LIVE / 3S";
            var snapshot = await Task.Run(
                () => _metricsCollector.CollectAsync(ssh.Client, _metricsCancellation.Token),
                _metricsCancellation.Token);
            Metrics = snapshot;
            MetricsStatus = "LIVE / 3S";
        }
        catch (PlatformNotSupportedException exception)
        {
            _metricsUnsupported = true;
            Metrics = null;
            MetricsStatus = exception.Message;
        }
        catch (OperationCanceledException) when (_metricsCancellation.IsCancellationRequested)
        {
            // The terminal tab is closing.
        }
        catch
        {
            MetricsStatus = Metrics is null ? "METRICS / RETRYING" : "STALE / RETRYING";
        }
        finally
        {
            _metricsRefreshRunning = false;
        }
    }

    private static string FormatUptime(double uptimeSeconds)
    {
        var uptime = TimeSpan.FromSeconds(Math.Max(0, uptimeSeconds));
        var days = (int)uptime.TotalDays;
        return days > 0 ? $"{days}D {uptime.Hours:D2}:{uptime.Minutes:D2}"
            : $"{uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}";
    }

    private static string FormatRate(double bytesPerSecond) => bytesPerSecond >= 1024 * 1024
        ? $"{bytesPerSecond / 1024 / 1024:F1} MiB/s"
        : $"{bytesPerSecond / 1024:F0} KiB/s";

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
        _metricsTimer.Stop();
        await _metricsCancellation.CancelAsync();
        if (_metricsRefreshTask is not null)
            await _metricsRefreshTask;
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

public sealed record ServerMetricsHistorySample(
    DateTimeOffset Timestamp,
    double CpuPercent,
    double MemoryPercent,
    double DiskPercent,
    double NetworkBytesPerSecond);
