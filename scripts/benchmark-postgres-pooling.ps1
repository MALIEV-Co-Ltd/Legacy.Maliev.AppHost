[CmdletBinding()]
param(
    [ValidateRange(1, 200)]
    [int]$Clients = 8,

    [ValidateRange(1, 16)]
    [int]$Jobs = 4,

    [ValidateRange(3, 120)]
    [int]$DurationSeconds = 10,

    [ValidateRange(1, 9)]
    [int]$Rounds = 3,

    [ValidateRange(1, 100)]
    [int]$Scale = 10,

    [string]$OutputPath = (Join-Path $PSScriptRoot '..\temp\pgbouncer-benchmark.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$runId = [guid]::NewGuid().ToString('N').Substring(0, 12)
$networkName = "legacy-pool-benchmark-$runId"
$postgresContainer = "legacy-pool-postgres-$runId"
$sessionContainer = "legacy-pool-session-$runId"
$transactionContainer = "legacy-pool-transaction-$runId"
$containers = @($transactionContainer, $sessionContainer, $postgresContainer)
$password = [guid]::NewGuid().ToString('N')
$sessionPoolMode = 'POOL_MODE=session'
$transactionPoolMode = 'POOL_MODE=transaction'
$results = [System.Collections.Generic.List[object]]::new()

function Invoke-Docker {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $output = @(& docker @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "docker $($Arguments[0]) failed: $($output -join ' | ')"
    }

    return $output
}

function Wait-ForPostgres {
    param(
        [Parameter(Mandatory)]
        [string]$Container,

        [Parameter(Mandatory)]
        [string]$HostName
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    do {
        & docker exec $Container pg_isready -h $HostName -p 5432 -U postgres -d benchmark 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            return
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "$Container did not become ready before the timeout."
}

function Start-Pooler {
    param(
        [Parameter(Mandatory)]
        [string]$Container,

        [Parameter(Mandatory)]
        [ValidateSet('session', 'transaction')]
        [string]$Mode
    )

    $poolMode = if ($Mode -eq 'session') { $sessionPoolMode } else { $transactionPoolMode }
    Invoke-Docker -Arguments @(
        'run', '-d', '--name', $Container,
        '--network', $networkName,
        '--cpus', '0.10', '--memory', '96m',
        '-e', "DB_HOST=$postgresContainer",
        '-e', 'DB_PORT=5432',
        '-e', 'DB_USER=postgres',
        '-e', "DB_PASSWORD=$password",
        '-e', 'AUTH_TYPE=scram-sha-256',
        '-e', $poolMode,
        '-e', 'DEFAULT_POOL_SIZE=3',
        '-e', 'MAX_CLIENT_CONN=200',
        '-e', 'MIN_POOL_SIZE=0',
        '-e', 'RESERVE_POOL_SIZE=1',
        '-e', 'SERVER_IDLE_TIMEOUT=60',
        'edoburu/pgbouncer:v1.25.2-p0'
    ) | Out-Null
    Wait-ForPostgres -Container $Container -HostName '127.0.0.1'
}

function Get-Median {
    param(
        [Parameter(Mandatory)]
        [double[]]$Values
    )

    $ordered = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($ordered.Count / 2)
    if ($ordered.Count % 2 -eq 1) {
        return $ordered[$middle]
    }

    return ($ordered[$middle - 1] + $ordered[$middle]) / 2
}

function Get-ContainerIpAddress {
    param(
        [Parameter(Mandatory)]
        [string]$Container
    )

    $address = (Invoke-Docker -Arguments @(
        'inspect', '--format', "{{with index .NetworkSettings.Networks `"$networkName`"}}{{.IPAddress}}{{end}}", $Container
    ) | Select-Object -Last 1).Trim()
    if (-not [Net.IPAddress]::TryParse($address, [ref]([Net.IPAddress]$null))) {
        throw "Could not resolve the benchmark network address for $Container."
    }

    return $address
}

function Invoke-BenchmarkCase {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$HostName,

        [Parameter(Mandatory)]
        [string]$BackendSourceIp,

        [Parameter(Mandatory)]
        [int]$Round
    )

    $sampler = Start-Job -ScriptBlock {
        param($Container, $DatabasePassword, $SourceIp)
        while ($true) {
            $sample = @(
                & docker exec -e "PGPASSWORD=$DatabasePassword" -e 'PGAPPNAME=sampler' $Container `
                    psql -U postgres -d benchmark -Atc `
                    "select count(*) from pg_stat_activity where datname = current_database() and application_name <> 'sampler' and client_addr = '$SourceIp'::inet;" `
                    2>$null
            )
            if ($LASTEXITCODE -eq 0 -and $sample.Count -gt 0) {
                Write-Output ([int]$sample[-1])
            }
            Start-Sleep -Milliseconds 100
        }
    } -ArgumentList $postgresContainer, $password, $BackendSourceIp

    try {
        $output = @(
            & docker exec -e "PGPASSWORD=$password" -e "PGAPPNAME=pgbench-$Name" $postgresContainer `
                pgbench -h $HostName -p 5432 -U postgres -d benchmark `
                --client=$Clients --jobs=$Jobs --time=$DurationSeconds `
                --report-per-command --no-vacuum 2>&1
        )
        if ($LASTEXITCODE -ne 0) {
            $postgresDiagnostics = @(
                & docker exec -e "PGPASSWORD=$password" $postgresContainer `
                    psql -U postgres -d benchmark -Atc `
                    "select pid, client_addr, state, wait_event_type, wait_event, left(query, 80) from pg_stat_activity where datname = current_database() order by pid;" `
                    2>&1
            )
            $poolerDiagnostics = if ($Name -eq 'direct') {
                @('not applicable')
            }
            else {
                @(
                    & docker exec -e "PGPASSWORD=$password" $postgresContainer `
                        psql -h $HostName -p 5432 -U postgres -d pgbouncer -Atc 'show pools;' 2>&1
                )
            }
            throw "pgbench failed for $Name round $Round`: $($output -join ' | ') | postgres=$($postgresDiagnostics -join ' / ') | pooler=$($poolerDiagnostics -join ' / ')"
        }
    }
    finally {
        Stop-Job -Job $sampler -ErrorAction SilentlyContinue
        $samples = @(Receive-Job -Job $sampler -ErrorAction SilentlyContinue)
        Remove-Job -Job $sampler -Force -ErrorAction SilentlyContinue
    }

    $text = $output -join "`n"
    $latencyMatch = [regex]::Match($text, 'latency average = ([0-9.]+) ms')
    $tpsMatch = [regex]::Match($text, 'tps = ([0-9.]+) \(without initial connection time\)')
    if (-not $latencyMatch.Success -or -not $tpsMatch.Success) {
        throw "Could not parse pgbench output for $Name round $Round`: $text"
    }

    $peakBackends = if ($samples.Count -eq 0) { 0 } else { [int](($samples | Measure-Object -Maximum).Maximum) }
    $postRunBackendStates = @(
        & docker exec -e "PGPASSWORD=$password" $postgresContainer `
            psql -U postgres -d benchmark -Atc `
            "select state || '=' || count(*) from pg_stat_activity where datname = current_database() and application_name <> 'sampler' and client_addr = '$BackendSourceIp'::inet group by state order by state;" `
            2>&1
    )
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect post-run backend state for $Name round $Round`: $($postRunBackendStates -join ' | ')"
    }

    return [ordered]@{
        target = $Name
        round = $Round
        latency_average_ms = [double]::Parse($latencyMatch.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
        transactions_per_second = [double]::Parse($tpsMatch.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
        peak_postgres_backends = $peakBackends
        post_run_backend_states = @($postRunBackendStates)
    }
}

try {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        throw 'docker is required for the disposable pooling benchmark.'
    }
    & docker info --format '{{.ServerVersion}}' | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Docker is installed but its engine is unavailable.'
    }

    Invoke-Docker -Arguments @('network', 'create', $networkName) | Out-Null
    Invoke-Docker -Arguments @(
        'run', '-d', '--name', $postgresContainer,
        '--network', $networkName,
        '--cpus', '0.75', '--memory', '1024m',
        '-e', "POSTGRES_PASSWORD=$password",
        '-e', 'POSTGRES_DB=benchmark',
        'postgres:18-alpine',
        '-c', 'max_connections=100',
        '-c', 'shared_buffers=256MB',
        '-c', 'work_mem=2MB'
    ) | Out-Null
    Wait-ForPostgres -Container $postgresContainer -HostName '127.0.0.1'

    Invoke-Docker -Arguments @(
        'exec', '-e', "PGPASSWORD=$password", $postgresContainer,
        'pgbench', '-U', 'postgres', '-d', 'benchmark', '--initialize', "--scale=$Scale"
    ) | Out-Null

    Start-Pooler -Container $sessionContainer -Mode 'session'
    Start-Pooler -Container $transactionContainer -Mode 'transaction'

    $postgresIp = Get-ContainerIpAddress -Container $postgresContainer
    $sessionIp = Get-ContainerIpAddress -Container $sessionContainer
    $transactionIp = Get-ContainerIpAddress -Container $transactionContainer
    $targets = @(
        [ordered]@{ Name = 'direct'; Host = $postgresContainer; SourceIp = $postgresIp },
        [ordered]@{ Name = 'session'; Host = $sessionContainer; SourceIp = $sessionIp },
        [ordered]@{ Name = 'transaction'; Host = $transactionContainer; SourceIp = $transactionIp }
    )
    for ($round = 1; $round -le $Rounds; $round++) {
        for ($offset = 0; $offset -lt $targets.Count; $offset++) {
            $target = $targets[($round - 1 + $offset) % $targets.Count]
            $results.Add((Invoke-BenchmarkCase -Name $target.Name -HostName $target.Host -BackendSourceIp $target.SourceIp -Round $round))
        }
    }

    $compatibilityProbe = @(
        & docker exec -e "PGPASSWORD=$password" $postgresContainer `
            psql -h $transactionContainer -p 5432 -U postgres -d benchmark -Atc `
            'begin; set local statement_timeout = ''5s''; select pg_advisory_xact_lock(20260718); select 1; commit;' `
            2>&1
    )
    if ($LASTEXITCODE -ne 0 -or '1' -notin $compatibilityProbe) {
        throw "The transaction-scoped compatibility probe failed: $($compatibilityProbe -join ' | ')"
    }

    $recoveryTimer = [Diagnostics.Stopwatch]::StartNew()
    Invoke-Docker -Arguments @('restart', $transactionContainer) | Out-Null
    Wait-ForPostgres -Container $transactionContainer -HostName '127.0.0.1'
    $recoveryProbe = @(
        & docker exec -e "PGPASSWORD=$password" $postgresContainer `
            psql -h $transactionContainer -p 5432 -U postgres -d benchmark -Atc 'select 1;' 2>&1
    )
    $recoveryTimer.Stop()
    if ($LASTEXITCODE -ne 0 -or $recoveryProbe.Count -ne 1 -or $recoveryProbe[0] -ne '1') {
        throw "PgBouncer did not recover after restart: $($recoveryProbe -join ' | ')"
    }

    $summary = @(
        foreach ($targetName in @('direct', 'session', 'transaction')) {
            $targetResults = @($results | Where-Object { $_.target -eq $targetName })
            [ordered]@{
                target = $targetName
                median_latency_ms = Get-Median -Values @($targetResults.latency_average_ms)
                median_transactions_per_second = Get-Median -Values @($targetResults.transactions_per_second)
                maximum_peak_postgres_backends = [int](($targetResults.peak_postgres_backends | Measure-Object -Maximum).Maximum)
            }
        }
    )

    $evidence = [ordered]@{
        generated_at_utc = [DateTimeOffset]::UtcNow.ToString('O')
        environment = 'disposable-local-docker'
        clients = $Clients
        jobs = $Jobs
        duration_seconds = $DurationSeconds
        rounds = $Rounds
        scale = $Scale
        postgres_image = 'postgres:18-alpine'
        pgbouncer_image = 'edoburu/pgbouncer:v1.25.2-p0'
        pooler_parameters = [ordered]@{
            default_pool_size = 3
            max_client_conn = 200
            min_pool_size = 0
            reserve_pool_size = 1
            server_idle_timeout = 60
        }
        runs = @($results)
        summary = $summary
        restart_recovery_seconds = [Math]::Round($recoveryTimer.Elapsed.TotalSeconds, 3)
        compatibility = [ordered]@{
            multi_statement_transaction = 'pass'
            set_local = 'pass'
            transaction_advisory_lock = 'pass'
            ef_core_application_flows = 'validated separately by verify-local-stack.ps1'
            session_state_features = 'unsupported in transaction mode; migrations/admin remain direct'
        }
        boundary = 'Local evidence only. CloudNativePG failover and existing-cluster capacity gates remain pending.'
    }

    $resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
    $outputDirectory = Split-Path -Parent $resolvedOutput
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    $evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resolvedOutput -Encoding utf8
    Write-Output "Pooling benchmark evidence: $resolvedOutput"
    $summary | Format-Table -AutoSize | Out-String | Write-Output
}
finally {
    foreach ($container in $containers) {
        & docker rm -f $container 2>$null | Out-Null
    }
    & docker network rm $networkName 2>$null | Out-Null
}
