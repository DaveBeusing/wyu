# Groundwork package handover

WYU independently versions its source at `0.1.0` and uses exact NuGet
references to the six Groundwork modules. `NuGet.Config` maps `Groundwork.*`
to a verified local feed and the committed lockfile records the SDK-generated
package content hashes.

| Groundwork producer | Pinned value |
| --- | --- |
| Package version | `0.1.0-dev.42137008200.1` |
| Master commit | `8da185b176961e365dc32261d8c80a672a01b7b9` |
| Windows CI run and attempt | [`42137008200.1`](https://github.com/DaveBeusing/Groundwork/actions/runs/42137008200) |
| Build manifest SHA-256 | `7981A019E4D8C4706E66D0C00B5E3E9150ABBA5DE77439DFC13C73FEC9AF2DB3` |
| Archive record SHA-256 | `293DEA0BA15F0895A7C37D8FD5D0B9D3B36234874839A72EE0AA42F4018D01A3` |

The [immutable GitHub release](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.42137008200.1-immutable)
contains the original six CI packages, the build manifest, the archive
record, and the Apache-2.0 license. Keep the release available while any
consumer pins the version.

CI validates the producer run and immutable release metadata, checks the
archive asset hashes and commit, then invokes `Test-VerifiedGroundworkConsumer.ps1`.
The script checks every package hash, rejects missing, tampered, and
incompatible packages, then restores and builds with a fresh cache from an
external feed. Headless and controlled-fault executable tests run on every
PR and `master` push. See [Windows host verification](../docs/build-bootstrap.md)
for the clean-checkout command and interactive Windows smoke option.
