using OnlyShell.Core.Files;
using OnlyShell.Core.Hosts;
using OnlyShell.Core.Security;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace OnlyShell.Infrastructure.Ssh;

public sealed class SftpSessionConnector(
    IKnownHostRepository knownHosts,
    IHostKeyConfirmationService confirmation) : ISftpSessionConnector
{
    public async Task<ISftpSession> ConnectAsync(
        HostProfile host,
        string secret,
        CancellationToken cancellationToken = default)
    {
        var client = new SftpClient(SshConnectionInfoFactory.Create(host, secret))
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20),
            OperationTimeout = TimeSpan.FromSeconds(30)
        };
        client.HostKeyReceived += (_, key) =>
        {
            var fingerprint = $"SHA256:{key.FingerPrintSHA256}";
            var previous = knownHosts.FindFingerprint(host.HostName, host.Port);
            key.CanTrust = string.Equals(previous, fingerprint, StringComparison.Ordinal)
                || confirmation.Confirm(host, fingerprint, previous);
            if (key.CanTrust && !string.Equals(previous, fingerprint, StringComparison.Ordinal))
                knownHosts.TrustFingerprint(host.HostName, host.Port, fingerprint);
        };

        try
        {
            await client.ConnectAsync(cancellationToken);
            return new SftpSession(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

internal sealed class SftpSession(SftpClient client) : ISftpSession
{
    public bool IsConnected => client.IsConnected;
    public string HomeDirectory => client.WorkingDirectory;

    public async Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var entries = new List<RemoteFileEntry>();
        await foreach (var file in client.ListDirectoryAsync(path, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Name is "." or "..")
                continue;
            entries.Add(new RemoteFileEntry(file.Name, file.FullName, file.IsDirectory, (long)file.Length, file.LastWriteTime, file.IsSymbolicLink));
        }
        return entries.OrderByDescending(entry => entry.IsDirectory).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task UploadAsync(string localPath, string remotePath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        await using var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true);
        // Progress<T> can dispatch a report after cancellation has closed the stream.
        // Capture its length up front so late callbacks never touch a disposed FileStream.
        var totalBytes = input.Length;
        var report = progress is null ? null : new Progress<UploadFileProgressReport>(value =>
            progress.Report(totalBytes == 0 ? 100 : Math.Clamp(value.TotalBytesUploaded * 100d / totalBytes, 0, 100)));
        var temporaryPath = $"{remotePath}.onlyshell-{Guid.NewGuid():N}.uploading";
        string? backupPath = null;
        try
        {
            await client.UploadFileAsync(input, temporaryPath, canOverride: false, report, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var existingAttributes = await GetRemoteAttributesOrNullAsync(remotePath);
            if (existingAttributes?.IsSymbolicLink == true)
                throw new IOException($"远程目标是符号链接，已跳过以避免覆盖链接目标：{remotePath}");
            if (existingAttributes?.IsDirectory == true)
                throw new IOException($"远程目标是目录，不能覆盖为文件：{remotePath}");
            if (existingAttributes is not null)
            {
                backupPath = $"{remotePath}.onlyshell-{Guid.NewGuid():N}.backup";
                await client.RenameFileAsync(remotePath, backupPath, CancellationToken.None);
            }

            try
            {
                await client.RenameFileAsync(temporaryPath, remotePath, CancellationToken.None);
            }
            catch
            {
                if (backupPath is not null && await GetRemoteAttributesOrNullAsync(backupPath) is not null)
                    await client.RenameFileAsync(backupPath, remotePath, CancellationToken.None);
                throw;
            }

            if (backupPath is not null)
                await TryDeleteRemoteFileAsync(backupPath);
        }
        catch
        {
            await TryDeleteRemoteFileAsync(temporaryPath);
            if (backupPath is not null
                && await GetRemoteAttributesOrNullAsync(backupPath) is not null
                && await GetRemoteAttributesOrNullAsync(remotePath) is null)
                await client.RenameFileAsync(backupPath, remotePath, CancellationToken.None);
            throw;
        }
    }

    public async Task DownloadAsync(string remotePath, string localPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var size = (long)(await client.GetAttributesAsync(remotePath, cancellationToken)).Size;
        var temporaryPath = $"{localPath}.onlyshell-{Guid.NewGuid():N}.downloading";
        var report = progress is null ? null : new Progress<DownloadFileProgressReport>(value =>
            progress.Report(size == 0 ? 100 : Math.Clamp(value.TotalBytesDownloaded * 100d / size, 0, 100)));
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true))
                await client.DownloadFileAsync(remotePath, output, report, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, localPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private async Task<SftpFileAttributes?> GetRemoteAttributesOrNullAsync(string path)
    {
        try
        {
            return await client.GetAttributesAsync(path, CancellationToken.None);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }

    private async Task TryDeleteRemoteFileAsync(string path)
    {
        try
        {
            if (await GetRemoteAttributesOrNullAsync(path) is not null)
                await client.DeleteFileAsync(path, CancellationToken.None);
        }
        catch (SftpException) { }
        catch (IOException) { }
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => client.CreateDirectoryAsync(path, cancellationToken);

    public async Task EnsureDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var attributes = await GetRemoteAttributesOrNullAsync(path);
        if (attributes?.IsSymbolicLink == true)
            throw new IOException($"远程目录是符号链接，递归传输不会跟随链接：{path}");
        if (attributes is { IsDirectory: true })
            return;
        if (attributes is not null)
            throw new IOException($"远程路径已存在且不是目录：{path}");
        await client.CreateDirectoryAsync(path, cancellationToken);
    }

    public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
        client.RenameFileAsync(sourcePath, destinationPath, cancellationToken);

    public async Task DeleteAsync(RemoteFileEntry entry, CancellationToken cancellationToken = default)
    {
        if (!entry.IsDirectory)
        {
            try
            {
                await client.DeleteFileAsync(entry.FullPath, cancellationToken);
                return;
            }
            catch (SftpPermissionDeniedException exception)
            {
                throw new IOException($"没有删除文件的权限：{entry.FullPath}", exception);
            }
            catch (SftpPathNotFoundException exception)
            {
                throw new IOException($"远程文件已不存在：{entry.FullPath}", exception);
            }
        }

        var children = new List<string>();
        try
        {
            await foreach (var child in client.ListDirectoryAsync(entry.FullPath, cancellationToken))
            {
                if (child.Name is "." or "..")
                    continue;
                children.Add(child.Name);
            }
        }
        catch (SftpPermissionDeniedException exception)
        {
            throw new IOException($"无法检查目录内容：当前用户没有读取目录的权限。\n{entry.FullPath}", exception);
        }
        catch (SftpPathNotFoundException exception)
        {
            throw new IOException($"远程目录已不存在：{entry.FullPath}", exception);
        }
        catch (SftpException exception)
        {
            throw new IOException($"无法检查目录是否为空：{entry.FullPath}\n服务器返回：{exception.Message}", exception);
        }

        if (children.Count > 0)
        {
            var examples = string.Join("、", children.Take(5));
            var remaining = children.Count > 5 ? $" 等（共 {children.Count} 项）" : $"（共 {children.Count} 项）";
            throw new IOException($"目录不为空，无法删除：{entry.FullPath}\n包含 {examples}{remaining}。请先删除或移动其中的内容。");
        }

        try
        {
            await client.DeleteDirectoryAsync(entry.FullPath, cancellationToken);
        }
        catch (SftpPermissionDeniedException exception)
        {
            throw new IOException($"没有删除目录的权限：{entry.FullPath}", exception);
        }
        catch (SftpPathNotFoundException exception)
        {
            throw new IOException($"远程目录已不存在：{entry.FullPath}", exception);
        }
        catch (SftpException exception)
        {
            throw new IOException($"服务器拒绝删除目录：{entry.FullPath}。目录可能非空或当前用户没有权限。服务器返回：{exception.Message}", exception);
        }
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}
