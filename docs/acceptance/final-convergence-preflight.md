# PCC Executive 0.1.0 — Final Convergence Preflight

**Authority:** Issue #1  
**Canonical task:** `PCCEXECUTIVE-T0001`  
**Purpose:** deterministic final composition/conflict rehearsal only. This document does not claim a release PASS and does not authorize merging historical closure streams.

## 1. Live heads inspected

| Stream | Live head | Relationship to current runtime | Preflight disposition |
|---|---|---|---|
| Runtime closure | `7b5d8f975baf7e4a56681e3cc538ea552c7c5d59` | baseline | ACCEPT AS COMPOSITION BASE |
| Browser safety / PR #36 | `6d19aecca2787b9152d8b116b4aaa58df25f22a2` | 4 commits ahead of runtime; 0 behind | ACCEPT CANDIDATE |
| Recovery / rollover / PR #37 | `ccae56121126fd8c1a433fc2853368e4141a5ca7` | 12 commits ahead of runtime; 0 behind | ACCEPT CANDIDATE |
| E2E acceptance | `5d4161e90f6ec9aeb765d607c788f68974b48f79` | 6 commits ahead of runtime; 0 behind | COMPOSE, BUT BLOCKED UNTIL ITS E2E FAILURES ARE FIXED |
| UI truth closure / PR #32 | `991d7c120ae0982bee08fad1a285d9c387241f00` | diverged: 2 commits ahead / 62 behind; merge-base `cce6fa7575dc71270dc12362edcc5930b153e7a1` | ACCEPT UI DELTA; REBASE/REPLAY LAST |

The UI branch is old by ancestry, but the 62 runtime commits after its merge-base do not modify any of PR #32's ten changed paths. It is therefore a stale-base problem, not a content-conflict problem.

## 2. PR state matrix

| PR | Scope | State observed | Mergeable observed | Classification |
|---|---|---|---|---|
| #32 | final visible-control/UI truth | OPEN, unmerged | true | ACCEPT UI CANDIDATE; replay/rebase after production streams |
| #33 | historical runtime closure | CLOSED, unmerged, draft | n/a | STALE / DO NOT MERGE |
| #34 | historical recovery/completion wrapper | OPEN, unmerged | false | **SUPERSEDED / DO NOT MERGE** |
| #35 | historical dispatch/final-enter closure | OPEN, unmerged | false | **SUPERSEDED / DO NOT MERGE** |
| #36 | Browser final send-boundary safety | OPEN, unmerged | true | ACCEPT BROWSER CANDIDATE |
| #37 | Recovery/rollover durability | OPEN, unmerged | true | ACCEPT RECOVERY CANDIDATE |

### Current CI reality

Do not infer acceptance from mergeability.

- PR #36: Browser-specific test families reached green in the inspected Windows run (`Browser.Tests` 39/39 and `Browser.Acceptance` 30/30), while the full Windows CI remained red because inherited E2E assertions failed.
- PR #37: inspected Windows run built with 0 compiler warnings/errors and reached `App.Tests` 40/40, `Application.Tests` 108/108, `Browser.Acceptance` 27/27, `Browser.Tests` 35/35, and `Domain.Tests` 11/11 before E2E failed on the same stale acceptance expectations.
- E2E head `5d4161e...`: its exact-head acceptance run is red. The run reported 14 E2E failures / 2 passes. Most new failures are test-harness project-lock contamination (`PCC Executive is already controlling project 'PCCEXECUTIVE' on this machine`), plus a stale source-string assertion for `ConversationLifecycleManager`, a `98.99` versus `99` expectation, and a 32-stage harness lookup failure. These are unresolved blockers; no PASS is recorded here.
- PR #32 had green results on its older base, but it must be re-executed after rebasing/replaying onto the final composed head.

## 3. Candidate changed-path ownership

### Browser / PR #36

Owns only:

- `src/PCCExecutive.Browser/DispatchAndResilience.cs`
- `src/PCCExecutive.Infrastructure/BrowserAgentProviderAdapter.cs`
- `tests/PCCExecutive.Browser.Acceptance/FinalBrowserSafetyAcceptanceTests.cs`
- `tests/PCCExecutive.Browser.Tests/FinalBrowserSendBoundaryTests.cs`

### Recovery / PR #37

Owns only:

- `src/PCCExecutive.App/Presentation/AutonomousConversationRolloverRuntime.cs`
- `src/PCCExecutive.App/Presentation/ConversationRecoveryInvariantPlanner.cs`
- `src/PCCExecutive.App/Presentation/DurableProviderAttentionPolicy.cs`
- `tests/PCCExecutive.App.Tests/DurableProviderAttentionPolicyTests.cs`
- `tests/PCCExecutive.App.Tests/ProductionRecoveryWiringContractTests.cs`
- `tests/PCCExecutive.App.Tests/RecoveryRolloverAcceptanceTests.cs`
- `tests/PCCExecutive.Application.Tests/LoopGuardRestartAcceptanceTests.cs`
- `tests/PCCExecutive.Infrastructure.Tests/FinalRecoveryAcceptanceTests.cs`

### E2E branch

Owns only:

- `.github/workflows/runtime-e2e-final.yml`
- `tests/PCCExecutive.E2E/ProductionRuntime32StageAcceptanceTests.cs`
- `tests/PCCExecutive.E2E/ProductionRuntimeAcceptanceHarness.cs`
- `tests/PCCExecutive.E2E/ProductionRuntimeSecurityNegativeTests.cs`

### UI / PR #32

Owns ten App/UI paths only, including `PresentationModels.cs`, `ScreenViewModels.cs`, XAML truth overrides/screens, and `VisibleControlTruthTests.cs`. It does not modify runtime orchestration, Browser, Infrastructure, recovery, dispatch, or completion authority.

There is **no accepted-stream changed-path overlap** between Browser, Recovery, E2E, and UI.

## 4. Composition rehearsal

A temporary local Git topology rehearsal was created from the live runtime ancestry. Direct repository cloning was unavailable in the execution container, so the rehearsal used the exact live Git ancestry and exact changed-path sets fetched from GitHub, then performed real local Git merges in candidate order. Actual production patches/contracts were inspected separately for semantic conflicts.

Rehearsed order:

1. runtime `7b5d8f...`
2. Browser `6d19aec...`
3. Recovery `ccae561...`
4. E2E `5d4161e...`
5. UI `991d7c1...`

Result:

```text
MERGE_browser=CLEAN
MERGE_recovery=CLEAN
MERGE_e2e=CLEAN
MERGE_ui=CLEAN
CONFLICT_COUNT=0
OVERLAP_browser_recovery=[]
OVERLAP_browser_e2e=[]
OVERLAP_browser_ui=[]
OVERLAP_recovery_e2e=[]
OVERLAP_recovery_ui=[]
OVERLAP_e2e_ui=[]
```

**Interpretation:** candidate composition is mechanically clean at the Git/path level. This is not a claim that the compiled composed binary passes; the full exact-head build/test/package sequence remains mandatory.

## 5. Recommended integration order

Use this order unless a stream moves, in which case refetch and repeat the rehearsal:

1. **Latest runtime closure** — establish the canonical composition root and durable orchestration baseline.
2. **PR #36 Browser safety** — establish the strongest final physical-send boundary before recovery/rollover is allowed to generate future sends.
3. **PR #37 Recovery/rollover** — compose automatic rollover, restart normalization, durable attention, and exactly-one-active recovery on top of the strengthened Browser boundary.
4. **Latest E2E branch** — apply acceptance-only changes after production contracts are fixed. Repair its test isolation/assertion drift; do not change production merely to satisfy stale source-string assertions.
5. **PR #32 UI truth closure** — replay/rebase its two commits onto the fully composed production/E2E head, then retest. Do not merge its stale checkpoint ancestry wholesale.

Do **not** include PR #33, #34, or #35 in this sequence.

## 6. Conflict map and exact resolution rules

### Accepted-stream conflicts

**Textual merge conflicts: 0.**

| Surface | Accepted-stream classification | Resolution |
|---|---|---|
| `IntegratedPresentationGateway.cs` / `PccExecutiveRuntimeHost` | CLEAN | Runtime remains composition authority. Accepted worker streams do not replace it. |
| `App.xaml.cs` | CLEAN | Keep `PccExecutiveRuntimeHost.Create()` production root. |
| `BrowserChatProvider` / `DispatchAndResilience.cs` | CLEAN with PR #36 ownership | Take PR #36 current implementation. |
| `BrowserAgentProviderAdapter.cs` | CLEAN with PR #36 ownership | Take PR #36 current implementation. |
| `ChatGptBrowserAdapter.cs` | CLEAN | Keep current runtime adapter contract; PR #36 strengthens authorization around it. |
| `ResilienceHardening.cs` | CLEAN | No accepted stream replaces it. |
| `ConversationLifecycleManager` contracts | CLEAN | Keep current Browser lifecycle files; do not restore historical wrapper code to satisfy a source-string test. |
| `AutonomousConversationRolloverRuntime.cs` | CLEAN with PR #37 ownership | Take current PR #37 recovery implementation. |
| `DurableStartupRecoveryService` | CLEAN | Keep runtime startup-reconstruction order. |
| Loop Guard durability | CLEAN | Keep runtime durable state plus PR #37 restart acceptance. |
| completion authority | CLEAN | Keep runtime evidence-only completion path. |
| E2E production-host composition | CLEAN mechanically; FUNCTIONALLY BLOCKED | Repair E2E test harness/expectations only unless a test proves an actual production defect. |
| workflow files | CLEAN | E2E adds a unique workflow; do not replay #33 historical workflow closure. |
| UI truth files | CLEAN mechanically | Rebase/replay PR #32 last and re-run all App/full-stack tests. |

### Historical/overlapping conflicts: 8 contract overlaps

#### DANGEROUS 1 — PR #34 composition root

PR #34 changes `App.xaml.cs` to instantiate `RecoveryCompletionPresentationGateway` and adds a large wrapper that reaches into `PccExecutiveRuntimeHost` internals. The current runtime already owns startup recovery, auto-resume gating, shutdown coordination, dispatch, completion, health and orchestration.

**Resolution rule:** retain `PccExecutiveRuntimeHost.Create()`. Do not add `RecoveryCompletionPresentationGateway.cs`; do not replace the composition root.

#### DANGEROUS 2 — PR #34 rollover implementation

PR #34 carries an older, wrapper-coupled `AutonomousConversationRolloverRuntime`. PR #37 is the active recovery owner and adds current exactly-one-active planning, durable attention restoration, retired-runtime closure, lifecycle bridging and restart acceptance.

**Resolution rule:** take PR #37's latest rollover implementation. Do not replay PR #34's rollover file or its wrapper-specific accessors.

#### DANGEROUS 3 — PR #34 separate completion authority

PR #34 adds `AuthoritativeCompletionAuthority.cs`, but current `PccExecutiveRuntimeHost` already caps Manager CLOSE below 100 and permits 100 only through independent, fresh authoritative verification.

**Resolution rule:** keep the integrated runtime completion authority. Do not introduce a second authority or a second path to 100.

#### DANGEROUS 4 — PR #35 parallel final-enter contract

PR #35 adds `FinalEnterAuthorization.cs` / `IFinalEnterAuthorizationAdapter`. Current runtime already has `IPhysicalSubmitAuthorizationAdapter`; the physical adapter performs Fill and invokes authorization immediately before Enter. PR #36 further strengthens that boundary by re-reading the runtime, re-inspecting semantic ownership/wrong-chat state, and re-proving PCC ownership immediately before physical Enter.

**Resolution rule:** keep `IPhysicalSubmitAuthorizationAdapter` plus PR #36's fresh final revalidation. Do not add PR #35's parallel interface or rewrite `ChatGptBrowserAdapter` back to that historical contract.

#### SEMANTIC 1 — PR #34 Loop Guard / health projection wrapper

PR #34 duplicates durable Loop Guard/health projection behavior in its wrapper. Runtime and PR #37 already own durable restart/recovery and attention reconstruction.

**Resolution rule:** one durable source of truth only: current runtime store/recovery + PR #37 tests. No wrapper-owned state machine.

#### SEMANTIC 2 — PR #35 `DispatchAndResilience.cs`

PR #35 overlaps the same final-send boundary that PR #36 owns.

**Resolution rule:** PR #36 wins. Its current-runtime + fresh-semantic + fresh-ownership authorization is the required final fence.

#### SEMANTIC 3 — PR #35 `AutonomousDispatchSafety.cs`

PR #35 repeats crash-fence / submitted-unknown protection already present in current runtime's durable dispatch journal/reservation path.

**Resolution rule:** keep current runtime stable dispatch identity and reconciliation. Do not create a second crash-fence implementation.

#### TRIVIAL 1 — PR #35 `BrowserAgentProviderAdapter.cs`

The observed one-line mapping of Browser `Prepared` or `SafeRetry` to domain `PREPARED` is identical to PR #36.

**Resolution rule:** take PR #36; do not replay the duplicate hunk.

### Historical PR classifications

- **PR #34: SUPERSEDED.** No unique production hunk is required for final convergence. Its intended recovery/completion concerns are covered by current runtime + PR #37; UI truth is handled by PR #32.
- **PR #35: SUPERSEDED.** No unique production hunk is required. Its physical-enter objective is already in runtime and strengthened by PR #36; its adapter hunk is duplicated exactly by PR #36.
- **PR #33: STALE/CLOSED.** Do not reopen or merge it. Its closure/workflow changes are already represented in the current runtime baseline or later streams.

## 7. P0 invariant audit

These are **contract-preservation findings**, not final execution PASS claims.

| Invariant | Preflight result | Required final proof |
|---|---|---|
| SEC-P0-001 — stable durable DispatchId, `SUBMITTED_UNKNOWN` reconciliation, zero duplicate submit | PRESERVED BY COMPOSITION | exact-head restart/uncertain-send acceptance; no retry until proven absence/SafeRetry |
| SEC-P0-002 — Manager CLOSE <=99; fresh authoritative evidence only grants 100 | PRESERVED BY COMPOSITION | exact-head stale/failing/missing-test/stale-HEAD negatives plus fresh all-green closure to 100 |
| SEC-P0-003 — fresh authorization after Fill, immediately before Enter | PRESERVED AND STRENGTHENED BY PR #36 | physical Enter counter remains zero for tamper/mismatch/ownership failure; one Enter only for authorized send |
| SEC-P0-004 — `BeginStartup`/`Reconstruct` before AutoResume; `SafeShutdownCoordinator` on normal shutdown | PRESERVED BY UNCHANGED RUNTIME HOST | real dispose/crash/reconstruct/resume acceptance on composed head |

Also preserve and re-prove WorkerSlot/task/conversation correlation; wrong-chat guard; rate-limit/offline/login/challenge global gates; automatic Manager and Worker rollover; exactly one active conversation after restart; Loop Guard durability; retired-conversation zero-send; UI truthfulness.

## 8. Generated-output and package hygiene

Current runtime-tree path audit found zero tracked `bin/**`, `obj/**`, `*.dll`, `*.pdb`, or `*.trx` entries. The accepted candidate streams add source/tests/workflow/UI files only.

The existing `build/Test-GeneratedOutputHygiene.ps1` rejects tracked `bin/`, `obj/`, DLL/PDB, TestResults/temp/cache and log outputs. It does not explicitly list `.trx`, so the final convergence worker must execute an explicit tracked-TRX assertion as an additional gate.

The existing `build/Test-ReleasePayload.ps1` rejects SQLite transient sidecars matching `*.db-wal`, `*.db-shm`, `*.sqlite-wal`, `*.sqlite-shm` (and sqlite3/journal variants), plus source/development/auth/browser-profile contamination.

Required repository hygiene commands on the final composed head:

```powershell
pwsh ./build/Test-GeneratedOutputHygiene.ps1
$trx = @(git ls-files '*.trx')
if ($trx.Count -ne 0) { throw "TRACKED_TRX_REJECTED: $($trx -join ', ')" }
$generated = @(git ls-files | Select-String -Pattern '(^|/)(bin|obj)/|\.(dll|pdb)$|(^|/)TestResults/')
if ($generated.Count -ne 0) { throw "TRACKED_GENERATED_OUTPUT_REJECTED" }
```

## 9. Exact final convergence test sequence

Run only after refetching all live heads and rebuilding the composed branch. The final candidate SHA must remain unchanged through the complete sequence.

### 9.1 Establish exact candidate and build with warnings as errors

```powershell
$ErrorActionPreference = 'Stop'
$sha = (git rev-parse HEAD).Trim().ToLowerInvariant()
git status --porcelain
if ($LASTEXITCODE -ne 0) { throw 'git status failed' }
if (git status --porcelain) { throw 'FINAL_CANDIDATE_WORKTREE_NOT_CLEAN' }

dotnet --version
dotnet restore PCCExecutive.sln
dotnet build PCCExecutive.sln -c Release --no-restore -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'FINAL_BUILD_FAILED' }
```

Zero compiler warnings and zero build errors are mandatory.

### 9.2 Test families — exact required order

```powershell
$filter = 'Category!=LiveBrowser'
$root = 'artifacts/final-convergence/test-results'
New-Item -ItemType Directory -Force -Path $root | Out-Null

dotnet test tests/PCCExecutive.Domain.Tests/PCCExecutive.Domain.Tests.csproj -c Release --no-build --filter $filter --logger 'trx;LogFileName=Domain.Tests.trx' --results-directory $root
dotnet test tests/PCCExecutive.Application.Tests/PCCExecutive.Application.Tests.csproj -c Release --no-build --filter $filter --logger 'trx;LogFileName=Application.Tests.trx' --results-directory $root
dotnet test tests/PCCExecutive.App.Tests/PCCExecutive.App.Tests.csproj -c Release --no-build --filter $filter --logger 'trx;LogFileName=App.Tests.trx' --results-directory $root
dotnet test tests/PCCExecutive.Browser.Tests/PCCExecutive.Browser.Tests.csproj -c Release --no-build --filter $filter --logger 'trx;LogFileName=Browser.Tests.trx' --results-directory $root
dotnet test tests/PCCExecutive.Browser.Acceptance/PCCExecutive.Browser.Acceptance.csproj -c Release --no-build --filter $filter --logger 'trx;LogFileName=Browser.Acceptance.trx' --results-directory $root
dotnet test tests/PCCExecutive.Infrastructure.Tests/PCCExecutive.Infrastructure.Tests.csproj -c Release --no-build --filter $filter --logger 'trx;LogFileName=Infrastructure.Tests.trx' --results-directory $root
dotnet test tests/PCCExecutive.Integration/PCCExecutive.Integration.csproj -c Release --no-build --filter $filter --logger 'trx;LogFileName=Integration.trx' --results-directory $root
dotnet test tests/PCCExecutive.E2E/PCCExecutive.E2E.csproj -c Release --no-build --filter $filter --logger 'trx;LogFileName=E2E.trx' --results-directory $root
```

Any failure or mandatory skip blocks convergence.

### 9.3 Explicit 32-stage/security acceptance

After the full E2E project is green, rerun the named final acceptance classes explicitly:

```powershell
dotnet test tests/PCCExecutive.E2E/PCCExecutive.E2E.csproj -c Release --no-build --filter "FullyQualifiedName~ProductionRuntime32StageAcceptanceTests|FullyQualifiedName~ProductionRuntimeSecurityNegativeTests" --logger 'trx;LogFileName=32-stage-security.trx' --results-directory artifacts/final-convergence/test-results
```

Required proof includes real `PccExecutiveRuntimeHost`, real SQLite restart/reconstruction, stable uncertain dispatch identity, zero duplicate submit, Manager/Worker rollover lineage, exactly-one-active recovery, retired zero-send, global health gates, Loop Guard durability, and fresh evidence-only 100.

### 9.4 Repository hygiene and canonical build orchestration

```powershell
pwsh ./build/Test-GeneratedOutputHygiene.ps1
$trx = @(git ls-files '*.trx'); if ($trx.Count -ne 0) { throw 'TRACKED_TRX_REJECTED' }
pwsh ./build/Build.ps1 -Configuration Release -RequireProduct
```

`Build.ps1` restores/builds the solution and every source project, runs every deterministic test project, then runs the integrated WPF smoke path. The explicit family sequence above remains required so no family can be hidden by discovery/order changes.

### 9.5 Exact-head GitHub gates

Push the single composed candidate head and require all of these to complete successfully **on that exact SHA**:

1. **PCC Executive Release Hardening** — exact source check, release hardening, update integrity, SBOM preflight, readiness report.
2. **PCC Executive Durability Convergence** — exact PR head, Infrastructure build and persistence tests. If this required workflow is absent/skipped, final convergence fails as a missing mandatory family.
3. **PCC Executive Windows CI** — release infrastructure, complete deterministic build/tests, package, install/persistence/uninstall lane.

Do not accept green evidence from an earlier SHA.

## 10. Final Setup / package acceptance sequence

Run only after all source/test gates above are green on the same SHA. Current persisted schema target is **v2**.

### 10.1 Self-contained win-x64 publish and contamination scan

```powershell
pwsh ./build/Publish-Windows.ps1 -Configuration Release -Runtime win-x64 -OutputRoot artifacts/publish/win-x64
pwsh ./build/Test-ReleasePayload.ps1 -PayloadRoot artifacts/publish/win-x64
```

`Publish-Windows.ps1` performs a self-contained `win-x64` publish, suppresses debug symbols, validates the payload, and writes an exact-source publish manifest.

### 10.2 Published-app smoke, Setup EXE, provenance, SBOM and manifests

With Inno Setup 6+ installed:

```powershell
pwsh ./build/Package.ps1 -Configuration Release -Runtime win-x64
```

`Package.ps1` is the canonical package orchestration. It re-runs build/tests; performs the published-app smoke before installer creation; builds `PCCExecutive-0.1.0-Setup-x64.exe`; writes exact source/build provenance; generates the SBOM; writes update/release manifests; hashes the app and installer; and runs manifest/package verification. Failure at any step blocks Setup acceptance.

Then explicitly re-scan the produced publish payload:

```powershell
pwsh ./build/Test-ReleasePayload.ps1 -PayloadRoot artifacts/publish/win-x64
```

The scan must reject any injected or real `*.db-wal`, `*.db-shm`, `*.sqlite-wal`, `*.sqlite-shm`, source file, developer log, browser-profile/auth file, secret pattern, or other forbidden contamination.

### 10.3 Fresh install, launch, schema-v2 first run, persistence reopen, uninstall preserve-data

Use the exact setup produced from `$sha`:

```powershell
$version = (Get-Content VERSION -Raw).Trim()
$installer = "artifacts/package/PCCExecutive-$version-Setup-x64.exe"
$manifest = "artifacts/package/PCCExecutive-$version-Setup-x64.manifest.json"

pwsh ./tests/installer/Test-Package.ps1 -InstallerPath $installer -ManifestPath $manifest -ExpectedSourceSha $sha
pwsh ./tests/installer/Smoke-FreshInstall.ps1 -InstallerPath $installer -ExpectedVersion $version -ExpectedSourceSha $sha -EvidencePath artifacts/install-evidence/fresh-install.json
pwsh ./tests/installer/Smoke-FirstRun.ps1 -InstallRoot "$env:LOCALAPPDATA/PCC Executive Smoke/Fresh" -ExpectedSchemaVersion 2 -EvidencePath artifacts/install-evidence/first-run.json
pwsh ./tests/installer/Smoke-Persistence.ps1 -InstallRoot "$env:LOCALAPPDATA/PCC Executive Smoke/Fresh" -EvidencePath artifacts/install-evidence/persistence-reopen.json
pwsh ./tests/installer/Smoke-Uninstall.ps1 -InstallRoot "$env:LOCALAPPDATA/PCC Executive Smoke/Fresh"
```

Acceptance requires: successful launch from the installed package, schema v2 on first run, persisted state surviving close/reopen, uninstall preserving user data according to the installer contract, exact SHA/version provenance, valid hashes/manifests/SBOM, and no package contamination.

## 11. Unresolved blockers before final convergence

1. **E2E branch is red.** The exact-head run has 14 E2E failures / 2 passes; final convergence cannot proceed as READY until these are repaired and rerun.
2. **E2E ProjectRunLock isolation is broken across multiple new tests.** Test cleanup/isolation must release the machine-level project lock deterministically; do not weaken production singleton locking.
3. **Stale source-string assertion:** `FinalRuntimeSourceSafetyTests` expects a `ConversationLifecycleManager` literal in a production source location that no longer reflects the current implementation. Update the acceptance assertion to prove the current lifecycle contract/behavior; do not restore PR #34 to satisfy text matching.
4. **`98.99` versus `99` assertion drift:** repair the test expectation to the canonical completion semantics without weakening SEC-P0-002. Manager CLOSE must remain <=99 and only fresh authoritative exact-head evidence may grant 100.
5. **New 32-stage harness lookup failure** (`Sequence contains no matching element`) must be fixed in the acceptance harness or, if it exposes a real production correlation defect, handed back to the owning production worker with evidence.
6. **Browser/Recovery Windows CI remains red because the inherited E2E project is red.** Their focused production suites being green is not sufficient for final convergence.
7. **UI PR #32 uses stale ancestry.** Replay/rebase its UI-only delta after Browser/Recovery/E2E composition and execute the complete test/package matrix again.
8. **No full compiled/package run has been performed on the five-stream composed rehearsal candidate in this preflight task.** Mechanical composition is proven clean; release acceptance remains pending.
9. **PR #34 and #35 remain open historical streams.** They must not be merged merely because they are open; both are superseded and conflict semantically with current authority.

## 12. Final handoff rule

Final Convergence should proceed only after the E2E blockers are resolved and all candidate heads are refetched. If any head moves, repeat ancestry/path/semantic rehearsal before integrating. The target branch must remain one coherent production composition with one Browser final-enter authority, one durable dispatch identity/reconciliation path, one recovery/rollover authority, one completion authority, and one truthful UI projection.

**Preflight conclusion:** merge mechanics are clean (`0` accepted-stream text conflicts), historical overlap is understood and fenced, the integration/test/package sequence is ready, but the candidate is **NOT READY FOR FINAL CONVERGENCE** until the current E2E failures and resulting red full Windows CI are cleared on the exact composed head.
