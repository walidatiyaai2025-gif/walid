namespace PCCExecutive.Browser;

public static class ChatGptPageSelectionPolicy
{
    public static int SelectForLaunch(IReadOnlyList<string> urls)
    {
        ArgumentNullException.ThrowIfNull(urls);
        var newSurface = ChatGptIndices(urls).Where(i => IsNewSurface(urls[i])).ToArray();
        return newSurface.Length == 0 ? -1 : newSurface[^1];
    }

    public static int SelectForRecovery(IReadOnlyList<string> urls, string? expectedProviderConversationIdentity)
    {
        ArgumentNullException.ThrowIfNull(urls);
        var chatGpt = ChatGptIndices(urls).ToArray();
        if (chatGpt.Length == 0) return -1;

        if (!string.IsNullOrWhiteSpace(expectedProviderConversationIdentity) &&
            !string.Equals(expectedProviderConversationIdentity, "NEW", StringComparison.OrdinalIgnoreCase))
        {
            var expected = NormalizeIdentity(expectedProviderConversationIdentity);
            if (string.IsNullOrWhiteSpace(expected)) return -1;
            var exact = chatGpt.Where(i =>
                TryGetConversationIdentity(urls[i], out var actual) &&
                StringComparer.OrdinalIgnoreCase.Equals(actual, expected)).ToArray();
            return exact.Length == 0 ? -1 : exact[^1];
        }

        var newSurface = chatGpt.Where(i => IsNewSurface(urls[i])).ToArray();
        if (newSurface.Length > 0) return newSurface[^1];
        var stable = chatGpt.Where(i => TryGetConversationIdentity(urls[i], out _)).ToArray();
        return stable.Length == 1 ? stable[0] : -1;
    }

    public static int SelectForLiveRefresh(IReadOnlyList<string> urls, int currentIndex)
    {
        ArgumentNullException.ThrowIfNull(urls);
        var chatGpt = ChatGptIndices(urls).ToArray();
        if (chatGpt.Length == 0) return -1;

        if (currentIndex >= 0 && currentIndex < urls.Count && IsChatGpt(urls[currentIndex]))
        {
            if (TryGetConversationIdentity(urls[currentIndex], out _))
                return currentIndex;

            if (IsNewSurface(urls[currentIndex]))
            {
                var stable = chatGpt.Where(i => i != currentIndex && TryGetConversationIdentity(urls[i], out _)).ToArray();
                return stable.Length == 1 ? stable[0] : currentIndex;
            }
        }

        var newSurface = chatGpt.Where(i => IsNewSurface(urls[i])).ToArray();
        if (newSurface.Length > 0) return newSurface[^1];
        var stableFallback = chatGpt.Where(i => TryGetConversationIdentity(urls[i], out _)).ToArray();
        return stableFallback.Length == 1 ? stableFallback[0] : -1;
    }

    public static bool TryGetConversationIdentity(string? value, out string identity)
    {
        identity = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsChatGptHost(uri.Host))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i + 1 < segments.Length; i++)
        {
            if (!string.Equals(segments[i], "c", StringComparison.OrdinalIgnoreCase)) continue;
            identity = segments[i + 1];
            return !string.IsNullOrWhiteSpace(identity);
        }
        return false;
    }

    private static IEnumerable<int> ChatGptIndices(IReadOnlyList<string> urls) =>
        Enumerable.Range(0, urls.Count).Where(i => IsChatGpt(urls[i]));

    private static bool IsChatGpt(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsChatGptHost(uri.Host);

    private static bool IsNewSurface(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsChatGptHost(uri.Host)) return false;
        var path = uri.AbsolutePath.Trim('/');
        return path.Length == 0 || string.Equals(path, "new", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsChatGptHost(string host) =>
        string.Equals(host, "chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".chatgpt.com", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeIdentity(string value)
    {
        if (TryGetConversationIdentity(value, out var fromUrl)) return fromUrl;
        var trimmed = value.Trim().Trim('/');
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
