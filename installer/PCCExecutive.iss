; PCC Executive production installer foundation.
; VERSION and exact source provenance are injected by build/Package.ps1.

#ifndef MyAppVersion
  #error MyAppVersion must be supplied by the package build.
#endif
#ifndef MyFileVersion
  #error MyFileVersion must be supplied by the package build.
#endif
#ifndef SourceDir
  #error SourceDir must be supplied by the package build.
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by the package build.
#endif
#ifndef SourceSha
  #error SourceSha must be supplied by the package build.
#endif

#define MyAppName "PCC Executive"
#define MyAppExeName "PCCExecutive.exe"
#define MyPublisher "PCC Executive"
#define MyAppId "{{B71D3FB5-9628-4D1D-9788-3AB64A846873}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyPublisher}
VersionInfoDescription=PCC Executive Windows Installer
VersionInfoProductName=PCC Executive
VersionInfoVersion={#MyFileVersion}
VersionInfoTextVersion={#MyAppVersion}
DefaultDirName={autopf}\PCC Executive
DefaultGroupName=PCC Executive
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=PCCExecutive-{#MyAppVersion}-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UsePreviousAppDir=yes
UsePreviousTasks=yes
CloseApplications=yes
CloseApplicationsFilter=PCCExecutive.exe
RestartApplications=no
SetupLogging=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName=PCC Executive {#MyAppVersion}
AppMutex=PCCExecutive.Application.Singleton
MinVersion=10.0.17763

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.runtimeconfig.dev.json"

[Icons]
Name: "{autoprograms}\PCC Executive"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\PCC Executive"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\PCC Executive"; ValueType: string; ValueName: "Version"; ValueData: "{#MyAppVersion}"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\PCC Executive"; ValueType: string; ValueName: "SourceSha"; ValueData: "{#SourceSha}"; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\PCC Executive"; ValueType: string; ValueName: "InstallRoot"; ValueData: "{app}"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch PCC Executive"; Flags: nowait postinstall skipifsilent

[Code]
var
  UpgradeBackupRoot: String;
  UpgradeAttemptId: String;

function DataRoot(): String;
begin
  Result := ExpandConstant('{localappdata}\PCC Executive');
end;

function InstallerCacheDir(): String;
begin
  Result := DataRoot() + '\InstallerCache';
end;

function HasCommandLineSwitch(const SwitchName: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
  begin
    if CompareText(ParamStr(I), SwitchName) = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function ExistingInstallDetected(): Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\{#MyAppExeName}'));
end;

function IsSameVersionRepair(): Boolean;
var
  ExistingVersion: String;
begin
  Result :=
    RegQueryStringValue(HKA, 'Software\PCC Executive', 'Version', ExistingVersion) and
    (CompareText(Trim(ExistingVersion), '{#MyAppVersion}') = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  UpdaterPath, BackupRoot, Params: String;
  ExitCode: Integer;
begin
  Result := '';
  if not ExistingInstallDetected() then
    Exit;

  { A same-version reinstall is a repair, not a schema/version upgrade. The durable
    data root is outside {app}, so replacing application files does not delete it.
    Do not invoke the already-installed updater here: older 0.1.0 builds launch the
    normal WPF app for --update-control, which collides with the running singleton
    and can never create checkpoint.json. Inno's CloseApplications boundary will
    close PCCExecutive.exe before files are replaced. }
  if IsSameVersionRepair() then
  begin
    UpgradeBackupRoot := '';
    UpgradeAttemptId := '';
    Log('Same-version PCC Executive repair detected; preserving durable data and skipping cross-version checkpoint handshake.');
    Exit;
  end;

  UpdaterPath := ExpandConstant('{app}\updater\PCCExecutive.Updater.exe');
  if not FileExists(UpdaterPath) then
  begin
    Result :=
      'An existing PCC Executive installation was detected, but its safe-upgrade helper is missing.' + #13#10 +
      'Setup stopped before replacing application files. User data has not been deleted.';
    Exit;
  end;

  UpgradeAttemptId :=
    'installer-' + GetDateTimeString('yyyymmdd-hhnnss', '-', ':');
  BackupRoot := DataRoot() + '\Backups\' + UpgradeAttemptId;
  UpgradeBackupRoot := BackupRoot;

  Params :=
    'prepare-installer-upgrade --backup-root "' + BackupRoot +
    '" --attempt "' + UpgradeAttemptId + '"';

  if (not Exec(UpdaterPath, Params, '', SW_HIDE, ewWaitUntilTerminated, ExitCode)) or
     (ExitCode <> 0) then
  begin
    UpgradeBackupRoot := '';
    Result :=
      'PCC Executive could not create a safe upgrade checkpoint.' + #13#10 +
      'Setup stopped before replacing application files. See the updater/recovery log for details.';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  CacheDir, CachePath, UpdaterPath, VerifyParams: String;
  ExitCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if UpgradeBackupRoot <> '' then
    begin
      UpdaterPath := ExpandConstant('{app}\updater\PCCExecutive.Updater.exe');
      if not FileExists(UpdaterPath) then
        RaiseException(
          'Upgrade files were installed but the new updater helper is missing. ' +
          'Recovery checkpoint: ' + UpgradeBackupRoot
        );

      VerifyParams :=
        'post-install-verify --backup-root "' + UpgradeBackupRoot +
        '" --attempt "' + UpgradeAttemptId + '"';

      if (not Exec(UpdaterPath, VerifyParams, '', SW_HIDE, ewWaitUntilTerminated, ExitCode)) or
         (ExitCode <> 0) then
        RaiseException(
          'Upgrade migration/startup health verification failed. ' +
          'The recovery checkpoint was preserved at: ' + UpgradeBackupRoot
        );
    end;

    CacheDir := InstallerCacheDir();
    ForceDirectories(CacheDir);
    CachePath := CacheDir + '\PCCExecutive-{#MyAppVersion}-Setup-x64.exe';
    if not FileCopy(ExpandConstant('{srcexe}'), CachePath, False) then
      Log('WARNING: unable to cache installer for future rollback: ' + CachePath);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  FullCleanup: Boolean;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  FullCleanup := HasCommandLineSwitch('/FULLCLEANUP=1');

  if (not UninstallSilent) and (not FullCleanup) then
  begin
    FullCleanup :=
      MsgBox(
        'PCC Executive application files were removed.' + #13#10 + #13#10 +
        'Keep projects, history, settings, SQLite data, checkpoints and installer rollback cache?' + #13#10 +
        'Choose Yes to KEEP DATA (recommended), or No for FULL CLEANUP.',
        mbConfirmation, MB_YESNO
      ) = IDNO;
  end;

  if FullCleanup then
  begin
    Log('FULL CLEANUP explicitly selected; deleting durable user data root.');
    DelTree(DataRoot(), True, True, True);
  end
  else
    Log('Preserving durable PCC Executive user data.');
end;