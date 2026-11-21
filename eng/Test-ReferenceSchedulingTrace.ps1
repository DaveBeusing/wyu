param([string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('wyu-scheduling-tests-' + [guid]::NewGuid().ToString('N'))))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Tests require a fresh directory.' }
$root = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'ReferenceSchedulingTrace.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -or ($ast.EndBlock.Statements | Where-Object { $_ -isnot [Management.Automation.Language.FunctionDefinitionAst] })) { throw 'Dot-source helper contains executable load-time statements or syntax errors.' }
. (Join-Path $PSScriptRoot 'ReferenceSchedulingTrace.ps1')
$productionCommand = ${function:Invoke-ReferenceSchedulingCommand}
$script:calls = [Collections.Generic.List[object]]::new()
$script:mode = 'ok'
$script:created = $false
function Invoke-ReferenceSchedulingCommand {
    param($Context, [string[]]$Arguments, [int]$DeadlineMilliseconds = 15000)
    $script:calls.Add($Arguments)
    $code = 0
    $timedOut = $false
    if ($Arguments[0] -eq 'query' -and $Arguments.Count -eq 2 -and !$script:created) { $code = 1 }
    if ($Arguments[0] -eq 'create') { $script:created = $true }
    if ($script:mode -eq $Arguments[0]) { $code = 7 }
    if ($script:mode -eq 'timeout' -and $Arguments[0] -eq 'start') { $timedOut = $true; $code = -1 }
    if ($Arguments[0] -eq 'start' -and $script:mode -eq 'ok') {
        [IO.File]::WriteAllBytes((Join-Path $Context.OutputDirectory 'dxgkrnl_000001.etl'), [byte[]](1,2,3))
    }
    $record = [pscustomobject]@{Arguments=$Arguments;ExitCode=$code;TimedOut=$timedOut;Error=$null;Stdout='fixture stdout';Stderr='fixture stderr'}
    $Context.Commands.Add($record)
    return $record
}
$fixtureExecutable = (Get-Process -Id $PID).Path
$reports = [Collections.Generic.List[object]]::new()
foreach ($mode in 'ok','create','start','timeout','partial','stop','delete') {
    $script:mode = $mode
    $script:created = $false
    $script:calls.Clear()
    $context = Start-ReferenceSchedulingTraceCore -OutputDirectory (Join-Path $root $mode) -RuntimeSeconds 2 -LogmanPath $fixtureExecutable -IsElevated $true
    $originalName = $context.CollectorName
    $context.CollectorName = 'ForeignCollector'
    $report = Stop-ReferenceSchedulingTrace $context
    if ($script:calls | Where-Object { $_ -contains '-ets' -or $_ -contains 'ForeignCollector' }) { throw 'Foreign collector or direct ETW session command was invoked.' }
    foreach ($verb in 'stop','delete') {
        if (!($script:calls | Where-Object { $_[0] -eq $verb -and $_[1] -eq $originalName })) { throw "Owned cleanup missing for $mode/$verb" }
    }
    if ($mode -eq 'ok' -and $report.Status -ne 'CAPTURED') { throw 'Valid trace was not reported.' }
    if ($mode -in 'create','start','timeout' -and $report.Status -ne 'UNAVAILABLE') { throw 'Failed startup was reported as available.' }
    if ($mode -in 'partial','stop','delete' -and $report.Status -ne 'PARTIAL') { throw 'Partial evidence was not disclosed.' }
    if ($report.FullQualification -or $report.EventLossChecked -or !$report.CircularCoverageMayWrap) { throw 'Unsupported qualification claim.' }
    if (![object]::ReferenceEquals($report, (Stop-ReferenceSchedulingTrace $context))) { throw 'Stop not idempotent.' }
    $create = @($script:calls | Where-Object { $_[0] -eq 'create' })[0]
    foreach ($required in '-rf','00:00:02','bincirc','256','0x0C008647','perf') { if ($required -notin $create) { throw "Missing bound $required" } }
    $reports.Add($report)
}
$script:calls.Clear()
$unavailable = Start-ReferenceSchedulingTraceCore -OutputDirectory (Join-Path $root 'non-admin') -RuntimeSeconds 2 -LogmanPath $fixtureExecutable -IsElevated $false
if ($unavailable.Status -ne 'UNAVAILABLE' -or $script:calls.Count -ne 0) { throw 'Non-admin capture invoked a command.' }
$script:mode = 'query'
$script:created = $false
$script:calls.Clear()
$unsupported = Start-ReferenceSchedulingTraceCore -OutputDirectory (Join-Path $root 'unsupported') -RuntimeSeconds 2 -LogmanPath $fixtureExecutable -IsElevated $true
if ($unsupported.Status -ne 'UNAVAILABLE' -or ($script:calls | Where-Object { $_[0] -in 'create','stop','delete' })) { throw 'Provider query failure was not isolated.' }
$script:calls.Clear()
$missing = Start-ReferenceSchedulingTraceCore -OutputDirectory (Join-Path $root 'missing-tool') -RuntimeSeconds 2 -LogmanPath (Join-Path $root 'missing.exe') -IsElevated $true
if ($missing.Status -ne 'UNAVAILABLE' -or $script:calls.Count) { throw 'Missing tool invoked a command.' }
# Restore production command to test a real, bounded process without ETW activity.
Set-Item Function:Invoke-ReferenceSchedulingCommand $productionCommand
$commandContext = [pscustomobject]@{ LogmanPath=$fixtureExecutable; Commands=[Collections.Generic.List[object]]::new() }
$result = Invoke-ReferenceSchedulingCommand $commandContext @('-NoProfile','-Command','[Threading.Thread]::Sleep(30000)') -DeadlineMilliseconds 100
if (!$result.TimedOut) { throw 'Owned command deadline was not enforced.' }
$names = @($reports | ForEach-Object { ($_.Commands | Where-Object { $_.Arguments[0] -eq 'create' })[0].Arguments[2] })
if (@($names | Select-Object -Unique).Count -ne $names.Count -or ($names | Where-Object { $_ -notmatch '^WyuGpuSchedule_[a-f0-9]{32}$' })) { throw 'Names not unique ASCII GUIDs.' }
$reports | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $root 'test-results.json') -Encoding utf8
Write-Output "Scheduling trace fixture checks passed. Evidence: $root"
