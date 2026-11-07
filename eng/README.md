# Groundwork package handover

WYU independently versions its source at `0.1.0` and uses exact NuGet
references to the ten Groundwork modules. `NuGet.Config` maps `Groundwork.*`
to a verified local feed and the committed lockfile records the SDK-generated
package content hashes. NuGet.org supplies the renderer's transitive
`TerraFX.Interop.Windows` dependency at `10.0.26100.6`.

| Groundwork producer | Pinned value |
| --- | --- |
| Package version | `0.1.0-dev.42417550607.1` |
| Master commit | `ae494c51eead61feb032ccdfdfed1089c04dc04a` |
| Windows CI run and attempt | [`42417550607.1`](https://github.com/DaveBeusing/Groundwork/actions/runs/42417550607/attempts/1) |
| Producer artifact ZIP SHA-256 | `3ABF542276CA93C84FD510272D0C5147457E895A27DA46B623D04D95D6A7322B` |
| Build manifest SHA-256 | `1FA434525FFF4382D4C7485C8A8F32F1EB68D929A59A773152078B8A901AEAFB` |
| Archive record SHA-256 | `FAED845933CBCC10C3313B3AA61D1CE62FD7EB546737C2B7D7964C98435315F4` |

The [immutable GitHub release](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.42417550607.1-immutable)
contains the original ten CI packages, the build manifest, the archive
record, and the Apache-2.0 license. Keep the release available while any
consumer pins the version.

CI validates the producer run and immutable release metadata, checks the
archive asset hashes and commit, then invokes `Test-VerifiedGroundworkConsumer.ps1`.
The script checks every package hash, rejects missing, tampered, and
incompatible packages, then restores and builds with a fresh cache from an
external feed. The portable WP07 rule tests, headless startup, controlled-fault
and save recovery tests run on every PR and `master` push. See
[Windows host verification](../docs/build-bootstrap.md)
for the clean-checkout command and interactive Windows and D3D12 smoke options.

Run the portable rule tests by themselves with the pinned .NET SDK:

```powershell
dotnet run --project tests/WorldsYetUnseen.Gameplay.Tests -c Release
```

The test executable uses the BCL and the WYU gameplay project. It needs no
Groundwork feed or Windows desktop.
