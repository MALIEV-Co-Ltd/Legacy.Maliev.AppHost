# Web backend source-contract review

Schema five is an explicit, inert extension of the accepted edge review renderer. The default API and CLI still return schema one. Schemas one, two, three and the separate supplied-observation schema four remain unchanged.

The gap is a missing producer contract, not evidence of a failed live route: the old package referenced Web port 8080 in Ingress but emitted no Service or BackendConfig association. Schema five renders the public Service port 8080 to targetPort 8080, a single port-scoped backend association, and the corresponding BackendConfig. Both public Web hosts reference that same Service and port.

## Source and final behavior

Original test commit f0b3422f23ff037c3f8f91f0338afcab2202e733, parent e27008a3f323a92dbf05076d93ac5c4f4d3f3e1e, changed only Maliev.Web.Tests/TechnicalSeoTests.cs (3db540910a1f38366066970a1dd659af1861e14e to 36a6926fded7d6143d1a9e272c51df888153a340). It strengthens the edge redirect, Service 8080-only backend association, and then-current HSTS/forwarding contract.

At final source edf4e45d7ffce2f55b0d607ca82d51e81c330781:

- Maliev.Web/service.yaml, blob 603d98cc37b09355eb8219835187df84faf4aa77, still binds port 8080 to targetPort 8080 and associates its backend only with that public port.
- Maliev.Web/deployment.yaml, blob bc840ee931dcd45d4b9a1788f27963962c8ad453, uses source label run: maliev-web and container port 8080.
- Maliev.Web/backendconfig.yaml, blob 5b17aa9410ff1cb82ce0a07a35c40be4948e0859, retains timeoutSec 120 but no longer adds browser security headers.
- Maliev.Web.Tests/TechnicalSeoTests.cs, blob ed42caf9765a8b7ea19eb190b009532f9dca795d, explicitly rejects a duplicate BackendConfig HSTS header because final application SecurityHeaderPolicy.cs, blob 3a0a9af2547b17d8588af84f9a51d5c129293967, owns it.

The final behavior supersedes the older BackendConfig HSTS assertion; schema five must not restore that obsolete header. It makes no change to Web forwarding policy or the separately tracked Web #744 correction.

## Deliberately incomplete adoption

The Service and BackendConfig names use the accepted edge review's legacy-maliev-web naming and maliev-legacy namespace. These remain proposed review names, not proof of deployed ownership. The source run label is provenance, not a newly verified target selector. The Deployment port fragment is explicitly labeled incomplete and is not a valid full Deployment; it omits image, credentials, service account, environment, replica/resource/probe policy and other workload fields rather than inventing them. The target image is null.

All selector/namespace/backend adoption and runtime-acceptance fields remain false. AppHost #33 still gates owner Aspire review and any deployment. No listener, workload, cloud resource, image publication, permission, data operation or live edge inspection is added.

## Opt-in consumption

After a normal reviewed Release build, the existing scripts/write-edge-review-package.ps1 accepts -SchemaVersion 5 with the same explicit reviewed-assembly SHA-256 and linked portable-PDB/source check. It continues to reject an unrelated supplied-observation file for schema five and uses exclusive output creation. Existing optional inert installer text export has the same semantics as schema three. No command in this document has been executed as part of authoring.

Registered tests exercise the real renderer's cross-object port/name/selector relationships and final header policy, incomplete-fragment and unverified-adoption fields, prior schema isolation, invalid owner inputs, and the actual CLI's reviewed-hash, observation and no-overwrite refusals. Native RED/GREEN, Release, focused/full, formatting, coverage and security checks are not run in this source-only proposal. No original source SHA is closed by a review model alone.
