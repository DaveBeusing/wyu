param(
    [string]$Assembly = (Join-Path $PSScriptRoot '../artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$client = (Resolve-Path -LiteralPath $Assembly).Path
$previous = $env:WYU_WP16_SMOKE
try {
    $env:WYU_WP16_SMOKE = '1'
    foreach ($seed in '42', '20261111') {
        $output = @(& dotnet $client --volume-smoke $seed hardware)
        $exitCode = $LASTEXITCODE
        $output | Write-Host
        if ($exitCode -ne 0 -or
            @($output | Where-Object { $_ -cmatch "^WP16 seed=$seed " }).Count -ne 1 -or
            $output -cnotcontains 'PASS: WYU WP11 exploration, rebase and native shutdown.') {
            throw "WP16 NVIDIA route failed for seed $seed (exit $exitCode)."
        }
    }
} finally {
    $env:WYU_WP16_SMOKE = $previous
}
