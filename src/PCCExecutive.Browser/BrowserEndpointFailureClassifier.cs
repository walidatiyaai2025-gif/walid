using System.Net.Sockets;

namespace PCCExecutive.Browser;

public static class BrowserEndpointFailureClassifier
{
    public static BrowserEndpointFailureKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        foreach (var current in Flatten(exception))
        {
            if (current is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
                return BrowserEndpointFailureKind.ConnectionRefused;
            if (current is TimeoutException or TaskCanceledException)
                return BrowserEndpointFailureKind.TimedOut;

            var message = current.Message;
            if (message.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("connection refused", StringComparison.OrdinalIgnoreCase))
                return BrowserEndpointFailureKind.ConnectionRefused;
            if (message.Contains("DevToolsActivePort", StringComparison.OrdinalIgnoreCase) &&
                (message.Contains("missing", StringComparison.OrdinalIgnoreCase) || message.Contains("not found", StringComparison.OrdinalIgnoreCase)))
                return BrowserEndpointFailureKind.EndpointMissing;
            if (message.Contains("DevToolsActivePort", StringComparison.OrdinalIgnoreCase) &&
                (message.Contains("invalid", StringComparison.OrdinalIgnoreCase) || message.Contains("malformed", StringComparison.OrdinalIgnoreCase)))
                return BrowserEndpointFailureKind.EndpointMalformed;
            if (message.Contains("process exited", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("browser has been closed", StringComparison.OrdinalIgnoreCase))
                return BrowserEndpointFailureKind.ProcessExited;
        }

        return BrowserEndpointFailureKind.Unknown;
    }

    public static bool IsRecoverableStaleEndpoint(Exception exception) => Classify(exception) is
        BrowserEndpointFailureKind.ConnectionRefused or
        BrowserEndpointFailureKind.EndpointMissing or
        BrowserEndpointFailureKind.EndpointMalformed or
        BrowserEndpointFailureKind.TimedOut or
        BrowserEndpointFailureKind.ProcessExited;

    private static IEnumerable<Exception> Flatten(Exception root)
    {
        for (Exception? current = root; current is not null; current = current.InnerException)
            yield return current;
    }
}
