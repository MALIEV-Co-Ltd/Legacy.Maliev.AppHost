# Local snapshot terminal validation

This gate proves that the local Aspire review stack consumes an authenticated production-derived
snapshot without crossing the deployment or cutover boundary. It binds the `MLVSNP02` manifest to
signed schema-v2 final migration evidence, the authoritative source commit, run identity, target
generation, restore identity, and independently reviewed baseline digests.

The repository baseline must name the exact 19 compiled repositories and pin each clean `main`
commit to the same `origin/main` commit and reviewed public `origin` URL. Dirty, detached, stale,
ahead, or unexpected-origin repositories fail closed.
The snapshot must contain the exact 24 migrated databases; Hangfire is retired and is rejected.
Local runtime topology must contain those 24 migrated databases plus Auth.

The gate performs a clean non-incremental Release build after checking all pinned repositories,
starts Aspire with fixtures disabled, waits for all 25 terminal jobs, requires all 16 services to
be healthy, and performs an actual loopback HTTP GET against each service-specific readiness path
frozen in `contracts/local-snapshot-review-routes.json`. It then obtains an in-memory short-lived
`legacy-intranet` service token and executes seven authenticated read-only list queries itself.
Only query IDs and `passed` status are retained; request headers, tokens, response bodies,
identities, counts, and cookies are never recorded. Health alone can never produce a passed result.
Validation otherwise uses only a read-only PostgreSQL topology query. It never runs fixture seeding
or write flows: the entire review is non-mutating, does not contact production endpoints, and does
not mutate GKE.

The MLVSNP02 `manifestDigestSha256` is the authenticated semantic digest checked by snapshot
preflight. Terminal evidence records it separately from the SHA-256 of the physical `manifest.json`
file; these values are not interchangeable. Signed migration evidence binds the same snapshot ID
and authoritative source commit, while the reviewed invocation supplies both expected manifest
digests.

The resulting terminal evidence is validated as an owner-only candidate and only then atomically
published create-only. It contains exact resource names and commit
or artifact digests only. It contains no PII, credentials, tokens, cookies, connection strings,
production row data, or row counts. `test-local-snapshot-verification-evidence.ps1` independently
rejects stale evidence, incomplete inventories, unsafe flags, old snapshot formats, and any
unreviewed repository state.

The repository baseline is a caller-owned JSON document with `schemaVersion: 1` and a
`repositories` array. Each entry contains only `name`, `commitSha`, and the reviewed `originUrl`.
Its raw-file SHA-256 must be approved and passed separately. The repository baseline, snapshot
directory, snapshot key, manifest, every encrypted archive, signed migration evidence, terminal
evidence file, and terminal-evidence parent directory must be owner-only and free of link or
reparse-point traversal. Snapshot artifacts remain open through restore and validation with write
and delete sharing denied, so the restored bytes and recorded digests cannot diverge.
Output uses a unique create-only path and is never overwritten. A failed startup, probe, cleanup, or
preflight cannot emit `passed` evidence. Cleanup must prove the runner, AppHost, DCP, and run-owned
Docker resources are gone before publication.
