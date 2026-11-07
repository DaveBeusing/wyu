# Groundwork package handover

WYU independently versions its source at `0.1.0` and uses exact NuGet
references to the ten Groundwork modules. `NuGet.Config` maps `Groundwork.*`
to a verified local feed and the committed lockfile records the SDK-generated
package content hashes. NuGet.org supplies the renderer's transitive
`TerraFX.Interop.Windows` dependency at `10.0.26100.6`.

| Groundwork producer | Pinned value |
| --- | --- |
| Package version | `0.1.0-dev.42420693798.1` |
| Master commit | `565dd78c580419d7f0678337f443c894256cc1af` |
| Windows CI run and attempt | [`42420693798.1`](https://github.com/DaveBeusing/Groundwork/actions/runs/42420693798/attempts/1) |
| Producer artifact ZIP SHA-256 | `4869B157251560E0587EF0644BDC2586059F9153A398C434A757959CCEA972B8` |
| Build manifest SHA-256 | `1F313B6C086B6C68A738210A4A2D8378941EC43BB217CE9B7357170F67DC3FBD` |
| Archive record SHA-256 | `D59DB1C8CC4BB1FBB22ECF106557D41008F94CCC4699BEA17D6ACBBA6B3A8A11` |

The [immutable GitHub release](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.42420693798.1-immutable)
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
