# Groundwork package handover

WYU independently versions its source at `0.1.0` and uses exact NuGet
references to the ten Groundwork modules. `NuGet.Config` maps `Groundwork.*`
to a verified local feed and the committed lockfile records the SDK-generated
package content hashes. NuGet.org supplies the renderer's transitive
`TerraFX.Interop.Windows` dependency at `10.0.26100.6`.

| Groundwork producer | Pinned value |
| --- | --- |
| Package version | `0.1.0-dev.43118949133.1` |
| Master commit | `bf56de9347c2a99159595812968a0141bf0e8708` |
| Windows CI run and attempt | [`43118949133.1`](https://github.com/DaveBeusing/Groundwork/actions/runs/43118949133/attempts/1) |
| Producer artifact ZIP SHA-256 | `30AF39204EB06E1A95E7E7300640747999602DA7B1B6212B80192928B2F8AC53` |
| Build manifest SHA-256 | `2406F20155B6CD05EA3B268DCCF256B95385E716BE7103A211850291FBB1B61D` |
| Archive record SHA-256 | `8AC2E84C3ECC0204AFEBEA4A336E7551874C818C2313C68D369D2A669AD597C4` |

The [immutable GitHub release](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.43118949133.1-immutable)
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
