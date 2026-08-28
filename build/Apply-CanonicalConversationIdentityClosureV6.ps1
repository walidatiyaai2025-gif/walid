$ErrorActionPreference = 'Stop'
$v4 = Join-Path $PSScriptRoot 'Apply-CanonicalConversationIdentityClosureV4.ps1'
$text = [IO.File]::ReadAllText($v4)
$text = $text.Replace('BrowserSessionReconciliationOutcome.Matched','BrowserReconciliationKind.MATCHED')
$old = '        var workerSlotId = candidateRuntime.WorkerSlotId is null ? null : new WorkerSlotId(int.Parse(candidateRuntime.WorkerSlotId));'
$new = '        WorkerSlotId? workerSlotId = candidateRuntime.WorkerSlotId is null ? null : new WorkerSlotId(int.Parse(candidateRuntime.WorkerSlotId));'
if (-not $text.Contains($old)) { throw 'WORKER_SLOT_NULLABLE_PATCH_ANCHOR_MISSING' }
$text = $text.Replace($old,$new)
[IO.File]::WriteAllText($v4,$text,[Text.UTF8Encoding]::new($false))
& $v4
