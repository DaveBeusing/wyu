# Groundwork package handover

WYU independently versions its source at `0.1.0` and uses exact NuGet
references to the ten Groundwork modules. `NuGet.Config` maps `Groundwork.*`
to a verified local feed and the committed lockfile records the SDK-generated
package content hashes. NuGet.org supplies the renderer's transitive
`TerraFX.Interop.Windows` dependency at `10.0.26100.6`.

| Groundwork producer | Pinned value |
| --- | --- |
| Package version | `0.1.0-dev.42411077659.1` |
| Master commit | `141a62ab6b22246d21cc48d74f45397cf902c910` |
| Windows CI run and attempt | [`42411077659.1`](https://github.com/DaveBeusing/Groundwork/actions/runs/42411077659/attempts/1) |
| Producer artifact ZIP SHA-256 | `895747DD801E1532418159AFFF609A61ECEBE5F6E6A0E220E022CF4DA78BF34A` |
| Build manifest SHA-256 | `EE49C1228ED2D7D1DDD579F851119DEB8949F70AFD8579E05CC5084F0F283623` |
| Archive record SHA-256 | `FEFD25854BE28C5B72ABDCB2A8711B0D7A7EE50953CEE714388C2EC22E27B1E5` |

The [immutable GitHub release](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.42411077659.1-immutable)
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
