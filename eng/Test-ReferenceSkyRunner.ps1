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
    Write-Host "PASS: Native launch failure preserved PID=0, attempts.csv and BLOCKED result. Evidence $OutputDirectory"

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
        foreach ($name in @('Number','Get-SkyNativeExecutions','Assert-SkyControlledWindow')) {
            $functionAst = $ast.Find({ param($node)
                $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
            }, $false)
            if ($null -eq $functionAst) { throw "Missing production parser helper $name." }
            . ([scriptblock]::Create($functionAst.Extent.Text))
        }
        $invariant = [Globalization.CultureInfo]::InvariantCulture
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
        Write-Host 'PASS: Distinct fences on one logical sequence retained and matched; preparation charged once; strict extras, duplicate fences and missing records rejected.'
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
