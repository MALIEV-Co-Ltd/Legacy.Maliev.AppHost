# Legacy Aspire Local Cluster Design

## Purpose

`Legacy.Maliev.AppHost` is the zero-cloud-cost integration environment for the temporary
legacy migration. It lets every extracted `Legacy.Maliev.*` service run against the same
database, cache, authentication, configuration, health, and resource boundaries it will
eventually use in the existing GKE cluster. It does not deploy or modify cloud resources.

## Chosen approach

Use a standalone .NET 10 Aspire AppHost with sibling project references. Aspire supplies
local PostgreSQL and Redis containers, starts extracted services, injects connection strings,
and exposes health/resource state in its dashboard. A small topology library owns the
production-to-local naming map so tests can prevent drift from GitOps.

A nested kind or k3d cluster is intentionally excluded. It would model Kubernetes scheduling
more literally, but duplicates the cluster runtime, consumes substantially more local memory,
and slows the service-by-service migration loop. Kubernetes-only behavior remains validated in
`maliev-gitops` tests.

## Runtime topology

- One PostgreSQL 18 container named `legacy-postgres-main` represents the CloudNativePG cluster.
- It exposes the 21 existing legacy database names without changing their schema names:
  `Country`, `Currency`, `Customer`, `CustomerIdentity`, `DataProtectionKeys`,
  `DataProtectionKeysEmployee`, `Employee`, `EmployeeIdentity`, `Invoice`, `JobOffers`,
  `Material`, `Message`, `Order`, `OrderStatus`, `Payment`, `PurchaseOrder`, `Quotation`,
  `QuotationRequest`, `Receipt`, `Supplier`, and `Upload`.
- One password-protected Redis 8 container named `legacy-redis` models the in-cluster cache.
- RSA key material is generated in memory for each local run. The public key is injected into
  all services; the private key will be injected only into the future Auth service.
- `Legacy.Maliev.CountryService` is the first service resource. It receives
  `ConnectionStrings__CountryDbContext`, `ConnectionStrings__redis`, `Jwt__PublicKey`,
  `Jwt__Issuer`, and `Jwt__Audience`, matching the GitOps environment names.
- PostgreSQL is capped at 1 GiB, Redis at 96 MiB, and Country at 192 MiB to keep local behavior
  near the GitOps limits. Local PostgreSQL uses one instance; CNPG replication, PDB,
  anti-affinity, backup, and failover remain GitOps-only validation concerns.

## Repository boundaries

- `Legacy.Maliev.AppHost`: orchestration and topology assertions only.
- `Legacy.Maliev.CountryService`: Country API/application/domain/data behavior.
- Future `Legacy.Maliev.*` repositories: independently buildable services added one at a time.
- `maliev-gitops`: dormant production manifests and Kubernetes-specific policy tests.
- `maliev-web`: legacy source-of-truth behavior and data migration validation until cutover.

The AppHost must not absorb business logic, EF migrations, production credentials, or copies of
service source. Missing sibling services fail at build time, making workspace drift explicit.

## Data and secrets

Local PostgreSQL volumes are disposable and never treated as source of truth. Production backup
data is not downloaded automatically. Sanitized restore fixtures may be loaded only through a
future explicit restore command with checksum and row-count gates.

No GSM values, service-account JSON, API keys, database passwords, or private keys are committed.
Aspire parameters and .NET user secrets hold local passwords; RSA material is ephemeral.

## Health and failure behavior

Country waits for PostgreSQL and Redis before startup. Aspire probes `/countries/liveness` and
`/countries/readiness`. Infrastructure or service startup failure remains visible in the Aspire
dashboard and causes integration tests to fail; no fallback silently substitutes in-memory data.

## Validation

1. Unit tests assert all 21 database names and exact GitOps configuration keys.
2. Unit tests assert generated RSA keys round-trip and never use symmetric production auth.
3. The solution builds on .NET 10 with zero warnings.
4. Aspire integration startup proves PostgreSQL, authenticated Redis, Country liveness, Scalar,
   and the anonymous legacy `/Countries` contract.
5. Gitleaks and vulnerable-package audits must pass before publication.

## Deployment boundary

The AppHost is local-only. No workflow may apply to GKE or sync Argo. Production deployment is a
separate final migration phase after every service and data validation gate passes, using only
the existing cluster and no additional infrastructure cost.
