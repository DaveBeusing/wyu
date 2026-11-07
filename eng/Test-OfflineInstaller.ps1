param(
    [Parameter(Mandatory)][string]$InstallerDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$ExpectedManifestSha256
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (![Environment]::UserInteractive) { throw 'Use an interactive Windows desktop.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run the installer test without elevation.'
}

$source = (Resolve-Path -LiteralPath $InstallerDirectory).Path
$manifestPath = Join-Path $source 'installer-manifest.json'
if ((Get-FileHash -LiteralPath $manifestPath).Hash -ine $ExpectedManifestSha256) { throw 'Unexpected installer manifest.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.target -cne 'win-x64' -or
    $manifest.installer -notmatch '^WYU-Alpha-\d+\.\d+\.\d+-win-x64\.exe$') { throw 'Invalid installer manifest.' }
$installer = Join-Path $source $manifest.installer
if ((Get-FileHash -LiteralPath $installer).Hash -ine $manifest.installerSha256) { throw 'Installer digest mismatch.' }

$app = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/Worlds Yet Unseen Alpha'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Worlds Yet Unseen Alpha.lnk'
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{E7123057-A9B2-46C8-A611-40C1D5504964}_is1'
$exe = Join-Path $app 'WorldsYetUnseen.Client.exe'
foreach ($path in $app,$shortcut,$registration) {
    if (Test-Path -LiteralPath $path) { throw "Use a profile without a previous WYU Alpha installation. Found $path." }
}
$sentinel = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('WorldsYetUnseen/InstallerTest-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sentinel | Out-Null
$dataHashes = @{}
foreach ($name in 'settings.json','save.a') {
    $path = Join-Path $sentinel $name
    [IO.File]::WriteAllText($path, [guid]::NewGuid().ToString('N'))
    $dataHashes[$path] = (Get-FileHash -LiteralPath $path).Hash
}

function Invoke-Setup([string]$Path) {
    $process = Start-Process -FilePath $Path -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Installer process returned $($process.ExitCode)." }
}

function Assert-Payload {
    foreach ($file in $manifest.payload) {
        $target = [IO.Path]::GetFullPath((Join-Path $app $file.path))
        if (!$target.StartsWith($app + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            (Get-FileHash -LiteralPath $target).Hash -ine $file.sha256) { throw "Installed payload mismatch for $($file.path)." }
    }
}

function Assert-UserData {
    foreach ($path in $dataHashes.Keys) {
        if ((Get-FileHash -LiteralPath $path).Hash -ine $dataHashes[$path]) { throw "User data changed at $path." }
    }
}

Invoke-Setup $installer
Assert-Payload
if (!(Test-Path -LiteralPath $registration)) { throw 'Missing per-user Installed apps entry.' }
$link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
if ($link.TargetPath -ine $exe -or $link.WorkingDirectory -ine $app -or $link.Arguments) { throw 'Invalid Start menu entry.' }
$client = Start-Process -FilePath $shortcut -PassThru
try {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 150
        $client.Refresh()
        if ($client.HasExited -or $clock.Elapsed.TotalSeconds -ge 30) { throw 'Start menu entry did not open the app.' }
    } until ($client.MainWindowTitle -like 'WYU | * | Arrows / Enter')
    $coreclr = @($client.Modules | Where-Object ModuleName -eq 'coreclr.dll')
    if ($coreclr.Count -ne 1 -or $coreclr[0].FileName -ine (Join-Path $app 'coreclr.dll')) {
        throw 'Start menu launch did not use the bundled .NET runtime.'
    }
    if (!$client.CloseMainWindow() -or !$client.WaitForExit(20000)) {
        throw 'The installed app did not close its start menu.'
    }
    if ($client.ExitCode -ne 0) { throw "The installed app returned $($client.ExitCode)." }
} finally {
    if (!$client.HasExited) { $client.Kill(); $client.WaitForExit() }
    $client.Dispose()
}
Write-Host 'PASS: Per-user installation and Start menu launch with bundled runtime.'

$repairFile = Join-Path $app 'WorldsYetUnseen.Client.runtimeconfig.json'
if (!(Test-Path -LiteralPath $repairFile -PathType Leaf)) { throw 'Repair fixture is missing.' }
Remove-Item -LiteralPath $repairFile
Invoke-Setup $installer
Assert-Payload
Assert-UserData
Write-Host 'PASS: Reinstall restored the removed program file and kept user data.'

$uninstall = (Get-ItemProperty -LiteralPath $registration).QuietUninstallString
if ($uninstall -notmatch '^"([^"]+)"(?:\s|$)') { throw 'Invalid uninstall command.' }
$uninstaller = $Matches[1]
if ([IO.Path]::GetDirectoryName($uninstaller) -ine $app -or [IO.Path]::GetFileName($uninstaller) -notmatch '^unins\d+\.exe$') {
    throw 'Unexpected uninstall path.'
}
Invoke-Setup $uninstaller
foreach ($path in $app,$shortcut,$registration) {
    if (Test-Path -LiteralPath $path) { throw "Uninstall left $path." }
}
Assert-UserData
Write-Host "PASS: Uninstall removed program files and Start menu entry and retained test data at $sentinel."
