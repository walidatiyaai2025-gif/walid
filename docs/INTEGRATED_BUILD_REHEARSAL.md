# Integrated Build Rehearsal

This Wave-2 release/build layer validates exact Worker-chain heads without taking canonical integration authority away from the PCC Executive Integration Lead.

`release/integration-rehearsal.json` records the live chain snapshot used by the rehearsal. `Get-IntegrationMatrix.ps1` reports `FOUND`, `PENDING_PR`, `BLOCKED`, or `INCOMPATIBLE`. The workflow validates each current consumable chain at its exact SHA, creates an ephemeral checkout from the current canonical task head, merges only explicitly accepted pending heads, restores/builds/tests every discovered .NET project, runs Worker-5 release/update integrity tests, and emits `artifacts/release/integrated-readiness.json`.

The ephemeral convergence SHA is validation evidence only. It is not the canonical task branch and is not a release source until the Integration Lead accepts the exact canonical source.

Current Browser packaging is governed by Worker 3's `SYSTEM_CHROME_CDP` implementation: Microsoft.Playwright 1.62.0 driver assets remain part of the .NET output, PCC Executive locates an installed Google Chrome executable and attaches over CDP, Playwright-managed Chromium installation is not currently required, and no personal or PCC-owned profile data is packaged.

The canonical branch now contains Domain, Application, PCC, GitHub, Browser, WPF App, Updater and Installer content. Worker 2 persistence/Infrastructure is still absent, so `databaseSchemaTarget` remains unresolved and the real Setup EXE/install/DB smoke path remains `BLOCKED_DEPENDENCY`. Once Infrastructure lands, the rehearsal stops treating packaging as blocked: schema identity must be resolved, the real self-contained Setup EXE is built, package verification runs, fresh install/launch smoke runs, database persistence smoke is required where the Worker 2 contract supplies it, and uninstall smoke runs. Missing required product modules are never translated to PASS.
