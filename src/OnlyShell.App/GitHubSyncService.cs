using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using OnlyShell.Core.Hosts;
using OnlyShell.Core.Secrets;
using OnlyShell.Core.Sync;
using OnlyShell.Infrastructure.Sync;
using OnlyShell.Presentation.Services;

namespace OnlyShell.App;

/// <summary>Desktop GitHub Device Flow and encrypted host configuration synchronization.</summary>
public sealed class GitHubSyncService(
    IHostRepository hosts,
    ICredentialStore credentials,
    ISyncInteractionService interaction,
    HttpClient httpClient) : IGitHubSyncService
{
    // Public GitHub App identifier. Device Flow does not use a client secret.
    public const string ClientId = "Iv23liV1eE9w5M69aEZj";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OnlyShell", "sync.json");
    private readonly GitHubDeviceFlowClient _deviceFlow = new(httpClient);
    private SyncSettings _settings = LoadSettings();

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_settings.TokenReference);
    public string AuthenticationLabel => IsAuthenticated ? "GITHUB / AUTHORIZED" : "GITHUB / NOT CONNECTED";
    public string? SavedOwner => _settings.Owner;
    public string? SavedRepository => _settings.Repository;
    public string? SavedBranch => _settings.Branch;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var authorization = await _deviceFlow.BeginAsync(ClientId, cancellationToken);
        await interaction.ShowDeviceCodeAsync(authorization.VerificationUri, authorization.UserCode);
        var token = await _deviceFlow.WaitForTokenAsync(ClientId, authorization, cancellationToken: cancellationToken);
        var tokenJson = JsonSerializer.Serialize(token, JsonOptions);
        var tokenReference = await credentials.StoreAsync(tokenJson, cancellationToken);
        if (!string.IsNullOrWhiteSpace(_settings.TokenReference))
            await credentials.DeleteAsync(_settings.TokenReference, cancellationToken);
        _settings = _settings with { TokenReference = tokenReference };
        SaveSettings();
    }

    public async Task PushAsync(string owner, string repository, string branch, string passphrase, CancellationToken cancellationToken = default)
    {
        ValidateRepository(owner, repository, branch);
        var token = await GetTokenAsync(cancellationToken);
        var provider = CreateProvider(owner, repository, branch, token.AccessToken);
        var current = await provider.DownloadAsync(cancellationToken);
        if (current is not null)
            _ = new AesGcmSyncPayloadProtector().Unprotect(current.Payload, passphrase);
        var serialized = SyncConfigurationSerializer.Serialize(await hosts.ListAsync(cancellationToken));
        var encrypted = new AesGcmSyncPayloadProtector().Protect(serialized, passphrase);
        await provider.UploadAsync(new SyncSnapshot(1, encrypted, null, DateTimeOffset.UtcNow), _settings.Version, cancellationToken);
        var updated = await provider.DownloadAsync(cancellationToken);
        _settings = _settings with { Owner = owner.Trim(), Repository = repository.Trim(), Branch = branch.Trim(), Version = updated?.Version };
        SaveSettings();
    }

    public async Task<IReadOnlyList<HostProfile>> PullAsync(string owner, string repository, string branch, string passphrase, CancellationToken cancellationToken = default)
    {
        ValidateRepository(owner, repository, branch);
        var token = await GetTokenAsync(cancellationToken);
        var provider = CreateProvider(owner, repository, branch, token.AccessToken);
        var snapshot = await provider.DownloadAsync(cancellationToken)
            ?? throw new InvalidOperationException("指定仓库中还没有 OnlyShell 配置。请先在已有设备上执行上传。");
        var clear = new AesGcmSyncPayloadProtector().Unprotect(snapshot.Payload, passphrase);
        var remote = SyncConfigurationSerializer.Deserialize(clear);
        var merged = SyncConfigurationSerializer.MergeDeviceSecrets(remote, await hosts.ListAsync(cancellationToken));
        _settings = _settings with { Owner = owner.Trim(), Repository = repository.Trim(), Branch = branch.Trim(), Version = snapshot.Version };
        SaveSettings();
        return merged;
    }

    private async Task<GitHubUserToken> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.TokenReference))
            throw new InvalidOperationException("请先连接 GitHub 账号。");
        var json = await credentials.ReadAsync(_settings.TokenReference, cancellationToken)
            ?? throw new InvalidOperationException("GitHub 令牌已从 Windows 凭据管理器中移除，请重新连接账号。");
        var token = JsonSerializer.Deserialize<GitHubUserToken>(json, JsonOptions)
            ?? throw new InvalidDataException("本机 GitHub 令牌数据格式无效。");
        if (token.ExpiresAt is null || token.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            return token;
        if (string.IsNullOrWhiteSpace(token.RefreshToken))
            throw new InvalidOperationException("GitHub 令牌已过期，请重新连接账号。");
        var refreshed = await _deviceFlow.RefreshAsync(ClientId, token.RefreshToken, cancellationToken);
        await credentials.DeleteAsync(_settings.TokenReference, cancellationToken);
        _settings = _settings with { TokenReference = await credentials.StoreAsync(JsonSerializer.Serialize(refreshed, JsonOptions), cancellationToken) };
        SaveSettings();
        return refreshed;
    }

    private GitHubConfigSyncProvider CreateProvider(string owner, string repository, string branch, string accessToken) =>
        new(httpClient, owner.Trim(), repository.Trim(), branch.Trim(), accessToken);

    private static void ValidateRepository(string owner, string repository, string branch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
    }

    private static SyncSettings LoadSettings()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<SyncSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new()
                : new();
        }
        catch { return new(); }
    }

    private void SaveSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, JsonOptions));
    }

    private sealed record SyncSettings(string? Owner = null, string? Repository = null, string? Branch = "main", string? TokenReference = null, string? Version = null);
}

public sealed class WpfSyncInteractionService : ISyncInteractionService
{
    public Task ShowDeviceCodeAsync(string verificationUri, string userCode)
    {
        Process.Start(new ProcessStartInfo(verificationUri) { UseShellExecute = true });
        var window = new SyncPromptWindow("GitHub 授权", $"浏览器已打开授权页面。\n\n输入以下一次性代码：\n\n{userCode}\n\n完成 GitHub 授权后，点击“已完成”继续。", false) { Owner = System.Windows.Application.Current.MainWindow };
        return window.ShowDialog() == true ? Task.CompletedTask : Task.FromException(new OperationCanceledException("GitHub 授权已取消。"));
    }

    public Task<string?> RequestPassphraseAsync(bool confirm)
    {
        var window = new SyncPromptWindow("配置加密口令", confirm ? "设置一个至少 12 位的同步加密口令。此口令不会上传或保存在本机。" : "输入此配置对应的同步加密口令。", true, confirm) { Owner = System.Windows.Application.Current.MainWindow };
        return Task.FromResult(window.ShowDialog() == true ? window.Secret : null);
    }

    public Task<bool> ConfirmImportAsync(int remoteHostCount, int localHostCount)
    {
        var window = new SyncPromptWindow("替换本地主机配置", $"远端包含 {remoteHostCount} 台主机，本机有 {localHostCount} 台。继续后将以远端列表替换本地列表；同 ID 主机的本地密码引用和私钥路径会保留。", false, true) { Owner = System.Windows.Application.Current.MainWindow };
        return Task.FromResult(window.ShowDialog() == true);
    }

    public Task ShowErrorAsync(string title, string message)
    {
        System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow, message, title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        return Task.CompletedTask;
    }
}
