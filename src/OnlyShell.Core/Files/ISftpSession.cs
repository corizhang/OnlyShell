namespace OnlyShell.Core.Files;

public interface ISftpSession : IAsyncDisposable
{
    bool IsConnected { get; }
    string HomeDirectory { get; }
    Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task UploadAsync(string localPath, string remotePath, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task DownloadAsync(string remotePath, string localPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task EnsureDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
    Task DeleteAsync(RemoteFileEntry entry, CancellationToken cancellationToken = default);
}
