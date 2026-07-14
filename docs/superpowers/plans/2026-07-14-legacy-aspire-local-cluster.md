# Legacy Aspire Local Cluster Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a zero-cloud-cost .NET 10 Aspire host that models the dormant legacy GKE runtime and starts the extracted Country service against PostgreSQL and authenticated Redis.

**Architecture:** A standalone AppHost references sibling service projects and a focused topology library. The topology library owns database/configuration names and ephemeral RSA generation; the AppHost owns only resource orchestration. Kubernetes-only replication and policy behavior stays in GitOps tests.

**Tech Stack:** .NET 10, Aspire AppHost SDK 13.4.6, Aspire PostgreSQL/Redis hosting, PostgreSQL 18, Redis 8.4, xUnit 2.9, Microsoft.Extensions.TimeProvider.Testing, Gitleaks.

## Global Constraints

- No cloud resource creation, Argo synchronization, or production data download.
- Repository and project names use the `Legacy.Maliev.*` convention.
- Local configuration keys match GitOps environment-variable names exactly.
- No committed credentials, private keys, database passwords, or service-account JSON.
- PostgreSQL source schemas remain unchanged.
- Every task ends in a focused test/build cycle and coherent commit.

---

### Task 1: Topology contract and tests

**Files:**
- Create: `Legacy.Maliev.AppHost.slnx`
- Create: `Legacy.Maliev.AppHost.Topology/Legacy.Maliev.AppHost.Topology.csproj`
- Create: `Legacy.Maliev.AppHost.Topology/LegacyTopology.cs`
- Create: `Legacy.Maliev.AppHost.Topology/LocalJwtKeyMaterial.cs`
- Create: `Legacy.Maliev.AppHost.Tests/Legacy.Maliev.AppHost.Tests.csproj`
- Create: `Legacy.Maliev.AppHost.Tests/LegacyTopologyTests.cs`

**Interfaces:**
- Produces: `LegacyTopology.DatabaseNames`, `LegacyTopology.CountryConfigurationKeys`, and `LocalJwtKeyMaterial.Create()`.
- Consumes: no service implementation.

- [ ] **Step 1: Write failing topology tests**

  Assert the exact ordered set of 21 database names, the five Country configuration keys, and
  that `LocalJwtKeyMaterial.Create()` returns Base64 PEM values that import into RSA and verify a
  signature.

- [ ] **Step 2: Run the focused tests and verify failure**

  Run `dotnet test Legacy.Maliev.AppHost.Tests/Legacy.Maliev.AppHost.Tests.csproj`.
  Expected: compilation failure because the topology types do not exist.

- [ ] **Step 3: Implement the minimal topology types**

  `LegacyTopology` exposes immutable string collections. `LocalJwtKeyMaterial.Create()` creates
  RSA-3072 keys, exports PKCS#8 private PEM and SubjectPublicKeyInfo public PEM, UTF-8 encodes each,
  and returns Base64 strings in `LocalJwtKeyMaterial(string PrivateKeyBase64, string PublicKeyBase64)`.

- [ ] **Step 4: Run tests and build**

  Run `dotnet test Legacy.Maliev.AppHost.Tests/Legacy.Maliev.AppHost.Tests.csproj` and
  `dotnet build Legacy.Maliev.AppHost.slnx --configuration Release`.
  Expected: all tests pass; zero warnings and errors.

- [ ] **Step 5: Commit**

  Commit message: `feat: define legacy local topology contract`.

### Task 2: Aspire infrastructure and Country orchestration

**Files:**
- Create: `Legacy.Maliev.AppHost/Legacy.Maliev.AppHost.csproj`
- Create: `Legacy.Maliev.AppHost/AppHost.cs`
- Create: `Legacy.Maliev.AppHost/appsettings.json`
- Create: `Legacy.Maliev.AppHost/Properties/launchSettings.json`
- Modify: `Legacy.Maliev.AppHost.slnx`
- Create: `Legacy.Maliev.AppHost.Tests/AppHostSourceContractTests.cs`

**Interfaces:**
- Consumes: `LegacyTopology.DatabaseNames`, `LocalJwtKeyMaterial.Create()`, and sibling `Legacy.Maliev.CountryService.Api`.
- Produces: Aspire resources `legacy-postgres-main`, `legacy-redis`, 21 databases, and `legacy-maliev-country-service`.

- [ ] **Step 1: Write failing orchestration contract tests**

  Assert AppHost source registers PostgreSQL 18, Redis 8.4, every database from
  `LegacyTopology.DatabaseNames`, exact Country connection/JWT environment keys, resource caps,
  `/countries/liveness`, `/countries/readiness`, and no GCP/Argo deployment command.

- [ ] **Step 2: Run the tests and verify failure**

  Run `dotnet test Legacy.Maliev.AppHost.Tests/Legacy.Maliev.AppHost.Tests.csproj`.
  Expected: failures because `AppHost.cs` is absent.

- [ ] **Step 3: Implement AppHost resources**

  Use `DistributedApplication.CreateBuilder(args)`, `AddPostgres`, `AddDatabase`, `AddRedis`, and
  `AddProject<Projects.Legacy_Maliev_CountryService_Api>`. Inject the exact configuration keys,
  wait for PostgreSQL/Redis, and configure container runtime memory/CPU caps.

- [ ] **Step 4: Run tests and build**

  Run focused tests, then `dotnet build Legacy.Maliev.AppHost.slnx --configuration Release`.
  Expected: all tests pass; zero warnings and errors.

- [ ] **Step 5: Commit**

  Commit message: `feat: orchestrate legacy country stack locally`.

### Task 3: Local run and integration verification

**Files:**
- Create: `scripts/verify-local-stack.ps1`
- Create: `README.md`
- Create: `.gitignore`
- Create: `.gitattributes`

**Interfaces:**
- Consumes: Aspire dashboard/resource endpoints and Country HTTP routes.
- Produces: repeatable local validation command with a non-zero exit on failed health or contract checks.

- [ ] **Step 1: Write the verification script contract test**

  Add a source test requiring condition-based polling, `/countries/liveness`,
  `/countries/readiness`, `/countries/scalar`, and `/Countries`, with no fixed long sleep.

- [ ] **Step 2: Run the test and verify failure**

  Run the focused source-contract test. Expected: failure because the script is absent.

- [ ] **Step 3: Implement the script and documentation**

  The script starts the AppHost, polls exported endpoint state with a bounded timeout, validates
  the four routes, reports container state on failure, and always stops local resources. README
  documents prerequisites, zero-cloud boundary, topology differences, user-secrets setup, and
  the one-command verification flow.

- [ ] **Step 4: Execute full verification**

  Run the script, all tests, Release build, format verification, vulnerable-package audit, and
  Gitleaks. Expected: all green and no local containers left running.

- [ ] **Step 5: Commit**

  Commit message: `test: verify local legacy aspire stack`.

### Task 4: Public CI and repository protection

**Files:**
- Create: `.github/workflows/_build-and-test.yml`
- Create: `.github/workflows/ci-develop.yml`
- Create: `.github/workflows/ci-staging.yml`
- Create: `.github/workflows/ci-main.yml`
- Create: `.github/workflows/pr-validation.yml`
- Create: `.github/dependabot.yml`

**Interfaces:**
- Consumes: public sibling repositories at pinned compatible commits.
- Produces: build/test/format/audit validation only; no deployment permissions or cloud identity.

- [ ] **Step 1: Add workflow source tests**

  Assert all required MALIEV workflow files exist, permissions are `contents: read`, concurrency
  is configured, dependencies are pinned, and no `id-token: write`, `gcloud`, `kubectl apply`, or
  Argo sync command exists.

- [ ] **Step 2: Run tests and verify failure**

  Expected: failures for missing workflow files.

- [ ] **Step 3: Implement validation-only CI and Dependabot**

  CI restores .NET 10, builds Release, runs tests and format, and audits vulnerable packages.
  Dependabot monitors NuGet and GitHub Actions weekly.

- [ ] **Step 4: Validate, publish, and protect main**

  Parse workflow YAML, run the full release gate, push, monitor the exact commit to green, and
  require the validation job on protected `main` with force-push/deletion disabled.

- [ ] **Step 5: Commit**

  Commit message: `ci: validate legacy apphost without cloud deployment`.
