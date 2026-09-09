[CmdletBinding()]
param(
    [string] $WorkspaceRoot = 'B:\maliev-legacy',
    [string] $PostgresCredentialFile = (Join-Path $env:LOCALAPPDATA 'Maliev\legacy-delta\runs\20260908-persistent\target.runtime'),
    [ValidateRange(1, 65535)]
    [int] $WebPort = 5188
)

$ErrorActionPreference = 'Stop'

function Assert-OwnerOnlyFile {
    param([Parameter(Mandatory)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Protected PostgreSQL credential file does not exist: $Path"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Protected PostgreSQL credential file cannot be a reparse point: $Path"
    }
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = Get-Acl -LiteralPath $Path
    if ($acl.Owner -ne $owner.Value -and $acl.Owner -ne "$env:USERDOMAIN\$env:USERNAME") {
        throw "Protected PostgreSQL credential file is not owned by the current user: $Path"
    }
    $foreignAllow = $acl.Access | Where-Object {
        $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
        $_.IdentityReference.Value -ne $owner.Value -and
        $_.IdentityReference.Value -ne "$env:USERDOMAIN\$env:USERNAME"
    }
    if ($foreignAllow) {
        throw "Protected PostgreSQL credential file grants access to another identity: $Path"
    }
}

$appHostRoot = Split-Path -Parent $PSScriptRoot
$appHostProject = Join-Path $appHostRoot 'Legacy.Maliev.AppHost\Legacy.Maliev.AppHost.csproj'
$webRepositoryRoot = Join-Path $WorkspaceRoot 'Legacy.Maliev.Web'
$webProject = Join-Path $webRepositoryRoot 'Legacy.Maliev.Web\Legacy.Maliev.Web.csproj'

Assert-OwnerOnlyFile -Path $PostgresCredentialFile
$connection = [Data.Common.DbConnectionStringBuilder]::new()
$connection.set_ConnectionString((Get-Content -LiteralPath $PostgresCredentialFile -Raw).Trim())
if ($connection.ContainsKey('ConnectionString')) {
    $innerConnection = [Data.Common.DbConnectionStringBuilder]::new()
    $innerConnection.set_ConnectionString([string]$connection['ConnectionString'])
    $connection = $innerConnection
}
$postgresUsername = if ($connection.ContainsKey('Username')) { [string]$connection['Username'] } elseif ($connection.ContainsKey('User ID')) { [string]$connection['User ID'] } else { '' }
$postgresPassword = if ($connection.ContainsKey('Password')) { [string]$connection['Password'] } else { '' }
if ([string]::IsNullOrWhiteSpace($postgresUsername) -or [string]::IsNullOrWhiteSpace($postgresPassword)) {
    throw 'Protected PostgreSQL credential file does not contain a username and password.'
}

& docker volume inspect 'legacy-maliev-exact23-postgres-data' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'The reconciled exact-23 PostgreSQL volume is not installed locally.'
}
if (Get-NetTCPConnection -LocalPort 15888 -State Listen -ErrorAction SilentlyContinue) {
    throw 'Local port 15888 is already in use. Stop the existing Aspire dashboard first.'
}

& dotnet build $appHostProject --configuration Release
if ($LASTEXITCODE -ne 0) {
    throw 'The Legacy Aspire solution did not build.'
}

$webBranch = (& git -C $webRepositoryRoot branch --show-current).Trim()
$webCommit = (& git -C $webRepositoryRoot rev-parse HEAD).Trim()
$webRepository = (& git -C $webRepositoryRoot remote get-url origin).Trim()
if ($LASTEXITCODE -ne 0 -or $webBranch -ne 'main' -or $webCommit -notmatch '^[0-9a-f]{40}$' -or
    [string]::IsNullOrWhiteSpace($webRepository)) {
    throw 'Legacy Web must be the canonical main checkout with a valid origin identity.'
}

$environment = @{
    MalievWorkspaceRoot = $WorkspaceRoot
    LEGACY_GKE_VALIDATION = 'false'
    LEGACY_LOCAL_SNAPSHOT = 'false'
    LEGACY_LOCAL_DELTA = 'false'
    LEGACY_LOCAL_DELTA_REVIEW = 'true'
    LEGACY_LOCAL_FIXTURES = 'false'
    LEGACY_WEB_PROJECT = $webProject
    LEGACY_WEB_REPOSITORY = $webRepository
    LEGACY_WEB_BRANCH = $webBranch
    LEGACY_WEB_COMMIT = $webCommit
    LEGACY_WEB_PORT = $WebPort.ToString([Globalization.CultureInfo]::InvariantCulture)
    ASPIRE_ALLOW_UNSECURED_TRANSPORT = 'true'
    ASPNETCORE_ENVIRONMENT = 'Development'
    ASPNETCORE_URLS = 'http://localhost:15888'
    'Parameters__legacy-postgres-username' = $postgresUsername
    'Parameters__legacy-postgres-password' = $postgresPassword
    'Parameters__legacy-redis-password' = [Guid]::NewGuid().ToString('N')
    'Parameters__legacy-web-google-maps-embed-api-key' = 'local-review-unconfigured'
    'Parameters__legacy-intranet-google-maps-browser-api-key' = 'local-review-unconfigured'
}

$logDirectory = Join-Path $env:LOCALAPPDATA 'Maliev\logs'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stdoutPath = Join-Path $logDirectory "legacy-local-delta-review-$timestamp.stdout.log"
$stderrPath = Join-Path $logDirectory "legacy-local-delta-review-$timestamp.stderr.log"
$arguments = @(
    'run', '--project', $appHostProject, '--no-build', '--configuration', 'Release', '--no-launch-profile',
    "-p:LegacyMalievWebProject=$webProject"
)

$process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $appHostRoot `
    -Environment $environment -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath `
    -PassThru -WindowStyle Hidden

Start-Sleep -Milliseconds 750
if ($process.HasExited) {
    $errorOutput = if (Test-Path -LiteralPath $stderrPath) { (Get-Content -LiteralPath $stderrPath -Raw).Trim() } else { 'No stderr log was created.' }
    throw "Legacy Aspire exited during startup with code $($process.ExitCode). $errorOutput"
}

[pscustomobject]@{
    ProcessId = $process.Id
    Dashboard = 'http://localhost:15888'
    WebCommit = $webCommit
    StandardOutput = $stdoutPath
    StandardError = $stderrPath
}
