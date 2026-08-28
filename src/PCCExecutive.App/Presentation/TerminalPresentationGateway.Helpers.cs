using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.Infrastructure;

namespace PCCExecutive.App.Presentation;

public sealed partial class TerminalPresentationGateway
{
    private UpdateSummary ReadUpdateState()
    {
        var source = Environment.GetEnvironmentVariable("PCC_EXECUTIVE_UPDATE_MANIFEST");
        if (string.IsNullOrWhiteSpace(source))
            return new UpdateSummary("0.1.0", null, "NOT CHECKED", "NOT STARTED", "NOT STARTED", "NOT NEEDED", false)
            {
                State = "NO_UPDATE_SOURCE_CONFIGURED",
                DisabledReason = "Set PCC_EXECUTIVE_UPDATE_MANIFEST to a real update manifest path."
            };

        try
        {
            if (!File.Exists(source))
                return new UpdateSummary("0.1.0", null, "SOURCE NOT FOUND", "NOT STARTED", "NOT STARTED", "NOT NEEDED", false)
                {
                    State = "UPDATE_SOURCE_UNAVAILABLE",
                    DisabledReason = $"Manifest path does not exist: {source}",
                    PackagePath = source
                };

            using var document = JsonDocument.Parse(File.ReadAllText(source));
            var version = FindString(document.RootElement, "version", "applicationVersion", "newVersion");
            var package = FindString(document.RootElement, "packagePath", "installerPath", "package");
            return new UpdateSummary("0.1.0", version, "MANIFEST LOADED · install verification not executed", "NOT STARTED", "NOT STARTED", "AVAILABLE IF STAGED BY UPDATER", false)
            {
                State = string.IsNullOrWhiteSpace(version) ? "MANIFEST_INVALID" : "UPDATE_SOURCE_CONFIGURED",
                DisabledReason = "Manifest metadata is visible, but staged installer execution is intentionally not exposed as an in-process WPF action.",
                PackagePath = package ?? source
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new UpdateSummary("0.1.0", null, "MANIFEST INVALID", "NOT STARTED", "NOT STARTED", "NOT NEEDED", false)
            {
                State = "MANIFEST_INVALID",
                DisabledReason = ex.Message,
                PackagePath = source
            };
        }
    }

    private (int? Verified, int? Estimate, CompletionMode Mode) CompletionDisplay()
    {
        if (_run is null) return (null, null, CompletionMode.Unknown);
        var meaningful = _run.State is not (ProjectRunState.Initializing or ProjectRunState.ManagerPlanning) ||
                         _run.ManagerEstimate.Percent > 0 || _run.VerifiedCompletion.Percent > 0;
        if (!meaningful) return (null, null, CompletionMode.Running);
        var mode = _run.CompletionMode switch
        {
            ProjectCompletionMode.ClosureMode => CompletionMode.ClosureMode,
            ProjectCompletionMode.VerifiedComplete => CompletionMode.Verified,
            ProjectCompletionMode.Blocked => CompletionMode.Blocked,
            _ => CompletionMode.Running
        };
        return ((int)_run.VerifiedCompletion.Percent, (int)_run.ManagerEstimate.Percent, mode);
    }

    private string CurrentWaveDisplay() => _run?.State switch
    {
        ProjectRunState.WaveReady => "WAVE READY",
        ProjectRunState.Dispatching => "DISPATCHING",
        ProjectRunState.WaveRunning => "WAVE RUNNING",
        ProjectRunState.Reconciling => "RECONCILING",
        ProjectRunState.ManagerReview => "MANAGER REVIEW",
        ProjectRunState.ClosureMode => "CLOSURE MODE",
        _ => "NO ACTIVE WAVE"
    };

    private string LogicalName(BrowserRuntimeRecord runtime)
    {
        if (_managerAgentId is { } manager && StringComparer.Ordinal.Equals(runtime.LogicalAgentId, manager.ToString())) return "Manager";
        for (var i = 0; i < _workerAgentIds.Length; i++)
            if (StringComparer.Ordinal.Equals(runtime.LogicalAgentId, _workerAgentIds[i].ToString())) return $"Worker {i + 1}";
        if (!string.IsNullOrWhiteSpace(runtime.WorkerSlotId))
        {
            var digits = new string(runtime.WorkerSlotId.Where(char.IsDigit).ToArray());
            if (!string.IsNullOrWhiteSpace(digits)) return $"Worker {digits}";
        }
        return "Manager";
    }

    private string ManagerReadiness(IReadOnlyList<SessionSummary> sessions)
    {
        if (_run is null) return "NO_PROJECT_SELECTED";
        var session = sessions.FirstOrDefault(x => x.LogicalName == "Manager" && (_run is null || x.LogicalAgentId == _managerAgentId?.ToString()));
        return session is null ? "LOGICAL_READY · NO_BROWSER" : session.Health.ToString().ToUpperInvariant();
    }

    private static string WorkerReadiness(IReadOnlyList<WorkerSummary> workers) => workers.Count == 0
        ? "NO_PROJECT_SELECTED"
        : $"{workers.Count(x => x.State is "READY" or "ACTIVE")}/{workers.Count} LOGICAL SLOTS READY";
}
