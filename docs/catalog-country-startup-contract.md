# Catalog Country startup ordering

Catalog uses the preserved Country database through `CatalogCountryDbContext` as
well as its Material store. Waiting for Material migration, PostgreSQL availability,
Redis and Auth does not establish that Country migration has completed. The AppHost
now declares `WaitForCompletion(countryMigrations)` directly on Catalog, alongside
its existing Material migration dependency. A failed or incomplete Country migration
therefore cannot satisfy the declared startup prerequisite.

This change keeps the existing pooled Catalog connection and direct Country migration
connection. It adds no migration, identity, provider configuration or executable
operation. The composition tests inspect real Aspire wait annotations, exact database
bindings, retained healthy dependencies and the absence of a dependency cycle in
Development and Production. They do not start the graph or contact a database.

## Source and consumer boundaries

- Accepted AppHost baseline: `3ad5eb9db7684f4b11ca8cee5c51254aadf34719`.
- Existing hosted Catalog pin: `de4b3364e924cca1aeebb3280817f0c9bd7ea6bc`.
  Its `Legacy.Maliev.CatalogService.Api/Program.cs` registers `CountryDbContext`;
  `Legacy.Maliev.CatalogService.Data/CatalogRepository.cs` consumes it.
- Existing hosted Country pin: `01113637c751c7bea1354517be7f8b56a1637bb9`.
- AppHost production path: `Legacy.Maliev.AppHost/AppHost.cs`.
- Regression path: `Legacy.Maliev.AppHost.Tests/AppHostCompositionTests.cs`.

The retained original-source checkpoint is
`135e526d0dab85c415b3afdcefd7b70fe2c82e2f`. This fixes a concrete dependency gap
in its migrated runtime graph; it does not close any whole original commit or
change the retained source-obligation ledger. In particular, the separate solution
graph obligation `72eb9f1949176392141951d35e6e06f7c30af4c2`, parent
`023aa2f143fa5a8f557d72c1302b9add64792d6a`, original path `Maliev.sln`, still
requires its own project/configuration dispositions.

## Validation and acceptance limits

Local Release build, focused tests, full suite and formatting were not run: this
task has no finite native-validation admission lease on the shared host. Use the
owner-approved migration validation-order exception for the draft PR. Hosted
validation must run on the exact candidate head: Release build with zero warnings
and errors, four focused composition cases, full suites, unchanged formatting,
security, coverage and manifest gates. The focused TRX is retained with the ordinary
Ubuntu evidence artifact. A green historical baseline is not candidate validation.

Actual startup against persistent or restored data, data/schema acceptance,
deployment and the stopped financial task remain outside this slice. No runtime
success or overall source parity is established by inspecting the graph.
