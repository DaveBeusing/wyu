param(
    [string]$Assembly = (Join-Path $PSScriptRoot '../artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll'),
    [ValidateSet('far-horizon', 'orbit-combined')][string]$Composition = 'far-horizon',
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
        & (Join-Path $PSScriptRoot 'Measure-ReferenceSky.ps1') -Smoke -Composition $Composition -Assembly $Assembly -OutputDirectory $OutputDirectory
    } catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or $failure -notmatch 'start process.*dotnet') {
        throw "Expected the original native launch failure, received: $failure"
    }
    $attempts = @(Import-Csv -LiteralPath (Join-Path $OutputDirectory 'attempts.csv'))
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $OutputDirectory 'manifest.json') | ConvertFrom-Json
    $result = Get-Content -Raw -LiteralPath (Join-Path $OutputDirectory 'result.json') | ConvertFrom-Json
    if ($manifest.Composition -cne $Composition.ToLowerInvariant() -or $attempts.Count -ne 1 -or
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
} finally {
    $env:PATH = $priorPath
}
