# PCC Executive durable recovery integration

This Wave-2 slice bridges the canonical `IOrchestrationStateStore` / `OrchestrationRecoverySnapshot` contracts to the existing SQLite durable store. It does not introduce a second orchestration domain model.

## Dispatch durability

The Browser ledger is treated as the submission fence. If a restart sees Domain `PREPARED` while the Browser ledger reached `Submitting` or `SubmittedUnknown`, recovery promotes the canonical dispatch to `SUBMITTED_UNKNOWN`. It never rewinds uncertainty to `PREPARED`. Browser reconciliation remains the only authority that may prove `SAFE_RETRY`.

## Checkpoints

Recovery checkpoints contain compact authoritative state only: project/run, logical agent, Worker slot/task/wave/conversation/dispatch, branch/HEAD/PR, status, completed work, blockers, decisions, next action, schema/application version, reason and a SHA-256 payload digest. They do not embed the historic chat transcript.

## Rollover

Worker-3 lifecycle ports are persisted against the canonical Domain conversations. Candidate creation keeps the predecessor active. Successful commit writes predecessor archive, successor activation, and `LogicalAgentSession.CurrentConversationId` in one SQLite transaction. Failure restores/preserves the predecessor.

## Restart / shutdown / update

Startup distinguishes a previous clean shutdown marker from an interrupted run and reconstructs the current orchestration snapshot. Safe shutdown pauses new sends, persists the orchestration snapshot/checkpoint, checkpoints WAL, and writes a clean marker. Pre-update preparation pauses sends, persists state, creates a SQLite online backup, runs `PRAGMA integrity_check`, records its SHA-256/schema version, and only then reports the checkpoint as safe to update.

Unknown Browser runtimes are never automatically adopted; only PCC-created or explicitly adopted runtime identities may reconcile as matched.
