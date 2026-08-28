using System.Diagnostics;

namespace PCCExecutive.Updater;

internal static class Program
{
    private const int Success = 0;
    private const int UsageError = 2;
    private const int UnsafePath = 10;
    private const int AppMissing = 20;
    private const int AppControlFailed = 30;
    private const int CheckpointMissing = 31;
    private const int AppStillRunning = 32;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args);
        }
        catch (Exception ex)
        {
            Log($"UNHANDLED {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine($"PCC Executive updater failed: {ex.Message}");
            return 99;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintUsage();
            return Success;
        }

        var command = args[0].ToLowerInvariant();
        var options = ParseOptions(args.Skip(1).ToArray());

        return command switch
        {
            "prepare-installer-upgrade" => await PrepareAsync(options, requireAttempt: false),
            "prepare-update" => await PrepareAsync(options, requireAttempt: true),
            "post-install-verify" => await ForwardToApplicationAsync("verify", options, requireCheckpoint: true),
            "restore-update-checkpoint" => await ForwardToApplicationAsync("restore", options, requireCheckpoint: true),
            _ => Usage($"Unknown command '{command}'.")
        };
    }

    private static async Task<int> PrepareAsync(
        IReadOnlyDictionary<string, string> options,
        bool requireAttempt)
    {
        if (!TryGetRequired(options, "--backup-root", out var backupRoot))
        {
            return Usage("--backup-root is required.");
        }

        if (requireAttempt && !TryGetRequired(options, "--attempt", out _))
        {
            return Usage("--attempt is required.");
        }

        if (!TryNormalizeBackupRoot(backupRoot, out var normalizedBackup))
        {
            Console.Error.WriteLine("Backup root is outside the PCC Executive durable backup boundary.");
            return UnsafePath;
        }

        Directory.CreateDirectory(normalizedBackup);
        var forwarded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in options)
        {
            forwarded[pair.Key] = pair.Value;
        }
        forwarded["--backup-root"] = normalizedBackup;

        var result = await RunApplicationControlAsync("prepare", forwarded);
        if (result != Success)
        {
            return result;
        }

        var checkpointPath = Path.Combine(normalizedBackup, "checkpoint.json");
        if (!File.Exists(checkpointPath))
        {
            Log($"CHECKPOINT_MISSING root={normalizedBackup}");
            Console.Error.WriteLine($"Application preparation returned success but checkpoint.json is missing: {checkpointPath}");
            return CheckpointMissing;
        }

        await Task.Delay(500);
        var running = Process.GetProcessesByName("PCCExecutive");
        try
        {
            if (running.Any(process => !process.HasExited))
            {
                Log($"APP_STILL_RUNNING count={running.Length}");
                Console.Error.WriteLine("PCC Executive is still running after update preparation. No installer files should be replaced.");
                return AppStillRunning;
            }
        }
        finally
        {
            foreach (var process in running)
            {
                process.Dispose();
            }
        }

        Log($"PREPARED checkpoint={checkpointPath}");
        return Success;
    }

    private static async Task<int> ForwardToApplicationAsync(
        string verb,
        IReadOnlyDictionary<string, string> options,
        bool requireCheckpoint)
    {
        if (!TryGetRequired(options, "--backup-root", out var backupRoot))
        {
            return Usage("--backup-root is required.");
        }

        if (!TryGetRequired(options, "--attempt", out _))
        {
            return Usage("--attempt is required.");
        }

        if (!TryNormalizeBackupRoot(backupRoot, out var normalizedBackup))
        {
            Console.Error.WriteLine("Backup root is outside the PCC Executive durable backup boundary.");
            return UnsafePath;
        }

        if (requireCheckpoint && !File.Exists(Path.Combine(normalizedBackup, "checkpoint.json")))
        {
            Console.Error.WriteLine("checkpoint.json is required for verify/restore.");
            return CheckpointMissing;
        }

        var forwarded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in options)
        {
            forwarded[pair.Key] = pair.Value;
        }
        forwarded["--backup-root"] = normalizedBackup;

        return await RunApplicationControlAsync(verb, forwarded);
    }

    private static async Task<int> RunApplicationControlAsync(
        string verb,
        IReadOnlyDictionary<string, string> options)
    {
        var installRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))
            ?.FullName;

        if (string.IsNullOrWhiteSpace(installRoot))
        {
            Console.Error.WriteLine("Unable to resolve PCC Executive installation root.");
            return AppMissing;
        }

        var appPath = Path.Combine(installRoot, "PCCExecutive.exe");
        if (!File.Exists(appPath))
        {
            Log($"APP_MISSING path={appPath}");
            Console.Error.WriteLine($"PCCExecutive.exe is missing: {appPath}");
            return AppMissing;
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = appPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        process.StartInfo.ArgumentList.Add("--update-control");
        process.StartInfo.ArgumentList.Add(verb);

        foreach (var pair in options.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            process.StartInfo.ArgumentList.Add(pair.Key);
            process.StartInfo.ArgumentList.Add(pair.Value);
        }

        Log($"APP_CONTROL_START verb={verb}");

        if (!process.Start())
        {
            Console.Error.WriteLine("Failed to start PCC Executive update-control boundary.");
            return AppControlFailed;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            Console.WriteLine(stdout.Trim());
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            Console.Error.WriteLine(stderr.Trim());
        }

        Log($"APP_CONTROL_EXIT verb={verb} exit={process.ExitCode}");

        return process.ExitCode == 0 ? Success : AppControlFailed;
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{key}'.");
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Option '{key}' requires a value.");
            }

            result[key] = args[++i];
        }

        return result;
    }

    private static bool TryGetRequired(
        IReadOnlyDictionary<string, string> options,
        string name,
        out string value)
    {
        if (options.TryGetValue(name, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            value = raw;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryNormalizeBackupRoot(string value, out string normalized)
    {
        normalized = Path.GetFullPath(value);

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataRootOverride = Environment.GetEnvironmentVariable("PCCEXECUTIVE_DATA_ROOT");
        var allowOverride = Environment.GetEnvironmentVariable("PCCEXECUTIVE_SMOKE_MODE") == "1";

        var dataRoot = allowOverride && !string.IsNullOrWhiteSpace(dataRootOverride)
            ? Path.GetFullPath(dataRootOverride)
            : Path.Combine(localData, "PCC Executive");

        var allowedRoot = Path.GetFullPath(Path.Combine(dataRoot, "Backups"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        var candidate = normalized.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHelp(string value) =>
        value.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("/?", StringComparison.OrdinalIgnoreCase);

    private static int Usage(string message)
    {
        Console.Error.WriteLine(message);
        PrintUsage();
        return UsageError;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            PCC Executive Updater

            Commands:
              prepare-installer-upgrade --backup-root <path>
              prepare-update --backup-root <path> --attempt <id>
              post-install-verify --backup-root <path> --attempt <id>
              restore-update-checkpoint --backup-root <path> --attempt <id>

            This process never force-kills PCC Executive and never handles ChatGPT credentials.
            """);
    }

    private static void Log(string message)
    {
        try
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logDir = Path.Combine(localData, "PCC Executive", "Logs");
            Directory.CreateDirectory(logDir);
            var line = $"{DateTimeOffset.UtcNow:o} {message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(logDir, "updater.log"), line);
        }
        catch
        {
            // Logging failure must not turn an otherwise safe update decision into a destructive action.
        }
    }
}
