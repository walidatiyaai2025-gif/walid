# PCC Executive — v1 Master Plan

STATUS: INITIAL PRODUCT PLAN
CANONICAL ISSUE: #1
PROJECT_ID: PCCEXECUTIVE
TARGET: Windows Desktop
PRIMARY AI RUNTIME: ChatGPT Web / Chrome
OPTIONAL AI RUNTIME: OpenAI API
REFERENCE UI: `assets/ui/`

## 1. Product mission

PCC Executive is an executive project-delivery desktop application that turns one ChatGPT Manager plus up to five ChatGPT Worker conversations into a persistent, evidence-aware project execution system.

The owner selects a registered project. PCC Executive resolves the project through Project Control Center, opens/recovers the Manager, obtains the next executable wave, dispatches non-overlapping tasks to Worker slots, monitors/reconciles their responses and GitHub/PCC evidence, then returns a consolidated result to the Manager for the next wave until the project reaches verified closure, an explicit blocker or a no-progress stop condition.

The application is intentionally Browser-first so normal operation can use the user's ChatGPT product session without requiring an API key. API support exists as a secondary provider only.

## 2. Product principles

1. **ChatGPT/Chrome first.** Browser provider is default and fully usable without API configuration.
2. **Zero-touch normal operation.** Routine waiting, limits, retries, session recovery and conversation rollover are automatic.
3. **Human attention is scarce.** Interrupt the owner only for unavoidable sign-in/challenge, missing authority/credential, irreversible approval or a real product decision.
4. **Logical roles survive conversations.** Manager/Worker identity persists across browser/session/conversation rollover.
5. **Evidence beats optimistic text.** Worker/Manager claims do not directly set verified completion.
6. **Five Workers is a maximum.** Never invent parallelism where dependencies or overlap make it unsafe.
7. **No blind retry.** Uncertain sends are reconciled before resubmission.
8. **No infinite loops.** Repeated no-progress/error patterns auto-stop/escalate.
9. **Crash-safe.** Project/wave/task/session state survives application/Windows/browser/network failures.
10. **Installer is part of v1.** Delivery is a Setup EXE, not a source folder.

## 3. End-to-end user flow

### First install

`Setup EXE -> Next -> Next -> Install -> Launch`

First launch:

1. detect installed Chrome / prepare PCC-owned browser profile/runtime;
2. open ChatGPT sign-in only if required;
3. connect to Project Control Center / GitHub source configuration;
4. show registered projects;
5. smart defaults are already enabled.

### Start project

1. Owner chooses project, e.g. `AIMWWeb`.
2. PCC Executive resolves canonical repo/scope/routing from live PCC.
3. Project singleton lock is acquired.
4. Manager logical session is created/recovered.
5. Manager receives a structured project-control prompt and live evidence packet.
6. Manager returns next wave (0..5 worker tasks) with dependencies/scopes.
7. Sanity/collision guards validate the proposed wave.
8. Worker tasks enter dispatch queue.
9. Browser dispatch occurs manually, assisted or automatically staged according to settings; v1 default is Automatic Staged with adaptive pacing enabled.
10. Workers run in PCC-owned hidden Chrome sessions.
11. Responses are collected, validated and reconciled.
12. Wave summary is sent to Manager.
13. Manager decides continue / repair / QA / integrate / close.
14. Repeat until closure/block/stall.

## 4. Primary runtime components

### 4.1 Desktop Shell

Responsibilities:

- navigation and premium UI;
- project selector;
- dashboard;
- manager/worker inspection;
- session controls;
- settings;
- Attention Center;
- Windows tray integration;
- safe shutdown.

### 4.2 Project Control Adapter

Reads live PCC routing/governance and resolves:

- project identity;
- repository;
- scope / variant where applicable;
- canonical task/issue context;
- required policies;
- evidence requirements.

PCC remains governance authority; PCC Executive is the desktop execution runtime.

### 4.3 Agent Runtime Abstraction

Interface concept:

- `IAgentProvider`
- `BrowserChatProvider` — primary/default.
- `OpenAIApiProvider` — optional.

Manager/Worker orchestration must not depend directly on browser DOM classes outside the Browser adapter boundary.

### 4.4 Browser Session Controller

Owns only PCC browser contexts/processes.

Capabilities:

- create/recover Manager and Worker sessions;
- persistent browser profile strategy;
- stable session/process identity;
- open/foreground/hide/minimize;
- restart individual session;
- kill individual session;
- Kill All PCC Sessions;
- orphan recovery;
- personal-Chrome exclusion;
- browser health telemetry.

### 4.5 ChatGPT Browser Adapter

Responsibilities:

- prove correct conversation before send;
- detect input readiness;
- submit prepared task where allowed by selected mode;
- detect generation state/completion;
- capture response state;
- classify ChatGPT UI errors/warnings;
- detect authentication/challenge state;
- detect conversation rollover signals;
- fail safe on unknown UI/selector state.

No component should attempt to defeat service protections or hide automation to bypass limits.

### 4.6 Dispatch Scheduler

Modes:

- Manual;
- Assisted;
- Automatic Staged.

Configuration:

- base interval (default 10 seconds);
- max active Workers (default 5);
- adaptive pacing;
- global limit pause;
- automatic safe resume;
- skip completed workers.

It maintains dispatch idempotency keys and per-send state.

### 4.7 ChatGPT Resilience Controller

Classifies and manages:

- `READY`
- `SENDING`
- `GENERATING`
- `SLOW`
- `THROTTLED`
- `RATE_LIMITED`
- `TEMP_ERROR`
- `PARTIAL_RESPONSE`
- `SESSION_EXPIRED`
- `LOGIN_REQUIRED`
- `OFFLINE`
- `STUCK`
- `RECOVERING`
- `PAUSED`
- `FAILED`
- `DONE`

Recovery rules:

- distinguish global vs per-session fault;
- preserve already-running generations where possible;
- pause new sends on global limit;
- adaptive cooldown/backoff;
- no blind duplicate send;
- recover existing conversation before creating replacement;
- surface user action only if recovery requires it.

### 4.8 Conversation Lifecycle Manager

A logical Manager/Worker may span many ChatGPT conversations.

Per conversation persist:

- logical role/session ID;
- conversation ID/URL;
- sequence number;
- created/retired time;
- health score/estimated growth;
- checkpoint ID;
- rollover reason;
- predecessor/successor lineage.

Preventive rollover:

`Fresh -> Growing -> Rollover Soon -> Rotate`

Because ChatGPT Web does not expose a guaranteed authoritative remaining-token counter, rollover health is heuristic and conservative.

Continuation packet contains only current authoritative state needed to continue, not the full historic transcript.

### 4.9 Orchestration Engine

Coordinates:

`PROJECT -> MANAGER_PLAN -> WAVE_VALIDATION -> DISPATCH -> WORKER_EXECUTION -> HANDOFF_VALIDATION -> RECONCILIATION -> MANAGER_DECISION -> NEXT_WAVE / CLOSURE`

Mandatory guards:

- dependency validation;
- Worker overlap/collision detection;
- duplicate task fingerprinting;
- wrong-chat prevention;
- Manager sanity checking;
- response quality gate;
- exact-head/evidence reconciliation.

### 4.10 Project Memory / State Store

SQLite is the v1 default.

Canonical memory is structured state, not accumulated prose summaries.

Core entities:

- Project;
- ProjectRun;
- Wave;
- Task;
- WorkerSlot;
- LogicalAgentSession;
- Conversation;
- Dispatch;
- Response;
- Handoff;
- ManagerDecision;
- EvidenceRecord;
- CompletionGate;
- Blocker;
- RecoveryEvent;
- AttentionRequest;
- Setting;
- AppVersion/Migration;
- UpdateAttempt.

### 4.11 Evidence & Completion Engine

Maintain separate values:

- Manager estimate;
- Verified completion.

Verified completion derives from applicable gates, not free-form percentages.

Suggested gate families:

- implementation;
- runtime connectivity;
- unit/integration tests;
- CI;
- QA;
- visual QA when required;
- security;
- integration;
- packaging;
- deployment/release when required;
- blockers/unknowns;
- exact-head provenance.

At 99% enter `CLOSURE_MODE`. Closure Mode forbids unrelated feature expansion and focuses on final QA/integration/artifact/blocker closure.

### 4.12 Loop Guard

Signals include:

- same HEAD/evidence for N waves;
- same blocker for N waves;
- same task fingerprint repeated;
- progress delta below threshold for N waves;
- same failed test/error repeated;
- same task reassigned without new evidence;
- manager/worker ping-pong.

Outcomes:

- continue with modified plan;
- reassign once;
- enter closure repair mode;
- `STALLED_AUTO_STOPPED`;
- Attention Center escalation when truly required.

### 4.13 Attention Center

Normal count should be zero.

Routine events are silent/automatic:

- Worker done;
- temporary limit recovered;
- slow response recovered;
- conversation rollover;
- transient retry;
- wave completion.

Notify/escalate for:

- sign-in required;
- CAPTCHA/account challenge;
- missing external credential or authority;
- irreversible/destructive approval;
- unresolved genuine blocker;
- project reaches 100% VERIFIED.

Every attention item must say:

1. what happened in simple language;
2. why automation cannot safely finish it;
3. one required action;
4. open the exact relevant place where possible.

### 4.14 Resource Governor

Monitor:

- Chrome process count;
- CPU;
- memory;
- stale/archived sessions;
- hidden browser lifetime.

Archived conversations do not remain as live Chrome processes unnecessarily.

### 4.15 Network / Power / Crash Recovery

On unexpected interruption:

- checkpoint mutable state frequently;
- mark uncertain dispatches explicitly;
- reacquire project lock;
- reconcile owned browser processes;
- reconcile live GitHub/PCC state;
- resume from last safe state.

Sleep/resume and network loss must not be misclassified as ChatGPT task failure.

## 5. State machines

### Project Run

`IDLE -> INITIALIZING -> MANAGER_PLANNING -> WAVE_READY -> DISPATCHING -> WAVE_RUNNING -> RECONCILING -> MANAGER_REVIEW -> ... -> CLOSURE_MODE -> VERIFIED_COMPLETE`

Terminal alternatives:

- `BLOCKED_EXTERNAL`
- `STALLED_AUTO_STOPPED`
- `STOPPED_BY_OPERATOR`

### Dispatch

`PREPARED -> SUBMITTING -> SUBMITTED -> ACKNOWLEDGED -> GENERATING -> RESPONSE_COMPLETE -> HANDOFF_VALIDATED`

Uncertain branch:

`SUBMITTING/SUBMITTED -> SUBMITTED_UNKNOWN -> RECONCILE -> ACKNOWLEDGED or SAFE_RETRY`

### Browser Session

`CREATING -> READY -> HIDDEN/VISIBLE -> ACTIVE -> DEGRADED -> RECOVERING -> READY`

Terminal/replacement:

`ARCHIVED / KILLED / FAILED_REQUIRES_ATTENTION`

## 6. Safety contracts

### Wrong-chat guard

Before every send prove:

- project;
- logical role;
- worker slot;
- current conversation identity;
- expected task/dispatch identity.

Unknown/mismatch => no send.

### Collision guard

Before wave dispatch compare:

- repository;
- branches;
- file/path scopes where available;
- declared components;
- dependencies;
- exclusive resources.

Unsafe overlap becomes sequential or returns to Manager for replan.

### Destructive action gate

Policy configurable but destructive operations are not silently inferred from autonomy. Production deletion, destructive migration, force push and equivalent actions require explicit allowed policy/approval and evidence.

## 7. UI / screen inventory

Authoritative v1 screens:

1. Setup Wizard
2. Upgrade Wizard
3. Chrome Connection
4. Project Selection
5. Executive Dashboard
6. Manager Workspace
7. Workers Dispatch
8. Worker Chat
9. Wave Summary
10. Task Board
11. Evidence & Verification
12. Loop Guard
13. ChatGPT Health & Recovery
14. Session Monitor
15. Settings
16. Update Center
17. Attention Center

Reference resolution: 1920x1080. See `docs/UI_AUTHORITY.md`.

## 8. Installer / update design

### Installer

Artifact example:

`PCCExecutive-Setup-x64-1.0.0.exe`

Required:

- standard GUI flow;
- install location;
- Start Menu shortcut;
- optional desktop shortcut;
- launch-after-install;
- prerequisites/runtime handling;
- application version identity.

### In-place upgrade

`Detect -> Checkpoint -> Stop/Pause owned runtime -> Backup -> Replace -> DB migrate -> Verify -> Restart`

If verification fails:

`Rollback binaries/schema where supported -> preserve backup -> report exact recovery state`.

Uninstall default keeps user/project data unless user explicitly selects full cleanup.

## 9. Development phases

### Phase 0 — Governance and foundation

- repository constitution;
- PCC registration/routing;
- Issue #1 canonical scope;
- UI assets committed;
- solution skeleton;
- versioning/CI baseline.

### Phase 1 — Desktop shell and persistence

- .NET/WPF shell;
- navigation/design system;
- SQLite migrations;
- project/settings storage;
- tray/safe shutdown;
- crash checkpoint framework.

### Phase 2 — Browser ownership and ChatGPT connection

- PCC Chrome runtime/profile;
- Manager + Worker session ownership;
- visibility controls;
- process ownership safety;
- Session Monitor;
- login-required flow.

### Phase 3 — Browser adapter and dispatch

- readiness/generation detection;
- structured prompts;
- staged dispatch;
- idempotency/unknown-send reconciliation;
- wrong-chat guard.

### Phase 4 — Resilience

- slow/error/limit classification;
- global cooldown;
- partial response handling;
- network recovery;
- browser restart/orphan recovery;
- health dashboard.

### Phase 5 — Conversation lifecycle

- growth heuristics;
- checkpoints;
- continuation packets;
- rollover/lineage/archive;
- Manager and Worker rotation tests.

### Phase 6 — Multi-worker orchestration

- Manager planning contract;
- 5 Worker slots;
- dependencies;
- collision guard;
- handoff quality validation;
- wave summary/reconciliation.

### Phase 7 — PCC/GitHub evidence and completion

- live routing/project state;
- evidence ingestion;
- Manager estimate vs Verified completion;
- Closure Mode;
- Loop Guard;
- canonical memory compaction.

### Phase 8 — Attention / autonomy

- smart escalation;
- minimal notifications;
- destructive action gates;
- Recovery/System Timeline;
- zero-touch acceptance scenarios.

### Phase 9 — Installer and update center

- Setup EXE;
- clean install;
- upgrade over previous version;
- data preservation;
- DB migration;
- rollback/recovery;
- uninstall keep/full cleanup choices.

### Phase 10 — Release closure

- end-to-end test against real controlled Browser sessions;
- crash/restart scenarios;
- temporary-limit scenarios;
- conversation rollover scenario;
- 5-worker wave scenario;
- installer/update scenario;
- exact-source release artifact;
- final QA and evidence ledger.

## 10. Test strategy

### Unit

- state transitions;
- scheduling;
- idempotency;
- task fingerprinting;
- collision rules;
- completion calculations;
- loop scoring;
- conversation checkpoint compaction.

### Integration

- SQLite migrations/restart;
- Chrome ownership;
- browser adapter fixtures;
- project lock;
- PCC/GitHub adapters;
- installer migration harness.

### End-to-end controlled scenarios

1. Manager creates 5 independent tasks.
2. One Worker is slow; others continue.
3. Global sending-too-fast warning pauses future dispatches and later resumes.
4. Response is partial; no false DONE.
5. Send state becomes uncertain; no duplicate dispatch.
6. Chat login expires; queues persist while owner signs in.
7. Manager conversation rolls over; logical Manager continues.
8. Worker conversation rolls over mid-task.
9. App crashes/restarts during active wave.
10. Windows/network interruption.
11. Worker tasks overlap; guard prevents unsafe parallelism.
12. Manager repeats same ineffective task; Loop Guard stops cycle.
13. Verified completion reaches 99%; Closure Mode triggers.
14. Setup on clean Windows environment.
15. Install version N+1 over N without data loss.

## 11. v1 Definition of Done

All of the following are required:

- installable signed-or-clearly-dev-labeled Setup EXE from exact source SHA;
- Browser-first operation works without API key;
- real Manager logical session + up to 5 Worker slots;
- hidden/open/restart/kill session control;
- personal Chrome exclusion proven;
- staged/adaptive dispatch;
- slow/error/temporary-limit recovery;
- duplicate/wrong-chat protection;
- Conversation Lifecycle Manager works for Manager and Worker;
- persistent project/wave/task state survives restart/crash;
- PCC/GitHub project/evidence integration;
- Manager Estimate and Verified Completion separated;
- Loop Guard / Closure Mode;
- Attention Center only interrupts for real human gates;
- update-over-old-version path preserves data/settings/history;
- automated tests plus exact-head E2E evidence;
- UI visually matches the approved authority closely enough for acceptance;
- Issue #1 acceptance ledger reconciled.

## 12. Explicit non-goals for initial v1

Unless later added by owner:

- supporting every browser;
- replacing PCC governance;
- autonomous destructive production operations without configured gates;
- building an automation mechanism intended to evade ChatGPT limits/protective controls;
- cross-platform macOS/Linux packaging before Windows v1 is closed.
