# Groundwork package handover

WYU independently versions its source at `0.1.0` and uses exact NuGet
references to the ten Groundwork modules. `NuGet.Config` maps `Groundwork.*`
to a verified local feed and the committed lockfile records the SDK-generated
package content hashes. NuGet.org supplies the renderer's transitive
`TerraFX.Interop.Windows` dependency at `10.0.26100.6`.

| Groundwork producer | Pinned value |
| --- | --- |
| Package version | `0.1.0-dev.43527092534.1` |
| Master commit | `20871f43a809aa81c6347e4ab93b163fca9df1df` |
| Windows CI run and attempt | [`43527092534.1`](https://github.com/DaveBeusing/Groundwork/actions/runs/43527092534/attempts/1) |
| Producer artifact ZIP SHA-256 | `641DD658DA19A344C6BE5F46BDC8D18DB6DAB359075689EA5AD3C17227D7FEDF` |
| Build manifest SHA-256 | `683743030047CFA68CD944E8C2C466EA5EE958D2EDE4079FD9F547B1AAE5D232` |
| Archive record SHA-256 | `6E97BA96AFD9A1E9882758304A505DA02E03D8951F296108FD0163C3FBB95FB1` |

The [immutable GitHub release](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.43527092534.1-immutable)
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
