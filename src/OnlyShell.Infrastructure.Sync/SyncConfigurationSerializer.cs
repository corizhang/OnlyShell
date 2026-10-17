using System.Text.Json;
using OnlyShell.Core.Hosts;
using OnlyShell.Core.Sync;

namespace OnlyShell.Infrastructure.Sync;

public static class SyncConfigurationSerializer
{
    private const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static string Serialize(IEnumerable<HostProfile> hosts)
    {
        var entries = hosts.Select(host => new SyncedHostProfile(
            host.Id,
            host.Name,
            host.HostName,
            host.Port,
            host.UserName,
            host.Group,
            host.LastConnectedAt,
            host.IsFavorite,
            host.Note,
            host.AuthenticationMethod)).ToArray();
        return JsonSerializer.Serialize(new SyncConfiguration(CurrentSchemaVersion, entries), JsonOptions);
    }

    public static SyncConfiguration Deserialize(string payload)
    {
        var configuration = JsonSerializer.Deserialize<SyncConfiguration>(payload, JsonOptions)
            ?? throw new InvalidDataException("同步配置为空或格式无效。");
        if (configuration.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"不支持的同步配置版本：{configuration.SchemaVersion}。");
        if (configuration.Hosts.Select(host => host.Id).Distinct().Count() != configuration.Hosts.Count)
            throw new InvalidDataException("同步配置包含重复的主机 ID。");
        if (configuration.Hosts.Any(host => host.Port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(host.Name)
            || string.IsNullOrWhiteSpace(host.HostName)
            || string.IsNullOrWhiteSpace(host.UserName)))
            throw new InvalidDataException("同步配置包含无效主机资料。");
        return configuration;
    }

    /// <summary>Applies cloud fields while retaining each device's local password reference and key path.</summary>
    public static IReadOnlyList<HostProfile> MergeDeviceSecrets(
        SyncConfiguration remote,
        IEnumerable<HostProfile> localHosts)
    {
        var localById = localHosts.ToDictionary(host => host.Id);
        return remote.Hosts.Select(host =>
        {
            localById.TryGetValue(host.Id, out var local);
            return new HostProfile(
                host.Id,
                host.Name,
                host.HostName,
                host.Port,
                host.UserName,
                host.Group,
                local?.CredentialReference,
                host.LastConnectedAt,
                host.IsFavorite,
                host.Note,
                host.AuthenticationMethod,
                local?.PrivateKeyPath);
        }).ToArray();
    }
}
