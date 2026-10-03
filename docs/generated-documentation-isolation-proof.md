# Committed XML documentation isolation proof

This bounded audit accounts for source commit
`03dc9a1271c16e6535934445e9dd6e3f30e8fffe` only. It does not complete logging
commit `5ac7d045c51194edd9e64d8564f1b726b001be34` or authorize a release.

## Inventory and ownership

`contracts/source-03dc9a1-documentation-owners.json` explicitly lists every one
of the 88 changed source project paths, their target owners, disposition and
rationale. The verifier independently reads that inventory from the committed
source diff and rejects omissions, duplicates, unknown owners and mutable refs.
The manifest pins 18 canonical repository main SHAs observed on 2026-09-07;
later checkout edits or movement of main cannot silently change the inspected
objects. Re-review and update pins deliberately for a later checkpoint.

The three PredictionService projects retain their approved deterministic-pricing
retirement. The preceding retirement/ownership evidence is the committed Web
`docs/source-parity-delta-through-4486f0e.json` entry for `f0640fe...` and its
`approved-retirement:deterministic-pricing` classification. Shared source helpers
are decomposed across CompatibilityContracts, ServiceDefaults and applications;
these records are ownership accounting, not claims of identical assemblies.

## Verification boundary

The verifier only runs Git `diff-tree`, `rev-parse`, `ls-tree` and `show`. It does
not fetch, check out source files, build/evaluate projects, import modules from
target repositories, or execute MSBuild targets. It reads committed `.csproj`,
`.props`, `.targets` text and tracked filenames, never row data, credentials or
runtime configuration. It emits metadata to stdout and writes nothing.

It rejects source-root or unresolved documentation/output paths, generated
assembly XML copy/publish metadata, and tracked project-root assembly XML.
SDK-default generation and recognized bin/obj/intermediate paths are allowed.
Unrelated XML and child asset directories such as `Baselines/**/*` are allowed;
there is no blanket XML ban or deletion. XML parsing prohibits DTD/external entity
resolution and handles the leading UTF-8 BOM found in committed projects.

All branches/conditions in repository metadata are inspected conservatively.
This is **not evaluated MSBuild or generated-output proof**. SDK/package imports,
external directory imports, property evaluation, custom tasks, command-line
overrides and runtime build behavior are not executed or certified. Property
references accepted as standard output roots rely on the inspected definitions
and normal SDK behavior; external overrides remain outside scope. A future
build-output guarantee requires a separate controlled build and output inspection.

## Commands and observed result

Run from the AppHost repository with PowerShell 7 and local Git objects:

```powershell
pwsh -NoProfile -File scripts/tests/test-generated-documentation-isolation.ps1
pwsh -NoProfile -File scripts/verify-generated-documentation-isolation.ps1 `
  -SourceRepository //maliev/repository/maliev-web `
  -WorkspaceRoot B:/maliev-legacy `
  -ManifestPath contracts/source-03dc9a1-documentation-owners.json
```

Observed on 2026-09-07: 27 standalone behavioral cases passed. The pinned audit
returned `committed-metadata-pass`: 88 source projects accounted for, including
3 retired projects, and 95 target projects / 113 build-metadata files across
18 repositories inspected. No target code changes were needed.

The synthetic suite exercises safe SDK/intermediate output, root/traversal/unknown
output failures, root generated-copy/publish failures, unrelated XML/assets,
tracked assembly XML, malformed XML/DTD, props/targets, BOM, empty inventory,
missing repository/commit and ownership failures. Red runs were observed before
implementation for the unsafe output, unknown-owner, BOM and asset-subtree cases.

AST parsing and `git diff --check` are the applicable static checks. .NET build,
service suites and database/container tests are not applicable to these standalone
metadata scripts; no .NET source/project was changed and no shared dependency was
built. This evidence can support the parent's reviewed source classification; the
script deliberately does not edit source registers, issues, or approval state.

## Superseding owner retirement overlay (2026-10-03)

The historical 88-project manifest and its 18 repository pins remain unchanged.
`contracts/source-03dc9a1-retirement-overlay.json` records the later explicit owner
approvals for SwaggerAuthorized and the three Prediction projects, without
rewriting the historical three-retired-project result. Modern OpenAPI/Scalar,
other Country behavior, and all other 84 projects are outside this overlay.

Root observed the no-op scaffold's two real-manifest regressions fail for the
intended behavior: historical/effective counts were 3/3 instead of 3/4, and
Swagger remained architecture-equivalent. Root also observed all original 27
standalone cases pass. This is RED evidence, not completed validation.

The optional `-RetirementOverlayPath` now requires the working manifest's actual
Git blob to equal `2f0a015e44382f9485e0285d5db7d640f921334e`. Read-only
`git hash-object --path` applies repository normalization; the verifier then reads
that immutable committed blob for parsing, rather than rereading a mutable file.
The overlay's own declared identity cannot substitute for this observation.
Only four exact, unique projects with exact historical ownership/dispositions
and approved scope-specific reasons/URLs may be projected. No input is mutated.
Unknown fields, missing projects, changed history, wrong authority and changed
historical files are rejected, including an actual CLI file-identity regression.

Existing invocations without the overlay keep their historical report schema and
retired count of 3. Overlay invocations additionally report historical count 3,
effective count 4, immutable identity, and four bounded approval records; the
other 84 projects and all 18 repository pins remain unchanged. This supersedes
classification only, not runtime, evaluated build, deployment or data acceptance.
The required build-and-test workflow adds the standalone suite without changing
any dependency pin, .NET validation, snapshot test, image or manifest check.
Root executed the standalone suite: 27 historical and 27 overlay cases passed,
including the actual CLI historical-file rejection. The intentional child failure
is captured and asserted without leaking its native exit status into CI.
Both committed metadata audits passed for all 88 source projects and 18 pinned
repositories: the legacy report retains three retirements; the overlay adds the
effective count of four. PowerShell AST parsing, workflow actionlint, owned-file
redacted secret scans and `git diff --check` passed. The historical manifest has
no diff. .NET build, service/runtime suites and database operations are not
applicable to this metadata-only slice; no C# or project/runtime input changed.
Required PR and post-main CI remain pending; these local checks are not runtime
migration acceptance or a claim that the overall migration is complete.
