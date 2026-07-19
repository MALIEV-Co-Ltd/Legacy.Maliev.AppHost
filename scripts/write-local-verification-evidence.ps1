[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputPath,

    [Parameter(Mandatory)]
    [ValidateSet('running', 'passed', 'failed')]
    [string]$Result,

    [Parameter(Mandatory)]
    [string]$CurrentStage,

    [string[]]$CompletedStages = @(),

    [ValidateSet('', 'preflight', 'build', 'orchestration', 'verification', 'cleanup', 'unexpected')]
    [string]$FailureCategory = '',

    [Parameter(Mandatory)]
    [DateTimeOffset]$StartedAtUtc,

    [DateTimeOffset]$FinishedAtUtc = [DateTimeOffset]::MinValue,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string]$AppHostCommit,

    [switch]$CleanupCompleted
)

$ErrorActionPreference = 'Stop'

if ($Result -eq 'failed' -and [string]::IsNullOrWhiteSpace($FailureCategory)) {
    throw 'Failed evidence requires a controlled failure category.'
}

if ($Result -ne 'failed' -and -not [string]::IsNullOrWhiteSpace($FailureCategory)) {
    throw 'Only failed evidence may contain a failure category.'
}

if ($Result -eq 'running' -and $FinishedAtUtc -ne [DateTimeOffset]::MinValue) {
    throw 'Running evidence cannot contain a completion timestamp.'
}

if ($Result -ne 'running' -and $FinishedAtUtc -eq [DateTimeOffset]::MinValue) {
    throw 'Terminal evidence requires a completion timestamp.'
}

$orderedStages = @(
    $CompletedStages |
        ForEach-Object { $_ -split ',' } |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ } |
        Select-Object -Unique
)
$evidence = [ordered]@{
    schemaVersion = 1
    result = $Result
    source = [ordered]@{
        repository = 'MALIEV-Co-Ltd/Legacy.Maliev.AppHost'
        commit = $AppHostCommit
    }
    timing = [ordered]@{
        startedAtUtc = $StartedAtUtc.ToUniversalTime().ToString('O')
        finishedAtUtc = if ($FinishedAtUtc -eq [DateTimeOffset]::MinValue) {
            $null
        }
        else {
            $FinishedAtUtc.ToUniversalTime().ToString('O')
        }
    }
    verification = [ordered]@{
        currentStage = $CurrentStage
        completedStages = $orderedStages
        failureCategory = if ($FailureCategory) { $FailureCategory } else { $null }
        cleanupCompleted = $CleanupCompleted.IsPresent
    }
    constraints = [ordered]@{
        environment = 'local-disposable'
        kubernetesNamespace = 'maliev-legacy'
        cutoverPercent = 0
        productionDeploymentAllowed = $false
        productionDataWritesAllowed = $false
        newNodePoolAllowed = $false
        cloudSqlAllowed = $false
        additionalInfrastructureCostAllowed = $false
    }
}

$parent = Split-Path -Parent $OutputPath
if ($parent) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}

$temporaryPath = "$OutputPath.$PID.tmp"
try {
    $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporaryPath -Encoding utf8NoBOM
    Move-Item -LiteralPath $temporaryPath -Destination $OutputPath -Force
}
finally {
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
}
