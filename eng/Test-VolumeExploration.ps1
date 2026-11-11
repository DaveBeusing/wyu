param(
    [string]$Assembly = (Join-Path $PSScriptRoot '../artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$client = (Resolve-Path -LiteralPath $Assembly).Path
foreach ($seed in @('42', '20261111')) {
    $reference = $null
    foreach ($adapter in @('warp', 'hardware')) {
        Write-Host "Exploration seed $seed on $adapter"
        $output = @(& dotnet $client --volume-smoke $seed $adapter)
        $exitCode = $LASTEXITCODE
        $output | Write-Host
        if ($exitCode -ne 0 -or $output -cnotcontains 'PASS: WYU WP11 exploration, rebase and native shutdown.') {
            throw "Exploration failed for seed $seed on $adapter (exit $exitCode)."
        }

        $checkpoints = @($output | Where-Object { $_ -cmatch '^WP11 (spawn|route) seed=' })
        if ($checkpoints.Count -ne 3) {
            throw "Expected spawn and two route checkpoints for seed $seed on $adapter."
        }
        $trace = $checkpoints -join "`n"
        if ($null -eq $reference) {
            $reference = $trace
        } elseif ($trace -cne $reference) {
            throw "Route checkpoints changed across fresh processes for seed $seed."
        }
    }
}

Write-Host 'PASS: Both WP11 seed routes matched across fresh WARP and NVIDIA processes.'
