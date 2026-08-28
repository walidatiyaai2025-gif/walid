# PCC Executive 0.1.0 — Final Convergence Preflight

**Authority:** Issue #1  
**Canonical task:** `PCCEXECUTIVE-T0001`  
**Scope:** composition/conflict/release-proof planning only. No production implementation is owned by this branch and no PASS below is inferred from mergeability.

## 1. Final live snapshot used by this preflight

| Stream | Live head | Relationship / disposition |
|---|---|---|
| Runtime closure | `7b5d8f975baf7e4a56681e3cc538ea552c7c5d59` | canonical composition base |
| Browser / PR #36 | `6d19aecca2787b9152d8b116b4aaa58df25f22a2` | 4 commits ahead of runtime; accepted Browser candidate |
| Recovery / PR #37 | `db775f43fbd335694f1efac6158e36992db77d45` | 15 commits ahead of runtime; accepted Recovery candidate |
| E2E / PR #38 | `6bc3b852beab5f386905130a66a88a8a41667fb9` | 30 commits ahead of runtime; **already composes Browser + Recovery + E2E** |
| UI / PR #32 | `991d7c120ae0982bee08fad1a285d9c387241f00` | stale ancestry: 2 commits ahead / 62 behind runtime merge-base `cce6fa7575dc71270dc12362edcc5930b153e7a1`; replay last |

The E2E head `6bc3b852...` is an octopus composition commit whose parents are the prior E2E head `987b1d79...`, Browser `6d19aecc...`, and Recovery `db775f43...`. Therefore Browser and Recovery must **not** be merged again independently and then re-applied as duplicate implementation. For final convergence, treat `6bc3b852...` as the consolidated B+C+D candidate after verifying its exact-head CI.

PR #32 is stale only by ancestry: runtime's 62 commits since its merge-base do not touch the ten UI paths changed by PR #32. Rebase/replay its UI delta last; do not merge its old checkpoint ancestry wholesale.

## 2. PR state matrix

| PR | State | Mergeable | Preflight classification |
|---|---|---:|---|
| #32 UI truth | OPEN, unmerged | true | ACCEPT UI DELTA; replay last |
| #33 historical runtime closure | CLOSED, unmerged, draft | n/a | STALE / DO NOT MERGE |
| #34 recovery/completion wrapper | OPEN, unmerged | false | **SUPERSEDED / DO NOT MERGE** |
| #35 dispatch/final-enter v2 | OPEN, unmerged | false | **SUPERSEDED / DO NOT MERGE** |
| #36 Browser safety | OPEN, unmerged | true | ACCEPTED INTO LATEST E2E COMPOSED HEAD |
| #37 Recovery/rollover | OPEN, unmerged | true | ACCEPTED INTO LATEST E2E COMPOSED HEAD |
| #38 E2E green-gate | OPEN, unmerged | true | CONSOLIDATED B+C+D CANDIDATE; exact-head gates still running at snapshot |

### CI facts at snapshot

- Browser #36 focused Browser suites were green in its inspected run; its older full Windows result was red because then-current E2E assertions were red.
- Recovery #37 at `db775f43...` is independently green for **Release Hardening, Durability Convergence, and Windows CI**.
- Previous E2E head `987b1d79...` built with 0 compiler warnings/errors and passed App/Application/Browser/Domain suites, but E2E remained red (12 failed / 4 passed) because the acceptance harness released a process-wide `ProjectRunLock` mutex from a non-owner context, then leaked the lock into subsequent tests.
- Latest composed E2E head `6bc3b852...`: Release Hardening was green; E2E Final Acceptance, Durability Convergence and Windows CI were still **in progress** at the final snapshot used to write this document. This preflight therefore records no final PASS for that head.
- PR #38 also reports a production-owner blocker discovered by E2E: logical conversation identity formatting differs (`D` vs canonical `N`) and can fail closed in `BrowserAgentProviderAdapter` with `WRONG_CONVERSATION_BINDING` before physical Enter. This must be resolved/verified by the owning runtime/recovery stream, not by this preflight branch.

## 3. Changed-path ownership and composition

### Browser #36

- `src/PCCExecutive.Browser/DispatchAndResilience.cs`
- `src/PCCExecutive.Infrastructure/BrowserAgentProviderAdapter.cs`
- `tests/PCCExecutive.Browser.Acceptance/FinalBrowserSafetyAcceptanceTests.cs`
- `tests/PCCExecutive.Browser.Tests/FinalBrowserSendBoundaryTests.cs`

### Recovery #37

- `src/PCCExecutive.App/Presentation/AutonomousConversationRolloverRuntime.cs`
- `src/PCCExecutive.App/Presentation/ConversationRecoveryInvariantPlanner.cs`
- `src/PCCExecutive.App/Presentation/DurableProviderAttentionPolicy.cs`
- `src/PCCExecutive.App/Presentation/RecoveryRolloverLifecycleBridge.cs`
- Recovery/App/Application/Infrastructure acceptance tests
- `tests/PCCExecutive.E2E/Final32StageE2ETests.cs`

### E2E #38

Latest head contains Browser + Recovery plus:

- `.github/workflows/runtime-e2e-final.yml`
- `tests/PCCExecutive.E2E/AssemblyInfo.cs`
- `tests/PCCExecutive.E2E/Final32StageE2ETests.cs`
- `tests/PCCExecutive.E2E/FinalRuntimeSourceSafetyTests.cs`
- `tests/PCCExecutive.E2E/ProductionRuntime32StageAcceptanceTests.cs`
- `tests/PCCExecutive.E2E/ProductionRuntimeAcceptanceHarness.cs`
- `tests/PCCExecutive.E2E/ProductionRuntimeSecurityNegativeTests.cs`

### UI #32

Ten App/UI paths only: App/MainWindow XAML, presentation/view-model truth projection, visible-control override XAML files, screens 04–09/13–15 and `VisibleControlTruthTests.cs`. No Browser, Infrastructure, orchestration, recovery or completion authority file is changed.

## 4. Composition rehearsal result

The execution container could not clone GitHub directly because outbound DNS was unavailable. The rehearsal therefore used live Git ancestry/changed-path/patch data fetched through the GitHub connector, constructed a temporary local Git topology, and performed real local merges. The single shared Recovery↔E2E hunk was additionally rehearsed with its exact patch content.

Logical rehearsal order before E2E composed the streams:

1. Runtime `7b5d8f...`
2. Browser `6d19aec...`
3. Recovery `db775f4...`
4. E2E acceptance
5. UI `991d7c1...`

Result:

```text
Browser -> CLEAN
Recovery -> CLEAN
Recovery/E2E shared Final32StageE2ETests hunk -> CLEAN
UI -> CLEAN
ACTUAL_TEXT_CONFLICTS=0
```

The only accepted-stream overlap was `tests/PCCExecutive.E2E/Final32StageE2ETests.cs`: Recovery and E2E both changed the canonical completion expectation from `99 / ClosureMode` to `98.99 / Active`; E2E adds explanatory comments. The exact hunk merged with zero unmerged entries. Classification: **TRIVIAL**, E2E version wins because semantics are identical plus comments.

The latest E2E head subsequently composed Browser and Recovery itself. Thus the safest current integration unit is now:

1. runtime `7b5d8f...`
2. **consolidated E2E head `6bc3b852...`** (contains Browser + Recovery + E2E)
3. UI delta `991d7c1...` replayed/rebased last

Do not separately merge #36/#37 again after taking `6bc3b852...`.

## 5. Ownership conflict audit

### Accepted candidate surfaces

| Surface | Classification | Correct resolution |
|---|---|---|
| `IntegratedPresentationGateway.cs` / `PccExecutiveRuntimeHost` | CLEAN | runtime remains production composition authority |
| `App.xaml.cs` | CLEAN | keep `PccExecutiveRuntimeHost.Create()` |
| `BrowserChatProvider` / `DispatchAndResilience.cs` | CLEAN | latest Browser #36 implementation already in E2E head |
| `BrowserAgentProviderAdapter.cs` | CLEAN | latest Browser #36 implementation; re-prove conversation-format correlation |
| `ChatGptBrowserAdapter.cs` | CLEAN | keep current physical submit contract |
| `ResilienceHardening` | CLEAN | no accepted stream replaces it |
| conversation lifecycle | CLEAN | keep current lifecycle primitives + Recovery lifecycle bridge |
| `AutonomousConversationRolloverRuntime.cs` | CLEAN | latest Recovery #37 implementation already in E2E head |
| startup recovery | CLEAN | preserve runtime `BeginStartup/Reconstruct` ordering |
| Loop Guard | CLEAN | preserve durable runtime state + Recovery restart acceptance |
| completion authority | CLEAN | preserve integrated evidence-only completion path |
| E2E production-host composition | CLEAN mechanically / NOT YET GREEN | repair acceptance isolation or real owner defects only; do not add test-only orchestration |
| workflows | CLEAN | E2E workflow is unique; no historical workflow replay |
| UI truth | CLEAN mechanically | replay #32 last and rerun full matrix |

### Historical conflict map: 8 overlaps

**DANGEROUS (4)**

1. **PR #34 composition root:** replaces App startup with `RecoveryCompletionPresentationGateway`. Resolution: keep `PccExecutiveRuntimeHost.Create()`; never add the parallel wrapper.
2. **PR #34 rollover:** older wrapper-coupled rollover conflicts with current Recovery #37. Resolution: #37 wins.
3. **PR #34 completion authority:** adds a second `AuthoritativeCompletionAuthority`. Resolution: keep the single integrated runtime authority; no second route to 100.
4. **PR #35 final-enter contract:** adds parallel `IFinalEnterAuthorizationAdapter`/`FinalEnterAuthorization`. Resolution: keep current `IPhysicalSubmitAuthorizationAdapter` plus #36's fresh post-Fill semantic/runtime/ownership revalidation immediately before Enter.

**SEMANTIC (3)**

1. PR #34 Loop Guard/health wrapper duplicates durable runtime/recovery state. Keep one durable source of truth.
2. PR #35 `DispatchAndResilience.cs` overlaps #36's final-send boundary. #36 wins.
3. PR #35 `AutonomousDispatchSafety.cs` duplicates current canonical dispatch reservation/crash-fence/reconciliation. Keep current stable DispatchId path only.

**TRIVIAL historical duplicate (1)**

- PR #35 `BrowserAgentProviderAdapter.cs` maps Browser Prepared/SafeRetry to domain PREPARED identically to #36. Take #36 only.

### Historical stream decisions

- **PR #34 = SUPERSEDED.** No unique production hunk is required.
- **PR #35 = SUPERSEDED.** No unique production hunk is required.
- **PR #33 = STALE/CLOSED.** Do not reopen/replay.

## 6. P0 invariant audit

These are composition-preservation findings, not final execution PASS claims.

| Invariant | Preflight finding | Mandatory final proof |
|---|---|---|
| SEC-P0-001 stable durable DispatchId / `SUBMITTED_UNKNOWN` / zero duplicate submit | PRESERVED BY CONTRACT | exact-head uncertain-send restart, duplicate block, SafeRetry only after proven absence |
| SEC-P0-002 Manager CLOSE <=99; fresh authoritative evidence only grants 100 | PRESERVED BY CONTRACT | all stale/failing/missing-test/stale-HEAD negatives + fresh exact-head all-green 100 |
| SEC-P0-003 authorization after Fill immediately before Enter | PRESERVED / STRENGTHENED BY #36 | zero Enter for identity/ownership tamper; one Enter only after final fresh authorization |
| SEC-P0-004 BeginStartup/Reconstruct before AutoResume; SafeShutdown on normal shutdown | PRESERVED BY RUNTIME HOST | real crash/dispose/reconstruct/resume and normal shutdown proof |

Also re-prove WorkerSlot/task/logical/provider conversation correlation, wrong-chat guard, rate-limit/offline/login/challenge global gates, Manager and Worker rollover, exactly-one-active recovery, Loop Guard durability, retired conversation zero-send and UI truthfulness.

## 7. Generated-file and package hygiene

Runtime-tree audit found zero tracked `bin/**`, `obj/**`, `*.dll`, `*.pdb`, `*.trx`, or `TestResults/**`. Accepted streams add source/test/workflow/UI content only.

Final repository gate:

```powershell
pwsh ./build/Test-GeneratedOutputHygiene.ps1
$bad = @(git ls-files | Select-String -Pattern '(^|/)(bin|obj|TestResults)/|\.(dll|pdb|trx)$')
if ($bad.Count -ne 0) { throw "TRACKED_GENERATED_OUTPUT_REJECTED: $($bad -join ', ')" }
```

The existing release payload scanner rejects SQLite transient sidecars including `*.db-wal`, `*.db-shm`, `*.sqlite-wal`, `*.sqlite-shm`, plus source/development/auth/profile contamination. Execute it on the exact final publish/package payload; no package PASS is claimed by this document.

## 8. Exact final convergence test sequence

Refetch heads first. Freeze one composed SHA and run all commands against that same SHA.

### Build

```powershell
$ErrorActionPreference = 'Stop'
$sha = (git rev-parse HEAD).Trim().ToLowerInvariant()
if (git status --porcelain) { throw 'FINAL_CANDIDATE_WORKTREE_NOT_CLEAN' }
dotnet restore PCCExecutive.sln
dotnet build PCCExecutive.sln -c Release --no-restore -warnaserror
```

Mandatory: 0 build errors, 0 compiler warnings.

### Test families in exact order

```powershell
$filter='Category!=LiveBrowser'
$results='artifacts/final-convergence/test-results'
New-Item -ItemType Directory -Force $results | Out-Null

dotnet test tests/PCCExecutive.Domain.Tests/PCCExecutive.Domain.Tests.csproj -c Release --no-build --filter $filter --logger 'trx;LogFileName=Domain.Tests.trx' --results-directory $results
dotnet test tests/PCCExecutive.Application.Tests/PCCExecutive.Application.Tests.csproj -c Release --no-build --filter $filter --logger 'trx;LogFileName=Application.Tests.trx' --results-directory $results
dotnet test tests/PCCExecutive.App.Tests/PCCExecutive.App.Tests.csproj -c Release --no-build --filter $filter --logger 'trx;LogFileName=App.Tests.trx' --results-directory $results
dotnet test tests/PCCExecutive.Browser.Tests/PCCExecutive.Browser.Tests.csproj -c Release --no-build --filter $filter --logger 'trx;LogFileName=Browser.Tests.trx' --results-directory $results
dotnet test tests/PCCExecutive.Browser.Acceptance/PCCExecutive.Browser.Acceptance.csproj -c Release --no-build --filter $filter --logger 'trx;LogFileName=Browser.Acceptance.trx' --results-directory $results
dotnet test tests/PCCExecutive.Infrastructure.Tests/PCCExecutive.Infrastructure.Tests.csproj -c Release --no-build --filter $filter --logger 'trx;LogFileName=Infrastructure.Tests.trx' --results-directory $results
dotnet test tests/PCCExecutive.Integration/PCCExecutive.Integration.csproj -c Release --no-build --filter $filter --logger 'trx;LogFileName=Integration.trx' --results-directory $results
dotnet test tests/PCCExecutive.E2E/PCCExecutive.E2E.csproj -c Release --no-build --filter $filter --logger 'trx;LogFileName=E2E.trx' --results-directory $results
```

Then explicit final acceptance:

```powershell
dotnet test tests/PCCExecutive.E2E/PCCExecutive.E2E.csproj -c Release --no-build --filter "FullyQualifiedName~ProductionRuntime32StageAcceptanceTests|FullyQualifiedName~ProductionRuntimeSecurityNegativeTests" --logger 'trx;LogFileName=32-stage-security.trx' --results-directory $results
pwsh ./build/Test-GeneratedOutputHygiene.ps1
pwsh ./build/Build.ps1 -Configuration Release -RequireProduct
```

Required: 0 failures, 0 mandatory skips.

### Exact-head GitHub workflow gates

On the exact same SHA require, in this order:

1. **PCC Executive Release Hardening**
2. **PCC Executive Durability Convergence**
3. **PCC Executive Windows CI**

Additionally require the E2E branch's **PCC Runtime E2E Final Acceptance** when present. No green evidence from an older SHA may be substituted.

## 9. Setup / release acceptance sequence

Only after all source/test gates are green on the same SHA. Current schema target is **v2**.

### Self-contained win-x64 publish and payload scan

```powershell
pwsh ./build/Publish-Windows.ps1 -Configuration Release -Runtime win-x64 -OutputRoot artifacts/publish/win-x64
pwsh ./build/Test-ReleasePayload.ps1 -PayloadRoot artifacts/publish/win-x64
```

### Setup EXE + provenance + SBOM + manifest

```powershell
pwsh ./build/Package.ps1 -Configuration Release -Runtime win-x64
pwsh ./build/Test-ReleasePayload.ps1 -PayloadRoot artifacts/publish/win-x64
```

`Package.ps1` is the canonical package orchestration: release build/tests, self-contained publish, published-app smoke, Setup EXE, exact-source provenance, hashes, SBOM, release/update manifests and package verification. Any failure blocks release acceptance.

### Fresh install / first run / persistence / uninstall

```powershell
$version=(Get-Content VERSION -Raw).Trim()
$installer="artifacts/package/PCCExecutive-$version-Setup-x64.exe"
$manifest="artifacts/package/PCCExecutive-$version-Setup-x64.manifest.json"

pwsh ./tests/installer/Test-Package.ps1 -InstallerPath $installer -ManifestPath $manifest -ExpectedSourceSha $sha
pwsh ./tests/installer/Smoke-FreshInstall.ps1 -InstallerPath $installer -ExpectedVersion $version -ExpectedSourceSha $sha -EvidencePath artifacts/install-evidence/fresh-install.json
pwsh ./tests/installer/Smoke-FirstRun.ps1 -InstallRoot "$env:LOCALAPPDATA/PCC Executive Smoke/Fresh" -ExpectedSchemaVersion 2 -EvidencePath artifacts/install-evidence/first-run.json
pwsh ./tests/installer/Smoke-Persistence.ps1 -InstallRoot "$env:LOCALAPPDATA/PCC Executive Smoke/Fresh" -EvidencePath artifacts/install-evidence/persistence-reopen.json
pwsh ./tests/installer/Smoke-Uninstall.ps1 -InstallRoot "$env:LOCALAPPDATA/PCC Executive Smoke/Fresh"
```

Acceptance requires installed launch, schema-v2 first run, persistence after reopen, uninstall preserving user data by contract, exact SHA/version provenance, valid SBOM/manifests/hashes, and zero development/test/SQLite-sidecar contamination.

## 10. Unresolved blockers / stop conditions

1. Latest consolidated E2E head `6bc3b852...` had only Release Hardening completed at snapshot; E2E Final Acceptance, Durability Convergence and Windows CI were still running. Do not call it accepted until all required exact-head gates are green.
2. Previous E2E head exposed a deterministic ProjectRunLock harness disposal defect (`ReleaseMutex` from an unsynchronized/non-owner context) followed by lock leakage. Latest head must prove that this is resolved; do not weaken the production singleton lock.
3. PR #38 reports a production logical-conversation formatting mismatch (`D` vs `N`) that can yield `WRONG_CONVERSATION_BINDING` before Enter. The owning production worker must resolve/verify it; this preflight must not implement a parallel fix.
4. UI #32 must be replayed/rebased onto the final consolidated production/E2E head and the entire matrix rerun.
5. No full Setup/install lifecycle has been executed on the final `6bc3b852... + UI` composition by this preflight worker. Package acceptance remains pending.
6. PR #34/#35 remain open but are superseded and must not be merged merely because they are open.

## 11. Final convergence handoff

Current safest order is **Runtime -> consolidated E2E(Browser+Recovery+E2E) -> UI**, not Runtime -> Browser -> Recovery -> the same consolidated E2E head, because that would replay already-composed streams. If any live head moves, refetch ancestry, compare changed paths, and repeat the overlap rehearsal before integrating.

**Preflight result:** source/patch composition is mechanically clean with **0 actual text conflicts**, one accepted TRIVIAL test overlap already resolved cleanly, and all historical dangerous/semantic overlaps have explicit resolution rules. The test/package plan is ready. **READY_FOR_FINAL_CONVERGENCE remains NO until the latest consolidated exact-head workflows and the reported runtime correlation blocker are green/resolved, then UI is replayed and the full matrix + Setup lifecycle passes on one immutable SHA.**
