[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$AcmeEmail,
    [Parameter(Mandatory)] [string]$ExistingStaticIpName,
    [Parameter(Mandatory)] [string]$OutputPath,
    [ValidateSet(1, 2, 3, 4)] [int]$SchemaVersion = 1,
    [Parameter(Mandatory)] [ValidatePattern('\A[A-Fa-f0-9]{64}\z')] [string]$ReviewedAssemblySha256,
    [string]$ReleaseObservationPath
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
if ($SchemaVersion -eq 4) {
    if ([string]::IsNullOrWhiteSpace($ReleaseObservationPath)) {
        throw 'Schema four requires an explicit supplied-observation file.'
    }
    $inputStream = [IO.FileStream]::new($ReleaseObservationPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($inputStream.Length -gt 32 * 1024) { throw 'Supplied observations exceed 32 KiB.' }
        $buffer = [byte[]]::new([int]$inputStream.Length)
        $inputStream.ReadExactly($buffer, 0, $buffer.Length)
        if ($inputStream.ReadByte() -ne -1) { throw 'Supplied observations changed while reading.' }
        $suppliedJson = [Text.UTF8Encoding]::new($false, $true).GetString($buffer)
    } finally {
        $inputStream.Dispose()
    }
    $package = [Legacy.Maliev.AppHost.Topology.LegacyEdgeReviewPackage]::RenderReleaseSelectorReview($AcmeEmail, $ExistingStaticIpName, $suppliedJson)
} else {
    if ($PSBoundParameters.ContainsKey('ReleaseObservationPath')) {
        throw 'A supplied-observation file is only permitted with schema four.'
    }
    $package = [Legacy.Maliev.AppHost.Topology.LegacyEdgeReviewPackage]::Render($AcmeEmail, $ExistingStaticIpName, $SchemaVersion)
}
$document = [Text.Json.JsonDocument]::Parse([string]$package)
try {
    $root = $document.RootElement
    if ($root.GetProperty('schemaVersion').GetInt32() -ne $SchemaVersion -or
        -not $root.GetProperty('reviewOnly').GetBoolean() -or
        $root.GetProperty('productionDeploymentAllowed').GetBoolean() -or
        $root.GetProperty('cutoverPercent').GetInt32() -ne 0 -or
        $root.GetProperty('objects').ValueKind -ne [Text.Json.JsonValueKind]::Array -or
        $root.GetProperty('objects').GetArrayLength() -eq 0 -or
        $root.GetProperty('unresolvedGates').ValueKind -ne [Text.Json.JsonValueKind]::Array -or
        $root.GetProperty('unresolvedGates').GetArrayLength() -eq 0) {
        throw 'Compiled renderer did not return an inert review package; rebuild the reviewed source.'
    }
    if ($SchemaVersion -ge 2) {
        $resourceReview = $root.GetProperty('workloadResourceReview')
        if ($resourceReview.GetProperty('semantics').GetString() -ne 'ConditionalTextReplacementIntent' -or
            $resourceReview.GetProperty('operationExecutionVerified').GetBoolean() -or
            $resourceReview.GetProperty('manifestAdoptionVerified').GetBoolean() -or
            $resourceReview.GetProperty('capacityAccepted').GetBoolean() -or
            $resourceReview.GetProperty('workloads').GetArrayLength() -ne 8) {
            throw 'Compiled renderer did not return conditional, unaccepted resource intent.'
        }
    }
    if ($SchemaVersion -ge 3) {
        $installer = $root.GetProperty('controllerInstallationReview')
        if ($installer.GetProperty('semantics').GetString() -ne 'UnexecutedSourceInstallationPlan' -or
            $installer.GetProperty('status').GetString() -ne 'Unverified' -or
            $installer.GetProperty('executionAllowed').GetBoolean() -or
            $installer.GetProperty('controllerInstalledVerified').GetBoolean() -or
            $installer.GetProperty('smokeTestVerified').GetBoolean() -or
            $installer.GetProperty('backupVerified').GetBoolean() -or
            $installer.GetProperty('namespaceOwnershipVerified').GetBoolean() -or
            $installer.GetProperty('nativeExitPropagationVerified').GetBoolean() -or
            $installer.GetProperty('operations').GetArrayLength() -ne 32 -or
            $installer.GetProperty('sourceSmokeResources').GetArrayLength() -ne 3) {
            throw 'Compiled renderer did not return an unexecuted, unverified installer plan.'
        }
    }
    if ($SchemaVersion -eq 4) {
        $selector = $root.GetProperty('releaseSelectorReview')
        if ($selector.GetProperty('semantics').GetString() -ne 'SuppliedSourceControlFlowReview' -or
            $selector.GetProperty('observationsVerified').GetBoolean() -or
            $selector.GetProperty('sourceHelperExecutionVerified').GetBoolean() -or
            $selector.GetProperty('callerDirectoryRestoredVerified').GetBoolean() -or
            $selector.GetProperty('successorMappingVerified').GetBoolean() -or
            $selector.GetProperty('trace').GetArrayLength() -ne $selector.GetProperty('selectedSourceServices').GetArrayLength()) {
            throw 'Compiled renderer did not return unverified supplied source control flow.'
        }
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
