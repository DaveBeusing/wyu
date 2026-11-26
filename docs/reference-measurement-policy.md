# Reference measurement policy

Effective 27 November 2026 upon adoption. See the [Groundwork decision](https://github.com/DaveBeusing/Groundwork/blob/master/docs/adr/ADR-0011-reference-measurement-policy.md).

| Decision | Required evidence |
| --- | --- |
| Merge | Follow repository policy for CI, independent reviews, functionality and safety on the final integration candidate. Reference-machine measurement is optional when the merge preserves the applicable release and production activation gates. |
| Preview | Follow the applicable CI, review, functional and safety checks. Clearly label the build as an unqualified preview, report missing or failed measurements, and keep any feature awaiting production qualification opt-in. Reference-machine measurement is optional for this decision. |
| Release or production/default activation | Require all applicable quantitative performance, memory, pressure and display gates, installed-route checks, owner visual approval where specified, and verified package provenance. Carry out Groundwork's master-push build and immutable archive workflow before pinning the exact consumer packages, lockfiles and provenance digests. |

For each candidate, record which measurements ran, their actual result, the tested source and package identities, hardware and any limits on applicability. Keep failed and cancelled attempts and label unmeasured gates as unmeasured. Reference-machine measurements remain the established protocol. Replacement evidence requires a documented protocol that preserves the applicable controls, routes, endpoints and thresholds, and an explicit decision accepting representative hardware and measurement suitability.
