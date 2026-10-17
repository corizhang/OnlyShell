using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnlyShell.Core.Hosts;
using OnlyShell.Core.Files;
using OnlyShell.Core.Secrets;
using OnlyShell.Presentation.Services;
using OnlyShell.Terminal;

namespace OnlyShell.Presentation.ViewModels;

public sealed partial class WorkspaceViewModel : ObservableObject
{
    private sealed record LocalTreeEntry(string FullPath, string RelativePath, bool IsDirectory, long Size);

    private sealed record QueuedSftpTransfer(
        SftpTransferItemViewModel Item,
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
    private ISftpSession? _sftpSession;
    private HostProfile? _sftpHost;
    private readonly Queue<QueuedSftpTransfer> _sftpTransferQueue = new();
    private CancellationTokenSource? _activeTransferCancellation;
    private Task? _transferQueueTask;
    private readonly DispatcherTimer _sftpConnectionMonitor;
    private IReadOnlyList<RemoteFileEntry> _selectedRemoteFiles = [];
    private IReadOnlyList<RemoteFileEntry> _selectedLocalFiles = [];

    public ObservableCollection<HostProfile> Hosts { get; } = [];
    public ObservableCollection<TerminalTabViewModel> TerminalTabs { get; } = [];
    public ObservableCollection<RemoteFileEntry> RemoteFiles { get; } = [];
    public ObservableCollection<RemoteFileEntry> LocalFiles { get; } = [];
    public IReadOnlyList<string> LocalDrives { get; } = DriveInfo.GetDrives().Where(drive => drive.IsReady).Select(drive => drive.Name).ToArray();
    public ObservableCollection<SftpTransferItemViewModel> SftpTransfers { get; } = [];
    public IReadOnlyList<RemoteFileEntry> SelectedRemoteFiles => _selectedRemoteFiles;
    public IReadOnlyList<RemoteFileEntry> SelectedLocalFiles => _selectedLocalFiles;
    public ICollectionView HostsView { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditHostCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteHostCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectToSelectedHostCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForgetCredentialCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectSftpCommand))]
    private HostProfile? selectedHost;

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

    public bool IsHostSection => string.Equals(ActiveSection, "主机", StringComparison.Ordinal);
    public bool IsFeaturePlaceholder => !IsHostSection && !IsTerminalSessionVisible && !IsTerminalEmpty && !IsSftpWorkspace && !IsSyncWorkspace;
    public bool IsSftpWorkspace => string.Equals(ActiveSection, "SFTP", StringComparison.Ordinal);
    public bool IsSyncWorkspace => string.Equals(ActiveSection, "同步", StringComparison.Ordinal);
    public bool IsSftpConnected => _sftpSession?.IsConnected == true;
    public bool IsSftpDisconnected => !IsSftpConnected;
    public HostProfile? SftpHost => _sftpHost;
    public bool HasSftpTransfers => SftpTransfers.Count > 0;
    public bool IsSftpTransferRunning => _activeTransferCancellation is not null;
    public int PendingTransferCount => SftpTransfers.Count(item => item.Status == "QUEUED");
    public bool IsTerminalSessionVisible => string.Equals(ActiveSection, "终端", StringComparison.Ordinal) && TerminalTabs.Count > 0;
    public bool IsTerminalEmpty => string.Equals(ActiveSection, "终端", StringComparison.Ordinal) && TerminalTabs.Count == 0;
    public string SectionHeading => IsHostSection ? "主机工作区" : $"{ActiveSection}工作区";
    public string SectionSubtitle => IsHostSection ? "本地主机与连接配置" : FeatureMessage;
    public string FeatureMessage => ActiveSection switch
    {
        "终端" => "通过 SSH 建立交互式终端会话；断开后保留当前窗口内的终端缓冲区。",
        "SFTP" => "本地与远程双栏文件管理；选择文件后直接加入传输队列。",
        "同步" => "GitHub Device Flow · 私有仓库 · AES-GCM 加密主机配置同步。",
        "设置" => "字体、主题、终端行为和本地数据设置将在设置页面阶段实现。",
        "传输" => "上传与下载任务队列将在 SFTP 功能阶段实现。",
        "历史" => "SSH 会话接入后会在此记录连接历史。",
        _ => "此工作区将在后续功能阶段实现。"
    };

    public int HostCount => Hosts.Count;
    public int ActiveSessionCount => TerminalTabs.Count;
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
        ISyncInteractionService syncInteraction)
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
        };

        HostsView = CollectionViewSource.GetDefaultView(Hosts);
        HostsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HostProfile.GroupLabel)));
        HostsView.SortDescriptions.Add(new SortDescription(nameof(HostProfile.Name), ListSortDirection.Ascending));
        HostsView.Filter = FilterHost;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var items = await _hostRepository.ListAsync(cancellationToken);
        Hosts.Clear();
        foreach (var item in items)
            Hosts.Add(item);
        StatusMessage = $"LOCAL / READY   ·   {HostCount} HOSTS";
    }

    partial void OnHostFilterChanged(string value) => HostsView.Refresh();

    partial void OnSelectedHostChanged(HostProfile? value)
    {
        OnPropertyChanged(nameof(HasSelectedHost));
        OnPropertyChanged(nameof(NoHostSelected));
        OnPropertyChanged(nameof(CredentialStatus));
    }

    partial void OnActiveSectionChanged(string value)
    {
        OnPropertyChanged(nameof(IsHostSection));
        OnPropertyChanged(nameof(IsSftpWorkspace));
        OnPropertyChanged(nameof(IsSyncWorkspace));
        OnPropertyChanged(nameof(IsSftpConnected));
        OnPropertyChanged(nameof(IsSftpDisconnected));
        OnPropertyChanged(nameof(IsFeaturePlaceholder));
        OnPropertyChanged(nameof(IsTerminalSessionVisible));
        OnPropertyChanged(nameof(IsTerminalEmpty));
        OnPropertyChanged(nameof(FeatureMessage));
        OnPropertyChanged(nameof(SectionHeading));
        OnPropertyChanged(nameof(SectionSubtitle));
    }

    private bool FilterHost(object item)
    {
        if (item is not HostProfile host || string.IsNullOrWhiteSpace(HostFilter))
            return item is HostProfile;

        return host.Name.Contains(HostFilter, StringComparison.CurrentCultureIgnoreCase)
            || host.HostName.Contains(HostFilter, StringComparison.CurrentCultureIgnoreCase)
            || host.UserName.Contains(HostFilter, StringComparison.CurrentCultureIgnoreCase)
            || (host.Group?.Contains(HostFilter, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }

    [RelayCommand]
    private async Task AddHostAsync()
    {
        var result = await _dialogs.EditAsync(null);
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

        var result = await _dialogs.EditAsync(SelectedHost);
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
    private async Task ConnectToSelectedHostAsync()
    {
        var host = SelectedHost;
        if (host is null)
            return;

        string? savedPassword = null;
        if (host.AuthenticationMethod == SshAuthenticationMethod.Password
            && !string.IsNullOrWhiteSpace(host.CredentialReference))
        {
            try
            {
                savedPassword = await _credentialStore.ReadAsync(host.CredentialReference);
            }
            catch (Exception exception)
            {
                await _dialogs.ShowErrorAsync($"读取本机保存的密码失败，将改为手动输入。\n\n{exception.Message}");
            }
        }

        var credential = await _credentialPrompt.PromptForSecretAsync(host, savedPassword);
        savedPassword = null;
        if (credential is null)
            return;
        var secret = credential.Secret;

        StatusMessage = $"SSH / CONNECTING   ·   {host.HostName}:{host.Port}";
        try
        {
            var session = await _sshConnector.ConnectAsync(host, secret);

            var tab = new TerminalTabViewModel(host, session);
            TerminalTabs.Add(tab);
            SelectedTerminalTab = tab;
            ActiveSection = "终端";
            StatusMessage = $"SSH / CONNECTED   ·   {host.HostName}:{host.Port}";

            string? stagedCredentialReference = null;
            try
            {
                var updatedHost = host with { LastConnectedAt = DateTimeOffset.UtcNow };
                if (host.AuthenticationMethod == SshAuthenticationMethod.Password && credential.RememberOnThisDevice)
                {
                    stagedCredentialReference = await _credentialStore.StoreAsync(secret);
                    updatedHost = updatedHost with { CredentialReference = stagedCredentialReference };
                }
                else if (host.AuthenticationMethod == SshAuthenticationMethod.Password
                    && !string.IsNullOrWhiteSpace(host.CredentialReference))
                {
                    await _credentialStore.DeleteAsync(host.CredentialReference);
                    updatedHost = updatedHost with { CredentialReference = null };
                }

                await _hostRepository.SaveAsync(updatedHost);
                var previousReference = host.CredentialReference;
                stagedCredentialReference = null;
                secret = string.Empty;
                if (credential.RememberOnThisDevice
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
            secret = string.Empty;
            StatusMessage = $"SSH / FAILED   ·   {host.HostName}:{host.Port}";
            await _dialogs.ShowErrorAsync($"无法连接到 {host.Name}。\n\n{exception.Message}");
        }
    }

    [RelayCommand]
    private async Task CloseTerminalAsync(TerminalTabViewModel? tab)
    {
        if (tab is null)
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
        if (tab is null || tab.Session.IsConnected)
            return;

        var host = tab.Host;
        string? savedPassword = null;
        if (host.AuthenticationMethod == SshAuthenticationMethod.Password
            && !string.IsNullOrWhiteSpace(host.CredentialReference))
        {
            try { savedPassword = await _credentialStore.ReadAsync(host.CredentialReference); }
            catch (Exception exception) { await _dialogs.ShowErrorAsync($"读取本机保存的密码失败，将改为手动输入。\n\n{exception.Message}"); }
        }

        var credential = await _credentialPrompt.PromptForSecretAsync(host, savedPassword);
        savedPassword = null;
        if (credential is null)
            return;

        StatusMessage = $"SSH / RECONNECTING   ·   {host.HostName}:{host.Port}";
        var secret = credential.Secret;
        VirtualTerminal.SecureShellSession session;
        try
        {
            session = await _sshConnector.ConnectAsync(host, secret);
        }
        catch (Exception exception)
        {
            secret = string.Empty;
            StatusMessage = $"SSH / RECONNECT FAILED   ·   {host.HostName}:{host.Port}";
            await _dialogs.ShowErrorAsync($"无法重新连接到 {host.Name}。\n\n{exception.Message}");
            return;
        }

        try
        {
            await tab.ReplaceSessionAsync(session);
            StatusMessage = $"SSH / RECONNECTED   ·   {host.HostName}:{host.Port}";
            var updatedHost = host with { LastConnectedAt = DateTimeOffset.UtcNow };
            string? stagedCredentialReference = null;
            if (host.AuthenticationMethod == SshAuthenticationMethod.Password && credential.RememberOnThisDevice)
            {
                stagedCredentialReference = await _credentialStore.StoreAsync(secret);
                updatedHost = updatedHost with { CredentialReference = stagedCredentialReference };
            }
            else if (host.AuthenticationMethod == SshAuthenticationMethod.Password
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

    public async Task CloseAllTerminalsAsync()
    {
        foreach (var tab in TerminalTabs.ToArray())
            await CloseTerminalAsync(tab);
        await CloseSftpAsync();
    }

    [RelayCommand(CanExecute = nameof(CanConnectSftp))]
    private async Task ConnectSftpAsync()
    {
        var host = SelectedHost;
        if (host is null)
            return;

        string? savedPassword = null;
        if (host.AuthenticationMethod == SshAuthenticationMethod.Password
            && !string.IsNullOrWhiteSpace(host.CredentialReference))
        {
            try { savedPassword = await _credentialStore.ReadAsync(host.CredentialReference); }
            catch (Exception exception) { await _sftpDialogs.ShowErrorAsync($"读取本机保存的密码失败，将改为手动输入。\n\n{exception.Message}"); }
        }

        var credential = await _credentialPrompt.PromptForSecretAsync(host, savedPassword);
        savedPassword = null;
        if (credential is null)
            return;

        SftpStatus = $"CONNECTING  {host.HostName}:{host.Port}";
        try
        {
            _sftpSession = await _sftpConnector.ConnectAsync(host, credential.Secret);
            _sftpHost = host;
            OnPropertyChanged(nameof(SftpHost));
            OnPropertyChanged(nameof(IsSftpConnected));
            OnPropertyChanged(nameof(IsSftpDisconnected));
            RemotePath = _sftpSession.HomeDirectory;
            await RefreshRemoteDirectoryAsync();
            await RefreshLocalDirectoryAsync();

            if (host.AuthenticationMethod == SshAuthenticationMethod.Password && credential.RememberOnThisDevice)
            {
                var reference = await _credentialStore.StoreAsync(credential.Secret);
                await _hostRepository.SaveAsync(host with { CredentialReference = reference, LastConnectedAt = DateTimeOffset.UtcNow });
                if (!string.IsNullOrWhiteSpace(host.CredentialReference))
                    await _credentialStore.DeleteAsync(host.CredentialReference);
                await LoadAsync();
            }
            else if (host.AuthenticationMethod == SshAuthenticationMethod.Password
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
            SftpStatus = $"CONNECTED  {host.UserName}@{host.HostName}";
        }
        catch (Exception exception)
        {
            await CloseSftpAsync();
            SftpStatus = $"FAILED  {host.HostName}:{host.Port}";
            await _sftpDialogs.ShowErrorAsync($"无法打开 {host.Name} 的 SFTP 会话。\n\n{exception.Message}");
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
                    EnqueueSftpTransfer(entry.Name, "UPLOAD TREE", remotePath,
                        (session, progress, token) => UploadDirectoryAsync(session, remotePath, tree, progress, token));
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
                    EnqueueSftpTransfer(entry.Name, "DOWNLOAD TREE", entry.FullPath,
                        (session, progress, token) => DownloadDirectoryAsync(session, entry, localPath, tree, progress, token));
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

    private bool CanConnectSftp() => SelectedHost is not null && _sftpSession is null;
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
        var result = new List<LocalTreeEntry> { new(root, string.Empty, true, 0) };
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
                result.Add(new LocalTreeEntry(info.FullName, relative, isDirectory, info is FileInfo file ? file.Length : 0));
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
        CancellationToken cancellationToken)
    {
        await session.EnsureDirectoryAsync(remoteRoot, cancellationToken);
        foreach (var directory in tree.Where(entry => entry.IsDirectory && entry.RelativePath.Length > 0).OrderBy(entry => entry.RelativePath.Length))
            await session.EnsureDirectoryAsync(JoinRemotePath(remoteRoot, directory.RelativePath.Replace('\\', '/')), cancellationToken);

        var files = tree.Where(entry => !entry.IsDirectory).ToArray();
        var totalBytes = files.Sum(file => file.Size);
        long completedBytes = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sentBeforeFile = completedBytes;
            var target = JoinRemotePath(remoteRoot, file.RelativePath.Replace('\\', '/'));
            var itemProgress = new Progress<double>(value => progress.Report(totalBytes == 0 ? 100 : Math.Clamp((sentBeforeFile + file.Size * value / 100d) * 100d / totalBytes, 0, 100)));
            await session.UploadAsync(file.FullPath, target, itemProgress, cancellationToken);
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
        long completedBytes = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = GetRemoteRelativePath(remoteRoot.FullPath, file.FullPath);
            var target = GetSafeLocalTarget(localRoot, relative);
            var sentBeforeFile = completedBytes;
            var itemProgress = new Progress<double>(value => progress.Report(totalBytes == 0 ? 100 : Math.Clamp((sentBeforeFile + file.Size * value / 100d) * 100d / totalBytes, 0, 100)));
            await session.DownloadAsync(file.FullPath, target, itemProgress, cancellationToken);
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
        _sftpTransferQueue.Enqueue(new QueuedSftpTransfer(item, execute));
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
        var passphrase = await _syncInteraction.RequestPassphraseAsync(confirm: true);
        if (passphrase is null)
            return;
        IsSyncBusy = true;
        SyncStatus = "ENCRYPTING / UPLOADING…";
        try
        {
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

            var remoteIds = merged.Select(host => host.Id).ToHashSet();
            foreach (var local in await _hostRepository.ListAsync())
                if (!remoteIds.Contains(local.Id))
                    await _hostRepository.DeleteAsync(local.Id);
            foreach (var host in merged)
                await _hostRepository.SaveAsync(host);
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
        if (!string.Equals(section, "主机", StringComparison.Ordinal))
        {
            StatusMessage = $"{section.ToUpperInvariant()} / NEXT MILESTONE";
            return;
        }

        StatusMessage = $"LOCAL / READY   ·   {HostCount} HOSTS";
    }

    private bool CanEditSelectedHost() => SelectedHost is not null;
    private bool CanForgetCredential() => !string.IsNullOrWhiteSpace(SelectedHost?.CredentialReference);

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
