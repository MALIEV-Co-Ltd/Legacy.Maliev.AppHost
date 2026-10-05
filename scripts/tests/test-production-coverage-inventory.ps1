[CmdletBinding()]
param([Parameter(Mandatory)] [string]$FixtureDll, [Parameter(Mandatory)] [string]$EvidenceRoot)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../coverage/production-coverage-inventory.ps1')
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ([IO.Directory]::Exists($EvidenceRoot)) { throw 'Use a new evidence directory; controls never replace evidence.' }
[void][IO.Directory]::CreateDirectory($EvidenceRoot)
$results = @()
$plan = Get-LegacyProductionAssemblyPlan -RepositoryRoot $repositoryRoot
if ($plan.Assemblies.Count -ne 4 -or $plan.LinkedTestSources.Count -ne 0) { throw 'Production solution ownership or linked-source gap remains unresolved.' }
$results += [pscustomobject]@{ Name = 'solution-owns-four-production-assemblies-with-no-linked-test-compilation'; Status = 'passed' }
$inventory = Get-LegacyAssemblyExecutableLines -Dll $FixtureDll -Pdb ([IO.Path]::ChangeExtension($FixtureDll, '.pdb'))
$generated = @($inventory.Documents | Where-Object { $_.Generated -and $_.Lines.Count })
if (-not $generated.Count) { throw 'Actual compiled fixture must contain executable generated-filename source.' }
$document = [xml]'<coverage><sources/><packages/></coverage>'
$package = $document.CreateElement('package')
$package.SetAttribute('name', $inventory.Name)
$classes = $document.CreateElement('classes')
[void]$package.AppendChild($classes)
[void]$document.SelectSingleNode('/coverage/packages').AppendChild($package)
foreach ($source in $inventory.Documents | Where-Object { $_.Lines.Count }) {
    $class = $document.CreateElement('class')
    $class.SetAttribute('filename', $source.Path)
    $class.SetAttribute('name', 'MetadataControl')
    $lines = $document.CreateElement('lines')
    foreach ($number in $source.Lines) {
        $line = $document.CreateElement('line')
        $line.SetAttribute('number', [string]$number)
        $line.SetAttribute('hits', '0')
        [void]$lines.AppendChild($line)
    }
    [void]$class.AppendChild($lines)
    [void]$classes.AppendChild($class)
}
$baselinePath = Join-Path $EvidenceRoot 'all-zero-control.xml'
$document.Save($baselinePath)
$baseline = Compare-LegacyCoverageInventory -Inventory @($inventory) -CoberturaPath $baselinePath -RepositoryRoot $repositoryRoot
if (-not $baseline.Complete -or $baseline.CoveragePercent -ne 0 -or $baseline.MeetsThreshold -or $baseline.GeneratedReportedLines -le 0) { throw 'All-zero completeness control falsely implied coverage acceptance or omitted generated lines.' }
$results += [pscustomobject]@{ Name = 'complete-real-pdb-inventory-with-zero-hits-stays-zero-and-keeps-generated-lines'; Status = 'passed'; RawLines = $baseline.RawLines; GeneratedReportedLines = $baseline.GeneratedReportedLines; CoveragePercent = 0; MeetsThreshold = $false }

# The small slot never builds the executable graph. Verify that a subset report cannot
# bypass missing real production DLLs in the acceptance wrapper. Once that graph exists,
# the same subset must instead reject its omitted production package.
$acceptanceRejection = $null
try { [void](Test-LegacyProductionCoverage -CoberturaPath $baselinePath -RepositoryRoot $repositoryRoot) }
catch { $acceptanceRejection = $_.Exception.Message }
if ($null -eq $acceptanceRejection) { throw 'Fixture-only report bypassed the complete production assembly gate.' }
$missingArtifacts = @($plan.Assemblies | Where-Object { -not [IO.File]::Exists($_.Dll) -or -not [IO.File]::Exists($_.Pdb) })
if ($missingArtifacts.Count) {
    $intendedPath = if (-not [IO.File]::Exists($missingArtifacts[0].Dll)) { $missingArtifacts[0].Dll } else { $missingArtifacts[0].Pdb }
    if ($acceptanceRejection -notmatch [Regex]::Escape($intendedPath)) { throw ('Subset rejection occurred before the intended missing production artifact boundary: ' + $acceptanceRejection) }
} elseif ($acceptanceRejection -notmatch 'Missing or duplicate production assembly') {
    throw ('Subset rejection occurred before the intended missing production package boundary: ' + $acceptanceRejection)
}
$results += [pscustomobject]@{ Name = 'fixture-subset-cannot-bypass-real-production-assembly-gate'; Status = 'passed'; Rejection = $acceptanceRejection }

function Assert-RejectedControl {
    param([string]$Name, [scriptblock]$Mutate, [string]$ExpectedReason)
    $copy = [xml]$document.OuterXml
    & $Mutate $copy
    $path = Join-Path $EvidenceRoot ($Name + '.xml')
    $copy.Save($path)
    $caught = $null
    try { [void](Compare-LegacyCoverageInventory -Inventory @($inventory) -CoberturaPath $path -RepositoryRoot $repositoryRoot) }
    catch { $caught = $_.Exception.Message }
    if ($null -eq $caught -or $caught -notmatch $ExpectedReason) { throw ($Name + ': missing rejection at intended coverage boundary: ' + $caught) }
    $script:results += [pscustomobject]@{ Name = $Name; Status = 'passed'; Rejection = $caught }
}

Assert-RejectedControl -Name 'omitted-assembly' -ExpectedReason 'Missing or duplicate production assembly' -Mutate {
    param($xml)
    [void]$xml.SelectSingleNode('/coverage/packages').RemoveAll()
}
Assert-RejectedControl -Name 'omitted-production-file' -ExpectedReason 'Missing or ambiguous production source' -Mutate {
    param($xml)
    $node = $xml.SelectSingleNode('/coverage/packages/package/classes/class')
    [void]$node.ParentNode.RemoveChild($node)
}
Assert-RejectedControl -Name 'omitted-executable-line' -ExpectedReason 'Omitted executable line' -Mutate {
    param($xml)
    $node = $xml.SelectSingleNode('/coverage/packages/package/classes/class/lines/line')
    [void]$node.ParentNode.RemoveChild($node)
}
Assert-RejectedControl -Name 'omitted-generated-source' -ExpectedReason 'Missing or ambiguous production source' -Mutate {
    param($xml)
    $node = @($xml.SelectNodes('/coverage/packages/package/classes/class') | Where-Object { $_.GetAttribute('filename') -eq $generated[0].Path })[0]
    [void]$node.ParentNode.RemoveChild($node)
}
Assert-RejectedControl -Name 'fabricated-source-padding' -ExpectedReason 'no unique compiled production owner' -Mutate {
    param($xml)
    $node = $xml.SelectSingleNode('/coverage/packages/package/classes/class').CloneNode($true)
    $node.SetAttribute('filename', 'fabricated-coverage-padding.cs')
    foreach ($line in $node.SelectNodes('./lines/line')) { $line.SetAttribute('hits', '100') }
    [void]$xml.SelectSingleNode('/coverage/packages/package/classes').AppendChild($node)
}
Assert-RejectedControl -Name 'fabricated-executable-line-padding' -ExpectedReason 'outside the compiled executable inventory' -Mutate {
    param($xml)
    $node = $xml.SelectSingleNode('/coverage/packages/package/classes/class/lines')
    $line = $xml.CreateElement('line')
    $line.SetAttribute('number', '2147483646')
    $line.SetAttribute('hits', '100')
    [void]$node.AppendChild($line)
}
Assert-RejectedControl -Name 'duplicate-assembly' -ExpectedReason 'Missing or duplicate production assembly' -Mutate {
    param($xml)
    [void]$xml.SelectSingleNode('/coverage/packages').AppendChild($xml.SelectSingleNode('/coverage/packages/package').CloneNode($true))
}
[pscustomobject]@{ Purpose = 'metadata-control-tests-only-not-production-coverage'; FixtureDllSha256 = $inventory.DllSha256; FixturePdbSha256 = $inventory.PdbSha256; Passed = $results.Count; Failed = 0; Cases = $results; ProductionCoverageAccepted = $false } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'results.json') -Encoding utf8NoBOM
Write-Output ('Coverage inventory controls PASS=' + $results.Count + '; production coverage acceptance remains pending.')
