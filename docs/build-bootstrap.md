# WP01-B00 — Worlds Yet Unseen consumer bootstrap

This is the separate Windows-targeted .NET 10 host build skeleton.
It is not a playable game or a simulated engine integration. The bootstrap
executable exits with status 2 and reports that WP01-L6 host wiring is missing.

## Prerequisites and build

Install a stable .NET 10 SDK and Git on Windows. global.json selects a
10.0 SDK at or above 10.0.100 in an installed .NET 10 feature band.
NuGet.Config is intentionally restricted to nuget.org plus a validated
local Groundwork feed outside all Git worktrees.

Set GROUNDWORK_NUGET_FEED to an absolute existing directory outside the
repositories before restore. Example PowerShell setup:

    $env:GROUNDWORK_NUGET_FEED = Join-Path $env:LOCALAPPDATA 'Groundwork/verified-feed'
    New-Item -ItemType Directory -Force $env:GROUNDWORK_NUGET_FEED | Out-Null
    dotnet restore .\WorldsYetUnseen.slnx --configfile .\NuGet.Config
    dotnet build .\WorldsYetUnseen.slnx -c Release --no-restore
    dotnet test .\WorldsYetUnseen.slnx -c Release --no-build

No game behavior or runnable tests are yet implemented. The build/test
commands have not been verified on a Windows runner by this bootstrap.
Generated bin/obj/artifacts and distributables must not be committed.
Each checkout/worktree keeps separate ignored build outputs.

## Package security: deliberate handover gate

Groundwork.Core and other engine packages must be consumed as exact
immutable NuGet versions, never direct cross-repository ProjectReference
or implicit package source fallback. Package source mapping ensures
Groundwork.* names select only the validated local feed; nuget.org is
restricted to approved other packages. No GitHub Packages feed is assumed.

The owner approved Groundwork.Core `0.1.0-dev.41920122570.1`, an immutable
candidate produced by successful Groundwork CI run `41920122570`.
WYU pins this full version in its PackageReference and commits the actual
NuGet-generated `packages.lock.json` from the verified CI package bytes.
Use `dotnet restore --locked-mode` and a fresh cache; do not invent NuGet
content hashes. `scripts/Verify-Bootstrap.ps1` checks this exact pin and lock
against the configured external feed but does not authenticate that feed.
The independent provenance-and-download gate is implemented separately in
WYU L6 PR #3; the original Actions artifact must be durably preserved before
its ordinary retention expires. A clean checkout without that package will
fail closed, not silently switch to another version.

Root source SemVer is independently 0.1.0; packaging uses unique
0.1.0-dev.<run-id>.<attempt> prereleases without per-commit source bumps.
Development artifacts are retained 90 days unless still pinned;
release archives/manifests require durable non-expiring storage.
L4 and release work own actual retention/publishing automation.

No software license is implied by a public repository. Third-party
licenses and vulnerabilities need review before dependency adoption.
This bootstrap includes a real pinned package and NuGet lock, while the separate
L6 PR handles independent CI artifact verification. No playable host or runtime
startup/shutdown test has been delivered.

## Ownership

B00 owns global.json, Directory.Build.props, NuGet.Config, the solution and
the project boundary. The L6 lane owns game-host implementation and package
consumption, and must preserve strict engine/game repository separation.
