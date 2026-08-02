[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$SnapshotDirectory,

    [string]$WorkspaceRoot = 'B:\maliev',
    [string]$AppHostProject = '',
    [string]$LegacyWebProject = '',
    [string]$LegacyWebRepository = '',
    [int]$LegacyWebPort = 5188,
    [string]$PostgresPassword = '',
    [string]$RedisPassword = '',
    [switch]$Wait
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $SnapshotDirectory -PathType Container)) {
    throw "Snapshot directory does not exist: $SnapshotDirectory"
}

$appHostRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($AppHostProject)) {
    $AppHostProject = Join-Path $appHostRoot 'Legacy.Maliev.AppHost\Legacy.Maliev.AppHost.csproj'
}

if ([string]::IsNullOrWhiteSpace($LegacyWebProject)) {
    $LegacyWebProject = Join-Path $WorkspaceRoot 'Legacy.Maliev.Web\Legacy.Maliev.Web\Legacy.Maliev.Web.csproj'
}

if ([string]::IsNullOrWhiteSpace($LegacyWebRepository)) {
    $LegacyWebRepository = Join-Path $WorkspaceRoot 'Legacy.Maliev.Web'
}

if (-not (Test-Path -LiteralPath $LegacyWebProject -PathType Leaf)) {
    throw "Legacy Web project does not exist: $LegacyWebProject"
}

if ([string]::IsNullOrWhiteSpace($PostgresPassword)) {
    $PostgresPassword = [Guid]::NewGuid().ToString('N')
}

if ([string]::IsNullOrWhiteSpace($RedisPassword)) {
    $RedisPassword = [Guid]::NewGuid().ToString('N')
}

$legacyWebBranch = (& git -C $LegacyWebRepository branch --show-current).Trim()
$legacyWebCommit = (& git -C $LegacyWebRepository rev-parse HEAD).Trim()
if ([string]::IsNullOrWhiteSpace($legacyWebBranch) -or [string]::IsNullOrWhiteSpace($legacyWebCommit)) {
    throw "Legacy Web must be checked out on a named branch with a resolvable commit."
}

$environment = @{
    MalievWorkspaceRoot = $WorkspaceRoot
    LEGACY_LOCAL_SNAPSHOT = 'true'
    LEGACY_LOCAL_SNAPSHOT_DIR = $SnapshotDirectory
    LEGACY_LOCAL_FIXTURES = 'true'
    LEGACY_WEB_PROJECT = $LegacyWebProject
    LEGACY_WEB_REPOSITORY = $LegacyWebRepository
    LEGACY_WEB_BRANCH = $legacyWebBranch
    LEGACY_WEB_COMMIT = $legacyWebCommit
    LEGACY_WEB_PORT = $LegacyWebPort.ToString()
    ASPIRE_ALLOW_UNSECURED_TRANSPORT = 'true'
    ASPNETCORE_ENVIRONMENT = 'Development'
    'Parameters__legacy-postgres-username' = 'postgres'
    'Parameters__legacy-postgres-password' = $PostgresPassword
    'Parameters__legacy-redis-password' = $RedisPassword
    'Parameters__legacy-web-google-maps-embed-api-key' = 'local-review-only-map-key'
    'Parameters__legacy-intranet-google-maps-browser-api-key' = 'local-review-only-map-key'
}

$stdoutPath = Join-Path $env:TEMP 'maliev-legacy-local-snapshot-aspire.stdout.log'
$stderrPath = Join-Path $env:TEMP 'maliev-legacy-local-snapshot-aspire.stderr.log'
Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue

$arguments = @(
    'run',
    '--project', $AppHostProject,
    '--no-build',
    '--configuration', 'Release',
    '--no-launch-profile'
)

$process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $appHostRoot `
    -Environment $environment -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath `
    -PassThru -WindowStyle Hidden

Write-Output "Started Legacy Aspire local snapshot mode (PID $($process.Id))."
Write-Output "Dashboard output: $stdoutPath"
Write-Output "Error output: $stderrPath"
Write-Output "Snapshot: $SnapshotDirectory"
Write-Output "Legacy Web branch: $legacyWebBranch ($legacyWebCommit)"

if ($Wait) {
    $process.WaitForExit()
    exit $process.ExitCode
}
