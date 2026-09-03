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
    [Parameter(Mandatory = $true)] [string]$EvidencePath,
    [ValidateRange(60, 1800)] [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$startedAtUtc = [DateTimeOffset]::UtcNow
$appHostProcess = $null
$validationSucceeded = $false
$cleanupCompleted = $false
$protectedHandles = [Collections.Generic.List[IO.FileStream]]::new()
$dcpProcessId = $null
$hostProcessId = $null
$localContainerNames = @()
$serviceToken = $null

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

function Open-ProtectedReadHandle([string]$Path, [string]$Label) {
    $fullPath = Assert-OwnerOnlyRegularFile $Path $Label
    $stream = [IO.File]::Open($fullPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $protectedHandles.Add($stream)
    return $stream
}

function Read-ProtectedJsonAndSha([IO.FileStream]$Stream, [string]$Label) {
    $Stream.Position = 0
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest = [Convert]::ToHexString($sha.ComputeHash($Stream)).ToLowerInvariant() } finally { $sha.Dispose() }
    $Stream.Position = 0
    $reader = [IO.StreamReader]::new($Stream, [Text.UTF8Encoding]::new($false, $true), $false, 4096, $true)
    try { $json = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $Stream.Position = 0
    try { $value = $json | ConvertFrom-Json -DateKind String } catch { throw "$Label is not valid UTF-8 JSON." }
    return [pscustomobject]@{ Value = $value; Sha256 = $digest }
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

function Assert-OwnerOnlyDirectory([string]$Path, [string]$Label = 'Directory') {
    $directory = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($Path)); $directory.Refresh()
    if (-not $directory.Exists -or $null -ne $directory.LinkTarget -or
        ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label must be a regular non-link directory."
    }
    for ($ancestor = $directory; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        $ancestor.Refresh()
        if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $null -ne $ancestor.LinkTarget) {
            throw "$Label path contains a link or reparse-point ancestor."
        }
    }
    if ($IsWindows) {
        $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $security = [IO.FileSystemAclExtensions]::GetAccessControl($directory)
        if ($security.GetOwner([Security.Principal.SecurityIdentifier]) -ne $owner) { throw "$Label must be owned by the current user." }
        foreach ($rule in $security.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and $rule.IdentityReference -ne $owner) {
                throw "$Label must have owner-only permissions."
            }
        }
    }
    else {
        $mode = [IO.File]::GetUnixFileMode($directory.FullName)
        $forbidden = [IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::GroupWrite -bor
            [IO.UnixFileMode]::GroupExecute -bor [IO.UnixFileMode]::OtherRead -bor
            [IO.UnixFileMode]::OtherWrite -bor [IO.UnixFileMode]::OtherExecute
        if (($mode -band [IO.UnixFileMode]::UserRead) -eq 0 -or ($mode -band $forbidden) -ne 0) {
            throw "$Label must have owner-only permissions."
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

function Get-DcpRuntime([int]$AppHostProcessId) {
    $dcp = Get-CimInstance Win32_Process | Where-Object {
        $_.Name -eq 'dcp.exe' -and $_.CommandLine -like '*start-apiserver*' -and
        $_.CommandLine -like "*--monitor $AppHostProcessId*"
    } | Select-Object -First 1
    if (-not $dcp) { return $null }
    $match = [regex]::Match($dcp.CommandLine, '--kubeconfig\s+"?(.+?)"?\s+--tls-cert')
    if (-not $match.Success) { throw 'The local DCP kubeconfig path could not be parsed.' }
    return [pscustomobject]@{ Kubeconfig = $match.Groups[1].Value; ProcessId = [int]$dcp.ProcessId }
}

function Get-SingleResource([object[]]$Items, [string]$Name) {
    $matches = @($Items | Where-Object { $_.metadata.name -like "$Name-*" })
    if ($matches.Count -ne 1) { throw "Expected exactly one local resource for '$Name'." }
    return $matches[0]
}

function Get-ResourceUrl([object]$Resource) {
    $endpoint = @($Resource.status.effectiveEnv | Where-Object name -eq 'ASPNETCORE_URLS' | ForEach-Object value)
    if ($endpoint.Count -ne 1 -or $endpoint[0] -notmatch '^https?://') {
        throw "Service '$($Resource.metadata.name)' has no unique exported local HTTP endpoint."
    }
    $baseUri = [Uri]$endpoint[0]
    if (-not $baseUri.IsLoopback) { throw "Service '$($Resource.metadata.name)' resolved to a non-local endpoint." }
    return $baseUri.AbsoluteUri.TrimEnd('/')
}

function Invoke-LocalReadinessProbe([object]$Resource, [object]$Route) {
    $name = [string]$Route.name
    $uri = (Get-ResourceUrl $Resource) + [string]$Route.readinessPath
    $response = Invoke-WebRequest -Uri $uri -Method Get -UseBasicParsing `
        -SkipCertificateCheck -SkipHttpErrorCheck -MaximumRedirection 0 -TimeoutSec 15
    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        throw "Service '$Name' readiness GET failed with HTTP $($response.StatusCode)."
    }
    return [ordered]@{ name = $Name; healthy = $true; probeId = 'readiness'; probeStatus = 'passed' }
}

function Invoke-AuthenticatedReadQueries([object[]]$Items, [object[]]$Queries) {
    $authResource = Get-SingleResource $Items 'legacy-maliev-auth-service'
    $intranetResource = Get-SingleResource $Items 'legacy-maliev-intranet-bff'
    $clientSecret = @($intranetResource.status.effectiveEnv | Where-Object name -eq 'ServiceAuthentication__ClientSecret' | ForEach-Object value)
    if ($clientSecret.Count -ne 1 -or [string]::IsNullOrWhiteSpace($clientSecret[0])) {
        throw 'The local Intranet service credential was not exported by Aspire.'
    }
    $loginResponse = Invoke-RestMethod -Uri ((Get-ResourceUrl $authResource) + '/auth/v1/service/login') `
        -Method Post -ContentType 'application/json' -Body (@{ clientId = 'legacy-intranet'; clientSecret = $clientSecret[0] } | ConvertTo-Json -Compress) `
        -SkipCertificateCheck -TimeoutSec 15
    $script:serviceToken = [string]$loginResponse.accessToken
    if ([string]::IsNullOrWhiteSpace($script:serviceToken)) { throw 'Service login returned no access token.' }
    $observations = @()
    foreach ($query in $Queries) {
        $resource = Get-SingleResource $Items ([string]$query.resource)
        $response = Invoke-WebRequest -Uri ((Get-ResourceUrl $resource) + [string]$query.path) -Method Get `
            -Headers @{ Authorization = "Bearer $script:serviceToken" } -UseBasicParsing -SkipCertificateCheck `
            -SkipHttpErrorCheck -MaximumRedirection 0 -TimeoutSec 15
        if ($response.StatusCode -ne 200) { throw "Authenticated read-only query '$($query.id)' failed with HTTP $($response.StatusCode)." }
        $observations += [ordered]@{ id = [string]$query.id; status = 'passed' }
    }
    return $observations
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
    $ApprovedBaselinePath, $RepositoryBaselinePath
)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) { throw "Required review input is missing: $requiredFile" }
}
if (Test-Path -LiteralPath $EvidencePath) { throw 'Terminal evidence output already exists; use a unique create-only path.' }
if (-not (Test-Path -LiteralPath $SnapshotDirectory -PathType Container)) { throw 'Snapshot directory is missing.' }
$SnapshotDirectory = [IO.Path]::GetFullPath($SnapshotDirectory)
Assert-OwnerOnlyDirectory $SnapshotDirectory 'Snapshot directory'
$manifestPath = Join-Path $SnapshotDirectory 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Authenticated snapshot manifest is missing.' }
$keyHandle = Open-ProtectedReadHandle $SnapshotEncryptionKeyFile 'Snapshot encryption key'
$manifestHandle = Open-ProtectedReadHandle $manifestPath 'Snapshot manifest'
$manifestInput = Read-ProtectedJsonAndSha $manifestHandle 'Snapshot manifest'
$manifest = $manifestInput.Value
if ($manifest.SchemaVersion -ne 2 -or $manifest.Format -cne 'MLVSNP02' -or
    $manifest.Encryption -cne 'AES-256-GCM-chunked-v2' -or $manifest.SnapshotId -cne $SnapshotId) {
    throw 'Snapshot is not the expected authenticated MLVSNP02 artifact.'
}
Assert-ExactNames @($manifest.Databases.Database) $migratedDatabases 'snapshot databases'
foreach ($entry in @($manifest.Databases)) {
    if ([string]::IsNullOrWhiteSpace([string]$entry.FileName) -or [IO.Path]::GetFileName([string]$entry.FileName) -cne [string]$entry.FileName) {
        throw "Snapshot archive file name is unsafe for '$($entry.Database)'."
    }
    [void](Open-ProtectedReadHandle (Join-Path $SnapshotDirectory ([string]$entry.FileName)) "Snapshot archive '$($entry.Database)'")
}
$manifestFileSha256 = $manifestInput.Sha256
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

$routeContractPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'contracts\local-snapshot-review-routes.json'
$routeContract = Get-Content -LiteralPath $routeContractPath -Raw | ConvertFrom-Json
Assert-ExactObjectKeys $routeContract @('schemaVersion', 'services', 'authenticatedQueries') '$routeContract'
if ($routeContract.schemaVersion -ne 1) { throw 'Local review route contract schema is unsupported.' }
Assert-ExactNames @($routeContract.services.name) $services 'route contract services'
Assert-ExactNames @($routeContract.authenticatedQueries.id) $authenticatedQueries 'route contract authenticated queries'
foreach ($query in @($routeContract.authenticatedQueries)) {
    Assert-ExactObjectKeys $query @('id', 'resource', 'method', 'path') '$routeContract.authenticatedQueries[]'
    if ($query.method -cne 'GET' -or $query.path -notmatch '^/[A-Za-z0-9/?=&._-]+$' -or $services -cnotcontains [string]$query.resource) {
        throw "Authenticated query route '$($query.id)' is not a reviewed local GET."
    }
}
foreach ($route in @($routeContract.services)) {
    Assert-ExactObjectKeys $route @('name', 'readinessPath') '$routeContract.services[]'
    if ($route.readinessPath -notmatch '^/[A-Za-z0-9/_-]+$') { throw "Readiness route for '$($route.name)' is invalid." }
}

$signedVerifier = Join-Path $PSScriptRoot 'verify-postgres-migration-evidence.ps1'
$migrationEvidenceHandle = Open-ProtectedReadHandle $MigrationEvidencePath 'Migration evidence'
& $signedVerifier -EvidencePath $MigrationEvidencePath -ExpectedDatabase $migratedDatabases `
    -RequiredAsOfUtc $RequiredAsOfUtc -TrustedPublicKeyPath $TrustedPublicKeyPath `
    -ExpectedAttestationKeyId $ExpectedAttestationKeyId -ApprovedBaselinePath $ApprovedBaselinePath `
    -ExpectedApprovedBaselineSha256 $ExpectedApprovedBaselineSha256 -ConsumptionLedgerPath $ConsumptionLedgerPath `
    -ExpectedRunId $ExpectedRunId -ExpectedTargetGeneration $ExpectedTargetGeneration -ExpectedRestoreId $ExpectedRestoreId
if ($LASTEXITCODE -ne 0) { throw 'Signed schema-v2 migration evidence validation failed.' }
$migrationEvidenceInput = Read-ProtectedJsonAndSha $migrationEvidenceHandle 'Migration evidence'
$migrationEvidence = $migrationEvidenceInput.Value
if ($migrationEvidence.mapping.sourceCommitSha -cne $ExpectedSourceCommitSha -or
    $migrationEvidence.source.snapshotId -cne $SnapshotId) {
    throw 'Snapshot, migration evidence, and authoritative source commit binding failed.'
}

$scriptRepositoryRoot = Split-Path -Parent $PSScriptRoot
$scriptCommit = (& git -C $scriptRepositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $scriptCommit -cne $appHostCommit -or
    @(& git -C $scriptRepositoryRoot status --porcelain=v1 --untracked-files=all).Count -ne 0) {
    throw 'The terminal gate script must execute from the reviewed clean AppHost commit.'
}
$appHostProject = Join-Path $scriptRepositoryRoot 'Legacy.Maliev.AppHost\Legacy.Maliev.AppHost.csproj'
$webProject = Join-Path $WorkspaceRoot 'Legacy.Maliev.Web\Legacy.Maliev.Web\Legacy.Maliev.Web.csproj'
$restoreArguments = @(
    'restore', $appHostProject,
    "-p:MalievWorkspaceRoot=$WorkspaceRoot", "-p:LegacyMalievWebProject=$webProject"
)
& dotnet @restoreArguments
if ($LASTEXITCODE -ne 0) { throw 'Canonical restore of the reviewed repository baseline failed.' }
$graphVerifier = Join-Path $PSScriptRoot 'test-canonical-runtime-build-graph.ps1'
& $graphVerifier -RootProjectPath $appHostProject -WorkspaceRoot $WorkspaceRoot -ReviewedRepository $repositories
$buildArguments = @(
    'build', $appHostProject, '--configuration', 'Release', '--no-incremental',
    "-p:MalievWorkspaceRoot=$WorkspaceRoot", "-p:LegacyMalievWebProject=$webProject"
)
& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) { throw 'Clean Release build of the reviewed repository baseline failed.' }
& $graphVerifier -RootProjectPath $appHostProject -WorkspaceRoot $WorkspaceRoot `
    -ReviewedRepository $repositories -RequireReleaseDeps
foreach ($entry in @($repositoryBaseline.repositories)) {
    $path = Join-Path $WorkspaceRoot $entry.name
    if ((& git -C $path branch --show-current).Trim() -cne 'main' -or
        (& git -C $path rev-parse HEAD).Trim() -cne $entry.commitSha -or
        (& git -C $path rev-parse origin/main).Trim() -cne $entry.commitSha -or
        (& git -C $path remote get-url origin).Trim() -cne $entry.originUrl -or
        @(& git -C $path status --porcelain=v1 --untracked-files=all).Count -ne 0) {
        throw "Repository '$($entry.name)' changed during the Release build."
    }
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
        if ($hostProcess) {
            $hostProcessId = [int]$hostProcess.ProcessId
            $dcpRuntime = Get-DcpRuntime $hostProcessId
            if ($dcpRuntime) { $kubeconfig = $dcpRuntime.Kubeconfig; $dcpProcessId = $dcpRuntime.ProcessId }
        }
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

    $containerResourceNames = @($items | Where-Object kind -eq 'Container' | ForEach-Object { [string]$_.metadata.name })
    $runningDockerNames = @(& docker ps --format '{{.Names}}')
    $localContainerNames = @($runningDockerNames | Where-Object {
        $candidate = $_
        @($containerResourceNames | Where-Object { $candidate -eq $_ -or $candidate -like "$_-*" }).Count -gt 0
    })
    if ($localContainerNames.Count -eq 0) { throw 'No run-owned local Docker resources were discoverable.' }

    $jobEvidence = @($terminalJobs | ForEach-Object { [ordered]@{ name = $_; state = 'finished'; exitCode = 0 } })
    $serviceEvidence = @($routeContract.services | ForEach-Object {
        Invoke-LocalReadinessProbe -Resource (Get-SingleResource $items ([string]$_.name)) -Route $_
    })
    $authenticatedQueryEvidence = @(Invoke-AuthenticatedReadQueries -Items $items -Queries @($routeContract.authenticatedQueries))
    Assert-ExactNames @($authenticatedQueryEvidence.id) $authenticatedQueries 'authenticated query observations'
    $validationSucceeded = $true
}
finally {
    $serviceToken = $null
    [Environment]::SetEnvironmentVariable('LEGACY_LOCAL_FIXTURES', $previousFixtures)
    if ($appHostProcess -and $appHostProcess.ProcessId) {
        Stop-Process -Id $appHostProcess.ProcessId -Force -ErrorAction SilentlyContinue
        $cleanupDeadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
        do {
            $runnerGone = $null -eq (Get-Process -Id $appHostProcess.ProcessId -ErrorAction SilentlyContinue)
            $hostGone = $null -eq $hostProcessId -or $null -eq (Get-Process -Id $hostProcessId -ErrorAction SilentlyContinue)
            $dcpGone = $null -eq $dcpProcessId -or $null -eq (Get-Process -Id $dcpProcessId -ErrorAction SilentlyContinue)
            $running = @(& docker ps -a --format '{{.Names}}' 2>$null)
            $containersGone = $LASTEXITCODE -eq 0 -and @($localContainerNames | Where-Object { $running -ccontains $_ }).Count -eq 0
            if (-not ($runnerGone -and $hostGone -and $dcpGone -and $containersGone)) { Start-Sleep -Milliseconds 500 }
        } while ([DateTimeOffset]::UtcNow -lt $cleanupDeadline -and -not ($runnerGone -and $hostGone -and $dcpGone -and $containersGone))
        $cleanupCompleted = $runnerGone -and $hostGone -and $dcpGone -and $containersGone
    }
    else { $cleanupCompleted = $true }
    foreach ($handle in $protectedHandles) { $handle.Dispose() }
}

if (-not $validationSucceeded -or -not $cleanupCompleted) {
    throw 'Local snapshot validation or cleanup did not complete; passed evidence is prohibited.'
}
foreach ($entry in @($repositoryBaseline.repositories)) {
    $path = Join-Path $WorkspaceRoot $entry.name
    if ((& git -C $path branch --show-current).Trim() -cne 'main' -or
        (& git -C $path rev-parse HEAD).Trim() -cne $entry.commitSha -or
        (& git -C $path rev-parse origin/main).Trim() -cne $entry.commitSha -or
        (& git -C $path remote get-url origin).Trim() -cne $entry.originUrl -or
        @(& git -C $path status --porcelain=v1 --untracked-files=all).Count -ne 0) {
        throw "Repository '$($entry.name)' changed during local snapshot validation."
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
$candidateEvidencePath = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($EvidencePath))) `
    ('.' + [IO.Path]::GetFileName($EvidencePath) + '.' + [Guid]::NewGuid().ToString('N') + '.candidate')
Write-AtomicJson $terminalEvidence $candidateEvidencePath
& (Join-Path $PSScriptRoot 'publish-local-snapshot-terminal-evidence.ps1') `
    -CandidateEvidencePath $candidateEvidencePath -FinalEvidencePath $EvidencePath `
    -ExpectedAppHostCommit $appHostCommit -ExpectedSourceCommitSha $ExpectedSourceCommitSha `
    -ExpectedSnapshotId $SnapshotId -ExpectedSemanticManifestDigestSha256 $ExpectedSemanticManifestDigestSha256 `
    -ExpectedManifestFileSha256 $ExpectedManifestFileSha256 `
    -ExpectedMigrationEvidencePayloadSha256 ([string]$migrationEvidence.attestation.payloadSha256) `
    -ExpectedApprovedBaselineSha256 $ExpectedApprovedBaselineSha256 `
    -ExpectedRepositoryBaselineSha256 $ExpectedRepositoryBaselineSha256 -MaximumAgeMinutes 30

Write-Output 'PASS: authenticated local snapshot terminal gate completed.'
