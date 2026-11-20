param(
    [string]$Assembly = (Join-Path $PSScriptRoot '../artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll'),
    [ValidateSet('warp', 'hardware')][string]$Adapter = 'hardware',
    [ValidateSet('far-horizon', 'orbit-combined')][string]$Composition = 'far-horizon',
    [string]$OutputDirectory,
    [switch]$SunlightOff
)
$ErrorActionPreference = 'Stop'
$client = (Resolve-Path -LiteralPath $Assembly).Path
$names = 'WYU_DAWN_SMOKE', 'WYU_DAWN_CAPTURE_DIR', 'WYU_PACKAGE_E', 'WYU_PACKAGE_G', 'WYU_WP15',
    'WYU_WP16_SMOKE', 'WYU_ORBIT_DEMO_SMOKE', 'WYU_ORBIT_PBR', 'WYU_ORBIT_BOULDER', 'WYU_ORBIT_PBR_MEASURE'
$prior = @{}
foreach ($name in $names) { $prior[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $env:WYU_DAWN_SMOKE, $env:WYU_PACKAGE_E, $env:WYU_PACKAGE_G = '1', 'on', 'on'
    $env:WYU_WP15 = if ($SunlightOff) { 'off' } else { 'on' }
    $env:WYU_WP16_SMOKE = '0'
    $env:WYU_ORBIT_DEMO_SMOKE = if ($Composition -eq 'orbit-combined') { '1' } else { $null }
    $env:WYU_ORBIT_PBR, $env:WYU_ORBIT_BOULDER, $env:WYU_ORBIT_PBR_MEASURE = 'on', 'on', $null
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
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $prior[$name]) }
}
Write-Host "PASS: dynamic dawn/dusk route on $Adapter ($Composition)."
