param(
    [Parameter(Mandatory)][string]$PresentMon,
    [string]$Assembly = (Join-Path $PSScriptRoot '../artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll'),
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('wyu-package-e-' + [guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
$client = (Resolve-Path -LiteralPath $Assembly).Path
$capture = (Resolve-Path -LiteralPath $PresentMon).Path
$root = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$session = 'WyuPackageE-' + [guid]::NewGuid().ToString('N')
$raw = Join-Path $root 'presentmon.csv'
$before = $env:WYU_PACKAGE_E, $env:WYU_PACKAGE_E_MEASURE, $env:WYU_WP13_MEASURE
$windows = [Collections.Generic.List[object]]::new()

function Invoke-Replay([int]$Pair, [string]$Mode) {
    $env:WYU_PACKAGE_E = $Mode
    $log = Join-Path $root "$Pair-$Mode.log"
    & dotnet $client --volume-smoke 42 hardware > $log 2>&1
    $exitCode = $LASTEXITCODE
    $lines = Get-Content -LiteralPath $log
    if ($exitCode -ne 0 -or $lines -cnotcontains 'PASS: WYU WP11 exploration, rebase and native shutdown.') {
        throw "Replay $Pair/$Mode failed. See $log."
    }
    foreach ($line in $lines) {
        if ($line -cmatch '^PACKAGE E WINDOW (\d+) (clear|dense|boundary) (\d+) (\d+)$') {
            $windows.Add([pscustomobject]@{ Pair=$Pair; Mode=$Mode; Process=[int]$Matches[1];
                Phase=$Matches[2]; Start=[long]$Matches[3]; End=[long]$Matches[4] })
        }
    }
    Write-Host "Replay $Pair/$Mode passed."
}

try {
    $env:WYU_PACKAGE_E_MEASURE = '1'
    $env:WYU_WP13_MEASURE = 'on'
    $arguments = @('--process_name', 'dotnet.exe', '--session_name', $session, '--no_console_stats',
        '--v2_metrics', '--qpc_time', '--output_file', ('"' + $raw + '"'))
    $process = Start-Process -FilePath $capture -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $root 'presentmon.stderr')
    Start-Sleep -Seconds 2
    if ($process.HasExited) { throw "PresentMon exited early. See $root." }
    Invoke-Replay 0 'off'
    Invoke-Replay 0 'on'
    for ($pair = 1; $pair -le 5; $pair++) {
        $modes = if ($pair % 2) { 'off', 'on' } else { 'on', 'off' }
        foreach ($mode in $modes) { Invoke-Replay $pair $mode }
    }
} finally {
    $env:WYU_PACKAGE_E, $env:WYU_PACKAGE_E_MEASURE, $env:WYU_WP13_MEASURE = $before
    if ($process -and -not $process.HasExited) {
        & $capture --session_name $session --terminate_existing_session
        if (-not $process.WaitForExit(10000)) { throw "PresentMon did not finish. See $root." }
    }
}

$windows | Export-Csv -LiteralPath (Join-Path $root 'windows.csv') -NoTypeInformation
$invariant = [Globalization.CultureInfo]::InvariantCulture
$byProcess = Import-Csv -LiteralPath $raw | Group-Object -Property ProcessID -AsHashTable -AsString
$samples = foreach ($window in $windows | Where-Object Pair -GT 0) {
    foreach ($record in $byProcess[[string]$window.Process]) {
        $qpc = [long]$record.CPUStartQPC
        if ($qpc -ge $window.Start -and $qpc -lt $window.End -and $record.GPUBusy -match '^\d+(\.\d+)?$') {
            $ms = [double]::Parse($record.GPUBusy, $invariant)
            if ($ms -gt 0) {
                [pscustomobject]@{ Pair=$window.Pair; Mode=$window.Mode; Phase=$window.Phase; Qpc=$qpc; GpuBusyMs=$ms;
                    PresentMode=$record.PresentMode }
            }
        }
    }
}
$samples | Export-Csv -LiteralPath (Join-Path $root 'samples.csv') -NoTypeInformation
$summary = foreach ($phase in 'clear','dense','boundary') {
    $off = @($samples | Where-Object { $_.Phase -eq $phase -and $_.Mode -eq 'off' } | Sort-Object GpuBusyMs)
    $on = @($samples | Where-Object { $_.Phase -eq $phase -and $_.Mode -eq 'on' } | Sort-Object GpuBusyMs)
    if ($off.Count -lt 2000 -or $on.Count -lt 2000) {
        throw "Insufficient GPU samples for $phase ($($off.Count)/$($on.Count)). See $root."
    }
    foreach ($percentile in .95,.99) {
        $limit = if ($percentile -eq .95) { 3 } else { 5 }
        $base = $off[[int][Math]::Ceiling($off.Count * $percentile) - 1].GpuBusyMs
        $candidate = $on[[int][Math]::Ceiling($on.Count * $percentile) - 1].GpuBusyMs
        $delta = 100 * ($candidate / $base - 1)
        [pscustomobject]@{ Phase=$phase; OffCount=$off.Count; OnCount=$on.Count; Percentile=$percentile;
            OffMs=$base; OnMs=$candidate; DeltaPercent=[Math]::Round($delta, 2); LimitPercent=$limit; Passed=$delta -le $limit }
    }
}
$summary | Export-Csv -LiteralPath (Join-Path $root 'summary.csv') -NoTypeInformation
$summary | Format-Table -AutoSize
Write-Host "Evidence: $root"
if ($summary.Passed -contains $false) { throw 'Package E GPU busy budget exceeded.' }
