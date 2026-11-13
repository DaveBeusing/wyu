param(
    [string]$Assembly = (Join-Path $PSScriptRoot '../artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll'),
    [string]$AfterInstallAssembly
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

function Test-LandedInterruption([string]$Root, [string]$WriterAssembly, [switch]$AfterInstall) {
    $slots = @(Get-ChildItem -LiteralPath (Join-Path $Root 'v2') -Directory)
    if ($slots.Count -ne 1) { throw 'Expected one landed slot.' }
    $slot = $slots[0]
    $a = Join-Path $slot.FullName 'volume.a'
    $b = Join-Path $slot.FullName 'volume.b'
    $intent = Join-Path $slot.FullName 'volume.intent'
    $before = @(Get-FileHash -LiteralPath $a,$b | ForEach-Object Hash)
    $priorIntent = if (Test-Path -LiteralPath $intent) { (Get-FileHash -LiteralPath $intent).Hash }
    $phase = if ($AfterInstall) { 'interrupt-installed' } else { 'interrupt' }
    $marker = if ($AfterInstall) { 'WP11 LANDED AFTER INSTALL READY' } else { 'WP11 LANDED INTERRUPT READY' }
    $log = Join-Path $Root "$phase.stdout"
    $errors = Join-Path $Root "$phase.stderr"
    $writer = $null
    $lock = if (!$AfterInstall) { [IO.File]::Open($a, 'Open', 'Read', 'ReadWrite') }
    $priorProbe = $env:WYU_LANDED_AFTER_PUBLISH
    try {
        if ($AfterInstall) { $env:WYU_LANDED_AFTER_PUBLISH = '1' }
        $writer = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @(
            ('"' + $WriterAssembly + '"'), '--volume-flight-smoke', $phase, ('"' + $Root + '"'), 'hardware'
        ) -PassThru -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError $errors
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        while (!(Select-String -LiteralPath $log -SimpleMatch $marker -Quiet)) {
            if ($writer.HasExited -or [DateTime]::UtcNow -ge $deadline) {
                Get-Content -LiteralPath $log,$errors | Write-Host
                throw "Landed writer did not reach $phase."
            }
            Start-Sleep -Milliseconds 100
        }
        if ((Get-FileHash -LiteralPath $intent).Hash -ceq $priorIntent) { throw 'Missing new landed write intent.' }
        $writer.Kill()
        if (!$writer.WaitForExit(5000)) { throw 'Landed writer did not exit.' }
    } finally {
        if ($writer) {
            if (!$writer.HasExited) { $writer.Kill(); $writer.WaitForExit(5000) | Out-Null }
            $writer.Dispose()
        }
        if ($lock) { $lock.Dispose() }
        $env:WYU_LANDED_AFTER_PUBLISH = $priorProbe
    }
    $after = @(Get-FileHash -LiteralPath $a,$b | ForEach-Object Hash)
    if ($after[1] -cne $before[1] -or ($AfterInstall.IsPresent -eq ($after[0] -ceq $before[0]))) {
        throw "Unexpected checkpoint member hashes after $phase."
    }
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
    $landed = @($flight | Where-Object { $_ -cmatch '^(WP11|PACKAGE E) LANDED ' })
    $resumed = @(Invoke-FlightPhase 'resume' $root $adapter | Where-Object { $_ -cmatch '^(WP11|PACKAGE E) LANDED ' })
    if ($landed.Count -ne 2 -or ($landed -join "`n") -cne ($resumed -join "`n")) {
        throw "The landed checkpoint changed after restart on $adapter."
    }
    if ($adapter -eq 'hardware') {
        Test-LandedInterruption $root $client
        $recovered = @(Invoke-FlightPhase 'resume' $root $adapter | Where-Object { $_ -cmatch '^(WP11|PACKAGE E) LANDED ' })
        if (($recovered -join "`n") -cne ($landed -join "`n")) {
            throw 'Landed checkpoint or fog changed after interrupted publication.'
        }
        Write-Host 'PASS: Interrupted landed publication preserved the prior checkpoint and reconstructed fog.'
        if ($AfterInstallAssembly) {
            $probe = (Resolve-Path -LiteralPath $AfterInstallAssembly).Path
            Test-LandedInterruption $root $probe -AfterInstall
            $new = @(Invoke-FlightPhase 'resume-installed' $root $adapter |
                Where-Object { $_ -cmatch '^(WP11|PACKAGE E) LANDED ' })
            $again = @(Invoke-FlightPhase 'resume-installed' $root $adapter |
                Where-Object { $_ -cmatch '^(WP11|PACKAGE E) LANDED ' })
            if ($new.Count -ne 2 -or $new[0] -cne $landed[0].Replace('generation=2', 'generation=3') -or
                $new[1] -ceq $landed[1] -or ($new -join "`n") -cne ($again -join "`n")) {
                throw 'Installed landing did not restore the new clock, unchanged pose, and repeatable fog.'
            }
            Write-Host 'PASS: Process exit after NTFS installation restored the new landed checkpoint and fog.'
        }
    }
}

$root = New-FlightFixture
$aborted = @(Invoke-FlightPhase 'abort' $root 'warp' | Where-Object { $_ -cmatch '^WP11 PREFLIGHT ' })
$preflight = @(Invoke-FlightPhase 'preflight' $root 'warp' | Where-Object { $_ -cmatch '^WP11 PREFLIGHT ' })
if ($aborted.Count -ne 1 -or $preflight.Count -ne 1 -or $aborted[0] -cne $preflight[0]) {
    throw 'The preflight checkpoint changed after abort and restart.'
}
Write-Host 'PASS: WARP and NVIDIA flight replay, touchdown continuation and aborted-flight recovery.'
