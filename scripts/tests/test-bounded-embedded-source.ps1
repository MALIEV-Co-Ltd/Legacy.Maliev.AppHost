[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../coverage/production-coverage-inventory.ps1')
$cases = @()
$bytes = [Text.Encoding]::UTF8.GetBytes("synthetic decoder control`n")
$plain = [byte[]]([BitConverter]::GetBytes(0) + $bytes)
if ([Convert]::ToHexString((Expand-LegacyEmbeddedSource -Blob $plain)) -ne [Convert]::ToHexString($bytes)) { throw 'Plain source bytes changed.' }
$cases += 'plain-bytes-preserved'
$stream = [IO.MemoryStream]::new()
$compressor = [IO.Compression.DeflateStream]::new($stream, [IO.Compression.CompressionMode]::Compress, $true)
try { $compressor.Write($bytes, 0, $bytes.Length) } finally { $compressor.Dispose() }
$compressedBytes = $stream.ToArray()
$stream.Dispose()
$compressed = [byte[]]([BitConverter]::GetBytes($bytes.Length) + $compressedBytes)
if ([Convert]::ToHexString((Expand-LegacyEmbeddedSource -Blob $compressed)) -ne [Convert]::ToHexString($bytes)) { throw 'Compressed source bytes changed.' }
$cases += 'compressed-bytes-preserved'
$invalid = @{
    'truncated-header' = [byte[]](1, 2, 3)
    'empty-payload' = [BitConverter]::GetBytes(0)
    'negative-declared-size' = [byte[]]([BitConverter]::GetBytes(-1) + $compressedBytes)
    'oversized-declared-size' = [byte[]]([BitConverter]::GetBytes(1048577) + $compressedBytes)
    'undersized-declared-output' = [byte[]]([BitConverter]::GetBytes($bytes.Length - 1) + $compressedBytes)
    'oversized-declared-output' = [byte[]]([BitConverter]::GetBytes($bytes.Length + 1) + $compressedBytes)
    'invalid-deflate-payload' = [byte[]]([BitConverter]::GetBytes(100) + [byte[]](255, 255, 255))
    'oversized-record' = [byte[]]::new(1048581)
}
foreach ($name in $invalid.Keys) {
    $refused = $false
    try { [void](Expand-LegacyEmbeddedSource -Blob $invalid[$name]) } catch { $refused = $true }
    if (-not $refused) { throw "Decoder control failed to refuse $name." }
    $cases += $name
}
$diskControl = Join-Path ([IO.Path]::GetTempPath()) ('legacy-embedded-source-disk-control-' + [Guid]::NewGuid().ToString('N') + '.bin')
try {
    $compiledBytes = Expand-LegacyEmbeddedSource -Blob $compressed
    $checksum = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($compiledBytes))
    [IO.File]::WriteAllBytes($diskControl, $compiledBytes)
    Assert-LegacyDiskSourceChecksum -Path $diskControl -Checksum $checksum
    $cases += 'embedded-and-disk-source-match'
    [IO.File]::WriteAllBytes($diskControl, [Text.Encoding]::UTF8.GetBytes('different clean disk source'))
    $refused = $false
    try { Assert-LegacyDiskSourceChecksum -Path $diskControl -Checksum $checksum } catch { $refused = $true }
    if (-not $refused) { throw 'Embedded compiled/disk source mismatch was not refused.' }
    $cases += 'embedded-vs-disk-source-mismatch-refused'
} finally { [IO.File]::Delete($diskControl) }
[pscustomobject]@{ Purpose = 'synthetic-decoder-controls-not-production-coverage'; Passed = $cases.Count; Failed = 0; Cases = $cases } | ConvertTo-Json -Depth 3
