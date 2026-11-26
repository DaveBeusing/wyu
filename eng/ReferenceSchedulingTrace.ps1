# Opt-in diagnostic ETW capture. Dot-sourcing defines functions only.
# DCS runtime (-rf) survives termination of the calling process. No -ets is used.
# https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/logman-create-trace
function Invoke-ReferenceSchedulingCommand {
    param($Context, [string[]]$Arguments, [int]$DeadlineMilliseconds = 15000)
    $record = [ordered]@{ Arguments=$Arguments; StartedUtc=[DateTimeOffset]::UtcNow.ToString('o'); ExitCode=$null; TimedOut=$false; Stdout=''; Stderr=''; Error=$null }
    $process = [Diagnostics.Process]::new()
    try {
        $start = [Diagnostics.ProcessStartInfo]::new($Context.LogmanPath)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
        $process.StartInfo = $start
        if (!$process.Start()) { throw 'logman did not start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($DeadlineMilliseconds)) {
            $record.TimedOut = $true
            $process.Kill()
            if (!$process.WaitForExit(5000)) { throw 'Owned logman command did not exit after termination.' }
        }
        if (![Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 2000)) {
            throw 'Owned command output streams did not close within the drain deadline.'
        }
        $record.Stdout = $stdout.GetAwaiter().GetResult()
        $record.Stderr = $stderr.GetAwaiter().GetResult()
        $record.ExitCode = $process.ExitCode
    } catch { $record.Error = $_.Exception.Message }
    finally { $process.Dispose() }
    $Context.Commands.Add([pscustomobject]$record)
    return [pscustomobject]$record
}

function Save-ReferenceSchedulingManifest {
    param($Context)
    try {
        $Context | Select-Object * -ExcludeProperty TestFixture | ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath (Join-Path $Context.OutputDirectory 'scheduling-trace.json') -Encoding utf8
    } catch { $Context.Errors.Add('Manifest: ' + $_.Exception.Message) }
}

function Stop-ReferenceSchedulingTrace {
    param([Parameter(Mandatory)]$Context)
    if ($Context.Stopped) { return $Context }
    $Context.Stopped = $true
    if ($Context.CreateAttempted) {
        $ownedName = $null
        if (Get-Variable ReferenceSchedulingOwners -Scope Script -ErrorAction SilentlyContinue) {
            foreach ($entry in $script:ReferenceSchedulingOwners.GetEnumerator()) {
                if ([object]::ReferenceEquals($entry.Value, $Context)) { $ownedName = $entry.Key; break }
            }
        }
        if (!$ownedName) {
            $Context.Errors.Add('Collector ownership unavailable. Cleanup refused.')
            Save-ReferenceSchedulingManifest $Context
            return $Context
        }
        # Only the unique name allocated by this invocation is eligible for cleanup.
        foreach ($verb in 'query','stop','delete') {
            $result = Invoke-ReferenceSchedulingCommand $Context @($verb, $ownedName)
            if ($verb -eq 'delete' -and ($result.ExitCode -ne 0 -or $result.Error -or $result.TimedOut)) {
                $Context.Errors.Add('Collector deletion failed. A stopped configuration may remain: ' + $Context.CollectorName)
            }
            if ($verb -eq 'stop' -and ($result.ExitCode -ne 0 -or $result.Error -or $result.TimedOut)) {
                $Context.Errors.Add('Explicit collector stop was not confirmed. The service runtime limit still applies.')
            }
        }
        $script:ReferenceSchedulingOwners.Remove($ownedName)
    }
    $Context.StoppedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $Context.StoppedQpc = [Diagnostics.Stopwatch]::GetTimestamp()
    $Context.TraceFiles = @(Get-ChildItem -LiteralPath $Context.OutputDirectory -Filter '*.etl' -File -ErrorAction SilentlyContinue | Select-Object FullName,Length)
    if ($Context.Started) {
        $hasTrace = @($Context.TraceFiles | Where-Object { $_.Length -gt 0 }).Count -gt 0
        $Context.Status = if ($hasTrace -and $Context.Errors.Count -eq 0) { 'CAPTURED' } else { 'PARTIAL' }
    }
    Save-ReferenceSchedulingManifest $Context
    return $Context
}

function Start-ReferenceSchedulingTraceCore {
    # Internal fixture seam. Production callers use Start-ReferenceSchedulingTrace.
    param([string]$OutputDirectory, [int]$RuntimeSeconds, [string]$LogmanPath, [bool]$IsElevated)
    if ($RuntimeSeconds -lt 1 -or $RuntimeSeconds -gt 720) { throw 'Trace runtime must be 1 through 720 seconds.' }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    if ($OutputDirectory.StartsWith('\\') -or
        ($IsWindows -and [IO.DriveInfo]::new([IO.Path]::GetPathRoot($OutputDirectory)).DriveType -eq [IO.DriveType]::Network)) {
        throw 'Scheduling trace artifacts require a local filesystem directory.'
    }
    if (Test-Path -LiteralPath $OutputDirectory) { throw 'Scheduling trace requires a fresh artifact directory.' }
    $directory = (New-Item -ItemType Directory -Path $OutputDirectory -ErrorAction Stop).FullName
    $context = [pscustomobject]@{
        Status='UNAVAILABLE'; DiagnosticOnly=$true; FullQualification=$false
        CollectorName=('WyuGpuSchedule_' + [guid]::NewGuid().ToString('N'))
        OutputDirectory=$directory; LogmanPath=$LogmanPath; LogmanVersion=$null
        RuntimeSeconds=$RuntimeSeconds; CreateAttempted=$false; Started=$false; Stopped=$false
        AttemptedUtc=[DateTimeOffset]::UtcNow.ToString('o'); AttemptedQpc=[Diagnostics.Stopwatch]::GetTimestamp()
        QpcFrequency=[Diagnostics.Stopwatch]::Frequency; StartedUtc=$null; StartedQpc=$null; StoppedUtc=$null; StoppedQpc=$null
        Provider='{802EC45A-1E99-4B83-9920-87C98277BA9D}'; ProviderKeywords='0x0C008647'
        EventLossChecked=$false; CircularCoverageMayWrap=$true
        Coverage='Diagnostic capture only. File presence does not prove complete coverage or zero event loss.'
        HardKillCleanup='The service runtime limit stops capture. The collector configuration may remain after caller termination.'
        TraceFiles=@(); Commands=[Collections.Generic.List[object]]::new(); Errors=[Collections.Generic.List[string]]::new()
    }
    try {
        if (!$IsElevated) { throw 'Administrator token unavailable. Scheduling capture was not started.' }
        if (!(Test-Path -LiteralPath $LogmanPath -PathType Leaf)) { throw 'System logman executable unavailable.' }
        $context.LogmanVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($LogmanPath).FileVersion
        $provider = Invoke-ReferenceSchedulingCommand $context @('query','providers',$context.Provider)
        if ($provider.ExitCode -ne 0 -or $provider.Error -or $provider.TimedOut) { throw 'DxgKrnl provider query unavailable. See command output.' }
        $existing = Invoke-ReferenceSchedulingCommand $context @('query',$context.CollectorName)
        if ($existing.ExitCode -eq 0 -or $existing.Error -or $existing.TimedOut) { throw 'Unique collector name availability could not be established.' }
        $duration = [TimeSpan]::FromSeconds($RuntimeSeconds).ToString('hh\:mm\:ss')
        $arguments = @('create','trace',$context.CollectorName,'-ln',$context.CollectorName,'-o',(Join-Path $directory 'dxgkrnl.etl'),'-f','bincirc','-max','256','-bs','64','-nb','16','32','-ct','perf','-rf',$duration,'-p',$context.Provider,$context.ProviderKeywords,'5')
        if (!(Get-Variable ReferenceSchedulingOwners -Scope Script -ErrorAction SilentlyContinue)) {
            $script:ReferenceSchedulingOwners = @{}
        }
        $script:ReferenceSchedulingOwners.Add($context.CollectorName, $context)
        $context.CreateAttempted = $true
        $create = Invoke-ReferenceSchedulingCommand $context $arguments
        if ($create.ExitCode -ne 0 -or $create.Error -or $create.TimedOut) { throw 'Collector creation failed. See command output.' }
        $start = Invoke-ReferenceSchedulingCommand $context @('start',$context.CollectorName)
        $context.StartedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        $context.StartedQpc = [Diagnostics.Stopwatch]::GetTimestamp()
        if ($start.ExitCode -ne 0 -or $start.Error -or $start.TimedOut) { throw 'Collector start failed. See command output.' }
        $context.Started = $true
        $context.Status = 'RUNNING'
        Save-ReferenceSchedulingManifest $context
    } catch {
        $context.Errors.Add($_.Exception.Message)
        $null = Stop-ReferenceSchedulingTrace $context
    }
    return $context
}

function Start-ReferenceSchedulingTrace {
    param([Parameter(Mandatory)][string]$OutputDirectory, [ValidateRange(1,720)][int]$RuntimeSeconds = 720)
    $elevated = $false
    if ($IsWindows) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        try { $elevated = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
        finally { $identity.Dispose() }
    }
    $logman = if ($IsWindows) { Join-Path ([Environment]::GetFolderPath('System')) 'logman.exe' } else { '' }
    Start-ReferenceSchedulingTraceCore -OutputDirectory $OutputDirectory -RuntimeSeconds $RuntimeSeconds -LogmanPath $logman -IsElevated $elevated
}
