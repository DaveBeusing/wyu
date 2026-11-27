param(
    [Parameter(Mandatory)][string]$InstalledDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$ManifestPath = (Join-Path $PSScriptRoot 'installer-manifest.json'),
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedManifestSha256,
    [ValidateRange(1,50)][int]$Samples = 5,
    [ValidateSet('hardware','warp')][string]$Adapter = 'hardware',
    [ValidateSet('Normal','Retry')][string[]]$Cohort = @('Normal','Retry'),
    [ValidateSet('Overlap','AtHandoff')][string]$RecoveryMode = 'Overlap',
    [switch]$TwilightPressure,
    [switch]$IndependentTextureCopy,
    [switch]$DiagnosticR09Installer,
    [switch]$RequireEarlyColdFallback,
    [switch]$RequireRecoveryColdFallback,
    [switch]$RequireRecoveryMeshRebuild
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($IndependentTextureCopy -and !$TwilightPressure) { throw 'Independent texture copy pressure requires the Far Horizon Twilight fixture.' }
if ($DiagnosticR09Installer -and (!$TwilightPressure -or !$IndependentTextureCopy)) {
    throw 'The R09 diagnostic installer requires both Twilight and independent texture copy pressure.'
}

function Assert-InstalledPayload([string]$Directory, $Manifest) {
    $expected = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Manifest.payload) {
        $path = [string]$entry.path
        if ($path -cnotmatch '^[^\\/:]+(?:/[^\\/:]+)*$' -or $path -match '(?:^|/)\.{1,2}(?:/|$)' -or
            $entry.sha256 -cnotmatch '^[A-Fa-f0-9]{64}$' -or !$expected.TryAdd($path, $entry.sha256)) {
            throw "Invalid or duplicate payload entry: $path"
        }
    }
    foreach ($required in 'WorldsYetUnseen.Client.exe','hostfxr.dll','hostpolicy.dll','coreclr.dll') {
        if (!$expected.ContainsKey($required)) { throw "Manifest omits required file: $required" }
    }
    foreach ($item in Get-ChildItem -LiteralPath $Directory -Recurse -Force) {
        $path = [IO.Path]::GetRelativePath($Directory, $item.FullName).Replace('\', '/')
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Unexpected reparse point: $path" }
        if ($item.PSIsContainer) { continue }
        if (!$expected.ContainsKey($path)) {
            if ($path -imatch '^unins[0-9]{3}\.(?:exe|dat|msg)$') { continue }
            throw "Unexpected installed file: $path"
        }
        if ((Get-FileHash -LiteralPath $item.FullName).Hash -ine $expected[$path]) {
            throw "Installed payload hash mismatch: $path"
        }
        [void]$expected.Remove($path)
    }
    if ($expected.Count) { throw "Missing installed files: $($expected.Keys -join ', ')" }
}

function Invoke-Measurement([string]$Executable, [string]$Slot, [string]$Mode, [string]$Log, [string]$Errors) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.WorkingDirectory = Split-Path -LiteralPath $Executable
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in '--volume-flight-smoke','flight',$Slot,$Adapter) { $start.ArgumentList.Add($argument) }
    foreach ($name in 'WYU_R09_RECOVERY_AT_HANDOFF','WYU_R09_TEST_FORCE_PREFETCH_MISS','WYU_R09_TEST_FORCE_RECOVERY_COLD',
        'WYU_R09_TEST_FORCE_RECOVERY_MESH_REBUILD',
        'WYU_R09_TWILIGHT_PRESSURE','WYU_R09_INDEPENDENT_TEXTURE_COPY','WYU_PACKAGE_G_MEASURE','WYU_SKY_MEASURE_SMOKE','WYU_SKY_MEASURE_PHASES',
        'WYU_SKY_DIAGNOSTICS','WYU_ORBIT_DEMO_SMOKE','WYU_ORBIT_PBR_MEASURE','WYU_DAWN_SMOKE',
        'WYU_HOST_RETRY_SMOKE','WYU_WP13_MEASURE','WYU_PACKAGE_G','WYU_PACKAGE_G_SMOKE',
        'WYU_WP16','WYU_WP16_SMOKE','WYU_WP15','WYU_WP15_PROBE','WYU_WP15_FLIP','WYU_PACKAGE_E',
        'WYU_PACKAGE_E_MEASURE','WYU_PACKAGE_E_WALK','WYU_PACKAGE_E_TIMESTAMP_PAIRS',
        'GROUNDWORK_TWILIGHT_PREVIEW','GROUNDWORK_SKY_LOOKUP_PROTOTYPE','GROUNDWORK_SKY_LOOKUP_REUSE_RESOURCES',
        'WYU_SKY_GPU_PASS_BREAKDOWN',
        'DOTNET_STARTUP_HOOKS','DOTNET_ADDITIONAL_DEPS','DOTNET_SHARED_STORE','DOTNET_DIAGNOSTIC_PORTS',
        'CORECLR_ENABLE_PROFILING','CORECLR_PROFILER','CORECLR_PROFILER_PATH','CORECLR_PROFILER_PATH_32','CORECLR_PROFILER_PATH_64',
        'COR_ENABLE_PROFILING','COR_PROFILER','COR_PROFILER_PATH','COR_PROFILER_PATH_32','COR_PROFILER_PATH_64') {
        [void]$start.Environment.Remove($name)
    }
    $start.Environment['WYU_R09_LATENCY'] = '1'
    if ($TwilightPressure) {
        $start.Environment['WYU_R09_TWILIGHT_PRESSURE'] = 'far-horizon'
        $start.Environment['GROUNDWORK_TWILIGHT_PREVIEW'] = '1'
        $start.Environment['WYU_PACKAGE_G'] = 'on'
        $start.Environment['WYU_PACKAGE_E'] = 'on'
        $start.Environment['WYU_WP15'] = 'off'
        $start.Environment['WYU_WP16_SMOKE'] = '0'
    }
    if ($IndependentTextureCopy) { $start.Environment['WYU_R09_INDEPENDENT_TEXTURE_COPY'] = '1' }
    if ($RecoveryMode -eq 'AtHandoff') { $start.Environment['WYU_R09_RECOVERY_AT_HANDOFF'] = '1' }
    if ($Mode -eq 'Retry') { $start.Environment['WYU_HOST_RETRY_SMOKE'] = '1' }
    if ($RequireEarlyColdFallback) { $start.Environment['WYU_R09_TEST_FORCE_PREFETCH_MISS'] = '1' }
    if ($RequireRecoveryColdFallback) { $start.Environment['WYU_R09_TEST_FORCE_RECOVERY_COLD'] = '1' }
    if ($RequireRecoveryMeshRebuild) { $start.Environment['WYU_R09_TEST_FORCE_RECOVERY_MESH_REBUILD'] = '1' }

    $outputStream = [IO.File]::Create($Log)
    try {
        $errorStream = [IO.File]::Create($Errors)
        try {
            $process = [Diagnostics.Process]::Start($start)
            try {
                $streamDeadline = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(212))
                $outputCopy = $process.StandardOutput.BaseStream.CopyToAsync($outputStream, $streamDeadline.Token)
                $errorCopy = $process.StandardError.BaseStream.CopyToAsync($errorStream, $streamDeadline.Token)
                if (!$process.WaitForExit(200000)) { throw "Measurement timed out. See $Log." }
                if ($process.ExitCode -ne 0) { throw "Measurement exited with code $($process.ExitCode). See $Log and $Errors." }
            } finally {
                try {
                    if (!$process.HasExited) {
                        $process.Kill($true)
                        if (!$process.WaitForExit(5000)) { throw 'Measurement process did not exit after cancellation.' }
                    }
                    $outputCopy.GetAwaiter().GetResult()
                    $errorCopy.GetAwaiter().GetResult()
                } finally {
                    $streamDeadline.Dispose()
                    $process.Dispose()
                }
            }
        } finally { $errorStream.Dispose() }
    } finally { $outputStream.Dispose() }
}

function Assert-WorkerProfile($Intervals, [string]$Path, [string]$Worker, [string[]]$Stages) {
    $outer = $Intervals["$Path/$Worker"]
    $queue = $Intervals["$Path/$Worker-queue"]
    $observation = $Intervals["$Path/$Worker-completion-observation"]
    if ($queue.ToQpc -ne $outer.FromQpc -or $observation.FromQpc -ne $outer.ToQpc) {
        throw "Invalid scheduler timing for $Path/$Worker. Queue and completion observation must meet Execute."
    }
    $previous = $null
    foreach ($stage in $Stages) {
        $interval = $Intervals["$Path/$Worker-$stage"]
        if ($interval.FromQpc -lt $outer.FromQpc -or $interval.ToQpc -gt $outer.ToQpc -or
            ($null -ne $previous -and $interval.FromQpc -ne $previous.ToQpc)) {
            throw "Invalid timing for $Path/$Worker-$stage. Worker stages must be contiguous and within Execute."
        }
        $previous = $interval
    }
}

function Assert-InventoryTiming($Items, $Outer, [long]$Frequency, [string]$Group) {
    $total = $Outer.ToQpc - $Outer.FromQpc
    $remaining = $total
    foreach ($record in $Items.Values) {
        if ($record.ElapsedTicks -gt $remaining) { throw "Item ticks exceed the outer interval for $Group." }
        $remaining -= $record.ElapsedTicks
        $record.Frequency = $Frequency
        $record.ElapsedMilliseconds = [double]$record.ElapsedTicks * 1000.0 / $Frequency
    }
    $total - $remaining
}

function Compare-BrickInventory($Publishing, $Private, [long]$Frequency, [string]$Mode, [int]$Sample, [string]$Path) {
    $shared = $sameStride = $sameDensity = $sameMaterial = $samePayload = $samePayloadAndStride = $reused = 0
    $privateSharedTicks = $privateSamePayloadTicks = $privateSamePayloadAndStrideTicks = $publishingSamePayloadTicks = 0L
    foreach ($key in $Publishing.Keys) {
        $published = $Publishing[$key]
        if (!$Private.ContainsKey($key)) {
            if ($published.Reused) { throw "Reused brick $Path/$key is missing from the source." }
            continue
        }
        $shared++
        $overlap = $Private[$key]
        $privateSharedTicks += $overlap.ElapsedTicks
        $strideMatches = $published.RequestedStride -eq $overlap.RequestedStride
        $densityMatches = $published.DensitySha256 -ceq $overlap.DensitySha256
        $materialMatches = $published.MaterialSha256 -ceq $overlap.MaterialSha256
        if ($published.Reused) {
            if (!$densityMatches -or !$materialMatches) { throw "Reused brick $Path/$key differs from the source." }
            $reused++
        }
        if ($strideMatches) { $sameStride++ }
        if ($densityMatches) { $sameDensity++ }
        if ($materialMatches) { $sameMaterial++ }
        if ($densityMatches -and $materialMatches) {
            $samePayload++
            $privateSamePayloadTicks += $overlap.ElapsedTicks
            $publishingSamePayloadTicks += $published.ElapsedTicks
            if ($strideMatches) {
                $samePayloadAndStride++
                $privateSamePayloadAndStrideTicks += $overlap.ElapsedTicks
            }
        }
    }
    [pscustomobject]@{
        Cohort = $Mode; Run = $Sample; Warmup = ($Sample -eq 0); Path = $Path
        PublishingBricks = $Publishing.Count; PrivateBricks = $Private.Count; SharedKeys = $shared
        ReusedBricks = $reused; RebuiltBricks = $Publishing.Count - $reused
        PublishingOnlyKeys = $Publishing.Count - $shared; PrivateOnlyKeys = $Private.Count - $shared
        SameRequestedStride = $sameStride; SameDensity = $sameDensity; SameMaterial = $sameMaterial
        SamePayload = $samePayload; SamePayloadAndRequestedStride = $samePayloadAndStride
        Frequency = $Frequency; PrivateSharedKeyTicks = $privateSharedTicks
        PrivateSamePayloadTicks = $privateSamePayloadTicks; PublishingSamePayloadTicks = $publishingSamePayloadTicks
        PrivateSamePayloadAndRequestedStrideTicks = $privateSamePayloadAndStrideTicks
        PrivateSharedKeyMilliseconds = [double]$privateSharedTicks * 1000.0 / $Frequency
        PrivateSamePayloadMilliseconds = [double]$privateSamePayloadTicks * 1000.0 / $Frequency
        PublishingSamePayloadMilliseconds = [double]$publishingSamePayloadTicks * 1000.0 / $Frequency
        PrivateSamePayloadAndRequestedStrideMilliseconds = [double]$privateSamePayloadAndStrideTicks * 1000.0 / $Frequency
    }
}

function Compare-MeshInventory($Publishing, $Private, [long]$Frequency, [string]$Mode, [int]$Sample, [string]$Path) {
    $shared = $sameLod = $sameTriangleCount = $sameGeometry = $sameLodAndGeometry = $reused = 0
    $privateSharedTicks = $privateSameGeometryTicks = $privateSameLodAndGeometryTicks = $publishingSameLodAndGeometryTicks = 0L
    foreach ($key in $Publishing.Keys) {
        $published = $Publishing[$key]
        if (!$Private.ContainsKey($key)) {
            if ($published.Reused) { throw "Reused mesh $Path/$key is missing from the source." }
            continue
        }
        $shared++
        $overlap = $Private[$key]
        $privateSharedTicks += $overlap.ElapsedTicks
        $lodMatches = $published.LodMask -eq $overlap.LodMask
        $countMatches = $published.TriangleCount -eq $overlap.TriangleCount
        $geometryMatches = $published.GeometrySha256 -ceq $overlap.GeometrySha256
        if ($published.Reused) {
            if (!$lodMatches -or !$countMatches -or !$geometryMatches) { throw "Reused mesh $Path/$key differs from the source." }
            $reused++
        }
        if ($lodMatches) { $sameLod++ }
        if ($countMatches) { $sameTriangleCount++ }
        if ($countMatches -and $geometryMatches) {
            $sameGeometry++
            $privateSameGeometryTicks += $overlap.ElapsedTicks
            if ($lodMatches) {
                $sameLodAndGeometry++
                $privateSameLodAndGeometryTicks += $overlap.ElapsedTicks
                $publishingSameLodAndGeometryTicks += $published.ElapsedTicks
            }
        }
    }
    [pscustomobject]@{
        Cohort = $Mode; Run = $Sample; Warmup = ($Sample -eq 0); Path = $Path
        PublishingMeshes = $Publishing.Count; PrivateMeshes = $Private.Count; SharedOrigins = $shared
        ReusedMeshes = $reused; RebuiltMeshes = $Publishing.Count - $reused
        PublishingOnlyOrigins = $Publishing.Count - $shared; PrivateOnlyOrigins = $Private.Count - $shared
        SameLodMask = $sameLod; SameTriangleCount = $sameTriangleCount
        SameGeometry = $sameGeometry; SameLodAndGeometry = $sameLodAndGeometry
        Frequency = $Frequency; PrivateSharedOriginTicks = $privateSharedTicks
        PrivateSameGeometryTicks = $privateSameGeometryTicks
        PrivateSameLodAndGeometryTicks = $privateSameLodAndGeometryTicks
        PublishingSameLodAndGeometryTicks = $publishingSameLodAndGeometryTicks
        PrivateSharedOriginMilliseconds = [double]$privateSharedTicks * 1000.0 / $Frequency
        PrivateSameGeometryMilliseconds = [double]$privateSameGeometryTicks * 1000.0 / $Frequency
        PrivateSameLodAndGeometryMilliseconds = [double]$privateSameLodAndGeometryTicks * 1000.0 / $Frequency
        PublishingSameLodAndGeometryMilliseconds = [double]$publishingSameLodAndGeometryTicks * 1000.0 / $Frequency
    }
}

$r09GpuOwners = @('Presentation','Depth','Terrain','Shadow','Props','Readback','Texture','TextureStaging')
$r09RendererPhases = @('far-renderer-stop','ground-renderer-startup','ground-renderer-switch',
    'private-build-renderer-overlap','mask-submit-to-receipt')
$r09RecoveryPhases = @('handoff-to-begin-tick','flight-overlap-window','recovery-no-flight-to-begin-tick') + $r09RendererPhases

function Complete-ResourceSamples($ResourceSamples, $GpuOwners, $Intervals, $Identities, [long]$Frequency) {
    if ($ResourceSamples.Count -ne 7 -or $GpuOwners.Count -ne 7 * $r09GpuOwners.Count) {
        throw 'Expected two resource checkpoints per path and one shutdown checkpoint.'
    }
    foreach ($path in 'Movement','FirstEdit','Recovery','Shutdown') {
        $points = if ($path -ceq 'Shutdown') { @('settled') } else { @('held','native-ready') }
        $pathSamples = foreach ($point in $points) {
            $key = "$path/$point"
            $snapshot = $ResourceSamples[$key]
            if ($null -eq $snapshot -or $snapshot.Frequency -ne $Frequency -or !$snapshot.Epoch -or
                $snapshot.CpuLifetimePeakChargedBytes -lt $snapshot.CpuChargedBytes -or
                !$snapshot.ProcessWorkingSetBytes -or !$snapshot.ProcessPrivateBytes -or
                $snapshot.ProcessLifetimePeakWorkingSetBytes -lt $snapshot.ProcessWorkingSetBytes -or
                $snapshot.GpuLedgerOverflow -or $snapshot.GpuSizeUnknown) {
                throw "Missing or incomplete resource evidence: $key."
            }
            $gpuCount = 0
            foreach ($owner in $r09GpuOwners) {
                $entry = $GpuOwners["$key/$owner"]
                if ($null -eq $entry -or $entry.Live -gt 128 -or $entry.Retained -gt 128 -or
                    ((!$entry.Live) -ne (!$entry.LiveBytes)) -or ((!$entry.Retained) -ne (!$entry.RetainedBytes))) {
                    throw "Missing or invalid GPU allocation evidence: $key/$owner."
                }
                $backing = $entry.LiveBytes + $entry.RetainedBytes
                $snapshot.GpuBackingBytes += $backing
                $gpuCount += $entry.Live + $entry.Retained
                if ($owner -ceq 'Terrain') { $snapshot.GpuTerrainBackingBytes = $backing }
            }
            if ($gpuCount -gt 128) { throw "GPU ledger is out of bounds: $key." }

            if ($point -ceq 'settled') {
                if ($snapshot.Qpc -lt $Intervals['Recovery/setup-to-begin-tick'].ToQpc -or
                    $snapshot.Epoch -ne $Identities['Recovery'].Epoch -or
                    $snapshot.Workers -or $snapshot.Queued -or $snapshot.CpuChargedBytes -or
                    $snapshot.Generations -or $snapshot.Readers -or $snapshot.GpuCustody -or $gpuCount) {
                    throw 'CPU terrain, speculative jobs or native GPU allocations remained after shutdown.'
                }
            } else {
                $bounds = if ($point -ceq 'held') { $Intervals["$path/held-upload"] }
                    else { $Intervals["$path/setup-to-begin-tick"] }
                $earliest = if ($point -ceq 'held') { $bounds.FromQpc }
                    else { $Intervals["$path/upload-to-observed-ready"].ToQpc }
                if ($snapshot.Epoch -ne $Identities[$path].Epoch -or $snapshot.Qpc -lt $earliest -or
                    $snapshot.Qpc -gt $bounds.ToQpc -or $snapshot.Workers -ne 1 -or $snapshot.Queued -ne 1 -or
                    !$snapshot.CpuChargedBytes -or !$snapshot.Generations -or !$snapshot.Readers -or
                    ($point -ceq 'native-ready' -and !$snapshot.GpuTerrainBackingBytes)) {
                    throw "Resource checkpoint did not demonstrate combined pressure: $key."
                }
            }
            $snapshot
        }
        if ($path -cne 'Shutdown') {
            $first = $pathSamples[0]
            [pscustomobject]@{
                Cohort = $first.Cohort; Run = $first.Run; Warmup = $first.Warmup; Path = $path
                RendererEpoch = $first.Epoch; Checkpoints = $pathSamples.Count
                CpuChargedSampleMaxBytes = ($pathSamples | Measure-Object CpuChargedBytes -Maximum).Maximum
                ProcessWorkingSetSampleMaxBytes = ($pathSamples | Measure-Object ProcessWorkingSetBytes -Maximum).Maximum
                ProcessPrivateSampleMaxBytes = ($pathSamples | Measure-Object ProcessPrivateBytes -Maximum).Maximum
                GpuBackingSampleMaxBytes = ($pathSamples | Measure-Object GpuBackingBytes -Maximum).Maximum
                GpuTerrainBackingSampleMaxBytes = ($pathSamples | Measure-Object GpuTerrainBackingBytes -Maximum).Maximum
            }
        }
    }
}

function Read-Measurements([string[]]$Lines, [string]$Mode, [int]$Sample) {
    $paths = @('Movement','FirstEdit','Recovery')
    $roles = @('publishing','private')
    $workerStages = @('overlay','bricks','plan','mesh','evidence','handoff')
    $brickStages = @('reservation','allocation','density','material','shared-vertices','publish','residual')
    $phases = @('worker','worker-queue','worker-completion-observation','admission','held-upload',
        'upload-to-observed-ready','held-upload-release-to-ready','submit-to-observed-present',
        'observed-present-to-begin-tick','authorized-receipt-to-begin-tick','setup-to-begin-tick')
    $phases += @($workerStages | ForEach-Object { "worker-$_" })
    $privatePhases = @('prebuild-worker','prebuild-worker-queue','prebuild-worker-completion-observation')
    $privatePhases += @($workerStages | ForEach-Object { "prebuild-worker-$_" })
    $intervals = @{}
    $identities = @{}
    $twilightDraws = @{}
    $textureCopies = @{}
    $pressure = @{}
    $resourceSamples = @{}
    $gpuOwners = @{}
    $reuse = @{}
    $recoveryBrickSource = $null
    $recoveryMeshPlanReused = $null
    $early = @{}
    $brickTimings = @{}
    $bricksByGroup = @{}
    $meshesByGroup = @{}
    foreach ($path in $paths) {
        foreach ($role in $roles) {
            $bricksByGroup["$path/$role"] = @{}
            $meshesByGroup["$path/$role"] = @{}
        }
    }
    $frequency = 0L
    foreach ($line in $Lines) {
        if ($line -cmatch '^WYU R09 IDENTITY (Movement|FirstEdit|Recovery) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+)$') {
            $path = $Matches[1]
            $generation, $frame, $view, $epoch, $accepted, $opened = 2..7 | ForEach-Object { [uint64]$Matches[$_] }
            if ($identities.ContainsKey($path) -or !$generation -or !$frame -or !$view -or !$epoch -or
                !$accepted -or $opened -le $accepted) { throw "Invalid or duplicate identity: $line" }
            $identities[$path] = [pscustomobject]@{
                Cohort = $Mode; Run = $Sample; Warmup = ($Sample -eq 0); Path = $path
                Generation = $generation; Frame = $frame; View = $view; Epoch = $epoch
                AcceptedTick = $accepted; OpenedTick = $opened
            }
        } elseif ($line -cmatch '^WYU R09 PRESSURE (Movement|FirstEdit|Recovery) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+)$') {
            $path = $Matches[1]
            $cpu, $native, $upload, $retirement = 2..5 | ForEach-Object { [uint64]$Matches[$_] }
            if ($pressure.ContainsKey($path) -or !$cpu) { throw "Invalid or duplicate pressure record: $line" }
            $pressure[$path] = [pscustomobject]@{
                Cohort = $Mode; Run = $Sample; Warmup = ($Sample -eq 0); Path = $path
                CpuPeakBytes = $cpu; NativePeakBytes = $native; UploadPeakBytes = $upload; RetirementPeakBytes = $retirement
            }
        } elseif ($line -cmatch '^WYU R09 RESOURCE (Movement|FirstEdit|Recovery|Shutdown) (held|native-ready|settled) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+) ([01]) ([01]) ([0-9]+)$') {
            $path, $point = $Matches[1], $Matches[2]
            $key = "$path/$point"
            if ($resourceSamples.ContainsKey($key) -or (($path -ceq 'Shutdown') -ne ($point -ceq 'settled'))) {
                throw "Invalid or duplicate resource checkpoint: $line"
            }
            $resourceSamples[$key] = [pscustomobject]@{
                Cohort = $Mode; Run = $Sample; Warmup = ($Sample -eq 0); Path = $path; Point = $point
                Qpc = [long]$Matches[3]; Frequency = [long]$Matches[4]; Epoch = [uint64]$Matches[5]
                Workers = [int]$Matches[6]; Queued = [int]$Matches[7]
                CpuChargedBytes = [long]$Matches[8]; CpuLifetimePeakChargedBytes = [long]$Matches[9]
                Generations = [int]$Matches[10]; Readers = [int]$Matches[11]
                ProcessWorkingSetBytes = [long]$Matches[12]; ProcessPrivateBytes = [long]$Matches[13]
                ProcessLifetimePeakWorkingSetBytes = [long]$Matches[14]
                GpuLedgerOverflow = ($Matches[15] -ceq '1'); GpuSizeUnknown = ($Matches[16] -ceq '1')
                GpuCustody = [int]$Matches[17]; GpuBackingBytes = 0L; GpuTerrainBackingBytes = 0L
            }
        } elseif ($line -cmatch '^WYU R09 GPU (Movement|FirstEdit|Recovery|Shutdown) (held|native-ready|settled) ([A-Za-z]+) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+)$') {
            $path, $point, $owner = $Matches[1], $Matches[2], $Matches[3]
            $key = "$path/$point/$owner"
            if ($gpuOwners.ContainsKey($key) -or $r09GpuOwners -cnotcontains $owner -or
                (($path -ceq 'Shutdown') -ne ($point -ceq 'settled'))) {
                throw "Invalid or duplicate GPU owner record: $line"
            }
            $gpuOwners[$key] = [pscustomobject]@{
                Cohort = $Mode; Run = $Sample; Warmup = ($Sample -eq 0); Path = $path; Point = $point
                Owner = $owner; Live = [int]$Matches[4]; LiveBytes = [long]$Matches[5]
                Retained = [int]$Matches[6]; RetainedBytes = [long]$Matches[7]
                Released = [uint64]$Matches[8]; ReleasedBytes = [uint64]$Matches[9]
            }
        } elseif ($line -cmatch '^WYU R09 EARLY (Movement|FirstEdit) (Ready|Pending|Rejected|Unavailable) ([0-9]+) ([0-9]+) ([0-9]+)$') {
            $path, $status = $Matches[1], $Matches[2]
            $attempt, $cut = [long]$Matches[3], [long]$Matches[4]
            $generation = [uint64]$Matches[5]
            if ($early.ContainsKey($path) -or !$cut -or $cut -lt $attempt -or
                (!$attempt -and ($status -cne 'Unavailable' -or $generation)) -or
                ($status -cin @('Ready','Pending') -and !$generation) -or
                ($status -ceq 'Rejected' -and $generation)) {
                throw "Invalid or duplicate early prebuild record: $line"
            }
            $early[$path] = [pscustomobject]@{ Status = $status; AttemptQpc = $attempt; CutQpc = $cut; Generation = $generation }
        } elseif ($line -cmatch '^WYU R09 REUSE (Movement|FirstEdit|Recovery) ([0-9]+) ([0-9]+) ([0-9]+)$') {
            $path = $Matches[1]
            $producer, $consumer, $source = 2..4 | ForEach-Object { [uint64]$Matches[$_] }
            if ($reuse.ContainsKey($path) -or !$consumer -or $producer -eq $consumer -or
                ($source -and $source -ne $producer)) { throw "Invalid or duplicate reuse record: $line" }
            $reuse[$path] = [pscustomobject]@{
                Cohort = $Mode; Run = $Sample; Warmup = ($Sample -eq 0); Path = $path
                PrivateGeneration = $producer; PublishingGeneration = $consumer; ReuseSourceGeneration = $source
                EarlyStatus = ''; EarlyAttemptQpc = $null; EarlyCutQpc = $null; EarlyAdmittedGeneration = $null
                EarlyLeadMilliseconds = $null; ReadyBeforeCutMilliseconds = $null; ColdFallback = !$source
                ReusedBricks = 0; RebuiltBricks = 0; ReusedMeshes = 0; RebuiltMeshes = 0
                PrivateBrickSourceGeneration = $null; ReusedPrivateBricks = $null; RebuiltPrivateBricks = $null
                ReusedPrivateMeshPlan = $null; ReusedPrivateMeshes = $null; RebuiltPrivateMeshes = $null
            }
        } elseif ($line -cmatch '^WYU R09 RECOVERYBRICKS ([0-9]+)$') {
            if ($null -ne $recoveryBrickSource) { throw "Duplicate Recovery brick source: $line" }
            $recoveryBrickSource = [uint64]$Matches[1]
        } elseif ($line -cmatch '^WYU R09 RECOVERYMESH ([01])$') {
            if ($null -ne $recoveryMeshPlanReused) { throw "Duplicate Recovery mesh plan evidence: $line" }
            $recoveryMeshPlanReused = $Matches[1] -ceq '1'
        } elseif ($line -cmatch '^WYU R09 BRICKTIMING (Movement|FirstEdit|Recovery) (publishing|private) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+)$') {
            $group = "$($Matches[1])/$($Matches[2])"
            if ($brickTimings.ContainsKey($group)) { throw "Duplicate brick timing: $line" }
            $brickTimings[$group] = @(3..8 | ForEach-Object { [long]$Matches[$_] })
        } elseif ($line -cmatch '^WYU R09 BRICK (Movement|FirstEdit|Recovery) (publishing|private) (-?[0-9]+) (-?[0-9]+) (-?[0-9]+) ([12]) ([a-f0-9]{64}) ([a-f0-9]{64}) ([0-9]+) ([01])$') {
            $path, $role = $Matches[1], $Matches[2]
            $x, $y, $z = 3..5 | ForEach-Object { [long]$Matches[$_] }
            $key = "$x,$y,$z"
            $group = $bricksByGroup["$path/$role"]
            $reused = $Matches[10] -ceq '1'
            if ($group.Count -ge 512 -or $group.ContainsKey($key) -or
                ($role -ceq 'private' -and $reused -and $path -cne 'Recovery')) {
                throw "Invalid or duplicate brick: $line"
            }
            $group[$key] = [pscustomobject]@{
                Cohort = $Mode; Run = $Sample; Warmup = ($Sample -eq 0); Path = $path; Role = $role
                BrickX = $x; BrickY = $y; BrickZ = $z; RequestedStride = [int]$Matches[6]
                DensitySha256 = $Matches[7]; MaterialSha256 = $Matches[8]
                Reused = $reused
                ElapsedTicks = [long]$Matches[9]; Frequency = 0L; ElapsedMilliseconds = 0.0
            }
        } elseif ($line -cmatch '^WYU R09 MESH (Movement|FirstEdit|Recovery) (publishing|private) (-?[0-9]+) (-?[0-9]+) (-?[0-9]+) ([0-9]+) ([0-9]+) ([a-f0-9]{64}) ([0-9]+) ([01])$') {
            $path, $role = $Matches[1], $Matches[2]
            $x, $y, $z = 3..5 | ForEach-Object { [long]$Matches[$_] }
            $key = "$x,$y,$z"
            $group = $meshesByGroup["$path/$role"]
            $reused = $Matches[10] -ceq '1'
            if ($group.Count -ge 64 -or $group.ContainsKey($key) -or
                ($role -ceq 'private' -and $reused -and $path -cne 'Recovery')) {
                throw "Invalid or duplicate mesh: $line"
            }
            $group[$key] = [pscustomobject]@{
                Cohort = $Mode; Run = $Sample; Warmup = ($Sample -eq 0); Path = $path; Role = $role
                OriginX = $x; OriginY = $y; OriginZ = $z; LodMask = [byte]$Matches[6]
                TriangleCount = [int]$Matches[7]; GeometrySha256 = $Matches[8]
                Reused = $reused
                ElapsedTicks = [long]$Matches[9]; Frequency = 0L; ElapsedMilliseconds = 0.0
            }
        } elseif ($line -cmatch '^WYU R09 (Movement|FirstEdit|Recovery) ([a-z-]+) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+)$') {
            $path = $Matches[1]
            $phase = $Matches[2]
            $from, $to, $qpc = 3..5 | ForEach-Object { [long]$Matches[$_] }
            $attempts, $denials = 6..7 | ForEach-Object { [int]$Matches[$_] }
            $key = "$path/$phase"
            $expectedAttempts = if ($phase -ne 'admission') { 0 } elseif ($Mode -eq 'Retry') { 2 } else { 1 }
            if ($intervals.ContainsKey($key) -or !$from -or $to -lt $from -or !$qpc -or
                ($frequency -and $qpc -ne $frequency) -or $attempts -ne $expectedAttempts -or
                $denials -ne [Math]::Max(0, $expectedAttempts - 1) -or
                ($phases -cnotcontains $phase -and $privatePhases -cnotcontains $phase -and
                    !($path -ceq 'Recovery' -and $r09RecoveryPhases -ccontains $phase))) {
                throw "Invalid or duplicate interval: $line"
            }
            $frequency = $qpc
            $scope = switch -CaseSensitive ($key) {
                'Recovery/setup-to-begin-tick' {
                    if ($RecoveryMode -eq 'AtHandoff') { 'without-flight-overlap' } else { 'includes-in-flight-overlap' }
                }
                'Recovery/recovery-no-flight-to-begin-tick' { 'without-flight-overlap' }
                'Recovery/flight-overlap-window' { 'flight-before-handoff' }
                'Recovery/handoff-to-begin-tick' { 'return-handoff' }
                'Recovery/far-renderer-stop' { 'renderer-handoff' }
                'Recovery/ground-renderer-startup' { 'renderer-handoff' }
                'Recovery/ground-renderer-switch' { 'renderer-handoff' }
                'Recovery/private-build-renderer-overlap' { 'concurrent-private-build-and-renderer' }
                'Recovery/mask-submit-to-receipt' { 'first-ground-mask' }
            }
            $intervals[$key] = [pscustomobject]@{
                Cohort = $Mode; Run = $Sample; Warmup = ($Sample -eq 0); Path = $path; Phase = $phase; MeasurementScope = $scope
                FromQpc = $from; ToQpc = $to; Frequency = $qpc; Milliseconds = (($to - $from) * 1000.0 / $qpc)
                Attempts = $attempts; Denials = $denials
            }
        } elseif ($line -cmatch '^WYU R09 TEXTURECOPY (Movement|FirstEdit) ((?:[0-9]+ ){15}[0-9]+)$') {
            $path = $Matches[1]
            if (!$IndependentTextureCopy -or $textureCopies.ContainsKey($path)) {
                throw "Unexpected or duplicate independent texture copy for $path."
            }
            $epoch,$ticket,$fence,$queued,$submitted,$started,$finished,$released,$mask,$presentFence,$presented,
                $retired,$heldFence,$retiredFence,$textureBytes,$stagingBytes = $Matches[2].Split(' ') | ForEach-Object { [uint64]$_ }
            if (!$epoch -or !$ticket -or !$fence -or !$queued -or !$mask -or $queued -gt $submitted -or
                $submitted -gt $started -or $started -ge $finished -or $finished -gt $released -or $released -gt $presented -or
                $presented -gt $retired -or $heldFence -ge $fence -or $presentFence -le $fence -or
                $retiredFence -lt $presentFence -or $textureBytes -lt 2MB -or $stagingBytes -lt 2MB) {
                throw "Invalid native copy fence, retained bytes, or pressure interval for $path."
            }
            $textureCopies[$path] = [pscustomobject]@{
                Cohort=$Mode; Run=$Sample; Warmup=($Sample -eq 0); Path=$path; Epoch=$epoch; Ticket=$ticket; CopyFence=$fence
                WaitQueuedQpc=$queued; SubmittedQpc=$submitted; WorkerStartQpc=$started; WorkerEndQpc=$finished
                ReleasedQpc=$released; MaskSequence=$mask; MaskDrawFence=$null; MaskPresentFence=$presentFence; MaskPresentedQpc=$presented
                RetiredObservationQpc=$retired; HeldCompletedFence=$heldFence; RetiredCompletedFence=$retiredFence
                TextureRetainedBytes=$textureBytes; StagingRetainedBytes=$stagingBytes; OverlapMs=$null; ReleaseToRetiredMs=$null
            }
        } elseif ($line -cmatch '^WYU R09 TWILIGHT (Movement|FirstEdit|Recovery) (far-horizon) (wyu\.far-horizon\.atmosphere\.v2) ([0-9]+) ([0-9]+) ([0-9]+) ([0-9]+) (2)$') {
            $path = $Matches[1]
            $epoch,$mask,$drawFence,$presentFence,$resourceUses = 4..8 | ForEach-Object { [uint64]$Matches[$_] }
            if (!$TwilightPressure -or $twilightDraws.ContainsKey($path) -or !$epoch -or !$mask -or
                !$drawFence -or $drawFence -ge $presentFence) {
                throw "Invalid native Direct Twilight draw for $path."
            }
            $twilightDraws[$path] = [pscustomobject]@{
                Composition=$Matches[2]; Profile=$Matches[3]; Epoch=$epoch; MaskSequence=$mask
                DrawFence=$drawFence; PresentFence=$presentFence; SkyResourceUses=$resourceUses
            }
        } elseif ($line.StartsWith('WYU R09 ', [StringComparison]::Ordinal)) {
            throw "Malformed R09 record: $line"
        }
    }
    $brickRows = [Collections.Generic.List[object]]::new()
    $meshRows = [Collections.Generic.List[object]]::new()
    $brickTimingRows = [Collections.Generic.List[object]]::new()
    $brickOverlap = [Collections.Generic.List[object]]::new()
    $meshOverlap = [Collections.Generic.List[object]]::new()
    $generations = [Collections.Generic.HashSet[uint64]]::new()
    foreach ($identity in $identities.Values) {
        if ($TwilightPressure -and $twilightDraws[$identity.Path]?.Epoch -ne $identity.Epoch) {
            throw "Missing native Direct Twilight draw for $($identity.Path) in renderer epoch $($identity.Epoch)."
        }
        if (!$generations.Add($identity.Generation)) { throw "Duplicate publishing generation: $($identity.Generation)" }
    }
    foreach ($path in $paths) {
        foreach ($phase in $phases) {
            if (!$intervals.ContainsKey("$path/$phase")) { throw "Missing interval: $path/$phase" }
        }
        if (!$identities.ContainsKey($path)) { throw "Missing identity: $path" }
        if (!$pressure.ContainsKey($path)) { throw "Missing pressure: $path" }
        Assert-WorkerProfile $intervals $path 'worker' $workerStages
        $hold = $intervals["$path/held-upload"]
        $publishingQueue = $intervals["$path/worker-queue"]
        $publishingObserved = $intervals["$path/worker-completion-observation"]
        $admission = $intervals["$path/admission"]
        $route = $intervals["$path/setup-to-begin-tick"]
        $upload = $intervals["$path/upload-to-observed-ready"]
        $release = $intervals["$path/held-upload-release-to-ready"]
        if ($admission.FromQpc -lt $route.FromQpc -or $admission.ToQpc -gt $publishingQueue.FromQpc -or
            $publishingObserved.ToQpc -gt $upload.FromQpc -or
            $upload.ToQpc -gt $route.ToQpc -or
            $hold.FromQpc -lt $upload.FromQpc -or $hold.ToQpc -ne $release.FromQpc -or
            $release.ToQpc -ne $upload.ToQpc) {
            throw "Invalid admission, publishing or upload ordering for $path."
        }
        if ($IndependentTextureCopy -and $path -cne 'Recovery') {
            $copy = $textureCopies[$path]
            $twilight = $twilightDraws[$path]
            $worker = $intervals["$path/worker"]
            if (!$copy -or $copy.Epoch -ne $identities[$path].Epoch -or $copy.MaskSequence -ge $identities[$path].Frame -or
                $copy.WaitQueuedQpc -lt $route.FromQpc -or $copy.WorkerStartQpc -ne $worker.FromQpc -or
                $copy.WorkerEndQpc -ne $worker.ToQpc -or $copy.RetiredObservationQpc -gt $upload.FromQpc) {
                throw "Missing or mismatched independent native texture copy for $path."
            }
            if ($copy.MaskSequence -ne $twilight.MaskSequence -or $copy.MaskPresentFence -ne $twilight.PresentFence -or
                $copy.CopyFence -ge $twilight.DrawFence) {
                throw "Independent native texture copy does not precede the identified Direct Twilight mask for $path."
            }
            $copy.MaskDrawFence = $twilight.DrawFence
            $copy.OverlapMs = 1000.0 * ($copy.WorkerEndQpc - $copy.WorkerStartQpc) / $frequency
            $copy.ReleaseToRetiredMs = 1000.0 * ($copy.RetiredObservationQpc - $copy.ReleasedQpc) / $frequency
        }
        $transfer = $reuse[$path]
        if ($null -eq $transfer -or $transfer.PublishingGeneration -ne $identities[$path].Generation) {
            throw "Missing reuse source or wrong publishing generation for $path."
        }
        $hasPrivate = $transfer.PrivateGeneration -ne 0
        foreach ($phase in $privatePhases) {
            if ($intervals.ContainsKey("$path/$phase") -ne $hasPrivate) {
                throw "Private profile does not match the offered source for $path/$phase."
            }
        }
        if ($hasPrivate) {
            Assert-WorkerProfile $intervals $path 'prebuild-worker' $workerStages
            $prebuildQueue = $intervals["$path/prebuild-worker-queue"]
            $prebuildObserved = $intervals["$path/prebuild-worker-completion-observation"]
        } elseif ($bricksByGroup["$path/private"].Count -or $meshesByGroup["$path/private"].Count -or
            $brickTimings.ContainsKey("$path/private")) {
            throw "Private inventory has no offered source for $path."
        }
        if ($path -ceq 'Recovery') {
            if (!$hasPrivate -or !$generations.Add($transfer.PrivateGeneration) -or
                $prebuildQueue.FromQpc -lt $admission.ToQpc -or
                $prebuildObserved.ToQpc -gt $publishingQueue.FromQpc) {
                throw 'Recovery requires a synchronous private profile before publishing.'
            }
            $transfer.EarlyStatus = 'Synchronous'
            $privateBricks = $bricksByGroup['Recovery/private']
            $seeded = @($privateBricks.Values | Where-Object Reused).Count
            if ($null -eq $recoveryBrickSource -or
                $recoveryBrickSource -ge $transfer.PublishingGeneration -or
                ($seeded -and !$recoveryBrickSource) -or
                ($RequireRecoveryColdFallback -and ($recoveryBrickSource -or $seeded))) {
                throw 'Invalid Recovery private brick source or fallback evidence.'
            }
            $transfer.PrivateBrickSourceGeneration = $recoveryBrickSource
            $transfer.ReusedPrivateBricks = $seeded
            $transfer.RebuiltPrivateBricks = $privateBricks.Count - $seeded
            $privateMeshes = $meshesByGroup['Recovery/private']
            $meshHits = @($privateMeshes.Values | Where-Object Reused).Count
            if ($null -eq $recoveryMeshPlanReused -or
                ($recoveryMeshPlanReused -and (!$recoveryBrickSource -or $seeded -ne $privateBricks.Count -or
                    $meshHits -ne $privateMeshes.Count)) -or
                (!$recoveryMeshPlanReused -and $meshHits) -or
                (($RequireRecoveryMeshRebuild -or $RequireRecoveryColdFallback) -and $recoveryMeshPlanReused)) {
                throw 'Invalid Recovery private mesh evidence.'
            }
            $transfer.ReusedPrivateMeshPlan = $recoveryMeshPlanReused
            $transfer.ReusedPrivateMeshes = $meshHits
            $transfer.RebuiltPrivateMeshes = $privateMeshes.Count - $meshHits
        } else {
            $decision = $early[$path]
            if ($null -eq $decision -or $decision.AttemptQpc -gt $route.FromQpc -or
                $decision.CutQpc -lt $admission.ToQpc -or $decision.CutQpc -gt $publishingQueue.FromQpc -or
                ($decision.Generation -and ($decision.Generation -ge $transfer.PublishingGeneration -or
                    !$generations.Add($decision.Generation))) -or
                (($decision.Status -ceq 'Ready') -ne $hasPrivate) -or
                ($hasPrivate -and ($transfer.PrivateGeneration -ne $decision.Generation -or
                    $prebuildQueue.FromQpc -lt $decision.AttemptQpc -or $prebuildQueue.FromQpc -gt $route.FromQpc -or
                    $prebuildObserved.ToQpc -gt $route.FromQpc))) {
                throw "Invalid early prebuild ordering or source identity for $path."
            }
            $transfer.EarlyStatus = $decision.Status
            $transfer.EarlyAttemptQpc = $decision.AttemptQpc
            $transfer.EarlyCutQpc = $decision.CutQpc
            $transfer.EarlyAdmittedGeneration = $decision.Generation
            if ($decision.AttemptQpc) {
                $transfer.EarlyLeadMilliseconds = (($route.FromQpc - $decision.AttemptQpc) * 1000.0 / $frequency)
            }
            if ($hasPrivate) {
                $transfer.ReadyBeforeCutMilliseconds = (($decision.CutQpc - $prebuildObserved.ToQpc) * 1000.0 / $frequency)
            }
        }
        $measuredRoles = if ($hasPrivate) { $roles } else { @('publishing') }
        foreach ($role in $measuredRoles) {
            $group = "$path/$role"
            $bricks = $bricksByGroup[$group]
            $meshes = $meshesByGroup[$group]
            if (!$meshes.Count -or $meshes.Count * 8 -ne $bricks.Count) {
                throw "Invalid inventory for $group. Each mesh must account for eight bricks."
            }
            if (!$brickTimings.ContainsKey($group)) { throw "Missing brick timing for $group." }
            $worker = if ($role -eq 'publishing') { 'worker' } else { 'prebuild-worker' }
            $outerBricks = $intervals["$path/$worker-bricks"]
            $brickItemTicks = Assert-InventoryTiming $bricks $outerBricks $frequency "$group/bricks"
            $null = Assert-InventoryTiming $meshes $intervals["$path/$worker-mesh"] $frequency "$group/meshes"
            $total = $outerBricks.ToQpc - $outerBricks.FromQpc
            $remaining = $brickItemTicks
            $stageTicks = $brickTimings[$group]
            foreach ($ticks in $stageTicks) {
                if ($ticks -gt $remaining) { throw "Brick stage ticks exceed the measured brick item total for $group." }
                $remaining -= $ticks
            }
            $stageTicks = $stageTicks + @($total - $brickItemTicks + $remaining)
            for ($index = 0; $index -lt $brickStages.Count; $index++) {
                $ticks = $stageTicks[$index]
                $brickTimingRows.Add([pscustomobject]@{
                    Cohort = $Mode; Run = $Sample; Warmup = ($Sample -eq 0); Path = $path; Role = $role
                    Phase = $brickStages[$index]; Ticks = $ticks; TotalBrickTicks = $total; Frequency = $frequency
                    Milliseconds = ([double]$ticks * 1000.0 / $frequency)
                })
            }
            foreach ($record in $bricks.Values) { $brickRows.Add($record) }
            foreach ($record in $meshes.Values) { $meshRows.Add($record) }
        }
        $brickMatch = Compare-BrickInventory $bricksByGroup["$path/publishing"] $bricksByGroup["$path/private"] $frequency $Mode $Sample $path
        $meshMatch = Compare-MeshInventory $meshesByGroup["$path/publishing"] $meshesByGroup["$path/private"] $frequency $Mode $Sample $path
        if (($brickMatch.ReusedBricks -or $meshMatch.ReusedMeshes) -and !$transfer.ReuseSourceGeneration) {
            throw "Reused entries have no source generation for $path."
        }
        $transfer.ReusedBricks = $brickMatch.ReusedBricks
        $transfer.RebuiltBricks = $brickMatch.RebuiltBricks
        $transfer.ReusedMeshes = $meshMatch.ReusedMeshes
        $transfer.RebuiltMeshes = $meshMatch.RebuiltMeshes
        $brickOverlap.Add($brickMatch)
        $meshOverlap.Add($meshMatch)
    }
    $route = $intervals['Recovery/setup-to-begin-tick']
    $handoff = $intervals['Recovery/handoff-to-begin-tick']
    $overlap = $intervals['Recovery/flight-overlap-window']
    $noFlight = $intervals['Recovery/recovery-no-flight-to-begin-tick']
    if ($null -eq $handoff -or $handoff.ToQpc -ne $route.ToQpc) { throw 'Invalid Recovery handoff endpoint.' }
    if ($RecoveryMode -eq 'AtHandoff') {
        if ($null -eq $noFlight -or $null -ne $overlap -or $noFlight.FromQpc -ne $route.FromQpc -or
            $noFlight.ToQpc -ne $route.ToQpc -or $handoff.FromQpc -ne $route.FromQpc -or
            $intervals['Recovery/admission'].FromQpc -ne $route.FromQpc) {
            throw 'Recovery without flight must start at handoff and include admission through the consuming tick.'
        }
    } elseif ($null -eq $overlap -or $null -ne $noFlight -or $overlap.FromQpc -ne $route.FromQpc -or
        $overlap.ToQpc -ne $handoff.FromQpc -or $overlap.ToQpc -le $overlap.FromQpc) {
        throw 'Recovery with flight must report the observed preload-to-handoff window separately.'
    }
    foreach ($phase in $r09RendererPhases) {
        if (!$intervals.ContainsKey("Recovery/$phase")) { throw "Missing interval: Recovery/$phase" }
    }
    $farStop = $intervals['Recovery/far-renderer-stop']
    $groundStartup = $intervals['Recovery/ground-renderer-startup']
    $rendererSwitch = $intervals['Recovery/ground-renderer-switch']
    $rendererOverlap = $intervals['Recovery/private-build-renderer-overlap']
    $groundMask = $intervals['Recovery/mask-submit-to-receipt']
    $privateBuild = $intervals['Recovery/prebuild-worker']
    if ($rendererSwitch.FromQpc -lt $handoff.FromQpc -or $rendererSwitch.FromQpc -ne $farStop.FromQpc -or
        $farStop.ToQpc -gt $groundStartup.FromQpc -or $groundStartup.ToQpc -ne $rendererSwitch.ToQpc -or
        $rendererSwitch.ToQpc -gt $groundMask.FromQpc -or
        $groundMask.ToQpc -gt $intervals['Recovery/upload-to-observed-ready'].FromQpc) {
        throw 'Invalid Recovery renderer switch or ground mask ordering.'
    }
    $overlapStart = [Math]::Max($rendererSwitch.FromQpc, $privateBuild.FromQpc)
    $overlapEnd = [Math]::Min($rendererSwitch.ToQpc, $privateBuild.ToQpc)
    if ($overlapEnd -le $overlapStart) {
        $overlapStart = $rendererSwitch.FromQpc
        $overlapEnd = $overlapStart
    }
    if ($rendererOverlap.FromQpc -ne $overlapStart -or $rendererOverlap.ToQpc -ne $overlapEnd) {
        throw 'Recovery renderer overlap does not match the private worker and renderer switch intervals.'
    }
    if ($IndependentTextureCopy) {
        $movementCopy = $textureCopies['Movement']
        $editCopy = $textureCopies['FirstEdit']
        if ($editCopy.Epoch -ne $movementCopy.Epoch -or $editCopy.Ticket -le $movementCopy.Ticket -or
            $editCopy.HeldCompletedFence -lt $movementCopy.RetiredCompletedFence -or
            $editCopy.WaitQueuedQpc -le $movementCopy.RetiredObservationQpc) {
            throw 'FirstEdit must use a distinct native texture copy in the same renderer epoch after Movement retires.'
        }
    }
    $resourcePeaks = @(Complete-ResourceSamples $resourceSamples $gpuOwners $intervals $identities $frequency)
    [pscustomobject]@{
        Intervals = $intervals.Values; Identities = $identities.Values; Pressure = $pressure.Values; Frequency = $frequency
        Bricks = $brickRows; Meshes = $meshRows; BrickTimings = $brickTimingRows
        BrickOverlap = $brickOverlap; MeshOverlap = $meshOverlap; Reuse = $reuse.Values
        ResourceSamples = $resourceSamples.Values; GpuOwners = $gpuOwners.Values; ResourcePeaks = $resourcePeaks
        TextureCopies = $textureCopies.Values
    }
}

$out = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $out) -or (Test-Path -LiteralPath "$out.zip")) { throw 'Choose a new evidence output directory.' }
$installed = Get-Item -LiteralPath $InstalledDirectory
if (!$installed.PSIsContainer -or ($installed.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Use a regular installed WYU directory.'
}
$installRoot = $installed.FullName
$installPrefix = [IO.Path]::TrimEndingDirectorySeparator($installRoot) + [IO.Path]::DirectorySeparatorChar
if ($out.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Store evidence outside the install directory.' }
[void][IO.Directory]::CreateDirectory($out)

$previousCulture = [Globalization.CultureInfo]::CurrentCulture
[Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::InvariantCulture
$rows = [Collections.Generic.List[object]]::new()
$identities = [Collections.Generic.List[object]]::new()
$pressure = [Collections.Generic.List[object]]::new()
$resourceSampleRows = [Collections.Generic.List[object]]::new()
$gpuOwnerRows = [Collections.Generic.List[object]]::new()
$resourcePeakRows = [Collections.Generic.List[object]]::new()
$bricks = [Collections.Generic.List[object]]::new()
$meshes = [Collections.Generic.List[object]]::new()
$brickTimings = [Collections.Generic.List[object]]::new()
$brickOverlap = [Collections.Generic.List[object]]::new()
$meshOverlap = [Collections.Generic.List[object]]::new()
$reuseRows = [Collections.Generic.List[object]]::new()
$textureCopyRows = [Collections.Generic.List[object]]::new()
$work = $null
try {
    $manifestFile = (Get-Item -LiteralPath $ManifestPath).FullName
    $archivedManifest = Join-Path $out 'installer-manifest.json'
    Copy-Item -LiteralPath $manifestFile -Destination $archivedManifest
    $manifestHash = (Get-FileHash -LiteralPath $archivedManifest).Hash
    Get-FileHash -LiteralPath $archivedManifest,$PSCommandPath |
        Select-Object @{n='File';e={Split-Path $_.Path -Leaf}},Hash |
        Export-Csv -LiteralPath (Join-Path $out 'input-hashes.csv') -NoTypeInformation
    if ($manifestHash -ine $ExpectedManifestSha256) { throw 'Unexpected installer manifest digest.' }
    $manifest = Get-Content -LiteralPath $archivedManifest -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.target -cne 'win-x64' -or
        $manifest.wyuVersion -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or
        $manifest.wyuCommit -cnotmatch '^[A-Fa-f0-9]{40}$' -or $manifest.groundworkCommit -cnotmatch '^[A-Fa-f0-9]{40}$' -or
        $manifest.groundworkManifestSha256 -cnotmatch '^[A-Fa-f0-9]{64}$' -or
        $manifest.installerSha256 -cnotmatch '^[A-Fa-f0-9]{64}$') { throw 'Unexpected win-x64 installer manifest.' }
    if ($DiagnosticR09Installer -and $manifest.installer -cnotmatch '^WYU-R09-\d+\.\d+\.\d+-win-x64\.exe$') {
        throw 'The local R09 diagnostic run requires the verified R09-specific installer.'
    }
    if ($TwilightPressure -and !$DiagnosticR09Installer -and
        $manifest.installer -cnotmatch '^WYU-Twilight-Preview-Unqualified-\d+\.\d+\.\d+-win-x64\.exe$') {
        throw 'Twilight pressure requires the verified Twilight Preview installer.'
    }
    Assert-InstalledPayload $installRoot $manifest
    $exe = Join-Path $installRoot 'WorldsYetUnseen.Client.exe'
    $meta = [ordered]@{
        Schema = 'wyu-r09-installed-flight-v11'; UTC = [DateTime]::UtcNow.ToString('o')
        OS = [Environment]::OSVersion.VersionString; LogicalProcessors = [Environment]::ProcessorCount
        Adapter = $Adapter; Cohorts = $Cohort; SamplesPerCohort = $Samples; WarmupsPerCohort = 1
        TwilightPressure = [bool]$TwilightPressure
        IndependentGpuCopyPressure = $false
        IndependentGpuCopyPaths = if ($IndependentTextureCopy) { @('Movement','FirstEdit') } else { @() }
        DiagnosticR09Installer = [bool]$DiagnosticR09Installer
        InstallerEvidence = if ($DiagnosticR09Installer) { 'Local R09 diagnostic only' }
            elseif ($TwilightPressure) { 'Unqualified Twilight Preview' } else { 'Installed R09 fixture' }
        RecoveryMode = $RecoveryMode
        EarlyPrefetchMode = if ($RequireEarlyColdFallback) { 'forced-pending' } else { 'natural' }
        RecoveryBrickMode = if ($RequireRecoveryColdFallback) { 'forced-cold' } else { 'published-bricks' }
        RecoveryMeshMode = if ($RequireRecoveryMeshRebuild -or $RequireRecoveryColdFallback) {
            'forced-mesh-rebuild'
        } else { 'published-terrain' }
        WyuCommit = $manifest.wyuCommit; GroundworkCommit = $manifest.groundworkCommit
        GroundworkVersion = $manifest.groundworkVersion; GroundworkManifestSha256 = $manifest.groundworkManifestSha256
        InstallerSha256 = $manifest.installerSha256; InstallerManifestSha256 = $manifestHash
        Runtime = $manifest.runtime; SDK = $manifest.sdk
    }
    try {
        $meta.Processor = @(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors)
        $meta.MemoryBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
        $meta.Video = @(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion)
    } catch { $meta.InventoryError = $_.Exception.Message }
    $meta | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out 'machine.json')

    $temp = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).ProviderPath
    $work = Join-Path $temp ('wyu-r09-slots-' + [guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $work) { throw 'Measurement slot directory already exists.' }
    [void][IO.Directory]::CreateDirectory($work)
    foreach ($mode in ($Cohort | Select-Object -Unique)) {
        for ($sample = 0; $sample -le $Samples; $sample++) {
            $label = "$mode-$sample"
            $slot = Join-Path $work $label
            [void][IO.Directory]::CreateDirectory($slot)
            [IO.File]::WriteAllText((Join-Path $slot 'wyu-volume-smoke.fixture'), '')
            $log = Join-Path $out "$label.stdout"
            $errors = Join-Path $out "$label.stderr"
            Invoke-Measurement $exe $slot $mode $log $errors
            $lines = @(Get-Content -LiteralPath $log)
            $finalReport = $lines -cmatch '^D3D12 live-object report:' | Select-Object -Last 1
            $retainedSuffix = if ($TwilightPressure) { '; retained preview objects=0' } else { '' }
            $settledPattern = 'errors=0, corruption=0, discarded=0, HRESULT=0x00000000, live child objects=0, retained session queue=False' + $retainedSuffix + '\.$'
            if ((Get-Item -LiteralPath $errors).Length -or !($lines -cmatch '^WP11 LANDED generation=2 ') -or
                $lines -cnotcontains 'PASS: WYU WP11 exploration, rebase and native shutdown.' -or
                $finalReport -cnotmatch $settledPattern -or
                ($mode -eq 'Retry' -and $lines -cnotcontains 'WP11 HOST RETRY SETTLED terrain=0 readers=0 generations=0')) {
                throw "$label did not demonstrate landing and clean settlement. See $log and $errors."
            }
            $run = Read-Measurements $lines $mode $sample
            if ($RequireEarlyColdFallback) {
                foreach ($decision in $run.Reuse | Where-Object Path -In @('Movement','FirstEdit')) {
                    if ($decision.EarlyStatus -cne 'Pending' -or !$decision.ColdFallback) {
                        throw "$label did not demonstrate forced pending fallback for $($decision.Path)."
                    }
                }
            }
            if ($rows.Count -gt 0 -and $run.Frequency -ne $rows[0].Frequency) {
                throw "$label used a different QPC frequency. See $log."
            }
            foreach ($record in $run.Intervals) { $rows.Add($record) }
            foreach ($record in $run.Identities) { $identities.Add($record) }
            foreach ($record in $run.Pressure) { $pressure.Add($record) }
            foreach ($record in $run.ResourceSamples) { $resourceSampleRows.Add($record) }
            foreach ($record in $run.GpuOwners) { $gpuOwnerRows.Add($record) }
            $resourcePeakRows.AddRange($run.ResourcePeaks)
            $bricks.AddRange($run.Bricks)
            $meshes.AddRange($run.Meshes)
            $brickTimings.AddRange($run.BrickTimings)
            $brickOverlap.AddRange($run.BrickOverlap)
            $meshOverlap.AddRange($run.MeshOverlap)
            foreach ($record in $run.Reuse) { $reuseRows.Add($record) }
            foreach ($record in $run.TextureCopies) { $textureCopyRows.Add($record) }
            Write-Host "$label passed."
        }
    }

    $ceilings = @{ Movement = @(1500,3000); FirstEdit = @(1550,3250); Recovery = @(2000,4000) }
    $summary = $rows | Where-Object { !$_.Warmup } | Group-Object Cohort,Path,Phase | ForEach-Object {
        $g = $_.Group[0]
        $values = @($_.Group.Milliseconds | Sort-Object)
        $n = $values.Count
        $limit = switch ($g.Phase) {
            'worker' { $ceilings[$g.Path][0] }
            'setup-to-begin-tick' { if ($g.Path -ne 'Recovery') { $ceilings[$g.Path][1] } }
            'recovery-no-flight-to-begin-tick' { $ceilings.Recovery[1] }
            'held-upload-release-to-ready' { 50 }
            'authorized-receipt-to-begin-tick' { 50 }
        }
        [pscustomobject]@{
            Cohort = $g.Cohort; Path = $g.Path; Phase = $g.Phase; MeasurementScope = $g.MeasurementScope; Samples = $n
            P50Ms = $values[[int][Math]::Ceiling($n * 0.50) - 1]
            P95Ms = $values[[int][Math]::Ceiling($n * 0.95) - 1]
            P99Ms = $values[[int][Math]::Ceiling($n * 0.99) - 1]
            MaxMs = $values[-1]; ProposedLimitMs = $limit
            AboveProposedLimit = if ($null -ne $limit) { @($values | Where-Object { $_ -gt $limit }).Count }
        }
    }
    $summary | Sort-Object Cohort,Path,Phase | Export-Csv -LiteralPath (Join-Path $out 'summary.csv') -NoTypeInformation
} catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $out 'run-error.txt')
    throw
} finally {
    try {
        $rows | Sort-Object Cohort,Run,Path,Phase | Export-Csv -LiteralPath (Join-Path $out 'observations.csv') -NoTypeInformation
        $identities | Sort-Object Cohort,Run,Path | Export-Csv -LiteralPath (Join-Path $out 'identities.csv') -NoTypeInformation
        $pressure | Sort-Object Cohort,Run,Path | Export-Csv -LiteralPath (Join-Path $out 'pressure.csv') -NoTypeInformation
        $resourceSampleRows | Sort-Object Cohort,Run,Qpc | Export-Csv -LiteralPath (Join-Path $out 'resource-samples.csv') -NoTypeInformation
        $gpuOwnerRows | Sort-Object Cohort,Run,Path,Point,Owner | Export-Csv -LiteralPath (Join-Path $out 'gpu-owners.csv') -NoTypeInformation
        $resourcePeakRows | Sort-Object Cohort,Run,Path | Export-Csv -LiteralPath (Join-Path $out 'combined-peaks.csv') -NoTypeInformation
        $bricks | Sort-Object Cohort,Run,Path,Role,BrickX,BrickY,BrickZ | Export-Csv -LiteralPath (Join-Path $out 'brick-inventory.csv') -NoTypeInformation
        $meshes | Sort-Object Cohort,Run,Path,Role,OriginX,OriginY,OriginZ | Export-Csv -LiteralPath (Join-Path $out 'mesh-inventory.csv') -NoTypeInformation
        $brickTimings | Sort-Object Cohort,Run,Path,Role,Phase | Export-Csv -LiteralPath (Join-Path $out 'brick-timings.csv') -NoTypeInformation
        $brickOverlap | Sort-Object Cohort,Run,Path | Export-Csv -LiteralPath (Join-Path $out 'brick-overlap.csv') -NoTypeInformation
        $meshOverlap | Sort-Object Cohort,Run,Path | Export-Csv -LiteralPath (Join-Path $out 'mesh-overlap.csv') -NoTypeInformation
        $reuseRows | Sort-Object Cohort,Run,Path | Export-Csv -LiteralPath (Join-Path $out 'reuse.csv') -NoTypeInformation
        if ($IndependentTextureCopy) {
            $textureCopyRows | Sort-Object Cohort,Run,Path | Export-Csv -LiteralPath (Join-Path $out 'independent-texture-copy.csv') -NoTypeInformation
        }
        $evidence = Get-ChildItem -LiteralPath $out -File
        Compress-Archive -LiteralPath $evidence.FullName -DestinationPath "$out.zip"
        Write-Host "Evidence: $out.zip"
    } finally {
        [Globalization.CultureInfo]::CurrentCulture = $previousCulture
        if ($work -and (Test-Path -LiteralPath $work)) {
            $slotRoot = Get-Item -LiteralPath $work
            if (($slotRoot.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                (Split-Path -Parent $slotRoot.FullName) -ine [IO.Path]::TrimEndingDirectorySeparator($temp)) {
                throw 'Refusing to remove an unexpected measurement slot directory.'
            }
            Remove-Item -LiteralPath $slotRoot.FullName -Recurse -Force
        }
    }
}
