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
                     expected_duration_seconds, elapsed_seconds)
                VALUES ($id, $task, $replay, $stage, $sequence, $status, 0, $duration, 0);
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$task", taskId);
            command.Parameters.AddWithValue("$replay", Path.GetFullPath(node.ReplayPath));
            command.Parameters.AddWithValue("$stage", node.StageNumber);
            command.Parameters.AddWithValue("$sequence", node.Sequence);
            command.Parameters.AddWithValue("$status", (int)RenderNodeStatus.Pending);
            command.Parameters.AddWithValue("$duration", node.ExpectedDurationSeconds);
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
                expected_duration_seconds REAL NOT NULL, started_at TEXT NULL, finished_at TEXT NULL,
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
                node_id TEXT NULL, updated_at TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
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
        GetNullableString(reader, "last_error"));

    private static DateTimeOffset? GetNullableDate(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal));
    }

    private static string? GetNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static object DbDate(DateTimeOffset? value) => value?.ToString("O") ?? (object)DBNull.Value;
}
