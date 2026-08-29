[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$EvidencePath,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string[]]$ExpectedDatabase,
    [Parameter(Mandatory = $true)] [DateTimeOffset]$RequiredAsOfUtc,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$TrustedPublicKeyPath,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$ExpectedAttestationKeyId
)

$ErrorActionPreference = 'Stop'

function Assert-ExactKeys {
    param([System.Collections.IDictionary]$Value, [string[]]$Expected, [string]$Path)
    $actual = @($Value.Keys | ForEach-Object { [string]$_ } | Sort-Object)
    $wanted = @($Expected | Sort-Object)
    if ($actual.Count -ne $wanted.Count -or (Compare-Object -ReferenceObject $wanted -DifferenceObject $actual)) {
        throw "$Path contains missing or unknown fields."
    }
}

function Assert-NoSensitiveKeys {
    param([AllowNull()] [object]$Value, [string]$Path)
    if ($Value -is [System.Collections.IDictionary]) {
        foreach ($key in $Value.Keys) {
            $keyText = [string]$key
            if ($keyText -match '(?i)(password|token|secret|private.?key|client.?secret|connection.?string|cookie|credential)') {
                throw "$Path contains a sensitive field '$keyText'."
            }
            Assert-NoSensitiveKeys $Value[$key] "$Path.$keyText"
        }
    }
    elseif ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        $index = 0
        foreach ($item in $Value) {
            Assert-NoSensitiveKeys $item "$Path[$index]"
            $index++
        }
    }
}

function Assert-Sha256 {
    param([object]$Value, [string]$Path)
    if ($Value -isnot [string] -or $Value -notmatch '^[0-9a-f]{64}$') {
        throw "$Path must be a lower-case 64-character SHA-256 hex value."
    }
}

function Assert-SafeIdentifier {
    param([object]$Value, [string]$Path)
    if ($Value -isnot [string] -or $Value -notmatch '^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$') {
        throw "$Path must be a bounded identifier without credentials or query parameters."
    }
}

function Assert-SafeBackupUri {
    param([object]$Value, [string]$Path)
    if ($Value -isnot [string] -or $Value -notmatch '^gs://[A-Za-z0-9._-]+(?:/[A-Za-z0-9._~!$&''()*+,;=:@%/-]*)*$') {
        throw "$Path must be a credential-free gs:// URI without query or fragment parameters."
    }
}

function Get-RequiredUtc {
    param([object]$Value, [string]$Path)
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value)) {
        throw "$Path must be an ISO-8601 UTC timestamp."
    }
    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse($Value, [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind, [ref]$parsed) -or $parsed.Offset -ne [TimeSpan]::Zero) {
        throw "$Path must be an ISO-8601 UTC timestamp with a zero offset."
    }
    if ($parsed -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
        throw "$Path cannot be in the future."
    }
    return $parsed.ToUniversalTime()
}

function ConvertTo-CanonicalValue {
    param([AllowNull()] [object]$Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [System.Collections.IDictionary]) {
        $ordered = [ordered]@{}
        foreach ($key in @($Value.Keys | ForEach-Object { [string]$_ } | Where-Object { $_ -ne 'attestation' } | Sort-Object)) {
            $ordered[$key] = ConvertTo-CanonicalValue $Value[$key]
        }
        return $ordered
    }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        [object[]]$items = @($Value | ForEach-Object { ConvertTo-CanonicalValue $_ })
        Write-Output -NoEnumerate $items
        return
    }
    return $Value
}

function Assert-SignedAttestation {
    param([System.Collections.IDictionary]$Evidence)
    Assert-ExactKeys $Evidence.attestation @('algorithm', 'keyId', 'payloadSha256', 'signatureBase64') '$.attestation'
    if ($Evidence.attestation.algorithm -ne 'ECDSA_P256_SHA256' -or
        $Evidence.attestation.keyId -ne $ExpectedAttestationKeyId) {
        throw 'Migration evidence is not signed by the expected P-256 attestation key.'
    }
    Assert-Sha256 $Evidence.attestation.payloadSha256 '$.attestation.payloadSha256'

    $canonical = ConvertTo-CanonicalValue $Evidence | ConvertTo-Json -Depth 100 -Compress
    $payloadHash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))
    $observedHash = [Convert]::ToHexString($payloadHash).ToLowerInvariant()
    if ($observedHash -cne $Evidence.attestation.payloadSha256) {
        throw 'Migration evidence payload hash does not match the signed receipt.'
    }

    if (-not (Test-Path -LiteralPath $TrustedPublicKeyPath -PathType Leaf)) {
        throw "Trusted attestation public key does not exist: $TrustedPublicKeyPath"
    }
    try {
        $signature = [Convert]::FromBase64String([string]$Evidence.attestation.signatureBase64)
        $ecdsa = [Security.Cryptography.ECDsa]::Create()
        try {
            $ecdsa.ImportFromPem((Get-Content -LiteralPath $TrustedPublicKeyPath -Raw))
            if ($ecdsa.KeySize -ne 256 -or -not $ecdsa.VerifyHash($payloadHash, $signature)) {
                throw 'Migration evidence signature verification failed.'
            }
        }
        finally { $ecdsa.Dispose() }
    }
    catch {
        throw 'Migration evidence signature verification failed.'
    }
}

if (-not (Test-Path -LiteralPath $EvidencePath -PathType Leaf)) {
    throw "Migration evidence does not exist: $EvidencePath"
}
try {
    $evidence = Get-Content -LiteralPath $EvidencePath -Raw | ConvertFrom-Json -AsHashtable -DateKind String
}
catch {
    throw "Migration evidence is not valid JSON: $EvidencePath"
}

Assert-ExactKeys $evidence @('schemaVersion', 'source', 'mapping', 'target', 'inventory', 'archives', 'databases', 'parity', 'constraints', 'attestation') '$'
Assert-NoSensitiveKeys $evidence '$'
if ($evidence.schemaVersion -isnot [long] -or $evidence.schemaVersion -ne 2) {
    throw 'Migration evidence has an unsupported schemaVersion.'
}
Assert-SignedAttestation $evidence

Assert-ExactKeys $evidence.source @('system', 'snapshotId', 'capturedAtUtc', 'backup') '$.source'
Assert-ExactKeys $evidence.source.backup @('uri', 'manifestSha256', 'databaseInventorySha256', 'objectGeneration', 'immutable') '$.source.backup'
Assert-ExactKeys $evidence.mapping @('schemaPlanVersion', 'planSha256', 'sourceCommitSha', 'runnerDigestSha256') '$.mapping'
Assert-ExactKeys $evidence.target @('system', 'cluster', 'namespace', 'mode', 'generation', 'capturedAtUtc', 'restoreId') '$.target'
Assert-ExactKeys $evidence.constraints @('productionDataWritesAllowed', 'canonicalTargetMutationAllowed', 'cutoverPercent', 'newNodePoolAllowed', 'cloudSqlAllowed', 'additionalInfrastructureCostAllowed') '$.constraints'

if ($evidence.source.system -ne 'sqlserver' -or $evidence.target.system -ne 'postgresql' -or
    $evidence.target.cluster -ne 'legacy-postgres-main' -or $evidence.target.namespace -ne 'maliev-legacy' -or
    $evidence.target.mode -ne 'shadow') {
    throw 'Migration evidence does not identify the approved SQL Server to PostgreSQL shadow boundary.'
}
Assert-SafeIdentifier $evidence.source.snapshotId '$.source.snapshotId'
Assert-SafeBackupUri $evidence.source.backup.uri '$.source.backup.uri'
Assert-Sha256 $evidence.source.backup.manifestSha256 '$.source.backup.manifestSha256'
Assert-Sha256 $evidence.source.backup.databaseInventorySha256 '$.source.backup.databaseInventorySha256'
Assert-SafeIdentifier $evidence.source.backup.objectGeneration '$.source.backup.objectGeneration'
if ($evidence.source.backup.immutable -isnot [bool] -or -not $evidence.source.backup.immutable) {
    throw 'Source backup provenance must be immutable.'
}
if ($evidence.mapping.schemaPlanVersion -ne '2.0' -or $evidence.mapping.sourceCommitSha -notmatch '^[0-9a-f]{40}$') {
    throw 'Migration mapping is not bound to the approved v2 source plan.'
}
Assert-Sha256 $evidence.mapping.planSha256 '$.mapping.planSha256'
Assert-Sha256 $evidence.mapping.runnerDigestSha256 '$.mapping.runnerDigestSha256'
Assert-SafeIdentifier $evidence.target.generation '$.target.generation'
Assert-SafeIdentifier $evidence.target.restoreId '$.target.restoreId'

$sourceCapturedAt = Get-RequiredUtc $evidence.source.capturedAtUtc '$.source.capturedAtUtc'
$targetCapturedAt = Get-RequiredUtc $evidence.target.capturedAtUtc '$.target.capturedAtUtc'
if ($RequiredAsOfUtc.Offset -ne [TimeSpan]::Zero) { throw 'RequiredAsOfUtc must use a zero UTC offset.' }
$requiredAt = $RequiredAsOfUtc.ToUniversalTime()
if ($sourceCapturedAt -lt $requiredAt) { throw "Source snapshot is older than the required as-of timestamp '$($requiredAt.ToString('O'))'." }
if ($targetCapturedAt -lt $sourceCapturedAt) { throw 'Target evidence predates the source snapshot.' }

if ($evidence.parity -ne 'exact') { throw "Migration parity is '$($evidence.parity)' rather than exact." }
if ($evidence.constraints.productionDataWritesAllowed -isnot [bool] -or $evidence.constraints.productionDataWritesAllowed -ne $false -or
    $evidence.constraints.canonicalTargetMutationAllowed -isnot [bool] -or $evidence.constraints.canonicalTargetMutationAllowed -ne $false -or
    $evidence.constraints.cutoverPercent -isnot [long] -or $evidence.constraints.cutoverPercent -ne 0 -or
    $evidence.constraints.newNodePoolAllowed -isnot [bool] -or $evidence.constraints.newNodePoolAllowed -ne $false -or
    $evidence.constraints.cloudSqlAllowed -isnot [bool] -or $evidence.constraints.cloudSqlAllowed -ne $false -or
    $evidence.constraints.additionalInfrastructureCostAllowed -isnot [bool] -or $evidence.constraints.additionalInfrastructureCostAllowed -ne $false) {
    throw 'Migration evidence violates the shadow-only/no-additional-infrastructure boundary.'
}

$approvedInventory = [ordered]@{
    ContactRequest = @('Legacy.Maliev.CompatibilityContracts', 'review_hold')
    Country = @('Legacy.Maliev.CatalogService', 'migrate'); Currency = @('Legacy.Maliev.CatalogService', 'migrate')
    Customer = @('Legacy.Maliev.CustomerService', 'migrate'); CustomerIdentity = @('Legacy.Maliev.AuthService', 'migrate')
    DataProtectionKeys = @('Legacy.Maliev.AuthService', 'migrate'); DataProtectionKeysEmployee = @('Legacy.Maliev.AuthService', 'migrate')
    Employee = @('Legacy.Maliev.EmployeeService', 'migrate'); EmployeeIdentity = @('Legacy.Maliev.AuthService', 'migrate')
    Hangfire = @('Legacy.Maliev.CompatibilityContracts', 'archive_only'); Invoice = @('Legacy.Maliev.AccountingService', 'migrate')
    JobOffers = @('Legacy.Maliev.CareerService', 'migrate'); LocationData = @('Legacy.Maliev.CompatibilityContracts', 'review_hold')
    Log = @('Legacy.Maliev.CompatibilityContracts', 'archive_only'); MachineLearning = @('Legacy.Maliev.CompatibilityContracts', 'excluded')
    MachineLearningData = @('Legacy.Maliev.CompatibilityContracts', 'excluded'); Material = @('Legacy.Maliev.CatalogService', 'migrate')
    Message = @('Legacy.Maliev.ContactService', 'migrate'); Order = @('Legacy.Maliev.OrderService', 'migrate')
    OrderStatus = @('Legacy.Maliev.OrderService', 'migrate'); Payment = @('Legacy.Maliev.AccountingService', 'migrate')
    PurchaseOrder = @('Legacy.Maliev.ProcurementService', 'migrate'); Quotation = @('Legacy.Maliev.QuotationService', 'migrate')
    QuotationRequest = @('Legacy.Maliev.QuotationService', 'migrate'); Receipt = @('Legacy.Maliev.AccountingService', 'migrate')
    Supplier = @('Legacy.Maliev.ProcurementService', 'migrate'); Upload = @('Legacy.Maliev.FileService', 'migrate')
}
if ($evidence.inventory -isnot [object[]] -or $evidence.inventory.Count -ne $approvedInventory.Count) {
    throw 'The migration inventory is incomplete.'
}
$inventoryNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($entry in $evidence.inventory) {
    Assert-ExactKeys $entry @('name', 'owner', 'disposition') '$.inventory[]'
    if (-not $inventoryNames.Add([string]$entry.name) -or -not $approvedInventory.Contains([string]$entry.name)) {
        throw "Migration inventory contains a duplicate or unknown database '$($entry.name)'."
    }
    $approved = $approvedInventory[[string]$entry.name]
    if ($entry.owner -cne $approved[0] -or $entry.disposition -cne $approved[1]) {
        throw "Database '$($entry.name)' has an unapproved owner or disposition."
    }
}

$approvedMigrated = @($approvedInventory.Keys | Where-Object { $approvedInventory[$_][1] -eq 'migrate' } | Sort-Object)
$callerExpectedRaw = @($ExpectedDatabase | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$callerExpected = @($callerExpectedRaw | Sort-Object -Unique)
if ($callerExpectedRaw.Count -ne $callerExpected.Count -or $callerExpected.Count -ne 21 -or
    (Compare-Object $approvedMigrated $callerExpected)) {
    throw 'ExpectedDatabase must equal the approved 21-database migrated inventory.'
}

if ($evidence.archives -isnot [object[]] -or $evidence.archives.Count -ne 2) { throw 'Immutable archive provenance is incomplete.' }
$archiveNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($archive in $evidence.archives) {
    Assert-ExactKeys $archive @('name', 'disposition', 'backupArtifactSha256', 'sourceSchemaSha256', 'sourceContentSha256', 'immutable') '$.archives[]'
    if (-not $archiveNames.Add([string]$archive.name) -or @('Hangfire', 'Log') -notcontains $archive.name -or
        $archive.disposition -ne 'archive_only' -or $archive.immutable -isnot [bool] -or -not $archive.immutable) {
        throw 'Immutable archive provenance contains a missing, duplicate, or unsafe entry.'
    }
    Assert-Sha256 $archive.backupArtifactSha256 '$.archives[].backupArtifactSha256'
    Assert-Sha256 $archive.sourceSchemaSha256 '$.archives[].sourceSchemaSha256'
    Assert-Sha256 $archive.sourceContentSha256 '$.archives[].sourceContentSha256'
}

if ($evidence.databases -isnot [object[]] -or $evidence.databases.Count -ne 21) { throw 'Exactly 21 migrated database receipts are required.' }
$databaseNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($database in $evidence.databases) {
    Assert-ExactKeys $database @('name', 'sourceSchemaSha256', 'mappingPlanSha256', 'targetSchemaSha256', 'sourceRowCount', 'targetRowCount', 'sourceContentSha256', 'targetContentSha256', 'foreignKeys', 'sequences', 'parity') '$.databases[]'
    if (-not $databaseNames.Add([string]$database.name) -or $approvedMigrated -notcontains $database.name) {
        throw "Migrated database receipt contains a duplicate or unknown database '$($database.name)'."
    }
    foreach ($field in @('sourceSchemaSha256', 'mappingPlanSha256', 'targetSchemaSha256', 'sourceContentSha256', 'targetContentSha256')) {
        Assert-Sha256 $database[$field] "$.databases[].$field"
    }
    if ($database.mappingPlanSha256 -cne $evidence.mapping.planSha256) { throw "Database '$($database.name)' is not bound to the approved mapping plan." }
    foreach ($field in @('sourceRowCount', 'targetRowCount')) {
        if ($database[$field] -isnot [long] -or $database[$field] -lt 0) { throw "Database '$($database.name)' has an invalid row count." }
    }
    if ($database.sourceRowCount -ne $database.targetRowCount -or
        $database.sourceContentSha256 -cne $database.targetContentSha256 -or $database.parity -ne 'exact') {
        throw "Database '$($database.name)' does not have exact row/content parity."
    }
    if ($database.foreignKeys -isnot [object[]]) { throw "Database '$($database.name)' must include per-constraint foreign-key evidence (an empty array is allowed)." }
    $foreignKeyNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($foreignKey in $database.foreignKeys) {
        Assert-ExactKeys $foreignKey @('name', 'sourceRelationshipCount', 'targetRelationshipCount', 'orphanCount') '$.databases[].foreignKeys[]'
        if (-not $foreignKeyNames.Add([string]$foreignKey.name) -or $foreignKey.name -notmatch '^[A-Za-z0-9_.:-]{1,128}$') {
            throw "Database '$($database.name)' has duplicate or unsafe foreign-key evidence."
        }
        foreach ($field in @('sourceRelationshipCount', 'targetRelationshipCount', 'orphanCount')) {
            if ($foreignKey[$field] -isnot [long] -or $foreignKey[$field] -lt 0) { throw "Database '$($database.name)' has invalid foreign-key evidence." }
        }
        if ($foreignKey.sourceRelationshipCount -ne $foreignKey.targetRelationshipCount -or $foreignKey.orphanCount -ne 0) {
            throw "Database '$($database.name)' does not have exact foreign-key parity."
        }
    }
    if ($database.sequences -isnot [object[]]) { throw "Database '$($database.name)' must include sequence evidence (an empty array is allowed)." }
    $sequenceNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($sequence in $database.sequences) {
        Assert-ExactKeys $sequence @('name', 'sourceNextValue', 'targetNextValue') '$.databases[].sequences[]'
        if (-not $sequenceNames.Add([string]$sequence.name) -or $sequence.name -notmatch '^[A-Za-z0-9_.:-]{1,128}$' -or
            $sequence.sourceNextValue -isnot [long] -or $sequence.targetNextValue -isnot [long] -or
            $sequence.sourceNextValue -ne $sequence.targetNextValue) {
            throw "Database '$($database.name)' has incomplete or mismatched sequence evidence."
        }
    }
}
if ((Compare-Object @($databaseNames | Sort-Object) $approvedMigrated)) { throw 'The migrated database receipt inventory is incomplete.' }

Write-Host "PASS: signed SQL Server-to-PostgreSQL shadow evidence validated for 21 databases as of $($requiredAt.ToString('O'))."
