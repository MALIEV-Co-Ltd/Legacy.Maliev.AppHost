# Certificate troubleshooting: owner review guide

Proposed documentation adaptation of original `a5b9ee39149c64d7649b708e894520f788cdfb61`, parent `3464a4fd4d966d32493ed8de27bcf99a681fe459`, sole path `Maliev.CertManager/TROUBLESHOOTING-GUIDE.md`, blob `7ecc4db992d9a102c62fc5cd81221ec1ff2e8cdd`. Target review base: AppHost `ef5532fe5eae206ee280b73eaaf7cf63cdb8ce51`.

This document is an inert proposal. No command below has been executed by this adaptation. Observations do not establish operational acceptance: `evidenceOnly=true`, `runtimeAcceptanceProven=false`, `productionDeploymentAllowed=false`. Application, ingress, namespace, certificate and account ownership require the existing [owner review checklist](owner-review-checklist.md). No source operation is retired here. Historical mutation and emergency actions remain deferred to the responsible owner under separate authority.

## Quick Diagnosis

First identify the owner-approved namespace and certificate; collect supplied public observations, not private key contents. Inventory certificates, challenges, requests sorted by age, Orders, ingress and issuer. Describe the specific resources and correlate owner references and events before drawing conclusions. For certificate expiry, decode only `tls.crt` and inspect public certificate dates; never collect or print `tls.key`, credentials or account secrets.

Historical read-only examples, with placeholders requiring owner binding:

```text
kubectl get certificates -n <approved-namespace>
kubectl describe certificate <certificate> -n <approved-namespace>
kubectl get challenges -n <approved-namespace>
kubectl describe challenge <challenge> -n <approved-namespace>
kubectl get certificaterequests -n <approved-namespace> --sort-by=.metadata.creationTimestamp
kubectl describe certificaterequest <request> -n <approved-namespace>
kubectl get orders -n <approved-namespace>
```

AppHost's existing `scripts/review-certificate-status.ps1` consumes supplied offline evidence, not a Kubernetes collector. Its schema is closed, input is bounded to 32 KiB, and explicit UTC as-of time is required. A reviewed DLL SHA and matching provenance must be supplied; output is created without overwrite. Do not infer collection or a running monitor from the CLI.

## Certificate Status Analysis

Retain the original Ready, Issuing/Renewing and Failed distinction. A healthy certificate has Ready evidence and a future `notAfter`; `renewalTime` is a separate observation. The historical 90-day lifetime and 30-day renewal example is not a current configured lifetime. Failed state requires the associated request, challenge, issuer and event explanation. Missing expiry is Unknown, not healthy; expiry at or before as-of is Expired.

Offline alerts retain strict thresholds: less than 7 days is Critical, less than 30 days is Warning, otherwise None; absent expiry is Unknown. False issuer readiness means inspect issuer configuration; absent readiness means obtain evidence. Challenge failure means inspect challenge and ingress; missing challenge observations remain Unknown. Schema 2 pending observations older than 10 minutes are Info, and failure counts greater than 3 are Critical. Neither threshold authorizes renewal or ingress changes.

## Challenge Debugging

HTTP-01 uses `http://<host>/.well-known/acme-challenge/<token>`. Preserve the token and expected `<TOKEN>.<ACCOUNT_KEY_HASH>` response relationship without retaining real account material. Observe challenge state/reason, ACME solver pod existence, readiness and logs, Service/endpoints and injected ingress path. A reachable endpoint must return the expected challenge response, not an application page, redirect loop or 404. HTTP-01 validation and controller self-check must both be considered; see [cert-manager ACME troubleshooting](https://cert-manager.io/docs/troubleshooting/acme/).

DNS-01, including wildcard issuance, requires the expected `_acme-challenge` TXT record and authoritative/public propagation. Inspect the configured DNS provider and its authorized access through the owner; this guide neither creates access nor requests secret contents.

For 404, inspect challenge path injection, solver backend and ingress class/URL map. For connection timeout, inspect public DNS, load-balancer address, firewall/reachability and backend health. For a wrong status or wrong body, distinguish redirects, TLS handshake/self-check problems and traffic reaching the wrong application. Bind observations to the actual challenge and host rather than inferring success from a different certificate.

## Ingress Configuration Issues

The source External ALB example used GCE class, a reserved static IP reference, issuer annotation, TLS for `api.maliev.com`, and `kubernetes.io/ingress.allow-http: false`. Preserve that as historical intent; it is not an apply-ready target template. The current inert edge renderer instead retains GCE class, `allow-http: true`, and FrontendConfig `legacy-maliev-https-redirect`. HTTP reachability and HTTPS redirection must be reviewed together for HTTP-01. Refer to [GKE ingress documentation](https://docs.cloud.google.com/kubernetes-engine/docs/how-to/load-balance-ingress).

Current review names are namespace `maliev-legacy`, ingress `legacy-maliev-edge`, issuer `legacy-maliev-letsencrypt-prod`; the static IP name must be an owner-provided existing resource, never a newly allocated address. Renderer hosts are `www.maliev.com`, `maliev.com`, `api.maliev.com`, and `intranet.maliev.com`, with host-derived `-tls` secret names. These names describe the reviewed artifact, not deployed resources.

The separate source `line-chatbot.maliev.com` / `line-chatbot-ingress` / `line-chatbot-tls` remains bound to source namespace `maliev`; ownership is unverified and runtime registration is false. Do not blanket-map it into `maliev-legacy`.

Compare ingress status/IP with owner-confirmed DNS (`nslookup` or equivalent); inspect the ACME path and solver backend. The source annotation removal/re-addition to force refresh, including its redirect annotation, is a deferred historical mutation, not an approved GCE remedy. Require provider-specific diagnosis and owner review before any annotation change.

## ClusterIssuer Problems

Inventory and describe the issuer, its Ready condition, events, ACME server and account reference. Not Ready requires the exact event/reason, email/account binding and server reachability; wrong solver requires matching ingress class or DNS solver selection. The source production and staging templates retain `https://acme-v02.api.letsencrypt.org/directory` and `https://acme-staging-v02.api.letsencrypt.org/directory`, respectively, owner-confirmed email, `privateKeySecretRef` name and HTTP-01 GCE solver. No real email or account key is supplied here, and staging is not production acceptance.

Retain the original named-ingress solver binding `http01.ingress.name=ingress-api`. Current review normalization binds HTTP-01 to `legacy-maliev-edge` for the four general hosts and to `line-chatbot-ingress` for the chatbot DNS-name selector. A DNS-name selector chooses a solver for a host; it does not turn that solver into DNS-01. Inspect the named ingress as well as its class. The chatbot's source-namespace ownership remains unverified, so this binding is an inert review obligation, not an authorized registration.

The source's generic 50-certificates-per-domain warning needs scope: the current limit for new certificates is per registered domain over seven days, and renewal treatment has additional rules. Preserve rate-limit diagnosis, distinguish account/order/authorization failures and use the actual response and retry information. The authoritative [Let's Encrypt rate limits](https://letsencrypt.org/docs/rate-limits/) document recommends staging during testing. This proposal does not provision or register an issuer/account.

## Google Cloud Load Balancer Issues

Retain the source External Application Load Balancer migration diagnosis: backend Service ports/endpoints and health, URL-map host/path routing, health-check configuration and results, and ingress propagation. The historical 10–15-minute propagation estimate is context, not a deadline or proof of convergence. Compare time-stamped actual state before attributing an ACME failure to propagation.

Historical read-only command families, requiring exact owner-confirmed project and resource names:

```text
gcloud compute backend-services list --project=<project>
gcloud compute backend-services get-health <backend> --global --project=<project>
gcloud compute url-maps list --project=<project>
gcloud compute url-maps describe <url-map> --global --project=<project>
gcloud compute health-checks describe <health-check> --project=<project>
```

No current GCP resource ownership, backend health or DNS correctness is claimed. Listing/inspection is not authorization to change cloud resources.

## Manual Certificate Renewal

Preserve the original operation inventory: inspect failed/stuck Challenge, CertificateRequest and Order; delete selected failed resources; annotate or delete/recreate Certificate; watch new requests/challenges/events; install/use Certbot with manual DNS verification; update the Kubernetes TLS secret from issued certificate/key files. All deletion, reissue, installation, DNS and secret writes are deferred owner-only operations and have not run. The source broad `delete challenges --all` is retained as a historical operation requiring scope review, not a recommendation for a shared namespace.

The source `cert-manager.io/issue-temporary-certificate` annotation creates temporary certificate behavior; it is not a general renewal trigger. Current [cert-manager certificate guidance](https://cert-manager.io/docs/usage/certificate/) documents `cmctl renew` for manual renewal. Neither mechanism is authorized here. Do not infer that deleting a certificate or secret is necessary or safe from an offline Failed observation.

The Certbot path `/etc/letsencrypt/live/<host>/` and `kubectl create secret tls` pattern are historical custody references only. Never attach private keys to this guide or transfer credential-bearing files through it. An owner-authorized renewal would require issuance identity, challenge ownership, actual API/readback and restored HTTPS evidence; offline schema checks cannot substitute.

## Emergency Procedures

For expiry less than 24 hours, retain immediate expiry diagnosis, owner escalation, renewal decision and the source five-minute observation cadence. This is a proposed incident procedure, not a scheduled task. If automation fails, retain manual DNS/Certbot escalation and rollback preparation with the owner; this proposal cannot sign a certificate or alter DNS.

The source outage fallback removes HTTPS redirect and TLS before restoring HTTPS. Retain this as a deferred historical security-changing operation; it is not authorized or recommended for execution by this adaptation. Any owner-approved incident change requires explicit impact review, rollback criteria, restoration of TLS and redirects, and end-to-end HTTPS evidence. Do not equate continued HTTP reachability with certificate recovery.

## Preventive Monitoring

Retain the source certificate inventory for API, www and intranet, including expiry and issuer/challenge state. The original expiry and challenge shell scripts are represented by supplied observations; no collectors, cron jobs or schedules are installed. Retain the ad-hoc 30-second watch and incident five-minute checks as historical observation cadences only.

Daily: inspect expiry, pending/failed challenge counts and issuer readiness. Weekly: review renewal success, load-balancer health and DNS configuration. Monthly: review cert-manager logs, proposed cert-manager updates and disaster recovery checks. An update or recovery drill requires its own owner authority.

Exact alert boundaries remain less than 7 days Critical, less than 30 days Warning, pending longer than 10 minutes Info, more than 3 failed challenges Critical. Source shell integer flooring is not the current precise UTC diagnostic calculation. Missing observations remain Unknown; no fabricated inventory or zero failure count is accepted.

## Common Error Messages and Solutions

| Original error | Preserved diagnosis and deferred action |
| --- | --- |
| `wrong status code '404', expected '200'` | Inspect ingress ACME path and load-balancer routing; verify solver response. |
| `Waiting for HTTP-01 challenge propagation` | Inspect actual endpoint/self-check and propagation; source 5–10-minute wait is historical, followed by fresh evidence. |
| `Failed to retrieve Order resource: 404` | Inspect Order identity, issuer/account and events; original restart/recreate remains a deferred owner mutation. |
| `Certificate request has failed to complete` | Inspect the related Challenge, request conditions/events and root cause before renewal. |
| `The ACME account was not found` | Inspect issuer email, ACME server and account reference; account creation/reconfiguration remains deferred. |

## Useful Commands Reference

Read-only families preserved above: inventory/describe certificates, requests, challenges, Orders and ingress; inspect public expiry; solver logs/readiness; ACME endpoint response; DNS; backend/URL-map/health checks; time-stamped watches and events. Mutation families preserved for owner review only: annotation refresh, selected cleanup and broad challenge cleanup, renewal/recreation, manual DNS/Certbot issuance, TLS secret writes, temporary HTTP/TLS fallback and restoration. No executable scripts or YAML manifests are produced by this document.

## Additional Resources

Use the primary references above and [Kubernetes ingress concepts](https://kubernetes.io/docs/concepts/services-networking/ingress/). The source last-updated August 2025 marker is historical provenance, not the review date. This proposal requires owner adoption, complete source coverage review and bounded documentation checks before any guide-level disposition changes. Existing diagnostic native results apply only to unchanged diagnostic components. Original source accounting remains unresolved; no whole-feature acceptance or source-count change is asserted.
