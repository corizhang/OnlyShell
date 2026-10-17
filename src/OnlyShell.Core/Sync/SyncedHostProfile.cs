using OnlyShell.Core.Hosts;

namespace OnlyShell.Core.Sync;

/// <summary>Explicit cloud-safe host fields. Device credentials and private-key paths are intentionally absent.</summary>
public sealed record SyncedHostProfile(
    Guid Id,
    string Name,
    string HostName,
    int Port,
    string UserName,
    string? Group,
    DateTimeOffset? LastConnectedAt,
    bool IsFavorite,
    string? Note,
    SshAuthenticationMethod AuthenticationMethod);

public sealed record SyncConfiguration(int SchemaVersion, IReadOnlyList<SyncedHostProfile> Hosts);
