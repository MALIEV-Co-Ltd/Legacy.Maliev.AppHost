[CmdletBinding()]
param([Parameter(Mandatory)] [string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
if ([IO.Directory]::Exists($EvidenceRoot)) { throw 'Use a new evidence directory; controls never replace evidence.' }
[void][IO.Directory]::CreateDirectory($EvidenceRoot)
$gate = Join-Path $PSScriptRoot '../coverage/verify-pg18-acceptance.ps1'
$testName = 'ProducerCustomArchive_RestoresSchemaAndRowsThroughProductionStreamingConsumer'
$baseline = [xml]('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testName="' + $testName + '" outcome="Passed" /></Results><ResultSummary><Counters failed="0" error="0" timeout="0" aborted="0" notExecuted="0" /></ResultSummary></TestRun>')
$controls = @(
    @{ Name = 'passed-result-shape'; Reject = $false; Change = { param($xml) } },
    @{ Name = 'skipped-result'; Reject = $true; Change = { param($xml) $xml.TestRun.Results.UnitTestResult.SetAttribute('outcome', 'NotExecuted') } },
    @{ Name = 'failed-result'; Reject = $true; Change = { param($xml) $xml.TestRun.Results.UnitTestResult.SetAttribute('outcome', 'Failed') } },
    @{ Name = 'missing-result'; Reject = $true; Change = { param($xml) [void]$xml.TestRun.Results.RemoveAll() } },
    @{ Name = 'duplicate-result'; Reject = $true; Change = { param($xml) [void]$xml.TestRun.Results.AppendChild($xml.TestRun.Results.UnitTestResult.CloneNode($true)) } },
    @{ Name = 'missing-counters'; Reject = $true; Change = { param($xml) [void]$xml.TestRun.ResultSummary.RemoveAll() } },
    @{ Name = 'missing-required-counter'; Reject = $true; Change = { param($xml) $xml.TestRun.ResultSummary.Counters.RemoveAttribute('notExecuted') } },
    @{ Name = 'nonzero-failure-counter'; Reject = $true; Change = { param($xml) $xml.TestRun.ResultSummary.Counters.SetAttribute('failed', '1') } },
    @{ Name = 'nonzero-skipped-counter'; Reject = $true; Change = { param($xml) $xml.TestRun.ResultSummary.Counters.SetAttribute('notExecuted', '1') } },
    @{ Name = 'invalid-counter'; Reject = $true; Change = { param($xml) $xml.TestRun.ResultSummary.Counters.SetAttribute('timeout', 'unknown') } }
)
$results = foreach ($control in $controls) {
    $xml = [xml]$baseline.OuterXml
    & $control.Change $xml
    $path = Join-Path $EvidenceRoot ($control.Name + '.trx')
    $xml.Save($path)
    $rejected = $false
    $message = $null
    try { [void](& $gate -TrxPath $path) }
    catch { $rejected = $true; $message = $_.Exception.Message }
    if ($rejected -ne $control.Reject) { throw "Unexpected acceptance outcome for $($control.Name)." }
    [pscustomobject]@{ Name = $control.Name; Status = 'passed'; Rejected = $rejected; Rejection = $message; EvidenceKind = 'synthetic-parser-control-only' }
}
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'results.json') -Encoding utf8NoBOM
$results
# Synthetic parser controls do not run PostgreSQL or establish runtime acceptance.
