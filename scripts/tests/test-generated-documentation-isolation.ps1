$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../verify-generated-documentation-isolation.ps1')

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
