# WP01-L6 — verified Groundwork CI consumer (draft)

This change adds a Windows CI acceptance gate to WYU without linking any
Groundwork source project or requiring a shared worktree.

## Candidate producer record

- Repository: DaveBeusing/Groundwork
- GitHub Actions run: 41920122570, attempt 1, conclusion success
- Workflow: Groundwork Windows CI
- Producer PR head: 6930d66d383fb26427cf927115efe5cf9fc9b74b
- Candidate version: 0.1.0-dev.41920122570.1
- Manifest SHA-256: E07D23C78F922E8EBB193E03E5B7F5C2A8FDD4EF50C081B914B183D4017296F9

The CI workflow checks the pinned producer build and separately obtains its
durable GitHub Release archive. It verifies release asset metadata and hashes,
compares the pinned manifest digest and source commit, then calls
eng/Test-VerifiedGroundworkConsumer.ps1.

The PowerShell script verifies all six artifact package names, sizes and
SHA-256 digests, then compares the **committed** WYU PackageReference and NuGet
lock against the **exact** candidate version. After these checks, it copies
only Groundwork.Core into a temporary external feed, clears the NuGet cache,
runs locked restore and a Windows Release build.

## Verified CI package pin and reproducibility

The repository owner approved pin migration to
`0.1.0-dev.41920122570.1`. The actual NuGet lock file was regenerated on
a Windows runner from the downloaded, SHA-256-checked package bytes and then
committed. The temporary lock-generation CI step was removed; subsequent runs
only verify the committed immutable pin and lock.

[WYU Windows CI run #41922561240](https://github.com/DaveBeusing/wyu/actions/runs/41922561240)
completed successfully after download, manifest verification and clean-cache
locked restore/build. Its success proves CI consumption of this particular
candidate, not runtime/game functionality.

The exact package is now archived as GitHub prerelease
[archive-groundwork-0.1.0-dev.41920122570.1](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.41920122570.1).
Nine uploaded assets include six original immutable-version nupkg byte streams,
the producer `manifest.json`, `archive-record.json` and Apache-2.0 `LICENSE`.
The cross-repository CI workflow consumes these **release assets** instead of
depending on the expiring original GitHub Actions artifact. It checks the
selected release identity, all nine asset names, independently pinned SHA-256
values for the manifest, archive record and license, source provenance, and
every package SHA-256 against the trusted manifest before a fresh-cache locked
restore and Windows build. A missing or altered asset fails closed.

**Remaining hardening:** The GitHub release API currently reports
`immutable: false`. Enabling GitHub Release immutability/administrative
protections is still required for full preservation controls. Until then,
the pinned independent hashes detect changed package bytes but do not prevent
an administrator from deleting or replacing assets. This is a public
prerelease archive, not a signed protected production supply-chain attestation.
The original CI-produced nupkg bytes predate the `PackageLicenseExpression`
setting added after the build; the archive provides LICENSE separately.

The current check does not establish actual Windows engine lifecycle behavior,
nor does it complete WYU L6 runtime composition. Recheck GitHub repository
access/permissions if cross-repository gh artifact download fails.
