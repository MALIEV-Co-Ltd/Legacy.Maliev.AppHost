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

$resolvedManifestPath = (Resolve-Path -LiteralPath $ManifestPath).Path
$manifest = Get-Content -LiteralPath $resolvedManifestPath -Raw | ConvertFrom-Json -AsHashtable
$bffName = 'legacy-maliev-intranet-bff'
$compatibilityName = 'legacy-maliev-intranet'
$authName = 'legacy-maliev-auth-service'
$bff = Get-RequiredResource $manifest $bffName
$auth = Get-RequiredResource $manifest $authName

# The Razor Pages compatibility host is intentionally dormant locally (see the NOTE above the
# Bff resource in AppHost.cs) and is no longer emitted into the generated manifest. If it's ever
# re-added as a live resource, require it to keep reusing the shared legacy-intranet credential.
if ($manifest.resources.ContainsKey($compatibilityName)) {
    $compatibility = $manifest.resources[$compatibilityName]
    $bffSecret = Get-RequiredEnvironmentValue $bff $bffName 'ServiceAuthentication__ClientSecret'
    $compatibilitySecret = Get-RequiredEnvironmentValue $compatibility $compatibilityName 'ServiceAuthentication__ClientSecret'
    Assert-Contract ($bffSecret -ceq $compatibilitySecret) 'The Intranet BFF must reuse the existing in-memory legacy-intranet credential.'
}

$clientId = Get-RequiredEnvironmentValue $bff $bffName 'ServiceAuthentication__ClientId'
Assert-Contract ($clientId -ceq 'legacy-intranet') "The Intranet BFF service-auth client id must remain 'legacy-intranet'."

$catalogBinding = '{legacy-maliev-catalog-service.bindings.http.url}'
$catalogEndpoint = Get-RequiredEnvironmentValue $bff $bffName 'Services__Catalog'
$catalogReference = Get-RequiredEnvironmentValue $bff $bffName 'services__legacy-maliev-catalog-service__http__0'
Assert-Contract ($catalogEndpoint -ceq $catalogBinding) 'Services__Catalog must reference the existing Catalog HTTP endpoint.'
Assert-Contract ($catalogReference -ceq $catalogBinding) 'The Intranet BFF must retain an Aspire Catalog service reference.'

foreach ($requiredKey in @(
    'Jwt__PublicKey',
    'Jwt__Issuer',
    'Jwt__Audience',
    'Jwt__KeyId',
    'DataProtection__CertificatePfxBase64',
    'DataProtection__CertificatePassword'
)) {
    [void](Get-RequiredEnvironmentValue $bff $bffName $requiredKey)
}

Assert-Contract (-not $bff.env.ContainsKey('Jwt__PrivateKeyPem')) 'The Intranet BFF must never receive the JWT private signing key.'

$intranetPermissionPrefix = 'ServiceClients__Clients__legacy-intranet__Permissions__'
$intranetPermissions = @(
    $auth.env.GetEnumerator() |
        Where-Object { $_.Key.StartsWith($intranetPermissionPrefix, [StringComparison]::Ordinal) } |
        ForEach-Object { [string]$_.Value }
)
Assert-Contract ($intranetPermissions -ccontains 'legacy-catalog.materials.read') 'The existing legacy-intranet Auth client allowlist must grant legacy-catalog.materials.read.'

Write-Output 'Intranet BFF generated manifest contract verified.'
