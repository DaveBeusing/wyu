param(
    [string]$Assembly = (Join-Path $PSScriptRoot '../artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll'),
    [ValidateSet('warp', 'hardware')][string]$Adapter = 'hardware',
    [string]$OutputDirectory,
    [switch]$SunlightOff
)
$ErrorActionPreference = 'Stop'
$client = (Resolve-Path -LiteralPath $Assembly).Path
$prior = $env:WYU_DAWN_SMOKE, $env:WYU_DAWN_CAPTURE_DIR, $env:WYU_PACKAGE_E, $env:WYU_PACKAGE_G, $env:WYU_WP15, $env:WYU_WP16_SMOKE
try {
    $env:WYU_DAWN_SMOKE, $env:WYU_PACKAGE_E, $env:WYU_PACKAGE_G = '1', 'on', 'on'
    $env:WYU_WP15 = if ($SunlightOff) { 'off' } else { 'on' }
    $env:WYU_WP16_SMOKE = '0'
    $env:WYU_DAWN_CAPTURE_DIR = if ($OutputDirectory) {
        (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
    } else { $null }
    $lines = @(& dotnet $client --volume-smoke 42 $Adapter)
    $code = $LASTEXITCODE
    $lines | Write-Host
    if ($code -ne 0 -or $lines -cnotcontains 'PASS: WYU animated sunrise/sunset with unchanged clock and terrain publication.' -or
        $lines -cnotcontains 'PASS: WYU WP11 exploration, rebase and native shutdown.') {
        throw 'Twilight route failed.'
    }
    foreach ($phase in 'sunrise','sunset') {
        $frames = @($lines | Where-Object { $_ -cmatch '^TWILIGHT FRAME ' } |
            ForEach-Object { $_.Substring(15) | ConvertFrom-Json } | Where-Object phase -ceq $phase)
        if ($frames.Count -ne 7) { throw "Missing $phase checkpoints." }
    }
} finally {
    $env:WYU_DAWN_SMOKE, $env:WYU_DAWN_CAPTURE_DIR, $env:WYU_PACKAGE_E, $env:WYU_PACKAGE_G, $env:WYU_WP15, $env:WYU_WP16_SMOKE = $prior
}
Write-Host "PASS: dynamic dawn/dusk route on $Adapter."
