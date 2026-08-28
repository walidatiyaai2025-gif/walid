$ErrorActionPreference = 'Stop'
$v4 = Join-Path $PSScriptRoot 'Apply-CanonicalConversationIdentityClosureV4.ps1'
$text = [IO.File]::ReadAllText($v4)
$text = $text.Replace('BrowserSessionReconciliationOutcome.Matched','BrowserReconciliationKind.MATCHED')
[IO.File]::WriteAllText($v4,$text,[Text.UTF8Encoding]::new($false))
& $v4
