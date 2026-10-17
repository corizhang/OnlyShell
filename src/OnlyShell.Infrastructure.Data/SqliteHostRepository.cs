using Microsoft.Data.Sqlite;
using OnlyShell.Core.Hosts;

namespace OnlyShell.Infrastructure.Data;

public sealed class SqliteHostRepository(string databasePath) : IHostRepository
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS Hosts (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL,
                HostName TEXT NOT NULL,
                Port INTEGER NOT NULL,
                UserName TEXT NOT NULL,
                GroupName TEXT NULL,
                CredentialReference TEXT NULL,
                LastConnectedAt TEXT NULL,
                IsFavorite INTEGER NOT NULL DEFAULT 0,
                Note TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Hosts_GroupName_Name ON Hosts(GroupName, Name);
            CREATE TABLE IF NOT EXISTS KnownHosts (
                HostName TEXT NOT NULL,
                Port INTEGER NOT NULL,
                Fingerprint TEXT NOT NULL,
                TrustedAt TEXT NOT NULL,
                PRIMARY KEY (HostName, Port)
            );
            PRAGMA user_version = 3;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var tableInfo = connection.CreateCommand())
        {
            tableInfo.CommandText = "PRAGMA table_info(Hosts);";
            await using var reader = await tableInfo.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                columns.Add(reader.GetString(1));
        }

        if (!columns.Contains("AuthenticationMethod"))
        {
            await using var migrate = connection.CreateCommand();
            migrate.CommandText = "ALTER TABLE Hosts ADD COLUMN AuthenticationMethod TEXT NOT NULL DEFAULT 'Password';";
            await migrate.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!columns.Contains("PrivateKeyPath"))
        {
            await using var migrate = connection.CreateCommand();
            migrate.CommandText = "ALTER TABLE Hosts ADD COLUMN PrivateKeyPath TEXT NULL;";
            await migrate.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version = 3;";
            await version.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<HostProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        var profiles = new List<HostProfile>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, HostName, Port, UserName, GroupName, CredentialReference, LastConnectedAt, IsFavorite, Note, AuthenticationMethod, PrivateKeyPath
            FROM Hosts
            ORDER BY COALESCE(GroupName, ''), Name COLLATE NOCASE;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            profiles.Add(new HostProfile(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)),
                reader.GetInt32(8) != 0,
                reader.IsDBNull(9) ? null : reader.GetString(9),
                Enum.TryParse<SshAuthenticationMethod>(reader.GetString(10), true, out var authenticationMethod)
                    ? authenticationMethod
                    : SshAuthenticationMethod.Password,
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return profiles;
    }

    public async Task SaveAsync(HostProfile profile, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Hosts (Id, Name, HostName, Port, UserName, GroupName, CredentialReference, LastConnectedAt, IsFavorite, Note, AuthenticationMethod, PrivateKeyPath)
            VALUES ($id, $name, $hostName, $port, $userName, $groupName, $credentialReference, $lastConnectedAt, $isFavorite, $note, $authenticationMethod, $privateKeyPath)
            ON CONFLICT(Id) DO UPDATE SET
                Name = excluded.Name,
                HostName = excluded.HostName,
                Port = excluded.Port,
                UserName = excluded.UserName,
                GroupName = excluded.GroupName,
                CredentialReference = excluded.CredentialReference,
                LastConnectedAt = excluded.LastConnectedAt,
                IsFavorite = excluded.IsFavorite,
                Note = excluded.Note,
                AuthenticationMethod = excluded.AuthenticationMethod,
                PrivateKeyPath = excluded.PrivateKeyPath;
            """;
        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddWithValue("$hostName", profile.HostName);
        command.Parameters.AddWithValue("$port", profile.Port);
        command.Parameters.AddWithValue("$userName", profile.UserName);
        command.Parameters.AddWithValue("$groupName", (object?)profile.Group ?? DBNull.Value);
        command.Parameters.AddWithValue("$credentialReference", (object?)profile.CredentialReference ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastConnectedAt", (object?)profile.LastConnectedAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$isFavorite", profile.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$note", (object?)profile.Note ?? DBNull.Value);
        command.Parameters.AddWithValue("$authenticationMethod", profile.AuthenticationMethod.ToString());
        command.Parameters.AddWithValue("$privateKeyPath", (object?)profile.PrivateKeyPath ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Hosts WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
