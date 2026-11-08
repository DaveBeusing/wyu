param([Parameter(Mandatory)][string]$Client)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Read-AlphaOverlay.ps1')
$clientPath = (Resolve-Path -LiteralPath $Client).Path
$id = [guid]::NewGuid().ToString('N')
$work = Join-Path ([IO.Path]::GetTempPath()) "wyu-mission-$id"
$slot = Join-Path $work 'surface'
$settings = Join-Path $work 'settings'
New-Item -ItemType Directory -Path $slot | Out-Null
Set-Content -LiteralPath (Join-Path $slot 'wyu-prototype-save.fixture') -Value $id
$process = $null
Write-Host "WP10 mission evidence: $work"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WyuMissionTestNative
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr key, IntPtr flags);
}
'@

function Start-Client([string]$Name, [string[]]$Arguments) {
    Start-Process -FilePath $clientPath -ArgumentList $Arguments -PassThru `
        -RedirectStandardOutput (Join-Path $work "$Name.out") -RedirectStandardError (Join-Path $work "$Name.err")
}

function Wait-Title([string]$Pattern, [int]$Seconds = 30) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $process.Refresh()
        if ($process.HasExited) { throw "Client exited with code $($process.ExitCode) waiting for '$Pattern'." }
        if ((Read-AlphaOverlay $process) -like $Pattern -or $process.MainWindowTitle -like $Pattern) { return }
        Start-Sleep -Milliseconds 10
    } while ($clock.Elapsed.TotalSeconds -lt $Seconds)
    throw "Expected '$Pattern', saw '$($process.MainWindowTitle)'."
}

function Send-KeyEvent([int]$Key, [bool]$Down) {
    $process.Refresh()
    $flags = if ($Key -ge 37 -and $Key -le 40) { 0x01000001 } else { 1 }
    $message = if ($Down) { 0x100 } else { 0x101 }
    if (!$Down) { $flags = $flags -bor 0xC0000000 }
    if (![WyuMissionTestNative]::PostMessage($process.MainWindowHandle, $message, $Key, $flags)) {
        throw "Could not send key $Key."
    }
}

function Send-Key([int]$Key) {
    Send-KeyEvent $Key $true
    Send-KeyEvent $Key $false
    Start-Sleep -Milliseconds 100
}

function Wait-Exit {
    if (!$process.WaitForExit(20000) -or $process.ExitCode -ne 0) {
        throw "Client did not exit cleanly. Inspect logs in $work."
    }
}

function Exit-Flight {
    Send-Key 27
    Wait-Title 'WYU paused*'
    Send-Key 40
    Send-Key 13
    Wait-Exit
}

try {
    $prototypeArgs = @('--prototype', "`"$slot`"", "`"$settings`"")
    $process = Start-Client 'fixture' @('--prototype-save-mission-fixture', "`"$slot`"", $id)
    Wait-Exit

    $process = Start-Client 'partial' $prototypeArgs
    Wait-Title '*Checkpoint 1*'
    Send-Key 13
    Wait-Title '*Next: build at cyan pad*Stone 1*'
    Send-Key 27
    Wait-Title 'WYU paused*'
    Send-Key 40
    Send-Key 13
    Wait-Title '*Saved generation 2*'
    Send-Key 40
    Send-Key 13
    Wait-Exit

    $process = Start-Client 'mission' $prototypeArgs
    Wait-Title '*Checkpoint 2*'
    Send-Key 13
    Wait-Title '*Next: build at cyan pad*Stone 1*'
    Send-KeyEvent 39 $true
    try { Wait-Title '*E: build signal head*' 8 }
    finally { Send-KeyEvent 39 $false }
    Send-Key 69
    Wait-Title '*E: send rescue signal*'
    Send-Key 69
    Wait-Title '*Rescue beacon online*Enter for guided flight*'
    Send-Key 13
    Wait-Title 'WYU WP10 | Ready*Space to launch*'
    Send-Key 27
    Wait-Title 'WYU paused*'
    Send-Key 27
    Send-Key 32
    Wait-Title 'WYU WP10 | Orbit*' 45
    Wait-Title '*Mission complete*Landing saved*' 130
    Exit-Flight

    $flightDir = @(Get-ChildItem -LiteralPath $slot -Directory -Filter 'flight-*')
    if ($flightDir.Count -ne 1) { throw 'Expected exactly one flight domain linked to the rescue save.' }
    $before = @(Get-ChildItem -LiteralPath $flightDir[0].FullName -File | Sort-Object Name | Get-FileHash |
        ForEach-Object Hash)
    $process = Start-Client 'resume' $prototypeArgs
    Wait-Title '*Rescue signal sent; Continue to flight*'
    Send-Key 13
    Wait-Title '*Mission complete*Landing saved*'
    Send-Key 32
    Wait-Title '*Mission complete*Landing saved*'
    Exit-Flight
    $after = @(Get-ChildItem -LiteralPath $flightDir[0].FullName -File | Sort-Object Name | Get-FileHash |
        ForEach-Object Hash)
    $missionLog = @(Get-Content -LiteralPath (Join-Path $work 'mission.out'))
    $resumeLog = @(Get-Content -LiteralPath (Join-Path $work 'resume.out'))
    $landing = @($missionLog | Where-Object { $_ -clike 'WP09 saved generation=2 *' })
    $restored = @($resumeLog | Where-Object { $_ -clike 'WP10 restored landing generation=2 *' })
    if ($before.Count -lt 2 -or ($before -join ',') -cne ($after -join ',') -or
        $landing.Count -ne 1 -or $restored.Count -ne 1 -or
        $landing[0].Replace('WP09 saved', 'WP10 restored landing') -cne $restored[0] -or
        $missionLog -cnotcontains 'WP09 trace v1 ticks=4332 sha256=7157DA3B7F3DB7C5B303BD4E28AEBE3A86A7D15156BAF431EE1278F3810CD230' -or
        !($missionLog -clike 'WP09 orbit angle=*')) {
        throw "The flight trace or landing resume did not match. Inspect logs in $work."
    }
    Write-Host 'PASS: Partial construction resumed, rescue committed, full orbit and landing completed, and a fresh process restored the landing without writing another checkpoint.'
}
finally {
    if ($null -ne $process -and !$process.HasExited) {
        $process.Kill()
        $process.WaitForExit()
    }
}
