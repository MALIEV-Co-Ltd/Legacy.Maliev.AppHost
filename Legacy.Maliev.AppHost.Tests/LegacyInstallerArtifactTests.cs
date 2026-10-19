using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Legacy.Maliev.AppHost.Topology;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class LegacyInstallerArtifactTests
{
    [Fact]
    public async Task Writer_DefaultIsJsonOnlyAndExplicitExportsAreCreateNew()
    {
        await InScratch(async directory =>
        {
            var output = Path.Combine(directory, "review.json");
            Assert.Equal(0, (await Writer(output, null, 1)).ExitCode);
            Assert.Equal(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-reviewed-ip"), await File.ReadAllTextAsync(output));
            Assert.Equal(["review.json"], Directory.GetFiles(directory)
                .Select(path => Path.GetFileName(path) ?? throw new InvalidOperationException("Scratch entry requires a file name.")).ToArray());
            var retained = Path.Combine(directory, "deploy-certificate-prod.ps1.txt");
            await File.WriteAllTextAsync(retained, "retained");
            var second = Path.Combine(directory, "second.json");
            Assert.NotEqual(0, (await Writer(second, directory, 3)).ExitCode);
            Assert.Equal("retained", await File.ReadAllTextAsync(retained));
            Assert.False(File.Exists(second));
            Assert.False(File.Exists(Path.Combine(directory, "deploy-certmanager.ps1.txt")));
            Assert.False(File.Exists(Path.Combine(directory, "deploy-clusterissuers.ps1.txt")));
        });
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 17)]
    [InlineData(false, true, 17)]
    [InlineData(true, true, 0)]
    public async Task ExportedInstaller_PreservesBackupsBranchesArgumentsWaitsAndNonzeroContinuation(bool backupExists, bool crdsExist, int nativeExit)
    {
        await InScratch(async directory =>
        {
            var output = Path.Combine(directory, "review.json");
            Assert.Equal(0, (await Writer(output, directory, 3)).ExitCode);
            Assert.Equal(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-reviewed-ip", 3), await File.ReadAllTextAsync(output));
            if (backupExists)
            {
                Directory.CreateDirectory(Path.Combine(directory, ".backup"));
                await File.WriteAllTextAsync(Path.Combine(directory, ".backup", "retained.txt"), "retained");
            }
            var trace = await Execute(directory, "deploy-certmanager.ps1.txt", crdsExist, nativeExit);
            var expected = CommonBefore.Concat(crdsExist ? Cleanup : []).Concat(CommonAfter).ToArray();
            Assert.Equal(crdsExist ? 33 : 14, trace.Calls.Length);
            Assert.Equal(expected.Length, trace.Calls.Length);
            for (var i = 0; i < expected.Length; i++) { Assert.Equal(expected[i].Split('|'), trace.Calls[i]); }
            Assert.Equal(crdsExist ? new[] { 5 } : [], trace.Sleeps);
            Assert.Equal(1, trace.Pauses);
            Assert.Equal(backupExists ? 0 : 1, trace.CreatedDirectories);
            foreach (var name in new[] { "certificates", "clusterissuers" })
            {
                var backup = Path.Combine(directory, ".backup", name + "-backup-20261019-000000.yaml");
                Assert.Equal("backup-review\n", (await File.ReadAllTextAsync(backup)).Replace("\r\n", "\n"));
            }
            if (backupExists) { Assert.Equal("retained", await File.ReadAllTextAsync(Path.Combine(directory, ".backup", "retained.txt"))); }
            Assert.Equal("namespace-review", trace.NamespaceInput.Trim());
            Assert.All(trace.Exits, value => Assert.Equal(nativeExit, value));
            Assert.Equal(trace.Calls.Length, trace.Exits.Length);
        });
    }

    [Theory]
    [InlineData("deploy-clusterissuers.ps1.txt", 3)]
    [InlineData("deploy-certificate-prod.ps1.txt", 1)]
    public async Task ExportedWrappers_PreserveNativeArgumentsAndPauseAfterNonzero(string name, int count)
    {
        await InScratch(async directory =>
        {
            Assert.Equal(0, (await Writer(Path.Combine(directory, "review.json"), directory, 3)).ExitCode);
            var trace = await Execute(directory, name, false, 17);
            var expected = name == "deploy-clusterissuers.ps1.txt"
                ? new[] { @"apply|-f|.\prod-clusterissuers.yaml", "describe|clusterissuer", "get|clusterissuer" }
                : new[] { "apply|-f|certificates-prod.yaml" };
            Assert.Equal(count, trace.Calls.Length);
            for (var i = 0; i < expected.Length; i++) { Assert.Equal(expected[i].Split('|'), trace.Calls[i]); }
            Assert.Equal(1, trace.Pauses);
            Assert.Empty(trace.Sleeps);
            Assert.All(trace.Exits, value => Assert.Equal(17, value));
            Assert.Equal(count, trace.Exits.Length);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Writer_ExportRequiresExplicitInstallerSchema(int schema)
    {
        await InScratch(async directory =>
        {
            Assert.NotEqual(0, (await Writer(Path.Combine(directory, "review.json"), directory, schema)).ExitCode);
            Assert.Empty(Directory.GetFiles(directory));
        });
    }

    [Theory]
    [InlineData("Start-Process kubectl")]
    [InlineData("& 'kubectl' get pods")]
    [InlineData("[IO.File]::ReadAllText('outside')")]
    [InlineData("$env:HOME")]
    [InlineData("New-Item -Path '../outside' -ItemType Directory")]
    public async Task Recorder_RejectsEscapeBeforeExecutingExport(string source)
    {
        await InScratch(async directory =>
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "rejected.ps1.txt"), source);
            await File.WriteAllTextAsync(Path.Combine(directory, "harness.ps1"), Harness);
            await File.WriteAllTextAsync(Path.Combine(directory, "recorder.ps1"), Recorder);
            Assert.NotEqual(0, (await Run(["-NoProfile", "-NonInteractive", "-File", Path.Combine(directory, "harness.ps1"), "rejected.ps1.txt", "false", "0"], directory)).ExitCode);
            Assert.False(File.Exists(Path.Combine(directory, "native.jsonl")));
            Assert.False(File.Exists(Path.Combine(directory, "result.json")));
        });
    }

    private static readonly string[] CommonBefore =
    [
        "get|certificates|-A|-o|yaml",
        "get|clusterissuers|-o|yaml",
        "create|namespace|cert-manager|--dry-run=client|-o|yaml",
        "apply|-f|-",
        "label|namespace|cert-manager|cert-manager.io/disable-validation=true|--overwrite",
        "get|crd"
    ];

    private static readonly string[] Cleanup =
    [
        "delete|namespace|cert-manager|--ignore-not-found=true",
        "delete|crd|certificaterequests.cert-manager.io|--ignore-not-found=true|--timeout=30s",
        "delete|crd|certificates.cert-manager.io|--ignore-not-found=true|--timeout=30s",
        "delete|crd|challenges.acme.cert-manager.io|--ignore-not-found=true|--timeout=30s",
        "delete|crd|clusterissuers.cert-manager.io|--ignore-not-found=true|--timeout=30s",
        "delete|crd|issuers.cert-manager.io|--ignore-not-found=true|--timeout=30s",
        "delete|crd|orders.acme.cert-manager.io|--ignore-not-found=true|--timeout=30s",
        """patch|crd|certificaterequests.cert-manager.io|-p|{\"metadata\":{\"finalizers\":[]}}|--type=merge""",
        """patch|crd|certificates.cert-manager.io|-p|{\"metadata\":{\"finalizers\":[]}}|--type=merge""",
        """patch|crd|challenges.acme.cert-manager.io|-p|{\"metadata\":{\"finalizers\":[]}}|--type=merge""",
        """patch|crd|clusterissuers.cert-manager.io|-p|{\"metadata\":{\"finalizers\":[]}}|--type=merge""",
        """patch|crd|issuers.cert-manager.io|-p|{\"metadata\":{\"finalizers\":[]}}|--type=merge""",
        """patch|crd|orders.acme.cert-manager.io|-p|{\"metadata\":{\"finalizers\":[]}}|--type=merge""",
        "delete|crd|certificaterequests.cert-manager.io|--force|--grace-period=0",
        "delete|crd|certificates.cert-manager.io|--force|--grace-period=0",
        "delete|crd|challenges.acme.cert-manager.io|--force|--grace-period=0",
        "delete|crd|clusterissuers.cert-manager.io|--force|--grace-period=0",
        "delete|crd|issuers.cert-manager.io|--force|--grace-period=0",
        "delete|crd|orders.acme.cert-manager.io|--force|--grace-period=0"
    ];

    private static readonly string[] CommonAfter =
    [
        "apply|-f|https://github.com/cert-manager/cert-manager/releases/download/v1.18.2/cert-manager.yaml",
        """patch|deployment|cert-manager|-n|cert-manager|--type=json|-p=[{"op": "add", "path": "/spec/template/spec/containers/0/args/-", "value": "--feature-gates=ACMEHTTP01IngressPathTypeExact=false"}]|--dry-run=client""",
        "wait|--namespace|cert-manager|--for=condition=Ready|pods|--selector=app.kubernetes.io/instance=cert-manager|--timeout=180s",
        "get|pods|--namespace|cert-manager",
        @"apply|-f|.\test-resources.yaml",
        "wait|certificate/selfsigned-cert|--namespace|cert-manager-test|--for=condition=Ready|--timeout=120s",
        "describe|certificate|selfsigned-cert|-n|cert-manager-test",
        @"delete|-f|.\test-resources.yaml"
    ];

    private static async Task<Trace> Execute(string directory, string name, bool crds, int nativeExit)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "harness.ps1"), Harness);
        await File.WriteAllTextAsync(Path.Combine(directory, "recorder.ps1"), Recorder);
        var result = await Run(["-NoProfile", "-NonInteractive", "-File", Path.Combine(directory, "harness.ps1"), name, crds ? "true" : "false", nativeExit.ToString(System.Globalization.CultureInfo.InvariantCulture)], directory);
        Assert.True(result.ExitCode == 0, result.Error);
        using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "result.json")));
        var calls = new List<string[]>();
        var exits = new List<int>();
        foreach (var line in await File.ReadAllLinesAsync(Path.Combine(directory, "native.jsonl")))
        {
            using var item = JsonDocument.Parse(line);
            calls.Add(item.RootElement.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()!).ToArray());
            exits.Add(item.RootElement.GetProperty("exit").GetInt32());
        }
        var root = summary.RootElement;
        return new Trace(calls.ToArray(), exits.ToArray(), root.GetProperty("sleeps").EnumerateArray().Select(value => value.GetInt32()).ToArray(),
            root.GetProperty("pauses").GetInt32(), root.GetProperty("created").GetInt32(),
            File.Exists(Path.Combine(directory, "namespace-input.txt")) ? await File.ReadAllTextAsync(Path.Combine(directory, "namespace-input.txt")) : "");
    }

    private sealed record Trace(string[][] Calls, int[] Exits, int[] Sleeps, int Pauses, int CreatedDirectories, string NamespaceInput);

    private static async Task<(int ExitCode, string Error)> Writer(string output, string? export, int schema)
    {
        var root = RepoRoot();
        var assembly = Path.Combine(root, "Legacy.Maliev.AppHost.Topology", "bin", "Release", "net10.0", "Legacy.Maliev.AppHost.Topology.dll");
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(assembly)));
        var arguments = new List<string>
        {
            "-NoProfile", "-NonInteractive", "-File", Path.Combine(root, "scripts", "write-edge-review-package.ps1"),
            "-AcmeEmail", "owner@maliev.test", "-ExistingStaticIpName", "existing-reviewed-ip", "-OutputPath", output,
            "-SchemaVersion", schema.ToString(System.Globalization.CultureInfo.InvariantCulture), "-ReviewedAssemblySha256", hash
        };
        if (export is not null) { arguments.AddRange(["-ExportDirectory", export]); }
        return await Run(arguments, Path.GetDirectoryName(output)!);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "scripts", "write-edge-review-package.ps1"))) { directory = directory.Parent; }
        return directory?.FullName ?? throw new DirectoryNotFoundException("AppHost review writer was not found.");
    }

    private static async Task<(int ExitCode, string Error)> Run(IEnumerable<string> arguments, string directory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("pwsh")
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var value in arguments) { process.StartInfo.ArgumentList.Add(value); }
        Assert.True(process.Start());
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(300));
            await process.WaitForExitAsync(deadline.Token);
            Assert.True((await stdout).Length < 32768);
            var error = await stderr;
            Assert.True(error.Length < 32768);
            return (process.ExitCode, error);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            var lease = Path.Combine(directory, "recorder-lease.json");
            if (File.Exists(lease))
            {
                using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(lease));
                try
                {
                    using var child = Process.GetProcessById(receipt.RootElement.GetProperty("pid").GetInt32());
                    if (!child.HasExited && child.StartTime.ToUniversalTime().Ticks == receipt.RootElement.GetProperty("startTicks").GetInt64()
                        && string.Equals(child.MainModule?.FileName, receipt.RootElement.GetProperty("executable").GetString(), StringComparison.Ordinal))
                    {
                        child.Kill();
                        await child.WaitForExitAsync();
                    }
                }
                catch (ArgumentException) { /* The exact recorded process has already exited. */ }
            }
        }
    }

    private static async Task InScratch(Func<string, Task> action)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var name = "legacy-installer-review-" + Guid.NewGuid().ToString("N");
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(path), name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Scratch path escaped its intended root.");
        }
        Directory.CreateDirectory(path);
        try { await action(path); }
        finally
        {
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), root, StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(path), name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Cleanup path escaped its intended root.");
            }
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(path));
            while (pending.TryPop(out var directory))
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("Scratch cleanup cannot follow a reparse point.");
                }
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InvalidOperationException("Scratch cleanup cannot follow a reparse point.");
                    }
                    if (entry is DirectoryInfo child) { pending.Push(child); }
                }
            }
            Directory.Delete(path, true);
        }
    }

    // The only native child is a fixed test-local recorder; exported input cannot select its executable.
    private const string Harness = """
        param([string]$Name, [string]$Crds, [int]$NativeExit)
        $ErrorActionPreference = 'Stop'
        $PSNativeCommandUseErrorActionPreference = $false
        $env:PATH = ''
        $env:REVIEW_CRDS = $Crds
        $env:REVIEW_EXIT = [string]$NativeExit
        $env:REVIEW_ROOT = $PWD.Path
        $script:native = [IO.Path]::Combine($PSHOME, $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' }))
        $script:recorder = [IO.Path]::Combine($PWD.Path, 'recorder.ps1')
        $source = [IO.File]::ReadAllText([IO.Path]::Combine($PWD.Path, $Name))
        $tokens = $null
        $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
        if ($errors.Count) { throw 'Export parse rejected.' }
        $commands = @('kubectl', 'Test-Path', 'Write-Host', 'New-Item', 'Out-Null', 'Get-Date', 'Select-String', 'Start-Sleep', 'pause')
        foreach ($node in $ast.FindAll({ param($n) $true }, $true)) {
            if ($node -is [Management.Automation.Language.CommandAst]) {
                $command = $node.GetCommandName()
                if ($command -cnotin $commands -or $node.InvocationOperator -ne 'Unknown') { throw 'Export command rejected.' }
                if ($command -in @('Test-Path', 'New-Item') -and $node.Extent.Text -notmatch ' -Path "\.backup"') { throw 'Export path rejected.' }
            }
            if ($node -is [Management.Automation.Language.VariableExpressionAst] -and
                $node.VariablePath.UserPath -cnotin @('certmanager_version', 'existingCRDs', 'null')) { throw 'Export variable rejected.' }
            if ($node -is [Management.Automation.Language.InvokeMemberExpressionAst] -or
                $node -is [Management.Automation.Language.MemberExpressionAst] -or
                $node -is [Management.Automation.Language.TypeExpressionAst] -or
                $node -is [Management.Automation.Language.FunctionDefinitionAst] -or
                $node -is [Management.Automation.Language.ScriptBlockExpressionAst] -or
                $node -is [Management.Automation.Language.LoopStatementAst] -or
                $node -is [Management.Automation.Language.TrapStatementAst]) { throw 'Export escape rejected.' }
            if ($node -is [Management.Automation.Language.FileRedirectionAst] -and
                $node.Location.Extent.Text -ne '$null' -and
                $node.Location.Extent.Text -notmatch '^\.backup/(certificates|clusterissuers)-backup-\$\(Get-Date -Format ''yyyyMMdd-HHmmss''\)\.yaml$') { throw 'Export redirection rejected.' }
        }
        $script:sleeps = [Collections.Generic.List[int]]::new()
        $script:pauses = 0
        $script:created = 0
        function Get-Date { param([string]$Format) if ($Format -cne 'yyyyMMdd-HHmmss') { throw 'Date format changed.' }; '20261019-000000' }
        function Start-Sleep { param([int]$Seconds) $script:sleeps.Add($Seconds) }
        function pause { $script:pauses++ }
        function New-Item {
            param([string]$Path, [string]$ItemType)
            if ($Path -cne '.backup' -or $ItemType -cne 'Directory') { throw 'Directory scope changed.' }
            $script:created++
            Microsoft.PowerShell.Management\New-Item -Path $Path -ItemType $ItemType
        }
        function kubectl {
            $arguments = @($args)
            $piped = @($input)
            $info = [Diagnostics.ProcessStartInfo]::new($script:native)
            $info.UseShellExecute = $false
            $info.RedirectStandardInput = $true
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            foreach ($value in @('-NoProfile', '-NonInteractive', '-File', $script:recorder) + $arguments) { $info.ArgumentList.Add([string]$value) }
            $child = [Diagnostics.Process]::new()
            $child.StartInfo = $info
            $started = $false
            try {
                $started = $child.Start()
                if (-not $started) { throw 'Recorder did not start.' }
                $lease = [IO.Path]::Combine($PWD.Path, 'recorder-lease.json')
                [IO.File]::WriteAllText($lease, (@{ pid = $child.Id; startTicks = $child.StartTime.ToUniversalTime().Ticks; executable = $child.MainModule.FileName } | ConvertTo-Json -Compress))
                $out = $child.StandardOutput.ReadToEndAsync()
                $err = $child.StandardError.ReadToEndAsync()
                if ($piped.Count) { $child.StandardInput.WriteLine(($piped -join [Environment]::NewLine)) }
                $child.StandardInput.Close()
                if (-not $child.WaitForExit(8000)) { throw 'Recorder deadline exceeded.' }
                $text = $out.GetAwaiter().GetResult()
                $errorText = $err.GetAwaiter().GetResult()
                if ($text.Length -gt 4096 -or $errorText.Length -gt 4096) { throw 'Recorder output exceeded bound.' }
                $global:LASTEXITCODE = $child.ExitCode
                if ($text) { $text.TrimEnd([char[]]@([char]13, [char]10)) }
            } finally {
                if ($started -and -not $child.HasExited) { $child.Kill(); $child.WaitForExit() }
                $child.Dispose()
                if ($started -and [IO.File]::Exists($lease)) { [IO.File]::Delete($lease) }
            }
        }
        & ([scriptblock]::Create($source))
        [IO.File]::WriteAllText([IO.Path]::Combine($PWD.Path, 'result.json'),
            (@{ sleeps = @($script:sleeps.ToArray()); pauses = $script:pauses; created = $script:created } | ConvertTo-Json -Compress))
        """;

    private const string Recorder = """
        $ErrorActionPreference = 'Stop'
        $values = @($args | ForEach-Object { [string]$_ })
        $root = $env:REVIEW_ROOT
        $exit = [int]$env:REVIEW_EXIT
        $line = @{ arguments = $values; exit = $exit } | ConvertTo-Json -Compress -Depth 4
        [IO.File]::AppendAllText([IO.Path]::Combine($root, 'native.jsonl'), $line + [char]10)
        if ($values.Count -eq 3 -and $values[0] -eq 'apply' -and $values[1] -eq '-f' -and $values[2] -eq '-') {
            [IO.File]::WriteAllText([IO.Path]::Combine($root, 'namespace-input.txt'), [Console]::In.ReadToEnd())
        }
        if ($values.Count -eq 2 -and $values[0] -eq 'get' -and $values[1] -eq 'crd') {
            if ($env:REVIEW_CRDS -eq 'true') { 'certificates.cert-manager.io' } else { 'unrelated.example.test' }
        } elseif ($values.Count -ge 2 -and $values[0] -eq 'get' -and $values[1] -in @('certificates', 'clusterissuers')) {
            'backup-review'
        } elseif ($values.Count -ge 2 -and $values[0] -eq 'create' -and $values[1] -eq 'namespace') {
            'namespace-review'
        }
        exit $exit
        """;
}
