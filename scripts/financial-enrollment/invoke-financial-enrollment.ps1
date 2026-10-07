[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ConfigurationPath,
    [Parameter(Mandatory)][ValidateSet('Plan','Verify','Apply','Revoke')][string]$Mode,
    [Parameter(Mandatory)][string]$ReceiptPath,
    [string]$PlanHash,
    [string]$AppliedReceiptPath,
    [scriptblock]$CleanupObserver
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'FinancialEnrollment.psm1') -Force
$configuration=Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json -Depth 20
$token=[Environment]::GetEnvironmentVariable('MALIEV_FINANCIAL_ENROLLMENT_OPERATOR_TOKEN')
if ([string]::IsNullOrWhiteSpace($token) -or $token.Length -gt 16384 -or $token -match '[\s\r\n]') { throw 'A private existing operator token is required through the selected environment input.' }
try {
    $parts=$token.Split('.')
    if($parts.Count -ne 3){throw 'Invalid token shape'}
    $payload=$parts[1].Replace('-','+').Replace('_','/')
    $payload=$payload.PadRight($payload.Length+((4-$payload.Length%4)%4),'=')
    $claims=[Text.UTF8Encoding]::new($false,$true).GetString([Convert]::FromBase64String($payload))|ConvertFrom-Json -Depth 10
    if($claims.sub -cne $configuration.OperatorPrincipalId){throw 'Operator identity mismatch'}
} catch {throw 'The declared operator GUID must match the existing token subject; IAM still verifies its signature and authority.'}
$events=[System.Collections.Generic.List[object]]::new()
$handler=$null;$client=$null;$output=$null;$journal=$null
$result=$null;$failure=$false
$executionState=@{ReviewedPlan=$null}
try {
    $output=[IO.File]::Open([IO.Path]::GetFullPath($ReceiptPath),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $journal=[IO.File]::Open([IO.Path]::GetFullPath($ReceiptPath)+'.events.jsonl',[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $handler=[System.Net.Http.HttpClientHandler]::new();$handler.AllowAutoRedirect=$false
    $client=[System.Net.Http.HttpClient]::new($handler);$client.Timeout=[timespan]::FromSeconds(10)
    $durableEvent={param($event) $bytes=[Text.Encoding]::UTF8.GetBytes(($event|ConvertTo-Json -Depth 10 -Compress)+"`n");$journal.Write($bytes);$journal.Flush($true)}.GetNewClosure()
    $durablePlan={param($plan,$hash) $executionState.ReviewedPlan=$plan;$bytes=[Text.Encoding]::UTF8.GetBytes((@{Kind='PlanAccepted';PlanHash=$hash;ReviewedPlan=$plan}|ConvertTo-Json -Depth 20 -Compress)+"`n");$journal.Write($bytes);$journal.Flush($true)}.GetNewClosure()
    $transport={
        param($method,$path,$body)
        $request=[System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($method),[Uri]::new([Uri]$configuration.IamOrigin,$path))
        $response=$null;$stream=$null;$buffered=$null
        try {
            $request.Headers.Authorization=[System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$token)
            if ($null -ne $body) { $request.Content=[System.Net.Http.StringContent]::new(($body|ConvertTo-Json -Depth 10 -Compress),[Text.Encoding]::UTF8,'application/json') }
            $response=$client.SendAsync($request,[System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            if ([int]$response.StatusCode -ge 300 -and [int]$response.StatusCode -lt 400) { throw 'Redirects are refused.' }
            if ($response.Content.Headers.ContentLength -gt 65536) { throw 'Response exceeds the configured bound.' }
            $stream=$response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();$buffered=[IO.MemoryStream]::new();$buffer=[byte[]]::new(8192)
            $deadline=[Threading.CancellationTokenSource]::new([timespan]::FromSeconds(10))
            try {
                while (($count=$stream.ReadAsync($buffer,0,$buffer.Length,$deadline.Token).GetAwaiter().GetResult()) -gt 0) {
                    if ($buffered.Length+$count -gt 65536) { throw 'Response exceeds the configured bound.' }
                    $buffered.Write($buffer,0,$count)
                }
            } finally { $deadline.Dispose() }
            $data=$null
            if ($buffered.Length -gt 0 -and [int]$response.StatusCode -lt 300) {
                $text=[Text.UTF8Encoding]::new($false,$true).GetString($buffered.ToArray())
                $data=ConvertFrom-Json -InputObject $text -Depth 20 -NoEnumerate
            }
            @{Status=[int]$response.StatusCode;Data=$data}
        } finally { if($buffered){$buffered.Dispose()};if($stream){$stream.Dispose()};if($response){$response.Dispose()};$request.Dispose() }
    }.GetNewClosure()
    $prior=if($AppliedReceiptPath){Get-Content -LiteralPath $AppliedReceiptPath -Raw|ConvertFrom-Json -Depth 20}else{$null}
    if($prior -and $prior.Result){$prior=$prior.Result}
    $result=Invoke-FinancialEnrollmentCoordinator -Configuration $configuration -Mode $Mode -Request $transport -ReceiptEvents $events -ExpectedPlanHash $PlanHash -AppliedReceipt $prior -OnReceiptEvent $durableEvent -OnReviewedPlan $durablePlan
} catch {
    $failure=$true
} finally {
    try {
        $receipt=[ordered]@{Mode=$Mode;IamOrigin=$(if($configuration.PSObject.Properties['IamOrigin']){$configuration.IamOrigin}else{$null});RoleId=$(if($configuration.PSObject.Properties['RoleId']){$configuration.RoleId}else{$null});PlanHash=$PlanHash;ReviewedPlan=$executionState.ReviewedPlan;Completed=(-not $failure);ReconciliationRequired=$failure;Events=@($events.ToArray());Result=$result;GenuineFinancialHttpAcceptance=$false}
        if($output){$bytes=[Text.Encoding]::UTF8.GetBytes(($receipt|ConvertTo-Json -Depth 20));$output.Write($bytes);$output.Flush($true)}
    } finally {
        $closed=@{};$cleanupFailures=[System.Collections.Generic.List[string]]::new()
        foreach($resource in @(@{Name='Journal';Value=$journal},@{Name='Receipt';Value=$output},@{Name='HttpClient';Value=$client},@{Name='Handler';Value=$(if(-not $client){$handler}else{$null})})) {
            if($null -ne $resource.Value){try{$resource.Value.Dispose();$closed[$resource.Name]=$true}catch{$cleanupFailures.Add($resource.Name)}}
        }
        $token=$null
        if($CleanupObserver){&$CleanupObserver $closed}
        if($cleanupFailures.Count){throw 'Financial enrollment resource disposal failed.'}
    }
}
if($failure){throw 'Financial enrollment did not complete. Retain the private receipt and reconcile exact outcomes before retrying.'}
Write-Output 'Financial enrollment receipt retained; no genuine financial HTTP acceptance is inferred.'
