# PCC Executive Release Hardening

This layer stacks on Worker 5 PR #6 and intentionally does not fabricate a v0.1.0 installer while the integrated WPF application and persistence/update contracts are missing.

## Canonical publish

`build/Publish-Windows.ps1` is the explicit win-x64 production publish boundary. It requires `PCCExecutive.App`, publishes .NET 10 self-contained, disables trimming/single-file assumptions for WPF safety, removes debug symbols, validates the payload, and records the exact source SHA and application hash.

## Release identity

A release candidate manifest must include Product, Version, SourceSha, BuildId, WorkflowRun, Target, Runtime, SelfContained, InstallerFile, InstallerSha256, ApplicationFileHash, GeneratedAt, DatabaseSchemaTarget, MinimumUpgradeVersion, SigningState and SbomReference.

## Signing

No certificate is committed or invented. `build/Sign-Release.ps1` consumes only a certificate thumbprint already provisioned securely on the Windows runner (`PCCEXECUTIVE_SIGNING_CERT_SHA1`) and an optional timestamp URL. Unsigned states remain explicit: `UNSIGNED_DEV` or `SIGNING_NOT_CONFIGURED`. Invalid signatures are fatal.

## Supply-chain evidence

`build/New-Sbom.ps1` records exact source SHA, .NET SDK, target frameworks, NuGet PackageReferences and the discovered Inno Setup compiler version. `build/Test-ReleasePayload.ps1` rejects browser profiles/session files, common auth-state/secret files, SQLite user data, source/debug files and developer logs.

## Release readiness

`build/Get-ReleaseReadiness.ps1` reports required modules as FOUND/MISSING and gates as PASS/FAIL/PENDING/BLOCKED_DEPENDENCY/NOT_APPLICABLE. Gate evidence is exact-head bound through `build/Write-GateEvidence.ps1`. ProductionCandidate mode cannot translate missing modules or missing evidence into PASS.

## Installer visual boundary

Installer correctness remains independent from Worker 4 visual ownership. Future approved setup graphics belong under `installer/theme/` and may be wired into Inno Setup after integration without changing update, hashing, provenance or data-preservation semantics.
