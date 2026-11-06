param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$ExpectedManifestSha256,
    [Parameter(Mandatory)][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [switch]$WindowSmoke,
    [switch]$D3D12Smoke
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($ExpectedManifestSha256 -notmatch '^[a-fA-F0-9]{64}$' -or $ExpectedSourceCommit -notmatch '^[a-fA-F0-9]{40}$') {
    throw 'Expected manifest and source SHA values must be complete.'
}
if ($ExpectedVersion -notmatch '^0\.1\.0-dev\.[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*$') { throw 'Expected exact version is invalid.' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not (Test-Path -LiteralPath $ArtifactDirectory -PathType Container)) { throw 'Groundwork package archive is unavailable.' }
$artifactRoot = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$expectedIds = @('Groundwork.Core','Groundwork.Platform.Windows','Groundwork.Simulation','Groundwork.Ecs',
    'Groundwork.Jobs','Groundwork.Diagnostics','Groundwork.Rendering','Groundwork.Rendering.D3D12')

function Read-VerifiedArchive([string]$Directory, [string]$Version) {
    $manifestPath = Join-Path $Directory 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Missing CI package manifest.' }
    $actualManifest = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
    if ($actualManifest -ine $ExpectedManifestSha256) { throw 'Manifest does not match independently pinned SHA-256.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.sourceCommit -ine $ExpectedSourceCommit) { throw 'Unexpected producer commit or schema.' }
    $entries = @($manifest.packages)
    if ($entries.Count -ne $expectedIds.Count) { throw "Expected $($expectedIds.Count) package entries." }
    $seen = @()
    foreach ($package in $entries) {
        $id = [string]$package.packageId
        if ($expectedIds -cnotcontains $id -or $seen -ccontains $id) { throw "Unexpected or duplicate package identity: $id" }
        $seen += $id
        if ([string]$package.packageVersion -cne $Version) { throw "Unexpected package version for $id" }
        $name = "$id.$Version.nupkg"
        if ([string]$package.filename -cne $name -or [string]$package.sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw "Invalid manifest package fields for $id" }
        $candidate = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "Missing CI package: $name" }
        if ((Get-Item -LiteralPath $candidate).Length -ne [long]$package.sizeBytes) { throw "Wrong package length: $name" }
        if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ine [string]$package.sha256) { throw "Wrong package digest: $name" }
    }
    if (@(Get-ChildItem -LiteralPath $Directory -Filter '*.nupkg' -File).Count -ne $expectedIds.Count) { throw 'Unexpected number of CI package files.' }
    return $entries
}

function Assert-ArchiveRejected([string]$Directory, [string]$Version, [string]$ExpectedMessage) {
    try { $null = Read-VerifiedArchive $Directory $Version }
    catch {
        if (-not $_.Exception.Message.StartsWith($ExpectedMessage, [StringComparison]::Ordinal)) { throw }
        Write-Host "PASS: $($_.Exception.Message)"
        return
    }
    throw "Invalid archive accepted. Expected rejection: $ExpectedMessage"
}

function Invoke-HostSmoke([string]$Assembly, [string]$Mode, [string]$PassLine) {
    $start = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.ArgumentList.Add($Assembly)
    $start.ArgumentList.Add($Mode)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            throw "WYU $Mode exceeded 30 seconds."
        }
        $output = $stdout.GetAwaiter().GetResult()
        $errors = $stderr.GetAwaiter().GetResult()
        Write-Host $output
        if ($process.ExitCode -ne 0 -or ($output -split '\r?\n') -cnotcontains $PassLine) {
            throw "WYU $Mode failed with exit $($process.ExitCode). $errors"
        }
    } finally { $process.Dispose() }
}

$entries = @(Read-VerifiedArchive $artifactRoot $ExpectedVersion)
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'src/WorldsYetUnseen.Client/WorldsYetUnseen.Client.csproj') -Raw
$references = @($project.SelectNodes('//PackageReference'))
if ($references.Count -ne $expectedIds.Count) { throw 'WYU must explicitly pin every approved Groundwork package.' }
foreach ($id in $expectedIds) {
    $pin = @($references | Where-Object { $_.GetAttribute('Include') -ceq $id })
    if ($pin.Count -ne 1 -or $pin[0].GetAttribute('Version') -cne "[$ExpectedVersion]") {
        throw "WYU must explicitly pin $id to [$ExpectedVersion] before consuming this archive."
    }
}

$tempRoot = [IO.Path]::TrimEndingDirectorySeparator((Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path)
$work = Join-Path $tempRoot ('wyu-verified-consumer-' + [guid]::NewGuid().ToString('N'))
$feed = Join-Path $work 'feed'
$cache = Join-Path $work 'packages'
$fixture = Join-Path $work 'invalid-archive'
New-Item -ItemType Directory -Path $work,$feed,$cache,$fixture | Out-Null
$oldFeed = $env:GROUNDWORK_NUGET_FEED
$oldPackages = $env:NUGET_PACKAGES
try {
    $fixtureManifest = Join-Path $fixture 'manifest.json'
    Copy-Item -LiteralPath (Join-Path $artifactRoot 'manifest.json') -Destination $fixtureManifest
    Assert-ArchiveRejected $fixture "$ExpectedVersion.incompatible" 'Unexpected package version'
    Assert-ArchiveRejected $fixture $ExpectedVersion 'Missing CI package:'
    $first = $entries[0]
    $firstPath = Join-Path $fixture $first.filename
    $bytes = [IO.File]::ReadAllBytes((Join-Path $artifactRoot $first.filename))
    $bytes[0] = $bytes[0] -bxor 1
    [IO.File]::WriteAllBytes($firstPath, $bytes)
    Assert-ArchiveRejected $fixture $ExpectedVersion 'Wrong package digest:'
    foreach ($package in $entries) {
        $id = ([string]$package.packageId).ToLowerInvariant()
        $versionDirectory = Join-Path (Join-Path $feed $id) $ExpectedVersion.ToLowerInvariant()
        New-Item -ItemType Directory -Path $versionDirectory -Force | Out-Null
        $dest = Join-Path $versionDirectory ([string]$package.filename).ToLowerInvariant()
        Copy-Item -LiteralPath (Join-Path $artifactRoot $package.filename) -Destination $dest
        if ((Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash -ine [string]$package.sha256) { throw "Copied package changed: $($package.packageId)" }

        # Derive NuGet's hierarchical feed indexes from the verified archive, not external sidecars.
        $zip = [IO.Compression.ZipFile]::OpenRead($dest)
        try {
            $nuspec = @($zip.Entries | Where-Object { $_.FullName -ieq "$id.nuspec" })
            if ($nuspec.Count -ne 1) { throw "Missing or duplicate nuspec: $id" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($nuspec[0], (Join-Path $versionDirectory "$id.nuspec"))
        } finally { $zip.Dispose() }
        $sha512 = [Security.Cryptography.SHA512]::HashData([IO.File]::ReadAllBytes($dest))
        [IO.File]::WriteAllText("$dest.sha512", [Convert]::ToBase64String($sha512))
    }
    $env:GROUNDWORK_NUGET_FEED = $feed
    $env:NUGET_PACKAGES = $cache
    Push-Location $root
    try {
        & ./scripts/Verify-Bootstrap.ps1
        $assembly = Join-Path $root 'artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll'
        if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) { throw 'Missing WYU Release host assembly.' }
        Invoke-HostSmoke $assembly '--headless-smoke' 'PASS: WYU headless startup and shutdown.'
        Invoke-HostSmoke $assembly '--fault-smoke' 'PASS: WYU controlled fault shutdown.'
        if ($WindowSmoke) { Invoke-HostSmoke $assembly '--window-smoke' 'PASS: WYU Windows startup and shutdown.' }
        if ($D3D12Smoke) { Invoke-HostSmoke $assembly '--d3d12-smoke' 'PASS: WYU D3D12 Windows startup and shutdown.' }
        Write-Host "PASS: WYU consumed $($expectedIds.Count) verified Groundwork $ExpectedVersion packages with a fresh locked restore."
        $global:LASTEXITCODE = 0
    } finally { Pop-Location }
} finally {
    $env:GROUNDWORK_NUGET_FEED = $oldFeed
    $env:NUGET_PACKAGES = $oldPackages
    $resolvedWork = (Resolve-Path -LiteralPath $work).Path
    if ((Split-Path -LiteralPath $resolvedWork) -ne $tempRoot) { throw 'Refusing to remove a fixture outside the temporary directory.' }
    Remove-Item -LiteralPath $resolvedWork -Recurse -Force
}
