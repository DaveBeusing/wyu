# Groundwork package handover

WYU independently versions its source at `0.1.0` and uses exact NuGet
references to the ten Groundwork modules. `NuGet.Config` maps `Groundwork.*`
to a verified local feed and the committed lockfile records the SDK-generated
package content hashes. NuGet.org supplies the renderer's transitive
`TerraFX.Interop.Windows` dependency at `10.0.26100.6`.

| Groundwork producer | Pinned value |
| --- | --- |
| Package version | `0.1.0-dev.43032545158.1` |
| Master commit | `3578bcc7143e33c9450f1eeb7e5dc586a0828b25` |
| Windows CI run and attempt | [`43032545158.1`](https://github.com/DaveBeusing/Groundwork/actions/runs/43032545158/attempts/1) |
| Producer artifact ZIP SHA-256 | `F2E6EA7A422802B8BCD33DFAB5E7C8F66FCA81C66E9B9496F4C5A2ABDD69163A` |
| Build manifest SHA-256 | `564B16515EA2312ED841FF4D3FBCC57AD7BD950D5B145CBC8FA787A49CB83E5C` |
| Archive record SHA-256 | `4806B1E512D433F28DEAA9511BBB38C67AA02A7D030461CB2947ED5D1C2DD00A` |

The [immutable GitHub release](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.43032545158.1-immutable)
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

Run the gameplay tests by themselves with the pinned .NET SDK and verified feed:

```powershell
dotnet run --project tests/WorldsYetUnseen.Gameplay.Tests -c Release
```

The test executable uses the WYU gameplay project and verified Groundwork
packages. It runs without a Windows desktop.
