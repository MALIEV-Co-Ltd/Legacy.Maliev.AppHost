[CmdletBinding()]
param([Parameter(Mandatory)] [string]$TrxPath)
$ErrorActionPreference = 'Stop'
$trx = [xml][IO.File]::ReadAllText([IO.Path]::GetFullPath($TrxPath))
$manager = [Xml.XmlNamespaceManager]::new($trx.NameTable)
$manager.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
$requiredTest = 'ProducerCustomArchive_RestoresSchemaAndRowsThroughProductionStreamingConsumer'
$results = @($trx.SelectNodes('//t:UnitTestResult', $manager) | Where-Object {
    $_.GetAttribute('testName') -eq $requiredTest -or $_.GetAttribute('testName').EndsWith('.' + $requiredTest, [StringComparison]::Ordinal)
})
if ($results.Count -ne 1 -or $results[0].GetAttribute('outcome') -ne 'Passed') {
    throw 'Actual PostgreSQL 18 producer-to-streaming-consumer test must appear exactly once and pass; a skipped or missing test cannot establish acceptance.'
}
$counters = $trx.SelectSingleNode('//t:ResultSummary/t:Counters', $manager)
foreach ($field in @('failed', 'error', 'timeout', 'aborted', 'notExecuted')) {
    if ($null -eq $counters -or -not $counters.HasAttribute($field) -or [long]$counters.GetAttribute($field) -ne 0) { throw 'PG18 acceptance run contains failed, incomplete, or unavailable counters.' }
}
[pscustomobject]@{ EvidenceKind = 'test-result-check-only'; RequiredTest = $requiredTest; Outcome = 'Passed'; TrxSha256 = (Get-FileHash -LiteralPath $TrxPath -Algorithm SHA256).Hash }
# This verifies a test result only. Exact source/build identity and prerequisite/native
# admission remain separate gates; the caller must retain the real runtime evidence.
