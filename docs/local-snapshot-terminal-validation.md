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

The gate starts Aspire with fixtures disabled, waits for all 25 terminal jobs, requires all 16
services to be healthy, and performs an actual loopback HTTP `GET /readiness` for every service.
Validation is non-mutating: readiness GETs and a read-only PostgreSQL topology query are allowed.
It never runs fixture seeding or write flows, and it does not contact production endpoints or
mutate GKE.

Authenticated list/detail checks cannot be generated before the production-derived identities are
restored. Therefore the gate requires a separate, owner-only redacted observation containing the
exact authenticated read-query IDs and `passed` status only. It is bound to the snapshot, source
commit, AppHost commit, and repository-baseline digest. Missing, stale, failed, or mismatched
authenticated-query evidence keeps the terminal result incomplete; health alone can never produce
a passed terminal result.

Authenticated-query evidence uses schema version 1 with exact root fields `snapshotId`,
`authoritativeSourceCommitSha`, `appHostCommit`, `repositoryBaselineSha256`, `completedAtUtc`, and
`queries`. Every query entry contains only `id` and `status`; request headers, cookies, tokens,
response bodies, identities, and counts are prohibited. The raw evidence-file SHA-256 is a separate
reviewed input.

The MLVSNP02 `manifestDigestSha256` is the authenticated semantic digest checked by snapshot
preflight. Terminal evidence records it separately from the SHA-256 of the physical `manifest.json`
file; these values are not interchangeable. Signed migration evidence binds the same snapshot ID
and authoritative source commit, while the reviewed invocation supplies both expected manifest
digests.

The resulting terminal evidence is written atomically and contains exact resource names and commit
or artifact digests only. It contains no PII, credentials, tokens, cookies, connection strings,
production row data, or row counts. `test-local-snapshot-verification-evidence.ps1` independently
rejects stale evidence, incomplete inventories, unsafe flags, old snapshot formats, and any
unreviewed repository state.

The repository baseline is a caller-owned JSON document with `schemaVersion: 1` and a
`repositories` array. Each entry contains only `name`, `commitSha`, and the reviewed `originUrl`.
Its raw-file SHA-256 must be approved and passed separately. The repository baseline,
authenticated-query evidence, terminal
evidence file, and terminal-evidence parent directory must be regular, owner-only, and free of link
or reparse-point traversal. Inputs are hashed and parsed through the same retained exclusive handle.
Output uses a unique create-only path and is never overwritten. A failed startup, probe, cleanup, or
preflight cannot emit `passed` evidence. The terminal gate must be run only after Release builds and
the exact snapshot and signed migration-evidence artifacts are available locally.
