param(
    [Parameter(Mandatory)][string]$Client,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1, 20)][int]$Pairs = 5,
    [string]$PresentMon,
    [string]$ExternalPresentMonDirectory,
    [ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedPresentMonSha256,
    [switch]$IncludeFlight,
    [string]$EvidenceLabel = 'workstation'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PresentMon -and $ExternalPresentMonDirectory) { throw 'Select a single PresentMon acquisition mode.' }
if ([bool]$ExternalPresentMonDirectory -ne [bool]$ExpectedPresentMonSha256) {
    throw 'External PresentMon requires the independently verified collector SHA-256.'
}
$path = (Resolve-Path -LiteralPath $Client).Path
$dotnet = [IO.Path]::GetExtension($path) -ieq '.dll'
$command = if ($dotnet) { (Get-Command dotnet).Source } else { $path }
$processName = if ($dotnet) { 'dotnet.exe' } else { Split-Path $path -Leaf }
$output = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$capture = if ($PresentMon) { (Resolve-Path -LiteralPath $PresentMon).Path }
$external = if ($ExternalPresentMonDirectory) { (Resolve-Path -LiteralPath $ExternalPresentMonDirectory).Path }
$frequency = [Diagnostics.Stopwatch]::Frequency
$invariant = [Globalization.CultureInfo]::InvariantCulture
$frames = [Collections.Generic.List[object]]::new()
$crossings = [Collections.Generic.List[object]]::new()
$runs = [Collections.Generic.List[object]]::new()
$variants = @(
    @{ Name='baseline'; Pbr='off'; Boulder='off' },
    @{ Name='terrain'; Pbr='on';  Boulder='off' },
    @{ Name='boulder'; Pbr='off'; Boulder='on'  },
    @{ Name='both';    Pbr='on';  Boulder='on'  }
)
$environment = @{
    WYU_ORBIT_DEMO_SMOKE='1'; WYU_ORBIT_PBR_MEASURE='1'; WYU_WP13_MEASURE='on'
    WYU_WP15='on'; WYU_PACKAGE_E='on'; WYU_PACKAGE_G='on'
    WYU_WP16_SMOKE='1'; WYU_ORBIT_CAPTURE=$null; WYU_WP15_PROBE=$null
    WYU_WP15_FLIP=$null; WYU_R09_LATENCY=$null; WYU_LANDED_AFTER_PUBLISH=$null
    WYU_ORBIT_PBR=$null; WYU_ORBIT_BOULDER=$null
}
$previous = @{}
foreach ($name in $environment.Keys) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name)
}

function Get-Hash([string]$File) {
    if ($File -and (Test-Path -LiteralPath $File -PathType Leaf)) { (Get-FileHash -LiteralPath $File).Hash }
}

function Parse-Milliseconds($Row, [string[]]$Columns) {
    foreach ($column in $Columns) {
        $value = $Row.PSObject.Properties[$column]
        if ($null -eq $value) { continue }
        $number = 0.0
        if ([double]::TryParse([string]$value.Value, [Globalization.NumberStyles]::Float, $invariant, [ref]$number) -and
            [double]::IsFinite($number) -and $number -ge 0) { return $number }
    }
}

function Save-Raw {
    $runs | Export-Csv -LiteralPath (Join-Path $output 'runs.csv') -NoTypeInformation
    $frames | Export-Csv -LiteralPath (Join-Path $output 'receipts.csv') -NoTypeInformation
    $crossings | Export-Csv -LiteralPath (Join-Path $output 'crossings.csv') -NoTypeInformation
}

function Invoke-Arm([int]$Pair, $Variant, [string]$Route) {
    $env:WYU_ORBIT_PBR, $env:WYU_ORBIT_BOULDER = $Variant.Pbr, $Variant.Boulder
    $key = '{0:D2}-{1}-{2}' -f $Pair, $Variant.Name, $Route
    $file = Join-Path $output "$key.log"
    $arguments = @()
    if ($dotnet) { $arguments += $path }
    if ($Route -eq 'flight') {
        $fixture = New-Item -ItemType Directory -Path (Join-Path $output "$key-fixture")
        New-Item -ItemType File -Path (Join-Path $fixture.FullName 'wyu-volume-smoke.fixture') | Out-Null
        $arguments += @('--volume-flight-smoke', 'flight', $fixture.FullName, 'hardware', '42')
    } else {
        $arguments += @('--volume-smoke', '42', 'hardware')
    }
    $timer = [Diagnostics.Stopwatch]::StartNew()
    & $command @arguments 2>&1 | Out-File -LiteralPath $file -Encoding utf8
    $code = $LASTEXITCODE
    $timer.Stop()
    $count = 0
    $last = 0L
    $lastSequence = 0UL
    $clean = $false
    foreach ($line in Get-Content -LiteralPath $file) {
        if ($line -clike 'PASS: WYU WP11 exploration, rebase and native shutdown.*') { $clean = $true }
        if ($line -cmatch '^ORBIT KPI FRAME (\d+) (surface|orbit) (\d+) (\d+) (\d+) (\d+) (\d+) (\d+) (\d+) (\d+) (\d+)$') {
            $frame = $Matches
            $start = [long]$frame[6]
            $end = [long]$frame[7]
            $seq = [ulong]$frame[4]
            if ($start -lt $last -or $end -lt $start -or $seq -le $lastSequence) { throw "Invalid time or frame order in $file." }
            $step = [int]$frame[3]
            $count++
            $phase = if ($Route -eq 'flight') { 'orbit' }
                elseif ($step -ge 60 -and $step -le 80) { 'rapid-turn' }
                elseif ($step -in 121,122) { 'synthetic-restore' }
                else { 'surface' }
            $frames.Add([pscustomobject]@{
                Run=$key; Pair=$Pair; Variant=$Variant.Name; Route=$Route; Phase=$phase; Pid=[int]$frame[1]
                Step=$step; Sequence=$seq; Generation=[ulong]$frame[5]; Warmup=($count -le 40)
                SubmitQpc=$start; ReceiptQpc=$end; SubmitToReceiptMs=1000.0 * ($end-$start) / $frequency
                ReceiptIntervalMs=if ($last -ne 0) { 1000.0 * ($end-$last) / $frequency }
                TextureResidentAndPendingBytes=[long]$frame[8]; TextureStagingBytes=[long]$frame[9]
                TextureNativeHighWaterBytes=[long]$frame[10]; TextureUploadedBytes=[ulong]$frame[11]
            })
            $last = $end
            $lastSequence = $seq
        } elseif ($line -cmatch '^ORBIT KPI CROSSING (\d+) (\d+) (\d+) (\d+) (\d+)$') {
            $start = [long]$Matches[4]
            $end = [long]$Matches[5]
            $crossings.Add([pscustomobject]@{
                Run=$key; Pair=$Pair; Variant=$Variant.Name; Route=$Route; Phase='surface-rebase'; Pid=[int]$Matches[1]
                Step=[int]$Matches[2]; Generation=[ulong]$Matches[3]
                StartQpc=$start; EndQpc=$end; ObservedMs=1000.0 * ($end-$start) / $frequency
            })
        } elseif ($line -cmatch '^WP13 QPC (\d+) (to-ground|to-orbit) (\d+) (\d+)$') {
            $start = [long]$Matches[3]
            $end = [long]$Matches[4]
            $crossings.Add([pscustomobject]@{
                Run=$key; Pair=$Pair; Variant=$Variant.Name; Route=$Route; Phase=$Matches[2]; Pid=[int]$Matches[1]
                Step=$null; Generation=$null
                StartQpc=$start; EndQpc=$end; ObservedMs=1000.0 * ($end-$start) / $frequency
            })
        }
    }
    $pass = $code -eq 0 -and $clean -and (($Route -eq 'surface' -and $count -eq 480) -or ($Route -eq 'flight' -and $count -gt 40))
    $runs.Add([pscustomobject]@{
        Run=$key; Pair=$Pair; Variant=$Variant.Name; Route=$Route; ExitCode=$code; ReplayPassed=$pass
        Frames=$count; WallMs=$timer.Elapsed.TotalMilliseconds; Log="$key.log"
    })
    Save-Raw
    Write-Host "$key frames=$count valid=$pass exit=$code"
}

$trace = Join-Path ($external ?? $output) 'presentmon.csv'
$session = 'WyuOrbitPbr-' + [guid]::NewGuid().ToString('N')
$process = $null
$traceError = $null
$externalSession = $null
$externalClaimed = $false
$routeStartQpc = 0L
$routeEndQpc = 0L
$metadata = [ordered]@{
    EvidenceLabel=$EvidenceLabel; Client=(Split-Path $path -Leaf); ClientSha256=(Get-Hash $path)
    ClientAssemblySha256=(Get-Hash (Join-Path (Split-Path $path) 'WorldsYetUnseen.Client.dll'))
    GroundworkD3D12Sha256=(Get-Hash (Join-Path (Split-Path $path) 'Groundwork.Rendering.D3D12.dll'))
    OS=[Environment]::OSVersion.VersionString; LogicalProcessors=[Environment]::ProcessorCount
    Processor=$env:PROCESSOR_IDENTIFIER; QpcFrequency=$frequency; Seed=42; Width=1920; Height=1080
    Pairs=$Pairs; IncludesFlight=$IncludeFlight.IsPresent; WarmupFramesPerRoute=40
    PresentMonSha256=(Get-Hash $capture); ExternalPresentMon=[bool]$external; StartUtc=[DateTime]::UtcNow
}
try {
    $metadata['Display'] = @(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,
        CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate)
} catch {
    $metadata['Display'] = @()
}
$metadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'measurement.json')

try {
    foreach ($name in $environment.Keys) { [Environment]::SetEnvironmentVariable($name, $environment[$name]) }
    if ($capture) {
        $options = @('--process_name', ('"' + $processName + '"'), '--session_name', $session,
            '--no_console_stats', '--no_track_input', '--v2_metrics', '--qpc_time',
            '--output_file', ('"' + $trace + '"'))
        $process = Start-Process -FilePath $capture -ArgumentList $options -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $output 'presentmon.stderr') -RedirectStandardOutput (Join-Path $output 'presentmon.stdout')
        Start-Sleep -Seconds 2
        if ($process.HasExited) { $traceError = 'PresentMon exited at startup; inspect presentmon.stderr.' }
    }
    if ($external) {
        $ready = Join-Path $external 'ready.json'
        $stopFile = Join-Path $external 'stop.requested'
        $resultFile = Join-Path $external 'collector-result.json'
        if (!(Test-Path -LiteralPath $ready -PathType Leaf) -or (Test-Path -LiteralPath $stopFile) -or
            (Test-Path -LiteralPath $resultFile)) { throw 'Expected a ready, fresh external PresentMon session.' }
        $externalSession = Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json
        if ($externalSession.Status -cne 'READY' -or $externalSession.Mode -cne 'Display' -or
            $externalSession.RunId -cnotmatch '^[a-f0-9]{32}$' -or
            (Split-Path -Leaf $external) -cne "p-$($externalSession.RunId)" -or $external -eq $output -or
            $externalSession.Target -cne $processName -or
            $externalSession.SessionName -cne "WyuPresent_$($externalSession.RunId)" -or
            $externalSession.PresentMonSha256 -ine $ExpectedPresentMonSha256 -or
            [long]$externalSession.QpcFrequency -ne $frequency -or [long]$externalSession.ReadyQpc -le 0) {
            throw 'Invalid external PresentMon ownership record.'
        }
        foreach ($inputPath in $external,$ready) {
            if ((Get-Item -LiteralPath $inputPath).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'External PresentMon handshake must use its original files.'
            }
        }
        if ([long]$externalSession.ReadyQpc -gt [Diagnostics.Stopwatch]::GetTimestamp()) { throw 'External PresentMon is not ready for this run.' }
        New-Item -ItemType File -Path (Join-Path $external 'runner.claimed') | Out-Null
        $externalClaimed = $true
        Copy-Item -LiteralPath $ready -Destination (Join-Path $output 'presentmon-ready.json')
    }
    $routeStartQpc = [Diagnostics.Stopwatch]::GetTimestamp()
    # Rotate and reverse a balanced four-arm order so thermal/ordering drift is observable.
    $orders = @([int[]](0,1,3,2), [int[]](1,2,0,3), [int[]](2,3,1,0), [int[]](3,0,2,1))
    for ($pair = 1; $pair -le $Pairs; $pair++) {
        foreach ($index in $orders[($pair-1) % $orders.Count]) {
            Invoke-Arm $pair $variants[$index] 'surface'
            if ($IncludeFlight) { Invoke-Arm $pair $variants[$index] 'flight' }
        }
    }
} finally {
    $routeEndQpc = [Diagnostics.Stopwatch]::GetTimestamp()
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    if ($process -and !$process.HasExited) {
        & $capture --session_name $session --terminate_existing_session *> (Join-Path $output 'presentmon-stop.log')
        if (!$process.WaitForExit(10000)) { $traceError = 'PresentMon did not finish; inspect the recording files.' }
    }
    if ($externalClaimed -and
        !(Test-Path -LiteralPath $resultFile) -and !(Test-Path -LiteralPath $stopFile)) {
        New-Item -ItemType File -Path $stopFile | Out-Null
        $deadline = [Diagnostics.Stopwatch]::StartNew()
        while (!(Test-Path -LiteralPath $resultFile -PathType Leaf) -and $deadline.Elapsed.TotalSeconds -lt 45) {
            Start-Sleep -Milliseconds 200
        }
    }
    Save-Raw
}

if ($externalSession) {
    if (!(Test-Path -LiteralPath $resultFile -PathType Leaf)) {
        $traceError = 'External PresentMon did not provide a terminal status.'
    } else {
        foreach ($inputPath in $resultFile,$trace) {
            if ((Test-Path -LiteralPath $inputPath -PathType Leaf) -and
                ((Get-Item -LiteralPath $inputPath).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'External PresentMon result must use original files.'
            }
        }
        Copy-Item -LiteralPath $resultFile -Destination (Join-Path $output 'presentmon-collector-result.json')
        $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
        if ($result.Status -cne 'CAPTURED' -or $result.RunId -cne $externalSession.RunId -or
            $result.SessionName -cne $externalSession.SessionName -or $result.Target -cne $externalSession.Target -or
            $result.PresentMonSha256 -ine $ExpectedPresentMonSha256 -or $result.CollectorExitCode -ne 0 -or
            [long]$result.CaptureStartQpc -ne [long]$externalSession.ReadyQpc -or [long]$result.CaptureStartQpc -gt $routeStartQpc -or
            [long]$result.CaptureEndQpc -lt $routeEndQpc -or
            $result.CsvRows -lt 1 -or $result.CsvSha256 -notmatch '^[a-fA-F0-9]{64}$') {
            $traceError = 'External PresentMon result is incomplete or does not cover this run.'
        } elseif ((Get-Hash $trace) -ine $result.CsvSha256) {
            $traceError = 'External PresentMon CSV was missing or changed.'
        } else {
            $archive = Join-Path $output 'presentmon.csv'
            Copy-Item -LiteralPath $trace -Destination $archive
            if ((Get-Hash $archive) -ine $result.CsvSha256) { $traceError = 'Copied PresentMon CSV hash mismatch.' }
            else { $trace = $archive }
        }
    }
}
$valid = @($runs | Where-Object ReplayPassed | ForEach-Object Run)
$presentSamples = [Collections.Generic.List[object]]::new()
$visibleCrossings = [Collections.Generic.List[object]]::new()
if (($capture -or $external) -and !$traceError -and (Test-Path -LiteralPath $trace -PathType Leaf)) {
    $presents = Import-Csv -LiteralPath $trace | Group-Object ProcessID -AsHashTable -AsString
    foreach ($batch in ($frames | Where-Object { $_.Run -in $valid } | Group-Object Run)) {
        $route = @($batch.Group)
        $rows = @($presents[[string]$route[0].Pid] | Where-Object { $_.CPUStartQPC -match '^\d+$' } |
            Sort-Object { [long]$_.CPUStartQPC })
        $index = 0
        foreach ($row in $rows) {
            $qpc = [long]$row.CPUStartQPC
            if ($qpc -lt $route[0].SubmitQpc -or $qpc -gt $route[-1].ReceiptQpc) { continue }
            while ($index + 1 -lt $route.Count -and $route[$index+1].SubmitQpc -le $qpc) { $index++ }
            $sample = $route[$index]
            # Match a clock window, not an exact Present/frame ID. Retain raw ETW rows.
            $presentSamples.Add([pscustomobject]@{
                Run=$sample.Run; Pair=$sample.Pair; Variant=$sample.Variant; Phase=$sample.Phase
                Warmup=$sample.Warmup; Pid=$sample.Pid; CpuStartQpc=$qpc; SyncInterval=$row.SyncInterval
                PresentMode=$row.PresentMode; SwapChainAddress=$row.PSObject.Properties['SwapChainAddress']?.Value
                GpuBusyMs=Parse-Milliseconds $row @('GPUBusy','MsGPUBusy')
                ApplicationCpuBusyMs=Parse-Milliseconds $row @('CPUBusy','MsCPUBusy')
                DisplayLatencyMs=Parse-Milliseconds $row @('DisplayLatency')
                DisplayedMs=Parse-Milliseconds $row @('DisplayedTime')
            })
        }
        foreach ($crossing in @($crossings | Where-Object Run -ceq $batch.Name)) {
            $peak = $null
            foreach ($row in $rows) {
                $latency = Parse-Milliseconds $row @('DisplayLatency')
                $duration = Parse-Milliseconds $row @('DisplayedTime')
                if ($null -eq $latency -or $null -eq $duration -or $duration -le 0) { continue }
                $displayStart = [long]$row.CPUStartQPC + $latency * $frequency / 1000
                $displayEnd = $displayStart + $duration * $frequency / 1000
                if ($displayStart -lt $crossing.EndQpc -and $displayEnd -gt $crossing.StartQpc -and
                    ($null -eq $peak -or $duration -gt $peak)) { $peak = $duration }
            }
            $visibleCrossings.Add([pscustomobject]@{
                Run=$crossing.Run; Pair=$crossing.Pair; Variant=$crossing.Variant; Phase=$crossing.Phase
                Step=$crossing.Step; Generation=$crossing.Generation; StartQpc=$crossing.StartQpc; EndQpc=$crossing.EndQpc
                LongestOverlappingDisplayIntervalMs=$peak
            })
        }
    }
}
$presentSamples | Export-Csv -LiteralPath (Join-Path $output 'presents-by-route-window.csv') -NoTypeInformation
$visibleCrossings | Export-Csv -LiteralPath (Join-Path $output 'crossing-display-intervals.csv') -NoTypeInformation

function Percentile($Sorted, [double]$P) {
    $Sorted[[int][Math]::Ceiling($Sorted.Count * $P) - 1]
}

function Summarize($Samples, [string[]]$Metrics, [string]$Scope) {
    $phases = @($Samples | ForEach-Object Phase | Sort-Object -Unique)
    foreach ($variant in $variants.Name) {
        foreach ($phase in $phases) {
            $selected = @($Samples | Where-Object { $_.Variant -ceq $variant -and $_.Phase -ceq $phase -and !$_.Warmup })
            foreach ($metric in $Metrics) {
                $values = @($selected | ForEach-Object { $_.$metric } | Where-Object { $null -ne $_ } | Sort-Object)
                if (!$values.Count) { continue }
                [pscustomobject]@{ Source=$Scope; Variant=$variant; Phase=$phase; Metric=$metric; Samples=$values.Count
                    P50=Percentile $values .50; P95=Percentile $values .95; P99=Percentile $values .99; Max=$values[-1] }
            }
        }
    }
}
$summary = @(
    Summarize @($frames | Where-Object { $_.Run -in $valid }) @('SubmitToReceiptMs','ReceiptIntervalMs','TextureNativeHighWaterBytes','TextureStagingBytes') 'renderer-observer'
    if ($presentSamples.Count) {
        Summarize $presentSamples @('GpuBusyMs','ApplicationCpuBusyMs','DisplayedMs') 'PresentMon-ETW'
    }
)
$summary | Export-Csv -LiteralPath (Join-Path $output 'summary.csv') -NoTypeInformation
$runsWithDisplayedFrames = @($presentSamples | Where-Object { $_.DisplayedMs -gt 0 -and $null -ne $_.DisplayLatencyMs } |
    Select-Object -ExpandProperty Run -Unique)
[ordered]@{
    EvidenceLabel=$EvidenceLabel; CompleteReplayCount=$valid.Count; InvalidReplayCount=$runs.Count-$valid.Count
    PresentMonRows=$presentSamples.Count; PresentMonError=$traceError
    RoutesWithDisplayedFrames=$runsWithDisplayedFrames.Count
    CrossingsWithDisplayIntervals=@($visibleCrossings | Where-Object { $null -ne $_.LongestOverlappingDisplayIntervalMs }).Count
    ExpectedCrossings=$crossings.Count
    CompleteReferenceAcceptance=$false
    MeasurementLimits=@('Renderer receipt is an observer interval, separate from display timing.',
        'PresentMon GPU Busy measures active GPU time and application CPU Busy is not render-owner CPU time.',
        'Script covers the ordinary surface route, its turn/rebase and optional scripted flight. Dedicated dense/open scenarios and mixed-load WP11 acceptance are separate.',
        'Texture backing high-water comes from native texture diagnostics. Staging is sampled and retirement is combined with native current backing.')
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'qualification.json')
$summary | Where-Object { $_.Metric -in 'SubmitToReceiptMs','GpuBusyMs','DisplayedMs' } | Format-Table
Write-Host "Orbit PBR evidence saved in $output."
if ($runs.Count -ne $valid.Count) { exit 1 }
if ($external -and ($traceError -or $runsWithDisplayedFrames.Count -ne $valid.Count -or
    @($visibleCrossings | Where-Object { $null -ne $_.LongestOverlappingDisplayIntervalMs }).Count -ne $crossings.Count -or
    $crossings.Count -eq 0)) {
    Write-Error 'External display capture did not cover every replay and crossing.'
}
