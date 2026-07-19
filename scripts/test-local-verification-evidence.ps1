[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$EvidencePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$ExpectedCommit
)

$ErrorActionPreference = 'Stop'

function Assert-ExactKeys {
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$Value,
        [Parameter(Mandatory)][string[]]$Expected,
        [Parameter(Mandatory)][string]$Path
    )

    $actual = @($Value.Keys | Sort-Object)
    $wanted = @($Expected | Sort-Object)
    if (Compare-Object $wanted $actual) {
        throw "$Path contains missing or unknown fields."
    }
}

if (-not (Test-Path -LiteralPath $EvidencePath -PathType Leaf)) {
    throw 'Local verification evidence was not found.'
}

try {
    $rawEvidence = Get-Content -LiteralPath $EvidencePath -Raw
    $jsonDocument = [System.Text.Json.JsonDocument]::Parse($rawEvidence)
    $timingElement = $jsonDocument.RootElement.GetProperty('timing')
    $startedAtText = $timingElement.GetProperty('startedAtUtc').GetString()
    $finishedAtText = $timingElement.GetProperty('finishedAtUtc').GetString()
    $evidence = $rawEvidence | ConvertFrom-Json -AsHashtable
}
catch {
    throw 'Local verification evidence is not valid JSON.'
}

Assert-ExactKeys $evidence @('schemaVersion', 'result', 'source', 'timing', 'verification', 'constraints') '$'
Assert-ExactKeys $evidence.source @('repository', 'commit') '$.source'
Assert-ExactKeys $evidence.timing @('startedAtUtc', 'finishedAtUtc') '$.timing'
Assert-ExactKeys $evidence.verification @('currentStage', 'completedStages', 'failureCategory', 'cleanupCompleted') '$.verification'
Assert-ExactKeys $evidence.constraints @(
    'environment',
    'kubernetesNamespace',
    'cutoverPercent',
    'productionDeploymentAllowed',
    'productionDataWritesAllowed',
    'newNodePoolAllowed',
    'cloudSqlAllowed',
    'additionalInfrastructureCostAllowed'
) '$.constraints'

if ($evidence.schemaVersion -isnot [long] -or $evidence.schemaVersion -ne 1) {
    throw 'Local verification evidence has an unsupported schema.'
}

if ($evidence.result -ne 'passed') {
    throw 'Local verification evidence is not a completed passing result.'
}

if ($ExpectedCommit -eq ('0' * 40) -or
    $evidence.source.repository -ne 'MALIEV-Co-Ltd/Legacy.Maliev.AppHost' -or
    $evidence.source.commit -cne $ExpectedCommit) {
    throw 'Local verification evidence does not match the tested AppHost commit.'
}

$startedAt = [DateTimeOffset]::MinValue
$finishedAt = [DateTimeOffset]::MinValue
$culture = [System.Globalization.CultureInfo]::InvariantCulture
$style = [System.Globalization.DateTimeStyles]::RoundtripKind
$utcTimestampPattern = '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}(?:Z|\+00:00)$'
if ($startedAtText -notmatch $utcTimestampPattern -or
    $finishedAtText -notmatch $utcTimestampPattern -or
    -not [DateTimeOffset]::TryParse($startedAtText, $culture, $style, [ref]$startedAt) -or
    -not [DateTimeOffset]::TryParse($finishedAtText, $culture, $style, [ref]$finishedAt) -or
    $startedAt.Offset -ne [TimeSpan]::Zero -or
    $finishedAt.Offset -ne [TimeSpan]::Zero -or
    $finishedAt -lt $startedAt) {
    throw 'Local verification evidence has invalid or reversed UTC timestamps.'
}

$requiredStages = @('preflight', 'build', 'orchestration', 'verification')
if ($evidence.verification.currentStage -ne 'complete' -or
    $evidence.verification.cleanupCompleted -isnot [bool] -or
    -not $evidence.verification.cleanupCompleted -or
    $null -ne $evidence.verification.failureCategory -or
    $evidence.verification.completedStages -isnot [object[]] -or
    (Compare-Object $requiredStages @($evidence.verification.completedStages) -SyncWindow 0)) {
    throw 'Local verification evidence is incomplete.'
}

$constraints = $evidence.constraints
if ($constraints.cutoverPercent -isnot [long] -or
    $constraints.productionDeploymentAllowed -isnot [bool] -or
    $constraints.productionDataWritesAllowed -isnot [bool] -or
    $constraints.newNodePoolAllowed -isnot [bool] -or
    $constraints.cloudSqlAllowed -isnot [bool] -or
    $constraints.additionalInfrastructureCostAllowed -isnot [bool] -or
    $constraints.environment -ne 'local-disposable' -or
    $constraints.kubernetesNamespace -ne 'maliev-legacy' -or
    $constraints.cutoverPercent -ne 0 -or
    $constraints.productionDeploymentAllowed -ne $false -or
    $constraints.productionDataWritesAllowed -ne $false -or
    $constraints.newNodePoolAllowed -ne $false -or
    $constraints.cloudSqlAllowed -ne $false -or
    $constraints.additionalInfrastructureCostAllowed -ne $false) {
    throw 'Local verification evidence violates the no-production/no-cost boundary.'
}

Write-Host "PASS: local verification evidence is complete for $ExpectedCommit."
