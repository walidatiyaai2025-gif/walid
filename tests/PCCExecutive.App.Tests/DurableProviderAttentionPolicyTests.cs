using PCCExecutive.App.Presentation;
using Xunit;

namespace PCCExecutive.App.Tests;

public sealed class DurableProviderAttentionPolicyTests
{
    [Theory]
    [InlineData("LOGINREQUIRED", "LOGIN_REQUIRED", "LOGIN_REQUIRED")]
    [InlineData("LOGIN_REQUIRED", "LOGIN_REQUIRED", "LOGIN_REQUIRED")]
    [InlineData("LOGINREQUIRED", "CHALLENGE_REQUIRES_MANUAL_RESOLUTION", "CHALLENGE")]
    public void Durable_auth_faults_restore_operator_attention(string state, string reason, string expected)
    {
        Assert.Equal(expected, DurableProviderAttentionPolicy.Classify(true, state, reason));
    }

    [Theory]
    [InlineData("RATELIMITED", "RATE_LIMITED")]
    [InlineData("OFFLINE", "NETWORK_OFFLINE")]
    public void Non_auth_global_health_remains_blocking_without_fabricating_auth_attention(string state, string reason)
    {
        Assert.Null(DurableProviderAttentionPolicy.Classify(true, state, reason));
    }

    [Fact]
    public void Cleared_health_does_not_restore_attention()
    {
        Assert.Null(DurableProviderAttentionPolicy.Classify(false, "LOGINREQUIRED", "LOGIN_REQUIRED"));
    }
}
