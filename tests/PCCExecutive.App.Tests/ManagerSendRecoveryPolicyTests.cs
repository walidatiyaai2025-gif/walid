using PCCExecutive.App.Presentation;
using Xunit;

namespace PCCExecutive.App.Tests;

public sealed class ManagerSendRecoveryPolicyTests
{
    [Theory]
    [InlineData("PAGE_RATELIMITED")]
    [InlineData("FINAL_PAGE_RATELIMITED")]
    [InlineData("RATE_LIMITED")]
    [InlineData("page-rate-limited")]
    public void Rate_limit_send_stop_enters_global_cooldown(string errorCode)
    {
        Assert.Equal(
            ManagerSendRecoveryAction.GlobalRateLimitCooldown,
            ManagerSendRecoveryPolicy.Classify(errorCode));
    }

    [Theory]
    [InlineData("BROWSER_ADAPTER_UNCERTAIN")]
    [InlineData("browser-adapter-uncertain")]
    public void Browser_adapter_uncertainty_requests_safe_semantic_reprobe(string errorCode)
    {
        Assert.Equal(
            ManagerSendRecoveryAction.BrowserAdapterReprobe,
            ManagerSendRecoveryPolicy.Classify(errorCode));
    }

    [Fact]
    public void Browser_adapter_uncertainty_can_be_detected_from_provider_evidence()
    {
        Assert.Equal(
            ManagerSendRecoveryAction.BrowserAdapterReprobe,
            ManagerSendRecoveryPolicy.Classify(null, "guard:BROWSER_ADAPTER_UNCERTAIN"));
    }

    [Theory]
    [InlineData("WRONG_CONVERSATION_BINDING")]
    [InlineData("GLOBAL_SEND_PAUSED")]
    [InlineData("BROWSER_RUNTIME_NOT_BOUND")]
    public void Non_rate_limit_send_stop_keeps_existing_handling(string errorCode)
    {
        Assert.Equal(
            ManagerSendRecoveryAction.None,
            ManagerSendRecoveryPolicy.Classify(errorCode));
    }
}
