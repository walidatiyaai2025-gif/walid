# PCC Executive — Guided Runtime / Runtime Inspector Execution Plan

STATUS: CONSTITUTION-BACKED EXECUTION PLAN
AUTHORITY: `AGENTS.md` sections 18-19 + `docs/UI_AUTHORITY.md`
CANONICAL PRODUCT AUTHORITY: Issue #1
TARGET: PCC Executive v1 Browser-first Windows runtime
EXECUTION STYLE: LARGE VERTICAL WORK PACKAGES

## 1. Mission

Turn PCC Executive from a passive multi-screen shell into a **guided, state-aware project commander** that prevents invalid operator flow, automatically recovers routine browser/runtime failures, and records enough diagnostic evidence to reconstruct exactly what the operator did and why the runtime responded the way it did.

The owner must not need to guess:

- which screen to open;
- which button to press;
- whether Chrome is actually connected;
- whether a project prerequisite is satisfied;
- why Manager/Dispatch/Worker controls are disabled;
- whether an error requires human action or should auto-recover;
- which action caused a failure;
- what data to send back to a developer for diagnosis.

The product must answer those questions itself through canonical state, deterministic guards and Runtime Inspector evidence.

## 2. Mandatory product outcome

The runtime must converge on this operator contract:

`01 CHROME -> 02 PROJECT -> 04 MANAGER -> 05+ AUTONOMOUS ORCHESTRATION`

This is not a hardcoded wizard sequence. It is a **prerequisite/state machine** whose truth is derived from live runtime state.

Examples:

- if Chrome is healthy and already recoverable, Step 01 becomes completed without forcing redundant operator work;
- if Project is already bound after restart, Step 02 is completed;
- if Manager exists but Chrome endpoint is stale, the runtime enters recovery and temporarily blocks downstream actions;
- if recovery succeeds, downstream state re-opens automatically;
- if recovery requires sign-in or a genuinely unavoidable human action, Attention Center becomes the only human escalation path.

## 3. Non-negotiable engineering rules

1. **No UI-only implementation.** Every visible state must map to an explicit runtime/state model.
2. **No text-derived truth.** `NEXT ACTION`, completed/blocked states and guard decisions come from canonical state, not strings.
3. **No invalid navigation side effects.** A screen cannot create a runtime state that bypasses prerequisites.
4. **No generic fatal startup on stale browser endpoint.** Recoverable connection refusal/stale DevTools state is classified and reconciled.
5. **No personal Chrome destruction.** All recovery actions remain bounded by proven PCC ownership.
6. **No hidden operator behavior.** Important clicks/commands/guard denials/recovery transitions are observable in Runtime Inspector.
7. **No secret leakage.** Runtime diagnostics must redact cookies, auth tokens, browser profile secrets and sensitive payloads.
8. **No micro-task fragmentation.** The work is intentionally divided into large vertical packages below.
9. **Tests are part of each package.** Production code + UI + tests + acceptance evidence travel together.
10. **Installer/runtime restart is part of acceptance.** State must remain coherent across restart/repair/upgrade scenarios.

## 4. Target state model

Implement a shared semantic model equivalent to the following concepts. Exact type names may vary if existing abstractions already cover them.

### 4.1 ExecutionStep

Required primary step identities:

- `ChromeConnection` — screen 01;
- `ProjectSelection` — screen 02;
- `ManagerWorkspace` — screen 04;
- `Orchestration` — screen 05+ runtime continuation.

Additional screens may expose derived state but must not invent alternate prerequisite truth.

### 4.2 StepState

At minimum:

- `Pending`
- `Current`
- `Completed`
- `Blocked`
- `Failed`
- `Recovering`
- `AttentionRequired`

Each state includes structured reason metadata, not only a label.

### 4.3 PrerequisiteEvaluation

Must support:

- prerequisite key;
- satisfied bool;
- severity;
- reason code;
- human-readable explanation;
- required screen ID;
- required action/control ID where applicable;
- whether the runtime can self-heal it;
- whether operator action is genuinely required.

### 4.4 NextAction

One canonical next-action record:

- priority;
- action type;
- target screen;
- target control/command;
- reason code;
- operator instruction;
- automation eligibility;
- blocking prerequisite IDs;
- correlation ID to Runtime Inspector events.

### 4.5 RuntimeDiagnosticEvent

Minimum fields:

- monotonic/event ID;
- UTC timestamp;
- category;
- project/run identity where available;
- logical session/worker identity where available;
- screen/control/command identity for user actions;
- before-state summary;
- after-state summary;
- allowed/blocked result;
- reason code;
- exception classification where applicable;
- recovery correlation ID;
- redacted detail payload.

Persistence/retention should be bounded and configurable enough not to create unlimited local growth.

---

# LARGE WORK PACKAGE GR-001 — Guided Execution State Machine + Navigation Guard + Visual Step Semantics

## Objective

Build the canonical state/guard layer that decides what the operator is allowed to do, what has already been completed, what is currently required, and what must be blocked. Wire that state into the WPF shell so the application visibly prevents wrong-path operation.

This is a large vertical slice spanning Domain/Application/Presentation/WPF tests.

## Exclusive ownership

GR-001 owns:

- step/prerequisite domain model;
- execution-step evaluator;
- navigation guard;
- command guard integration;
- canonical `NEXT ACTION` calculation;
- semantic step state projection to WPF;
- left-nav status rendering;
- blocked-action UX;
- `Go to Required Step` behavior;
- state-driven top guidance banner;
- tests proving step truth and guard behavior.

GR-001 does **not** own low-level Chrome process recovery internals; it consumes browser health/recovery state exposed by GR-003.

## Required implementation

### A. Canonical prerequisite graph

Implement one source of truth for at least:

**01 Chrome complete when:**
- Browser-first provider selected;
- required signed-in/profile/runtime readiness is proven;
- active/recoverable Manager browser runtime is healthy enough for project execution, or runtime is in a safe completed-ready state before Manager creation depending on architecture;
- no unresolved login/challenge state blocks automation.

**02 Project complete when:**
- project resolves through live PCC routing;
- canonical project identity/repository/scope is known;
- singleton/project-run state is valid.

**04 Manager available when:**
- Chrome prerequisite is satisfied;
- Project prerequisite is satisfied;
- Manager logical runtime can be created/recovered safely.

**05+ Orchestration available when:**
- Manager has produced/entered a valid planning/runtime state;
- dispatch prerequisites are valid;
- runtime is not blocked by global safety/recovery state.

Do not force these exact predicates if live architecture has a more precise equivalent; preserve their semantic intent.

### B. NavigationGuard service

Every primary navigation request passes through one guard layer.

Guard result must be structured:

- Allowed;
- Blocked;
- RedirectRecommended;
- reason code;
- missing prerequisites;
- target required step/action.

The shell must not navigate into an invalid workflow screen just because the user clicked the menu.

Diagnostic/read-only screens such as Runtime Inspector, Attention Center, Session Monitor or Settings may remain accessible when safe, even if the main execution path is blocked.

### C. Command gating

Buttons/commands must use the same prerequisite evaluator as navigation.

Examples:

- `Start / Continue Manager` cannot enable only because a project card exists;
- `Dispatch` cannot enable while Manager or browser state is unresolved;
- Worker commands cannot imply active dispatch when no Worker runtime exists.

Avoid duplicated `CanExecute` business logic in individual view models. Centralize guard evaluation.

### D. Semantic navigation rendering

Apply UI authority states:

- completed: green + check/icon/text cue;
- current: purple/blue accent + current marker;
- blocked/failed: red + state/reason cue;
- pending: muted;
- recovering: warning/active status;
- attention required: explicit attention semantic.

Never depend on color alone.

### E. Blocked-flow UX

On a wrong-path click:

- do not navigate into the invalid target screen;
- show attempted action;
- show missing prerequisite(s);
- show exact required numbered screen;
- show exact control/command name;
- offer `Go to Required Step` when safe;
- record the event for Runtime Inspector.

### F. `NEXT ACTION`

Compute it from live state. Required properties:

- one primary instruction at a time;
- exact screen number/name;
- exact control name for human-required steps;
- automation steps should execute automatically instead of telling the operator to do them;
- after state transition, guidance recalculates immediately;
- no contradictory banner vs nav state.

### G. Restart/state reconciliation

After app restart:

- do not reset all steps to pending;
- recompute from persisted + live runtime state;
- stale persisted completion must not override live failure;
- recoverable runtime state may show Recovering before Completed.

## Required tests

At minimum:

1. no Chrome + no project -> Step 01 current/blocked red where appropriate, Manager blocked;
2. Chrome ready + no project -> Step 01 completed, Step 02 current;
3. Chrome ready + project selected -> Steps 01/02 completed, Manager current;
4. project selected but Chrome stale -> Project remains completed, Chrome failed/recovering, Manager blocked;
5. wrong-path Manager click -> no navigation + exact redirect instruction;
6. wrong-path Dispatch click -> no navigation + exact missing Manager prerequisite;
7. state update automatically changes nav colors/state and `NEXT ACTION`;
8. restart does not invent completion;
9. status has text/icon cue in addition to color;
10. Runtime Inspector/Attention remain reachable when execution path is blocked.

## Acceptance

A tester with no product knowledge can launch PCC Executive and, without external instructions, identify exactly what to do next. The tester cannot enter a downstream execution screen in a state that would cause invalid runtime behavior.

---

# LARGE WORK PACKAGE GR-002 — Runtime Inspector + Behavior Trace + Diagnostic Export

## Objective

Create the diagnostic authority that records what the operator did, what the runtime believed, what guards decided, and what recovery occurred. It must be sufficient for a developer to reproduce a behavioral defect from a screenshot/export without asking the operator to remember the sequence.

This is a large vertical slice spanning runtime instrumentation, persistence, redaction, UI and test infrastructure.

## Exclusive ownership

GR-002 owns:

- diagnostic event model;
- event correlation IDs;
- user-action instrumentation;
- guard-decision instrumentation;
- runtime transition instrumentation hooks;
- bounded persistence/retention;
- Runtime Inspector screen 16;
- diagnostic snapshot copy/export;
- redaction policy/tests;
- timeline rendering;
- current prerequisite truth table rendering.

GR-002 does not define browser recovery behavior; it records GR-003 events.

## Required implementation

### A. Event collector

Provide a central service that can record:

- navigation attempts;
- button/command invocations;
- allowed vs blocked decisions;
- project selection/resolution;
- browser connect/recover attempts;
- Manager start/recover;
- dispatch start/pause/resume;
- Worker session lifecycle;
- guard changes;
- Attention requests;
- classified exceptions;
- recovery start/success/failure;
- application startup/shutdown recovery milestones.

### B. Correlation model

A user click that triggers recovery should be traceable as one correlated chain:

`User Action -> Guard Evaluation -> Runtime Command -> Recovery Attempt -> Result -> State Recalculation -> Next Action`.

### C. Inspector views

Runtime Inspector must expose at minimum:

1. **Current State**
   - project/run;
   - provider;
   - Chrome/profile/runtime health;
   - Manager/Workers;
   - active sessions;
   - dispatch/autopilot;
   - current Next Action.

2. **Prerequisite Truth Table**
   - step;
   - prerequisite;
   - pass/fail;
   - reason code;
   - automation/human requirement.

3. **Operator Behavior Timeline**
   - time;
   - screen;
   - control/command;
   - allowed/blocked;
   - result.

4. **Recovery/Decision Timeline**
   - stale endpoint;
   - ECONNREFUSED;
   - ownership check;
   - replacement/recovery;
   - attention escalation;
   - state transition.

5. **Browser Runtime Evidence**
   - selected profile/source label;
   - PCC runtime ID;
   - logical role;
   - process ID where safe;
   - DevTools endpoint health status;
   - ownership proof summary;
   - do not show tokens/cookies.

### D. Diagnostic export

Implement a bounded snapshot suitable for Copy or Save:

- app version;
- exact source/build identity if available;
- timestamps;
- current state summary;
- recent N diagnostic events;
- current step truth table;
- current browser/session metadata;
- latest exception classifications;
- latest recovery decisions.

Redact:

- cookies;
- OAuth tokens;
- authorization headers;
- passwords;
- browser storage secrets;
- full prompt/response payloads unless separately approved and scrubbed;
- local filesystem data not required for diagnosis.

### E. Persistence

Persist enough events to survive restart and explain startup recovery. Use bounded retention, for example by event count/time/size, without silently growing forever.

## Required tests

1. every navigation attempt produces an inspector event;
2. blocked navigation includes reason + required step;
3. browser connection refusal produces classified diagnostic event;
4. recovery chain uses one correlation ID;
5. snapshot survives app restart;
6. retention removes old events safely;
7. redaction removes known token/cookie/auth patterns;
8. export never serializes raw browser cookies/local storage;
9. inspector is accessible while core execution is blocked;
10. diagnostic snapshot identifies exact current Next Action and prerequisite failures.

## Acceptance

Given only the exported diagnostic snapshot from a failed user run, a developer can answer:

- what the operator clicked;
- in what order;
- what was blocked;
- what browser endpoint/state existed;
- whether PCC owned the relevant process;
- what recovery was attempted;
- why the app chose the displayed Next Action.

---

# LARGE WORK PACKAGE GR-003 — Browser Runtime Self-Recovery + Persistent Profile/DevTools Ownership Hardening

## Objective

Eliminate fatal or confusing runtime behavior caused by stale DevTools endpoints, dead PCC-owned Chrome processes, persistent-profile reuse, and recovery ambiguity. Routine browser faults must self-heal while preserving personal Chrome safety.

This is a large vertical Browser/runtime package spanning ownership, persistence, startup recovery, profile launch semantics and browser acceptance tests.

## Exclusive ownership

GR-003 owns:

- startup browser reconciliation;
- stale endpoint detection;
- connection-refused classification;
- PCC-owned process/context validation;
- persistent Manager/Worker browser runtime strategy;
- selected existing signed-in profile/source semantics;
- safe runtime replacement;
- endpoint refresh/re-registration;
- orphan cleanup bounded by ownership proof;
- browser recovery telemetry emitted to GR-002;
- browser acceptance tests.

GR-003 must not redesign visible navigation semantics owned by GR-001.

## Required implementation

### A. Startup recovery must not fatal on stale local endpoint

Errors equivalent to:

`connect ECONNREFUSED 127.0.0.1:<port>`

must be classified as stale/dead endpoint candidates, not automatically as fatal startup exceptions.

Required sequence:

1. identify persisted runtime/session record;
2. prove PCC ownership before any destructive action;
3. probe process/endpoint health;
4. if endpoint stale/dead, mark runtime degraded/recovering;
5. safely retire/replace only the PCC-owned runtime;
6. preserve logical Manager/Worker identity and project lineage;
7. refresh persisted endpoint/process metadata;
8. notify state/inspector layers;
9. continue startup if recovery succeeds;
10. escalate only if authentication/challenge or unrecoverable ownership ambiguity prevents safe continuation.

### B. Persistent profile semantics

The selected profile source must behave as a stable signed-in state source, consistent with the approved GPTDesktop-style user expectation.

Do not create a fresh arbitrary runtime profile per UUID if that destroys login continuity.

Maintain separation between:

- personal/existing profile source;
- PCC-owned runtime profile/process that PCC may safely control.

If the implementation copies/adopts state, document exactly when and how; do not mutate or kill the personal source browser unless explicitly allowed by ownership contract.

### C. Endpoint lifecycle

Never assume persisted DevTools port remains valid after restart/crash.

Endpoint is runtime metadata and must be re-probed/reconciled.

### D. Recovery state contract

Expose structured states/reasons to GR-001/GR-002, e.g.:

- `READY`
- `DEGRADED_ENDPOINT_STALE`
- `RECOVERING_RUNTIME`
- `REPLACED_PCC_RUNTIME`
- `LOGIN_REQUIRED`
- `OWNERSHIP_UNCERTAIN`
- `RECOVERY_FAILED`

Use existing enums/contracts where possible rather than inventing parallel state systems.

### E. Safety

Recovery must prove:

- unrelated personal Chrome PID is not killed;
- unrelated Chrome profile is not deleted;
- `Kill All PCC Sessions` remains ownership-scoped;
- runtime replacement preserves logical identity;
- uncertain ownership fails safe.

## Required tests

1. persisted endpoint refuses connection -> app starts and recovery runs;
2. PCC-owned dead runtime -> replacement succeeds;
3. unrelated personal Chrome stays alive;
4. stale endpoint metadata is replaced, not reused blindly;
5. logical Manager identity survives replacement;
6. logical Worker identity survives replacement;
7. selected signed-in profile source yields persistent authenticated PCC runtime where source state is valid;
8. login required is classified and escalated, not retried forever;
9. ownership uncertain -> no destructive action;
10. crash/restart during active wave reconciles browser state safely;
11. repeated stale endpoint does not create unbounded Chrome processes;
12. recovery emits inspector timeline/correlation events.

## Acceptance

Killing a PCC-owned Chrome runtime or invalidating its local DevTools port must no longer produce a fatal `PCC Executive startup failed` dialog when automatic safe recovery is possible.

---

# LARGE WORK PACKAGE GR-004 — Autonomous Next-Action Router + Attention Integration + Wrong-Path Prevention End-to-End

## Objective

Combine the state/guard layer, browser recovery and diagnostics into an operator experience where PCC Executive handles routine work itself and asks the user for exactly one explicit action only when human intervention is genuinely unavoidable.

This is the convergence package for smart guidance/autopilot UX.

## Dependencies

Requires usable contracts from GR-001, GR-002 and GR-003.

## Exclusive ownership

GR-004 owns:

- automation-vs-human action arbitration;
- Next Action router;
- safe auto-navigation/auto-recovery triggers;
- Attention Center integration;
- actionable guidance wording policy;
- guard/Attention conflict resolution;
- downstream screen behavior while recovery is active;
- end-to-end operator flow integration tests.

## Required implementation

### A. Automation-first action policy

For every unmet prerequisite classify:

1. **Automatically recoverable** -> application performs it;
2. **Safe one-click operator action** -> show exact button/screen;
3. **Human-authentication/challenge required** -> Attention Center;
4. **External authority/credential required** -> Attention Center;
5. **Destructive approval required** -> approval gate/Attention Center;
6. **Unrecoverable internal defect** -> explicit blocked state + diagnostic export, not vague instruction.

Do not tell the operator to click `Connect / Recover Chrome` when the application can safely perform that recovery automatically as part of project open/startup.

### B. Project-open convergence

When a project is opened:

- evaluate Chrome prerequisite;
- auto-connect/recover Manager Chrome if safe;
- if successful, advance canonical state;
- if sign-in required, route to Attention;
- if recovery in progress, show Recovering and block Manager until resolved;
- do not leave the operator on Projects with a contradictory instruction banner.

### C. Manager-start convergence

When Chrome + Project are ready:

- Manager screen becomes current/available;
- exact Start/Continue action is highlighted;
- if Manager can be safely auto-created under current dispatch/autopilot policy, perform safe setup automatically and present only the next meaningful owner action;
- record all transitions.

### D. Attention contract

Attention Center must not receive routine technical noise.

Examples that should normally remain automatic:

- stale endpoint;
- dead PCC-owned Chrome process;
- transient connection refusal;
- safe runtime replacement;
- normal cooldown;
- recoverable network flap.

Examples that may require Attention:

- ChatGPT login required and no valid authenticated runtime can be recovered;
- account challenge/CAPTCHA;
- missing external project authority/credential;
- irreversible action requiring approval;
- ownership ambiguity that makes browser process control unsafe.

### E. Guided error copy

Technical detail may remain available in Runtime Inspector, but primary UI language must be actionable.

Bad primary message:

`connect ECONNREFUSED 127.0.0.1:58760`

Better primary message:

`Chrome runtime connection was lost. PCC Executive is recovering its managed session automatically.`

If recovery fails due to sign-in:

`Chrome session needs sign-in. Open 01 Chrome and complete ChatGPT sign-in, then PCC Executive will continue automatically.`

The technical endpoint/error belongs in Inspector/diagnostic detail.

### F. Recovery lock/guard coordination

While a prerequisite is recovering:

- downstream commands remain blocked;
- duplicate recovery commands do not create parallel runtimes;
- Next Action says recovery is in progress rather than encouraging repeated clicks;
- timeout/failure transitions are deterministic.

## Required tests

1. project open with healthy Chrome -> no redundant user reconnect instruction;
2. project open with stale endpoint -> auto-recovery starts;
3. user clicks Manager during recovery -> blocked with reason, no duplicate recovery;
4. recovery succeeds -> Manager automatically becomes available/current;
5. login required -> Attention item with one action;
6. Attention does not receive transient recoverable endpoint error;
7. Next Action never contradicts current nav state;
8. one highest-priority instruction shown at a time;
9. rapid repeated operator clicks cannot create invalid duplicate runtimes;
10. inspector reconstructs the complete action/recovery chain.

## Acceptance

A non-technical operator can follow the application without memorizing a manual. Routine faults are handled automatically; unavoidable human tasks are singular, explicit and navigable.

---

# LARGE WORK PACKAGE GR-005 — Deterministic Acceptance, Persistence, Installer/Upgrade and Release Closure

## Objective

Prove the entire guided-runtime initiative as a release-quality feature set across restart, browser failure, installer repair/upgrade and realistic user behavior. Close gaps that only appear when the vertical packages compose.

This is a large integration/acceptance package, not a feature-invention package.

## Dependencies

GR-001 through GR-004 should be substantially complete before final closure.

## Exclusive ownership

GR-005 owns:

- deterministic integration harness;
- cross-package acceptance matrix;
- persistence/restart proof;
- installer repair/upgrade behavior with new diagnostic state;
- UI acceptance against `docs/UI_AUTHORITY.md`;
- package provenance;
- exact-head evidence;
- regression closure only where needed for composition.

## Required acceptance scenarios

### Scenario 1 — Clean first run

- install;
- launch;
- Step 01 current;
- choose/use valid signed-in profile/runtime;
- Chrome becomes completed;
- Project becomes current;
- open project;
- Manager becomes current;
- no contradictory guidance.

### Scenario 2 — Restart after project binding

- Chrome/project previously ready;
- close app;
- reopen;
- state is recomputed honestly;
- completed project remains completed if still valid;
- stale browser endpoint triggers recovery rather than fake completion.

### Scenario 3 — Dead DevTools endpoint

- persist runtime with endpoint;
- kill PCC-owned browser or invalidate port;
- restart;
- no fatal startup dialog if recoverable;
- Step 01 shows Recovering/Failed as appropriate;
- Manager blocked until reconciliation;
- recovery succeeds or Attention explains one human action.

### Scenario 4 — Wrong-path behavior

Attempt:

- Manager before Chrome/Project;
- Dispatch before Manager;
- Worker Chat before Worker runtime;

Expected:

- navigation/command blocked;
- exact missing prerequisite shown;
- `Go to Required Step` offered where appropriate;
- inspector records each denied attempt.

### Scenario 5 — User behavior reconstruction

Perform a known click sequence including one wrong-path attempt and one recovery event. Export diagnostic snapshot. A test must verify the export contains the complete correlated sequence and no secret values.

### Scenario 6 — Personal Chrome safety

Run unrelated personal Chrome while forcing PCC runtime recovery. Verify personal process/session remains untouched.

### Scenario 7 — Installer same-version repair

- app has guided-runtime state/history;
- installer repair over same version/test build;
- persistent project/settings/diagnostic state preserved according to retention policy;
- startup remains healthy.

### Scenario 8 — Upgrade N -> N+1

- checkpoint;
- preserve SQLite state;
- migrate schema if new diagnostic tables exist;
- verify current Step/Next Action recomputed from live state;
- rollback/recovery proof if migration fails.

### Scenario 9 — DPI/UI authority

At reference and supported reduced viewports verify:

- semantic step status remains visible;
- status not color-only;
- Runtime Inspector is usable;
- blocked-action dialog/callout is readable;
- primary controls do not disappear.

### Scenario 10 — Stress/repeated clicks

Rapidly click navigation/recovery controls while Chrome is recovering. Verify:

- no duplicate manager runtimes;
- no duplicate browser recovery loops;
- no thread-affinity WPF exception;
- no unbounded diagnostic event storm;
- state converges deterministically.

## Required release evidence

- exact accepted source SHA;
- build/test results;
- Browser acceptance results;
- WPF UI/state tests;
- persistence/restart test results;
- installer verification;
- same-version repair;
- upgrade/data preservation if applicable;
- package hash;
- known blockers explicitly listed.

## Acceptance

The release candidate must survive the same sequence a real operator is likely to perform without external coaching and without requiring the operator to interpret raw browser/runtime errors.

---

## 5. Work-package sequencing

Recommended programmer allocation:

- **Worker A / GR-001** — state machine, navigation guard, semantic steps, Next Action foundation;
- **Worker B / GR-002** — Runtime Inspector, event trace, persistence, redaction/export;
- **Worker C / GR-003** — browser self-recovery, profile/runtime ownership, stale endpoint handling;
- **Worker D / GR-004** — autopilot action router, Attention integration, convergence UX;
- **Worker E / GR-005** — full deterministic acceptance, persistence/install/update/release closure.

Parallelism:

- GR-001, GR-002 and GR-003 may begin in parallel after agreeing shared contracts.
- GR-004 consumes the first stable contracts from those three.
- GR-005 begins test-harness preparation early but final acceptance occurs after GR-004 convergence.

Do not split these work packages into tiny per-file assignments unless a specific blocker requires reassignment.

## 6. Shared contract checkpoint before parallel coding

Before Workers A-C diverge, Integration Lead must establish or approve:

- canonical Step IDs;
- structured prerequisite result contract;
- NextAction contract;
- runtime diagnostic event/correlation contract;
- browser recovery state/reason contract;
- ownership boundaries for persistence changes.

This checkpoint prevents three parallel state models from being invented.

## 7. Definition of done for this initiative

The initiative is not complete until all are true:

- navigation visually distinguishes completed/current/blocked/pending/recovering states;
- state is not color-only;
- invalid operator paths are guarded;
- blocked paths explain exact next screen/action;
- `NEXT ACTION` is derived from live canonical state;
- routine browser failures auto-recover when safe;
- stale DevTools endpoint does not cause avoidable fatal startup;
- personal Chrome remains protected;
- Runtime Inspector reconstructs user behavior and runtime decisions;
- diagnostic export is redacted and bounded;
- restart preserves/reconciles state;
- installer repair/upgrade preserves required data;
- end-to-end tests prove the complete operator flow;
- final package traces to exact source SHA;
- unresolved failures are explicit blockers, not hidden behind `READY` or optimistic UI.
