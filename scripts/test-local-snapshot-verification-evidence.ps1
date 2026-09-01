[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$EvidencePath,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{40}$')] [string]$ExpectedAppHostCommit,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{40}$')] [string]$ExpectedSourceCommitSha,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$')] [string]$ExpectedSnapshotId,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{64}$')] [string]$ExpectedManifestDigestSha256,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{64}$')] [string]$ExpectedMigrationEvidencePayloadSha256,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{64}$')] [string]$ExpectedApprovedBaselineSha256,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{64}$')] [string]$ExpectedRepositoryBaselineSha256,
    [ValidateRange(1, 1440)] [int]$MaximumAgeMinutes = 30
)

$ErrorActionPreference = 'Stop'

$terminalJobs = @(
    'legacy-country-migrations', 'legacy-auth-migrations',
    'legacy-customer-identity-migrations', 'legacy-employee-identity-migrations',
    'legacy-customer-migrations', 'legacy-employee-migrations', 'legacy-catalog-migrations',
    'legacy-supplier-migrations', 'legacy-purchase-order-migrations', 'legacy-file-migrations',
    'legacy-order-migrations', 'legacy-order-status-migrations', 'legacy-quotation-migrations',
    'legacy-quotation-request-migrations', 'legacy-career-migrations', 'legacy-contact-migrations',
    'legacy-payment-migrations', 'legacy-invoice-migrations', 'legacy-receipt-migrations',
    'legacy-contact-request-snapshot', 'legacy-currency-snapshot',
    'legacy-data-protection-keys-snapshot', 'legacy-data-protection-keys-employee-snapshot',
    'legacy-location-data-snapshot', 'legacy-log-archive-snapshot'
)
$services = @(
    'legacy-maliev-country-service', 'legacy-maliev-document-service', 'legacy-maliev-auth-service',
    'legacy-maliev-customer-service', 'legacy-maliev-employee-service', 'legacy-maliev-catalog-service',
    'legacy-maliev-procurement-service', 'legacy-maliev-file-service', 'legacy-maliev-order-service',
    'legacy-maliev-quotation-service', 'legacy-maliev-notification-service', 'legacy-maliev-web',
    'legacy-maliev-intranet-bff', 'legacy-maliev-career-service', 'legacy-maliev-contact-service',
    'legacy-maliev-accounting-service'
)
$repositories = @(
    'Legacy.Maliev.AppHost', 'Legacy.Maliev.AccountingService', 'Legacy.Maliev.AuthService',
    'Legacy.Maliev.CareerService', 'Legacy.Maliev.CatalogService', 'Legacy.Maliev.CompatibilityContracts',
    'Legacy.Maliev.ContactService', 'Legacy.Maliev.CountryService', 'Legacy.Maliev.CustomerService',
    'Legacy.Maliev.DocumentService', 'Legacy.Maliev.EmployeeService', 'Legacy.Maliev.FileService',
    'Legacy.Maliev.Intranet', 'Legacy.Maliev.NotificationService', 'Legacy.Maliev.OrderService',
    'Legacy.Maliev.ProcurementService', 'Legacy.Maliev.QuotationService',
    'Legacy.Maliev.ServiceDefaults', 'Legacy.Maliev.Web'
)
$migratedDatabases = @(
    'ContactRequest', 'Country', 'Currency', 'Customer', 'CustomerIdentity', 'DataProtectionKeys',
    'DataProtectionKeysEmployee', 'Employee', 'EmployeeIdentity', 'Invoice', 'JobOffers',
    'LocationData', 'Log', 'Material', 'Message', 'Order', 'OrderStatus', 'Payment',
    'PurchaseOrder', 'Quotation', 'QuotationRequest', 'Receipt', 'Supplier', 'Upload'
)
$runtimeDatabases = @($migratedDatabases) + @('Auth')

function Assert-ExactKeys {
    param([System.Collections.IDictionary]$Value, [string[]]$Expected, [string]$Path)
    $actual = @($Value.Keys | ForEach-Object { [string]$_ } | Sort-Object)
    $wanted = @($Expected | Sort-Object)
    if ($actual.Count -ne $wanted.Count -or (Compare-Object $wanted $actual -CaseSensitive)) {
        throw "$Path contains missing or unknown fields."
    }
}

function Assert-NoSensitiveKeys {
    param([AllowNull()] [object]$Value, [string]$Path)
    if ($Value -is [System.Collections.IDictionary]) {
        foreach ($key in $Value.Keys) {
            $keyText = [string]$key
            if ($keyText -match '(?i)(password|token|secret|private.?key|client.?secret|connection.?string|cookie|credential|e-?mail|phone|telephone|first.?name|last.?name|full.?name)') {
                throw "$Path contains prohibited sensitive or PII field '$keyText'."
            }
            Assert-NoSensitiveKeys -Value $Value[$key] -Path "$Path.$keyText"
        }
    }
    elseif ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        $index = 0
        foreach ($item in $Value) {
            Assert-NoSensitiveKeys -Value $item -Path "$Path[$index]"
            $index++
        }
    }
}

function Assert-Sha {
    param([object]$Value, [int]$Length, [string]$Path)
    if ($Value -isnot [string] -or $Value -notmatch "^[0-9a-f]{$Length}$") {
        throw "$Path must be a lower-case hexadecimal digest of length $Length."
    }
}

function Assert-ExactInventory {
    param([object[]]$Actual, [string[]]$Expected, [string]$Path)
    $values = @($Actual | ForEach-Object { [string]$_ })
    if ($values.Count -ne $Expected.Count -or
        ($values | Group-Object -CaseSensitive | Where-Object Count -ne 1) -or
        (Compare-Object ($Expected | Sort-Object) ($values | Sort-Object) -CaseSensitive)) {
        throw "$Path does not match the exact review inventory."
    }
}

if (-not (Test-Path -LiteralPath $EvidencePath -PathType Leaf)) {
    throw "Terminal evidence does not exist: $EvidencePath"
}
$evidence = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json -AsHashtable -Depth 30 -DateKind String
if ($evidence -isnot [System.Collections.IDictionary]) {
    throw 'Terminal evidence root must be a JSON object.'
}
Assert-NoSensitiveKeys -Value $evidence -Path '$'
Assert-ExactKeys -Value $evidence -Expected @(
    'schemaVersion', 'status', 'startedAtUtc', 'completedAtUtc', 'appHostCommit',
    'authoritativeSourceCommitSha', 'migrationEvidencePayloadSha256', 'approvedBaselineSha256',
    'repositoryBaselineSha256', 'snapshot', 'repositories', 'jobs', 'databases', 'services',
    'constraints', 'cleanup'
) -Path '$'

if ($evidence.schemaVersion -ne 1 -or $evidence.status -ne 'passed') {
    throw 'Terminal evidence must be schema version 1 with passed status.'
}
Assert-Sha $evidence.appHostCommit 40 '$.appHostCommit'
Assert-Sha $evidence.authoritativeSourceCommitSha 40 '$.authoritativeSourceCommitSha'
Assert-Sha $evidence.migrationEvidencePayloadSha256 64 '$.migrationEvidencePayloadSha256'
Assert-Sha $evidence.approvedBaselineSha256 64 '$.approvedBaselineSha256'
Assert-Sha $evidence.repositoryBaselineSha256 64 '$.repositoryBaselineSha256'
if ($evidence.appHostCommit -cne $ExpectedAppHostCommit -or
    $evidence.authoritativeSourceCommitSha -cne $ExpectedSourceCommitSha -or
    $evidence.migrationEvidencePayloadSha256 -cne $ExpectedMigrationEvidencePayloadSha256 -or
    $evidence.approvedBaselineSha256 -cne $ExpectedApprovedBaselineSha256 -or
    $evidence.repositoryBaselineSha256 -cne $ExpectedRepositoryBaselineSha256) {
    throw 'Terminal evidence is not bound to the expected AppHost and authoritative source commits.'
}

$started = [DateTimeOffset]::MinValue
$completed = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse([string]$evidence.startedAtUtc, [ref]$started) -or
    -not [DateTimeOffset]::TryParse([string]$evidence.completedAtUtc, [ref]$completed) -or
    $started.Offset -ne [TimeSpan]::Zero -or $completed.Offset -ne [TimeSpan]::Zero -or
    $started -gt $completed -or $completed -gt [DateTimeOffset]::UtcNow.AddMinutes(1) -or
    $completed -lt [DateTimeOffset]::UtcNow.AddMinutes(-$MaximumAgeMinutes)) {
    throw 'Terminal evidence timestamps are invalid, non-UTC, future-dated, or stale.'
}

if ($evidence.snapshot -isnot [System.Collections.IDictionary]) { throw '$.snapshot must be an object.' }
Assert-ExactKeys $evidence.snapshot @('id', 'format', 'manifestDigestSha256') '$.snapshot'
if ($evidence.snapshot.id -cne $ExpectedSnapshotId -or $evidence.snapshot.format -cne 'MLVSNP02') {
    throw 'Terminal evidence does not identify the expected authenticated MLVSNP02 snapshot.'
}
Assert-Sha $evidence.snapshot.manifestDigestSha256 64 '$.snapshot.manifestDigestSha256'
if ($evidence.snapshot.manifestDigestSha256 -cne $ExpectedManifestDigestSha256) {
    throw 'Terminal evidence is not bound to the expected authenticated snapshot manifest.'
}

if ($evidence.repositories -isnot [object[]]) { throw '$.repositories must be an array.' }
$repositoryNames = @()
foreach ($repository in $evidence.repositories) {
    if ($repository -isnot [System.Collections.IDictionary]) { throw '$.repositories contains a non-object value.' }
    Assert-ExactKeys $repository @('name', 'commitSha', 'branch', 'clean', 'headMatchesOriginMain') '$.repositories[]'
    $repositoryNames += [string]$repository.name
    Assert-Sha $repository.commitSha 40 '$.repositories[].commitSha'
    if ($repository.branch -cne 'main' -or $repository.clean -ne $true -or $repository.headMatchesOriginMain -ne $true) {
        throw "Repository '$($repository.name)' is not a reviewed clean protected-main checkout."
    }
    if ($repository.name -ceq 'Legacy.Maliev.AppHost' -and $repository.commitSha -cne $ExpectedAppHostCommit) {
        throw 'AppHost repository evidence does not match the expected commit.'
    }
}
Assert-ExactInventory $repositoryNames $repositories '$.repositories[].name'

if ($evidence.jobs -isnot [object[]]) { throw '$.jobs must be an array.' }
$jobNames = @()
foreach ($job in $evidence.jobs) {
    if ($job -isnot [System.Collections.IDictionary]) { throw '$.jobs contains a non-object value.' }
    Assert-ExactKeys $job @('name', 'state', 'exitCode') '$.jobs[]'
    $jobNames += [string]$job.name
    if ($job.state -cne 'finished' -or $job.exitCode -ne 0) { throw "Terminal job '$($job.name)' did not succeed." }
}
Assert-ExactInventory $jobNames $terminalJobs '$.jobs[].name'

if ($evidence.databases -isnot [object[]]) { throw '$.databases must be an array.' }
Assert-ExactInventory @($evidence.databases) $runtimeDatabases '$.databases'
if (@($evidence.databases) -ccontains 'Hangfire') { throw 'Hangfire is retired and must not appear in local review topology.' }

if ($evidence.services -isnot [object[]]) { throw '$.services must be an array.' }
$serviceNames = @()
foreach ($service in $evidence.services) {
    if ($service -isnot [System.Collections.IDictionary]) { throw '$.services contains a non-object value.' }
    Assert-ExactKeys $service @('name', 'healthy', 'readProbe') '$.services[]'
    $serviceNames += [string]$service.name
    if ($service.healthy -ne $true -or $service.readProbe -cne 'passed') {
        throw "Service '$($service.name)' did not pass health and read-only validation."
    }
}
Assert-ExactInventory $serviceNames $services '$.services[].name'

if ($evidence.constraints -isnot [System.Collections.IDictionary]) { throw '$.constraints must be an object.' }
Assert-ExactKeys $evidence.constraints @('fixturesEnabled', 'mutatingProbes', 'gkeWrites', 'productionEndpointAccess') '$.constraints'
foreach ($constraint in @('fixturesEnabled', 'mutatingProbes', 'gkeWrites', 'productionEndpointAccess')) {
    if ($evidence.constraints[$constraint] -ne $false) { throw "Unsafe terminal constraint '$constraint' is enabled." }
}
if ($evidence.cleanup -cne 'completed') { throw 'Terminal review cleanup did not complete.' }

Write-Output 'PASS: redacted local snapshot terminal evidence is complete and bound.'
