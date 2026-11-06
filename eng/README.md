# Groundwork package handover

WYU independently versions its source at `0.1.0` and uses exact NuGet
references to the ten Groundwork modules. `NuGet.Config` maps `Groundwork.*`
to a verified local feed and the committed lockfile records the SDK-generated
package content hashes. NuGet.org supplies the renderer's transitive
`TerraFX.Interop.Windows` dependency at `10.0.26100.6`.

| Groundwork producer | Pinned value |
| --- | --- |
| Package version | `0.1.0-dev.42303559178.1` |
| Master commit | `5ab62bbbee7498a178b75a246804709a63238e07` |
| Windows CI run and attempt | [`42303559178.1`](https://github.com/DaveBeusing/Groundwork/actions/runs/42303559178/attempts/1) |
| Producer artifact ZIP SHA-256 | `62863269EBD0C46C4B44250A4072A1D135DB5996CA009FA8A320E54692477A1E` |
| Build manifest SHA-256 | `D82D42F2271A5A885E18937B7B0124854268EB3CDDFD1820FCBF84EBE50580E8` |
| Archive record SHA-256 | `E8AD1A170D3CD7FE93BD0F32AF22BB6E2794DF28BEBB6BDDF77980E850592C20` |

The [immutable GitHub release](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.42303559178.1-immutable)
contains the original ten CI packages, the build manifest, the archive
record, and the Apache-2.0 license. Keep the release available while any
consumer pins the version.

CI validates the producer run and immutable release metadata, checks the
archive asset hashes and commit, then invokes `Test-VerifiedGroundworkConsumer.ps1`.
The script checks every package hash, rejects missing, tampered, and
incompatible packages, then restores and builds with a fresh cache from an
external feed. Headless startup, controlled-fault and save recovery tests run on every
PR and `master` push. See [Windows host verification](../docs/build-bootstrap.md)
for the clean-checkout command and interactive Windows and D3D12 smoke options.
