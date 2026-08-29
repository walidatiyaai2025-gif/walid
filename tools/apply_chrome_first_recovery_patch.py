from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def replace_once(path, old, new):
    text = path.read_text(encoding='utf-8')
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'Expected one match in {path}, found {count}')
    path.write_text(text.replace(old, new, 1), encoding='utf-8')

runtime = ROOT / 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'

a = '''    private IReadOnlyList<ConversationHistorySummary> _conversationHistory = [];
    private DateTimeOffset _nextExternalEvidenceRetryAt = DateTimeOffset.MinValue;
'''
b = '''    private IReadOnlyList<ConversationHistorySummary> _conversationHistory = [];
    private DateTimeOffset _nextExternalEvidenceRetryAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextChromeRecoveryRetryAt = DateTimeOffset.MinValue;
'''
replace_once(runtime, a, b)

old_connect = '''    private async Task ConnectManagerChromeAsync(CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        var managerAgentId = _managerAgentId ?? throw new InvalidOperationException("Manager logical identity is not initialized.");
        try
        {
            var existing = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(x => StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()) && StringComparer.Ordinal.Equals(x.LogicalAgentId, managerAgentId.ToString()) && !x.IsArchived && x.State is not BrowserSessionState.Killed);
            if (existing is null)
                await _sessions.CreateAsync(new BrowserSessionRequest(run.Id.ToString(), managerAgentId.ToString(), DefaultVisibility: BrowserVisibility.Hidden), cancellationToken).ConfigureAwait(false);
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "READY", "PCC-owned Manager Chrome runtime initialized; personal Chrome remains excluded.", true));
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or TimeoutException)
        {
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "BROWSER BLOCKED", ex.Message, false));
        }
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (_settings.AutoResume) EnsureAutopilotLoop();
    }
'''
new_connect = '''    private async Task ConnectManagerChromeAsync(CancellationToken cancellationToken)
    {
        var run = RequireActiveRun();
        var managerAgentId = _managerAgentId ?? throw new InvalidOperationException("Manager logical identity is not initialized.");
        try
        {
            var existing = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(x => StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()) && StringComparer.Ordinal.Equals(x.LogicalAgentId, managerAgentId.ToString()) && !x.IsArchived && x.State is not BrowserSessionState.Killed);
            if (existing is null)
            {
                await _sessions.CreateAsync(new BrowserSessionRequest(run.Id.ToString(), managerAgentId.ToString(), DefaultVisibility: BrowserVisibility.Hidden), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var proof = await _ownership.ProveAsync(existing, cancellationToken).ConfigureAwait(false);
                if (!proof.IsProven || existing.State is BrowserSessionState.Creating or BrowserSessionState.Degraded or BrowserSessionState.Recovering or BrowserSessionState.FailedRequiresAttention)
                {
                    var recovered = await _sessions.RecoverOrphanAsync(existing.RuntimeId, cancellationToken).ConfigureAwait(false);
                    if (!recovered.Succeeded)
                        throw new InvalidOperationException($"Manager Chrome recovery failed: {recovered.Reason}.");
                }
            }
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "READY", "PCC-owned Manager Chrome runtime initialized/recovered; personal Chrome remains excluded.", true));
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or TimeoutException)
        {
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "BROWSER BLOCKED", ex.Message, false));
        }
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (_settings.AutoResume) EnsureAutopilotLoop();
    }

    private async Task<bool> EnsureManagerChromeReadyAsync(CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow < _nextChromeRecoveryRetryAt)
            return false;

        var run = RequireActiveRun();
        var managerAgentId = _managerAgentId ?? throw new InvalidOperationException("Manager logical identity is not initialized.");
        var runtime = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(x => !x.IsArchived && x.State is not BrowserSessionState.Killed && StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()) && StringComparer.Ordinal.Equals(x.LogicalAgentId, managerAgentId.ToString()));

        var ownership = runtime is null ? null : await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
        if (runtime is null || ownership is null || !ownership.IsProven || runtime.State is BrowserSessionState.Creating or BrowserSessionState.Degraded or BrowserSessionState.Recovering or BrowserSessionState.FailedRequiresAttention)
        {
            _autopilot = "RECOVERING";
            _latestManagerHandoff = runtime is null
                ? "RECOVERING_CHROME — no active PCC-owned Manager Chrome session exists. Connecting automatically before Manager planning."
                : $"RECOVERING_CHROME — Manager Chrome readiness is not proven ({ownership?.Reason ?? runtime.State.ToString()}). Recovering before Manager planning.";
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "RECOVERING_CHROME", _latestManagerHandoff, true));
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            await ConnectManagerChromeAsync(cancellationToken).ConfigureAwait(false);

            runtime = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(x => !x.IsArchived && x.State is not BrowserSessionState.Killed && StringComparer.Ordinal.Equals(x.ProjectRunId, run.Id.ToString()) && StringComparer.Ordinal.Equals(x.LogicalAgentId, managerAgentId.ToString()));
        }

        if (runtime is null)
        {
            _nextChromeRecoveryRetryAt = DateTimeOffset.UtcNow.AddSeconds(5);
            _autopilot = "RECOVERING";
            _latestManagerHandoff = "RECOVERING_CHROME — PCC-owned Manager Chrome session is still unavailable. Automatic retry in 5 seconds.";
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        ownership = await _ownership.ProveAsync(runtime, cancellationToken).ConfigureAwait(false);
        if (!ownership.IsProven)
        {
            _nextChromeRecoveryRetryAt = DateTimeOffset.UtcNow.AddSeconds(5);
            _autopilot = "RECOVERING";
            _latestManagerHandoff = $"RECOVERING_CHROME — ownership/readiness is still unproven ({ownership.Reason}). Automatic retry in 5 seconds.";
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "RECOVERING_CHROME", ownership.Reason, false));
            await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        _nextChromeRecoveryRetryAt = DateTimeOffset.MinValue;
        _latestManagerHandoff = "CHROME_READY — PCC-owned Manager Chrome session and ownership are proven. Continuing to Manager evidence/planning.";
        _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "CHROME_READY", runtime.RuntimeId, true));
        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
'''
replace_once(runtime, old_connect, new_connect)

old_start = '''        var managerAgentId = _managerAgentId ?? throw new InvalidOperationException("Manager logical identity is not initialized.");
        if (_autopilot == "PAUSED") throw new InvalidOperationException("Resume AI before starting Manager.");
        var runtime = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false))
'''
new_start = '''        var managerAgentId = _managerAgentId ?? throw new InvalidOperationException("Manager logical identity is not initialized.");
        if (_autopilot == "PAUSED") throw new InvalidOperationException("Resume AI before starting Manager.");
        if (!await EnsureManagerChromeReadyAsync(cancellationToken).ConfigureAwait(false))
            return;
        var runtime = (await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false))
'''
replace_once(runtime, old_start, new_start)

# Regression contract: Chrome must be recovered/proven before evidence/planning.
test = ROOT / 'tests/PCCExecutive.App.Tests/ProductionRecoveryWiringContractTests.cs'
marker = '''    [Fact]
    public void Normal_disposal_invokes_safe_shutdown_coordinator()
'''
insert = '''    [Fact]
    public void Manager_start_recovers_and_proves_pcc_chrome_before_live_evidence_or_planning()
    {
        var source = ReadSource("src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs");
        var readiness = Slice(source, "private async Task<bool> EnsureManagerChromeReadyAsync", "private async Task StartManagerAsync");
        var start = Slice(source, "private async Task StartManagerAsync", "private string BuildManagerPrompt");

        Assert.Contains("RECOVERING_CHROME", readiness, StringComparison.Ordinal);
        Assert.Contains("ConnectManagerChromeAsync(cancellationToken)", readiness, StringComparison.Ordinal);
        Assert.Contains("_ownership.ProveAsync(runtime", readiness, StringComparison.Ordinal);
        Assert.Contains("CHROME_READY", readiness, StringComparison.Ordinal);
        AssertOrdered(start,
            "EnsureManagerChromeReadyAsync(cancellationToken)",
            "_baseline.BuildAsync");
    }

    [Fact]
    public void Normal_disposal_invokes_safe_shutdown_coordinator()
'''
replace_once(test, marker, insert)
