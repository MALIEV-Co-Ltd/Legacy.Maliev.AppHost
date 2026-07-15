[CmdletBinding()]
param(
    [ValidateRange(30, 600)]
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot 'Legacy.Maliev.AppHost.slnx'
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
        [int[]]$ExpectedStatus
    )

    $response = Invoke-WebRequest -Uri $Uri -Method Post -ContentType 'application/json' `
        -Body '{}' -UseBasicParsing -SkipHttpErrorCheck
    if ($response.StatusCode -notin $ExpectedStatus) {
        throw "Unexpected HTTP $($response.StatusCode) from POST $Uri. Expected: $($ExpectedStatus -join ', ')."
    }
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
    & dotnet build $solutionPath --configuration Release
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
                $migrationResource = $resourceItems | Where-Object {
                    $_.metadata.name -like 'legacy-country-migrations-*'
                } | Select-Object -First 1
                $migrationSucceeded = $migrationResource.status.state -eq 'Finished' -and
                    $migrationResource.status.exitCode -eq 0
                $unhealthy = @($resourceItems | Where-Object {
                    $_.metadata.name -notlike 'legacy-country-migrations-*' -and
                    $_.status.healthStatus -ne 'Healthy'
                })
                $countryIsPresent = @($resourceItems | Where-Object {
                    $_.metadata.name -like 'legacy-maliev-country-service-*'
                }).Count -eq 1
                $documentIsPresent = @($resourceItems | Where-Object {
                    $_.metadata.name -like 'legacy-maliev-document-service-*'
                }).Count -eq 1
                if (
                    $resourceItems.Count -ge 6 -and
                    $countryIsPresent -and
                    $documentIsPresent -and
                    $migrationSucceeded -and
                    $unhealthy.Count -eq 0
                ) {
                    break
                }
            }
        }

        Start-Sleep -Milliseconds 500
    }

    if ($resourceItems.Count -lt 6) {
        throw 'The local Aspire resources did not become observable before the timeout.'
    }

    $migrationResource = $resourceItems | Where-Object {
        $_.metadata.name -like 'legacy-country-migrations-*'
    } | Select-Object -First 1
    if ($migrationResource.status.state -ne 'Finished' -or $migrationResource.status.exitCode -ne 0) {
        throw 'The Country schema migration did not complete successfully.'
    }

    $unhealthy = @($resourceItems | Where-Object {
        $_.metadata.name -notlike 'legacy-country-migrations-*' -and
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
                    'OTEL_EXPORTER_OTLP_HEADERS'
                )
            }
    )
    if ($ambientCredentialNames.Count -gt 0) {
        throw "Ambient credential variables reached local resources: $($ambientCredentialNames -join ', ')."
    }

    $countryResource = $resourceItems | Where-Object {
        $_.metadata.name -like 'legacy-maliev-country-service-*'
    } | Select-Object -First 1
    $countryUrl = ($countryResource.status.effectiveEnv | Where-Object {
        $_.name -eq 'ASPNETCORE_URLS'
    }).value
    if (-not $countryUrl) {
        throw 'The Country service URL was not exported by the local orchestrator.'
    }

    Invoke-ExpectedStatus -Uri "$countryUrl/countries/liveness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$countryUrl/countries/readiness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$countryUrl/countries/scalar" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$countryUrl/Countries" -ExpectedStatus 200, 404

    $documentResource = $resourceItems | Where-Object {
        $_.metadata.name -like 'legacy-maliev-document-service-*'
    } | Select-Object -First 1
    $documentUrl = ($documentResource.status.effectiveEnv | Where-Object {
        $_.name -eq 'ASPNETCORE_URLS'
    }).value
    if (-not $documentUrl) {
        throw 'The Document service URL was not exported by the local orchestrator.'
    }

    Invoke-ExpectedStatus -Uri "$documentUrl/documents/liveness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$documentUrl/documents/readiness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$documentUrl/documents/scalar" -ExpectedStatus 200
    Invoke-ExpectedPostStatus -Uri "$documentUrl/Pdfs/invoice" -ExpectedStatus 401

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

    $missingDatabases = @(
        [Legacy.Maliev.AppHost.Topology.LegacyTopology]::DatabaseNames |
            Where-Object { $_ -notin $actualDatabases }
    )
    if ($missingDatabases.Count -gt 0) {
        throw "Missing legacy databases: $($missingDatabases -join ', ')."
    }

    Write-Host 'PASS: PostgreSQL, Redis, Country API, Document API, 21 database names, and environment isolation are healthy.'
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
