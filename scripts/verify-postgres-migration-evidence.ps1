[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$EvidencePath,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string[]]$ExpectedDatabase,
    [Parameter(Mandatory = $true)] [DateTimeOffset]$RequiredAsOfUtc,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$TrustedPublicKeyPath,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$ExpectedAttestationKeyId,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$ApprovedBaselinePath,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$ExpectedApprovedBaselineSha256,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$ConsumptionLedgerPath,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$ExpectedRunId,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$ExpectedTargetGeneration,
    [Parameter(Mandatory = $true)] [ValidateNotNullOrEmpty()] [string]$ExpectedRestoreId
)

$ErrorActionPreference = 'Stop'

function Assert-ExactKeys {
    param([System.Collections.IDictionary]$Value, [string[]]$Expected, [string]$Path)
    $actual = @($Value.Keys | ForEach-Object { [string]$_ } | Sort-Object)
    $wanted = @($Expected | Sort-Object)
    if ($actual.Count -ne $wanted.Count -or (Compare-Object -ReferenceObject $wanted -DifferenceObject $actual -CaseSensitive)) {
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

function Get-InventorySha256 {
    param([string[]]$Names)
    [string[]]$sorted = @($Names)
    [Array]::Sort($sorted, [StringComparer]::Ordinal)
    $bytes = [Text.Encoding]::UTF8.GetBytes(($sorted -join "`n"))
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Assert-SafeIdentifier {
    param([object]$Value, [string]$Path)
    if ($Value -isnot [string] -or $Value -notmatch '^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$') {
        throw "$Path must be a bounded identifier without credentials or query parameters."
    }
}

function ConvertTo-NormalizedGuid {
    param([object]$Value, [string]$Path)
    $parsed = [Guid]::Empty
    if ($Value -isnot [string] -or -not [Guid]::TryParseExact($Value, 'D', [ref]$parsed) -or $parsed -eq [Guid]::Empty) {
        throw "$Path must be a non-empty canonical GUID."
    }
    return $parsed.ToString('D').ToLowerInvariant()
}

function Assert-SafeBackupUri {
    param([object]$Value, [string]$Path)
    if ($Value -isnot [string] -or $Value -notmatch '^gs://[A-Za-z0-9._-]+(?:/[A-Za-z0-9._~!$&''()*+,;=:@%/-]*)*$') {
        throw "$Path must be a credential-free gs:// URI without query or fragment parameters."
    }
}

function Get-ExactNameInventory {
    param([object]$Value, [string]$Path, [bool]$AllowEmpty = $true)
    if ($Value -isnot [object[]] -or (-not $AllowEmpty -and $Value.Count -eq 0)) {
        throw "$Path must be an explicit JSON array."
    }
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $Value) {
        if ($name -isnot [string] -or $name -notmatch '^[A-Za-z0-9_.:-]{1,128}$' -or -not $names.Add($name)) {
            throw "$Path contains an empty, unsafe, or duplicate name."
        }
    }
    return @($names | Sort-Object)
}

function Get-TablePlanSha256 {
    param(
        [string]$Name,
        [string[]]$Columns,
        [string[]]$ApprovedAggregates,
        [long]$ExpectedBatchCount,
        [string]$BatchInventorySha256
    )
    $canonical = @(
        "name=$Name"
        "columnsSha256=$(Get-InventorySha256 $Columns)"
        "approvedAggregatesSha256=$(Get-InventorySha256 $ApprovedAggregates)"
        "expectedBatchCount=$ExpectedBatchCount"
        "batchInventorySha256=$BatchInventorySha256"
    ) -join "`n"
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
}

function Get-RequiredUtc {
    param([object]$Value, [string]$Path, [switch]$AllowFuture)
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value)) {
        throw "$Path must be an ISO-8601 UTC timestamp."
    }
    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse($Value, [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind, [ref]$parsed) -or $parsed.Offset -ne [TimeSpan]::Zero) {
        throw "$Path must be an ISO-8601 UTC timestamp with a zero offset."
    }
    if (-not $AllowFuture -and $parsed -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
        throw "$Path cannot be in the future."
    }
    return $parsed.ToUniversalTime()
}

function ConvertTo-CanonicalValue {
    param([AllowNull()] [object]$Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [System.Collections.IDictionary]) {
        $ordered = [ordered]@{}
        [string[]]$keys = @($Value.Keys | ForEach-Object { [string]$_ } | Where-Object { $_ -ne 'attestation' })
        [Array]::Sort($keys, [StringComparer]::Ordinal)
        foreach ($key in $keys) {
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

Assert-ExactKeys $evidence @('schemaVersion', 'source', 'mapping', 'target', 'execution', 'inventory', 'archives', 'databases', 'parity', 'constraints', 'attestation') '$'
Assert-NoSensitiveKeys $evidence '$'
if ($evidence.schemaVersion -isnot [long] -or $evidence.schemaVersion -ne 2) {
    throw 'Migration evidence has an unsupported schemaVersion.'
}
Assert-SignedAttestation $evidence

Assert-ExactKeys $evidence.source @('system', 'snapshotId', 'capturedAtUtc', 'backup') '$.source'
Assert-ExactKeys $evidence.source.backup @('uri', 'manifestSha256', 'databaseInventorySha256', 'objectGeneration', 'immutable') '$.source.backup'
Assert-ExactKeys $evidence.mapping @('schemaPlanVersion', 'planSha256', 'sourceCommitSha', 'runnerDigestSha256', 'databases') '$.mapping'
Assert-ExactKeys $evidence.target @('system', 'cluster', 'namespace', 'mode', 'generation', 'capturedAtUtc', 'restoreId') '$.target'
Assert-ExactKeys $evidence.execution @('runId', 'evidenceId', 'issuedAtUtc', 'expiresAtUtc', 'leaseId', 'leaseAcquiredAtUtc', 'leaseExpiresAtUtc', 'targetGeneration', 'restoreId', 'state') '$.execution'
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
$normalizedRunId = ConvertTo-NormalizedGuid $evidence.execution.runId '$.execution.runId'
$normalizedEvidenceId = ConvertTo-NormalizedGuid $evidence.execution.evidenceId '$.execution.evidenceId'
$normalizedLeaseId = ConvertTo-NormalizedGuid $evidence.execution.leaseId '$.execution.leaseId'
$normalizedExpectedRunId = ConvertTo-NormalizedGuid $ExpectedRunId 'ExpectedRunId'
if ($normalizedRunId -cne $normalizedExpectedRunId -or
    $evidence.execution.targetGeneration -cne $ExpectedTargetGeneration -or
    $evidence.execution.restoreId -cne $ExpectedRestoreId -or
    $evidence.execution.targetGeneration -cne $evidence.target.generation -or
    $evidence.execution.restoreId -cne $evidence.target.restoreId -or
    $evidence.execution.state -ne 'completed') {
    throw 'Migration execution identity does not match the authorized run, target generation, restore, or terminal state.'
}
if ($normalizedRunId -ceq $normalizedEvidenceId -or
    $normalizedRunId -ceq $normalizedLeaseId -or
    $normalizedEvidenceId -ceq $normalizedLeaseId) {
    throw 'Run, evidence, and lease identifiers must be unique.'
}

$sourceCapturedAt = Get-RequiredUtc $evidence.source.capturedAtUtc '$.source.capturedAtUtc'
$targetCapturedAt = Get-RequiredUtc $evidence.target.capturedAtUtc '$.target.capturedAtUtc'
if ($RequiredAsOfUtc.Offset -ne [TimeSpan]::Zero) { throw 'RequiredAsOfUtc must use a zero UTC offset.' }
$requiredAt = $RequiredAsOfUtc.ToUniversalTime()
if ($sourceCapturedAt -lt $requiredAt) { throw "Source snapshot is older than the required as-of timestamp '$($requiredAt.ToString('O'))'." }
if ($targetCapturedAt -lt $sourceCapturedAt) { throw 'Target evidence predates the source snapshot.' }
$issuedAt = Get-RequiredUtc $evidence.execution.issuedAtUtc '$.execution.issuedAtUtc'
$expiresAt = Get-RequiredUtc $evidence.execution.expiresAtUtc '$.execution.expiresAtUtc' -AllowFuture
$leaseAcquiredAt = Get-RequiredUtc $evidence.execution.leaseAcquiredAtUtc '$.execution.leaseAcquiredAtUtc'
$leaseExpiresAt = Get-RequiredUtc $evidence.execution.leaseExpiresAtUtc '$.execution.leaseExpiresAtUtc' -AllowFuture
$nowUtc = [DateTimeOffset]::UtcNow
if ($targetCapturedAt -lt $issuedAt -or $expiresAt -le $issuedAt -or $expiresAt - $issuedAt -gt [TimeSpan]::FromHours(1) -or
    $nowUtc -gt $expiresAt -or $leaseAcquiredAt -lt $issuedAt -or $targetCapturedAt -lt $leaseAcquiredAt -or
    $leaseExpiresAt -le $leaseAcquiredAt -or $targetCapturedAt -gt $leaseExpiresAt -or
    $leaseExpiresAt -gt $expiresAt -or $nowUtc -gt $leaseExpiresAt) {
    throw 'Migration evidence or its one-time lease is stale, future-dated, expired, or exceeds the one-hour authorization window.'
}

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
    ContactRequest = @('Legacy.Maliev.ContactService', 'migrate')
    Country = @('Legacy.Maliev.CatalogService', 'migrate'); Currency = @('Legacy.Maliev.CatalogService', 'migrate')
    Customer = @('Legacy.Maliev.CustomerService', 'migrate'); CustomerIdentity = @('Legacy.Maliev.AuthService', 'migrate')
    DataProtectionKeys = @('Legacy.Maliev.AuthService', 'migrate'); DataProtectionKeysEmployee = @('Legacy.Maliev.AuthService', 'migrate')
    Employee = @('Legacy.Maliev.EmployeeService', 'migrate'); EmployeeIdentity = @('Legacy.Maliev.AuthService', 'migrate')
    Invoice = @('Legacy.Maliev.AccountingService', 'migrate')
    JobOffers = @('Legacy.Maliev.CareerService', 'migrate'); LocationData = @('Legacy.Maliev.CatalogService', 'migrate')
    Log = @('Legacy.Maliev.CompatibilityContracts', 'migrate'); Hangfire = @('Legacy.Maliev.CompatibilityContracts', 'excluded')
    MachineLearning = @('Legacy.Maliev.CompatibilityContracts', 'excluded')
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
    if (-not $inventoryNames.Add([string]$entry.name) -or @($approvedInventory.Keys) -cnotcontains [string]$entry.name) {
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
if ($callerExpectedRaw.Count -ne $callerExpected.Count -or $callerExpected.Count -ne 24 -or
    (Compare-Object $approvedMigrated $callerExpected -CaseSensitive)) {
    throw 'ExpectedDatabase must equal the approved 24-database migrated inventory.'
}

Assert-Sha256 $ExpectedApprovedBaselineSha256 'ExpectedApprovedBaselineSha256'
if (-not (Test-Path -LiteralPath $ApprovedBaselinePath -PathType Leaf)) {
    throw 'The independently approved migration baseline file is missing.'
}
$approvedBaselineBytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $ApprovedBaselinePath))
$approvedBaselineHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($approvedBaselineBytes)).ToLowerInvariant()
if ($approvedBaselineHash -cne $ExpectedApprovedBaselineSha256) {
    throw 'The independently approved migration baseline file hash does not match the owner-approved hash.'
}
try {
    $approvedBaseline = [Text.Encoding]::UTF8.GetString($approvedBaselineBytes) | ConvertFrom-Json -AsHashtable -Depth 100
}
catch {
    throw 'The independently approved migration baseline is not valid JSON.'
}
Assert-ExactKeys $approvedBaseline @('schemaVersion', 'sourceCommitSha', 'planSha256', 'databases') '$baseline'
Assert-NoSensitiveKeys $approvedBaseline '$baseline'
if ($approvedBaseline.schemaVersion -isnot [long] -or $approvedBaseline.schemaVersion -ne 2) {
    throw 'The independently approved migration baseline has an unsupported schema version.'
}
Assert-Sha256 $approvedBaseline.planSha256 '$baseline.planSha256'
if ($approvedBaseline.sourceCommitSha -isnot [string] -or $approvedBaseline.sourceCommitSha -notmatch '^[0-9a-f]{40}$' -or
    $approvedBaseline.sourceCommitSha -cne $evidence.mapping.sourceCommitSha -or
    $approvedBaseline.planSha256 -cne $evidence.mapping.planSha256) {
    throw 'The signed evidence mapping is not bound to the owner-approved source commit and plan hash.'
}
if ($approvedBaseline.databases -isnot [object[]] -or $approvedBaseline.databases.Count -ne 24) {
    throw 'The independently approved baseline must describe all 24 migrated databases.'
}
$approvedBaselineDatabases = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
foreach ($baselineDatabase in $approvedBaseline.databases) {
    Assert-ExactKeys $baselineDatabase @('name', 'tableInventorySha256', 'foreignKeyInventorySha256', 'sequenceInventorySha256', 'tables', 'foreignKeys', 'sequences') '$baseline.databases[]'
    if ($baselineDatabase.name -isnot [string] -or $approvedMigrated -cnotcontains $baselineDatabase.name -or
        $approvedBaselineDatabases.ContainsKey($baselineDatabase.name)) {
        throw 'The independently approved baseline contains a duplicate or unknown database.'
    }
    if ($baselineDatabase.tables -isnot [object[]] -or $baselineDatabase.tables.Count -eq 0) {
        throw 'Every independently approved database baseline must include explicit per-table plans.'
    }
    $baselineTablePlans = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($baselineTable in $baselineDatabase.tables) {
        Assert-ExactKeys $baselineTable @('name', 'columns', 'approvedAggregates', 'expectedBatchCount', 'batchInventorySha256', 'tablePlanSha256') '$baseline.databases[].tables[]'
        if ($baselineTable.name -isnot [string] -or $baselineTable.name -notmatch '^[A-Za-z0-9_.:-]{1,128}$' -or
            $baselineTablePlans.ContainsKey($baselineTable.name)) {
            throw 'The independently approved baseline contains a duplicate or unsafe table plan.'
        }
        $baselineColumns = @(Get-ExactNameInventory $baselineTable.columns '$baseline.databases[].tables[].columns' $false)
        $baselineAggregates = @(Get-ExactNameInventory $baselineTable.approvedAggregates '$baseline.databases[].tables[].approvedAggregates')
        if ($baselineTable.expectedBatchCount -isnot [long] -or $baselineTable.expectedBatchCount -lt 0) {
            throw 'The independently approved baseline contains an invalid expected batch count.'
        }
        Assert-Sha256 $baselineTable.batchInventorySha256 '$baseline.databases[].tables[].batchInventorySha256'
        Assert-Sha256 $baselineTable.tablePlanSha256 '$baseline.databases[].tables[].tablePlanSha256'
        $recomputedTablePlanSha256 = Get-TablePlanSha256 $baselineTable.name $baselineColumns $baselineAggregates $baselineTable.expectedBatchCount $baselineTable.batchInventorySha256
        if ($baselineTable.tablePlanSha256 -cne $recomputedTablePlanSha256) {
            throw 'The independently approved baseline table-plan hash does not match its exact nested evidence.'
        }
        $baselineTablePlans[$baselineTable.name] = [ordered]@{
            Columns = $baselineColumns
            Aggregates = $baselineAggregates
            ExpectedBatchCount = $baselineTable.expectedBatchCount
            BatchInventorySha256 = $baselineTable.batchInventorySha256
            TablePlanSha256 = $recomputedTablePlanSha256
        }
    }
    $baselineTables = @($baselineTablePlans.Keys | Sort-Object)
    $baselineForeignKeys = @(Get-ExactNameInventory $baselineDatabase.foreignKeys '$baseline.databases[].foreignKeys')
    $baselineSequences = @(Get-ExactNameInventory $baselineDatabase.sequences '$baseline.databases[].sequences')
    foreach ($field in @('tableInventorySha256', 'foreignKeyInventorySha256', 'sequenceInventorySha256')) {
        Assert-Sha256 $baselineDatabase[$field] "`$baseline.databases[].$field"
    }
    if ($baselineDatabase.tableInventorySha256 -cne (Get-InventorySha256 $baselineTables) -or
        $baselineDatabase.foreignKeyInventorySha256 -cne (Get-InventorySha256 $baselineForeignKeys) -or
        $baselineDatabase.sequenceInventorySha256 -cne (Get-InventorySha256 $baselineSequences)) {
        throw 'The independently approved baseline inventory hashes do not match its canonical explicit inventories.'
    }
    $approvedBaselineDatabases[$baselineDatabase.name] = [ordered]@{
        Receipt = $baselineDatabase
        Tables = $baselineTablePlans
        ForeignKeys = $baselineForeignKeys
        Sequences = $baselineSequences
    }
}
if ((Compare-Object @($approvedBaselineDatabases.Keys | Sort-Object) $approvedMigrated -CaseSensitive)) {
    throw 'The independently approved baseline database inventory is incomplete.'
}

if ($evidence.mapping.databases -isnot [object[]] -or $evidence.mapping.databases.Count -ne 24) {
    throw 'The signed mapping plan must explicitly describe all 24 migrated databases.'
}
$mappingPlans = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
foreach ($databasePlan in $evidence.mapping.databases) {
    Assert-ExactKeys $databasePlan @('name', 'tableInventorySha256', 'foreignKeyInventorySha256', 'sequenceInventorySha256', 'expectedTableCount', 'expectedForeignKeyCount', 'expectedSequenceCount', 'tables', 'foreignKeys', 'sequences') '$.mapping.databases[]'
    if ($databasePlan.name -isnot [string] -or $approvedMigrated -cnotcontains $databasePlan.name -or $mappingPlans.ContainsKey($databasePlan.name)) {
        throw 'The signed mapping plan contains a duplicate or unknown database.'
    }
    foreach ($field in @('tableInventorySha256', 'foreignKeyInventorySha256', 'sequenceInventorySha256')) {
        Assert-Sha256 $databasePlan[$field] "$.mapping.databases[].$field"
    }
    $foreignKeyPlanNames = @(Get-ExactNameInventory $databasePlan.foreignKeys '$.mapping.databases[].foreignKeys')
    $sequencePlanNames = @(Get-ExactNameInventory $databasePlan.sequences '$.mapping.databases[].sequences')
    foreach ($field in @('expectedTableCount', 'expectedForeignKeyCount', 'expectedSequenceCount')) {
        if ($databasePlan[$field] -isnot [long] -or $databasePlan[$field] -lt 0) { throw 'The signed mapping plan has an invalid inventory count.' }
    }
    if ($databasePlan.tables -isnot [object[]] -or $databasePlan.tables.Count -eq 0) {
        throw 'Every migrated database must have an explicit non-empty table plan.'
    }
    if ($databasePlan.expectedTableCount -ne $databasePlan.tables.Count -or
        $databasePlan.expectedForeignKeyCount -ne $foreignKeyPlanNames.Count -or
        $databasePlan.expectedSequenceCount -ne $sequencePlanNames.Count) {
        throw 'The signed mapping-plan inventory counts do not match their explicit inventories.'
    }
    $tablePlans = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($tablePlan in $databasePlan.tables) {
        Assert-ExactKeys $tablePlan @('name', 'columns', 'approvedAggregates', 'expectedColumnCount', 'expectedAggregateCount', 'expectedBatchCount', 'batchInventorySha256') '$.mapping.databases[].tables[]'
        if ($tablePlan.name -isnot [string] -or $tablePlan.name -notmatch '^[A-Za-z0-9_.:-]{1,128}$' -or $tablePlans.ContainsKey($tablePlan.name)) {
            throw 'The signed mapping plan contains a duplicate or unsafe table name.'
        }
        $columns = @(Get-ExactNameInventory $tablePlan.columns '$.mapping.databases[].tables[].columns' $false)
        $aggregates = @(Get-ExactNameInventory $tablePlan.approvedAggregates '$.mapping.databases[].tables[].approvedAggregates')
        foreach ($field in @('expectedColumnCount', 'expectedAggregateCount', 'expectedBatchCount')) {
            if ($tablePlan[$field] -isnot [long] -or $tablePlan[$field] -lt 0) { throw 'The signed mapping plan has an invalid expected table evidence count.' }
        }
        if ($tablePlan.expectedColumnCount -ne $columns.Count -or $tablePlan.expectedAggregateCount -ne $aggregates.Count) {
            throw 'The signed table-plan column or aggregate count does not match its inventory.'
        }
        Assert-Sha256 $tablePlan.batchInventorySha256 '$.mapping.databases[].tables[].batchInventorySha256'
        $tablePlanSha256 = Get-TablePlanSha256 $tablePlan.name $columns $aggregates $tablePlan.expectedBatchCount $tablePlan.batchInventorySha256
        $tablePlans[$tablePlan.name] = [ordered]@{
            Columns = $columns
            Aggregates = $aggregates
            ExpectedColumnCount = $tablePlan.expectedColumnCount
            ExpectedAggregateCount = $tablePlan.expectedAggregateCount
            ExpectedBatchCount = $tablePlan.expectedBatchCount
            BatchInventorySha256 = $tablePlan.batchInventorySha256
            TablePlanSha256 = $tablePlanSha256
        }
    }
    $tablePlanNames = @($tablePlans.Keys | Sort-Object)
    if ($databasePlan.tableInventorySha256 -cne (Get-InventorySha256 $tablePlanNames) -or
        $databasePlan.foreignKeyInventorySha256 -cne (Get-InventorySha256 $foreignKeyPlanNames) -or
        $databasePlan.sequenceInventorySha256 -cne (Get-InventorySha256 $sequencePlanNames)) {
        throw 'The signed mapping-plan inventory hashes do not match its canonical explicit inventories.'
    }
    $approvedPlan = $approvedBaselineDatabases[$databasePlan.name]
    if ($databasePlan.tableInventorySha256 -cne $approvedPlan.Receipt.tableInventorySha256 -or
        $databasePlan.foreignKeyInventorySha256 -cne $approvedPlan.Receipt.foreignKeyInventorySha256 -or
        $databasePlan.sequenceInventorySha256 -cne $approvedPlan.Receipt.sequenceInventorySha256 -or
        (Compare-Object $tablePlanNames @($approvedPlan.Tables.Keys | Sort-Object) -CaseSensitive) -or
        (Compare-Object $foreignKeyPlanNames $approvedPlan.ForeignKeys -CaseSensitive) -or
        (Compare-Object $sequencePlanNames $approvedPlan.Sequences -CaseSensitive)) {
        throw 'The signed mapping-plan inventories differ from the independently owner-approved baseline.'
    }
    foreach ($tablePlanName in $tablePlanNames) {
        $tablePlan = $tablePlans[$tablePlanName]
        $approvedTablePlan = $approvedPlan.Tables[$tablePlanName]
        if ($tablePlan.TablePlanSha256 -cne $approvedTablePlan.TablePlanSha256 -or
            $tablePlan.ExpectedBatchCount -ne $approvedTablePlan.ExpectedBatchCount -or
            $tablePlan.BatchInventorySha256 -cne $approvedTablePlan.BatchInventorySha256 -or
            (Compare-Object $tablePlan.Columns $approvedTablePlan.Columns -CaseSensitive) -or
            (Compare-Object $tablePlan.Aggregates $approvedTablePlan.Aggregates -CaseSensitive)) {
            throw "The signed table plan '$tablePlanName' differs from the independently owner-approved nested evidence."
        }
    }
    $mappingPlans[$databasePlan.name] = [ordered]@{
        Receipt = $databasePlan
        Tables = $tablePlans
        ForeignKeys = $foreignKeyPlanNames
        Sequences = $sequencePlanNames
    }
}
if ((Compare-Object @($mappingPlans.Keys | Sort-Object) $approvedMigrated -CaseSensitive)) {
    throw 'The signed mapping-plan database inventory is incomplete.'
}

if ($evidence.archives -isnot [object[]] -or $evidence.archives.Count -ne 0) { throw 'Migrated inventory must not retain backup-only archive entries.' }
$archiveNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($archive in $evidence.archives) {
    Assert-ExactKeys $archive @('name', 'disposition', 'backupArtifactSha256', 'sourceSchemaSha256', 'sourceContentSha256', 'immutable') '$.archives[]'
    if (-not $archiveNames.Add([string]$archive.name) -or @('Log') -cnotcontains $archive.name -or
        $archive.disposition -ne 'archive_only' -or $archive.immutable -isnot [bool] -or -not $archive.immutable) {
        throw 'Immutable archive provenance contains a missing, duplicate, or unsafe entry.'
    }
    Assert-Sha256 $archive.backupArtifactSha256 '$.archives[].backupArtifactSha256'
    Assert-Sha256 $archive.sourceSchemaSha256 '$.archives[].sourceSchemaSha256'
    Assert-Sha256 $archive.sourceContentSha256 '$.archives[].sourceContentSha256'
}

if ($evidence.databases -isnot [object[]] -or $evidence.databases.Count -ne 24) { throw 'Exactly 24 migrated database receipts are required.' }
$databaseNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($database in $evidence.databases) {
    Assert-ExactKeys $database @('name', 'sourceSchemaSha256', 'mappingPlanSha256', 'targetSchemaSha256', 'sourceRowCount', 'targetRowCount', 'sourceContentSha256', 'targetContentSha256', 'tableInventorySha256', 'foreignKeyInventorySha256', 'sequenceInventorySha256', 'tableCount', 'foreignKeyCount', 'sequenceCount', 'tables', 'foreignKeys', 'sequences', 'parity') '$.databases[]'
    if (-not $databaseNames.Add([string]$database.name) -or $approvedMigrated -cnotcontains $database.name) {
        throw "Migrated database receipt contains a duplicate or unknown database '$($database.name)'."
    }
    $plan = $mappingPlans[$database.name]
    foreach ($field in @('sourceSchemaSha256', 'mappingPlanSha256', 'targetSchemaSha256', 'sourceContentSha256', 'targetContentSha256', 'tableInventorySha256', 'foreignKeyInventorySha256', 'sequenceInventorySha256')) {
        Assert-Sha256 $database[$field] "$.databases[].$field"
    }
    if ($database.mappingPlanSha256 -cne $evidence.mapping.planSha256 -or
        $database.tableInventorySha256 -cne $plan.Receipt.tableInventorySha256 -or
        $database.foreignKeyInventorySha256 -cne $plan.Receipt.foreignKeyInventorySha256 -or
        $database.sequenceInventorySha256 -cne $plan.Receipt.sequenceInventorySha256) {
        throw "Database '$($database.name)' is not bound to the exact signed schema/inventory plan."
    }
    foreach ($field in @('tableCount', 'foreignKeyCount', 'sequenceCount')) {
        if ($database[$field] -isnot [long] -or $database[$field] -lt 0) { throw "Database '$($database.name)' has an invalid observed inventory count." }
    }
    if ($database.tableCount -ne $plan.Receipt.expectedTableCount -or
        $database.foreignKeyCount -ne $plan.Receipt.expectedForeignKeyCount -or
        $database.sequenceCount -ne $plan.Receipt.expectedSequenceCount) {
        throw "Database '$($database.name)' observed inventory counts do not match the signed plan."
    }
    foreach ($field in @('sourceRowCount', 'targetRowCount')) {
        if ($database[$field] -isnot [long] -or $database[$field] -lt 0) { throw "Database '$($database.name)' has an invalid row count." }
    }
    if ($database.sourceRowCount -ne $database.targetRowCount -or $database.sourceContentSha256 -cne $database.targetContentSha256 -or $database.parity -ne 'exact') {
        throw "Database '$($database.name)' does not have exact row/content parity."
    }

    if ($database.tables -isnot [object[]] -or $database.tables.Count -ne $plan.Tables.Count) {
        throw "Database '$($database.name)' does not include every signed table receipt."
    }
    $tableNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [long]$sourceTableRows = 0
    [long]$targetTableRows = 0
    foreach ($table in $database.tables) {
        Assert-ExactKeys $table @('name', 'sourceRowCount', 'targetRowCount', 'columnCount', 'aggregateCount', 'batchCount', 'columns', 'aggregates', 'batchInventorySha256', 'batches', 'parity') '$.databases[].tables[]'
        if (-not $tableNames.Add([string]$table.name) -or -not $plan.Tables.ContainsKey($table.name)) {
            throw "Database '$($database.name)' contains a duplicate or unplanned table receipt."
        }
        $tablePlan = $plan.Tables[$table.name]
        foreach ($field in @('columnCount', 'aggregateCount', 'batchCount')) {
            if ($table[$field] -isnot [long] -or $table[$field] -lt 0) { throw "Table '$($table.name)' has an invalid observed evidence count." }
        }
        if ($table.columnCount -ne $tablePlan.ExpectedColumnCount -or
            $table.aggregateCount -ne $tablePlan.ExpectedAggregateCount -or
            $table.batchCount -ne $tablePlan.ExpectedBatchCount) {
            throw "Table '$($table.name)' observed evidence counts do not match the signed plan."
        }
        foreach ($field in @('sourceRowCount', 'targetRowCount')) {
            if ($table[$field] -isnot [long] -or $table[$field] -lt 0) { throw "Table '$($table.name)' has an invalid row count." }
        }
        if ($table.sourceRowCount -ne $table.targetRowCount -or $table.parity -ne 'exact') { throw "Table '$($table.name)' does not have exact row parity." }
        $sourceTableRows += $table.sourceRowCount
        $targetTableRows += $table.targetRowCount

        if ($table.columns -isnot [object[]] -or $table.columns.Count -ne $tablePlan.Columns.Count) { throw "Table '$($table.name)' has incomplete column evidence." }
        $columnNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($column in $table.columns) {
            Assert-ExactKeys $column @('name', 'sourceNullCount', 'targetNullCount') '$.databases[].tables[].columns[]'
            if (-not $columnNames.Add([string]$column.name) -or $tablePlan.Columns -cnotcontains $column.name -or
                $column.sourceNullCount -isnot [long] -or $column.targetNullCount -isnot [long] -or
                $column.sourceNullCount -lt 0 -or $column.sourceNullCount -gt $table.sourceRowCount -or
                $column.sourceNullCount -ne $column.targetNullCount) {
                throw "Table '$($table.name)' has incomplete or mismatched per-column null evidence."
            }
        }

        if ($table.aggregates -isnot [object[]] -or $table.aggregates.Count -ne $tablePlan.Aggregates.Count) { throw "Table '$($table.name)' has incomplete approved-aggregate evidence." }
        $aggregateNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($aggregate in $table.aggregates) {
            Assert-ExactKeys $aggregate @('name', 'sourceValueSha256', 'targetValueSha256') '$.databases[].tables[].aggregates[]'
            if (-not $aggregateNames.Add([string]$aggregate.name) -or $tablePlan.Aggregates -cnotcontains $aggregate.name) { throw "Table '$($table.name)' has an unapproved aggregate." }
            Assert-Sha256 $aggregate.sourceValueSha256 '$.databases[].tables[].aggregates[].sourceValueSha256'
            Assert-Sha256 $aggregate.targetValueSha256 '$.databases[].tables[].aggregates[].targetValueSha256'
            if ($aggregate.sourceValueSha256 -cne $aggregate.targetValueSha256) { throw "Table '$($table.name)' has aggregate drift." }
        }

        Assert-Sha256 $table.batchInventorySha256 '$.databases[].tables[].batchInventorySha256'
        if ($table.batchInventorySha256 -cne $tablePlan.BatchInventorySha256 -or $table.batches -isnot [object[]] -or
            $table.batches.Count -ne $tablePlan.ExpectedBatchCount) { throw "Table '$($table.name)' has incomplete signed batch evidence." }
        $batchOrdinals = [Collections.Generic.HashSet[long]]::new()
        [long]$sourceBatchRows = 0
        [long]$targetBatchRows = 0
        foreach ($batch in $table.batches) {
            Assert-ExactKeys $batch @('ordinal', 'sourceRowCount', 'targetRowCount', 'sourceContentSha256', 'targetContentSha256') '$.databases[].tables[].batches[]'
            if ($batch.ordinal -isnot [long] -or $batch.ordinal -lt 0 -or -not $batchOrdinals.Add($batch.ordinal) -or
                $batch.sourceRowCount -isnot [long] -or $batch.targetRowCount -isnot [long] -or $batch.sourceRowCount -lt 0 -or
                $batch.sourceRowCount -ne $batch.targetRowCount) { throw "Table '$($table.name)' has invalid batch row evidence." }
            Assert-Sha256 $batch.sourceContentSha256 '$.databases[].tables[].batches[].sourceContentSha256'
            Assert-Sha256 $batch.targetContentSha256 '$.databases[].tables[].batches[].targetContentSha256'
            if ($batch.sourceContentSha256 -cne $batch.targetContentSha256) { throw "Table '$($table.name)' has batch content drift." }
            $sourceBatchRows += $batch.sourceRowCount
            $targetBatchRows += $batch.targetRowCount
        }
        $expectedOrdinals = if ($tablePlan.ExpectedBatchCount -eq 0) { @() } else { @(0..($tablePlan.ExpectedBatchCount - 1)) }
        if ($sourceBatchRows -ne $table.sourceRowCount -or $targetBatchRows -ne $table.targetRowCount -or
            (Compare-Object @($batchOrdinals | Sort-Object) $expectedOrdinals)) {
            throw "Table '$($table.name)' has a missing, duplicated, or non-contiguous batch."
        }
    }
    if ($sourceTableRows -ne $database.sourceRowCount -or $targetTableRows -ne $database.targetRowCount -or
        (Compare-Object @($tableNames | Sort-Object) @($plan.Tables.Keys | Sort-Object) -CaseSensitive)) {
        throw "Database '$($database.name)' table totals or signed inventory do not reconcile."
    }

    if ($database.foreignKeys -isnot [object[]] -or $database.foreignKeys.Count -ne $plan.ForeignKeys.Count) { throw "Database '$($database.name)' has incomplete signed foreign-key evidence." }
    $foreignKeyNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($foreignKey in $database.foreignKeys) {
        Assert-ExactKeys $foreignKey @('name', 'sourceRelationshipCount', 'targetRelationshipCount', 'orphanCount') '$.databases[].foreignKeys[]'
        if (-not $foreignKeyNames.Add([string]$foreignKey.name) -or $plan.ForeignKeys -cnotcontains $foreignKey.name) { throw "Database '$($database.name)' has an unplanned foreign key." }
        foreach ($field in @('sourceRelationshipCount', 'targetRelationshipCount', 'orphanCount')) {
            if ($foreignKey[$field] -isnot [long] -or $foreignKey[$field] -lt 0) { throw "Database '$($database.name)' has invalid foreign-key evidence." }
        }
        if ($foreignKey.sourceRelationshipCount -ne $foreignKey.targetRelationshipCount -or $foreignKey.orphanCount -ne 0) { throw "Database '$($database.name)' does not have exact foreign-key parity." }
    }

    if ($database.sequences -isnot [object[]] -or $database.sequences.Count -ne $plan.Sequences.Count) { throw "Database '$($database.name)' has incomplete signed sequence evidence." }
    $sequenceNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($sequence in $database.sequences) {
        Assert-ExactKeys $sequence @('name', 'sourceNextValue', 'targetNextValue') '$.databases[].sequences[]'
        if (-not $sequenceNames.Add([string]$sequence.name) -or $plan.Sequences -cnotcontains $sequence.name -or
            $sequence.sourceNextValue -isnot [long] -or $sequence.targetNextValue -isnot [long] -or
            $sequence.sourceNextValue -ne $sequence.targetNextValue) { throw "Database '$($database.name)' has incomplete or mismatched sequence evidence." }
    }
}
if ((Compare-Object @($databaseNames | Sort-Object) $approvedMigrated -CaseSensitive)) { throw 'The migrated database receipt inventory is incomplete.' }

if ([string]::IsNullOrWhiteSpace($ConsumptionLedgerPath)) { throw 'A local one-time consumption ledger is required.' }
$null = New-Item -ItemType Directory -Path $ConsumptionLedgerPath -Force
$runMarker = Join-Path $ConsumptionLedgerPath "run-$normalizedRunId"
$evidenceMarker = Join-Path $ConsumptionLedgerPath "evidence-$normalizedEvidenceId"
$leaseMarker = Join-Path $ConsumptionLedgerPath "lease-$normalizedLeaseId"
$createdMarkers = [Collections.Generic.List[string]]::new()
try {
    foreach ($marker in @($runMarker, $evidenceMarker, $leaseMarker)) {
        $null = New-Item -ItemType Directory -Path $marker -ErrorAction Stop
        $createdMarkers.Add($marker)
    }
}
catch {
    for ($index = $createdMarkers.Count - 1; $index -ge 0; $index--) {
        Remove-Item -LiteralPath $createdMarkers[$index] -Force -ErrorAction SilentlyContinue
    }
    throw 'Migration evidence run, evidence, or lease identity was already consumed; replay is rejected.'
}

Write-Host "PASS: signed one-time SQL Server-to-PostgreSQL shadow evidence validated for 24 databases as of $($requiredAt.ToString('O'))."
