# WP01-L6 Windows host

Build and run from a clean WYU checkout on Windows with the .NET 10 SDK,
PowerShell 7, and GitHub CLI. The engine is supplied by the immutable
Groundwork NuGet archive. WYU pins all six packages at
`0.1.0-dev.42137008200.1` and commits the SDK-generated lockfile.

Download the archive to a new temporary directory and run the verification
gate from the WYU repository root.

```powershell
$archive = Join-Path ([IO.Path]::GetTempPath()) ('wyu-archive-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $archive | Out-Null
gh release download archive-groundwork-0.1.0-dev.42137008200.1 `
  --repo DaveBeusing/Groundwork --dir $archive
if ($LASTEXITCODE -ne 0) { throw 'Groundwork archive download failed.' }
./eng/Test-VerifiedGroundworkConsumer.ps1 -ArtifactDirectory $archive `
  -ExpectedManifestSha256 7981A019E4D8C4706E66D0C00B5E3E9150ABBA5DE77439DFC13C73FEC9AF2DB3 `
  -ExpectedSourceCommit 8da185b176961e365dc32261d8c80a672a01b7b9 `
  -ExpectedVersion 0.1.0-dev.42137008200.1
```

The gate checks package provenance, stages a temporary external NuGet feed,
restores in locked mode with a fresh cache, builds in Release, and runs
headless startup/shutdown and controlled-fault smoke cases. Add `-WindowSmoke`
on an interactive Windows desktop to open and close a real window under the
same gate. To run the built host interactively, omit the smoke arguments and
close its window to stop.

```powershell
dotnet ./artifacts/bin/WorldsYetUnseen.Client/release/WorldsYetUnseen.Client.dll
```

For archive provenance and CI verification, see [the package handover](../eng/README.md).
