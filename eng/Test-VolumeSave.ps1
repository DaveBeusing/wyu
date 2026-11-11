param(
    [string]$Assembly = (Join-Path $PSScriptRoot '../artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$client = (Resolve-Path -LiteralPath $Assembly).Path
$root = Join-Path ([IO.Path]::GetTempPath()) ('wyu-w03-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
New-Item -ItemType File -Path (Join-Path $root 'wyu-volume-smoke.fixture') | Out-Null
Write-Host "Volume fixture $root"

function Invoke-VolumePhase([string]$Phase) {
    $output = @(& dotnet $client --volume-save-smoke $Phase $root)
    $exitCode = $LASTEXITCODE
    $output | Write-Host
    $checkpoints = @($output | Where-Object { $_ -cmatch '^WP11 CHECKPOINT ' })
    if ($exitCode -ne 0 -or $checkpoints.Count -ne 1 -or
        $output -cnotcontains 'PASS: WYU WP11 exploration, rebase and native shutdown.') {
        throw "Volume phase $Phase failed (exit $exitCode)."
    }
    return $checkpoints[0]
}

$expected = Invoke-VolumePhase 'init'
if ((Invoke-VolumePhase 'restore') -cne $expected) {
    throw 'Reopened checkpoint changed after normal exit.'
}
$slots = @(Get-ChildItem -LiteralPath (Join-Path $root 'v2') -Directory)
if ($slots.Count -ne 1) { throw 'Expected exactly one reserved slot.' }
$a = Join-Path $slots[0].FullName 'volume.a'
$b = Join-Path $slots[0].FullName 'volume.b'
$intent = Join-Path $slots[0].FullName 'volume.intent'
$before = @((Get-FileHash -LiteralPath $a).Hash, (Get-FileHash -LiteralPath $b).Hash)
$beforeIntent = if (Test-Path -LiteralPath $intent) { (Get-FileHash -LiteralPath $intent).Hash } else { '' }
$log = Join-Path $root 'interrupt.stdout'
$errors = Join-Path $root 'interrupt.stderr'

# Keep the existing generation-one member readable but deny rename over it.
$lock = [IO.File]::Open($a, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
$writer = $null
try {
    $processOptions = @{
        FilePath = (Get-Command dotnet).Source
        ArgumentList = @(('"' + $client + '"'), '--volume-save-smoke', 'interrupt', ('"' + $root + '"'))
        PassThru = $true
        WindowStyle = 'Hidden'
        RedirectStandardOutput = $log
        RedirectStandardError = $errors
    }
    $writer = Start-Process @processOptions
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        if ($writer.HasExited) {
            Get-Content -LiteralPath $log,$errors | Write-Host
            throw "The volume writer exited before the interruption point (exit $($writer.ExitCode))."
        }
        if (Select-String -LiteralPath $log -SimpleMatch 'WP11 INTERRUPT READY:' -Quiet) { break }
        if ([DateTime]::UtcNow -ge $deadline) { throw 'The writer did not reach the blocked publication.' }
        Start-Sleep -Milliseconds 100
    } while ($true)
    Get-Content -LiteralPath $log | Write-Host
    if ((Get-FileHash -LiteralPath $intent).Hash -ceq $beforeIntent) {
        throw 'The new write intent was not installed before interruption.'
    }
    $writer.Kill()
    if (!$writer.WaitForExit(5000)) { throw 'Could not interrupt the volume writer.' }
} finally {
    if ($null -ne $writer) {
        if (!$writer.HasExited) { $writer.Kill(); $writer.WaitForExit(5000) | Out-Null }
        $writer.Dispose()
    }
    $lock.Dispose()
}

if ((Get-FileHash -LiteralPath $a).Hash -cne $before[0] -or (Get-FileHash -LiteralPath $b).Hash -cne $before[1]) {
    throw 'Interrupted publication changed a committed member.'
}
if ((Invoke-VolumePhase 'verify') -cne $expected) {
    throw 'Recovered checkpoint changed after interrupted publication.'
}

$legacyId = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
$legacy = Join-Path (Join-Path $root 'v2') $legacyId
New-Item -ItemType Directory -Path $legacy | Out-Null
$oldSave = Join-Path $legacy 'volume.a'
$oldManifest = Join-Path (Join-Path $root 'v2') "$legacyId.wyu"
[IO.File]::WriteAllText($oldSave, 'unsupported alpha save fixture')
[IO.File]::WriteAllText($oldManifest, 'unsupported alpha manifest')
$oldHashes = @((Get-FileHash -LiteralPath $oldSave).Hash, (Get-FileHash -LiteralPath $oldManifest).Hash)
& dotnet $client --volume-save-smoke protected $root
if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $oldSave).Hash -cne $oldHashes[0] -or
    (Get-FileHash -LiteralPath $oldManifest).Hash -cne $oldHashes[1] -or
    @(Get-ChildItem -LiteralPath $legacy -Force).Count -ne 1) {
    throw 'The incompatible occupied slot was changed.'
}
Write-Host 'PASS: NTFS restart and interrupted publication preserved both members and the edited world.'
