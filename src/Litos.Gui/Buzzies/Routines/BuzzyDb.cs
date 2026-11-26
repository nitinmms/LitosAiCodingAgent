using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Litos.Gui.Buzzies.Routines;

/// <summary>
/// buzzies.db: routines and their run history (Implement_Buzzies.md, Phase 1). SQLite in WAL mode
/// with a short connection per operation, so the scheduler's thread and the UI thread never share
/// one. A Buzzy's own settings stay in its buzzy.json; this database never holds a second copy of
/// them. The (routine, occurrence key) pair is unique, which is what stops one scheduled
/// occurrence from being queued twice, even by two Litos windows or after a restart.
/// </summary>
public sealed class BuzzyDb
{
    private const int SchemaVersion = 1;

    private readonly string _connectionString;

    public BuzzyDb(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 10,
        }.ToString();
        Migrate();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Migrate()
    {
        using var connection = Open();
        Execute(connection, "PRAGMA journal_mode=WAL;");
        var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version >= SchemaVersion)
            return;

        using var transaction = connection.BeginTransaction();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS routines (
                id TEXT PRIMARY KEY,
                buzzy_id TEXT NOT NULL,
                name TEXT NOT NULL,
                prompt TEXT NOT NULL,
                trigger TEXT NOT NULL,
                time_of_day TEXT,
                day_of_week INTEGER,
                interval_minutes INTEGER,
                time_zone TEXT NOT NULL,
                enabled INTEGER NOT NULL,
                max_minutes INTEGER NOT NULL,
                max_tokens INTEGER NOT NULL,
                missed_runs TEXT NOT NULL,
                created_at TEXT NOT NULL,
                last_scheduled_at TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_routines_buzzy ON routines(buzzy_id);
            CREATE TABLE IF NOT EXISTS routine_runs (
                id TEXT PRIMARY KEY,
                routine_id TEXT NOT NULL,
                buzzy_id TEXT NOT NULL,
                occurrence_key TEXT NOT NULL,
                trigger TEXT NOT NULL,
                status TEXT NOT NULL,
                queued_at TEXT NOT NULL,
                started_at TEXT,
                finished_at TEXT,
                session_id TEXT,
                summary TEXT,
                error TEXT,
                input_tokens INTEGER NOT NULL DEFAULT 0,
                output_tokens INTEGER NOT NULL DEFAULT 0,
                outputs TEXT NOT NULL DEFAULT '[]',
                UNIQUE (routine_id, occurrence_key)
            );
            CREATE INDEX IF NOT EXISTS ix_runs_status ON routine_runs(status);
            CREATE INDEX IF NOT EXISTS ix_runs_buzzy ON routine_runs(buzzy_id, queued_at);
            """, transaction);
        Execute(connection, $"PRAGMA user_version = {SchemaVersion};", transaction);
        transaction.Commit();
    }

    // ---- Routines ------------------------------------------------------------------------------

    public void SaveRoutine(Routine routine)
    {
        using var connection = Open();
        Execute(connection, """
            INSERT INTO routines (id, buzzy_id, name, prompt, trigger, time_of_day, day_of_week, interval_minutes,
                                  time_zone, enabled, max_minutes, max_tokens, missed_runs, created_at, last_scheduled_at)
            VALUES ($id, $buzzy, $name, $prompt, $trigger, $time, $day, $interval, $zone, $enabled, $maxMinutes,
                    $maxTokens, $missed, $created, $lastScheduled)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, prompt = excluded.prompt, trigger = excluded.trigger,
                time_of_day = excluded.time_of_day, day_of_week = excluded.day_of_week,
                interval_minutes = excluded.interval_minutes, time_zone = excluded.time_zone,
                enabled = excluded.enabled, max_minutes = excluded.max_minutes, max_tokens = excluded.max_tokens,
                missed_runs = excluded.missed_runs, last_scheduled_at = excluded.last_scheduled_at;
            """, null,
            ("$id", routine.Id), ("$buzzy", routine.BuzzyId), ("$name", routine.Name), ("$prompt", routine.Prompt),
            ("$trigger", routine.Trigger.ToString()), ("$time", routine.TimeOfDay?.ToString("HH:mm", CultureInfo.InvariantCulture)),
            ("$day", routine.Day is { } d ? (int)d : null), ("$interval", routine.IntervalMinutes),
            ("$zone", routine.TimeZoneId), ("$enabled", routine.Enabled ? 1 : 0), ("$maxMinutes", routine.MaxMinutes),
            ("$maxTokens", routine.MaxTokens), ("$missed", routine.MissedRuns.ToString()), ("$created", Iso(routine.CreatedAt)),
            ("$lastScheduled", routine.LastScheduledAt is { } at ? Iso(at) : null));
    }

    public Routine? GetRoutine(string id) =>
        QueryRoutines("SELECT * FROM routines WHERE id = $id", ("$id", id)).FirstOrDefault();

    /// <summary>Every routine, or one Buzzy's, oldest first.</summary>
    public IReadOnlyList<Routine> ListRoutines(string? buzzyId = null) => buzzyId is null
        ? QueryRoutines("SELECT * FROM routines ORDER BY created_at")
        : QueryRoutines("SELECT * FROM routines WHERE buzzy_id = $buzzy ORDER BY created_at", ("$buzzy", buzzyId));

    public void SetEnabled(string routineId, bool enabled)
    {
        using var connection = Open();
        Execute(connection, "UPDATE routines SET enabled = $enabled WHERE id = $id", null, ("$enabled", enabled ? 1 : 0), ("$id", routineId));
    }

    /// <summary>Moves the routine's high-water mark forward; never backward, so two ticks racing
    /// can't make an occurrence look new again.</summary>
    public void SetLastScheduled(string routineId, DateTimeOffset at)
    {
        using var connection = Open();
        Execute(connection, """
            UPDATE routines SET last_scheduled_at = $at
            WHERE id = $id AND (last_scheduled_at IS NULL OR last_scheduled_at < $at)
            """, null, ("$at", Iso(at)), ("$id", routineId));
    }

    /// <summary>Deletes a routine and its run history.</summary>
    public void DeleteRoutine(string routineId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, "DELETE FROM routine_runs WHERE routine_id = $id", transaction, ("$id", routineId));
        Execute(connection, "DELETE FROM routines WHERE id = $id", transaction, ("$id", routineId));
        transaction.Commit();
    }

    /// <summary>Deletes every routine and run belonging to a Buzzy (when the Buzzy is deleted).</summary>
    public void DeleteForBuzzy(string buzzyId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, "DELETE FROM routine_runs WHERE buzzy_id = $buzzy", transaction, ("$buzzy", buzzyId));
        Execute(connection, "DELETE FROM routines WHERE buzzy_id = $buzzy", transaction, ("$buzzy", buzzyId));
        transaction.Commit();
    }

    // ---- Runs ----------------------------------------------------------------------------------

    /// <summary>Adds a run unless one already exists for the same routine and occurrence key.
    /// True when this call added it: the one claim that lets a scheduled occurrence run once.</summary>
    public bool TryAddRun(RoutineRun run)
    {
        using var connection = Open();
        return Execute(connection, """
            INSERT OR IGNORE INTO routine_runs (id, routine_id, buzzy_id, occurrence_key, trigger, status, queued_at,
                                                started_at, finished_at, session_id, summary, error, input_tokens,
                                                output_tokens, outputs)
            VALUES ($id, $routine, $buzzy, $key, $trigger, $status, $queued, $started, $finished, $session, $summary,
                    $error, $in, $out, $outputs)
            """, null,
            ("$id", run.Id), ("$routine", run.RoutineId), ("$buzzy", run.BuzzyId), ("$key", run.OccurrenceKey),
            ("$trigger", run.Trigger.ToString()), ("$status", run.Status.ToString()), ("$queued", Iso(run.QueuedAt)),
            ("$started", run.StartedAt is { } s ? Iso(s) : null), ("$finished", run.FinishedAt is { } f ? Iso(f) : null),
            ("$session", run.SessionId), ("$summary", run.Summary), ("$error", run.Error), ("$in", run.InputTokens),
            ("$out", run.OutputTokens), ("$outputs", JsonSerializer.Serialize(run.Outputs ?? []))) == 1;
    }

    /// <summary>Queued → Running, only if the run is still queued (a cancel may have won the race).</summary>
    public bool TryMarkRunning(string runId, string sessionId, DateTimeOffset startedAt)
    {
        using var connection = Open();
        return Execute(connection, """
            UPDATE routine_runs SET status = 'Running', session_id = $session, started_at = $started
            WHERE id = $id AND status = 'Queued'
            """, null, ("$session", sessionId), ("$started", Iso(startedAt)), ("$id", runId)) == 1;
    }

    /// <summary>Records how an active run ended. A run that already ended keeps its first ending.</summary>
    public bool Finish(string runId, RunStatus status, DateTimeOffset finishedAt, string? summary = null, string? error = null,
        int inputTokens = 0, int outputTokens = 0, IReadOnlyList<string>? outputs = null)
    {
        using var connection = Open();
        return Execute(connection, """
            UPDATE routine_runs SET status = $status, finished_at = $finished, summary = $summary, error = $error,
                                    input_tokens = $in, output_tokens = $out, outputs = $outputs
            WHERE id = $id AND status IN ('Queued', 'Running')
            """, null,
            ("$status", status.ToString()), ("$finished", Iso(finishedAt)), ("$summary", summary), ("$error", error),
            ("$in", inputTokens), ("$out", outputTokens), ("$outputs", JsonSerializer.Serialize(outputs ?? [])), ("$id", runId)) == 1;
    }

    public RoutineRun? GetRun(string runId) =>
        QueryRuns("SELECT * FROM routine_runs WHERE id = $id", ("$id", runId)).FirstOrDefault();

    public IReadOnlyList<RoutineRun> ListQueued() =>
        QueryRuns("SELECT * FROM routine_runs WHERE status = 'Queued' ORDER BY queued_at");

    /// <summary>Newest first; for one Buzzy, or all when <paramref name="buzzyId"/> is null.</summary>
    public IReadOnlyList<RoutineRun> ListRecentRuns(string? buzzyId, int limit) => buzzyId is null
        ? QueryRuns("SELECT * FROM routine_runs ORDER BY queued_at DESC, rowid DESC LIMIT $limit", ("$limit", limit))
        : QueryRuns("SELECT * FROM routine_runs WHERE buzzy_id = $buzzy ORDER BY queued_at DESC, rowid DESC LIMIT $limit",
            ("$buzzy", buzzyId), ("$limit", limit));

    /// <summary>On start: runs left Running by a Litos that stopped mid-run become Interrupted. They
    /// are shown to the user, never retried, since a half-finished run may already have written
    /// files. Returns how many there were.</summary>
    public int MarkInterrupted(DateTimeOffset now)
    {
        using var connection = Open();
        return Execute(connection, """
            UPDATE routine_runs SET status = 'Interrupted', finished_at = $now,
                   error = 'Litos stopped while this run was in progress. It was not retried.'
            WHERE status = 'Running'
            """, null, ("$now", Iso(now)));
    }

    // ---- Plumbing ------------------------------------------------------------------------------

    private IReadOnlyList<Routine> QueryRoutines(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        using var command = Command(connection, sql, null, parameters);
        using var reader = command.ExecuteReader();
        var routines = new List<Routine>();
        while (reader.Read())
        {
            routines.Add(new Routine(
                Id: reader.GetString(reader.GetOrdinal("id")),
                BuzzyId: reader.GetString(reader.GetOrdinal("buzzy_id")),
                Name: reader.GetString(reader.GetOrdinal("name")),
                Prompt: reader.GetString(reader.GetOrdinal("prompt")),
                Trigger: Enum.Parse<RoutineTrigger>(reader.GetString(reader.GetOrdinal("trigger"))),
                TimeOfDay: NullableString(reader, "time_of_day") is { } t ? TimeOnly.ParseExact(t, "HH:mm", CultureInfo.InvariantCulture) : null,
                Day: NullableInt(reader, "day_of_week") is { } d ? (DayOfWeek)d : null,
                IntervalMinutes: NullableInt(reader, "interval_minutes"),
                TimeZoneId: reader.GetString(reader.GetOrdinal("time_zone")),
                Enabled: reader.GetInt32(reader.GetOrdinal("enabled")) != 0,
                MaxMinutes: reader.GetInt32(reader.GetOrdinal("max_minutes")),
                MaxTokens: reader.GetInt32(reader.GetOrdinal("max_tokens")),
                MissedRuns: Enum.Parse<MissedRunPolicy>(reader.GetString(reader.GetOrdinal("missed_runs"))),
                CreatedAt: ParseIso(reader.GetString(reader.GetOrdinal("created_at"))),
                LastScheduledAt: NullableString(reader, "last_scheduled_at") is { } at ? ParseIso(at) : null));
        }

        return routines;
    }

    private IReadOnlyList<RoutineRun> QueryRuns(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        using var command = Command(connection, sql, null, parameters);
        using var reader = command.ExecuteReader();
        var runs = new List<RoutineRun>();
        while (reader.Read())
        {
            runs.Add(new RoutineRun(
                Id: reader.GetString(reader.GetOrdinal("id")),
                RoutineId: reader.GetString(reader.GetOrdinal("routine_id")),
                BuzzyId: reader.GetString(reader.GetOrdinal("buzzy_id")),
                OccurrenceKey: reader.GetString(reader.GetOrdinal("occurrence_key")),
                Trigger: Enum.Parse<RunTrigger>(reader.GetString(reader.GetOrdinal("trigger"))),
                Status: Enum.Parse<RunStatus>(reader.GetString(reader.GetOrdinal("status"))),
                QueuedAt: ParseIso(reader.GetString(reader.GetOrdinal("queued_at"))),
                StartedAt: NullableString(reader, "started_at") is { } s ? ParseIso(s) : null,
                FinishedAt: NullableString(reader, "finished_at") is { } f ? ParseIso(f) : null,
                SessionId: NullableString(reader, "session_id"),
                Summary: NullableString(reader, "summary"),
                Error: NullableString(reader, "error"),
                InputTokens: reader.GetInt32(reader.GetOrdinal("input_tokens")),
                OutputTokens: reader.GetInt32(reader.GetOrdinal("output_tokens")),
                Outputs: JsonSerializer.Deserialize<List<string>>(reader.GetString(reader.GetOrdinal("outputs"))) ?? []));
        }

        return runs;
    }

    private static int Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, transaction, parameters);
        return command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = Command(connection, sql, null, []);
        return command.ExecuteScalar();
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction,
        (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static string? NullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int? NullableInt(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    /// <summary>Round-trip UTC text, which also sorts correctly as a string.</summary>
    private static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
