[CmdletBinding()]
param(
    [string] $WebRepositoryRoot,
    [ValidateRange(1, 65535)]
    [int] $WebPort = 5088,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $PreflightOnly
)

$ErrorActionPreference = 'Stop'

function Invoke-Git {
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryRoot,
        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    $result = @(& git -C $RepositoryRoot @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed in ${RepositoryRoot}: $($result -join [Environment]::NewLine)"
    }

    return ($result -join [Environment]::NewLine).Trim()
}

function Get-PortOwner {
    param([int] $Port)

    $listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
    foreach ($listener in $listeners) {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)"
        [pscustomobject]@{
            Address = $listener.LocalAddress
            Port = $listener.LocalPort
            ProcessId = $listener.OwningProcess
            ParentProcessId = $process.ParentProcessId
            ExecutablePath = $process.ExecutablePath
            CommandLine = $process.CommandLine
            CreationDate = $process.CreationDate
        }
    }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$appHostProject = Join-Path $repositoryRoot 'Legacy.Maliev.AppHost\Legacy.Maliev.AppHost.csproj'
if (-not $WebRepositoryRoot) {
    $gitCommonDirectory = Invoke-Git -RepositoryRoot $repositoryRoot -Arguments @(
        'rev-parse', '--path-format=absolute', '--git-common-dir')
    $appHostCheckoutRoot = Split-Path -Parent $gitCommonDirectory
    $workspaceRoot = Split-Path -Parent $appHostCheckoutRoot
    $WebRepositoryRoot = Join-Path $workspaceRoot 'Legacy.Maliev.Web'
}

$WebRepositoryRoot = (Resolve-Path -LiteralPath $WebRepositoryRoot).Path
$webProject = Join-Path $WebRepositoryRoot 'Legacy.Maliev.Web\Legacy.Maliev.Web.csproj'
if (-not (Test-Path -LiteralPath $webProject -PathType Leaf)) {
    throw "Legacy Web source .csproj was not found at $webProject. A .worktrees/*/bin output is never accepted."
}
if ($webProject -match '[\\/](bin|obj)[\\/]') {
    throw "Legacy Web project must be a source .csproj, never a bin or obj output: $webProject"
}

$dirtyBeforeBuild = Invoke-Git -RepositoryRoot $WebRepositoryRoot -Arguments @('status', '--porcelain')
if ($dirtyBeforeBuild) {
    throw "Legacy Web checkout must be clean before the deterministic build:`n$dirtyBeforeBuild"
}

$branch = Invoke-Git -RepositoryRoot $WebRepositoryRoot -Arguments @('branch', '--show-current')
if (-not $branch) {
    throw 'Legacy Web checkout must be on a named branch, not detached HEAD.'
}
$commitBeforeBuild = Invoke-Git -RepositoryRoot $WebRepositoryRoot -Arguments @('rev-parse', 'HEAD')
$repository = Invoke-Git -RepositoryRoot $WebRepositoryRoot -Arguments @('remote', 'get-url', 'origin')

$env:LEGACY_WEB_PROJECT = $webProject
$env:LEGACY_WEB_REPOSITORY = $repository
$env:LEGACY_WEB_BRANCH = $branch
$env:LEGACY_WEB_COMMIT = $commitBeforeBuild
$env:LEGACY_WEB_PORT = $WebPort.ToString([Globalization.CultureInfo]::InvariantCulture)
[Environment]::SetEnvironmentVariable('Parameters__legacy-postgres-username', 'legacy_local')
[Environment]::SetEnvironmentVariable('Parameters__legacy-postgres-password', [guid]::NewGuid().ToString('N'))
[Environment]::SetEnvironmentVariable('Parameters__legacy-redis-password', [guid]::NewGuid().ToString('N'))

Write-Host "Building Legacy Web source before port inspection: repo=$repository branch=$branch commit=$commitBeforeBuild project=$webProject"
& dotnet build $appHostProject --configuration $Configuration --verbosity minimal `
    "-p:LegacyMalievWebProject=$webProject"
if ($LASTEXITCODE -ne 0) {
    throw 'The exact Legacy Web replacement build failed; the current listener remains untouched.'
}

$commitAfterBuild = Invoke-Git -RepositoryRoot $WebRepositoryRoot -Arguments @('rev-parse', 'HEAD')
$dirtyAfterBuild = Invoke-Git -RepositoryRoot $WebRepositoryRoot -Arguments @('status', '--porcelain')
if ($commitAfterBuild -ne $commitBeforeBuild -or $dirtyAfterBuild) {
    throw 'Legacy Web source changed during compilation. The replacement will not start.'
}

$portOwners = @(Get-PortOwner -Port $WebPort)
if ($portOwners.Count -gt 0) {
    $details = $portOwners | Format-List | Out-String
    throw "Port $WebPort is occupied. The verified replacement is compiled at commit $commitAfterBuild, but this script does not terminate the owner. Coordinate the cutover, then rerun.`n$details"
}

Write-Host "PASS: exact Legacy Web source is compiled and port $WebPort is available. commit=$commitAfterBuild"
if ($PreflightOnly) {
    return
}

& dotnet run --project $appHostProject --configuration $Configuration --launch-profile http --no-build `
    "-p:LegacyMalievWebProject=$webProject"
if ($LASTEXITCODE -ne 0) {
    throw "Legacy Aspire exited with code $LASTEXITCODE."
}
