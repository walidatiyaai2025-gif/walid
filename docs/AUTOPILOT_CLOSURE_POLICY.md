# PCC Executive — Autopilot, Attention and Closure Policy

This document records the Wave 3 Application-layer decision policy for `PCCEXECUTIVE-T0001`. It extends the Manager orchestration seam from PR #14 and does not replace Browser, persistence, WPF, installer or release implementations.

## Autopilot

The auditable project-control states are `OFF`, `PAUSED`, `AUTOMATIC_STAGED`, `RECOVERING`, `WAITING_FOR_DEPENDENCY`, `WAITING_FOR_EVIDENCE`, `ATTENTION_REQUIRED`, `CLOSURE_MODE`, `STALLED_AUTO_STOPPED`, `VERIFIED_COMPLETE`, and `BLOCKED_EXTERNAL`.

Routine temporary errors, slow sessions, recoverable network loss, cooldowns, Worker completion/slot reuse, dependency release, evidence refresh, conversation rollover and restart recovery stay autonomous. Unsafe sends are suppressed until state/evidence is reconciled.

## Attention intelligence

Only genuine human gates create Attention: login, CAPTCHA/account challenge, missing credential, destructive approval, external authority, real business decision, unresolved security decision or an external blocker. Network loss and adapter uncertainty remain automatic unless policy establishes that they are prolonged/nonrecoverable.

Attention identity is fingerprinted from project run, category and applicable resource/task/logical-session/blocker context. Repeated observations update the active item instead of creating notification spam. The persistence boundary is `IAttentionLifecycleStore`; storage remains Worker 2 ownership. A login Attention may auto-resolve when provider health becomes authenticated-ready.

## Evidence quality

`EvidenceQualityEvaluator` classifies evidence as `STRONG`, `ACCEPTABLE`, `WEAK`, `STALE`, `CONTRADICTED`, or `MISSING`. Exact source SHA, freshness, CI/tests/runtime/artifact verification, branch/PR identity, confidence and contradictions are evaluated before evidence can satisfy a completion gate. A textual Worker or Manager claim is not itself completion evidence.

## Completion and Closure Mode

Completion families are implementation, runtime, tests, CI, UI, persistence, browser, orchestration, recovery, security, installer, update, E2E, packaging and release. `ManagerEstimate` remains separate from `VerifiedCompletion`.

At verified completion >=99%, the policy enters Closure Mode rather than declaring DONE. Closure Mode admits only closure work such as failed-test repair, integration/security/visual/package/installer defects, release evidence and critical acceptance bugs. New features, style-only refactors, optional enhancements and unrelated scope expansion are rejected.

100% VERIFIED requires every configured mandatory family/gate to be present and evidence-satisfied, no unresolved P0/P1 blocker, and no required verification left not executed. Missing required E2E or release/package evidence prevents 100%; 99.x is never rounded to 100.

Remaining closure work is ordered P0 verification blocker, P1 release blocker, then P2 polish.

## Stagnation and reassignment

The stagnation engine compares a configurable observation window for unchanged source HEAD, repeated task/blocker/failed-test/PR/evidence/Manager recommendation and negligible `VerifiedCompletionDelta`. Manager estimate movement alone is not progress. A meaningful verified delta resets stagnation.

Automatic reassignment is bounded: the same task receives at most one automatic reassignment and only when new strategy or evidence exists. Identical Worker bouncing is rejected. Repeated no-progress signals lead through refresh/replan/strategy change toward `STALLED_AUTO_STOPPED`; they do not generate endless ChatGPT traffic.

## Blockers and recovery

Internal-fixable blockers route to executable Worker work. Dependency blockers wait. CI-infrastructure blockers route to bounded CI repair. Auth/product-decision gates create Attention. External service/authority blockers may terminate autonomous execution as `BLOCKED_EXTERNAL`. Unverified blockers refresh evidence first.

Recovery is fail-safe: temporary error -> bounded retry; offline -> wait/retry; rate limit -> global pause/cooldown; login/challenge -> Attention; `SUBMITTED_UNKNOWN` -> reconcile first; browser-adapter uncertainty -> no send; persistent uncertainty -> Attention. Conversation rollover and application restart use the existing recovery/persistence seams.

## Notifications, destructive actions and terminal outcomes

Routine retries and Worker completions do not notify. Meaningful notifications are limited to verified-completion milestones, Attention Required, Stalled Auto-Stopped, External Blocker, installer/update candidate ready and 100% Verified.

Destructive or irreversible actions require explicit approval. Terminal records are evidence-bearing and support `VERIFIED_100`, `BLOCKED_EXTERNAL`, `STALLED_AUTO_STOPPED`, and `STOPPED_BY_OPERATOR`.

After reconciliation, deterministic continuation policy chooses one of: `START_NEXT_WAVE`, `WAIT_FOR_RUNNING_WORK`, `WAIT_FOR_DEPENDENCY`, `REFRESH_EVIDENCE`, `ENTER_CLOSURE_MODE`, `AUTO_STOP_STALLED`, `BLOCKED_EXTERNAL`, or `VERIFIED_COMPLETE`. The Manager is not consulted again when policy can safely decide the next step.

## Persistence seams

`IAttentionLifecycleStore` and `IDecisionJournal` are application contracts only. Worker 2 may persist Attention lifecycle and decision journal records in SQLite. Wave 3 does not implement storage.
