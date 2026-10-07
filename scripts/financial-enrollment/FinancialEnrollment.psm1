Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-FinancialPlanHash($Plan) {
    $json = $Plan | ConvertTo-Json -Depth 20 -Compress
    [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($json)))
}

function Add-FinancialReceiptEvent($Events, $Event, $OnReceiptEvent) {
    $Events.Add($Event)
    if ($null -ne $OnReceiptEvent) { & $OnReceiptEvent $Event }
}

function Assert-FinancialMutationDeadline($Configuration, $Clock) {
    $current = [datetimeoffset](& $Clock)
    if ([datetimeoffset]::Parse($Configuration.ExpiresAtUtc) -le $current.AddSeconds(20)) {
        throw 'The approved expiry no longer covers the bounded mutation request; replan with a reviewed future expiry.'
    }
}

function Assert-FinancialConfiguration($Configuration, [datetimeoffset]$Now) {
    $uri = $null
    if (-not [Uri]::TryCreate($Configuration.IamOrigin, [UriKind]::Absolute, [ref]$uri) -or
        $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/' -or
        $Configuration.IamOrigin -cne $uri.GetLeftPart([UriPartial]::Authority) -or
        ($uri.Scheme -ne 'https' -and -not ($uri.Scheme -eq 'http' -and $uri.IsLoopback -and $Configuration.Environment -ceq 'Testing'))) {
        throw 'An exact approved HTTPS IAM origin is required; only Testing allows loopback HTTP.'
    }
    $operatorId = [guid]::Empty
    $expiry = [datetimeoffset]::MinValue
    if (-not [guid]::TryParseExact($Configuration.OperatorPrincipalId, 'D', [ref]$operatorId) -or $operatorId -eq [guid]::Empty -or
        $Configuration.ExpiresAtUtc -cnotmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$' -or
        -not [datetimeoffset]::TryParse($Configuration.ExpiresAtUtc, [ref]$expiry) -or $expiry.Offset -ne [timespan]::Zero -or $expiry -le $Now -or
        $Configuration.ApproveGlobalFinancialScope -ne $true -or
        [string]::IsNullOrWhiteSpace($Configuration.RoleId) -or $Configuration.RoleId.Length -gt 128 -or
        $Configuration.RoleId -match '[\s*/\\]' -or $Configuration.RoleId.StartsWith('roles.workloads.', [StringComparison]::Ordinal) -or
        $Configuration.RoleId.StartsWith('roles.platform.', [StringComparison]::Ordinal)) {
        throw 'Explicit operator GUID, approved financial role, future UTC expiry and global-scope approval are required.'
    }
    foreach ($component in @('Auth', 'Accounting', 'Defaults', 'IAM')) {
        if ($Configuration.SourceRevisions.$component -cnotmatch '^[0-9a-f]{40}$') { throw 'Exact approved source revisions are required.' }
    }
    if ($Configuration.SourceRevisions.IAM -cne '4fe6642e5674013de9a3672505aec898fdcae0ed') { throw 'Unsupported IAM API contract revision.' }
}

function Invoke-FinancialEnrollmentCoordinator {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Configuration,
        [Parameter(Mandatory)][ValidateSet('Plan','Verify','Apply','Revoke')][string]$Mode,
        [Parameter(Mandatory)][scriptblock]$Request,
        [Parameter(Mandatory)][AllowEmptyCollection()][System.Collections.Generic.List[object]]$ReceiptEvents,
        [string]$ExpectedPlanHash,
        $AppliedReceipt,
        [scriptblock]$OnReceiptEvent,
        [scriptblock]$OnReviewedPlan,
        [scriptblock]$Clock = { [datetimeoffset]::UtcNow },
        [datetimeoffset]$Now = [datetimeoffset]::UtcNow
    )
    Assert-FinancialConfiguration $Configuration $Now
    $permission = 'legacy.accounting-financial-ownership.read'
    $catalogue = & $Request 'GET' "/iam/v1/permissions/$permission" $null
    if ($catalogue.Status -ne 200 -or $catalogue.Data.permissionId -cne $permission -or
        $catalogue.Data.serviceName -cne 'legacy' -or $catalogue.Data.resourceType -cne 'accounting-financial-ownership' -or
        $catalogue.Data.action -cne 'read') { throw 'The normal financial catalogue row is absent or mismatched.' }
    $role = & $Request 'GET' ('/iam/v1/roles/' + [Uri]::EscapeDataString($Configuration.RoleId)) $null
    if ($role.Status -ne 200 -or $role.Data.roleId -cne $Configuration.RoleId -or
        @($role.Data.permissionIds).Count -ne 1 -or $role.Data.permissionIds[0] -cne $permission) {
        throw 'The approved role must contain exactly the financial read permission.'
    }
    $callers = @()
    foreach ($clientId in @('legacy-auth','legacy-quotation')) {
        $email = "service:$clientId@serviceaccount.maliev.local"
        $principal = & $Request 'GET' ('/iam/v1/principals/by-email/' + [Uri]::EscapeDataString($email)) $null
        $principalId = $null; $bindingId = $null
        if ($principal.Status -eq 200) {
            $id = [guid]::Empty
            if (-not [guid]::TryParseExact($principal.Data.principalId, 'D', [ref]$id) -or $id -eq [guid]::Empty -or
                $principal.Data.email -cne $email -or $principal.Data.principalType -cne 'service_account' -or $principal.Data.isActive -ne $true) {
                throw 'Existing principal does not match the exact active service caller.'
            }
            $principalId = $id.ToString('D')
            $bindings = & $Request 'GET' "/iam/v1/principals/$principalId/roles" $null
            if ($bindings.Status -ne 200) { throw 'Normal binding readback failed.' }
            $rows = @($bindings.Data)
            if ($rows.Count -gt 1) { throw 'Unknown existing role bindings require reconciliation.' }
            if ($rows.Count -eq 1) {
                $b = $rows[0]; $bid = [guid]::Empty
                if (-not [guid]::TryParseExact($b.bindingId, 'D', [ref]$bid) -or $bid -eq [guid]::Empty -or
                    $b.principalId -cne $principalId -or $b.roleId -cne $Configuration.RoleId -or $null -ne $b.resourcePath -or
                    [datetimeoffset]::Parse($b.expiresAt) -ne [datetimeoffset]::Parse($Configuration.ExpiresAtUtc)) {
                    throw 'Existing binding does not match the reviewed finite global financial scope.'
                }
                $bindingId = $bid.ToString('D')
            }
        } elseif ($principal.Status -ne 404) { throw 'Normal principal readback failed.' }
        $callers += [ordered]@{ ClientId=$clientId; Email=$email; PrincipalId=$principalId; BindingId=$bindingId }
    }
    $plan = [ordered]@{ IamOrigin=$Configuration.IamOrigin; Environment=$Configuration.Environment; OperatorPrincipalId=$Configuration.OperatorPrincipalId;
        SourceRevisions=$Configuration.SourceRevisions; RoleId=$Configuration.RoleId; PermissionId=$permission;
        ResourcePath=$null; ExpiresAtUtc=$Configuration.ExpiresAtUtc; Callers=$callers }
    $hash = Get-FinancialPlanHash $plan
    $reviewedPlan = $plan | ConvertTo-Json -Depth 20 | ConvertFrom-Json -AsHashtable -Depth 20
    if ($Mode -eq 'Plan') { return [ordered]@{ Plan=$plan; PlanHash=$hash; SourceRevisionsDeclaredNotRuntimeVerified=$true } }
    if ($Mode -eq 'Verify') {
        if (@($callers | Where-Object { -not $_.PrincipalId -or -not $_.BindingId }).Count) { throw 'Enrollment is incomplete.' }
        return [ordered]@{ Plan=$plan; PlanHash=$hash; EnrollmentReadbackVerified=$true; GenuineFinancialHttpAcceptance=$false }
    }
    if ($Mode -eq 'Apply' -and ($ExpectedPlanHash -cnotmatch '^[0-9a-f]{64}$' -or $ExpectedPlanHash -cne $hash)) {
        throw 'The exact current plan hash is required; changed state must be replanned.'
    }
    if ($Mode -eq 'Apply' -and $null -ne $OnReviewedPlan) { & $OnReviewedPlan $reviewedPlan $hash }
    if ($Mode -eq 'Revoke') {
        if ($null -eq $AppliedReceipt -or $AppliedReceipt.IamOrigin -cne $Configuration.IamOrigin -or
            $AppliedReceipt.RoleId -cne $Configuration.RoleId -or $AppliedReceipt.PlanHash -cne $ExpectedPlanHash -or
            (Get-FinancialPlanHash $AppliedReceipt.ReviewedPlan) -cne $ExpectedPlanHash) {
            throw 'An exact prior apply receipt and its plan hash are required for revocation.'
        }
        foreach ($event in @($AppliedReceipt.Events | Where-Object Kind -eq 'BindingCreated')) {
            $original = @($AppliedReceipt.ReviewedPlan.Callers | Where-Object ClientId -eq $event.ClientId)
            if ($original.Count -ne 1 -or $original[0].BindingId -or
                ($original[0].PrincipalId -and $original[0].PrincipalId -cne $event.PrincipalId)) {
                throw 'A pre-existing or unreviewed binding cannot be claimed for revocation.'
            }
            $caller = @($callers | Where-Object ClientId -eq $event.ClientId)
            if ($caller.Count -ne 1 -or $caller[0].PrincipalId -cne $event.PrincipalId -or
                ($caller[0].BindingId -and $caller[0].BindingId -cne $event.BindingId)) {
                throw 'Current ownership does not match the exact created binding receipt.'
            }
            if (-not $caller[0].BindingId) {
                Add-FinancialReceiptEvent $ReceiptEvents ([ordered]@{Kind='BindingAlreadyAbsent';ClientId=$event.ClientId;PrincipalId=$event.PrincipalId;BindingId=$event.BindingId}) $OnReceiptEvent
                continue
            }
            Assert-FinancialMutationDeadline $Configuration $Clock
            $deleted = & $Request 'DELETE' "/iam/v1/principals/$($event.PrincipalId)/roles/$($event.BindingId)" $null
            if ($deleted.Status -ne 204) { throw 'Exact binding revocation failed.' }
            Add-FinancialReceiptEvent $ReceiptEvents ([ordered]@{Kind='BindingRevoked';ClientId=$event.ClientId;PrincipalId=$event.PrincipalId;BindingId=$event.BindingId}) $OnReceiptEvent
            $readback = & $Request 'GET' "/iam/v1/principals/$($event.PrincipalId)/roles" $null
            if ($readback.Status -ne 200 -or @($readback.Data | Where-Object bindingId -eq $event.BindingId).Count) { throw 'Revocation readback failed.' }
        }
        return [ordered]@{ PlanHash=$ExpectedPlanHash; RevocationReadbackVerified=$true; GenuineFinancialHttpAcceptance=$false }
    }
    foreach ($caller in $callers) {
        if (-not $caller.PrincipalId) {
            Assert-FinancialMutationDeadline $Configuration $Clock
            $created = & $Request 'POST' '/iam/v1/principals' @{principalType='service_account';email=$caller.Email}
            $id = [guid]::Empty
            if ($created.Status -ne 201 -or -not [guid]::TryParseExact($created.Data.principalId, 'D', [ref]$id) -or $id -eq [guid]::Empty) {
                throw 'Principal create outcome requires reconciliation; no blind retry or cleanup.'
            }
            $caller.PrincipalId=$id.ToString('D')
            Add-FinancialReceiptEvent $ReceiptEvents ([ordered]@{Kind='PrincipalCreated';ClientId=$caller.ClientId;PrincipalId=$caller.PrincipalId}) $OnReceiptEvent
            $readback = & $Request 'GET' ('/iam/v1/principals/by-email/' + [Uri]::EscapeDataString($caller.Email)) $null
            if ($readback.Status -ne 200 -or $readback.Data.principalId -cne $caller.PrincipalId -or
                $readback.Data.email -cne $caller.Email -or $readback.Data.principalType -cne 'service_account' -or $readback.Data.isActive -ne $true) { throw 'Created principal readback failed.' }
        }
        if (-not $caller.BindingId) {
            Assert-FinancialMutationDeadline $Configuration $Clock
            $bound = & $Request 'POST' "/iam/v1/principals/$($caller.PrincipalId)/roles" @{roleId=$Configuration.RoleId;resourcePath=$null;expiresAt=$Configuration.ExpiresAtUtc}
            $id = [guid]::Empty
            if ($bound.Status -ne 200 -or -not [guid]::TryParseExact($bound.Data.bindingId, 'D', [ref]$id) -or $id -eq [guid]::Empty) {
                throw 'Binding create outcome requires reconciliation; no broad rollback.'
            }
            $caller.BindingId=$id.ToString('D')
            Add-FinancialReceiptEvent $ReceiptEvents ([ordered]@{Kind='BindingCreated';ClientId=$caller.ClientId;PrincipalId=$caller.PrincipalId;BindingId=$caller.BindingId}) $OnReceiptEvent
            $check = & $Request 'GET' "/iam/v1/principals/$($caller.PrincipalId)/roles" $null
            if ($check.Status -ne 200 -or @($check.Data).Count -ne 1 -or $check.Data[0].bindingId -cne $caller.BindingId -or
                $check.Data[0].principalId -cne $caller.PrincipalId -or $check.Data[0].roleId -cne $Configuration.RoleId -or
                $null -ne $check.Data[0].resourcePath -or [datetimeoffset]::Parse($check.Data[0].expiresAt) -ne [datetimeoffset]::Parse($Configuration.ExpiresAtUtc)) {
                throw 'Created binding readback failed.'
            }
        }
    }
    Assert-FinancialMutationDeadline $Configuration $Clock
    [ordered]@{ IamOrigin=$Configuration.IamOrigin;RoleId=$Configuration.RoleId;PlanHash=$hash;ReviewedPlan=$reviewedPlan;Events=@($ReceiptEvents.ToArray());EnrollmentReadbackVerified=$true;GenuineFinancialHttpAcceptance=$false }
}
Export-ModuleMember -Function Invoke-FinancialEnrollmentCoordinator
