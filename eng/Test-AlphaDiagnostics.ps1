param(
    [Parameter(Mandatory)][string]$Client,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$ExpectedCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$clientPath = (Resolve-Path -LiteralPath $Client).Path
$root = Join-Path ([IO.Path]::GetTempPath()) "wyu-diagnostics-$([guid]::NewGuid().ToString('N'))"
$process = $null
Write-Host "WP10 diagnostics evidence: $root"

function Wait-For([scriptblock]$Condition, [string]$Description) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $process.Refresh()
        if ($process.HasExited) { throw "Client exited ($($process.ExitCode)) before $Description." }
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 30
    } while ($clock.Elapsed.TotalSeconds -lt 15)
    throw "Timed out waiting for $Description. Current title: '$($process.MainWindowTitle)'."
}

function Read-Log([string]$Path) {
    if (Test-Path -LiteralPath $Path) { Get-Content -LiteralPath $Path -Raw }
}

try {
    foreach ($scenario in 'startup','graphics','close') {
        $fixture = Join-Path $root $scenario
        New-Item -ItemType Directory -Path $fixture | Out-Null
        New-Item -ItemType File -Path (Join-Path $fixture 'wyu-diagnostics.fixture') | Out-Null
        $log = Join-Path $fixture 'client.log'
        if ($scenario -eq 'startup') {
            [IO.File]::WriteAllBytes($log, [byte[]]::new(65520))
            $previousHash = (Get-FileHash -LiteralPath $log).Hash
        }

        $process = Start-Process -FilePath $clientPath -ArgumentList @('--diagnostics-smoke', $scenario, "`"$fixture`"") -PassThru
        if ($scenario -eq 'close') {
            Wait-For { $process.MainWindowTitle -eq 'WYU | Checking save slot' -and
                (Read-Log $log) -match 'phase=SaveLoad event=load-ready' } 'tracked save loading'
            $phase = 'SaveLoad'
            $exitCode = 0
        } else {
            $title = if ($scenario -eq 'graphics') { '*Graphics error' } else { '*Client error' }
            Wait-For { $process.MainWindowTitle -like $title } "visible $scenario dialog"
            $phase = if ($scenario -eq 'graphics') { 'GraphicsStartup' } else { 'Startup' }
            $exitCode = 1
        }
        if (!$process.CloseMainWindow() -or !$process.WaitForExit(10000) -or $process.ExitCode -ne $exitCode) {
            throw "$scenario did not settle with exit code $exitCode."
        }

        $contents = Read-Log $log
        if ($contents -notmatch "build=\S+\+$ExpectedCommit pid=$($process.Id) phase=$phase ") {
            throw "$scenario log does not identify the build, process and phase."
        }
        if ($scenario -eq 'close') {
            if ($contents -match 'event=fault' -or
                $contents -notmatch 'phase=SaveLoad event=window-close' -or
                $contents -notmatch 'phase=SaveLoad event=exit' -or
                (Test-Path -LiteralPath (Join-Path $fixture 'save'))) {
                throw 'Closing during load did not cancel cleanly or touched the save directory.'
            }
        } elseif ($contents -notmatch "phase=$phase event=fault .*controlled $scenario failure") {
            throw "$scenario fault was not logged."
        }

        $files = @(Get-ChildItem -LiteralPath $fixture -Filter '*.log')
        if ($files.Count -gt 2 -or @($files | Where-Object Length -gt 65536).Count) { throw 'Log storage exceeded its limit.' }
        if ($scenario -eq 'startup' -and ($files.Count -ne 2 -or
            (Get-FileHash -LiteralPath (Join-Path $fixture 'client.previous.log')).Hash -ne $previousHash)) {
            throw 'The full log was not rotated intact.'
        }
        Write-Host "PASS: $scenario dialog or clean close, exact build/phase, and bounded logs."
        $process.Dispose()
        $process = $null
    }
}
finally {
    if ($null -ne $process) {
        if (!$process.HasExited) {
            $process.Kill()
            $process.WaitForExit()
        }
        $process.Dispose()
    }
}
