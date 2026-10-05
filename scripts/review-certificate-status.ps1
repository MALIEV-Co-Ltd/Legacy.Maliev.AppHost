[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$EvidencePath,
    [Parameter(Mandatory)] [datetimeoffset]$AsOfUtc,
    [Parameter(Mandatory)] [string]$OutputPath,
    [Parameter(Mandatory)] [ValidatePattern('\A[A-Fa-f0-9]{64}\z')] [string]$ReviewedAssemblySha256
)

$ErrorActionPreference = 'Stop'
$assemblyPath = Join-Path $PSScriptRoot '../Legacy.Maliev.AppHost.Topology/bin/Release/net10.0/Legacy.Maliev.AppHost.Topology.dll'
$pdbPath = [IO.Path]::ChangeExtension($assemblyPath, '.pdb')
$diagnosticSourcePath = Join-Path $PSScriptRoot '../Legacy.Maliev.AppHost.Topology/LegacyCertificateReviewDiagnostics.cs'
if ([System.Management.Automation.PSTypeName]::new('Legacy.Maliev.AppHost.Topology.LegacyCertificateReviewDiagnostics').Type) {
    throw 'Use a fresh PowerShell process for the reviewed diagnostic assembly.'
}
if ((Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash -ne $ReviewedAssemblySha256) {
    throw 'Diagnostic DLL differs from the explicitly reviewed artifact hash.'
}
Add-Type -Path $assemblyPath
[Legacy.Maliev.AppHost.Topology.LegacyRendererSourceProvenance]::ValidateCertificateDiagnostics(
    $assemblyPath, $pdbPath, $diagnosticSourcePath, $ReviewedAssemblySha256)
$inputStream = [IO.File]::OpenRead([IO.Path]::GetFullPath($EvidencePath))
try {
    $buffer = [byte[]]::new(32769)
    $length = 0
    while ($length -lt $buffer.Length) {
        $read = $inputStream.Read($buffer, $length, $buffer.Length - $length)
        if ($read -eq 0) { break }
        $length += $read
    }
    if ($length -gt 32768) { throw 'Certificate evidence exceeds its bound.' }
    $evidence = [Text.UTF8Encoding]::new($false, $true).GetString($buffer, 0, $length)
} finally { $inputStream.Dispose() }
$report = [Legacy.Maliev.AppHost.Topology.LegacyCertificateReviewDiagnostics]::Diagnose($evidence, $AsOfUtc)
$output = [IO.FileStream]::new([IO.Path]::GetFullPath($OutputPath), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($report)
    $output.Write($bytes, 0, $bytes.Length)
    $output.Flush($true)
} finally { $output.Dispose() }
Write-Output "Offline certificate observation review written: $OutputPath"
