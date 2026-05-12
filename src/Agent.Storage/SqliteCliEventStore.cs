using System.Diagnostics;
using Agent.Core;

namespace Agent.Storage;

public sealed class SqliteCliEventStore : IEventStore
{
    private readonly string _dbPath;

    public SqliteCliEventStore(string workspaceRoot)
    {
        var storageRoot = Path.Combine(workspaceRoot, ".evoloop", "storage");
        Directory.CreateDirectory(storageRoot);
        _dbPath = Path.Combine(storageRoot, "agent.db");
        EnsureSchema();
    }

    public static bool IsAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sqlite3",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(1000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public Task<SessionInfo> StartSessionAsync(string workspaceRoot, string profile, string task, CancellationToken ct)
    {
        var session = new SessionInfo(Guid.NewGuid().ToString("n"), DateTimeOffset.UtcNow);
        return StartSessionProjectionAsync(session, workspaceRoot, profile, task, ct)
            .ContinueWith(_ => session, ct);
    }

    internal Task StartSessionProjectionAsync(SessionInfo session, string workspaceRoot, string profile, string task, CancellationToken ct)
    {
        var sql = $@"
INSERT INTO sessions(id, started_at_utc, workspace_root, profile, task, status)
VALUES('{Esc(session.SessionId)}', '{Esc(session.StartedAtUtc.ToString("O"))}', '{Esc(workspaceRoot)}', '{Esc(profile)}', '{Esc(task)}', 'running');";

        Execute(sql);
        return Task.CompletedTask;
    }

    public Task AppendStepAsync(SessionStep step, CancellationToken ct)
    {
        var sql = $@"
INSERT INTO steps(session_id, step_number, action, tool_name, reasoning, success, output, timestamp_utc, duration_ms, error)
VALUES(
    '{Esc(step.SessionId)}',
    {step.StepNumber},
    '{Esc(step.Action)}',
    '{Esc(step.ToolName)}',
    '{Esc(step.Reasoning)}',
    {(step.Success ? 1 : 0)},
    '{Esc(step.Output)}',
    '{Esc(step.TimestampUtc.ToString("O"))}',
    {step.DurationMs},
    {(step.Error is null ? "NULL" : $"'{Esc(step.Error)}'")}
);";

        Execute(sql);
        return Task.CompletedTask;
    }

    public Task CompleteSessionAsync(string sessionId, string finalStatus, CancellationToken ct)
    {
        var sql = $@"
UPDATE sessions
SET status = '{Esc(finalStatus)}', completed_at_utc = '{Esc(DateTimeOffset.UtcNow.ToString("O"))}'
WHERE id = '{Esc(sessionId)}';";

        Execute(sql);
        return Task.CompletedTask;
    }

    private void EnsureSchema()
    {
        var sql = @"
CREATE TABLE IF NOT EXISTS sessions (
    id TEXT PRIMARY KEY,
    started_at_utc TEXT NOT NULL,
    completed_at_utc TEXT NULL,
    workspace_root TEXT NOT NULL,
    profile TEXT NOT NULL,
    task TEXT NOT NULL,
    status TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS steps (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    step_number INTEGER NOT NULL,
    action TEXT NOT NULL,
    tool_name TEXT NOT NULL,
    reasoning TEXT NOT NULL,
    success INTEGER NOT NULL,
    output TEXT NOT NULL,
    timestamp_utc TEXT NOT NULL,
    duration_ms INTEGER NOT NULL,
    error TEXT NULL,
    FOREIGN KEY(session_id) REFERENCES sessions(id)
);";

        Execute(sql);
    }

    private void Execute(string sql)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sqlite3",
            ArgumentList = { _dbPath, sql },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start sqlite3 process.");
        }

        process.WaitForExit();
        var stderr = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"sqlite3 command failed: {stderr}");
        }
    }

    private static string Esc(string value)
    {
        return value.Replace("'", "''");
    }
}
