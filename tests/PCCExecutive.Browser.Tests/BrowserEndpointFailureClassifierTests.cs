using System.Net;
using System.Net.Sockets;
using PCCExecutive.Browser;
using Xunit;

namespace PCCExecutive.Browser.Tests;

public sealed class BrowserEndpointFailureClassifierTests
{
    [Theory]
    [InlineData("connect ECONNREFUSED 127.0.0.1:58760", BrowserEndpointFailureKind.ConnectionRefused)]
    [InlineData("DevToolsActivePort missing", BrowserEndpointFailureKind.EndpointMissing)]
    [InlineData("DevToolsActivePort is malformed", BrowserEndpointFailureKind.EndpointMalformed)]
    [InlineData("browser process exited before endpoint became ready", BrowserEndpointFailureKind.ProcessExited)]
    public void Classifies_stale_endpoint_failures_without_relying_on_exception_type(string message, BrowserEndpointFailureKind expected) =>
        Assert.Equal(expected, BrowserEndpointFailureClassifier.Classify(new InvalidOperationException(message)));

    [Fact]
    public void Classifies_wrapped_socket_connection_refusal()
    {
        var socket = new SocketException((int)SocketError.ConnectionRefused);
        Assert.Equal(BrowserEndpointFailureKind.ConnectionRefused,
            BrowserEndpointFailureClassifier.Classify(new InvalidOperationException("CDP failed", socket)));
    }

    [Fact]
    public void Unknown_programming_failure_is_not_a_stale_endpoint_candidate() =>
        Assert.False(BrowserEndpointFailureClassifier.IsRecoverableStaleEndpoint(new InvalidOperationException("ownership marker mismatch")));
}
