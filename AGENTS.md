# PCC Executive Repository Constitution

PROJECT_ID: PCCEXECUTIVE
DISPLAY_NAME: PCC Executive
REPOSITORY: walidatiyaai2025-gif/walid
PROJECT_MODEL: STANDALONE
DEFAULT_SCOPE: PROJECT
CONTROL_PLANE: walidatiyaai2025-gif/project-control-center
CONTROL_PLANE_VERSION_BASELINE: v1.6.0
CANONICAL_V1_ISSUE: #1

## 1. Product authority

PCC Executive is a Windows desktop AI Project Commander. The owner is final product authority. Project Control Center (PCC) is the authoritative routing/governance control plane. This repository is the authoritative implementation source for PCC Executive.

The v1 product direction is **browser-first**:

- ChatGPT Web in PCC-owned Chrome sessions is the primary execution provider.
- One Manager/Controller session coordinates up to five Worker slots.
- OpenAI API support is optional, separately configured, and disabled by default.
- Product functionality must not require an API key when Browser mode is selected.

## 2. Required read order

Before implementation, every Manager/Lead/Worker must read:

1. this `AGENTS.md`;
2. `docs/MASTER_PLAN.md`;
3. `docs/ARCHITECTURE.md`;
4. `docs/UI_AUTHORITY.md` for visible work;
5. `docs/GUIDED_RUNTIME_EXECUTION_PLAN.md` for runtime guidance, navigation guards, Runtime Inspector, self-recovery and operator-flow work;
6. live PCC routing/governance state;
7. live GitHub Issue #1 and current PR/branch state.

Do not trust stale SHAs or historical prompts.

## 3. Canonical v1 mission

Issue #1 is the canonical v1 delivery authority until explicitly superseded. `CODE EXISTS != FEATURE COMPLETE`.

The v1 chain is:

`OWNER INTENT -> PCC ROUTE -> ISSUE #1 -> IMPLEMENTATION -> REAL CHATGPT/CHROME RUNTIME -> PERSISTENCE/RECOVERY -> TESTS -> INSTALLER -> UPDATE PATH -> QA -> RELEASE ARTIFACT`.

A rendered UI or mocked browser is not completion.

## 4. Browser session law

PCC Executive owns only browser processes/contexts it creates or explicitly adopts.

Mandatory rules:

- personal Chrome must never be killed by PCC controls;
- every Manager/Worker session has stable internal identity, process/context ownership, conversation lineage and project/task identity;
- sessions are hidden by default but can be opened/foregrounded at any time;
- individual Open, Hide, Restart and Kill are required;
- `Kill All PCC Sessions` only targets proven PCC-owned sessions;
- browser UI uncertainty is fail-safe: if the adapter cannot prove the target input/chat/state, it must not send.

## 5. ChatGPT resilience law

Transient ChatGPT behavior is a runtime state, not a reason to lose project state.

P0 states include slow response, temporary limit/sending-too-fast, transient error, partial response, session expiry, login required, offline/network failure, stuck generation, uncertain send and conversation-size/limit rollover.

Required behavior:

- preserve queues, tasks and evidence before recovery;
- pause unsafe new sends while allowing already-running replies to complete when possible;
- use adaptive pacing/cooldown instead of blind repeated sends;
- never resend an uncertain dispatch until current chat state is reconciled;
- do not implement mechanisms whose purpose is to evade service limits or protective controls;
- routine recovery is automatic; user interruption is reserved for genuinely unavoidable actions.

## 6. Conversation Lifecycle Manager

Manager and Workers are logical roles, not permanent ChatGPT conversations.

When a conversation approaches or reaches practical limits:

1. stop assigning new work to that conversation;
2. checkpoint canonical state;
3. preserve old conversation URL/identity;
4. create a continuation conversation;
5. send a compact continuation packet, not the full historic transcript;
6. verify continuation readiness;
7. switch active lineage only after successful handoff;
8. retain old session as archived evidence.

Recursive summaries alone are not canonical memory. Project memory must be reconstructable from persisted state plus live GitHub/PCC evidence.

## 7. Manager/Worker orchestration law

Maximum active Worker slots: 5. This is a ceiling, not a requirement to invent five tasks.

The Manager must:

- decompose work into non-overlapping executable tasks;
- respect dependencies;
- avoid duplicate/repeated tasks;
- consume Worker responses as evidence, not automatic truth;
- reconcile exact GitHub/project state before next-wave decisions.

PCC Executive must independently enforce:

- worker collision/scope overlap guard;
- wrong-chat guard;
- duplicate-send guard;
- project singleton lock;
- response quality/handoff validation;
- manager sanity checks against canonical state.

## 8. Completion law

Display two concepts separately:

- `MANAGER_ESTIMATE` — AI planning estimate;
- `VERIFIED_COMPLETION` — evidence-backed application state.

Verified completion is derived from applicable implementation, connectivity, tests, CI, QA, security, integration, packaging, deployment, unresolved blockers and exact-head evidence.

At 99% the system enters `CLOSURE_MODE`; it does not blindly declare DONE. 100% requires satisfied closure gates, or the project stops in an explicit blocked/stalled state.

## 9. Loop/stagnation guard

The runtime must detect repeated no-progress waves, repeated task fingerprints, unchanged source/evidence, repeated blockers, repeated failed tests and repeated manager reassignment.

A loop guard stops or escalates work; it must not generate infinite ChatGPT traffic.

## 10. Zero-touch / Attention Center law

Default design objective: routine operation should not require the owner to understand internal mechanics.

Automatically handle when safe:

- waiting;
- pacing;
- cooldown;
- transient retries;
- session recovery;
- conversation rollover;
- queue restoration;
- worker continuation;
- reconciliation.

Escalate only when human input is genuinely unavoidable, such as manual sign-in/account challenge, CAPTCHA, missing external credential/authority, or configured irreversible-action approval.

When escalation is required, the UI must explain one clear reason, open the exact required place, and present one clear action.

## 11. Destructive action gate

Autonomy does not imply unrestricted irreversible action. Force push, destructive production/database operations, irreversible deletion or other policy-defined destructive actions require an explicit configured approval gate and exact evidence.

## 12. Persistence and crash recovery

SQLite/local durable state must support restart recovery. Windows restart, app crash, browser crash, network outage or sleep must not reset project orchestration.

Persist at minimum:

- projects;
- waves;
- tasks;
- session identities/lineage;
- conversation URLs/health;
- dispatch records and idempotency keys;
- worker handoffs;
- manager decisions;
- evidence/checkpoints;
- settings;
- recovery events;
- installer/update state.

## 13. Installer/update law

v1 requires a user-facing Windows Setup executable with a standard `Next -> Next -> Install -> Launch` flow.

Future installers must upgrade over the current installation while preserving project data/settings/history. Database migrations must be versioned and update failure must have a safe rollback/recovery path.

## 14. UI authority

`assets/ui/` and `docs/UI_AUTHORITY.md` define the v1 visual authority. Reference application screens are 1920x1080 and use the approved premium dark PCC Executive design system.

Implementation may improve accessibility/responsiveness, but must not silently replace the product structure or Browser-first UX.

## 15. Implementation baseline

Preferred v1 stack:

- .NET 10;
- WPF Windows desktop;
- SQLite;
- Playwright for PCC-owned browser orchestration;
- provider abstraction with Browser-first default and optional API provider;
- structured state machines and machine-checkable handoffs.

Architecture changes that replace this baseline require explicit rationale and must preserve all product contracts.

## 16. Branch / integration discipline

Use one canonical branch per routed task and focused PRs. Do not force-push or delete unique work. Fetch live state before continuing an existing task. Release/installer conclusions must trace to one exact accepted source SHA.

## 17. Required Worker handoff

Every implementation Worker returns at minimum:

`TASK, STATUS, HEAD, CHANGED, VALIDATION, BLOCKER, NEXT_ACTION`.

Unsupported `DONE` is rejected.

## 18. Guided execution / operator-flow law

PCC Executive must behave as an active project commander, not as a passive collection of screens. The operator must never be required to guess which screen, button or recovery action is correct.

The canonical operator path is modeled as machine-readable prerequisite state, not free-form UI copy. At minimum the primary first-run/runtime path is:

`01 CHROME -> 02 PROJECT -> 04 MANAGER -> 05+ ORCHESTRATION/AUTOPILOT`.

Mandatory behavior:

- every actionable screen exposes a deterministic prerequisite contract;
- completed prerequisites are visibly marked as completed;
- missing/failed prerequisites are visibly marked as blocked or failed;
- the current valid action is visually distinct;
- future steps are visually muted/locked until their prerequisites are true;
- downstream navigation that would create an invalid runtime state must be blocked;
- every blocked action must state **what is missing, why it matters, and the exact numbered action/button to use next**;
- where safe, the UI provides `Go to Required Step` rather than making the operator navigate manually;
- `NEXT ACTION` is derived from canonical runtime state and guard evaluation, never from stale text or screen location alone;
- a technical exception must not be shown as the only instruction when the application can classify and recover it automatically;
- after a recoverable failure, the application updates step state, recovery state and `NEXT ACTION` atomically.

### 18.1 Step semantic states

The navigation/runtime guidance model must support at least:

- `COMPLETED` — semantic green plus icon/text label;
- `CURRENT` — primary accent plus explicit current marker;
- `BLOCKED` / `FAILED` — semantic red plus reason;
- `PENDING` — neutral/muted;
- `RECOVERING` — warning/active recovery state;
- `ATTENTION_REQUIRED` — explicit human action only when automation cannot safely continue.

Status must never be encoded by color alone.

### 18.2 Runtime Inspector requirement

PCC Executive must include a Runtime Inspector capable of reconstructing operator behavior and runtime decisions without asking the operator to describe the sequence from memory.

The inspector must expose at minimum:

- user action trace: screen, control, command, timestamp, allowed/blocked and reason;
- current project, Manager and Worker runtime identities;
- Chrome profile/source, PCC-owned runtime/process/context and DevTools endpoint state;
- current guard evaluation and prerequisite truth table;
- current valid next action;
- current/previous recovery reason;
- recent classified exceptions and connection failures;
- browser ownership proof and personal-Chrome exclusion evidence;
- Manager/Worker dispatch state and active session count;
- decision/recovery timeline sufficient to reconstruct why the application transitioned state.

The Runtime Inspector is diagnostic authority for reproducing owner behavior. It must support copy/export of a bounded diagnostic snapshot that excludes secrets, authentication tokens and sensitive browser data.

## 19. Large work-package execution law

For the guided-runtime initiative, implementation work must be assigned as **large vertical work packages**, not fragmented micro-tasks. Each routed Worker should own a coherent end-to-end slice that includes production code, persistence/state where applicable, UI wiring, tests and acceptance evidence.

Rules:

- prefer 4-6 substantial work packages over dozens of small implementation tasks;
- one work package may span multiple projects/layers when that is necessary to produce a complete vertical capability;
- split only at real ownership boundaries: state/navigation, runtime inspection, browser recovery, autopilot/attention, acceptance/release;
- do not create separate tasks for trivial model, view-model, XAML and test edits that belong to one capability;
- each work package must have explicit dependencies, exclusive ownership and measurable acceptance criteria;
- each work package must end in one focused PR or one explicitly coordinated integration handoff;
- a Worker must not claim completion from UI appearance alone; the capability must be exercised through runtime/state tests;
- the canonical detailed work packages and sequencing are defined in `docs/GUIDED_RUNTIME_EXECUTION_PLAN.md`.
