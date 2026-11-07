param(
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$ExpectedManifestSha256,
    [Parameter(Mandatory)][string]$ExpectedSourceCommit,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [switch]$WindowSmoke,
    [switch]$D3D12Smoke,
    [switch]$TerrainSmoke,
    [switch]$WalkableSmoke,
    [switch]$PlanetSmoke,
    [switch]$FlightSmoke,
    [switch]$SaveSmoke,
    [switch]$PrototypeSmoke,
    [switch]$PrototypeSaveSmoke,
    [string]$InstallerCompiler,
    [string]$InstallerOutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([bool]$InstallerCompiler -ne [bool]$InstallerOutputDirectory) {
    throw 'Specify InstallerCompiler and InstallerOutputDirectory together.'
}
if ($InstallerCompiler) {
    $InstallerCompiler = (Resolve-Path -LiteralPath $InstallerCompiler).Path
    $InstallerOutputDirectory = [IO.Path]::GetFullPath($InstallerOutputDirectory)
}
if ($ExpectedManifestSha256 -notmatch '^[a-fA-F0-9]{64}$' -or $ExpectedSourceCommit -notmatch '^[a-fA-F0-9]{40}$') {
    throw 'Expected manifest and source SHA values must be complete.'
}
if ($ExpectedVersion -notmatch '^0\.1\.0-dev\.[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*$') { throw 'Expected exact version is invalid.' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not (Test-Path -LiteralPath $ArtifactDirectory -PathType Container)) { throw 'Groundwork package archive is unavailable.' }
$artifactRoot = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$expectedIds = @('Groundwork.Core','Groundwork.Platform.Windows','Groundwork.Simulation','Groundwork.Ecs',
    'Groundwork.Jobs','Groundwork.Diagnostics','Groundwork.Rendering','Groundwork.Rendering.D3D12','Groundwork.Voxels','Groundwork.Persistence')

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

function Start-HostSmoke([string]$Assembly, [string]$Mode, [string[]]$Arguments) {
    $start = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.ArgumentList.Add($Assembly)
    $start.ArgumentList.Add($Mode)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    [System.Diagnostics.Process]::Start($start)
}

function Invoke-HostSmoke([string]$Assembly, [string]$Mode, [string]$PassLine, [int]$TimeoutSeconds = 30,
        [string[]]$Arguments = @(), [switch]$Capture) {
    $process = Start-HostSmoke $Assembly $Mode $Arguments
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            [void]$process.WaitForExit(5000)
            throw "WYU $Mode exceeded $TimeoutSeconds seconds."
        }
        $output = $stdout.GetAwaiter().GetResult()
        $errors = $stderr.GetAwaiter().GetResult()
        Write-Host $output
        if ($process.ExitCode -ne 0 -or ($output -split '\r?\n') -cnotcontains $PassLine) {
            throw "WYU $Mode failed with exit $($process.ExitCode). $errors"
        }
        if ($Capture) { return $output -split '\r?\n' }
    } finally { $process.Dispose() }
}

function Invoke-FlightSmoke([string]$Assembly, [string]$Work) {
    $expectedTrace = 'WP09 trace v1 ticks=4332 sha256=7157DA3B7F3DB7C5B303BD4E28AEBE3A86A7D15156BAF431EE1278F3810CD230'
    $baseline = $null
    foreach ($run in 1, 2) {
        $slot = Join-Path $Work "flight-$run"
        $flight = @(Invoke-HostSmoke $Assembly '--flight-smoke' 'PASS: WYU WP09 flight mission and native shutdown.' `
            -TimeoutSeconds 160 -Arguments @($slot) -Capture)
        $reload = @(Invoke-HostSmoke $Assembly '--flight-reload-smoke' 'PASS: WYU WP09 landing checkpoint reload and native shutdown.' `
            -Arguments @($slot) -Capture)
        $saved = @($flight | Where-Object { $_ -cmatch '^WP09 saved generation=2 ' })
        $restored = @($reload | Where-Object { $_ -cmatch '^WP09 restored generation=2 ' })
        if ($saved.Count -ne 1 -or $restored.Count -ne 1 -or $saved[0].Replace('WP09 saved ', 'WP09 restored ') -cne $restored[0]) {
            throw "Flight $run reload did not reproduce the committed pose and canonical edits."
        }

        $checkpoints = @($flight | Where-Object { $_ -cmatch '^WP09 (checkpoint|orbit|trace|saved) ' })
        if ($checkpoints.Count -ne 12 -or $checkpoints -cnotcontains $expectedTrace) {
            throw "Flight $run did not match the pinned mission input trace."
        }
        $semanticTrace = $checkpoints -join "`n"
        if ($null -eq $baseline) { $baseline = $semanticTrace }
        elseif ($baseline -cne $semanticTrace) { throw 'WP09 semantic checkpoints differed between runs.' }
    }
    Write-Host 'PASS: Two WP09 flight traces and both fresh-process landing reloads matched.'
}

function Stop-HostAtSaveMarker([string]$Assembly, [string]$Mode, [string[]]$Arguments,
        [string]$Marker, [string]$MarkerSuffix, [scriptblock]$AssertBoundary) {
    $writer = Start-HostSmoke $Assembly $Mode $Arguments
    try {
        $stdout = $writer.StandardOutput.ReadToEndAsync()
        $stderr = $writer.StandardError.ReadToEndAsync()
        $elapsed = [System.Diagnostics.Stopwatch]::StartNew()
        $expectedMarker = "$($Arguments[1]) $($writer.Id)$MarkerSuffix"
        $ready = $false
        while (!$writer.HasExited -and $elapsed.Elapsed.TotalSeconds -lt 30) {
            if ((Test-Path -LiteralPath $marker -PathType Leaf) -and
                (Get-Content -LiteralPath $marker -Raw -ErrorAction SilentlyContinue) -ceq $expectedMarker) {
                $ready = $true
                break
            }
            Start-Sleep -Milliseconds 50
        }
        if ($writer.HasExited) {
            throw "WYU save writer exited before interruption. $($stdout.GetAwaiter().GetResult()) $($stderr.GetAwaiter().GetResult())"
        }
        if (!$ready) { throw "WYU save writer did not reach the expected boundary ($Mode)." }
        & $AssertBoundary
        $writer.Kill()
        if (!$writer.WaitForExit(5000)) { throw 'WYU save writer did not terminate.' }
    } finally {
        if (!$writer.HasExited) {
            $writer.Kill()
            [void]$writer.WaitForExit(5000)
        }
        $writer.Dispose()
    }

}

function Assert-SaveGeneration([string]$Path, [uint64]$Generation) {
    $frame = [System.IO.File]::ReadAllBytes($Path)
    if ($frame.Length -lt 24 -or [BitConverter]::ToUInt64($frame, 16) -ne $Generation) {
        throw "Save generation $Generation was not present in '$Path'."
    }
}

function Invoke-SaveSmoke([string]$Assembly, [string]$WorkDirectory) {
    $slot = Join-Path $WorkDirectory 'save-smoke'
    [void](New-Item -ItemType Directory -Path $slot)
    $token = [guid]::NewGuid().ToString('N')
    Set-Content -LiteralPath (Join-Path $slot 'wyu-save-smoke.fixture') -Value $token
    $arguments = @($slot, $token)
    Invoke-HostSmoke $Assembly '--save-smoke-init' 'PASS: WYU committed the initial edited terrain.' -Arguments $arguments

    $first = Join-Path $slot 'save.a'
    $second = Join-Path $slot 'save.b'
    $pending = Join-Path $slot 'save.pending'
    $marker = Join-Path $slot 'wyu-save-smoke.ready'
    $committedHash = (Get-FileHash -LiteralPath $first -Algorithm SHA256).Hash
    Stop-HostAtSaveMarker $Assembly '--save-smoke-interrupt' $arguments $marker '' {
        Assert-SaveGeneration $pending 2
        if (Test-Path -LiteralPath $second) { throw 'Generation 2 was already installed.' }
    }
    if ((Get-FileHash -LiteralPath $first -Algorithm SHA256).Hash -cne $committedHash -or (Test-Path -LiteralPath $second)) {
        throw 'The interruption changed the committed checkpoint.'
    }
    Write-Host 'PASS: Windows terminated the writer after generation 2 was flushed and before it was installed.'
    Invoke-HostSmoke $Assembly '--save-smoke-resume' 'PASS: WYU restored committed terrain, position and inventory after interrupted save.' -Arguments $arguments
}

function Invoke-PrototypeSaveSmoke([string]$Assembly, [string]$WorkDirectory) {
    $slot = Join-Path $WorkDirectory 'prototype-save-smoke'
    [void](New-Item -ItemType Directory -Path $slot)
    $drive = [System.IO.DriveInfo]::new([System.IO.Path]::GetPathRoot($slot))
    if ($drive.DriveType -ne [System.IO.DriveType]::Fixed -or $drive.DriveFormat -cne 'NTFS') {
        throw 'The WP07 interruption test requires a local NTFS fixture directory.'
    }

    $token = [guid]::NewGuid().ToString('N')
    Set-Content -LiteralPath (Join-Path $slot 'wyu-prototype-save.fixture') -Value $token
    $arguments = @($slot, $token)
    Invoke-HostSmoke $Assembly '--prototype-save-init' 'PASS: WP07 committed the mining checkpoint.' -Arguments $arguments

    $first = Join-Path $slot 'save.a'
    $second = Join-Path $slot 'save.b'
    $pending = Join-Path $slot 'save.pending'
    $marker = Join-Path $slot 'wyu-prototype-save.ready'
    $committedHash = (Get-FileHash -LiteralPath $first -Algorithm SHA256).Hash
    Stop-HostAtSaveMarker $Assembly '--prototype-save-before-publish' $arguments $marker ' --prototype-save-before-publish' {
        Assert-SaveGeneration $pending 2
        if (Test-Path -LiteralPath $second) { throw 'WP07 generation 2 was already installed.' }
    }
    if ((Get-FileHash -LiteralPath $first -Algorithm SHA256).Hash -cne $committedHash -or (Test-Path -LiteralPath $second)) {
        throw 'WP07 pre-publication interruption changed the committed checkpoint.'
    }
    Write-Host 'PASS: WP07 interruption before installation preserved the first committed file byte for byte.'

    Remove-Item -LiteralPath $marker
    Stop-HostAtSaveMarker $Assembly '--prototype-save-after-publish' $arguments $marker ' --prototype-save-after-publish' {
        Assert-SaveGeneration $second 2
        if (Test-Path -LiteralPath $pending) { throw 'WP07 generation 2 was not moved atomically.' }
    }
    if ((Get-FileHash -LiteralPath $first -Algorithm SHA256).Hash -cne $committedHash) {
        throw 'WP07 generation 2 overwrote the previous committed checkpoint.'
    }
    Assert-SaveGeneration $second 2
    Write-Host 'PASS: WP07 interruption after installation left a complete generation 2 for a fresh process.'

    Invoke-HostSmoke $Assembly '--prototype-save-finish' 'PASS: WP07 resumed partial construction and committed the rescue signal without mutating the staged checkpoint.' -Arguments $arguments
    Invoke-HostSmoke $Assembly '--prototype-save-verify' 'PASS: WP07 restored the completed beacon and protected invalid or unsupported checkpoints.' -Arguments $arguments
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
        if ($TerrainSmoke) { Invoke-HostSmoke $assembly '--terrain-smoke' 'PASS: WYU D3D12 terrain presentation and native shutdown.' }
        if ($WalkableSmoke) { Invoke-HostSmoke $assembly '--walkable-smoke' 'PASS: WYU walkable route and native shutdown.' -TimeoutSeconds 130 }
        if ($PlanetSmoke) { Invoke-HostSmoke $assembly '--planet-smoke' 'PASS: WYU WP08 planet route and native shutdown.' -TimeoutSeconds 130 }
        if ($FlightSmoke) { Invoke-FlightSmoke $assembly $work }
        if ($PrototypeSmoke) { Invoke-HostSmoke $assembly '--prototype-smoke' 'PASS: WYU WP07 rescue beacon route and native shutdown.' -TimeoutSeconds 130 }
        if ($SaveSmoke) { Invoke-SaveSmoke $assembly $work }
        if ($PrototypeSaveSmoke) { Invoke-PrototypeSaveSmoke $assembly $work }
        if ($InstallerCompiler) {
            & (Join-Path $PSScriptRoot 'Build-OfflineInstaller.ps1') -Compiler $InstallerCompiler `
                -OutputDirectory $InstallerOutputDirectory -WorkDirectory $work -GroundworkVersion $ExpectedVersion `
                -GroundworkCommit $ExpectedSourceCommit -GroundworkManifestHash $ExpectedManifestSha256 `
                -GroundworkLicense (Join-Path $artifactRoot 'LICENSE')
        }
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
