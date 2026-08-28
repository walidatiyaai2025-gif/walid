# Integrated Build Rehearsal

This Wave-2 release/build layer validates exact Worker-chain heads without taking canonical integration authority away from the PCC Executive Integration Lead.

`release/integration-rehearsal.json` records the live chain snapshot used by the rehearsal. `Get-IntegrationMatrix.ps1` reports `FOUND`, `PENDING_PR`, `BLOCKED`, or `INCOMPATIBLE`. The workflow validates each current Worker chain at its exact SHA, creates an ephemeral checkout from the current canonical task head, restores/builds/tests every discovered .NET project, runs Worker-5 release/update integrity tests, and emits `artifacts/release/integrated-readiness.json`.

The build driver intentionally builds every discovered `src/**/*.csproj` even when a solution exists. Parallel Worker integration can make the solution file lag the canonical tree; WPF, Browser, Infrastructure, PCC/GitHub or Updater therefore cannot be silently excluded from CI.

Current Browser packaging is governed by Worker 3's `SYSTEM_CHROME_CDP` implementation: Microsoft.Playwright 1.62.0 driver assets remain part of the .NET output, PCC Executive locates an installed Google Chrome executable and attaches over CDP, Playwright-managed Chromium installation is not currently required, and no personal or PCC-owned profile data is packaged.

The current canonical branch contains Domain, Application including Manager orchestration, PCC, GitHub, Browser, WPF App/service binding, Infrastructure, Updater and Installer content. SQLite schema target is explicitly `1`. The rehearsal therefore attempts the real self-contained Setup EXE, fresh install/launch, first-run durable-state initialization, standalone persistence smoke and default uninstall preservation. Any integration gap is reported as `FAIL`, never as PASS.
