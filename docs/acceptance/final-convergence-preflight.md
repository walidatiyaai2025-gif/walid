# PCC Executive 0.1.0 — Final Convergence Preflight

Authority: Issue #1  
Canonical task: `PCCEXECUTIVE-T0001`  
Scope: evidence/planning only. No production feature implementation is owned here and no release PASS is fabricated.

## Live heads inspected

| Stream | Live head | Preflight disposition |
|---|---|---|
| Runtime closure | `7b5d8f975baf7e4a56681e3cc538ea552c7c5d59` | canonical base |
| Browser / PR #36 | `6d19aecca2787b9152d8b116b4aaa58df25f22a2` | accepted Browser candidate |
| Recovery / PR #37 | `db775f43fbd335694f1efac6158e36992db77d45` | accepted Recovery candidate |
| E2E / PR #38 | `6bc3b852beab5f386905130a66a88a8a41667fb9` | consolidated Browser + Recovery + E2E candidate |
| UI / PR #32 | `991d7c120ae0982bee08fad1a285d9c387241f00` | stale ancestry; replay/rebase last |

The latest E2E head is an octopus composition commit with parents `987b1d79...` (prior E2E), `6d19aecc...` (Browser), and `db775f43...` (Recovery). A final convergence worker must therefore not merge #36 and #37 separately and then replay the same composed E2E head. Treat `6bc3b852...` as the current consolidated B+C+D unit.

PR #32 is 2 commits ahead / 62 behind the current runtime from merge-base `cce6fa7575dc71270dc12362edcc5930b153e7a1`, but the intervening runtime commits do not touch its ten UI paths. Its problem is stale ancestry, not a production-file merge conflict.

## PR state matrix

| PR | State | Mergeable | Classification |
|---|---|---:|---|
| #32 | OPEN, unmerged | true | ACCEPT UI DELTA; replay last |
| #33 | CLOSED, unmerged, draft | n/a | STALE / DO NOT MERGE |
| #34 | OPEN, unmerged | false | SUPERSEDED / DO NOT MERGE |
| #35 | OPEN, unmerged | false | SUPERSEDED / DO NOT MERGE |
| #36 | OPEN, unmerged | true | accepted and already included in `6bc3b852...` |
| #37 | OPEN, unmerged | true | accepted and already included in `6bc3b852...` |
| #38 | OPEN, unmerged | true | consolidated B+C+D candidate; mandatory gates still red |

## Exact-head CI status

Recovery `db775f43...` is independently green for Release Hardening, Durability Convergence, and Windows CI.

Consolidated E2E `6bc3b852...` completed:

- Release Hardening: SUCCESS
- Durability Convergence: SUCCESS
- PCC Runtime E2E Final Acceptance: FAILURE
- PCC Executive Windows CI: FAILURE
- build/compiler: 0 warnings, 0 errors before test failure
- App.Tests: 40/40
- Application.Tests: 108/108
- Browser.Acceptance: 30/30
- Browser.Tests: 39/39
- Domain.Tests: 11/11
- E2E: 12 failed / 4 passed / 0 skipped

The first current E2E failure is deterministic acceptance-harness cleanup: the production-host harness disposes the process-wide project lock from a context that does not own its mutex, causing `ReleaseMutex` to fail. The process lock then remains held and later tests fail with the existing-project-control guard. Correct action: repair E2E lock ownership/isolation; do not weaken production singleton locking.

PR #38 also reports a separate production-owner correlation blocker: logical conversation identity formatting can differ between runtime binding and canonical `ConversationId` representation, causing a fail-closed `WRONG_CONVERSATION_BINDING` before Enter. This must be resolved or disproved by the runtime/recovery owner; preflight must not implement a parallel fix.

## Composition rehearsal

Direct repository cloning was unavailable in the execution container because outbound DNS was unavailable. The rehearsal therefore used exact live Git ancestry, changed-file sets and patches fetched through the GitHub connector, created a temporary local Git topology, and performed real local merges. The sole shared Recovery/E2E hunk was additionally replayed with exact patch content.

Logical order rehearsed before E2E composed the streams:

1. Runtime
2. Browser
3. Recovery
4. E2E
5. UI

Result:

```text
Browser=CLEAN
Recovery=CLEAN
Recovery/E2E shared Final32StageE2ETests hunk=CLEAN
UI=CLEAN
ACTUAL_TEXT_CONFLICTS=0
```

The only accepted-stream overlap is `tests/PCCExecutive.E2E/Final32StageE2ETests.cs`. Recovery and E2E apply the same completion expectation (`98.99 / Active`); E2E adds explanatory comments. Exact-hunk rehearsal had zero unmerged entries. Classification: TRIVIAL; use the E2E version.

Because the latest E2E head now already contains Browser and Recovery, the current safest integration order is:

1. runtime `7b5d8f...`
2. consolidated Browser+Recovery+E2E `6bc3b852...`
3. replay/rebase UI `991d7c1...` last

## Conflict map and resolution rules

Accepted candidate surfaces:

| Surface | Class | Resolution |
|---|---|---|
| `IntegratedPresentationGateway.cs` / `PccExecutiveRuntimeHost` | CLEAN | runtime remains composition authority |
| `App.xaml.cs` | CLEAN | retain `PccExecutiveRuntimeHost.Create()` |
| Browser send boundary | CLEAN | use #36 version already in consolidated head |
| `BrowserAgentProviderAdapter` | CLEAN, correlation verification pending | keep #36 code; prove exact task/slot/conversation correlation |
| `ChatGptBrowserAdapter` | CLEAN | keep current physical-submit contract |
| resilience/global gates | CLEAN | no historical replacement |
| conversation lifecycle | CLEAN | current lifecycle primitives + #37 bridge |
| rollover | CLEAN | use latest #37 implementation |
| startup recovery | CLEAN | preserve BeginStartup/Reconstruct before AutoResume |
| Loop Guard | CLEAN | preserve durable state/history |
| completion authority | CLEAN | preserve one evidence-only authority |
| E2E production-host composition | CLEAN mechanically / RED functionally | fix acceptance isolation or owner-proven defects only |
| workflow files | CLEAN | no #33 replay |
| UI truth | CLEAN mechanically | replay #32 last and rerun everything |

Historical/overlapping streams contain 8 contract overlaps:

### DANGEROUS (4)

1. PR #34 replaces the production root with `RecoveryCompletionPresentationGateway`. Keep `PccExecutiveRuntimeHost.Create()` and do not add a parallel composition wrapper.
2. PR #34 carries older wrapper-coupled rollover. Latest #37 rollover wins.
3. PR #34 adds a second completion authority. Keep the single integrated runtime authority; no second route to 100.
4. PR #35 adds a parallel final-enter authorization contract. Keep current `IPhysicalSubmitAuthorizationAdapter` plus #36's fresh post-Fill semantic/runtime/ownership authorization immediately before Enter.

### SEMANTIC (3)

1. PR #34 duplicates Loop Guard/health projection state. Keep one durable runtime/recovery source of truth.
2. PR #35 overlaps `DispatchAndResilience`; #36 wins.
3. PR #35 duplicates stable DispatchId/crash-fence/reconciliation. Keep current canonical durable dispatch path only.

### TRIVIAL historical duplicate (1)

PR #35's `BrowserAgentProviderAdapter` state mapping duplicates #36. Take #36 only.

Therefore:

- PR #34 = SUPERSEDED. No unique production hunk required.
- PR #35 = SUPERSEDED. No unique production hunk required.
- PR #33 = STALE/CLOSED. Do not reopen/replay.

## P0 invariant audit

These are preservation findings, not final PASS claims.

| Invariant | Finding | Required final proof |
|---|---|---|
| SEC-P0-001 stable durable DispatchId, uncertain reconciliation, zero duplicate submit | PRESERVED | restart with same uncertain identity; no retry until proven absence/SafeRetry |
| SEC-P0-002 Manager CLOSE <=99; fresh authority only grants 100 | PRESERVED | stale/failing/missing-test/stale-head negatives plus fresh exact-head all-green 100 |
| SEC-P0-003 fresh authorization after Fill immediately before Enter | PRESERVED/STRENGTHENED | zero Enter for mismatch/tamper/ownership failure; one Enter only after final authorization |
| SEC-P0-004 startup reconstruct before AutoResume; safe normal shutdown | PRESERVED | real crash/reconstruct/resume and normal-shutdown evidence |

Also re-prove WorkerSlot/task/logical/provider conversation correlation, wrong-chat guard, rate-limit/offline/login/challenge global gates, Manager and Worker rollover, exactly-one-active recovery, Loop Guard durability, retired-conversation zero-send, and UI truthfulness.

## Generated-file hygiene

Current runtime-tree audit found zero tracked `bin/**`, `obj/**`, `*.dll`, `*.pdb`, `*.trx`, or `TestResults/**`. Final convergence must run the existing generated-output hygiene script and an explicit tracked-TRX check. The release payload scanner must reject `*.db-wal`, `*.db-shm`, `*.sqlite-wal`, `*.sqlite-shm` plus development/test/auth/profile contamination.

## Required final test sequence

Freeze one exact composed SHA. Build with warnings as errors, then run in this exact family order:

1. `PCCExecutive.Domain.Tests`
2. `PCCExecutive.Application.Tests`
3. `PCCExecutive.App.Tests`
4. `PCCExecutive.Browser.Tests`
5. `PCCExecutive.Browser.Acceptance`
6. `PCCExecutive.Infrastructure.Tests`
7. `PCCExecutive.Integration`
8. `PCCExecutive.E2E`
9. explicit `ProductionRuntime32StageAcceptanceTests` + `ProductionRuntimeSecurityNegativeTests`
10. `build/Test-GeneratedOutputHygiene.ps1` plus `git ls-files '*.trx'` must return zero
11. `build/Build.ps1 -Configuration Release -RequireProduct`

Mandatory result: 0 failed, 0 mandatory skipped, 0 build errors, 0 compiler warnings.

Then require exact-head GitHub workflows, all on the same SHA:

1. PCC Executive Release Hardening
2. PCC Executive Durability Convergence
3. PCC Runtime E2E Final Acceptance
4. PCC Executive Windows CI

No older-SHA green evidence may substitute for a current failure.

## Final Setup / package acceptance sequence

Only after the source/test gates are green on one SHA. Current schema target is v2.

Run, in order:

1. `build/Publish-Windows.ps1 -Configuration Release -Runtime win-x64 -OutputRoot artifacts/publish/win-x64`
2. `build/Test-ReleasePayload.ps1 -PayloadRoot artifacts/publish/win-x64`
3. published-app smoke
4. `build/Package.ps1 -Configuration Release -Runtime win-x64`
5. re-run release-payload scanner
6. verify Setup EXE source provenance and hashes
7. verify SBOM and release/update manifests
8. `tests/installer/Test-Package.ps1` against exact source SHA
9. `Smoke-FreshInstall.ps1`
10. installed application launch
11. `Smoke-FirstRun.ps1` with expected schema version 2
12. `Smoke-Persistence.ps1` after close/reopen
13. `Smoke-Uninstall.ps1`
14. verify uninstall preserves user data by contract
15. re-assert zero development/test/SQLite-sidecar contamination

## Unresolved blockers

1. Latest consolidated `6bc3b852...` is RED: E2E Final Acceptance and Windows CI failed, despite Release Hardening and Durability Convergence succeeding.
2. Current deterministic E2E blocker is project-lock mutex ownership/cleanup in the acceptance harness; it leaks the singleton lock into subsequent tests. Fix test isolation, not runtime singleton semantics.
3. PR #38 reports logical-conversation identity-format correlation failure before Enter; production owner must resolve/verify it.
4. UI #32 must be replayed/rebased last and the complete matrix rerun.
5. No final Setup/install lifecycle PASS exists for consolidated E2E + UI on one immutable SHA.
6. PR #34 and #35 remain open but are superseded; do not merge them.

## Final convergence recommendation

**Runtime `7b5d8f...` -> consolidated Browser+Recovery+E2E `6bc3b852...` -> replayed UI `991d7c1...`.**

If any live head moves, refetch ancestry and patches before integration. Preserve one runtime root, one final-enter authority, one stable dispatch/reconciliation path, one recovery/rollover authority, and one completion authority.

**Preflight result:** composition mechanics are CLEAN with 0 actual text conflicts and one TRIVIAL accepted test overlap rehearsed cleanly. Historical PR #34/#35 conflicts are fully fenced. Final test and Setup plans are ready. **READY_FOR_FINAL_CONVERGENCE = NO** until current E2E/Windows failures and the reported correlation blocker are resolved, UI is replayed, and all source/package/install gates pass on one exact SHA.
