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
    foreach ($group in $xml.Project.PropertyGroup) {
        if ($group.TargetFramework) { $frameworks += [pscustomobject]@{ project=$relativeProject; targetFramework=[string]$group.TargetFramework } }
        if ($group.TargetFrameworks) {
            foreach ($tfm in ([string]$group.TargetFrameworks -split ';')) { $frameworks += [pscustomobject]@{ project=$relativeProject; targetFramework=$tfm } }
        }
    }
    foreach ($itemGroup in $xml.Project.ItemGroup) {
        foreach ($reference in $itemGroup.PackageReference) {
            if ($null -eq $reference) { continue }
            $name = [string]$reference.Include
            if ([string]::IsNullOrWhiteSpace($name)) { $name = [string]$reference.Update }
            $version = [string]$reference.Version
            if ([string]::IsNullOrWhiteSpace($version) -and $reference.ChildNodes) {
                $versionNode = $reference.ChildNodes | Where-Object Name -eq 'Version' | Select-Object -First 1
                if ($versionNode) { $version = [string]$versionNode.InnerText }
            }
            if (-not [string]::IsNullOrWhiteSpace($name)) {
                $packages += [pscustomobject]@{ project=$relativeProject; name=$name; version=$version }
            }
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
