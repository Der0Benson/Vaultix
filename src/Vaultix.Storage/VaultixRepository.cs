using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Vaultix.Shared;

namespace Vaultix.Storage;

public sealed class VaultixRepository
{
    private readonly string _connectionString;
    private readonly ContentAddressedObjectStore _objects;

    public VaultixRepository(string rootPath)
    {
        var canonicalRoot = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(canonicalRoot);
        Directory.CreateDirectory(Path.Combine(canonicalRoot, "database"));
        Directory.CreateDirectory(Path.Combine(canonicalRoot, "metadata"));
        Directory.CreateDirectory(Path.Combine(canonicalRoot, "logs"));
        _objects = new ContentAddressedObjectStore(canonicalRoot);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(canonicalRoot, "database", "vaultix.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
    }

    public string RootPath => _objects.RootPath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS devices (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                secret_hash BLOB NOT NULL,
                created_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS storage_objects (
                hash TEXT PRIMARY KEY,
                size INTEGER NOT NULL,
                created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS snapshots (
                id TEXT PRIMARY KEY,
                device_id TEXT NOT NULL REFERENCES devices(id),
                name TEXT NOT NULL,
                source_root TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                file_count INTEGER NOT NULL,
                total_bytes INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS snapshot_entries (
                snapshot_id TEXT NOT NULL REFERENCES snapshots(id),
                relative_path TEXT NOT NULL,
                object_hash TEXT NOT NULL REFERENCES storage_objects(hash),
                size INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                last_write_utc TEXT NOT NULL,
                attributes INTEGER NOT NULL,
                PRIMARY KEY(snapshot_id, relative_path)
            );
            CREATE INDEX IF NOT EXISTS ix_snapshots_device_created ON snapshots(device_id, created_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_entries_object ON snapshot_entries(object_hash);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PairDeviceResponse> PairDeviceAsync(string deviceName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceName) || deviceName.Length > 128)
        {
            throw new ArgumentException("Der Gerätename ist ungültig.", nameof(deviceName));
        }

        var id = Guid.NewGuid();
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var secret = Convert.ToBase64String(secretBytes);
        var secretHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        var now = DateTimeOffset.UtcNow;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO devices(id,name,secret_hash,created_utc,last_seen_utc) VALUES($id,$name,$secret,$created,$seen);";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$name", deviceName.Trim());
        command.Parameters.AddWithValue("$secret", secretHash);
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        command.Parameters.AddWithValue("$seen", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(secretBytes);
        return new PairDeviceResponse(id, secret);
    }

    public async Task<bool> AuthenticateAsync(Guid deviceId, string secret, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return false;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT secret_hash FROM devices WHERE id=$id;";
        command.Parameters.AddWithValue("$id", deviceId.ToString("D"));
        var stored = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[];
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return stored is not null && stored.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(stored, supplied);
    }

    public bool ObjectExists(string hash) => _objects.Exists(hash);

    public async Task<long> StoreObjectAsync(string hash, Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        var normalized = ContentAddressedObjectStore.NormalizeHash(hash);
        var size = await _objects.PutAsync(normalized, stream, maxBytes, cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO storage_objects(hash,size,created_utc) VALUES($hash,$size,$created);";
        command.Parameters.AddWithValue("$hash", normalized);
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return size;
    }

    public async Task<SnapshotResponse> CreateSnapshotAsync(Guid deviceId, CreateSnapshotRequest request, CancellationToken cancellationToken)
    {
        if (request.Entries.Count > 2_000_000)
        {
            throw new InvalidDataException("Der Snapshot enthält zu viele Einträge.");
        }

        var entries = request.Entries.Select(ValidateEntry).ToArray();
        if (entries.Select(entry => entry.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length)
        {
            throw new InvalidDataException("Der Snapshot enthält doppelte Dateipfade.");
        }

        var snapshot = new SnapshotResponse(
            Guid.NewGuid(),
            string.IsNullOrWhiteSpace(request.Name) ? "Snapshot" : request.Name.Trim()[..Math.Min(request.Name.Trim().Length, 128)],
            request.SourceRoot,
            DateTimeOffset.UtcNow,
            entries.Length,
            entries.Sum(entry => entry.Size));

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            await using var exists = connection.CreateCommand();
            exists.Transaction = (SqliteTransaction)transaction;
            exists.CommandText = "SELECT 1 FROM storage_objects WHERE hash=$hash;";
            exists.Parameters.AddWithValue("$hash", entry.ObjectHash);
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            {
                throw new InvalidDataException($"Das referenzierte Storage-Objekt fehlt: {entry.ObjectHash}");
            }
        }

        await using (var insertSnapshot = connection.CreateCommand())
        {
            insertSnapshot.Transaction = (SqliteTransaction)transaction;
            insertSnapshot.CommandText = "INSERT INTO snapshots(id,device_id,name,source_root,created_utc,file_count,total_bytes) VALUES($id,$device,$name,$root,$created,$count,$bytes);";
            insertSnapshot.Parameters.AddWithValue("$id", snapshot.Id.ToString("D"));
            insertSnapshot.Parameters.AddWithValue("$device", deviceId.ToString("D"));
            insertSnapshot.Parameters.AddWithValue("$name", snapshot.Name);
            insertSnapshot.Parameters.AddWithValue("$root", snapshot.SourceRoot);
            insertSnapshot.Parameters.AddWithValue("$created", snapshot.CreatedUtc.ToString("O"));
            insertSnapshot.Parameters.AddWithValue("$count", snapshot.FileCount);
            insertSnapshot.Parameters.AddWithValue("$bytes", snapshot.TotalBytes);
            await insertSnapshot.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var entry in entries)
        {
            await using var insertEntry = connection.CreateCommand();
            insertEntry.Transaction = (SqliteTransaction)transaction;
            insertEntry.CommandText = "INSERT INTO snapshot_entries(snapshot_id,relative_path,object_hash,size,created_utc,last_write_utc,attributes) VALUES($snapshot,$path,$hash,$size,$created,$modified,$attributes);";
            insertEntry.Parameters.AddWithValue("$snapshot", snapshot.Id.ToString("D"));
            insertEntry.Parameters.AddWithValue("$path", entry.RelativePath);
            insertEntry.Parameters.AddWithValue("$hash", entry.ObjectHash);
            insertEntry.Parameters.AddWithValue("$size", entry.Size);
            insertEntry.Parameters.AddWithValue("$created", entry.CreatedUtc.ToString("O"));
            insertEntry.Parameters.AddWithValue("$modified", entry.LastWriteUtc.ToString("O"));
            insertEntry.Parameters.AddWithValue("$attributes", entry.Attributes);
            await insertEntry.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    public async Task<IReadOnlyCollection<SnapshotResponse>> ListSnapshotsAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var snapshots = new List<SnapshotResponse>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,source_root,created_utc,file_count,total_bytes FROM snapshots WHERE device_id=$device ORDER BY created_utc DESC;";
        command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            snapshots.Add(new SnapshotResponse(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), reader.GetInt32(4), reader.GetInt64(5)));
        }

        return snapshots;
    }

    public async Task<SnapshotDetailsResponse?> GetSnapshotAsync(Guid deviceId, Guid snapshotId, CancellationToken cancellationToken)
    {
        SnapshotResponse? snapshot = null;
        var entries = new List<SnapshotEntryDto>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var snapshotCommand = connection.CreateCommand())
        {
            snapshotCommand.CommandText = "SELECT id,name,source_root,created_utc,file_count,total_bytes FROM snapshots WHERE id=$id AND device_id=$device;";
            snapshotCommand.Parameters.AddWithValue("$id", snapshotId.ToString("D"));
            snapshotCommand.Parameters.AddWithValue("$device", deviceId.ToString("D"));
            await using var reader = await snapshotCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            snapshot = new SnapshotResponse(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), reader.GetInt32(4), reader.GetInt64(5));
        }

        await using (var entriesCommand = connection.CreateCommand())
        {
            entriesCommand.CommandText = "SELECT relative_path,object_hash,size,created_utc,last_write_utc,attributes FROM snapshot_entries WHERE snapshot_id=$id ORDER BY relative_path;";
            entriesCommand.Parameters.AddWithValue("$id", snapshotId.ToString("D"));
            await using var reader = await entriesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(new SnapshotEntryDto(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                    DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), reader.GetInt32(5)));
            }
        }

        return new SnapshotDetailsResponse(snapshot, entries);
    }

    public async Task<(FileStream Stream, SnapshotEntryDto Entry)?> OpenSnapshotFileAsync(
        Guid deviceId, Guid snapshotId, string relativePath, CancellationToken cancellationToken)
    {
        var safePath = ValidateRelativePath(relativePath);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.relative_path,e.object_hash,e.size,e.created_utc,e.last_write_utc,e.attributes
            FROM snapshot_entries e
            JOIN snapshots s ON s.id=e.snapshot_id
            WHERE e.snapshot_id=$snapshot AND s.device_id=$device AND e.relative_path=$path;
            """;
        command.Parameters.AddWithValue("$snapshot", snapshotId.ToString("D"));
        command.Parameters.AddWithValue("$device", deviceId.ToString("D"));
        command.Parameters.AddWithValue("$path", safePath);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var entry = new SnapshotEntryDto(
            reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), reader.GetInt32(5));
        return (_objects.OpenRead(entry.ObjectHash), entry);
    }

    public long GetAvailableBytes() => new DriveInfo(Path.GetPathRoot(RootPath)!).AvailableFreeSpace;

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static SnapshotEntryDto ValidateEntry(SnapshotEntryDto entry)
    {
        if (entry.Size < 0)
        {
            throw new InvalidDataException("Eine Dateigröße darf nicht negativ sein.");
        }

        return entry with
        {
            RelativePath = ValidateRelativePath(entry.RelativePath),
            ObjectHash = ContentAddressedObjectStore.NormalizeHash(entry.ObjectHash)
        };
    }

    private static string ValidateRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || relativePath.Contains('\0'))
        {
            throw new InvalidDataException("Der relative Dateipfad ist ungültig.");
        }

        var normalized = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (normalized.Split(Path.DirectorySeparatorChar).Any(part => part is ".." or "." || part.Length == 0))
        {
            throw new InvalidDataException("Der relative Dateipfad enthält unzulässige Segmente.");
        }

        return normalized;
    }
}
