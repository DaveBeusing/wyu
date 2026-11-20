param(
    [string]$Assembly = (Join-Path $PSScriptRoot '../artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll'),
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('hardware', 'warp')][string]$Adapter = 'hardware',
    [string]$ExpectedClientSha256,
    [string]$ExpectedRendererSha256,
    [string]$RendererSourceCommit,
    [switch]$Smoke
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $Smoke -and $Adapter -ne 'hardware') { throw 'Full qualification requires the hardware adapter.' }
$client = (Resolve-Path -LiteralPath $Assembly).Path
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a fresh evidence directory.' }
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$sourceCommit = & git -C $repository rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the WYU source commit.' }
$workingTree = @(& git -C $repository status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the WYU working tree.' }
$actualClientHash = (Get-FileHash -LiteralPath $client).Hash
$actualRendererHash = (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $client) 'Groundwork.Rendering.D3D12.dll')).Hash
if (-not $Smoke) {
    if ($workingTree.Count) { throw 'Full measurements require a clean WYU working tree.' }
    if ($ExpectedClientSha256 -notmatch '^[a-fA-F0-9]{64}$' -or $ExpectedRendererSha256 -notmatch '^[a-fA-F0-9]{64}$' -or
        $RendererSourceCommit -notmatch '^[a-fA-F0-9]{40}$') { throw 'Full measurements require independently recorded client/renderer SHA256 and renderer source commit.' }
}
if ($ExpectedClientSha256 -and $ExpectedClientSha256 -ine $actualClientHash) { throw 'Client assembly hash mismatch.' }
if ($ExpectedRendererSha256 -and $ExpectedRendererSha256 -ine $actualRendererHash) { throw 'Renderer assembly hash mismatch.' }
$root = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$invariant = [Globalization.CultureInfo]::InvariantCulture
$pairs = if ($Smoke) { 1 } else { 5 }
$replays = if ($Smoke) { 1 } else { 6 }
$count = if ($Smoke) { 8 } else { 400 }
$deadline = if ($Smoke) { 210000 } else { 660000 }
$attempts = [Collections.Generic.List[object]]::new()
$samples = [Collections.Generic.List[object]]::new()
$handoffs = [Collections.Generic.List[object]]::new()
$identities = @{}
$environmentNames = 'WYU_PACKAGE_G_MEASURE','WYU_SKY_MEASURE_SMOKE','WYU_SKY_MEASURE_PHASES',
    'WYU_PACKAGE_G','WYU_PACKAGE_G_SMOKE','WYU_PACKAGE_E','WYU_WP13_MEASURE','WYU_WP15',
    'WYU_WP15_PROBE','WYU_WP16','WYU_WP16_SMOKE','WYU_R09_LATENCY','WYU_HOST_RETRY_SMOKE',
    'WYU_R09_RECOVERY_AT_HANDOFF','WYU_PACKAGE_E_MEASURE','WYU_PACKAGE_E_TIMESTAMP_PAIRS'
$previous = @{}
foreach ($name in $environmentNames) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
$hashes = Get-ChildItem -LiteralPath (Split-Path -Parent $client) -Filter '*.dll' |
    ForEach-Object { [pscustomobject]@{ File=$_.Name; SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash } }
[pscustomobject]@{
    Schema=1; Smoke=[bool]$Smoke; Qualified=$false; Adapter=$Adapter; Assembly=$client
    StartedUtc=[DateTime]::UtcNow.ToString('O'); OS=[Environment]::OSVersion.VersionString
    Runtime=(& dotnet --version); Hashes=@($hashes); Pairs=$pairs; FlightsPerPairPerVariant=$replays
    SourceCommit=$sourceCommit; WorkingTreeStatus=$workingTree; RendererSourceCommit=$RendererSourceCommit
    ExpectedClientSha256=$ExpectedClientSha256; ExpectedRendererSha256=$ExpectedRendererSha256
    Provenance='Operator-identified diagnostic assemblies. Hook binding and recorded hashes do not establish immutable archive approval.'
    GpuFrameInterval='Clear through last draw pass in one graphics command list, excluding separate uploads and native Present. Phase geometry is held fixed.'
    PresentationConfiguration='Native DXGI Present syncInterval=1 flags=0. Actual compositor/scanout mode is not captured by this runner.'
    SamplesPerPhasePerRun=$count; Seeds=@(42,20261111); SkyProfile='wyu.far-horizon.atmosphere.v2'
    Flags=@{ B='on'; E='on'; CanonicalG='on'; WP15='off'; Props='off' }
    OpenGates=@('Reference hardware identity and production limits must be supplied by the operator.',
        'Combined atmosphere preparation/recording CPU and total heap allocation.',
        'Driver-aligned incremental peak GPU memory including overlapping handoff resources.',
        'Protected movement/edit and speculative CPU/GPU pressure qualification.',
        'Published immutable package provenance and installed flight/Continue acceptance.')
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'manifest.json')

function Number($value) {
    $number = [Convert]::ToDouble($value, $invariant)
    if (-not [double]::IsFinite($number) -or $number -lt 0) { throw 'Invalid timing value.' }
    $number
}

function Percentile($values, [double]$percentile) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -eq 0) { throw 'No percentile samples.' }
    $sorted[[int][Math]::Ceiling($sorted.Count * $percentile) - 1]
}

function Invoke-Replay([int]$Pair, [int]$Replay, [string]$Mode) {
    $prefix = "$Pair-$Replay-$Mode"
    $world = Join-Path $root "$prefix-world"
    $log = Join-Path $root "$prefix.stdout.log"
    $errorLog = Join-Path $root "$prefix.stderr.log"
    $seed = if ($Replay % 2) { 42 } else { 20261111 }
    $env:WYU_PACKAGE_G_MEASURE = $Mode
    $env:WYU_SKY_MEASURE_PHASES = if ($Replay -eq 1) { '1' } else { '0' }
    $process = $null
    $stdout = $null
    $stderr = $null
    $outputCopy = $null
    $errorCopy = $null
    $exitCode = $null
    $started = $false
    $ownedProcessId = 0
    $passed = $false
    $failure = ''
    $primaryFailure = $null
    $cleanupFailures = [Collections.Generic.List[string]]::new()
    try {
        New-Item -ItemType Directory -Path $world | Out-Null
        New-Item -ItemType File -Path (Join-Path $world 'wyu-volume-smoke.fixture') | Out-Null
        if ((Get-FileHash -LiteralPath $client).Hash -cne $actualClientHash -or
            (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $client) 'Groundwork.Rendering.D3D12.dll')).Hash -cne $actualRendererHash) {
            throw 'Measured assemblies changed during the run.'
        }
        $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @($client,'--volume-flight-smoke','flight',$world,$Adapter,[string]$seed)) {
            $start.ArgumentList.Add($argument)
        }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $start
        if (-not $process.Start()) { throw 'Could not start the owned sky replay.' }
        $started = $true
        $ownedProcessId = $process.Id
        $stdout = [IO.FileStream]::new($log, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        $stderr = [IO.FileStream]::new($errorLog, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        $outputCopy = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $errorCopy = $process.StandardError.BaseStream.CopyToAsync($stderr)
        if (-not $process.WaitForExit($deadline)) {
            throw "Replay exceeded its bounded deadline. See $log and $errorLog."
        }
        $exitCode = $process.ExitCode
        if (-not $outputCopy.Wait(10000) -or -not $errorCopy.Wait(10000)) { throw 'Replay log streams did not drain.' }
        $stdout.Dispose()
        $stderr.Dispose()
        $lines = @(Get-Content -LiteralPath $log)
        if ($exitCode -ne 0 -or $lines -cnotcontains 'PASS: WYU WP11 exploration, rebase and native shutdown.') {
            throw "Replay failed with exit $exitCode. See $log and $errorLog."
        }
        if (-not $Smoke -and -not ($lines -match '^D3D12 adapter: .*software=False\.')) {
            throw 'Full replay did not identify a hardware adapter.'
        }
        $rows = @($lines | Where-Object { $_.StartsWith('PACKAGE G MEASURE ') } |
            ForEach-Object { $_.Substring('PACKAGE G MEASURE '.Length) | ConvertFrom-Json })
        $owners = @($rows | Where-Object kind -CEQ owner)
        if ($owners.Count -ne 3 -or @($owners | Where-Object { $_.mode -cne $Mode -or $_.engineHash -cne $actualRendererHash }).Count) {
            throw 'Missing or inconsistent near/far/near owner records.'
        }
        foreach ($direction in 'to-orbit','to-ground') {
            $handoff = @($rows | Where-Object { $_.kind -ceq 'handoff' -and $_.direction -ceq $direction })
            if ($handoff.Count -ne 1 -or $handoff[0].end -le $handoff[0].start -or $handoff[0].fence -le 0 -or
                $handoff[0].frequency -le 0 -or $handoff[0].sequence -le 0 -or $handoff[0].sourceFrameSequence -ne $handoff[0].sequence) {
                throw "Missing valid native Present handoff for $direction."
            }
            $h = $handoff[0]
            $handoffs.Add([pscustomobject]@{ Pair=$Pair; Replay=$Replay; Mode=$Mode; Seed=$seed; Direction=$direction
                Owner=$h.owner; Fence=$h.fence; Sequence=$h.sequence; SourceFrameSequence=$h.sourceFrameSequence
                StartQpc=$h.start; EndQpc=$h.end; Frequency=$h.frequency
                Milliseconds=1000.0 * ($h.end - $h.start) / $h.frequency })
        }
        $trace = @($lines | Where-Object { $_.StartsWith('WP11 FLIGHT ') -or $_.StartsWith('WP11 LANDED ') })
        if ($trace.Count -lt 4) { throw 'Missing deterministic flight/landing trace.' }
        $identityKey = "$Pair-$Replay-route"
        $routeIdentity = $trace -join "`n"
        if ($identities.ContainsKey($identityKey) -and $identities[$identityKey] -cne $routeIdentity) {
            throw 'The G-off/on routes changed seed, clock, geometry or landing state.'
        }
        $identities[$identityKey] = $routeIdentity
        if ($Replay -eq 1) {
            $gpu = @{}
            $cpu = @{}
            foreach ($row in $rows) {
                if ($row.kind -ceq 'gpu') {
                    $key = "$($row.owner)-$($row.sequence)"
                    if ($gpu.ContainsKey($key)) { throw 'Duplicate retired GPU timing.' }
                    $gpu.Add($key, $row)
                }
                if ($row.kind -ceq 'record') {
                    $key = "$($row.owner)-$($row.sequence)"
                    if ($cpu.ContainsKey($key)) { throw 'Duplicate command recording timing.' }
                    $cpu.Add($key, $row)
                }
            }
            foreach ($phase in 'surface','ascent','orbit') {
                $window = @($rows | Where-Object { $_.kind -ceq 'phase' -and $_.phase -ceq $phase })
                if ($window.Count -ne 1) { throw "Missing $phase sequence window." }
                $w = $window[0]
                if ($w.last - $w.first + 1 -ne $count -or
                    (-not $Smoke -and ($w.width -ne 1920 -or $w.height -ne 1080))) { throw 'Unexpected sample window or resolution.' }
                $identityKey = "$Pair-$phase"
                if ($identities.ContainsKey($identityKey) -and $identities[$identityKey] -cne $w.identity) {
                    throw "Canonical $phase camera/clock/terrain changed between variants."
                }
                $identities[$identityKey] = $w.identity
                for ($i = [ulong]$w.first; $i -le [ulong]$w.last; $i++) {
                    $key = "$($w.owner)-$i"
                    if (-not $gpu.ContainsKey($key) -or -not $cpu.ContainsKey($key)) { throw "Missing retired native sample $key." }
                    $g = $gpu[$key]
                    $c = $cpu[$key]
                    if ([bool]$g.hasSky -ne ($Mode -ceq 'on')) { throw 'The measured sky pass does not match the variant.' }
                    $frame = Number $g.frameMicroseconds
                    if ($frame -le 0) { throw 'Zero GPU frame timestamp interval.' }
                    $samples.Add([pscustomobject]@{ Pair=$Pair; Replay=$Replay; Mode=$Mode; Phase=$phase; Seed=$seed
                        Owner=$w.owner; Sequence=$i; Identity=$w.identity; Width=$w.width; Height=$w.height
                        FrameMs=$frame / 1000; SkyMs=(Number $g.skyMicroseconds) / 1000
                        RecordMs=(Number $c.recordMicroseconds) / 1000; AllocatedBytes=[long]$c.allocatedBytes; Draws=[int]$c.draws })
                }
            }
        }
        $passed = $true
    } catch {
        $primaryFailure = $_
        $failure = $_.Exception.Message
    } finally {
        if ($started) {
            try {
                if (-not $process.HasExited) {
                    $process.Kill($true)
                    if (-not $process.WaitForExit(10000)) { throw 'Owned replay process did not stop during cleanup.' }
                }
            } catch { $cleanupFailures.Add("process stop: $($_.Exception.Message)") }
        }
        foreach ($copy in @(@('stdout', $outputCopy), @('stderr', $errorCopy))) {
            try {
                if ($copy[1] -and -not $copy[1].Wait(10000)) { throw 'Log stream did not drain during cleanup.' }
            } catch { $cleanupFailures.Add("$($copy[0]) drain: $($_.Exception.Message)") }
        }
        foreach ($stream in @(@('stdout', $stdout), @('stderr', $stderr), @('process', $process))) {
            try { if ($stream[1]) { $stream[1].Dispose() } }
            catch { $cleanupFailures.Add("$($stream[0]) dispose: $($_.Exception.Message)") }
        }
        $attempt = [pscustomobject]@{ Pair=$Pair; Replay=$Replay; Mode=$Mode; Seed=$seed
            Started=$started; ProcessId=$ownedProcessId; ExitCode=$exitCode
            Passed=($passed -and $cleanupFailures.Count -eq 0); Failure=$failure
            CleanupFailures=($cleanupFailures -join ' | '); Stdout=$log; Stderr=$errorLog }
        $attempts.Add($attempt)
        foreach ($export in @(@('attempts', $attempts), @('samples', $samples), @('handoffs', $handoffs))) {
            try { $export[1] | Export-Csv -LiteralPath (Join-Path $root "$($export[0]).csv") -NoTypeInformation }
            catch { $cleanupFailures.Add("$($export[0]) evidence: $($_.Exception.Message)") }
        }
        $attempt.CleanupFailures = $cleanupFailures -join ' | '
        $attempt.Passed = $passed -and $cleanupFailures.Count -eq 0
    }
    if ($primaryFailure) { throw $primaryFailure }
    if ($cleanupFailures.Count) { throw "Replay cleanup/evidence failed: $($cleanupFailures -join ' | ')" }
    Write-Host "Sky replay $prefix seed=$seed passed."
}

try {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $null) }
    $env:WYU_SKY_MEASURE_SMOKE = if ($Smoke) { '1' } else { '0' }
    $env:WYU_PACKAGE_G, $env:WYU_PACKAGE_G_SMOKE, $env:WYU_PACKAGE_E, $env:WYU_WP13_MEASURE = 'on','1','on','on'
    $env:WYU_WP15, $env:WYU_WP16 = 'off','off'
    for ($pair = 1; $pair -le $pairs; $pair++) {
        $modes = if ($pair % 2) { 'off','on' } else { 'on','off' }
        foreach ($mode in $modes) {
            for ($replay = 1; $replay -le $replays; $replay++) { Invoke-Replay $pair $replay $mode }
        }
    }
} catch {
    [pscustomobject]@{ Status='BLOCKED'; FullQualification=$false; Failure=$_.Exception.Message
        CleanupFailures=@($attempts | Where-Object { $_.CleanupFailures } | Select-Object Pair,Replay,Mode,CleanupFailures)
        Attempts=$attempts.Count; FailedAttempts=@($attempts | Where-Object { -not $_.Passed }).Count
        RequiredAttempts=2*$pairs*$replays; RemainingGates='See manifest.json OpenGates.' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'result.json')
    throw
} finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
}

$summary = [Collections.Generic.List[object]]::new()
foreach ($phase in 'surface','ascent','orbit') {
    foreach ($pair in 0..$pairs) {
        $off = @($samples | Where-Object { $_.Phase -ceq $phase -and $_.Mode -ceq 'off' -and ($pair -eq 0 -or $_.Pair -eq $pair) })
        $on = @($samples | Where-Object { $_.Phase -ceq $phase -and $_.Mode -ceq 'on' -and ($pair -eq 0 -or $_.Pair -eq $pair) })
        $expected = if ($pair -eq 0) { $pairs * $count } else { $count }
        if ($off.Count -ne $expected -or $on.Count -ne $expected) { throw "Incomplete $phase GPU denominator." }
        $delta95 = 100 * ((Percentile $on.FrameMs .95) / (Percentile $off.FrameMs .95) - 1)
        $delta99 = 100 * ((Percentile $on.FrameMs .99) / (Percentile $off.FrameMs .99) - 1)
        $sky95 = Percentile $on.SkyMs .95
        $sky99 = Percentile $on.SkyMs .99
        $draws = ($on.Draws | Measure-Object -Maximum).Maximum
        $allocated = ($on.AllocatedBytes | Measure-Object -Maximum).Maximum
        $record99 = Percentile $on.RecordMs .99
        $summary.Add([pscustomobject]@{ Kind='GPU'; Phase=$phase; Pair=$pair; OffCount=$off.Count; OnCount=$on.Count
            FrameP95DeltaPercent=$delta95; FrameP99DeltaPercent=$delta99; SkyP95Ms=$sky95; SkyP99Ms=$sky99
            RecordP99Ms=$record99; RecordMaxAllocatedBytes=$allocated; SkyDrawsMax=$draws
            Passed=($delta95 -le 5 -and $delta99 -le 8 -and $sky95 -le .8 -and $sky99 -le 1.2 -and $record99 -le .10 -and $draws -eq 1 -and $allocated -eq 0) })
    }
}
foreach ($direction in 'to-orbit','to-ground') {
    foreach ($pair in 0..$pairs) {
        $off = @($handoffs | Where-Object { $_.Direction -ceq $direction -and $_.Mode -ceq 'off' -and ($pair -eq 0 -or $_.Pair -eq $pair) })
        $on = @($handoffs | Where-Object { $_.Direction -ceq $direction -and $_.Mode -ceq 'on' -and ($pair -eq 0 -or $_.Pair -eq $pair) })
        $expected = if ($pair -eq 0) { $pairs * $replays } else { $replays }
        if ($off.Count -ne $expected -or $on.Count -ne $expected) { throw "Incomplete $direction handoff denominator." }
        $delta = 100 * ((Percentile $on.Milliseconds .95) / (Percentile $off.Milliseconds .95) - 1)
        $summary.Add([pscustomobject]@{ Kind='handoff'; Phase=$direction; Pair=$pair; OffCount=$off.Count; OnCount=$on.Count
            FrameP95DeltaPercent=$delta; FrameP99DeltaPercent=$null; SkyP95Ms=$null; SkyP99Ms=$null
            RecordP99Ms=$null; RecordMaxAllocatedBytes=$null; SkyDrawsMax=$null; Passed=($delta -le 10) })
    }
}
$summary | Export-Csv -LiteralPath (Join-Path $root 'summary.csv') -NoTypeInformation
$summary | Where-Object Pair -EQ 0 | Format-Table -AutoSize
$failed = @($summary | Where-Object { -not $_.Passed }).Count
[pscustomobject]@{ Status=if ($Smoke) { 'SMOKE ONLY' } elseif ($failed) { 'CHANGES REQUIRED' } else { 'MEASURED GATES PASSED' }
    FullQualification=$false; Attempts=$attempts.Count; FailedAttempts=@($attempts | Where-Object { -not $_.Passed }).Count
    RequiredAttempts=2*$pairs*$replays; FailedSummaryRows=$failed; RemainingGates='See manifest.json OpenGates.' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'result.json')
Write-Host "Evidence $root"
if ($Smoke) { Write-Host 'SMOKE ONLY. Counts and resolution do not qualify the reference budgets.' }
elseif ($failed) { throw 'A required measured sky/frame/handoff budget failed.' }
