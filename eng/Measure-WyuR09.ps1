param(
    [Parameter(Mandatory)][string]$InstalledDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$ManifestPath = (Join-Path $PSScriptRoot 'installer-manifest.json'),
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedManifestSha256,
    [ValidateRange(1,50)][int]$Samples = 5,
    [ValidateSet('hardware','warp')][string]$Adapter = 'hardware',
    [ValidateSet('Normal','Retry')][string[]]$Cohort = @('Normal','Retry')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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
    foreach ($name in 'WYU_HOST_RETRY_SMOKE','WYU_WP13_MEASURE','WYU_PACKAGE_G','WYU_PACKAGE_G_SMOKE',
        'WYU_WP16','WYU_WP16_SMOKE','WYU_WP15','WYU_WP15_PROBE','WYU_WP15_FLIP','WYU_PACKAGE_E',
        'WYU_PACKAGE_E_MEASURE','WYU_PACKAGE_E_WALK','WYU_PACKAGE_E_TIMESTAMP_PAIRS',
        'DOTNET_STARTUP_HOOKS','DOTNET_ADDITIONAL_DEPS','DOTNET_SHARED_STORE','DOTNET_DIAGNOSTIC_PORTS',
        'CORECLR_ENABLE_PROFILING','CORECLR_PROFILER','CORECLR_PROFILER_PATH','CORECLR_PROFILER_PATH_32','CORECLR_PROFILER_PATH_64',
        'COR_ENABLE_PROFILING','COR_PROFILER','COR_PROFILER_PATH','COR_PROFILER_PATH_32','COR_PROFILER_PATH_64') {
        [void]$start.Environment.Remove($name)
    }
    $start.Environment['WYU_R09_LATENCY'] = '1'
    if ($Mode -eq 'Retry') { $start.Environment['WYU_HOST_RETRY_SMOKE'] = '1' }

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

function Read-Measurements([string[]]$Lines, [string]$Mode, [int]$Sample) {
    $paths = @('Movement','FirstEdit','Recovery')
    $phases = @('worker','worker-queue','worker-completion-observation','admission',
        'upload-to-observed-ready','held-upload-release-to-ready','submit-to-observed-present',
        'observed-present-to-begin-tick','authorized-receipt-to-begin-tick','setup-to-begin-tick')
    $intervals = @{}
    $identities = @{}
    $pressure = @{}
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
                ($phases -cnotcontains $phase -and $key -cne 'Recovery/handoff-to-begin-tick')) {
                throw "Invalid or duplicate interval: $line"
            }
            $frequency = $qpc
            $scope = if ($key -eq 'Recovery/setup-to-begin-tick') { 'includes-in-flight-overlap' }
            $intervals[$key] = [pscustomobject]@{
                Cohort = $Mode; Run = $Sample; Warmup = ($Sample -eq 0); Path = $path; Phase = $phase; MeasurementScope = $scope
                FromQpc = $from; ToQpc = $to; Frequency = $qpc; Milliseconds = (($to - $from) * 1000.0 / $qpc)
                Attempts = $attempts; Denials = $denials
            }
        } elseif ($line.StartsWith('WYU R09 ', [StringComparison]::Ordinal)) {
            throw "Malformed R09 record: $line"
        }
    }
    foreach ($path in $paths) {
        foreach ($phase in $phases) {
            if (!$intervals.ContainsKey("$path/$phase")) { throw "Missing interval: $path/$phase" }
        }
        if (!$identities.ContainsKey($path)) { throw "Missing identity: $path" }
        if (!$pressure.ContainsKey($path)) { throw "Missing pressure: $path" }
    }
    if (!$intervals.ContainsKey('Recovery/handoff-to-begin-tick')) { throw 'Missing recovery handoff interval.' }
    [pscustomobject]@{ Intervals = $intervals.Values; Identities = $identities.Values; Pressure = $pressure.Values }
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
    Assert-InstalledPayload $installRoot $manifest
    $exe = Join-Path $installRoot 'WorldsYetUnseen.Client.exe'
    $meta = [ordered]@{
        Schema = 'wyu-r09-installed-flight-v2'; UTC = [DateTime]::UtcNow.ToString('o')
        OS = [Environment]::OSVersion.VersionString; LogicalProcessors = [Environment]::ProcessorCount
        Adapter = $Adapter; Cohorts = $Cohort; SamplesPerCohort = $Samples; WarmupsPerCohort = 1
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
            if ((Get-Item -LiteralPath $errors).Length -or !($lines -cmatch '^WP11 LANDED generation=2 ') -or
                $lines -cnotcontains 'PASS: WYU WP11 exploration, rebase and native shutdown.' -or
                $finalReport -cnotmatch 'errors=0, corruption=0, discarded=0, HRESULT=0x00000000, live child objects=0, retained session queue=False\.$' -or
                ($mode -eq 'Retry' -and $lines -cnotcontains 'WP11 HOST RETRY SETTLED terrain=0 readers=0 generations=0')) {
                throw "$label did not demonstrate landing and clean settlement. See $log and $errors."
            }
            $run = Read-Measurements $lines $mode $sample
            foreach ($record in $run.Intervals) { $rows.Add($record) }
            foreach ($record in $run.Identities) { $identities.Add($record) }
            foreach ($record in $run.Pressure) { $pressure.Add($record) }
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
