param(
    [string]$Assembly = (Join-Path $PSScriptRoot '../artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll'),
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('wyu-wp13-' + [guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'
$client = (Resolve-Path -LiteralPath $Assembly).Path
$root = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$prior = $env:WYU_WP13_MEASURE
$limits = @{ground = @(3, 5); orbit = @(3, 5); 'near-pack' = @(10); 'globe-build' = @(10); 'to-orbit' = @(10); 'to-ground' = @(10)}
$failures = [Collections.Generic.List[string]]::new()
$results = [Collections.Generic.List[object]]::new()
function Replay([string]$Mode, [int]$Pair, [string]$Kind) {
    $file = Join-Path $root "$Pair-$Mode-$Kind.log"
    $env:WYU_WP13_MEASURE = $Mode
    if ($Kind -eq 'ground') {
        & dotnet $client --volume-smoke 42 hardware > $file 2>&1
    } else {
        $fixture = (New-Item -ItemType Directory -Path (Join-Path $root "$Pair-$Mode-world")).FullName
        New-Item -ItemType File -Path (Join-Path $fixture 'wyu-volume-smoke.fixture') | Out-Null
        & dotnet $client --volume-flight-smoke flight $fixture hardware > $file 2>&1
    }
    $exitCode = $LASTEXITCODE
    $lines = Get-Content -LiteralPath $file
    if ($exitCode -ne 0 -or $lines -cnotcontains 'PASS: WYU WP11 exploration, rebase and native shutdown.') {
        $failures.Add("$Pair $Mode $Kind (exit $exitCode)")
    }
    foreach ($line in $lines) {
        if ($line -cmatch '^WP13 METRIC (\S+) ([0-9.]+)$') {
            $results.Add([pscustomobject]@{ Pair = $Pair; Mode = $Mode; Phase = $Matches[1];
                Milliseconds = [double]::Parse($Matches[2], [Globalization.CultureInfo]::InvariantCulture) })
        }
    }
    Write-Host "Replay $Pair $Mode $Kind finished with exit $exitCode."
}
function Percentile([double[]]$Samples, [double]$Probability) {
    [Array]::Sort($Samples)
    $Samples[[int][Math]::Ceiling($Samples.Length * $Probability) - 1]
}
try {
    foreach ($mode in 'off', 'on') {
        Replay $mode 0 'ground'
        Replay $mode 0 'flight'
    }
    $results.Clear()
    for ($pair = 1; $pair -le 5; $pair++) {
        $modes = if ($pair % 2) { 'off', 'on' } else { 'on', 'off' }
        foreach ($mode in $modes) {
            Replay $mode $pair 'ground'
            Replay $mode $pair 'flight'
        }
    }
} finally {
    $env:WYU_WP13_MEASURE = $prior
}
$results | Export-Csv -LiteralPath (Join-Path $root 'samples.csv') -NoTypeInformation
$summary = foreach ($phase in ($limits.Keys | Sort-Object)) {
    $off = @($results | Where-Object { $_.Phase -eq $phase -and $_.Mode -eq 'off' } | ForEach-Object Milliseconds)
    $on = @($results | Where-Object { $_.Phase -eq $phase -and $_.Mode -eq 'on' } | ForEach-Object Milliseconds)
    if (!$off.Count -or !$on.Count) {
        [pscustomobject]@{ Phase=$phase; OffCount=$off.Count; OnCount=$on.Count; Percentile=''; OffMs=''; OnMs=''; DeltaPercent=''; LimitPercent=''; Status='missing' }
        continue
    }
    $checks = $limits[$phase]
    for ($i = 0; $i -lt $checks.Count; $i++) {
        $pct = if ($i -eq 0) { .95 } else { .99 }
        $baseline = Percentile $off $pct
        $candidate = Percentile $on $pct
        $delta = 100 * ($candidate / $baseline - 1)
        $status = if (($phase -in 'ground','orbit') -and [Math]::Min($off.Count,$on.Count) -lt 2000) { 'insufficient' }
            elseif ($delta -le $checks[$i]) { 'pass' } else { 'over' }
        [pscustomobject]@{ Phase=$phase; OffCount=$off.Count; OnCount=$on.Count; Percentile=$pct; OffMs=$baseline; OnMs=$candidate;
            DeltaPercent=[Math]::Round($delta, 2); LimitPercent=$checks[$i]; Status=$status }
    }
}
$summary | Export-Csv -LiteralPath (Join-Path $root 'summary.csv') -NoTypeInformation
$summary | Format-Table -AutoSize
Write-Host "Evidence: $root"
if ($failures.Count) { throw "Replay failures: $($failures -join ', ')." }
