# Groundwork package handover

WYU independently versions its source at `0.1.0` and uses exact NuGet
references to the nine Groundwork modules. `NuGet.Config` maps `Groundwork.*`
to a verified local feed and the committed lockfile records the SDK-generated
package content hashes. NuGet.org supplies the renderer's transitive
`TerraFX.Interop.Windows` dependency at `10.0.26100.6`.

| Groundwork producer | Pinned value |
| --- | --- |
| Package version | `0.1.0-dev.42292324031.1` |
| Master commit | `269ab9e1128c15f5764f0dded499e8e02db4110c` |
| Windows CI run and attempt | [`42292324031.1`](https://github.com/DaveBeusing/Groundwork/actions/runs/42292324031/attempts/1) |
| Producer artifact ZIP SHA-256 | `24BC64FE2EF66337C910A83846B2BA1C834901F32833E88432E6EDDEF71A7B57` |
| Build manifest SHA-256 | `4294F71A1B42B9D6ED132B203D6553B673AB8B62C23FB23E37A16B77E03EA43E` |
| Archive record SHA-256 | `22957F7AA16EA11B4C3060E9DFEEBE63FC510B6DE6D0FB906DA1DF89C5515F48` |

The [immutable GitHub release](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.42292324031.1-immutable)
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
