# Signed PostgreSQL shadow-migration evidence v2

`verify-postgres-migration-evidence.ps1` is a fail-closed local acceptance gate for a SQL
Server-to-PostgreSQL **shadow** migration receipt. It reads a signed JSON receipt, a trusted
P-256 public key, and an independently owner-approved baseline whose raw file hash is supplied on
the command line. It then atomically records the run/evidence/lease IDs in a local consumption ledger. It
never connects to SQL Server, PostgreSQL, GKE, Cloud Storage, or Secret Manager and cannot
authorize deployment, cutover, or canonical database writes.

## What the receipt proves

SQL Server and PostgreSQL schemas are different representations, so their schema hashes are not
expected to match. Every database instead binds its source schema hash and resulting target schema
hash to one signed mapping-plan hash. The signed plan explicitly lists every expected table,
column, approved aggregate, content-hash batch, foreign key, and sequence. The plan hash, source
commit, and exact table/foreign-key/sequence inventories must also equal the independently approved
baseline. Empty relationship or sequence arrays are accepted only when that external baseline
explicitly expects none.

For all 24 migrated databases the receipt must reconcile:

- database and table row counts;
- per-column null counts;
- each named, pre-approved aggregate as a canonical value hash;
- contiguous batches, their row counts, inventory hash, and canonical content hash;
- every planned foreign key, relationship count, and zero target orphans; and
- every planned sequence or identity next value.

Database totals must equal the sum of all planned table and batch receipts. Missing, extra,
duplicated, or renamed evidence fails. The complete 27-database disposition inventory is also
signed: 24 migrate, including `ContactRequest`, `LocationData`, and `Log`; `Hangfire` is excluded
under its retirement decision, and `MachineLearning` and `MachineLearningData` are excluded under
the retired PredictionService decision. Excluded databases must never appear in migrated database
evidence.

## Signed execution and replay boundary

The exact root contains `schemaVersion`, `source`, `mapping`, `target`, `execution`, `inventory`,
`archives`, `databases`, `parity`, `constraints`, and `attestation`.

`execution` contains D-format GUIDs for `runId`, `evidenceId`, and `leaseId`; `issuedAtUtc`
and `expiresAtUtc`; lease acquisition/expiry timestamps; `targetGeneration`; `restoreId`; and
`state=completed`. The issue-to-expiry window cannot exceed one hour. Both the evidence and lease
must still be valid when checked. Run, generation, and restore values must match the independently
supplied expected values and the signed target.

Before authorization, uniqueness checks, and ledger naming, every parsed GUID is normalized to its
lower-case D representation. After every cryptographic and reconciliation check passes, the verifier creates
`run-<runId>`, `evidence-<evidenceId>`, and `lease-<leaseId>` directories under
`-ConsumptionLedgerPath` as one rollback-safe operation. Reusing any identity fails, including lease
reuse by a differently signed receipt. The ledger must be durable for the complete review/release period and must not be cleared
to make a receipt pass again. The ledger contains identifiers only, never credentials or data.

## Mapping and database receipt shape

The external baseline has exact root keys `schemaVersion=2`, `sourceCommitSha`, `planSha256`, and
`databases`. Each database freezes its name, the explicit foreign-key/sequence name arrays, and a
per-table object containing the exact column and approved-aggregate inventories, expected batch
count, batch inventory hash, and recomputed `tablePlanSha256`. The baseline file itself is accepted only when its raw
SHA-256 equals `-ExpectedApprovedBaselineSha256`. Inventory hashing sorts names ordinally, joins
them with a single LF byte, and hashes the UTF-8 bytes; the empty inventory is SHA-256 of zero
bytes. Both baseline and receipt inventories are recomputed before comparison, preventing an
arbitrary self-attested hash or planned-empty omission from becoming its own authority. A table-plan
hash is SHA-256 over UTF-8 LF-separated `name`, column-inventory hash, aggregate-inventory hash,
expected batch count, and batch inventory hash fields. The verifier recomputes it for the baseline
and for the signed receipt before comparing the exact nested values.

An approved baseline database therefore contains table entries shaped like this:

```json
{
  "name": "dbo.customers",
  "columns": ["id", "email"],
  "approvedAggregates": ["id_range"],
  "expectedBatchCount": 2,
  "batchInventorySha256": "<64 lower-case hex>",
  "tablePlanSha256": "<recomputed 64 lower-case hex>"
}
```

Each entry in `mapping.databases` has this form:

```json
{
  "name": "Customer",
  "tableInventorySha256": "<64 lower-case hex>",
  "foreignKeyInventorySha256": "<64 lower-case hex>",
  "sequenceInventorySha256": "<64 lower-case hex>",
  "expectedTableCount": 1,
  "expectedForeignKeyCount": 1,
  "expectedSequenceCount": 1,
  "tables": [{
    "name": "dbo.customers",
    "columns": ["id", "email"],
    "approvedAggregates": ["id_range"],
    "expectedColumnCount": 2,
    "expectedAggregateCount": 1,
    "expectedBatchCount": 2,
    "batchInventorySha256": "<64 lower-case hex>"
  }],
  "foreignKeys": ["fk_customer_company"],
  "sequences": ["customer_id"]
}
```

The matching `databases` entry contains the three identical inventory hashes plus exhaustive
receipts:

```json
{
  "name": "Customer",
  "sourceSchemaSha256": "<64 lower-case hex>",
  "mappingPlanSha256": "<mapping.planSha256>",
  "targetSchemaSha256": "<64 lower-case hex; may differ from source>",
  "sourceRowCount": 123,
  "targetRowCount": 123,
  "sourceContentSha256": "<64 lower-case hex>",
  "targetContentSha256": "<same canonical hash>",
  "tableInventorySha256": "<signed plan value>",
  "foreignKeyInventorySha256": "<signed plan value>",
  "sequenceInventorySha256": "<signed plan value>",
  "tableCount": 1,
  "foreignKeyCount": 1,
  "sequenceCount": 1,
  "tables": [{
    "name": "dbo.customers",
    "sourceRowCount": 123,
    "targetRowCount": 123,
    "columnCount": 2,
    "aggregateCount": 1,
    "batchCount": 1,
    "columns": [{"name":"email","sourceNullCount":2,"targetNullCount":2}],
    "aggregates": [{
      "name": "id_range",
      "sourceValueSha256": "<64 lower-case hex>",
      "targetValueSha256": "<same hash>"
    }],
    "batchInventorySha256": "<signed plan value>",
    "batches": [{
      "ordinal": 0,
      "sourceRowCount": 123,
      "targetRowCount": 123,
      "sourceContentSha256": "<64 lower-case hex>",
      "targetContentSha256": "<same hash>"
    }],
    "parity": "exact"
  }],
  "foreignKeys": [{
    "name": "fk_customer_company",
    "sourceRelationshipCount": 120,
    "targetRelationshipCount": 120,
    "orphanCount": 0
  }],
  "sequences": [{"name":"customer_id","sourceNextValue":124,"targetNextValue":124}],
  "parity": "exact"
}
```

The source backup includes its credential-free `gs://` URI, manifest and database-inventory
hashes, immutable object generation, and `immutable=true`. `Log` remains covered by signed migrated
schema and content evidence. Constraints remain zero
cutover, no canonical/production writes, no new node pool, no Cloud SQL, and no added cost.

## Attestation canonicalization

The signed payload is compact UTF-8 JSON with root `attestation` removed, all object keys sorted by
ordinal code-point order, array order preserved, and strings emitted without HTML escaping.
`payloadSha256` is the lower-case SHA-256 of those bytes. `signatureBase64` is the P-256 ECDSA
signature over the 32 hash bytes. Producers must use those rules exactly.

## Run the local gate

```powershell
pwsh ./scripts/verify-postgres-migration-evidence.ps1 `
  -EvidencePath C:/review/postgres-shadow.json `
  -ExpectedDatabase ContactRequest,Country,Currency,Customer,CustomerIdentity,DataProtectionKeys,DataProtectionKeysEmployee,Employee,EmployeeIdentity,Invoice,JobOffers,LocationData,Log,Material,Message,Order,OrderStatus,Payment,PurchaseOrder,Quotation,QuotationRequest,Receipt,Supplier,Upload `
  -RequiredAsOfUtc 2026-08-29T00:00:00Z `
  -TrustedPublicKeyPath C:/review/migration-review-public.pem `
  -ExpectedAttestationKeyId migration-review-2026-08 `
  -ApprovedBaselinePath C:/review/owner-approved-migration-baseline.json `
  -ExpectedApprovedBaselineSha256 <owner-recorded-64-lower-case-hex> `
  -ConsumptionLedgerPath C:/review/consumed `
  -ExpectedRunId 11111111-1111-4111-8111-111111111111 `
  -ExpectedTargetGeneration shadow-generation-1 `
  -ExpectedRestoreId restore-current
```

Keep production-derived receipts, the public key, and the consumption ledger outside Git. Never
store private keys, credentials, connection strings, tokens, or raw production data in evidence.
A pass is Aspire review evidence only; explicit owner approval remains required before deployment
or cutover.
