[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$CandidateEvidencePath,
    [Parameter(Mandatory = $true)] [string]$FinalEvidencePath,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{40}$')] [string]$ExpectedAppHostCommit,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{40}$')] [string]$ExpectedSourceCommitSha,
    [Parameter(Mandatory = $true)] [string]$ExpectedSnapshotId,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{64}$')] [string]$ExpectedSemanticManifestDigestSha256,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{64}$')] [string]$ExpectedManifestFileSha256,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{64}$')] [string]$ExpectedMigrationEvidencePayloadSha256,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{64}$')] [string]$ExpectedApprovedBaselineSha256,
    [Parameter(Mandatory = $true)] [ValidatePattern('^[0-9a-f]{64}$')] [string]$ExpectedRepositoryBaselineSha256,
    [ValidateRange(1, 120)] [int]$MaximumAgeMinutes = 30
)

$ErrorActionPreference = 'Stop'
$candidate = [IO.Path]::GetFullPath($CandidateEvidencePath)
$final = [IO.Path]::GetFullPath($FinalEvidencePath)
if ([IO.Path]::GetDirectoryName($candidate) -cne [IO.Path]::GetDirectoryName($final)) {
    throw 'Candidate and final evidence must be in the same protected directory.'
}
if (Test-Path -LiteralPath $final) { throw 'Final terminal evidence already exists.' }

$handle = $null
try {
    # Share read access with the independent validator and delete access only so this
    # process can atomically rename the exact retained bytes. Write sharing is denied.
    $handle = [IO.File]::Open($candidate, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        [IO.FileShare]::Read -bor [IO.FileShare]::Delete)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $before = [Convert]::ToHexString($sha.ComputeHash($handle)) } finally { $sha.Dispose() }
    $handle.Position = 0

    & (Join-Path $PSScriptRoot 'test-local-snapshot-verification-evidence.ps1') -EvidencePath $candidate `
        -ExpectedAppHostCommit $ExpectedAppHostCommit -ExpectedSourceCommitSha $ExpectedSourceCommitSha `
        -ExpectedSnapshotId $ExpectedSnapshotId -ExpectedSemanticManifestDigestSha256 $ExpectedSemanticManifestDigestSha256 `
        -ExpectedManifestFileSha256 $ExpectedManifestFileSha256 `
        -ExpectedMigrationEvidencePayloadSha256 $ExpectedMigrationEvidencePayloadSha256 `
        -ExpectedApprovedBaselineSha256 $ExpectedApprovedBaselineSha256 `
        -ExpectedRepositoryBaselineSha256 $ExpectedRepositoryBaselineSha256 -MaximumAgeMinutes $MaximumAgeMinutes

    $sha = [Security.Cryptography.SHA256]::Create()
    try { $after = [Convert]::ToHexString($sha.ComputeHash($handle)) } finally { $sha.Dispose() }
    if ($before -cne $after) { throw 'Candidate terminal evidence changed during validation.' }
    $handle.Position = 0
    [IO.File]::Move($candidate, $final, $false)
}
finally {
    if ($handle) { $handle.Dispose() }
    if (Test-Path -LiteralPath $candidate) { [IO.File]::Delete($candidate) }
}

Write-Output 'PASS: terminal evidence validated and published create-only.'
