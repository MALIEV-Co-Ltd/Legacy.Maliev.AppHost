[CmdletBinding()]
param([string]$SourceRepository, [string]$WorkspaceRoot, [string]$ManifestPath)

function Test-DocumentationOwnerManifest($Manifest, [string[]]$SourceProjects) {
    $known = @('AccountingService', 'AuthService', 'CareerService', 'CatalogService', 'ContactService', 'CountryService', 'CustomerService', 'DocumentService', 'EmployeeService', 'FileService', 'Intranet', 'NotificationService', 'OrderService', 'ProcurementService', 'QuotationService', 'ServiceDefaults', 'Web', 'CompatibilityContracts') | ForEach-Object { "Legacy.Maliev.$_" }
    if ($Manifest.sourceCommit -ne '03dc9a1271c16e6535934445e9dd6e3f30e8fffe') { throw 'Unexpected source commit.' }
    $owners = @($Manifest.repositories | ForEach-Object { $_.name })
    if ($owners.Count -eq 0 -or @($owners | Sort-Object -Unique).Count -ne $owners.Count) { throw 'Invalid repository inventory.' }
    foreach ($repo in $Manifest.repositories) {
        if ($repo.name -notin $known -or $repo.commit -cnotmatch '^[0-9a-f]{40}$') { throw 'Unknown repository or non-immutable commit.' }
    }
    $paths = @($Manifest.projects | ForEach-Object { $_.sourceProject })
    if ($paths.Count -eq 0 -or @($paths | Sort-Object -Unique).Count -ne $paths.Count -or
        @(Compare-Object @($SourceProjects | Sort-Object) @($paths | Sort-Object)).Count) { throw 'Source project accounting mismatch.' }
    foreach ($project in $Manifest.projects) {
        if ([string]::IsNullOrWhiteSpace($project.rationale)) { throw 'Missing disposition rationale.' }
        if ($project.disposition -eq 'approved-retirement') {
            if ($project.sourceProject -notmatch '^Maliev\.PredictionService\.' -or @($project.owners).Count -ne 0) { throw 'Invalid retirement.' }
        } elseif ($project.disposition -eq 'architecture-equivalent') {
            if (@($project.owners).Count -eq 0) { throw 'Missing owner.' }
            foreach ($owner in $project.owners) { if ($owner -notin $owners) { throw 'Unknown project owner.' } }
        } else { throw 'Unknown disposition.' }
    }
}

function Invoke-ReadOnlyGit([string]$Repository, [string[]]$Arguments) {
    if (-not [IO.Directory]::Exists($Repository)) { throw 'Repository is missing.' }
    $result = @(& git -C $Repository @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'Committed metadata read failed; repository or object is unavailable.' }
    return $result
}

function Read-CommittedBuildMetadata([string]$Repository, [string]$Commit) {
    if ($Commit -cnotmatch '^[0-9a-f]{40}$') { throw 'An immutable commit is required.' }
    $resolved = Invoke-ReadOnlyGit $Repository @('rev-parse', '--verify', "$Commit^{commit}")
    if ([string]$resolved -ne $Commit) { throw 'Commit identity mismatch.' }
    $paths = @(Invoke-ReadOnlyGit $Repository @('ls-tree', '-r', '--name-only', $Commit))
    $files = @{}
    foreach ($path in @($paths | Where-Object { $_ -match '\.(csproj|props|targets)$' })) {
        $files[$path] = (Invoke-ReadOnlyGit $Repository @('show', "${Commit}:$path")) -join "`n"
    }
    return @{ Files = $files; TrackedPaths = $paths }
}

function Test-IsolatedOutputPath([string]$Value) {
    $path = $Value.Replace('\', '/')
    return $path -notmatch '(^|/)\.\.(/|$)' -and
        $path -match '^(?:bin/|obj/|\$\((?:IntermediateOutputPath|BaseIntermediateOutputPath|OutputPath|BaseOutputPath|TargetDir)\))'
}

function Test-DocumentationMetadata {
    param([System.Collections.IDictionary]$Files, [string[]]$TrackedPaths)
    $issues = [Collections.Generic.List[string]]::new()
    $projects = @($Files.Keys | Where-Object { $_ -like '*.csproj' })
    if ($projects.Count -eq 0) { $issues.Add('missing_project_metadata') }
    $documents = @{}
    $assemblyNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $Files.Keys) {
        try {
            $settings = [Xml.XmlReaderSettings]::new()
            $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
            $settings.XmlResolver = $null
            $text = ([string]$Files[$path]).TrimStart([char]0xFEFF)
            $reader = [Xml.XmlReader]::Create([IO.StringReader]::new($text), $settings)
            try {
                $document = [Xml.XmlDocument]::new()
                $document.XmlResolver = $null
                $document.Load($reader)
            } finally { $reader.Dispose() }
            if ($document.DocumentElement.LocalName -ne 'Project') { throw 'invalid root' }
            $documents[$path] = $document
            if ($path -like '*.csproj') {
                $names = @([IO.Path]::GetFileNameWithoutExtension($path))
                $names += @($document.SelectNodes('//*[local-name()="AssemblyName"]') | ForEach-Object { $_.InnerText })
                foreach ($name in $names) {
                    if ($name -notmatch '^[A-Za-z0-9_.-]+$') { $issues.Add("unresolved_assembly_name:$path"); continue }
                    [void]$assemblyNames.Add($name + '.xml')
                    $directory = ($path -replace '/[^/]+$', '')
                    $candidate = if ($directory -eq $path) { $name + '.xml' } else { "$directory/$name.xml" }
                    if ($TrackedPaths -contains $candidate) { $issues.Add("tracked_assembly_xml:$candidate") }
                }
            }
        } catch { $issues.Add("invalid_metadata:$path") }
    }
    foreach ($path in $documents.Keys) {
        $document = $documents[$path]
        foreach ($node in $document.SelectNodes('//*[local-name()="DocumentationFile" or local-name()="OutputPath" or local-name()="BaseOutputPath" or local-name()="IntermediateOutputPath" or local-name()="BaseIntermediateOutputPath"]')) {
            if (-not (Test-IsolatedOutputPath $node.InnerText)) { $issues.Add("nonisolated_or_unresolved_output:$path/$($node.LocalName)") }
        }
        foreach ($node in $document.SelectNodes('//*[@Include or @Update]')) {
            $copy = @($node.SelectNodes('*[local-name()="CopyToOutputDirectory" or local-name()="CopyToPublishDirectory"]') | ForEach-Object { $_.InnerText })
            $copy += @($node.GetAttribute('CopyToOutputDirectory'), $node.GetAttribute('CopyToPublishDirectory'))
            if (-not @($copy | Where-Object { $_ -and $_ -ne 'Never' }).Count) { continue }
            $item = ($node.GetAttribute('Include') + ';' + $node.GetAttribute('Update')).Replace('\', '/')
            foreach ($pattern in $item.Split(';', [StringSplitOptions]::RemoveEmptyEntries)) {
                if (Test-IsolatedOutputPath $pattern) { continue }
                # A literal child subtree cannot copy a project-root generated artifact.
                # Do not reject unrelated XML/assets such as Baselines/**/*.
                if ($pattern -match '^[A-Za-z0-9_-]+/' -and $pattern -notmatch '(^|/)\.\.(/|$)') { continue }
                $leaf = ($pattern -split '/')[-1]
                if ($leaf -match '\$\(' -or @($assemblyNames | Where-Object { $_ -like $leaf }).Count) {
                    $issues.Add("generated_xml_copy:$path/$($node.LocalName)")
                }
            }
        }
    }
    return $issues.ToArray() | Sort-Object -Unique
}

if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'
    if (-not $SourceRepository -or -not $WorkspaceRoot -or -not $ManifestPath) { throw 'SourceRepository, WorkspaceRoot, and ManifestPath are required.' }
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json -AsHashtable
    $source = @(Invoke-ReadOnlyGit $SourceRepository @('diff-tree', '--no-commit-id', '--name-only', '-r', '03dc9a1271c16e6535934445e9dd6e3f30e8fffe', '--', '*.csproj'))
    if ($source.Count -ne 88) { throw 'Expected exactly 88 committed source project changes.' }
    Test-DocumentationOwnerManifest $manifest $source
    $reports = @()
    foreach ($repo in $manifest.repositories) {
        $metadata = Read-CommittedBuildMetadata (Join-Path $WorkspaceRoot $repo.name) $repo.commit
        $issues = @(Test-DocumentationMetadata $metadata.Files $metadata.TrackedPaths)
        if ($issues.Count) { throw "$($repo.name): $($issues -join '; ')" }
        $reports += @{ repository = $repo.name; commit = $repo.commit; metadataFiles = $metadata.Files.Count; projects = @($metadata.Files.Keys | Where-Object { $_ -like '*.csproj' }).Count }
    }
    @{ status = 'committed-metadata-pass'; sourceCommit = $manifest.sourceCommit; sourceProjects = $source.Count; retiredProjects = @($manifest.projects | Where-Object { $_.disposition -eq 'approved-retirement' }).Count; repositories = $reports; limitation = 'Structural committed metadata only; not evaluated MSBuild, generated outputs, external imports, or runtime evidence.' } | ConvertTo-Json -Depth 6
}
