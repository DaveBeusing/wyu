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

The exact package is now preserved in the public **immutable** GitHub prerelease
[archive-groundwork-0.1.0-dev.41920122570.1-immutable](https://github.com/DaveBeusing/Groundwork/releases/tag/archive-groundwork-0.1.0-dev.41920122570.1-immutable).
The new archive has nine assets: the original six unmodified nupkg files,
the original producer `manifest.json`, an archive-specific `archive-record.json`,
and the Apache-2.0 `LICENSE`. The original mutable archive is retained for
historical provenance, but is no longer WYU's CI dependency.

GitHub release metadata was independently checked and reports `immutable: true`.
WYU CI **requires** that field and rejects a mutable replacement release.
The workflow also verifies pinned release identity, nine asset names,
the manifest SHA-256, the new archive-record SHA-256
(`8D6F79929EA159A146DB537B2344C561A1A1FAF0CD07376F291B8AAB121CB92E`),
the LICENSE digest, producer run and source provenance, and all six package
hashes. Only after successful verification does it restore from an external,
isolated package feed with a fresh cache, `--locked-mode`, and Release build.

The Groundwork `PackageReference` remains exactly
`[0.1.0-dev.41920122570.1]`, and the NuGet-generated `packages.lock.json`
remains unchanged. Release immutability protects the published archive assets
against normal modification or replacement within GitHub's supported controls;
it is not an external signed software supply-chain attestation.
The archived nupkg bytes were produced before the later
`PackageLicenseExpression=Apache-2.0` setting; the archive includes LICENSE
separately.

The current check does not establish actual Windows engine lifecycle behavior,
nor does it complete WYU L6 runtime composition. Recheck GitHub repository
access/permissions if cross-repository gh artifact download fails.
