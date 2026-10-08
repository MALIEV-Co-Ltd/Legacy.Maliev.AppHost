Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('financial-cli-cleanup-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$originalToken=[Environment]::GetEnvironmentVariable('MALIEV_FINANCIAL_ENROLLMENT_OPERATOR_TOKEN')
$operator='00000000-0000-0000-0000-000000000001'
$payload=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((@{sub=$operator}|ConvertTo-Json -Compress))).TrimEnd('=').Replace('+','-').Replace('/','_')
$env:MALIEV_FINANCIAL_ENROLLMENT_OPERATOR_TOKEN="synthetic.$payload.synthetic"
$passed=0
try {
    foreach($missing in @('RoleId','IamOrigin')) {
        $configuration=@{IamOrigin='https://iam.fixture.invalid';Environment='Testing';OperatorPrincipalId=$operator;RoleId='roles.legacy.financial-read';ExpiresAtUtc=[datetimeoffset]::UtcNow.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:ssZ");ApproveGlobalFinancialScope=$true;SourceRevisions=@{Auth=('a'*40);Accounting=('b'*40);Defaults=('c'*40);IAM='4fe6642e5674013de9a3672505aec898fdcae0ed'}}
        $configuration.Remove($missing)
        $configPath=Join-Path $root "$missing-config.json";$receiptPath=Join-Path $root "$missing-receipt.json"
        [IO.File]::WriteAllText($configPath,($configuration|ConvertTo-Json -Depth 10))
        $observed=@{Closed=$null};$failed=$false
        try { & (Join-Path $PSScriptRoot '../financial-enrollment/invoke-financial-enrollment.ps1') -ConfigurationPath $configPath -Mode Plan -ReceiptPath $receiptPath -CleanupObserver {$observed.Closed=$args[0]} | Out-Null } catch {$failed=$true}
        if(-not $failed -or $null -eq $observed.Closed -or -not $observed.Closed['Receipt'] -or -not $observed.Closed['Journal'] -or -not $observed.Closed['HttpClient']){throw 'Malformed configuration did not dispose all owned resources.'}
        $passed++
        foreach($path in @($receiptPath, ($receiptPath + '.events.jsonl'))) {
            $probe=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$probe.Dispose();$passed++
        }
        $receipt=Get-Content -LiteralPath $receiptPath -Raw|ConvertFrom-Json
        if($receipt.Completed -or -not $receipt.ReconciliationRequired -or @($receipt.Events).Count){throw 'Malformed configuration receipt falsely claimed progress.'}
        $passed++
    }
} finally {
    [Environment]::SetEnvironmentVariable('MALIEV_FINANCIAL_ENROLLMENT_OPERATOR_TOKEN',$originalToken)
    $resolved=[IO.Path]::GetFullPath($root);$tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if(-not $resolved.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'financial-cli-cleanup-*'){throw 'Cleanup target escaped owned temporary root.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
    if(Test-Path -LiteralPath $resolved){throw 'Owned temporary test files remain.'}
}
Write-Output "Financial CLI cleanup controls: $passed passed; 0 HTTP requests; receipt/journal/client disposed and temporary files removed."
