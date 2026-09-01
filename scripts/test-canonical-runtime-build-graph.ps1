[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string]$RootProjectPath,
    [Parameter(Mandatory = $true)] [string]$WorkspaceRoot,
    [Parameter(Mandatory = $true)] [string[]]$ReviewedRepository,
    [switch]$RequireReleaseDeps
)

$ErrorActionPreference = 'Stop'

function Get-FullPathWithSeparator([string]$Path) {
    return [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
}

$workspace = Get-FullPathWithSeparator $WorkspaceRoot
$reviewedRoots = @($ReviewedRepository | ForEach-Object {
    $root = Get-FullPathWithSeparator (Join-Path $workspace $_)
    if (-not [IO.Directory]::Exists($root)) { throw "Reviewed repository is missing: $_" }
    $root
})

function Assert-InReviewedRoot([string]$Path, [string]$Label) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not @($reviewedRoots | Where-Object { $fullPath.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }).Count) {
        throw "$Label escapes the reviewed canonical repository roots: $fullPath"
    }
    if ($fullPath -match '(?i)[\\/]\.worktrees[\\/]') { throw "$Label references a worktree path." }
    return $fullPath
}

$rootProject = Assert-InReviewedRoot $RootProjectPath 'Root project'
$pending = [Collections.Generic.Queue[string]]::new()
$visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$pending.Enqueue($rootProject)

while ($pending.Count -gt 0) {
    $projectPath = $pending.Dequeue()
    if (-not $visited.Add($projectPath)) { continue }
    if (-not [IO.File]::Exists($projectPath)) { throw "Reviewed project is missing: $projectPath" }

    $projectDirectory = [IO.Path]::GetDirectoryName($projectPath)
    $assetsPath = Join-Path $projectDirectory 'obj\project.assets.json'
    if (-not [IO.File]::Exists($assetsPath)) { throw "Restore assets are missing for reviewed project: $projectPath" }
    $assetsText = [IO.File]::ReadAllText($assetsPath, [Text.Encoding]::UTF8)
    if ($assetsText -match '(?i)[\\/]\.worktrees[\\/]') { throw "Restore assets contain a worktree path: $assetsPath" }
    $assets = $assetsText | ConvertFrom-Json -DateKind String
    $assetProjectPath = Assert-InReviewedRoot ([string]$assets.project.restore.projectPath) "Restore project path for '$projectPath'"
    $assetUniqueName = Assert-InReviewedRoot ([string]$assets.project.restore.projectUniqueName) "Restore project identity for '$projectPath'"
    [void](Assert-InReviewedRoot ([string]$assets.project.restore.outputPath) "Restore output path for '$projectPath'")
    if (-not $assetProjectPath.Equals($projectPath, [StringComparison]::OrdinalIgnoreCase) -or
        -not $assetUniqueName.Equals($projectPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Restore assets are not bound to the reviewed project path: $projectPath"
    }
    foreach ($framework in @($assets.project.restore.frameworks.PSObject.Properties)) {
        foreach ($reference in @($framework.Value.projectReferences.PSObject.Properties)) {
            $referencePath = Assert-InReviewedRoot ([string]$reference.Name) "Project reference from '$projectPath'"
            if ([string]$reference.Value.projectPath -and -not
                ([IO.Path]::GetFullPath([string]$reference.Value.projectPath)).Equals($referencePath, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Project-reference identity is inconsistent in restore assets for '$projectPath'."
            }
            $pending.Enqueue($referencePath)
        }
    }
}

if ($RequireReleaseDeps) {
    $depsCount = 0
    foreach ($projectPath in $visited) {
        $releaseDirectory = Join-Path ([IO.Path]::GetDirectoryName($projectPath)) 'bin\Release'
        if (-not [IO.Directory]::Exists($releaseDirectory)) { continue }
        foreach ($depsFile in @(Get-ChildItem -LiteralPath $releaseDirectory -Recurse -File -Filter '*.deps.json')) {
            $depsCount++
            $depsText = [IO.File]::ReadAllText($depsFile.FullName, [Text.Encoding]::UTF8)
            if ($depsText -match '(?i)[\\/]\.worktrees[\\/]') { throw "Release dependency manifest contains a worktree path: $($depsFile.FullName)" }
        }
    }
    if ($depsCount -eq 0) { throw 'No Release dependency manifests were produced for the reviewed runtime graph.' }
}

Write-Output "PASS: canonical runtime graph contains $($visited.Count) reviewed projects and no worktree references."
