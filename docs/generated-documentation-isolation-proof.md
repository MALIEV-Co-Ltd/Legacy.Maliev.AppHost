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
