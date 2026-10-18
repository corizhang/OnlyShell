using OnlyShell.Core.Files;
using OnlyShell.Core.Hosts;
using OnlyShell.Core.Security;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using System.Security.Cryptography;

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
        var temporaryPath = $"{remotePath}.onlyshell-resume.uploading";
        string? backupPath = null;
        try
        {
            var temporaryAttributes = await GetRemoteAttributesOrNullAsync(temporaryPath);
            if (temporaryAttributes is { IsDirectory: true } or { IsSymbolicLink: true })
                throw new IOException($"上传暂存目标不是普通文件，已停止以避免覆盖：{temporaryPath}");
            var offset = 0L;
            if (temporaryAttributes is not null && temporaryAttributes.Size > 0 && temporaryAttributes.Size <= totalBytes)
            {
                var partialSize = (long)temporaryAttributes.Size;
                input.Position = 0;
                await using var remotePartial = client.OpenRead(temporaryPath);
                var localPrefixHash = await HashPrefixAsync(input, partialSize, cancellationToken);
                var remotePrefixHash = await HashPrefixAsync(remotePartial, partialSize, cancellationToken);
                if (CryptographicOperations.FixedTimeEquals(localPrefixHash, remotePrefixHash))
                    offset = partialSize;
            }
            if (offset == 0 && temporaryAttributes is not null)
                await client.DeleteFileAsync(temporaryPath, cancellationToken);

            input.Position = offset;
            await using (var remoteOutput = client.Open(temporaryPath,
                offset == 0 ? FileMode.Create : FileMode.Open, FileAccess.Write))
            {
                remoteOutput.Position = offset;
                var buffer = new byte[128 * 1024];
                var copied = offset;
                progress?.Report(totalBytes == 0 ? 100 : Math.Clamp(copied * 100d / totalBytes, 0, 100));
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                        break;
                    await remoteOutput.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    copied += read;
                    progress?.Report(totalBytes == 0 ? 100 : Math.Clamp(copied * 100d / totalBytes, 0, 100));
                }
                await remoteOutput.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();

            var uploadedAttributes = await client.GetAttributesAsync(temporaryPath, cancellationToken);
            if ((long)uploadedAttributes.Size != totalBytes)
                throw new IOException($"上传完整性校验失败：本地 {totalBytes} 字节，远程临时文件 {uploadedAttributes.Size} 字节。原目标文件未替换。 ");
            input.Position = 0;
            var localHash = await SHA256.HashDataAsync(input, cancellationToken);
            await using (var remoteFile = client.OpenRead(temporaryPath))
            {
                var remoteHash = await SHA256.HashDataAsync(remoteFile, cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(localHash, remoteHash))
                    throw new IOException($"上传 SHA-256 校验失败：本地 {Convert.ToHexString(localHash)}，远程临时文件 {Convert.ToHexString(remoteHash)}。原目标文件未替换。");
            }

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
            if (backupPath is not null
                && await GetRemoteAttributesOrNullAsync(backupPath) is not null
                && await GetRemoteAttributesOrNullAsync(remotePath) is null)
                await client.RenameFileAsync(backupPath, remotePath, CancellationToken.None);
            throw;
        }
    }

    public async Task DownloadAsync(string remotePath, string localPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var attributes = await client.GetAttributesAsync(remotePath, cancellationToken);
        if (attributes.IsDirectory || attributes.IsSymbolicLink)
            throw new IOException($"只支持下载普通文件：{remotePath}");
        var size = (long)attributes.Size;
        var temporaryPath = $"{localPath}.onlyshell-resume.downloading";
        var offset = 0L;
        await using var remoteFile = client.OpenRead(remotePath);
        if (File.Exists(temporaryPath))
        {
            var partialSize = new FileInfo(temporaryPath).Length;
            if (partialSize <= size && partialSize > 0)
            {
                bool prefixMatches;
                await using (var localPartial = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true))
                {
                    var remotePrefixHash = await HashPrefixAsync(remoteFile, partialSize, cancellationToken);
                    var localPrefixHash = await HashPrefixAsync(localPartial, partialSize, cancellationToken);
                    prefixMatches = CryptographicOperations.FixedTimeEquals(remotePrefixHash, localPrefixHash);
                }
                if (prefixMatches)
                    offset = partialSize;
                else
                    File.Delete(temporaryPath);
            }
            else
                File.Delete(temporaryPath);
        }

        remoteFile.Position = offset;
        await using var output = new FileStream(temporaryPath, offset == 0 ? FileMode.Create : FileMode.Append,
            FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        var buffer = new byte[128 * 1024];
        var copied = offset;
        progress?.Report(size == 0 ? 100 : Math.Clamp(copied * 100d / size, 0, 100));
        while (true)
        {
            var read = await remoteFile.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            progress?.Report(size == 0 ? 100 : Math.Clamp(copied * 100d / size, 0, 100));
        }
        await output.FlushAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (new FileInfo(temporaryPath).Length != size)
            throw new IOException($"下载完整性校验失败：远程 {size} 字节，本地临时文件 {new FileInfo(temporaryPath).Length} 字节。可重试以续传；原目标文件未替换。");
        remoteFile.Position = 0;
        byte[] remoteHash;
        byte[] localHash;
        await using (var localFile = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true))
        {
            remoteHash = await SHA256.HashDataAsync(remoteFile, cancellationToken);
            localHash = await SHA256.HashDataAsync(localFile, cancellationToken);
        }
        if (!CryptographicOperations.FixedTimeEquals(remoteHash, localHash))
            throw new IOException($"下载 SHA-256 校验失败：远程 {Convert.ToHexString(remoteHash)}，本地临时文件 {Convert.ToHexString(localHash)}。原目标文件未替换。");
        File.Move(temporaryPath, localPath, overwrite: true);
    }

    private static async Task<byte[]> HashPrefixAsync(Stream stream, long length, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("无法校验暂存文件前缀；远端文件可能已发生变化。");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return hash.GetHashAndReset();
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
