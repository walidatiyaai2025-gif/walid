namespace PCCExecutive.App.Installer;

/// <summary>
/// Visual handoff for Worker 5. Keeps Setup / Upgrade aligned with PCC Executive identity
/// without owning installer packaging or update execution.
/// </summary>
public static class InstallerVisualContract
{
    public const string ProductName = "PCC Executive";
    public const string Subtitle = "AI Project Commander";
    public const string Background = "#050A13";
    public const string Surface = "#0B1625";
    public const string Border = "#1C2D42";
    public const string Accent = "#8B5CF6";
    public const string Text = "#F4F7FB";
    public const string MutedText = "#8FA3B8";
    public const int ReferenceWidth = 1920;
    public const int ReferenceHeight = 1080;

    public static readonly string[] SetupSteps =
    [
        "Welcome",
        "Recommended installation",
        "Install location",
        "Shortcuts",
        "Install",
        "Launch PCC Executive"
    ];

    public static readonly string[] UpgradeAssurances =
    [
        "Checkpoint active project state",
        "Preserve projects, settings and history",
        "Verify update package",
        "Back up application data",
        "Apply versioned migration",
        "Restart and verify",
        "Rollback safely if verification fails"
    ];
}
