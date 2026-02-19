using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Agent.Tools;

public sealed record ProcessExecutionResult(int ExitCode, string StdOut, string StdErr, long DurationMs)
{
    public bool Success => ExitCode == 0;
}

public static class ProcessRunner
{
    public static async Task<ProcessExecutionResult> RunAsync(
        string fileName,
        IEnumerable<string> args,
        string workingDirectory,
        CancellationToken ct,
        int maxOutputBytes = 64 * 1024)
    {
        var stopwatch = Stopwatch.StartNew();

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        ApplyDotnetPrivacyEnv(psi);

        using var process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        stopwatch.Stop();

        return new ProcessExecutionResult(
            process.ExitCode,
            TruncateUtf8(stdout, maxOutputBytes),
            TruncateUtf8(stderr, maxOutputBytes),
            stopwatch.ElapsedMilliseconds);
    }

    public static Task<ProcessExecutionResult> RunShellAsync(
        string command,
        string workingDirectory,
        CancellationToken ct,
        int maxOutputBytes = 64 * 1024)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return RunAsync("cmd.exe", new[] { "/c", command }, workingDirectory, ct, maxOutputBytes);
        }

        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrWhiteSpace(shell))
        {
            shell = "/bin/sh";
        }

        return RunAsync(shell, new[] { "-lc", command }, workingDirectory, ct, maxOutputBytes);
    }

    public static bool CommandExists(string command)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add(command);
            ApplyDotnetPrivacyEnv(psi);

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(500);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string TruncateUtf8(string input, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(input) <= maxBytes)
        {
            return input;
        }

        var value = input;
        while (value.Length > 0 && Encoding.UTF8.GetByteCount(value) > maxBytes)
        {
            value = value[..Math.Max(0, value.Length - 128)];
        }

        return value + "\n[truncated]";
    }

    private static void ApplyDotnetPrivacyEnv(ProcessStartInfo psi)
    {
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
    }
}
