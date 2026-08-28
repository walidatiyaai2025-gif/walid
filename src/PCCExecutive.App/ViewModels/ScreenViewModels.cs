using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PCCExecutive.App.Presentation;

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
        _selectedChromeProfile = ChromeProfiles.FirstOrDefault(x =>
            string.Equals(x.DirectoryName, savedDirectory, StringComparison.OrdinalIgnoreCase))
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

    public string ChromeProfileStatusText => SelectedChromeProfile is null
        ? "No local Chrome profile was detected. PCC will use a fresh isolated managed profile."
        : $"Selected: {SelectedChromeProfile.DisplayLabel}. PCC copies it into an isolated managed runtime; your personal Chrome profile is never controlled or killed.";

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
        var userDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google",
            "Chrome",
            "User Data");

        if (!Directory.Exists(userDataRoot)) return Array.Empty<ChromeProfileChoice>();

        var profiles = new Dictionary<string, ChromeProfileChoice>(StringComparer.OrdinalIgnoreCase);
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

        return profiles.Values
            .OrderBy(x => string.Equals(x.DirectoryName, "Default", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
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
public sealed class ConversationHistoryViewModel(MainViewModel shell) : ScreenViewModelBase(shell);
