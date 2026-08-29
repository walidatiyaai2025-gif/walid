from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace_once(path: Path, old: str, new: str):
    text = path.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"Expected exactly one match in {path} but found {count}: {old[:120]!r}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


# ---------------------------------------------------------------------------
# 1) PCC control-plane reads: do not burn unauthenticated REST quota forever.
#    Keep a short fresh in-memory capture, keep the last successful capture as
#    an explicitly stale safety fallback, and use public GitHub commit-feed +
#    raw immutable SHA reads when the REST branch endpoint is rate-limited.
# ---------------------------------------------------------------------------
pcc = ROOT / "src/PCCExecutive.Pcc/PccProjectControl.cs"
replace_once(
    pcc,
    "using System.Text.Json;\nusing PCCExecutive.Application;",
    "using System.Text.Json;\nusing System.Text.RegularExpressions;\nusing PCCExecutive.Application;",
)
replace_once(
    pcc,
    "    private readonly IPccDocumentCache? _cache;\n",
    "    private readonly IPccDocumentCache? _cache;\n"
    "    private PccDocumentCapture? _lastSuccessfulCapture;\n"
    "    private static readonly TimeSpan FreshCaptureWindow = TimeSpan.FromMinutes(2);\n",
)
replace_once(
    pcc,
    "    public async Task<PccDocumentCapture> CaptureAsync(CancellationToken cancellationToken = default)\n"
    "    {\n"
    "        try\n",
    "    public async Task<PccDocumentCapture> CaptureAsync(CancellationToken cancellationToken = default)\n"
    "    {\n"
    "        var remembered = _lastSuccessfulCapture;\n"
    "        if (remembered is not null && DateTimeOffset.UtcNow - remembered.CapturedAt <= FreshCaptureWindow)\n"
    "            return remembered;\n\n"
    "        try\n",
)
replace_once(
    pcc,
    "            if (!branchResponse.IsSuccessStatusCode)\n"
    "                return await FailureOrCacheAsync(Classify(branchResponse), $\"PCC_BRANCH_{(int)branchResponse.StatusCode}\", cancellationToken);\n",
    "            if (!branchResponse.IsSuccessStatusCode)\n"
    "            {\n"
    "                var status = Classify(branchResponse);\n"
    "                if (status is ExternalReadStatus.RateLimited or ExternalReadStatus.Unauthorized or ExternalReadStatus.TemporaryFailure)\n"
    "                {\n"
    "                    var publicFallback = await TryCaptureFromPublicGitAsync(cancellationToken).ConfigureAwait(false);\n"
    "                    if (publicFallback is not null) return publicFallback;\n"
    "                }\n"
    "                return await FailureOrCacheAsync(status, $\"PCC_BRANCH_{(int)branchResponse.StatusCode}\", cancellationToken);\n"
    "            }\n",
)
replace_once(
    pcc,
    "            var capture = new PccDocumentCapture(ExternalReadStatus.Success, sourceSha, DateTimeOffset.UtcNow, false, documents);\n"
    "            if (_cache is not null) await _cache.PutAsync(capture, cancellationToken);\n"
    "            return capture;\n",
    "            var capture = new PccDocumentCapture(ExternalReadStatus.Success, sourceSha, DateTimeOffset.UtcNow, false, documents);\n"
    "            _lastSuccessfulCapture = capture;\n"
    "            if (_cache is not null) await _cache.PutAsync(capture, cancellationToken);\n"
    "            return capture;\n",
)
replace_once(
    pcc,
    "    private async Task<PccDocumentCapture> FailureOrCacheAsync(ExternalReadStatus status, string errorCode, CancellationToken cancellationToken)\n"
    "    {\n"
    "        if (_cache is not null)\n",
    "    private async Task<PccDocumentCapture> FailureOrCacheAsync(ExternalReadStatus status, string errorCode, CancellationToken cancellationToken)\n"
    "    {\n"
    "        if (_lastSuccessfulCapture is { } remembered)\n"
    "            return remembered with { Status = ExternalReadStatus.StaleCache, IsStale = true, ErrorCode = errorCode };\n\n"
    "        if (_cache is not null)\n",
)
replace_once(
    pcc,
    "            if (cached is not null)\n"
    "                return cached with { Status = ExternalReadStatus.StaleCache, IsStale = true, ErrorCode = errorCode };\n",
    "            if (cached is not null)\n"
    "            {\n"
    "                _lastSuccessfulCapture = cached;\n"
    "                return cached with { Status = ExternalReadStatus.StaleCache, IsStale = true, ErrorCode = errorCode };\n"
    "            }\n",
)
replace_once(
    pcc,
    "    private string Api(string path) => $\"https://api.github.com/repos/{_repository}/{path}\";\n",
    "    private async Task<PccDocumentCapture?> TryCaptureFromPublicGitAsync(CancellationToken cancellationToken)\n"
    "    {\n"
    "        try\n"
    "        {\n"
    "            using var feedResponse = await _httpClient.GetAsync(\n"
    "                $\"https://github.com/{_repository}/commits/{Uri.EscapeDataString(_branch)}.atom\", cancellationToken).ConfigureAwait(false);\n"
    "            if (!feedResponse.IsSuccessStatusCode) return null;\n\n"
    "            var feed = await feedResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);\n"
    "            var sourceSha = ExtractCommitSha(feed);\n"
    "            if (sourceSha is null) return null;\n\n"
    "            var documents = new Dictionary<string, string>(StringComparer.Ordinal);\n"
    "            foreach (var path in RequiredPaths)\n"
    "            {\n"
    "                using var response = await _httpClient.GetAsync(\n"
    "                    $\"https://raw.githubusercontent.com/{_repository}/{sourceSha}/{path}\", cancellationToken).ConfigureAwait(false);\n"
    "                if (!response.IsSuccessStatusCode) return null;\n"
    "                documents[path] = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);\n"
    "            }\n\n"
    "            var capture = new PccDocumentCapture(ExternalReadStatus.Success, sourceSha, DateTimeOffset.UtcNow, false, documents);\n"
    "            _lastSuccessfulCapture = capture;\n"
    "            if (_cache is not null) await _cache.PutAsync(capture, cancellationToken).ConfigureAwait(false);\n"
    "            return capture;\n"
    "        }\n"
    "        catch (HttpRequestException) { return null; }\n"
    "        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }\n"
    "    }\n\n"
    "    private static string? ExtractCommitSha(string payload)\n"
    "    {\n"
    "        var match = Regex.Match(payload, @\"(?:/commit/|Commit/)([0-9a-fA-F]{40})\", RegexOptions.CultureInvariant);\n"
    "        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;\n"
    "    }\n\n"
    "    private string Api(string path) => $\"https://api.github.com/repos/{_repository}/{path}\";\n",
)

# ---------------------------------------------------------------------------
# 2) Product-repository evidence: the default branch and its exact head are
#    mandatory baseline inputs. When REST is quota-blocked, recover those two
#    facts from GitHub's public commit Atom feed. Optional PR/check/release REST
#    evidence stays fail-closed/partial and is never fabricated.
# ---------------------------------------------------------------------------
gh = ROOT / "src/PCCExecutive.GitHub/GitHubEvidenceClient.cs"
replace_once(
    gh,
    "using System.Text.Json;\nusing PCCExecutive.Application;",
    "using System.Text.Json;\nusing System.Text.RegularExpressions;\nusing PCCExecutive.Application;",
)
replace_once(
    gh,
    "    public async Task<ExternalResult<GitHubRepositorySnapshot>> GetRepositoryAsync(string repository, CancellationToken cancellationToken = default)\n"
    "    {\n"
    "        var result = await GetJsonAsync(Api(repository, \"\"), cancellationToken);\n"
    "        return Map(result, root => GitHubPayloadMapper.Repository(repository, root));\n"
    "    }\n",
    "    public async Task<ExternalResult<GitHubRepositorySnapshot>> GetRepositoryAsync(string repository, CancellationToken cancellationToken = default)\n"
    "    {\n"
    "        var result = await GetJsonAsync(Api(repository, \"\"), cancellationToken);\n"
    "        if (!result.IsSuccess && result.Status is ExternalReadStatus.RateLimited or ExternalReadStatus.Unauthorized or ExternalReadStatus.TemporaryFailure)\n"
    "        {\n"
    "            var fallback = await TryGetPublicRepositoryAsync(repository, cancellationToken).ConfigureAwait(false);\n"
    "            if (fallback is not null) return fallback;\n"
    "        }\n"
    "        return Map(result, root => GitHubPayloadMapper.Repository(repository, root));\n"
    "    }\n",
)
replace_once(
    gh,
    "    public async Task<ExternalResult<GitHubBranchSnapshot>> GetBranchAsync(string repository, string branch, CancellationToken cancellationToken = default)\n"
    "    {\n"
    "        var result = await GetJsonAsync(Api(repository, $\"branches/{Uri.EscapeDataString(branch)}\"), cancellationToken);\n"
    "        return Map(result, root => GitHubPayloadMapper.Branch(repository, root));\n"
    "    }\n",
    "    public async Task<ExternalResult<GitHubBranchSnapshot>> GetBranchAsync(string repository, string branch, CancellationToken cancellationToken = default)\n"
    "    {\n"
    "        var result = await GetJsonAsync(Api(repository, $\"branches/{Uri.EscapeDataString(branch)}\"), cancellationToken);\n"
    "        if (!result.IsSuccess && result.Status is ExternalReadStatus.RateLimited or ExternalReadStatus.Unauthorized or ExternalReadStatus.TemporaryFailure)\n"
    "        {\n"
    "            var fallback = await TryGetPublicBranchAsync(repository, branch, cancellationToken).ConfigureAwait(false);\n"
    "            if (fallback is not null) return fallback;\n"
    "        }\n"
    "        return Map(result, root => GitHubPayloadMapper.Branch(repository, root));\n"
    "    }\n",
)
replace_once(
    gh,
    "    private async Task<JsonReadResult> GetJsonAsync(string url, CancellationToken cancellationToken)\n",
    "    private async Task<ExternalResult<GitHubRepositorySnapshot>?> TryGetPublicRepositoryAsync(string repository, CancellationToken cancellationToken)\n"
    "    {\n"
    "        foreach (var branch in new[] { \"main\", \"master\" })\n"
    "        {\n"
    "            var head = await TryGetPublicBranchHeadAsync(repository, branch, cancellationToken).ConfigureAwait(false);\n"
    "            if (head is not null)\n"
    "                return new(ExternalReadStatus.Success, new GitHubRepositorySnapshot(repository, branch, false, false, $\"https://github.com/{repository}\"), DateTimeOffset.UtcNow);\n"
    "        }\n"
    "        return null;\n"
    "    }\n\n"
    "    private async Task<ExternalResult<GitHubBranchSnapshot>?> TryGetPublicBranchAsync(string repository, string branch, CancellationToken cancellationToken)\n"
    "    {\n"
    "        var head = await TryGetPublicBranchHeadAsync(repository, branch, cancellationToken).ConfigureAwait(false);\n"
    "        return head is null\n"
    "            ? null\n"
    "            : new(ExternalReadStatus.Success, new GitHubBranchSnapshot(repository, branch, head, false), DateTimeOffset.UtcNow);\n"
    "    }\n\n"
    "    private async Task<string?> TryGetPublicBranchHeadAsync(string repository, string branch, CancellationToken cancellationToken)\n"
    "    {\n"
    "        try\n"
    "        {\n"
    "            using var response = await _httpClient.GetAsync(\n"
    "                $\"https://github.com/{repository.Trim('/')}/commits/{Uri.EscapeDataString(branch)}.atom\", cancellationToken).ConfigureAwait(false);\n"
    "            if (!response.IsSuccessStatusCode) return null;\n"
    "            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);\n"
    "            var match = Regex.Match(payload, @\"(?:/commit/|Commit/)([0-9a-fA-F]{40})\", RegexOptions.CultureInvariant);\n"
    "            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;\n"
    "        }\n"
    "        catch (HttpRequestException) { return null; }\n"
    "        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }\n"
    "    }\n\n"
    "    private async Task<JsonReadResult> GetJsonAsync(string url, CancellationToken cancellationToken)\n",
)

# ---------------------------------------------------------------------------
# 3) Runtime: a transient pre-plan evidence outage is recoverable infrastructure,
#    not a semantic loop. It must not be promoted to terminal STALLED after three
#    tries. Also make the already-advertised zero-touch Manager step actually
#    start automatically once PCC-owned Chrome is ready.
# ---------------------------------------------------------------------------
runtime = ROOT / "src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs"
replace_once(
    runtime,
    "    private IReadOnlyList<ConversationHistorySummary> _conversationHistory = [];\n",
    "    private IReadOnlyList<ConversationHistorySummary> _conversationHistory = [];\n"
    "    private DateTimeOffset _nextExternalEvidenceRetryAt = DateTimeOffset.MinValue;\n",
)
replace_once(
    runtime,
    "                    if (loop.AutoStopped)\n"
    "                    {\n"
    "                        _run = _run is null ? null : _run with { State = ProjectRunState.StalledAutoStopped };\n"
    "                        _autopilot = \"STALLED\";\n"
    "                    }\n",
    "                    if (loop.AutoStopped)\n"
    "                    {\n"
    "                        var recoverablePrePlanRuntimeStall = _currentPlan is null && loop.RuntimeErrorCount >= 3 && _settings.AutoResume;\n"
    "                        if (recoverablePrePlanRuntimeStall)\n"
    "                        {\n"
    "                            _run = _run is null ? null : _run with { State = ProjectRunState.ManagerPlanning };\n"
    "                            _runtimeErrorFingerprint = null;\n"
    "                            _runtimeErrorCount = 0;\n"
    "                            _autopilot = \"RECOVERING\";\n"
    "                            _latestManagerHandoff = \"RECOVERING_EVIDENCE — retrying the previous pre-plan infrastructure failure automatically.\";\n"
    "                            if (_run is not null) store.SaveProjectRunAsync(_run).GetAwaiter().GetResult();\n"
    "                            store.SaveCheckpointAsync(new DurableCheckpoint($\"loop-guard:{run.Id}\", run.Id.ToString(), \"loop-guard-v2\", JsonSerializer.Serialize(new DurableLoopGuard(loop.PlanFingerprints, loop.VerifiedCompletion, null, 0, false)), DateTimeOffset.UtcNow)).GetAwaiter().GetResult();\n"
    "                        }\n"
    "                        else\n"
    "                        {\n"
    "                            _run = _run is null ? null : _run with { State = ProjectRunState.StalledAutoStopped };\n"
    "                            _autopilot = \"STALLED\";\n"
    "                        }\n"
    "                    }\n",
)
replace_once(
    runtime,
    "        var baseline = await _baseline.BuildAsync(_projectControlId ?? throw new InvalidOperationException(\"Selected PCC project identity is unavailable.\"), cancellationToken).ConfigureAwait(false);\n"
    "        if (!baseline.IsSuccess || baseline.Value is null)\n"
    "            throw new InvalidOperationException($\"Manager start requires fresh PCC/GitHub evidence: {baseline.ErrorCode ?? baseline.Status.ToString()}.\");\n",
    "        var baseline = await _baseline.BuildAsync(_projectControlId ?? throw new InvalidOperationException(\"Selected PCC project identity is unavailable.\"), cancellationToken).ConfigureAwait(false);\n"
    "        if (!baseline.IsSuccess || baseline.Value is null)\n"
    "        {\n"
    "            var evidenceCode = baseline.ErrorCode ?? baseline.Status.ToString();\n"
    "            if (baseline.Status is ExternalReadStatus.RateLimited or ExternalReadStatus.TemporaryFailure or ExternalReadStatus.Offline)\n"
    "            {\n"
    "                _autopilot = \"RECOVERING\";\n"
    "                _nextExternalEvidenceRetryAt = DateTimeOffset.UtcNow.AddSeconds(30);\n"
    "                _latestManagerHandoff = $\"RECOVERING_EVIDENCE — fresh PCC/GitHub evidence is temporarily unavailable ({evidenceCode}). Automatic retry is scheduled.\";\n"
    "                _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, \"RECOVERING_EVIDENCE\", evidenceCode, true));\n"
    "                await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);\n"
    "                return;\n"
    "            }\n"
    "            throw new InvalidOperationException($\"Manager start requires fresh PCC/GitHub evidence: {evidenceCode}.\");\n"
    "        }\n"
    "        _nextExternalEvidenceRetryAt = DateTimeOffset.MinValue;\n"
    "        if (run.State == ProjectRunState.StalledAutoStopped)\n"
    "        {\n"
    "            run = run with { State = ProjectRunState.ManagerPlanning };\n"
    "            _run = run;\n"
    "            _runtimeErrorFingerprint = null;\n"
    "            _runtimeErrorCount = 0;\n"
    "            await _store.SaveProjectRunAsync(run, cancellationToken).ConfigureAwait(false);\n"
    "            await PersistLoopGuardAsync(false, cancellationToken).ConfigureAwait(false);\n"
    "        }\n",
)
replace_once(
    runtime,
    "        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);\n"
    "    }\n\n"
    "    private async Task StartManagerAsync(CancellationToken cancellationToken)\n",
    "        await RefreshLocalSnapshotAsync(cancellationToken).ConfigureAwait(false);\n"
    "        if (_settings.AutoResume) EnsureAutopilotLoop();\n"
    "    }\n\n"
    "    private async Task StartManagerAsync(CancellationToken cancellationToken)\n",
)
replace_once(
    runtime,
    "                    if (_currentWave?.State == WaveState.Running)\n"
    "                        await ReconcileWorkerResponsesAsync(cancellationToken).ConfigureAwait(false);\n"
    "                    else if (_autopilot is \"PLANNING\" or \"MANAGER_REVIEW\")\n",
    "                    if (_currentPlan is null &&\n"
    "                        _autopilot is \"READY\" or \"RECOVERING\" &&\n"
    "                        DateTimeOffset.UtcNow >= _nextExternalEvidenceRetryAt)\n"
    "                        await StartManagerAsync(cancellationToken).ConfigureAwait(false);\n"
    "                    else if (_currentWave?.State == WaveState.Running)\n"
    "                        await ReconcileWorkerResponsesAsync(cancellationToken).ConfigureAwait(false);\n"
    "                    else if (_autopilot is \"PLANNING\" or \"MANAGER_REVIEW\")\n",
)

# Remove the one-shot patcher and workflow from the patch commit itself.
for temporary in [
    ROOT / "tools/apply_pcc_evidence_recovery_patch.py",
    ROOT / ".github/workflows/temp-pcc-evidence-recovery-patch.yml",
]:
    if temporary.exists():
        temporary.unlink()

print("PCC evidence recovery patch applied successfully.")
