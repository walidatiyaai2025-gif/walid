$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'Apply-CanonicalConversationIdentityClosureV2.ps1'
$text = [IO.File]::ReadAllText($source)
$text = $text.Replace('throw "REGEX_ANCHOR:$l:$($m.Count)"','throw "REGEX_ANCHOR:${l}:$($m.Count)"')
$temp = Join-Path $env:RUNNER_TEMP 'Apply-CanonicalConversationIdentityClosureV2.fixed.ps1'
[IO.File]::WriteAllText($temp, $text, [Text.UTF8Encoding]::new($false))
& $temp
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
