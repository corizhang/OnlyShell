using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Renci.SshNet.Common;
using OnlyShell.Core.Hosts;
using OnlyShell.Core.Files;
using OnlyShell.Core.Secrets;
using OnlyShell.Core.Scripts;
using OnlyShell.Presentation.Services;
using OnlyShell.Terminal;
using VirtualTerminal;

namespace OnlyShell.Presentation.ViewModels;

public sealed partial class WorkspaceViewModel : ObservableObject
{
    public sealed record SplitHostConnectionRequest(HostProfile Host, TerminalTabViewModel Placeholder);
    public sealed record SplitTabMoveRequest(TerminalTabViewModel Tab, bool ToLeftPane);

    private sealed record LocalTreeEntry(string FullPath, string RelativePath, bool IsDirectory, long Size, DateTime LastWriteTimeUtc);

    private sealed class DirectoryTransferManifest
    {
        private readonly Dictionary<string, string> _completed = new(StringComparer.Ordinal);
        public bool IsCompleted(string relativePath, string fingerprint) => _completed.TryGetValue(relativePath, out var completed) && completed == fingerprint;
        public void MarkCompleted(string relativePath, string fingerprint) => _completed[relativePath] = fingerprint;
    }

    private sealed record QueuedSftpTransfer(
        SftpTransferItemViewModel Item,
        Guid HostId,
        Func<ISftpSession, IProgress<double>, CancellationToken, Task> Execute);

    private readonly IHostRepository _hostRepository;
    private readonly IHostEditorDialogService _dialogs;
    private readonly ISshTerminalConnector _sshConnector;
    private readonly IConnectionCredentialPrompt _credentialPrompt;
    private readonly ICredentialStore _credentialStore;
    private readonly ISftpSessionConnector _sftpConnector;
    private readonly ISftpDialogService _sftpDialogs;
    private readonly IGitHubSyncService _gitHubSync;
    private readonly ISyncInteractionService _syncInteraction;
    private readonly ILocalTerminalConnector _localTerminalConnector;
    private readonly ITerminalWorkspaceStateStore _terminalWorkspaceStateStore;
    private readonly IScriptCenterStore _scriptCenterStore;
    private readonly IScriptExecutionInputsStore _scriptExecutionInputsStore;
    private readonly IRemoteScriptRunner _remoteScriptRunner;
    private readonly ILocalScriptRunner _localScriptRunner;
    private readonly IScriptCenterInteractionService _scriptCenterInteraction;
    private bool _scriptCenterLoaded;
    private Task _selectedScriptDefaultsLoading = Task.CompletedTask;
    private bool _localExecutionConfirmed;
    private CancellationTokenSource? _scriptRunCancellation;
    private readonly SemaphoreSlim _batchCredentialPromptLock = new(1, 1);
    private readonly SemaphoreSlim _batchCredentialPersistenceLock = new(1, 1);
    private bool _restoringTerminalWorkspace;
    private ISftpSession? _sftpSession;
    private HostProfile? _sftpHost;
    private readonly Queue<QueuedSftpTransfer> _sftpTransferQueue = new();
    private readonly Dictionary<SftpTransferItemViewModel, QueuedSftpTransfer> _sftpRetryActions = [];
    private CancellationTokenSource? _activeTransferCancellation;
    private Task? _transferQueueTask;
    private readonly DispatcherTimer _sftpConnectionMonitor;
    private IReadOnlyList<RemoteFileEntry> _selectedRemoteFiles = [];
    private IReadOnlyList<RemoteFileEntry> _selectedLocalFiles = [];

    private static readonly string TerminalPreferencesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnlyShell", "terminal-preferences.json");
    private bool _loadingTerminalPreferences = true;

    [ObservableProperty]
    private string appearanceTheme = "午夜暗色";

    [ObservableProperty]
    private string uiFontFamily = "Segoe UI";

    [ObservableProperty]
    private double uiFontSize = 11;

    [ObservableProperty]
    private string terminalFontFamily = "Cascadia Mono";

    [ObservableProperty]
    private double terminalFontSize = 14;

    [ObservableProperty]
    private int terminalScrollbackLines = 10000;

    [ObservableProperty]
    private bool confirmMultiLinePaste = true;

    public IReadOnlyList<string> TerminalFontFamilies { get; } = ["Cascadia Mono", "Consolas", "NSimSun"];
    public IReadOnlyList<double> TerminalFontSizes { get; } = [10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24];
    public IReadOnlyList<int> TerminalScrollbackOptions { get; } = [1000, 5000, 10000, 25000, 50000];
    public IReadOnlyList<string> AppearanceThemes { get; } = ["午夜暗色", "海洋蓝", "高对比"];
    public IReadOnlyList<string> UiFontFamilies { get; } = ["Segoe UI", "Segoe UI Variable Text", "Microsoft YaHei UI", "Arial"];
    public IReadOnlyList<double> UiFontSizes { get; } = [9, 10, 11, 12, 13, 14];

    public ObservableCollection<HostProfile> Hosts { get; } = [];
    public ObservableCollection<HostCatalogNode> HostCatalogNodes { get; } = [];
    public ObservableCollection<HostCatalogNode> VisibleHostCatalogNodes { get; } = [];
    public ObservableCollection<TerminalTabViewModel> TerminalTabs { get; } = [];
    public ObservableCollection<TerminalTabViewModel> LeftSplitTabs { get; } = [];
    public ObservableCollection<TerminalTabViewModel> RightSplitTabs { get; } = [];
    public IReadOnlyList<TerminalTabViewModel> SplitCandidates => TerminalTabs
        .Where(tab => !tab.IsNewTab && !ReferenceEquals(tab, SelectedTerminalTab))
        .ToArray();
    public string SplitToggleLabel => IsSplitView ? "[ EXIT SPLIT ]" : "[ SPLIT VIEW ]";
    public ObservableCollection<RemoteFileEntry> RemoteFiles { get; } = [];
    public ObservableCollection<RemoteFileEntry> LocalFiles { get; } = [];
    public IReadOnlyList<string> LocalDrives { get; } = DriveInfo.GetDrives().Where(drive => drive.IsReady).Select(drive => drive.Name).ToArray();
    public ObservableCollection<SftpTransferItemViewModel> SftpTransfers { get; } = [];
    public ObservableCollection<ScriptDefinition> Scripts { get; } = [];
    public ObservableCollection<ScriptTemplateFieldViewModel> ScriptTemplateFields { get; } = [];
    public ObservableCollection<ScriptCatalogNode> ScriptCatalogNodes { get; } = [];
    public ObservableCollection<ScriptCatalogNode> VisibleScriptCatalogNodes { get; } = [];
    public ObservableCollection<ScriptRunLog> RecentScriptRuns { get; } = [];
    public IReadOnlyList<string> ScriptExecutionTargetOptions { get; } = ["远程主机", "本机"];
    public IReadOnlyList<string> ScriptInputTypeOptions { get; } = ["文本", "数字", "布尔", "路径", "选项", "密码"];
    public IReadOnlyList<string> AvailableScriptRuntimeOptions => SelectedScriptExecutionTarget == "本机"
        ? ["PowerShell", "CMD", "Python 3"]
        : ["Bash", "Python 3"];
    public IReadOnlyList<RemoteFileEntry> SelectedRemoteFiles => _selectedRemoteFiles;
    public IReadOnlyList<RemoteFileEntry> SelectedLocalFiles => _selectedLocalFiles;
    public ICollectionView HostsView { get; }
    public ICollectionView ScriptsView { get; }

    private GridLength _scriptCatalogPanelHeight = new(2, GridUnitType.Star);
    private GridLength _recentRunsPanelHeight = new(1, GridUnitType.Star);
    public GridLength ScriptCatalogPanelHeight
    {
        get => IsScriptCatalogExpanded ? _scriptCatalogPanelHeight : GridLength.Auto;
        set
        {
            if (value.IsStar && value.Value > 0)
                _scriptCatalogPanelHeight = value;
        }
    }
    public GridLength RecentRunsPanelHeight
    {
        get => IsRecentRunsExpanded ? _recentRunsPanelHeight : GridLength.Auto;
        set
        {
            if (value.IsStar && value.Value > 0)
                _recentRunsPanelHeight = value;
        }
    }
    public GridLength ScriptCatalogListHeight => IsScriptCatalogExpanded
        ? new GridLength(1, GridUnitType.Star)
        : new GridLength(0);
    public GridLength RecentRunsListHeight => IsRecentRunsExpanded
        ? new GridLength(1, GridUnitType.Star)
        : new GridLength(0);
    public string ScriptCatalogToggleLabel => IsScriptCatalogExpanded ? "[ − ]" : "[ + ]";
    public string RecentRunsToggleLabel => IsRecentRunsExpanded ? "[ − ]" : "[ + ]";
    public string ScriptFolderPathDisplay => string.IsNullOrWhiteSpace(ScriptFolderPath) ? "/ (根目录)" : $"/ {ScriptFolderPath}";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditHostCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteHostCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectToSelectedHostCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForgetCredentialCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectSftpCommand))]
    private HostProfile? selectedHost;

    [ObservableProperty]
    private HostCatalogNode? selectedHostCatalogNode;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenRemoteDirectoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadRemoteFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameRemoteEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteRemoteEntryCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenRemoteDirectoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadRemoteFileCommand))]
    private RemoteFileEntry? selectedRemoteFile;

    [ObservableProperty]
    private TerminalTabViewModel? selectedTerminalTab;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleSplitViewCommand))]
    private bool isSplitView;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleSplitViewCommand))]
    private TerminalTabViewModel? splitSecondaryTab;

    [ObservableProperty]
    private TerminalTabViewModel? leftSplitSelectedTab;

    [ObservableProperty]
    private string hostFilter = string.Empty;

    [ObservableProperty]
    private string statusMessage = "LOCAL / READY";

    [ObservableProperty]
    private string activeSection = "主机";

    [ObservableProperty]
    private string remotePath = string.Empty;

    [ObservableProperty]
    private string localPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [ObservableProperty]
    private string selectedLocalDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? "C:\\";

    [ObservableProperty]
    private RemoteFileEntry? selectedLocalEntry;

    [ObservableProperty]
    private string sftpStatus = "DISCONNECTED";

    [ObservableProperty]
    private string syncOwner = string.Empty;

    [ObservableProperty]
    private string syncRepository = string.Empty;

    [ObservableProperty]
    private string syncBranch = "main";

    [ObservableProperty]
    private string syncStatus = "NOT CONNECTED";

    [ObservableProperty]
    private bool isSyncBusy;

    [ObservableProperty]
    private ScriptDefinition? selectedScript;

    [ObservableProperty]
    private ScriptCatalogNode? selectedScriptCatalogNode;

    [ObservableProperty]
    private string scriptName = string.Empty;

    [ObservableProperty]
    private string scriptDescription = string.Empty;

    [ObservableProperty]
    private string scriptContent = string.Empty;

    [ObservableProperty]
    private string selectedScriptRuntime = "Bash";

    [ObservableProperty]
    private string selectedScriptExecutionTarget = "远程主机";

    [ObservableProperty]
    private string scriptFolderPath = string.Empty;

    partial void OnScriptFolderPathChanged(string value) => OnPropertyChanged(nameof(ScriptFolderPathDisplay));

    [ObservableProperty]
    private string scriptInputTemplate = string.Empty;

    [ObservableProperty]
    private string scriptSearchText = string.Empty;

    [ObservableProperty]
    private bool isScriptCatalogExpanded = true;

    [ObservableProperty]
    private bool isRecentRunsExpanded = true;

    [ObservableProperty]
    private ScriptRunLog? selectedScriptRun;

    [ObservableProperty]
    private bool isScriptRunning;

    [ObservableProperty]
    private string scriptCenterStatus = "READY / SELECT A SCRIPT AND TARGET";

    public bool IsHostSection => string.Equals(ActiveSection, "主机", StringComparison.Ordinal);
    public bool IsFeaturePlaceholder => !IsHostSection && !IsTerminalSessionVisible && !IsTerminalEmpty && !IsSftpWorkspace && !IsSyncWorkspace && !IsScriptCenterWorkspace && !IsSettingsWorkspace;
    public bool IsSftpWorkspace => string.Equals(ActiveSection, "SFTP", StringComparison.Ordinal);
    public bool IsSyncWorkspace => string.Equals(ActiveSection, "同步", StringComparison.Ordinal);
    public bool IsScriptCenterWorkspace => string.Equals(ActiveSection, "脚本", StringComparison.Ordinal);
    public bool IsSettingsWorkspace => string.Equals(ActiveSection, "设置", StringComparison.Ordinal);
    public string SelectedScriptRunText => SelectedScriptRun is null
        ? "// NO RUN SELECTED"
        : $"[{SelectedScriptRun.StartedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}] {SelectedScriptRun.TargetType.ToUpperInvariant()} / {SelectedScriptRun.HostName} · {(SelectedScriptRun.ExitCode?.ToString() ?? (IsScriptRunning ? "RUNNING" : "ERROR"))}\n{SelectedScriptRun.Output}{(string.IsNullOrWhiteSpace(SelectedScriptRun.Error) ? string.Empty : $"\n--- STDERR / ERROR ---\n{SelectedScriptRun.Error}")}";
    public string ScriptExecutionTargetDescription => SelectedScriptExecutionTarget == "本机"
        ? "LOCAL / 当前 Windows 用户"
        : SelectedHost?.Name ?? "未选择主机";
    public bool IsSftpConnected => _sftpSession?.IsConnected == true;
    public bool IsSftpDisconnected => !IsSftpConnected;
    public HostProfile? SftpHost => _sftpHost;
    public bool HasSftpTransfers => SftpTransfers.Count > 0;
    public bool IsSftpTransferRunning => _activeTransferCancellation is not null;
    public int PendingTransferCount => SftpTransfers.Count(item => item.Status == "QUEUED");
    public bool IsTerminalSessionVisible => string.Equals(ActiveSection, "终端", StringComparison.Ordinal) && TerminalTabs.Count > 0;
    public bool IsTerminalEmpty => string.Equals(ActiveSection, "终端", StringComparison.Ordinal) && TerminalTabs.Count == 0;
    public bool IsTerminalWorkspace => string.Equals(ActiveSection, "终端", StringComparison.Ordinal);
    public string SectionHeading => IsHostSection ? "主机工作区" : $"{ActiveSection}工作区";
    public string SectionSubtitle => IsHostSection ? "本地主机与连接配置" : FeatureMessage;
    public string FeatureMessage => ActiveSection switch
    {
        "终端" => "在统一标签工作区运行本地 PowerShell、CMD 或 SSH；重启后恢复标签配置。",
        "SFTP" => "本地与远程双栏文件管理；选择文件后直接加入传输队列。",
        "同步" => "GitHub Device Flow · 私有仓库 · AES-GCM 加密主机配置同步。",
        "脚本" => "本机 PowerShell / CMD / Python 3，或远程 Bash / Python 3；执行结果和历史记录保存在本机。",
        "设置" => "调整终端外观与交互；更改会立即应用并保存在本机。",
        "传输" => "上传与下载任务队列将在 SFTP 功能阶段实现。",
        "历史" => "SSH 会话接入后会在此记录连接历史。",
        _ => "此工作区将在后续功能阶段实现。"
    };

    public int HostCount => Hosts.Count;
    public int ActiveSessionCount => TerminalTabs.Count(tab => !tab.IsNewTab);
    public bool HasSelectedHost => SelectedHost is not null;
    public bool NoHostSelected => SelectedHost is null;
    public string CredentialStatus => SelectedHost?.AuthenticationMethod == SshAuthenticationMethod.PrivateKey
        ? "LOCAL / PRIVATE KEY PATH"
        : string.IsNullOrWhiteSpace(SelectedHost?.CredentialReference)
            ? "LOCAL / NOT SAVED"
            : "LOCAL / SAVED IN CREDENTIAL MANAGER";

    public WorkspaceViewModel(
        IHostRepository hostRepository,
        IHostEditorDialogService dialogs,
        ISshTerminalConnector sshConnector,
        IConnectionCredentialPrompt credentialPrompt,
        ICredentialStore credentialStore,
        ISftpSessionConnector sftpConnector,
        ISftpDialogService sftpDialogs,
        IGitHubSyncService gitHubSync,
        ISyncInteractionService syncInteraction,
        ILocalTerminalConnector localTerminalConnector,
        ITerminalWorkspaceStateStore terminalWorkspaceStateStore,
        IScriptCenterStore scriptCenterStore,
        IScriptExecutionInputsStore scriptExecutionInputsStore,
        IRemoteScriptRunner remoteScriptRunner,
        ILocalScriptRunner localScriptRunner,
        IScriptCenterInteractionService scriptCenterInteraction)
    {
        _hostRepository = hostRepository;
        _dialogs = dialogs;
        _sshConnector = sshConnector;
        _credentialPrompt = credentialPrompt;
        _credentialStore = credentialStore;
        _sftpConnector = sftpConnector;
        _sftpDialogs = sftpDialogs;
        _gitHubSync = gitHubSync;
        _syncInteraction = syncInteraction;
        _localTerminalConnector = localTerminalConnector;
        _terminalWorkspaceStateStore = terminalWorkspaceStateStore;
        _scriptCenterStore = scriptCenterStore;
        _scriptExecutionInputsStore = scriptExecutionInputsStore;
        _remoteScriptRunner = remoteScriptRunner;
        _localScriptRunner = localScriptRunner;
        _scriptCenterInteraction = scriptCenterInteraction;
        LoadTerminalPreferences();
        _loadingTerminalPreferences = false;
        PropertyChanged += (_, args) =>
        {
            if (!_loadingTerminalPreferences && args.PropertyName is nameof(TerminalFontFamily)
                or nameof(TerminalFontSize) or nameof(TerminalScrollbackLines) or nameof(ConfirmMultiLinePaste)
                or nameof(AppearanceTheme) or nameof(UiFontFamily) or nameof(UiFontSize))
                SaveTerminalPreferences();
        };
        SyncOwner = _gitHubSync.SavedOwner ?? string.Empty;
        SyncRepository = _gitHubSync.SavedRepository ?? string.Empty;
        SyncBranch = _gitHubSync.SavedBranch ?? "main";
        SyncStatus = _gitHubSync.AuthenticationLabel;
        _sftpConnectionMonitor = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _sftpConnectionMonitor.Tick += (_, _) =>
        {
            if (_sftpSession is not null && !_sftpSession.IsConnected
                && SftpStatus != "CONNECTION LOST  ·  RECONNECT REQUIRED")
            {
                SftpStatus = "CONNECTION LOST  ·  RECONNECT REQUIRED";
                OnPropertyChanged(nameof(IsSftpConnected));
                OnPropertyChanged(nameof(IsSftpDisconnected));
                RetrySftpTransferCommand.NotifyCanExecuteChanged();
            }
        };
        _sftpConnectionMonitor.Start();
        Hosts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HostCount));
        SftpTransfers.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSftpTransfers));
            OnPropertyChanged(nameof(PendingTransferCount));
        };
        TerminalTabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ActiveSessionCount));
            OnPropertyChanged(nameof(IsTerminalSessionVisible));
            OnPropertyChanged(nameof(IsTerminalEmpty));
            OnPropertyChanged(nameof(IsFeaturePlaceholder));
            OnPropertyChanged(nameof(SplitCandidates));
            ToggleSplitViewCommand.NotifyCanExecuteChanged();
            foreach (var tab in TerminalTabs.Where(tab => !tab.IsNewTab))
            {
                if (IsSplitView && !LeftSplitTabs.Contains(tab) && !RightSplitTabs.Contains(tab))
                    LeftSplitTabs.Add(tab);
            }
            if (SplitCandidates.Count == 0)
                IsSplitView = false;
            if (!_restoringTerminalWorkspace)
                _ = SaveTerminalWorkspaceStateAsync();
        };

        HostsView = CollectionViewSource.GetDefaultView(Hosts);
        HostsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HostProfile.GroupLabel)));
        HostsView.SortDescriptions.Add(new SortDescription(nameof(HostProfile.Name), ListSortDirection.Ascending));
        HostsView.Filter = FilterHost;
        ScriptsView = CollectionViewSource.GetDefaultView(Scripts);
        ScriptsView.Filter = FilterScript;
    }

    private void LoadTerminalPreferences()
    {
        try
        {
            if (!File.Exists(TerminalPreferencesPath))
                return;
            var saved = JsonSerializer.Deserialize<TerminalPreferences>(File.ReadAllText(TerminalPreferencesPath));
            if (saved is null)
                return;

            AppearanceTheme = AppearanceThemes.Contains(saved.AppearanceTheme) ? saved.AppearanceTheme : "午夜暗色";
            UiFontFamily = UiFontFamilies.Contains(saved.UiFontFamily) ? saved.UiFontFamily : "Segoe UI";
            UiFontSize = UiFontSizes.Contains(saved.UiFontSize) ? saved.UiFontSize : 11;
            TerminalFontFamily = TerminalFontFamilies.Contains(saved.FontFamily) ? saved.FontFamily : "Cascadia Mono";
            TerminalFontSize = TerminalFontSizes.Contains(saved.FontSize) ? saved.FontSize : 14;
            TerminalScrollbackLines = TerminalScrollbackOptions.Contains(saved.ScrollbackLines) ? saved.ScrollbackLines : 10000;
            ConfirmMultiLinePaste = saved.ConfirmMultiLinePaste;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Could not load terminal preferences: {exception}");
        }
    }

    private void SaveTerminalPreferences()
    {
        try
        {
            var directory = Path.GetDirectoryName(TerminalPreferencesPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = TerminalPreferencesPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new TerminalPreferences(
                TerminalFontFamily, TerminalFontSize, TerminalScrollbackLines, ConfirmMultiLinePaste,
                AppearanceTheme, UiFontFamily, UiFontSize)));
            File.Move(temporaryPath, TerminalPreferencesPath, overwrite: true);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Could not save terminal preferences: {exception}");
        }
    }

    [RelayCommand]
    private void ResetAppearance()
    {
        AppearanceTheme = "午夜暗色";
        UiFontFamily = "Segoe UI";
        UiFontSize = 11;
        TerminalFontFamily = "Cascadia Mono";
        TerminalFontSize = 14;
    }

    private sealed record TerminalPreferences(
        string FontFamily,
        double FontSize,
        int ScrollbackLines,
        bool ConfirmMultiLinePaste,
        string AppearanceTheme = "午夜暗色",
        string UiFontFamily = "Segoe UI",
        double UiFontSize = 11);

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var items = await _hostRepository.ListAsync(cancellationToken);
        Hosts.Clear();
        foreach (var item in items)
            Hosts.Add(item);
        RebuildHostCatalogTree();
        StatusMessage = $"LOCAL / READY   ·   {HostCount} HOSTS";
    }

    public async Task LoadScriptCenterAsync(CancellationToken cancellationToken = default)
    {
        if (_scriptCenterLoaded)
            return;
        try
        {
            foreach (var script in await _scriptCenterStore.LoadScriptsAsync(cancellationToken))
                Scripts.Add(script);
            foreach (var run in await _scriptCenterStore.LoadRecentRunsAsync(100, cancellationToken))
                RecentScriptRuns.Add(run);
            SelectedScript = Scripts.FirstOrDefault();
            RebuildScriptCatalogTree();
            SelectedScriptRun = RecentScriptRuns.FirstOrDefault();
            _scriptCenterLoaded = true;
        }
        catch (Exception exception)
        {
            ScriptCenterStatus = "LOAD FAILED / SCRIPT CATALOG";
            await _syncInteraction.ShowErrorAsync("加载脚本中心失败", exception.Message);
        }
    }

    public async Task RestoreTerminalWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        if (_restoringTerminalWorkspace)
            return;
        _restoringTerminalWorkspace = true;
        try
        {
            var state = await _terminalWorkspaceStateStore.LoadAsync(cancellationToken);
            if (state is null)
                return;

            foreach (var saved in state.Tabs.Take(12))
            {
                if (string.Equals(saved.Kind, "local", StringComparison.OrdinalIgnoreCase))
                {
                    var shellId = saved.Shell is "cmd" or "pwsh" ? saved.Shell : "powershell";
                    try
                    {
                        var session = _localTerminalConnector.Start(ParseLocalShell(shellId), saved.WorkingDirectory);
                        TerminalTabs.Add(new TerminalTabViewModel(null, session, TerminalSessionKind.Local, shellId, saved.WorkingDirectory));
                    }
                    catch (Exception exception)
                    {
                        StatusMessage = $"LOCAL SHELL / RESTORE FAILED  ·  {exception.Message}";
                    }
                    continue;
                }

                if (string.Equals(saved.Kind, "ssh", StringComparison.OrdinalIgnoreCase)
                    && saved.HostId is { } hostId
                    && Hosts.FirstOrDefault(host => host.Id == hostId) is { } host)
                    TerminalTabs.Add(new TerminalTabViewModel(host, null, TerminalSessionKind.Ssh));
            }

            SelectedTerminalTab = TerminalTabs.Count == 0
                ? null
                : TerminalTabs[Math.Clamp(state.SelectedTabIndex, 0, TerminalTabs.Count - 1)];
            ActiveSection = state.ActiveSection is "终端" or "主机" or "SFTP" or "同步" or "脚本" or "设置"
                ? state.ActiveSection
                : TerminalTabs.Count > 0 ? "终端" : "主机";
            if (TerminalTabs.Count > 0 && state.ActiveSection == "终端")
                StatusMessage = $"TERMINAL / RESTORED  ·  {TerminalTabs.Count} TABS";
        }
        finally
        {
            _restoringTerminalWorkspace = false;
            await SaveTerminalWorkspaceStateAsync(cancellationToken);
        }
    }

    partial void OnHostFilterChanged(string value)
    {
        HostsView.Refresh();
        RebuildHostCatalogTree();
    }

    partial void OnScriptSearchTextChanged(string value)
    {
        ScriptsView.Refresh();
        RebuildScriptCatalogTree();
    }

    partial void OnIsScriptCatalogExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ScriptCatalogListHeight));
        OnPropertyChanged(nameof(ScriptCatalogPanelHeight));
        OnPropertyChanged(nameof(RecentRunsPanelHeight));
        OnPropertyChanged(nameof(RecentRunsListHeight));
        OnPropertyChanged(nameof(ScriptCatalogToggleLabel));
    }

    partial void OnIsRecentRunsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ScriptCatalogListHeight));
        OnPropertyChanged(nameof(ScriptCatalogPanelHeight));
        OnPropertyChanged(nameof(RecentRunsPanelHeight));
        OnPropertyChanged(nameof(RecentRunsListHeight));
        OnPropertyChanged(nameof(RecentRunsToggleLabel));
    }

    partial void OnSelectedHostChanged(HostProfile? value)
    {
        OnPropertyChanged(nameof(HasSelectedHost));
        OnPropertyChanged(nameof(NoHostSelected));
        OnPropertyChanged(nameof(CredentialStatus));
        OnPropertyChanged(nameof(ScriptExecutionTargetDescription));
        ConnectSftpCommand.NotifyCanExecuteChanged();
        if (value is not null)
            SelectedHostCatalogNode = VisibleHostCatalogNodes.FirstOrDefault(node => node.Host?.Id == value.Id);
        else if (SelectedHostCatalogNode?.Host is not null)
            SelectedHostCatalogNode = null;
    }

    partial void OnSelectedHostCatalogNodeChanged(HostCatalogNode? value)
    {
        if (value?.Host is { } host && SelectedHost?.Id != host.Id)
            SelectedHost = Hosts.FirstOrDefault(item => item.Id == host.Id) ?? host;
        else if (value is { IsFolder: true } && SelectedHost is not null)
            SelectedHost = null;
        else if (value is null && SelectedHost is not null)
            SelectedHost = null;
    }

    partial void OnActiveSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsHostSection));
        OnPropertyChanged(nameof(IsSftpWorkspace));
        OnPropertyChanged(nameof(IsSyncWorkspace));
        OnPropertyChanged(nameof(IsScriptCenterWorkspace));
        OnPropertyChanged(nameof(IsSettingsWorkspace));
        OnPropertyChanged(nameof(IsSftpConnected));
        OnPropertyChanged(nameof(IsSftpDisconnected));
        OnPropertyChanged(nameof(IsFeaturePlaceholder));
        OnPropertyChanged(nameof(IsTerminalSessionVisible));
        OnPropertyChanged(nameof(IsTerminalEmpty));
        OnPropertyChanged(nameof(IsTerminalWorkspace));
        OnPropertyChanged(nameof(FeatureMessage));
        OnPropertyChanged(nameof(SectionHeading));
        OnPropertyChanged(nameof(SectionSubtitle));
        if (!_restoringTerminalWorkspace)
            _ = SaveTerminalWorkspaceStateAsync();
        if (IsScriptCenterWorkspace)
            _ = LoadScriptCenterAsync();
    }

    partial void OnSelectedScriptChanged(ScriptDefinition? value)
    {
        if (value is null)
        {
            ScriptName = string.Empty;
            ScriptDescription = string.Empty;
            ScriptContent = string.Empty;
            ScriptFolderPath = string.Empty;
            ScriptInputTemplate = string.Empty;
            ScriptTemplateFields.Clear();
            SelectedScriptRuntime = "Bash";
            SelectedScriptCatalogNode = null;
            return;
        }
        ScriptName = value.Name;
        ScriptDescription = value.Description;
        ScriptContent = value.Content;
        ScriptFolderPath = value.FolderPath;
        ScriptInputTemplate = value.InputTemplate ?? string.Empty;
        ScriptTemplateFields.Clear();
        var fields = value.InputFields is { Count: > 0 } ? value.InputFields : ParseLegacyInputTemplate(value.InputTemplate);
        foreach (var field in fields)
            ScriptTemplateFields.Add(new ScriptTemplateFieldViewModel(field.Kind, field.Name, InputTypeName(field.ValueType), field.IsRequired,
                field.IsSensitive || field.ValueType == ScriptInputValueType.Secret, field.Options, group: field.Group));
        _selectedScriptDefaultsLoading = LoadScriptInputDefaultsAsync(value.Id);
        SelectedScriptExecutionTarget = value.ExecutionTarget == "Local" ? "本机" : "远程主机";
        SelectedScriptRuntime = RuntimeName(value.Runtime);
        SelectedScriptCatalogNode = VisibleScriptCatalogNodes.FirstOrDefault(node => node.Script?.Id == value.Id);
    }

    partial void OnSelectedScriptCatalogNodeChanged(ScriptCatalogNode? value)
    {
        if (value?.Script is { } script && SelectedScript?.Id != script.Id)
            SelectedScript = Scripts.FirstOrDefault(item => item.Id == script.Id);
    }

    partial void OnSelectedScriptExecutionTargetChanged(string value)
    {
        OnPropertyChanged(nameof(AvailableScriptRuntimeOptions));
        OnPropertyChanged(nameof(ScriptExecutionTargetDescription));
        if (!AvailableScriptRuntimeOptions.Contains(SelectedScriptRuntime, StringComparer.Ordinal))
            ScriptCenterStatus = $"TARGET CHANGED / CHOOSE A SUPPORTED RUNTIME (CURRENT: {SelectedScriptRuntime})";
    }

    private static ScriptRuntimeKind ParseRuntime(string runtime) => runtime switch
    {
        "Python 3" => ScriptRuntimeKind.Python3,
        "PowerShell" => ScriptRuntimeKind.PowerShell,
        "CMD" => ScriptRuntimeKind.Cmd,
        _ => ScriptRuntimeKind.Bash
    };

    private static string RuntimeName(ScriptRuntimeKind runtime) => runtime switch
    {
        ScriptRuntimeKind.Python3 => "Python 3",
        ScriptRuntimeKind.PowerShell => "PowerShell",
        ScriptRuntimeKind.Cmd => "CMD",
        _ => "Bash"
    };

    partial void OnSelectedTerminalTabChanged(TerminalTabViewModel? value)
    {
        OnPropertyChanged(nameof(SplitCandidates));
        OnPropertyChanged(nameof(SplitToggleLabel));
        ToggleSplitViewCommand.NotifyCanExecuteChanged();
        if (value?.IsNewTab == true && !IsSplitView)
            IsSplitView = false;
        else if (IsSplitView && value is not null && LeftSplitTabs.Contains(value))
            LeftSplitSelectedTab = value;
        if (value?.Host is { } host && SelectedHost?.Id != host.Id)
            SelectedHost = Hosts.FirstOrDefault(item => item.Id == host.Id) ?? host;
        ConnectSftpCommand.NotifyCanExecuteChanged();
        if (!_restoringTerminalWorkspace)
            _ = SaveTerminalWorkspaceStateAsync();
    }

    partial void OnIsSplitViewChanged(bool value)
    {
        OnPropertyChanged(nameof(SplitToggleLabel));
        ToggleSplitViewCommand.NotifyCanExecuteChanged();
        if (!value)
        {
            LeftSplitTabs.Clear();
            RightSplitTabs.Clear();
        }
    }

    partial void OnSplitSecondaryTabChanged(TerminalTabViewModel? value)
    {
        if (IsSplitView && value is not null && ReferenceEquals(value, LeftSplitSelectedTab))
            SplitSecondaryTab = RightSplitTabs.FirstOrDefault(tab => !ReferenceEquals(tab, value));
    }

    partial void OnLeftSplitSelectedTabChanged(TerminalTabViewModel? value)
    {
        if (IsSplitView && value is not null && ReferenceEquals(value, SplitSecondaryTab))
            SplitSecondaryTab = RightSplitTabs.FirstOrDefault(tab => !ReferenceEquals(tab, value));
    }

    private bool CanToggleSplitView() => IsSplitView
        || (SelectedTerminalTab is { IsNewTab: false } && SplitCandidates.Count > 0);

    [RelayCommand(CanExecute = nameof(CanToggleSplitView))]
    private void ToggleSplitView()
    {
        if (IsSplitView)
        {
            IsSplitView = false;
            return;
        }

        SplitSecondaryTab = SplitCandidates.FirstOrDefault();
        if (SplitSecondaryTab is null)
            return;

        LeftSplitTabs.Clear();
        RightSplitTabs.Clear();
        foreach (var tab in TerminalTabs.Where(tab => !tab.IsNewTab))
        {
            if (ReferenceEquals(tab, SplitSecondaryTab))
                RightSplitTabs.Add(tab);
            else
                LeftSplitTabs.Add(tab);
        }
        LeftSplitSelectedTab = SelectedTerminalTab;
        IsSplitView = true;
    }

    [RelayCommand]
    private Task CloseLeftSplitTabAsync(TerminalTabViewModel? tab) => CloseSplitPaneTabAsync(tab, LeftSplitTabs, true);

    [RelayCommand]
    private Task CloseRightSplitTabAsync(TerminalTabViewModel? tab) => CloseSplitPaneTabAsync(tab, RightSplitTabs, false);

    [RelayCommand]
    private void AddLeftSplitTab() => AddSplitQuickConnectTab(LeftSplitTabs, true);

    [RelayCommand]
    private void AddRightSplitTab() => AddSplitQuickConnectTab(RightSplitTabs, false);

    private void AddSplitQuickConnectTab(ObservableCollection<TerminalTabViewModel> paneTabs, bool isLeftPane)
    {
        if (!IsSplitView)
            return;

        var tab = new TerminalTabViewModel(null, null, TerminalSessionKind.New);
        paneTabs.Add(tab);
        if (isLeftPane)
            LeftSplitSelectedTab = tab;
        else
            SplitSecondaryTab = tab;
    }

    [RelayCommand]
    private void MoveSplitTab(SplitTabMoveRequest? request)
    {
        if (request is null || request.Tab.IsNewTab)
            return;

        var source = LeftSplitTabs.Contains(request.Tab) ? LeftSplitTabs
            : RightSplitTabs.Contains(request.Tab) ? RightSplitTabs
            : null;
        var target = request.ToLeftPane ? LeftSplitTabs : RightSplitTabs;
        if (source is null || ReferenceEquals(source, target))
            return;

        source.Remove(request.Tab);
        if (!target.Contains(request.Tab))
            target.Add(request.Tab);
        if (request.ToLeftPane)
            LeftSplitSelectedTab = request.Tab;
        else
            SplitSecondaryTab = request.Tab;
    }

    [RelayCommand]
    private Task OpenHostInSplitTabAsync(SplitHostConnectionRequest? request)
    {
        if (request is null)
            return Task.CompletedTask;
        var paneTabs = LeftSplitTabs.Contains(request.Placeholder) ? LeftSplitTabs
            : RightSplitTabs.Contains(request.Placeholder) ? RightSplitTabs
            : null;
        return paneTabs is null ? Task.CompletedTask : OpenHostTerminalCoreAsync(request.Host, request.Placeholder, paneTabs);
    }

    private async Task CloseSplitPaneTabAsync(TerminalTabViewModel? tab, ObservableCollection<TerminalTabViewModel> paneTabs, bool isLeftPane)
    {
        if (tab is null || !paneTabs.Remove(tab))
            return;

        if (isLeftPane && ReferenceEquals(LeftSplitSelectedTab, tab))
            LeftSplitSelectedTab = LeftSplitTabs.FirstOrDefault();
        else if (!isLeftPane && ReferenceEquals(SplitSecondaryTab, tab))
            SplitSecondaryTab = RightSplitTabs.FirstOrDefault(item => !ReferenceEquals(item, LeftSplitSelectedTab));

        if (LeftSplitTabs.Contains(tab) || RightSplitTabs.Contains(tab))
            return;

        if (tab.IsNewTab)
        {
            await tab.DisposeAsync();
            return;
        }

        TerminalTabs.Remove(tab);
        await tab.DisposeAsync();
        if (ReferenceEquals(SelectedTerminalTab, tab))
            SelectedTerminalTab = TerminalTabs.FirstOrDefault(item => !item.IsNewTab);
        if (TerminalTabs.Count(item => !item.IsNewTab) < 2)
            IsSplitView = false;
    }

    private bool FilterHost(object item)
    {
        if (item is not HostProfile host || string.IsNullOrWhiteSpace(HostFilter))
            return item is HostProfile;

        return host.Name.Contains(HostFilter, StringComparison.CurrentCultureIgnoreCase)
            || host.HostName.Contains(HostFilter, StringComparison.CurrentCultureIgnoreCase)
            || host.UserName.Contains(HostFilter, StringComparison.CurrentCultureIgnoreCase)
            || host.GroupLabel.Contains(HostFilter, StringComparison.CurrentCultureIgnoreCase);
    }

    [RelayCommand]
    private void ToggleHostGroup(HostCatalogNode? node)
    {
        if (node is not { IsFolder: true })
            return;
        node.IsExpanded = !node.IsExpanded;
        RebuildVisibleHostCatalogNodes();
    }

    private void RebuildHostCatalogTree()
    {
        var expansion = EnumerateHostCatalogNodes(HostCatalogNodes)
            .Where(node => node.IsFolder)
            .ToDictionary(node => node.GroupPath, node => node.IsExpanded, StringComparer.OrdinalIgnoreCase);
        HostCatalogNodes.Clear();
        VisibleHostCatalogNodes.Clear();
        var folders = new Dictionary<string, HostCatalogNode>(StringComparer.OrdinalIgnoreCase);
        var query = HostFilter.Trim();
        foreach (var host in Hosts.Where(host => MatchesHostSearch(host, query)))
        {
            var siblings = HostCatalogNodes;
            var segments = NormalizeCatalogPath(host.Group ?? "未分组").Split('/', StringSplitOptions.RemoveEmptyEntries);
            var pathSegments = new List<string>();
            foreach (var segment in segments)
            {
                pathSegments.Add(segment);
                var path = string.Join('/', pathSegments);
                if (!folders.TryGetValue(path, out var folder))
                {
                    folder = new HostCatalogNode(segment, path)
                    {
                        IsExpanded = query.Length > 0 || !expansion.TryGetValue(path, out var wasExpanded) || wasExpanded
                    };
                    folders.Add(path, folder);
                    siblings.Add(folder);
                }
                siblings = folder.Children;
            }
            siblings.Add(new HostCatalogNode(host.Name, NormalizeCatalogPath(host.Group ?? "未分组"), host));
        }
        SortHostCatalogNodes(HostCatalogNodes);
        RebuildVisibleHostCatalogNodes();
        SelectedHostCatalogNode = VisibleHostCatalogNodes.FirstOrDefault(node => node.Host?.Id == SelectedHost?.Id);
    }

    private void RebuildVisibleHostCatalogNodes()
    {
        VisibleHostCatalogNodes.Clear();
        AddVisibleHostNodes(HostCatalogNodes, 0);
    }

    private void AddVisibleHostNodes(IReadOnlyList<HostCatalogNode> siblings, int depth)
    {
        foreach (var node in siblings)
        {
            node.SetDepth(depth);
            VisibleHostCatalogNodes.Add(node);
            if (node.IsFolder && node.IsExpanded)
                AddVisibleHostNodes(node.Children, depth + 1);
        }
    }

    private bool MatchesHostSearch(HostProfile host, string query) => query.Length == 0
        || host.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || host.HostName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || host.UserName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || host.GroupLabel.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private static IEnumerable<HostCatalogNode> EnumerateHostCatalogNodes(IEnumerable<HostCatalogNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in EnumerateHostCatalogNodes(node.Children))
                yield return child;
        }
    }

    private static void SortHostCatalogNodes(ObservableCollection<HostCatalogNode> nodes)
    {
        var sorted = nodes.OrderByDescending(node => node.IsFolder)
            .ThenBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        nodes.Clear();
        foreach (var node in sorted)
        {
            SortHostCatalogNodes(node.Children);
            nodes.Add(node);
        }
    }

    private static string NormalizeCatalogPath(string? path) => string.Join('/',
        (path ?? "").Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => segment is not "." and not ".."));

    [RelayCommand]
    private async Task AddHostAsync()
    {
        var result = await _dialogs.EditAsync(null, Hosts.Select(host => host.Group ?? "").Where(group => group.Length > 0).ToArray());
        if (result is null)
            return;

        await SaveAndRefreshAsync(result);
        SelectedHost = Hosts.FirstOrDefault(host => host.Id == result.Id);
    }

    [RelayCommand(CanExecute = nameof(CanEditSelectedHost))]
    private async Task EditHostAsync()
    {
        if (SelectedHost is null)
            return;

        var result = await _dialogs.EditAsync(SelectedHost, Hosts.Select(host => host.Group ?? "").Where(group => group.Length > 0).ToArray());
        if (result is null)
            return;

        await SaveAndRefreshAsync(result);
        SelectedHost = Hosts.FirstOrDefault(host => host.Id == result.Id);
    }

    [RelayCommand(CanExecute = nameof(CanEditSelectedHost))]
    private async Task DeleteHostAsync()
    {
        if (SelectedHost is null || !await _dialogs.ConfirmDeleteAsync(SelectedHost))
            return;

        var host = SelectedHost;
        var id = host.Id;
        try
        {
            await _hostRepository.DeleteAsync(id);
            await LoadAsync();
            SelectedHost = null;
            if (!string.IsNullOrWhiteSpace(host.CredentialReference))
            {
                try
                {
                    await _credentialStore.DeleteAsync(host.CredentialReference);
                }
                catch (Exception credentialException)
                {
                    await _dialogs.ShowErrorAsync($"主机配置已删除，但 Windows 凭据管理器未能删除关联密码。\n\n{credentialException.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            StatusMessage = "LOCAL DB / ERROR";
            await _dialogs.ShowErrorAsync($"删除主机配置失败。\n\n{exception.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanForgetCredential))]
    private async Task ForgetCredentialAsync()
    {
        var host = SelectedHost;
        if (host is null || string.IsNullOrWhiteSpace(host.CredentialReference))
            return;
        if (!await _dialogs.ConfirmForgetCredentialAsync(host))
            return;

        try
        {
            await _credentialStore.DeleteAsync(host.CredentialReference);
            await _hostRepository.SaveAsync(host with { CredentialReference = null });
            SelectedHost = host with { CredentialReference = null };
            await LoadAsync();
            SelectedHost = Hosts.FirstOrDefault(item => item.Id == host.Id);
            StatusMessage = "LOCAL / SAVED CREDENTIAL FORGOTTEN";
        }
        catch (Exception exception)
        {
            await _dialogs.ShowErrorAsync($"无法删除本机保存的 SSH 密码。\n\n{exception.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSelectedHost))]
    private Task ConnectToSelectedHostAsync() => OpenHostTerminalAsync(SelectedHost);

    [RelayCommand]
    private void AddTerminalTab()
    {
        ActiveSection = "终端";
        if (SelectedTerminalTab?.IsNewTab == true)
            return;
        var tab = new TerminalTabViewModel(null, null, TerminalSessionKind.New);
        TerminalTabs.Add(tab);
        SelectedTerminalTab = tab;
        StatusMessage = "TERMINAL / SELECT A HOST";
    }

    [RelayCommand]
    private Task OpenHostTerminalAsync(HostProfile? host) => OpenHostTerminalCoreAsync(host, null, null);

    private async Task OpenHostTerminalCoreAsync(
        HostProfile? host,
        TerminalTabViewModel? splitPlaceholder,
        ObservableCollection<TerminalTabViewModel>? splitPaneTabs)
    {
        if (host is null)
            return;

        var connectingTab = new TerminalTabViewModel(host, null, TerminalSessionKind.Ssh);
        connectingTab.SetConnectionStatus("CONNECTING");
        TerminalTabViewModel? replacedPlaceholder = null;
        var replacedPlaceholderIndex = -1;
        if (splitPlaceholder is not null && splitPaneTabs is not null)
        {
            var splitIndex = splitPaneTabs.IndexOf(splitPlaceholder);
            if (splitIndex < 0)
                return;
            splitPaneTabs[splitIndex] = connectingTab;
            if (ReferenceEquals(splitPaneTabs, LeftSplitTabs))
                LeftSplitSelectedTab = connectingTab;
            else
                SplitSecondaryTab = connectingTab;
            TerminalTabs.Add(connectingTab);
            replacedPlaceholder = splitPlaceholder;
            replacedPlaceholderIndex = splitIndex;
        }
        else
        {
            replacedPlaceholder = SelectedTerminalTab is { IsNewTab: true } newTab ? newTab : null;
            if (replacedPlaceholder is not null)
            {
                replacedPlaceholderIndex = TerminalTabs.IndexOf(replacedPlaceholder);
                TerminalTabs[replacedPlaceholderIndex] = connectingTab;
            }
            else
            {
                TerminalTabs.Add(connectingTab);
            }
            SelectedTerminalTab = connectingTab;
        }
        ActiveSection = "终端";
        StatusMessage = $"SSH / CONNECTING   ·   {host.HostName}:{host.Port}";
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        try
        {
            var (session, credential) = await ConnectWithCredentialFallbackAsync(
                host,
                (secret, token) => _sshConnector.ConnectAsync(host, secret, token),
                message => _dialogs.ShowErrorAsync(message));
            if (session is null || credential is null)
            {
                await RemoveConnectingTabAsync(connectingTab, replacedPlaceholder, replacedPlaceholderIndex, splitPaneTabs);
                StatusMessage = $"SSH / CONNECTION CANCELLED   ·   {host.HostName}:{host.Port}";
                return;
            }

            if (!TerminalTabs.Contains(connectingTab))
            {
                session.Dispose();
                return;
            }

            var secret = credential.Secret;
            await connectingTab.ReplaceSessionAsync(session);
            if (splitPlaceholder is not null && splitPaneTabs is not null)
            {
                if (replacedPlaceholder is not null)
                    await replacedPlaceholder.DisposeAsync();
                if (ReferenceEquals(splitPaneTabs, LeftSplitTabs))
                    LeftSplitSelectedTab = connectingTab;
                else
                    SplitSecondaryTab = connectingTab;
            }
            else
            {
                if (replacedPlaceholder is not null)
                    await replacedPlaceholder.DisposeAsync();
                SelectedTerminalTab = connectingTab;
            }
            StatusMessage = $"SSH / CONNECTED   ·   {host.HostName}:{host.Port}";
            await TryConnectSftpForSshAsync(host, credential.Secret);

            string? stagedCredentialReference = null;
            try
            {
                var updatedHost = host with { LastConnectedAt = DateTimeOffset.UtcNow };
                if (host.AuthenticationMethod == SshAuthenticationMethod.Password && credential.RememberOnThisDevice && !credential.WasSavedCredential)
                {
                    stagedCredentialReference = await _credentialStore.StoreAsync(secret);
                    updatedHost = updatedHost with { CredentialReference = stagedCredentialReference };
                }
                else if (host.AuthenticationMethod == SshAuthenticationMethod.Password && !credential.WasSavedCredential
                    && !string.IsNullOrWhiteSpace(host.CredentialReference))
                {
                    await _credentialStore.DeleteAsync(host.CredentialReference);
                    updatedHost = updatedHost with { CredentialReference = null };
                }

                await _hostRepository.SaveAsync(updatedHost);
                var previousReference = host.CredentialReference;
                stagedCredentialReference = null;
                secret = string.Empty;
                if (credential.RememberOnThisDevice && !credential.WasSavedCredential
                    && !string.IsNullOrWhiteSpace(previousReference))
                    await _credentialStore.DeleteAsync(previousReference);
                await LoadAsync();
            }
            catch (Exception exception)
            {
                if (!string.IsNullOrWhiteSpace(stagedCredentialReference))
                {
                    try { await _credentialStore.DeleteAsync(stagedCredentialReference); }
                    catch { /* Keep the connection usable; the orphan can be cleaned up later. */ }
                }
                secret = string.Empty;
                StatusMessage = "SSH / CONNECTED   ·   LOCAL CREDENTIAL ERROR";
                await _dialogs.ShowErrorAsync($"SSH 已连接，但本地凭据或连接时间更新失败。\n\n{exception.Message}");
            }
        }
        catch (Exception exception)
        {
            connectingTab.SetConnectionStatus("CONNECTION FAILED");
            StatusMessage = $"SSH / FAILED   ·   {host.HostName}:{host.Port}";
            await _dialogs.ShowErrorAsync($"无法连接到 {host.Name}。\n\n{exception.Message}");
        }
    }

    private async Task RemoveConnectingTabAsync(
        TerminalTabViewModel connectingTab,
        TerminalTabViewModel? replacedPlaceholder,
        int replacedPlaceholderIndex,
        ObservableCollection<TerminalTabViewModel>? splitPaneTabs)
    {
        TerminalTabs.Remove(connectingTab);
        if (splitPaneTabs is not null)
        {
            var index = splitPaneTabs.IndexOf(connectingTab);
            if (index >= 0 && replacedPlaceholder is not null)
                splitPaneTabs[index] = replacedPlaceholder;
            if (ReferenceEquals(splitPaneTabs, LeftSplitTabs))
                LeftSplitSelectedTab = replacedPlaceholder;
            else
                SplitSecondaryTab = replacedPlaceholder;
        }
        else if (replacedPlaceholder is not null && replacedPlaceholderIndex >= 0)
        {
            TerminalTabs[replacedPlaceholderIndex] = replacedPlaceholder;
            SelectedTerminalTab = replacedPlaceholder;
        }
        else if (ReferenceEquals(SelectedTerminalTab, connectingTab))
        {
            SelectedTerminalTab = TerminalTabs.LastOrDefault(item => !item.IsNewTab);
        }

        await connectingTab.DisposeAsync();
    }

    [RelayCommand]
    private async Task CloseTerminalAsync(TerminalTabViewModel? tab)
    {
        if (tab is null)
            return;
        if (!await _dialogs.ConfirmCloseTerminalAsync(tab.DisplayName))
            return;

        var wasSelected = ReferenceEquals(SelectedTerminalTab, tab);
        TerminalTabs.Remove(tab);
        await tab.DisposeAsync();

        if (wasSelected)
            SelectedTerminalTab = TerminalTabs.LastOrDefault();

        if (TerminalTabs.Count == 0)
        {
            ActiveSection = "主机";
            StatusMessage = $"LOCAL / READY   ·   {HostCount} HOSTS";
        }
    }

    [RelayCommand]
    private async Task ReconnectTerminalAsync(TerminalTabViewModel? tab)
    {
        if (tab is null || tab.IsConnected)
            return;

        if (tab.Kind == TerminalSessionKind.Local)
        {
            try
            {
                var local = _localTerminalConnector.Start(ParseLocalShell(tab.LocalShellId ?? "powershell"), tab.LocalWorkingDirectory);
                await tab.ReplaceSessionAsync(local);
                StatusMessage = $"LOCAL / RESTARTED  ·  {tab.LocalShellLabel}";
            }
            catch (Exception exception)
            {
                StatusMessage = "LOCAL SHELL / RESTART FAILED";
                await _dialogs.ShowErrorAsync($"无法重新启动本地终端。\n\n{exception.Message}");
            }
            return;
        }

        var host = tab.Host;
        if (host is null)
            return;
        tab.SetConnectionStatus("CONNECTING");
        StatusMessage = $"SSH / RECONNECTING   ·   {host.HostName}:{host.Port}";
        SecureShellSession session;
        ConnectionCredential credential;
        try
        {
            var connection = await ConnectWithCredentialFallbackAsync(
                host,
                (secret, token) => _sshConnector.ConnectAsync(host, secret, token),
                message => _dialogs.ShowErrorAsync(message));
            if (connection.Connection is null || connection.Credential is null)
            {
                tab.SetConnectionStatus("RESTORED / DISCONNECTED");
                return;
            }
            session = connection.Connection;
            credential = connection.Credential;
        }
        catch (Exception exception)
        {
            tab.SetConnectionStatus("CONNECTION FAILED");
            StatusMessage = $"SSH / RECONNECT FAILED   ·   {host.HostName}:{host.Port}";
            await _dialogs.ShowErrorAsync($"无法重新连接到 {host.Name}。\n\n{exception.Message}");
            return;
        }

        var secret = credential.Secret;
        try
        {
            await tab.ReplaceSessionAsync(session);
            StatusMessage = $"SSH / RECONNECTED   ·   {host.HostName}:{host.Port}";
            await TryConnectSftpForSshAsync(host, credential.Secret);
            var updatedHost = host with { LastConnectedAt = DateTimeOffset.UtcNow };
            string? stagedCredentialReference = null;
            if (host.AuthenticationMethod == SshAuthenticationMethod.Password && credential.RememberOnThisDevice && !credential.WasSavedCredential)
            {
                stagedCredentialReference = await _credentialStore.StoreAsync(secret);
                updatedHost = updatedHost with { CredentialReference = stagedCredentialReference };
            }
            else if (host.AuthenticationMethod == SshAuthenticationMethod.Password && !credential.WasSavedCredential
                && !string.IsNullOrWhiteSpace(host.CredentialReference))
            {
                await _credentialStore.DeleteAsync(host.CredentialReference);
                updatedHost = updatedHost with { CredentialReference = null };
            }

            await _hostRepository.SaveAsync(updatedHost);
            if (!string.IsNullOrWhiteSpace(stagedCredentialReference)
                && !string.IsNullOrWhiteSpace(host.CredentialReference))
                await _credentialStore.DeleteAsync(host.CredentialReference);
            await LoadAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = $"SSH / RECONNECTED   ·   LOCAL UPDATE FAILED";
            await _dialogs.ShowErrorAsync($"SSH 已重连，但本机连接记录或密码保存失败。\n\n{exception.Message}");
        }
        finally
        {
            secret = string.Empty;
        }
    }

    [RelayCommand]
    private void StartLocalShell(string? shellId)
    {
        var id = shellId is "cmd" or "pwsh" ? shellId : "powershell";
        var workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            var session = _localTerminalConnector.Start(ParseLocalShell(id), workingDirectory);
            var tab = new TerminalTabViewModel(null, session, TerminalSessionKind.Local, id, workingDirectory);
            var placeholder = SelectedTerminalTab is { IsNewTab: true } ? SelectedTerminalTab : null;
            if (placeholder is not null)
            {
                var index = TerminalTabs.IndexOf(placeholder);
                TerminalTabs[index] = tab;
                _ = placeholder.DisposeAsync();
            }
            else
            {
                TerminalTabs.Add(tab);
            }
            SelectedTerminalTab = tab;
            ActiveSection = "终端";
            StatusMessage = $"LOCAL / STARTED  ·  {tab.LocalShellLabel}";
        }
        catch (Exception exception)
        {
            StatusMessage = "LOCAL SHELL / START FAILED";
            _ = _dialogs.ShowErrorAsync($"无法启动本地终端。\n\n{exception.Message}");
        }
    }

    private static LocalShellKind ParseLocalShell(string shellId) => shellId switch
    {
        "cmd" => LocalShellKind.CommandPrompt,
        "pwsh" => LocalShellKind.PowerShell7,
        _ => LocalShellKind.PowerShell
    };

    private async Task SaveTerminalWorkspaceStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var persistedTabs = TerminalTabs.Where(tab => !tab.IsNewTab).ToArray();
            var tabs = persistedTabs.Select(tab => tab.Kind == TerminalSessionKind.Local
                ? new RestorableTerminalTab("local", Shell: tab.LocalShellId, WorkingDirectory: tab.LocalWorkingDirectory)
                : new RestorableTerminalTab("ssh", HostId: tab.Host?.Id)).ToArray();
            var selectedIndex = SelectedTerminalTab is null || SelectedTerminalTab.IsNewTab
                ? -1
                : Array.IndexOf(persistedTabs, SelectedTerminalTab);
            await _terminalWorkspaceStateStore.SaveAsync(new TerminalWorkspaceState(1, tabs, selectedIndex, ActiveSection), cancellationToken);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Could not save terminal workspace state: {exception}");
        }
    }

    public async Task CloseAllTerminalsAsync()
    {
        // Keep the last saved tab list so the next app launch can restore the workspace.
        // The process cleanup is only for shutdown; it must not be persisted as user-closed tabs.
        _restoringTerminalWorkspace = true;
        try
        {
            foreach (var tab in TerminalTabs.ToArray())
            {
                TerminalTabs.Remove(tab);
                await tab.DisposeAsync();
            }
            SelectedTerminalTab = null;
            await CloseSftpAsync();
        }
        finally
        {
            _restoringTerminalWorkspace = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnectSftp))]
    private async Task ConnectSftpAsync()
    {
        var host = SelectedHost;
        if (host is null)
            return;

        if (_sftpSession?.IsConnected == true && _sftpHost?.Id == host.Id)
            return;
        if (_sftpSession is not null)
            await CloseSftpAsync();

        SftpStatus = $"CONNECTING  {host.HostName}:{host.Port}";
        ConnectionCredential credential;
        try
        {
            var connection = await ConnectWithCredentialFallbackAsync(
                host,
                (secret, token) => _sftpConnector.ConnectAsync(host, secret, token),
                message => _sftpDialogs.ShowErrorAsync(message));
            if (connection.Connection is null || connection.Credential is null)
                return;
            _sftpSession = connection.Connection;
            credential = connection.Credential;
            _sftpHost = host;
            OnPropertyChanged(nameof(SftpHost));
            OnPropertyChanged(nameof(IsSftpConnected));
            OnPropertyChanged(nameof(IsSftpDisconnected));
            RetrySftpTransferCommand.NotifyCanExecuteChanged();
            RemotePath = _sftpSession.HomeDirectory;
            await RefreshRemoteDirectoryAsync();
            await RefreshLocalDirectoryAsync();

            if (host.AuthenticationMethod == SshAuthenticationMethod.Password && credential.RememberOnThisDevice && !credential.WasSavedCredential)
            {
                var reference = await _credentialStore.StoreAsync(credential.Secret);
                await _hostRepository.SaveAsync(host with { CredentialReference = reference, LastConnectedAt = DateTimeOffset.UtcNow });
                if (!string.IsNullOrWhiteSpace(host.CredentialReference))
                    await _credentialStore.DeleteAsync(host.CredentialReference);
                await LoadAsync();
            }
            else if (host.AuthenticationMethod == SshAuthenticationMethod.Password && !credential.WasSavedCredential
                && !string.IsNullOrWhiteSpace(host.CredentialReference))
            {
                await _credentialStore.DeleteAsync(host.CredentialReference);
                await _hostRepository.SaveAsync(host with { CredentialReference = null, LastConnectedAt = DateTimeOffset.UtcNow });
                await LoadAsync();
            }
            else if (host.AuthenticationMethod == SshAuthenticationMethod.PrivateKey)
            {
                await _hostRepository.SaveAsync(host with { LastConnectedAt = DateTimeOffset.UtcNow });
                await LoadAsync();
            }
            else if (host.AuthenticationMethod == SshAuthenticationMethod.Password && credential.WasSavedCredential)
            {
                await _hostRepository.SaveAsync(host with { LastConnectedAt = DateTimeOffset.UtcNow });
                await LoadAsync();
            }
            SftpStatus = $"CONNECTED  {host.UserName}@{host.HostName}";
        }
        catch (Exception exception)
        {
            await CloseSftpAsync();
            SftpStatus = $"FAILED  {host.HostName}:{host.Port}";
            await _sftpDialogs.ShowErrorAsync($"无法打开 {host.Name} 的 SFTP 会话。\n\n{exception.Message}");
        }
    }

    private async Task TryConnectSftpForSshAsync(HostProfile host, string secret)
    {
        if (_sftpSession?.IsConnected == true)
        {
            if (_sftpHost?.Id == host.Id)
            {
                SftpStatus = $"CONNECTED  {host.UserName}@{host.HostName}";
                return;
            }

            if (SftpTransfers.Any(item => item.Status is "QUEUED" or "RUNNING"))
            {
                SftpStatus = $"CONNECTED TO {_sftpHost?.Name}  ·  TRANSFER IN PROGRESS";
                return;
            }

            try { await CloseSftpAsync(); }
            catch { /* A stale SFTP transport should not block the new SSH terminal. */ }
        }

        if (_sftpSession is not null)
            await CloseSftpAsync();

        SftpStatus = $"CONNECTING  {host.HostName}:{host.Port}";
        try
        {
            var session = await _sftpConnector.ConnectAsync(host, secret);
            _sftpSession = session;
            _sftpHost = host;
            OnPropertyChanged(nameof(SftpHost));
            OnPropertyChanged(nameof(IsSftpConnected));
            OnPropertyChanged(nameof(IsSftpDisconnected));
            RetrySftpTransferCommand.NotifyCanExecuteChanged();
            RemotePath = session.HomeDirectory;
            var entries = await session.ListDirectoryAsync(RemotePath);
            SetSelectedRemoteFiles([]);
            RemoteFiles.Clear();
            foreach (var entry in entries)
                RemoteFiles.Add(entry);
            SftpStatus = $"CONNECTED  {host.UserName}@{host.HostName}  ·  {entries.Count} ITEMS";
        }
        catch (Exception exception)
        {
            if (_sftpSession is not null && _sftpHost?.Id == host.Id)
            {
                try { await CloseSftpAsync(); }
                catch { /* Keep the SFTP error visible without failing the SSH connection. */ }
            }
            SftpStatus = $"SFTP FAILED  ·  {exception.Message}";
        }
        finally
        {
            ConnectSftpCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private async Task DisconnectSftpAsync() => await CloseSftpAsync();

    [RelayCommand]
    private async Task RefreshRemoteDirectoryAsync()
    {
        if (_sftpSession is null || string.IsNullOrWhiteSpace(RemotePath))
            return;
        try
        {
            SftpStatus = $"READING  {RemotePath}";
            var entries = await _sftpSession.ListDirectoryAsync(RemotePath);
            SetSelectedRemoteFiles([]);
            RemoteFiles.Clear();
            foreach (var entry in entries)
                RemoteFiles.Add(entry);
            SftpStatus = $"{entries.Count} ITEMS  ·  {RemotePath}";
        }
        catch (Exception exception)
        {
            SftpStatus = "DIRECTORY READ FAILED";
            await _sftpDialogs.ShowErrorAsync($"无法读取远程目录。\n\n{exception.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenRemoteDirectory))]
    private async Task OpenRemoteDirectoryAsync()
    {
        if (SelectedRemoteFile is { IsDirectory: true } directory)
        {
            RemotePath = directory.FullPath;
            await RefreshRemoteDirectoryAsync();
        }
    }

    [RelayCommand]
    private async Task NavigateRemoteParentAsync()
    {
        if (_sftpSession is null)
            return;
        var normalized = RemotePath.TrimEnd('/');
        var separator = normalized.LastIndexOf('/');
        RemotePath = separator <= 0 ? "/" : normalized[..separator];
        await RefreshRemoteDirectoryAsync();
    }

    [RelayCommand(CanExecute = nameof(CanUploadSelectedFiles))]
    private async Task UploadFileAsync()
    {
        if (_sftpSession is null)
            return;
        foreach (var entry in _selectedLocalFiles)
        {
            try
            {
                var remotePath = JoinRemotePath(RemotePath, entry.Name);
                var existing = RemoteFiles.FirstOrDefault(item => string.Equals(item.Name, entry.Name, StringComparison.Ordinal));
                if (entry.IsDirectory)
                {
                    if (existing is { IsDirectory: false })
                    {
                        await _sftpDialogs.ShowErrorAsync($"远程同名目标不是目录：{remotePath}");
                        continue;
                    }
                    var tree = await GetLocalTreeAsync(entry.FullPath);
                    var existingFiles = new HashSet<string>(StringComparer.Ordinal);
                    if (existing is { IsDirectory: true })
                        await CollectRemoteFilesAsync(existing, existingFiles, new HashSet<string>(StringComparer.Ordinal));
                    var approved = true;
                    foreach (var file in tree.Where(item => !item.IsDirectory))
                    {
                        var target = JoinRemotePath(remotePath, file.RelativePath.Replace('\\', '/'));
                        if (existingFiles.Contains(target) && !await _sftpDialogs.ConfirmOverwriteAsync(target))
                        {
                            approved = false;
                            break;
                        }
                    }
                    foreach (var directory in tree.Where(item => item.IsDirectory && item.RelativePath.Length > 0))
                    {
                        var target = JoinRemotePath(remotePath, directory.RelativePath.Replace('\\', '/'));
                        if (existingFiles.Contains(target))
                        {
                            await _sftpDialogs.ShowErrorAsync($"远程文件与待上传目录冲突：{target}");
                            approved = false;
                            break;
                        }
                    }
                    if (!approved)
                        continue;
                    var manifest = new DirectoryTransferManifest();
                    var approvedPaths = tree.Select(item => item.RelativePath).ToHashSet(StringComparer.Ordinal);
                    EnqueueSftpTransfer(entry.Name, "UPLOAD TREE", remotePath,
                        async (session, progress, token) =>
                        {
                            var currentTree = (await GetLocalTreeAsync(entry.FullPath)).Where(item => approvedPaths.Contains(item.RelativePath)).ToArray();
                            if (currentTree.Count(item => !item.IsDirectory) != tree.Count(item => !item.IsDirectory))
                                throw new IOException("所选目录中的文件在传输期间被删除或移动；请重新选择目录后重试。");
                            await UploadDirectoryAsync(session, remotePath, currentTree, progress, manifest, token);
                        });
                }
                else
                {
                    if (existing is { IsDirectory: true })
                    {
                        await _sftpDialogs.ShowErrorAsync($"远程同名目标是目录：{remotePath}");
                        continue;
                    }
                    if (existing is not null && !await _sftpDialogs.ConfirmOverwriteAsync(remotePath))
                        continue;
                    EnqueueSftpTransfer(entry.Name, "UPLOAD", remotePath,
                        (session, progress, token) => session.UploadAsync(entry.FullPath, remotePath, progress, token));
                }
            }
            catch (Exception exception)
            {
                await _sftpDialogs.ShowErrorAsync($"无法准备上传“{entry.Name}”。\n\n{exception.Message}");
            }
        }
        StartSftpTransferQueue();
    }

    [RelayCommand(CanExecute = nameof(CanDownloadRemoteFile))]
    private async Task DownloadRemoteFileAsync()
    {
        if (_sftpSession is null)
            return;
        var entries = _selectedRemoteFiles.Count > 0
            ? _selectedRemoteFiles.ToArray()
            : SelectedRemoteFile is { IsDirectory: false } selected ? [selected] : [];
        if (entries.Length == 0)
            return;
        foreach (var entry in entries)
        {
            try
            {
                var localPath = GetSafeLocalTarget(LocalPath, entry.Name);
                if (entry.IsDirectory)
                {
                    if (File.Exists(localPath))
                    {
                        await _sftpDialogs.ShowErrorAsync($"本地同名目标不是目录：{localPath}");
                        continue;
                    }
                    var tree = await GetRemoteTreeAsync(entry);
                    var approved = true;
                    foreach (var file in tree.Where(item => !item.IsDirectory))
                    {
                        var relativePath = GetRemoteRelativePath(entry.FullPath, file.FullPath);
                        var target = GetSafeLocalTarget(localPath, relativePath);
                        if (File.Exists(target) && !await _sftpDialogs.ConfirmLocalOverwriteAsync(target))
                        {
                            approved = false;
                            break;
                        }
                        if (Directory.Exists(target))
                        {
                            await _sftpDialogs.ShowErrorAsync($"本地同名目标是目录：{target}");
                            approved = false;
                            break;
                        }
                    }
                    foreach (var directory in tree.Where(item => item.IsDirectory && item.FullPath != entry.FullPath))
                    {
                        var relativePath = GetRemoteRelativePath(entry.FullPath, directory.FullPath);
                        var target = GetSafeLocalTarget(localPath, relativePath);
                        if (File.Exists(target))
                        {
                            await _sftpDialogs.ShowErrorAsync($"本地文件与待下载目录冲突：{target}");
                            approved = false;
                            break;
                        }
                    }
                    if (!approved)
                        continue;
                    var manifest = new DirectoryTransferManifest();
                    var approvedPaths = tree.Select(item => item.FullPath).ToHashSet(StringComparer.Ordinal);
                    EnqueueSftpTransfer(entry.Name, "DOWNLOAD TREE", entry.FullPath,
                        async (session, progress, token) =>
                        {
                            var currentTree = (await GetRemoteTreeAsync(entry)).Where(item => approvedPaths.Contains(item.FullPath)).ToArray();
                            if (currentTree.Count(item => !item.IsDirectory) != tree.Count(item => !item.IsDirectory))
                                throw new IOException("远程目录中的文件在传输期间被删除或移动；请重新选择目录后重试。");
                            await DownloadDirectoryAsync(session, entry, localPath, currentTree, progress, manifest, token);
                        });
                }
                else
                {
                    if (Directory.Exists(localPath))
                    {
                        await _sftpDialogs.ShowErrorAsync($"本地同名目标是目录：{localPath}");
                        continue;
                    }
                    if (File.Exists(localPath) && !await _sftpDialogs.ConfirmLocalOverwriteAsync(localPath))
                        continue;
                    EnqueueSftpTransfer(entry.Name, "DOWNLOAD", entry.FullPath,
                        (session, progress, token) => session.DownloadAsync(entry.FullPath, localPath, progress, token));
                }
            }
            catch (Exception exception)
            {
                await _sftpDialogs.ShowErrorAsync($"无法准备下载“{entry.Name}”。\n\n{exception.Message}");
            }
        }
        StartSftpTransferQueue();
    }

    [RelayCommand]
    private async Task CreateRemoteDirectoryAsync()
    {
        if (_sftpSession is null)
            return;
        var name = await _sftpDialogs.PromptForNameAsync("SFTP / NEW DIRECTORY", "DIRECTORY_NAME");
        if (name is null)
            return;
        try
        {
            await _sftpSession.CreateDirectoryAsync(JoinRemotePath(RemotePath, name));
            await RefreshRemoteDirectoryAsync();
        }
        catch (Exception exception) { await _sftpDialogs.ShowErrorAsync($"创建目录失败。\n\n{exception.Message}"); }
    }

    [RelayCommand(CanExecute = nameof(CanRenameRemoteEntry))]
    private async Task RenameRemoteEntryAsync()
    {
        var entry = SelectedRemoteFile;
        if (_sftpSession is null || entry is null)
            return;
        var name = await _sftpDialogs.PromptForNameAsync("SFTP / RENAME", "NEW_NAME", entry.Name);
        if (name is null || name == entry.Name)
            return;
        try
        {
            await _sftpSession.RenameAsync(entry.FullPath, JoinRemotePath(RemotePath, name));
            await RefreshRemoteDirectoryAsync();
        }
        catch (Exception exception) { await _sftpDialogs.ShowErrorAsync($"重命名失败。\n\n{exception.Message}"); }
    }

    [RelayCommand(CanExecute = nameof(CanRenameRemoteEntry))]
    private async Task DeleteRemoteEntryAsync()
    {
        var entry = SelectedRemoteFile;
        if (_sftpSession is null || entry is null || !await _sftpDialogs.ConfirmDeleteAsync(entry))
            return;
        try
        {
            await _sftpSession.DeleteAsync(entry);
            await RefreshRemoteDirectoryAsync();
        }
        catch (Exception exception) { await _sftpDialogs.ShowErrorAsync($"删除失败。\n\n{exception.Message}"); }
    }

    private bool CanConnectSftp() => SelectedHost is not null && (_sftpSession is null || !_sftpSession.IsConnected || _sftpHost?.Id != SelectedHost.Id);
    private bool CanOpenRemoteDirectory() => _sftpSession is not null && SelectedRemoteFile?.IsDirectory == true;
    private bool CanDownloadRemoteFile() => _sftpSession is not null
        && (_selectedRemoteFiles.Count > 0 || SelectedRemoteFile is { IsDirectory: false, IsSymbolicLink: false });
    private bool CanUploadSelectedFiles() => _sftpSession is not null && _selectedLocalFiles.Count > 0;
    private bool CanRenameRemoteEntry() => _sftpSession is not null && SelectedRemoteFile is not null;
    private static string JoinRemotePath(string directory, string name) => $"{directory.TrimEnd('/')}/{name}";

    public void SetSelectedRemoteFiles(IEnumerable<RemoteFileEntry> entries)
    {
        _selectedRemoteFiles = entries.Distinct().Where(entry => !entry.IsSymbolicLink).ToArray();
        OnPropertyChanged(nameof(SelectedRemoteFiles));
        DownloadRemoteFileCommand.NotifyCanExecuteChanged();
    }

    public void SetSelectedLocalFiles(IEnumerable<RemoteFileEntry> entries)
    {
        _selectedLocalFiles = entries.Distinct().Where(entry => !entry.IsSymbolicLink).ToArray();
        OnPropertyChanged(nameof(SelectedLocalFiles));
        UploadFileCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task RefreshLocalDirectoryAsync()
    {
        try
        {
            var path = LocalPath;
            var entries = await Task.Run(() =>
            {
                var directory = new DirectoryInfo(path);
                if (!directory.Exists)
                    throw new DirectoryNotFoundException($"本地目录不存在：{path}");

                return directory.EnumerateFileSystemInfos()
                    .Select(info => new RemoteFileEntry(
                        info.Name,
                        info.FullName,
                        (info.Attributes & FileAttributes.Directory) != 0,
                        info is FileInfo file ? file.Length : 0,
                        info.LastWriteTime,
                        (info.Attributes & FileAttributes.ReparsePoint) != 0))
                    .OrderByDescending(entry => entry.IsDirectory)
                    .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            });

            if (!string.Equals(path, LocalPath, StringComparison.OrdinalIgnoreCase))
                return;

            var drive = Path.GetPathRoot(path);
            if (drive is not null && LocalDrives.Contains(drive, StringComparer.OrdinalIgnoreCase))
                SelectedLocalDrive = drive;

            SetSelectedLocalFiles([]);
            LocalFiles.Clear();
            foreach (var entry in entries)
                LocalFiles.Add(entry);
        }
        catch (Exception exception)
        {
            SetSelectedLocalFiles([]);
            LocalFiles.Clear();
            _ = _sftpDialogs.ShowErrorAsync($"无法读取本地目录。\n\n{exception.Message}");
        }
    }

    [RelayCommand]
    private Task NavigateLocalParentAsync()
    {
        try
        {
            var current = Path.GetFullPath(LocalPath);
            var root = Path.GetPathRoot(current);
            LocalPath = Directory.GetParent(current)?.FullName ?? root ?? current;
        }
        catch (Exception exception)
        {
            _ = _sftpDialogs.ShowErrorAsync($"本地路径无效。\n\n{exception.Message}");
        }
        return RefreshLocalDirectoryAsync();
    }

    [RelayCommand]
    private Task OpenLocalDirectoryAsync()
    {
        if (SelectedLocalEntry is { IsDirectory: true } directory)
        {
            LocalPath = directory.FullPath;
            return RefreshLocalDirectoryAsync();
        }
        return Task.CompletedTask;
    }

    public void SwitchLocalDrive(string? drive)
    {
        if (string.IsNullOrWhiteSpace(drive))
            return;
        if (string.Equals(Path.GetPathRoot(LocalPath), drive, StringComparison.OrdinalIgnoreCase))
            return;
        LocalPath = drive;
        _ = RefreshLocalDirectoryAsync();
    }

    private static Task<IReadOnlyList<LocalTreeEntry>> GetLocalTreeAsync(string root) => Task.Run<IReadOnlyList<LocalTreeEntry>>(() =>
    {
        var result = new List<LocalTreeEntry> { new(root, string.Empty, true, 0, Directory.GetLastWriteTimeUtc(root)) };
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            foreach (var info in new DirectoryInfo(current).EnumerateFileSystemInfos())
            {
                var attributes = info.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var relative = Path.GetRelativePath(root, info.FullName);
                result.Add(new LocalTreeEntry(info.FullName, relative, isDirectory, info is FileInfo file ? file.Length : 0, info.LastWriteTimeUtc));
                if (isDirectory)
                    pending.Push(info.FullName);
            }
        }
        return result;
    });

    private async Task CollectRemoteFilesAsync(RemoteFileEntry directory, ISet<string> files, ISet<string> visited)
    {
        if (!visited.Add(directory.FullPath))
            return;
        foreach (var entry in await _sftpSession!.ListDirectoryAsync(directory.FullPath))
        {
            if (entry.IsSymbolicLink)
                continue;
            if (entry.IsDirectory)
                await CollectRemoteFilesAsync(entry, files, visited);
            else
                files.Add(entry.FullPath);
        }
    }

    private async Task<IReadOnlyList<RemoteFileEntry>> GetRemoteTreeAsync(RemoteFileEntry root)
    {
        var result = new List<RemoteFileEntry> { root };
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<RemoteFileEntry>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            if (!visited.Add(directory.FullPath))
                continue;
            foreach (var entry in await _sftpSession!.ListDirectoryAsync(directory.FullPath))
            {
                if (entry.IsSymbolicLink)
                    continue;
                result.Add(entry);
                if (entry.IsDirectory)
                    pending.Push(entry);
            }
        }
        return result;
    }

    private static string GetRemoteRelativePath(string root, string path)
    {
        var prefix = root.TrimEnd('/') + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            throw new IOException($"远程路径不在所选目录内：{path}");
        var relative = path[prefix.Length..];
        if (relative.Split('/').Any(segment => segment is "" or "." or ".." || segment.Contains('\\')))
            throw new IOException($"远程路径包含不安全的目录项：{path}");
        return relative;
    }

    private static string GetSafeLocalTarget(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split(['/', '\\']).Any(segment => segment is ".." or "."))
            throw new IOException($"远程文件名不是安全的相对路径：{relative}");
        var rootPath = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(rootPath, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = Path.TrimEndingDirectorySeparator(rootPath) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"下载目标超出本地目录：{target}");
        return target;
    }

    private static async Task UploadDirectoryAsync(
        ISftpSession session,
        string remoteRoot,
        IReadOnlyList<LocalTreeEntry> tree,
        IProgress<double> progress,
        DirectoryTransferManifest manifest,
        CancellationToken cancellationToken)
    {
        await session.EnsureDirectoryAsync(remoteRoot, cancellationToken);
        foreach (var directory in tree.Where(entry => entry.IsDirectory && entry.RelativePath.Length > 0).OrderBy(entry => entry.RelativePath.Length))
            await session.EnsureDirectoryAsync(JoinRemotePath(remoteRoot, directory.RelativePath.Replace('\\', '/')), cancellationToken);

        var files = tree.Where(entry => !entry.IsDirectory).ToArray();
        var totalBytes = files.Sum(file => file.Size);
        long completedBytes = files.Where(file => manifest.IsCompleted(file.RelativePath, $"{file.Size}:{file.LastWriteTimeUtc.Ticks}")).Sum(file => file.Size);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fingerprint = $"{file.Size}:{file.LastWriteTimeUtc.Ticks}";
            if (manifest.IsCompleted(file.RelativePath, fingerprint))
                continue;
            var sentBeforeFile = completedBytes;
            var target = JoinRemotePath(remoteRoot, file.RelativePath.Replace('\\', '/'));
            var itemProgress = new Progress<double>(value => progress.Report(totalBytes == 0 ? 100 : Math.Clamp((sentBeforeFile + file.Size * value / 100d) * 100d / totalBytes, 0, 100)));
            await session.UploadAsync(file.FullPath, target, itemProgress, cancellationToken);
            manifest.MarkCompleted(file.RelativePath, fingerprint);
            completedBytes += file.Size;
        }
        progress.Report(100);
    }

    private static async Task DownloadDirectoryAsync(
        ISftpSession session,
        RemoteFileEntry remoteRoot,
        string localRoot,
        IReadOnlyList<RemoteFileEntry> tree,
        IProgress<double> progress,
        DirectoryTransferManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(localRoot);
        foreach (var directory in tree.Where(entry => entry.IsDirectory && entry.FullPath != remoteRoot.FullPath))
        {
            var relative = GetRemoteRelativePath(remoteRoot.FullPath, directory.FullPath);
            Directory.CreateDirectory(GetSafeLocalTarget(localRoot, relative));
        }

        var files = tree.Where(entry => !entry.IsDirectory).ToArray();
        var totalBytes = files.Sum(file => file.Size);
        long completedBytes = files.Where(file => manifest.IsCompleted(GetRemoteRelativePath(remoteRoot.FullPath, file.FullPath), $"{file.Size}:{file.LastWriteTime.Ticks}")).Sum(file => file.Size);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = GetRemoteRelativePath(remoteRoot.FullPath, file.FullPath);
            var fingerprint = $"{file.Size}:{file.LastWriteTime.Ticks}";
            if (manifest.IsCompleted(relative, fingerprint))
                continue;
            var target = GetSafeLocalTarget(localRoot, relative);
            var sentBeforeFile = completedBytes;
            var itemProgress = new Progress<double>(value => progress.Report(totalBytes == 0 ? 100 : Math.Clamp((sentBeforeFile + file.Size * value / 100d) * 100d / totalBytes, 0, 100)));
            await session.DownloadAsync(file.FullPath, target, itemProgress, cancellationToken);
            manifest.MarkCompleted(relative, fingerprint);
            completedBytes += file.Size;
        }
        progress.Report(100);
    }

    private async Task CloseSftpAsync()
    {
        if (_sftpSession is null)
            return;

        foreach (var item in SftpTransfers.Where(item => item.Status == "QUEUED"))
            item.Status = "CANCELLED";
        CancelActiveTransferSafely();
        if (_transferQueueTask is not null)
        {
            try { await _transferQueueTask; }
            catch { /* Transfer errors are represented on their queue items. */ }
        }

        await _sftpSession.DisposeAsync();
        _sftpSession = null;
        _sftpHost = null;
        OnPropertyChanged(nameof(SftpHost));
        RemoteFiles.Clear();
        SelectedRemoteFile = null;
        RemotePath = string.Empty;
        SftpStatus = "DISCONNECTED";
        OnPropertyChanged(nameof(IsSftpConnected));
        OnPropertyChanged(nameof(IsSftpDisconnected));
        RetrySftpTransferCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void CancelSftpTransfer(SftpTransferItemViewModel? item)
    {
        if (item is null || item.Status is "COMPLETE" or "FAILED" or "CANCELLED")
            return;

        if (item.Status == "RUNNING")
            CancelActiveTransferSafely();
        else
            item.Status = "CANCELLED";
        OnPropertyChanged(nameof(PendingTransferCount));
    }

    private bool CanRetrySftpTransfer(SftpTransferItemViewModel? item) =>
        item?.Status == "FAILED"
        && _sftpSession?.IsConnected == true
        && _sftpHost is not null
        && _sftpRetryActions.TryGetValue(item, out var transfer)
        && transfer.HostId == _sftpHost.Id;

    [RelayCommand(CanExecute = nameof(CanRetrySftpTransfer))]
    private void RetrySftpTransfer(SftpTransferItemViewModel? item)
    {
        if (item is null || !CanRetrySftpTransfer(item) || !_sftpRetryActions.TryGetValue(item, out var transfer))
            return;

        item.Progress = 0;
        item.ErrorMessage = null;
        item.Status = "QUEUED";
        _sftpTransferQueue.Enqueue(transfer);
        OnPropertyChanged(nameof(PendingTransferCount));
        StartSftpTransferQueue();
    }

    private void CancelActiveTransferSafely()
    {
        try
        {
            // SSH.NET cancellation callbacks run synchronously from Cancel(). A callback failure
            // must not escape the button command onto WPF's dispatcher and terminate the app.
            _activeTransferCancellation?.Cancel();
        }
        catch (Exception exception)
        {
            if (_activeTransferCancellation?.IsCancellationRequested == true)
                return;

            var activeItem = SftpTransfers.FirstOrDefault(item => item.Status == "RUNNING");
            if (activeItem is not null)
                activeItem.ErrorMessage = $"取消请求失败：{exception.Message}";
        }
    }

    private void EnqueueSftpTransfer(
        string name,
        string direction,
        string remotePath,
        Func<ISftpSession, IProgress<double>, CancellationToken, Task> execute)
    {
        var item = new SftpTransferItemViewModel(name, direction, remotePath);
        SftpTransfers.Insert(0, item);
        var queued = new QueuedSftpTransfer(item, _sftpHost?.Id ?? Guid.Empty, execute);
        _sftpRetryActions[item] = queued;
        _sftpTransferQueue.Enqueue(queued);
        RetrySftpTransferCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PendingTransferCount));
    }

    private void StartSftpTransferQueue()
    {
        if (_transferQueueTask is null)
            _transferQueueTask = ProcessSftpTransferQueueAsync();
    }

    private async Task ProcessSftpTransferQueueAsync()
    {
        await Task.Yield();
        try
        {
            while (_sftpTransferQueue.TryDequeue(out var transfer))
            {
                var item = transfer.Item;
                if (item.Status == "CANCELLED")
                    continue;

                if (_sftpSession is null)
                {
                    item.Status = "FAILED";
                    item.ErrorMessage = "SFTP session has been disconnected.";
                    continue;
                }

                using var cancellation = new CancellationTokenSource();
                _activeTransferCancellation = cancellation;
                OnPropertyChanged(nameof(IsSftpTransferRunning));
                item.Status = "RUNNING";
                SftpStatus = $"{transfer.Item.Direction}  {item.Name}";
                var progress = new Progress<double>(value =>
                {
                    // Progress<T> may dispatch a queued UI update after the transfer has finished.
                    // Ignore stale reports so a completed/cancelled row cannot move backwards.
                    if (item.Status == "RUNNING")
                        item.Progress = value;
                });
                try
                {
                    await transfer.Execute(_sftpSession, progress, cancellation.Token);
                    item.Progress = 100;
                    item.Status = "COMPLETE";
                    await RefreshRemoteDirectoryAsync();
                }
                catch (OperationCanceledException)
                {
                    item.Status = "CANCELLED";
                }
                catch (Exception exception)
                {
                    item.Status = "FAILED";
                    item.ErrorMessage = exception.Message;
                }
                finally
                {
                    _activeTransferCancellation = null;
                    OnPropertyChanged(nameof(IsSftpTransferRunning));
                    OnPropertyChanged(nameof(PendingTransferCount));
                    RetrySftpTransferCommand.NotifyCanExecuteChanged();
                }
            }
        }
        finally
        {
            _transferQueueTask = null;
            if (_sftpSession is not null && _sftpTransferQueue.Count == 0)
                SftpStatus = $"TRANSFER QUEUE IDLE  ·  {RemotePath}";
            OnPropertyChanged(nameof(PendingTransferCount));
            if (_sftpTransferQueue.Count > 0 && _sftpSession is not null)
                StartSftpTransferQueue();
        }
    }

    [RelayCommand]
    private async Task ConnectGitHubAsync()
    {
        if (IsSyncBusy)
            return;
        IsSyncBusy = true;
        SyncStatus = "OPENING DEVICE AUTHORIZATION…";
        try
        {
            await _gitHubSync.ConnectAsync();
            SyncStatus = _gitHubSync.AuthenticationLabel;
            StatusMessage = "GITHUB / AUTHORIZED";
        }
        catch (OperationCanceledException)
        {
            SyncStatus = "AUTHORIZATION CANCELLED";
        }
        catch (Exception exception)
        {
            SyncStatus = "GITHUB / ERROR";
            await _syncInteraction.ShowErrorAsync("GitHub 授权失败", exception.Message);
        }
        finally { IsSyncBusy = false; }
    }

    [RelayCommand]
    private async Task PushSyncConfigurationAsync()
    {
        if (IsSyncBusy)
            return;
        IsSyncBusy = true;
        SyncStatus = "PREPARING ENCRYPTION…";
        try
        {
            var passphrase = await _gitHubSync.GetPassphraseForPushAsync();
            if (passphrase is null)
            {
                SyncStatus = "PUSH CANCELLED";
                return;
            }

            SyncStatus = "ENCRYPTING / UPLOADING…";
            await _gitHubSync.PushAsync(SyncOwner, SyncRepository, SyncBranch, passphrase);
            SyncStatus = "SYNC / PUSH COMPLETE";
            StatusMessage = "GITHUB / CONFIGURATION PUSHED";
        }
        catch (Exception exception)
        {
            SyncStatus = "PUSH / ERROR";
            await _syncInteraction.ShowErrorAsync("上传同步配置失败", exception.Message);
        }
        finally { IsSyncBusy = false; }
    }

    [RelayCommand]
    private async Task PullSyncConfigurationAsync()
    {
        if (IsSyncBusy)
            return;
        var passphrase = await _syncInteraction.RequestPassphraseAsync(confirm: false);
        if (passphrase is null)
            return;
        IsSyncBusy = true;
        SyncStatus = "DOWNLOADING / DECRYPTING…";
        try
        {
            var merged = await _gitHubSync.PullAsync(SyncOwner, SyncRepository, SyncBranch, passphrase);
            if (!await _syncInteraction.ConfirmImportAsync(merged.Count, Hosts.Count))
            {
                SyncStatus = "PULL CANCELLED";
                return;
            }

            await _hostRepository.ReplaceAllAsync(merged);
            await LoadAsync();
            SyncStatus = "SYNC / PULL COMPLETE";
            StatusMessage = "GITHUB / CONFIGURATION PULLED";
        }
        catch (Exception exception)
        {
            SyncStatus = "PULL / ERROR";
            await _syncInteraction.ShowErrorAsync("下载同步配置失败", exception.Message);
        }
        finally { IsSyncBusy = false; }
    }

    [RelayCommand]
    private void SelectSection(string section)
    {
        ActiveSection = section;
        if (string.Equals(section, "终端", StringComparison.Ordinal) && TerminalTabs.Count == 0)
            AddTerminalTab();
        if (section == "脚本")
        {
            StatusMessage = "SCRIPT CENTER / READY";
            return;
        }
        if (!string.Equals(section, "主机", StringComparison.Ordinal))
        {
            StatusMessage = $"{section.ToUpperInvariant()} / NEXT MILESTONE";
            return;
        }

        StatusMessage = $"LOCAL / READY   ·   {HostCount} HOSTS";
    }

    [RelayCommand]
    private void NewScript()
    {
        var runtime = AvailableScriptRuntimeOptions.Contains(SelectedScriptRuntime, StringComparer.Ordinal)
            ? ParseRuntime(SelectedScriptRuntime)
            : SelectedScriptExecutionTarget == "本机" ? ScriptRuntimeKind.PowerShell : ScriptRuntimeKind.Bash;
        SelectedScriptRuntime = RuntimeName(runtime);
        var content = runtime switch
        {
            ScriptRuntimeKind.PowerShell => "Write-Output 'hello from OnlyShell'\nGet-Location\n",
            ScriptRuntimeKind.Cmd => "@echo off\necho hello from OnlyShell\ncd\n",
            ScriptRuntimeKind.Python3 => "import os\nprint('hello from OnlyShell')\nprint(os.getcwd())\n",
            _ => "#!/usr/bin/env bash\nset -euo pipefail\n\necho 'hello from OnlyShell'\n"
        };
        var script = new ScriptDefinition(
            Guid.NewGuid(),
            "新脚本",
            runtime,
            string.Empty,
            content,
            DateTimeOffset.UtcNow,
            ExecutionTarget: SelectedScriptExecutionTarget == "本机" ? "Local" : "Remote");
        Scripts.Insert(0, script);
        SelectedScript = script;
        RebuildScriptCatalogTree();
        ScriptCenterStatus = "NEW SCRIPT / UNSAVED";
    }

    [RelayCommand]
    private void AddScriptArgumentTemplateField() => ScriptTemplateFields.Add(new ScriptTemplateFieldViewModel("ARG"));

    [RelayCommand]
    private void AddScriptEnvironmentTemplateField() => ScriptTemplateFields.Add(new ScriptTemplateFieldViewModel("ENV"));

    [RelayCommand]
    private void RemoveScriptTemplateField(ScriptTemplateFieldViewModel? field)
    {
        if (field is not null)
            ScriptTemplateFields.Remove(field);
    }

    [RelayCommand]
    private async Task SaveScriptAsync()
    {
        await _selectedScriptDefaultsLoading;
        if (string.IsNullOrWhiteSpace(ScriptName))
        {
            ScriptCenterStatus = "SAVE FAILED / SCRIPT NAME REQUIRED";
            return;
        }
        if (string.IsNullOrWhiteSpace(ScriptContent))
        {
            ScriptCenterStatus = "SAVE FAILED / SCRIPT CONTENT REQUIRED";
            return;
        }

        var inputFields = SerializeScriptInputFields(ScriptTemplateFields);
        var inputTemplateError = ValidateScriptInputFields(inputFields);
        if (inputTemplateError is not null)
        {
            ScriptCenterStatus = "SAVE FAILED / INPUT TEMPLATE";
            await _syncInteraction.ShowErrorAsync("参数模板无效", inputTemplateError);
            return;
        }

        var script = new ScriptDefinition(
            SelectedScript?.Id ?? Guid.NewGuid(),
            ScriptName.Trim(),
            ParseRuntime(SelectedScriptRuntime),
            ScriptDescription.Trim(),
            ScriptContent,
            DateTimeOffset.UtcNow,
            NormalizeScriptFolderPath(ScriptFolderPath),
            SelectedScriptExecutionTarget == "本机" ? "Local" : "Remote",
            SerializeScriptInputTemplate(inputFields),
            inputFields);
        try
        {
            await _scriptCenterStore.SaveScriptAsync(script);
            var defaultSaveError = (Exception?)null;
            try
            {
                await _scriptExecutionInputsStore.SaveDefaultsAsync(script.Id, CreateDefaultInputs(ScriptTemplateFields));
            }
            catch (Exception exception)
            {
                defaultSaveError = exception;
            }
            var index = Scripts.IndexOf(SelectedScript!);
            if (index >= 0)
                Scripts[index] = script;
            else
                Scripts.Insert(0, script);
            SelectedScript = script;
            RebuildScriptCatalogTree();
            ScriptCenterStatus = defaultSaveError is null
                ? $"SAVED / {script.Name}"
                : $"SCRIPT SAVED / DEFAULT VALUES FAILED · {script.Name}";
            if (defaultSaveError is not null)
                await _syncInteraction.ShowErrorAsync("脚本已保存，但加密默认值保存失败", defaultSaveError.Message);
        }
        catch (Exception exception)
        {
            ScriptCenterStatus = "SAVE FAILED";
            await _syncInteraction.ShowErrorAsync("保存脚本失败", exception.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteScriptAsync()
    {
        if (SelectedScript is not { } script)
            return;
        if (!await _scriptCenterInteraction.ConfirmDeleteAsync(script))
            return;
        try
        {
            await _scriptCenterStore.DeleteScriptAsync(script.Id);
            var inputCleanupFailed = false;
            try
            {
                await _scriptExecutionInputsStore.DeleteAsync(script.Id);
            }
            catch (Exception cleanupException)
            {
                inputCleanupFailed = true;
                ScriptCenterStatus = $"DELETED / INPUT CLEANUP FAILED · {script.Name}";
                await _syncInteraction.ShowErrorAsync("脚本已删除，但未能清除本机保存的加密输入", cleanupException.Message);
            }
            Scripts.Remove(script);
            RebuildScriptCatalogTree();
            SelectedScript = Scripts.FirstOrDefault();
            if (!inputCleanupFailed)
                ScriptCenterStatus = $"DELETED / {script.Name}";
        }
        catch (Exception exception)
        {
            ScriptCenterStatus = "DELETE FAILED";
            await _syncInteraction.ShowErrorAsync("删除脚本失败", exception.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteScriptRunAsync(ScriptRunLog? run)
    {
        if (run is null || !RecentScriptRuns.Contains(run))
            return;

        try
        {
            await _scriptCenterStore.DeleteRunAsync(run.Id);
            var index = RecentScriptRuns.IndexOf(run);
            RecentScriptRuns.RemoveAt(index);
            if (ReferenceEquals(SelectedScriptRun, run))
                SelectedScriptRun = RecentScriptRuns.Count > 0
                    ? RecentScriptRuns[Math.Min(index, RecentScriptRuns.Count - 1)]
                    : null;
            ScriptCenterStatus = $"RUN DELETED / {run.ScriptName} / {run.StartedAt.LocalDateTime:HH:mm:ss}";
        }
        catch (Exception exception)
        {
            ScriptCenterStatus = "DELETE RUN FAILED";
            await _syncInteraction.ShowErrorAsync("删除运行记录失败", exception.Message);
        }
    }

    [RelayCommand]
    private async Task RunScriptAsync()
    {
        if (IsScriptRunning)
            return;
        var isLocal = SelectedScriptExecutionTarget == "本机";
        if (!AvailableScriptRuntimeOptions.Contains(SelectedScriptRuntime, StringComparer.Ordinal))
        {
            ScriptCenterStatus = $"RUN BLOCKED / {SelectedScriptRuntime} CANNOT RUN ON {SelectedScriptExecutionTarget}";
            return;
        }
        var host = isLocal ? null : SelectedHost;
        if (!isLocal && host is null)
        {
            ScriptCenterStatus = "RUN BLOCKED / SELECT A HOST IN THE LEFT PANE";
            return;
        }
        if (isLocal && !_localExecutionConfirmed)
        {
            if (!await _scriptCenterInteraction.ConfirmLocalExecutionAsync())
            {
                ScriptCenterStatus = "RUN CANCELLED / LOCAL EXECUTION NOT CONFIRMED";
                return;
            }
            _localExecutionConfirmed = true;
        }
        if (string.IsNullOrWhiteSpace(ScriptName) || string.IsNullOrWhiteSpace(ScriptContent))
        {
            ScriptCenterStatus = "RUN BLOCKED / SAVE A SCRIPT FIRST";
            return;
        }

        await SaveScriptAsync();
        if (SelectedScript is not { } script
            || ScriptCenterStatus.StartsWith("SAVE FAILED", StringComparison.Ordinal)
            || ScriptCenterStatus.StartsWith("SCRIPT SAVED / DEFAULT VALUES FAILED", StringComparison.Ordinal))
            return;

        ScriptExecutionInputs? inputs;
        try
        {
            var savedInputs = await _scriptExecutionInputsStore.LoadAsync(script.Id);
            var defaults = await _scriptExecutionInputsStore.LoadDefaultsAsync(script.Id);
            var fields = script.InputFields is { Count: > 0 } ? script.InputFields : ParseLegacyInputTemplate(script.InputTemplate);
            var initialInputs = MergeScriptInputs(defaults, savedInputs);
            inputs = await _scriptCenterInteraction.PromptForExecutionInputsAsync(initialInputs, fields);
            if (inputs is not null)
                await _scriptExecutionInputsStore.SaveAsync(script.Id, inputs);
        }
        catch (Exception exception)
        {
            ScriptCenterStatus = "INPUTS FAILED / LOCAL ENCRYPTED STORAGE";
            await _syncInteraction.ShowErrorAsync("读取或保存脚本参数失败", exception.Message);
            return;
        }
        if (inputs is null)
        {
            ScriptCenterStatus = "RUN CANCELLED / INPUT DIALOG CLOSED";
            return;
        }

        IsScriptRunning = true;
        _scriptRunCancellation = new CancellationTokenSource();
        var cancellationToken = _scriptRunCancellation.Token;
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var targetName = isLocal ? "本机" : host!.Name;
        var targetType = isLocal ? "Local" : "Remote";
        var runtime = ParseRuntime(SelectedScriptRuntime);
        var runId = Guid.NewGuid();
        var liveRun = new ScriptRunLog(runId, script.Id, script.Name, host?.Id, targetName, startedAt,
            0, null, string.Empty, string.Empty, false, targetType);
        var outputBuffer = new StringBuilder();
        var errorBuffer = new StringBuilder();
        var outputLock = new object();
        SelectedScriptRun = liveRun;

        void OnLocalOutput(string chunk, bool isError)
        {
            string outputSnapshot;
            string errorSnapshot;
            lock (outputLock)
            {
                (isError ? errorBuffer : outputBuffer).Append(chunk);
                outputSnapshot = outputBuffer.ToString();
                errorSnapshot = errorBuffer.ToString();
            }

            _ = Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (SelectedScriptRun is { } current && current.Id == runId)
                    SelectedScriptRun = current with { Output = outputSnapshot, Error = errorSnapshot };
            });
        }

        (string Output, string Error) GetCapturedOutput()
        {
            lock (outputLock)
                return (outputBuffer.ToString(), errorBuffer.ToString());
        }

        ScriptCenterStatus = $"RUNNING / {targetName}";
        string secret = string.Empty;
        try
        {
            RemoteScriptRunResult result;
            if (isLocal)
            {
                var workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                result = await _localScriptRunner.RunAsync(runtime, ScriptContent, workingDirectory, inputs, cancellationToken, OnLocalOutput);
            }
            else
            {
                var remoteHost = host!;
                var connection = await ConnectWithCredentialFallbackAsync(
                    remoteHost,
                    (value, token) => _remoteScriptRunner.RunAsync(remoteHost, value, runtime, ScriptContent, inputs, token),
                    message => _syncInteraction.ShowErrorAsync("读取本机保存的密码失败，将改为手动输入", message),
                    cancellationToken);
                if (connection.Connection is null || connection.Credential is null)
                {
                    ScriptCenterStatus = "RUN CANCELLED / CREDENTIAL PROMPT CLOSED";
                    return;
                }

                result = connection.Connection;
                var credential = connection.Credential;
                secret = credential.Secret;
                if (remoteHost.AuthenticationMethod == SshAuthenticationMethod.Password && credential.RememberOnThisDevice && !credential.WasSavedCredential)
                {
                    var previousReference = remoteHost.CredentialReference;
                    var stagedCredentialReference = await _credentialStore.StoreAsync(secret, cancellationToken);
                    try
                    {
                        await _hostRepository.SaveAsync(remoteHost with { CredentialReference = stagedCredentialReference }, cancellationToken);
                    }
                    catch
                    {
                        try { await _credentialStore.DeleteAsync(stagedCredentialReference, CancellationToken.None); }
                        catch { /* Preserve the host persistence error. */ }
                        throw;
                    }
                    if (!string.IsNullOrWhiteSpace(previousReference) && previousReference != stagedCredentialReference)
                    {
                        try { await _credentialStore.DeleteAsync(previousReference, cancellationToken); }
                        catch { /* The new host credential reference is already committed. */ }
                    }
                    await LoadAsync(cancellationToken);
                    SelectedHost = Hosts.FirstOrDefault(item => item.Id == remoteHost.Id);
                }
            }
            stopwatch.Stop();
            var log = new ScriptRunLog(
                runId, script.Id, script.Name, host?.Id, targetName, startedAt,
                stopwatch.ElapsedMilliseconds, result.ExitCode, result.Output, result.Error, result.ExitCode == 0, targetType);
            await _scriptCenterStore.AppendRunAsync(log);
            RecentScriptRuns.Insert(0, log);
            while (RecentScriptRuns.Count > 100)
                RecentScriptRuns.RemoveAt(RecentScriptRuns.Count - 1);
            SelectedScriptRun = log;
            ScriptCenterStatus = result.ExitCode == 0
                ? $"SUCCESS / {targetName} / {stopwatch.Elapsed.TotalSeconds:F1}s"
                : $"FAILED / EXIT {result.ExitCode} / {targetName}";
            StatusMessage = $"SCRIPT / {(result.ExitCode == 0 ? "SUCCESS" : "FAILED")}  ·  {script.Name}  ·  {targetName}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            var captured = GetCapturedOutput();
            var log = new ScriptRunLog(Guid.NewGuid(), script.Id, script.Name, host?.Id, targetName,
                startedAt, stopwatch.ElapsedMilliseconds, null, captured.Output,
                string.IsNullOrEmpty(captured.Error) ? "Execution cancelled by user." : $"{captured.Error}\nExecution cancelled by user.", false, targetType);
            try
            {
                await _scriptCenterStore.AppendRunAsync(log);
                RecentScriptRuns.Insert(0, log);
                SelectedScriptRun = log;
            }
            catch { /* Keep the cancellation state visible if local history storage is unavailable. */ }
            ScriptCenterStatus = $"CANCELLED / {targetName}";
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            var captured = GetCapturedOutput();
            var log = new ScriptRunLog(Guid.NewGuid(), script.Id, script.Name, host?.Id, targetName,
                startedAt, stopwatch.ElapsedMilliseconds, null, captured.Output,
                string.IsNullOrEmpty(captured.Error) ? exception.Message : $"{captured.Error}\n{exception.Message}", false, targetType);
            try
            {
                await _scriptCenterStore.AppendRunAsync(log);
                RecentScriptRuns.Insert(0, log);
                SelectedScriptRun = log;
            }
            catch { /* Keep the execution error visible even if local log persistence fails. */ }
            ScriptCenterStatus = $"FAILED / {targetName}";
            await _syncInteraction.ShowErrorAsync("脚本执行失败", exception.Message);
        }
        finally
        {
            secret = string.Empty;
            _scriptRunCancellation?.Dispose();
            _scriptRunCancellation = null;
            IsScriptRunning = false;
        }
    }

    [RelayCommand]
    private async Task RunScriptOnMultipleHostsAsync()
    {
        if (IsScriptRunning)
            return;
        if (SelectedScriptExecutionTarget == "本机")
        {
            ScriptCenterStatus = "BATCH RUN BLOCKED / REMOTE HOSTS ONLY";
            return;
        }
        if (!AvailableScriptRuntimeOptions.Contains(SelectedScriptRuntime, StringComparer.Ordinal))
        {
            ScriptCenterStatus = $"RUN BLOCKED / {SelectedScriptRuntime} CANNOT RUN ON REMOTE HOSTS";
            return;
        }
        if (string.IsNullOrWhiteSpace(ScriptName) || string.IsNullOrWhiteSpace(ScriptContent))
        {
            ScriptCenterStatus = "RUN BLOCKED / SAVE A SCRIPT FIRST";
            return;
        }

        var selectedHosts = await _scriptCenterInteraction.SelectHostsForBatchAsync(Hosts.ToArray(), SelectedHost?.Id);
        if (selectedHosts is null)
        {
            ScriptCenterStatus = "BATCH RUN CANCELLED / HOST SELECTION CLOSED";
            return;
        }

        await SaveScriptAsync();
        if (SelectedScript is not { } script
            || ScriptCenterStatus.StartsWith("SAVE FAILED", StringComparison.Ordinal)
            || ScriptCenterStatus.StartsWith("SCRIPT SAVED / DEFAULT VALUES FAILED", StringComparison.Ordinal))
            return;

        ScriptExecutionInputs? inputs;
        try
        {
            var savedInputs = await _scriptExecutionInputsStore.LoadAsync(script.Id);
            var defaults = await _scriptExecutionInputsStore.LoadDefaultsAsync(script.Id);
            var fields = script.InputFields is { Count: > 0 } ? script.InputFields : ParseLegacyInputTemplate(script.InputTemplate);
            inputs = await _scriptCenterInteraction.PromptForExecutionInputsAsync(MergeScriptInputs(defaults, savedInputs), fields);
            if (inputs is not null)
                await _scriptExecutionInputsStore.SaveAsync(script.Id, inputs);
        }
        catch (Exception exception)
        {
            ScriptCenterStatus = "INPUTS FAILED / LOCAL ENCRYPTED STORAGE";
            await _syncInteraction.ShowErrorAsync("读取或保存脚本参数失败", exception.Message);
            return;
        }
        if (inputs is null)
        {
            ScriptCenterStatus = "BATCH RUN CANCELLED / INPUT DIALOG CLOSED";
            return;
        }

        IsScriptRunning = true;
        _scriptRunCancellation = new CancellationTokenSource();
        var cancellationToken = _scriptRunCancellation.Token;
        var runtime = ParseRuntime(SelectedScriptRuntime);
        var scriptContent = script.Content;
        var scriptName = script.Name;
        var completedCount = 0;
        var startedCount = 0;
        var credentialUpdated = 0;
        var results = new ConcurrentDictionary<int, ScriptRunLog>();
        var combinedOutput = new StringBuilder();
        try
        {
            try
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, selectedHosts.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Min(3, selectedHosts.Count), CancellationToken = cancellationToken },
                    async (index, token) =>
                {
                    var target = selectedHosts[index];
                    var started = Interlocked.Increment(ref startedCount);
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                        ScriptCenterStatus = $"BATCH RUNNING / {started} STARTED / {selectedHosts.Count} HOSTS");
                    var startedAt = DateTimeOffset.UtcNow;
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var output = string.Empty;
                    var error = string.Empty;
                    int? exitCode = null;
                    try
                    {
                        var connection = await ConnectWithCredentialFallbackAsync(
                            target,
                            (secret, ct) => _remoteScriptRunner.RunAsync(target, secret, runtime, scriptContent, inputs, ct),
                            message => Application.Current.Dispatcher.InvokeAsync(() => _syncInteraction.ShowErrorAsync("读取本机保存的密码失败，将改为手动输入", message)).Task.Unwrap(),
                            token,
                            _batchCredentialPromptLock);
                        if (connection.Connection is null || connection.Credential is null)
                        {
                            _scriptRunCancellation?.Cancel();
                            throw new OperationCanceledException("Credential prompt cancelled.", token);
                        }
                        var credential = connection.Credential;
                        if (target.AuthenticationMethod == SshAuthenticationMethod.Password && credential.RememberOnThisDevice && !credential.WasSavedCredential)
                        {
                            await PersistBatchCredentialAsync(target, credential.Secret, token);
                            Interlocked.Exchange(ref credentialUpdated, 1);
                        }
                        output = connection.Connection.Output;
                        error = connection.Connection.Error;
                        exitCode = connection.Connection.ExitCode;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        error = "Execution cancelled by user.";
                        throw;
                    }
                    catch (Exception exception)
                    {
                        error = exception.Message;
                    }
                    finally
                    {
                        stopwatch.Stop();
                    }

                    var log = new ScriptRunLog(Guid.NewGuid(), script.Id, scriptName, target.Id, target.Name, startedAt,
                        stopwatch.ElapsedMilliseconds, exitCode, output, error, exitCode == 0, "Remote");
                    try
                    {
                        await _scriptCenterStore.AppendRunAsync(log, token);
                    }
                    catch (Exception persistException)
                    {
                        error = string.IsNullOrWhiteSpace(error) ? $"Run history save failed: {persistException.Message}" : $"{error}\nRun history save failed: {persistException.Message}";
                        log = log with { Error = error, Succeeded = false };
                    }
                    results[index] = log;
                    var done = Interlocked.Increment(ref completedCount);
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        RecentScriptRuns.Insert(0, log);
                        while (RecentScriptRuns.Count > 100)
                            RecentScriptRuns.RemoveAt(RecentScriptRuns.Count - 1);
                        ScriptCenterStatus = $"BATCH RUNNING / {done} OF {selectedHosts.Count} COMPLETE";
                    });
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Completed hosts retain their individual results; unfinished hosts stop.
            }
            if (credentialUpdated != 0)
                await LoadAsync(CancellationToken.None);

            foreach (var result in results.OrderBy(pair => pair.Key).Select(pair => pair.Value))
            {
                combinedOutput.AppendLine($"===== {result.HostName} / {(result.ExitCode is null ? "ERROR" : $"EXIT {result.ExitCode}")} =====");
                if (!string.IsNullOrEmpty(result.Output)) combinedOutput.AppendLine(result.Output.TrimEnd());
                if (!string.IsNullOrWhiteSpace(result.Error)) combinedOutput.AppendLine($"STDERR: {result.Error.TrimEnd()}");
            }
            var succeeded = results.Values.Count(result => result.Succeeded);
            var failed = results.Count - succeeded;
            var wasCancelled = cancellationToken.IsCancellationRequested;
            if (results.Count > 0)
            {
                var last = results.OrderBy(pair => pair.Key).Last().Value;
                SelectedScriptRun = last with { Output = combinedOutput.ToString(), TargetType = "Batch" };
            }
            ScriptCenterStatus = wasCancelled
                ? $"BATCH CANCELLED / {succeeded} SUCCESS / {failed} FAILED / {results.Count} OF {selectedHosts.Count} COMPLETED"
                : $"BATCH COMPLETE / {succeeded} SUCCESS / {failed} FAILED";
            StatusMessage = $"SCRIPT BATCH / {succeeded} SUCCESS / {failed} FAILED";
        }
        finally
        {
            _scriptRunCancellation?.Dispose();
            _scriptRunCancellation = null;
            IsScriptRunning = false;
        }
    }

    private async Task PersistBatchCredentialAsync(HostProfile host, string secret, CancellationToken cancellationToken)
    {
        await _batchCredentialPersistenceLock.WaitAsync(cancellationToken);
        try
        {
            var previousReference = host.CredentialReference;
            var newReference = await _credentialStore.StoreAsync(secret, cancellationToken);
            try
            {
                await _hostRepository.SaveAsync(host with { CredentialReference = newReference }, cancellationToken);
            }
            catch
            {
                try { await _credentialStore.DeleteAsync(newReference, CancellationToken.None); }
                catch { /* Preserve the host persistence error. */ }
                throw;
            }
            if (!string.IsNullOrWhiteSpace(previousReference) && previousReference != newReference)
            {
                try { await _credentialStore.DeleteAsync(previousReference, cancellationToken); }
                catch { /* The new credential reference is already committed. */ }
            }
        }
        finally { _batchCredentialPersistenceLock.Release(); }
    }

    [RelayCommand]
    private void CancelScriptRun() => _scriptRunCancellation?.Cancel();

    private async Task<(T? Connection, ConnectionCredential? Credential)> ConnectWithCredentialFallbackAsync<T>(
        HostProfile host,
        Func<string, CancellationToken, Task<T>> connect,
        Func<string, Task> showCredentialReadError,
        CancellationToken cancellationToken = default,
        SemaphoreSlim? promptLock = null)
        where T : class
    {
        string? savedSecret = null;
        if (host.AuthenticationMethod == SshAuthenticationMethod.Password
            && !string.IsNullOrWhiteSpace(host.CredentialReference))
        {
            try
            {
                savedSecret = await _credentialStore.ReadAsync(host.CredentialReference, cancellationToken);
            }
            catch (Exception exception)
            {
                await showCredentialReadError(exception.Message);
            }
        }

        if (host.AuthenticationMethod == SshAuthenticationMethod.Password
            && !string.IsNullOrWhiteSpace(savedSecret))
        {
            try
            {
                var connection = await connect(savedSecret, cancellationToken);
                return (connection, new ConnectionCredential(savedSecret, RememberOnThisDevice: true, WasSavedCredential: true));
            }
            catch (SshAuthenticationException)
            {
                // The saved password is stale. Ask once for a replacement and
                // let the successful connection path update the local credential.
            }
        }

        if (promptLock is not null)
            await promptLock.WaitAsync(cancellationToken);
        ConnectionCredential? credential;
        try { credential = await _credentialPrompt.PromptForSecretAsync(host, savedSecret: null); }
        finally { promptLock?.Release(); }
        if (credential is null)
            return (null, null);

        var authenticatedConnection = await connect(credential.Secret, cancellationToken);
        return (authenticatedConnection, credential);
    }

    partial void OnSelectedScriptRunChanged(ScriptRunLog? value)
    {
        OnPropertyChanged(nameof(SelectedScriptRunText));
        if (value is not null)
            ScriptCenterStatus = $"{(value.Succeeded ? "SUCCESS" : "FAILED")} / {value.HostName} / {value.StartedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}";
    }

    private bool CanEditSelectedHost() => SelectedHost is not null;
    private bool CanForgetCredential() => !string.IsNullOrWhiteSpace(SelectedHost?.CredentialReference);

    [RelayCommand]
    private void ToggleScriptCatalog() => IsScriptCatalogExpanded = !IsScriptCatalogExpanded;

    [RelayCommand]
    private void ToggleRecentRuns() => IsRecentRunsExpanded = !IsRecentRunsExpanded;

    private bool FilterScript(object item)
    {
        if (item is not ScriptDefinition script)
            return false;
        var query = ScriptSearchText.Trim();
        return query.Length == 0
            || script.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || script.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || script.FolderPath.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || script.Runtime.ToString().Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    [RelayCommand]
    private void ToggleScriptFolder(ScriptCatalogNode? node)
    {
        if (node is not { IsFolder: true })
            return;
        node.IsExpanded = !node.IsExpanded;
        RebuildVisibleScriptCatalogNodes();
    }

    private void RebuildScriptCatalogTree()
    {
        var folderExpansion = EnumerateScriptCatalogNodes(ScriptCatalogNodes)
            .Where(node => node.IsFolder)
            .ToDictionary(node => node.FolderPath, node => node.IsExpanded, StringComparer.OrdinalIgnoreCase);
        ScriptCatalogNodes.Clear();
        VisibleScriptCatalogNodes.Clear();

        var folders = new Dictionary<string, ScriptCatalogNode>(StringComparer.OrdinalIgnoreCase);
        var query = ScriptSearchText.Trim();
        foreach (var script in Scripts.Where(script => MatchesScriptSearch(script, query)))
        {
            var segments = script.FolderPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var siblings = ScriptCatalogNodes;
            var pathSegments = new List<string>();
            foreach (var segment in segments)
            {
                pathSegments.Add(segment);
                var path = string.Join('/', pathSegments);
                if (!folders.TryGetValue(path, out var folder))
                {
                    var isExpanded = query.Length > 0
                        || !folderExpansion.TryGetValue(path, out var wasExpanded)
                        || wasExpanded;
                    folder = new ScriptCatalogNode(segment, path) { IsExpanded = isExpanded };
                    folders.Add(path, folder);
                    siblings.Add(folder);
                }
                siblings = folder.Children;
            }

            siblings.Add(new ScriptCatalogNode(script.Name, script.FolderPath, script));
        }

        SortScriptCatalogNodes(ScriptCatalogNodes);
        RebuildVisibleScriptCatalogNodes();
        SelectedScriptCatalogNode = VisibleScriptCatalogNodes.FirstOrDefault(node => node.Script?.Id == SelectedScript?.Id);
    }

    private void RebuildVisibleScriptCatalogNodes()
    {
        VisibleScriptCatalogNodes.Clear();
        AddVisibleScriptNodes(ScriptCatalogNodes, []);
    }

    private void AddVisibleScriptNodes(IReadOnlyList<ScriptCatalogNode> siblings, IReadOnlyList<bool> ancestorHasFollowingSibling)
    {
        for (var index = 0; index < siblings.Count; index++)
        {
            var node = siblings[index];
            var hasFollowingSibling = index < siblings.Count - 1;
            node.SetTreePresentation(ancestorHasFollowingSibling.Count, ancestorHasFollowingSibling, hasFollowingSibling);
            VisibleScriptCatalogNodes.Add(node);

            if (node.IsFolder && node.IsExpanded)
                AddVisibleScriptNodes(node.Children, [.. ancestorHasFollowingSibling, hasFollowingSibling]);
        }
    }

    private bool MatchesScriptSearch(ScriptDefinition script, string query) => query.Length == 0
        || script.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || script.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || script.FolderPath.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || script.Runtime.ToString().Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private static IEnumerable<ScriptCatalogNode> EnumerateScriptCatalogNodes(IEnumerable<ScriptCatalogNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in EnumerateScriptCatalogNodes(node.Children))
                yield return child;
        }
    }

    private static void SortScriptCatalogNodes(ObservableCollection<ScriptCatalogNode> nodes)
    {
        var sorted = nodes.OrderByDescending(node => node.IsFolder)
            .ThenBy(node => node.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        nodes.Clear();
        foreach (var node in sorted)
        {
            SortScriptCatalogNodes(node.Children);
            nodes.Add(node);
        }
    }

    private static string NormalizeScriptFolderPath(string path) => string.Join('/',
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => segment is not "." and not ".."));

    private static IReadOnlyList<ScriptInputFieldDefinition> ParseLegacyInputTemplate(string? template) =>
        (template ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => (Line: line, Kind: line.StartsWith("ARG:", StringComparison.OrdinalIgnoreCase) ? "ARG" : line.StartsWith("ENV:", StringComparison.OrdinalIgnoreCase) ? "ENV" : string.Empty))
            .Where(item => item.Kind.Length > 0 && item.Line.Length > 4 && !string.IsNullOrWhiteSpace(item.Line[4..]))
            .Select(item => new ScriptInputFieldDefinition(item.Kind, item.Line[4..].Trim()))
            .ToArray();

    private static IReadOnlyList<ScriptInputFieldDefinition> SerializeScriptInputFields(IEnumerable<ScriptTemplateFieldViewModel> fields) =>
        fields.Where(field => !string.IsNullOrWhiteSpace(field.Name))
            .Select(field => new ScriptInputFieldDefinition(
                field.Kind == "ENV" ? "ENV" : "ARG",
                field.Name.Trim(),
                ParseInputTypeName(field.ValueType),
                field.IsRequired,
                field.IsSensitive || field.ValueType == "密码",
                field.Options.Trim(),
                field.Group.Trim()))
            .ToArray();

    private static string SerializeScriptInputTemplate(IEnumerable<ScriptInputFieldDefinition> fields) => string.Join(Environment.NewLine,
        fields.Select(field => $"{field.Kind}: {field.Name}"));

    private static string? ValidateScriptInputFields(IReadOnlyList<ScriptInputFieldDefinition> fields)
    {
        foreach (var kind in new[] { "ARG", "ENV" })
        {
            var duplicate = fields.Where(field => field.Kind == kind)
                .GroupBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
                return $"{kind} 名称重复：{duplicate.Key}";
        }
        foreach (var field in fields.Where(field => field.Kind == "ENV"))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(field.Name, "^[A-Za-z_][A-Za-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                return $"环境变量名称无效：{field.Name}";
            if (field.Name.Equals("ONLYSHELL_ARG_COUNT", StringComparison.OrdinalIgnoreCase)
                || field.Name.StartsWith("ONLYSHELL_ARG_", StringComparison.OrdinalIgnoreCase))
                return $"{field.Name} 是 OnlyShell 为 CMD 参数保留的名称。";
        }
        foreach (var field in fields.Where(field => field.ValueType == ScriptInputValueType.Choice))
            if (string.IsNullOrWhiteSpace(field.Options))
                return $"选项参数“{field.Name}”至少需要配置一个选项。";
        return null;
    }

    private static ScriptInputValueType ParseInputTypeName(string value) => value switch
    {
        "数字" => ScriptInputValueType.Number,
        "布尔" => ScriptInputValueType.Boolean,
        "路径" => ScriptInputValueType.Path,
        "选项" => ScriptInputValueType.Choice,
        "密码" => ScriptInputValueType.Secret,
        _ => ScriptInputValueType.Text
    };

    private static string InputTypeName(ScriptInputValueType value) => value switch
    {
        ScriptInputValueType.Number => "数字",
        ScriptInputValueType.Boolean => "布尔",
        ScriptInputValueType.Path => "路径",
        ScriptInputValueType.Choice => "选项",
        ScriptInputValueType.Secret => "密码",
        _ => "文本"
    };

    private static ScriptExecutionInputs CreateDefaultInputs(IEnumerable<ScriptTemplateFieldViewModel> fields) =>
        new(fields.Where(field => field.Kind == "ARG").Select(field => field.DefaultValue).ToArray(),
            new Dictionary<string, string>(fields.Where(field => field.Kind == "ENV")
                .Select(field => new KeyValuePair<string, string>(field.Name.Trim(), field.DefaultValue)), StringComparer.OrdinalIgnoreCase));

    private static ScriptExecutionInputs MergeScriptInputs(ScriptExecutionInputs? defaults, ScriptExecutionInputs? lastInputs)
    {
        defaults ??= new ScriptExecutionInputs([], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        if (lastInputs is null)
            return defaults;
        var argCount = Math.Max(defaults.Arguments.Count, lastInputs.Arguments.Count);
        var arguments = Enumerable.Range(0, argCount)
            .Select(index => index < lastInputs.Arguments.Count && lastInputs.Arguments[index].Length > 0
                ? lastInputs.Arguments[index]
                : index < defaults.Arguments.Count ? defaults.Arguments[index] : string.Empty)
            .ToArray();
        var environment = new Dictionary<string, string>(defaults.EnvironmentVariables, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in lastInputs.EnvironmentVariables)
            environment[pair.Key] = pair.Value;
        return new ScriptExecutionInputs(arguments, environment);
    }

    private async Task LoadScriptInputDefaultsAsync(Guid scriptId)
    {
        try
        {
            var defaults = await _scriptExecutionInputsStore.LoadDefaultsAsync(scriptId);
            if (defaults is null || SelectedScript?.Id != scriptId)
                return;
            var arguments = ScriptTemplateFields.Where(field => field.Kind == "ARG").ToArray();
            for (var index = 0; index < arguments.Length && index < defaults.Arguments.Count; index++)
                arguments[index].DefaultValue = defaults.Arguments[index];
            foreach (var field in ScriptTemplateFields.Where(field => field.Kind == "ENV"))
                if (defaults.EnvironmentVariables.TryGetValue(field.Name, out var value))
                    field.DefaultValue = value;
        }
        catch (Exception exception)
        {
            ScriptCenterStatus = "INPUT DEFAULTS LOAD FAILED";
            Application.Current?.Dispatcher.BeginInvoke(() => _ = _syncInteraction.ShowErrorAsync("读取加密参数默认值失败", exception.Message));
        }
    }

    private async Task SaveAndRefreshAsync(HostProfile profile)
    {
        try
        {
            await _hostRepository.SaveAsync(profile);
            await LoadAsync();
        }
        catch (Exception exception)
        {
            StatusMessage = "LOCAL DB / ERROR";
            await _dialogs.ShowErrorAsync($"保存主机配置失败。\n\n{exception.Message}");
        }
    }
}
