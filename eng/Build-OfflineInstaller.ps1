param(
    [Parameter(Mandatory)][string]$Compiler,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [Parameter(Mandatory)][string]$GroundworkVersion,
    [Parameter(Mandatory)][string]$GroundworkCommit,
    [Parameter(Mandatory)][string]$GroundworkManifestHash,
    [Parameter(Mandatory)][string]$GroundworkLicense
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Called by the verified consumer while its validated feed and fresh cache are active.
if (!$env:GROUNDWORK_NUGET_FEED -or !$env:NUGET_PACKAGES) { throw 'Use the verified consumer gate to build the installer.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new installer output directory.' }
$dirty = git status --porcelain
if ($LASTEXITCODE -or $dirty) { throw 'Installer provenance requires a clean WYU checkout.' }
$commit = git rev-parse HEAD
if ($LASTEXITCODE) { throw 'Cannot determine WYU commit.' }
$sdk = dotnet --version
if ($LASTEXITCODE -or $sdk -cne '10.0.403') { throw "Installer build requires .NET SDK 10.0.403. Found $sdk." }
$compilerFile = Get-Item -LiteralPath $Compiler
$compilerHash = (Get-FileHash -LiteralPath $compilerFile.FullName).Hash
if ($compilerHash -cne '0A8757031B33777E4C9CBFFEE40F11A5062B36D25CBE144C1DB73B6102B80AD7') {
    throw 'Use ISCC.exe from the reviewed Inno Setup 6.7.3 distribution.'
}

$project = 'src/WorldsYetUnseen.Client/WorldsYetUnseen.Client.csproj'
$version = dotnet msbuild $project -nologo -getProperty:Version
if ($LASTEXITCODE -or $version -notmatch '^\d+\.\d+\.\d+$') { throw 'Cannot determine the WYU installer version.' }
$payload = Join-Path $WorkDirectory 'installer-payload'
$dotnetArtifacts = Join-Path $WorkDirectory 'installer-dotnet'
$options = @('-r', 'win-x64', '-p:SelfContained=true', '-p:WyuInstallerBuild=true', "-p:ArtifactsPath=$dotnetArtifacts")
dotnet restore $project --configfile NuGet.Config --locked-mode @options
if ($LASTEXITCODE) { throw 'Installer locked restore failed.' }
dotnet publish $project -c Release --no-restore -o $payload @options
if ($LASTEXITCODE) { throw 'Installer publish failed.' }

foreach ($name in 'WorldsYetUnseen.Client.exe','hostfxr.dll','hostpolicy.dll','coreclr.dll') {
    if (!(Test-Path -LiteralPath (Join-Path $payload $name) -PathType Leaf)) { throw "Self-contained payload is missing $name." }
}
$deps = Get-Content -LiteralPath (Join-Path $payload 'WorldsYetUnseen.Client.deps.json') -Raw | ConvertFrom-Json
$runtime = @($deps.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/*' })
if ($runtime.Count -ne 1 -or $runtime[0] -cne 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.14') {
    throw 'Installer build requires the reviewed .NET 10.0.14 runtime.'
}
$engines = @($deps.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'Groundwork.*/*' })
if ($engines.Count -ne 10 -or @($engines | Where-Object { ($_ -split '/')[1] -cne $GroundworkVersion }).Count) {
    throw 'Published Groundwork versions do not match the verified package pin.'
}
$runtimePackage = Join-Path $env:NUGET_PACKAGES 'microsoft.netcore.app.runtime.win-x64/10.0.14'
$runtimeHash = (Get-FileHash -LiteralPath (Join-Path $runtimePackage 'microsoft.netcore.app.runtime.win-x64.10.0.14.nupkg')).Hash
if ($runtimeHash -cne 'A3EA61B4A2DA6D28E562DD1CDACF80EB4DD6B9727B0A2B47F9D6ABA90DBBCB1F') { throw 'Unexpected runtime package.' }
if ((Get-FileHash -LiteralPath $GroundworkLicense).Hash -cne 'B1D2870F1A00E4D7F56576E5F0870CBA109E041F8AF66866CF5B499478E654E7') {
    throw 'Unexpected Groundwork license.'
}
$licenses = Join-Path $payload 'licenses'
New-Item -ItemType Directory -Path $licenses | Out-Null
Copy-Item -LiteralPath $GroundworkLicense -Destination (Join-Path $licenses 'Groundwork.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses/TerraFX.txt') -Destination $licenses
Copy-Item -LiteralPath (Join-Path $runtimePackage 'LICENSE.TXT') -Destination (Join-Path $licenses 'dotnet.txt')
Copy-Item -LiteralPath (Join-Path $runtimePackage 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licenses 'dotnet-third-party-notices.txt')

New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
& $compilerFile.FullName "/DAppVersion=$version" "/DPayloadDirectory=$payload" "/DInstallerOutput=$OutputDirectory" (Join-Path $PSScriptRoot 'WyuAlpha.iss')
if ($LASTEXITCODE) { throw 'Inno Setup compilation failed.' }
$name = "WYU-Alpha-$version-win-x64.exe"
$installer = Join-Path $OutputDirectory $name
$files = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path = [IO.Path]::GetRelativePath($payload, $_.FullName).Replace('\', '/')
        sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash
    }
})
[ordered]@{
    schemaVersion = 1
    wyuCommit = $commit
    wyuVersion = $version
    groundworkCommit = $GroundworkCommit
    groundworkVersion = $GroundworkVersion
    groundworkManifestSha256 = $GroundworkManifestHash
    sdk = $sdk
    runtime = ($runtime[0] -split '/')[1]
    runtimePackageSha256 = $runtimeHash
    target = 'win-x64'
    compilerVersion = '6.7.3'
    compilerSha256 = $compilerHash
    installer = $name
    installerSha256 = (Get-FileHash -LiteralPath $installer).Hash
    payload = $files
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'installer-manifest.json')
Write-Host "PASS: Built offline installer $installer"
