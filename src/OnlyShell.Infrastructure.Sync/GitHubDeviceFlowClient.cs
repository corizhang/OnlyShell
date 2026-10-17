using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace OnlyShell.Infrastructure.Sync;

public sealed class GitHubDeviceFlowClient(HttpClient httpClient)
{
    private static readonly Uri DeviceCodeEndpoint = new("https://github.com/login/device/code");
    private static readonly Uri AccessTokenEndpoint = new("https://github.com/login/oauth/access_token");

    public async Task<DeviceAuthorization> BeginAsync(string clientId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        using var request = new HttpRequestMessage(HttpMethod.Post, DeviceCodeEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"] = "offline_access"
            })
        };
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "GitHub 无法发起设备授权", cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<DeviceCodeResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("GitHub 返回了无效的设备授权数据。");
        return new DeviceAuthorization(
            result.DeviceCode,
            result.UserCode,
            result.VerificationUri,
            TimeSpan.FromSeconds(result.ExpiresIn),
            TimeSpan.FromSeconds(Math.Max(result.Interval, 5)));
    }

    public async Task<GitHubUserToken> WaitForTokenAsync(
        string clientId,
        DeviceAuthorization authorization,
        IProgress<TimeSpan>? remaining = null,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + authorization.ExpiresIn;
        var interval = authorization.PollInterval;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            remaining?.Report(deadline - DateTimeOffset.UtcNow);
            await Task.Delay(interval, cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Post, AccessTokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["device_code"] = authorization.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                })
            };
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await httpClient.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, "GitHub 设备授权请求失败", cancellationToken);
            var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken)
                ?? throw new InvalidDataException("GitHub 返回了无效的令牌数据。");

            switch (tokenResponse.Error)
            {
                case null:
                    if (string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
                        throw new InvalidDataException("GitHub 未返回访问令牌。");
                    return ToToken(tokenResponse);
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "access_denied":
                    throw new OperationCanceledException("用户拒绝了 GitHub 授权。");
                case "expired_token":
                    throw new TimeoutException("GitHub 设备授权码已过期，请重新登录。");
                default:
                    throw new InvalidOperationException($"GitHub 设备授权失败：{tokenResponse.ErrorDescription ?? tokenResponse.Error}");
            }
        }

        throw new TimeoutException("GitHub 设备授权等待超时，请重新登录。");
    }

    public async Task<GitHubUserToken> RefreshAsync(string clientId, string refreshToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AccessTokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken
            })
        };
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "GitHub 令牌续期失败", cancellationToken);
        var result = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("GitHub 返回了无效的续期数据。");
        if (result.Error is not null)
            throw new InvalidOperationException($"GitHub 令牌续期失败：{result.ErrorDescription ?? result.Error}");
        return ToToken(result);
    }

    private static GitHubUserToken ToToken(TokenResponse response) => new(
        response.AccessToken!,
        response.RefreshToken,
        response.ExpiresIn is { } expires ? DateTimeOffset.UtcNow.AddSeconds(expires) : null,
        response.RefreshTokenExpiresIn is { } refreshExpires ? DateTimeOffset.UtcNow.AddSeconds(refreshExpires) : null);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string message, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"{message}（HTTP {(int)response.StatusCode}）：{detail}");
    }

    private sealed record DeviceCodeResponse(
        [property: JsonPropertyName("device_code")] string DeviceCode,
        [property: JsonPropertyName("user_code")] string UserCode,
        [property: JsonPropertyName("verification_uri")] string VerificationUri,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("interval")] int Interval);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("refresh_token_expires_in")] int? RefreshTokenExpiresIn,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);
}

public sealed record DeviceAuthorization(string DeviceCode, string UserCode, string VerificationUri, TimeSpan ExpiresIn, TimeSpan PollInterval);
public sealed record GitHubUserToken(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt, DateTimeOffset? RefreshTokenExpiresAt);
