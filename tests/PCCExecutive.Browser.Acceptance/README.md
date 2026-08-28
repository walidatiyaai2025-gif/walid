# PCC Executive Browser Acceptance

This project is the Worker 3 controlled Browser-first acceptance harness. It does not replace the production Manager orchestration engine, durable SQLite store, WPF shell, installer, updater, or CI ownership.

## Deterministic mode

Normal tests require no ChatGPT account and no live network. The harness models Manager plus 1/3/5 Worker topologies, staged dispatch, wrong-chat protection, adapter uncertainty, uncertain-send reconciliation, resilience faults, rollover, restart envelopes, resource retirement, and personal-Chrome invariants.

Wave 3 adds deterministic boundary tests for explicit live opt-in gating, manual-login and challenge boundaries, authenticated-ready recognition, controlled ChatGPT conversation identity parsing, Manager/Worker identity separation, stable multi-signal response completion, safe `SUBMITTED_UNKNOWN` fault injection, restart identity reconciliation, and fail-closed privacy artifact sanitization.

Normal CI must continue to exclude `Category=LiveBrowser`.

## Live Browser pilot mode

Live tests are marked `Category=LiveBrowser` and remain explicit opt-in only. Worker 5 owns the dedicated self-hosted Windows workflow; this project conforms to that existing contract and does not weaken it.

Base live probe:

`PCCEXECUTIVE_LIVE_BROWSER=1`

Real prompt submission requires a second explicit opt-in:

`PCCEXECUTIVE_LIVE_PILOT_SUBMIT=1`

The real pilot also requires operator-provided controlled conversation URLs. They are identifiers only; no profile/cookie/token material is imported or serialized.

Required for Level 1:

- `PCCEXECUTIVE_LIVE_MANAGER_URL=https://chatgpt.com/c/<controlled-manager-id>`
- `PCCEXECUTIVE_LIVE_WORKER1_URL=https://chatgpt.com/c/<controlled-worker-id>`

Optional configuration:

- `PCCEXECUTIVE_LIVE_ALLOW_MANUAL_LOGIN=1` permits bringing only the exact PCC-owned runtime to the foreground while the operator signs in manually; no password or CAPTCHA automation is performed.
- `PCCEXECUTIVE_LIVE_PILOT_LEVEL=1|2|3` defaults to Level 1.
- `PCCEXECUTIVE_LIVE_WORKERS=1..5`; Level 1 always resolves to one Worker, Level 2 resolves to 2..3, and Level 3 resolves to 4..5.
- `PCCEXECUTIVE_LIVE_WORKER2_URL` through `PCCEXECUTIVE_LIVE_WORKER5_URL` are required only for the selected progressive level.
- `PCCEXECUTIVE_LIVE_PROFILE_ROOT=<PCC-owned profile root>`
- `PCCEXECUTIVE_ACCEPTANCE_OUTPUT=<privacy-safe report directory>`
- `PCC_EXECUTIVE_CHROME_PATH=<Chrome executable>` when Chrome is not in a standard location.
- `PCCEXECUTIVE_LIVE_TEST_WRONG_CHAT=1` navigates one controlled Worker runtime to another controlled conversation and proves `MISMATCH -> NO SEND`, then restores the expected conversation.
- `PCCEXECUTIVE_LIVE_TEST_UNCERTAIN_SEND=1` converts one actually-triggered harmless pilot submission into `SUBMITTED_UNKNOWN`, immediately proves duplicate blocking, and reconciles the real conversation without granting `SAFE_RETRY` from weak absence evidence.
- `PCCEXECUTIVE_LIVE_TEST_CRASH_RECOVERY=1` terminates only the selected PCC-owned runtime through ownership proof and exercises logical-identity recovery.

## Progressive acceptance levels

Level 1 is the first real acceptance target: PCC-owned Manager + one PCC-owned Worker, authenticated-ready or explicit manual-login boundary, one harmless Worker prompt, stable structured response association, duplicate-send protection, and privacy-safe evidence.

Level 2 permits Manager + 2..3 Workers only after Level 1 is reliable. Worker sends remain staged at least ten seconds apart.

Level 3 permits Manager + 4..5 Workers. A deterministic five-Worker harness is not evidence that Level 3 live acceptance passed. Live state must be reported honestly as `PASS`, `FAIL`, `BLOCKED_LOGIN`, `BLOCKED_CHALLENGE`, `BLOCKED_RUNNER`, `BLOCKED_DEPENDENCY`, or `NOT_EXECUTED`.

## Harmless live prompt

The pilot asks a Worker to return exactly these machine-readable fields: `TASK_ID`, `WORKER_SLOT`, `STATUS: ACK`, and `NON_DESTRUCTIVE_MARKER: PCC_EXECUTIVE_LIVE_PILOT`.

No production-destructive GitHub action is part of the Browser pilot.

## Submission and completion evidence

A keyboard event alone is not treated as proof of submission. The existing Browser adapter must prove semantic post-submit evidence or the dispatch becomes `SUBMITTED_UNKNOWN`.

Response completion is accepted only after multiple semantic signals agree and the captured assistant response is stable across observations. Visible text alone is insufficient. Partial responses never become `DONE`.

The live uncertain-send probe deliberately withholds negative proof when it cannot find the exact user message. Only positive message presence is asserted from a live DOM scan; weak non-observation remains `CANNOT_DETERMINE` and cannot trigger automatic resend.

## Authentication and service protections

The pilot may detect `LOGIN_REQUIRED` or `CHALLENGE`, bring the exact PCC-owned runtime to the foreground, and wait for manual operator action when explicitly allowed. It never automates passwords, scrapes credentials, bypasses CAPTCHA/account challenges, copies personal Chrome cookies or browser profiles, imports the user's personal Chrome profile, introduces anti-detection behavior, intentionally generates traffic to force a rate limit, or attempts to evade service limits.

Natural rate-limit evidence pauses new sends through the existing global send gate. Slow generation is observed without aggressive reload/restart.

## Privacy-safe evidence

`LivePilotArtifactSanitizer` is fail-closed for obvious sensitive material. Artifact generation throws if it detects common authorization/cookie/password/token/JWT/API-key patterns.

Permitted artifact fields are limited to scenario/source SHA, adapter version, opaque runtime/logical/conversation IDs, semantic state transitions, timings, failure codes, session-monitor metadata, and short evidence codes. Prompts and response bodies are not serialized.

Never upload or persist through acceptance artifacts cookies, local-storage secrets, authentication headers, access/refresh/session tokens, passwords, browser profiles, personal ChatGPT content, or unnecessary conversation transcripts. The authenticated PCC profile, if present on a dedicated runner, remains local to that runner and is not a CI artifact.

## Integration boundaries

Worker 1 owns Manager orchestration. This branch does not recreate it; a full live Manager-plan -> Worker-handoff -> Manager-summary pilot requires the accepted Manager orchestration integration.

Worker 2 owns SQLite/restart implementation. The live pilot exposes restart identity reconciliation ports and verifies identity matching without introducing another database.

Worker 4 owns WPF bindings. The live evidence surface exposes LogicalAgentId, WorkerSlot, ConversationId, RuntimeId, OwnedByPcc, State, Heartbeat, Visibility, Health, and LastActivity for UI consumption without implementing a screen.

Worker 5 owns CI/package/release. The existing dedicated `pcc-browser-acceptance` Windows runner and workflow remain the execution boundary for real Browser tests.
