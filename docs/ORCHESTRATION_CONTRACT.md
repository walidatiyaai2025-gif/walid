# PCC Executive Manager Orchestration Contract

This document records the Wave 2 Application-layer orchestration seam for `PCCEXECUTIVE-T0001`.

## Structured Manager plan

Manager output is dispatchable only after it parses into `StructuredManagerPlan`. A plan may contain zero through five Worker tasks. Free-form prose is non-dispatchable. Every task carries stable task identity, objective, routed repository/scope, dependency and acceptance evidence, priority, optional slot guidance, expected evidence and rationale.

## Validation before dispatch

`ManagerWaveValidator` layers live PCC/GitHub assumptions over the canonical Foundation `WaveValidator`. Blocking findings include worker-count overflow, duplicate task identity/fingerprint, completed-work repetition, missing/cyclic dependencies, wrong repository/scope/variant, stale or unrecognized HEAD, invalid PR/branch assumptions, and Closure Mode feature expansion. Scope/exclusive-resource collisions are not dispatched in parallel and are marked for sequentialization.

## Scheduling

`SafeDispatchPlanner` and `DependencyAwareWaveScheduler` treat five Worker slots as a ceiling. Dependencies gate readiness, occupied/active slots are preserved, unrelated ready tasks may continue while another Worker is active, and global runtime pause suppresses all new sends. Automatic staged pacing defaults to ten seconds and consumes runtime health guidance without placing browser timing in Domain.

## Dispatch safety

`DispatchCoordinator` preserves stable `DispatchId` and content fingerprint reservations. `SUBMITTED_UNKNOWN` is reconciled through `IDispatchReconciliationService` before any retry; an uncertain dispatch is never blindly duplicated. Browser/session correctness is supplied through `IAgentSessionGuard` and `IAgentProvider` interfaces.

## Worker handoff and live evidence

`WorkerHandoffParser` requires the standard Task/Worker/Project/Repository/Status/HEAD/Branch/PR/Changed/Tests/Build/Blocker/Next Action fields and retains specialist extensions. `WorkerHandoffQualityGate` classifies `VALID`, `PARTIAL`, `INVALID`, `STALE`, and `CONTRADICTED_BY_LIVE_EVIDENCE` against live project/PR/head/CI evidence.

`LiveWaveEvidenceReconciler` obtains a fresh baseline through the PCC/GitHub evidence layer, compares it with the persisted baseline, and then validates Worker handoffs against the live routing and exact-head state. Contradictory evidence is surfaced rather than copied into canonical state.

## Manager review and sanity

`ManagerReviewPacketBuilder` produces one consolidated structured packet containing task results, validated handoffs, live head/PR/CI state, evidence, blockers, completion inputs, loop signals, attention items and a recommended next decision. Raw Worker transcripts are not the authority.

`ManagerSanityChecker` rejects unsupported completion claims, Closure Mode expansion, repeated completed work/reassignment and ignored repeated blockers. `LoopDecisionEngine` maps loop/stagnation signals to continue/replan/sequentialize/reassign-once/closure-repair/stalled-auto-stopped/attention-required outcomes.

## Completion and recovery

Manager Estimate remains separate from Verified Completion. The existing evidence-backed `CompletionEngine` controls Closure Mode and verified completion. Mutable orchestration state is represented by `OrchestrationRecoverySnapshot` and persisted through `IOrchestrationStateStore`; SQLite implementation remains Worker 2 ownership.

No Playwright, SQLite implementation, WPF binding, installer/updater or release mutation is owned by this slice.
