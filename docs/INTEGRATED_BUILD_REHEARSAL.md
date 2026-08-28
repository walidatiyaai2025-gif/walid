# Integrated Build Rehearsal

This Wave-2 release/build layer validates exact Worker-chain heads without taking canonical integration authority away from the PCC Executive Integration Lead.

`release/integration-rehearsal.json` records the live chain snapshot used by the rehearsal. `Get-IntegrationMatrix.ps1` reports `FOUND`, `PENDING_PR`, `BLOCKED`, or `INCOMPATIBLE`. The workflow validates each consumable chain at its exact SHA, creates an ephemeral convergence checkout from the canonical task baseline, merges only the configured available child heads, restores/builds/tests every discovered .NET project, runs Worker-5 release/update integrity tests, and emits `artifacts/release/integrated-readiness.json`.

The ephemeral convergence SHA is validation evidence only. It is not the canonical task branch and is not a release source until the Integration Lead performs and accepts canonical integration.

Current Browser packaging is governed by Worker 3's `SYSTEM_CHROME_CDP` implementation: Microsoft.Playwright 1.62.0 driver assets remain part of the .NET output, PCC Executive locates an installed Google Chrome executable and attaches over CDP, Playwright-managed Chromium installation is not currently required, and no personal or PCC-owned profile data is packaged.

When `PCCExecutive.App` and `PCCExecutive.Infrastructure` become available, the same rehearsal stops treating packaging as optional: database schema identity must be resolved, the real self-contained Setup EXE is built, package verification runs, fresh install/launch smoke runs, and uninstall smoke runs. Missing required product modules remain `BLOCKED_DEPENDENCY`; they are never translated to PASS.
