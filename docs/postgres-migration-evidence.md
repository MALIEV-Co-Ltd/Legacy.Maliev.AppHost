# Signed PostgreSQL shadow-migration evidence v2

`verify-postgres-migration-evidence.ps1` is a read-only, fail-closed acceptance gate for a
SQL Server-to-PostgreSQL **shadow** migration receipt. It reads only a JSON receipt and a trusted
P-256 public key. It never connects to SQL Server, PostgreSQL, GKE, Cloud Storage, or Secret
Manager, and it cannot authorize deployment, cutover, or canonical database writes.

Version 2 deliberately does not compare source and target schema hashes. SQL Server and
PostgreSQL schemas are different representations. Instead, every migrated database records its
source schema hash, exact signed mapping-plan hash, resulting target schema hash, row/content
reconciliation, foreign-key reconciliation, and mapped sequence/identity reconciliation.

The receipt freezes the complete 27-database disposition inventory. Exactly 21 databases must
have `migrate` receipts. `Hangfire` and `Log` require immutable archive provenance;
`MachineLearning` and `MachineLearningData` remain excluded; `ContactRequest` and `LocationData`
remain on review hold. Missing, duplicate, renamed, unknown, or differently owned entries fail.

## Receipt shape

The root keys are exact and `schemaVersion` must be `2`:

```json
{
  "schemaVersion": 2,
  "source": {
    "system": "sqlserver",
    "snapshotId": "source-2026-08-29",
    "capturedAtUtc": "2026-08-29T00:05:00.0000000+00:00",
    "backup": {
      "uri": "gs://maliev.com/database/full/2026-08-29/",
      "manifestSha256": "<64 lower-case hex>",
      "databaseInventorySha256": "<64 lower-case hex>",
      "objectGeneration": "<immutable object generation identifier>",
      "immutable": true
    }
  },
  "mapping": {
    "schemaPlanVersion": "2.0",
    "planSha256": "<64 lower-case hex>",
    "sourceCommitSha": "<40 lower-case hex>",
    "runnerDigestSha256": "<64 lower-case hex>"
  },
  "target": {
    "system": "postgresql",
    "cluster": "legacy-postgres-main",
    "namespace": "maliev-legacy",
    "mode": "shadow",
    "generation": "<shadow generation>",
    "capturedAtUtc": "2026-08-29T00:30:00.0000000+00:00",
    "restoreId": "<restore identifier>"
  },
  "inventory": [
    { "name": "Customer", "owner": "Legacy.Maliev.CustomerService", "disposition": "migrate" }
  ],
  "archives": [
    {
      "name": "Hangfire", "disposition": "archive_only", "immutable": true,
      "backupArtifactSha256": "<64 lower-case hex>",
      "sourceSchemaSha256": "<64 lower-case hex>",
      "sourceContentSha256": "<64 lower-case hex>"
    }
  ],
  "databases": [
    {
      "name": "Customer",
      "sourceSchemaSha256": "<64 lower-case hex>",
      "mappingPlanSha256": "<same mapping.planSha256>",
      "targetSchemaSha256": "<64 lower-case hex; may differ from source>",
      "sourceRowCount": 123, "targetRowCount": 123,
      "sourceContentSha256": "<64 lower-case hex>",
      "targetContentSha256": "<same canonical content hash>",
      "foreignKeys": { "sourceCount": 5, "targetCount": 5, "orphanCount": 0 },
      "sequences": [{ "name": "customer_id", "sourceNextValue": 124, "targetNextValue": 124 }],
      "parity": "exact"
    }
  ],
  "parity": "exact",
  "constraints": {
    "productionDataWritesAllowed": false,
    "canonicalTargetMutationAllowed": false,
    "cutoverPercent": 0,
    "newNodePoolAllowed": false,
    "cloudSqlAllowed": false,
    "additionalInfrastructureCostAllowed": false
  },
  "attestation": {
    "algorithm": "ECDSA_P256_SHA256",
    "keyId": "<approved key id>",
    "payloadSha256": "<SHA-256 of canonical root without attestation>",
    "signatureBase64": "<P-256 signature over payload hash bytes>"
  }
}
```

The abbreviated arrays document field shape only. A real receipt includes the exact 27-entry
disposition inventory, both archive receipts, and all 21 migrated database receipts. Unknown
fields, sensitive field names, stale timestamps, untrusted keys, invalid signatures, non-shadow
targets, or any reconciliation drift are rejected. A database with no mapped sequence uses an
empty `sequences` array.

The signed payload is the compact UTF-8 JSON root with `attestation` removed, every object key
sorted by ordinal code-point order, array order preserved, and JSON strings emitted without HTML
escaping. `payloadSha256` is the lower-case SHA-256 of those bytes. `signatureBase64` is the P-256
ECDSA signature over the 32 hash bytes. Producers must use those rules exactly; the verifier never
normalizes or signs evidence on their behalf.

## Run the local acceptance gate

```powershell
pwsh ./scripts/verify-postgres-migration-evidence.ps1 `
  -EvidencePath ./.evidence/postgres-shadow-2026-08-29.json `
  -ExpectedDatabase Country,Currency,Customer,CustomerIdentity,DataProtectionKeys,DataProtectionKeysEmployee,Employee,EmployeeIdentity,Invoice,JobOffers,Material,Message,Order,OrderStatus,Payment,PurchaseOrder,Quotation,QuotationRequest,Receipt,Supplier,Upload `
  -RequiredAsOfUtc 2026-08-29T00:00:00Z `
  -TrustedPublicKeyPath C:/trusted/migration-review-public.pem `
  -ExpectedAttestationKeyId migration-review-2026-08
```

Keep real receipts and public keys outside the repository. Never store private attestation keys,
credentials, connection strings, tokens, or raw production data in the receipt or repository. A
pass is Aspire review evidence only; explicit owner approval remains required before deployment or
cutover.
