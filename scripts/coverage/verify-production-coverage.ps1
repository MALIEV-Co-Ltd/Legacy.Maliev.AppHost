[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$CoberturaPath,
    [Parameter(Mandatory)] [string]$OutputPath,
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'production-coverage-inventory.ps1')
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$result = Test-LegacyProductionCoverage -CoberturaPath $CoberturaPath -RepositoryRoot $repositoryRoot -Configuration $Configuration -MinimumPercent 80
$bytes = [Text.UTF8Encoding]::new($false).GetBytes(($result | ConvertTo-Json -Depth 25))
$stream = [IO.FileStream]::new([IO.Path]::GetFullPath($OutputPath), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
if (-not $result.Coverage.MeetsThreshold) {
    $failed = @($result.Coverage.Assemblies | Where-Object { -not $_.MeetsThreshold } | ForEach-Object { $_.Name + '=' + $_.CoveragePercent + '%' })
    throw ('Raw production line coverage is below 80% per assembly: ' + ($failed -join ', '))
}
Write-Output ('Raw production coverage accepted: ' + $result.Coverage.CoveragePercent + '%; generated reported lines=' + $result.Coverage.GeneratedReportedLines)
