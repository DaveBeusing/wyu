# Groundwork package handover

WYU independently versions its source at `0.1.0` and uses exact NuGet
references to the ten Groundwork modules. `NuGet.Config` maps `Groundwork.*`
to a verified local feed and the committed lockfile records the SDK-generated
package content hashes. NuGet.org supplies the renderer's transitive
`TerraFX.Interop.Windows` dependency at `10.0.26100.6`.

| Groundwork producer | Pinned value |
| --- | --- |
| Package version | `0.1.0-dev.42367247347.1` |
| Master commit | `5f3f7bd5c7e2e59cef898d4bea8842afa123e905` |
| Windows CI run and attempt | [`42367247347.1`](https://github.com/DaveBeusing/Groundwork/actions/runs/42367247347/attempts/1) |
| Producer artifact ZIP SHA-256 | `BA7CD711E192B10A43DDEA383B7344CF74D9CCFB761CCCDFA2854DD2CB97FFA3` |
| Build manifest SHA-256 | `0BBD6B3C94BF6EAB3E7D9D798374D50EA962BA1D9585BE9670F29DC8DA3D26A5` |
| Archive record SHA-256 | `68CC7736814E8E29058FCDA889632609DFB076B0D2CEB74C29812F82C28DFB76` |

The [immutable GitHub release](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.42367247347.1-immutable)
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
