using Microsoft.Data.Sqlite;
using System.Globalization;
using Vaultix.Core;
using Vaultix.Shared;

namespace Vaultix.Service;

public sealed record QueueJob(
    long Id,
    Guid RunId,
    string RootPath,
    string FilePath,
    string RelativePath,
    BackupQueueState State,
    int RetryCount,
    long Size,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastWriteUtc,
    int Attributes);

public sealed record QueueStatistics(int Pending, int Failed, long ProtectedFiles, long ProtectedBytes);

public sealed record ReadyRun(Guid Id, string RootPath);

public sealed class BackupQueueStore(VaultixPaths paths)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = paths.QueueDatabase,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        Pooling = false
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS backup_runs (
                id TEXT PRIMARY KEY,
                root_path TEXT NOT NULL,
                state TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                snapshot_created INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS backup_queue (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL REFERENCES backup_runs(id),
                root_path TEXT NOT NULL,
                file_path TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                state TEXT NOT NULL,
                retry_count INTEGER NOT NULL DEFAULT 0,
                created_utc TEXT NOT NULL,
                next_retry_utc TEXT NULL,
                error TEXT NULL,
                size INTEGER NOT NULL,
                file_created_utc TEXT NOT NULL,
                last_write_utc TEXT NOT NULL,
                attributes INTEGER NOT NULL,
                hash TEXT NULL,
                UNIQUE(run_id, file_path)
            );
            CREATE INDEX IF NOT EXISTS ix_queue_due ON backup_queue(state,next_retry_utc);
            CREATE TABLE IF NOT EXISTS file_versions (
                root_path TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                object_hash TEXT NOT NULL,
                size INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                last_write_utc TEXT NOT NULL,
                attributes INTEGER NOT NULL,
                last_seen_run TEXT NOT NULL,
                PRIMARY KEY(root_path, relative_path)
            );
            UPDATE backup_queue SET state='Pending' WHERE state IN ('WaitingForStableFile','Hashing','CheckingServer','Uploading');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid?> FindActiveRunAsync(string rootPath, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM backup_runs WHERE root_path=$root AND state='Active' ORDER BY created_utc LIMIT 1;";
        command.Parameters.AddWithValue("$root", rootPath);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return value is null ? null : Guid.Parse(value);
    }

    public async Task RequeueActiveRunsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE backup_queue SET state='Pending',retry_count=0,next_retry_utc=NULL,error=NULL
            WHERE run_id IN (SELECT id FROM backup_runs WHERE state='Active');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RetryFailedRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE backup_queue SET state='Pending',next_retry_utc=NULL,error=NULL WHERE run_id=$run AND state='Failed';";
        command.Parameters.AddWithValue("$run", runId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> CreateRunAsync(string rootPath, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO backup_runs(id,root_path,state,created_utc) VALUES($id,$root,'Active',$created);";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$root", rootPath);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task EnqueueAsync(Guid runId, FileCandidate file, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO backup_queue(
                run_id,root_path,file_path,relative_path,state,created_utc,size,file_created_utc,last_write_utc,attributes)
            VALUES($run,$root,$file,$relative,'Pending',$queued,$size,$created,$modified,$attributes);
            """;
        command.Parameters.AddWithValue("$run", runId.ToString("D"));
        command.Parameters.AddWithValue("$root", file.RootPath);
        command.Parameters.AddWithValue("$file", file.FullPath);
        command.Parameters.AddWithValue("$relative", file.RelativePath);
        command.Parameters.AddWithValue("$queued", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$size", file.Size);
        command.Parameters.AddWithValue("$created", file.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$modified", file.LastWriteUtc.ToString("O"));
        command.Parameters.AddWithValue("$attributes", (int)file.Attributes);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<QueueJob?> LeaseNextAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var select = connection.CreateCommand();
        select.Transaction = (SqliteTransaction)transaction;
        select.CommandText = """
            SELECT id,run_id,root_path,file_path,relative_path,state,retry_count,size,file_created_utc,last_write_utc,attributes
            FROM backup_queue
            WHERE state='Pending' OR (state='RetryScheduled' AND next_retry_utc <= $now)
            ORDER BY id LIMIT 1;
            """;
        select.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        QueueJob? job = null;
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                job = new QueueJob(
                    reader.GetInt64(0), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    Enum.Parse<BackupQueueState>(reader.GetString(5)), reader.GetInt32(6), reader.GetInt64(7),
                    DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture), DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture), reader.GetInt32(10));
            }
        }

        if (job is not null)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = "UPDATE backup_queue SET state='WaitingForStableFile' WHERE id=$id;";
            update.Parameters.AddWithValue("$id", job.Id);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return job;
    }

    public async Task SetStateAsync(long id, BackupQueueState state, string? hash, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE backup_queue SET state=$state,hash=COALESCE($hash,hash),error=NULL WHERE id=$id;";
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$hash", (object?)hash ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteAsync(QueueJob job, string hash, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var version = connection.CreateCommand())
        {
            version.Transaction = (SqliteTransaction)transaction;
            version.CommandText = """
                INSERT INTO file_versions(root_path,relative_path,object_hash,size,created_utc,last_write_utc,attributes,last_seen_run)
                VALUES($root,$relative,$hash,$size,$created,$modified,$attributes,$run)
                ON CONFLICT(root_path,relative_path) DO UPDATE SET
                    object_hash=excluded.object_hash,size=excluded.size,created_utc=excluded.created_utc,
                    last_write_utc=excluded.last_write_utc,attributes=excluded.attributes,last_seen_run=excluded.last_seen_run;
                """;
            version.Parameters.AddWithValue("$root", job.RootPath);
            version.Parameters.AddWithValue("$relative", job.RelativePath);
            version.Parameters.AddWithValue("$hash", hash);
            version.Parameters.AddWithValue("$size", job.Size);
            version.Parameters.AddWithValue("$created", job.CreatedUtc.ToString("O"));
            version.Parameters.AddWithValue("$modified", job.LastWriteUtc.ToString("O"));
            version.Parameters.AddWithValue("$attributes", job.Attributes);
            version.Parameters.AddWithValue("$run", job.RunId.ToString("D"));
            await version.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var queue = connection.CreateCommand())
        {
            queue.Transaction = (SqliteTransaction)transaction;
            queue.CommandText = "UPDATE backup_queue SET state='Completed',hash=$hash,error=NULL WHERE id=$id;";
            queue.Parameters.AddWithValue("$hash", hash);
            queue.Parameters.AddWithValue("$id", job.Id);
            await queue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RetryAsync(QueueJob job, string error, FileInfo? currentFile, CancellationToken cancellationToken)
    {
        var next = DateTimeOffset.UtcNow + RetrySchedule.ForAttempt(job.RetryCount);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE backup_queue SET state='RetryScheduled',retry_count=retry_count+1,next_retry_utc=$next,error=$error,
                size=COALESCE($size,size),file_created_utc=COALESCE($created,file_created_utc),
                last_write_utc=COALESCE($modified,last_write_utc),attributes=COALESCE($attributes,attributes)
            WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$next", next.ToString("O"));
        command.Parameters.AddWithValue("$error", Truncate(error, 1024));
        command.Parameters.AddWithValue("$size", (object?)currentFile?.Length ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", currentFile is null ? DBNull.Value : currentFile.CreationTimeUtc.ToString("O"));
        command.Parameters.AddWithValue("$modified", currentFile is null ? DBNull.Value : currentFile.LastWriteTimeUtc.ToString("O"));
        command.Parameters.AddWithValue("$attributes", (object?)(currentFile is null ? null : (int)currentFile.Attributes) ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", job.Id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FailAsync(QueueJob job, string error, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE backup_queue SET state='Failed',error=$error WHERE id=$id;";
        command.Parameters.AddWithValue("$error", Truncate(error, 1024));
        command.Parameters.AddWithValue("$id", job.Id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyCollection<ReadyRun>> GetReadyRunsAsync(CancellationToken cancellationToken)
    {
        var result = new List<ReadyRun>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.id,r.root_path FROM backup_runs r
            WHERE r.state='Active' AND r.snapshot_created=0
              AND NOT EXISTS(SELECT 1 FROM backup_queue q WHERE q.run_id=r.id AND q.state <> 'Completed');
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ReadyRun(Guid.Parse(reader.GetString(0)), reader.GetString(1)));
        }

        return result;
    }

    public async Task<IReadOnlyCollection<SnapshotEntryDto>> GetSnapshotEntriesAsync(ReadyRun run, CancellationToken cancellationToken)
    {
        var entries = new List<SnapshotEntryDto>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT relative_path,object_hash,size,created_utc,last_write_utc,attributes FROM file_versions WHERE root_path=$root AND last_seen_run=$run ORDER BY relative_path;";
        command.Parameters.AddWithValue("$root", run.RootPath);
        command.Parameters.AddWithValue("$run", run.Id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new SnapshotEntryDto(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), reader.GetInt32(5)));
        }

        return entries;
    }

    public async Task CompleteRunAsync(ReadyRun run, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var removeDeleted = connection.CreateCommand())
        {
            removeDeleted.Transaction = (SqliteTransaction)transaction;
            removeDeleted.CommandText = "DELETE FROM file_versions WHERE root_path=$root AND last_seen_run <> $run;";
            removeDeleted.Parameters.AddWithValue("$root", run.RootPath);
            removeDeleted.Parameters.AddWithValue("$run", run.Id.ToString("D"));
            await removeDeleted.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var complete = connection.CreateCommand())
        {
            complete.Transaction = (SqliteTransaction)transaction;
            complete.CommandText = "UPDATE backup_runs SET state='Completed',snapshot_created=1 WHERE id=$id;";
            complete.Parameters.AddWithValue("$id", run.Id.ToString("D"));
            await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<QueueStatistics> GetStatisticsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM backup_queue WHERE state IN ('Pending','WaitingForStableFile','Hashing','CheckingServer','Uploading','RetryScheduled')),
              (SELECT COUNT(*) FROM backup_queue WHERE state='Failed'),
              (SELECT COUNT(*) FROM file_versions),
              COALESCE((SELECT SUM(size) FROM file_versions),0);
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new QueueStatistics(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
