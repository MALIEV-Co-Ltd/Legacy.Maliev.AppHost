[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$EvidencePath,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{40}$')] [string]$ExpectedAppHostCommit,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{40}$')] [string]$ExpectedSourceCommitSha,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$')] [string]$ExpectedSnapshotId,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{64}$')] [string]$ExpectedSemanticManifestDigestSha256,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{64}$')] [string]$ExpectedManifestFileSha256,
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
    'legacy-location-data-snapshot'
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
    'LocationData', 'Material', 'Message', 'Order', 'OrderStatus', 'Payment',
    'PurchaseOrder', 'Quotation', 'QuotationRequest', 'Receipt', 'Supplier', 'Upload'
)
$runtimeDatabases = @($migratedDatabases) + @('Auth')
$authenticatedQueries = @(
    'customer-list', 'employee-list', 'catalog-material-list', 'procurement-supplier-list',
    'order-list', 'quotation-request-list', 'accounting-payment-list'
)

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

function Assert-OwnerOnlyRegularFile {
    param([string]$Path)
    $fullPath = [IO.Path]::GetFullPath($Path)
    for ($directory = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($fullPath)); $null -ne $directory; $directory = $directory.Parent) {
        $directory.Refresh()
        if ($directory.Exists -and (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $null -ne $directory.LinkTarget)) {
            throw 'Terminal evidence path contains a link or reparse-point ancestor.'
        }
    }
    $file = [IO.FileInfo]::new($fullPath)
    $file.Refresh()
    if (-not $file.Exists -or $null -ne $file.LinkTarget -or
        ($file.Attributes -band ([IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint)) -ne 0) {
        throw 'Terminal evidence must be a regular non-link file.'
    }
    if ($IsWindows) {
        $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $security = [IO.FileSystemAclExtensions]::GetAccessControl($file)
        if ($security.GetOwner([Security.Principal.SecurityIdentifier]) -ne $owner) {
            throw 'Terminal evidence must be owned by the current user.'
        }
        foreach ($rule in $security.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and $rule.IdentityReference -ne $owner) {
                throw 'Terminal evidence must have owner-only permissions.'
            }
        }
    }
    else {
        $mode = [IO.File]::GetUnixFileMode($fullPath)
        $forbidden = [IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::GroupWrite -bor
            [IO.UnixFileMode]::GroupExecute -bor [IO.UnixFileMode]::OtherRead -bor
            [IO.UnixFileMode]::OtherWrite -bor [IO.UnixFileMode]::OtherExecute
        if (($mode -band [IO.UnixFileMode]::UserRead) -eq 0 -or ($mode -band $forbidden) -ne 0) {
            throw 'Terminal evidence must have owner-only permissions.'
        }
    }
    return $fullPath
}

function Read-SecureJson {
    param([string]$Path)
    $fullPath = Assert-OwnerOnlyRegularFile $Path
    # Read sharing permits a trusted publisher to retain the exact candidate bytes;
    # write and delete sharing remain denied by this validator handle.
    $stream = [IO.File]::Open($fullPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false, $true), $false, 4096, $true)
        try { $json = $reader.ReadToEnd() } finally { $reader.Dispose() }
        return $json | ConvertFrom-Json -AsHashtable -Depth 30 -DateKind String
    }
    finally { $stream.Dispose() }
}

$evidence = Read-SecureJson $EvidencePath
if ($evidence -isnot [System.Collections.IDictionary]) {
    throw 'Terminal evidence root must be a JSON object.'
}
Assert-NoSensitiveKeys -Value $evidence -Path '$'
Assert-ExactKeys -Value $evidence -Expected @(
    'schemaVersion', 'status', 'startedAtUtc', 'completedAtUtc', 'appHostCommit',
    'authoritativeSourceCommitSha', 'migrationEvidencePayloadSha256', 'approvedBaselineSha256',
    'repositoryBaselineSha256', 'snapshot', 'repositories', 'jobs', 'databases', 'services',
    'authenticatedQueries', 'constraints', 'cleanup'
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
Assert-ExactKeys $evidence.snapshot @('id', 'format', 'semanticManifestDigestSha256', 'manifestFileSha256') '$.snapshot'
if ($evidence.snapshot.id -cne $ExpectedSnapshotId -or $evidence.snapshot.format -cne 'MLVSNP02') {
    throw 'Terminal evidence does not identify the expected authenticated MLVSNP02 snapshot.'
}
Assert-Sha $evidence.snapshot.semanticManifestDigestSha256 64 '$.snapshot.semanticManifestDigestSha256'
Assert-Sha $evidence.snapshot.manifestFileSha256 64 '$.snapshot.manifestFileSha256'
if ($evidence.snapshot.semanticManifestDigestSha256 -cne $ExpectedSemanticManifestDigestSha256 -or
    $evidence.snapshot.manifestFileSha256 -cne $ExpectedManifestFileSha256) {
    throw 'Terminal evidence is not bound to the expected authenticated snapshot manifest.'
}

if ($evidence.repositories -isnot [object[]]) { throw '$.repositories must be an array.' }
$repositoryNames = @()
foreach ($repository in $evidence.repositories) {
    if ($repository -isnot [System.Collections.IDictionary]) { throw '$.repositories contains a non-object value.' }
    Assert-ExactKeys $repository @('name', 'commitSha', 'branch', 'clean', 'headMatchesOriginMain', 'originUrl') '$.repositories[]'
    $repositoryNames += [string]$repository.name
    Assert-Sha $repository.commitSha 40 '$.repositories[].commitSha'
    if ($repository.branch -cne 'main' -or $repository.clean -ne $true -or $repository.headMatchesOriginMain -ne $true) {
        throw "Repository '$($repository.name)' is not a reviewed clean protected-main checkout."
    }
    if ($repository.name -ceq 'Legacy.Maliev.AppHost' -and $repository.commitSha -cne $ExpectedAppHostCommit) {
        throw 'AppHost repository evidence does not match the expected commit.'
    }
    $expectedOrigin = "https://github.com/MALIEV-Co-Ltd/$($repository.name)"
    if ($repository.originUrl -cne $expectedOrigin -and $repository.originUrl -cne "$expectedOrigin.git") {
        throw "Repository '$($repository.name)' has an unreviewed origin URL."
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
    Assert-ExactKeys $service @('name', 'healthy', 'probeId', 'probeStatus') '$.services[]'
    $serviceNames += [string]$service.name
    if ($service.healthy -ne $true -or $service.probeId -cne 'readiness' -or $service.probeStatus -cne 'passed') {
        throw "Service '$($service.name)' did not pass health and read-only validation."
    }
}
Assert-ExactInventory $serviceNames $services '$.services[].name'

if ($evidence.authenticatedQueries -isnot [object[]]) { throw '$.authenticatedQueries must be an array.' }
$queryIds = @()
foreach ($query in $evidence.authenticatedQueries) {
    if ($query -isnot [System.Collections.IDictionary]) { throw '$.authenticatedQueries contains a non-object value.' }
    Assert-ExactKeys $query @('id', 'status') '$.authenticatedQueries[]'
    $queryIds += [string]$query.id
    if ($query.status -cne 'passed') { throw "Authenticated read-only query '$($query.id)' did not pass." }
}
Assert-ExactInventory $queryIds $authenticatedQueries '$.authenticatedQueries[].id'

if ($evidence.constraints -isnot [System.Collections.IDictionary]) { throw '$.constraints must be an object.' }
Assert-ExactKeys $evidence.constraints @('fixturesEnabled', 'mutatingProbes', 'gkeWrites', 'productionEndpointAccess') '$.constraints'
foreach ($constraint in @('fixturesEnabled', 'mutatingProbes', 'gkeWrites', 'productionEndpointAccess')) {
    if ($evidence.constraints[$constraint] -ne $false) { throw "Unsafe terminal constraint '$constraint' is enabled." }
}
if ($evidence.cleanup -cne 'completed') { throw 'Terminal review cleanup did not complete.' }

Write-Output 'PASS: redacted local snapshot terminal evidence is complete and bound.'
