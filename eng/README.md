# Groundwork package handover

WYU independently versions its source at `0.1.0` and uses exact NuGet
references to the nine Groundwork modules. `NuGet.Config` maps `Groundwork.*`
to a verified local feed and the committed lockfile records the SDK-generated
package content hashes. NuGet.org supplies the renderer's transitive
`TerraFX.Interop.Windows` dependency at `10.0.26100.6`.

| Groundwork producer | Pinned value |
| --- | --- |
| Package version | `0.1.0-dev.42274145071.1` |
| Master commit | `49a55eb3447765bca58c6552bf3eebd26f5310ef` |
| Windows CI run and attempt | [`42274145071.1`](https://github.com/DaveBeusing/Groundwork/actions/runs/42274145071/attempts/1) |
| Producer artifact ZIP SHA-256 | `222DFA1ECE6659EB163886F9D0C8796C70E3AF366ED4090AD15C30A0480D42AF` |
| Build manifest SHA-256 | `A1F13142A00EDFC29F42AE72A8CA217C598B024C86DA5A83EDD7853AD811EA39` |
| Archive record SHA-256 | `B5C098E4D7E7207ADC476F0033A8CE424594BDF46D372ADB6B9823052C4801CF` |

The [immutable GitHub release](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.42274145071.1-immutable)
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
