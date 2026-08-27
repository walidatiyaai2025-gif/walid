# PCC Executive — UI Authority

REFERENCE RESOLUTION: 1920x1080
ASSET ROOT: `assets/ui/`
PRIMARY THEME: Premium dark / navy / purple PCC Executive

## 1. Visual authority

The approved screen pack in `assets/ui/PCC_Executive_Final_Premium_UI_Assets_1920x1080.zip` is the v1 visual/product workflow authority. The application may improve responsiveness/accessibility while preserving the screen hierarchy, core controls and Browser-first workflow.

## 2. Screen inventory

### 00 — Setup Wizard
Purpose: standard Windows install flow.

Must show:
- PCC Executive identity;
- recommended installation;
- install path;
- desktop shortcut;
- launch after setup;
- preserve data on upgrades;
- `Next` flow.

### 00 — Upgrade Wizard
Purpose: upgrade over existing version.

Must show:
- current/new versions;
- checkpoint/preserve/migrate plan;
- upgrade and restart action;
- data preservation/rollback expectations.

### 01 — Chrome Connection
Purpose: establish Browser-first runtime.

Must communicate:
- ChatGPT Web / Chrome is primary;
- session/login health;
- browser profile/runtime readiness;
- optional API is not required.

### 02 — Project Selection
Purpose: choose a PCC-routed project.

Cards show:
- display name;
- health/progress;
- active/paused state;
- recent project context.

### 03 — Executive Dashboard
Purpose: owner-level project status.

Must show:
- Verified Completion;
- wave;
- active Workers;
- P0/P1/blockers;
- Worker pool;
- ChatGPT Health;
- Loop Guard;
- Attention Center count;
- Autopilot state.

The dashboard must make `0 Attention Required` prominent when normal operation needs no user action.

### 04 — Manager Workspace
Purpose: inspect the Manager logical session and current planning conversation.

Must show:
- Manager messages/responses;
- context/evidence sources;
- current wave/session identity;
- ability to open the real ChatGPT conversation;
- logical Manager survives conversation rollover.

### 05 — Workers Dispatch
Purpose: control staged dispatch.

Must show:
- dispatch mode;
- base interval;
- adaptive pacing;
- auto pause/resume policy;
- max Worker slots;
- queue status;
- next-send countdown;
- progress;
- pause and emergency stop controls.

Default v1 configuration:
- Automatic Staged;
- base 10 seconds;
- Adaptive Pacing ON;
- Pause on global limit ON;
- Auto Resume ON;
- Duplicate-send protection ON.

### 06 — Worker Chat
Purpose: inspect any Worker and optionally open the real browser chat.

Must show:
- logical Worker slot/role;
- current task;
- status/progress;
- response/handoff;
- current conversation health;
- `Open in ChatGPT`/bring-to-front control.

### 07 — Wave Summary
Purpose: reconcile Worker results before Manager next-wave decision.

Must show:
- every Worker/task state;
- collected responses;
- wave progress;
- unresolved blockers;
- reconcile/send-to-Manager action/state.

### 08 — Task Board
Purpose: human-readable project queue.

States include To Do, In Progress, Testing, Done. Do not present unverified work as complete simply because a Worker claimed DONE.

### 09 — Evidence & Verification
Purpose: evidence-backed truth.

Must separate:
- implementation;
- tests;
- CI/CD;
- QA;
- security;
- deployment;
- documentation/other project gates;
- overall VERIFIED completion.

### 10 — Loop Guard & Stability
Purpose: show why the system is continuing or stopping.

Must show monitoring for:
- same HEAD/evidence across waves;
- same blocker;
- low progress delta;
- repeated failed test;
- repeated task/fingerprint;
- repeated reassignment.

### 11 — ChatGPT Health & Recovery
Purpose: resilience status.

Must show:
- global health;
- account/auth state;
- Manager/Worker readiness;
- slow session count;
- temporary limit/cooldown;
- automatic recovery actions;
- recent recovery timeline.

Routine recovery controls are available but explicitly secondary to automatic recovery.

### 12 — Session Monitor
Purpose: exact ownership/control of Manager + Workers.

Must show:
- logical session;
- role;
- runtime state;
- hidden/visible state;
- PCC-owned PID/context evidence;
- conversation identity;
- last activity;
- Open / Hide / Restart / Kill per session;
- Kill All PCC Sessions.

Critical UI copy: personal Chrome is excluded.

### 13 — Settings
Purpose: advanced configuration without making configuration mandatory.

Execution provider:
- ChatGPT Web / Chrome — PRIMARY / selected by default;
- OpenAI API — optional;
- Hybrid — optional future/secondary mode.

Autopilot defaults are smart and conservative.

### 14 — Update Center
Purpose: in-place application upgrade.

Must show:
- current/new version;
- package/compatibility verification;
- data backup/preservation;
- migration/rollback readiness;
- Install Update & Restart.

### 15 — Attention Center
Purpose: shield the owner from routine technical problems.

Normal target: `0 — Nothing needs you`.

Automatic/no-user cases:
- slow ChatGPT;
- temporary sending-too-fast limit;
- transient ChatGPT error;
- Worker stuck within recovery policy;
- conversation rollover;
- normal wave completion.

User-required cases:
- ChatGPT login/sign-in;
- CAPTCHA/account challenge;
- missing external credential/authority;
- configured irreversible production/destructive approval;
- genuine project decision that cannot be inferred safely.

When attention is required, present:
1. one simple explanation;
2. exact location/session;
3. one clear action.

## 3. Shared top-level controls

Where applicable:
- `Pause AI` checkpoints state and stops new dispatches without destroying sessions;
- `Resume` continues safe orchestration;
- `Kill All PCC Sessions` terminates only proven PCC-owned sessions;
- project and Autopilot state remain visible.

## 4. Status language

Prefer semantic states:

- READY
- RUNNING
- GENERATING
- WAITING
- DONE / VERIFIED
- SLOW
- COOLDOWN
- RECOVERING
- LOGIN REQUIRED
- BLOCKED
- STALLED

Avoid generic `Error` when a more useful recovery state exists.

## 5. Accessibility / usability constraints

- high contrast text;
- readable default font size;
- keyboard-accessible primary controls;
- destructive controls visually distinct and confirmed;
- status never encoded only by color;
- no essential action hidden exclusively behind hover;
- window scales/reflows reasonably below 1920x1080 while keeping reference layout authority.
