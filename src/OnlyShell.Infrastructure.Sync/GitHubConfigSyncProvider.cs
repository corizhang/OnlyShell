using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OnlyShell.Core.Sync;

namespace OnlyShell.Infrastructure.Sync;

/// <summary>Stores a single encrypted OnlyShell document in a user-selected GitHub repository.</summary>
public sealed class GitHubConfigSyncProvider(
    HttpClient httpClient,
    string owner,
    string repository,
    string branch,
    string accessToken) : IConfigSyncProvider
{
    private const string ConfigPath = ".onlyshell/config.v1.json";
    private static readonly Uri ApiBaseUri = new("https://api.github.com/");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private bool? _repositoryIsPrivate;

    public async Task<SyncSnapshot?> DownloadAsync(CancellationToken cancellationToken = default)
    {
        ValidateOptions();
        await EnsurePrivateRepositoryAsync(cancellationToken);
        using var response = await SendApiAsync(
            HttpMethod.Get,
            $"repos/{Escape(owner)}/{Escape(repository)}/contents/{EscapePath(ConfigPath)}?ref={Escape(branch)}",
            cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, "下载同步配置失败", cancellationToken);

        var file = await response.Content.ReadFromJsonAsync<ContentFileResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("GitHub 返回了空的同步文件数据。");
        if (!string.Equals(file.Type, "file", StringComparison.Ordinal))
            throw new InvalidDataException("GitHub 同步路径不是文件。");
        var encoded = file.Content.Replace("\n", string.Empty, StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal);
        var encryptedPayload = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        return new SyncSnapshot(1, encryptedPayload, file.Sha, DateTimeOffset.UtcNow);
    }

    public async Task UploadAsync(SyncSnapshot snapshot, string? expectedVersion, CancellationToken cancellationToken = default)
    {
        ValidateOptions();
        await EnsurePrivateRepositoryAsync(cancellationToken);

        var current = await GetCurrentFileAsync(cancellationToken);
        if (!string.Equals(current?.Sha, expectedVersion, StringComparison.Ordinal))
            throw new SyncConflictException("GitHub 上的配置版本已变化。请先下载并检查远端配置，再决定如何同步。", expectedVersion, current?.Sha);

        var body = new PutContentsRequest(
            "OnlyShell encrypted configuration sync",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(snapshot.Payload)),
            current?.Sha,
            branch);
        using var response = await SendApiAsync(
            HttpMethod.Put,
            $"repos/{Escape(owner)}/{Escape(repository)}/contents/{EscapePath(ConfigPath)}",
            body,
            cancellationToken);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
            throw new SyncConflictException("GitHub 拒绝了并发更新。请重新读取远端版本后再操作。", expectedVersion, null);
        await EnsureSuccessAsync(response, "上传同步配置失败", cancellationToken);
    }

    private async Task<ContentFileResponse?> GetCurrentFileAsync(CancellationToken cancellationToken)
    {
        using var response = await SendApiAsync(
            HttpMethod.Get,
            $"repos/{Escape(owner)}/{Escape(repository)}/contents/{EscapePath(ConfigPath)}?ref={Escape(branch)}",
            cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, "读取 GitHub 同步版本失败", cancellationToken);
        return await response.Content.ReadFromJsonAsync<ContentFileResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("GitHub 返回了空的同步文件数据。");
    }

    private async Task EnsurePrivateRepositoryAsync(CancellationToken cancellationToken)
    {
        if (_repositoryIsPrivate == true)
            return;
        using var response = await SendApiAsync(
            HttpMethod.Get,
            $"repos/{Escape(owner)}/{Escape(repository)}",
            cancellationToken: cancellationToken);
        await EnsureSuccessAsync(response, "无法读取指定 GitHub 仓库。请检查仓库名称、授权和应用的 Repository contents 权限。", cancellationToken);
        var repositoryInfo = await response.Content.ReadFromJsonAsync<RepositoryResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("GitHub 返回了无效仓库数据。");
        if (!repositoryInfo.Private)
            throw new InvalidOperationException("OnlyShell 只允许将配置写入 GitHub 私有仓库。");
        _repositoryIsPrivate = true;
    }

    private async Task<HttpResponseMessage> SendApiAsync(
        HttpMethod method,
        string relativeUrl,
        object? body = null,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, new Uri(ApiBaseUri, relativeUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("OnlyShell/1.0");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Escape));

    private void ValidateOptions()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string message, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"{message}（HTTP {(int)response.StatusCode}）：{detail}", null, response.StatusCode);
    }

    private sealed record ContentFileResponse(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("sha")] string Sha,
        [property: JsonPropertyName("content")] string Content);

    private sealed record RepositoryResponse([property: JsonPropertyName("private")] bool Private);

    private sealed record PutContentsRequest(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("sha")] string? Sha,
        [property: JsonPropertyName("branch")] string Branch);
}

public sealed class SyncConflictException(string message, string? expectedVersion, string? currentVersion)
    : InvalidOperationException(message)
{
    public string? ExpectedVersion { get; } = expectedVersion;
    public string? CurrentVersion { get; } = currentVersion;
}
