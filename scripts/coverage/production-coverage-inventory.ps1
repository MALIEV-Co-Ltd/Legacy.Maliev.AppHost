# Metadata-only coverage inventory. Dot-source this file; it starts no application.
Set-StrictMode -Version Latest

function Get-LegacyProductionAssemblyPlan {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string]$RepositoryRoot, [string]$Configuration = 'Release')
    $root = [IO.Path]::GetFullPath($RepositoryRoot)
    $solution = [xml][IO.File]::ReadAllText((Join-Path $root 'Legacy.Maliev.AppHost.slnx'))
    $assemblies = @()
    $linkedSources = @()
    foreach ($entry in $solution.SelectNodes('//Project[@Path]')) {
        $projectPath = [IO.Path]::GetFullPath((Join-Path $root $entry.Path))
        if (-not $projectPath.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Project escapes AppHost source root.' }
        $project = [xml][IO.File]::ReadAllText($projectPath)
        $testSdk = $project.SelectNodes("//PackageReference[@Include='Microsoft.NET.Test.Sdk']").Count -gt 0
        if ($testSdk -or $project.SelectNodes("//IsTestProject[text()='true']").Count -gt 0) {
            foreach ($compile in $project.SelectNodes('//Compile[@Include]')) {
                $include = [string]$compile.Include
                if ($include.Contains('$(')) { throw 'Linked test source needs explicit property resolution.' }
                $linkedSources += [pscustomobject]@{ TestProject = $projectPath; Source = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($projectPath)) $include)) }
            }
            continue
        }
        $frameworkNode = $project.SelectSingleNode('//TargetFramework')
        if ($null -eq $frameworkNode -or $frameworkNode.InnerText.Contains('$(')) { throw 'Production target framework needs explicit resolution.' }
        $assemblyNode = $project.SelectSingleNode('//AssemblyName')
        $name = if ($null -eq $assemblyNode) { [IO.Path]::GetFileNameWithoutExtension($projectPath) } else { $assemblyNode.InnerText }
        if ($name.Contains('$(')) { throw 'Production assembly name needs explicit resolution.' }
        $directory = [IO.Path]::GetDirectoryName($projectPath)
        $dll = Join-Path $directory ('bin/' + $Configuration + '/' + $frameworkNode.InnerText + '/' + $name + '.dll')
        $assemblies += [pscustomobject]@{ Name = $name; Project = $projectPath; Dll = $dll; Pdb = [IO.Path]::ChangeExtension($dll, '.pdb') }
    }
    if (-not $assemblies.Count) { throw 'No production assemblies were inventoried.' }
    $productionLinks = @($linkedSources | Where-Object {
        $source = $_.Source
        @($assemblies | Where-Object {
            $projectDirectory = [IO.Path]::GetDirectoryName($_.Project) + [IO.Path]::DirectorySeparatorChar
            $source.StartsWith($projectDirectory, [StringComparison]::OrdinalIgnoreCase)
        }).Count -gt 0
    })
    [pscustomobject]@{ RepositoryRoot = $root; Assemblies = $assemblies; LinkedTestSources = $productionLinks; AllExplicitTestCompileLinks = $linkedSources }
}

function Get-LegacyAssemblyExecutableLines {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string]$Dll, [Parameter(Mandatory)] [string]$Pdb)
    $dllStream = [IO.File]::OpenRead($Dll)
    $pe = [Reflection.PortableExecutable.PEReader]::new($dllStream)
    $pdbStream = $null
    $provider = $null
    try {
        $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $name = $metadata.GetString($metadata.GetAssemblyDefinition().Name)
        $codeViewEntries = @($pe.ReadDebugDirectory() | Where-Object Type -eq ([Reflection.PortableExecutable.DebugDirectoryEntryType]::CodeView))
        if ($codeViewEntries.Count -ne 1) { throw 'Exactly one CodeView entry is required.' }
        $codeView = $pe.ReadCodeViewDebugDirectoryData($codeViewEntries[0])
        $pdbStream = [IO.File]::OpenRead($Pdb)
        $provider = [Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream($pdbStream)
        $reader = $provider.GetMetadataReader()
        $id = [byte[]]@($reader.DebugMetadataHeader.Id)
        $guidBytes = [byte[]]$id[0..15]
        $stamp = [BitConverter]::ToUInt32($id, 16)
        if ($id.Length -ne 20 -or [Guid]::new($guidBytes) -ne $codeView.Guid -or $codeView.Age -ne 1 -or $stamp -ne $codeViewEntries[0].Stamp) { throw 'PDB does not belong to production DLL.' }
        $files = @{}
        foreach ($documentHandle in $reader.Documents) {
            $document = $reader.GetDocument($documentHandle)
            $path = $reader.GetString($document.Name).Replace('\', '/')
            if (-not $files.ContainsKey($path)) { $files[$path] = [Collections.Generic.HashSet[int]]::new() }
        }
        foreach ($handle in $reader.MethodDebugInformation) {
            $method = $reader.GetMethodDebugInformation($handle)
            foreach ($point in $method.GetSequencePoints()) {
                if ($point.IsHidden) { continue }
                $documentHandle = if ($point.Document.IsNil) { $method.Document } else { $point.Document }
                if ($documentHandle.IsNil) { throw 'Executable point has no source document.' }
                $document = $reader.GetDocument($documentHandle)
                $path = $reader.GetString($document.Name).Replace('\', '/')
                if (-not $files.ContainsKey($path)) { $files[$path] = [Collections.Generic.HashSet[int]]::new() }
                for ($line = $point.StartLine; $line -le $point.EndLine; $line++) { [void]$files[$path].Add($line) }
            }
        }
        if (-not $files.Count) { throw 'Production assembly has no executable source inventory.' }
        $documents = foreach ($path in ($files.Keys | Sort-Object)) {
            [pscustomobject]@{ Path = $path; Lines = @($files[$path] | Sort-Object); Generated = ($path -match '(?:/obj/|\.g\.cs\z|\.generated\.cs\z)') }
        }
        [pscustomobject]@{ Name = $name; Dll = [IO.Path]::GetFullPath($Dll); Pdb = [IO.Path]::GetFullPath($Pdb); DllSha256 = (Get-FileHash -LiteralPath $Dll -Algorithm SHA256).Hash; PdbSha256 = (Get-FileHash -LiteralPath $Pdb -Algorithm SHA256).Hash; Documents = @($documents) }
    } finally {
        if ($null -ne $provider) { $provider.Dispose() }
        if ($null -ne $pdbStream) { $pdbStream.Dispose() }
        $pe.Dispose()
        $dllStream.Dispose()
    }
}

function Compare-LegacyCoverageInventory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Inventory,
        [Parameter(Mandatory)] [string]$CoberturaPath,
        [Parameter(Mandatory)] [string]$RepositoryRoot,
        [ValidateRange(0, 100)] [double]$MinimumPercent = 80
    )
    $report = [xml][IO.File]::ReadAllText($CoberturaPath)
    $packages = @($report.SelectNodes('/coverage/packages/package'))
    $reportSources = @($report.SelectNodes('/coverage/sources/source') | ForEach-Object InnerText)
    $expectedNames = @($Inventory | ForEach-Object Name)
    if (@($expectedNames | Sort-Object -Unique).Count -ne $expectedNames.Count) { throw 'Duplicate production assembly inventory.' }
    $covered = 0
    $total = 0
    $generatedReported = 0
    $assemblyResults = @()
    foreach ($assembly in $Inventory) {
        $assemblyTotal = 0
        $assemblyCovered = 0
        $assemblyGenerated = 0
        $package = @($packages | Where-Object { $_.GetAttribute('name') -eq $assembly.Name })
        if ($package.Count -ne 1) { throw ('Missing or duplicate production assembly: ' + $assembly.Name) }
        $observed = @{}
        foreach ($class in $package[0].SelectNodes('./classes/class')) {
            $file = $class.GetAttribute('filename').Replace('\', '/')
            if (-not $observed.ContainsKey($file)) { $observed[$file] = @{} }
            foreach ($line in $class.SelectNodes('./lines/line')) {
                $number = [int]$line.GetAttribute('number')
                $hits = [long]$line.GetAttribute('hits')
                if ($number -le 0 -or $hits -lt 0) { throw 'Invalid raw coverage line.' }
                if (-not $observed[$file].ContainsKey($number) -or $observed[$file][$number] -lt $hits) { $observed[$file][$number] = $hits }
            }
        }
        foreach ($document in $assembly.Documents) {
            $absolute = $document.Path.Replace('\', '/')
            $relative = [IO.Path]::GetRelativePath($RepositoryRoot, $absolute).Replace('\', '/')
            $matches = @($observed.Keys | Where-Object {
                if ($_ -eq $absolute -or $_ -eq $relative) { return $true }
                $file = $_
                foreach ($sourceRoot in $reportSources) {
                    if (-not [IO.Path]::IsPathFullyQualified($file) -and [IO.Path]::GetFullPath((Join-Path $sourceRoot $file)).Replace('\', '/') -eq $absolute) { return $true }
                }
                return $false
            })
            # Ambiguous basename matches are deliberately forbidden.
            if (-not $document.Lines.Count -and -not $matches.Count) { continue }
            if ($matches.Count -ne 1) { throw ('Missing or ambiguous production source: ' + $absolute) }
            foreach ($number in $document.Lines) {
                if (-not $observed[$matches[0]].ContainsKey([int]$number)) { throw ('Omitted executable line: ' + $absolute + ':' + $number) }
            }
        }
        foreach ($file in $observed.Keys) {
            $owners = @($assembly.Documents | Where-Object {
                $absolute = $_.Path.Replace('\', '/')
                $relative = [IO.Path]::GetRelativePath($RepositoryRoot, $absolute).Replace('\', '/')
                if ($file -eq $absolute -or $file -eq $relative) { return $true }
                foreach ($sourceRoot in $reportSources) {
                    if (-not [IO.Path]::IsPathFullyQualified($file) -and [IO.Path]::GetFullPath((Join-Path $sourceRoot $file)).Replace('\', '/') -eq $absolute) { return $true }
                }
                return $false
            })
            if ($owners.Count -ne 1) { throw ('Reported source has no unique compiled production owner: ' + $file) }
            foreach ($number in $observed[$file].Keys) {
                if ([int]$number -notin $owners[0].Lines) { throw ('Reported line is outside the compiled executable inventory: ' + $file + ':' + $number) }
            }
        }
        # Preserve every valid reported production line, including generated documents.
        # No attribute, filename, or generated-code exclusions; fabricated spans are rejected.
        foreach ($file in $observed.Keys) {
            foreach ($hits in $observed[$file].Values) { $total++; $assemblyTotal++; if ($hits -gt 0) { $covered++; $assemblyCovered++ } }
            if ($file -match '(?:/obj/|\.g\.cs\z|\.generated\.cs\z)') { $generatedReported += $observed[$file].Count; $assemblyGenerated += $observed[$file].Count }
        }
        if (-not $assemblyTotal) { throw ('Empty production assembly denominator: ' + $assembly.Name) }
        $assemblyPercent = 100.0 * $assemblyCovered / $assemblyTotal
        $assemblyResults += [pscustomobject]@{ Name = $assembly.Name; RawLines = $assemblyTotal; CoveredLines = $assemblyCovered; GeneratedReportedLines = $assemblyGenerated; CoveragePercent = $assemblyPercent; MeetsThreshold = ($assemblyPercent -ge $MinimumPercent) }
    }
    if (-not $total) { throw 'Empty production line denominator.' }
    $percent = 100.0 * $covered / $total
    [pscustomobject]@{ Complete = $true; ProductionAssemblies = $expectedNames; Assemblies = $assemblyResults; RawLines = $total; CoveredLines = $covered; GeneratedReportedLines = $generatedReported; CoveragePercent = $percent; MinimumPercent = $MinimumPercent; MeetsThreshold = (@($assemblyResults | Where-Object { -not $_.MeetsThreshold }).Count -eq 0) }
}

function Test-LegacyProductionCoverage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string]$CoberturaPath,
        [Parameter(Mandatory)] [string]$RepositoryRoot,
        [string]$Configuration = 'Release',
        [ValidateRange(0, 100)] [double]$MinimumPercent = 80
    )
    # Acceptance derives the entire inventory from the solution and real compiled artifacts.
    # A caller-supplied subset or fabricated line inventory is never an acceptance input.
    $plan = Get-LegacyProductionAssemblyPlan -RepositoryRoot $RepositoryRoot -Configuration $Configuration
    if ($plan.LinkedTestSources.Count) { throw 'Duplicate linked production test compilation remains unresolved.' }
    $inventory = @()
    foreach ($assembly in $plan.Assemblies) {
        $actual = Get-LegacyAssemblyExecutableLines -Dll $assembly.Dll -Pdb $assembly.Pdb
        if ($actual.Name -ne $assembly.Name) { throw 'Compiled production assembly differs from solution ownership.' }
        $inventory += $actual
    }
    $coverage = Compare-LegacyCoverageInventory -Inventory $inventory -CoberturaPath $CoberturaPath -RepositoryRoot $RepositoryRoot -MinimumPercent $MinimumPercent
    [pscustomobject]@{ ArtifactInventory = $inventory; Coverage = $coverage }
}
