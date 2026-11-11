param(
    [string]$Assembly = (Join-Path $PSScriptRoot '../artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$client = (Resolve-Path -LiteralPath $Assembly).Path

function New-FlightFixture {
    $path = Join-Path ([IO.Path]::GetTempPath()) ('wyu-w04-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $path | Out-Null
    New-Item -ItemType File -Path (Join-Path $path 'wyu-volume-smoke.fixture') | Out-Null
    return $path
}

function Invoke-FlightPhase([string]$Phase, [string]$Root, [string]$Adapter) {
    Write-Host "Procedural flight $Phase on $Adapter, fixture $Root"
    $output = @(& dotnet $client --volume-flight-smoke $Phase $Root $Adapter)
    $exitCode = $LASTEXITCODE
    $output | Write-Host
    if ($exitCode -ne 0 -or $output -cnotcontains 'PASS: WYU WP11 exploration, rebase and native shutdown.') {
        throw "Flight $Phase failed on $Adapter (exit $exitCode)."
    }
    return $output
}

$reference = $null
foreach ($adapter in @('warp', 'hardware')) {
    $root = New-FlightFixture
    $flight = @(Invoke-FlightPhase 'flight' $root $adapter)
    $trace = @($flight | Where-Object { $_ -cmatch '^WP11 FLIGHT ' }) -join [Environment]::NewLine
    foreach ($phase in @('PAD','launch','far-ascent','Orbit','Brake','near-return','touchdown')) {
        if ($trace -cnotmatch "(?m)^WP11 FLIGHT $phase ") { throw "Missing flight trace $phase on $adapter." }
    }
    if ($null -eq $reference) {
        $reference = $trace
    } elseif ($trace -cne $reference) {
        throw 'The semantic flight trace differed across the two runs.'
    }
    $landed = @($flight | Where-Object { $_ -cmatch '^WP11 LANDED ' })
    $resumed = @(Invoke-FlightPhase 'resume' $root $adapter | Where-Object { $_ -cmatch '^WP11 LANDED ' })
    if ($landed.Count -ne 1 -or $resumed.Count -ne 1 -or $landed[0] -cne $resumed[0]) {
        throw "The landed checkpoint changed after restart on $adapter."
    }
}

$root = New-FlightFixture
$aborted = @(Invoke-FlightPhase 'abort' $root 'warp' | Where-Object { $_ -cmatch '^WP11 PREFLIGHT ' })
$preflight = @(Invoke-FlightPhase 'preflight' $root 'warp' | Where-Object { $_ -cmatch '^WP11 PREFLIGHT ' })
if ($aborted.Count -ne 1 -or $preflight.Count -ne 1 -or $aborted[0] -cne $preflight[0]) {
    throw 'The preflight checkpoint changed after abort and restart.'
}
Write-Host 'PASS: WARP and NVIDIA flight replay, touchdown continuation and aborted-flight recovery.'
