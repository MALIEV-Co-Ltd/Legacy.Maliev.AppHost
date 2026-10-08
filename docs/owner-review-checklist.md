# Legacy Aspire owner review and release gate

**Current decision: RELEASE BLOCKED — cutover 0%.** This package is for local Aspire review only. It does not authorize publishing images, synchronizing Argo, changing GKE, modifying ingress or DNS, replacing a production site, or writing production data.

The machine-readable gate state is [owner-review-evidence.json](owner-review-evidence.json). `OwnerReviewPackageContractTests` validates the immutable completed evidence and deliberately requires every unresolved release gate to remain false. A gate may be changed only in a separately reviewed slice that links direct evidence and updates the contract tests.

## Frozen review baseline

- [x] AppHost baseline: `7bf160457cc984b753609f9f7a43e45a23d7168f`, [exact-main CI](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.AppHost/actions/runs/29647507804).
- [x] Issue #56 service pins remain unchanged by this package.
- [x] Intranet: `dfb99b3b3af5b7fd6ed2d6d8bf93379495df68e1`, 486 tests, [exact-main CI](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.Intranet/actions/runs/29643178549).
- [x] DocumentService: `a56a2cadb55aba93026cb5b7dbb8bb0e94597df5`, 83 tests, all 22 immutable PDFs at 150 DPI including Thai tone-mark crops, [exact-main CI](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.DocumentService/actions/runs/29651958706).
- [x] Dormant GitOps PostgreSQL readiness: `a7eb1c48a320669eceaf92011d3a08b06a17be23`, 26 contract tests, [validation CI](https://github.com/MALIEV-Co-Ltd/maliev-gitops/actions/runs/29650766400). Nothing was synced to the cluster.
- [x] [Repository audit](https://github.com/MALIEV-Co-Ltd/maliev-web/issues/14#issuecomment-5011696569): all 20 extracted legacy repositories are public and protect `main` with `validate / validate`, admin enforcement, and zero required approvals.

## Owner review sequence

Perform this review only against a source identity shown in the Aspire dashboard and Web response headers. Do not stop or replace the existing listener on port 5088 as part of this package; use the non-destructive identity/preflight process documented in the README and coordinate any later handoff separately.

### 1. Local topology and workflows

- [ ] Confirm every retained service is healthy or produces the documented bounded failure in Aspire.
- [ ] Confirm Web and Intranet show their exact source commit/build identity.
- [ ] Re-run the full local verifier with disposable PostgreSQL/Redis data and attach its machine-readable result.
- [ ] Review auth access/refresh rotation, Web and Intranet BFF cookies, CSRF-protected writes, SignalR, messaging, health/readiness, resilience, cache behavior, upload scanning/finalization, and stateless PDF rendering.
- [ ] Confirm local placeholders cannot send provider email, create a real GCS object, or emit a production conversion.

### 2. Instant Quotation and upload release blockers

- [ ] Close Web [#148](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.Web/issues/148) only after the full production-parity epic is verified.
- [ ] Close Web [#149](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.Web/issues/149), [#150](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.Web/issues/150), [#151](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.Web/issues/151), [#152](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.Web/issues/152), and [#153](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.Web/issues/153) with browser, contract, fixture, localization, accessibility, consent, analytics, and Aspire evidence.
- [ ] Close FileService [#7](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.FileService/issues/7) after the upload session, signed transfer, malware quarantine, finalization, idempotency, ownership, GCS, and failure contracts pass and Web integration consumes that exact contract.

### 3. Thai address and company lookup release blockers

- [ ] Complete AppHost [#144](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.AppHost/issues/144) and Catalog [#44](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.CatalogService/issues/44), [#45](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.CatalogService/issues/45), and [#46](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.CatalogService/issues/46) with merged implementation revisions and joined evidence. The `thaiAddressAndCompanyLookup` pending gate in the machine-readable package must remain false until separately reviewed evidence and matching contract-test changes are recorded.
- [ ] Verify actual Catalog discovery/client configuration, separate LocationData connection if selected, initializer ordering, health/readiness and least-privilege workload permissions. Keep existing customer/staff save permissions, trusted-field locks and CSRF protections. Web public-form lookups must use bounded, rate-limited same-origin server calls with server-held identity; browser/customer principals receive no Catalog staff permissions.
- [ ] Test Production environment dataset initialization safely using disposable non-production data, reviewed provenance, source/license/version/hash and retained-source reconciliation. Demonstrate controlled refresh and version rollback. Local snapshot restoration alone does not satisfy initialization or API acceptance.
- [ ] Verify province-first and postcode-first narrowing with all supplied constraints combined, string codes/postcodes and complete pagination; changing parents clears incompatible children while preserving detail. Verify Thai/Bangkok/mixed-language plaintext, conflict and ambiguity handling without selecting an arbitrary first candidate or inferring uniqueness from a truncated preview.
- [ ] Demonstrate actual Web and Intranet address/company selection and manual correction, existing persistence contracts, save and readback, localization, keyboard access and stale asynchronous-result protection. Include applicable customer/billing/shipping, supplier, employee and supported quotation flows; coordinate the Intranet relation editors with [#262](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.Intranet/issues/262).
- [ ] Verify unauthorized access, dataset/provider unavailability, unsupported company capability, bounded request/response/timeout behavior and manual-entry fallback. Confirm Creden production usage rights and supported provider contract before live enablement; unavailable company facts remain null. Keep credentials and full customer addresses out of committed evidence.
- [ ] Attach exact-revision service tests and real joined HTTP/browser evidence to #144 and this release review #33. Proposed DTOs, mocks and a healthy service alone do not prove these flows. Retain release blocked and cutover 0% until every prerequisite and explicit owner approval are satisfied.

### 4. Google malware, measurement, and search checks

- [ ] Confirm container scanning and every GTM destination are clean; remove or disable any malware-flagged destination before retest.
- [ ] Confirm Google Safe Browsing reports the reviewed public host clean.
- [ ] Confirm Search Console Security Issues is clean and the reviewed sitemap, canonical, `hreflang`, and ownership state are correct.
- [ ] Use GTM Preview to prove consent defaults deny storage and accepted consent enables only the intended tags.
- [ ] Use GA4 DebugView and Google Ads diagnostics to prove each approved conversion fires exactly once, with no Aspire/local event sent into production reporting.
- [ ] Attach screenshots or exports that identify the host, timestamp, container/property, consent state, event, and result to Web #152 and program Wave 7 [#12](https://github.com/MALIEV-Co-Ltd/maliev-web/issues/12).

### 5. Existing-cluster CloudNativePG gate

- [ ] Record schedulable CPU, memory, ephemeral storage, persistent-volume capacity, and disruption headroom in the existing GKE cluster. Do not add a node pool.
- [ ] Prove backup/WAL recovery into the dormant `legacy-postgres-recovery-rehearsal` cluster in `maliev-legacy` without reading from or writing to production databases outside the approved recovery path.
- [ ] Record schema, row-count, checksum, identity, and application shadow parity before final sync.
- [ ] Rehearse database and service rollback, including the maximum acceptable recovery point and recovery time.
- [ ] Keep `--require-cutover` failing closed until capacity, recovery, parity, rollback, this Aspire review, and explicit owner approval are all recorded.

### 6. Rollback and approval

- [ ] Record the prior immutable image digest and manifest for every service.
- [ ] Rehearse service rollback without changing schema ownership or the source-of-truth databases.
- [ ] Rehearse PostgreSQL rollback/recovery and verify no writes can diverge silently during the cutover window.
- [ ] Rehearse ingress/DNS/site rollback and GTM/GA4/Ads measurement rollback.
- [ ] Confirm the existing cluster and `maliev-legacy` namespace can carry the migration with no Cloud SQL, new node pool, load balancer, disk, paid runner, or other additional infrastructure cost.
- [ ] Owner records explicit production approval in AppHost [#33](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.AppHost/issues/33). Until then, production deployment, ingress/DNS changes, and site replacement remain prohibited.

## Approval record

- Owner: pending
- Review date: pending
- Reviewed AppHost commit: pending
- Aspire evidence URL: pending
- Rollback evidence URL: pending
- Google diagnostics evidence URL: pending
- Existing-cluster capacity evidence URL: pending
- Decision: **not approved**
