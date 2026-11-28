param(
    [string]$Assembly = (Join-Path $PSScriptRoot '../artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll'),
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('hardware', 'warp')][string]$Adapter = 'hardware',
    [ValidateSet('far-horizon', 'orbit-combined')][string]$Composition = 'far-horizon',
    [string]$ExpectedClientSha256,
    [string]$ExpectedRendererSha256,
    [string]$ExpectedRenderingSha256,
    [string]$RendererSourceCommit,
    [switch]$Smoke,
    [switch]$Diagnostics,
    [switch]$DiagnosticProbe,
    [switch]$GpuSchedulingTrace,
    [ValidateSet('Direct8x4', 'ScatteringAtlas', 'TiledScatteringAtlas')][string]$SkyBackend = 'Direct8x4',
    [switch]$AtlasPrefilter,
    [switch]$LookupPrototype,
    [switch]$FullLookupSeries,
    [switch]$ReuseLookupResources,
    [switch]$GpuPassBreakdown
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($DiagnosticProbe -and $Smoke) { throw 'A diagnostic probe uses full-size native windows, not Smoke.' }
if ($FullLookupSeries -and ($Smoke -or $DiagnosticProbe -or $GpuSchedulingTrace)) {
    throw 'FullLookupSeries cannot use Smoke, DiagnosticProbe or GpuSchedulingTrace.'
}
if ($FullLookupSeries) { $LookupPrototype = $true }
if ($GpuSchedulingTrace -and -not $DiagnosticProbe) { throw 'GpuSchedulingTrace requires DiagnosticProbe; it is diagnostic evidence only.' }
if ($GpuPassBreakdown -and -not $DiagnosticProbe) { throw 'GpuPassBreakdown requires DiagnosticProbe.' }
if ($LookupPrototype -and -not ($DiagnosticProbe -or $Smoke -or $FullLookupSeries)) {
    throw 'LookupPrototype requires DiagnosticProbe or Smoke, or the explicit FullLookupSeries diagnostic mode.'
}
if ($ReuseLookupResources -and -not $LookupPrototype) { throw 'ReuseLookupResources requires the diagnostic Lookup prototype.' }
if ($LookupPrototype -and $PSBoundParameters.ContainsKey('SkyBackend')) {
    throw 'Choose either SkyBackend or LookupPrototype; their backend selections cannot be combined.'
}
$selectedSkyBackend = if ($LookupPrototype) { 'LookupPrototype' } else {
    switch ($SkyBackend) {
        'ScatteringAtlas' { 'ScatteringAtlas' }
        'TiledScatteringAtlas' { 'TiledScatteringAtlas' }
        default { 'Direct8x4' }
    }
}
$tiledSky = $selectedSkyBackend -ceq 'TiledScatteringAtlas'
$expectedOnDraws = if ($tiledSky) { 2 } else { 1 }
if ($selectedSkyBackend -ceq 'ScatteringAtlas' -and -not ($DiagnosticProbe -or $Smoke)) {
    throw 'The experimental ScatteringAtlas backend requires DiagnosticProbe or Smoke.'
}
if ($AtlasPrefilter -and $selectedSkyBackend -cnotin 'ScatteringAtlas', 'TiledScatteringAtlas') {
    throw 'AtlasPrefilter requires the explicit ScatteringAtlas or TiledScatteringAtlas backend.'
}
if ($tiledSky) { $AtlasPrefilter = $true }
$inheritedSkyOptions = @(Get-ChildItem Env: | Where-Object {
    $_.Name -match '^GROUNDWORK_SKY_' -and $_.Name -ine 'GROUNDWORK_SKY_REQUIRE_DEBUG'
})
$activeInheritedSkyOptions = @($inheritedSkyOptions | Where-Object { $_.Value -cnotin @('', '0') } | ForEach-Object Name)
if ($activeInheritedSkyOptions.Count) {
    throw "Unset inherited sky backend options ($($activeInheritedSkyOptions -join ', ')); select the backend explicitly through the runner."
}
if ($DiagnosticProbe -or $LookupPrototype -or $selectedSkyBackend -ceq 'ScatteringAtlas') { $Diagnostics = $true }
if ($Diagnostics) { . (Join-Path $PSScriptRoot 'ReferenceGpuDiagnostics.ps1') }
if ($GpuSchedulingTrace) { . (Join-Path $PSScriptRoot 'ReferenceSchedulingTrace.ps1') }
$Composition = $Composition.ToLowerInvariant()
$Adapter = $Adapter.ToLowerInvariant()
if ($tiledSky -and $Adapter -cne 'hardware') { throw 'TiledScatteringAtlas requires hardware VRS and mesh shaders.' }
if ($env:WYU_R09_LATENCY -eq '1') { throw 'Run R09 pressure qualification separately; this sky runner does not compose native pressure hooks.' }
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
$actualRenderingHash = (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $client) 'Groundwork.Rendering.dll')).Hash
if (-not $Smoke) {
    if ($workingTree.Count) { throw 'Full measurements require a clean WYU working tree.' }
    if ($ExpectedClientSha256 -notmatch '^[a-fA-F0-9]{64}$' -or $ExpectedRendererSha256 -notmatch '^[a-fA-F0-9]{64}$' -or
        $ExpectedRenderingSha256 -notmatch '^[a-fA-F0-9]{64}$' -or
        $RendererSourceCommit -notmatch '^[a-fA-F0-9]{40}$') { throw 'Full measurements require independently recorded client, Rendering and D3D12 SHA256 and renderer source commit.' }
}
if ($ExpectedClientSha256 -and $ExpectedClientSha256 -ine $actualClientHash) { throw 'Client assembly hash mismatch.' }
if ($ExpectedRendererSha256 -and $ExpectedRendererSha256 -ine $actualRendererHash) { throw 'Renderer assembly hash mismatch.' }
if ($ExpectedRenderingSha256 -and $ExpectedRenderingSha256 -ine $actualRenderingHash) { throw 'Rendering assembly hash mismatch.' }
$root = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$invariant = [Globalization.CultureInfo]::InvariantCulture
$pairs = if ($Smoke -or $DiagnosticProbe) { 1 } else { 5 }
$replays = if ($Smoke -or $DiagnosticProbe) { 1 } else { 6 }
$count = if ($Smoke) { 8 } else { 400 }
$deadline = if (-not $Smoke) { 660000 } elseif ($Adapter -eq 'warp' -and $Composition -ceq 'orbit-combined') { 390000 } else { 210000 }
$attempts = [Collections.Generic.List[object]]::new()
$samples = [Collections.Generic.List[object]]::new()
$handoffs = [Collections.Generic.List[object]]::new()
$nativeExtras = [Collections.Generic.List[object]]::new()
$identities = @{}
$environmentNames = 'WYU_PACKAGE_G_MEASURE','WYU_SKY_MEASURE_SMOKE','WYU_SKY_MEASURE_PHASES',
    'WYU_PACKAGE_G','WYU_PACKAGE_G_SMOKE','WYU_PACKAGE_E','WYU_WP13_MEASURE','WYU_WP15',
    'WYU_WP15_PROBE','WYU_WP16','WYU_WP16_SMOKE','WYU_R09_LATENCY','WYU_HOST_RETRY_SMOKE',
    'WYU_R09_RECOVERY_AT_HANDOFF','WYU_PACKAGE_E_MEASURE','WYU_PACKAGE_E_TIMESTAMP_PAIRS',
    'WYU_SKY_MEASURE_COMPOSITION','WYU_ORBIT_DEMO_SMOKE','WYU_ORBIT_PBR','WYU_ORBIT_BOULDER',
    'WYU_ORBIT_PBR_MEASURE','WYU_ORBIT_CAPTURE','WYU_WP15_FLIP','WYU_SKY_DIAGNOSTICS',
    'GROUNDWORK_TWILIGHT_PREVIEW','GROUNDWORK_SKY_LOOKUP_PROTOTYPE','GROUNDWORK_SKY_LOOKUP_REUSE_RESOURCES','WYU_SKY_GPU_PASS_BREAKDOWN',
    'GROUNDWORK_SKY_SCATTERING_ATLAS','GROUNDWORK_SKY_ATLAS_PREFILTER','GROUNDWORK_SKY_SPLIT_DRAW'
$environmentNames += @($inheritedSkyOptions | ForEach-Object Name)
$environmentNames = @($environmentNames | Sort-Object -Unique)
$previous = @{}
foreach ($name in $environmentNames) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
$hashes = Get-ChildItem -LiteralPath (Split-Path -Parent $client) -Filter '*.dll' |
    ForEach-Object { [pscustomobject]@{ File=$_.Name; SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash } }
[pscustomobject]@{
    Schema=3; HookSchema=2; Smoke=[bool]$Smoke; Qualified=$false; Adapter=$Adapter; Assembly=$client; Composition=$Composition
    ProcessDeadlineMilliseconds=$deadline
    Diagnostics=[bool]$Diagnostics; DiagnosticProbe=[bool]$DiagnosticProbe; FullLookupSeries=[bool]$FullLookupSeries
    GpuSchedulingTrace=[bool]$GpuSchedulingTrace
    GpuPassBreakdown=[bool]$GpuPassBreakdown
    SchedulingTrace='Opt-in per-replay DxgKrnl circular ETL; capture status and commands are in each scheduling directory. Event loss and full coverage are unverified.'
    TwilightPreview=$true; LookupPrototype=[bool]$LookupPrototype; ReuseLookupResources=[bool]$ReuseLookupResources
    SkyBackendOn=$selectedSkyBackend; SkyBackendOff='Disabled'; AtlasPrefilter=[bool]$AtlasPrefilter
    ExpectedSkyDrawsOn=$expectedOnDraws; ExpectedSkyDrawsOff=0
    SkyImplementation=$(switch ($selectedSkyBackend) {
        'LookupPrototype' { 'Experimental fixed-size GPU lookup; visual approval pending; all build/update work charged.' }
        'ScatteringAtlas' { 'Experimental scattering atlas with exact fallbacks; sample-level routing is not inferred.' }
        'TiledScatteringAtlas' { 'Opt-in scattering atlas with hardware VRS and a tiled mesh correction draw.' }
        default { 'Direct 8x4 integration' }
    })
    SkyBackendEvidence='Per-replay native Atlas, prefilter and tiled mesh activation, Lookup owner binding and measured renderer hash. See attempts.csv and retained native draw counts.'
    AcceptanceRequiredPairs=5; AcceptanceRequiredFlights=60; AcceptanceSamplesPerPhasePerVariant=2000
    EvidenceKind=$(if ($FullLookupSeries) { 'DIAGNOSTIC ONLY: experimental lookup with full WYU counts and original measured limits.' }
        elseif ($Diagnostics) { 'DIAGNOSTIC ONLY: telemetry and/or reduced flight count; never acceptance evidence.' }
        else { 'Required measurement subset; all original gates apply.' })
    EventOutput='Bounded value buffers flushed after each phase/native endpoint; overflow fails, no sample selection.'
    StartedUtc=[DateTime]::UtcNow.ToString('O'); OS=[Environment]::OSVersion.VersionString
    Runtime=(& dotnet --version); Hashes=@($hashes); Pairs=$pairs; FlightsPerPairPerVariant=$replays
    SourceCommit=$sourceCommit; WorkingTreeStatus=$workingTree; RendererSourceCommit=$RendererSourceCommit
    ExpectedClientSha256=$ExpectedClientSha256; ExpectedRendererSha256=$ExpectedRendererSha256
    ExpectedRenderingSha256=$ExpectedRenderingSha256
    Provenance='Independently identified assemblies. Hook binding and recorded hashes do not establish immutable archive approval.'
    GpuFrameInterval='Clear through last draw pass in one graphics command list, excluding separate uploads and native Present. Phase geometry is held fixed.'
    PresentationConfiguration='Native DXGI Present syncInterval=1 flags=0. Actual compositor/scanout mode is not captured by this runner.'
    HeldPresentationWait='Exact renderer sequence acknowledgement, 30-second bound, fault/cancellation checks; no additional 8 ms polling.'
    SamplesPerPhasePerRun=$count; Seeds=@(42,20261111); SkyProfile='wyu.far-horizon.atmosphere.v2'
    Flags=@{ B='on'; E='on'; CanonicalG='on'; WP15=$(if ($Composition -ceq 'orbit-combined') { 'on' } else { 'off' })
        Props=$(if ($Composition -ceq 'orbit-combined') { 'on' } else { 'off' })
        OrbitPreset=($Composition -ceq 'orbit-combined'); OrbitPbr=($Composition -ceq 'orbit-combined'); Boulder=($Composition -ceq 'orbit-combined') }
    CpuInterval='Per-sequence sum of synchronous production atmosphere/sky/sunlight preparation and owner sky recording. Await, telemetry serialization, unrelated camera/terrain work are excluded.'
    NativeIdentity='GPU/record rows join by owner+native fence. Repeated logical sequences are retained; preparation is charged once to the lowest fence, later executions explicitly reuse it. Strict controlled windows reject native extras after preserving evidence.'
    OpenGates=@('Reference hardware identity and production limits must be supplied by the operator.',
        'Driver-aligned incremental peak GPU memory including overlapping handoff resources.',
        'Protected movement/edit and speculative CPU/GPU pressure qualification.',
        'Published immutable package provenance and installed flight/Continue acceptance.')
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'manifest.json')

function Number($value) {
    $number = [Convert]::ToDouble($value, $invariant)
    if (-not [double]::IsFinite($number) -or $number -lt 0) { throw 'Invalid timing value.' }
    $number
}

function Assert-SkyLookupOwners($Owners, [bool]$Requested, [bool]$ReuseRequested = $false) {
    if (@($Owners).Count -ne 3) { throw 'Missing lookup mode owner evidence.' }
    foreach ($owner in $Owners) {
        $mode = $owner.PSObject.Properties['lookupPrototype']
        if ($null -eq $mode -or $mode.Value -isnot [bool] -or $mode.Value -ne $Requested) {
            throw 'Lookup mode was not positively verified for every native owner.'
        }
        $reuse = $owner.PSObject.Properties['lookupResourceReuse']
        if (($null -eq $reuse -and $ReuseRequested) -or
            ($null -ne $reuse -and ($reuse.Value -isnot [bool] -or $reuse.Value -ne $ReuseRequested))) {
            throw 'Lookup resource reuse was not positively verified for every native owner.'
        }
    }
}

function Get-ObservedSkyBackend([string[]]$Lines, [string]$Mode, [string]$ExpectedBackend, [bool]$ExpectPrefilter) {
    $atlas = [bool]($Lines -cmatch '^SKY SCATTERING ATLAS active(?: |$)')
    $prefilter = [bool]($Lines -cmatch '^SKY SCATTERING PREFILTER active(?:,| |$)')
    $legacySolar = [bool]($Lines -cmatch '^SKY SOLAR TABLE active(?: |$)')
    $vrs = [bool]($Lines -cmatch '^SKY ADAPTIVE VRS active(?: |$)')
    $split = [bool]($Lines -cmatch '^SKY ADAPTIVE VRS active tier 2, 16px tiles, R8_UINT \d+x\d+ viewport, 65536 native DEFAULT bytes, split sky draw\.$')
    $mesh = [bool]($Lines -cmatch '^SKY MESH CORRECTION active(?:,| |$)')
    if ($legacySolar) { throw 'Unexpected legacy solar-depth backend activity.' }
    if ($Mode -ceq 'off') {
        if ($atlas -or $prefilter -or $vrs -or $mesh) { throw 'Experimental sky backend active in the disabled-sky baseline.' }
        return 'Disabled'
    }
    if ($ExpectedBackend -ceq 'TiledScatteringAtlas') {
        if (!$atlas -or !$prefilter -or !$mesh -or !$split) {
            throw 'The tiled atlas did not report its required native atlas, prefilter, split VRS and mesh activity.'
        }
        return $ExpectedBackend
    }
    if ($vrs -or $mesh) { throw "Unexpected tiled sky activity in the selected $ExpectedBackend backend." }
    if ($ExpectedBackend -ceq 'ScatteringAtlas') {
        if (-not $atlas) { throw 'The requested scattering atlas did not report a submitted draw.' }
        if ($prefilter -ne $ExpectPrefilter) { throw 'Observed atlas prefilter activity differs from the selected mode.' }
    } elseif ($atlas -or $prefilter) {
        throw "Unexpected experimental sky activity in the selected $ExpectedBackend backend."
    }
    return $ExpectedBackend
}

function Get-SkyGpuInteger($Row, [string]$Field) {
    $property = $Row.PSObject.Properties[$Field]
    if ($null -eq $property) { throw "Missing GPU timing integer $Field." }
    $value = $property.Value
    if (($value -isnot [int] -and $value -isnot [long] -and $value -isnot [ulong] -and $value -isnot [bigint]) -or
        $value -lt 0 -or ($value -is [bigint] -and $value -gt [bigint][ulong]::MaxValue)) {
        throw "Invalid GPU timing integer $Field."
    }
    [ulong]$value
}

function Get-SkyPrepassIndex($Rows, [string]$Kind) {
    $index = @{}
    foreach ($row in $Rows) {
        if ($row.kind -cne $Kind) { continue }
        $owner = Get-SkyGpuInteger $row 'owner'
        $fence = Get-SkyGpuInteger $row 'fence'
        $sequence = Get-SkyGpuInteger $row 'sequence'
        if ($owner -eq 0 -or $fence -eq 0 -or $sequence -eq 0) { throw "Invalid $Kind timing identity." }
        $key = "$owner-$fence"
        if ($index.ContainsKey($key)) { throw "Duplicate $Kind timing fence." }
        $index.Add($key, $row)
    }
    return $index
}

function Assert-SkyPrepassTiming($Prepass, $FrameTicks, $PassTicks, $Execution, [string]$Phase, [string]$Mode) {
    $key = "$($Execution.Owner)-$($Execution.Fence)"
    $coarse = $Prepass[$key]
    $frame = $FrameTicks[$key]
    $pass = $PassTicks[$key]
    foreach ($row in $coarse, $frame, $pass) {
        if ($null -eq $row -or $row.sequence -ne $Execution.Sequence -or $row.phase -cne $Phase) {
            throw 'GPU pass timing evidence is missing or does not match the measured execution.'
        }
    }
    foreach ($field in 'terrainMicroseconds','nearShadowMicroseconds','farShadowMicroseconds') {
        $property = $coarse.PSObject.Properties[$field]
        if ($null -eq $property -or $property.Value -isnot [ValueType] -or $property.Value -is [bool]) {
            throw 'Missing numeric GPU pass interval.'
        }
        $null = Number $property.Value
    }
    $frameStart = Get-SkyGpuInteger $frame 'frameStart'
    $frameEnd = Get-SkyGpuInteger $frame 'frameEnd'
    $skyStart = Get-SkyGpuInteger $frame 'skyStart'
    $skyEnd = Get-SkyGpuInteger $frame 'skyEnd'
    $frequency = Get-SkyGpuInteger $frame 'frequency'
    $terrainStart = Get-SkyGpuInteger $pass 'terrainStart'
    $terrainEnd = Get-SkyGpuInteger $pass 'terrainEnd'
    $drawStart = Get-SkyGpuInteger $pass 'drawStart'
    $drawEnd = Get-SkyGpuInteger $pass 'drawEnd'
    if ($frequency -eq 0 -or $frameStart -eq 0 -or $frameStart -ge $frameEnd) {
        throw 'Invalid measured GPU frame timestamp interval.'
    }
    if ($Mode -ceq 'on') {
        if ($skyStart -lt $frameStart -or $skyEnd -gt $frameEnd -or $skyStart -gt $drawStart -or
            $drawStart -gt $drawEnd -or $drawEnd -gt $skyEnd) {
            throw 'GPU draw timestamps are missing or outside the measured sky interval.'
        }
    } elseif ($skyStart -ne 0 -or $skyEnd -ne 0 -or $drawStart -ne 0 -or $drawEnd -ne 0) {
        throw 'Unexpected GPU sky or draw timestamps in the disabled baseline.'
    }
    if (($terrainStart -ne 0 -or $terrainEnd -ne 0) -and
        ($terrainStart -lt $frameStart -or $terrainStart -gt $terrainEnd -or $terrainEnd -gt $frameEnd -or
            ($Mode -ceq 'on' -and $terrainEnd -gt $skyStart))) {
        throw 'Invalid GPU terrain timestamp interval.'
    }
    # Subtract the integer stamps before converting to double; GPU counters can exceed 2^53.
    $frameMs = 1000.0 * [double]([decimal]$frameEnd - [decimal]$frameStart) / $frequency
    $skyMs = 1000.0 * [double]([decimal]$skyEnd - [decimal]$skyStart) / $frequency
    if ([Math]::Abs($frameMs - $Execution.FrameMs) -gt 0.000000001 -or
        [Math]::Abs($skyMs - $Execution.SkyMs) -gt 0.000000001) {
        throw 'GPU timestamp intervals do not match the measured frame and sky durations.'
    }
    $terrainMicroseconds = 1000000.0 * [double]([decimal]$terrainEnd - [decimal]$terrainStart) / $frequency
    if ([Math]::Abs($terrainMicroseconds - $coarse.terrainMicroseconds) -gt 0.000001) {
        throw 'GPU terrain timestamps do not match the reported terrain duration.'
    }
    $Prepass.Remove($key)
    $FrameTicks.Remove($key)
    $PassTicks.Remove($key)
}

function Get-SkyLookupSplits($Rows) {
    $gpu=@{}; $record=@{}
    foreach ($row in $Rows) {
        if ($row.kind -notin @('lookup-gpu-split','lookup-record-split')) { continue }
        if ($row.owner -le 0 -or $row.fence -le 0 -or $row.sequence -le 0) { throw 'Invalid lookup timing identity.' }
        $entry=[pscustomobject]@{ Sequence=$row.sequence; Phase=$row.phase
            ComputeMs=(Number $row.computeMicroseconds)/1000; DrawMs=(Number $row.drawBarrierMicroseconds)/1000 }
        $target=if ($row.kind -ceq 'lookup-gpu-split') { $gpu } else { $record }
        $key="$($row.owner)-$($row.fence)"
        if ($target.ContainsKey($key)) { throw 'Duplicate lookup timing fence.' }
        $target[$key]=$entry
    }
    [pscustomobject]@{ Gpu=$gpu; Record=$record }
}

function Get-SkyLookupExecutionTiming($Splits, $Execution, [string]$Phase) {
    $key="$($Execution.Owner)-$($Execution.Fence)"
    if (-not $Splits.Gpu.ContainsKey($key) -or -not $Splits.Record.ContainsKey($key)) {
        throw 'Missing compute/draw timing evidence for a measured lookup execution.'
    }
    $gpu=$Splits.Gpu[$key]; $cpu=$Splits.Record[$key]
    if ($gpu.Sequence -ne $Execution.Sequence -or $cpu.Sequence -ne $Execution.Sequence -or
        $gpu.Phase -cne $Phase -or $cpu.Phase -cne $Phase -or
        [Math]::Abs($gpu.ComputeMs+$gpu.DrawMs-$Execution.SkyMs) -gt 0.000001 -or
        [Math]::Abs($cpu.ComputeMs+$cpu.DrawMs-$Execution.RecordMs) -gt 0.000001) {
        throw 'Lookup timing components do not match their original complete native interval.'
    }
    [pscustomobject]@{Gpu=$gpu;Cpu=$cpu}
}

function Percentile($values, [double]$percentile) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -eq 0) { throw 'No percentile samples.' }
    $sorted[[int][Math]::Ceiling($sorted.Count * $percentile) - 1]
}

function Get-SkyNativeExecutions($Rows, $Window, [string]$Mode, [ValidateRange(1,2)][int]$ExpectedOnDraws = 1) {
    $expectedDraws = if ($Mode -ceq 'on') { $ExpectedOnDraws } else { 0 }
    $gpu = @{}
    $cpu = @{}
    $preparation = @{}
    $bySequence = @{}
    foreach ($row in $Rows) {
        if ($row.kind -ceq 'gpu' -or $row.kind -ceq 'record') {
            if ([ulong]$row.fence -eq 0) { throw 'Native timing requires a nonzero fence.' }
            $key = "$($row.owner)-$($row.fence)"
            $index = if ($row.kind -ceq 'gpu') { $gpu } else { $cpu }
            if ($index.ContainsKey($key)) { throw "Duplicate native $($row.kind) fence $key." }
            $index.Add($key, $row)
            if ($row.kind -ceq 'gpu') {
                $logicalKey = "$($row.owner)-$($row.sequence)"
                if (-not $bySequence.ContainsKey($logicalKey)) { $bySequence.Add($logicalKey, [Collections.Generic.List[object]]::new()) }
                $bySequence[$logicalKey].Add($row)
            }
        }
        if ($row.kind -ceq 'prepare') {
            $key = "$($row.owner)-$($row.sequence)"
            if ($preparation.ContainsKey($key)) { throw 'Duplicate atmosphere preparation timing.' }
            $preparation.Add($key, $row)
        }
    }
    foreach ($key in @($gpu.Keys) + @($cpu.Keys) | Sort-Object -Unique) {
        if (-not $gpu.ContainsKey($key) -or -not $cpu.ContainsKey($key)) { throw "Missing native GPU/record pair for fence $key." }
        if ($gpu[$key].sequence -ne $cpu[$key].sequence -or $gpu[$key].phase -cne $cpu[$key].phase) {
            throw "Native fence $key has inconsistent sequence/phase."
        }
    }
    $joined = [Collections.Generic.List[object]]::new()
    for ($sequence = [ulong]$Window.first; $sequence -le [ulong]$Window.last; $sequence++) {
        $logicalKey = "$($Window.owner)-$sequence"
        if (-not $preparation.ContainsKey($logicalKey)) { throw "Missing preparation sample $logicalKey." }
        $p = $preparation[$logicalKey]
        if ($p.phase -cne $Window.phase) { throw 'Preparation phase mismatch.' }
        $executions = if ($bySequence.ContainsKey($logicalKey)) {
            @($bySequence[$logicalKey] | Sort-Object { [ulong]$_.fence })
        } else { @() }
        $executions = @($executions)
        if ($executions.Count -eq 0) { throw "Missing retired native sample $logicalKey." }
        for ($executionIndex = 0; $executionIndex -lt $executions.Count; $executionIndex++) {
            $g = $executions[$executionIndex]
            $c = $cpu["$($g.owner)-$($g.fence)"]
            if ($g.phase -cne $Window.phase) { throw 'Native timing phase mismatch.' }
            if ([bool]$g.hasSky -ne ($Mode -ceq 'on')) { throw 'The measured sky pass does not match the variant.' }
            if ([int]$c.draws -ne $expectedDraws) {
                throw "Native sky draw count does not match the variant. Expected $expectedDraws, observed $($c.draws)."
            }
            $frame = Number $g.frameMicroseconds
            if ($frame -le 0) { throw 'Zero GPU frame timestamp interval.' }
            $recordMs = (Number $c.recordMicroseconds) / 1000
            $prepareMs = if ($executionIndex -eq 0) { (Number $p.preparationMicroseconds) / 1000 } else { 0.0 }
            $recordAllocated = [long](Number $c.allocatedBytes)
            $prepareAllocated = if ($executionIndex -eq 0) { [long](Number $p.allocatedBytes) } else { 0L }
            $joined.Add([pscustomobject]@{
                Owner=$Window.owner; Sequence=$sequence; Fence=[ulong]$g.fence; ExecutionIndex=$executionIndex
                ReusedPreparation=($executionIndex -gt 0); FrameMs=$frame / 1000; SkyMs=(Number $g.skyMicroseconds) / 1000
                RecordMs=$recordMs; PreparationMs=$prepareMs; CombinedCpuMs=$recordMs + $prepareMs
                RecordAllocatedBytes=$recordAllocated; PreparationAllocatedBytes=$prepareAllocated
                AllocatedBytes=$recordAllocated + $prepareAllocated; Draws=[int]$c.draws
            })
        }
    }
    [pscustomobject]@{ Rows=$joined.ToArray(); RawNativeExtras=$joined.Count - ([long]$Window.last - [long]$Window.first + 1) }
}

function Assert-SkyControlledWindow([long]$RawNativeExtras, [bool]$Smoke) {
    if (-not $Smoke -and $RawNativeExtras -ne 0) {
        throw 'Strict controlled sky windows require exactly one native execution per requested sequence; all extra observations were retained.'
    }
}

function Get-SkyNativeExtraCount($Observations) {
    if ($Observations.Count -eq 0) { return 0L }
    [long]($Observations | Measure-Object RawNativeExtras -Sum).Sum
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
    $expectedSkyBackend = if ($Mode -ceq 'off') { 'Disabled' } else { $selectedSkyBackend }
    $observedSkyBackend = 'Unverified'
    $failure = ''
    $primaryFailure = $null
    $cleanupFailures = [Collections.Generic.List[string]]::new()
    $collector = $null
    $collectorReport = $null
    $schedulingCollector = $null
    $schedulingReport = $null
    $replayStartedUtc = [DateTime]::UtcNow.ToString('O')
    $replayStartedQpc = [Diagnostics.Stopwatch]::GetTimestamp()
    try {
        New-Item -ItemType Directory -Path $world | Out-Null
        New-Item -ItemType File -Path (Join-Path $world 'wyu-volume-smoke.fixture') | Out-Null
        if ((Get-FileHash -LiteralPath $client).Hash -cne $actualClientHash -or
            (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $client) 'Groundwork.Rendering.D3D12.dll')).Hash -cne $actualRendererHash -or
            (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $client) 'Groundwork.Rendering.dll')).Hash -cne $actualRenderingHash) {
            throw 'Measured assemblies changed during the run.'
        }
        $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        if ($tiledSky -and $Mode -ceq 'on') {
            $start.Environment['GROUNDWORK_SKY_SCATTERING_ATLAS'] = '1'
            $start.Environment['GROUNDWORK_SKY_ATLAS_PREFILTER'] = '1'
            $start.Environment['GROUNDWORK_SKY_SPLIT_DRAW'] = '1'
        }
        foreach ($argument in @($client,'--volume-flight-smoke','flight',$world,$Adapter,[string]$seed)) {
            $start.ArgumentList.Add($argument)
        }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $start
        if ($Diagnostics) {
            $collector = Start-ReferenceGpuDiagnostics -OutputDirectory (Join-Path $root "$prefix-gpu") -MaximumDurationMilliseconds ($deadline + 20000)
        }
        if ($GpuSchedulingTrace) {
            $traceDirectory = Join-Path $root "$prefix-scheduling"
            try {
                $schedulingCollector = Start-ReferenceSchedulingTrace -OutputDirectory $traceDirectory -RuntimeSeconds ([Math]::Min(720, [Math]::Ceiling($deadline / 1000.0) + 30))
                $schedulingReport = $schedulingCollector
            } catch {
                $schedulingReport = [pscustomobject]@{ Status='UNAVAILABLE'; FullQualification=$false; OutputDirectory=$traceDirectory; Errors=@($_.Exception.ToString()) }
            }
        }
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
        if ($owners.Count -ne 3 -or @($owners | Where-Object { $_.mode -cne $Mode -or $_.engineHash -cne $actualRendererHash -or
                $_.composition -cne $Composition -or [bool]$_.wp15 -ne ($Composition -ceq 'orbit-combined') -or
                [bool]$_.orbitPbr -ne ($Composition -ceq 'orbit-combined') -or [bool]$_.boulder -ne ($Composition -ceq 'orbit-combined') }).Count) {
            throw 'Missing or inconsistent near/far/near owner records.'
        }
        Assert-SkyLookupOwners $owners ([bool]$LookupPrototype) ([bool]$ReuseLookupResources)
        $observedSkyBackend = Get-ObservedSkyBackend $lines $Mode $selectedSkyBackend ([bool]$AtlasPrefilter)
        if ($GpuPassBreakdown) {
            $prepass = Get-SkyPrepassIndex $rows 'gpu-prepass'
            $frameTicks = Get-SkyPrepassIndex $rows 'gpu-ticks'
            $passTicks = Get-SkyPrepassIndex $rows 'gpu-pass-ticks'
        }
        $lookupSplits=if ($LookupPrototype) { Get-SkyLookupSplits $rows } else { $null }
        if ($LookupPrototype -and $Mode -ceq 'off' -and ($lookupSplits.Gpu.Count -or $lookupSplits.Record.Count)) {
            throw 'Lookup work was reported for the disabled-sky baseline.'
        }
        foreach ($direction in 'to-orbit','to-ground') {
            $handoff = @($rows | Where-Object { $_.kind -ceq 'handoff' -and $_.direction -ceq $direction })
            if ($handoff.Count -ne 1 -or $handoff[0].end -le $handoff[0].start -or $handoff[0].fence -le 0 -or
                $handoff[0].frequency -le 0 -or $handoff[0].sequence -le 0 -or $handoff[0].sourceFrameSequence -ne $handoff[0].sequence) {
                throw "Missing valid native Present handoff for $direction."
            }
            $h = $handoff[0]
            $handoffs.Add([pscustomobject]@{ Pair=$Pair; Replay=$Replay; Mode=$Mode; Composition=$Composition; Seed=$seed; Direction=$direction
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
            $replayNativeExtras = 0L
            foreach ($phase in 'surface','ascent','orbit') {
                $window = @($rows | Where-Object { $_.kind -ceq 'phase' -and $_.phase -ceq $phase })
                if ($window.Count -ne 1) { throw "Missing $phase sequence window." }
                $w = $window[0]
                if ($w.last - $w.first + 1 -ne $count -or
                    $w.composition -cne $Composition -or
                    (-not $Smoke -and ($w.width -ne 1920 -or $w.height -ne 1080))) { throw 'Unexpected sample window or resolution.' }
                $identityKey = "$Pair-$phase"
                if ($identities.ContainsKey($identityKey) -and $identities[$identityKey] -cne $w.identity) {
                    throw "Canonical $phase camera/clock/terrain changed between variants."
                }
                $identities[$identityKey] = $w.identity
                $joined = Get-SkyNativeExecutions $rows $w $Mode $expectedOnDraws
                $replayNativeExtras += $joined.RawNativeExtras
                $nativeExtras.Add([pscustomobject]@{ Pair=$Pair; Replay=$Replay; Mode=$Mode; Composition=$Composition; Phase=$phase
                    RequestedSequences=$count; NativeExecutions=$joined.Rows.Count; RawNativeExtras=$joined.RawNativeExtras })
                foreach ($execution in $joined.Rows) {
                    if ($GpuPassBreakdown) { Assert-SkyPrepassTiming $prepass $frameTicks $passTicks $execution $phase $Mode }
                    $lookupGpu=$null; $lookupCpu=$null
                    if ($LookupPrototype -and $Mode -ceq 'on') {
                        $timing=Get-SkyLookupExecutionTiming $lookupSplits $execution $phase
                        $lookupGpu=$timing.Gpu; $lookupCpu=$timing.Cpu
                    }
                    $samples.Add([pscustomobject]@{ Pair=$Pair; Replay=$Replay; Mode=$Mode; Composition=$Composition; Phase=$phase; Seed=$seed
                        Owner=$execution.Owner; Sequence=$execution.Sequence; Fence=$execution.Fence
                        ExecutionIndex=$execution.ExecutionIndex; ReusedPreparation=$execution.ReusedPreparation
                        Identity=$w.identity; Width=$w.width; Height=$w.height
                        FrameMs=$execution.FrameMs; SkyMs=$execution.SkyMs
                        LookupComputeMs=$(if ($null -ne $lookupGpu) { $lookupGpu.ComputeMs } else { $null })
                        LookupDrawBarrierMs=$(if ($null -ne $lookupGpu) { $lookupGpu.DrawMs } else { $null })
                        LookupComputeRecordMs=$(if ($null -ne $lookupCpu) { $lookupCpu.ComputeMs } else { $null })
                        LookupDrawRecordMs=$(if ($null -ne $lookupCpu) { $lookupCpu.DrawMs } else { $null })
                        RecordMs=$execution.RecordMs; PreparationMs=$execution.PreparationMs; CombinedCpuMs=$execution.CombinedCpuMs
                        RecordAllocatedBytes=$execution.RecordAllocatedBytes; PreparationAllocatedBytes=$execution.PreparationAllocatedBytes
                        AllocatedBytes=$execution.AllocatedBytes; Draws=$execution.Draws })
                }
            }
            Assert-SkyControlledWindow $replayNativeExtras ([bool]$Smoke)
        }
        if ($GpuPassBreakdown -and ($prepass.Count -ne 0 -or $frameTicks.Count -ne 0 -or $passTicks.Count -ne 0)) {
            throw 'Unexpected GPU pass timing outside the measured executions.'
        }
        $passed = $true
    } catch {
        $primaryFailure = $_
        $failure = $_.Exception.Message
    } finally {
        if ($null -ne $schedulingCollector) {
            try { $schedulingReport = Stop-ReferenceSchedulingTrace -Context $schedulingCollector }
            catch { $schedulingReport = [pscustomobject]@{ Status='PARTIAL'; FullQualification=$false; OutputDirectory=$schedulingCollector.OutputDirectory; Errors=@($_.Exception.ToString()) } }
        }
        if ($null -ne $collector) {
            try { $collectorReport = Stop-ReferenceGpuDiagnostics -Context $collector }
            catch { $collectorReport = [pscustomobject]@{ Availability='Error'; Failure=$_.Exception.ToString() } }
        }
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
        $attempt = [pscustomobject]@{ Pair=$Pair; Replay=$Replay; Mode=$Mode; Composition=$Composition; Seed=$seed
            StartedUtc=$replayStartedUtc; StartedQpc=$replayStartedQpc; FinishedUtc=[DateTime]::UtcNow.ToString('O')
            FinishedQpc=[Diagnostics.Stopwatch]::GetTimestamp(); QpcFrequency=[Diagnostics.Stopwatch]::Frequency
            Started=$started; ProcessId=$ownedProcessId; ExitCode=$exitCode
            ExpectedSkyBackend=$expectedSkyBackend; ObservedSkyBackend=$observedSkyBackend
            ExpectedSkyDraws=$(if ($Mode -ceq 'on') { $expectedOnDraws } else { 0 })
            SchedulingTraceStatus=$(if ($schedulingReport) { $schedulingReport.Status } else { 'DISABLED' })
            SchedulingTraceDirectory=$(if ($schedulingReport) { $schedulingReport.OutputDirectory } else { $null })
            Passed=($passed -and $cleanupFailures.Count -eq 0); Failure=$failure
            CleanupFailures=($cleanupFailures -join ' | '); Stdout=$log; Stderr=$errorLog }
        $attempts.Add($attempt)
        if ($Diagnostics) {
            try {
                $timeline = if (Test-Path -LiteralPath $log) {
                    @(Get-Content -LiteralPath $log | Where-Object { $_.StartsWith('PACKAGE G MEASURE ') } |
                        ForEach-Object { $_.Substring('PACKAGE G MEASURE '.Length) | ConvertFrom-Json } |
                        Where-Object { $_.kind -cin @('owner','phase','phase-input','handoff','handoff-stage','handoff-incomplete','gpu-ticks','gpu-prepass','gpu-pass-ticks','sky-pipeline','gpu-clock-calibration','clock-anchor','lookup-gpu-split','lookup-record-split','lookup-startup') })
                } else { @() }
                [pscustomobject]@{ Schema=1; EvidenceKind='DIAGNOSTIC ONLY'; Pair=$Pair; Replay=$Replay; Mode=$Mode
                    Attempt=$attempt; Collector=$collectorReport; SchedulingTrace=$schedulingReport; Events=@($timeline) } |
                    ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $root "$prefix-diagnostics.json")
            } catch { $cleanupFailures.Add("diagnostic timeline: $($_.Exception.Message)") }
        }
        foreach ($export in @(@('attempts', $attempts), @('samples', $samples), @('handoffs', $handoffs), @('native-extras', $nativeExtras))) {
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
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, [NullString]::Value) }
    $env:WYU_SKY_MEASURE_SMOKE = if ($Smoke) { '1' } else { '0' }
    $env:WYU_PACKAGE_G, $env:WYU_PACKAGE_G_SMOKE, $env:WYU_PACKAGE_E, $env:WYU_WP13_MEASURE = 'on','1','on','on'
    $env:WYU_WP15, $env:WYU_WP16 = 'off','off'
    $env:WYU_SKY_MEASURE_COMPOSITION = $Composition
    $env:WYU_SKY_DIAGNOSTICS = if ($Diagnostics) { '1' } else { '0' }
    $env:GROUNDWORK_TWILIGHT_PREVIEW = '1'
    $env:GROUNDWORK_SKY_LOOKUP_PROTOTYPE = if ($LookupPrototype) { '1' } else { $null }
    $env:GROUNDWORK_SKY_LOOKUP_REUSE_RESOURCES = if ($ReuseLookupResources) { '1' } else { $null }
    $env:GROUNDWORK_SKY_SCATTERING_ATLAS = if ($selectedSkyBackend -ceq 'ScatteringAtlas') { '1' } else { $null }
    $env:GROUNDWORK_SKY_ATLAS_PREFILTER = if (!$tiledSky -and $AtlasPrefilter) { '1' } else { $null }
    $env:WYU_SKY_GPU_PASS_BREAKDOWN = if ($GpuPassBreakdown) { '1' } else { $null }
    if ($Composition -ceq 'orbit-combined') {
        $env:WYU_ORBIT_DEMO_SMOKE, $env:WYU_ORBIT_PBR, $env:WYU_ORBIT_BOULDER = '1','on','on'
        $env:WYU_WP15, $env:WYU_WP16, $env:WYU_WP16_SMOKE = 'on','on','1'
    }
    for ($pair = 1; $pair -le $pairs; $pair++) {
        $modes = if ($pair % 2) { 'off','on' } else { 'on','off' }
        foreach ($mode in $modes) {
            for ($replay = 1; $replay -le $replays; $replay++) { Invoke-Replay $pair $replay $mode }
        }
    }
} catch {
    [pscustomobject]@{ Status='BLOCKED'; FullQualification=$false; LookupPrototype=[bool]$LookupPrototype; Failure=$_.Exception.Message
        SkyBackendOn=$selectedSkyBackend; AtlasPrefilter=[bool]$AtlasPrefilter; ExpectedSkyDrawsOn=$expectedOnDraws; ExpectedSkyDrawsOff=0
        Composition=$Composition; Diagnostics=[bool]$Diagnostics; DiagnosticProbe=[bool]$DiagnosticProbe; FullLookupSeries=[bool]$FullLookupSeries
        ReuseLookupResources=[bool]$ReuseLookupResources
        GpuSchedulingTrace=[bool]$GpuSchedulingTrace; GpuPassBreakdown=[bool]$GpuPassBreakdown
        SchedulingTraces=@($attempts | Select-Object Pair,Replay,Mode,SchedulingTraceStatus,SchedulingTraceDirectory)
        CleanupFailures=@($attempts | Where-Object { $_.CleanupFailures } | Select-Object Pair,Replay,Mode,CleanupFailures)
        Attempts=$attempts.Count; FailedAttempts=@($attempts | Where-Object { -not $_.Passed }).Count
        RequiredAttempts=2*$pairs*$replays; RawNativeExtras=(Get-SkyNativeExtraCount $nativeExtras)
        RemainingGates='See manifest.json OpenGates.' } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'result.json')
    throw
} finally {
    foreach ($name in $environmentNames) {
        if ($null -eq $previous[$name]) { [Environment]::SetEnvironmentVariable($name, [NullString]::Value) }
        else { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    }
}

$summary = [Collections.Generic.List[object]]::new()
foreach ($phase in 'surface','ascent','orbit') {
    foreach ($pair in 0..$pairs) {
        $off = @($samples | Where-Object { $_.Phase -ceq $phase -and $_.Mode -ceq 'off' -and ($pair -eq 0 -or $_.Pair -eq $pair) })
        $on = @($samples | Where-Object { $_.Phase -ceq $phase -and $_.Mode -ceq 'on' -and ($pair -eq 0 -or $_.Pair -eq $pair) })
        $expected = if ($pair -eq 0) { $pairs * $count } else { $count }
        if ((-not $Smoke -and ($off.Count -ne $expected -or $on.Count -ne $expected)) -or
            ($Smoke -and ($off.Count -lt $expected -or $on.Count -lt $expected))) { throw "Incomplete $phase GPU denominator." }
        $delta95 = 100 * ((Percentile $on.FrameMs .95) / (Percentile $off.FrameMs .95) - 1)
        $delta99 = 100 * ((Percentile $on.FrameMs .99) / (Percentile $off.FrameMs .99) - 1)
        $sky95 = Percentile $on.SkyMs .95
        $sky99 = Percentile $on.SkyMs .99
        $draws = ($on.Draws | Measure-Object -Maximum).Maximum
        $allocated = ($on.AllocatedBytes | Measure-Object -Maximum).Maximum
        $record99 = Percentile $on.RecordMs .99
        $combined99 = Percentile $on.CombinedCpuMs .99
        $summary.Add([pscustomobject]@{ Kind='GPU'; Phase=$phase; Pair=$pair; Composition=$Composition; OffCount=$off.Count; OnCount=$on.Count
            RequestedCountPerVariant=$expected; OffNativeExtras=$off.Count-$expected; OnNativeExtras=$on.Count-$expected
            FrameP95DeltaPercent=$delta95; FrameP99DeltaPercent=$delta99; SkyP95Ms=$sky95; SkyP99Ms=$sky99
            RecordP99Ms=$record99; CombinedCpuP99Ms=$combined99; RecordMaxAllocatedBytes=($on.RecordAllocatedBytes | Measure-Object -Maximum).Maximum
            CombinedMaxAllocatedBytes=$allocated; SkyDrawsMax=$draws
            Passed=($delta95 -le 5 -and $delta99 -le 8 -and $sky95 -le .8 -and $sky99 -le 1.2 -and $combined99 -le .10 -and $draws -eq $expectedOnDraws -and $allocated -eq 0) })
    }
}
foreach ($direction in 'to-orbit','to-ground') {
    foreach ($pair in 0..$pairs) {
        $off = @($handoffs | Where-Object { $_.Direction -ceq $direction -and $_.Mode -ceq 'off' -and ($pair -eq 0 -or $_.Pair -eq $pair) })
        $on = @($handoffs | Where-Object { $_.Direction -ceq $direction -and $_.Mode -ceq 'on' -and ($pair -eq 0 -or $_.Pair -eq $pair) })
        $expected = if ($pair -eq 0) { $pairs * $replays } else { $replays }
        if ($off.Count -ne $expected -or $on.Count -ne $expected) { throw "Incomplete $direction handoff denominator." }
        $delta = 100 * ((Percentile $on.Milliseconds .95) / (Percentile $off.Milliseconds .95) - 1)
        $summary.Add([pscustomobject]@{ Kind='handoff'; Phase=$direction; Pair=$pair; Composition=$Composition; OffCount=$off.Count; OnCount=$on.Count
            RequestedCountPerVariant=$expected; OffNativeExtras=0; OnNativeExtras=0
            FrameP95DeltaPercent=$delta; FrameP99DeltaPercent=$null; SkyP95Ms=$null; SkyP99Ms=$null
            RecordP99Ms=$null; CombinedCpuP99Ms=$null; RecordMaxAllocatedBytes=$null; CombinedMaxAllocatedBytes=$null; SkyDrawsMax=$null; Passed=($delta -le 10) })
    }
}
$summary | Export-Csv -LiteralPath (Join-Path $root 'summary.csv') -NoTypeInformation
$summary | Where-Object Pair -EQ 0 | Format-Table -AutoSize
$failed = @($summary | Where-Object { -not $_.Passed }).Count
[pscustomobject]@{ Status=if ($Smoke) { 'SMOKE ONLY' } elseif ($failed) { 'CHANGES REQUIRED' } elseif ($Diagnostics) { 'DIAGNOSTIC ONLY' } else { 'MEASURED GATES PASSED' }
    Composition=$Composition; Diagnostics=[bool]$Diagnostics; DiagnosticProbe=[bool]$DiagnosticProbe; FullLookupSeries=[bool]$FullLookupSeries
    SkyBackendOn=$selectedSkyBackend; AtlasPrefilter=[bool]$AtlasPrefilter; ExpectedSkyDrawsOn=$expectedOnDraws; ExpectedSkyDrawsOff=0
    GpuSchedulingTrace=[bool]$GpuSchedulingTrace; GpuPassBreakdown=[bool]$GpuPassBreakdown
    SchedulingTraces=@($attempts | Select-Object Pair,Replay,Mode,SchedulingTraceStatus,SchedulingTraceDirectory)
    LookupPrototype=[bool]$LookupPrototype; ReuseLookupResources=[bool]$ReuseLookupResources
    FullQualification=$false; Attempts=$attempts.Count; FailedAttempts=@($attempts | Where-Object { -not $_.Passed }).Count
    RequiredAttempts=2*$pairs*$replays; FailedSummaryRows=$failed
    RawNativeExtras=(Get-SkyNativeExtraCount $nativeExtras)
    RemainingGates='See manifest.json OpenGates.' } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'result.json')
Write-Host "Evidence $root"
if ($Smoke) { Write-Host 'SMOKE ONLY. Counts and resolution do not qualify the reference budgets.' }
elseif ($failed) { throw 'A required measured sky/frame/handoff budget failed.' }
