# PCC Executive — Runtime Architecture

## 1. High-level topology

```text
Windows Desktop / WPF
        |
        +-- Desktop Shell / Tray / Attention Center
        |
        +-- Orchestration Engine
        |      +-- Manager Coordinator
        |      +-- Wave Planner Validator
        |      +-- Worker Slot Manager (max 5)
        |      +-- Collision / Dependency Guard
        |      +-- Handoff Quality Gate
        |      +-- Loop Guard / Closure Mode
        |
        +-- Agent Provider Layer
        |      +-- BrowserChatProvider [DEFAULT]
        |      |      +-- Browser Session Controller
        |      |      +-- ChatGPT Browser Adapter
        |      |      +-- Dispatch Scheduler
        |      |      +-- Resilience Controller
        |      |      +-- Conversation Lifecycle Manager
        |      +-- OpenAIApiProvider [OPTIONAL]
        |
        +-- Project Control / Evidence Layer
        |      +-- PCC Adapter
        |      +-- GitHub Adapter
        |      +-- Completion Engine
        |      +-- Manager Sanity Checker
        |
        +-- Durable State
               +-- SQLite
               +-- Checkpoints
               +-- Recovery Timeline
               +-- Update/Migration State
```

## 2. Solution/module proposal

```text
src/
  PCCExecutive.App/                 WPF shell, navigation, tray
  PCCExecutive.Domain/              state machines and domain contracts
  PCCExecutive.Application/         use cases/orchestration services
  PCCExecutive.Infrastructure/      SQLite, filesystem, process/network
  PCCExecutive.Browser/             Playwright + ChatGPT adapter
  PCCExecutive.Pcc/                 PCC routing/state integration
  PCCExecutive.GitHub/              GitHub evidence integration
  PCCExecutive.Updater/             update/migration orchestration
  PCCExecutive.Installer.Bootstrap/ installer bootstrap integration as needed

tests/
  PCCExecutive.Domain.Tests/
  PCCExecutive.Application.Tests/
  PCCExecutive.Browser.Tests/
  PCCExecutive.Integration.Tests/
  PCCExecutive.E2E.Tests/

assets/
  ui/

docs/
```

## 3. Domain identities

Use stable IDs independent of UI windows:

- `ProjectId`
- `ProjectRunId`
- `WaveId`
- `TaskId`
- `WorkerSlotId`
- `LogicalAgentId`
- `ConversationId`
- `DispatchId`
- `CheckpointId`
- `EvidenceId`
- `AttentionRequestId`

A Chrome PID, tab ID, Playwright page ID or ChatGPT URL is runtime evidence, not the logical role identity.

## 4. Browser ownership model

PCC Executive must maintain an explicit ownership registry. A browser process/context is killable only when ownership evidence exists.

Suggested ownership record:

```text
BrowserRuntime
- RuntimeId
- ProjectRunId
- LogicalAgentId
- ProcessId
- ProcessStartIdentity
- ContextIdentity
- ProfilePath
- CreatedByPcc
- AdoptedExplicitly
- LastHeartbeatAt
- Visibility
- State
```

Never identify a process as owned only because its executable is `chrome.exe`.

## 5. Agent session model

```text
LogicalAgentSession
- LogicalAgentId
- Type: Manager | Worker
- WorkerSlotId?
- ProjectRunId
- Role
- CurrentTaskId?
- CurrentConversationId
- State

Conversation
- ConversationId
- LogicalAgentId
- Sequence
- Provider
- Url/ProviderIdentity
- CreatedAt
- RetiredAt?
- PredecessorId?
- SuccessorId?
- HealthScore
- EstimatedGrowth
- CheckpointId
- RolloverReason?
```

The UI shows `Manager` / `Worker 1`, not raw conversation sequence unless the user opens history.

## 6. Dispatch idempotency

Every outbound instruction gets a stable `DispatchId` and content fingerprint.

Required fields:

```text
Dispatch
- DispatchId
- ProjectRunId
- WaveId
- TaskId
- LogicalAgentId
- ConversationId
- ContentHash
- PreparedAt
- State
- SubmittedAt?
- AcknowledgedAt?
- CompletedAt?
- RetryOfDispatchId?
- ReconciliationEvidence
```

If submit outcome is unknown, state becomes `SUBMITTED_UNKNOWN`; the adapter reconciles the conversation before any safe retry.

## 7. Browser adapter confidence

Every critical UI detection returns both semantic state and confidence/evidence.

Examples:

```text
InputState = READY | DISABLED | UNKNOWN
GenerationState = IDLE | GENERATING | COMPLETE | UNKNOWN
AuthState = AUTHENTICATED | LOGIN_REQUIRED | CHALLENGE | UNKNOWN
ConversationMatch = MATCH | MISMATCH | UNKNOWN
```

`UNKNOWN` is a stop condition for sending. It is not treated as READY.

Selectors should be layered by semantic evidence rather than one brittle CSS selector. Adapter versions are tracked, and unknown UI drift is surfaced as `BROWSER_ADAPTER_UNCERTAIN`.

## 8. Resilience policy

### Global conditions

Examples:

- sending too quickly / temporary global limit;
- account/session auth issue;
- offline network.

Global conditions pause new sends across Manager and Workers while preserving state.

### Per-session conditions

Examples:

- one slow generation;
- one partial response;
- one broken conversation tab.

Other independent sessions may continue when safe.

### Backoff

The application uses conservative adaptive waiting and server/UI guidance when available. It does not create retry traffic whose purpose is to circumvent service protections.

## 9. Conversation rollover

Rollover is transactional:

```text
OLD ACTIVE
 -> CHECKPOINT CREATED
 -> NEW CONVERSATION CREATED
 -> CONTINUATION PACKET SENT
 -> CONTINUATION VALIDATED
 -> NEW ACTIVE
 -> OLD ARCHIVED
```

If creation/continuation fails, old conversation/checkpoint remain authoritative and the task is not lost.

Continuation packet includes:

- project/repository/scope;
- logical agent role;
- current task and acceptance criteria;
- latest known branch/PR/HEAD evidence;
- completed work summary from canonical state;
- unresolved blockers;
- explicit next action;
- instruction to fetch/reconcile live state before claiming completion.

## 10. Manager planning contract

Prefer structured response parsing. A plan contains:

```text
WavePlan
- WaveId
- ManagerEstimate
- Tasks[] (0..5)
  - TaskId / temporary proposed ID
  - Role
  - Objective
  - Scope
  - Dependencies
  - Exclusions
  - AcceptanceCriteria
  - Priority
- ProjectDecision
- Blockers
```

Before dispatch, application guards validate overlap, duplicate fingerprints, dependencies and routing.

## 11. Worker handoff contract

Required semantic fields:

```text
TASK
STATUS
HEAD
CHANGED
VALIDATION
BLOCKER
NEXT_ACTION
```

The parser may tolerate presentation differences but never converts vague prose such as `done` into verified completion without required evidence.

## 12. Completion model

A weighted gate engine should be configurable per project. Store gate states explicitly:

- `NOT_APPLICABLE`
- `UNKNOWN`
- `PENDING`
- `PARTIAL`
- `PASS`
- `FAIL`
- `BLOCKED_EXTERNAL`

Unknown/failed mandatory gates cap verified completion. `100%` is only possible when all mandatory gates are PASS or NOT_APPLICABLE and terminal blockers are cleared.

## 13. Loop/stagnation scoring

Record each reconciliation snapshot:

```text
WaveFingerprint
- ExactHead(s)
- OpenTaskFingerprints
- BlockerFingerprints
- FailedTestFingerprints
- EvidenceDelta
- VerifiedCompletionDelta
```

The Loop Guard compares rolling windows and produces:

- `NORMAL`
- `WATCH`
- `STAGNATING`
- `LOOP_DETECTED`
- `AUTO_STOPPED`

## 14. Persistence

SQLite uses WAL mode where appropriate and versioned migrations.

Important transactional boundaries:

- dispatch state transition + checkpoint;
- response import + handoff parse;
- manager decision + new wave creation;
- conversation rollover;
- update migration.

No in-memory-only state may be required to recover an active project run.

## 15. Singleton and locking

Use an OS-level application/project lock plus durable lease metadata.

Second app instance may:

- focus existing instance;
- open read-only;
- perform explicit safe takeover only after proving old lease is stale.

Never run two autonomous controllers against the same ProjectRun by accident.

## 16. Resource governor

Suggested policies:

- keep only current Manager + active Worker conversations live;
- archive/close retired conversations after checkpoint;
- configurable max browser processes;
- memory/CPU pressure states;
- suspend new Worker launch under high pressure;
- never kill a generating session solely to optimize memory without an explicit recovery strategy.

## 17. Safe shutdown

Closing main window defaults to tray while active work exists.

Exit options:

- Minimize to tray;
- Pause and exit after checkpoint;
- Kill PCC sessions and exit.

Unexpected termination is recovered from the durable state store.

## 18. Update architecture

Separate updater/bootstrap process is preferred so application binaries can be replaced safely.

Update transaction:

1. download/verify package;
2. checkpoint active run;
3. create data backup/rollback marker;
4. stop application-owned runtime safely;
5. replace binaries;
6. run migrations;
7. verify application startup/schema;
8. mark update successful;
9. rollback/report if verification fails.

## 19. Observability

Human-facing timeline should use semantic events, not raw technical noise:

`Worker 3 dispatch sent -> response slow -> recovered -> handoff accepted`.

Raw logs may retain:

- adapter diagnostics;
- selectors/version;
- process events;
- state transitions;
- exception traces;
- migration/update diagnostics.

Sensitive authentication/session material must not be dumped into logs.

## 20. Security baseline

- protect local secrets using Windows-supported secure storage where possible;
- API key, if configured, is never committed or logged;
- never expose ChatGPT cookies/session tokens in UI logs;
- validate imported/exported state;
- sanitize paths and process control targets;
- destructive actions require policy gate;
- update package verification required before replacement.
