param(
    [string]$Assembly = (Join-Path $PSScriptRoot '../artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll'),
    [ValidateSet('far-horizon', 'orbit-combined')][string]$Composition = 'far-horizon',
    [ValidateSet('hardware', 'warp')][string]$Adapter = 'hardware',
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('wyu-sky-runner-fault-' + [guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$priorPath = $env:PATH
try {
    # Mock shell metadata only. ProcessStartInfo still invokes the native process
    # resolver, whose empty PATH makes the actual replay launch fail before a PID exists.
    function git {
        $global:LASTEXITCODE = 0
        if ($args -contains 'rev-parse') { '0000000000000000000000000000000000000000' }
    }
    function dotnet { 'fault-test-metadata-only' }
    $env:PATH = ''
    $failure = $null
    try {
        & (Join-Path $PSScriptRoot 'Measure-ReferenceSky.ps1') -Smoke -Composition $Composition -Adapter $Adapter -Assembly $Assembly -OutputDirectory $OutputDirectory
    } catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or $failure -notmatch 'start process.*dotnet') {
        throw "Expected the original native launch failure, received: $failure"
    }
    $attempts = @(Import-Csv -LiteralPath (Join-Path $OutputDirectory 'attempts.csv'))
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $OutputDirectory 'manifest.json') | ConvertFrom-Json
    $result = Get-Content -Raw -LiteralPath (Join-Path $OutputDirectory 'result.json') | ConvertFrom-Json
    if ($manifest.Composition -cne $Composition.ToLowerInvariant() -or $manifest.Adapter -cne $Adapter.ToLowerInvariant() -or $attempts.Count -ne 1 -or
        $attempts[0].Composition -cne $Composition.ToLowerInvariant() -or $attempts[0].Started -cne 'False' -or $attempts[0].ProcessId -ne '0' -or
        $attempts[0].Passed -cne 'False' -or $attempts[0].Failure -cne $failure -or
        $attempts[0].CleanupFailures -or $result.Status -cne 'BLOCKED' -or $result.Failure -cne $failure -or
        $result.Attempts -ne 1 -or $result.FailedAttempts -ne 1) {
        throw 'Native launch failure lost its primary error or attempt evidence.'
    }
    if ($manifest.SkyBackendOn -cne 'Direct8x4' -or $attempts[0].ExpectedSkyBackend -cne 'Disabled' -or
        $attempts[0].ObservedSkyBackend -cne 'Unverified') {
        throw 'Native launch failure was assigned an observed sky backend without evidence.'
    }
    Write-Host "PASS: Native launch failure preserved PID=0, attempts.csv and BLOCKED result. Evidence $OutputDirectory"

    $lookupOutput=Join-Path $OutputDirectory 'lookup-smoke'
    $lookupFailure=$null
    try {
        & (Join-Path $PSScriptRoot 'Measure-ReferenceSky.ps1') -Smoke -LookupPrototype -Composition $Composition -Adapter $Adapter -Assembly $Assembly -OutputDirectory $lookupOutput
    } catch { $lookupFailure=$_.Exception.Message }
    if (-not (Test-Path -LiteralPath (Join-Path $lookupOutput 'manifest.json'))) {
        throw "Lookup Smoke did not create its manifest: $lookupFailure"
    }
    $lookupManifest=Get-Content -Raw (Join-Path $lookupOutput 'manifest.json') | ConvertFrom-Json
    $lookupResult=Get-Content -Raw (Join-Path $lookupOutput 'result.json') | ConvertFrom-Json
    if ($lookupFailure -notmatch 'start process.*dotnet' -or -not $lookupManifest.Diagnostics -or
        -not $lookupManifest.LookupPrototype -or $lookupManifest.DiagnosticProbe -or
        $lookupResult.Status -cne 'BLOCKED' -or $lookupResult.FullQualification) {
        throw 'Lookup Smoke did not enable its required diagnostics or lost original failure evidence.'
    }
    $forbiddenOutput=Join-Path $OutputDirectory 'lookup-full-forbidden'
    $rejected=$false
    try {
        & (Join-Path $PSScriptRoot 'Measure-ReferenceSky.ps1') -LookupPrototype -Assembly $Assembly -OutputDirectory $forbiddenOutput
    } catch { $rejected=$_.Exception.Message -like '*requires DiagnosticProbe or Smoke*' }
    if (-not $rejected -or (Test-Path -LiteralPath $forbiddenOutput)) { throw 'Lookup admitted to full acceptance.' }
    Write-Host 'PASS: Actual Lookup+Smoke enables diagnostics; Lookup without a diagnostic scope is rejected.'

    function Assert-SkyBackendProvenance {
        $runner = Join-Path $PSScriptRoot 'Measure-ReferenceSky.ps1'
        $output = Join-Path $OutputDirectory 'atlas-smoke'
        $savedAtlas = [Environment]::GetEnvironmentVariable('GROUNDWORK_SKY_SCATTERING_ATLAS')
        $savedPrefilter = [Environment]::GetEnvironmentVariable('GROUNDWORK_SKY_ATLAS_PREFILTER')
        try {
            $env:GROUNDWORK_SKY_SCATTERING_ATLAS = '1'
            $caught = $null
            try { & $runner -Smoke -Assembly $Assembly -OutputDirectory $output } catch { $caught = $_.Exception.Message }
            if ($caught -notlike '*Unset inherited sky backend options*' -or (Test-Path -LiteralPath $output)) {
                throw 'An inherited Atlas selector was silently accepted.'
            }
            $env:GROUNDWORK_SKY_SCATTERING_ATLAS = '0'
            $env:GROUNDWORK_SKY_ATLAS_PREFILTER = '0'
            foreach ($arguments in @(@{SkyBackend='ScatteringAtlas'},
                @{SkyBackend='ScatteringAtlas'; LookupPrototype=$true; Smoke=$true},
                @{SkyBackend='Direct8x4'; AtlasPrefilter=$true; Smoke=$true})) {
                $caught = $null
                try { & $runner @arguments -Assembly $Assembly -OutputDirectory $output } catch { $caught = $_.Exception.Message }
                if ($caught -notmatch 'requires DiagnosticProbe or Smoke|cannot be combined|requires the explicit' -or
                    (Test-Path -LiteralPath $output)) {
                    throw "An invalid backend combination was accepted: $caught"
                }
            }
            $caught = $null
            try {
                & $runner -Smoke -SkyBackend ScatteringAtlas -AtlasPrefilter -Assembly $Assembly -OutputDirectory $output
            } catch { $caught = $_.Exception.Message }
            $record = Get-Content -Raw -LiteralPath (Join-Path $output 'manifest.json') | ConvertFrom-Json
            $result = Get-Content -Raw -LiteralPath (Join-Path $output 'result.json') | ConvertFrom-Json
            if ($caught -notmatch 'start process.*dotnet' -or $record.SkyBackendOn -cne 'ScatteringAtlas' -or
                $record.SkyBackendOff -cne 'Disabled' -or -not $record.AtlasPrefilter -or -not $record.Diagnostics -or
                $record.SkyImplementation -match 'Direct 8x4' -or $result.SkyBackendOn -cne 'ScatteringAtlas' -or
                $env:GROUNDWORK_SKY_SCATTERING_ATLAS -cne '0' -or $env:GROUNDWORK_SKY_ATLAS_PREFILTER -cne '0') {
                throw "Explicit Atlas mode lost its manifest or previous environment: $caught"
            }
        } finally {
            if ($null -eq $savedAtlas) { [Environment]::SetEnvironmentVariable('GROUNDWORK_SKY_SCATTERING_ATLAS', [NullString]::Value) }
            else { [Environment]::SetEnvironmentVariable('GROUNDWORK_SKY_SCATTERING_ATLAS', $savedAtlas) }
            if ($null -eq $savedPrefilter) { [Environment]::SetEnvironmentVariable('GROUNDWORK_SKY_ATLAS_PREFILTER', [NullString]::Value) }
            else { [Environment]::SetEnvironmentVariable('GROUNDWORK_SKY_ATLAS_PREFILTER', $savedPrefilter) }
        }

        $tokens = $null; $parseErrors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile($runner, [ref]$tokens, [ref]$parseErrors)
        if ($parseErrors.Count) { throw 'Reference runner has syntax errors.' }
        $observer = $ast.Find({ param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-ObservedSkyBackend'
        }, $false)
        . ([scriptblock]::Create($observer.Extent.Text))
        $atlas = 'SKY SCATTERING ATLAS active compressed full chart diagnostic with shared solar depth RG16F'
        $prefilter = 'SKY SCATTERING PREFILTER active, native DEFAULT bytes 65536.'
        if ((Get-ObservedSkyBackend @() 'off' 'ScatteringAtlas' $true) -cne 'Disabled' -or
            (Get-ObservedSkyBackend @() 'on' 'Direct8x4' $false) -cne 'Direct8x4' -or
            (Get-ObservedSkyBackend @($atlas, $prefilter) 'on' 'ScatteringAtlas' $true) -cne 'ScatteringAtlas' -or
            (Get-ObservedSkyBackend @($atlas) 'on' 'ScatteringAtlas' $false) -cne 'ScatteringAtlas') {
            throw 'Valid native backend evidence was rejected.'
        }
        $vrs = 'SKY ADAPTIVE VRS active tier 2, 16px tiles, R8_UINT 1920x1080 viewport, 65536 native DEFAULT bytes, split sky draw.'
        $mesh = 'SKY MESH CORRECTION active, using the shared R8_UINT rate image.'
        if ((Get-ObservedSkyBackend @($atlas, $prefilter, $vrs, $mesh) 'on' 'TiledScatteringAtlas' $true) -cne 'TiledScatteringAtlas' -or
            (Get-ObservedSkyBackend @() 'off' 'TiledScatteringAtlas' $true) -cne 'Disabled') {
            throw 'Valid split draw evidence was rejected.'
        }
        foreach ($invalid in @(@{Log=@($atlas, $prefilter, $vrs); Mode='on'; Backend='TiledScatteringAtlas'; Prefilter=$true},
                @{Log=@($atlas, $prefilter, $mesh); Mode='on'; Backend='TiledScatteringAtlas'; Prefilter=$true},
                @{Log=@($vrs, $mesh); Mode='off'; Backend='TiledScatteringAtlas'; Prefilter=$true},
                @{Log=@($mesh); Mode='on'; Backend='Direct8x4'; Prefilter=$false},
                @{Log=@($atlas); Mode='off'; Backend='ScatteringAtlas'; Prefilter=$false},
                @{Log=@(); Mode='on'; Backend='ScatteringAtlas'; Prefilter=$false},
                @{Log=@($atlas); Mode='on'; Backend='ScatteringAtlas'; Prefilter=$true},
                @{Log=@($atlas, $prefilter); Mode='on'; Backend='ScatteringAtlas'; Prefilter=$false},
                @{Log=@($atlas); Mode='on'; Backend='Direct8x4'; Prefilter=$false},
                @{Log=@('SKY SOLAR TABLE active prepared optical depth'); Mode='on'; Backend='Direct8x4'; Prefilter=$false})) {
            $rejected = $false
            try { $null = Get-ObservedSkyBackend $invalid.Log $invalid.Mode $invalid.Backend $invalid.Prefilter }
            catch { $rejected = $true }
            if (-not $rejected) { throw 'Ambiguous native sky backend evidence was accepted.' }
        }
        Write-Host 'PASS: Explicit sky backend provenance rejects inherited selectors, conflicting modes and inconsistent native activation.'
    }
    Assert-SkyBackendProvenance

    function Assert-FullSkySeries {
        $runner = Join-Path $PSScriptRoot 'Measure-ReferenceSky.ps1'
        foreach ($argument in 'Smoke', 'DiagnosticProbe', 'GpuSchedulingTrace') {
            $forbidden = @{ FullLookupSeries = $true; OutputDirectory = $forbiddenOutput }
            $forbidden[$argument] = $true
            $rejected = $false
            try { & $runner @forbidden }
            catch { $rejected = $_.Exception.Message -like 'FullLookupSeries cannot use*' }
            if (-not $rejected -or (Test-Path -LiteralPath $forbiddenOutput)) {
                throw "FullLookupSeries admitted incompatible mode $argument."
            }
        }

        $client = (Resolve-Path -LiteralPath $Assembly).Path
        $directory = Split-Path -Parent $client
        $identity = @{
            Assembly = $client
            ExpectedClientSha256 = (Get-FileHash -LiteralPath $client).Hash
            ExpectedRendererSha256 = (Get-FileHash -LiteralPath (Join-Path $directory 'Groundwork.Rendering.D3D12.dll')).Hash
            ExpectedRenderingSha256 = (Get-FileHash -LiteralPath (Join-Path $directory 'Groundwork.Rendering.dll')).Hash
            RendererSourceCommit = '0000000000000000000000000000000000000000'
        }
        $blockedOutput = Join-Path $OutputDirectory 'lookup-series-launch-fault'
        $failure = $null
        try { & $runner @identity -FullLookupSeries -OutputDirectory $blockedOutput }
        catch { $failure = $_.Exception.Message }
        $blockedResult = Get-Content -Raw -LiteralPath (Join-Path $blockedOutput 'result.json') | ConvertFrom-Json
        if ($failure -notmatch 'start process.*dotnet' -or $blockedResult.Status -cne 'BLOCKED' -or
            $blockedResult.Failure -cne $failure -or -not $blockedResult.FullLookupSeries -or
            -not $blockedResult.LookupPrototype -or $blockedResult.FullQualification -or
            $blockedResult.Composition -cne 'far-horizon' -or $blockedResult.Attempts -ne 1 -or
            $blockedResult.FailedAttempts -ne 1 -or $blockedResult.RequiredAttempts -ne 60) {
            throw 'FullLookupSeries lost its mode identity or native launch failure evidence.'
        }

        # Replace only native replay execution. Exercise the real mode flags,
        # schedule, pair/pooled denominators, summary gates and terminal result.
        $tokens = $null; $parseErrors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile($runner, [ref]$tokens, [ref]$parseErrors)
        if ($parseErrors.Count) { throw 'Reference runner has syntax errors.' }
        $replay = $ast.Find({ param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Replay'
        }, $false)
        $substitute = @'
function Invoke-Replay([int]$Pair, [int]$Replay, [string]$Mode) {
    $orbit = $Composition -ceq 'orbit-combined'
    $requiredFlag = if ($orbit) { 'on' } else { 'off' }
    $requiredOrbit = if ($orbit) { '1' } else { '' }
    $lookupFlag = if ($tiledSky) { '' } else { '1' }
    $diagnosticFlag = if ($tiledSky) { '0' } else { '1' }
    if ($env:GROUNDWORK_TWILIGHT_PREVIEW -cne '1' -or [string]$env:GROUNDWORK_SKY_LOOKUP_PROTOTYPE -cne $lookupFlag -or
        $env:WYU_SKY_DIAGNOSTICS -cne $diagnosticFlag -or
        $env:WYU_SKY_MEASURE_SMOKE -cne '0' -or $env:WYU_PACKAGE_E -cne 'on' -or
        $env:WYU_WP15 -cne $requiredFlag -or $env:WYU_WP16 -cne $requiredFlag -or
        [string]$env:WYU_ORBIT_DEMO_SMOKE -cne $requiredOrbit -or $env:WYU_SKY_MEASURE_COMPOSITION -cne $Composition) {
        throw 'Full series inherited or omitted required environment flags.'
    }
    $attempts.Add([pscustomobject]@{ Pair=$Pair; Replay=$Replay; Mode=$Mode; Passed=$true
        SchedulingTraceStatus='DISABLED'; SchedulingTraceDirectory=$null })
    foreach ($direction in 'to-orbit', 'to-ground') {
        $handoffs.Add([pscustomobject]@{ Direction=$direction; Pair=$Pair; Mode=$Mode; Milliseconds=10.0 })
    }
    if ($Replay -ne 1) { return }
    foreach ($phase in 'surface', 'ascent', 'orbit') {
        for ($i = 0; $i -lt $count; $i++) {
            $isOn = $Mode -ceq 'on'
            $frameMs = if ($orbit -and $isOn -and $phase -ceq 'ascent') { 1.1 } else { 1.0 }
            $samples.Add([pscustomobject]@{ Phase=$phase; Pair=$Pair; Mode=$Mode; FrameMs=$frameMs
                SkyMs=0.01; Draws=$(if (!$isOn) { 0 } elseif ($tiledSky) { 2 } else { 1 })
                AllocatedBytes=0; RecordAllocatedBytes=0; RecordMs=0.01; CombinedCpuMs=0.02 })
        }
    }
}
'@
        $fixture = (New-Item -ItemType Directory -Path (Join-Path $OutputDirectory 'lookup-series-fixture')).FullName
        $fixtureRunner = Join-Path $fixture 'Measure-ReferenceSky.ps1'
        $source = $ast.Extent.Text
        $source.Replace($replay.Extent.Text, $substitute) | Set-Content -LiteralPath $fixtureRunner
        New-Item -ItemType File -Path (Join-Path $fixture 'ReferenceGpuDiagnostics.ps1') | Out-Null
        $cases = @(
            @{ Mode='lookup'; Arguments=@{FullLookupSeries=$true}; Composition='far-horizon'; PassStatus='DIAGNOSTIC ONLY' },
            @{ Mode='lookup'; Arguments=@{FullLookupSeries=$true}; Composition='orbit-combined'; PassStatus='DIAGNOSTIC ONLY' },
            @{ Mode='tiled'; Arguments=@{SkyBackend='tiledscatteringatlas'}; Composition='far-horizon'; PassStatus='MEASURED GATES PASSED' },
            @{ Mode='tiled'; Arguments=@{SkyBackend='TiledScatteringAtlas'}; Composition='orbit-combined'; PassStatus='MEASURED GATES PASSED' })
        foreach ($case in $cases) {
            $configuration = $case.Composition
            $options = $case.Arguments
            $isLookup = $case.Mode -ceq 'lookup'
            $output = Join-Path $OutputDirectory "$($case.Mode)-series-$configuration"
            $failure = $null
            try { & $fixtureRunner @identity @options -Composition $configuration -OutputDirectory $output }
            catch { $failure = $_.Exception.Message }
            $resultPath = Join-Path $output 'result.json'
            if (-not (Test-Path -LiteralPath $resultPath)) { throw "Fixture produced no result: $failure" }
            $result = Get-Content -Raw -LiteralPath $resultPath | ConvertFrom-Json
            $manifest = Get-Content -Raw -LiteralPath (Join-Path $output 'manifest.json') | ConvertFrom-Json
            $summaryPath = Join-Path $output 'summary.csv'
            if (-not (Test-Path -LiteralPath $summaryPath)) { throw "Fixture produced no summary: $failure" }
            $rows = @(Import-Csv -LiteralPath $summaryPath)
            if ($result.FullLookupSeries -ne $isLookup -or $result.LookupPrototype -ne $isLookup -or $result.Diagnostics -ne $isLookup -or
                $result.FullQualification -or $result.DiagnosticProbe -or $result.GpuSchedulingTrace -or
                $result.Composition -cne $configuration -or $result.Attempts -ne 60 -or
                $result.RequiredAttempts -ne 60 -or $result.FailedAttempts -ne 0 -or $rows.Count -ne 30 -or
                $manifest.FullLookupSeries -ne $isLookup -or $manifest.Pairs -ne 5 -or $manifest.FlightsPerPairPerVariant -ne 6 -or
                $manifest.SamplesPerPhasePerRun -ne 400 -or $manifest.ProcessDeadlineMilliseconds -ne 660000) {
                throw 'Full sky series changed its counters, gates or mode identity.'
            }
            if (!$isLookup -and ($result.SkyBackendOn -cne 'TiledScatteringAtlas' -or !$manifest.AtlasPrefilter -or
                $manifest.ExpectedSkyDrawsOn -ne 2 -or $manifest.ExpectedSkyDrawsOff -ne 0 -or
                $result.ExpectedSkyDrawsOn -ne 2 -or $result.ExpectedSkyDrawsOff -ne 0 -or
                @($rows | Where-Object { $_.Kind -ceq 'GPU' -and $_.SkyDrawsMax -ne 2 }).Count)) {
                throw 'Full tiled sky series lost its exact two-draw contract.'
            }
            for ($i = 0; $i -lt 60; $i++) {
                $trace = $result.SchedulingTraces[$i]
                $pair = 1 + [int][Math]::Floor($i / 12)
                $offFirst = $pair % 2 -eq 1
                $expectedMode = if (($i % 12 -lt 6) -eq $offFirst) { 'off' } else { 'on' }
                if ($trace.Pair -ne $pair -or $trace.Replay -ne (1 + $i % 6) -or $trace.Mode -cne $expectedMode) {
                    throw "Unexpected replay order at index $i."
                }
            }
            $pooled = @($rows | Where-Object Pair -EQ '0')
            if ($pooled.Count -ne 5 -or @($pooled | Where-Object {
                $expected = if ($_.Kind -ceq 'GPU') { '2000' } else { '30' }
                $_.OffCount -ne $expected -or $_.OnCount -ne $expected
            }).Count) { throw 'Full series pooled denominators changed.' }
            if ($configuration -ceq 'far-horizon') {
                if ($failure -or $result.Status -cne $case.PassStatus -or $result.FailedSummaryRows -ne 0) {
                    throw 'Full-count passing fixture was promoted or rejected.'
                }
            } elseif ($failure -notlike 'A required measured sky/frame/handoff budget failed*' -or
                $result.Status -cne 'CHANGES REQUIRED' -or $result.FailedSummaryRows -ne 6) {
                throw 'An Ascent overrun did not fail every applicable pair and pooled row.'
            }
        }
        Write-Host 'PASS: Full lookup and tiled sky series preserve native launch faults, 60-flight schedules and all numerical gates.'
    }
    Assert-FullSkySeries

    function Assert-InstalledAppHostEvidence {
        $runner = Join-Path $PSScriptRoot 'Measure-ReferenceSky.ps1'
        $payload = (New-Item -ItemType Directory -Path (Join-Path $OutputDirectory 'installed-apphost-fixture')).FullName
        $clientSource = (Resolve-Path -LiteralPath $Assembly).Path
        $sourceDirectory = Split-Path -Parent $clientSource
        $client = Join-Path $payload 'WorldsYetUnseen.Client.dll'
        $appHost = Join-Path $payload 'WorldsYetUnseen.Client.exe'
        $coreClr = Join-Path $payload 'coreclr.dll'
        $rendering = Join-Path $payload 'Groundwork.Rendering.dll'
        $renderer = Join-Path $payload 'Groundwork.Rendering.D3D12.dll'
        Copy-Item -LiteralPath $clientSource -Destination $client
        Copy-Item -LiteralPath (Join-Path $sourceDirectory 'Groundwork.Rendering.dll') -Destination $rendering
        Copy-Item -LiteralPath (Join-Path $sourceDirectory 'Groundwork.Rendering.D3D12.dll') -Destination $renderer
        Set-Content -LiteralPath $appHost -Value 'Invalid PE for exercising native launch failure.'
        Set-Content -LiteralPath $coreClr -Value 'Fixture bytes for independent digest checks.'
        $identity = @{
            Assembly=$client; InstalledAppHost=$appHost
            ExpectedAppHostSha256=(Get-FileHash -LiteralPath $appHost).Hash
            ExpectedCoreClrSha256=(Get-FileHash -LiteralPath $coreClr).Hash
            ExpectedClientSha256=(Get-FileHash -LiteralPath $client).Hash
            ExpectedRendererSha256=(Get-FileHash -LiteralPath $renderer).Hash
            ExpectedRenderingSha256=(Get-FileHash -LiteralPath $rendering).Hash
            RendererSourceCommit='0000000000000000000000000000000000000000'
        }
        $wrong = $identity.Clone()
        $wrong.ExpectedCoreClrSha256 = '0' * 64
        $invalidOutput = Join-Path $OutputDirectory 'invalid-apphost'
        $failure = $null
        try { & $runner @wrong -SkyBackend TiledScatteringAtlas -OutputDirectory $invalidOutput }
        catch { $failure = $_.Exception.Message }
        if ($failure -notlike '*bundled CLR hash mismatch*' -or (Test-Path -LiteralPath $invalidOutput)) {
            throw "Mismatched runtime digest was not rejected before startup: $failure"
        }

        $nativeFailure = Join-Path $OutputDirectory 'installed-apphost-launch-fault'
        $failure = $null
        try { & $runner @identity -SkyBackend TiledScatteringAtlas -OutputDirectory $nativeFailure }
        catch { $failure = $_.Exception.Message }
        $manifest = Get-Content -Raw -LiteralPath (Join-Path $nativeFailure 'manifest.json') | ConvertFrom-Json
        $attempt = Import-Csv -LiteralPath (Join-Path $nativeFailure 'attempts.csv')
        $result = Get-Content -Raw -LiteralPath (Join-Path $nativeFailure 'result.json') | ConvertFrom-Json
        if ($failure -notmatch 'start process.*Client\.exe' -or $manifest.ExecutionHost -cne 'InstalledAppHost' -or
            $result.ExecutionHost -cne 'InstalledAppHost' -or $attempt.ExecutionHost -cne 'InstalledAppHost' -or
            $result.Status -cne 'BLOCKED' -or $attempt.Started -cne 'False' -or $attempt.ObservedCoreClr -or
            $manifest.ExpectedCoreClrSha256 -cne $identity.ExpectedCoreClrSha256) {
            throw "Installed native launch fault lost its executable, CLR correlation or failure: $failure"
        }

        $tokens = $null; $parseErrors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile($runner, [ref]$tokens, [ref]$parseErrors)
        if ($parseErrors.Count) { throw 'Reference runner has syntax errors.' }
        $observer = $ast.Find({ param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-OwnedReplayCoreClr'
        }, $false)
        . ([scriptblock]::Create($observer.Extent.Text))
        $module = [pscustomobject]@{ ModuleName='coreclr.dll'; FileName=$coreClr }
        $process = [pscustomobject]@{ HasExited=$false; Modules=@($module); ExitCode=123 }
        $process | Add-Member -MemberType ScriptMethod -Name Refresh -Value {}
        if ((Get-OwnedReplayCoreClr $process $coreClr) -cne $coreClr) { throw 'Bundled CLR evidence was rejected.' }
        $module.FileName = Join-Path $OutputDirectory 'coreclr.dll'
        $failure = $null
        try { $null = Get-OwnedReplayCoreClr $process $coreClr } catch { $failure = $_.Exception.Message }
        if ($failure -notlike '*did not load the verified bundled CLR*') { throw 'Unbundled runtime was accepted.' }
        $process.HasExited = $true
        $failure = $null
        try { $null = Get-OwnedReplayCoreClr $process $coreClr } catch { $failure = $_.Exception.Message }
        if ($failure -notlike '*code 123 before loading*') { throw 'AppHost runtime startup failure lost the exit code.' }
        Write-Host 'PASS: Installed AppHost identity, native launch failure, and bundled CLR module checks.'
    }
    Assert-InstalledAppHostEvidence

    # Exercise the implementation's actual cleanup block with a faulted copy
    # task and a throwing disposable. This test never starts a native process.
    function Assert-CleanupFailureEvidence {
        $parseErrors = $null
        $tokens = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $PSScriptRoot 'Measure-ReferenceSky.ps1'), [ref]$tokens, [ref]$parseErrors)
        if ($parseErrors.Count) { throw 'Reference runner has syntax errors.' }
        $replay = $ast.Find({ param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Replay'
        }, $false)
        $statement = $replay.Body.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.TryStatementAst] }
        $text = $statement.Finally.Extent.Text
        $cleanup = [scriptblock]::Create($text.Substring(1, $text.Length - 2))
        $root = (New-Item -ItemType Directory -Path (Join-Path $OutputDirectory 'copy-fault')).FullName
        $attempts = [Collections.Generic.List[object]]::new()
        $samples = [Collections.Generic.List[object]]::new()
        $handoffs = [Collections.Generic.List[object]]::new()
        $nativeExtras = [Collections.Generic.List[object]]::new()
        $cleanupFailures = [Collections.Generic.List[string]]::new()
        $process = [Diagnostics.Process]::new()
        $started = $false
        $ownedProcessId = 0
        $exitCode = $null
        $passed = $false
        $failure = 'original-sentinel'
        $Pair = 1; $Replay = 1; $Mode = 'off'; $seed = 42
        $expectedSkyBackend = 'Disabled'; $observedSkyBackend = 'Unverified'; $expectedOnDraws = 1
        $executionHost = 'DotnetSource'; $observedCoreClr = $null
        $Diagnostics = $false; $collector = $null; $collectorReport = $null
        $schedulingCollector = $null; $schedulingReport = $null
        $replayStartedUtc = 'cleanup-test'; $replayStartedQpc = 0L
        $log = Join-Path $root 'stdout.log'
        $errorLog = Join-Path $root 'stderr.log'
        $outputCopy = [Threading.Tasks.Task]::FromException([IO.IOException]::new('copy-sentinel'))
        $errorCopy = [Threading.Tasks.Task]::CompletedTask
        $stdout = [IO.MemoryStream]::new()
        $stderr = [pscustomobject]@{}
        $stderr | Add-Member -MemberType ScriptMethod -Name Dispose -Value { throw 'dispose-sentinel' }
        . $cleanup
        $evidence = @(Import-Csv -LiteralPath (Join-Path $root 'attempts.csv'))
        if ($evidence.Count -ne 1 -or $evidence[0].Failure -cne 'original-sentinel' -or
            $evidence[0].CleanupFailures -notmatch 'copy-sentinel' -or
            $evidence[0].CleanupFailures -notmatch 'dispose-sentinel' -or $evidence[0].Passed -cne 'False') {
            throw 'Faulted stream cleanup lost the primary failure or secondary evidence.'
        }
        Write-Host "PASS: Faulted copy and throwing disposal preserved primary and cleanup evidence. Evidence $root"
    }
    Assert-CleanupFailureEvidence

    function Assert-NativeExecutionJoining {
        $parseErrors = $null
        $tokens = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $PSScriptRoot 'Measure-ReferenceSky.ps1'), [ref]$tokens, [ref]$parseErrors)
        if ($parseErrors.Count) { throw 'Reference runner has syntax errors.' }
        foreach ($name in @('Number','Get-SkyNativeExecutions','Assert-SkyControlledWindow','Assert-SkyLookupOwners','Get-SkyLookupSplits','Get-SkyLookupExecutionTiming','Get-SkyGpuInteger','Get-SkyPrepassIndex','Assert-SkyPrepassTiming')) {
            $functionAst = $ast.Find({ param($node)
                $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
            }, $false)
            if ($null -eq $functionAst) { throw "Missing production parser helper $name." }
            . ([scriptblock]::Create($functionAst.Extent.Text))
        }
        $invariant = [Globalization.CultureInfo]::InvariantCulture
        $passRow = [pscustomobject]@{kind='gpu-prepass';owner=2;sequence=80;fence=100;phase='ascent'
            terrainMicroseconds=31.5;nearShadowMicroseconds=0;farShadowMicroseconds=2.25}
        $start = 1795298507913167488L
        $frameTickRow = [pscustomobject]@{kind='gpu-ticks';owner=2;sequence=80;fence=100;phase='ascent'
            frameStart=$start;frameEnd=$start+90000;skyStart=$start+40000;skyEnd=$start+60000;frequency=1000000000}
        $passTickRow = [pscustomobject]@{kind='gpu-pass-ticks';owner=2;sequence=80;fence=100;phase='ascent'
            terrainStart=$start+1;terrainEnd=$start+31501;drawStart=$start+45001;drawEnd=$start+55001}
        function Assert-PassFixture($Rows, [string]$Mode, [string]$ErrorPattern) {
            $failure = $null
            try {
                $passExecution = [pscustomobject]@{Owner=2;Sequence=80;Fence=100;FrameMs=0.09
                    SkyMs=$(if ($Mode -ceq 'on') { 0.02 } else { 0.0 })}
                $passIndex = Get-SkyPrepassIndex $Rows 'gpu-prepass'
                $frameTickIndex = Get-SkyPrepassIndex $Rows 'gpu-ticks'
                $passTickIndex = Get-SkyPrepassIndex $Rows 'gpu-pass-ticks'
                Assert-SkyPrepassTiming $passIndex $frameTickIndex $passTickIndex $passExecution 'ascent' $Mode
                if ($passIndex.Count -or $frameTickIndex.Count -or $passTickIndex.Count) {
                    throw 'GPU pass evidence was not joined exactly once.'
                }
            } catch { $failure = $_.Exception.Message }
            if (($ErrorPattern -and $failure -notlike $ErrorPattern) -or (-not $ErrorPattern -and $failure)) {
                throw "GPU pass fixture expected [$ErrorPattern] but received [$failure]."
            }
        }
        $passRows = @($passRow, $frameTickRow, $passTickRow)
        Assert-PassFixture $passRows 'on' ''
        Assert-PassFixture $passRows 'off' 'Unexpected GPU sky or draw timestamps*'
        $maximumTick = '{"tick":18446744073709551615}' | ConvertFrom-Json
        if ((Get-SkyGpuInteger $maximumTick 'tick') -ne [ulong]::MaxValue) { throw 'Unsigned GPU tick was rounded.' }
        foreach ($row in $passRows) {
            Assert-PassFixture @($passRows | Where-Object kind -CNE $row.kind) 'on' 'GPU pass timing evidence is missing*'
            Assert-PassFixture ($passRows + $row) 'on' "Duplicate $($row.kind) timing fence."
        }
        foreach ($case in @(
            @{Index=0; Field='terrainMicroseconds'; Value=-1; Error='Invalid timing value.'},
            @{Index=0; Field='terrainMicroseconds'; Value=$true; Error='Missing numeric GPU pass interval.'},
            @{Index=0; Field='terrainMicroseconds'; Value=31.0; Error='GPU terrain timestamps do not match*'},
            @{Index=1; Field='phase'; Value='surface'; Error='GPU pass timing evidence is missing*'},
            @{Index=1; Field='frequency'; Value=0; Error='Invalid measured GPU frame timestamp interval.'},
            @{Index=1; Field='frameEnd'; Value=$start-1; Error='Invalid measured GPU frame timestamp interval.'},
            @{Index=1; Field='frameEnd'; Value=$start+90001; Error='GPU timestamp intervals do not match*'},
            @{Index=1; Field='skyEnd'; Value=$start+60001; Error='GPU timestamp intervals do not match*'},
            @{Index=2; Field='sequence'; Value=81; Error='GPU pass timing evidence is missing*'},
            @{Index=2; Field='owner'; Value='2'; Error='Invalid GPU timing integer owner.'},
            @{Index=2; Field='terrainStart'; Value=-1; Error='Invalid GPU timing integer terrainStart.'},
            @{Index=2; Field='terrainStart'; Value=1.5; Error='Invalid GPU timing integer terrainStart.'},
            @{Index=2; Field='terrainStart'; Value=([bigint][ulong]::MaxValue + 1); Error='Invalid GPU timing integer terrainStart.'},
            @{Index=2; Field='terrainStart'; Value=0; Error='Invalid GPU terrain timestamp interval.'},
            @{Index=2; Field='terrainStart'; Value=$start+50000; Error='Invalid GPU terrain timestamp interval.'},
            @{Index=2; Field='terrainEnd'; Value=$start+40001; Error='Invalid GPU terrain timestamp interval.'},
            @{Index=2; Field='terrainEnd'; Value=$start+90001; Error='Invalid GPU terrain timestamp interval.'},
            @{Index=2; Field='drawStart'; Value=0; Error='GPU draw timestamps are missing*'},
            @{Index=2; Field='drawStart'; Value=$start+55002; Error='GPU draw timestamps are missing*'},
            @{Index=2; Field='drawEnd'; Value=$start+60001; Error='GPU draw timestamps are missing*'}
        )) {
            $copy = @($passRows | ForEach-Object { $_.PSObject.Copy() })
            $copy[$case.Index].PSObject.Properties[$case.Field].Value = $case.Value
            Assert-PassFixture $copy 'on' $case.Error
        }
        $copy = @($passRows | ForEach-Object { $_.PSObject.Copy() })
        $copy[2].PSObject.Properties.Remove('drawEnd')
        Assert-PassFixture $copy 'on' 'Missing GPU timing integer drawEnd.'
        $copy = @($passRows | ForEach-Object { $_.PSObject.Copy() })
        $copy[2].drawStart = $copy[2].drawEnd
        Assert-PassFixture $copy 'on' ''
        $copy[1].skyStart = $copy[1].skyEnd = 0
        Assert-PassFixture $copy 'off' 'Unexpected GPU sky or draw timestamps*'
        $copy[2].drawStart = $copy[2].drawEnd = 0
        Assert-PassFixture $copy 'off' ''
        Assert-PassFixture $copy 'on' 'GPU draw timestamps are missing*'
        $copy[0].terrainMicroseconds = 0
        $copy[2].terrainEnd = $copy[2].terrainStart
        Assert-PassFixture $copy 'off' ''
        $copy[2].terrainStart = $copy[2].terrainEnd = 0
        Assert-PassFixture $copy 'off' ''
        Write-Host 'PASS: GPU pass timestamps reconcile exact large counters, disabled or empty intervals and reported durations.'
        foreach ($requested in $false, $true) {
            $valid = @(1..3 | ForEach-Object { [pscustomobject]@{lookupPrototype=$requested} })
            Assert-SkyLookupOwners $valid $requested
            foreach ($invalid in @(
                @($valid[0],$valid[1]),
                @($valid[0],$valid[1],[pscustomobject]@{lookupPrototype=(-not $requested)}),
                @($valid[0],$valid[1],[pscustomobject]@{lookupPrototype='true'}),
                @($valid[0],$valid[1],[pscustomobject]@{})
            )) {
                $rejected=$false
                try { Assert-SkyLookupOwners $invalid $requested } catch { $rejected=$true }
                if (-not $rejected) { throw 'Missing, mistyped or inconsistent prototype owner identity was accepted.' }
            }
        }
        $reuseOwners = @(1..3 | ForEach-Object { [pscustomobject]@{lookupPrototype=$true; lookupResourceReuse=$true} })
        Assert-SkyLookupOwners $reuseOwners $true $true
        foreach ($reuseMode in $false, 'true', $null) {
            $invalidOwner = [pscustomobject]@{ lookupPrototype=$true }
            if ($null -ne $reuseMode) { $invalidOwner | Add-Member -NotePropertyName lookupResourceReuse -NotePropertyValue $reuseMode }
            $rejected = $false
            try { Assert-SkyLookupOwners @($reuseOwners[0], $reuseOwners[1], $invalidOwner) $true $true }
            catch { $rejected = $true }
            if (-not $rejected) { throw 'Missing, mistyped or disabled resource reuse was accepted.' }
        }
        Write-Host 'PASS: Direct and lookup mode identities fail closed for every native owner.'
        $split=[pscustomobject]@{kind='lookup-gpu-split';owner=1;sequence=80;fence=100;phase='surface';computeMicroseconds=5.25;drawBarrierMicroseconds=14.75}
        $recordSplit=[pscustomobject]@{kind='lookup-record-split';owner=1;sequence=80;fence=100;phase='surface';computeMicroseconds=4.125;drawBarrierMicroseconds=5.875}
        $indexed=Get-SkyLookupSplits @($split,$recordSplit)
        if ($indexed.Gpu.Count -ne 1 -or $indexed.Record.Count -ne 1 -or $indexed.Gpu['1-100'].ComputeMs -ne .00525 -or
            $indexed.Record['1-100'].DrawMs -ne .005875) { throw 'Lookup timing identity or units changed.' }
        $execution=[pscustomobject]@{Owner=1;Fence=100;Sequence=80;SkyMs=.020;RecordMs=.010}
        Get-SkyLookupExecutionTiming $indexed $execution 'surface' | Out-Null
        foreach($property in 'Sequence','SkyMs','RecordMs','Fence') {
            $copy=[pscustomobject]@{Owner=1;Fence=100;Sequence=80;SkyMs=.020;RecordMs=.010}; $copy.$property++
            $rejected=$false
            try { Get-SkyLookupExecutionTiming $indexed $copy 'surface' | Out-Null } catch { $rejected=$true }
            if(-not $rejected) {throw "Mismatched lookup $property accepted."}
        }
        $rejected=$false
        try { Get-SkyLookupExecutionTiming $indexed $execution 'orbit' | Out-Null } catch { $rejected=$true }
        if(-not $rejected) {throw 'Mismatched lookup phase accepted.'}
        $indexed.Record.Clear()
        $rejected=$false
        try { Get-SkyLookupExecutionTiming $indexed $execution 'surface' | Out-Null } catch { $rejected=$true }
        if(-not $rejected) {throw 'Missing lookup CPU evidence accepted.'}
        $rejected=$false
        try { Get-SkyLookupSplits @($split,$split) | Out-Null } catch { $rejected=$true }
        if (-not $rejected) { throw 'Duplicate lookup timing fence accepted.' }
        $split.computeMicroseconds=[double]::NaN
        $rejected=$false
        try { Get-SkyLookupSplits @($split) | Out-Null } catch { $rejected=$true }
        if (-not $rejected) { throw 'Invalid lookup timing accepted.' }
        Write-Host 'PASS: Lookup timing components preserve units/fences and reject duplicates/non-finite values.'
        $window = [pscustomobject]@{ owner=1; first=80; last=80; phase='surface' }
        $prepare = [pscustomobject]@{ kind='prepare'; owner=1; sequence=80; phase='surface'; preparationMicroseconds=7; allocatedBytes=50 }
        $gpuFirst = [pscustomobject]@{ kind='gpu'; owner=1; sequence=80; fence=100; phase='surface'; frameMicroseconds=100; skyMicroseconds=20; hasSky=$true }
        $gpuSecond = [pscustomobject]@{ kind='gpu'; owner=1; sequence=80; fence=101; phase='surface'; frameMicroseconds=110; skyMicroseconds=21; hasSky=$true }
        $recordFirst = [pscustomobject]@{ kind='record'; owner=1; sequence=80; fence=100; phase='surface'; recordMicroseconds=10; allocatedBytes=100; draws=1 }
        $recordSecond = [pscustomobject]@{ kind='record'; owner=1; sequence=80; fence=101; phase='surface'; recordMicroseconds=11; allocatedBytes=101; draws=1 }
        # Deliberately reverse input order: the first preparation belongs to the
        # earliest native fence, not to whichever callback line was parsed first.
        $rows = @($prepare,$gpuSecond,$recordFirst,$gpuFirst,$recordSecond)
        $joined = Get-SkyNativeExecutions $rows $window 'on'
        if ($joined.Rows.Count -ne 2 -or $joined.RawNativeExtras -ne 1 -or
            $joined.Rows[0].Fence -ne 100 -or $joined.Rows[1].Fence -ne 101 -or
            $joined.Rows[0].ExecutionIndex -ne 0 -or $joined.Rows[1].ExecutionIndex -ne 1 -or
            $joined.Rows[0].ReusedPreparation -or -not $joined.Rows[1].ReusedPreparation -or
            $joined.Rows[0].PreparationMs -ne .007 -or $joined.Rows[1].PreparationMs -ne 0 -or
            $joined.Rows[0].CombinedCpuMs -ne .017 -or $joined.Rows[1].CombinedCpuMs -ne .011 -or
            $joined.Rows[0].AllocatedBytes -ne 150 -or $joined.Rows[1].AllocatedBytes -ne 101) {
            throw 'Distinct native fences were lost, mismatched or charged preparation more than once.'
        }
        Assert-SkyControlledWindow $joined.RawNativeExtras $true
        $strictRejected = $false
        try { Assert-SkyControlledWindow $joined.RawNativeExtras $false } catch { $strictRejected = $_.Exception.Message -like '*exactly one native execution*' }
        if (-not $strictRejected -or $joined.Rows.Count -ne 2) { throw 'Strict extras rejection did not retain both observations.' }
        Assert-SkyControlledWindow 0 $false
        $single = Get-SkyNativeExecutions @($prepare,$gpuFirst,$recordFirst) $window 'on'
        if ($single.Rows.Count -ne 1 -or $single.RawNativeExtras -ne 0) { throw 'A single native execution changed the strict denominator.' }
        foreach ($case in @(
            @{ Rows=@($rows) + @($gpuFirst); Error='Duplicate native gpu fence*' },
            @{ Rows=@($rows) + @($recordFirst); Error='Duplicate native record fence*' },
            @{ Rows=@($prepare,$gpuFirst,$recordFirst,$gpuSecond); Error='Missing native GPU/record pair*' }
        )) {
            $rejected = $false
            try { Get-SkyNativeExecutions $case.Rows $window 'on' | Out-Null }
            catch { $rejected = $_.Exception.Message -like $case.Error }
            if (-not $rejected) { throw "Native parser did not reject $($case.Error)" }
        }
        $recordFirst.draws = 2
        $tiled = Get-SkyNativeExecutions @($prepare,$gpuFirst,$recordFirst) $window 'on' 2
        if ($tiled.Rows[0].Draws -ne 2) { throw 'Tiled native draw count was not retained.' }
        $recordFirst.draws = 1
        $rejected = $false
        try { $null = Get-SkyNativeExecutions @($prepare,$gpuFirst,$recordFirst) $window 'on' 2 }
        catch { $rejected = $_.Exception.Message -like '*Expected 2, observed 1*' }
        if (!$rejected) { throw 'Tiled fallback to one draw was accepted.' }
        $gpuFirst.hasSky = $false
        $rejected = $false
        try { $null = Get-SkyNativeExecutions @($prepare,$gpuFirst,$recordFirst) $window 'off' 2 }
        catch { $rejected = $_.Exception.Message -like '*Expected 0, observed 1*' }
        if (!$rejected) { throw 'Disabled sky incorrectly accepted a native draw.' }
        $recordFirst.draws = 0
        $off = Get-SkyNativeExecutions @($prepare,$gpuFirst,$recordFirst) $window 'off' 2
        if ($off.Rows[0].Draws -ne 0) { throw 'Disabled sky native draw count was not retained.' }
        Write-Host 'PASS: Native fences, preparation, strict denominators, and exact Direct, tiled and disabled draw counts verified.'
    }
    Assert-NativeExecutionJoining
    function Assert-DeadlinePolicy {
        $tokens = $null; $parseErrors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $PSScriptRoot 'Measure-ReferenceSky.ps1'), [ref]$tokens, [ref]$parseErrors)
        $assignment = $ast.Find({ param($node)
            $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -ceq '$deadline'
        }, $true)
        $policy = [scriptblock]::Create($assignment.Right.Extent.Text)
        foreach ($Smoke in $false, $true) {
            foreach ($Adapter in 'hardware', 'warp') {
                foreach ($Composition in 'far-horizon', 'orbit-combined') {
                    $actual = & $policy
                    $expected = if (-not $Smoke) { 660000 }
                        elseif ($Adapter -eq 'warp' -and $Composition -eq 'orbit-combined') { 390000 } else { 210000 }
                    if ($actual -ne $expected) { throw 'Measurement deadline changed outside the software combined smoke scope.' }
                }
            }
        }
        Write-Host 'PASS: Software combined smoke has a bounded allowance; hardware and full-run deadlines remain unchanged.'
    }
    Assert-DeadlinePolicy
} finally {
    $env:PATH = $priorPath
}
