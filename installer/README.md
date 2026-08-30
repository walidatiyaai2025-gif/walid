# PCC Executive Installer

The installer is an Inno Setup 6+ x64 GUI setup driven from the canonical root `VERSION`.

Build entrypoint:

```powershell
./build/Package.ps1
```

Expected artifact:

`PCCExecutive-<VERSION>-Setup-x64.exe`

## Data law

Application binaries install under Program Files by default. Durable state belongs outside the application directory under the Windows user data root, currently expected at:

`%LOCALAPPDATA%\PCC Executive\`

The installer does not package or ship project databases, browser profiles, ChatGPT sessions, cookies, API keys, or credentials.

Uninstall defaults to **KEEP DATA**. Full cleanup requires an explicit GUI choice or `/FULLCLEANUP=1`.

## Upgrade contract

A future in-place installer refuses to overwrite an existing PCC Executive installation unless the installed version exposes:

`<InstallRoot>\updater\PCCExecutive.Updater.exe prepare-installer-upgrade --backup-root <path>`

That helper must safely checkpoint the active runtime and coordinate SQLite backup/migration state with the persistence layer. This prevents the installer from guessing how to copy a live SQLite/WAL database.

Every successfully installed setup caches its own installer under the durable data root so the updater has a previous known binary installer available for rollback.

The Update Center should use `updater/Stage-Update.ps1` and the updater executable/script contract rather than launching unverified downloads.

Direct in-place setup upgrades run the installed helper before file replacement and the newly installed helper again after replacement for migration/startup health verification. If that verification fails, setup surfaces the exact preserved checkpoint; Update Center orchestration can use the cached previous installer plus that checkpoint for recovery.
