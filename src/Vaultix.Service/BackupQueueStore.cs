using System.Globalization;
using Microsoft.Data.Sqlite;
using Vaultix.Core;
using Vaultix.Shared;

namespace Vaultix.Service;

public enum DetectedChangeType
{
    New,
    Changed
}

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
    int Attributes,
    DetectedChangeType ChangeType);

public sealed record FileVersion(
    string ObjectHash,
    long Size,
    DateTimeOffset CreatedUtc,
    DateTimeOffset LastWriteUtc,
    int Attributes);

public sealed record QueueStatistics(int Pending, int Failed, long ProtectedFiles, long ProtectedBytes);
public sealed record ReadyRun(Guid Id, string RootPath);
public sealed record FolderStatistics(long FileCount, long TotalBytes);

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
                snapshot_created INTEGER NOT NULL DEFAULT 0,
                scan_completed INTEGER NOT NULL DEFAULT 0
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
                change_type TEXT NOT NULL DEFAULT 'Changed',
                UNIQUE(run_id, file_path)
            );
            CREATE INDEX IF NOT EXISTS ix_queue_due ON backup_queue(state,next_retry_utc);
            CREATE INDEX IF NOT EXISTS ix_queue_run_state ON backup_queue(run_id,state);
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
            CREATE INDEX IF NOT EXISTS ix_versions_seen ON file_versions(root_path,last_seen_run);
            CREATE INDEX IF NOT EXISTS ix_versions_hash ON file_versions(object_hash);
            CREATE TABLE IF NOT EXISTS backup_sessions (
                id TEXT PRIMARY KEY REFERENCES backup_runs(id),
                root_path TEXT NOT NULL,
                started_utc TEXT NOT NULL,
                completed_utc TEXT NULL,
                status TEXT NOT NULL,
                files_discovered INTEGER NOT NULL DEFAULT 0,
                files_processed INTEGER NOT NULL DEFAULT 0,
                files_uploaded INTEGER NOT NULL DEFAULT 0,
                files_skipped INTEGER NOT NULL DEFAULT 0,
                files_failed INTEGER NOT NULL DEFAULT 0,
                new_files INTEGER NOT NULL DEFAULT 0,
                changed_files INTEGER NOT NULL DEFAULT 0,
                deleted_files INTEGER NOT NULL DEFAULT 0,
                bytes_logical INTEGER NOT NULL DEFAULT 0,
                bytes_hashed INTEGER NOT NULL DEFAULT 0,
                bytes_uploaded INTEGER NOT NULL DEFAULT 0,
                bytes_deduplicated INTEGER NOT NULL DEFAULT 0,
                average_upload_speed REAL NOT NULL DEFAULT 0,
                peak_upload_speed REAL NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_sessions_started ON backup_sessions(started_utc DESC);
            CREATE TABLE IF NOT EXISTS metric_minutes (
                bucket_utc TEXT PRIMARY KEY,
                uploaded_bytes INTEGER NOT NULL DEFAULT 0,
                downloaded_bytes INTEGER NOT NULL DEFAULT 0,
                upload_speed_sum REAL NOT NULL DEFAULT 0,
                download_speed_sum REAL NOT NULL DEFAULT 0,
                sample_count INTEGER NOT NULL DEFAULT 0,
                max_queue_length INTEGER NOT NULL DEFAULT 0
            );
            UPDATE backup_queue SET state='Pending' WHERE state IN ('WaitingForStableFile','Hashing','CheckingServer','Uploading');
            INSERT OR IGNORE INTO backup_sessions(id,root_path,started_utc,status)
                SELECT id,root_path,created_utc,state FROM backup_runs;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "backup_queue", "change_type", "TEXT NOT NULL DEFAULT 'Changed'", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "backup_runs", "scan_completed", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
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

    public async Task ResetForServerChangeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var clearQueue = connection.CreateCommand())
        {
            clearQueue.Transaction = (SqliteTransaction)transaction;
            clearQueue.CommandText = "DELETE FROM backup_queue WHERE run_id IN (SELECT id FROM backup_runs WHERE state='Active');";
            await clearQueue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var resetRuns = connection.CreateCommand())
        {
            resetRuns.Transaction = (SqliteTransaction)transaction;
            resetRuns.CommandText = "UPDATE backup_runs SET scan_completed=0,snapshot_created=0 WHERE state='Active';";
            await resetRuns.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var clearVersions = connection.CreateCommand())
        {
            clearVersions.Transaction = (SqliteTransaction)transaction;
            clearVersions.CommandText = "DELETE FROM file_versions;";
            await clearVersions.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RetryFailedRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE backup_queue SET state='Pending',next_retry_utc=NULL,error=NULL WHERE run_id=$run AND state='Failed';";
        command.Parameters.AddWithValue("$run", runId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsScanCompleteAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT scan_completed FROM backup_runs WHERE id=$id;";
        command.Parameters.AddWithValue("$id", runId.ToString("D"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
    }

    public async Task PrepareRunForRescanAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var queue = connection.CreateCommand())
        {
            queue.Transaction = (SqliteTransaction)transaction;
            queue.CommandText = "DELETE FROM backup_queue WHERE run_id=$run;";
            queue.Parameters.AddWithValue("$run", runId.ToString("D"));
            await queue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var session = connection.CreateCommand())
        {
            session.Transaction = (SqliteTransaction)transaction;
            session.CommandText = """
                UPDATE backup_sessions SET status='Scanning',files_discovered=0,files_processed=0,files_uploaded=0,
                    files_skipped=0,files_failed=0,new_files=0,changed_files=0,deleted_files=0,bytes_logical=0,
                    bytes_hashed=0,bytes_uploaded=0,bytes_deduplicated=0 WHERE id=$run;
                """;
            session.Parameters.AddWithValue("$run", runId.ToString("D"));
            await session.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> CreateRunAsync(string rootPath, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var run = connection.CreateCommand())
        {
            run.Transaction = (SqliteTransaction)transaction;
            run.CommandText = "INSERT INTO backup_runs(id,root_path,state,created_utc) VALUES($id,$root,'Active',$created);";
            run.Parameters.AddWithValue("$id", id.ToString("D"));
            run.Parameters.AddWithValue("$root", rootPath);
            run.Parameters.AddWithValue("$created", started.ToString("O"));
            await run.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var session = connection.CreateCommand())
        {
            session.Transaction = (SqliteTransaction)transaction;
            session.CommandText = "INSERT INTO backup_sessions(id,root_path,started_utc,status) VALUES($id,$root,$started,'Scanning');";
            session.Parameters.AddWithValue("$id", id.ToString("D"));
            session.Parameters.AddWithValue("$root", rootPath);
            session.Parameters.AddWithValue("$started", started.ToString("O"));
            await session.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task<FileVersion?> GetFileVersionAsync(string rootPath, string relativePath, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT object_hash,size,created_utc,last_write_utc,attributes FROM file_versions WHERE root_path=$root AND relative_path=$relative;";
        command.Parameters.AddWithValue("$root", rootPath);
        command.Parameters.AddWithValue("$relative", relativePath);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new FileVersion(reader.GetString(0), reader.GetInt64(1), ParseTimestamp(reader.GetString(2)), ParseTimestamp(reader.GetString(3)), reader.GetInt32(4))
            : null;
    }

    public async Task MarkUnchangedAsync(Guid runId, FileCandidate file, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var version = connection.CreateCommand())
        {
            version.Transaction = (SqliteTransaction)transaction;
            version.CommandText = "UPDATE file_versions SET last_seen_run=$run,attributes=$attributes WHERE root_path=$root AND relative_path=$relative;";
            version.Parameters.AddWithValue("$run", runId.ToString("D"));
            version.Parameters.AddWithValue("$attributes", (int)file.Attributes);
            version.Parameters.AddWithValue("$root", file.RootPath);
            version.Parameters.AddWithValue("$relative", file.RelativePath);
            await version.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await UpdateSessionAsync(connection, (SqliteTransaction)transaction, runId,
            "files_discovered=files_discovered+1,files_processed=files_processed+1,files_skipped=files_skipped+1,bytes_logical=bytes_logical+$bytes",
            [("$bytes", file.Size)], cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnqueueAsync(Guid runId, FileCandidate file, DetectedChangeType changeType, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var seen = connection.CreateCommand())
        {
            seen.Transaction = (SqliteTransaction)transaction;
            seen.CommandText = "UPDATE file_versions SET last_seen_run=$run WHERE root_path=$root AND relative_path=$relative;";
            seen.Parameters.AddWithValue("$run", runId.ToString("D"));
            seen.Parameters.AddWithValue("$root", file.RootPath);
            seen.Parameters.AddWithValue("$relative", file.RelativePath);
            await seen.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO backup_queue(
                    run_id,root_path,file_path,relative_path,state,created_utc,size,file_created_utc,last_write_utc,attributes,change_type)
                VALUES($run,$root,$file,$relative,'Pending',$queued,$size,$created,$modified,$attributes,$change);
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
            command.Parameters.AddWithValue("$change", changeType.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var changeColumn = changeType == DetectedChangeType.New ? "new_files=new_files+1" : "changed_files=changed_files+1";
        await UpdateSessionAsync(connection, (SqliteTransaction)transaction, runId,
            $"files_discovered=files_discovered+1,{changeColumn},bytes_logical=bytes_logical+$bytes",
            [("$bytes", file.Size)], cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CompleteScanAsync(Guid runId, string rootPath, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var count = connection.CreateCommand();
        count.Transaction = (SqliteTransaction)transaction;
        count.CommandText = "SELECT COUNT(*) FROM file_versions WHERE root_path=$root AND last_seen_run<>$run;";
        count.Parameters.AddWithValue("$root", rootPath);
        count.Parameters.AddWithValue("$run", runId.ToString("D"));
        var deleted = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        await UpdateSessionAsync(connection, (SqliteTransaction)transaction, runId,
            "deleted_files=$deleted,status='BackingUp'", [("$deleted", deleted)], cancellationToken).ConfigureAwait(false);
        await using (var completed = connection.CreateCommand())
        {
            completed.Transaction = (SqliteTransaction)transaction;
            completed.CommandText = "UPDATE backup_runs SET scan_completed=1 WHERE id=$run;";
            completed.Parameters.AddWithValue("$run", runId.ToString("D"));
            await completed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    public async Task<QueueJob?> LeaseNextAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var select = connection.CreateCommand();
        select.Transaction = (SqliteTransaction)transaction;
        select.CommandText = """
            SELECT id,run_id,root_path,file_path,relative_path,state,retry_count,size,file_created_utc,last_write_utc,attributes,change_type
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
                    ParseTimestamp(reader.GetString(8)), ParseTimestamp(reader.GetString(9)), reader.GetInt32(10),
                    Enum.Parse<DetectedChangeType>(reader.GetString(11)));
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

    public async Task RecordHashedAsync(Guid runId, long bytes, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await UpdateSessionAsync(connection, (SqliteTransaction)transaction, runId,
            "bytes_hashed=bytes_hashed+$bytes", [("$bytes", bytes)], cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteAsync(QueueJob job, string hash, bool uploaded, CancellationToken cancellationToken)
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

        var update = uploaded
            ? "files_processed=files_processed+1,files_uploaded=files_uploaded+1,bytes_uploaded=bytes_uploaded+$bytes"
            : "files_processed=files_processed+1,files_skipped=files_skipped+1,bytes_deduplicated=bytes_deduplicated+$bytes";
        await UpdateSessionAsync(connection, (SqliteTransaction)transaction, job.RunId, update,
            [("$bytes", job.Size)], cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteMissingAsync(QueueJob job, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "UPDATE backup_queue SET state='Completed',error=NULL WHERE id=$id;";
            command.Parameters.AddWithValue("$id", job.Id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await UpdateSessionAsync(connection, (SqliteTransaction)transaction, job.RunId,
            "files_processed=files_processed+1,deleted_files=deleted_files+1", [], cancellationToken).ConfigureAwait(false);
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
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "UPDATE backup_queue SET state='Failed',error=$error WHERE id=$id;";
            command.Parameters.AddWithValue("$error", Truncate(error, 1024));
            command.Parameters.AddWithValue("$id", job.Id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await UpdateSessionAsync(connection, (SqliteTransaction)transaction, job.RunId,
            "files_failed=files_failed+1,status='Warning'", [], cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyCollection<ReadyRun>> GetReadyRunsAsync(CancellationToken cancellationToken)
    {
        var result = new List<ReadyRun>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.id,r.root_path FROM backup_runs r
            WHERE r.state='Active' AND r.snapshot_created=0 AND r.scan_completed=1
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
                ParseTimestamp(reader.GetString(3)), ParseTimestamp(reader.GetString(4)), reader.GetInt32(5)));
        }

        return entries;
    }

    public async Task<BackupSessionDto> GetSessionAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SessionSelect + " WHERE id=$id;";
        command.Parameters.AddWithValue("$id", runId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadSession(reader)
            : throw new InvalidOperationException("Backup session was not found.");
    }

    public async Task<IReadOnlyCollection<BackupSessionDto>> GetRecentSessionsAsync(int count, CancellationToken cancellationToken)
    {
        var sessions = new List<BackupSessionDto>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SessionSelect + " ORDER BY started_utc DESC LIMIT $count;";
        command.Parameters.AddWithValue("$count", Math.Clamp(count, 1, 100));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sessions.Add(ReadSession(reader));
        }

        return sessions;
    }

    public async Task<bool> HasChangesSinceAsync(string rootPath, DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM backup_sessions
                WHERE root_path=$root AND started_utc >= $since AND (new_files > 0 OR changed_files > 0 OR deleted_files > 0));
            """;
        command.Parameters.AddWithValue("$root", rootPath);
        command.Parameters.AddWithValue("$since", sinceUtc.ToString("O"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
    }

    public async Task CompleteRunAsync(ReadyRun run, double averageUploadSpeed, double peakUploadSpeed, CancellationToken cancellationToken)
    {
        var completed = DateTimeOffset.UtcNow;
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

        await UpdateSessionAsync(connection, (SqliteTransaction)transaction, run.Id,
            "completed_utc=$completed,status=CASE WHEN files_failed>0 THEN 'Warning' ELSE 'Completed' END,average_upload_speed=$average,peak_upload_speed=$peak",
            [("$completed", completed.ToString("O")), ("$average", averageUploadSpeed), ("$peak", peakUploadSpeed)], cancellationToken).ConfigureAwait(false);
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

    public async Task<bool> HasFileVersionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM file_versions);";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
    }

    public async Task<FolderStatistics> GetFolderStatisticsAsync(string rootPath, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*),COALESCE(SUM(size),0) FROM file_versions WHERE root_path=$root;";
        command.Parameters.AddWithValue("$root", rootPath);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new FolderStatistics(reader.GetInt64(0), reader.GetInt64(1));
    }

    public async Task RecordMetricSampleAsync(TransferSampleDto sample, long uploadedDelta, long downloadedDelta, CancellationToken cancellationToken)
    {
        var timestamp = sample.TimestampUtc.UtcDateTime;
        var bucket = new DateTimeOffset(timestamp.Year, timestamp.Month, timestamp.Day, timestamp.Hour, timestamp.Minute, 0, TimeSpan.Zero);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO metric_minutes(bucket_utc,uploaded_bytes,downloaded_bytes,upload_speed_sum,download_speed_sum,sample_count,max_queue_length)
            VALUES($bucket,$uploaded,$downloaded,$uploadSpeed,$downloadSpeed,1,$queue)
            ON CONFLICT(bucket_utc) DO UPDATE SET
                uploaded_bytes=uploaded_bytes+excluded.uploaded_bytes,
                downloaded_bytes=downloaded_bytes+excluded.downloaded_bytes,
                upload_speed_sum=upload_speed_sum+excluded.upload_speed_sum,
                download_speed_sum=download_speed_sum+excluded.download_speed_sum,
                sample_count=sample_count+1,
                max_queue_length=MAX(max_queue_length,excluded.max_queue_length);
            """;
        command.Parameters.AddWithValue("$bucket", bucket.ToString("O"));
        command.Parameters.AddWithValue("$uploaded", uploadedDelta);
        command.Parameters.AddWithValue("$downloaded", downloadedDelta);
        command.Parameters.AddWithValue("$uploadSpeed", sample.UploadBytesPerSecond);
        command.Parameters.AddWithValue("$downloadSpeed", sample.DownloadBytesPerSecond);
        command.Parameters.AddWithValue("$queue", sample.QueueLength);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var prune = connection.CreateCommand();
        prune.CommandText = "DELETE FROM metric_minutes WHERE bucket_utc < $cutoff;";
        prune.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-30).ToString("O"));
        await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string SessionSelect = """
        SELECT id,started_utc,completed_utc,status,files_discovered,files_processed,files_uploaded,files_skipped,
               files_failed,new_files,changed_files,deleted_files,bytes_logical,bytes_hashed,bytes_uploaded,
               bytes_deduplicated,average_upload_speed,peak_upload_speed
        FROM backup_sessions
        """;

    private static BackupSessionDto ReadSession(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        ParseTimestamp(reader.GetString(1)),
        reader.IsDBNull(2) ? null : ParseTimestamp(reader.GetString(2)),
        reader.GetString(3),
        reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8),
        reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12), reader.GetInt64(13),
        reader.GetInt64(14), reader.GetInt64(15), reader.GetDouble(16), reader.GetDouble(17));

    private static async Task UpdateSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid runId,
        string assignments,
        IReadOnlyCollection<(string Name, object Value)> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE backup_sessions SET {assignments} WHERE id=$id;";
        command.Parameters.AddWithValue("$id", runId.ToString("D"));
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
