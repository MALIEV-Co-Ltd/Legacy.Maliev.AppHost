# Local snapshot terminal validation

This gate proves that the local Aspire review stack consumes an authenticated production-derived
snapshot without crossing the deployment or cutover boundary. It binds the `MLVSNP02` manifest to
signed schema-v2 final migration evidence, the authoritative source commit, run identity, target
generation, restore identity, and independently reviewed baseline digests.

The repository baseline must name the exact 19 compiled repositories and pin each clean `main`
commit to the same `origin/main` commit. Dirty, detached, stale, or ahead repositories fail closed.
The snapshot must contain the exact 24 migrated databases; Hangfire is retired and is rejected.
Local runtime topology must contain those 24 migrated databases plus Auth.

The gate starts Aspire with fixtures disabled, waits for all 25 terminal jobs, and requires all 16
services to be healthy. Validation is non-mutating: only Aspire health checks and a read-only
PostgreSQL topology query are allowed. It never runs fixture seeding, login, account, quotation,
or other write flows, and it does not contact production endpoints or mutate GKE.

The resulting terminal evidence is written atomically and contains exact resource names and commit
or artifact digests only. It contains no PII, credentials, tokens, cookies, connection strings,
production row data, or row counts. `test-local-snapshot-verification-evidence.ps1` independently
rejects stale evidence, incomplete inventories, unsafe flags, old snapshot formats, and any
unreviewed repository state.

The repository baseline is a caller-owned JSON document with `schemaVersion: 1` and a
`repositories` array. Each entry contains only `name` and `commitSha`. Its raw-file SHA-256 must be
approved and passed separately. The terminal gate must be run only after Release builds and the
exact snapshot and signed migration-evidence artifacts are available locally.
