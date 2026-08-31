[CmdletBinding()]
param(
    [string] $WorkspaceRoot = 'B:\maliev-legacy',
    [string] $SnapshotDirectory,
    [string]$SnapshotEncryptionKeyFile,
    [string]$SnapshotId,
    [ValidateRange(1, 65535)]
    [int] $WebPort = 5188
)

$ErrorActionPreference = 'Stop'

$appHostRoot = Split-Path -Parent $PSScriptRoot
$appHostProject = Join-Path $appHostRoot 'Legacy.Maliev.AppHost\Legacy.Maliev.AppHost.csproj'
$migrationRunnerProject = Join-Path $appHostRoot 'Legacy.Maliev.AppHost.MigrationRunner\Legacy.Maliev.AppHost.MigrationRunner.csproj'
$webRepositoryRoot = Join-Path $WorkspaceRoot 'Legacy.Maliev.Web'
$webProject = Join-Path $webRepositoryRoot 'Legacy.Maliev.Web\Legacy.Maliev.Web.csproj'

if ([string]::IsNullOrWhiteSpace($SnapshotId) -or
    [string]::IsNullOrWhiteSpace($SnapshotEncryptionKeyFile)) {
    throw 'Local review requires an authenticated v2 shadow snapshot. Supply -SnapshotId and -SnapshotEncryptionKeyFile; an empty database or synthetic identity fixture is prohibited.'
}
if ($SnapshotId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') {
    throw "Snapshot id is not valid: $SnapshotId"
}

if ([string]::IsNullOrWhiteSpace($SnapshotDirectory)) {
    $snapshotRoot = Join-Path $env:LOCALAPPDATA 'MALIEV\legacy-postgres-snapshots'
    if (Test-Path -LiteralPath $snapshotRoot -PathType Container) {
        $SnapshotDirectory = Get-ChildItem -LiteralPath $snapshotRoot -Directory |
            Where-Object {
                $candidateManifestPath = Join-Path $_.FullName 'manifest.json'
                if (-not (Test-Path -LiteralPath $candidateManifestPath -PathType Leaf)) {
                    return $false
                }
                try {
                    $candidateManifest = Get-Content -LiteralPath $candidateManifestPath -Raw | ConvertFrom-Json
                    return $candidateManifest.SchemaVersion -eq 2 -and
                        $candidateManifest.Format -eq 'MLVSNP02' -and
                        $candidateManifest.Encryption -eq 'AES-256-GCM-chunked-v2' -and
                        $candidateManifest.SnapshotId -eq $SnapshotId
                }
                catch {
                    return $false
                }
            } |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }
}
if ([string]::IsNullOrWhiteSpace($SnapshotDirectory)) {
    throw "Local review requires the authenticated v2 shadow snapshot '$SnapshotId'. Supply -SnapshotDirectory or install it under %LOCALAPPDATA%\MALIEV\legacy-postgres-snapshots."
}
if (-not (Test-Path -LiteralPath $SnapshotDirectory -PathType Container)) {
    throw "Shadow snapshot directory does not exist: $SnapshotDirectory"
}
$SnapshotDirectory = (Resolve-Path -LiteralPath $SnapshotDirectory).Path

$manifestPath = Join-Path $SnapshotDirectory 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "The authenticated v2 shadow snapshot requires manifest.json: $SnapshotDirectory"
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.SchemaVersion -ne 2 -or
    $manifest.Format -ne 'MLVSNP02' -or
    $manifest.Encryption -ne 'AES-256-GCM-chunked-v2' -or
    $manifest.SnapshotId -ne $SnapshotId) {
    throw "The selected directory is not the authenticated v2 shadow snapshot '$SnapshotId': $SnapshotDirectory"
}
if (-not (Test-Path -LiteralPath $SnapshotEncryptionKeyFile -PathType Leaf)) {
    throw "Shadow snapshot encryption key file does not exist: $SnapshotEncryptionKeyFile"
}
$SnapshotEncryptionKeyFile = (Resolve-Path -LiteralPath $SnapshotEncryptionKeyFile).Path

$previousSnapshotDirectory = [Environment]::GetEnvironmentVariable('LEGACY_SNAPSHOT_DIRECTORY')
$previousSnapshotKeyFile = [Environment]::GetEnvironmentVariable('LEGACY_SNAPSHOT_ENCRYPTION_KEY_FILE')
$previousSnapshotId = [Environment]::GetEnvironmentVariable('LEGACY_SNAPSHOT_ID')
try {
    [Environment]::SetEnvironmentVariable('LEGACY_SNAPSHOT_DIRECTORY', $SnapshotDirectory)
    [Environment]::SetEnvironmentVariable('LEGACY_SNAPSHOT_ENCRYPTION_KEY_FILE', $SnapshotEncryptionKeyFile)
    [Environment]::SetEnvironmentVariable('LEGACY_SNAPSHOT_ID', $SnapshotId)
    & dotnet run --project $migrationRunnerProject --configuration Release -- snapshot-preflight
    if ($LASTEXITCODE -ne 0) {
        throw 'Authenticated encrypted exact-24 shadow snapshot preflight failed.'
    }
}
finally {
    [Environment]::SetEnvironmentVariable('LEGACY_SNAPSHOT_DIRECTORY', $previousSnapshotDirectory)
    [Environment]::SetEnvironmentVariable('LEGACY_SNAPSHOT_ENCRYPTION_KEY_FILE', $previousSnapshotKeyFile)
    [Environment]::SetEnvironmentVariable('LEGACY_SNAPSHOT_ID', $previousSnapshotId)
}

if (-not (Test-Path -LiteralPath $appHostProject -PathType Leaf)) {
    throw "Legacy AppHost project does not exist: $appHostProject"
}
if (-not (Test-Path -LiteralPath $webProject -PathType Leaf)) {
    throw "Legacy Web project does not exist: $webProject"
}

$webBranch = (& git -C $webRepositoryRoot branch --show-current).Trim()
$webCommit = (& git -C $webRepositoryRoot rev-parse HEAD).Trim()
$webRepository = (& git -C $webRepositoryRoot remote get-url origin).Trim()
if ($LASTEXITCODE -ne 0 -or
    [string]::IsNullOrWhiteSpace($webBranch) -or
    [string]::IsNullOrWhiteSpace($webCommit) -or
    [string]::IsNullOrWhiteSpace($webRepository)) {
    throw 'Legacy Web must be a Git checkout on a named branch with an origin remote.'
}

$environment = @{
    MalievWorkspaceRoot = $WorkspaceRoot
    LEGACY_GKE_VALIDATION = 'false'
    LEGACY_LOCAL_SNAPSHOT = 'true'
    LEGACY_LOCAL_SNAPSHOT_DIR = $SnapshotDirectory
    LEGACY_MIGRATION_SNAPSHOT_ENCRYPTION_KEY_FILE = $SnapshotEncryptionKeyFile
    LEGACY_LOCAL_SNAPSHOT_ID = $SnapshotId
    LEGACY_LOCAL_FIXTURES = 'false'
    LEGACY_WEB_PROJECT = $webProject
    LEGACY_WEB_REPOSITORY = $webRepository
    LEGACY_WEB_BRANCH = $webBranch
    LEGACY_WEB_COMMIT = $webCommit
    LEGACY_WEB_PORT = $WebPort.ToString([Globalization.CultureInfo]::InvariantCulture)
    ASPIRE_ALLOW_UNSECURED_TRANSPORT = 'true'
    ASPNETCORE_ENVIRONMENT = 'Development'
    ASPNETCORE_URLS = 'http://localhost:15888'
    'Parameters__legacy-postgres-username' = 'legacy_local'
    'Parameters__legacy-postgres-password' = [Guid]::NewGuid().ToString('N')
    'Parameters__legacy-redis-password' = [Guid]::NewGuid().ToString('N')
}

$googleMapsSecretPath = Join-Path $appHostRoot 'sharedsecrets.json'
$googleMapsApiKey = $null
if (Test-Path -LiteralPath $googleMapsSecretPath -PathType Leaf) {
    $googleMapsApiKey = (Get-Content -LiteralPath $googleMapsSecretPath -Raw |
        ConvertFrom-Json).GoogleMaps.BrowserApiKey
}
if ([string]::IsNullOrWhiteSpace($googleMapsApiKey)) {
    # Aspire parameters must resolve before dependent resources are created. A clearly
    # invalid local placeholder keeps map calls fail-closed without retrieving cloud secrets.
    $googleMapsApiKey = 'local-review-unconfigured'
}
$environment['Parameters__legacy-web-google-maps-embed-api-key'] = $googleMapsApiKey
$environment['Parameters__legacy-intranet-google-maps-browser-api-key'] = $googleMapsApiKey

$logDirectory = Join-Path $env:LOCALAPPDATA 'Maliev\logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stdoutPath = Join-Path $logDirectory "legacy-local-review-aspire-$timestamp.stdout.log"
$stderrPath = Join-Path $logDirectory "legacy-local-review-aspire-$timestamp.stderr.log"

$arguments = @(
    'run',
    '--project', $appHostProject,
    '--no-build',
    '--configuration', 'Release',
    '--no-launch-profile',
    "-p:LegacyMalievWebProject=$webProject"
)

$process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $appHostRoot `
    -Environment $environment -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath `
    -PassThru -WindowStyle Hidden

Start-Sleep -Milliseconds 750
if ($process.HasExited) {
    $errorOutput = if (Test-Path -LiteralPath $stderrPath) {
        (Get-Content -LiteralPath $stderrPath -Raw).Trim()
    } else {
        'No stderr log was created.'
    }
    throw "Legacy Aspire exited during startup with code $($process.ExitCode). $errorOutput"
}

[pscustomobject]@{
    ProcessId = $process.Id
    Dashboard = 'http://localhost:15888'
    WebCommit = $webCommit
    StandardOutput = $stdoutPath
    StandardError = $stderrPath
}
