using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ScriptSuite.Models;

namespace ScriptSuite.Services;

/// <summary>
/// Run history persistence. SQLite at ~/.local/share/scriptsuite/history.db
/// (XDG counterpart of %LocalAppData%\ScriptSuite\history.db on Windows).
/// Schema matches windows-scripts exactly so history semantics stay identical.
/// Ported verbatim apart from the storage path (injected via AppPaths).
/// </summary>
public sealed class RunHistoryStore
{
    private readonly string _dbPath;

    public RunHistoryStore(string dbPath)
    {
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS RunHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ScriptId TEXT NOT NULL,
                StartedAt TEXT NOT NULL,
                FinishedAt TEXT,
                Outcome TEXT NOT NULL,
                Summary TEXT
            );
            CREATE TABLE IF NOT EXISTS ScheduledRuns (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ScriptId TEXT NOT NULL,
                ScheduledFor TEXT NOT NULL,
                StartedAt TEXT NOT NULL,
                FinishedAt TEXT,
                Outcome TEXT NOT NULL CHECK(Outcome IN ('Success','Warning','Failed','SkippedBusy','Cancelled')),
                Summary TEXT,
                Trigger TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public long Insert(string scriptId, DateTime startedAt, DateTime finishedAt, RunOutcome outcome, string? summary)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO RunHistory (ScriptId, StartedAt, FinishedAt, Outcome, Summary)
            VALUES ($scriptId, $startedAt, $finishedAt, $outcome, $summary);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$scriptId", scriptId);
        cmd.Parameters.AddWithValue("$startedAt", startedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$finishedAt", finishedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$outcome", OutcomeToText(outcome));
        cmd.Parameters.AddWithValue("$summary", (object?)summary ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    public List<RunHistoryEntry> GetRecent(int limit = 200)
    {
        var rows = new List<RunHistoryEntry>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, ScriptId, StartedAt, FinishedAt, Outcome, Summary FROM RunHistory ORDER BY Id DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new RunHistoryEntry
            {
                Id = reader.GetInt64(0),
                ScriptId = reader.GetString(1),
                StartedAt = reader.IsDBNull(2) ? null : reader.GetString(2),
                FinishedAt = reader.IsDBNull(3) ? null : reader.GetString(3),
                Outcome = reader.GetString(4),
                Summary = reader.IsDBNull(5) ? null : reader.GetString(5),
            });
        }
        return rows;
    }

    public Dictionary<string, RunHistoryEntry> GetLatestByScript()
    {
        var map = new Dictionary<string, RunHistoryEntry>(StringComparer.OrdinalIgnoreCase);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, ScriptId, StartedAt, FinishedAt, Outcome, Summary FROM RunHistory
            WHERE Id IN (SELECT MAX(Id) FROM RunHistory GROUP BY ScriptId);
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var entry = new RunHistoryEntry
            {
                Id = reader.GetInt64(0),
                ScriptId = reader.GetString(1),
                StartedAt = reader.IsDBNull(2) ? null : reader.GetString(2),
                FinishedAt = reader.IsDBNull(3) ? null : reader.GetString(3),
                Outcome = reader.GetString(4),
                Summary = reader.IsDBNull(5) ? null : reader.GetString(5),
            };
            map[entry.ScriptId] = entry;
        }
        return map;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA busy_timeout=5000;";
            cmd.ExecuteNonQuery();
        }
        return conn;
    }

    public static string? BuildSummary(IReadOnlyList<string> logs)
    {
        foreach (var raw in logs.Reverse())
        {
            var line = raw.StartsWith("INFO: ") ? raw[6..] : raw;
            if (Regex.IsMatch(line, @":\s*\d+\s"))
                return line.Length <= 120 ? line : line[..117] + "...";
        }
        var last = logs.LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (last is null) return null;
        return last.Length <= 120 ? last : last[..117] + "...";
    }

    public long InsertScheduled(string scriptId, DateTime scheduledFor, DateTime startedAt, DateTime finishedAt, string outcome, string? summary, string trigger)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ScheduledRuns (ScriptId, ScheduledFor, StartedAt, FinishedAt, Outcome, Summary, Trigger)
            VALUES ($scriptId, $scheduledFor, $startedAt, $finishedAt, $outcome, $summary, $trigger);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$scriptId", scriptId);
        cmd.Parameters.AddWithValue("$scheduledFor", scheduledFor.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$startedAt", startedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$finishedAt", finishedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("$outcome", outcome);
        cmd.Parameters.AddWithValue("$summary", (object?)summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$trigger", trigger);
        return (long)cmd.ExecuteScalar()!;
    }

    public List<ScheduledRunEntry> GetRecentScheduled(int limit = 200)
    {
        var rows = new List<ScheduledRunEntry>();
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, ScriptId, ScheduledFor, StartedAt, FinishedAt, Outcome, Summary, Trigger FROM ScheduledRuns ORDER BY Id DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ScheduledRunEntry
            {
                Id = reader.GetInt64(0),
                ScriptId = reader.GetString(1),
                ScheduledFor = reader.GetString(2),
                StartedAt = reader.GetString(3),
                FinishedAt = reader.IsDBNull(4) ? null : reader.GetString(4),
                Outcome = reader.GetString(5),
                Summary = reader.IsDBNull(6) ? null : reader.GetString(6),
                Trigger = reader.GetString(7),
            });
        }
        return rows;
    }

    private static string OutcomeToText(RunOutcome o) => o switch
    {
        RunOutcome.Success => "Success",
        RunOutcome.Warning => "Warning",
        RunOutcome.Failed => "Failed",
        RunOutcome.Cancelled => "Cancelled",
        _ => "Failed",
    };
}

public sealed class RunHistoryEntry
{
    public long Id { get; set; }
    public string ScriptId { get; set; } = "";
    public string? StartedAt { get; set; }
    public string? FinishedAt { get; set; }
    public string Outcome { get; set; } = "";
    public string? Summary { get; set; }
}

public sealed class ScheduledRunEntry
{
    public long Id { get; set; }
    public string ScriptId { get; set; } = "";
    public string ScheduledFor { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string? FinishedAt { get; set; }
    public string Outcome { get; set; } = "";
    public string? Summary { get; set; }
    public string Trigger { get; set; } = "";
}
