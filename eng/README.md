# Groundwork package handover

WYU independently versions its source at `0.1.0` and uses exact NuGet
references to the eight Groundwork modules. `NuGet.Config` maps `Groundwork.*`
to a verified local feed and the committed lockfile records the SDK-generated
package content hashes. NuGet.org supplies the renderer's transitive
`TerraFX.Interop.Windows` dependency at `10.0.26100.6`.

| Groundwork producer | Pinned value |
| --- | --- |
| Package version | `0.1.0-dev.42210884016.1` |
| Master commit | `7eaefbfd2bba96618617c9dcfeb6dbcd6a0de828` |
| Windows CI run and attempt | [`42210884016.1`](https://github.com/DaveBeusing/Groundwork/actions/runs/42210884016/attempts/1) |
| Build manifest SHA-256 | `1A4899932E28C69391BB6C3FA4DBEBDD9D597CFA8FD2597E027355DFE34EF310` |
| Archive record SHA-256 | `5E653B394673FCB7194F2D2221BAB187AFD9E2D81BF0B71182998193CEF6B1CD` |

The [immutable GitHub release](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.42210884016.1-immutable)
contains the original eight CI packages, the build manifest, the archive
record, and the Apache-2.0 license. Keep the release available while any
consumer pins the version.

CI validates the producer run and immutable release metadata, checks the
archive asset hashes and commit, then invokes `Test-VerifiedGroundworkConsumer.ps1`.
The script checks every package hash, rejects missing, tampered, and
incompatible packages, then restores and builds with a fresh cache from an
external feed. Headless and controlled-fault executable tests run on every
PR and `master` push. See [Windows host verification](../docs/build-bootstrap.md)
for the clean-checkout command and interactive Windows and D3D12 smoke options.
