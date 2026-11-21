param(
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('wyu-gpu-diagnostic-tests-' + [guid]::NewGuid().ToString('N')))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReferenceGpuDiagnostics.ps1')
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Tests require a fresh, exclusively owned artifact directory.' }
$root = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$stubSource = Join-Path $root 'stub'
$stubBin = Join-Path $root 'bin'
New-Item -ItemType Directory -Path $stubSource | Out-Null
@'
<Project>
  <PropertyGroup><ImportDirectoryBuildProps>false</ImportDirectoryBuildProps><ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets></PropertyGroup>
  <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
    <UseAppHost>true</UseAppHost><AssemblyName>DiagnosticCollectorStub</AssemblyName>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
</Project>
'@ | Set-Content -LiteralPath (Join-Path $stubSource 'Stub.csproj')
@'
using System;
using System.IO;
using System.Threading;
class Program {
    static int Main(string[] args) {
        if (args.Length != 3 || !args[0].StartsWith("--query-gpu=timestamp,index,uuid,name,pstate,") ||
            args[1] != "--format=csv,nounits" || args[2] != "--loop-ms=1000") return 99;
        string mode = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
        if (mode == "fail") { Console.Error.WriteLine("unsupported-query-sentinel"); return 7; }
        if (mode == "hang") { while (true) { Console.WriteLine("owned-live-sample"); Thread.Sleep(100); } }
        for (int i=0; i<20000; i++) Console.WriteLine("sample-" + i + "," + new string('x', 80));
        Console.Error.WriteLine("complete-stderr-sentinel");
        return 0;
    }
}
'@ | Set-Content -LiteralPath (Join-Path $stubSource 'Program.cs')
& dotnet build (Join-Path $stubSource 'Stub.csproj') -c Release -o $stubBin --ignore-failed-sources *> (Join-Path $root 'stub-build.log')
if ($LASTEXITCODE -ne 0) { throw "Stub build failed. See $root/stub-build.log" }
foreach ($name in 'ok','fail','hang') {
    Copy-Item -LiteralPath (Join-Path $stubBin 'DiagnosticCollectorStub.exe') -Destination (Join-Path $stubBin "$name.exe")
}
$reports = [Collections.Generic.List[object]]::new()
try {
    $context = Start-ReferenceGpuDiagnostics -OutputDirectory (Join-Path $root 'complete') -ApplicationPath (Join-Path $stubBin 'ok.exe') -MaximumDurationMilliseconds 15000
    if (-not $context.Started -or -not $context.Process.WaitForExit(15000)) { throw "Full-output stub did not finish: $($context.SetupError)" }
    $report = Stop-ReferenceGpuDiagnostics $context
    $reports.Add($report)
    $lines = @(Get-Content -LiteralPath $report.Stdout)
    if ($report.Availability -ne 'Collected' -or $report.ExitCode -ne 0 -or $report.ProcessId -le 0 -or
        $lines.Count -ne 20000 -or $lines[-1] -notlike 'sample-19999,*' -or
        (Get-Content -Raw -LiteralPath $report.Stderr).Trim() -ne 'complete-stderr-sentinel' -or
        $report.StartedQpc -lt $report.AttemptedQpc -or $report.StoppedQpc -lt $report.StartedQpc -or $report.CleanupErrors.Count) {
        throw 'Full stdout/stderr streaming or process provenance failed.'
    }
    if (-not [object]::ReferenceEquals($report, (Stop-ReferenceGpuDiagnostics $context))) { throw 'Stop is not idempotent.' }

    $context = Start-ReferenceGpuDiagnostics -OutputDirectory (Join-Path $root 'unsupported') -ApplicationPath (Join-Path $stubBin 'fail.exe') -MaximumDurationMilliseconds 15000
    if (-not $context.Started -or -not $context.Process.WaitForExit(15000)) { throw 'Nonzero-exit stub did not finish.' }
    $report = Stop-ReferenceGpuDiagnostics $context
    $reports.Add($report)
    if ($report.Availability -ne 'Error' -or $report.ExitCode -ne 7 -or
        (Get-Content -Raw -LiteralPath $report.Stderr).Trim() -ne 'unsupported-query-sentinel') { throw 'Nonzero query failure was hidden.' }

    $context = Start-ReferenceGpuDiagnostics -OutputDirectory (Join-Path $root 'deadline') -ApplicationPath (Join-Path $stubBin 'hang.exe') -MaximumDurationMilliseconds 250
    if (-not $context.Started -or -not $context.Process.WaitForExit(5000)) { throw 'Owned collector deadline did not stop its process.' }
    $report = Stop-ReferenceGpuDiagnostics $context
    $reports.Add($report)
    if (-not $report.TimedOut -or -not $report.ProcessExited -or $report.Availability -ne 'TimedOut') { throw 'Owned timeout was not retained.' }

    $context = Start-ReferenceGpuDiagnostics -OutputDirectory (Join-Path $root 'owner-stop') -ApplicationPath (Join-Path $stubBin 'hang.exe') -MaximumDurationMilliseconds 15000
    $report = Stop-ReferenceGpuDiagnostics $context
    $reports.Add($report)
    if (-not $report.Started -or -not $report.StoppedByOwner -or -not $report.ProcessExited -or $report.CleanupErrors.Count) { throw 'Explicit owner cleanup failed.' }

    $primaryFailure = $null
    try {
        $context = Start-ReferenceGpuDiagnostics -OutputDirectory (Join-Path $root 'start-failure') -ApplicationPath (Join-Path $root 'missing-owned-stub.exe')
        throw 'measurement-failure-sentinel'
    } catch { $primaryFailure = $_.Exception.Message }
    finally { $report = Stop-ReferenceGpuDiagnostics $context }
    $reports.Add($report)
    if ($primaryFailure -ne 'measurement-failure-sentinel' -or $report.Started -or $null -ne $report.ProcessId -or
        $report.Availability -ne 'Unavailable' -or -not $report.SetupError) { throw 'Start failure fabricated a PID, disappeared or masked the measurement failure.' }

    function Get-Command { param($Name, $CommandType, $ErrorAction) $null }
    $context = Start-ReferenceGpuDiagnostics -OutputDirectory (Join-Path $root 'absent-tool')
    $report = Stop-ReferenceGpuDiagnostics $context
    $reports.Add($report)
    if ($report.Started -or $null -ne $report.ExitCode -or $report.SetupError -notlike '*not found in PATH*') { throw 'Absent telemetry tool was not explicit.' }
    Remove-Item Function:\Get-Command
    Write-Host "PASS: Full streams, fixed arguments, nonzero exit, owned deadline/cleanup, start failure, absent tool and idempotent Stop. Evidence $root"
}
finally {
    if (Get-Variable -Name context -ErrorAction SilentlyContinue) { Stop-ReferenceGpuDiagnostics $context | Out-Null }
    $reports | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $root 'test-reports.json')
}
