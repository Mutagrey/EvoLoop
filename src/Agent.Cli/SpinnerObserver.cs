using System.Text;
using System.Text.RegularExpressions;
using Agent.Core;
using Agent.Tools;

namespace Agent.Cli;

internal sealed class SpinnerObserver : IAgentRunObserver, IDisposable
{
    private readonly AnsiRenderer _renderer;
    private readonly object _sync = new();
    private readonly List<string> _activityFeed = new();
    private readonly HashSet<string> _editedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _readFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _ranCommands = new();
    private readonly List<string> _searchNotes = new();
    private readonly List<string> _exploreNotes = new();
    private int? _lastAnnouncedStep;
    private DateTimeOffset _spinnerStartedAtUtc;
    private DateTimeOffset _sessionStartedAtUtc;
    private CancellationTokenSource? _spinnerCts;
    private Task? _spinnerTask;

    public SpinnerObserver(AnsiRenderer renderer)
    {
        _renderer = renderer;
    }

    public Task OnEventAsync(AgentRunEvent evt, CancellationToken ct)
    {
        switch (evt.Type)
        {
            case AgentRunEventType.SessionStarted:
                _sessionStartedAtUtc = DateTimeOffset.UtcNow;
                _renderer.WriteStatus("SESSION", evt.Message, ConsoleColor.Cyan);
                break;
            case AgentRunEventType.MemoryLoaded:
                _renderer.WriteStatus("MEMORY", evt.Message, ConsoleColor.DarkCyan);
                break;
            case AgentRunEventType.ContextCompacted:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("MEMORY", evt.Message, ConsoleColor.DarkCyan, depth: 1);
                break;
            case AgentRunEventType.ModelCallStarted:
                StartSpinner(evt.Step, evt.Message);
                break;
            case AgentRunEventType.ModelCallCompleted:
                StopSpinner();
                AnnounceStepIfNeeded(evt.Step);
                break;
            case AgentRunEventType.ModelProfileSwitched:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("MODEL", evt.Message, ConsoleColor.Cyan, depth: 1);
                break;
            case AgentRunEventType.ModelDecisionRecovered:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("RECOVER", evt.Message, ConsoleColor.Cyan, depth: 1);
                break;
            case AgentRunEventType.ModelResponseInvalid:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("WARN", evt.Message, ConsoleColor.Yellow, depth: 1);
                break;
            case AgentRunEventType.FinalRejectedRequiresTool:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("WARN", evt.Message, ConsoleColor.Yellow, depth: 1);
                break;
            case AgentRunEventType.ToolDecision:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("PLAN", evt.Message, ConsoleColor.Magenta, depth: 1);
                break;
            case AgentRunEventType.PolicyDenied:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("DENY", evt.Message, ConsoleColor.Red, depth: 2, isLast: true);
                break;
            case AgentRunEventType.ApprovalRequired:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("APPROVAL", evt.Message, ConsoleColor.Yellow, depth: 2);
                break;
            case AgentRunEventType.ApprovalGranted:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("APPROVAL", "Approved", ConsoleColor.Green, depth: 2);
                break;
            case AgentRunEventType.ApprovalRejected:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("APPROVAL", "Rejected", ConsoleColor.Red, depth: 2, isLast: true);
                break;
            case AgentRunEventType.ToolExecutionStarted:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus("RUN", evt.Message, ConsoleColor.Cyan, depth: 2);
                break;
            case AgentRunEventType.ToolExecutionCompleted:
                AnnounceStepIfNeeded(evt.Step);
                _renderer.WriteStatus(
                    "RESULT",
                    evt.Message,
                    evt.Message.Contains("failed", StringComparison.OrdinalIgnoreCase) ? ConsoleColor.Red : ConsoleColor.Green,
                    depth: 2,
                    isLast: true);
                TrackActivity(evt.Message);
                break;
            case AgentRunEventType.MemoryUpdated:
                _renderer.WriteStatus("MEMORY", evt.Message, ConsoleColor.DarkCyan);
                break;
            case AgentRunEventType.SessionCompleted:
                StopSpinner();
                _renderer.WriteStatus("DONE", evt.Message, ConsoleColor.Green);
                break;
            case AgentRunEventType.Error:
                StopSpinner();
                _renderer.WriteStatus("ERROR", evt.Message, ConsoleColor.Red);
                break;
        }

        return Task.CompletedTask;
    }

    private void AnnounceStepIfNeeded(int? step)
    {
        if (!step.HasValue)
        {
            return;
        }

        if (_lastAnnouncedStep.HasValue && _lastAnnouncedStep.Value == step.Value)
        {
            return;
        }

        _lastAnnouncedStep = step.Value;
        _renderer.WriteStatus("STEP", $"Step {step.Value}", ConsoleColor.Cyan);
    }

    public void Dispose()
    {
        StopSpinner();
    }

    public async Task WriteActivitySummaryAsync(string workspace, CancellationToken ct)
    {
        if (_activityFeed.Count == 0 &&
            _editedFiles.Count == 0 &&
            _readFiles.Count == 0 &&
            _searchNotes.Count == 0 &&
            _ranCommands.Count == 0)
        {
            return;
        }

        var sb = new StringBuilder();
        var diffStats = await ReadGitNumStatAsync(workspace, _editedFiles, ct);
        var elapsed = _sessionStartedAtUtc == default
            ? TimeSpan.Zero
            : DateTimeOffset.UtcNow - _sessionStartedAtUtc;

        sb.AppendLine(
            $"Reads: {_readFiles.Count}  Searches: {_searchNotes.Count}  Edits: {_editedFiles.Count}  Commands: {_ranCommands.Distinct(StringComparer.OrdinalIgnoreCase).Count()}  Elapsed: {elapsed.TotalSeconds:0.0}s");
        sb.AppendLine();

        if (_readFiles.Count > 0)
        {
            sb.AppendLine($"Read ({_readFiles.Count})");
            foreach (var file in _readFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).TakeLast(8))
            {
                sb.AppendLine($" - {file}");
            }
            sb.AppendLine();
        }

        if (_editedFiles.Count > 0)
        {
            sb.AppendLine($"Edited ({_editedFiles.Count})");
            foreach (var file in _editedFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (diffStats.TryGetValue(file, out var stat))
                {
                    sb.AppendLine($" - {file}  +{stat.Added} -{stat.Deleted}");
                }
                else
                {
                    sb.AppendLine($" - {file}");
                }
            }
            sb.AppendLine();
        }

        if (_exploreNotes.Count > 0 || _searchNotes.Count > 0)
        {
            sb.AppendLine($"Observed ({_exploreNotes.Count + _searchNotes.Count})");
            foreach (var note in _exploreNotes.Concat(_searchNotes).TakeLast(8))
            {
                sb.AppendLine($" - {note}");
            }
            sb.AppendLine();
        }

        if (_ranCommands.Count > 0)
        {
            sb.AppendLine($"Ran ({_ranCommands.Distinct(StringComparer.OrdinalIgnoreCase).Count()})");
            foreach (var cmd in _ranCommands.Distinct(StringComparer.OrdinalIgnoreCase).TakeLast(10))
            {
                sb.AppendLine($" - {cmd}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Timeline (latest)");
        foreach (var line in _activityFeed.TakeLast(14))
        {
            sb.AppendLine($" > {line}");
        }

        _renderer.WritePanel("Activity", sb.ToString().TrimEnd());
    }

    private void StartSpinner(int? step, string? hint)
    {
        if (!_renderer.SupportsTransientOutput)
        {
            return;
        }

        lock (_sync)
        {
            StopSpinner();
            _spinnerCts = new CancellationTokenSource();
            var token = _spinnerCts.Token;
            _spinnerStartedAtUtc = DateTimeOffset.UtcNow;
            var hintText = SimplifySpinnerHint(string.IsNullOrWhiteSpace(hint) ? "Analyzing next action" : hint);
            var stepToken = TryParseStepProgress(hintText, out var current, out var total)
                ? $"step {current}/{total} {BuildStepBar(current, total)}"
                : step.HasValue ? $"step {step.Value}" : "step";
            _spinnerTask = Task.Run(async () =>
            {
                var frames = new[] { ".  ", ".. ", "..." };
                var index = 0;
                var lastPrintedWidth = 0;
                while (!token.IsCancellationRequested)
                {
                    var elapsed = (DateTimeOffset.UtcNow - _spinnerStartedAtUtc).TotalSeconds;
                    var frame = frames[index++ % frames.Length];
                    var line = $"{stepToken}  {hintText}  {elapsed,5:0.0}s  {frame}";
                    line = FitInline(line, GetConsoleWidth() - 1);
                    var padded = line.PadRight(Math.Max(line.Length, lastPrintedWidth));
                    Console.Write($"\r{padded}");
                    lastPrintedWidth = padded.Length;
                    try
                    {
                        await Task.Delay(120, token);
                    }
                    catch
                    {
                        break;
                    }
                }

                Console.Write("\r" + new string(' ', Math.Max(20, lastPrintedWidth)) + "\r");
            }, token);
        }
    }

    private static bool TryParseStepProgress(string text, out int current, out int total)
    {
        current = 0;
        total = 0;
        var match = Regex.Match(text, @"Step\s+(\d+)\s*/\s*(\d+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups[1].Value, out current) ||
            !int.TryParse(match.Groups[2].Value, out total) ||
            total <= 0)
        {
            current = 0;
            total = 0;
            return false;
        }

        current = Math.Clamp(current, 0, total);
        return true;
    }

    private static string BuildStepBar(int current, int total)
    {
        const int width = 16;
        if (total <= 0)
        {
            return "[" + new string('.', width) + "]";
        }

        var filled = (int)Math.Round((current / (double)total) * width);
        filled = Math.Clamp(filled, 0, width);
        return "[" + new string('=', filled) + new string('.', width - filled) + "]";
    }

    private static int GetConsoleWidth()
    {
        try
        {
            return Math.Max(60, Console.WindowWidth);
        }
        catch
        {
            return 100;
        }
    }

    private static string FitInline(string text, int width)
    {
        if (width <= 0 || text.Length <= width)
        {
            return text;
        }

        if (width < 4)
        {
            return text[..width];
        }

        return text[..(width - 3)] + "...";
    }

    private void StopSpinner()
    {
        if (!_renderer.SupportsTransientOutput)
        {
            return;
        }

        lock (_sync)
        {
            if (_spinnerCts is null)
            {
                return;
            }

            _spinnerCts.Cancel();
            try
            {
                _spinnerTask?.Wait(200);
            }
            catch
            {
                // ignored
            }

            _spinnerTask = null;
            _spinnerCts.Dispose();
            _spinnerCts = null;
        }
    }

    private void TrackActivity(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var normalized = ToOneLine(message, 220);
        _activityFeed.Add(normalized);

        if (normalized.StartsWith("Read ", StringComparison.OrdinalIgnoreCase))
        {
            var subject = normalized[(normalized.IndexOf(' ') + 1)..];
            var file = NormalizePath(subject);
            if (!string.IsNullOrWhiteSpace(file))
            {
                _readFiles.Add(file);
            }
            return;
        }

        if (normalized.StartsWith("Listed ", StringComparison.OrdinalIgnoreCase))
        {
            _exploreNotes.Add(normalized);
            return;
        }

        if (normalized.StartsWith("Edited ", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Patched ", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Wrote ", StringComparison.OrdinalIgnoreCase))
        {
            var subject = normalized[(normalized.IndexOf(' ') + 1)..];
            var statIndex = subject.IndexOf("  +", StringComparison.Ordinal);
            if (statIndex >= 0)
            {
                subject = subject[..statIndex];
            }

            var file = NormalizePath(subject);
            if (!string.IsNullOrWhiteSpace(file))
            {
                _editedFiles.Add(file);
            }
            return;
        }

        if (normalized.StartsWith("Searched ", StringComparison.OrdinalIgnoreCase))
        {
            _searchNotes.Add(normalized);
            return;
        }

        if (normalized.StartsWith("Explored ", StringComparison.OrdinalIgnoreCase))
        {
            _exploreNotes.Add(normalized);
            return;
        }

        if (normalized.StartsWith("Ran ", StringComparison.OrdinalIgnoreCase))
        {
            var cmd = normalized["Ran ".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(cmd))
            {
                _ranCommands.Add(cmd);
            }
        }
    }

    private static async Task<Dictionary<string, (string Added, string Deleted)>> ReadGitNumStatAsync(
        string workspace,
        IReadOnlyCollection<string> editedFiles,
        CancellationToken ct)
    {
        var stats = new Dictionary<string, (string Added, string Deleted)>(StringComparer.OrdinalIgnoreCase);
        if (editedFiles.Count == 0)
        {
            return stats;
        }

        var args = new List<string> { "diff", "--numstat", "--" };
        foreach (var file in editedFiles)
        {
            args.Add(file);
        }

        ProcessExecutionResult result;
        try
        {
            result = await ProcessRunner.RunAsync("git", args, workspace, ct, 32 * 1024);
        }
        catch
        {
            return stats;
        }

        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return stats;
        }

        var lines = result.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            var file = NormalizePath(parts[2].Trim());
            if (string.IsNullOrWhiteSpace(file))
            {
                continue;
            }

            stats[file] = (parts[0], parts[1]);
        }

        return stats;
    }

    private static string NormalizePath(string path)
    {
        return path.Trim().Replace('\\', '/');
    }

    private static string ToOneLine(string value, int maxLen)
    {
        var oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (oneLine.Length <= maxLen)
        {
            return oneLine;
        }

        return oneLine[..maxLen] + "...";
    }

    private static string SimplifySpinnerHint(string hint)
    {
        var normalized = ToOneLine(hint, 110);
        if (normalized.StartsWith("Step ", StringComparison.OrdinalIgnoreCase))
        {
            var separator = normalized.IndexOf(':');
            if (separator >= 0 && separator + 1 < normalized.Length)
            {
                normalized = normalized[(separator + 1)..].Trim();
            }
        }

        return normalized switch
        {
            "Model response received" => "processing model response",
            _ => normalized.ToLowerInvariant()
        };
    }
}

