# PCC Executive Browser Acceptance

This project is the Worker 3 controlled Browser-first acceptance harness. It does not replace the production orchestration engine, durable SQLite store, WPF shell, installer, or CI ownership.

## Deterministic mode

Normal tests require no ChatGPT account and no live network. The harness models Manager plus 1/3/5 Worker topologies, staged dispatch, wrong-chat protection, adapter uncertainty, uncertain-send reconciliation, resilience faults, rollover, restart envelopes, resource retirement, and personal-Chrome invariants.

The deterministic acceptance report is deliberately privacy-safe. It contains scenario/source SHA/adapter version/runtime and logical IDs/state transitions/timings/failure codes/evidence codes only. It does not serialize prompts, response bodies, cookies, tokens, credentials, or browser profiles.

## Live Browser mode

Live tests are marked `Category=LiveBrowser` and are opt-in. They run only when:

`PCCEXECUTIVE_LIVE_BROWSER=1`

Optional settings:

- `PCCEXECUTIVE_LIVE_WORKERS=1..5`
- `PCCEXECUTIVE_LIVE_PROFILE_ROOT=<PCC-owned profile root>`
- `PCCEXECUTIVE_ACCEPTANCE_OUTPUT=<privacy-safe report directory>`
- `PCC_EXECUTIVE_CHROME_PATH=<Chrome executable>` when Chrome is not in a standard location.

The live boundary launches only PCC-owned Chrome profiles through `PlaywrightChromeRuntimeHost`, probes `AUTHENTICATED_READY`, `LOGIN_REQUIRED`, or `CHALLENGE`, and closes through the ownership-proof `BrowserSessionController`. It never attempts CAPTCHA/account-challenge bypass and never writes authenticated profile material to acceptance artifacts.

Worker 5 PR #6 already defines the compatible opt-in workflow contract: normal CI filters `Category!=LiveBrowser`; the dedicated self-hosted Windows workflow executes `Category=LiveBrowser`. This Worker does not modify that workflow.
