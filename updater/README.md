# PCC Executive Update Boundary

This directory defines the non-fake Update Center transaction boundary. It deliberately does not own browser logic, WPF screens, or SQLite schema design.

## State flow

`CHECK -> DOWNLOAD -> VERIFY -> VERIFIED_STAGED -> CHECKPOINT -> STOP OWNED RUNTIME -> INSTALL -> MIGRATE/HEALTH -> VERIFIED`

Failure flow:

`HEALTH/MIGRATION FAIL -> PREVIOUS INSTALLER -> RESTORE CHECKPOINT -> ROLLED_BACK`

`Stage-Update.ps1` refuses a package unless product, repository, task, semantic version, exact 40-character source SHA, architecture, package identity, filename and SHA-256 digest agree.

`Invoke-Upgrade.ps1` requires the installed updater helper to produce `checkpoint.json` before replacement. The persistence implementation owns how SQLite/WAL-safe backup and migration rollback work; this layer never copies a live database blindly.

## Updater executable contract

`src/PCCExecutive.Updater` implements the out-of-process helper and is packaged under `<InstallRoot>\updater\`.

Commands:

- `prepare-installer-upgrade --backup-root <path>`
- `prepare-update --backup-root <path> --attempt <id>`
- `post-install-verify --attempt <id> --backup-root <path>`
- `restore-update-checkpoint --backup-root <path> --attempt <id>`

The helper never force-kills the application. It forwards to the installed application's `--update-control prepare|verify|restore` boundary, verifies that a preparation checkpoint exists, and refuses installer replacement while `PCCExecutive` is still running.

The application/persistence integration must implement the `--update-control` boundary so preparation safely checkpoints orchestration state, coordinates shutdown, creates a SQLite/WAL-safe backup, and emits `<backup-root>\checkpoint.json`. Post-install verification must run required migrations and startup/schema health without requiring live ChatGPT Web.

The public update feed endpoint remains configurable for v0.1.0. The application may download a candidate from that configured feed, but installation must cross this verification boundary.
