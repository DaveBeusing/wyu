param(
    [Parameter(Mandatory)][string]$Assembly,
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$client = (Resolve-Path -LiteralPath $Assembly).Path
$output = New-Item -ItemType Directory -Path $OutputDirectory
$old = $env:WYU_WP15_PROBE, $env:WYU_WP15, $env:WYU_PACKAGE_E, $env:WYU_PACKAGE_G, $env:WYU_WP13_MEASURE
$invariant = [Globalization.CultureInfo]::InvariantCulture
$phases = 'midday', 'low', 'turn'
$columns = 'Sequence', 'Frame', 'Shadow', 'Terrain', 'Cpu', 'Allocated', 'ShadowBytes', 'Draws', 'GeometryBuilds'
$samples = [Collections.Generic.List[object]]::new()

try {
    $env:WYU_WP15_PROBE, $env:WYU_PACKAGE_E, $env:WYU_PACKAGE_G, $env:WYU_WP13_MEASURE = 'measure', 'on', 'on', 'on'
    for ($pair = 1; $pair -le 5; $pair++) {
        $modes = if ($pair % 2) { 'off', 'on' } else { 'on', 'off' }
        foreach ($mode in $modes) {
            $env:WYU_WP15 = $mode
            $path = Join-Path $output "$pair-$mode.log"
            & dotnet $client --volume-smoke 42 hardware > $path 2>&1
            $lines = Get-Content -LiteralPath $path
            if ($LASTEXITCODE -ne 0 -or $lines -cnotcontains 'PASS: WYU WP11 exploration, rebase and native shutdown.') {
                throw "WP15 replay $pair/$mode failed. See $path."
            }
            $data = @{}
            foreach ($line in $lines) {
                if ($line -cmatch '^WP15 GPU (.+)$') {
                    $row = $Matches[1] | ConvertFrom-Csv -Header $columns
                    $data.Add([ulong]$row.Sequence, $row)
                }
            }
            foreach ($phase in $phases) {
                $span = @($lines | Where-Object { $_ -cmatch "^WP15 SEQUENCES $phase (\d+) 400$" })
                if ($span.Count -ne 1) { throw "Missing sequence window for $phase. See $path." }
                $first = [ulong]($span[0] -split ' ')[3]
                for ($i = 0; $i -lt 400; $i++) {
                    $row = $data[($first + [ulong]$i)]
                    if ($null -eq $row) { throw "Missing GPU timestamp for sequence $($first + $i). See $path." }
                    $samples.Add([pscustomobject]@{
                        Pair = $pair; Mode = $mode; Phase = $phase; Sample = $i
                        Frame = [double]::Parse($row.Frame, $invariant)
                        Shadow = [double]::Parse($row.Shadow, $invariant)
                        Terrain = [double]::Parse($row.Terrain, $invariant)
                        Cpu = [double]::Parse($row.Cpu, $invariant)
                        Allocated = [long]$row.Allocated; ShadowBytes = [long]$row.ShadowBytes
                        Draws = [int]$row.Draws; GeometryBuilds = [long]$row.GeometryBuilds
                    })
                }
            }
            Write-Host "WP15 replay $pair/$mode passed."
        }
    }
} finally {
    $env:WYU_WP15_PROBE, $env:WYU_WP15, $env:WYU_PACKAGE_E, $env:WYU_PACKAGE_G, $env:WYU_WP13_MEASURE = $old
}

function Percentile($values, [double]$p) {
    $sorted = @($values | Sort-Object)
    $sorted[[int][Math]::Ceiling($sorted.Count * $p) - 1]
}

$samples | Export-Csv (Join-Path $output 'samples.csv')
$summary = foreach ($phase in $phases) {
    foreach ($pair in 0..5) {
        $rows = @($samples | Where-Object { $_.Phase -ceq $phase -and ($pair -eq 0 -or $_.Pair -eq $pair) })
        $off = @($rows | Where-Object Mode -ceq off)
        $on = @($rows | Where-Object Mode -ceq on)
        $extra = for ($i = 0; $i -lt $on.Count; $i++) { $on[$i].Shadow - $off[$i].Shadow + $on[$i].Terrain - $off[$i].Terrain }
        $off95 = Percentile $off.Frame 0.95
        $off99 = Percentile $off.Frame 0.99
        $delta95 = 100 * ((Percentile $on.Frame 0.95) / $off95 - 1)
        $delta99 = 100 * ((Percentile $on.Frame 0.99) / $off99 - 1)
        $extra95 = Percentile $extra 0.95
        $extra99 = Percentile $extra 0.99
        $cpu99 = Percentile $on.Cpu 0.99
        $peak = ($on.ShadowBytes | Measure-Object -Maximum).Maximum
        $alloc = ($on.Allocated | Measure-Object -Maximum).Maximum
        $draws = ($on.Draws | Measure-Object -Maximum).Maximum
        $rebuilds = ($on.GeometryBuilds | Measure-Object -Maximum).Maximum - ($on.GeometryBuilds | Measure-Object -Minimum).Minimum
        [pscustomobject]@{
            Phase = $phase; Pair = $pair; CountPerVariant = $on.Count; OffP95Ms = $off95; OffP99Ms = $off99
            ShadowP95Ms = Percentile $on.Shadow 0.95; ShadowP99Ms = Percentile $on.Shadow 0.99
            ExtraP95Ms = [Math]::Round($extra95, 4); ExtraP99Ms = [Math]::Round($extra99, 4)
            FrameDeltaP95Pct = [Math]::Round($delta95, 2); FrameDeltaP99Pct = [Math]::Round($delta99, 2)
            CpuP99Ms = $cpu99; HeapMaxBytes = $alloc; ShadowPeakBytes = $peak; ShadowDrawsMax = $draws; GeometryRebuilds = $rebuilds
            Passed = $extra95 -le 1.5 -and $extra99 -le 2 -and $delta95 -le 10 -and $delta99 -le 15 -and
                $cpu99 -le 0.30 -and $alloc -eq 0 -and $peak -le 16MB -and $rebuilds -eq 0
        }
    }
}
$summary | Export-Csv (Join-Path $output 'summary.csv')
$summary | Where-Object Pair -eq 0 | Format-Table -AutoSize
Write-Host "Evidence: $($output.FullName)"
if (@($summary | Where-Object { -not $_.Passed }).Count) { exit 1 }
