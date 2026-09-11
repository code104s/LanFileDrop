using Microsoft.Data.Sqlite;

namespace LanFileDrop.Core;

public sealed class HistoryRepository
{
    private readonly string _connectionString;

    public HistoryRepository(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS transfers (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                file_name TEXT NOT NULL,
                size INTEGER NOT NULL,
                peer_name TEXT NOT NULL,
                peer_address TEXT NOT NULL,
                direction INTEGER NOT NULL,
                status INTEGER NOT NULL,
                sha256 TEXT NULL,
                started_at TEXT NOT NULL,
                completed_at TEXT NULL,
                error TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_transfers_started_at ON transfers(started_at DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> AddAsync(TransferRecord record, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transfers(file_name,size,peer_name,peer_address,direction,status,sha256,started_at,completed_at,error)
            VALUES($file,$size,$peer,$address,$direction,$status,$hash,$started,$completed,$error);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$file", record.FileName);
        command.Parameters.AddWithValue("$size", record.Size);
        command.Parameters.AddWithValue("$peer", record.PeerName);
        command.Parameters.AddWithValue("$address", record.PeerAddress);
        command.Parameters.AddWithValue("$direction", (int)record.Direction);
        command.Parameters.AddWithValue("$status", (int)record.Status);
        command.Parameters.AddWithValue("$hash", (object?)record.Sha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", record.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$completed", record.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)record.Error ?? DBNull.Value);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    public async Task<IReadOnlyList<TransferRecord>> QueryAsync(
        DateTimeOffset? from = null, TransferDirection? direction = null, long? minimumSize = null,
        CancellationToken cancellationToken = default)
    {
        var result = new List<TransferRecord>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,file_name,size,peer_name,peer_address,direction,status,sha256,started_at,completed_at,error
            FROM transfers
            WHERE ($from IS NULL OR started_at >= $from)
              AND ($direction IS NULL OR direction = $direction)
              AND ($size IS NULL OR size >= $size)
            ORDER BY started_at DESC LIMIT 500;
            """;
        command.Parameters.AddWithValue("$from", from?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$direction", direction is null ? DBNull.Value : (int)direction.Value);
        command.Parameters.AddWithValue("$size", minimumSize ?? (object)DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TransferRecord(
                reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4),
                (TransferDirection)reader.GetInt32(5), (TransferStatus)reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), DateTimeOffset.Parse(reader.GetString(8)),
                reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }
        return result;
    }
}
