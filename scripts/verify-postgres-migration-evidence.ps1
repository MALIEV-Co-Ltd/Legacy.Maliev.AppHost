[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$EvidencePath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string[]]$ExpectedDatabase,

    [Parameter(Mandatory = $true)]
    [DateTimeOffset]$RequiredAsOfUtc
)

$ErrorActionPreference = 'Stop'

function Assert-ExactKeys {
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$Value,

        [Parameter(Mandatory = $true)]
        [string[]]$Expected,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $actual = @($Value.Keys | ForEach-Object { [string]$_ } | Sort-Object)
    $wanted = @($Expected | Sort-Object)
    if ($actual.Count -ne $wanted.Count -or (Compare-Object -ReferenceObject $wanted -DifferenceObject $actual)) {
        throw "$Path contains missing or unknown fields."
    }
}

function Assert-NoSensitiveKeys {
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$Value,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ($Value -is [System.Collections.IDictionary]) {
        foreach ($key in $Value.Keys) {
            $keyText = [string]$key
            if ($keyText -match '(?i)(password|token|secret|private.?key|client.?secret|connection.?string|cookie|credential)') {
                throw "$Path contains a sensitive field '$keyText'."
            }

            Assert-NoSensitiveKeys $Value[$key] "$Path.$keyText"
        }

        return
    }

    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        $index = 0
        foreach ($item in $Value) {
            Assert-NoSensitiveKeys $item "$Path[$index]"
            $index++
        }
    }
}

function Get-RequiredUtc {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value)) {
        throw "$Path must be an ISO-8601 UTC timestamp."
    }

    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
            $Value,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$parsed) -or $parsed.Offset -ne [TimeSpan]::Zero) {
        throw "$Path must be an ISO-8601 UTC timestamp with a zero offset."
    }

    if ($parsed -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
        throw "$Path cannot be in the future."
    }

    return $parsed.ToUniversalTime()
}

function Assert-Sha256 {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ($Value -isnot [string] -or $Value -notmatch '^[0-9a-fA-F]{64}$') {
        throw "$Path must be a 64-character SHA-256 hex value."
    }
}

function Assert-SafeIdentifier {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ($Value -isnot [string] -or $Value -notmatch '^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$') {
        throw "$Path must be a bounded identifier without credentials or query parameters."
    }
}

function Assert-SafeBackupUri {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if ($Value -isnot [string] -or $Value -notmatch '^gs://[A-Za-z0-9._-]+(?:/[A-Za-z0-9._~!$&''()*+,;=:@%/-]*)*$') {
        throw "$Path must be a credential-free gs:// URI without query or fragment parameters."
    }
}

if (-not (Test-Path -LiteralPath $EvidencePath -PathType Leaf)) {
    throw "Migration evidence does not exist: $EvidencePath"
}

try {
    $raw = Get-Content -LiteralPath $EvidencePath -Raw
    # Keep timestamps as strings so the validator controls the exact UTC/offset contract.
    $evidence = $raw | ConvertFrom-Json -AsHashtable -DateKind String
}
catch {
    throw "Migration evidence is not valid JSON: $EvidencePath"
}

Assert-ExactKeys $evidence @('schemaVersion', 'source', 'target', 'databases', 'parity', 'constraints') '$'
Assert-NoSensitiveKeys $evidence '$'

if ($evidence.schemaVersion -isnot [long] -or $evidence.schemaVersion -ne 1) {
    throw 'Migration evidence has an unsupported schemaVersion.'
}

Assert-ExactKeys $evidence.source @('system', 'snapshotId', 'capturedAtUtc', 'backupUri') '$.source'
Assert-ExactKeys $evidence.target @('system', 'cluster', 'namespace', 'capturedAtUtc', 'restoreId') '$.target'
Assert-ExactKeys $evidence.constraints @(
    'productionDataWritesAllowed',
    'cutoverPercent',
    'newNodePoolAllowed',
    'cloudSqlAllowed',
    'additionalInfrastructureCostAllowed'
) '$.constraints'

if ($evidence.source.system -ne 'relational-source' -or
    $evidence.target.system -ne 'postgresql' -or
    $evidence.target.cluster -ne 'legacy-postgres-main' -or
    $evidence.target.namespace -ne 'maliev-legacy') {
    throw 'Migration evidence does not identify the approved relational-source to legacy-postgres-main target boundary.'
}

foreach ($field in @('snapshotId', 'backupUri')) {
    if ($evidence.source[$field] -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$evidence.source[$field])) {
        throw "$.source.$field must be a non-empty string."
    }
}
Assert-SafeIdentifier $evidence.source.snapshotId '$.source.snapshotId'
Assert-SafeBackupUri $evidence.source.backupUri '$.source.backupUri'
if ($evidence.target.restoreId -isnot [string] -or [string]::IsNullOrWhiteSpace([string]$evidence.target.restoreId)) {
    throw '$.target.restoreId must be a non-empty string.'
}
Assert-SafeIdentifier $evidence.target.restoreId '$.target.restoreId'

$sourceCapturedAt = Get-RequiredUtc $evidence.source.capturedAtUtc '$.source.capturedAtUtc'
$targetCapturedAt = Get-RequiredUtc $evidence.target.capturedAtUtc '$.target.capturedAtUtc'
if ($RequiredAsOfUtc.Offset -ne [TimeSpan]::Zero) {
    throw 'RequiredAsOfUtc must use a zero UTC offset.'
}
$requiredAt = $RequiredAsOfUtc.ToUniversalTime()
if ($sourceCapturedAt -lt $requiredAt) {
    throw "Source snapshot is older than the required as-of timestamp '$($requiredAt.ToString('O'))'."
}
if ($targetCapturedAt -lt $sourceCapturedAt) {
    throw 'Target restore evidence predates the source snapshot.'
}

if ($evidence.parity -ne 'exact') {
    throw "Migration parity is '$($evidence.parity)' rather than exact."
}

if ($evidence.constraints.productionDataWritesAllowed -isnot [bool] -or
    $evidence.constraints.productionDataWritesAllowed -ne $false -or
    $evidence.constraints.cutoverPercent -isnot [long] -or
    $evidence.constraints.cutoverPercent -ne 0 -or
    $evidence.constraints.newNodePoolAllowed -isnot [bool] -or
    $evidence.constraints.newNodePoolAllowed -ne $false -or
    $evidence.constraints.cloudSqlAllowed -isnot [bool] -or
    $evidence.constraints.cloudSqlAllowed -ne $false -or
    $evidence.constraints.additionalInfrastructureCostAllowed -isnot [bool] -or
    $evidence.constraints.additionalInfrastructureCostAllowed -ne $false) {
    throw 'Migration evidence violates the no-production-write/no-additional-infrastructure boundary.'
}

if ($evidence.databases -isnot [object[]] -or $evidence.databases.Count -eq 0) {
    throw '$.databases must contain at least one database entry.'
}

$expected = @(
    $ExpectedDatabase |
        ForEach-Object { $_ -split ',' } |
        ForEach-Object { $_.Trim() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Sort-Object -Unique)
if ($expected.Count -ne @($ExpectedDatabase | ForEach-Object { $_ -split ',' } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -or
    $expected.Count -eq 0) {
    throw 'ExpectedDatabase must contain unique, non-empty database names.'
}

$observedNames = [System.Collections.Generic.List[string]]::new()
foreach ($database in $evidence.databases) {
    Assert-ExactKeys $database @(
        'name',
        'sourceRowCount',
        'targetRowCount',
        'sourceSchemaSha256',
        'targetSchemaSha256',
        'sourceDataSha256',
        'targetDataSha256',
        'parity'
    ) "$.databases[$($observedNames.Count)]"

    if ($database.name -isnot [string] -or [string]::IsNullOrWhiteSpace($database.name)) {
        throw "$.databases[$($observedNames.Count)].name must be non-empty."
    }
    if ($observedNames.Contains([string]$database.name)) {
        throw "Duplicate database entry '$($database.name)'."
    }
    $observedNames.Add([string]$database.name)

    foreach ($countField in @('sourceRowCount', 'targetRowCount')) {
        if ($database[$countField] -isnot [long] -or $database[$countField] -lt 0) {
            throw "$.databases[$($observedNames.Count - 1)].$countField must be a non-negative integer."
        }
    }
    if ($database.sourceRowCount -ne $database.targetRowCount) {
        throw "Database '$($database.name)' has a source/target row-count mismatch."
    }
    foreach ($hashField in @('sourceSchemaSha256', 'targetSchemaSha256', 'sourceDataSha256', 'targetDataSha256')) {
        Assert-Sha256 $database[$hashField] "$.databases[$($observedNames.Count - 1)].$hashField"
    }
    if ($database.sourceSchemaSha256 -ine $database.targetSchemaSha256 -or
        $database.sourceDataSha256 -ine $database.targetDataSha256 -or
        $database.parity -ne 'exact') {
        throw "Database '$($database.name)' does not have exact schema/data parity."
    }
}

$observed = @($observedNames | Sort-Object)
if ($observed.Count -ne $expected.Count -or (Compare-Object -ReferenceObject $expected -DifferenceObject $observed)) {
    throw 'The migration evidence database inventory does not exactly match ExpectedDatabase.'
}

Write-Host "PASS: exact relational-source to PostgreSQL migration evidence validated for $($observed.Count) databases as of $($requiredAt.ToString('O'))."
