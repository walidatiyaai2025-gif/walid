# Integrated Build Rehearsal

This Wave-2 release/build layer validates exact Worker-chain heads without taking canonical integration authority away from the PCC Executive Integration Lead.

`release/integration-rehearsal.json` records the live chain snapshot used by the rehearsal. `Get-IntegrationMatrix.ps1` reports `FOUND`, `PENDING_PR`, `BLOCKED`, or `INCOMPATIBLE`. The workflow validates each current Worker chain at its exact SHA, preserves source-head failures as evidence, then validates the current rehearsal candidate at its own exact SHA so a later dependency/build bridge can be proven rather than hidden.

The canonical build driver builds every discovered `src/**/*.csproj` even when a solution exists and executes the integrated WPF `--smoke-test`. Parallel Worker integration therefore cannot silently disappear behind a stale solution file.

Current Browser packaging is governed by Worker 3's `SYSTEM_CHROME_CDP` implementation: Microsoft.Playwright 1.62.0 driver assets remain part of the .NET output, PCC Executive locates an installed Google Chrome executable and attaches over CDP, Playwright-managed Chromium installation is not currently required, and no personal or PCC-owned profile data is packaged.

The current canonical branch contains Domain, Application/Manager orchestration, PCC, GitHub, Browser, WPF App/service binding, Infrastructure, Updater and Installer content. SQLite schema target is `1`; the native bundle vulnerability detected by exact-head validation is overridden in canonical and the rehearsal branch uses current patched dependency versions. The rehearsal attempts the real self-contained Setup EXE, fresh install/launch, first-run durable-state initialization, standalone persistence smoke and default uninstall preservation. Any remaining integration gap is reported as `FAIL`, never as PASS.
