# Source-backed PostgreSQL migration evidence

`verify-postgres-migration-evidence.ps1` is a read-only, fail-closed validator for the SQL
Server-to-PostgreSQL parity receipt required before a legacy migration runner may be
authorized against a copied database. It does not connect to SQL Server, PostgreSQL, GKE,
Cloud Storage, or Secret Manager and it never changes data.

The evidence file must contain exactly these fields:

```json
{
  "schemaVersion": 1,
  "source": {
    "system": "sqlserver",
    "snapshotId": "approved-source-snapshot-id",
    "capturedAtUtc": "2026-08-07T00:00:00.0000000+00:00",
    "backupUri": "gs://approved-bucket/approved-backup"
  },
  "target": {
    "system": "postgresql",
    "cluster": "legacy-postgres-main",
    "namespace": "maliev-legacy",
    "capturedAtUtc": "2026-08-07T00:30:00.0000000+00:00",
    "restoreId": "approved-restore-id"
  },
  "databases": [
    {
      "name": "Customer",
      "sourceRowCount": 123,
      "targetRowCount": 123,
      "sourceSchemaSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      "targetSchemaSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      "sourceDataSha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
      "targetDataSha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
      "parity": "exact"
    }
  ],
  "parity": "exact",
  "constraints": {
    "productionDataWritesAllowed": false,
    "cutoverPercent": 0,
    "newNodePoolAllowed": false,
    "cloudSqlAllowed": false,
    "additionalInfrastructureCostAllowed": false
  }
}
```

Run it with the complete database inventory from `LegacyTopology.DatabaseNames` (and any
explicitly approved local-only identity database) and the owner-approved source cutoff:

```powershell
pwsh ./scripts/verify-postgres-migration-evidence.ps1 `
  -EvidencePath ./.evidence/postgres-migration-2026-08-07.json `
  -ExpectedDatabase Country,Currency,Customer,CustomerIdentity,DataProtectionKeys,DataProtectionKeysEmployee,Employee,EmployeeIdentity,Invoice,JobOffers,Material,Message,Order,OrderStatus,Payment,PurchaseOrder,Quotation,QuotationRequest,Receipt,Supplier,Upload `
  -RequiredAsOfUtc 2026-08-07T00:00:00Z
```

The validator rejects stale or future timestamps, target restores that predate the source,
missing/extra/duplicate databases, negative or mismatched row counts, schema/data checksum
drift, non-exact parity, sensitive fields, and any production-write or infrastructure-cost
flag. A passing result is evidence only; it does not authorize cutover or production
deployment. No evidence file containing credentials, tokens, cookies, connection strings,
or private keys may be committed.
