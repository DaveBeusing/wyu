# Groundwork package handover

WYU independently versions its source at `0.1.0` and uses exact NuGet
references to the ten Groundwork modules. `NuGet.Config` maps `Groundwork.*`
to a verified local feed and the committed lockfile records the SDK-generated
package content hashes. NuGet.org supplies the renderer's transitive
`TerraFX.Interop.Windows` dependency at `10.0.26100.6`.

| Groundwork producer | Pinned value |
| --- | --- |
| Package version | `0.1.0-dev.43301223359.1` |
| Master commit | `8d6b239f3bc78a61158d122be427b202342ae47a` |
| Windows CI run and attempt | [`43301223359.1`](https://github.com/DaveBeusing/Groundwork/actions/runs/43301223359/attempts/1) |
| Producer artifact ZIP SHA-256 | `FC8E76742B64A8FE02825BC94B13D951A2A14280EA8C416C80A3BDF486CABEFC` |
| Build manifest SHA-256 | `BC7931C80C74F910E2C715650062232B1A59F57EE956394E399B96DB7A46B51D` |
| Archive record SHA-256 | `58DC4595612E7AE33CFD33D8FF54713194AF8B56C031D61CDFF660048AC0201C` |

The [immutable GitHub release](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.43301223359.1-immutable)
contains the original ten CI packages, the build manifest, the archive
record, and the Apache-2.0 license. Keep the release available while any
consumer pins the version.

CI validates the producer run and immutable release metadata, checks the
archive asset hashes and commit, then invokes `Test-VerifiedGroundworkConsumer.ps1`.
The script checks every package hash, rejects missing, tampered, and
incompatible packages, then restores and builds with a fresh cache from an
external feed. The WP07 rule tests, WP11 reference planet tests, headless startup, controlled-fault
and save recovery tests run on every PR and `master` push. See
[Windows host verification](../docs/build-bootstrap.md)
for the clean-checkout command and interactive Windows and D3D12 smoke options.

To qualify the WP12 cave route on a Windows desktop with NVIDIA and Graphics
Tools, build the verified consumer and run the two-process smoke harness:

```powershell
pwsh -File eng/Test-CaveExploration.ps1
```

The harness creates its own temporary cave world, checks the mined checkpoint
in a fresh process and records native shutdown with the Debug Layer and DRED.

Run the gameplay tests by themselves with the pinned .NET SDK and verified feed:

```powershell
dotnet run --project tests/WorldsYetUnseen.Gameplay.Tests -c Release
```

The test executable uses the WYU gameplay project and verified Groundwork
packages. It runs without a Windows desktop.
