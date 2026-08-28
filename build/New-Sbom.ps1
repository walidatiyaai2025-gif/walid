[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [string]$OutputPath,
    [string]$InstallerCompilerPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path }
$RepositoryRoot = (Resolve-Path $RepositoryRoot).Path
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $RepositoryRoot 'artifacts/release/sbom.json' }

$sourceSha = (& git -C $RepositoryRoot rev-parse HEAD 2>$null).Trim().ToLowerInvariant()
if ($sourceSha -notmatch '^[0-9a-f]{40}$') { throw "SBOM requires exact source SHA; got '$sourceSha'." }
$dotnetSdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Unable to establish .NET SDK version.' }

$packages = @()
$frameworks = @()
foreach ($project in Get-ChildItem (Join-Path $RepositoryRoot 'src') -Recurse -File -Filter '*.csproj' -ErrorAction SilentlyContinue | Sort-Object FullName) {
    [xml]$xml = Get-Content $project.FullName -Raw
    $relativeProject = [IO.Path]::GetRelativePath($RepositoryRoot, $project.FullName).Replace('\\','/')

    foreach ($node in @($xml.SelectNodes('//TargetFramework'))) {
        if ($node -and -not [string]::IsNullOrWhiteSpace($node.InnerText)) {
            $frameworks += [pscustomobject]@{ project=$relativeProject; targetFramework=$node.InnerText.Trim() }
        }
    }
    foreach ($node in @($xml.SelectNodes('//TargetFrameworks'))) {
        if ($node -and -not [string]::IsNullOrWhiteSpace($node.InnerText)) {
            foreach ($tfm in ($node.InnerText -split ';')) {
                if (-not [string]::IsNullOrWhiteSpace($tfm)) { $frameworks += [pscustomobject]@{ project=$relativeProject; targetFramework=$tfm.Trim() } }
            }
        }
    }

    foreach ($reference in @($xml.SelectNodes('//PackageReference'))) {
        if (-not $reference) { continue }
        $name = $reference.GetAttribute('Include')
        if ([string]::IsNullOrWhiteSpace($name)) { $name = $reference.GetAttribute('Update') }
        $packageVersion = $reference.GetAttribute('Version')
        if ([string]::IsNullOrWhiteSpace($packageVersion)) {
            $versionNode = $reference.SelectSingleNode('Version')
            if ($versionNode) { $packageVersion = $versionNode.InnerText.Trim() }
        }
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            $packages += [pscustomobject]@{ project=$relativeProject; name=$name; version=$packageVersion }
        }
    }
}

$installerVersion = 'NOT_PRESENT'
if (-not [string]::IsNullOrWhiteSpace($InstallerCompilerPath) -and (Test-Path $InstallerCompilerPath)) {
    $installerVersion = (Get-Item $InstallerCompilerPath).VersionInfo.FileVersion
}

$version = (Get-Content (Join-Path $RepositoryRoot 'VERSION') -Raw).Trim()
$sbom = [ordered]@{
    schema = 'PCCEXECUTIVE-SBOM-1'
    product = 'PCC Executive'
    version = $version
    sourceSha = $sourceSha
    target = 'win-x64'
    dotnetSdk = $dotnetSdk
    installerTool = [ordered]@{ name='Inno Setup'; version=$installerVersion }
    projectFrameworks = @($frameworks | Sort-Object project,targetFramework)
    nugetDependencies = @($packages | Sort-Object name,version,project)
}

$parent = Split-Path $OutputPath -Parent
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$sbom | ConvertTo-Json -Depth 8 | Set-Content $OutputPath -Encoding UTF8
Write-Host "SBOM=$OutputPath"
