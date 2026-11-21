param(
    [string]$Assembly = (Join-Path $PSScriptRoot '../artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll'),
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('wyu-scheduling-runner-tests-' + [guid]::NewGuid().ToString('N')))
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Tests require a fresh directory.' }
$root = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$fixture = (New-Item -ItemType Directory -Path (Join-Path $root 'fixture')).FullName
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Measure-ReferenceSky.ps1') -Destination $fixture
@'
function Start-ReferenceGpuDiagnostics { param($OutputDirectory,$MaximumDurationMilliseconds) return $null }
function Stop-ReferenceGpuDiagnostics { param($Context) return $null }
'@ | Set-Content -LiteralPath (Join-Path $fixture 'ReferenceGpuDiagnostics.ps1')
@'
function Start-ReferenceSchedulingTrace {
    param($OutputDirectory,$RuntimeSeconds)
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
    [pscustomobject]@{RuntimeSeconds=$RuntimeSeconds;StartedBeforeReplay=$true} | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory 'start.json')
    if ($env:WYU_SCHEDULING_FIXTURE -eq 'start-throw') { throw 'trace-start-sentinel' }
    return [pscustomobject]@{OutputDirectory=$OutputDirectory;Status='RUNNING';FullQualification=$false}
}
function Stop-ReferenceSchedulingTrace {
    param($Context)
    'called' | Set-Content (Join-Path $Context.OutputDirectory 'stop.txt')
    if ($env:WYU_SCHEDULING_FIXTURE -eq 'stop-throw') { throw 'trace-stop-sentinel' }
    $Context.Status = 'PARTIAL'
    return $Context
}
'@ | Set-Content -LiteralPath (Join-Path $fixture 'ReferenceSchedulingTrace.ps1')
$priorPath = $env:PATH
$priorFixture = $env:WYU_SCHEDULING_FIXTURE
$client = (Resolve-Path -LiteralPath $Assembly).Path
$renderer = Join-Path (Split-Path -Parent $client) 'Groundwork.Rendering.D3D12.dll'
$rendering = Join-Path (Split-Path -Parent $client) 'Groundwork.Rendering.dll'
try {
    function git { $global:LASTEXITCODE=0; if ($args -contains 'rev-parse') { '0000000000000000000000000000000000000000' } }
    function dotnet { 'fixture-metadata-only' }
    $env:PATH = ''
    $runner = Join-Path $fixture 'Measure-ReferenceSky.ps1'
    foreach ($mode in 'partial','start-throw','stop-throw') {
        $env:WYU_SCHEDULING_FIXTURE = $mode
        $output = Join-Path $root $mode
        $failure = $null
        try {
            & $runner -Assembly $client -OutputDirectory $output -DiagnosticProbe -GpuSchedulingTrace -ExpectedClientSha256 (Get-FileHash $client).Hash -ExpectedRendererSha256 (Get-FileHash $renderer).Hash -ExpectedRenderingSha256 (Get-FileHash $rendering).Hash -RendererSourceCommit ('0' * 40)
        } catch { $failure = $_.Exception.Message }
        if ($failure -notmatch 'start process.*dotnet') { throw "Trace masked primary replay failure: $failure" }
        $attempt = @(Import-Csv (Join-Path $output 'attempts.csv'))[0]
        $manifest = Get-Content -Raw (Join-Path $output 'manifest.json') | ConvertFrom-Json
        $result = Get-Content -Raw (Join-Path $output 'result.json') | ConvertFrom-Json
        $diagnostic = Get-Content -Raw (Join-Path $output '1-1-off-diagnostics.json') | ConvertFrom-Json
        $traceDirectory = Join-Path $output '1-1-off-scheduling'
        $start = Get-Content -Raw (Join-Path $traceDirectory 'start.json') | ConvertFrom-Json
        $expectedStatus = if ($mode -eq 'start-throw') { 'UNAVAILABLE' } else { 'PARTIAL' }
        if (!$manifest.Diagnostics -or !$manifest.DiagnosticProbe -or !$manifest.GpuSchedulingTrace -or !$result.GpuSchedulingTrace -or $result.FullQualification -or
            $attempt.SchedulingTraceStatus -ne $expectedStatus -or $attempt.SchedulingTraceDirectory -ne $traceDirectory -or $attempt.Failure -ne $failure -or
            $diagnostic.SchedulingTrace.Status -ne $expectedStatus -or $start.RuntimeSeconds -ne 690 -or !$start.StartedBeforeReplay) {
            throw 'Scheduling forwarding, bounded runtime, or failure evidence changed.'
        }
        if ($mode -ne 'start-throw' -and !(Test-Path (Join-Path $traceDirectory 'stop.txt'))) { throw 'Replay failure skipped trace cleanup.' }
    }
    foreach ($smoke in $false,$true) {
        $output = Join-Path $root "forbidden-$smoke"
        $rejected = $false
        try { & $runner -Assembly $client -OutputDirectory $output -GpuSchedulingTrace -Smoke:$smoke }
        catch { $rejected = $_.Exception.Message -like '*requires DiagnosticProbe*' }
        if (!$rejected -or (Test-Path $output)) { throw 'Scheduling trace admitted outside DiagnosticProbe.' }
    }
    Write-Output "Scheduling runner fixtures passed. Evidence: $root"
} finally { $env:PATH=$priorPath; $env:WYU_SCHEDULING_FIXTURE=$priorFixture }
