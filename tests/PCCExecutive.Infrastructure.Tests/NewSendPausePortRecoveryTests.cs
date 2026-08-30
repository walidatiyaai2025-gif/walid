using PCCExecutive.Browser;
using PCCExecutive.Infrastructure;
using Xunit;

namespace PCCExecutive.Infrastructure.Tests;

public sealed class NewSendPausePortRecoveryTests
{
    [Fact]
    public async Task Startup_recovery_fence_is_cleared_only_by_startup_safe_resume()
    {
        var gate = new GlobalBrowserSendGate();
        var port = new BrowserNewSendPausePort(gate);
        await port.PauseNewSendsAsync("STARTUP_BROWSER_RECONCILIATION:STALE_ENDPOINT");

        await Assert.ThrowsAsync<InvalidOperationException>(() => port.ResumeNewSendsAsync("Operator resumed AI"));
        Assert.True(gate.Snapshot.IsPaused);

        await port.ResumeNewSendsAsync("STARTUP_BROWSER_RECONCILIATION:SAFE_AUTO_RESUME");
        Assert.False(gate.Snapshot.IsPaused);
    }
}
