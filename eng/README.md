# Groundwork package handover

WYU independently versions its source at `0.1.0` and uses exact NuGet
references to the ten Groundwork modules. `NuGet.Config` maps `Groundwork.*`
to a verified local feed and the committed lockfile records the SDK-generated
package content hashes. NuGet.org supplies the renderer's transitive
`TerraFX.Interop.Windows` dependency at `10.0.26100.6`.

| Groundwork producer | Pinned value |
| --- | --- |
| Package version | `0.1.0-dev.44495989322.1` |
| Master commit | `38262531b273bd1b9e9c257de35eb04159a1084c` |
| Windows CI run and attempt | [`44495989322.1`](https://github.com/DaveBeusing/Groundwork/actions/runs/44495989322/attempts/1) |
| Producer artifact ZIP SHA-256 | `E2A9541B73B00C94435C4FE9E0D9664DAAE0BC2C3EF1AF8C38DB941B0E4520F3` |
| Build manifest SHA-256 | `3D5970161DBFCA2B30A989545B5984FBDF0CE0CBB3A976900F1A73553AC6A15E` |
| Archive record SHA-256 | `D28F9D2BAD7C14928598D3906B912AE231E6A730ACEEEE9B0C254DCCF1D28F9C` |

The [immutable GitHub release](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.44495989322.1-immutable-complete)
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
