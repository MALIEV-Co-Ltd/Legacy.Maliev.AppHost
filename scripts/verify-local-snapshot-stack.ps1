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
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{64}$')] [string]$ExpectedSemanticManifestDigestSha256,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{64}$')] [string]$ExpectedManifestFileSha256,
    [Parameter(Mandatory = $true)] [string]$AuthenticatedQueryEvidencePath,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{64}$')] [string]$ExpectedAuthenticatedQueryEvidenceSha256,
    [Parameter(Mandatory = $true)] [string]$EvidencePath,
    [ValidateRange(60, 1800)] [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$startedAtUtc = [DateTimeOffset]::UtcNow
$appHostProcess = $null
$validationSucceeded = $false
$cleanupCompleted = $false

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
$authenticatedQueries = @(
    'auth-session-current', 'document-receipt-read', 'customer-list', 'employee-list',
    'catalog-material-list', 'procurement-supplier-list', 'file-list', 'order-list',
    'quotation-list', 'intranet-customer-list', 'accounting-invoice-list'
)

function Get-Sha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-OwnerOnlyRegularFile([string]$Path, [string]$Label) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    for ($directory = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($fullPath)); $null -ne $directory; $directory = $directory.Parent) {
        $directory.Refresh()
        if ($directory.Exists -and (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $null -ne $directory.LinkTarget)) {
            throw "$Label path contains a link or reparse-point ancestor."
        }
    }
    $file = [IO.FileInfo]::new($fullPath); $file.Refresh()
    if (-not $file.Exists -or $null -ne $file.LinkTarget -or
        ($file.Attributes -band ([IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint)) -ne 0) {
        throw "$Label must be a regular non-link file."
    }
    if ($IsWindows) {
        $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $security = [IO.FileSystemAclExtensions]::GetAccessControl($file)
        if ($security.GetOwner([Security.Principal.SecurityIdentifier]) -ne $owner) { throw "$Label must be owned by the current user." }
        foreach ($rule in $security.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and $rule.IdentityReference -ne $owner) {
                throw "$Label must have owner-only permissions."
            }
        }
    }
    else {
        $mode = [IO.File]::GetUnixFileMode($fullPath)
        $forbidden = [IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::GroupWrite -bor
            [IO.UnixFileMode]::GroupExecute -bor [IO.UnixFileMode]::OtherRead -bor
            [IO.UnixFileMode]::OtherWrite -bor [IO.UnixFileMode]::OtherExecute
        if (($mode -band [IO.UnixFileMode]::UserRead) -eq 0 -or ($mode -band $forbidden) -ne 0) { throw "$Label must have owner-only permissions." }
    }
    return $fullPath
}

function Read-SecureJsonAndSha([string]$Path, [string]$Label) {
    $fullPath = Assert-OwnerOnlyRegularFile $Path $Label
    $stream = [IO.File]::Open($fullPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $digest = [Convert]::ToHexString($sha.ComputeHash($stream)).ToLowerInvariant() } finally { $sha.Dispose() }
        $stream.Position = 0
        $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false, $true), $false, 4096, $true)
        try { $json = $reader.ReadToEnd() } finally { $reader.Dispose() }
        return [pscustomobject]@{ Value = ($json | ConvertFrom-Json -DateKind String); Sha256 = $digest }
    }
    finally { $stream.Dispose() }
}

function Set-OwnerOnlyFile([string]$Path) {
    if ($IsWindows) {
        $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $security = [Security.AccessControl.FileSecurity]::new()
        $security.SetOwner($owner)
        $security.SetAccessRuleProtection($true, $false)
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $owner, [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.AccessControlType]::Allow))
        [IO.FileSystemAclExtensions]::SetAccessControl([IO.FileInfo]::new($Path), $security)
    }
    else {
        [IO.File]::SetUnixFileMode($Path, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
    }
}

function Assert-OwnerOnlyDirectory([string]$Path) {
    $directory = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($Path)); $directory.Refresh()
    if (-not $directory.Exists -or $null -ne $directory.LinkTarget -or
        ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Terminal evidence parent directory must be a regular non-link directory.'
    }
    for ($ancestor = $directory; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        $ancestor.Refresh()
        if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $null -ne $ancestor.LinkTarget) {
            throw 'Terminal evidence path contains a link or reparse-point ancestor.'
        }
    }
    if ($IsWindows) {
        $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $security = [IO.FileSystemAclExtensions]::GetAccessControl($directory)
        if ($security.GetOwner([Security.Principal.SecurityIdentifier]) -ne $owner) { throw 'Terminal evidence directory must be owned by the current user.' }
        foreach ($rule in $security.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and $rule.IdentityReference -ne $owner) {
                throw 'Terminal evidence directory must have owner-only permissions.'
            }
        }
    }
    else {
        $mode = [IO.File]::GetUnixFileMode($directory.FullName)
        $forbidden = [IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::GroupWrite -bor
            [IO.UnixFileMode]::GroupExecute -bor [IO.UnixFileMode]::OtherRead -bor
            [IO.UnixFileMode]::OtherWrite -bor [IO.UnixFileMode]::OtherExecute
        if (($mode -band [IO.UnixFileMode]::UserRead) -eq 0 -or ($mode -band $forbidden) -ne 0) {
            throw 'Terminal evidence directory must have owner-only permissions.'
        }
    }
}

function Assert-ExactNames([object[]]$Actual, [string[]]$Expected, [string]$Path) {
    $names = @($Actual | ForEach-Object { [string]$_ })
    if ($names.Count -ne $Expected.Count -or
        ($names | Group-Object -CaseSensitive | Where-Object Count -ne 1) -or
        (Compare-Object ($Expected | Sort-Object) ($names | Sort-Object) -CaseSensitive)) {
        throw "$Path does not match the exact local-review inventory."
    }
}

function Assert-ExactObjectKeys([object]$Value, [string[]]$Expected, [string]$Path) {
    $actual = @($Value.PSObject.Properties.Name | Sort-Object)
    $wanted = @($Expected | Sort-Object)
    if ($actual.Count -ne $wanted.Count -or (Compare-Object $wanted $actual -CaseSensitive)) {
        throw "$Path contains missing or unknown fields."
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

function Invoke-LocalReadinessProbe([object]$Resource, [string]$Name) {
    $endpoint = @($Resource.status.urls | ForEach-Object {
        if ($_ -is [string]) { $_ } elseif ($_.url) { [string]$_.url }
    } | Where-Object { $_ -match '^https?://' } | Select-Object -First 1)
    if ($endpoint.Count -ne 1) { throw "Service '$Name' has no discoverable local HTTP endpoint." }
    $baseUri = [Uri]$endpoint[0]
    if (-not $baseUri.IsLoopback) { throw "Service '$Name' resolved to a non-local endpoint." }
    $builder = [UriBuilder]::new($baseUri)
    $builder.Path = (($builder.Path.TrimEnd('/')) + '/readiness')
    $response = Invoke-WebRequest -Uri $builder.Uri.AbsoluteUri -Method Get -UseBasicParsing `
        -SkipCertificateCheck -SkipHttpErrorCheck -MaximumRedirection 0 -TimeoutSec 15
    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        throw "Service '$Name' readiness GET failed with HTTP $($response.StatusCode)."
    }
    return [ordered]@{ name = $Name; healthy = $true; probeId = 'readiness'; probeStatus = 'passed' }
}

function Write-AtomicJson([System.Collections.IDictionary]$Value, [string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath $fullPath) { throw 'Terminal evidence output already exists; use a unique create-only path.' }
    $parent = [IO.Path]::GetDirectoryName($fullPath)
    if (-not [IO.Directory]::Exists($parent)) { throw 'Terminal evidence parent directory must already exist and be owner-only.' }
    Assert-OwnerOnlyDirectory $parent
    $temporary = Join-Path $parent ('.' + [IO.Path]::GetFileName($fullPath) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false), 4096, $true)
            try { $writer.Write(($Value | ConvertTo-Json -Depth 12)); $writer.Flush(); $stream.Flush($true) } finally { $writer.Dispose() }
        }
        finally { $stream.Dispose() }
        Set-OwnerOnlyFile $temporary
        [IO.File]::Move($temporary, $fullPath, $false)
        [void](Assert-OwnerOnlyRegularFile $fullPath 'Terminal evidence')
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) }
    }
}

foreach ($requiredFile in @(
    $SnapshotEncryptionKeyFile, $MigrationEvidencePath, $TrustedPublicKeyPath,
    $ApprovedBaselinePath, $RepositoryBaselinePath, $AuthenticatedQueryEvidencePath
)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) { throw "Required review input is missing: $requiredFile" }
}
if (Test-Path -LiteralPath $EvidencePath) { throw 'Terminal evidence output already exists; use a unique create-only path.' }
if (-not (Test-Path -LiteralPath $SnapshotDirectory -PathType Container)) { throw 'Snapshot directory is missing.' }
$manifestPath = Join-Path $SnapshotDirectory 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Authenticated snapshot manifest is missing.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.SchemaVersion -ne 2 -or $manifest.Format -cne 'MLVSNP02' -or
    $manifest.Encryption -cne 'AES-256-GCM-chunked-v2' -or $manifest.SnapshotId -cne $SnapshotId) {
    throw 'Snapshot is not the expected authenticated MLVSNP02 artifact.'
}
Assert-ExactNames @($manifest.Databases.Database) $migratedDatabases 'snapshot databases'
$manifestFileSha256 = Get-Sha256 $manifestPath
if ($manifest.ManifestDigestSha256 -cne $ExpectedSemanticManifestDigestSha256 -or
    $manifestFileSha256 -cne $ExpectedManifestFileSha256) {
    throw 'Snapshot semantic manifest digest or raw manifest-file digest does not match the reviewed value.'
}

$repositoryBaselineInput = Read-SecureJsonAndSha $RepositoryBaselinePath 'Repository baseline'
if ($repositoryBaselineInput.Sha256 -cne $ExpectedRepositoryBaselineSha256) {
    throw 'Repository baseline digest does not match the reviewed value.'
}
$repositoryBaseline = $repositoryBaselineInput.Value
Assert-ExactObjectKeys $repositoryBaseline @('schemaVersion', 'repositories') '$repositoryBaseline'
if ($repositoryBaseline.schemaVersion -ne 1) { throw 'Repository baseline schema version is unsupported.' }
Assert-ExactNames @($repositoryBaseline.repositories.name) $repositories 'repository baseline'
$repositoryEvidence = @()
foreach ($entry in @($repositoryBaseline.repositories)) {
    Assert-ExactObjectKeys $entry @('name', 'commitSha', 'originUrl') '$repositoryBaseline.repositories[]'
    if ($entry.commitSha -notmatch '^[0-9a-f]{40}$' -or
        $entry.originUrl -cnotmatch '^https://github\.com/MALIEV-Co-Ltd/[A-Za-z0-9._-]+(?:\.git)?$') {
        throw "Repository baseline identity is invalid for '$($entry.name)'."
    }
    $path = Join-Path $WorkspaceRoot $entry.name
    if (-not (Test-Path -LiteralPath (Join-Path $path '.git'))) {
        # Worktrees use a .git file and canonical clones use a .git directory.
        if (-not (Test-Path -LiteralPath (Join-Path $path '.git') -PathType Leaf)) { throw "Repository is missing: $($entry.name)" }
    }
    $branch = (& git -C $path branch --show-current).Trim()
    $head = (& git -C $path rev-parse HEAD).Trim()
    $originMain = (& git -C $path rev-parse origin/main).Trim()
    $originUrl = (& git -C $path remote get-url origin).Trim()
    $dirty = @(& git -C $path status --porcelain=v1 --untracked-files=all)
    if ($branch -cne 'main' -or $head -cne $entry.commitSha -or $originMain -cne $entry.commitSha -or
        $originUrl -cne $entry.originUrl -or $dirty.Count -ne 0) {
        throw "Repository '$($entry.name)' is not the reviewed clean protected-main commit."
    }
    $repositoryEvidence += [ordered]@{
        name = $entry.name; commitSha = $head; branch = $branch; clean = $true
        headMatchesOriginMain = $true; originUrl = $originUrl
    }
}
$appHostCommit = [string](@($repositoryEvidence | Where-Object name -eq 'Legacy.Maliev.AppHost')[0].commitSha)

$authenticatedInput = Read-SecureJsonAndSha $AuthenticatedQueryEvidencePath 'Authenticated query evidence'
if ($authenticatedInput.Sha256 -cne $ExpectedAuthenticatedQueryEvidenceSha256) {
    throw 'Authenticated query evidence digest does not match the reviewed value.'
}
$authenticatedEvidence = $authenticatedInput.Value
Assert-ExactObjectKeys $authenticatedEvidence @(
    'schemaVersion', 'snapshotId', 'authoritativeSourceCommitSha', 'appHostCommit',
    'repositoryBaselineSha256', 'completedAtUtc', 'queries'
) '$authenticatedQueryEvidence'
if ($authenticatedEvidence.schemaVersion -ne 1 -or $authenticatedEvidence.snapshotId -cne $SnapshotId -or
    $authenticatedEvidence.authoritativeSourceCommitSha -cne $ExpectedSourceCommitSha -or
    $authenticatedEvidence.appHostCommit -cne $appHostCommit -or
    $authenticatedEvidence.repositoryBaselineSha256 -cne $ExpectedRepositoryBaselineSha256) {
    throw 'Authenticated query evidence is not bound to this snapshot, source, AppHost, and repository baseline.'
}
$authenticatedCompletedAtUtc = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse([string]$authenticatedEvidence.completedAtUtc, [ref]$authenticatedCompletedAtUtc) -or
    $authenticatedCompletedAtUtc.Offset -ne [TimeSpan]::Zero -or
    $authenticatedCompletedAtUtc -lt [DateTimeOffset]::UtcNow.AddMinutes(-30) -or
    $authenticatedCompletedAtUtc -gt [DateTimeOffset]::UtcNow.AddMinutes(1)) {
    throw 'Authenticated query evidence is invalid or stale.'
}
Assert-ExactNames @($authenticatedEvidence.queries.id) $authenticatedQueries 'authenticated query evidence'
foreach ($query in @($authenticatedEvidence.queries)) {
    Assert-ExactObjectKeys $query @('id', 'status') '$authenticatedQueryEvidence.queries[]'
    if ($query.status -cne 'passed') { throw "Authenticated read-only query '$($query.id)' did not pass." }
}
$authenticatedQueryEvidence = @($authenticatedEvidence.queries | ForEach-Object { [ordered]@{ id = $_.id; status = 'passed' } })

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
    $serviceEvidence = @($services | ForEach-Object {
        Invoke-LocalReadinessProbe -Resource (Get-SingleResource $items $_) -Name $_
    })
    $validationSucceeded = $true
}
finally {
    [Environment]::SetEnvironmentVariable('LEGACY_LOCAL_FIXTURES', $previousFixtures)
    if ($appHostProcess -and $appHostProcess.ProcessId) {
        Stop-Process -Id $appHostProcess.ProcessId -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $appHostProcess.ProcessId -Timeout 30 -ErrorAction SilentlyContinue
        $cleanupCompleted = $null -eq (Get-Process -Id $appHostProcess.ProcessId -ErrorAction SilentlyContinue)
    }
    else { $cleanupCompleted = $true }
}

if (-not $validationSucceeded -or -not $cleanupCompleted) {
    throw 'Local snapshot validation or cleanup did not complete; passed evidence is prohibited.'
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
    snapshot = [ordered]@{
        id = $SnapshotId
        format = 'MLVSNP02'
        semanticManifestDigestSha256 = $ExpectedSemanticManifestDigestSha256
        manifestFileSha256 = $ExpectedManifestFileSha256
    }
    repositories = $repositoryEvidence
    jobs = $jobEvidence
    databases = $runtimeDatabases
    services = $serviceEvidence
    authenticatedQueries = $authenticatedQueryEvidence
    constraints = [ordered]@{ fixturesEnabled = $false; mutatingProbes = $false; gkeWrites = $false; productionEndpointAccess = $false }
    cleanup = 'completed'
}
Write-AtomicJson $terminalEvidence $EvidencePath
& (Join-Path $PSScriptRoot 'test-local-snapshot-verification-evidence.ps1') -EvidencePath $EvidencePath `
    -ExpectedAppHostCommit $appHostCommit -ExpectedSourceCommitSha $ExpectedSourceCommitSha `
    -ExpectedSnapshotId $SnapshotId -ExpectedSemanticManifestDigestSha256 $ExpectedSemanticManifestDigestSha256 `
    -ExpectedManifestFileSha256 $ExpectedManifestFileSha256 `
    -ExpectedMigrationEvidencePayloadSha256 ([string]$migrationEvidence.attestation.payloadSha256) `
    -ExpectedApprovedBaselineSha256 $ExpectedApprovedBaselineSha256 `
    -ExpectedRepositoryBaselineSha256 $ExpectedRepositoryBaselineSha256 -MaximumAgeMinutes 30
if ($LASTEXITCODE -ne 0) { throw 'Atomic terminal evidence failed independent validation.' }

Write-Output 'PASS: authenticated local snapshot terminal gate completed.'
