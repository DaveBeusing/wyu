param(
    [Parameter(Mandatory)][string]$ProviderBuild,
    [Parameter(Mandatory)][string]$PackageCache,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot ("../artifacts/wp11-w04-31/WARP-" + [guid]::NewGuid().ToString('N'))),
    [switch]$LandingMarkerOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$provider = (Resolve-Path -LiteralPath $ProviderBuild).Path
$cache = (Resolve-Path -LiteralPath $PackageCache).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$wyuCommit = '24c6c14d1642d8191e3cc4256bb493eafb3b7f26'
$wyuTree = '63dbe0b27366a249e8836d55625c3374adffb027'
$wyuOverlayCommit = '769a2021ff2a48979cbc98bd60219cb25ab3861e'
$wyuOverlayTree = '3fb8361050b930245598e124d9611e67b47ebd78'
$groundworkCommit = '49497e4f033641fa05105d79819a04302c195eed'
$packageVersion = '0.1.0-dev.44364420373.1'
$providerName = 'Groundwork.Rendering.D3D12.dll'
$qualification = if ($LandingMarkerOnly) { 'marker-readback' } else { 'readback' }
$providerInfoPath = Join-Path $provider 'provider-build-provenance.json'
$providerInfo = Get-Content -LiteralPath $providerInfoPath -Raw | ConvertFrom-Json
$packageManifest = Join-Path $provider 'provider-stock-package-manifest.json'
$patch = Join-Path $PSScriptRoot 'patches/wp11-warp-present-readback-groundwork.patch'
$providerDll = Join-Path $provider $providerName
$packageManifestHash = Get-Sha256 $packageManifest
$baseTree = & git -C $repo rev-parse --verify "${wyuCommit}^{tree}" 2>$null
if ($LASTEXITCODE -ne 0 -or $baseTree -cne $wyuTree) {
    throw "The pinned WYU source tree is unavailable. Fetch WYU commit $wyuCommit before running the diagnostic."
}
$overlayIdentity = & git -C $repo show -s --format='%P %T' $wyuOverlayCommit 2>$null
if ($LASTEXITCODE -ne 0 -or $overlayIdentity -cne "$wyuCommit $wyuOverlayTree") {
    throw "The pinned WYU diagnostic tree is unavailable or has unexpected provenance. Fetch WYU commit $wyuOverlayCommit and its parent."
}
if ($providerInfo.sourceCommit -cne $groundworkCommit -or $providerInfo.packageVersion -cne $packageVersion -or
    $packageManifestHash -cne 'DBA7D2AADB8A28942B97F845FDF401018AA257DA9F01C152845423B937E674C6' -or
    $packageManifestHash -ine $providerInfo.stockPackageManifestSha256 -or
    !$providerInfo.shadersMatchStock -or $providerInfo.shaderProvenanceSha256 -cne $providerInfo.stockShaderProvenanceSha256 -or
    (Get-Sha256 $patch) -ine $providerInfo.patchSha256 -or
    (Get-Sha256 $providerDll) -ine $providerInfo.providerDllSha256 -or
    [Diagnostics.FileVersionInfo]::GetVersionInfo($providerDll).ProductVersion -cne $providerInfo.providerProductVersion) {
    throw 'WYU base, provider overlay, or immutable package provenance did not match.'
}

$packages = (Get-Content -LiteralPath $packageManifest -Raw | ConvertFrom-Json).packages
if (@($packages).Count -ne 10) { throw 'Expected ten verified Groundwork packages.' }
$archives = @{}
foreach ($package in $packages) {
    $archive = Join-Path $cache ($package.packageId.ToLowerInvariant() + "/$packageVersion/" + $package.filename.ToLowerInvariant())
    if ((Get-Sha256 $archive) -ine $package.sha256) { throw "Package SHA-256 mismatch for $($package.packageId)." }
    $archives[$package.packageId] = $archive
}
$terraFx = Join-Path $cache 'terrafx.interop.windows/10.0.26100.6/terrafx.interop.windows.10.0.26100.6.nupkg'
if ((Get-Sha256 $terraFx) -cne 'A02D560C0EF0D64541F5A746661D8A0C2BC98AC45C3A4C380B2F3BC038E1DAA7') {
    throw 'TerraFX package SHA-256 mismatch.'
}

if (Test-Path -LiteralPath $output) { throw "Output directory already exists: $output" }
New-Item -ItemType Directory -Path $output | Out-Null
Copy-Item -LiteralPath $providerInfoPath,$packageManifest -Destination $output
$feed = Join-Path $output 'feed'
New-Item -ItemType Directory -Path $feed | Out-Null
Copy-Item -LiteralPath @($archives.Values) -Destination $feed
Copy-Item -LiteralPath $terraFx -Destination $feed
$sourceNames = @('Program.cs', 'ProceduralFlightRendering.cs', 'VolumeExploration.cs', 'VolumeExplorationFlight.cs',
    'VolumeExplorationHost.cs', 'VolumeFlightSmoke.cs', 'Wp11WarpReadback.cs', 'Wp11WarpReadbackSamples.cs')
$sourceArchive = Join-Path $output 'wyu-diagnostic.zip'
$sourceRoot = Join-Path $output 'src-overlay'
& git -C $repo archive --format=zip "--output=$sourceArchive" $wyuOverlayCommit -- src Directory.Build.props global.json
if ($LASTEXITCODE -ne 0) { throw 'Could not archive the pinned WYU diagnostic tree.' }
[IO.Compression.ZipFile]::ExtractToDirectory($sourceArchive, $sourceRoot)
$buildArtifacts = Join-Path $output 'obj'
$clientDir = Join-Path $output 'client'
$buildCache = Join-Path $output 'packages'
$config = Join-Path $output 'offline.nuget.config'
'<configuration><packageSources><clear /></packageSources></configuration>' | Set-Content -LiteralPath $config -Encoding utf8
$project = Join-Path $sourceRoot 'src/WorldsYetUnseen.Client/WorldsYetUnseen.Client.csproj'
Push-Location -LiteralPath $sourceRoot
try {
    $clientSdk = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or $clientSdk -cne $providerInfo.sdkVersion) { throw 'Client and provider SDKs must match.' }
    & dotnet restore $project --configfile $config --source $feed --packages $buildCache --locked-mode --disable-parallel -m:1 `
        "-p:ArtifactsPath=$buildArtifacts"
    if ($LASTEXITCODE -ne 0) { throw 'Locked offline client restore failed.' }
    & dotnet build $project -c Release --no-restore -m:1 "-p:ArtifactsPath=$buildArtifacts" -o $clientDir
    if ($LASTEXITCODE -ne 0) { throw 'Diagnostic client build failed.' }
} finally {
    Pop-Location
}

$assemblies = foreach ($package in $packages) {
    $name = $package.packageId + '.dll'
    $archive = $archives[$package.packageId]
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entry = @($zip.Entries | Where-Object { $_.FullName.StartsWith('lib/', [StringComparison]::Ordinal) -and $_.Name -ceq $name })
        if ($entry.Count -ne 1) { throw "Could not identify the stock assembly for $name." }
        $bytes = $entry[0].Open()
        try { $expectedHash = (Get-FileHash -InputStream $bytes -Algorithm SHA256).Hash } finally { $bytes.Dispose() }
    } finally { $zip.Dispose() }
    $target = Join-Path $clientDir $name
    if ((Get-Sha256 $target) -cne $expectedHash) { throw "Stock assembly mismatch for $name." }
    if ($name -ceq $providerName) {
        if ($expectedHash -ine $providerInfo.stockProviderDllSha256) { throw 'Stock provider SHA-256 mismatch.' }
        Copy-Item -LiteralPath $providerDll -Destination $target -Force
        if ((Get-Sha256 $target) -ine $providerInfo.providerDllSha256) { throw 'Diagnostic provider copy SHA-256 mismatch.' }
    }
    [ordered]@{ name = $name; stockSha256 = $expectedHash; loadedFileSha256 = Get-Sha256 $target }
}
$client = Join-Path $clientDir 'WorldsYetUnseen.Client.dll'
[ordered]@{
    wyuBaseCommit = $wyuCommit
    wyuBaseTree = $wyuTree
    wyuOverlayCommit = $wyuOverlayCommit
    wyuOverlayTree = $wyuOverlayTree
    wyuSourceArchiveSha256 = Get-Sha256 $sourceArchive
    groundworkSourceCommit = $groundworkCommit
    packageVersion = $packageVersion
    qualification = $qualification
    diagnosticOverlay = $true
    clientSha256 = Get-Sha256 $client
    providerProvenanceSha256 = Get-Sha256 $providerInfoPath
    runnerSha256 = Get-Sha256 $PSCommandPath
    clientSources = @($sourceNames | ForEach-Object {
        [ordered]@{ name = $_; sha256 = Get-Sha256 (Join-Path $sourceRoot "src/WorldsYetUnseen.Client/$_") }
    })
    groundworkAssemblies = $assemblies
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'run-provenance.json') -Encoding utf8

$fixture = Join-Path $output 'fixture'
New-Item -ItemType Directory -Path $fixture | Out-Null
New-Item -ItemType File -Path (Join-Path $fixture 'wyu-volume-smoke.fixture') | Out-Null
$start = [Diagnostics.ProcessStartInfo]::new((Get-Command dotnet -CommandType Application).Source)
$start.WorkingDirectory = $clientDir
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in @($client, '--volume-flight-smoke', $qualification, $fixture, 'warp')) { $start.ArgumentList.Add($argument) }
foreach ($key in @($start.Environment.Keys)) {
    if ($key.StartsWith('WYU_', [StringComparison]::OrdinalIgnoreCase)) { $null = $start.Environment.Remove($key) }
}

$stdout = Join-Path $output 'readback.stdout'
$stderr = Join-Path $output 'readback.stderr'
$process = [Diagnostics.Process]::Start($start)
if ($null -eq $process) { throw 'Could not start the WARP qualification.' }
try {
    $outText = $process.StandardOutput.ReadToEndAsync()
    $errorText = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(210000)) { $process.Kill($true); $process.WaitForExit(); throw 'WARP qualification timed out.' }
} finally {
    $outText.GetAwaiter().GetResult() | Set-Content -LiteralPath $stdout -Encoding utf8 -NoNewline
    $errorText.GetAwaiter().GetResult() | Set-Content -LiteralPath $stderr -Encoding utf8 -NoNewline
    $exitCode = $process.ExitCode
    $process.Dispose()
}

$captures = Join-Path $fixture 'warp-present-readback'
$presentManifest = Join-Path $captures 'present-manifest.json'
if ($exitCode -ne 0 -or (Get-Item -LiteralPath $stderr).Length -ne 0 -or
    !(Select-String -LiteralPath $stdout -SimpleMatch 'PASS: WYU WP11 exploration, rebase and native shutdown.' -Quiet) -or
    !(Test-Path -LiteralPath $presentManifest -PathType Leaf)) {
    throw "WARP qualification failed (exit $exitCode). See $stdout and $stderr."
}
$presents = @(Get-Content -LiteralPath $presentManifest -Raw | ConvertFrom-Json)
if ($LandingMarkerOnly) {
    if ($presents.Count -ne 1 -or $presents[0].Checkpoint -cne 'landing-marker') {
        throw 'The targeted run did not capture exactly one landing-marker Present.'
    }
} elseif ($presents.Count -ne 6) {
    throw 'The run did not capture all six Presents.'
}
$runtime = Get-Content -LiteralPath (Join-Path $captures 'runtime-assemblies.json') -Raw | ConvertFrom-Json
if ($runtime.ClientSha256 -cne (Get-Sha256 $client) -or $runtime.ProviderSha256 -ine $providerInfo.providerDllSha256) {
    throw 'Runtime assembly SHA-256 values differ from the diagnostic build.'
}
foreach ($present in $presents) {
    $capture = Get-Content -LiteralPath (Join-Path $captures ($present.Checkpoint + '.json')) -Raw | ConvertFrom-Json
    foreach ($buffer in @($capture.color, $capture.depth)) {
        $path = Join-Path $captures $buffer.Name
        if ((Get-Item -LiteralPath $path).Length -ne $buffer.Length -or (Get-Sha256 $path) -cne $buffer.Sha256) {
            throw "Readback buffer verification failed for $($buffer.Name)."
        }
    }
}
$evidenceFiles = @((Get-ChildItem -LiteralPath $captures -File).FullName) +
    @($stdout, $stderr, (Join-Path $output 'run-provenance.json'), (Join-Path $output 'provider-build-provenance.json'),
        (Join-Path $output 'provider-stock-package-manifest.json'))
[ordered]@{
    status = 'PASSED'
    qualification = $qualification
    presents = $presents
    files = @($evidenceFiles | ForEach-Object {
        [ordered]@{ file = [IO.Path]::GetRelativePath($output, $_); sha256 = Get-Sha256 $_ }
    })
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'evidence.json') -Encoding utf8
Write-Host "WARP qualification passed. Evidence: $output"
