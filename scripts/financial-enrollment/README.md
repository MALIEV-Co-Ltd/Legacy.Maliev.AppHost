# Explicit financial IAM operator tool

This tool is separate from AppHost startup. It targets the reviewed ordinary IAM `4fe6642e5674013de9a3672505aec898fdcae0ed` API; it never seeds SQL, creates catalogue rows/roles, elevates an operator, adds wildcard permissions or deletes principals. Implementation and offline tests do not authorize or establish live execution.

`invoke-financial-enrollment.ps1` reads an operator-owned JSON configuration with:

| Field | Required value |
|---|---|
| `IamOrigin` | Exact approved HTTPS origin, without a trailing slash/path/query/userinfo. Only `Environment=Testing` permits HTTP loopback. |
| `Environment` | Selected deployment environment. |
| `OperatorPrincipalId` | Existing approved operator's server-created GUID, matching the supplied JWT subject. IAM still validates signature and authority. |
| `RoleId` | Existing explicitly approved role whose permission list equals exactly `legacy.accounting-financial-ownership.read`. No platform/workload/wildcard roles. |
| `ExpiresAtUtc` | Owner-selected future UTC expiry. No implicit indefinite binding. |
| `ApproveGlobalFinancialScope` | Explicit `true`, because the current Accounting financial route checks global scope. No invented operation path or `*` scope. |
| `SourceRevisions` | Owner-reviewed full commit strings for `Auth`, `Accounting`, `Defaults`, `IAM`. These are declared metadata, not remote deployment attestation. IAM must equal the supported revision above. |

Supply the existing operator bearer through the private process environment `MALIEV_FINANCIAL_ENROLLMENT_OPERATOR_TOKEN`; do not place it in configuration, command arguments or receipts. Actual source compatibility, TLS trust, caller admission and live-check credential enrollment remain owner prerequisites. The tool does not launch or reconfigure IAM, Auth, Accounting, RabbitMQ or Redis.

Invocation forms (shown for review only; none executed here):

```powershell
./scripts/financial-enrollment/invoke-financial-enrollment.ps1 -ConfigurationPath <approved-config.json> -Mode Plan -ReceiptPath <new-private-plan-receipt.json>
./scripts/financial-enrollment/invoke-financial-enrollment.ps1 -ConfigurationPath <approved-config.json> -Mode Apply -PlanHash <plan-receipt.Result.PlanHash> -ReceiptPath <new-private-apply-receipt.json>
./scripts/financial-enrollment/invoke-financial-enrollment.ps1 -ConfigurationPath <approved-config.json> -Mode Verify -ReceiptPath <new-private-verification-receipt.json>
./scripts/financial-enrollment/invoke-financial-enrollment.ps1 -ConfigurationPath <approved-config.json> -Mode Revoke -PlanHash <original-apply-plan-hash> -AppliedReceiptPath <original-private-apply-receipt.json> -ReceiptPath <new-private-revoke-receipt.json>
```

Plan reads the exact financial catalogue, selected role, and two canonical service-account emails. It records existing server GUIDs/bindings or verified absence. Apply regenerates that plan and requires its exact SHA-256 before any POST. Changed state requires a new plan. Missing principals are created with only `principalType=service_account` and the exact source-derived email; no caller-supplied GUID or linked entity. The server-generated ID is recorded and read back before the binding POST. Binding bodies contain only the approved role, null global scope and finite expiry.

Only `legacy-auth` and `legacy-quotation` are targets, resolving unchanged ordinary subjects through `service:legacy-auth@serviceaccount.maliev.local` and `service:legacy-quotation@serviceaccount.maliev.local`. Unknown existing bindings, inactive/mismatched principals, missing catalogue, extra role permissions and denied/ambiguous responses stop execution. Normal IAM `BindingService` retains its managed-workload rejection. Existing reusable bindings are verified without being claimed as tool-created.

Revoke deletes only binding IDs recorded as created by the supplied apply receipt, after current principal and binding ownership readback. It leaves principals/catalogue/roles and pre-existing bindings alone. `BindingAlreadyAbsent` means the exact binding is absent from IAM's active-only role list. It does not prove physical deletion of an expired or inactive database row; the tool issues no DELETE in this case. `RevocationReadbackVerified` likewise reports active-list absence, not database erasure. Partial failures retain both an exclusive final receipt and a flushed `.events.jsonl` journal, then require reconciliation; there is no blind retry or broad rollback. Unknown server outcomes are not guessed. Do not delete these receipts as helper cleanup.

Immediately before each mutation, the coordinator rechecks current UTC time with a twenty-second safety budget for the request. Requests have ten-second header/body deadlines, a 64-KiB response bound and disabled redirects. Clients, streams, cancellation sources and receipt handles are disposed in `finally`. Receipts contain service GUIDs and operation metadata, never bearer/live-check secrets or raw API bodies. A successful binding readback is not genuine Auth-issued financial HTTP acceptance, live revocation proof or event-delivery proof; those remain separately required.

The component runner is `scripts/tests/financial-enrollment-component-tests.ps1`. Its injected in-memory transport covers ordinary request shapes and fail-closed controls without HTTP, SDK builds or native helpers. The CLI cleanup runner, `scripts/tests/financial-enrollment-cli-cleanup-tests.ps1`, verifies malformed RoleId/IamOrigin configurations dispose receipt, journal and HttpClient resources, permit exclusive file reopening and remove owned temporary files. Full repository build/tests/static checks and independently reviewed genuine IAM/Accounting integration are still required before this source slice is committed or published.
