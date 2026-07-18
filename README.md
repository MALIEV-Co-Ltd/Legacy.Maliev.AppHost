# Legacy.Maliev.AppHost

Local-only .NET 10 Aspire orchestration for the MALIEV legacy migration. It models the shared
runtime that will eventually run in the existing GKE cluster, without creating or changing any
cloud resource.

## Current topology

- PostgreSQL 18 with the 21 legacy database names preserved exactly and a separate `Auth` database
  for refresh sessions and single-use account-action tokens. Application traffic passes through a
  resource-bounded PgBouncer 1.25.2 container using the same transaction-pool limits as the dormant
  CloudNativePG Pooler; migration and bootstrap jobs keep direct PostgreSQL connections.
- Authenticated Redis 8.4.
- Ephemeral RSA-3072 service-auth key material, independent random `legacy-web` and
  `legacy-intranet` credentials, and a short-lived
  Web data-protection certificate generated for every local run. They are never persisted or read
  from production Secret Manager.
- `Legacy.Maliev.CountryService` wired to PostgreSQL, Redis, auth, health checks, telemetry, and
  resource limits using the same configuration keys as the dormant GitOps manifests.
- `Legacy.Maliev.DocumentService` wired as a stateless JWT-protected QuestPDF workload with health
  checks, Scalar, telemetry, and a 192 MiB managed-heap ceiling. It intentionally has no database,
  Redis, storage, or migration dependency.
- `Legacy.Maliev.AuthService` wired to its isolated PostgreSQL runtime database, separate customer
  and employee identity databases, five total migration jobs, ephemeral signing key, and
  least-privilege `legacy-web` client. Disposable synthetic customer and employee identities prove
  the post-copy PostgreSQL login path locally; they are never sourced from or deployed to production.
- `Legacy.Maliev.CustomerService` wired to its preserved `Customer` PostgreSQL database, migration
  job, Redis cache, JWT trust, and AuthService boundary.
- `Legacy.Maliev.EmployeeService`, `Legacy.Maliev.CatalogService`, and
  `Legacy.Maliev.ProcurementService` wired to their preserved PostgreSQL databases, isolated
  migration jobs, Redis caches, JWT trust, health checks, Scalar, telemetry, and bounded heaps.
  Supplier and purchase-order databases remain separate.
- `Legacy.Maliev.FileService` wired to the preserved `Upload` database and JWT boundary. Local
  verification checks startup and authorization without uploading an object; real upload tests use
  ADC/Workload Identity and the configured ClamAV scanner rather than stored service-account keys.
- `Legacy.Maliev.NotificationService` wired with JWT trust and a development-only placeholder Brevo
  credential. The local verifier never sends email, so it cannot contact the production provider.
- `Legacy.Maliev.CareerService` and `Legacy.Maliev.ContactService` wired to their preserved
  `JobOffers` and `Message` databases, isolated migrations, Redis, JWT trust, health checks, and
  bounded heaps. Career reads stay public; Contact message reads require permission and Web has
  create-only access.
- `Legacy.Maliev.AccountingService` wired as a standalone protected historical-record API over the
  separate `Payment`, `Invoice`, and `Receipt` databases. It is intentionally not connected to Web,
  Intranet, Omise/Opn, or any payment-execution workflow.
- `Legacy.Maliev.Web` wired to Auth, Customer, Notification, Country, Career, Contact, Document, Redis, encrypted
  server-side sessions, and the ephemeral `legacy-web` credential. Public account surfaces can be
  exercised locally; reCAPTCHA-protected signup submission remains fail closed without local ADC.
- `Legacy.Maliev.Intranet` wired independently to Auth, Customer, Employee, Catalog, Procurement,
  Order, Document, File, Notification, and Redis. Its short-lived machine JWT has an exact
  wildcard-free permission list, while the browser receives only an opaque HttpOnly employee
  session cookie backed by Redis.
- The local `legacy-web` service identity includes only the CustomerService customer read/update
  and address create/update permissions required by the authenticated Member address page. Web
  derives the customer database ID from the Auth-issued token, stores it in the encrypted
  server-side session, and reloads owned record IDs before writes; the browser cannot select
  another customer or receive the service token.
- Member profile validation additionally grants only company create/update/delete. The Web BFF
  reloads the owned customer and nested company ID before any operation; no company identifier is
  accepted from the browser. Company read is not granted because the customer response already
  contains the owned company projection.
- A fail-closed environment policy that prevents unrelated machine credentials from reaching
  Aspire resources or appearing in the dashboard.

The AppHost is a functional local mirror, not a nested Kubernetes cluster. CloudNativePG
replication, persistent volumes, pod disruption budgets, anti-affinity, network policies,
Workload Identity, External Secrets, ingress, and Argo reconciliation remain Kubernetes/GitOps
concerns. They will be validated separately before the one future deployment to the existing
cluster and `maliev-legacy` namespace.

## Prerequisites

- .NET SDK 10
- Docker Desktop
- `kubectl` (used only with Aspire DCP's generated temporary local kubeconfig)
- Sibling repositories at `B:\maliev\Legacy.Maliev.AuthService`,
  `B:\maliev\Legacy.Maliev.CountryService`, `B:\maliev\Legacy.Maliev.CustomerService`,
  `B:\maliev\Legacy.Maliev.DocumentService`, `B:\maliev\Legacy.Maliev.EmployeeService`,
  `B:\maliev\Legacy.Maliev.CatalogService`, `B:\maliev\Legacy.Maliev.ProcurementService`,
  `B:\maliev\Legacy.Maliev.FileService`, `B:\maliev\Legacy.Maliev.NotificationService`,
  `B:\maliev\Legacy.Maliev.CareerService`, `B:\maliev\Legacy.Maliev.ContactService`,
  `B:\maliev\Legacy.Maliev.AccountingService`, `B:\maliev\Legacy.Maliev.Web`,
  `B:\maliev\Legacy.Maliev.Intranet`,
  `B:\maliev\Maliev.Aspire`, and
  `B:\maliev\Maliev.MessagingContracts`.

## Verify locally

From PowerShell:

```powershell
.\scripts\verify-local-stack.ps1
```

The command builds the solution, creates fresh local-only passwords, starts the Aspire stack,
polls resource health, verifies all nineteen migrations and all sixteen services, proves synthetic
customer and employee PostgreSQL login, confirms the anonymous Country/Web surfaces and protected
service boundaries, renders the seeded Career listing through Web, persists a Contact request with
the create-only Web identity, keeps Accounting frontend-disconnected, validates the exact Intranet
service-token permissions, signs into the
Intranet, exercises Dashboard plus Customer/Employee/Material/Supplier/Order/PurchaseOrder pages,
checks all 21
preserved database names plus the isolated Auth runtime database, proves a real Country query
through PgBouncer, rejects ambient credential
leakage, and removes the local containers in `finally` even when validation fails.

For interactive development, set the three `Parameters__legacy-*` environment variables to
local-only values and run:

```powershell
dotnet run --project .\Legacy.Maliev.AppHost\Legacy.Maliev.AppHost.csproj --launch-profile http
```

The dashboard is served at `http://localhost:15888`. Do not reuse production credentials for
local parameters.

### PostgreSQL connection boundary

The local resource `legacy-postgres-pooler-rw` mirrors the prepared CloudNativePG Pooler name and
uses transaction pooling with `default_pool_size=3`, `max_client_conn=200`, `min_pool_size=0`,
`reserve_pool_size=1`, and `server_idle_timeout=60`. Retained APIs receive pooled connection strings;
each Npgsql client pool is capped at ten connections so the nineteen application connection strings
cannot exceed the pooler's 200-client ceiling in aggregate. All nineteen migration connection strings
still reference the direct disposable PostgreSQL resource.
The verifier executes `select current_database()` through PgBouncer after the service workflows.
This local topology does not apply the dormant manifests or change GKE.

Run the disposable direct/session/transaction comparison with:

```powershell
.\scripts\benchmark-postgres-pooling.ps1
```

The script rotates target order across three rounds, attributes PostgreSQL backends to the target
container address, validates transaction-scoped operations, measures pooler restart recovery, writes
machine-readable output under the ignored `temp` directory, and always removes its containers and
network. See [the recorded local baseline](docs/postgres-pooling-baseline.md) for results, the observed
20-client saturation boundary, and the remaining CloudNativePG validation gates.

### Redis protocol compatibility

The local Redis resource is Redis 8.4 with password authentication. AppHost explicitly appends
`protocol=resp3` to every retained API, Intranet compatibility host, and Intranet BFF connection.
`Legacy.Maliev.Web` receives the same discovery connection but deliberately overrides it to RESP2 in
its own startup configuration until the owner approves that separate compatibility cutover.

Run the focused authenticated protocol, shared-cache, Lua lease, encrypted session, and data-protection
key-reload suite with:

```powershell
dotnet test .\Legacy.Maliev.AppHost.Tests\Legacy.Maliev.AppHost.Tests.csproj `
  -c Release `
  --filter FullyQualifiedName~RedisResp3CompatibilityTests
```

The suite uses a disposable password-protected `redis:8.4-alpine` Testcontainer. It does not connect to,
modify, publish, or deploy anything in GKE.

## Zero-cost boundary

This repository contains no deployment workflow, cloud identity permission, `gcloud` operation,
Argo synchronization, or cluster mutation. Nothing here creates a node pool, Cloud SQL instance,
load balancer, disk, or any other billable Google Cloud resource.
