param(
    [string]$Assembly = (Join-Path $PSScriptRoot '../artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll'),
    [ValidateSet('warp', 'hardware')][string]$Adapter = 'hardware'
)

$ErrorActionPreference = 'Stop'
$client = (Resolve-Path -LiteralPath $Assembly).Path
$prior = $env:WYU_PACKAGE_G_SMOKE
try {
    $env:WYU_PACKAGE_G_SMOKE = '1'
    foreach ($seed in '42','20261111') {
        $root = Join-Path ([IO.Path]::GetTempPath()) ('wyu-sky-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $root | Out-Null
        New-Item -ItemType File -Path (Join-Path $root 'wyu-volume-smoke.fixture') | Out-Null
        $landed = $null
        foreach ($action in 'flight','resume') {
            $output = @(& dotnet $client --volume-flight-smoke $action $root $Adapter $seed)
            $exitCode = $LASTEXITCODE
            $output | Write-Host
            if ($exitCode -ne 0 -or $output -cnotcontains 'PASS: WYU WP11 exploration, rebase and native shutdown.') {
                throw "Sky route $seed/$action failed."
            }
            $checkpoints = if ($action -eq 'flight') { 'twilight','to-orbit','orbit','to-ground','landed' } else { 'landed' }
            foreach ($stage in $checkpoints) {
                $matchesForStage = @($output | Where-Object { $_ -cmatch "^PACKAGE G $stage seed=$seed " })
                if ($matchesForStage.Count -ne 1) { throw "Expected one $stage checkpoint for seed $seed." }
                if ($stage -eq 'landed') {
                    if ($null -ne $landed -and $landed -cne $matchesForStage[0]) { throw 'Restored sky changed.' }
                    $landed = $matchesForStage[0]
                }
            }
        }
    }
} finally {
    $env:WYU_PACKAGE_G_SMOKE = $prior
}
Write-Host "PASS: Package G two-seed sky route and Continue on $Adapter."
