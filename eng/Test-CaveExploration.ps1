param(
    [string]$Assembly = (Join-Path $PSScriptRoot '../artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows) { throw 'The cave qualification requires a Windows desktop with an NVIDIA adapter.' }

$client = (Resolve-Path -LiteralPath $Assembly).Path
$fixture = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ('wyu-wp12-' + [guid]::NewGuid().ToString('N')))
New-Item -ItemType File -Path (Join-Path $fixture.FullName 'wyu-cave-smoke.fixture') | Out-Null
Write-Host "Cave fixture $($fixture.FullName)"

function Invoke-CavePhase([string]$Phase, [string]$CheckpointLabel) {
    $output = @(& dotnet $client --cave-smoke $Phase $fixture.FullName)
    $exitCode = $LASTEXITCODE
    $output | Write-Host
    $checkpoint = @($output | Where-Object { $_.StartsWith($CheckpointLabel, [StringComparison]::Ordinal) })
    $nvidia = @($output | Where-Object { $_ -cmatch '^D3D12 adapter: .* \(10DE:[0-9A-F]{4}\), .*software=False\.$' })
    if ($exitCode -ne 0 -or $checkpoint.Count -ne 1 -or $nvidia.Count -ne 1 -or
        $output -cnotcontains 'D3D12 debug layer=True; DRED=True.' -or
        $output -cnotcontains 'PASS: WYU WP12 cave route and native shutdown.') {
        throw "Cave phase $Phase failed (exit $exitCode)."
    }
    $state = $checkpoint[0].Substring($CheckpointLabel.Length).Trim()
    if (!$state) { throw "Cave phase $Phase omitted its checkpoint evidence." }
    return $state
}

function Get-CommittedMemberHashes([string]$Slot) {
    foreach ($name in @('volume.a', 'volume.b')) {
        $member = Join-Path $Slot $name
        if (Test-Path -LiteralPath $member -PathType Leaf) {
            "$name $((Get-FileHash -LiteralPath $member -Algorithm SHA256).Hash)"
        }
    }
}

$saved = Invoke-CavePhase 'init' 'WP12 cave saved:'
$slots = @(Get-ChildItem -LiteralPath (Join-Path $fixture.FullName 'v2') -Directory)
if ($slots.Count -ne 1) { throw 'Expected exactly one cave slot.' }
$memberHashes = @(Get-CommittedMemberHashes $slots[0].FullName)
if ($memberHashes.Count -eq 0) { throw 'No committed cave checkpoint was written.' }

$continued = Invoke-CavePhase 'resume' 'WP12 cave continued:'
if ($continued -cne $saved) { throw 'The cave checkpoint changed after a new process opened it.' }
if ((@(Get-CommittedMemberHashes $slots[0].FullName) -join "`n") -cne ($memberHashes -join "`n")) {
    throw 'Continuing under ground rewrote a committed member.'
}
Write-Host 'PASS: NVIDIA cave mining, underground continuation and native shutdown.'
