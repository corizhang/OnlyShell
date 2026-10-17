using Microsoft.Data.Sqlite;
using OnlyShell.Core.Security;

namespace OnlyShell.Infrastructure.Data;

public sealed class SqliteKnownHostRepository(string databasePath) : IKnownHostRepository
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public string? FindFingerprint(string hostName, int port)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Fingerprint FROM KnownHosts WHERE HostName = $hostName AND Port = $port;";
        command.Parameters.AddWithValue("$hostName", hostName);
        command.Parameters.AddWithValue("$port", port);
        return command.ExecuteScalar() as string;
    }

    public void TrustFingerprint(string hostName, int port, string fingerprint)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO KnownHosts (HostName, Port, Fingerprint, TrustedAt)
            VALUES ($hostName, $port, $fingerprint, $trustedAt)
            ON CONFLICT(HostName, Port) DO UPDATE SET Fingerprint = excluded.Fingerprint, TrustedAt = excluded.TrustedAt;
            """;
        command.Parameters.AddWithValue("$hostName", hostName);
        command.Parameters.AddWithValue("$port", port);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$trustedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
}
