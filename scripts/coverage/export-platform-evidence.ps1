[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$CoberturaPath,
    [Parameter(Mandatory)] [string]$OutputDirectory,
    [ValidateSet('ubuntu', 'windows')] [string]$Platform
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'production-coverage-inventory.ps1')
if (($Platform -eq 'windows') -ne $IsWindows) { throw 'Platform evidence does not match actual runner OS.' }
$sdkVersion = & dotnet --version
if ($LASTEXITCODE -ne 0 -or $sdkVersion -ne '10.0.401') { throw 'Platform evidence requires the reviewed exact SDK.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Platform evidence output must be new.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$plan = Get-LegacyProductionAssemblyPlan -RepositoryRoot $root
$assemblies = @($plan.Assemblies)
if ($Platform -eq 'windows') { $assemblies = @($assemblies | Where-Object Name -eq 'Legacy.Maliev.AppHost.LocalDeltaRunner') }
if ($assemblies.Count -ne $(if ($Platform -eq 'windows') { 1 } else { 4 })) { throw 'Unexpected platform assembly ownership.' }
$inventory = @()
foreach ($assembly in $assemblies) {
    $actual = Get-LegacyAssemblyExecutableLines -Dll $assembly.Dll -Pdb $assembly.Pdb
    if ($actual.Name -ne $assembly.Name) { throw 'Assembly ownership mismatch.' }
    foreach ($document in $actual.Documents) {
        $relative = [IO.Path]::GetRelativePath($root, $document.Path).Replace('\', '/')
        if ($relative.StartsWith('../') -or [IO.Path]::IsPathFullyQualified($relative)) { throw 'Source escapes repository.' }
        $algorithm = switch ($document.PdbChecksum.Algorithm) {
            '8829d00f-11b8-4213-878b-770e8597ac16' { 'SHA256' }
            'ff1816ec-aa5e-4d10-87f7-6f4963833460' { 'SHA1' }
            default { throw 'Unknown source checksum algorithm.' }
        }
        if ((Get-FileHash -LiteralPath $document.Path -Algorithm $algorithm).Hash -ne $document.PdbChecksum.Value) { throw 'Compiled source checksum mismatch.' }
        $blob = $null
        if (-not $document.Generated) {
            $blob = & git -C $root rev-parse "HEAD:$relative"
            if ($LASTEXITCODE -ne 0) { throw 'Production source is not in exact Git tree.' }
            $actualBlob = & git -C $root hash-object --path $relative -- $document.Path
            if ($LASTEXITCODE -ne 0 -or $actualBlob -ne $blob) { throw 'Production source differs from Git blob.' }
        }
        $copy = Join-Path $OutputDirectory ('sources/' + $relative)
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copy))
        Copy-Item -LiteralPath $document.Path -Destination $copy
        $document | Add-Member RelativePath $relative
        $document | Add-Member SourceSha256 (Get-FileHash -LiteralPath $document.Path -Algorithm SHA256).Hash
        $document | Add-Member GitBlob $blob
    }
    $moduleDirectory = Join-Path $OutputDirectory ('modules/' + $actual.Name)
    [void][IO.Directory]::CreateDirectory($moduleDirectory)
    Copy-Item -LiteralPath $actual.Dll, $actual.Pdb -Destination $moduleDirectory
    $inventory += $actual
}
$coverage = Compare-LegacyCoverageInventory -Inventory $inventory -CoberturaPath $CoberturaPath -RepositoryRoot $root
$head = & git -C $root rev-parse HEAD
$tree = & git -C $root rev-parse 'HEAD^{tree}'
$parents = & git -C $root show -s '--format=%P' HEAD
$event = Get-Content -LiteralPath $env:GITHUB_EVENT_PATH -Raw | ConvertFrom-Json
$candidateHead = if ($null -ne $event.PSObject.Properties['pull_request']) { $event.pull_request.head.sha } else { $head }
$dependencies = @{}
foreach ($directory in Get-ChildItem -LiteralPath (Split-Path $root) -Directory -Filter 'Legacy.Maliev.*') {
    if (-not (Test-Path -LiteralPath (Join-Path $directory.FullName '.git'))) { continue }
    $dependencyHead = & git -C $directory.FullName rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Dependency identity unavailable.' }
    $dependencyTree = & git -C $directory.FullName rev-parse 'HEAD^{tree}'
    if ($LASTEXITCODE -ne 0) { throw 'Dependency tree unavailable.' }
    $dependencies[$directory.Name] = @{ Head = $dependencyHead; Tree = $dependencyTree }
}
Copy-Item -LiteralPath $CoberturaPath -Destination (Join-Path $OutputDirectory 'raw.cobertura.xml')
$evidence = [ordered]@{ Version = 1; Platform = $Platform; SdkVersion = $sdkVersion; CandidateHead = $candidateHead; Head = $head; Tree = $tree; Parents = $parents; Dependencies = $dependencies; RepositoryRoot = $root; RawSha256 = (Get-FileHash -LiteralPath $CoberturaPath -Algorithm SHA256).Hash; ArtifactInventory = $inventory; Coverage = $coverage }
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'identity.json'), ($evidence | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
if ((Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | Measure-Object Length -Sum).Sum -gt 52428800) { throw 'Platform evidence exceeds bounded 50 MiB.' }
