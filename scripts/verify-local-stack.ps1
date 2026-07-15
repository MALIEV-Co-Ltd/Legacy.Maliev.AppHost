[CmdletBinding()]
param(
    [ValidateRange(30, 600)]
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$appHostProject = Join-Path $repositoryRoot 'Legacy.Maliev.AppHost\Legacy.Maliev.AppHost.csproj'
$stdoutPath = Join-Path $env:TEMP "legacy-maliev-apphost-$PID.stdout.log"
$stderrPath = Join-Path $env:TEMP "legacy-maliev-apphost-$PID.stderr.log"
$appHostRunner = $null
$localContainerNames = @()

$parameterNames = @(
    'Parameters__legacy-postgres-username',
    'Parameters__legacy-postgres-password',
    'Parameters__legacy-redis-password',
    'GITHUB_ACTIONS'
)
$previousEnvironment = @{}
foreach ($parameterName in $parameterNames) {
    $previousEnvironment[$parameterName] = [Environment]::GetEnvironmentVariable($parameterName)
}

function Get-DcpKubeconfig {
    param([int]$AppHostProcessId)

    $dcp = Get-CimInstance Win32_Process | Where-Object {
        $_.Name -eq 'dcp.exe' -and
        $_.CommandLine -like '*start-apiserver*' -and
        $_.CommandLine -like "*--monitor $AppHostProcessId*"
    } | Select-Object -First 1

    if (-not $dcp) {
        return $null
    }

    $match = [regex]::Match($dcp.CommandLine, '--kubeconfig\s+"?(.+?)"?\s+--tls-cert')
    if (-not $match.Success) {
        throw 'The local DCP kubeconfig path could not be parsed.'
    }

    return $match.Groups[1].Value
}

function Invoke-ExpectedStatus {
    param(
        [string]$Uri,
        [int[]]$ExpectedStatus
    )

    $response = Invoke-WebRequest -Uri $Uri -UseBasicParsing -SkipHttpErrorCheck
    if ($response.StatusCode -notin $ExpectedStatus) {
        throw "Unexpected HTTP $($response.StatusCode) from $Uri. Expected: $($ExpectedStatus -join ', ')."
    }
}

function Invoke-ExpectedPostStatus {
    param(
        [string]$Uri,
        [int[]]$ExpectedStatus,
        [string]$Body = '{}'
    )

    $response = Invoke-WebRequest -Uri $Uri -Method Post -ContentType 'application/json' `
        -Body $Body -UseBasicParsing -SkipHttpErrorCheck
    if ($response.StatusCode -notin $ExpectedStatus) {
        throw "Unexpected HTTP $($response.StatusCode) from POST $Uri. Expected: $($ExpectedStatus -join ', '). Response: $($response.Content)"
    }
}

function Invoke-WebMemberAddressFlow {
    param([string]$WebUrl)

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    try {
        $loginPage = $client.GetAsync("$WebUrl/Account/Login").GetAwaiter().GetResult()
        $loginPageContent = $loginPage.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ([int]$loginPage.StatusCode -ne 200) {
            throw "The Web login page returned HTTP $([int]$loginPage.StatusCode)."
        }

        $antiforgery = [regex]::Match(
            $loginPageContent,
            'name="__RequestVerificationToken"[^>]*value="([^"]+)"')
        if (-not $antiforgery.Success) {
            throw 'The Web login form did not expose an antiforgery token.'
        }

        $antiforgeryCookie = @($loginPage.Headers.GetValues('Set-Cookie') | ForEach-Object {
            ($_ -split ';', 2)[0]
        }) -join '; '
        if (-not $antiforgeryCookie) {
            throw 'The Web login form did not issue an antiforgery cookie.'
        }

        $loginRequest = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::Post,
            "$WebUrl/Account/Login?handler=Login")
        $null = $loginRequest.Headers.TryAddWithoutValidation('Cookie', $antiforgeryCookie)
        $loginForm = [System.Collections.Generic.Dictionary[string,string]]::new()
        $loginForm.Add(
            '__RequestVerificationToken',
            [System.Net.WebUtility]::HtmlDecode($antiforgery.Groups[1].Value))
        $loginForm.Add('Email', 'local.customer@maliev.test')
        $loginForm.Add('Password', 'local-test-only')
        $loginForm.Add('RememberMe', 'false')
        $loginRequest.Content = [System.Net.Http.FormUrlEncodedContent]::new($loginForm)
        $login = $client.SendAsync($loginRequest).GetAwaiter().GetResult()
        if ([int]$login.StatusCode -notin 302, 303) {
            throw "The Web BFF login returned HTTP $([int]$login.StatusCode)."
        }

        $sessionCookie = @($login.Headers.GetValues('Set-Cookie') | ForEach-Object {
            ($_ -split ';', 2)[0]
        }) -join '; '
        if (-not $sessionCookie) {
            throw 'The Web BFF login did not issue an encrypted session cookie.'
        }

        $addressRequest = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::Get,
            "$WebUrl/member/account/manage/address?culture=en")
        $null = $addressRequest.Headers.TryAddWithoutValidation(
            'Cookie',
            "$antiforgeryCookie; $sessionCookie")
        $address = $client.SendAsync($addressRequest).GetAwaiter().GetResult()
        $addressContent = $address.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ([int]$address.StatusCode -ne 200 -or $addressContent -notmatch 'name="BillingAddress1"') {
            throw 'The authenticated Member address boundary did not render through the Web BFF.'
        }
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }
}

function Get-SingleResource {
    param(
        [object[]]$Items,
        [string]$NamePattern
    )

    $matches = @($Items | Where-Object { $_.metadata.name -like $NamePattern })
    if ($matches.Count -ne 1) {
        throw "Expected one resource matching '$NamePattern', found $($matches.Count)."
    }

    return $matches[0]
}

function Get-ResourceUrl {
    param([object]$Resource)

    $url = ($Resource.status.effectiveEnv | Where-Object {
        $_.name -eq 'ASPNETCORE_URLS'
    }).value
    if (-not $url) {
        throw "The URL for $($Resource.metadata.name) was not exported by the local orchestrator."
    }

    return $url
}

try {
    foreach ($commandName in @('docker', 'dotnet', 'kubectl')) {
        if (-not (Get-Command $commandName -ErrorAction SilentlyContinue)) {
            throw "Required command '$commandName' was not found."
        }
    }

    if (Get-NetTCPConnection -LocalPort 15888 -State Listen -ErrorAction SilentlyContinue) {
        throw 'Local port 15888 is already in use. Stop the existing Aspire dashboard first.'
    }

    & docker info --format '{{.ServerVersion}}' | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Docker is not available.'
    }

    $env:GITHUB_ACTIONS = 'false'
    & dotnet build $appHostProject --configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw 'The local Aspire solution did not build.'
    }
    $topologyAssembly = Join-Path $repositoryRoot `
        'Legacy.Maliev.AppHost.Topology\bin\Release\net10.0\Legacy.Maliev.AppHost.Topology.dll'
    [System.Reflection.Assembly]::LoadFrom($topologyAssembly) | Out-Null

    [Environment]::SetEnvironmentVariable('Parameters__legacy-postgres-username', 'legacy_local')
    [Environment]::SetEnvironmentVariable('Parameters__legacy-postgres-password', [guid]::NewGuid().ToString('N'))
    [Environment]::SetEnvironmentVariable('Parameters__legacy-redis-password', [guid]::NewGuid().ToString('N'))

    $appHostRunner = Start-Process -FilePath 'dotnet' `
        -ArgumentList @(
            'run',
            '--project', $appHostProject,
            '--configuration', 'Release',
            '--launch-profile', 'http',
            '--no-build'
        ) `
        -WorkingDirectory $repositoryRoot `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -WindowStyle Hidden `
        -PassThru

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $resourceItems = @()
    $kubeconfig = $null
    $migrationPatterns = @(
        'legacy-country-migrations-*',
        'legacy-auth-migrations-*',
        'legacy-customer-identity-migrations-*',
        'legacy-employee-identity-migrations-*',
        'legacy-customer-migrations-*'
    )
    $servicePatterns = @(
        'legacy-maliev-country-service-*',
        'legacy-maliev-document-service-*',
        'legacy-maliev-auth-service-*',
        'legacy-maliev-customer-service-*',
        'legacy-maliev-notification-service-*',
        'legacy-maliev-web-*'
    )

    while ((Get-Date) -lt $deadline) {
        if ($appHostRunner.HasExited) {
            throw "The local AppHost exited with code $($appHostRunner.ExitCode)."
        }

        $appHost = Get-CimInstance Win32_Process | Where-Object {
            $_.Name -eq 'Legacy.Maliev.AppHost.exe' -and
            $_.ParentProcessId -eq $appHostRunner.Id
        } | Select-Object -First 1

        if ($appHost) {
            $kubeconfig = Get-DcpKubeconfig -AppHostProcessId $appHost.ProcessId
        }

        if ($kubeconfig -and (Test-Path -LiteralPath $kubeconfig)) {
            $resourceJson = & kubectl --kubeconfig $kubeconfig get containers,executables -o json 2>$null
            if ($LASTEXITCODE -eq 0 -and $resourceJson) {
                $resourceItems = @((ConvertFrom-Json ($resourceJson -join "`n")).items)
                $migrationResources = @(
                    foreach ($pattern in $migrationPatterns) {
                        $resourceItems | Where-Object { $_.metadata.name -like $pattern }
                    }
                )
                $migrationsSucceeded = $migrationResources.Count -eq $migrationPatterns.Count -and
                    @($migrationResources | Where-Object {
                        $_.status.state -ne 'Finished' -or $_.status.exitCode -ne 0
                    }).Count -eq 0
                $unhealthy = @($resourceItems | Where-Object {
                    $_.metadata.name -notlike 'legacy-*-migrations-*' -and
                    $_.status.healthStatus -ne 'Healthy'
                })
                $servicesPresent = @(
                    foreach ($pattern in $servicePatterns) {
                        @($resourceItems | Where-Object { $_.metadata.name -like $pattern }).Count -eq 1
                    }
                ) -notcontains $false
                if (
                    $resourceItems.Count -ge 13 -and
                    $servicesPresent -and
                    $migrationsSucceeded -and
                    $unhealthy.Count -eq 0
                ) {
                    break
                }
            }
        }

        Start-Sleep -Milliseconds 500
    }

    if ($resourceItems.Count -lt 11) {
        throw 'The local Aspire resources did not become observable before the timeout.'
    }

    foreach ($pattern in $migrationPatterns) {
        $migrationResource = Get-SingleResource -Items $resourceItems -NamePattern $pattern
        if ($migrationResource.status.state -ne 'Finished' -or $migrationResource.status.exitCode -ne 0) {
            throw "The migration resource $($migrationResource.metadata.name) did not complete successfully."
        }
    }

    $unhealthy = @($resourceItems | Where-Object {
        $_.metadata.name -notlike 'legacy-*-migrations-*' -and
        $_.status.healthStatus -ne 'Healthy'
    })
    if ($unhealthy.Count -gt 0) {
        throw "Resources failed health validation: $($unhealthy.metadata.name -join ', ')."
    }

    $localContainerNames = @(
        $resourceItems |
            Where-Object { $_.kind -eq 'Container' } |
            ForEach-Object { $_.metadata.name }
    )

    $ambientCredentialNames = @(
        $resourceItems.status.effectiveEnv.name |
            Sort-Object -Unique |
            Where-Object {
                $_ -match '(TOKEN|API.?KEY|PASSWORD|SECRET|CREDENTIAL|BW_SESSION|NUGET_PASSWORD)' -and
                $_ -notlike 'Parameters__legacy-*' -and
                $_ -notin @(
                    'POSTGRES_PASSWORD',
                    'REDIS_PASSWORD',
                    'DASHBOARD__API__PRIMARYAPIKEY',
                    'DASHBOARD__OTLP__PRIMARYAPIKEY',
                    'DASHBOARD__RESOURCESERVICECLIENT__APIKEY',
                    'DASHBOARD__FRONTEND__BROWSERTOKEN',
                    'OTEL_EXPORTER_OTLP_HEADERS',
                    'ServiceAuthentication__ClientSecret',
                    'ServiceClients__Clients__legacy-web__SecretSha256',
                    'DataProtection__CertificatePassword',
                    'Brevo__ApiKey'
                )
            }
    )
    if ($ambientCredentialNames.Count -gt 0) {
        throw "Ambient credential variables reached local resources: $($ambientCredentialNames -join ', ')."
    }

    $countryResource = Get-SingleResource -Items $resourceItems -NamePattern 'legacy-maliev-country-service-*'
    $countryUrl = Get-ResourceUrl -Resource $countryResource

    Invoke-ExpectedStatus -Uri "$countryUrl/countries/liveness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$countryUrl/countries/readiness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$countryUrl/countries/scalar" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$countryUrl/Countries" -ExpectedStatus 200, 404

    $documentResource = Get-SingleResource -Items $resourceItems -NamePattern 'legacy-maliev-document-service-*'
    $documentUrl = Get-ResourceUrl -Resource $documentResource

    Invoke-ExpectedStatus -Uri "$documentUrl/documents/liveness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$documentUrl/documents/readiness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$documentUrl/documents/scalar" -ExpectedStatus 200
    Invoke-ExpectedPostStatus -Uri "$documentUrl/Pdfs/invoice" -ExpectedStatus 401

    $authResource = Get-SingleResource -Items $resourceItems -NamePattern 'legacy-maliev-auth-service-*'
    $authUrl = Get-ResourceUrl -Resource $authResource
    Invoke-ExpectedStatus -Uri "$authUrl/auth/liveness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$authUrl/auth/readiness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$authUrl/auth/scalar" -ExpectedStatus 200
    Invoke-ExpectedPostStatus -Uri "$authUrl/auth/v1/service/login" -ExpectedStatus 400
    Invoke-ExpectedPostStatus -Uri "$authUrl/auth/v1/login" -ExpectedStatus 200 -Body (@{
            userName = 'local.customer@maliev.test'
            password = 'local-test-only'
            identityKind = 0
        } | ConvertTo-Json -Compress)
    Invoke-ExpectedPostStatus -Uri "$authUrl/auth/v1/login" -ExpectedStatus 200 -Body (@{
            userName = 'local.employee@maliev.test'
            password = 'local-test-only'
            identityKind = 1
        } | ConvertTo-Json -Compress)

    $customerResource = Get-SingleResource -Items $resourceItems -NamePattern 'legacy-maliev-customer-service-*'
    $customerUrl = Get-ResourceUrl -Resource $customerResource
    Invoke-ExpectedStatus -Uri "$customerUrl/customer/liveness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$customerUrl/customer/readiness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$customerUrl/customer/scalar" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$customerUrl/customers" -ExpectedStatus 401

    $notificationResource = Get-SingleResource -Items $resourceItems -NamePattern 'legacy-maliev-notification-service-*'
    $notificationUrl = Get-ResourceUrl -Resource $notificationResource
    Invoke-ExpectedStatus -Uri "$notificationUrl/emails/liveness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$notificationUrl/emails/readiness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$notificationUrl/emails/scalar" -ExpectedStatus 200
    Invoke-ExpectedPostStatus -Uri "$notificationUrl/notifications/v1/email/noreply" -ExpectedStatus 401

    $webResource = Get-SingleResource -Items $resourceItems -NamePattern 'legacy-maliev-web-*'
    $webUrl = Get-ResourceUrl -Resource $webResource
    Invoke-ExpectedStatus -Uri "$webUrl/web/liveness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$webUrl/web/readiness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$webUrl/Account/Login" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$webUrl/Account/Signup" -ExpectedStatus 200
    Invoke-WebMemberAddressFlow -WebUrl $webUrl

    $postgresContainer = $resourceItems | Where-Object {
        $_.metadata.name -like 'legacy-postgres-main-*'
    } | Select-Object -First 1
    $actualDatabases = @(
        & docker exec $postgresContainer.metadata.name sh -lc `
            'PGPASSWORD="$POSTGRES_PASSWORD" psql -U "$POSTGRES_USER" -d postgres -Atc "select datname from pg_database where datistemplate = false order by datname;"'
    )
    if ($LASTEXITCODE -ne 0) {
        throw 'PostgreSQL database topology could not be queried.'
    }

    $requiredDatabases = @([Legacy.Maliev.AppHost.Topology.LegacyTopology]::DatabaseNames) + 'Auth'
    $missingDatabases = @(
        $requiredDatabases |
            Where-Object { $_ -notin $actualDatabases }
    )
    if ($missingDatabases.Count -gt 0) {
        throw "Missing legacy databases: $($missingDatabases -join ', ')."
    }

    Write-Host 'PASS: PostgreSQL, Redis, six services, five migrations, 21 preserved databases plus Auth runtime state, customer/employee login, authenticated Member address BFF, protected boundaries, and environment isolation are healthy.'
}
finally {
    if ($appHostRunner -and -not $appHostRunner.HasExited) {
        Stop-Process -Id $appHostRunner.Id -Force -ErrorAction SilentlyContinue
    }

    Start-Sleep -Milliseconds 500
    foreach ($containerName in $localContainerNames) {
        & docker rm -f $containerName 2>$null | Out-Null
    }

    foreach ($parameterName in $parameterNames) {
        [Environment]::SetEnvironmentVariable($parameterName, $previousEnvironment[$parameterName])
    }

    Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
}
