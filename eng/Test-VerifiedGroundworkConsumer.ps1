param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$ExpectedManifestSha256,
    [Parameter(Mandatory)][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][string]$ExpectedVersion
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($ExpectedManifestSha256 -notmatch '^[a-fA-F0-9]{64}$' -or $ExpectedSourceCommit -notmatch '^[a-fA-F0-9]{40}$') {
    throw 'Expected manifest and source SHA values must be complete.'
}
if ($ExpectedVersion -notmatch '^0\.1\.0-dev\.[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*$') { throw 'Expected exact version is invalid.' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactRoot = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$manifestPath = Join-Path $artifactRoot 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Missing CI package manifest.' }
$actualManifest = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
if ($actualManifest -ine $ExpectedManifestSha256) { throw 'Manifest does not match independently pinned SHA-256.' }
$m = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($m.schemaVersion -ne 1 -or $m.sourceCommit -ine $ExpectedSourceCommit) { throw 'Unexpected producer commit or schema.' }
$expectedIds = @('Groundwork.Core','Groundwork.Platform.Windows','Groundwork.Simulation','Groundwork.Ecs','Groundwork.Jobs','Groundwork.Diagnostics')
$entries = @($m.packages)
if ($entries.Count -ne $expectedIds.Count) { throw 'Expected six package entries.' }
$seen = @()
foreach ($p in $entries) {
    $id = [string]$p.packageId
    $version = [string]$p.packageVersion
    if ($expectedIds -cnotcontains $id -or $seen -ccontains $id) { throw "Unexpected/duplicate package identity: $id" }
    $seen += $id
    if ($version -cne $ExpectedVersion) { throw "Unexpected package version for $id" }
    $name = "$id.$version.nupkg"
    if ([string]$p.filename -cne $name -or [string]$p.sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw "Invalid manifest package fields for $id" }
    $candidate = Join-Path $artifactRoot $name
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "Missing CI package: $name" }
    if ((Get-Item -LiteralPath $candidate).Length -ne [long]$p.sizeBytes) { throw "Wrong package length: $name" }
    if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ine [string]$p.sha256) { throw "Wrong package digest: $name" }
}
$files = @(Get-ChildItem -LiteralPath $artifactRoot -Filter '*.nupkg' -File)
if ($files.Count -ne $expectedIds.Count) { throw 'Unexpected number of CI package files.' }
$projectPath = Join-Path $root 'src/WorldsYetUnseen.Client/WorldsYetUnseen.Client.csproj'
$lockPath = Join-Path $root 'src/WorldsYetUnseen.Client/packages.lock.json'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$references = @($project.SelectNodes('//PackageReference[@Include="Groundwork.Core"]'))
if ($references.Count -ne 1 -or $references[0].GetAttribute('Version') -cne "[$ExpectedVersion]") {
    throw "WYU must explicitly pin [$ExpectedVersion] before consuming this CI artifact."
}
if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) { throw 'Committed NuGet lockfile is missing.' }
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$framework = $lock.dependencies.PSObject.Properties | Where-Object { $_.Name -like 'net10.0-windows*' } | Select-Object -First 1
if ($null -eq $framework) { throw 'Unexpected NuGet lock framework.' }
$locked = $framework.Value.'Groundwork.Core'
if ($null -eq $locked -or [string]$locked.resolved -cne $ExpectedVersion -or [string]$locked.requested -cne "[$ExpectedVersion, $ExpectedVersion]") {
    throw 'WYU lockfile does not match the exact CI package version.'
}
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('wyu-verified-consumer-' + [guid]::NewGuid().ToString('N'))
$feed = Join-Path $work 'feed'
$cache = Join-Path $work 'packages'
New-Item -ItemType Directory -Path $feed,$cache -Force | Out-Null
$oldFeed = $env:GROUNDWORK_NUGET_FEED
$oldPackages = $env:NUGET_PACKAGES
try {
    $core = $entries | Where-Object { $_.packageId -ceq 'Groundwork.Core' } | Select-Object -First 1
    $from = Join-Path $artifactRoot $core.filename
    $dest = Join-Path $feed $core.filename
    Copy-Item -LiteralPath $from -Destination $dest
    if ((Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash -ine [string]$core.sha256) { throw 'Copied package changed.' }
    $env:GROUNDWORK_NUGET_FEED = $feed
    $env:NUGET_PACKAGES = $cache
    Push-Location $root
    try {
        & dotnet restore 'WorldsYetUnseen.slnx' --configfile 'NuGet.Config' --locked-mode --force-evaluate
        if ($LASTEXITCODE -ne 0) { throw 'Fresh-cache locked WYU restore failed.' }
        & dotnet build 'WorldsYetUnseen.slnx' -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'WYU Release build failed.' }
        Write-Host "PASS: WYU consumed verified Groundwork.Core $ExpectedVersion from isolated CI artifact."
        $global:LASTEXITCODE = 0
    } finally { Pop-Location }
} finally {
    $env:GROUNDWORK_NUGET_FEED = $oldFeed
    $env:NUGET_PACKAGES = $oldPackages
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
