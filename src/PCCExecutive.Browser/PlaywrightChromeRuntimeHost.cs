using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Playwright;

namespace PCCExecutive.Browser;

public interface IPlaywrightPageProvider
{
    Task<IPage?> GetPageAsync(string runtimeId, CancellationToken cancellationToken = default);
}

public interface IBrowserWindowVisibilityController
{
    Task<bool> HideAsync(int processId, CancellationToken cancellationToken = default);
    Task<bool> ShowAsync(int processId, bool bringToFront, CancellationToken cancellationToken = default);
}

public interface IChromeExecutableLocator
{
    string LocateChrome();
}

public sealed class ChromeExecutableLocator : IChromeExecutableLocator
{
    public string LocateChrome()
    {
        var configured = Environment.GetEnvironmentVariable("PCC_EXECUTIVE_CHROME_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
        };
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("Google Chrome was not found. Set PCC_EXECUTIVE_CHROME_PATH to the Chrome executable.");
    }
}

public sealed class WindowsBrowserWindowVisibilityController : IBrowserWindowVisibilityController
{
    private const int SwHide = 0; private const int SwShow = 5; private const int SwRestore = 9;
    public Task<bool> HideAsync(int processId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); if (!OperatingSystem.IsWindows()) return Task.FromResult(false);
        var window = FindTopLevelWindow(processId); if (window == IntPtr.Zero) return Task.FromResult(false); _ = ShowWindow(window, SwHide); return Task.FromResult(true);
    }
    public Task<bool> ShowAsync(int processId, bool bringToFront, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); if (!OperatingSystem.IsWindows()) return Task.FromResult(false);
        var window = FindTopLevelWindow(processId); if (window == IntPtr.Zero) return Task.FromResult(false);
        _ = ShowWindow(window, bringToFront ? SwRestore : SwShow); if (bringToFront) _ = SetForegroundWindow(window); return Task.FromResult(true);
    }
    private static IntPtr FindTopLevelWindow(int processId)
    {
        if (processId <= 0) return IntPtr.Zero;
        var targetPid = checked((uint)processId);
        var result = IntPtr.Zero;
        _ = EnumWindows((window, lParam) =>
        {
            _ = lParam;
            _ = GetWindowThreadProcessId(window, out var ownerPid);
            if (ownerPid == targetPid)
            {
                result = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hWnd);
}

public sealed class PlaywrightChromeRuntimeHost : IBrowserRuntimeHost, IPlaywrightPageProvider
{
    private const string ProfileEnvironmentVariable = "PCC_EXECUTIVE_CHROME_PROFILE_SOURCE";
    private static readonly HashSet<string> SkippedProfileDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache", "Code Cache", "GPUCache", "DawnCache", "GrShaderCache", "ShaderCache",
        "Crashpad", "BrowserMetrics", "component_crx_cache", "Safe Browsing Network"
    };
    private static readonly HashSet<string> SkippedProfileFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "LOCK", "DevToolsActivePort", "SingletonLock", "SingletonCookie", "SingletonSocket"
    };

    private sealed record Connection(Process Process, IBrowser Browser, IPage Page, string ContextIdentity);
    private readonly string _profileRoot; private readonly IChromeExecutableLocator _chromeLocator; private readonly IBrowserWindowVisibilityController _windows;
    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.Ordinal); private readonly SemaphoreSlim _playwrightLock = new(1, 1); private IPlaywright? _playwright;

    public PlaywrightChromeRuntimeHost(string profileRoot, IChromeExecutableLocator? chromeLocator = null, IBrowserWindowVisibilityController? windows = null)
    { _profileRoot = Path.GetFullPath(profileRoot); _chromeLocator = chromeLocator ?? new ChromeExecutableLocator(); _windows = windows ?? new WindowsBrowserWindowVisibilityController(); }

    public async Task<BrowserRuntimeRecord> LaunchAsync(BrowserSessionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Directory.CreateDirectory(_profileRoot);
        var runtimeId = request.RuntimeId ?? Guid.NewGuid().ToString("N"); var profilePath = Path.Combine(_profileRoot, SanitizeRuntimeId(runtimeId)); Directory.CreateDirectory(profilePath);
        var selectedProfileDirectory = ResolveSelectedProfileDirectory();
        if (selectedProfileDirectory is not null)
            SeedManagedProfile(profilePath, selectedProfileDirectory, cancellationToken);

        var chrome = _chromeLocator.LocateChrome();
        var startInfo = new ProcessStartInfo { FileName = chrome, UseShellExecute = false, CreateNoWindow = false, WorkingDirectory = Path.GetDirectoryName(chrome) ?? Environment.CurrentDirectory };
        startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1"); startInfo.ArgumentList.Add("--remote-debugging-port=0"); startInfo.ArgumentList.Add($"--user-data-dir={profilePath}");
        if (selectedProfileDirectory is not null) startInfo.ArgumentList.Add($"--profile-directory={selectedProfileDirectory}");
        startInfo.ArgumentList.Add("--no-first-run"); startInfo.ArgumentList.Add("--no-default-browser-check"); startInfo.ArgumentList.Add("--disable-background-mode"); startInfo.ArgumentList.Add("--start-minimized"); startInfo.ArgumentList.Add("https://chatgpt.com/");
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Chrome process failed to start."); var processIdentity = BrowserProcessIdentity.From(process); var ownershipNonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        try
        {
            var endpoint = await WaitForDevToolsEndpointAsync(profilePath, process, cancellationToken).ConfigureAwait(false); var playwright = await GetPlaywrightAsync(cancellationToken).ConfigureAwait(false); var browser = await playwright.Chromium.ConnectOverCDPAsync(endpoint).ConfigureAwait(false);
            var context = browser.Contexts.FirstOrDefault() ?? throw new InvalidOperationException("Chrome CDP connection has no browser context."); var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync().ConfigureAwait(false); var contextIdentity = Guid.NewGuid().ToString("N");
            _connections[runtimeId] = new(process, browser, page, contextIdentity);
            if (request.DefaultVisibility == BrowserVisibility.Hidden) _ = await _windows.HideAsync(process.Id, cancellationToken).ConfigureAwait(false); else _ = await _windows.ShowAsync(process.Id, false, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            return new BrowserRuntimeRecord { RuntimeId = runtimeId, ProjectRunId = request.ProjectRunId, LogicalAgentId = request.LogicalAgentId, WorkerSlotId = request.WorkerSlotId, TaskId = request.TaskId, ProcessId = process.Id, ProcessStartIdentity = processIdentity, ContextIdentity = contextIdentity, ProfilePath = profilePath, CreatedByPcc = true, AdoptedExplicitly = false, ConversationIdentity = request.ConversationIdentity, ProviderConversationIdentity = request.ProviderConversationIdentity, Visibility = request.DefaultVisibility, State = request.DefaultVisibility == BrowserVisibility.Hidden ? BrowserSessionState.Hidden : BrowserSessionState.Visible, LastHeartbeatAt = now, LastActivityAt = now, OwnershipNonce = ownershipNonce };
        }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); throw; }
    }

    public async Task<bool> RecoverAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); if (runtime.ProcessId is not > 0) return false;
        if (_connections.TryGetValue(runtime.RuntimeId, out var existing) && !existing.Process.HasExited) return true;
        Process process; try { process = Process.GetProcessById(runtime.ProcessId.Value); if (process.HasExited) return false; } catch (ArgumentException) { return false; }
        var endpoint = await WaitForDevToolsEndpointAsync(runtime.ProfilePath, process, cancellationToken).ConfigureAwait(false); var playwright = await GetPlaywrightAsync(cancellationToken).ConfigureAwait(false); var browser = await playwright.Chromium.ConnectOverCDPAsync(endpoint).ConfigureAwait(false); var context = browser.Contexts.FirstOrDefault(); if (context is null) return false;
        var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync().ConfigureAwait(false); _connections[runtime.RuntimeId] = new(process, browser, page, runtime.ContextIdentity ?? Guid.NewGuid().ToString("N")); return true;
    }

    public async Task SetVisibilityAsync(BrowserRuntimeRecord runtime, BrowserVisibility visibility, bool bringToFront, CancellationToken cancellationToken = default)
    {
        if (runtime.ProcessId is not > 0) throw new InvalidOperationException("Runtime has no process identity.");
        if (visibility == BrowserVisibility.Hidden) _ = await _windows.HideAsync(runtime.ProcessId.Value, cancellationToken).ConfigureAwait(false); else _ = await _windows.ShowAsync(runtime.ProcessId.Value, bringToFront, cancellationToken).ConfigureAwait(false);
    }

    public Task KillAsync(BrowserRuntimeRecord runtime, OwnershipProof proof, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); if (!proof.IsProven || !StringComparer.Ordinal.Equals(proof.RuntimeId, runtime.RuntimeId)) throw new InvalidOperationException("Browser process termination refused without matching positive PCC ownership proof."); if (runtime.ProcessId is not > 0) throw new InvalidOperationException("Owned runtime has no process id.");
        if (_connections.TryRemove(runtime.RuntimeId, out var connection)) connection.Process.Dispose();
        try { using var process = Process.GetProcessById(runtime.ProcessId.Value); if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (ArgumentException) { }
        return Task.CompletedTask;
    }

    public Task<BrowserRuntimeTelemetry> GetTelemetryAsync(BrowserRuntimeRecord runtime, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); if (runtime.ProcessId is not > 0) return Task.FromResult(new BrowserRuntimeTelemetry(runtime.RuntimeId, false, 0, 0, TimeSpan.Zero, runtime.LastHeartbeatAt, true, runtime.IsArchived));
        try { using var process = Process.GetProcessById(runtime.ProcessId.Value); if (process.HasExited) return Task.FromResult(new BrowserRuntimeTelemetry(runtime.RuntimeId, false, 0, 0, TimeSpan.Zero, runtime.LastHeartbeatAt, true, runtime.IsArchived)); return Task.FromResult(new BrowserRuntimeTelemetry(runtime.RuntimeId, true, 1, process.WorkingSet64, process.TotalProcessorTime, runtime.LastHeartbeatAt, DateTimeOffset.UtcNow - runtime.LastActivityAt > TimeSpan.FromMinutes(5), runtime.IsArchived)); }
        catch (ArgumentException) { return Task.FromResult(new BrowserRuntimeTelemetry(runtime.RuntimeId, false, 0, 0, TimeSpan.Zero, runtime.LastHeartbeatAt, true, runtime.IsArchived)); }
    }

    public Task<IPage?> GetPageAsync(string runtimeId, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(_connections.TryGetValue(runtimeId, out var connection) ? connection.Page : null); }
    private async Task<IPlaywright> GetPlaywrightAsync(CancellationToken cancellationToken) { if (_playwright is not null) return _playwright; await _playwrightLock.WaitAsync(cancellationToken).ConfigureAwait(false); try { _playwright ??= await Microsoft.Playwright.Playwright.CreateAsync().ConfigureAwait(false); return _playwright; } finally { _playwrightLock.Release(); } }

    private static string? ResolveSelectedProfileDirectory()
    {
        var selected = Environment.GetEnvironmentVariable(ProfileEnvironmentVariable)?.Trim();
        if (string.IsNullOrWhiteSpace(selected)) return null;
        if (selected.Contains(Path.DirectorySeparatorChar) || selected.Contains(Path.AltDirectorySeparatorChar) || selected.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("The selected Chrome profile directory is invalid.");
        return selected;
    }

    private static void SeedManagedProfile(string managedRoot, string selectedProfileDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "User Data");
        var sourceProfile = Path.Combine(sourceRoot, selectedProfileDirectory);
        if (!Directory.Exists(sourceProfile))
            throw new InvalidOperationException($"Selected Chrome profile '{selectedProfileDirectory}' no longer exists. Choose another profile in PCC Executive.");

        var destinationProfile = Path.Combine(managedRoot, selectedProfileDirectory);
        var markerPath = Path.Combine(managedRoot, ".pcc-source-profile");
        if (File.Exists(markerPath) && Directory.Exists(destinationProfile))
        {
            try
            {
                var marker = File.ReadAllText(markerPath).Trim();
                if (string.Equals(marker, selectedProfileDirectory, StringComparison.OrdinalIgnoreCase)) return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // Never attach Playwright/CDP to the user's live profile. We seed an isolated PCC-owned
        // user-data directory, preserving the ownership/kill safety contract while carrying the
        // selected profile's sign-in state when Chrome permits those files to be read.
        Directory.CreateDirectory(managedRoot);
        CopySharedFile(Path.Combine(sourceRoot, "Local State"), Path.Combine(managedRoot, "Local State"), required: true);
        CopyProfileDirectory(sourceProfile, destinationProfile, cancellationToken);
        File.WriteAllText(markerPath, selectedProfileDirectory);
    }

    private static void CopyProfileDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(source).ToArray(); }
        catch (IOException) { files = Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { files = Array.Empty<string>(); }

        foreach (var sourceFile in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(sourceFile);
            if (SkippedProfileFiles.Contains(fileName)) continue;
            CopySharedFile(sourceFile, Path.Combine(destination, fileName), required: false);
        }

        IEnumerable<string> directories;
        try { directories = Directory.EnumerateDirectories(source).ToArray(); }
        catch (IOException) { directories = Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { directories = Array.Empty<string>(); }

        foreach (var sourceDirectory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryName = Path.GetFileName(sourceDirectory);
            if (SkippedProfileDirectories.Contains(directoryName)) continue;
            CopyProfileDirectory(sourceDirectory, Path.Combine(destination, directoryName), cancellationToken);
        }
    }

    private static void CopySharedFile(string source, string destination, bool required)
    {
        if (!File.Exists(source))
        {
            if (required) throw new InvalidOperationException($"Required Chrome profile file is missing: {Path.GetFileName(source)}.");
            return;
        }

        try
        {
            var parent = Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
        catch (Exception ex) when (!required && ex is IOException or UnauthorizedAccessException)
        {
            // Best effort for transient/locked cache and session files. Chrome will rebuild them.
        }
        catch (Exception ex) when (required && ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Unable to copy required Chrome profile file '{Path.GetFileName(source)}'. Close Chrome and retry profile connection.", ex);
        }
    }

    private static async Task<string> WaitForDevToolsEndpointAsync(string profilePath, Process process, CancellationToken cancellationToken)
    {
        var file = Path.Combine(profilePath, "DevToolsActivePort"); var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline) { cancellationToken.ThrowIfCancellationRequested(); if (process.HasExited) throw new InvalidOperationException($"Chrome exited before CDP became ready (exit code {process.ExitCode})."); if (File.Exists(file)) { try { var lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false); if (lines.Length > 0 && int.TryParse(lines[0], out var port) && port is > 0 and <= 65535) return $"http://127.0.0.1:{port}"; } catch (IOException) { } } await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false); }
        throw new TimeoutException("Timed out waiting for the PCC-owned Chrome DevTools endpoint.");
    }
    private static string SanitizeRuntimeId(string runtimeId) { var invalid = Path.GetInvalidFileNameChars(); return new string(runtimeId.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()); }
}
