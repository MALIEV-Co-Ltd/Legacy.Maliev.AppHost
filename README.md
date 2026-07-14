# Legacy.Maliev.AppHost

Local-only .NET 10 Aspire orchestration for the MALIEV legacy migration. It models the shared
runtime that will eventually run in the existing GKE cluster, without creating or changing any
cloud resource.

## Current topology

- PostgreSQL 18 with the 21 legacy database names preserved exactly.
- Authenticated Redis 8.4.
- Ephemeral RSA-3072 service-auth key material generated for every local run.
- `Legacy.Maliev.CountryService` wired to PostgreSQL, Redis, auth, health checks, telemetry, and
  resource limits using the same configuration keys as the dormant GitOps manifests.
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
- Sibling repositories at `B:\maliev\Legacy.Maliev.CountryService`, `B:\maliev\Maliev.Aspire`,
  and `B:\maliev\Maliev.MessagingContracts`

## Verify locally

From PowerShell:

```powershell
.\scripts\verify-local-stack.ps1
```

The command builds the solution, creates fresh local-only passwords, starts the Aspire stack,
polls resource health, verifies liveness/readiness/Scalar/the anonymous legacy `/Countries`
contract, checks all 21 database names, rejects ambient credential leakage, and removes the local
containers in `finally` even when validation fails.

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
