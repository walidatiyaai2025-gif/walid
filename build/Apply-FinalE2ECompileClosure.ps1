$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$utf8 = [Text.UTF8Encoding]::new($false)

$e2ePath = Join-Path $root 'tests/PCCExecutive.E2E/Final32StageE2ETests.cs'
$e2e = [IO.File]::ReadAllText($e2ePath)
if ($e2e.Contains('private static WorkerTask Task(')) {
    $e2e = [regex]::Replace($e2e, '(?<![\w.])Task\(', 'WorkerTaskFor(')
}
$e2e = $e2e.Replace('WorkerTaskFor(TaskId.New(), "missing dependency", "tests/dependency", [TaskId.New()])', 'WorkerTaskFor(TaskId.New(), "missing dependency", "tests/dependency", new HashSet<TaskId> { TaskId.New() })')
$e2e = $e2e.Replace('WorkerTaskFor(TaskId.New(), "overlap a", "tests/shared", [])', 'WorkerTaskFor(TaskId.New(), "overlap a", "tests/shared", new HashSet<TaskId>())')
$e2e = $e2e.Replace('WorkerTaskFor(TaskId.New(), "overlap b", "tests/shared/child", [])', 'WorkerTaskFor(TaskId.New(), "overlap b", "tests/shared/child", new HashSet<TaskId>())')
if ($e2e.Contains('private static WorkerTask Task(')) { throw 'E2E Task helper still shadows System.Threading.Tasks.Task.' }
if (-not $e2e.Contains('private static WorkerTask WorkerTaskFor(')) { throw 'E2E WorkerTaskFor helper not found.' }
[IO.File]::WriteAllText($e2ePath, $e2e, $utf8)

$compositionPath = Join-Path $root 'tests/PCCExecutive.E2E/ProductionRuntimeHostCompositionTests.cs'
$composition = [IO.File]::ReadAllText($compositionPath)
$composition = $composition.Replace('var snapshot = await host.SnapshotAsync();', 'var snapshot = host.Snapshot;')
if ($composition.Contains('SnapshotAsync()')) { throw 'Stale PccExecutiveRuntimeHost SnapshotAsync call remains.' }
if (-not $composition.Contains('var snapshot = host.Snapshot;')) { throw 'Production host Snapshot composition assertion missing.' }
[IO.File]::WriteAllText($compositionPath, $composition, $utf8)

Write-Host 'Final E2E compile reconciliation applied.'
