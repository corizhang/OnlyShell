using OnlyShell.Core.Sync;

namespace OnlyShell.Presentation.Services;

public interface IGitHubSyncService
{
    bool IsAuthenticated { get; }
    string AuthenticationLabel { get; }
    string? SavedOwner { get; }
    string? SavedRepository { get; }
    string? SavedBranch { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task PushAsync(string owner, string repository, string branch, string passphrase, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OnlyShell.Core.Hosts.HostProfile>> PullAsync(string owner, string repository, string branch, string passphrase, CancellationToken cancellationToken = default);
}

public interface ISyncInteractionService
{
    Task ShowDeviceCodeAsync(string verificationUri, string userCode);
    Task<string?> RequestPassphraseAsync(bool confirm);
    Task<bool> ConfirmImportAsync(int remoteHostCount, int localHostCount);
    Task ShowErrorAsync(string title, string message);
}
