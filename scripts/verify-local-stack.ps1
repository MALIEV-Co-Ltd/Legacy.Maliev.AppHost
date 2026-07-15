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

function Invoke-WebInstantQuotationFlow {
    param([string]$WebUrl)

    $pageUri = "$WebUrl/InstantQuotation/3D-Printing?culture=en"
    $page = Invoke-WebRequest -Uri $pageUri -UseBasicParsing -SkipHttpErrorCheck
    if (
        $page.StatusCode -ne 200 -or
        $page.Content -notmatch 'Get an instant manufacturing estimate' -or
        $page.Content -notmatch '<option value="PLA" selected>'
    ) {
        throw "The public instant quotation page did not render its deterministic pricing form (HTTP $($page.StatusCode))."
    }

    $estimateUri = "$WebUrl/InstantQuotation/3D-Printing" +
        '?handler=GetEstimate&material=PLA&dimensionZ=10&volume=1000&footprint=100&quantity=1&currency=THB'
    $estimate = Invoke-RestMethod -Uri $estimateUri -Method Get
    if (
        -not $estimate.success -or
        $estimate.process -ne 'fdm' -or
        $estimate.currency -ne 'THB' -or
        $estimate.unitPrice -le 0 -or
        $estimate.subtotal -le 0
    ) {
        throw 'The public instant quotation endpoint did not return a valid deterministic THB estimate.'
    }
}

function Get-JwtPayload {
    param([string]$Token)

    $segments = $Token.Split('.')
    if ($segments.Count -ne 3) {
        throw 'The service login response did not contain a compact JWT.'
    }

    $payload = $segments[1].Replace('-', '+').Replace('_', '/')
    $payload = $payload.PadRight($payload.Length + ((4 - ($payload.Length % 4)) % 4), '=')
    return ConvertFrom-Json ([System.Text.Encoding]::UTF8.GetString(
        [Convert]::FromBase64String($payload)))
}

function New-WebCustomerSession {
    param(
        [string]$WebUrl,
        [System.Net.Http.HttpClient]$Client,
        [string]$Email,
        [string]$Password
    )

    $loginPage = $Client.GetAsync("$WebUrl/Account/Login").GetAwaiter().GetResult()
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
    $loginForm.Add('Email', $Email)
    $loginForm.Add('Password', $Password)
    $loginForm.Add('RememberMe', 'false')
    $loginRequest.Content = [System.Net.Http.FormUrlEncodedContent]::new($loginForm)
    $login = $Client.SendAsync($loginRequest).GetAwaiter().GetResult()
    if ([int]$login.StatusCode -notin 302, 303) {
        throw "The Web BFF login for $Email returned HTTP $([int]$login.StatusCode)."
    }

    $sessionCookie = @($login.Headers.GetValues('Set-Cookie') | ForEach-Object {
        ($_ -split ';', 2)[0]
    }) -join '; '
    if (-not $sessionCookie) {
        throw 'The Web BFF login did not issue an encrypted session cookie.'
    }

    return @{
        AntiforgeryCookie = $antiforgeryCookie
        SessionCookie = $sessionCookie
    }
}

function Invoke-WebMemberAccountFlow {
    param(
        [string]$WebUrl
    )

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    try {
        $session = New-WebCustomerSession `
            -WebUrl $WebUrl `
            -Client $client `
            -Email 'local.customer@maliev.test' `
            -Password 'local-test-only'
        $antiforgeryCookie = $session.AntiforgeryCookie
        $sessionCookie = $session.SessionCookie

        $addressRequest = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::Get,
            "$WebUrl/member/account/manage/address?culture=en")
        $null = $addressRequest.Headers.TryAddWithoutValidation(
            'Cookie',
            "$antiforgeryCookie; $sessionCookie")
        $address = $client.SendAsync($addressRequest).GetAwaiter().GetResult()
        $addressContent = $address.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (
            [int]$address.StatusCode -ne 200 -or
            $addressContent -notmatch 'name="BillingAddress1"' -or
            $addressContent -match 'address profile could not be loaded'
        ) {
            throw 'The authenticated Member address boundary did not render through the Web BFF.'
        }

        $profileRequest = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::Get,
            "$WebUrl/member/account/manage/profile?culture=en")
        $null = $profileRequest.Headers.TryAddWithoutValidation(
            'Cookie',
            "$antiforgeryCookie; $sessionCookie")
        $profile = $client.SendAsync($profileRequest).GetAwaiter().GetResult()
        $profileContent = $profile.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $profileAntiforgery = [regex]::Match(
            $profileContent,
            'name="__RequestVerificationToken"[^>]*value="([^"]+)"')
        if (
            [int]$profile.StatusCode -ne 200 -or
            -not $profileAntiforgery.Success -or
            $profileContent -match 'profile could not be loaded'
        ) {
            throw 'The authenticated Member profile boundary did not render through the Web BFF.'
        }

        $profileUpdate = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::Post,
            "$WebUrl/member/account/manage/profile?handler=UpdateProfile")
        $null = $profileUpdate.Headers.TryAddWithoutValidation(
            'Cookie',
            "$antiforgeryCookie; $sessionCookie")
        $profileForm = [System.Collections.Generic.Dictionary[string,string]]::new()
        $profileForm.Add(
            '__RequestVerificationToken',
            [System.Net.WebUtility]::HtmlDecode($profileAntiforgery.Groups[1].Value))
        $profileForm.Add('FirstName', 'Local')
        $profileForm.Add('LastName', 'Customer')
        $profileForm.Add('CompanyName', 'Local Test Company')
        $profileUpdate.Content = [System.Net.Http.FormUrlEncodedContent]::new($profileForm)
        $profileResult = $client.SendAsync($profileUpdate).GetAwaiter().GetResult()
        if ([int]$profileResult.StatusCode -notin 302, 303) {
            $profileResultContent = $profileResult.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $validationMessages = @([regex]::Matches(
                $profileResultContent,
                '<li>([^<]+)</li>') | ForEach-Object {
                    [System.Net.WebUtility]::HtmlDecode($_.Groups[1].Value)
                })
            $validationDetail = if ($validationMessages.Count -gt 0) {
                $validationMessages -join '; '
            }
            else {
                'no validation detail was rendered'
            }
            $diagnostics = @(
                @('legacy-maliev-web', 'legacy-maliev-customer-service') | ForEach-Object {
                    & aspire logs $_ --apphost $appHostProject --tail 100 --format Table `
                        --non-interactive 2>&1
                } | Where-Object {
                    $_ -match '(warn|fail|error|companies|customers/|status code|rejected|unavailable|permission| 4\d\d | 5\d\d )' -and
                    $_ -notmatch '"?isError"?'
                } | Select-Object -Last 40
            ) -join ' | '
            throw "The Member profile update returned HTTP $([int]$profileResult.StatusCode): $validationDetail. Diagnostics: $diagnostics"
        }

        $quotationHistoryRequest = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::Get,
            "$WebUrl/member/quotations/index?culture=en")
        $null = $quotationHistoryRequest.Headers.TryAddWithoutValidation(
            'Cookie',
            "$antiforgeryCookie; $sessionCookie")
        $quotationHistory = $client.SendAsync($quotationHistoryRequest).GetAwaiter().GetResult()
        $quotationHistoryContent = $quotationHistory.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (
            [int]$quotationHistory.StatusCode -ne 200 -or
            $quotationHistoryContent -notmatch 'Quotation[^<]*#1'
        ) {
            throw 'The authenticated customer quotation history did not render the seeded quotation.'
        }

        $quotationRequest = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::Get,
            "$WebUrl/member/quotations/view?id=1&culture=en")
        $null = $quotationRequest.Headers.TryAddWithoutValidation(
            'Cookie',
            "$antiforgeryCookie; $sessionCookie")
        $quotationPage = $client.SendAsync($quotationRequest).GetAwaiter().GetResult()
        $quotationContent = $quotationPage.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (
            [int]$quotationPage.StatusCode -ne 200 -or
            $quotationContent -notmatch 'Local CNC quotation line' -or
            $quotationContent -notmatch 'quotations/local-cnc-quotation.pdf' -or
            $quotationContent -match 'paypal'
        ) {
            $diagnostics = @(
                @('legacy-maliev-web', 'legacy-maliev-quotation-service') | ForEach-Object {
                    & aspire logs $_ --apphost $appHostProject --tail 120 --format Table `
                        --non-interactive 2>&1
                } | Where-Object {
                    $_ -match '(warn|fail|error|quotations/|status code|rejected|unavailable|permission| 4\d\d | 5\d\d )' -and
                    $_ -notmatch '"?isError"?'
                } | Select-Object -Last 50
            ) -join ' | '
            throw "The authenticated owned quotation detail did not render through the Web BFF. Diagnostics: $diagnostics"
        }

        $serviceOrderCompatibilityRoutes = @(
            @{ Path = '/member/orders/3d-printing'; ExpectedItem = '3D-Printing' },
            @{ Path = '/member/orders/3d-scanning'; ExpectedItem = '3D-Scanning' },
            @{ Path = '/member/orders/cnc-machining'; ExpectedItem = 'CNC-Machining' }
        )
        foreach ($route in $serviceOrderCompatibilityRoutes) {
            $compatibilityRequest = [System.Net.Http.HttpRequestMessage]::new(
                [System.Net.Http.HttpMethod]::Get,
                "$WebUrl$($route.Path)")
            $null = $compatibilityRequest.Headers.TryAddWithoutValidation(
                'Cookie',
                "$antiforgeryCookie; $sessionCookie")
            $compatibilityResponse = $client.SendAsync($compatibilityRequest).GetAwaiter().GetResult()
            $locationHeader = $compatibilityResponse.Headers.Location
            if ([int]$compatibilityResponse.StatusCode -notin 302, 303 -or $null -eq $locationHeader) {
                throw "The authenticated compatibility route $($route.Path) did not redirect to the quotation request."
            }

            $location = [Uri]::new([Uri]$WebUrl, $locationHeader)
            if (
                $location.AbsolutePath -notin '/Quotation', '/Quotation/Index' -or
                $location.Query -ne "?item=$($route.ExpectedItem)"
            ) {
                throw "The compatibility route $($route.Path) redirected to unexpected location $location."
            }
        }

        $historyRequest = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::Get,
            "$WebUrl/member/orders/history?culture=en")
        $null = $historyRequest.Headers.TryAddWithoutValidation(
            'Cookie',
            "$antiforgeryCookie; $sessionCookie")
        $historyPage = $client.SendAsync($historyRequest).GetAwaiter().GetResult()
        $historyContent = $historyPage.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ([int]$historyPage.StatusCode -ne 200 -or $historyContent -notmatch 'Local CNC order') {
            $historyErrors = @([regex]::Matches(
                $historyContent,
                '<li>([^<]+)</li>') | ForEach-Object {
                    [System.Net.WebUtility]::HtmlDecode($_.Groups[1].Value)
                }) -join '; '
            $diagnostics = @(
                @('legacy-maliev-web', 'legacy-maliev-order-service') | ForEach-Object {
                    & aspire logs $_ --apphost $appHostProject --tail 120 --format Table `
                        --non-interactive 2>&1
                } | Where-Object {
                    $_ -match '(warn|fail|error|orders/|status code|rejected|unavailable|permission| 4\d\d | 5\d\d )' -and
                    $_ -notmatch '"?isError"?'
                } | Select-Object -Last 50
            ) -join ' | '
            throw "The authenticated order history returned HTTP $([int]$historyPage.StatusCode) without the seeded order. Rendered errors: $historyErrors. Diagnostics: $diagnostics"
        }

        $orderRequest = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::Get,
            "$WebUrl/member/orders/view?itemID=1&culture=en")
        $null = $orderRequest.Headers.TryAddWithoutValidation(
            'Cookie',
            "$antiforgeryCookie; $sessionCookie")
        $orderPage = $client.SendAsync($orderRequest).GetAwaiter().GetResult()
        $orderContent = $orderPage.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $orderAntiforgery = [regex]::Match(
            $orderContent,
            'name="__RequestVerificationToken"[^>]*value="([^"]+)"')
        if (
            [int]$orderPage.StatusCode -ne 200 -or
            -not $orderAntiforgery.Success -or
            $orderContent -notmatch 'Reviewing' -or
            $orderContent -notmatch 'orders/local-cnc-part.step'
        ) {
            throw 'The authenticated owned order detail did not render through the Web BFF.'
        }

        $cancelRequest = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::Post,
            "$WebUrl/member/orders/view?handler=CancelOrder")
        $null = $cancelRequest.Headers.TryAddWithoutValidation(
            'Cookie',
            "$antiforgeryCookie; $sessionCookie")
        $cancelForm = [System.Collections.Generic.Dictionary[string,string]]::new()
        $cancelForm.Add(
            '__RequestVerificationToken',
            [System.Net.WebUtility]::HtmlDecode($orderAntiforgery.Groups[1].Value))
        $cancelForm.Add('orderId', '1')
        $cancelRequest.Content = [System.Net.Http.FormUrlEncodedContent]::new($cancelForm)
        $cancelResult = $client.SendAsync($cancelRequest).GetAwaiter().GetResult()
        if ([int]$cancelResult.StatusCode -notin 302, 303) {
            $cancelContent = $cancelResult.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $cancelErrors = @([regex]::Matches(
                $cancelContent,
                '<li>([^<]+)</li>') | ForEach-Object {
                    [System.Net.WebUtility]::HtmlDecode($_.Groups[1].Value)
                }) -join '; '
            $diagnostics = @(
                @('legacy-maliev-web', 'legacy-maliev-order-service') | ForEach-Object {
                    & aspire logs $_ --apphost $appHostProject --tail 160 --format Table `
                        --non-interactive 2>&1
                } | Where-Object {
                    $_ -match '(warn|fail|error|cancel|orders/|status code|rejected|unavailable|permission|transition|InvalidOperationException|Sequence|Npgsql| 4\d\d | 5\d\d )' -and
                    $_ -notmatch '"?isError"?'
                } | Select-Object -Last 70
            ) -join ' | '
            throw "The owned order cancellation returned HTTP $([int]$cancelResult.StatusCode). Rendered errors: $cancelErrors. Diagnostics: $diagnostics"
        }

        $cancelledRequest = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::Get,
            "$WebUrl/member/orders/view?itemID=1&culture=en")
        $null = $cancelledRequest.Headers.TryAddWithoutValidation(
            'Cookie',
            "$antiforgeryCookie; $sessionCookie")
        $cancelledPage = $client.SendAsync($cancelledRequest).GetAwaiter().GetResult()
        $cancelledContent = $cancelledPage.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ([int]$cancelledPage.StatusCode -ne 200 -or $cancelledContent -notmatch 'Cancelled') {
            throw 'The cancelled status was not observable through the owned order detail boundary.'
        }

        $passwordRequest = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::Get,
            "$WebUrl/member/account/manage/changepassword?culture=en")
        $null = $passwordRequest.Headers.TryAddWithoutValidation(
            'Cookie',
            "$antiforgeryCookie; $sessionCookie")
        $passwordPage = $client.SendAsync($passwordRequest).GetAwaiter().GetResult()
        $passwordContent = $passwordPage.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $passwordAntiforgery = [regex]::Match(
            $passwordContent,
            'name="__RequestVerificationToken"[^>]*value="([^"]+)"')
        if ([int]$passwordPage.StatusCode -ne 200 -or -not $passwordAntiforgery.Success) {
            throw 'The authenticated password-change boundary did not render through the Web BFF.'
        }

        $passwordUpdate = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::Post,
            "$WebUrl/member/account/manage/changepassword?handler=ChangePassword")
        $null = $passwordUpdate.Headers.TryAddWithoutValidation(
            'Cookie',
            "$antiforgeryCookie; $sessionCookie")
        $passwordForm = [System.Collections.Generic.Dictionary[string,string]]::new()
        $passwordForm.Add(
            '__RequestVerificationToken',
            [System.Net.WebUtility]::HtmlDecode($passwordAntiforgery.Groups[1].Value))
        $passwordForm.Add('CurrentPassword', 'local-test-only')
        $passwordForm.Add('NewPassword', 'local-test-updated')
        $passwordForm.Add('ConfirmPassword', 'local-test-updated')
        $passwordUpdate.Content = [System.Net.Http.FormUrlEncodedContent]::new($passwordForm)
        $passwordResult = $client.SendAsync($passwordUpdate).GetAwaiter().GetResult()
        if ([int]$passwordResult.StatusCode -notin 302, 303) {
            throw "The Member password change returned HTTP $([int]$passwordResult.StatusCode)."
        }

        $session = New-WebCustomerSession `
            -WebUrl $WebUrl `
            -Client $client `
            -Email 'local.customer@maliev.test' `
            -Password 'local-test-updated'
        $antiforgeryCookie = $session.AntiforgeryCookie
        $sessionCookie = $session.SessionCookie

        $emailRequest = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::Get,
            "$WebUrl/member/account/manage/changeemail?culture=en")
        $null = $emailRequest.Headers.TryAddWithoutValidation(
            'Cookie',
            "$antiforgeryCookie; $sessionCookie")
        $emailPage = $client.SendAsync($emailRequest).GetAwaiter().GetResult()
        $emailContent = $emailPage.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $emailAntiforgery = [regex]::Match(
            $emailContent,
            'name="__RequestVerificationToken"[^>]*value="([^"]+)"')
        if ([int]$emailPage.StatusCode -ne 200 -or -not $emailAntiforgery.Success) {
            throw 'The authenticated email-change boundary did not render through the Web BFF.'
        }

        $emailUpdate = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::Post,
            "$WebUrl/member/account/manage/changeemail?handler=ChangeEmail")
        $null = $emailUpdate.Headers.TryAddWithoutValidation(
            'Cookie',
            "$antiforgeryCookie; $sessionCookie")
        $emailForm = [System.Collections.Generic.Dictionary[string,string]]::new()
        $emailForm.Add(
            '__RequestVerificationToken',
            [System.Net.WebUtility]::HtmlDecode($emailAntiforgery.Groups[1].Value))
        $emailForm.Add('CurrentPassword', 'local-test-updated')
        $emailForm.Add('NewEmail', 'local.changed@maliev.test')
        $emailUpdate.Content = [System.Net.Http.FormUrlEncodedContent]::new($emailForm)
        $emailResult = $client.SendAsync($emailUpdate).GetAwaiter().GetResult()
        $emailLocation = $emailResult.Headers.Location
        if ([int]$emailResult.StatusCode -notin 302, 303 -or $null -eq $emailLocation) {
            throw "The Member email change returned HTTP $([int]$emailResult.StatusCode)."
        }

        $emailRedirect = [Uri]::new([Uri]$WebUrl, $emailLocation)
        if (
            $emailRedirect.AbsolutePath -ne '/Account/Login' -or
            [System.Net.WebUtility]::UrlDecode($emailRedirect.Query) -notmatch 'email=local.changed@maliev.test'
        ) {
            throw "The Member email change redirected to unexpected location $emailRedirect."
        }

        $clearCookies = @($emailResult.Headers.GetValues('Set-Cookie'))
        if (-not ($clearCookies | Where-Object { $_ -match '__Host-Maliev\.Legacy\.Session=;' })) {
            throw 'The Member email change did not clear the encrypted BFF session cookie.'
        }

        $signedOutRequest = [System.Net.Http.HttpRequestMessage]::new(
            [System.Net.Http.HttpMethod]::Get,
            "$WebUrl/member/account/manage/changeemail")
        $null = $signedOutRequest.Headers.TryAddWithoutValidation(
            'Cookie',
            "$antiforgeryCookie; $sessionCookie")
        $signedOut = $client.SendAsync($signedOutRequest).GetAwaiter().GetResult()
        if ([int]$signedOut.StatusCode -notin 302, 303) {
            throw 'The invalidated BFF session still accessed an authenticated Member route.'
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
        'legacy-customer-migrations-*',
        'legacy-order-migrations-*',
        'legacy-order-status-migrations-*',
        'legacy-quotation-migrations-*',
        'legacy-quotation-request-migrations-*'
    )
    $servicePatterns = @(
        'legacy-maliev-country-service-*',
        'legacy-maliev-document-service-*',
        'legacy-maliev-auth-service-*',
        'legacy-maliev-customer-service-*',
        'legacy-maliev-order-service-*',
        'legacy-maliev-quotation-service-*',
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
                    $resourceItems.Count -ge 19 -and
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

    $orderResource = Get-SingleResource -Items $resourceItems -NamePattern 'legacy-maliev-order-service-*'
    $orderUrl = Get-ResourceUrl -Resource $orderResource
    Invoke-ExpectedStatus -Uri "$orderUrl/order/liveness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$orderUrl/order/readiness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$orderUrl/order/scalar" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$orderUrl/orders/customers/1" -ExpectedStatus 401

    $quotationResource = Get-SingleResource -Items $resourceItems -NamePattern 'legacy-maliev-quotation-service-*'
    $quotationUrl = Get-ResourceUrl -Resource $quotationResource
    Invoke-ExpectedStatus -Uri "$quotationUrl/quotation/liveness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$quotationUrl/quotation/readiness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$quotationUrl/quotation/scalar" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$quotationUrl/quotations/customers/1" -ExpectedStatus 401

    $notificationResource = Get-SingleResource -Items $resourceItems -NamePattern 'legacy-maliev-notification-service-*'
    $notificationUrl = Get-ResourceUrl -Resource $notificationResource
    Invoke-ExpectedStatus -Uri "$notificationUrl/emails/liveness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$notificationUrl/emails/readiness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$notificationUrl/emails/scalar" -ExpectedStatus 200
    Invoke-ExpectedPostStatus -Uri "$notificationUrl/notifications/v1/email/noreply" -ExpectedStatus 401

    $webResource = Get-SingleResource -Items $resourceItems -NamePattern 'legacy-maliev-web-*'
    $webUrl = Get-ResourceUrl -Resource $webResource
    $webClientSecret = ($webResource.status.effectiveEnv | Where-Object {
        $_.name -eq 'ServiceAuthentication__ClientSecret'
    }).value
    if (-not $webClientSecret) {
        throw 'The Web service credential was not resolved by the local orchestrator.'
    }

    $serviceLogin = Invoke-RestMethod -Uri "$authUrl/auth/v1/service/login" -Method Post `
        -ContentType 'application/json' -Body (@{
            clientId = 'legacy-web'
            clientSecret = $webClientSecret
        } | ConvertTo-Json -Compress)
    $serviceClaims = Get-JwtPayload -Token $serviceLogin.accessToken
    $runtimePermissions = @($serviceClaims.permissions)
    $requiredMemberPermissions = @(
        'legacy-customer.customers.read',
        'legacy-customer.customers.update',
        'legacy-customer.addresses.create',
        'legacy-customer.addresses.update',
        'legacy-customer.companies.create',
        'legacy-customer.companies.update',
        'legacy-customer.companies.delete'
        'legacy.notifications.send'
        'legacy.customer-orders.read'
        'legacy.customer-orders.cancel'
        'legacy.customer-quotations.read'
    )
    $missingMemberPermissions = @($requiredMemberPermissions | Where-Object {
        $_ -notin $runtimePermissions
    })
    if ($missingMemberPermissions.Count -gt 0) {
        throw "The Web service JWT is missing Member permissions: $($missingMemberPermissions -join ', ')."
    }

    $serviceHeaders = @{ Authorization = "Bearer $($serviceLogin.accessToken)" }
    $companyCreate = Invoke-WebRequest -Uri "$customerUrl/customers/companies" -Method Post `
        -Headers $serviceHeaders -ContentType 'application/json' -SkipHttpErrorCheck `
        -Body (@{ name = 'Local Permission Probe' } | ConvertTo-Json -Compress)
    if ($companyCreate.StatusCode -ne 201) {
        throw "The Web service identity could not create companies (HTTP $($companyCreate.StatusCode)): $($companyCreate.Content)"
    }

    $probeCompany = ConvertFrom-Json $companyCreate.Content
    $companyUpdate = Invoke-WebRequest -Uri "$customerUrl/customers/companies/$($probeCompany.id)" `
        -Method Put -Headers $serviceHeaders -ContentType 'application/json' -SkipHttpErrorCheck `
        -Body (@{ name = 'Updated Local Permission Probe' } | ConvertTo-Json -Compress)
    if ($companyUpdate.StatusCode -ne 204) {
        throw "The Web service identity could not update companies (HTTP $($companyUpdate.StatusCode)): $($companyUpdate.Content)"
    }

    $companyDelete = Invoke-WebRequest -Uri "$customerUrl/customers/companies/$($probeCompany.id)" `
        -Method Delete -Headers $serviceHeaders -SkipHttpErrorCheck
    if ($companyDelete.StatusCode -ne 204) {
        throw "The Web service identity could not delete companies (HTTP $($companyDelete.StatusCode)): $($companyDelete.Content)"
    }

    Invoke-ExpectedStatus -Uri "$webUrl/web/liveness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$webUrl/web/readiness" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$webUrl/Account/Login" -ExpectedStatus 200
    Invoke-ExpectedStatus -Uri "$webUrl/Account/Signup" -ExpectedStatus 200
    Invoke-WebInstantQuotationFlow -WebUrl $webUrl
    Invoke-WebMemberAccountFlow -WebUrl $webUrl

    $recordedResponse = Invoke-RestMethod `
        -Uri "$notificationUrl/notifications/development/recorded" `
        -Method Get
    $recordedNotifications = [System.Collections.Generic.List[object]]::new()
    foreach ($recordedNotification in $recordedResponse) {
        $recordedNotifications.Add($recordedNotification)
    }
    $passwordNotification = @($recordedNotifications | Where-Object {
        $_.to -eq 'local.customer@maliev.test' -and
        $_.subject -eq 'Your MALIEV password was changed'
    })
    $emailNotification = @($recordedNotifications | Where-Object {
        $_.to -eq 'local.changed@maliev.test' -and
        $_.subject -eq 'Confirm your new MALIEV email address'
    })
    if (
        $recordedNotifications.Count -ne 2 -or
        $passwordNotification.Count -ne 1 -or
        $emailNotification.Count -ne 1
    ) {
        $recordedSummary = @($recordedNotifications | ForEach-Object {
            "$($_.to) | $($_.subject)"
        }) -join '; '
        $notificationDiagnostics = @(
            @('legacy-maliev-web', 'legacy-maliev-notification-service', 'legacy-maliev-auth-service') |
                ForEach-Object {
                    & aspire logs $_ --apphost $appHostProject --tail 160 --format Table `
                        --non-interactive 2>&1
                } | Where-Object {
                    $_ -match '(warn|fail|error|notification|status|permission|unauthorized|forbidden| 4\d\d | 5\d\d )' -and
                    $_ -notmatch '"?isError"?'
                } | Select-Object -Last 60
        ) -join ' | '
        throw "The development notification provider did not record the password and email security messages. Count: $($recordedNotifications.Count). Recorded: $recordedSummary. Diagnostics: $notificationDiagnostics"
    }

    $changedCustomer = Invoke-RestMethod -Uri "$customerUrl/customers/1" -Headers $serviceHeaders
    if ($changedCustomer.email -ne 'local.changed@maliev.test') {
        throw 'The Customer profile did not retain the new email address after the Web BFF change.'
    }

    foreach ($email in @('local.customer@maliev.test', 'local.changed@maliev.test')) {
        Invoke-ExpectedPostStatus -Uri "$authUrl/auth/v1/login" -ExpectedStatus 401 -Body (@{
                userName = $email
                password = 'local-test-updated'
                identityKind = 0
            } | ConvertTo-Json -Compress)
    }

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

    Write-Host 'PASS: PostgreSQL, Redis, eight services, nine migrations, 21 preserved databases plus Auth runtime state, public instant quotation, recorded local security notifications, customer/employee login, authenticated Member address/profile/quotation/order cancellation/order compatibility/password/email BFF flows, protected boundaries, and environment isolation are healthy.'
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
