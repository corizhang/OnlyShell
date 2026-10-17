namespace OnlyShell.Core.Sync;

public interface IConfigSyncProvider
{
    Task<SyncSnapshot?> DownloadAsync(CancellationToken cancellationToken = default);
    Task UploadAsync(SyncSnapshot snapshot, string? expectedVersion, CancellationToken cancellationToken = default);
}

public sealed record SyncSnapshot(int SchemaVersion, string Payload, string? Version, DateTimeOffset UpdatedAt);
