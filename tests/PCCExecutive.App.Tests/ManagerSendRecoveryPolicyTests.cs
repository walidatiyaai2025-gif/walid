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
