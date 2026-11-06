# Groundwork package handover

WYU independently versions its source at `0.1.0` and uses exact NuGet
references to the nine Groundwork modules. `NuGet.Config` maps `Groundwork.*`
to a verified local feed and the committed lockfile records the SDK-generated
package content hashes. NuGet.org supplies the renderer's transitive
`TerraFX.Interop.Windows` dependency at `10.0.26100.6`.

| Groundwork producer | Pinned value |
| --- | --- |
| Package version | `0.1.0-dev.42225951792.1` |
| Master commit | `d7645731c18693e4c8e42ebe05e794ea30b0454e` |
| Windows CI run and attempt | [`42225951792.1`](https://github.com/DaveBeusing/Groundwork/actions/runs/42225951792/attempts/1) |
| Build manifest SHA-256 | `15DBEE87CE4BC3936757676E365D93ECF3F252F7C5C255B2615CB25BFEB0E841` |
| Archive record SHA-256 | `FFE62598133A308E8561B9107FF65578B51D275947B5EADDAABD3FB0837513F6` |

The [immutable GitHub release](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.42225951792.1-immutable)
contains the original nine CI packages, the build manifest, the archive
record, and the Apache-2.0 license. Keep the release available while any
consumer pins the version.

CI validates the producer run and immutable release metadata, checks the
archive asset hashes and commit, then invokes `Test-VerifiedGroundworkConsumer.ps1`.
The script checks every package hash, rejects missing, tampered, and
incompatible packages, then restores and builds with a fresh cache from an
external feed. Headless and controlled-fault executable tests run on every
PR and `master` push. See [Windows host verification](../docs/build-bootstrap.md)
for the clean-checkout command and interactive Windows and D3D12 smoke options.
