# Read-only, opt-in telemetry. Dot-source this file; it never starts a process on load.
function Initialize-ReferenceGpuDiagnosticsDeadline {
    if ('Wyu.ReferenceGpuDiagnostics.OwnedDeadlineV1' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
namespace Wyu.ReferenceGpuDiagnostics {
    public sealed class OwnedDeadlineV1 : IDisposable {
        private readonly object gate = new object();
        private readonly Process process;
        private readonly SafeFileHandle job;
        private Timer timer;
        private bool disposed;
        private bool cancelled;
        public bool TimedOut { get; private set; }
        public string DeadlineError { get; private set; }
        public OwnedDeadlineV1(Process process, int milliseconds) {
            this.process = process;
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Create collector ownership job");
            try {
                var limits = new ExtendedLimits();
                limits.Basic.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE, including caller termination.
                if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Set collector ownership job");
                if (!process.HasExited && !AssignProcessToJobObject(job, process.Handle))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Assign owned collector process");
                timer = new Timer(Expire, null, milliseconds, Timeout.Infinite);
            } catch { job.Dispose(); throw; }
        }
        private void Expire(object state) {
            lock (gate) {
                if (disposed || cancelled) return;
                try {
                    if (!process.HasExited) { TimedOut = true; process.Kill(); }
                } catch (Exception error) { DeadlineError = error.Message; }
            }
        }
        public void CancelDeadline() {
            lock (gate) { cancelled = true; timer?.Change(Timeout.Infinite, Timeout.Infinite); }
        }
        public void Dispose() {
            lock (gate) {
                if (disposed) return;
                disposed = true;
                timer?.Dispose();
                job.Dispose();
            }
        }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits {
            public long ProcessTime, JobTime;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters {
            public ulong Reads, Writes, Other, ReadBytes, WriteBytes, OtherBytes;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int info, ref ExtendedLimits limits, uint length);
        [DllImport("kernel32.dll", SetLastError=true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    }
}
'@ -ErrorAction Stop
}

function Start-ReferenceGpuDiagnostics {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$OutputDirectory,
        [string]$ApplicationPath,
        [ValidateRange(100, 3600000)][int]$MaximumDurationMilliseconds = 660000,
        [ValidateRange(100, 10000)][int]$DrainTimeoutMilliseconds = 5000
    )
    $context = [pscustomobject]@{
        Availability='Unavailable'; SetupError=$null; CleanupErrors=[Collections.Generic.List[string]]::new()
        OutputDirectory=$null; CreatedDirectory=$false; ApplicationPath=$null; Arguments=@()
        Process=$null; Deadline=$null; Stdout=$null; Stderr=$null; StdoutCopy=$null; StderrCopy=$null
        ProcessId=$null; Started=$false; Stopped=$false; StopReport=$null
        AttemptedUtc=[DateTime]::UtcNow.ToString('O'); AttemptedQpc=[Diagnostics.Stopwatch]::GetTimestamp()
        StartedUtc=$null; StartedQpc=$null; MaximumDurationMilliseconds=$MaximumDurationMilliseconds
        DrainTimeoutMilliseconds=$DrainTimeoutMilliseconds; StdoutPath=$null; StderrPath=$null
    }
    try {
        $context.OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
        if (Test-Path -LiteralPath $context.OutputDirectory) { throw 'Use a fresh GPU diagnostic output directory.' }
        New-Item -ItemType Directory -Path $context.OutputDirectory -ErrorAction Stop | Out-Null
        $context.CreatedDirectory = $true
        $context.StdoutPath = Join-Path $context.OutputDirectory 'stdout.csv'
        $context.StderrPath = Join-Path $context.OutputDirectory 'stderr.log'
        if (-not $ApplicationPath) {
            $command = Get-Command nvidia-smi -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($null -eq $command) { throw 'nvidia-smi was not found in PATH; no GPU telemetry was collected.' }
            $ApplicationPath = $command.Source
        }
        $context.ApplicationPath = [IO.Path]::GetFullPath($ApplicationPath)
        Initialize-ReferenceGpuDiagnosticsDeadline
        $context.Arguments = @(
            '--query-gpu=timestamp,index,uuid,name,pstate,temperature.gpu,utilization.gpu,clocks.current.graphics,clocks.current.memory,power.draw,power.limit,clocks_event_reasons.active',
            '--format=csv,nounits', '--loop-ms=1000'
        )
        $start = [Diagnostics.ProcessStartInfo]::new($context.ApplicationPath)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in $context.Arguments) { $start.ArgumentList.Add($argument) }
        $context.Process = [Diagnostics.Process]::new()
        $context.Process.StartInfo = $start
        $context.Stdout = [IO.FileStream]::new($context.StdoutPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        $context.Stderr = [IO.FileStream]::new($context.StderrPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        if (-not $context.Process.Start()) { throw 'Could not start the owned nvidia-smi diagnostic process.' }
        $context.Started = $true
        $context.ProcessId = $context.Process.Id
        $context.StartedUtc = [DateTime]::UtcNow.ToString('O')
        $context.StartedQpc = [Diagnostics.Stopwatch]::GetTimestamp()
        $context.StdoutCopy = $context.Process.StandardOutput.BaseStream.CopyToAsync($context.Stdout)
        $context.StderrCopy = $context.Process.StandardError.BaseStream.CopyToAsync($context.Stderr)
        $context.Deadline = [Wyu.ReferenceGpuDiagnostics.OwnedDeadlineV1]::new($context.Process, $MaximumDurationMilliseconds)
        $context.Availability = 'Started'
    } catch {
        $context.SetupError = $_.Exception.Message
        # A setup failure must never orphan a child or mask the caller's failure.
        Stop-ReferenceGpuDiagnostics -Context $context | Out-Null
    }
    $context
}

function Stop-ReferenceGpuDiagnostics {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Context)
    if ($Context.Stopped) { return $Context.StopReport }
    $stoppedByOwner = $false
    $timedOut = $false
    $exitCode = $null
    $exited = $null
    try {
        if ($null -ne $Context.Deadline) {
            $Context.Deadline.CancelDeadline()
            $timedOut = $Context.Deadline.TimedOut
            if ($Context.Deadline.DeadlineError) { $Context.CleanupErrors.Add("deadline: $($Context.Deadline.DeadlineError)") }
        }
        if ($Context.Started) {
            if ($Context.Process.Id -ne $Context.ProcessId) { throw 'Collector context no longer matches its owned process identity.' }
            if (-not $Context.Process.HasExited) {
                $stoppedByOwner = -not $timedOut
                $Context.Process.Kill()
            }
            $exited = $Context.Process.WaitForExit($Context.DrainTimeoutMilliseconds)
            if (-not $exited) { throw 'Owned diagnostic process did not exit within its cleanup bound.' }
            $exitCode = $Context.Process.ExitCode
        }
    } catch { $Context.CleanupErrors.Add("process: $($_.Exception.Message)") }
    finally {
        try { if ($null -ne $Context.Deadline) { $Context.Deadline.Dispose() } }
        catch { $Context.CleanupErrors.Add("ownership job: $($_.Exception.Message)") }
        foreach ($copy in @(@('stdout', $Context.StdoutCopy), @('stderr', $Context.StderrCopy))) {
            try {
                if ($null -ne $copy[1] -and -not $copy[1].Wait($Context.DrainTimeoutMilliseconds)) {
                    throw 'Diagnostic stream did not drain within its bound.'
                }
            } catch { $Context.CleanupErrors.Add("$($copy[0]) drain: $($_.Exception.Message)") }
        }
        foreach ($resource in @(@('stdout', $Context.Stdout), @('stderr', $Context.Stderr), @('process', $Context.Process))) {
            try { if ($null -ne $resource[1]) { $resource[1].Dispose() } }
            catch { $Context.CleanupErrors.Add("$($resource[0]) dispose: $($_.Exception.Message)") }
        }
    }
    $stdoutBytes = $null
    try {
        if ($Context.StdoutPath -and (Test-Path -LiteralPath $Context.StdoutPath)) {
            $stdoutBytes = (Get-Item -LiteralPath $Context.StdoutPath -ErrorAction Stop).Length
        }
    } catch { $Context.CleanupErrors.Add("stdout size: $($_.Exception.Message)") }
    $availability = if ($Context.SetupError) { 'Unavailable' } elseif ($timedOut) { 'TimedOut' }
        elseif ($Context.CleanupErrors.Count -ne 0 -or (-not $stoppedByOwner -and $null -ne $exitCode -and $exitCode -ne 0)) { 'Error' }
        elseif ($stdoutBytes -eq 0) { 'NoData' }
        else { 'Collected' }
    $report = [pscustomobject]@{
        Schema=1; Availability=$availability; SetupError=$Context.SetupError
        WindowsTimeZoneId=[TimeZoneInfo]::Local.Id; LocalUtcOffset=[DateTimeOffset]::Now.Offset.ToString()
        AvailabilityError=$(if ($availability -eq 'Error') { 'Diagnostic query or cleanup failed; retain stderr and cleanup errors. Unsupported fields are not replaced with zeros.' } else { $null })
        ApplicationPath=$Context.ApplicationPath; Arguments=$Context.Arguments; ProcessId=$Context.ProcessId
        Started=$Context.Started; AttemptedUtc=$Context.AttemptedUtc; AttemptedQpc=$Context.AttemptedQpc
        StartedUtc=$Context.StartedUtc; StartedQpc=$Context.StartedQpc
        StoppedUtc=[DateTime]::UtcNow.ToString('O'); StoppedQpc=[Diagnostics.Stopwatch]::GetTimestamp()
        QpcFrequency=[Diagnostics.Stopwatch]::Frequency; MaximumDurationMilliseconds=$Context.MaximumDurationMilliseconds
        NvidiaTimestampTimeZone='Driver default/local timestamp; enclosing collector boundaries are UTC and monotonic QPC.'
        TimedOut=$timedOut; StoppedByOwner=$stoppedByOwner; ProcessExited=$exited; ExitCode=$exitCode
        Stdout=$Context.StdoutPath; Stderr=$Context.StderrPath; StdoutBytes=$stdoutBytes; CleanupErrors=@($Context.CleanupErrors.ToArray())
        Scope='Read-only nvidia-smi query at 1000 ms. Missing/unsupported data are not zero metrics. No clock/power/device settings changed.'
    }
    $Context.Stopped = $true
    $Context.Availability = $availability
    $Context.StopReport = $report
    if ($Context.CreatedDirectory) {
        try { $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $Context.OutputDirectory 'collector.json') -Encoding utf8 -ErrorAction Stop }
        catch { $Context.CleanupErrors.Add("report: $($_.Exception.Message)"); $report.CleanupErrors=$Context.CleanupErrors.ToArray(); $report.Availability='Error'; $Context.Availability='Error' }
    }
    $report
}
