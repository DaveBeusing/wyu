param([string]$Assembly = (Join-Path $PSScriptRoot '../artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll'))

$ErrorActionPreference = 'Stop'
$client = (Resolve-Path -LiteralPath $Assembly).Path
$fixture = [IO.Directory]::CreateTempSubdirectory('wyu-wp17-').FullName
New-Item -ItemType File -Path (Join-Path $fixture 'wyu-cave-smoke.fixture') | Out-Null
Write-Host "WP17 fixture: $fixture"
foreach ($phase in 'v3-init', 'v3-resume') {
    $output = @(& dotnet $client --cave-smoke $phase $fixture)
    $exitCode = $LASTEXITCODE
    $output | Write-Host
    $output | Set-Content -LiteralPath (Join-Path $fixture "$phase.log")
    if ($exitCode -ne 0 -or ($phase -eq 'v3-resume' -and @($output | Where-Object { $_ -cmatch '^WP17 mesh profile ' }).Count -ne 3) -or
        $output -cnotcontains 'PASS: WYU WP12 cave route and native shutdown.') {
        throw "WP17 failed in $phase (exit $exitCode)."
    }
    if ($phase -eq 'v3-resume' -and $output -cnotcontains 'PASS: WP17 V3 orbit and return retained cave edits and ore.') {
        throw 'WP17 orbit return was not verified.'
    }
}
