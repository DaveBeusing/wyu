# Groundwork package handover

WYU independently versions its source at `0.1.0` and uses exact NuGet
references to the ten Groundwork modules. `NuGet.Config` maps `Groundwork.*`
to a verified local feed and the committed lockfile records the SDK-generated
package content hashes. NuGet.org supplies the renderer's transitive
`TerraFX.Interop.Windows` dependency at `10.0.26100.6`.

| Groundwork producer | Pinned value |
| --- | --- |
| Package version | `0.1.0-dev.43332217203.1` |
| Master commit | `2d86e96db95e855c190b2cebbf0b1f4ab2c70a53` |
| Windows CI run and attempt | [`43332217203.1`](https://github.com/DaveBeusing/Groundwork/actions/runs/43332217203/attempts/1) |
| Producer artifact ZIP SHA-256 | `085C6BB7F1D76F0801E07E7F5C175C7C6D243A1BE7FEC9DB3E2A8AB5DA638B25` |
| Build manifest SHA-256 | `078C3925FCE45E0B5710246A3BC54440A0702B7BEAD7CD7462FBEEB2B9C92E6C` |
| Archive record SHA-256 | `40E67D519C49CD08F0CB8D8AB0C86AE0346350950D8A032C274412696BE94944` |

The [immutable GitHub release](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.43332217203.1-immutable-complete)
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
