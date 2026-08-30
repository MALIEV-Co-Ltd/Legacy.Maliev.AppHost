[CmdletBinding()]
param(
    [string] $WorkspaceRoot = 'B:\maliev-legacy',
    [ValidateRange(1, 65535)]
    [int] $WebPort = 5188
)

$ErrorActionPreference = 'Stop'

$appHostRoot = Split-Path -Parent $PSScriptRoot
$appHostProject = Join-Path $appHostRoot 'Legacy.Maliev.AppHost\Legacy.Maliev.AppHost.csproj'
$webRepositoryRoot = Join-Path $WorkspaceRoot 'Legacy.Maliev.Web'
$webProject = Join-Path $webRepositoryRoot 'Legacy.Maliev.Web\Legacy.Maliev.Web.csproj'

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
    LEGACY_LOCAL_SNAPSHOT = 'false'
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
