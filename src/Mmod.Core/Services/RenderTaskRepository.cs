using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mmod.Core.Models;

namespace Mmod.Core.Services;

public sealed class RenderTaskRepository
{
    private readonly string _connectionString;

    public RenderTaskRepository(string? databasePath = null)
    {
        databasePath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ProjectConstants.AppDataFolderName,
            ProjectConstants.TaskDatabaseFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        Initialize();
    }

    public string CreateTask(NewRenderTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Nodes.Count == 0)
            throw new ArgumentException("任务至少需要一个执行节点。", nameof(task));

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var taskId = Guid.NewGuid().ToString("N");
        var position = ScalarInt(connection, transaction, "SELECT COALESCE(MAX(queue_position), -1) + 1 FROM render_tasks;");
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO render_tasks
                    (id, map_name, player_name, track_number, output_path, status, queue_position,
                     settings_json, created_at, elapsed_seconds)
                VALUES ($id, $map, $player, $track, $output, $status, $position, $settings, $created, 0);
                """;
            command.Parameters.AddWithValue("$id", taskId);
            command.Parameters.AddWithValue("$map", task.MapName);
            command.Parameters.AddWithValue("$player", task.PlayerName);
            command.Parameters.AddWithValue("$track", task.TrackNumber);
            command.Parameters.AddWithValue("$output", task.OutputPath);
            command.Parameters.AddWithValue("$status", (int)RenderTaskStatus.Pending);
            command.Parameters.AddWithValue("$position", position);
            command.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(task.Settings));
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        foreach (var node in task.Nodes.OrderBy(n => n.Sequence))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO render_nodes
                    (id, task_id, replay_path, stage_number, sequence, status, retry_count,
                     expected_duration_seconds, expected_tick_count, elapsed_seconds)
                VALUES ($id, $task, $replay, $stage, $sequence, $status, 0, $duration, $ticks, 0);
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$task", taskId);
            command.Parameters.AddWithValue("$replay", Path.GetFullPath(node.ReplayPath));
            command.Parameters.AddWithValue("$stage", node.StageNumber);
            command.Parameters.AddWithValue("$sequence", node.Sequence);
            command.Parameters.AddWithValue("$status", (int)RenderNodeStatus.Pending);
            command.Parameters.AddWithValue("$duration", node.ExpectedDurationSeconds);
            command.Parameters.AddWithValue("$ticks", node.ExpectedTickCount);
            command.ExecuteNonQuery();
        }

        InsertLog(connection, transaction, taskId, null, "Info", "任务已创建并加入队列。");
        transaction.Commit();
        return taskId;
    }

    public IReadOnlyList<RenderTaskRecord> GetTasks(bool includeHistory = true)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = includeHistory
            ? "SELECT * FROM render_tasks ORDER BY queue_position, created_at;"
            : "SELECT * FROM render_tasks WHERE status NOT IN ($completed, $canceled) ORDER BY queue_position;";
        if (!includeHistory)
        {
            command.Parameters.AddWithValue("$completed", (int)RenderTaskStatus.Completed);
            command.Parameters.AddWithValue("$canceled", (int)RenderTaskStatus.Canceled);
        }
        using var reader = command.ExecuteReader();
        var result = new List<RenderTaskRecord>();
        while (reader.Read())
            result.Add(ReadTask(reader));
        return result;
    }

    public IReadOnlyList<RenderNodeRecord> GetNodes(string taskId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM render_nodes WHERE task_id = $task ORDER BY sequence;";
        command.Parameters.AddWithValue("$task", taskId);
        using var reader = command.ExecuteReader();
        var result = new List<RenderNodeRecord>();
        while (reader.Read())
            result.Add(ReadNode(reader));
        return result;
    }

    public IReadOnlyList<TaskLogRecord> GetLogs(string taskId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM task_logs WHERE task_id = $task ORDER BY id;";
        command.Parameters.AddWithValue("$task", taskId);
        using var reader = command.ExecuteReader();
        var result = new List<TaskLogRecord>();
        while (reader.Read())
        {
            result.Add(new TaskLogRecord(
                reader.GetInt64(reader.GetOrdinal("id")),
                taskId,
                GetNullableString(reader, "node_id"),
                DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("timestamp"))),
                reader.GetString(reader.GetOrdinal("level")),
                reader.GetString(reader.GetOrdinal("message"))));
        }
        return result;
    }

    public void UpdateTaskStatus(string taskId, RenderTaskStatus status, string? error = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE render_tasks SET status = $status, last_error = $error,
                started_at = CASE WHEN $status = $starting THEN COALESCE(started_at, $now) ELSE started_at END,
                finished_at = CASE WHEN $terminal = 1 THEN $now ELSE finished_at END
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$starting", (int)RenderTaskStatus.Starting);
        command.Parameters.AddWithValue("$terminal", status is RenderTaskStatus.Completed or RenderTaskStatus.Canceled or RenderTaskStatus.ClipsReadyNeedsManualMerge ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", taskId);
        command.ExecuteNonQuery();
        InsertLog(connection, transaction, taskId, null, error is null ? "Info" : "Error", error ?? $"任务状态变更为 {status}。");
        transaction.Commit();
    }

    public void UpdateNode(RenderNodeRecord node)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE render_nodes SET status=$status, retry_count=$retry, clip_path=$clip,
                started_at=$started, finished_at=$finished, elapsed_seconds=$elapsed, last_error=$error
            WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$status", (int)node.Status);
        command.Parameters.AddWithValue("$retry", node.RetryCount);
        command.Parameters.AddWithValue("$clip", (object?)node.ClipPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$started", DbDate(node.StartedAt));
        command.Parameters.AddWithValue("$finished", DbDate(node.FinishedAt));
        command.Parameters.AddWithValue("$elapsed", node.ElapsedSeconds);
        command.Parameters.AddWithValue("$error", (object?)node.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", node.Id);
        command.ExecuteNonQuery();
    }

    public void UpdateTaskElapsed(string taskId, double elapsedSeconds)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE render_tasks SET elapsed_seconds=$elapsed WHERE id=$id;";
        command.Parameters.AddWithValue("$elapsed", elapsedSeconds);
        command.Parameters.AddWithValue("$id", taskId);
        command.ExecuteNonQuery();
    }

    public void UpdatePendingTaskSettings(string taskId, RenderSettingsSnapshot settings)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE render_tasks SET settings_json=$settings WHERE id=$id AND status=$pending;";
        command.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(settings));
        command.Parameters.AddWithValue("$id", taskId);
        command.Parameters.AddWithValue("$pending", (int)RenderTaskStatus.Pending);
        command.ExecuteNonQuery();
    }

    // ---- render_attempts (fine-grained state machine persistence) ----

    public string CreateAttempt(RenderAttemptRecord attempt)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO render_attempts
                (id, session_id, task_id, node_id, attempt_number, stage, sequence_prefix,
                 temp_clip_path, created_at, updated_at, last_error, failure_kind, cleanup_state,
                 game_process_id, game_process_started_at, netcon_port, expected_map,
                 fed_count, submitted_frame_count, last_tga_index)
            VALUES ($id, $session, $task, $node, $attempt, $stage, $prefix,
                    $temp, $created, $updated, $error, $kind, $cleanup,
                    $gamePid, $gameStart, $port, $map,
                    $fed, $submitted, $lastTga);
            """;
        command.Parameters.AddWithValue("$id", attempt.Id);
        command.Parameters.AddWithValue("$session", attempt.SessionId);
        command.Parameters.AddWithValue("$task", attempt.TaskId);
        command.Parameters.AddWithValue("$node", attempt.NodeId);
        command.Parameters.AddWithValue("$attempt", attempt.AttemptNumber);
        command.Parameters.AddWithValue("$stage", (int)attempt.Stage);
        command.Parameters.AddWithValue("$prefix", attempt.SequencePrefix);
        command.Parameters.AddWithValue("$temp", (object?)attempt.TempClipPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", attempt.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", attempt.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$error", (object?)attempt.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", attempt.FailureKind is null ? DBNull.Value : (int)attempt.FailureKind);
        command.Parameters.AddWithValue("$cleanup", (int)attempt.CleanupState);
        command.Parameters.AddWithValue("$gamePid", (object?)attempt.GameProcessId ?? DBNull.Value);
        command.Parameters.AddWithValue("$gameStart", attempt.GameProcessStartedUtc is null ? DBNull.Value : attempt.GameProcessStartedUtc.Value.ToString("O"));
        command.Parameters.AddWithValue("$port", (object?)attempt.NetConPort ?? DBNull.Value);
        command.Parameters.AddWithValue("$map", (object?)attempt.ExpectedMap ?? DBNull.Value);
        command.Parameters.AddWithValue("$fed", attempt.FedCount);
        command.Parameters.AddWithValue("$submitted", attempt.SubmittedFrameCount);
        command.Parameters.AddWithValue("$lastTga", (object?)attempt.LastTgaIndex ?? DBNull.Value);
        command.ExecuteNonQuery();
        return attempt.Id;
    }

    /// <summary>
    /// Atomic stage transition guarded by the expected old stage. Returns false
    /// when the stored stage differs (the transition is rejected).
    /// </summary>
    public bool TryTransitionAttemptStage(string attemptId, NodeExecutionStage expectedOld, NodeExecutionStage newStage, long fedCount, long submitted, int? lastTga)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE render_attempts SET stage=$new, updated_at=$now,
                fed_count=$fed, submitted_frame_count=$submitted, last_tga_index=$lastTga
            WHERE id=$id AND stage=$old;
            """;
        command.Parameters.AddWithValue("$new", (int)newStage);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$fed", fedCount);
        command.Parameters.AddWithValue("$submitted", submitted);
        command.Parameters.AddWithValue("$lastTga", (object?)lastTga ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", attemptId);
        command.Parameters.AddWithValue("$old", (int)expectedOld);
        return command.ExecuteNonQuery() == 1;
    }

    public void UpdateAttemptFailure(string attemptId, RecordingFailureKind? kind, string? error, CaptureCleanupState cleanupState)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE render_attempts SET failure_kind=$kind, last_error=$error,
                cleanup_state=$cleanup, finished_at=$now, updated_at=$now, stage=$stage
            WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$kind", kind is null ? DBNull.Value : (int)kind);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$cleanup", (int)cleanupState);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$stage", (int)NodeExecutionStage.Failed);
        command.Parameters.AddWithValue("$id", attemptId);
        command.ExecuteNonQuery();
    }

    public void CompleteAttempt(string attemptId, long fedCount, long submitted, int? lastTga)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE render_attempts SET stage=$stage, finished_at=$now, updated_at=$now,
                fed_count=$fed, submitted_frame_count=$submitted, last_tga_index=$lastTga
            WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$stage", (int)NodeExecutionStage.Completed);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$fed", fedCount);
        command.Parameters.AddWithValue("$submitted", submitted);
        command.Parameters.AddWithValue("$lastTga", (object?)lastTga ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", attemptId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// M4: persists a media-validated partial clip on the attempt. The
    /// node's formal ClipPath is never touched here; merge input can only
    /// read Completed nodes, so partials can never be merged.
    /// </summary>
    public void UpdateAttemptPartial(
        string attemptId,
        string partialPath,
        DateTimeOffset validatedAt,
        long outputFrames,
        string reason)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE render_attempts SET partial_state=$state, partial_path=$path,
                partial_validated_at=$at, partial_output_frames=$frames, partial_reason=$reason,
                updated_at=$now
            WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$state", (int)PartialState.Validated);
        command.Parameters.AddWithValue("$path", partialPath);
        command.Parameters.AddWithValue("$at", validatedAt.ToString("O"));
        command.Parameters.AddWithValue("$frames", outputFrames);
        command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", attemptId);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<RenderAttemptRecord> GetAttemptsForNode(string taskId, string nodeId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM render_attempts WHERE task_id=$task AND node_id=$node ORDER BY attempt_number;";
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$node", nodeId);
        using var reader = command.ExecuteReader();
        var result = new List<RenderAttemptRecord>();
        while (reader.Read())
            result.Add(ReadAttempt(reader));
        return result;
    }

    /// <summary>All non-terminal attempts (crash recovery + diagnostics).</summary>
    public IReadOnlyList<RenderAttemptRecord> GetActiveAttempts()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM render_attempts WHERE stage NOT IN ($completed, $failed, $canceled);";
        command.Parameters.AddWithValue("$completed", (int)NodeExecutionStage.Completed);
        command.Parameters.AddWithValue("$failed", (int)NodeExecutionStage.Failed);
        command.Parameters.AddWithValue("$canceled", (int)NodeExecutionStage.Canceled);
        using var reader = command.ExecuteReader();
        var result = new List<RenderAttemptRecord>();
        while (reader.Read())
            result.Add(ReadAttempt(reader));
        return result;
    }

    /// <summary>
    /// M4: attempts with a persisted Validated partial (crash recovery keeps
    /// them; unvalidated temp outputs are conservatively deleted).
    /// </summary>
    public IReadOnlyList<RenderAttemptRecord> GetAttemptsWithValidatedPartial()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM render_attempts WHERE partial_state=$validated;";
        command.Parameters.AddWithValue("$validated", (int)PartialState.Validated);
        using var reader = command.ExecuteReader();
        var result = new List<RenderAttemptRecord>();
        while (reader.Read())
            result.Add(ReadAttempt(reader));
        return result;
    }

    // ---- runner_session (crash recovery) ----

    public void SaveRunnerSession(RunnerSessionRecord session)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO runner_session (singleton, process_id, netcon_port, netcon_password,
                task_id, node_id, updated_at, exe_path, process_started_at, game_session_id,
                capture_session_id, sequence_prefix, ownership_token, watch_directory)
            VALUES (1, $pid, $port, $password, $task, $node, $now, $exe, $start, $game, $cap, $prefix, $token, $watch)
            ON CONFLICT(singleton) DO UPDATE SET
                process_id=$pid, netcon_port=$port, netcon_password=$password, task_id=$task,
                node_id=$node, updated_at=$now, exe_path=$exe, process_started_at=$start,
                game_session_id=$game, capture_session_id=$cap, sequence_prefix=$prefix,
                ownership_token=$token, watch_directory=$watch;
            """;
        command.Parameters.AddWithValue("$pid", (object?)session.ProcessId ?? DBNull.Value);
        command.Parameters.AddWithValue("$port", (object?)session.NetConPort ?? DBNull.Value);
        command.Parameters.AddWithValue("$password", (object?)session.NetConPassword ?? DBNull.Value);
        command.Parameters.AddWithValue("$task", (object?)session.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$node", (object?)session.NodeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$exe", (object?)session.ExePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$start", session.ProcessStartedAt is null ? DBNull.Value : session.ProcessStartedAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$game", (object?)session.GameSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$cap", (object?)session.CaptureSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$prefix", (object?)session.SequencePrefix ?? DBNull.Value);
        command.Parameters.AddWithValue("$token", (object?)session.OwnershipToken ?? DBNull.Value);
        command.Parameters.AddWithValue("$watch", (object?)session.WatchDirectory ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public RunnerSessionRecord? GetRunnerSession()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM runner_session WHERE singleton=1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return new RunnerSessionRecord(
            GetNullableInt(reader, "process_id"),
            GetNullableInt(reader, "netcon_port"),
            GetNullableString(reader, "netcon_password"),
            GetNullableString(reader, "task_id"),
            GetNullableString(reader, "node_id"),
            GetNullableString(reader, "exe_path"),
            GetNullableDateTimeUtc(reader, "process_started_at"),
            GetNullableString(reader, "game_session_id"),
            GetNullableString(reader, "capture_session_id"),
            GetNullableString(reader, "sequence_prefix"),
            GetNullableString(reader, "ownership_token"),
            GetNullableString(reader, "watch_directory"));
    }

    public void ClearRunnerSession()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM runner_session;";
        command.ExecuteNonQuery();
    }

    public void AddLog(string taskId, string? nodeId, string level, string message)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        InsertLog(connection, transaction, taskId, nodeId, level, message);
        transaction.Commit();
    }

    public void MovePendingTask(string taskId, int newPosition)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var tasks = GetPendingIds(connection, transaction);
        if (!tasks.Remove(taskId))
            return;
        tasks.Insert(Math.Clamp(newPosition, 0, tasks.Count), taskId);
        for (var i = 0; i < tasks.Count; i++)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE render_tasks SET queue_position=$position WHERE id=$id;";
            command.Parameters.AddWithValue("$position", i);
            command.Parameters.AddWithValue("$id", tasks[i]);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void DeleteTaskRecord(string taskId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM render_tasks WHERE id=$id AND status NOT IN ($running, $starting, $merging);";
        command.Parameters.AddWithValue("$id", taskId);
        command.Parameters.AddWithValue("$running", (int)RenderTaskStatus.Running);
        command.Parameters.AddWithValue("$starting", (int)RenderTaskStatus.Starting);
        command.Parameters.AddWithValue("$merging", (int)RenderTaskStatus.Merging);
        command.ExecuteNonQuery();
    }

    private void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS render_tasks (
                id TEXT PRIMARY KEY, map_name TEXT NOT NULL, player_name TEXT NOT NULL,
                track_number INTEGER NOT NULL, output_path TEXT NOT NULL, status INTEGER NOT NULL,
                queue_position INTEGER NOT NULL, settings_json TEXT NOT NULL, created_at TEXT NOT NULL,
                started_at TEXT NULL, finished_at TEXT NULL, elapsed_seconds REAL NOT NULL DEFAULT 0,
                last_error TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS render_nodes (
                id TEXT PRIMARY KEY, task_id TEXT NOT NULL REFERENCES render_tasks(id) ON DELETE CASCADE,
                replay_path TEXT NOT NULL, stage_number INTEGER NOT NULL, sequence INTEGER NOT NULL,
                status INTEGER NOT NULL, retry_count INTEGER NOT NULL DEFAULT 0, clip_path TEXT NULL,
                expected_duration_seconds REAL NOT NULL, expected_tick_count INTEGER NOT NULL DEFAULT 0, started_at TEXT NULL, finished_at TEXT NULL,
                elapsed_seconds REAL NOT NULL DEFAULT 0, last_error TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_render_nodes_task_sequence ON render_nodes(task_id, sequence);
            CREATE TABLE IF NOT EXISTS task_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT, task_id TEXT NOT NULL REFERENCES render_tasks(id) ON DELETE CASCADE,
                node_id TEXT NULL, timestamp TEXT NOT NULL, level TEXT NOT NULL, message TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS runner_session (
                singleton INTEGER PRIMARY KEY CHECK(singleton=1), process_id INTEGER NULL,
                netcon_port INTEGER NULL, netcon_password TEXT NULL, task_id TEXT NULL,
                node_id TEXT NULL, updated_at TEXT NOT NULL, exe_path TEXT NULL,
                process_started_at TEXT NULL, game_session_id TEXT NULL,
                capture_session_id TEXT NULL, sequence_prefix TEXT NULL,
                ownership_token TEXT NULL, watch_directory TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS render_attempts (
                id TEXT PRIMARY KEY, session_id TEXT NOT NULL, task_id TEXT NOT NULL,
                node_id TEXT NOT NULL, attempt_number INTEGER NOT NULL, stage INTEGER NOT NULL,
                sequence_prefix TEXT NOT NULL, temp_clip_path TEXT NULL, created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL, finished_at TEXT NULL, last_error TEXT NULL,
                failure_kind INTEGER NULL, cleanup_state INTEGER NOT NULL DEFAULT 0,
                game_process_id INTEGER NULL, game_process_started_at TEXT NULL,
                netcon_port INTEGER NULL, expected_map TEXT NULL,
                fed_count INTEGER NOT NULL DEFAULT 0, submitted_frame_count INTEGER NOT NULL DEFAULT 0,
                last_tga_index INTEGER NULL,
                partial_state INTEGER NOT NULL DEFAULT 0, partial_path TEXT NULL,
                partial_validated_at TEXT NULL, partial_output_frames INTEGER NULL,
                partial_reason TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_render_attempts_node ON render_attempts(task_id, node_id, attempt_number);
            CREATE INDEX IF NOT EXISTS ix_render_attempts_active ON render_attempts(stage);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "render_nodes", "expected_tick_count", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "runner_session", "exe_path", "TEXT NULL");
        EnsureColumn(connection, "runner_session", "process_started_at", "TEXT NULL");
        EnsureColumn(connection, "runner_session", "game_session_id", "TEXT NULL");
        EnsureColumn(connection, "runner_session", "capture_session_id", "TEXT NULL");
        EnsureColumn(connection, "runner_session", "sequence_prefix", "TEXT NULL");
        EnsureColumn(connection, "runner_session", "ownership_token", "TEXT NULL");
        EnsureColumn(connection, "runner_session", "watch_directory", "TEXT NULL");
        // M4: partial-clip lifecycle columns (nullable, idempotent; old DBs read as "no partial").
        EnsureColumn(connection, "render_attempts", "partial_state", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "render_attempts", "partial_path", "TEXT NULL");
        EnsureColumn(connection, "render_attempts", "partial_validated_at", "TEXT NULL");
        EnsureColumn(connection, "render_attempts", "partial_output_frames", "INTEGER NULL");
        EnsureColumn(connection, "render_attempts", "partial_reason", "TEXT NULL");
        NormalizeInterruptedWork(connection);
    }

    private static void NormalizeInterruptedWork(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var nodes = connection.CreateCommand();
        nodes.Transaction = transaction;
        nodes.CommandText = "UPDATE render_nodes SET status=$pending, last_error='应用中断，节点将在继续时从头执行。' WHERE status IN ($recording, $synthesizing);";
        nodes.Parameters.AddWithValue("$pending", (int)RenderNodeStatus.Pending);
        nodes.Parameters.AddWithValue("$recording", (int)RenderNodeStatus.Recording);
        nodes.Parameters.AddWithValue("$synthesizing", (int)RenderNodeStatus.Synthesizing);
        nodes.ExecuteNonQuery();
        using var tasks = connection.CreateCommand();
        tasks.Transaction = transaction;
        tasks.CommandText = "UPDATE render_tasks SET status=$paused, last_error='应用上次运行中断，可继续执行。' WHERE status IN ($starting, $running, $merging);";
        tasks.Parameters.AddWithValue("$paused", (int)RenderTaskStatus.Paused);
        tasks.Parameters.AddWithValue("$starting", (int)RenderTaskStatus.Starting);
        tasks.Parameters.AddWithValue("$running", (int)RenderTaskStatus.Running);
        tasks.Parameters.AddWithValue("$merging", (int)RenderTaskStatus.Merging);
        tasks.ExecuteNonQuery();
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table});";
        using var reader = check.ExecuteReader();
        while (reader.Read()) if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private static int ScalarInt(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void InsertLog(SqliteConnection connection, SqliteTransaction transaction, string taskId, string? nodeId, string level, string message)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO task_logs(task_id,node_id,timestamp,level,message) VALUES($task,$node,$time,$level,$message);";
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$node", (object?)nodeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$level", level);
        command.Parameters.AddWithValue("$message", message);
        command.ExecuteNonQuery();
    }

    private static List<string> GetPendingIds(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM render_tasks WHERE status=$pending ORDER BY queue_position;";
        command.Parameters.AddWithValue("$pending", (int)RenderTaskStatus.Pending);
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    private static RenderTaskRecord ReadTask(SqliteDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("id")),
        reader.GetString(reader.GetOrdinal("map_name")),
        reader.GetString(reader.GetOrdinal("player_name")),
        reader.GetInt32(reader.GetOrdinal("track_number")),
        reader.GetString(reader.GetOrdinal("output_path")),
        (RenderTaskStatus)reader.GetInt32(reader.GetOrdinal("status")),
        reader.GetInt32(reader.GetOrdinal("queue_position")),
        reader.GetString(reader.GetOrdinal("settings_json")),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        GetNullableDate(reader, "started_at"), GetNullableDate(reader, "finished_at"),
        reader.GetDouble(reader.GetOrdinal("elapsed_seconds")), GetNullableString(reader, "last_error"));

    private static RenderNodeRecord ReadNode(SqliteDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("id")), reader.GetString(reader.GetOrdinal("task_id")),
        reader.GetString(reader.GetOrdinal("replay_path")), reader.GetInt32(reader.GetOrdinal("stage_number")),
        reader.GetInt32(reader.GetOrdinal("sequence")), (RenderNodeStatus)reader.GetInt32(reader.GetOrdinal("status")),
        reader.GetInt32(reader.GetOrdinal("retry_count")), GetNullableString(reader, "clip_path"),
        reader.GetDouble(reader.GetOrdinal("expected_duration_seconds")), GetNullableDate(reader, "started_at"),
        GetNullableDate(reader, "finished_at"), reader.GetDouble(reader.GetOrdinal("elapsed_seconds")),
        GetNullableString(reader, "last_error"), reader.GetInt32(reader.GetOrdinal("expected_tick_count")));

    private static RenderAttemptRecord ReadAttempt(SqliteDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("id")),
        reader.GetString(reader.GetOrdinal("session_id")),
        reader.GetString(reader.GetOrdinal("task_id")),
        reader.GetString(reader.GetOrdinal("node_id")),
        reader.GetInt32(reader.GetOrdinal("attempt_number")),
        (NodeExecutionStage)reader.GetInt32(reader.GetOrdinal("stage")),
        reader.GetString(reader.GetOrdinal("sequence_prefix")),
        GetNullableString(reader, "temp_clip_path"),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
        GetNullableDate(reader, "finished_at"),
        GetNullableString(reader, "last_error"),
        GetNullableInt(reader, "failure_kind") is { } kind ? (RecordingFailureKind)kind : null,
        (CaptureCleanupState)reader.GetInt32(reader.GetOrdinal("cleanup_state")),
        GetNullableInt(reader, "game_process_id"),
        GetNullableDateTimeUtc(reader, "game_process_started_at"),
        GetNullableInt(reader, "netcon_port"),
        GetNullableString(reader, "expected_map"),
        reader.GetInt64(reader.GetOrdinal("fed_count")),
        reader.GetInt64(reader.GetOrdinal("submitted_frame_count")),
        GetNullableInt(reader, "last_tga_index"),
        (PartialState)reader.GetInt32(reader.GetOrdinal("partial_state")),
        GetNullableString(reader, "partial_path"),
        GetNullableDate(reader, "partial_validated_at"),
        reader.IsDBNull(reader.GetOrdinal("partial_output_frames")) ? null : reader.GetInt64(reader.GetOrdinal("partial_output_frames")),
        GetNullableString(reader, "partial_reason"));

    private static DateTimeOffset? GetNullableDate(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));
    }

    private static int? GetNullableInt(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static DateTime? GetNullableDateTimeUtc(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : DateTime.Parse(reader.GetString(ordinal)).ToUniversalTime();
    }

    private static string? GetNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static object DbDate(DateTimeOffset? value) => value?.ToString("O") ?? (object)DBNull.Value;
}
