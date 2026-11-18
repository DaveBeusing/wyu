#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$GroundworkGitDirectory,
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$DxcPackagePath,
    [Parameter(Mandatory)][string]$TerraFxPackagePath,
    [Parameter(Mandatory)][string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$groundworkCommit = '49497e4f033641fa05105d79819a04302c195eed'
$groundworkPrHead = '8a5efa4c01f9a67fb3ca7847e037d06b202ed09b'
$groundworkPrBase = '3f3e2905a5c3f2af9e8ba3ee2775fbff421d937f'
$packageVersion = '0.1.0-dev.44364420373.1'
$manifestSha256 = 'DBA7D2AADB8A28942B97F845FDF401018AA257DA9F01C152845423B937E674C6'
$lockedObjects = [ordered]@{
    'src' = '7a6f123c574488243c579deaf17f3b1ec23e5407'
    'eng' = '6d75bc3293dd125eb2e30cc04f98043953fa6323'
    'Directory.Build.props' = 'a17a5cde10ef3a744a77b6a1e0b9044e608afcea'
    'global.json' = 'd46d21e50b3f3284ca3589173cac1e0af1076810'
    'NuGet.Config' = 'f83ad1b8698e67dcd94a0691c923d403d177ce6b'
    'LICENSE' = 'ff9ad4530f5716304e042cbac72cecd7c40f0e0e'
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Assert-Sha256([string]$Path, [string]$Expected) {
    if ((Get-Sha256 $Path) -cne $Expected) { throw "Unexpected SHA-256 for $Path." }
}

$gitDirectory = (Resolve-Path -LiteralPath $GroundworkGitDirectory).ProviderPath
$packageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).ProviderPath
$dxcPath = (Resolve-Path -LiteralPath $DxcPackagePath).ProviderPath
$terraFxPath = (Resolve-Path -LiteralPath $TerraFxPackagePath).ProviderPath
$patchPath = Join-Path $PSScriptRoot 'patches/wp11-warp-present-readback-groundwork.patch'
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a fresh output directory for the diagnostic provider build.' }

$manifestPath = Join-Path $packageDirectory 'manifest.json'
Assert-Sha256 $manifestPath $manifestSha256
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.sourceCommit -cne $groundworkCommit -or $manifest.packages.Count -ne 10) {
    throw 'The package manifest does not identify the expected ten Groundwork packages.'
}
foreach ($package in $manifest.packages) {
    $packagePath = Join-Path $packageDirectory $package.filename
    if ($package.packageVersion -cne $packageVersion -or (Get-Item -LiteralPath $packagePath).Length -ne $package.sizeBytes) {
        throw "Unexpected pin or size for $($package.packageId)."
    }
    Assert-Sha256 $packagePath $package.sha256
}

Assert-Sha256 $dxcPath '73CFA082245918C2CC1EC6740250EB7377E83A757B3B3A3E16E0D71535BFBFC3'
Assert-Sha256 $terraFxPath 'A02D560C0EF0D64541F5A746661D8A0C2BC98AC45C3A4C380B2F3BC038E1DAA7'
$patchSha256 = Get-Sha256 $patchPath

$sourceCommit = $groundworkCommit
& git "--git-dir=$gitDirectory" cat-file -e "${groundworkCommit}^{commit}" 2>$null
if ($LASTEXITCODE -ne 0) {
    # The approved merge changes docs and tests only. Its reviewed head and base
    # have the same locked source and build objects used for this provider build.
    $sourceCommit = $groundworkPrHead
    & git "--git-dir=$gitDirectory" diff --exit-code $groundworkPrBase $groundworkPrHead -- @($lockedObjects.Keys)
    if ($LASTEXITCODE -ne 0) { throw 'The Groundwork merge source or the equivalent approved PR objects are required.' }
}
foreach ($entry in $lockedObjects.GetEnumerator()) {
    $oid = & git "--git-dir=$gitDirectory" rev-parse "${sourceCommit}:$($entry.Key)"
    if ($LASTEXITCODE -ne 0 -or $oid -cne $entry.Value) { throw "Unexpected Groundwork Git object for $($entry.Key)." }
}

New-Item -ItemType Directory -Path $output | Out-Null
$sourceArchive = Join-Path $output 'provider-pristine-source.zip'
& git "--git-dir=$gitDirectory" archive --format=zip "--output=$sourceArchive" $sourceCommit -- @($lockedObjects.Keys)
if ($LASTEXITCODE -ne 0) { throw 'Failed to archive the pinned Groundwork build sources.' }
$source = Join-Path $output 'provider-source'
[IO.Compression.ZipFile]::ExtractToDirectory($sourceArchive, $source)

$savedPatch = Join-Path $output 'provider.patch'
Copy-Item -LiteralPath $patchPath -Destination $savedPatch
Assert-Sha256 $savedPatch $patchSha256
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $output 'provider-stock-package-manifest.json')

$feed = Join-Path $output 'provider-feed'
New-Item -ItemType Directory -Path $feed | Out-Null
Copy-Item -LiteralPath $terraFxPath -Destination (Join-Path $feed 'terrafx.interop.windows.10.0.26100.6.nupkg')
$localConfig = Join-Path $output 'provider-nuget.config'
'<configuration><packageSources><clear /></packageSources></configuration>' | Set-Content -LiteralPath $localConfig -Encoding utf8
$packageCache = Join-Path $output 'provider-packages'
$buildLog = Join-Path $output 'provider-build.log'
$project = 'src/Groundwork.Rendering.D3D12/Groundwork.Rendering.D3D12.csproj'
$providerName = 'Groundwork.Rendering.D3D12.dll'
$diagnosticVersion = "0.1.0+${groundworkCommit}.wp11-warp-readback"

$previousGitCeiling = $env:GIT_CEILING_DIRECTORIES
$env:GIT_CEILING_DIRECTORIES = $output
Push-Location -LiteralPath $source
try {
    & git apply --check $savedPatch
    if ($LASTEXITCODE -ne 0) { throw 'The diagnostic provider patch does not match the pinned source.' }
    & git apply $savedPatch
    if ($LASTEXITCODE -ne 0) { throw 'The diagnostic provider patch could not be applied.' }

    $sdkVersion = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -cne '10.0.403') { throw 'The pinned .NET SDK 10.0.403 is required.' }
    & dotnet restore $project --configfile $localConfig --source $feed --packages $packageCache --locked-mode --disable-parallel -m:1 2>&1 |
        Tee-Object -FilePath $buildLog | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "Locked diagnostic provider restore failed. See $buildLog." }

    & dotnet build $project -c Release --no-restore -m:1 '-p:ContinuousIntegrationBuild=true' "-p:DxcPackagePath=$dxcPath" `
        "-p:InformationalVersion=$diagnosticVersion" '-p:IncludeSourceRevisionInInformationalVersion=false' 2>&1 |
        Tee-Object -FilePath $buildLog -Append | Write-Host
    if ($LASTEXITCODE -ne 0) { throw "Diagnostic provider build failed. See $buildLog." }
} finally {
    Pop-Location
    $env:GIT_CEILING_DIRECTORIES = $previousGitCeiling
}

$providerPackage = $manifest.packages | Where-Object packageId -CEQ 'Groundwork.Rendering.D3D12'
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $packageDirectory $providerPackage.filename))
$stockDll = Join-Path $output 'provider-stock.dll'
$stockShaderProvenance = Join-Path $output 'provider-stock-shader-provenance.json'
try {
    $entry = $zip.GetEntry("lib/net10.0-windows7.0/$providerName")
    if ($null -eq $entry) { throw 'The pinned D3D12 package has no provider assembly.' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $stockDll)
    $entry = $zip.GetEntry('shader-provenance.json')
    if ($null -eq $entry) { throw 'The pinned D3D12 package has no shader provenance.' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $stockShaderProvenance)
} finally {
    $zip.Dispose()
}
$providerDll = Join-Path $output $providerName
Copy-Item -LiteralPath (Join-Path $source "artifacts/bin/Groundwork.Rendering.D3D12/release/$providerName") -Destination $providerDll
$assemblyName = [Reflection.AssemblyName]::GetAssemblyName($providerDll).FullName
if ($assemblyName -cne [Reflection.AssemblyName]::GetAssemblyName($stockDll).FullName -or
    (Get-Sha256 $providerDll) -ceq (Get-Sha256 $stockDll) -or
    [Diagnostics.FileVersionInfo]::GetVersionInfo($providerDll).ProductVersion -cne $diagnosticVersion) {
    throw 'Diagnostic provider assembly identity or diagnostic build marker is inconsistent.'
}

$shaderProvenance = Join-Path $source 'artifacts/obj/Groundwork.Rendering.D3D12/release/shaders/shader-provenance.json'
$archivedShaderProvenance = Join-Path $output 'provider-shader-provenance.json'
Copy-Item -LiteralPath $shaderProvenance -Destination $archivedShaderProvenance
$stockShaders = Get-Content -LiteralPath $stockShaderProvenance -Raw | ConvertFrom-Json
$providerShaders = Get-Content -LiteralPath $archivedShaderProvenance -Raw | ConvertFrom-Json
if ((ConvertTo-Json $providerShaders -Depth 8 -Compress) -cne (ConvertTo-Json $stockShaders -Depth 8 -Compress)) {
    throw 'Diagnostic provider shader provenance differs from the pinned package.'
}
$provenancePath = Join-Path $output 'provider-build-provenance.json'
[ordered]@{
    provider = 'source-built diagnostic overlay'
    sourceCommit = $groundworkCommit
    sourceArchiveCommit = $sourceCommit
    sourceObjects = $lockedObjects
    sourceArchiveSha256 = Get-Sha256 $sourceArchive
    packageVersion = $packageVersion
    stockPackageManifestSha256 = $manifestSha256
    stockProviderPackageSha256 = $providerPackage.sha256
    stockProviderDllSha256 = Get-Sha256 $stockDll
    providerDllSha256 = Get-Sha256 $providerDll
    providerAssemblyName = $assemblyName
    providerProductVersion = $diagnosticVersion
    patchSha256 = $patchSha256
    dxcSha256 = Get-Sha256 $dxcPath
    terraFxSha256 = Get-Sha256 $terraFxPath
    stockShaderProvenanceSha256 = Get-Sha256 $stockShaderProvenance
    shaderProvenanceSha256 = Get-Sha256 $archivedShaderProvenance
    shadersMatchStock = $true
    sdkVersion = $sdkVersion
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $provenancePath -Encoding utf8

Write-Host "Diagnostic provider: $providerDll"
Write-Host "Build provenance: $provenancePath"
