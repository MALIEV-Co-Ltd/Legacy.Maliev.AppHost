# Legacy.Maliev.AppHost

Local-only .NET 10 Aspire orchestration for the MALIEV legacy migration. It models the shared
runtime that will eventually run in the existing GKE cluster, without creating or changing any
cloud resource.

## Current topology

- PostgreSQL 18 with the 21 legacy database names preserved exactly and a separate `Auth` database
  for refresh sessions and single-use account-action tokens.
- Authenticated Redis 8.4.
- Ephemeral RSA-3072 service-auth key material, a random `legacy-web` credential, and a short-lived
  Web data-protection certificate generated for every local run. They are never persisted or read
  from production Secret Manager.
- `Legacy.Maliev.CountryService` wired to PostgreSQL, Redis, auth, health checks, telemetry, and
  resource limits using the same configuration keys as the dormant GitOps manifests.
- `Legacy.Maliev.DocumentService` wired as a stateless JWT-protected QuestPDF workload with health
  checks, Scalar, telemetry, and a 192 MiB managed-heap ceiling. It intentionally has no database,
  Redis, storage, or migration dependency.
- `Legacy.Maliev.AuthService` wired to its isolated PostgreSQL runtime database, migration job,
  ephemeral signing key, and least-privilege `legacy-web` client. The two legacy SQL Server identity
  readers deliberately point to a fail-closed local placeholder until their verified PostgreSQL
  copy migration is complete; liveness, API documentation, and service-token issuance work, while
  legacy customer/employee password login is not represented as migrated yet.
- `Legacy.Maliev.CustomerService` wired to its preserved `Customer` PostgreSQL database, migration
  job, Redis cache, JWT trust, and AuthService boundary.
- `Legacy.Maliev.NotificationService` wired with JWT trust and a development-only placeholder Brevo
  credential. The local verifier never sends email, so it cannot contact the production provider.
- `Legacy.Maliev.Web` wired to Auth, Customer, Notification, Country, Document, Redis, encrypted
  server-side sessions, and the ephemeral `legacy-web` credential. Public account surfaces can be
  exercised locally; reCAPTCHA-protected signup submission remains fail closed without local ADC.
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
  `B:\maliev\Legacy.Maliev.DocumentService`, `B:\maliev\Legacy.Maliev.NotificationService`,
  `B:\maliev\Legacy.Maliev.Web`, `B:\maliev\Maliev.Aspire`, and
  `B:\maliev\Maliev.MessagingContracts`.

## Verify locally

From PowerShell:

```powershell
.\scripts\verify-local-stack.ps1
```

The command builds the solution, creates fresh local-only passwords, starts the Aspire stack,
polls resource health, verifies all three migrations and all six services, confirms the anonymous
Country/Web surfaces and protected Auth/Customer/Notification/Document boundaries, checks all 21
preserved database names plus the isolated Auth runtime database, rejects ambient credential
leakage, and removes the local containers in `finally` even when validation fails.

For interactive development, set the three `Parameters__legacy-*` environment variables to
local-only values and run:

```powershell
dotnet run --project .\Legacy.Maliev.AppHost\Legacy.Maliev.AppHost.csproj --launch-profile http
```

The dashboard is served at `http://localhost:15888`. Do not reuse production credentials for
local parameters.

## Zero-cost boundary

This repository contains no deployment workflow, cloud identity permission, `gcloud` operation,
Argo synchronization, or cluster mutation. Nothing here creates a node pool, Cloud SQL instance,
load balancer, disk, or any other billable Google Cloud resource.
