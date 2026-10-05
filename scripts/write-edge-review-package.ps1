[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$AcmeEmail,
    [Parameter(Mandatory)] [string]$ExistingStaticIpName,
    [Parameter(Mandatory)] [string]$OutputPath,
    [Parameter(Mandatory)] [ValidatePattern('\A[A-Fa-f0-9]{64}\z')] [string]$ReviewedAssemblySha256
)

$ErrorActionPreference = 'Stop'
$assemblyPath = Join-Path $PSScriptRoot '..\Legacy.Maliev.AppHost.Topology\bin\Release\net10.0\Legacy.Maliev.AppHost.Topology.dll'
if (-not [IO.File]::Exists($assemblyPath)) {
    throw 'Build the reviewed Topology project in Release before rendering the edge package.'
}
$pdbPath = [IO.Path]::ChangeExtension($assemblyPath, '.pdb')
$rendererSourcePath = Join-Path $PSScriptRoot '..\Legacy.Maliev.AppHost.Topology\LegacyEdgeReviewPackage.cs'
foreach ($requiredPath in @($pdbPath, $rendererSourcePath)) {
    if (-not [IO.File]::Exists($requiredPath)) {
        throw 'The reviewed DLL requires its linked portable PDB and current renderer source.'
    }
}
# The supplied hash must come from the independently reviewed build record.
# Do not derive it here from whichever DLL happens to occupy this path.
if ((Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash -ne $ReviewedAssemblySha256) {
    throw 'Renderer DLL differs from the explicitly reviewed artifact hash.'
}
if ([System.Management.Automation.PSTypeName]::new('Legacy.Maliev.AppHost.Topology.LegacyEdgeReviewPackage').Type) {
    throw 'Use a fresh PowerShell process; a previously loaded renderer cannot establish reviewed build identity.'
}
Add-Type -Path $assemblyPath
[Legacy.Maliev.AppHost.Topology.LegacyRendererSourceProvenance]::Validate(
    $assemblyPath, $pdbPath, $rendererSourcePath, $ReviewedAssemblySha256)
$package = [Legacy.Maliev.AppHost.Topology.LegacyEdgeReviewPackage]::Render($AcmeEmail, $ExistingStaticIpName)
$document = [Text.Json.JsonDocument]::Parse([string]$package)
try {
    $root = $document.RootElement
    if ($root.GetProperty('schemaVersion').GetInt32() -ne 1 -or
        -not $root.GetProperty('reviewOnly').GetBoolean() -or
        $root.GetProperty('productionDeploymentAllowed').GetBoolean() -or
        $root.GetProperty('cutoverPercent').GetInt32() -ne 0 -or
        $root.GetProperty('objects').ValueKind -ne [Text.Json.JsonValueKind]::Array -or
        $root.GetProperty('objects').GetArrayLength() -eq 0 -or
        $root.GetProperty('unresolvedGates').ValueKind -ne [Text.Json.JsonValueKind]::Array -or
        $root.GetProperty('unresolvedGates').GetArrayLength() -eq 0) {
        throw 'Compiled renderer did not return an inert review package; rebuild the reviewed source.'
    }
} finally {
    $document.Dispose()
}
$destination = [IO.Path]::GetFullPath($OutputPath)
$bytes = [Text.UTF8Encoding]::new($false).GetBytes($package)
$stream = [IO.FileStream]::new($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush($true)
} finally {
    $stream.Dispose()
}
Write-Output "Offline edge review package written: $destination"
