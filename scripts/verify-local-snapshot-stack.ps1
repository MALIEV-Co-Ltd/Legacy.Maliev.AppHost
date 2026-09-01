[CmdletBinding()]
param(
    [string]$WorkspaceRoot = 'B:\maliev-legacy',
    [Parameter(Mandatory = $true)] [string]$SnapshotDirectory,
    [Parameter(Mandatory = $true)] [string]$SnapshotEncryptionKeyFile,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$')] [string]$SnapshotId,
    [Parameter(Mandatory = $true)] [string]$MigrationEvidencePath,
    [Parameter(Mandatory = $true)] [string]$TrustedPublicKeyPath,
    [Parameter(Mandatory = $true)] [string]$ExpectedAttestationKeyId,
    [Parameter(Mandatory = $true)] [string]$ApprovedBaselinePath,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{64}$')] [string]$ExpectedApprovedBaselineSha256,
    [Parameter(Mandatory = $true)] [string]$ConsumptionLedgerPath,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{40}$')] [string]$ExpectedSourceCommitSha,
    [Parameter(Mandatory = $true)] [string]$ExpectedRunId,
    [Parameter(Mandatory = $true)] [string]$ExpectedTargetGeneration,
    [Parameter(Mandatory = $true)] [string]$ExpectedRestoreId,
    [Parameter(Mandatory = $true)] [DateTimeOffset]$RequiredAsOfUtc,
    [Parameter(Mandatory = $true)] [string]$RepositoryBaselinePath,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{64}$')] [string]$ExpectedRepositoryBaselineSha256,
    [Parameter(Mandatory = $true)] [string]$EvidencePath,
    [ValidateRange(60, 1800)] [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$startedAtUtc = [DateTimeOffset]::UtcNow
$appHostProcess = $null

# These inventories mirror LegacySnapshotReviewContract. Its tests freeze the public contract;
# this script remains directly auditable and does not dynamically execute repository code.
$terminalJobs = @(
    'legacy-country-migrations', 'legacy-auth-migrations', 'legacy-customer-identity-migrations',
    'legacy-employee-identity-migrations', 'legacy-customer-migrations', 'legacy-employee-migrations',
    'legacy-catalog-migrations', 'legacy-supplier-migrations', 'legacy-purchase-order-migrations',
    'legacy-file-migrations', 'legacy-order-migrations', 'legacy-order-status-migrations',
    'legacy-quotation-migrations', 'legacy-quotation-request-migrations', 'legacy-career-migrations',
    'legacy-contact-migrations', 'legacy-payment-migrations', 'legacy-invoice-migrations',
    'legacy-receipt-migrations', 'legacy-contact-request-snapshot', 'legacy-currency-snapshot',
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

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-ExactNames([object[]]$Actual, [string[]]$Expected, [string]$Path) {
    $names = @($Actual | ForEach-Object { [string]$_ })
    if ($names.Count -ne $Expected.Count -or
        ($names | Group-Object -CaseSensitive | Where-Object Count -ne 1) -or
        (Compare-Object ($Expected | Sort-Object) ($names | Sort-Object) -CaseSensitive)) {
        throw "$Path does not match the exact local-review inventory."
    }
}

function Get-DcpKubeconfig([int]$AppHostProcessId) {
    $dcp = Get-CimInstance Win32_Process | Where-Object {
        $_.Name -eq 'dcp.exe' -and $_.CommandLine -like '*start-apiserver*' -and
        $_.CommandLine -like "*--monitor $AppHostProcessId*"
    } | Select-Object -First 1
    if (-not $dcp) { return $null }
    $match = [regex]::Match($dcp.CommandLine, '--kubeconfig\s+"?(.+?)"?\s+--tls-cert')
    if (-not $match.Success) { throw 'The local DCP kubeconfig path could not be parsed.' }
    return $match.Groups[1].Value
}

function Get-SingleResource([object[]]$Items, [string]$Name) {
    $matches = @($Items | Where-Object { $_.metadata.name -like "$Name-*" })
    if ($matches.Count -ne 1) { throw "Expected exactly one local resource for '$Name'." }
    return $matches[0]
}

function Write-AtomicJson([System.Collections.IDictionary]$Value, [string]$Path) {
    $parent = Split-Path -Parent $Path
    if ([string]::IsNullOrWhiteSpace($parent)) { $parent = (Get-Location).Path }
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $temporary = Join-Path $parent ('.' + [IO.Path]::GetFileName($Path) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

foreach ($requiredFile in @(
    $SnapshotEncryptionKeyFile, $MigrationEvidencePath, $TrustedPublicKeyPath,
    $ApprovedBaselinePath, $RepositoryBaselinePath
)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) { throw "Required review input is missing: $requiredFile" }
}
if (-not (Test-Path -LiteralPath $SnapshotDirectory -PathType Container)) { throw 'Snapshot directory is missing.' }
$manifestPath = Join-Path $SnapshotDirectory 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Authenticated snapshot manifest is missing.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.SchemaVersion -ne 2 -or $manifest.Format -cne 'MLVSNP02' -or
    $manifest.Encryption -cne 'AES-256-GCM-chunked-v2' -or $manifest.SnapshotId -cne $SnapshotId) {
    throw 'Snapshot is not the expected authenticated MLVSNP02 artifact.'
}
Assert-ExactNames @($manifest.Databases.Database) $migratedDatabases 'snapshot databases'

if ((Get-Sha256 $RepositoryBaselinePath) -cne $ExpectedRepositoryBaselineSha256) {
    throw 'Repository baseline digest does not match the reviewed value.'
}
$repositoryBaseline = Get-Content -LiteralPath $RepositoryBaselinePath -Raw | ConvertFrom-Json
if ($repositoryBaseline.schemaVersion -ne 1) { throw 'Repository baseline schema version is unsupported.' }
Assert-ExactNames @($repositoryBaseline.repositories.name) $repositories 'repository baseline'
$repositoryEvidence = @()
foreach ($entry in @($repositoryBaseline.repositories)) {
    if ($entry.commitSha -notmatch '^[0-9a-f]{40}$') { throw "Repository baseline commit is invalid for '$($entry.name)'." }
    $path = Join-Path $WorkspaceRoot $entry.name
    if (-not (Test-Path -LiteralPath (Join-Path $path '.git'))) {
        # Worktrees use a .git file and canonical clones use a .git directory.
        if (-not (Test-Path -LiteralPath (Join-Path $path '.git') -PathType Leaf)) { throw "Repository is missing: $($entry.name)" }
    }
    $branch = (& git -C $path branch --show-current).Trim()
    $head = (& git -C $path rev-parse HEAD).Trim()
    $originMain = (& git -C $path rev-parse origin/main).Trim()
    $dirty = @(& git -C $path status --porcelain=v1 --untracked-files=all)
    if ($branch -cne 'main' -or $head -cne $entry.commitSha -or $originMain -cne $entry.commitSha -or $dirty.Count -ne 0) {
        throw "Repository '$($entry.name)' is not the reviewed clean protected-main commit."
    }
    $repositoryEvidence += [ordered]@{ name = $entry.name; commitSha = $head; branch = $branch; clean = $true; headMatchesOriginMain = $true }
}
$appHostCommit = [string](@($repositoryEvidence | Where-Object name -eq 'Legacy.Maliev.AppHost')[0].commitSha)

$signedVerifier = Join-Path $PSScriptRoot 'verify-postgres-migration-evidence.ps1'
& $signedVerifier -EvidencePath $MigrationEvidencePath -ExpectedDatabase $migratedDatabases `
    -RequiredAsOfUtc $RequiredAsOfUtc -TrustedPublicKeyPath $TrustedPublicKeyPath `
    -ExpectedAttestationKeyId $ExpectedAttestationKeyId -ApprovedBaselinePath $ApprovedBaselinePath `
    -ExpectedApprovedBaselineSha256 $ExpectedApprovedBaselineSha256 -ConsumptionLedgerPath $ConsumptionLedgerPath `
    -ExpectedRunId $ExpectedRunId -ExpectedTargetGeneration $ExpectedTargetGeneration -ExpectedRestoreId $ExpectedRestoreId
if ($LASTEXITCODE -ne 0) { throw 'Signed schema-v2 migration evidence validation failed.' }
$migrationEvidence = Get-Content -LiteralPath $MigrationEvidencePath -Raw | ConvertFrom-Json
if ($migrationEvidence.mapping.sourceCommitSha -cne $ExpectedSourceCommitSha -or
    $migrationEvidence.source.snapshotId -cne $SnapshotId) {
    throw 'Snapshot, migration evidence, and authoritative source commit binding failed.'
}

$previousFixtures = [Environment]::GetEnvironmentVariable('LEGACY_LOCAL_FIXTURES')
try {
    [Environment]::SetEnvironmentVariable('LEGACY_LOCAL_FIXTURES', 'false')
    # start-local-review-aspire.ps1 authenticates MLVSNP02 through the migration runner's
    # snapshot-preflight command before starting any local database or service resource.
    $startScript = Join-Path $PSScriptRoot 'start-local-review-aspire.ps1'
    $appHostProcess = & $startScript -WorkspaceRoot $WorkspaceRoot -SnapshotDirectory $SnapshotDirectory `
        -SnapshotEncryptionKeyFile $SnapshotEncryptionKeyFile -SnapshotId $SnapshotId
    if (-not $appHostProcess.ProcessId) { throw 'Local Aspire did not return a process identity.' }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $items = @()
    do {
        $runner = Get-Process -Id $appHostProcess.ProcessId -ErrorAction SilentlyContinue
        if (-not $runner) { throw 'Local Aspire exited before terminal validation.' }
        $hostProcess = Get-CimInstance Win32_Process | Where-Object {
            $_.Name -eq 'Legacy.Maliev.AppHost.exe' -and $_.ParentProcessId -eq $appHostProcess.ProcessId
        } | Select-Object -First 1
        if ($hostProcess) { $kubeconfig = Get-DcpKubeconfig $hostProcess.ProcessId }
        if ($kubeconfig -and (Test-Path -LiteralPath $kubeconfig -PathType Leaf)) {
            $json = & kubectl --kubeconfig $kubeconfig get containers,executables -o json 2>$null
            if ($LASTEXITCODE -eq 0 -and $json) { $items = @((ConvertFrom-Json ($json -join "`n")).items) }
        }
        $jobsReady = $items.Count -gt 0
        foreach ($name in $terminalJobs) {
            try { $resource = Get-SingleResource $items $name } catch { $jobsReady = $false; break }
            if ($resource.status.state -cne 'Finished' -or $resource.status.exitCode -ne 0) { $jobsReady = $false; break }
        }
        $servicesReady = $items.Count -gt 0
        foreach ($name in $services) {
            try { $resource = Get-SingleResource $items $name } catch { $servicesReady = $false; break }
            if ($resource.status.healthStatus -cne 'Healthy') { $servicesReady = $false; break }
        }
        if (-not ($jobsReady -and $servicesReady)) { Start-Sleep -Milliseconds 750 }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if (-not ($jobsReady -and $servicesReady)) { throw 'Exact local jobs and services did not reach terminal healthy state before timeout.' }

    $postgresResource = Get-SingleResource $items 'legacy-postgres-main'
    $dockerName = @(& docker ps --format '{{.Names}}' | Where-Object { $_ -like "$($postgresResource.metadata.name)*" })
    if ($dockerName.Count -ne 1) { throw 'Local PostgreSQL container identity is ambiguous.' }
    $databaseOutput = & docker exec $dockerName[0] sh -lc 'psql -U "$POSTGRES_USER" -d postgres -Atc "SELECT datname FROM pg_database WHERE datistemplate = false AND datname <> ''postgres'' ORDER BY datname"' 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Read-only local PostgreSQL topology query failed.' }
    Assert-ExactNames @($databaseOutput) $runtimeDatabases 'local PostgreSQL databases'

    $jobEvidence = @($terminalJobs | ForEach-Object { [ordered]@{ name = $_; state = 'finished'; exitCode = 0 } })
    # Aspire health checks are GET/read-only probes; this gate never runs fixture or write flows.
    $serviceEvidence = @($services | ForEach-Object { [ordered]@{ name = $_; healthy = $true; readProbe = 'passed' } })
}
finally {
    [Environment]::SetEnvironmentVariable('LEGACY_LOCAL_FIXTURES', $previousFixtures)
    if ($appHostProcess -and $appHostProcess.ProcessId) {
        Stop-Process -Id $appHostProcess.ProcessId -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $appHostProcess.ProcessId -Timeout 30 -ErrorAction SilentlyContinue
    }
}

$terminalEvidence = [ordered]@{
    schemaVersion = 1
    status = 'passed'
    startedAtUtc = $startedAtUtc.ToUniversalTime().ToString('O')
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    appHostCommit = $appHostCommit
    authoritativeSourceCommitSha = $ExpectedSourceCommitSha
    migrationEvidencePayloadSha256 = [string]$migrationEvidence.attestation.payloadSha256
    approvedBaselineSha256 = $ExpectedApprovedBaselineSha256
    repositoryBaselineSha256 = $ExpectedRepositoryBaselineSha256
    snapshot = [ordered]@{ id = $SnapshotId; format = 'MLVSNP02'; manifestDigestSha256 = (Get-Sha256 $manifestPath) }
    repositories = $repositoryEvidence
    jobs = $jobEvidence
    databases = $runtimeDatabases
    services = $serviceEvidence
    constraints = [ordered]@{ fixturesEnabled = $false; mutatingProbes = $false; gkeWrites = $false; productionEndpointAccess = $false }
    cleanup = 'completed'
}
Write-AtomicJson $terminalEvidence $EvidencePath
& (Join-Path $PSScriptRoot 'test-local-snapshot-verification-evidence.ps1') -EvidencePath $EvidencePath `
    -ExpectedAppHostCommit $appHostCommit -ExpectedSourceCommitSha $ExpectedSourceCommitSha `
    -ExpectedSnapshotId $SnapshotId -ExpectedManifestDigestSha256 (Get-Sha256 $manifestPath) `
    -ExpectedMigrationEvidencePayloadSha256 ([string]$migrationEvidence.attestation.payloadSha256) `
    -ExpectedApprovedBaselineSha256 $ExpectedApprovedBaselineSha256 `
    -ExpectedRepositoryBaselineSha256 $ExpectedRepositoryBaselineSha256 -MaximumAgeMinutes 30
if ($LASTEXITCODE -ne 0) { throw 'Atomic terminal evidence failed independent validation.' }

Write-Output 'PASS: authenticated local snapshot terminal gate completed.'
