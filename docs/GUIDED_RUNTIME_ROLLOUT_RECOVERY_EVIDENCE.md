# Guided Runtime — Recovered Codex Rollout Evidence

This file preserves source-control and acceptance evidence recovered from the interrupted Codex rollout session so terminal closure does not repeat repository discovery or lose already-proven work.

## Recovery source

- Rollout session timestamp window: 2026-08-28T23:46Z through 2026-08-29T00:03Z.
- Session stopped because the Codex primary usage window reached 100%, not because of a repository crash.
- The GR-005 worktree was clean and tracking its remote at the last recorded `git status`.

## Integrated source-control state

The terminal convergence branch now contains both parents:

- prior convergence head: `48fa140012691dd52b49931c0ad37a1f424844bc`
- GR-005 final pushed head: `12f994954e97d59e0b46c9c0d21bcf7ba173a9a5`

They were integrated through merge commit:

- `a2fe66caa80b80e0b8713a88707778a58b460479`

GR-005 preserved checkpoints observed in the rollout:

- `017cd9cb4b27f808c27e2fc3158fb37057ea499e` — 55-case guided-runtime acceptance matrix
- `e99c0f3619c6a4599ee868d7586666578556f07a` — same-version repair/data-preservation acceptance wiring
- `12f994954e97d59e0b46c9c0d21bcf7ba173a9a5` — production E2E diagnostics-composition adaptation

GR-002 final evidence reported by its subagent:

- final head: `bdf8fdc50f64c152ef29246c2ab4dbed782811a8`
- early checkpoint: `9ad7d286eeb084b951cce0ee2b308f24ac41352d`
- reported validation: Infrastructure 100 passed, App 41 passed, Release app build succeeded with 0 warnings/errors, worktree clean, final branch pushed.

## Acceptance evidence already observed

The following results were captured from the composed GR-005 worktree during the rollout. These results are useful historical evidence, but terminal closure must re-run required gates on the final exact head before claiming release completion.

### Guided/runtime release harness

- Guided-runtime acceptance matrix: `VALID` — 55 cases.
- Release hardening: `RELEASE_HARDENING_TESTS_PASS`.
- Release infrastructure: `RELEASE_INFRASTRUCTURE_VALID version=0.1.0`.
- Release payload validation completed successfully.
- Release manifest validation completed successfully.

### Test families observed green

- PCCExecutive.App.Tests: 50 passed / 0 failed.
- PCCExecutive.Application.Tests: 119 passed / 0 failed.
- PCCExecutive.Browser.Acceptance: 31 passed / 0 failed.
- PCCExecutive.Browser.Tests: 54 passed / 0 failed.
- PCCExecutive.Domain.Tests: 11 passed / 0 failed.
- PCCExecutive.Infrastructure.Tests: 101 passed / 0 failed on the later composed run.
- PCCExecutive.Integration: 9 passed / 0 failed on the later composed run.

### GR-002-specific historical validation

The GR-002 subagent separately reported:

- Infrastructure: 100 passed.
- App: 41 passed.
- Release App build: succeeded with 0 warnings/errors.

Counts differ from later composed runs because additional tests landed during convergence; use the later exact-head run for final release truth.

## E2E history and remaining proof

A composed E2E run initially produced integration failures after Runtime Diagnostics / Autonomous Router composition. GR-005 then adapted the E2E harness to provide the required `RuntimeDiagnosticCollector` and updated the LOGIN_REQUIRED assertion to the semantic `sign-in` user language.

Those changes were committed and pushed at:

- `12f994954e97d59e0b46c9c0d21bcf7ba173a9a5`

Important: the rollout does **not** contain a recorded fully-green E2E rerun after that final GR-005 commit and the later GR-004 convergence fix. Therefore terminal closure must run the E2E suite on the current exact convergence head. Do not infer an E2E pass from earlier runs.

## Environment failure already diagnosed

A packaging/build attempt failed because the system drive reached `0` bytes free while Playwright output was being copied. This was classified as disk exhaustion, not a product compile defect.

Generated `bin`/`obj` output across completed worktrees was identified as the major reclaimable space consumer. Source work was already committed/pushed before cleanup. Terminal closure should monitor free disk before publish/package operations and may remove generated `bin`, `obj`, `TestResults`, and temporary artifact directories after confirming they contain no unique source.

## Installer tooling observed

The rollout detected Inno Setup 6 at:

`C:\Program Files (x86)\Inno Setup 6\ISCC.exe`

This is environment evidence only; terminal closure must rediscover the tool path when running in a different machine/session.

## Last recorded GR-005 worktree state

At the final recorded status check before packaging continuation:

- branch: `worker/gr005-guided-runtime-acceptance`
- remote tracking: `origin/worker/gr005-guided-runtime-acceptance`
- head: `12f994954e97d59e0b46c9c0d21bcf7ba173a9a5`
- no modified/untracked source lines were printed by `git status --short --branch`.

This supports that no known GR-005 source remained only local at that checkpoint.

## Terminal closure — remaining required gates

Do not repeat GR-001 through GR-004 implementation unless a failing exact-head gate proves a regression. Start from current `codex/pcc-guided-runtime-terminal-convergence` and close only the remaining proof/integration gaps:

1. Fetch and verify exact current convergence HEAD.
2. Confirm working tree is clean and no newer worker commit is missing.
3. Build Release exact head.
4. Re-run all required test families, especially PCCExecutive.E2E after the final GR-004 + GR-005 composition.
5. Re-run Guided Runtime 55-case matrix, Release Hardening, and Release Infrastructure gates.
6. Build the real Windows publish/package and Setup EXE.
7. Verify package provenance/hash against exact final HEAD.
8. Run fresh-install acceptance.
9. Run same-version repair/data-preservation acceptance.
10. Run upgrade acceptance where supported.
11. Run installed-app guided-flow, Runtime Inspector, stale-DevTools recovery, profile reuse, and personal-Chrome-safety acceptance.
12. Trigger/verify final Windows CI on the exact final candidate.
13. Create the single owner-facing terminal PR only after the above evidence is authoritative.

## Do not claim from recovered evidence alone

The recovered rollout does not establish final-pass evidence for:

- exact-head E2E after all last convergence commits;
- final installer artifact/hash;
- fresh installed-app acceptance on the final candidate;
- same-version repair on the final candidate;
- upgrade acceptance on the final candidate;
- final exact-head Windows CI;
- final owner-facing terminal PR.

These are the remaining release-closure responsibilities.