using System.IO;
using System.Globalization;
using System.Diagnostics;
using Renci.SshNet;

namespace OnlyShell.Terminal;

/// <summary>Collects a lightweight Linux host snapshot through the already authenticated SSH session.</summary>
public sealed class LinuxServerMetricsCollector
{
    private static readonly string CommandText =
        "OS_NAME=$(uname -s); printf 'OS|%s\\n' \"$OS_NAME\"; " +
        "if [ \"$OS_NAME\" != 'Linux' ]; then exit 0; fi; " +
        "printf 'HOST|'; cat /proc/sys/kernel/hostname; " +
        "printf 'UPTIME|'; awk '{print $1}' /proc/uptime; " +
        "printf 'LOAD|'; awk '{print $1 \"|\" $2 \"|\" $3}' /proc/loadavg; " +
        "printf 'CPU|'; awk '/^cpu / { for (i=2; i<=NF; i++) printf \"%s%s\", $i, (i==NF ? \"\\n\" : \"|\") }' /proc/stat; " +
        "printf 'MEM|'; awk '/MemTotal:/ {t=$2} /MemAvailable:/ {a=$2} END {printf \"%s|%s\\n\", t, a}' /proc/meminfo; " +
        "printf 'SWAP|'; awk '/SwapTotal:/ {t=$2} /SwapFree:/ {f=$2} END {printf \"%s|%s\\n\", t, f}' /proc/meminfo; " +
        "printf 'DISK|'; df -Pk / | awk 'NR==2 {gsub(/%/, \"\", $5); printf \"%s|%s|%s\\n\", $2, $3, $5}'; " +
        "printf 'NET|'; awk -F'[: ]+' 'NR>2 {rx+=$3; tx+=$11} END {printf \"%.0f|%.0f\\n\", rx, tx}' /proc/net/dev; " +
        "printf 'PROCS|'; set -- /proc/[0-9]*; printf '%s\\n' \"$#\"";

    private long[]? _previousCpu;
    private long? _previousReceive;
    private long? _previousTransmit;
    private DateTimeOffset? _previousSampleAt;

    public async Task<ServerMetricsSnapshot> CollectAsync(ISshClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!client.IsConnected)
            throw new InvalidOperationException("SSH session is disconnected.");

        var startedAt = Stopwatch.GetTimestamp();
        using var command = client.CreateCommand(CommandText);
        command.CommandTimeout = TimeSpan.FromSeconds(8);
        await command.ExecuteAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (command.ExitStatus != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(command.Error) ? "Metrics command failed." : command.Error.Trim());

        var values = Parse(command.Result);
        var roundTripMilliseconds = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        var now = DateTimeOffset.UtcNow;
        var elapsedSeconds = _previousSampleAt is { } previousAt ? (now - previousAt).TotalSeconds : 0;
        var cpuPercent = CalculateCpuPercent(values.Cpu, _previousCpu);
        var receiveRate = CalculateRate(values.ReceiveBytes, _previousReceive, elapsedSeconds);
        var transmitRate = CalculateRate(values.TransmitBytes, _previousTransmit, elapsedSeconds);

        _previousCpu = values.Cpu;
        _previousReceive = values.ReceiveBytes;
        _previousTransmit = values.TransmitBytes;
        _previousSampleAt = now;

        var memoryTotal = values.MemoryTotalKiB / 1024d;
        var memoryUsed = Math.Max(0, (values.MemoryTotalKiB - values.MemoryAvailableKiB) / 1024d);
        var diskTotal = values.DiskTotalKiB / 1024d / 1024d;
        var diskUsed = values.DiskUsedKiB / 1024d / 1024d;
        return new ServerMetricsSnapshot(
            values.HostName,
            values.UptimeSeconds,
            values.Load1,
            values.Load5,
            values.Load15,
            values.ProcessCount,
            cpuPercent,
            memoryUsed,
            memoryTotal,
            memoryTotal <= 0 ? 0 : memoryUsed / memoryTotal * 100,
            values.SwapTotalKiB <= 0 ? 0 : Math.Max(0, values.SwapTotalKiB - values.SwapFreeKiB) / 1024d,
            values.SwapTotalKiB / 1024d,
            values.SwapTotalKiB <= 0 ? 0 : Math.Clamp((values.SwapTotalKiB - values.SwapFreeKiB) * 100d / values.SwapTotalKiB, 0, 100),
            diskUsed,
            diskTotal,
            values.DiskPercent,
            receiveRate,
            transmitRate,
            roundTripMilliseconds);
    }

    private static (string HostName, double UptimeSeconds, double Load1, double Load5, double Load15, int ProcessCount, long[] Cpu, long MemoryTotalKiB, long MemoryAvailableKiB, long SwapTotalKiB, long SwapFreeKiB, long DiskTotalKiB, long DiskUsedKiB, double DiskPercent, long ReceiveBytes, long TransmitBytes) Parse(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('|');
            if (separator > 0)
                values[line[..separator]] = line[(separator + 1)..].Trim();
        }

        if (!values.TryGetValue("OS", out var os) || !string.Equals(os, "Linux", StringComparison.OrdinalIgnoreCase))
            throw new PlatformNotSupportedException("首版服务器监控目前支持 Linux 主机。");

        var cpu = ParseLongs(Required(values, "CPU"));
        var memory = ParseLongs(Required(values, "MEM"));
        var swap = ParseLongs(Required(values, "SWAP"));
        var load = Required(values, "LOAD").Split('|');
        var disk = Required(values, "DISK").Split('|');
        var network = ParseLongs(Required(values, "NET"));
        if (cpu.Length < 4 || memory.Length != 2 || swap.Length != 2 || load.Length != 3 || disk.Length != 3 || network.Length != 2)
            throw new InvalidDataException("Linux returned an incomplete metrics snapshot.");

        return (
            Required(values, "HOST"),
            ParseDouble(Required(values, "UPTIME")),
            ParseDouble(load[0]), ParseDouble(load[1]), ParseDouble(load[2]),
            checked((int)ParseLong(Required(values, "PROCS"))),
            cpu, memory[0], memory[1], swap[0], swap[1],
            ParseLong(disk[0]), ParseLong(disk[1]), ParseDouble(disk[2]), network[0], network[1]);
    }

    private static double? CalculateCpuPercent(long[] current, long[]? previous)
    {
        if (previous is null || previous.Length != current.Length)
            return null;

        var totalDelta = current.Take(8).Zip(previous.Take(8), (now, before) => Math.Max(0, now - before)).Sum();
        var idleDelta = Math.Max(0, current[3] - previous[3])
            + (current.Length > 4 ? Math.Max(0, current[4] - previous[4]) : 0);
        return totalDelta <= 0 ? 0 : Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100);
    }

    private static double CalculateRate(long current, long? previous, double elapsedSeconds) =>
        previous is null || elapsedSeconds <= 0 || current < previous ? 0 : (current - previous.Value) / elapsedSeconds;

    private static string Required(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : throw new InvalidDataException($"Linux metrics field '{key}' is missing.");

    private static long[] ParseLongs(string value) => value.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(ParseLong).ToArray();

    private static long ParseLong(string value) => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static double ParseDouble(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
}
