using PCCExecutive.Application;
using PCCExecutive.Browser;

namespace PCCExecutive.App.Presentation;

internal sealed class BrowserRecoveryDiagnosticSink(IRuntimeDiagnosticCollector diagnostics) : IBrowserRecoveryTelemetrySink
{
    public Task EmitAsync(BrowserRecoveryTelemetryEvent recoveryEvent, CancellationToken cancellationToken = default)
    {
        var record = diagnostics.Create(
            RuntimeDiagnosticKind.Recovery,
            recoveryEvent.ReasonCode,
            $"Browser recovery {recoveryEvent.Phase}: {recoveryEvent.ReasonCode}",
            recoveryEvent.CorrelationId,
            target: recoveryEvent.ReplacementRuntimeId ?? recoveryEvent.RuntimeId,
            allowed: recoveryEvent.Succeeded,
            beforeState: recoveryEvent.Phase.ToString(),
            afterState: recoveryEvent.Succeeded ? "SUCCEEDED" : "FAILED",
            projectRunId: recoveryEvent.ProjectRunId,
            runtimeId: recoveryEvent.RuntimeId,
            details:
            [
                new("logicalAgentId", recoveryEvent.LogicalAgentId),
                new("replacementRuntimeId", recoveryEvent.ReplacementRuntimeId),
            ]);
        return diagnostics.RecordAsync(record, cancellationToken);
    }
}
