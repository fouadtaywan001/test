using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pusher.Core.Abstractions;
using Pusher.Core.Models;

namespace Pusher.Storage;

/// <summary>
/// SQLite implementation of <see cref="IStateStore"/>. Single-file DB in ProgramData,
/// WAL mode, forward-only schema migrations keyed on user_version.
/// </summary>
public sealed class SqliteStateStore : IStateStore
{
    private readonly string _connectionString;

    public SqliteStateStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(cmd.ExecuteScalar());

        if (version < 1)
        {
            using var tx = conn.BeginTransaction();
            using var ddl = conn.CreateCommand();
            ddl.Transaction = tx;
            ddl.CommandText = """
                CREATE TABLE IF NOT EXISTS projects (
                    id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    name           TEXT NOT NULL,
                    local_path     TEXT NOT NULL,
                    staging_path   TEXT NULL,
                    remote_url     TEXT NOT NULL,
                    branch         TEXT NOT NULL DEFAULT 'main',
                    status         INTEGER NOT NULL DEFAULT 0,
                    script_text    TEXT NOT NULL,
                    script_version TEXT NOT NULL DEFAULT '1.0',
                    script_seed    INTEGER NOT NULL DEFAULT 0,
                    created_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS commit_plans (
                    id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id     INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                    ord            INTEGER NOT NULL,
                    message        TEXT NOT NULL,
                    body           TEXT NOT NULL DEFAULT '',
                    file_globs     TEXT NOT NULL,   -- JSON array
                    resolved_files TEXT NOT NULL,   -- JSON array (snapshot at activation)
                    mode           INTEGER NOT NULL DEFAULT 0,
                    status         INTEGER NOT NULL DEFAULT 0,
                    commit_sha     TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_commit_plans_project ON commit_plans(project_id);
                CREATE INDEX IF NOT EXISTS ix_commit_plans_status  ON commit_plans(status);

                CREATE TABLE IF NOT EXISTS slots (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    commit_plan_id  INTEGER NOT NULL REFERENCES commit_plans(id) ON DELETE CASCADE,
                    scheduled_at_utc TEXT NOT NULL,
                    executed_at_utc  TEXT NULL,
                    result          INTEGER NOT NULL DEFAULT 0,
                    log             TEXT NOT NULL DEFAULT ''
                );
                CREATE INDEX IF NOT EXISTS ix_slots_scheduled ON slots(scheduled_at_utc);

                CREATE TABLE IF NOT EXISTS hooks (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_id   INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
                    kind         TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    status       INTEGER NOT NULL DEFAULT 0
                );

                PRAGMA user_version = 1;
                """;
            ddl.ExecuteNonQuery();
            tx.Commit();
        }
        if (version < 2)
        {
            using var tx = conn.BeginTransaction();
            using var ddl = conn.CreateCommand();
            ddl.Transaction = tx;
            // mode: 0 = RealTime (existing projects keep worker-driven behavior), 1 = Backdated.
            ddl.CommandText = """
                ALTER TABLE projects ADD COLUMN mode INTEGER NOT NULL DEFAULT 0;
                PRAGMA user_version = 2;
                """;
            ddl.ExecuteNonQuery();
            tx.Commit();
        }
        // Future migrations: if (version < 3) { ... PRAGMA user_version = 3; }
    }

    // ---------------- Projects ----------------

    public Project? GetProject(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM projects WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadProject(r) : null;
    }

    public IReadOnlyList<Project> GetAllProjects()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM projects ORDER BY created_at_utc DESC;";
        using var r = cmd.ExecuteReader();
        var list = new List<Project>();
        while (r.Read()) list.Add(ReadProject(r));
        return list;
    }

    public long UpsertProject(Project p)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        if (p.Id == 0)
        {
            cmd.CommandText = """
                INSERT INTO projects (name, local_path, staging_path, remote_url, branch, status, mode,
                                      script_text, script_version, script_seed, created_at_utc)
                VALUES ($name, $path, $staging, $remote, $branch, $status, $mode, $script, $ver, $seed, $created)
                RETURNING id;
                """;
        }
        else
        {
            cmd.CommandText = """
                UPDATE projects SET name=$name, local_path=$path, staging_path=$staging, remote_url=$remote,
                                    branch=$branch, status=$status, mode=$mode, script_text=$script, script_version=$ver,
                                    script_seed=$seed, created_at_utc=$created
                WHERE id=$id RETURNING id;
                """;
            cmd.Parameters.AddWithValue("$id", p.Id);
        }
        cmd.Parameters.AddWithValue("$name", p.Name);
        cmd.Parameters.AddWithValue("$path", p.LocalPath);
        cmd.Parameters.AddWithValue("$staging", (object?)p.StagingPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$remote", p.RemoteUrl);
        cmd.Parameters.AddWithValue("$branch", p.Branch);
        cmd.Parameters.AddWithValue("$status", (int)p.Status);
        cmd.Parameters.AddWithValue("$mode", (int)p.Mode);
        cmd.Parameters.AddWithValue("$script", p.ScriptText);
        cmd.Parameters.AddWithValue("$ver", p.ScriptVersion);
        cmd.Parameters.AddWithValue("$seed", p.ScriptSeed);
        cmd.Parameters.AddWithValue("$created", p.CreatedAtUtc.ToString("O"));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void DeleteProject(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM projects WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ---------------- Commit plans ----------------

    public IReadOnlyList<CommitPlan> GetCommitPlans(long projectId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM commit_plans WHERE project_id = $p ORDER BY ord;";
        cmd.Parameters.AddWithValue("$p", projectId);
        using var r = cmd.ExecuteReader();
        var list = new List<CommitPlan>();
        while (r.Read()) list.Add(ReadCommitPlan(r));
        return list;
    }

    public void SaveCommitPlans(long projectId, IReadOnlyList<CommitPlan> plans)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var plan in plans)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO commit_plans (project_id, ord, message, body, file_globs, resolved_files, mode, status, commit_sha)
                VALUES ($p, $ord, $msg, $body, $globs, $files, $mode, $status, $sha)
                RETURNING id;
                """;
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.Parameters.AddWithValue("$ord", plan.Order);
            cmd.Parameters.AddWithValue("$msg", plan.Message);
            cmd.Parameters.AddWithValue("$body", plan.Body);
            cmd.Parameters.AddWithValue("$globs", JsonSerializer.Serialize(plan.FileGlobs));
            cmd.Parameters.AddWithValue("$files", JsonSerializer.Serialize(plan.ResolvedFiles));
            cmd.Parameters.AddWithValue("$mode", (int)plan.Mode);
            cmd.Parameters.AddWithValue("$status", (int)plan.Status);
            cmd.Parameters.AddWithValue("$sha", (object?)plan.CommitSha ?? DBNull.Value);
            plan.Id = Convert.ToInt64(cmd.ExecuteScalar());
        }
        tx.Commit();
    }

    public IReadOnlyList<CommitPlan> GetRecoverableCommits()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM commit_plans WHERE status = {(int)CommitPlanStatus.Committed};";
        using var r = cmd.ExecuteReader();
        var list = new List<CommitPlan>();
        while (r.Read()) list.Add(ReadCommitPlan(r));
        return list;
    }

    public void MarkCommitted(long id, string sha)
    {
        SetCommitStatus(id, CommitPlanStatus.Committed, sha);
    }

    public void MarkPushed(long id) => SetCommitStatus(id, CommitPlanStatus.Pushed, null);
    public void MarkFailed(long id) => SetCommitStatus(id, CommitPlanStatus.Failed, null);
    public void MarkSkipped(long id) => SetCommitStatus(id, CommitPlanStatus.Skipped, null);

    public void ResetFailed(long projectId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        // Slots first: requeue only slots whose plan is Failed for this project.
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                UPDATE slots SET result={(int)SlotResult.None}, executed_at_utc=NULL, log=''
                WHERE commit_plan_id IN
                    (SELECT id FROM commit_plans WHERE project_id=$p AND status={(int)CommitPlanStatus.Failed});
                """;
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                UPDATE commit_plans SET status={(int)CommitPlanStatus.Pending}, commit_sha=NULL
                WHERE project_id=$p AND status={(int)CommitPlanStatus.Failed};
                """;
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void DeletePendingPlans(long projectId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                DELETE FROM slots WHERE commit_plan_id IN
                    (SELECT id FROM commit_plans WHERE project_id=$p AND status={(int)CommitPlanStatus.Pending});
                """;
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM commit_plans WHERE project_id=$p AND status={(int)CommitPlanStatus.Pending};";
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void UpdatePendingCommitMessage(long commitPlanId, string message)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        // Guarded on status: an edit racing the worker can never rewrite a pushed commit.
        cmd.CommandText = $"UPDATE commit_plans SET message=$m WHERE id=$id AND status={(int)CommitPlanStatus.Pending};";
        cmd.Parameters.AddWithValue("$m", message);
        cmd.Parameters.AddWithValue("$id", commitPlanId);
        cmd.ExecuteNonQuery();
    }

    public void UpdatePendingSlotSchedule(long commitPlanId, DateTimeOffset scheduledAtUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE slots SET scheduled_at_utc=$at
            WHERE commit_plan_id=$id AND result={(int)SlotResult.None}
              AND (SELECT status FROM commit_plans WHERE id=$id) = {(int)CommitPlanStatus.Pending};
            """;
        cmd.Parameters.AddWithValue("$at", scheduledAtUtc.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$id", commitPlanId);
        cmd.ExecuteNonQuery();
    }

    private void SetCommitStatus(long id, CommitPlanStatus status, string? sha)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sha is null
            ? "UPDATE commit_plans SET status=$s WHERE id=$id;"
            : "UPDATE commit_plans SET status=$s, commit_sha=$sha WHERE id=$id;";
        cmd.Parameters.AddWithValue("$s", (int)status);
        cmd.Parameters.AddWithValue("$id", id);
        if (sha is not null) cmd.Parameters.AddWithValue("$sha", sha);
        cmd.ExecuteNonQuery();
    }

    // ---------------- Slots ----------------

    public IReadOnlyList<Slot> GetSlots(long projectId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.* FROM slots s
            JOIN commit_plans cp ON cp.id = s.commit_plan_id
            WHERE cp.project_id = $p
            ORDER BY s.scheduled_at_utc;
            """;
        cmd.Parameters.AddWithValue("$p", projectId);
        using var r = cmd.ExecuteReader();
        var list = new List<Slot>();
        while (r.Read()) list.Add(ReadSlot(r));
        return list;
    }

    public void SaveSlots(long projectId, IReadOnlyList<Slot> slots)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var slot in slots)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO slots (commit_plan_id, scheduled_at_utc, executed_at_utc, result, log)
                VALUES ($cp, $at, $exec, $res, $log) RETURNING id;
                """;
            cmd.Parameters.AddWithValue("$cp", slot.CommitPlanId);
            cmd.Parameters.AddWithValue("$at", slot.ScheduledAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$exec", (object?)slot.ExecutedAtUtc?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$res", (int)slot.Result);
            cmd.Parameters.AddWithValue("$log", slot.Log);
            slot.Id = Convert.ToInt64(cmd.ExecuteScalar());
        }
        tx.Commit();
    }

    public IReadOnlyList<Slot> GetDueSlots(DateTimeOffset asOfUtc)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT s.* FROM slots s
            JOIN commit_plans cp ON cp.id = s.commit_plan_id
            JOIN projects p ON p.id = cp.project_id
            WHERE s.scheduled_at_utc <= $now
              AND s.result = {(int)SlotResult.None}
              AND cp.status IN ({(int)CommitPlanStatus.Pending}, {(int)CommitPlanStatus.Committed})
              AND p.status = {(int)ProjectStatus.Active}
            ORDER BY s.scheduled_at_utc;
            """;
        cmd.Parameters.AddWithValue("$now", asOfUtc.ToString("O"));
        using var r = cmd.ExecuteReader();
        var list = new List<Slot>();
        while (r.Read()) list.Add(ReadSlot(r));
        return list;
    }

    public void RecordSlotResult(long slotId, SlotResult result, DateTimeOffset executedAtUtc, string log)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE slots SET result=$r, executed_at_utc=$at, log=$log WHERE id=$id;";
        cmd.Parameters.AddWithValue("$r", (int)result);
        cmd.Parameters.AddWithValue("$at", executedAtUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$log", log);
        cmd.Parameters.AddWithValue("$id", slotId);
        cmd.ExecuteNonQuery();
    }

    // ---------------- Hooks ----------------

    public IReadOnlyList<HookAction> GetPendingHooks(long projectId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM hooks WHERE project_id=$p AND status={(int)CommitPlanStatus.Pending};";
        cmd.Parameters.AddWithValue("$p", projectId);
        using var r = cmd.ExecuteReader();
        var list = new List<HookAction>();
        while (r.Read())
        {
            list.Add(new HookAction
            {
                Id = r.GetInt64(r.GetOrdinal("id")),
                ProjectId = r.GetInt64(r.GetOrdinal("project_id")),
                Kind = r.GetString(r.GetOrdinal("kind")),
                PayloadJson = r.GetString(r.GetOrdinal("payload_json")),
                Status = (CommitPlanStatus)r.GetInt32(r.GetOrdinal("status")),
            });
        }
        return list;
    }

    public void SaveHooks(long projectId, IReadOnlyList<HookAction> hooks)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var hook in hooks)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO hooks (project_id, kind, payload_json, status)
                VALUES ($p, $kind, $payload, $status) RETURNING id;
                """;
            cmd.Parameters.AddWithValue("$p", projectId);
            cmd.Parameters.AddWithValue("$kind", hook.Kind);
            cmd.Parameters.AddWithValue("$payload", hook.PayloadJson);
            cmd.Parameters.AddWithValue("$status", (int)hook.Status);
            hook.Id = Convert.ToInt64(cmd.ExecuteScalar());
        }
        tx.Commit();
    }

    public void SetHookStatus(long hookId, CommitPlanStatus status)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE hooks SET status=$s WHERE id=$id;";
        cmd.Parameters.AddWithValue("$s", (int)status);
        cmd.Parameters.AddWithValue("$id", hookId);
        cmd.ExecuteNonQuery();
    }

    // ---------------- Readers ----------------

    private static Project ReadProject(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        Name = r.GetString(r.GetOrdinal("name")),
        LocalPath = r.GetString(r.GetOrdinal("local_path")),
        StagingPath = r.IsDBNull(r.GetOrdinal("staging_path")) ? null : r.GetString(r.GetOrdinal("staging_path")),
        RemoteUrl = r.GetString(r.GetOrdinal("remote_url")),
        Branch = r.GetString(r.GetOrdinal("branch")),
        Status = (ProjectStatus)r.GetInt32(r.GetOrdinal("status")),
        Mode = (ProjectMode)r.GetInt32(r.GetOrdinal("mode")),
        ScriptText = r.GetString(r.GetOrdinal("script_text")),
        ScriptVersion = r.GetString(r.GetOrdinal("script_version")),
        ScriptSeed = r.GetInt32(r.GetOrdinal("script_seed")),
        CreatedAtUtc = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("created_at_utc"))),
    };

    private static CommitPlan ReadCommitPlan(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        ProjectId = r.GetInt64(r.GetOrdinal("project_id")),
        Order = r.GetInt32(r.GetOrdinal("ord")),
        Message = r.GetString(r.GetOrdinal("message")),
        Body = r.GetString(r.GetOrdinal("body")),
        FileGlobs = JsonSerializer.Deserialize<List<string>>(r.GetString(r.GetOrdinal("file_globs")))!,
        ResolvedFiles = JsonSerializer.Deserialize<List<string>>(r.GetString(r.GetOrdinal("resolved_files")))!,
        Mode = (CommitMode)r.GetInt32(r.GetOrdinal("mode")),
        Status = (CommitPlanStatus)r.GetInt32(r.GetOrdinal("status")),
        CommitSha = r.IsDBNull(r.GetOrdinal("commit_sha")) ? null : r.GetString(r.GetOrdinal("commit_sha")),
    };

    private static Slot ReadSlot(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        CommitPlanId = r.GetInt64(r.GetOrdinal("commit_plan_id")),
        ScheduledAtUtc = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("scheduled_at_utc"))),
        ExecutedAtUtc = r.IsDBNull(r.GetOrdinal("executed_at_utc")) ? null : DateTimeOffset.Parse(r.GetString(r.GetOrdinal("executed_at_utc"))),
        Result = (SlotResult)r.GetInt32(r.GetOrdinal("result")),
        Log = r.GetString(r.GetOrdinal("log")),
    };
}
