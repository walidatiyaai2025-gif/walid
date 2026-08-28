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
    private async Task<IReadOnlyList<ProjectSummary>> LoadProjectsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var capture = await _pccSource.CaptureAsync(cancellationToken).ConfigureAwait(false);
            if (capture.Status is not (ExternalReadStatus.Success or ExternalReadStatus.StaleCache) ||
                !capture.Documents.TryGetValue(PccRoutingPath, out var json))
            {
                _lastResolution = new ProjectResolution(MapResolution(capture.Status), null, capture.ErrorCode ?? capture.Status.ToString());
                return Array.Empty<ProjectSummary>();
            }

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("PROJECTS", out var projects) || projects.ValueKind != JsonValueKind.Array)
                return Array.Empty<ProjectSummary>();

            return projects.EnumerateArray()
                .Select(item => new ProjectSummary(
                    Text(item, "PROJECT_ID") ?? "",
                    Text(item, "DISPLAY_NAME") ?? Text(item, "PROJECT_ID") ?? "Unnamed project",
                    Text(item, "REPOSITORY") ?? "—",
                    null,
                    Text(item, "ROUTING_STATE") ?? "UNKNOWN",
                    null,
                    capture.CapturedAt)
                {
                    Model = Text(item, "PROJECT_MODEL"),
                    Scope = Text(item, "DEFAULT_SCOPE"),
                    RoutingState = Text(item, "ROUTING_STATE"),
                    RoutingMessage = capture.IsStale ? "STALE CACHE" : null
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _lastResolution = new ProjectResolution(ProjectResolutionStatus.Offline, null, $"PCC project list unavailable: {ex.GetType().Name}");
            return Array.Empty<ProjectSummary>();
        }
    }
}
