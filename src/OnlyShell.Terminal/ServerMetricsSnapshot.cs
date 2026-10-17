namespace OnlyShell.Terminal;

public sealed record ServerMetricsSnapshot(
    string HostName,
    double UptimeSeconds,
    double LoadAverage1,
    double LoadAverage5,
    double LoadAverage15,
    int ProcessCount,
    double? CpuPercent,
    double MemoryUsedMiB,
    double MemoryTotalMiB,
    double MemoryPercent,
    double SwapUsedMiB,
    double SwapTotalMiB,
    double SwapPercent,
    double DiskUsedGiB,
    double DiskTotalGiB,
    double DiskPercent,
    double NetworkReceiveBytesPerSecond,
    double NetworkTransmitBytesPerSecond,
    double SshRoundTripMilliseconds);
