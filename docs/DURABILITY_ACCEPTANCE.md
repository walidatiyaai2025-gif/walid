# PCC Executive durability acceptance policy

Wave 3 extends the canonical SQLite/Recovery implementation; it does not introduce a second orchestration model. `CrashConsistentOrchestrationStore` implements the canonical `IOrchestrationStateStore` and persists one authoritative orchestration snapshot plus its idempotency/revision record in the same SQLite transaction.

## SQLite policy

Critical durability connections use WAL, `synchronous=FULL`, foreign keys enabled, a 5 second busy timeout, bounded busy retry, shared cache, and serializable write transactions. In-process canonical writers are serialized by database path. Stale compare-and-swap writes can be rejected by expected orchestration revision rather than overwriting newer state.

## Crash consistency

Fault injection points are `BEFORE_BEGIN`, `AFTER_BEGIN`, `AFTER_FIRST_WRITE`, `BEFORE_COMMIT`, `AFTER_COMMIT`, `BEFORE_ACK_SAVE`, and `AFTER_ACK_SAVE`. Before commit, failures roll back the complete canonical mutation. After commit, recovery observes the committed state even if the caller died before receiving success.

The Browser dispatch ledger remains the submission fence. A persisted Browser `Submitting`/`SubmittedUnknown` state recovers Domain state as `SUBMITTED_UNKNOWN`; it is never reset to `PREPARED`. A proven `Submitted` state remains submitted, and retry remains a Browser reconciliation decision.

## Integrity and backups

Startup/maintenance integrity uses SQLite `integrity_check` plus `foreign_key_check` and classifies `INTEGRITY_OK`, `INTEGRITY_WARNING`, or `INTEGRITY_FAILED`. Corruption recovery preserves the source database before restore and accepts only a hash-verified, readable, integrity-clean, schema-consistent backup.

Backup manifests include BackupId, SourceDatabaseId, SchemaVersion, ApplicationVersion, optional SourceSha, CreatedAt, Reason, FileHash, IntegrityStatus and file path. Pre-migration/update coordinators require a verified online backup before a schema transition is considered safe.

## Migrations

Schema target 2 adds only durability acceptance metadata: operation/idempotency journal, backup manifests, recovery journal, active-conversation pointer support, and orchestration revisions. Migration journal states are `PENDING`, `RUNNING`, `APPLIED`, `FAILED`, `ROLLED_BACK`, and `RECOVERY_REQUIRED`. Schema compatibility is explicit: `CURRENT`, `UPGRADE_REQUIRED`, `NEWER_THAN_APP`, `UNSUPPORTED`, `CORRUPTED`. A newer database is never silently downgraded.

## Conversation and restart invariants

Rollover continues to use the Worker 3 lifecycle ports wired in Wave 2. Predecessor remains canonical during candidate/continuation work; successful active switch atomically archives predecessor, activates successor, and changes `LogicalAgentSession.CurrentConversationId`. Recovery can assert exactly one active Domain conversation per logical agent.

Full restart reconstruction combines the canonical orchestration snapshot with durable logical sessions and conversations. Browser inventory reconciliation reports matched, missing, orphaned-owned, mismatch, or unknown; unknown personal runtimes are `DO_NOT_ADOPT`.

## Retention, privacy, maintenance

Checkpoint recompaction creates a new compact authoritative checkpoint instead of recursively embedding older payloads. Retention only removes old recovery checkpoints not present in an explicit protected set. Maintenance exposes WAL checkpoint and SQLite optimize hooks and refuses expensive maintenance while a critical dispatch transaction is active.

Operational persistence rejects obvious password, authorization, cookie, bearer/API-key or ChatGPT-session material. Full conversation transcripts are not canonical recovery memory.
