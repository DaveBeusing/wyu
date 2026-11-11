param(
    [Parameter(Mandatory)][string]$Client,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$ExpectedCommit
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'Read-AlphaOverlay.ps1')
$clientPath = (Resolve-Path -LiteralPath $Client).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ('wyu-installed-planet-' + [guid]::NewGuid().ToString('N'))
$process = $null
New-Item -ItemType Directory -Path $root | Out-Null
Write-Host "Installed planet evidence $root"

if (!('WyuPlanetInput' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class WyuPlanetInput
{
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    private delegate bool Visitor(IntPtr window, IntPtr data);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr window, int attribute,
        out Rect bounds, int size);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, Visitor visitor, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr size, StringBuilder text,
        uint flags, uint milliseconds, out IntPtr result);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr window, uint message, IntPtr key, IntPtr flags);
    public static bool Pitch(IntPtr window, int pixels)
    {
        var previous = SetThreadDpiAwarenessContext((IntPtr)(-4));
        try
        {
            if (!GetClientRect(window, out Rect bounds)) return false;
            var center = new Point { X = bounds.Right / 2, Y = bounds.Bottom / 2 };
            return ClientToScreen(window, ref center) && SetCursorPos(center.X, center.Y + pixels);
        }
        finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
    }
    public static string Dialog(IntPtr owner)
    {
        var result = new StringBuilder();
        EnumChildWindows(owner, (window, _) =>
        {
            var value = new StringBuilder(1024);
            SendMessageTimeout(window, 0x000d, (IntPtr)value.Capacity, value, 2, 100, out _);
            result.AppendLine(value.ToString());
            return true;
        }, IntPtr.Zero);
        return result.ToString();
    }
}
'@
}

function New-Fixture([string]$Name) {
    $path = Join-Path $root $Name
    New-Item -ItemType Directory -Path $path | Out-Null
    New-Item -ItemType File -Path (Join-Path $path 'wyu-planet-playtest.fixture') | Out-Null
    return $path
}

function Start-Planet([string]$Fixture, [string]$Failure = '') {
    $arguments = @('--planet-playtest', ('"' + $Fixture + '"'))
    if ($Failure) { $arguments += $Failure }
    Start-Process -FilePath $clientPath -ArgumentList $arguments -PassThru
}

function Wait-For([scriptblock]$Condition, [string]$Expected, [int]$Seconds = 35) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $process.Refresh()
        if ($process.HasExited) { throw "Client exited ($($process.ExitCode)) waiting for $Expected." }
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 45
    } while ($clock.Elapsed.TotalSeconds -lt $Seconds)
    throw "Waiting for $Expected timed out. Title='$($process.MainWindowTitle)'; sidebar='$(Read-AlphaOverlay $process)'; metrics='$(Read-AlphaOverlay $process 'performance')'."
}

function Wait-Panel([string]$Pattern, [int]$Seconds = 35) {
    Wait-For { (Read-AlphaOverlay $process) -like $Pattern } $Pattern $Seconds
}

function Set-Key([int]$Key, [bool]$Down) {
    if ($Down) { Focus-AlphaWindow $process }
    $message = if ($Down) { 0x100 } else { 0x101 }
    $flags = if ($Key -ge 37 -and $Key -le 40) { 0x01000001 } else { 1 }
    if (!$Down) { $flags = $flags -bor 0xC0000000 }
    if (![WyuPlanetInput]::PostMessage($process.MainWindowHandle, $message, $Key, $flags)) {
        throw "Could not send planet key $Key."
    }
}

function Press([int]$Key) {
    Set-Key $Key $true
    Set-Key $Key $false
    Start-Sleep -Milliseconds 85
}

function Enter-Seed([string]$Seed) {
    Wait-Panel '*Planet worlds*New World*'
    Press 13
    Wait-Panel '*Type up to 20 digits*'
    foreach ($digit in $Seed.ToCharArray()) { Press ([int]$digit) }
    Press 13
}

function Continue-Planet {
    Wait-Panel '*Planet worlds*New World*'
    Press 40
    Wait-Panel '*Continue 1/1*'
    Press 13
}

function Wait-Exit([int]$Code = 0) {
    if (!$process.WaitForExit(20000) -or $process.ExitCode -ne $Code) {
        throw "Expected client exit $Code. Evidence $root."
    }
    $process.Dispose()
}

function Screenshot([string]$Name) {
    Add-Type -AssemblyName System.Drawing
    $process.Refresh()
    $bounds = [WyuPlanetInput+Rect]::new()
    if ([WyuPlanetInput]::DwmGetWindowAttribute($process.MainWindowHandle, 9, [ref]$bounds, 16) -ne 0) {
        throw 'Cannot capture the WYU window.'
    }
    $width = $bounds.Right - $bounds.Left
    $height = $bounds.Bottom - $bounds.Top
    if ($width -le 0 -or $height -le 0 -or $width -gt 4096 -or $height -gt 4096) { throw 'Invalid WYU capture rectangle.' }
    $bitmap = [Drawing.Bitmap]::new($width, $height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size)
        $bitmap.Save((Join-Path $root "$Name.png"), [Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Read-Metrics {
    $text = Read-AlphaOverlay $process 'performance'
    if ($text -match 'Frame ([0-9.]+) ms.*\nX ([0-9.-]+) Y ([0-9.-]+) Z ([0-9.-]+) / origin (\d+)') {
        [pscustomobject]@{FrameMs = [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
            Z = [double]::Parse($Matches[4], [Globalization.CultureInfo]::InvariantCulture); Origin = [int]$Matches[5]}
    }
}

function Walk-To([int]$Key, [double]$Target, [bool]$Increasing, [bool]$AlreadyHeld = $false) {
    if (!$AlreadyHeld) { Set-Key $Key $true }
    try {
        Wait-For {
            $sample = Read-Metrics
            if ($null -eq $sample) { return $false }
            $frames.Add($sample.FrameMs)
            if ($Increasing) { return $sample.Z -ge $Target }
            return $sample.Z -le $Target
        } "walk to Z=$Target" 55
    } finally { Set-Key $Key $false }
}

function Set-Pitch([int]$Pixels) {
    Focus-AlphaWindow $process
    if (![WyuPlanetInput]::Pitch($process.MainWindowHandle, $Pixels)) { throw 'Cannot aim the WYU camera.' }
    Start-Sleep -Milliseconds 250
}

function Read-Events([string]$Fixture) {
    $log = Join-Path $Fixture 'client.log'
    $data = Get-Content -LiteralPath $log -Raw
    if ($data -notmatch "build=\S+\+$ExpectedCommit pid=" -or (Get-Item -LiteralPath $log).Length -gt 65536) {
        throw 'Installed log does not identify the exact build or exceeds the log quota.'
    }
    return $data
}

function Verify-Dialog([string]$Case, [string]$Title, [string]$Message, [string]$Phase) {
    $fixture = New-Fixture $Case
    $script:process = Start-Planet $fixture $Case
    Enter-Seed '42'
    Wait-For { $process.MainWindowTitle -like $Title -and
        [WyuPlanetInput]::Dialog($process.MainWindowHandle) -like $Message } "$Case dialog text"
    Screenshot $Case
    if (!$process.CloseMainWindow()) { throw "Could not close the $Case dialog." }
    Wait-Exit 1
    $script:process = $null
    if ((Read-Events $fixture) -notmatch "phase=$Phase event=fault") { throw "$case fault phase missing." }
    Write-Host "PASS: installed $Case failure displayed a visible dialog and exact build/phase."
}

try {
    $fixture = New-Fixture 'mission'
    $process = Start-Planet $fixture
    Enter-Seed '18446744073709551616'
    Wait-Panel '*Seed must be between*'
    Screenshot 'invalid-seed'
    Press 27
    Enter-Seed '42'
    Set-Key 68 $true
    try {
        Wait-Panel '*E remove*F6 flight*'
        $runtime = @($process.Modules | Where-Object ModuleName -eq 'coreclr.dll')
        if ($runtime.Count -ne 1 -or $runtime[0].FileName -ine (Join-Path (Split-Path $clientPath) 'coreclr.dll')) {
            throw 'The installed planet did not use its bundled runtime.'
        }
        Press 112
        Wait-For { $null -ne (Read-Metrics) } 'planet metrics'
        $before = Read-Metrics
        $frames = [Collections.Generic.List[double]]::new()
        Walk-To 68 46 $true $true
    } finally { Set-Key 68 $false }
    $after = Read-Metrics
    if ($after.Origin -le $before.Origin -or $frames.Count -lt 20) { throw 'Installed walk did not cross an origin boundary.' }
    Screenshot 'exploration'
    Walk-To 65 24 $false

    Set-Pitch -170
    Press 69
    Wait-Panel '*Aim at visible terrain*'
    Set-Pitch 220
    Press 50
    Press 81
    Wait-Panel '*Terrain changed*revision 2*'
    Screenshot 'edited-terrain'

    $slot = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'v2') -Directory)
    if ($slot.Count -ne 1) { throw 'Expected one new world in the isolated fixture.' }
    $lockPath = Join-Path $slot[0].FullName 'volume.writer'
    $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        Press 116
        Wait-Panel '*Save busy*'
        Screenshot 'save-busy'
    } finally { $lock.Dispose() }
    Press 121
    Wait-Exit
    $process = $null

    $process = Start-Planet $fixture
    Continue-Planet
    Wait-Panel '*E remove*F6 flight*'
    Press 117
    Wait-For {
        $panel = Read-AlphaOverlay $process
        if ($panel -like '*Takeoff blocked*') { throw "Installed takeoff was rejected: $panel" }
        return $panel -like '*Flight: Orbit*'
    } 'orbital flight' 70
    Screenshot 'orbit'
    Wait-Panel '*Touchdown confirmed*' 140
    Screenshot 'touchdown'
    Press 121
    Wait-Exit
    $process = $null

    $members = @((Join-Path $slot[0].FullName 'volume.a'), (Join-Path $slot[0].FullName 'volume.b'))
    $beforeResume = @($members | Get-FileHash | ForEach-Object Hash)
    $process = Start-Planet $fixture
    Continue-Planet
    Wait-Panel '*Continued from a verified touchdown*'
    Screenshot 'touchdown-resumed'
    Press 27
    Wait-Exit
    $process = $null
    $afterResume = @($members | Get-FileHash | ForEach-Object Hash)
    $events = Read-Events $fixture
    if (($beforeResume -join ',') -cne ($afterResume -join ',') -or
        $events -notmatch 'event=edit operation=Add revision=2' -or $events -notmatch 'event=save-held status=Busy' -or
        $events -notmatch 'event=continue seed=42 generator=1 revision=2 generation=1' -or
        $events -notmatch 'event=flight stage=Orbit' -or $events -notmatch 'event=touchdown .*revision=2' -or
        $events -notmatch 'event=continue seed=42 generator=1 revision=2 generation=2' -or
        $events -notmatch 'event=touchdown-restored .*revision=2' -or $events -notmatch 'event=adapter .*NVIDIA') {
        throw 'Installed mission logs or checkpoint identities did not match the UI route.'
    }
    $ordered = @($frames | Sort-Object)
    $p50 = $ordered[[int][Math]::Ceiling(0.5 * $ordered.Count) - 1]
    $p95 = $ordered[[int][Math]::Ceiling(0.95 * $ordered.Count) - 1]
    $frames | Set-Content -LiteralPath (Join-Path $root 'walking-overlay-frame-ms.txt')
    Write-Host "PASS: Installed UI seed/edit/save/orbit/touchdown/continue. Walking overlay samples $($frames.Count), p50 $p50 ms, p95 $p95 ms."

    foreach ($case in @('incompatible', 'corrupt')) {
        $bad = New-Fixture $case
        $badV2 = Join-Path $bad 'v2'
        Copy-Item -LiteralPath (Join-Path $fixture 'v2') -Destination $badV2 -Recurse
        if ($case -eq 'incompatible') {
            [IO.File]::WriteAllText((Join-Path $badV2 "$($slot[0].Name).wyu"), 'old alpha format')
            $pattern = '*incompatible world format*files were kept*'
        } else {
            foreach ($name in @('volume.a','volume.b')) {
                $member = Join-Path (Join-Path $badV2 $slot[0].Name) $name
                $bytes = [IO.File]::ReadAllBytes($member)
                $bytes[-1] = $bytes[-1] -bxor 1
                [IO.File]::WriteAllBytes($member, $bytes)
            }
            $pattern = '*damaged checkpoint data*files were kept*'
        }
        $badMembers = @((Join-Path $badV2 "$($slot[0].Name).wyu")) + @(
            'volume.a','volume.b' | ForEach-Object { Join-Path (Join-Path $badV2 $slot[0].Name) $_ })
        $hashes = @($badMembers | Get-FileHash | ForEach-Object Hash)
        $process = Start-Planet $bad
        Continue-Planet
        Wait-Panel $pattern
        Screenshot $case
        Press 27
        Wait-Exit
        $process = $null
        if (($hashes -join ',') -cne (@($badMembers | Get-FileHash | ForEach-Object Hash) -join ',')) {
            throw "$case altered protected save data."
        }
    }

    Verify-Dialog 'graphics' '*Graphics error' '*Direct3D 12 renderer could not start*' 'GraphicsStartup'
    Verify-Dialog 'budget' '*Planet loading error' '*terrain streaming budget is full*' 'PlanetTerrain'
    Write-Host "PASS: installed planet mission and visible failure cases. Evidence $root"
} finally {
    if ($null -ne $process) {
        if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
}
