[CmdletBinding()]
param([ValidateSet('All', 'Historical', 'RetirementOverlay')][string]$TestGroup = 'All')

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../verify-generated-documentation-isolation.ps1')

if ($TestGroup -ne 'RetirementOverlay') {
$cases = @(
    @{ Name = 'Reject source-root documentation'; Xml = '<Project><PropertyGroup><DocumentationFile>App.xml</DocumentationFile></PropertyGroup></Project>'; Expected = 1 },
    @{ Name = 'Allow SDK documentation'; Xml = '<Project><PropertyGroup><GenerateDocumentationFile>true</GenerateDocumentationFile></PropertyGroup></Project>'; Expected = 0 },
    @{ Name = 'Allow intermediate documentation'; Xml = '<Project><PropertyGroup><DocumentationFile>$(IntermediateOutputPath)App.xml</DocumentationFile></PropertyGroup></Project>'; Expected = 0 },
    @{ Name = 'Reject assembly XML copy'; Xml = '<Project><ItemGroup><None Update="App.xml"><CopyToOutputDirectory>Always</CopyToOutputDirectory></None></ItemGroup></Project>'; Expected = 1 },
    @{ Name = 'Allow unrelated XML copy'; Xml = '<Project><ItemGroup><Content Include="schema.xml"><CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory></Content></ItemGroup></Project>'; Expected = 0 },
    @{ Name = 'Allow isolated baseline subtree copy'; Xml = '<Project><ItemGroup><Content Include="Baselines\**\*" CopyToOutputDirectory="PreserveNewest" /></ItemGroup></Project>'; Expected = 0 },
    @{ Name = 'Reject traversal back to root XML copy'; Xml = '<Project><ItemGroup><Content Include="Baselines/../App.xml" CopyToOutputDirectory="Always" /></ItemGroup></Project>'; Expected = 1 },
    @{ Name = 'Reject traversal in output'; Xml = '<Project><PropertyGroup><DocumentationFile>obj/../App.xml</DocumentationFile></PropertyGroup></Project>'; Expected = 1 },
    @{ Name = 'Reject unknown property output'; Xml = '<Project><PropertyGroup><DocumentationFile>$(CustomPath)App.xml</DocumentationFile></PropertyGroup></Project>'; Expected = 1 }
)
$passed = 0
foreach ($case in $cases) {
    $issues = @(Test-DocumentationMetadata -Files @{ 'App/App.csproj' = $case.Xml } -TrackedPaths @('App/App.csproj'))
    if ($issues.Count -ne $case.Expected) { throw "$($case.Name): expected $($case.Expected) issue(s), got $($issues.Count)." }
    $passed++
}
$metadata = @{ 'App/App.csproj' = '<Project />' }
if (@(Test-DocumentationMetadata $metadata @('App/App.csproj', 'App/App.xml')).Count -ne 1) { throw 'Tracked assembly XML was accepted.' }
$passed++
if (@(Test-DocumentationMetadata $metadata @('App/App.csproj', 'App/schema.xml')).Count -ne 0) { throw 'Unrelated tracked XML was rejected.' }
$passed++
foreach ($name in @('Directory.Build.props', 'build/docs.targets')) {
    $files = @{ 'App/App.csproj' = '<Project />' }
    $files[$name] = '<Project><PropertyGroup><DocumentationFile>App.xml</DocumentationFile></PropertyGroup></Project>'
    if (@(Test-DocumentationMetadata $files @($files.Keys)).Count -ne 1) { throw "Unsafe imported metadata accepted: $name" }
    $passed++
}
foreach ($xml in @('<Project>', '<WrongRoot />', '<!DOCTYPE Project [<!ENTITY x SYSTEM "file:///not-read">]><Project>&x;</Project>')) {
    if (@(Test-DocumentationMetadata @{ 'App/App.csproj' = $xml } @('App/App.csproj')).Count -eq 0) { throw 'Invalid XML accepted.' }
    $passed++
}
if (@(Test-DocumentationMetadata @{} @()).Count -eq 0) { throw 'Empty repository accepted.' }
$passed++
if (@(Test-DocumentationMetadata @{ 'App/App.csproj' = '<Project><PropertyGroup><AssemblyName>Renamed</AssemblyName></PropertyGroup><ItemGroup><Content Include="Renamed.xml" CopyToPublishDirectory="Always" /></ItemGroup></Project>' } @('App/App.csproj')).Count -ne 1) { throw 'Renamed assembly copy accepted.' }
$passed++
if (@(Test-DocumentationMetadata @{ 'App/App.csproj' = '<Project><PropertyGroup><OutputPath>./</OutputPath></PropertyGroup></Project>' } @('App/App.csproj')).Count -eq 0) { throw 'Source-root build output accepted.' }
$passed++
$bom = [string][char]0xFEFF + '<Project />'
if (@(Test-DocumentationMetadata @{ 'App/App.csproj' = $bom } @('App/App.csproj')).Count -ne 0) { throw 'Valid UTF-8 BOM metadata rejected.' }
$passed++
$manifest = @{ sourceCommit = '03dc9a1271c16e6535934445e9dd6e3f30e8fffe'; repositories = @(@{ name = 'Legacy.Maliev.Web'; commit = ('a' * 40) }); projects = @(@{ sourceProject = 'App/App.csproj'; owners = @('Legacy.Maliev.Web'); disposition = 'architecture-equivalent'; rationale = 'SDK-managed docs' }) }
Test-DocumentationOwnerManifest $manifest @('App/App.csproj')
$passed++
foreach ($mutation in @('unknown-owner', 'missing-project', 'duplicate-project', 'invalid-commit')) {
    $copy = $manifest | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable
    switch ($mutation) {
        'unknown-owner' { $copy.projects[0].owners = @('Unknown.Owner') }
        'missing-project' { $copy.projects = @() }
        'duplicate-project' { $copy.projects += $copy.projects[0] }
        'invalid-commit' { $copy.repositories[0].commit = 'main' }
    }
    $failed = $false
    try { Test-DocumentationOwnerManifest $copy @('App/App.csproj') } catch { $failed = $true }
    if (-not $failed) { throw "Invalid ownership accepted: $mutation" }
    $passed++
}
foreach ($repository in @((Join-Path $PSScriptRoot 'missing-repository'), (Join-Path $PSScriptRoot '../..'))) {
    $failed = $false
    try { Read-CommittedBuildMetadata $repository ('0' * 40) } catch { $failed = $true }
    if (-not $failed) { throw 'Missing repository/commit accepted.' }
    $passed++
}
Write-Output "Passed $passed documentation metadata and boundary cases."
}

if ($TestGroup -ne 'Historical') {
    $root = Join-Path $PSScriptRoot '../..'
    $historical = Get-Content -LiteralPath (Join-Path $root 'contracts/source-03dc9a1-documentation-owners.json') -Raw | ConvertFrom-Json -AsHashtable
    $overlay = Get-Content -LiteralPath (Join-Path $root 'contracts/source-03dc9a1-retirement-overlay.json') -Raw | ConvertFrom-Json -AsHashtable
    Test-DocumentationOwnerManifest $historical @($historical.projects.sourceProject)
    $manifestFile = (Resolve-Path -LiteralPath (Join-Path $root 'contracts/source-03dc9a1-documentation-owners.json')).Path
    $observedBlob = [string](Invoke-ReadOnlyGit $root @('hash-object', '--path=contracts/source-03dc9a1-documentation-owners.json', '--', $manifestFile))
    $historicalBefore = $historical | ConvertTo-Json -Depth 30 -Compress
    $overlayBefore = $overlay | ConvertTo-Json -Depth 30 -Compress
    $result = Resolve-DocumentationRetirementOverlay $historical $overlay $observedBlob
    $failures = [Collections.Generic.List[string]]::new()
    $overlayPassed = 0
    if ($result.historicalRetiredProjects -ne 3 -or $result.effectiveRetiredProjects -ne 4) {
        $failures.Add("Effective retirement counts: expected historical=3/effective=4, observed historical=$($result.historicalRetiredProjects)/effective=$($result.effectiveRetiredProjects).")
    } else { $overlayPassed++ }
    $swagger = @($result.projects | Where-Object { $_.sourceProject -ceq 'Maliev.Middleware.SwaggerAuthorized/Maliev.Middleware.SwaggerAuthorized.csproj' })
    if ($swagger.Count -ne 1 -or $swagger[0].disposition -cne 'approved-retirement' -or @($swagger[0].owners).Count -ne 0) {
        $failures.Add('Swagger effective disposition: expected one approved-retirement project with no effective owners.')
    } else { $overlayPassed++ }
    $unaffected = @($historical.projects | Where-Object { $_.sourceProject -cnotin @($overlay.projects.sourceProject) })
    $effectiveUnaffected = @($result.projects | Where-Object { $_.sourceProject -cnotin @($overlay.projects.sourceProject) })
    if ($unaffected.Count -ne 84 -or ($unaffected | ConvertTo-Json -Depth 30 -Compress) -cne
        ($effectiveUnaffected | ConvertTo-Json -Depth 30 -Compress) -or @($historical.repositories).Count -ne 18 -or
        ($historical | ConvertTo-Json -Depth 30 -Compress) -cne $historicalBefore -or
        ($overlay | ConvertTo-Json -Depth 30 -Compress) -cne $overlayBefore) {
        $failures.Add('Valid projection changed an unrelated project, repository pin, or input.')
    } else { $overlayPassed++ }
    $legacy = Resolve-DocumentationRetirementOverlay $historical $null
    if ($legacy.historicalRetiredProjects -ne 3 -or $legacy.effectiveRetiredProjects -ne 3 -or
        ($legacy.projects | ConvertTo-Json -Depth 30 -Compress) -cne ($historical.projects | ConvertTo-Json -Depth 30 -Compress)) {
        $failures.Add('No-overlay resolution changed historical classifications.')
    } else { $overlayPassed++ }

    foreach ($mutation in @('unknown-root-field', 'wrong-source', 'wrong-declared-blob', 'wrong-observed-blob',
        'wrong-schema', 'missing-project', 'duplicate-project', 'unknown-project', 'country-project', 'scalar-project',
        'wrong-url', 'empty-reason', 'wrong-reason', 'wrong-previous-disposition', 'wrong-previous-owners',
        'nonempty-owners', 'null-owners', 'missing-entry-field', 'unknown-entry-field', 'project-case',
        'changed-history-disposition', 'changed-history-owners')) {
        $candidate = $overlayBefore | ConvertFrom-Json -AsHashtable
        $history = $historicalBefore | ConvertFrom-Json -AsHashtable
        $blob = $observedBlob
        switch ($mutation) {
            'unknown-root-field' { $candidate.unapproved = $true }
            'wrong-source' { $candidate.sourceCommit = '5fac706a7983a6d359b39acbd670e6800afe020e' }
            'wrong-declared-blob' { $candidate.historicalManifestBlob = '0' * 40 }
            'wrong-observed-blob' { $blob = '0' * 40 }
            'wrong-schema' { $candidate.schemaVersion = '1' }
            'missing-project' { $candidate.projects = @($candidate.projects | Select-Object -First 3) }
            'duplicate-project' { $candidate.projects[3] = $candidate.projects[2] }
            'unknown-project' { $candidate.projects[0].sourceProject = 'Unknown/Unknown.csproj' }
            'country-project' { $candidate.projects[0].sourceProject = 'Maliev.CountryService.Tests/Maliev.CountryService.Tests.csproj' }
            'scalar-project' { $candidate.projects[0].sourceProject = 'Maliev.Aspire.ServiceDefaults/Maliev.Aspire.ServiceDefaults.csproj' }
            'wrong-url' { $candidate.projects[0].evidenceUrl = $candidate.projects[1].evidenceUrl }
            'empty-reason' { $candidate.projects[0].reason = '' }
            'wrong-reason' { $candidate.projects[0].reason = 'Migrated and runtime accepted.' }
            'wrong-previous-disposition' { $candidate.projects[0].previousDisposition = 'approved-retirement' }
            'wrong-previous-owners' { $candidate.projects[0].previousOwners = @('Legacy.Maliev.Web') }
            'nonempty-owners' { $candidate.projects[0].owners = @('Legacy.Maliev.ServiceDefaults') }
            'null-owners' { $candidate.projects[0].owners = $null }
            'missing-entry-field' { $candidate.projects[0].Remove('reason') }
            'unknown-entry-field' { $candidate.projects[0].runtimeAccepted = $true }
            'project-case' { $candidate.projects[0].sourceProject = $candidate.projects[0].sourceProject.ToLowerInvariant() }
            'changed-history-disposition' { ($history.projects | Where-Object sourceProject -CEQ $candidate.projects[0].sourceProject).disposition = 'approved-retirement' }
            'changed-history-owners' { ($history.projects | Where-Object sourceProject -CEQ $candidate.projects[0].sourceProject).owners = @('Legacy.Maliev.Web') }
        }
        $candidateBefore = $candidate | ConvertTo-Json -Depth 30 -Compress
        $historyBefore = $history | ConvertTo-Json -Depth 30 -Compress
        $rejected = $false
        try { $null = Resolve-DocumentationRetirementOverlay $history $candidate $blob } catch { $rejected = $true }
        if (-not $rejected -or ($candidate | ConvertTo-Json -Depth 30 -Compress) -cne $candidateBefore -or
            ($history | ConvertTo-Json -Depth 30 -Compress) -cne $historyBefore) {
            $failures.Add("Retirement guard failed or mutated inputs: $mutation.")
        } else { $overlayPassed++ }
    }

    # Exercise the CLI's actual file binding, not a caller-supplied declaration.
    # The altered rationale still passes the historical validator.
    $temp = Join-Path ([IO.Path]::GetTempPath()) ('apphost-retirement-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($temp)
    try {
        $changedHistory = $historicalBefore | ConvertFrom-Json -AsHashtable
        $changedHistory.projects[0].rationale = 'Different historical file with otherwise valid ownership.'
        Test-DocumentationOwnerManifest $changedHistory @($historical.projects.sourceProject)
        $changedFile = Join-Path $temp 'changed-historical-manifest.json'
        [IO.File]::WriteAllText($changedFile, ($changedHistory | ConvertTo-Json -Depth 30))
        $pwsh = (Get-Process -Id $PID).Path
        $cliOutput = @(& $pwsh -NoProfile -File (Join-Path $root 'scripts/verify-generated-documentation-isolation.ps1') -SourceRepository $root -WorkspaceRoot $root -ManifestPath $changedFile -RetirementOverlayPath (Join-Path $root 'contracts/source-03dc9a1-retirement-overlay.json') 2>&1)
        $cliExitCode = $LASTEXITCODE
        # This intentional rejection is consumed by the assertion, not the suite's exit status.
        $global:LASTEXITCODE = 0
        if ($cliExitCode -eq 0 -or ($cliOutput -join "`n") -notmatch 'Historical manifest file identity mismatch') {
            $failures.Add('CLI did not reject an actually changed historical file at its identity boundary.')
        } else { $overlayPassed++ }
    } finally {
        [IO.Directory]::Delete($temp, $true)
    }
    foreach ($failure in $failures) { Write-Output "FAIL: $failure" }
    Write-Output "Retirement overlay cases: passed=$overlayPassed; failed=$($failures.Count)."
    if ($failures.Count) { throw 'Retirement overlay behavioral assertions failed.' }
}
