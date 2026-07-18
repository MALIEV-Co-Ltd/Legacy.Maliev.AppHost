param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Contract {
    param(
        [Parameter(Mandatory = $true)]
        [bool]$Condition,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-RequiredResource {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Manifest,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    Assert-Contract $Manifest.resources.ContainsKey($Name) "Manifest resource '$Name' is missing."
    return $Manifest.resources[$Name]
}

function Get-RequiredEnvironmentValue {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Resource,

        [Parameter(Mandatory = $true)]
        [string]$ResourceName,

        [Parameter(Mandatory = $true)]
        [string]$Key
    )

    Assert-Contract $Resource.ContainsKey('env') "Manifest resource '$ResourceName' has no environment contract."
    Assert-Contract $Resource.env.ContainsKey($Key) "Manifest resource '$ResourceName' is missing environment key '$Key'."
    $value = [string]$Resource.env[$Key]
    Assert-Contract (-not [string]::IsNullOrWhiteSpace($value)) "Manifest environment key '$Key' on '$ResourceName' is empty."
    return $value
}

$expectedPermissions = @(
    'legacy.documents.render',
    'legacy-file.uploads.create',
    'legacy-file.uploads.read',
    'legacy-file.uploads.delete',
    'legacy.notifications.send',
    'legacy-customer.customers.read',
    'legacy-employee.signatures.read',
    'legacy.quotations.read',
    'legacy.customer-quotations.read',
    'legacy.quotation-lines.read',
    'legacy.quotations.update',
    'legacy-employee.employees.read',
    'legacy-catalog.currencies.read',
    'legacy-catalog.countries.read'
)

$resolvedManifestPath = (Resolve-Path -LiteralPath $ManifestPath).Path
$manifest = Get-Content -LiteralPath $resolvedManifestPath -Raw | ConvertFrom-Json -AsHashtable
$authName = 'legacy-maliev-auth-service'
$accountingName = 'legacy-maliev-accounting-service'
$auth = Get-RequiredResource $manifest $authName
$accounting = Get-RequiredResource $manifest $accountingName

$clientId = Get-RequiredEnvironmentValue $accounting $accountingName 'ServiceAuthentication__ClientId'
Assert-Contract ($clientId -ceq 'legacy-accounting') "The Accounting service-auth client id must remain 'legacy-accounting'."
[void](Get-RequiredEnvironmentValue $accounting $accountingName 'ServiceAuthentication__ClientSecret')
[void](Get-RequiredEnvironmentValue $auth $authName 'ServiceClients__Clients__legacy-accounting__SecretSha256')

$permissionPrefix = 'ServiceClients__Clients__legacy-accounting__Permissions__'
$manifestPermissionKeys = @(
    $auth.env.Keys | Where-Object { $_.StartsWith($permissionPrefix, [StringComparison]::Ordinal) }
)
Assert-Contract ($manifestPermissionKeys.Count -eq $expectedPermissions.Count) 'The legacy-accounting Auth client must receive exactly fourteen permissions.'

for ($permissionIndex = 0; $permissionIndex -lt $expectedPermissions.Count; $permissionIndex++) {
    $key = "${permissionPrefix}${permissionIndex}"
    $actual = Get-RequiredEnvironmentValue $auth $authName $key
    Assert-Contract ($actual -ceq $expectedPermissions[$permissionIndex]) "Manifest permission '$key' does not match the approved exact grant."
    Assert-Contract (-not $actual.Contains('*', [StringComparison]::Ordinal)) "Manifest permission '$key' must not contain a wildcard."
}

Assert-Contract (-not $auth.env.ContainsKey("${permissionPrefix}14")) 'The legacy-accounting Auth client must not receive a fifteenth permission.'

Write-Output 'Accounting identity generated manifest contract verified.'
