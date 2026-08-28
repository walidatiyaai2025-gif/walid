namespace PCCExecutive.Browser;

public sealed record FinalEnterAuthorizationResult(
    bool IsAuthorized,
    string Reason,
    IReadOnlyList<string> Evidence)
{
    public static FinalEnterAuthorizationResult Authorized(IEnumerable<string>? evidence = null) =>
        new(true, "FINAL_ENTER_AUTHORIZED", (evidence ?? []).ToArray());

    public static FinalEnterAuthorizationResult Denied(string reason, IEnumerable<string>? evidence = null) =>
        new(false, reason, (evidence ?? []).ToArray());
}

/// <summary>
/// Adapter capability used when the provider must re-authorize the exact runtime
/// after composer preparation and immediately before the Enter-capable operation.
/// </summary>
public interface IFinalEnterAuthorizationAdapter : IChatGptBrowserAdapter
{
    Task<AdapterSubmissionResult> SubmitWithFinalAuthorizationAsync(
        BrowserRuntimeRecord runtime,
        BrowserDispatchExpectation expectation,
        string prompt,
        Func<CancellationToken, Task<FinalEnterAuthorizationResult>> finalAuthorization,
        CancellationToken cancellationToken = default);
}
