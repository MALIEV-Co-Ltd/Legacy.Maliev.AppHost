# Legacy Public Workflows Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create and validate a public shared workflow repository that makes every fresh-history `Legacy.Maliev.*` repository fork-safe, independently testable, and deployable through immutable-image GitOps handoff without direct cluster mutation.

**Architecture:** `Legacy.Maliev.Workflows` supplies SHA-pinned reusable workflows, a composite .NET validation action, publication/bootstrap scripts, and executable contract tests. Service repositories keep thin caller workflows and service-specific dependency checkouts; protected `main` may publish images through Workload Identity and hand an immutable digest to `maliev-gitops`, while pull requests remain read-only and secretless.

**Tech Stack:** GitHub Actions, PowerShell 7, .NET 10/xUnit, YamlDotNet, Gitleaks, Trivy, Docker Buildx, Google Workload Identity Federation, GitHub CLI, GitOps/Kustomize.

## Global Constraints

- The original `maliev-web` repository and its Git history remain private.
- Every migrated repository uses a fresh root history and a `Legacy.Maliev.*` name.
- Public pull requests receive no repository, environment, GitHub App, PAT, package-write, or cloud credential.
- Deployment runs only from protected `main` or an explicitly authorized trusted dispatch.
- Deployment uses the existing cluster, existing node pool, and `maliev-legacy`; no direct `kubectl`, Helm, or Argo sync is allowed from service repositories.
- No new billable Google Cloud resource is created.
- All third-party Actions are pinned to complete 40-character commit SHAs.
- Every meaningful validated slice is committed independently.

---

### Task 1: Shared repository contract and executable tests

**Files:**
- Create: `Legacy.Maliev.Workflows/README.md`
- Create: `Legacy.Maliev.Workflows/SECURITY.md`
- Create: `Legacy.Maliev.Workflows/.gitignore`
- Create: `Legacy.Maliev.Workflows/tests/Legacy.Maliev.Workflows.Tests/Legacy.Maliev.Workflows.Tests.csproj`
- Create: `Legacy.Maliev.Workflows/tests/Legacy.Maliev.Workflows.Tests/RepositoryContractTests.cs`
- Create: `Legacy.Maliev.Workflows/Legacy.Maliev.Workflows.slnx`

**Interfaces:**
- Consumes: the approved design in `Legacy.Maliev.AppHost/docs/superpowers/specs/2026-07-14-legacy-public-repository-and-deployment-standard-design.md`.
- Produces: `RepositoryContractTests.FindRepositoryRoot()` and source-contract gates consumed by every later task.

- [ ] **Step 1: Create the public repository shell**

Run:

```powershell
gh repo create MALIEV-Co-Ltd/Legacy.Maliev.Workflows --public --description "Reusable CI/CD and publication gates for migrated MALIEV legacy services" --clone
```

Expected: an empty public repository cloned at `B:\maliev\Legacy.Maliev.Workflows`.

- [ ] **Step 2: Write failing repository contract tests**

Create xUnit tests that require `README.md`, `SECURITY.md`, `.github/dependabot.yml`, the reusable workflows named in Tasks 2-4, and scripts named in Task 5. Add a workflow-source assertion equivalent to:

```csharp
Assert.DoesNotContain("pull_request_target", source, StringComparison.OrdinalIgnoreCase);
Assert.DoesNotContain("kubectl", source, StringComparison.OrdinalIgnoreCase);
Assert.DoesNotContain("argocd", source, StringComparison.OrdinalIgnoreCase);
Assert.Matches(@"uses:\s+[^\s@]+@[0-9a-f]{40}(?:\s|$)", actionLine);
```

- [ ] **Step 3: Run the tests and verify the red phase**

Run:

```powershell
dotnet test .\tests\Legacy.Maliev.Workflows.Tests\Legacy.Maliev.Workflows.Tests.csproj
```

Expected: failures listing the missing workflows, scripts, and Dependabot file.

- [ ] **Step 4: Add repository documentation and solution structure**

Document the fork-safe CI boundary, protected-main deployment boundary, fresh-history rule, zero-cost constraint, and consumer interfaces. `SECURITY.md` must direct private vulnerability reports to GitHub private vulnerability reporting and prohibit secrets in issues.

- [ ] **Step 5: Commit the repository contract**

```powershell
git add .
git commit -m "test: define legacy workflow repository contract"
```

---

### Task 2: Fork-safe .NET validation action and reusable workflow

**Files:**
- Create: `Legacy.Maliev.Workflows/actions/dotnet-validate/action.yml`
- Create: `Legacy.Maliev.Workflows/.github/workflows/dotnet-validate.yml`
- Modify: `Legacy.Maliev.Workflows/tests/Legacy.Maliev.Workflows.Tests/RepositoryContractTests.cs`

**Interfaces:**
- Consumes: caller inputs `solution`, `working-directory`, and `dotnet-version`.
- Produces: required check `validate / validate` and composite action `MALIEV-Co-Ltd/Legacy.Maliev.Workflows/actions/dotnet-validate@<sha>`.

- [ ] **Step 1: Add failing workflow-permission tests**

Require the reusable workflow to contain:

```yaml
permissions:
  contents: read
```

Reject `secrets: inherit`, `id-token: write`, `packages: write`, `pull_request_target`, `${{ secrets.`, `gcloud`, `kubectl`, and `argocd`.

- [ ] **Step 2: Verify the focused tests fail**

Run:

```powershell
dotnet test .\Legacy.Maliev.Workflows.slnx --filter "FullyQualifiedName~ForkSafeValidation"
```

Expected: failure because the validation action/workflow is absent.

- [ ] **Step 3: Implement the composite action**

The composite action runs, in order:

```yaml
- run: dotnet restore "${{ inputs.solution }}"
- run: dotnet build "${{ inputs.solution }}" --configuration Release --no-restore
- run: dotnet test "${{ inputs.solution }}" --configuration Release --no-build --no-restore
- run: dotnet format "${{ inputs.solution }}" --verify-no-changes --no-restore
- run: dotnet list "${{ inputs.solution }}" package --vulnerable --include-transitive --no-restore
```

Use `actions/setup-dotnet`, `actions/cache`, and `gitleaks/gitleaks-action` pinned to verified full SHAs. The caller may perform service-specific sibling checkouts before invoking the composite action.

- [ ] **Step 4: Implement the reusable workflow**

Expose `workflow_call` inputs and check out the caller repository with `persist-credentials: false`. Because GitHub does not allow a reusable workflow to dynamically self-reference a composite action at its own immutable commit, implement the same five validation commands directly in this workflow and enforce parity through `RepositoryContractTests`. Set a 20-minute timeout and commit/ref concurrency cancellation. Complex callers use the composite action after their own dependency checkouts; self-contained services use the reusable workflow.

- [ ] **Step 5: Run tests and actionlint**

Run:

```powershell
dotnet test .\Legacy.Maliev.Workflows.slnx
actionlint .github/workflows/dotnet-validate.yml
```

Expected: all tests and actionlint pass.

- [ ] **Step 6: Commit**

```powershell
git add actions .github tests
git commit -m "feat: add fork-safe dotnet validation"
```

---

### Task 3: Trusted image publication workflow

**Files:**
- Create: `Legacy.Maliev.Workflows/.github/workflows/publish-image.yml`
- Modify: `Legacy.Maliev.Workflows/tests/Legacy.Maliev.Workflows.Tests/RepositoryContractTests.cs`

**Interfaces:**
- Consumes: inputs `image`, `dockerfile`, `context`, `environment`, `workload-identity-provider`, and `service-account`; caller permission `id-token: write`.
- Produces: output `digest` formatted as `sha256:<64 lowercase hex characters>` and image reference `<registry>/<image>@<digest>`.

- [ ] **Step 1: Add failing trusted-event and permission tests**

Require `workflow_call`, environment binding, `contents: read`, and `id-token: write`. Reject static credential JSON, service-account keys, `pull_request`, `pull_request_target`, direct cluster commands, and mutable deployment tags.

- [ ] **Step 2: Implement build, scan, and publish**

Pin checkout, Google auth, Docker login/setup-buildx/build-push, and Trivy Actions to full SHAs. Authenticate through WIF, build once, scan before push, push the commit tag, resolve the registry digest, and expose only the digest through `$GITHUB_OUTPUT`.

- [ ] **Step 3: Verify contracts and YAML**

Run the focused tests and `actionlint`. Expected: the workflow has no secret reference and no cluster mutation command.

- [ ] **Step 4: Commit**

```powershell
git add .github/workflows/publish-image.yml tests
git commit -m "feat: publish legacy images with workload identity"
```

---

### Task 4: Immutable GitOps handoff action

**Files:**
- Create: `Legacy.Maliev.Workflows/actions/gitops-handoff/action.yml`
- Create: `Legacy.Maliev.Workflows/scripts/Set-GitOpsImageDigest.ps1`
- Modify: `Legacy.Maliev.Workflows/README.md`
- Modify: `Legacy.Maliev.Workflows/tests/Legacy.Maliev.Workflows.Tests/RepositoryContractTests.cs`

**Interfaces:**
- Consumes: `service`, `image`, `digest`, `gitops-path`, allowlist `contract-version`, and a token input supplied by a service-owned job bound to that service repository's protected deployment environment.
- Produces: `changed`, `status`, and deterministic `branch` outputs plus, only when changed, one branch/PR in `MALIEV-Co-Ltd/maliev-gitops` changing the allowlisted service image digest.
- Stores no deployment secret in the public shared repository. Contract `v1` initially permits only `Legacy.Maliev.CountryService` -> `3-apps/_legacy-country-service/overlays/legacy/kustomization.yaml` -> `legacy-maliev-country-service`.

- [ ] **Step 1: Write failing path and digest validation tests**

Test that `Set-GitOpsImageDigest.ps1` rejects service names outside `Legacy.Maliev.*`, well-formed but unallowlisted services, non-`sha256` digests, control-character inputs, paths outside the GitOps checkout, symlink/reparse traversal, namespace changes, node-pool selectors, and more than the expected image field. Test same-digest success as an explicit no-op and enforce one-file diffs.

- [ ] **Step 2: Implement the minimal updater**

The script resolves the target with `GetFullPath`, confirms it remains below the supplied GitOps root, rejects every reparse/symlink component, resolves the exact versioned allowlist entry, updates exactly one `newTag` or digest field, emits hardened action outputs, and fails if the resulting Git diff contains any other file.

- [ ] **Step 3: Implement the secretless composite handoff action**

Require protected `main` and a trusted push/dispatch context, accept the caller's environment-scoped token as an action input, check out `maliev-gitops` with credential persistence disabled, run the updater, and stop successfully on `changed=false`. On change, run the GitOps validation command, push a deterministic branch with a per-command authenticated header, and open or update a PR with step-scoped `GH_TOKEN`. Verify no credential configuration or authenticated remote remains. The token is limited to `maliev-gitops` Contents read/write and Pull requests read/write. Do not merge, sync, or call the cluster.

The allowlisted CountryService path is not yet on `maliev-gitops` main. Its adoption task must land the dormant validated manifest before enabling this action in the service repository.

- [ ] **Step 4: Test and commit**

Run unit/source-contract tests against a temporary fixture repository, then commit:

```powershell
git commit -m "feat: hand immutable image digests to gitops"
```

- [ ] **Step 5: Apply the secret-ownership review correction**

Remove the secret-bearing reusable workflow, add the composite action and explicit allowlist/no-op/path-hardening tests, update caller documentation, and commit the correction independently.

---

### Task 5: Fresh-history publication and GitHub protection tooling

**Files:**
- Create: `Legacy.Maliev.Workflows/scripts/Test-LegacyPublication.ps1`
- Create: `Legacy.Maliev.Workflows/scripts/Publish-LegacyRepository.ps1`
- Create: `Legacy.Maliev.Workflows/tests/publication/Test-LegacyPublication.Tests.ps1`
- Create: `Legacy.Maliev.Workflows/.github/dependabot.yml`

**Interfaces:**
- Consumes: local clean repository path and GitHub name `MALIEV-Co-Ltd/Legacy.Maliev.*`.
- Produces: public repository, verified visibility, protected `main`, required check, and protected deployment environment.

- [ ] **Step 1: Write fixture-based failing publication tests**

Create temporary repositories for: clean fresh history, `maliev-web` remote/history, tracked `.env`, seeded historical token, unpinned Action, unsafe PR permission, and direct cluster command. Assert only the clean repository passes.

- [ ] **Step 2: Implement the local publication gate**

Validate naming, root commit ancestry, remotes, clean status, Gitleaks current tree/history, forbidden tracked extensions, workflow permissions, Action pins, build/test/format/audit commands, and optional Docker scan. Emit redacted failures only.

- [ ] **Step 3: Implement publication and protection**

`Publish-LegacyRepository.ps1` runs the gate, creates the public GitHub repository only after success, pushes `main`, waits for `validate / validate`, configures branch protection, enables vulnerability reporting, verifies visibility/protection via `gh api`, and fails closed on readback mismatch.

- [ ] **Step 4: Add Dependabot**

Configure weekly NuGet, Docker, and GitHub Actions updates with grouped pull requests.

- [ ] **Step 5: Run fixture tests, Gitleaks, and commit**

```powershell
dotnet test .\Legacy.Maliev.Workflows.slnx
gitleaks detect --source . --redact
git commit -m "feat: gate and publish fresh legacy repositories"
```

---

### Task 6: Publish and protect Legacy.Maliev.Workflows

**Files:**
- Modify: repository settings only after all prior tasks pass.

**Interfaces:**
- Consumes: publication tooling from Task 5.
- Produces: immutable shared-workflow commit SHA for service callers.

- [ ] **Step 1: Run the complete release gate**

Run tests, actionlint, Gitleaks, dependency audit, and workflow source checks. Expected: all green.

- [ ] **Step 2: Push a pull request and monitor exact-commit CI**

Create a PR, record its head SHA, and wait for every required check on that SHA.

- [ ] **Step 3: Merge and protect main**

Require `validate / validate`, enforce admins, linear history, stale-review dismissal, conversation resolution, and disable force-push/deletion.

- [ ] **Step 4: Record the immutable workflow SHA**

Use the merged main SHA in every caller; never reference `main`, a tag, or a floating version.

---

### Task 7: Pilot Legacy.Maliev.CountryService

**Files:**
- Modify: `Legacy.Maliev.CountryService/.github/workflows/_build-and-test.yml`
- Modify: `Legacy.Maliev.CountryService/Legacy.Maliev.CountryService.Tests/Workflows/WorkflowContractTests.cs`

**Interfaces:**
- Consumes: shared composite validation action at Task 6 SHA.
- Produces: unchanged required check `validate / validate` with caller-owned sibling dependency checkouts.

- [x] **Step 1: Add a failing caller-pin test**

Require the shared action reference to equal the exact Task 6 SHA and reject duplicated restore/build/test/format/audit commands after adoption.

- [x] **Step 2: Replace duplicated validation steps**

Keep CountryService, `Maliev.Aspire`, and `Maliev.MessagingContracts` checkouts in the caller job, then call the pinned composite action with `Legacy.Maliev.CountryService.slnx`.

- [x] **Step 3: Run local gates, push PR, and monitor CI**

Build/test/format/audit/Gitleaks locally, push, and wait for the exact PR commit to pass.

- [x] **Step 4: Merge and verify protected main**

Confirm required-check continuity and no visibility/protection regression.

---

### Task 8: Adopt shared validation in Legacy.Maliev.AppHost

**Files:**
- Modify: `Legacy.Maliev.AppHost/.github/workflows/_build-and-test.yml`
- Modify: `Legacy.Maliev.AppHost/Legacy.Maliev.AppHost.Tests/WorkflowContractTests.cs`
- Modify: `Legacy.Maliev.AppHost/docs/superpowers/plans/2026-07-14-legacy-public-workflows-implementation.md`

**Interfaces:**
- Consumes: Task 6 shared action SHA and Task 7 pilot evidence.
- Produces: second independent public consumer and completed shared-foundation checklist.

- [x] **Step 1: Add failing pinned-caller tests**

Require the exact shared SHA and retain AppHost/Country/Aspire/Messaging dependency checkouts.

- [x] **Step 2: Adopt the shared action**

Replace duplicated .NET validation commands with the pinned composite action.

- [x] **Step 3: Run the local AppHost release and integration gates**

Run build, 20+ tests, format, dependency audit, full-history Gitleaks, and `scripts/verify-local-stack.ps1`; confirm no containers remain.

- [ ] **Step 4: Publish through protected main**

Push a PR, monitor exact-commit CI, merge after green, and verify main CI/readback.

- [ ] **Step 5: Close the implementation checklist**

Mark every completed step in this plan and commit the evidence update.
