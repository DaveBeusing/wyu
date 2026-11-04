# WP01-B00: local Windows host scaffold build verification (not package acceptance).
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
function Assert-True([bool] $condition, [string] $message) {
    if (-not $condition) { throw $message }
}
function Invoke-Native([string] $exe, [string[]] $arguments) {
    Write-Host ('> ' + $exe + ' ' + ($arguments -join ' '))
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) {
        throw ("{0} failed with exit code {1}" -f $exe, $LASTEXITCODE)
    }
}
Push-Location $root
try {
    Assert-True (Test-Path 'WorldsYetUnseen.slnx') 'WYU solution is missing.'
    [xml] $solution = Get-Content 'WorldsYetUnseen.slnx' -Raw
    $projects = @($solution.SelectNodes('//Project') | ForEach-Object { $_.GetAttribute('Path') })
    Assert-True (($projects.Count -eq 1) -and ($projects[0] -eq 'src/WorldsYetUnseen.Client/WorldsYetUnseen.Client.csproj')) 'Unexpected WYU project graph.'
    [xml] $props = Get-Content 'Directory.Build.props' -Raw
    $version = $props.SelectSingleNode('//PropertyGroup/Version')
    Assert-True (($null -ne $version) -and ($version.InnerText -eq '0.1.0')) 'Source version must be 0.1.0.'
    [xml] $project = Get-Content $projects[0] -Raw
    $refs = @($project.SelectNodes('//ProjectReference'))
    Assert-True ($refs.Count -eq 0) 'The WYU host must not use source ProjectReference dependencies.'
    $packages = @($project.SelectNodes('//PackageReference'))
    Assert-True ($packages.Count -eq 0) 'B00 has no approved engine package yet; do not fabricate package pins.'
    [xml] $config = Get-Content 'NuGet.Config' -Raw
    $feed = $config.SelectSingleNode('//packageSources/add[@key="groundwork-validated-local"]')
    Assert-True (($null -ne $feed) -and ($feed.GetAttribute('value') -eq '%GROUNDWORK_NUGET_FEED%')) 'Validated feed environment mapping is missing.'
    $groundworkMapping = $config.SelectSingleNode('//packageSourceMapping/packageSource[@key="groundwork-validated-local"]/package[@pattern="Groundwork.*"]')
    Assert-True ($null -ne $groundworkMapping) 'Groundwork packages must map to the validated local feed.'
    Assert-True (-not [string]::IsNullOrWhiteSpace($env:GROUNDWORK_NUGET_FEED)) 'Set GROUNDWORK_NUGET_FEED to a validated local directory outside worktrees.'
    $feedPath = [System.IO.Path]::GetFullPath($env:GROUNDWORK_NUGET_FEED)
    $rootWithSeparator = $root.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    Assert-True (-not $feedPath.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) 'The NuGet feed must be outside the WYU worktree.'
    Assert-True (Test-Path $feedPath -PathType Container) 'Validated feed directory does not exist.'
    $tracked = @(& git ls-files)
    if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed.' }
    $generated = @($tracked | Where-Object { $_ -match '(^|/)(bin|obj|artifacts|dist)/|\.(nupkg|snupkg|dll|exe)$' })
    Assert-True ($generated.Count -eq 0) ('Generated files are tracked: ' + ($generated -join ', '))
    Invoke-Native 'dotnet' @('--version')
    Invoke-Native 'dotnet' @('restore', 'WorldsYetUnseen.slnx', '--configfile', 'NuGet.Config', '--force-evaluate')
    Invoke-Native 'dotnet' @('build', 'WorldsYetUnseen.slnx', '-c', 'Release', '--no-restore')
    Write-Host 'PASS: WYU scaffold structure, restore and Release build.' -ForegroundColor Green
    Write-Host 'This does NOT validate Groundwork NuGet pinning, package provenance or game integration.'
} finally {
    Pop-Location
}
