using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PCCExecutive.App.Presentation;
using PCCExecutive.Browser;
using PCCExecutive.Application;

namespace PCCExecutive.App.ViewModels;

public abstract class ScreenViewModelBase(MainViewModel shell)
{
    public MainViewModel Shell { get; } = shell;
}

public sealed record ChromeProfileChoice(string DirectoryName, string DisplayName)
{
    public string DisplayLabel => string.Equals(DirectoryName, DisplayName, StringComparison.Ordinal)
        ? DisplayName
        : $"{DisplayName} — {DirectoryName}";
}

public sealed class ChromeConnectionViewModel : ScreenViewModelBase, INotifyPropertyChanged
{
    private const string ProfileEnvironmentVariable = "PCC_EXECUTIVE_CHROME_PROFILE_SOURCE";
    private readonly string _selectionPath;
    private ChromeProfileChoice? _selectedChromeProfile;

    public ChromeConnectionViewModel(MainViewModel shell) : base(shell)
    {
        _selectionPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCC Executive",
            "chrome-profile-selection.txt");

        ChromeProfiles = DiscoverChromeProfiles();
        var savedDirectory = LoadSavedProfileDirectory();
        var gptDesktopProfile = ChromeProfiles.FirstOrDefault(x =>
            string.Equals(x.DirectoryName, PlaywrightChromeRuntimeHost.GptDesktopProfileSource, StringComparison.Ordinal));

        // Existing GPTDeskTop ChromeProfile is intentionally preferred when present. That is the
        // user's already-established ChatGPT browser identity and matches GPTDeskTop's proven
        // persistent-profile launch model instead of creating a fresh per-runtime browser identity.
        _selectedChromeProfile = gptDesktopProfile
            ?? ChromeProfiles.FirstOrDefault(x => string.Equals(x.DirectoryName, savedDirectory, StringComparison.OrdinalIgnoreCase))
            ?? ChromeProfiles.FirstOrDefault();

        ApplySelection(_selectedChromeProfile);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<ChromeProfileChoice> ChromeProfiles { get; }

    public ChromeProfileChoice? SelectedChromeProfile
    {
        get => _selectedChromeProfile;
        set
        {
            if (Equals(_selectedChromeProfile, value)) return;
            _selectedChromeProfile = value;
            ApplySelection(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChromeProfileStatusText));
        }
    }

    public string ChromeProfileStatusText
    {
        get
        {
            if (SelectedChromeProfile is null)
                return "No reusable Chrome profile was detected. Connect GPTDeskTop or choose a local Chrome profile before starting the browser runtime.";

            if (string.Equals(SelectedChromeProfile.DirectoryName, PlaywrightChromeRuntimeHost.GptDesktopProfileSource, StringComparison.Ordinal))
                return "Using the existing GPTDeskTop signed-in ChromeProfile as the login source. PCC keeps a persistent Manager/Worker profile and reuses it on later launches instead of creating a fresh profile every time.";

            return $"Selected: {SelectedChromeProfile.DisplayLabel}. PCC imports this profile once into a persistent Manager/Worker Chrome profile and reuses it on later launches.";
        }
    }

    private void ApplySelection(ChromeProfileChoice? selection)
    {
        if (selection is null)
        {
            Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, null);
            return;
        }

        Environment.SetEnvironmentVariable(ProfileEnvironmentVariable, selection.DirectoryName);
        try
        {
            var directory = Path.GetDirectoryName(_selectionPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_selectionPath, selection.DirectoryName);
        }
        catch (IOException)
        {
            // Selection still applies to this process even when persistence is temporarily unavailable.
        }
        catch (UnauthorizedAccessException)
        {
            // Selection still applies to this process even when persistence is temporarily unavailable.
        }
    }

    private string? LoadSavedProfileDirectory()
    {
        try
        {
            return File.Exists(_selectionPath) ? File.ReadAllText(_selectionPath).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<ChromeProfileChoice> DiscoverChromeProfiles()
    {
        var profiles = new Dictionary<string, ChromeProfileChoice>(StringComparer.OrdinalIgnoreCase);

        var gptDesktopProfile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GPTDeskTop",
            "ChromeProfile");
        if (Directory.Exists(gptDesktopProfile))
        {
            profiles[PlaywrightChromeRuntimeHost.GptDesktopProfileSource] = new ChromeProfileChoice(
                PlaywrightChromeRuntimeHost.GptDesktopProfileSource,
                "GPTDeskTop — existing signed-in Chrome");
        }

        var userDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google",
            "Chrome",
            "User Data");

        if (Directory.Exists(userDataRoot))
        {
            var localState = Path.Combine(userDataRoot, "Local State");
            if (File.Exists(localState))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(localState));
                    if (document.RootElement.TryGetProperty("profile", out var profile) &&
                        profile.TryGetProperty("info_cache", out var infoCache) &&
                        infoCache.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var item in infoCache.EnumerateObject())
                        {
                            if (!IsSafeProfileDirectoryName(item.Name)) continue;
                            var profilePath = Path.Combine(userDataRoot, item.Name);
                            if (!Directory.Exists(profilePath)) continue;
                            var displayName = item.Value.TryGetProperty("name", out var nameElement)
                                ? nameElement.GetString()
                                : null;
                            profiles[item.Name] = new ChromeProfileChoice(
                                item.Name,
                                string.IsNullOrWhiteSpace(displayName) ? item.Name : displayName.Trim());
                        }
                    }
                }
                catch (JsonException)
                {
                    // Fall back to directory discovery below.
                }
                catch (IOException)
                {
                    // Fall back to directory discovery below.
                }
                catch (UnauthorizedAccessException)
                {
                    // Fall back to directory discovery below.
                }
            }

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(userDataRoot))
                {
                    var name = Path.GetFileName(directory);
                    if (!IsSafeProfileDirectoryName(name)) continue;
                    if (string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                    {
                        profiles.TryAdd(name, new ChromeProfileChoice(name, name));
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return profiles.Values
            .OrderBy(x => string.Equals(x.DirectoryName, PlaywrightChromeRuntimeHost.GptDesktopProfileSource, StringComparison.Ordinal) ? 0
                : string.Equals(x.DirectoryName, "Default", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static bool IsSafeProfileDirectoryName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        !name.Contains(Path.DirectorySeparatorChar) &&
        !name.Contains(Path.AltDirectorySeparatorChar) &&
        !name.Contains("..", StringComparison.Ordinal);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class ProjectSelectionViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class DashboardViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class ManagerWorkspaceViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class WorkersDispatchViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class WorkerChatViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class WaveSummaryViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class TaskBoardViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class EvidenceVerificationViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class LoopGuardViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class ChatGptHealthViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class SessionMonitorViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class SettingsViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class UpdateCenterViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class AttentionCenterViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
public sealed class RuntimeInspectorViewModel : ScreenViewModelBase, INotifyPropertyChanged
{
    private readonly RuntimeInspectorServices? _services;
    private string _currentState = "Diagnostic runtime is not configured.";
    private string _lastExportStatus = "";

    public RuntimeInspectorViewModel(MainViewModel shell, RuntimeInspectorServices? services) : base(shell)
    {
        _services = services;
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => _services is not null, ex => LastExportStatus = ex.Message);
        CopySnapshotCommand = new AsyncRelayCommand(_ => CopyAsync(), _ => _services is not null, ex => LastExportStatus = ex.Message);
        SaveSnapshotCommand = new AsyncRelayCommand(_ => SaveAsync(), _ => _services is not null, ex => LastExportStatus = ex.Message);
        _ = RefreshAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<RuntimeDiagnosticRecord> BehaviorTimeline { get; } = [];
    public ObservableCollection<RuntimeDiagnosticRecord> RecoveryTimeline { get; } = [];
    public ObservableCollection<RuntimePrerequisiteEvidence> Prerequisites { get; } = [];
    public ObservableCollection<BrowserRuntimeEvidence> BrowserEvidence { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand CopySnapshotCommand { get; }
    public AsyncRelayCommand SaveSnapshotCommand { get; }
    public string CurrentState { get => _currentState; private set { _currentState = value; Changed(); } }
    public string LastExportStatus { get => _lastExportStatus; private set { _lastExportStatus = value; Changed(); } }

    public async Task RefreshAsync()
    {
        if (_services is null) return;
        var state = await _services.StateSource.CaptureAsync();
        var events = await _services.Collector.ReadRecentAsync(250);
        Replace(Prerequisites, state.Prerequisites); Replace(BrowserEvidence, state.BrowserRuntimes);
        Replace(BehaviorTimeline, events.Where(e => e.Event.Kind is RuntimeDiagnosticKind.UserAction or RuntimeDiagnosticKind.Navigation or RuntimeDiagnosticKind.Command or RuntimeDiagnosticKind.GuardDecision));
        Replace(RecoveryTimeline, events.Where(e => e.Event.Kind is RuntimeDiagnosticKind.Recovery or RuntimeDiagnosticKind.StateTransition or RuntimeDiagnosticKind.BrowserHealth or RuntimeDiagnosticKind.Exception or RuntimeDiagnosticKind.Attention));
        CurrentState = $"Project/run: {state.ProjectRunId ?? "—"} · Provider: {state.Provider} · Browser: {state.BrowserHealth} · Manager: {state.ManagerState} · Workers: {state.WorkerState} · Sessions: {state.ActiveSessionCount} · Dispatch: {state.DispatchState} · Next: {state.NextAction?.Instruction ?? "—"}";
    }

    private async Task CopyAsync()
    {
        var json = await ExportAsync();
        System.Windows.Clipboard.SetText(json);
        LastExportStatus = $"Copied redacted snapshot ({json.Length:N0} characters).";
    }

    private async Task SaveAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Save Runtime Diagnostic Snapshot", Filter = "JSON files (*.json)|*.json", FileName = $"pcc-runtime-diagnostic-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json" };
        if (dialog.ShowDialog() != true) return;
        await File.WriteAllTextAsync(dialog.FileName, await ExportAsync());
        LastExportStatus = $"Saved redacted snapshot to {dialog.FileName}.";
    }

    private Task<string> ExportAsync() => _services!.ExportJson(typeof(RuntimeInspectorViewModel).Assembly.GetName().Version?.ToString() ?? "unknown", Environment.GetEnvironmentVariable("PCC_SOURCE_SHA"), 250, CancellationToken.None);
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values) { target.Clear(); foreach (var value in values) target.Add(value); }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
public sealed class ConversationHistoryViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
