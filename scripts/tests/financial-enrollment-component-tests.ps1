Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot '../financial-enrollment/FinancialEnrollment.psm1') -Force
$now=[datetimeoffset]'2026-10-07T00:00:00Z'
$config=[pscustomobject]@{IamOrigin='https://iam.fixture.invalid';Environment='Testing';OperatorPrincipalId='00000000-0000-0000-0000-000000000001';RoleId='roles.legacy.financial-read';ExpiresAtUtc='2026-10-08T00:00:00Z';ApproveGlobalFinancialScope=$true;SourceRevisions=[ordered]@{Auth=('a'*40);Accounting=('b'*40);Defaults=('c'*40);IAM='4fe6642e5674013de9a3672505aec898fdcae0ed'}}
$state=@{Principals=@{};Bindings=@{};Requests=[System.Collections.Generic.List[object]]::new();ExtraRole=$false;BadCatalogue=$false;FailBinding=$false;NextId=10}
$transport={param($method,$path,$body)
    $state.Requests.Add(@{Method=$method;Path=$path;Body=$body})
    if($path -like '/iam/v1/permissions/*'){return @{Status=200;Data=@{permissionId='legacy.accounting-financial-ownership.read';serviceName=if($state.BadCatalogue){'wrong'}else{'legacy'};resourceType='accounting-financial-ownership';action='read'}}}
    if($path -like '/iam/v1/roles/*'){return @{Status=200;Data=@{roleId=$config.RoleId;permissionIds=@(if($state.ExtraRole){'legacy.accounting-financial-ownership.read';'*'}else{'legacy.accounting-financial-ownership.read'})}}}
    if($method -eq 'GET' -and $path -like '/iam/v1/principals/by-email/*'){
        $email=[Uri]::UnescapeDataString($path.Substring('/iam/v1/principals/by-email/'.Length))
        if($state.Principals.ContainsKey($email)){return @{Status=200;Data=$state.Principals[$email]}}
        return @{Status=404;Data=$null}
    }
    if($method -eq 'POST' -and $path -eq '/iam/v1/principals'){
        if(@($body.Keys).Count -ne 2 -or $body.principalType -ne 'service_account'){throw 'Wrong principal wire shape'}
        $state.NextId++;$id=('00000000-0000-0000-0000-'+$state.NextId.ToString('000000000000'))
        $state.Principals[$body.email]=@{principalId=$id;email=$body.email;principalType='service_account';isActive=$true}
        return @{Status=201;Data=@{principalId=$id;createdAt=$now.ToString('o')}}
    }
    if($path -match '^/iam/v1/principals/([^/]+)/roles(?:/([^/]+))?$'){
        $id=$Matches[1]
        if($method -eq 'GET'){return @{Status=200;Data=@(if($state.Bindings.ContainsKey($id)){$state.Bindings[$id]})}}
        if($method -eq 'POST'){
            if($state.FailBinding){return @{Status=409;Data=$null}}
            if(@($body.Keys).Count -ne 3 -or $null -ne $body.resourcePath -or $body.roleId -ne $config.RoleId){throw 'Wrong binding wire shape'}
            $state.NextId++;$bid=('00000000-0000-0000-0000-'+$state.NextId.ToString('000000000000'))
            $state.Bindings[$id]=@{bindingId=$bid;principalId=$id;roleId=$body.roleId;resourcePath=$null;expiresAt=$body.expiresAt}
            return @{Status=200;Data=$state.Bindings[$id]}
        }
        if($method -eq 'DELETE'){
            if($state.Bindings[$id].bindingId -ne $Matches[2]){throw 'Wrong binding deletion'}
            $state.Bindings.Remove($id);return @{Status=204;Data=$null}
        }
    }
    throw 'Unexpected request'
}.GetNewClosure()
$passed=0
function Check([bool]$value,[string]$name){if(-not $value){throw $name};$script:passed++}
function Expect-Failure([scriptblock]$action,[string]$name){$failed=$false;try{&$action|Out-Null}catch{$failed=$true};Check $failed $name}
function Events { ,[System.Collections.Generic.List[object]]::new() }
$e=Events
$plan=Invoke-FinancialEnrollmentCoordinator $config Plan $transport $e -Now $now -Clock { $now }
Check ($state.Principals.Count -eq 0 -and $state.Bindings.Count -eq 0) 'Plan must never mutate'
Expect-Failure {Invoke-FinancialEnrollmentCoordinator $config Apply $transport (Events) -ExpectedPlanHash ('0'*64) -Now $now -Clock { $now }} 'Wrong plan hash denied'
Check ($state.Principals.Count -eq 0) 'Wrong hash cannot create principals'
$state.ExtraRole=$true
Expect-Failure {Invoke-FinancialEnrollmentCoordinator $config Plan $transport (Events) -Now $now -Clock { $now }} 'Extra wildcard role denied'
$state.ExtraRole=$false;$state.BadCatalogue=$true
Expect-Failure {Invoke-FinancialEnrollmentCoordinator $config Plan $transport (Events) -Now $now -Clock { $now }} 'Mismatched catalogue denied'
$state.BadCatalogue=$false
$config.IamOrigin='https://iam.fixture.invalid/path'
Expect-Failure {Invoke-FinancialEnrollmentCoordinator $config Plan $transport (Events) -Now $now -Clock { $now }} 'Non-origin URI denied'
$config.IamOrigin='https://iam.fixture.invalid';$config.ExpiresAtUtc='2026-10-06T00:00:00Z'
Expect-Failure {Invoke-FinancialEnrollmentCoordinator $config Plan $transport (Events) -Now $now -Clock { $now }} 'Expired input denied'
$config.ExpiresAtUtc='2026-10-08T00:00:00Z'
$applied=Invoke-FinancialEnrollmentCoordinator $config Apply $transport $e -ExpectedPlanHash $plan.PlanHash -Now $now -Clock { $now }
Check ($state.Principals.Count -eq 2 -and $state.Bindings.Count -eq 2) 'Normal two caller creation/binding'
Check (@($e|Where-Object Kind -eq 'PrincipalCreated').Count -eq 2 -and @($e|Where-Object Kind -eq 'BindingCreated').Count -eq 2) 'Exact server IDs retained'
Check (-not $applied.GenuineFinancialHttpAcceptance) 'Component results cannot assert genuine acceptance'
$verified=Invoke-FinancialEnrollmentCoordinator $config Verify $transport (Events) -Now $now -Clock { $now }
Check $verified.EnrollmentReadbackVerified 'Normal API readback verified'
Expect-Failure {Invoke-FinancialEnrollmentCoordinator $config Apply $transport (Events) -ExpectedPlanHash $plan.PlanHash -Now $now -Clock { $now }} 'Changed state needs new plan'
$revoked=Invoke-FinancialEnrollmentCoordinator $config Revoke $transport (Events) -ExpectedPlanHash $applied.PlanHash -AppliedReceipt $applied -Now $now -Clock { $now }
Check ($revoked.RevocationReadbackVerified -and $state.Bindings.Count -eq 0 -and $state.Principals.Count -eq 2) 'Exact revoke preserves principals'
Expect-Failure {Invoke-FinancialEnrollmentCoordinator $config Verify $transport (Events) -Now $now -Clock { $now }} 'Absent binding denied'
$partialPlan=Invoke-FinancialEnrollmentCoordinator $config Plan $transport (Events) -Now $now -Clock { $now }
$state.FailBinding=$true;$partial=Events
Expect-Failure {Invoke-FinancialEnrollmentCoordinator $config Apply $transport $partial -ExpectedPlanHash $partialPlan.PlanHash -Now $now -Clock { $now }} 'Partial binding failure refused'
Check ($state.Bindings.Count -eq 0) 'Failed binding creates no broad success'
$state.FailBinding=$false
$state.Principals.Clear();$state.Bindings.Clear()
$fresh=Invoke-FinancialEnrollmentCoordinator $config Plan $transport (Events) -Now $now -Clock { $now }
$state.FailBinding=$true;$partial=Events;$journal=[System.Collections.Generic.List[object]]::new()
Expect-Failure {Invoke-FinancialEnrollmentCoordinator $config Apply $transport $partial -ExpectedPlanHash $fresh.PlanHash -Now $now -Clock { $now } -OnReceiptEvent {$journal.Add($args[0])}} 'Fresh partial create is retained'
Check ($state.Principals.Count -eq 1 -and $partial.Count -eq 1 -and $partial[0].Kind -eq 'PrincipalCreated') 'Partial principal receipt preserves exact ID'
Check ($journal.Count -eq 1) 'Mutation event reaches durable receipt callback'
$state.FailBinding=$false
$state.Principals.Values|ForEach-Object {$_.isActive=$false}
Expect-Failure {Invoke-FinancialEnrollmentCoordinator $config Plan $transport (Events) -Now $now -Clock { $now }} 'Inactive existing caller denied'
$state.Principals.Clear();$state.Bindings.Clear()
$clockState=@{Current=$now}
$slowReads={param($method,$path,$body) $answer=&$transport $method $path $body;$clockState.Current=[datetimeoffset]'2026-10-08T00:00:01Z';$answer}.GetNewClosure()
$writesBefore=@($state.Requests|Where-Object Method -eq 'POST').Count
Expect-Failure {Invoke-FinancialEnrollmentCoordinator $config Apply $slowReads (Events) -ExpectedPlanHash $fresh.PlanHash -Now $now -Clock {$clockState.Current}} 'Expiry advanced during planning denies first mutation'
Check (@($state.Requests|Where-Object Method -eq 'POST').Count -eq $writesBefore) 'Planning expiry makes no POST'
$clockState.Current=$now
$expiresAfterPrincipal={param($method,$path,$body) $answer=&$transport $method $path $body;if($method -eq 'POST' -and $path -eq '/iam/v1/principals'){$clockState.Current=[datetimeoffset]'2026-10-08T00:00:01Z'};$answer}.GetNewClosure()
$expiredPartial=Events
Expect-Failure {Invoke-FinancialEnrollmentCoordinator $config Apply $expiresAfterPrincipal $expiredPartial -ExpectedPlanHash $fresh.PlanHash -Now $now -Clock {$clockState.Current}} 'Expiry between mutations denies binding POST'
Check ($state.Principals.Count -eq 1 -and $state.Bindings.Count -eq 0 -and $expiredPartial.Count -eq 1) 'First accepted principal retained without expired binding'
$state.Principals.Clear();$state.Bindings.Clear()
$activePlan=Invoke-FinancialEnrollmentCoordinator $config Plan $transport (Events) -Now $now -Clock {$now}
$activeApply=Invoke-FinancialEnrollmentCoordinator $config Apply $transport (Events) -ExpectedPlanHash $activePlan.PlanHash -Now $now -Clock {$now}
# Model the reviewed API's active-only list: inactive physical rows remain stored.
foreach($row in $state.Bindings.Values){$row.expiresAt=$now.AddSeconds(-1).ToString('o')}
$activeOnly={param($method,$path,$body) if($method -eq 'GET' -and $path -match '^/iam/v1/principals/[^/]+/roles$'){return @{Status=200;Data=@()}};&$transport $method $path $body}.GetNewClosure()
$deletesBefore=@($state.Requests|Where-Object Method -eq 'DELETE').Count
$absentEvents=Events
$absence=Invoke-FinancialEnrollmentCoordinator $config Revoke $activeOnly $absentEvents -ExpectedPlanHash $activeApply.PlanHash -AppliedReceipt $activeApply -Now $now -Clock {$now}
Check ($absence.RevocationReadbackVerified -and @($absentEvents|Where-Object Kind -eq 'BindingAlreadyAbsent').Count -eq 2) 'AlreadyAbsent reports active-list absence'
Check ($state.Bindings.Count -eq 2) 'Active-list absence does not prove physical expired-row deletion'
Check (@($state.Requests|Where-Object Method -eq 'DELETE').Count -eq $deletesBefore) 'Absent active bindings issue no DELETE'
Write-Output "Financial enrollment component controls: $passed passed; 0 live requests; mocked normal API transport only."
