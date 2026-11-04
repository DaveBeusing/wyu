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

At bootstrap there is no trusted, built Groundwork nupkg or signed-off
version/manifest, so the host intentionally declares NO Groundwork package
references yet. Do not invent an approved prerelease coordinate or a
NuGet contentHash. After Groundwork B00 produces a real package and L4
supplies independently trusted source/manifest SHA-256 verification, WYU
L6 must:
1. Choose and record an exact package ID/version approved for consumption.
2. Verify and stage package bytes to GROUNDWORK_NUGET_FEED outside the worktree.
3. Add explicit version-pinned Groundwork PackageReference dependencies.
4. Run a fresh-cache restore and commit SDK-generated packages.lock.json.
5. Require dotnet restore --locked-mode in CI and negative-test missing,
   modified or wrong-version packages and stale locks.

Root source SemVer is independently 0.1.0; packaging uses unique
0.1.0-dev.<run-id>.<attempt> prereleases without per-commit source bumps.
Development artifacts are retained 90 days unless still pinned;
release archives/manifests require durable non-expiring storage.
L4 and release work own actual retention/publishing automation.

No software license is implied by a public repository. Third-party
licenses and vulnerabilities need review before dependency adoption.
This draft does not claim that trust verification, a package pin, lock,
CI workflow, Windows smoke test, or playable host has been delivered.

## Ownership

B00 owns global.json, Directory.Build.props, NuGet.Config, the solution and
the project boundary. The L6 lane owns game-host implementation and package
consumption, and must preserve strict engine/game repository separation.
