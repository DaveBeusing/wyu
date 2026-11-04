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

The CI workflow validates the immutable candidate build record, fetches its
artifact from GitHub Actions, compares the pinned manifest hash, checks the
manifest's source commit against the producer run head and then calls
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

The Groundwork package is still held in an ordinary CI artifact with requested
90-day retention, not a durable release archive. Before expiry, promote this
exact package and its reviewed manifest to approved durable storage or deliberately
move WYU to a newly verified and retained package. Never rewrite NuGet contentHash
values. A PR-build workflow run is not a protected signed release attestation.

The current check does not establish actual Windows engine lifecycle behavior,
nor does it complete WYU L6 runtime composition. Recheck GitHub repository
access/permissions if cross-repository gh artifact download fails.
