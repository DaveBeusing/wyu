# Windows host, cube and terrain

Build and run from a clean WYU checkout on Windows with the .NET 10 SDK,
PowerShell 7, and GitHub CLI. The engine is supplied by the immutable
Groundwork NuGet archive. WYU pins all nine packages at
`0.1.0-dev.42225951792.1` and commits the SDK-generated lockfile.
The renderer runs on Windows x64 and requires a D3D12 hardware adapter.

Download the archive to a new temporary directory and run the verification
gate from the WYU repository root.

```powershell
$archive = Join-Path ([IO.Path]::GetTempPath()) ('wyu-archive-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $archive | Out-Null
gh release download archive-groundwork-0.1.0-dev.42225951792.1-immutable `
  --repo DaveBeusing/Groundwork --dir $archive
if ($LASTEXITCODE -ne 0) { throw 'Groundwork archive download failed.' }
./eng/Test-VerifiedGroundworkConsumer.ps1 -ArtifactDirectory $archive `
  -ExpectedManifestSha256 15DBEE87CE4BC3936757676E365D93ECF3F252F7C5C255B2615CB25BFEB0E841 `
  -ExpectedSourceCommit d7645731c18693e4c8e42ebe05e794ea30b0454e `
  -ExpectedVersion 0.1.0-dev.42225951792.1
```

The gate checks package provenance, stages a temporary external NuGet feed,
restores in locked mode with a fresh cache, builds in Release, and runs
headless startup/shutdown and controlled-fault smoke cases. Add `-WindowSmoke`
on an interactive Windows desktop to open and close a real window under the
same gate. Add `-D3D12Smoke` to verify hardware adapter selection, first
cube presentation and native window close. The D3D12 smoke requests Debug
Layer and DRED. Windows Graphics Tools enables the debug layer and live-object
report. Start the renderer interactively with `--d3d12` and close the window to
stop. Starting without an argument opens the renderer-free host.

```powershell
dotnet ./artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll --d3d12
```

For archive provenance and CI verification, see [the package handover](../eng/README.md).

Launch with `--terrain` to view WP03's two adjacent voxel chunks and the raised
half of the second chunk. Escape or native window close stops the host. Use
`-TerrainSmoke` with the verified consumer script on an interactive Windows
desktop with Graphics Tools installed. It requires hardware D3D12, an active
Debug Layer and DRED, a presented terrain frame, native close and a clean
D3D12 live-object report.
