using System;
using System.IO;
using Xunit;

namespace PCCExecutive.App.Tests;

public sealed class ManagerResponseBrowserRecoveryContractTests
{
    private static string Source()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PCCExecutive.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "src", "PCCExecutive.App", "Presentation", "IntegratedPresentationGateway.cs"));
    }

    [Fact]
    public void Manager_response_reconciliation_proves_live_chrome_before_runtime_inspection()
    {
        var source = Source();
        var method = source.IndexOf("private async Task ReconcileManagerResponseAsync", StringComparison.Ordinal);
        var liveGate = source.IndexOf("if (!await EnsureManagerChromeReadyAsync(cancellationToken)", method, StringComparison.Ordinal);
        var runtimeLookup = source.IndexOf("var runtime = (await _runtimeRegistry.ListAsync", method, StringComparison.Ordinal);
        var semanticInspect = source.IndexOf("var semantic = await _browserAdapter.InspectAsync", method, StringComparison.Ordinal);

        Assert.True(method >= 0);
        Assert.True(liveGate > method);
        Assert.True(runtimeLookup > liveGate);
        Assert.True(semanticInspect > runtimeLookup);
        Assert.Contains("RECOVERING_MANAGER_BROWSER", source, StringComparison.Ordinal);
        Assert.Contains("no Manager prompt will be resent", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void All_unknown_manager_semantics_reenter_browser_recovery_without_loopguard_failure()
    {
        var source = Source();
        var method = source.IndexOf("private async Task ReconcileManagerResponseAsync", StringComparison.Ordinal);
        var semanticGap = source.IndexOf("semanticBrowserEvidenceMissing", method, StringComparison.Ordinal);
        var ordinaryRead = source.IndexOf("READING_MANAGER_RESPONSE — response observed but completion is not yet proven", method, StringComparison.Ordinal);

        Assert.True(semanticGap > method);
        Assert.True(ordinaryRead > semanticGap);
        Assert.Contains("semantic.AssistantMessageCount == 0", source, StringComparison.Ordinal);
        Assert.Contains("_nextChromeRecoveryRetryAt = DateTimeOffset.MinValue", source, StringComparison.Ordinal);
        Assert.Contains("await PersistLoopGuardAsync(false, cancellationToken)", source, StringComparison.Ordinal);
        Assert.Contains("no resend is authorized", source, StringComparison.OrdinalIgnoreCase);
    }
}
