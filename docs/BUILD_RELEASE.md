# PCC Executive Build, Installer, Update and Provenance

## Canonical version

Root `VERSION` is the only product version authority. Development baseline is currently `0.1.0`.

Every package candidate must carry:

- repository;
- task;
- product version;
- exact source SHA;
- CI workflow/run/build identity;
- target architecture;
- UTC generation timestamp;
- package identity;
- SHA-256 artifact digest.

Authoritative release candidates are never named `latest`, `final`, or other mutable aliases.

## Deterministic CI

`.github/workflows/windows-ci.yml` uses Windows + .NET 10 and dynamically discovers the current solution/project set. Once `src/PCCExecutive.App/PCCExecutive.App.csproj` exists, build and all normal test projects become mandatory. Live ChatGPT Web is excluded from normal hosted CI; deterministic Browser adapter fixtures belong in normal test projects.

Before product source is integrated, CI reports the product build/package lane as dependency-blocked rather than fabricating a placeholder application or installer.

## Installer

`build/Package.ps1` publishes x64 self-contained application binaries, rejects browser profile/session material, writes installed build provenance, compiles the Inno Setup GUI installer, hashes it, and writes the external update manifest.

Expected artifact:

`PCCExecutive-<VERSION>-Setup-x64.exe`

The installer supports Start Menu shortcut, optional Desktop shortcut, launch-after-install, installed version/source metadata, standard uninstall, previous install-directory reuse and explicit x64 targeting.

Durable data is outside the application binary directory. Default uninstall preserves it.

## Upgrade / rollback

Update Center integration must use the verified staging and safe-upgrade boundary under `updater/`.

The persistence worker owns SQLite-safe checkpoint/backup/migration implementation. Installer/updater infrastructure requires that contract and intentionally refuses an unsafe overwrite if an installed upgrade helper cannot produce a checkpoint.

Every successful setup caches its installer in the durable data area. That enables binary rollback to the previous immutable installer when a later package fails migration/startup health. Persistence rollback is then delegated to the previous updater helper using the saved checkpoint.

## Smoke gates

Executable smoke scripts cover:

- fresh install;
- upgrade with seeded durable data;
- injected failed-upgrade rollback;
- uninstall with preserve-data default and explicit full cleanup;
- package manifest/hash/source verification.

Upgrade and failed-upgrade smoke require two real integrated installer versions. They cannot be truthfully executed until an older application installer and the persistence/updater CLI contracts exist.
