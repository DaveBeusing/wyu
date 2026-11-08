param(
    [Parameter(Mandatory)][string]$Client,
    [switch]$ExpectLargerWindow
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$clientPath = (Resolve-Path -LiteralPath $Client).Path
$fixture = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) "wyu-alpha-$([guid]::NewGuid().ToString('N'))")
$slot = Join-Path $fixture.FullName 'slot'
$settings = Join-Path $fixture.FullName 'settings'
$settingsFile = Join-Path $settings 'settings.json'
$process = $null

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WyuAlphaTestNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr key, IntPtr flags);
}
'@

function Wait-Title([string]$Pattern) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $process.Refresh()
        if ($process.HasExited) { throw "Client exited with code $($process.ExitCode) waiting for '$Pattern'." }
        if ($process.MainWindowTitle -like $Pattern) { return }
        Start-Sleep -Milliseconds 50
    } while ($clock.Elapsed.TotalSeconds -lt 30)
    throw "Expected '$Pattern', saw '$($process.MainWindowTitle)'."
}

function Send-Key([int]$Key) {
    $handle = $process.MainWindowHandle
    $flags = if ($Key -ge 37 -and $Key -le 40) { 0x01000001 } else { 1 }
    if (![WyuAlphaTestNative]::PostMessage($handle, 0x100, $Key, $flags) -or
        ![WyuAlphaTestNative]::PostMessage($handle, 0x101, $Key, ($flags -bor 0xC0000000))) {
        throw "Could not send key $Key."
    }
    Start-Sleep -Milliseconds 100
}

function Get-ClientWidth {
    $rect = [WyuAlphaTestNative+Rect]::new()
    if (![WyuAlphaTestNative]::GetClientRect($process.MainWindowHandle, [ref]$rect)) { throw 'Cannot read client size.' }
    $rect.Right - $rect.Left
}

function Stop-Client {
    Send-Key 27
    if (!$process.WaitForExit(20000) -or $process.ExitCode -ne 0) { throw 'Client did not exit cleanly.' }
}

try {
    $arguments = @('--prototype', "`"$slot`"", "`"$settings`"")
    $process = Start-Process -FilePath $clientPath -ArgumentList $arguments -PassThru
    Wait-Title '*Start test mission*Empty slot*'
    if ((Get-ClientWidth) -ne 960) { throw "Unexpected default client width $(Get-ClientWidth)." }
    Send-Key 40
    Send-Key 40
    Send-Key 13
    Wait-Title '*> Window 960x540*Mouse 100%*Invert Off*'
    Send-Key 39
    Send-Key 40
    Send-Key 39
    Send-Key 40
    Send-Key 39
    Send-Key 40
    Send-Key 13
    Wait-Title '*Settings saved*'
    $published = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
    $width = Get-ClientWidth
    if ($published.version -ne 1 -or $published.sensitivity -ne 125 -or !$published.invertVertical -or
        ($published.sizeIndex -eq 1 -and $width -ne 1280) -or ($published.sizeIndex -eq 0 -and $width -ne 960) -or
        ($ExpectLargerWindow -and $width -ne 1280)) { throw 'Settings were not applied as expected.' }
    $digest = (Get-FileHash -LiteralPath $settingsFile).Hash

    Send-Key 13
    Wait-Title '*Mouse 125%*Invert On*'
    Send-Key 38
    Send-Key 13
    Wait-Title '*Mouse 100%*Invert Off*'
    Send-Key 38
    Send-Key 13
    Wait-Title '*Settings saved*'
    if ((Get-FileHash -LiteralPath $settingsFile).Hash -ne $digest -or (Get-ClientWidth) -ne $width) {
        throw 'Cancel changed the applied settings.'
    }
    Send-Key 40
    Send-Key 13
    Wait-Title '*Controls 1/4*'
    Send-Key 13
    Wait-Title '*Controls 2/4*'
    Send-Key 27
    Wait-Title 'WYU | * | Arrows / Enter'
    Send-Key 13
    Wait-Title '*Controls 1/4*'
    Send-Key 27
    Wait-Title 'WYU | * | Arrows / Enter'
    Stop-Client
    Write-Host "PASS: Keyboard navigation, guidance skip/replay, Apply, staged reset and Cancel (client width $width)."

    Set-Content -LiteralPath (Join-Path $settings 'settings-interrupted.tmp') -Value '{"version":'
    $process = Start-Process -FilePath $clientPath -ArgumentList $arguments -PassThru
    Wait-Title '*Start test mission*Empty slot*'
    if ((Get-ClientWidth) -ne $width) { throw 'Window size did not survive restart.' }
    Send-Key 40
    Send-Key 40
    Send-Key 13
    Wait-Title '*Mouse 125%*Invert On*'
    Send-Key 27
    Wait-Title 'WYU | * | Arrows / Enter'
    Stop-Client
    Write-Host 'PASS: Relaunch kept the committed settings despite an incomplete staged write.'

    Set-Content -LiteralPath $settingsFile -Value '{"version":'
    $badDigest = (Get-FileHash -LiteralPath $settingsFile).Hash
    $process = Start-Process -FilePath $clientPath -ArgumentList $arguments -PassThru
    Wait-Title '*Invalid settings; using defaults*'
    if ((Get-ClientWidth) -ne 960 -or (Get-FileHash -LiteralPath $settingsFile).Hash -ne $badDigest) {
        throw 'Recovery did not preserve the damaged file or restore safe defaults.'
    }
    Send-Key 40
    Send-Key 40
    Send-Key 13
    Wait-Title '*Mouse 100%*Invert Off*'
    Send-Key 40
    Send-Key 40
    Send-Key 40
    Send-Key 13
    Wait-Title '*Settings saved*'
    $backup = @(Get-ChildItem -LiteralPath $settings -Filter 'settings-rejected-*.json')
    if ($backup.Count -ne 1 -or (Get-FileHash -LiteralPath $backup[0].FullName).Hash -ne $badDigest) {
        throw 'The rejected file was not retained on Apply.'
    }
    Write-Host "PASS: Damaged settings recovered and original bytes retained. Evidence: $($fixture.FullName)"

    Send-Key 38
    Send-Key 38
    Send-Key 13
    Wait-Title '*Controls 1/4*'
    Send-Key 27
    Wait-Title '*Rescue beacon*'
    Send-Key 27
    Wait-Title 'WYU paused*'
    if (!$process.CloseMainWindow() -or !$process.WaitForExit(20000) -or $process.ExitCode -ne 0) {
        throw 'Client did not exit cleanly from the paused mission.'
    }
    Write-Host 'PASS: Skipping first-run guidance opened the rescue mission and the pause menu worked.'
}
finally {
    if ($null -ne $process -and !$process.HasExited) {
        $process.Kill()
        $process.WaitForExit()
    }
}
