# Legacy Public Repository and Deployment Standard

## Status

Approved for implementation on 2026-07-14. This is the mandatory foundation for every service extracted from the private `maliev-web` monorepo.

## Outcome

Every migrated service lives in a fresh public repository named `Legacy.Maliev.<Service>`, receives unlimited public GitHub Actions execution, and can build and deploy independently without publishing the private monorepo or exposing credentials to public forks.

The existing `maliev-web` repository remains private and retains the original history. Source is copied into a fresh repository only after migration and validation; the old `.git` directory and historical objects are never transferred.

## Repository Architecture

Create a public `MALIEV-Co-Ltd/Legacy.Maliev.Workflows` repository as the shared control plane for migrated legacy repositories. It contains:

- reusable .NET 10 validation workflows;
- reusable container build, vulnerability scan, and Artifact Registry publication workflows;
- a GitOps handoff workflow that submits an immutable image digest;
- a publication-gate script;
- a repository bootstrap/protection script;
- standard `SECURITY.md`, Dependabot, CODEOWNERS, and workflow templates;
- tests that reject unsafe permissions, unpinned Actions, direct cluster mutation, and fork-secret exposure.

Service repositories own their application source, Dockerfile, tests, service-specific configuration contract, and thin caller workflows. Shared workflows never own service credentials.

## Public History Contract

Migrated services are created from a clean export, not with `git filter-repo` from the monorepo. Each public repository starts with a new root commit containing only migrated source and publication tooling.

Before visibility is confirmed public, the publication gate must prove:

1. repository name matches `Legacy.Maliev.*`;
2. no source remote or Git object belongs to `maliev-web`;
3. the current tree and complete new history pass Gitleaks;
4. no private key, service-account JSON, credential-bearing configuration, generated secret documentation, database backup, dump, archive, or local environment file is tracked;
5. build, tests, formatting, package audit, and container scan pass;
6. Actions and reusable workflows are pinned to full commit SHAs;
7. repository visibility, branch protection, environment restrictions, and required checks are read back from GitHub after configuration.

Any credential encountered during extraction is treated as compromised and rotated outside the repository before publication.

## CI Trust Model

Pull-request validation is deliberately secretless:

- `permissions: contents: read`;
- no `pull_request_target`;
- no environment secrets, cloud identity, package-write permission, or GitHub App/PAT token;
- no execution of repository-controlled deployment scripts with privileged credentials;
- build, test, format, dependency audit, Gitleaks, and container lint/scan only;
- concurrency cancellation for superseded commits.

Public forks can run the validation workflow without receiving any organization or repository secret.

## CD Trust Model

Deployment is a separate workflow and job boundary. It runs only when all of these are true:

- event is a push to protected `main` or a manually authorized trusted dispatch;
- the exact commit passed the required validation check;
- the job targets a protected GitHub environment;
- permissions are minimal: `contents: read` and `id-token: write`, plus package write only when required;
- Google authentication uses the existing Workload Identity Federation provider, never a service-account JSON key;
- the image is pushed once and identified by immutable digest;
- deployment is handed to `maliev-gitops` as a digest update; the service repository never runs `kubectl apply`, `helm upgrade`, or Argo sync;
- all Kubernetes resources remain in `maliev-legacy` and use the existing cluster and node pool.

Until the full migration is approved for cutover, GitOps applications remain dormant/manual and workflow completion stops after validated GitOps handoff.

## Cloud Identity and Secret Boundaries

The Workload Identity provider may be shared, but authorization is repository- and branch-constrained. Each service receives a least-privilege deploy identity or a narrowly scoped shared legacy image-publisher identity. Kubernetes runtime configuration continues to come from the single Google Secret Manager secret `maliev-legacy-secrets` through External Secrets.

The shared workflow repository contains no deployment secret. Trusted caller repositories provide only environment-scoped configuration and identifiers. Static PATs are avoided where GitHub App or OIDC federation is available; if a GitOps handoff token is temporarily unavoidable, it is restricted to protected `main`, stored as an environment secret, and never exposed to pull-request jobs.

## Branch and Environment Protection

Every migrated repository receives:

- public visibility;
- protected `main` requiring `validate / validate`;
- strict up-to-date checks;
- pull requests required, including for administrators;
- stale review dismissal and conversation resolution;
- linear history;
- force-push and deletion disabled;
- Dependabot for NuGet, Docker, and GitHub Actions;
- production deployment environment restricted to `main`;
- deployment concurrency of one per service/environment.

## Failure Handling

- Publication gate failure leaves the repository private or aborts creation before source push.
- CI failure blocks merge.
- Image build or scan failure prevents publication.
- GitOps handoff failure does not mutate the cluster and can be retried idempotently for the same digest.
- A detected secret immediately blocks publication, records only the file/rule location with redaction, and requires rotation plus a fresh clean history.
- GitHub setting verification failure is treated as incomplete configuration, not success.

## Validation Strategy

The workflow repository uses source-contract and executable tests to verify:

- YAML parses and reusable workflow interfaces are stable;
- all third-party Actions use full SHA pins;
- PR workflows have read-only permissions and no secret references;
- deploy workflows are trusted-event-only and contain no direct cluster command;
- publication gate catches seeded current-tree and historical secrets;
- bootstrap is idempotent and rejects non-`Legacy.Maliev.*` targets;
- repository visibility and protection settings match the required contract;
- a representative service can call validation and deployment workflows successfully.

`Legacy.Maliev.CountryService` is the pilot caller. `Legacy.Maliev.AppHost` follows after the pilot is green. Remaining services adopt the standard at creation.

## Migration Sequence

1. Publish and protect `Legacy.Maliev.Workflows`.
2. Migrate CountryService and AppHost callers to the shared workflows without changing their runtime behavior.
3. Extract services in dependency-safe waves: shared/auth foundations; reference/catalog services; customer/employee/contact; order/quotation/upload/PDF; accounting/payment; public web and intranet composition.
4. Add each extracted service to the local legacy AppHost and pass contract/data-parity tests.
5. Prepare dormant GitOps resources in `maliev-legacy` using the existing cluster only.
6. Execute database restore and comparison rehearsals against self-hosted CloudNativePG without changing the legacy source database.
7. Cut over only after all services, data, analytics/tagging, rollback, and operational gates are green.

## Non-Goals

- Publishing `maliev-web` or any of its original Git history.
- Adding a node pool, Cloud SQL, paid runner, load balancer, or other new billable infrastructure.
- Deploying uncompleted services or syncing dormant Argo applications during migration.
- Sharing runtime secrets through the public workflow repository.
