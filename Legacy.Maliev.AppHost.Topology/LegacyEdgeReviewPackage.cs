using System.Text.Json;

namespace Legacy.Maliev.AppHost.Topology;

/// <summary>Renders the retained source edge intentions for review without applying infrastructure.</summary>
public static class LegacyEdgeReviewPackage
{
    /// <summary>Gets the only namespace permitted in a legacy edge review.</summary>
    public const string Namespace = "maliev-legacy";

    private const string IngressName = "legacy-maliev-edge";
    private const string IssuerName = "legacy-maliev-letsencrypt-prod";
    private const string LineChatbotHost = "line-chatbot.maliev.com";
    private const string SourceLineChatbotIngressName = "line-chatbot-ingress";

    private static readonly (string Path, string Service)[] ApiRoutes =
    [
        ("/auth", "auth"), ("/countries", "country"), ("/currencies", "catalog"),
        ("/customers", "customer"), ("/materials", "catalog"), ("/suppliers", "procurement"),
        ("/orderstatuses", "order"), ("/uploads", "file"), ("/orders", "order"),
        ("/emails", "notification"), ("/quotations", "quotation"), ("/employees", "employee"),
        ("/payments", "accounting"), ("/pdfs", "document"), ("/jobs", "career"),
        ("/invoices", "accounting"), ("/messages", "contact"), ("/purchaseorders", "procurement"),
        ("/receipts", "accounting"), ("/quotationrequests", "quotation"),
        // Retain every legacy path above while forwarding the committed producers' canonical
        // API/Scalar prefixes unchanged. Prefix matching is by path segment, not string prefix.
        ("/country", "country"), ("/documents", "document"), ("/customer", "customer"),
        ("/order", "order"), ("/quotation", "quotation"), ("/employee", "employee"),
        ("/catalog", "catalog"), ("/procurement", "procurement"), ("/file", "file"),
        ("/Jobs", "career"), ("/accounting", "accounting"),
        // Retain exact aliases and forward the source-exclusive v1 namespaces so action case/trailing forms reach Auth.
        ("/customers/v1/validate", "auth"), ("/Employees/v1/validate", "auth"),
        ("/customers/v1", "auth"), ("/customers/V1", "auth"),
        ("/employees/v1", "auth"), ("/employees/V1", "auth"),
        ("/Employees/v1", "auth"), ("/Employees/V1", "auth")
    ];

    /// <summary>Reviews supplied legacy child outcomes without executing any deployment or directory operation.</summary>
    /// <param name="acmeEmail">The explicitly supplied ACME contact for the unchanged base envelope.</param>
    /// <param name="existingStaticIpName">The explicitly supplied existing static-IP name.</param>
    /// <param name="suppliedObservationJson">Closed, bounded caller-supplied observations, never collected live.</param>
    /// <returns>A schema-four envelope whose modeled source result is separate from real execution.</returns>
    public static string RenderReleaseSelectorReview(string acmeEmail, string existingStaticIpName, string suppliedObservationJson)
    {
        ArgumentNullException.ThrowIfNull(suppliedObservationJson);
        if (new System.Text.UTF8Encoding(false, true).GetByteCount(suppliedObservationJson) > 32 * 1024)
        {
            throw new ArgumentException("Supplied selector observations exceed 32 KiB.", nameof(suppliedObservationJson));
        }
        using var document = JsonDocument.Parse(suppliedObservationJson, new JsonDocumentOptions { MaxDepth = 8 });
        var input = document.RootElement;
        RejectDuplicateSelectorProperties(input);
        RequireSelectorProperties(input, "schemaVersion", "selector", "children");
        if (input.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number
            || !input.GetProperty("schemaVersion").TryGetInt32(out var inputVersion) || inputVersion != 1)
        {
            throw new JsonException("Only supplied-observation schema one is supported.");
        }
        if (input.GetProperty("selector").ValueKind != JsonValueKind.String)
        {
            throw new JsonException("An explicit source selector is required.");
        }
        var selector = input.GetProperty("selector").GetString()!;
        var source = ReleaseSelectorSource(selector);
        var children = input.GetProperty("children");
        if (children.ValueKind != JsonValueKind.Array) { throw new JsonException("Children must be an array."); }
        var reports = new Dictionary<string, (bool? File, bool? InvocationFailed, int? ExitCode)>(StringComparer.Ordinal);
        // Validate every supplied record before considering the first modeled step.
        foreach (var child in children.EnumerateArray())
        {
            RequireSelectorProperties(child, "sourceService", "deployScriptIsFile", "invocationFailed", "exitCode");
            if (child.GetProperty("sourceService").ValueKind != JsonValueKind.String)
            {
                throw new JsonException("A selected source service is required.");
            }
            var service = child.GetProperty("sourceService").GetString()!;
            if (!source.Members.Contains(service, StringComparer.Ordinal)) { throw new JsonException("Source service is outside the selected group."); }
            var file = SelectorBoolean(child, "deployScriptIsFile");
            var invocationFailed = SelectorBoolean(child, "invocationFailed");
            int? exitCode = null;
            if (child.TryGetProperty("exitCode", out var exit) && exit.ValueKind != JsonValueKind.Null)
            {
                if (exit.ValueKind != JsonValueKind.Number || !exit.TryGetInt32(out var value))
                {
                    throw new JsonException("A supplied exit code must be an Int32 or null.");
                }
                exitCode = value;
            }
            if ((file == false && (invocationFailed.HasValue || exitCode.HasValue))
                || (invocationFailed == true && exitCode.HasValue))
            {
                throw new JsonException("Supplied observations contradict source invocation preconditions.");
            }
            if (!reports.TryAdd(service, (file, invocationFailed, exitCode)))
            {
                throw new JsonException("Duplicate source service observations are not permitted.");
            }
        }
        var trace = new List<object>();
        var stopped = false;
        var status = "ReportedComplete";
        int? modeledSourceExitCode = 0;
        foreach (var service in source.Members)
        {
            var provided = reports.TryGetValue(service, out var report);
            var consumed = !stopped;
            var outcome = "NotReached";
            if (consumed)
            {
                if (!provided || report.File is null) { outcome = "Unknown"; }
                else if (report.File == false) { outcome = "MissingLeaf"; }
                else if (report.InvocationFailed is null) { outcome = "Unknown"; }
                else if (report.InvocationFailed == true) { outcome = "InvocationFailed"; }
                else if (report.ExitCode is null) { outcome = "Unknown"; }
                else if (report.ExitCode != 0) { outcome = "ChildExitFailure"; }
                else { outcome = "ReportedSuccess"; }
                if (outcome != "ReportedSuccess")
                {
                    stopped = true;
                    status = outcome == "Unknown" ? "Unknown" : "ReportedFailure";
                    modeledSourceExitCode = outcome == "Unknown" ? null : 1;
                }
            }
            trace.Add(new
            {
                sourceService = service,
                sourceDeployScriptRelativePath = service + "/deploy.ps1",
                observationProvided = provided,
                consumed,
                outcome,
                reportedExitCode = consumed && provided ? report.ExitCode : null,
                executionVerified = false
            });
        }
        var root = System.Text.Json.Nodes.JsonNode.Parse(Render(acmeEmail, existingStaticIpName, 3))!.AsObject();
        root["schemaVersion"] = 4;
        root["releaseSelectorReview"] = JsonSerializer.SerializeToNode(new
        {
            semantics = "SuppliedSourceControlFlowReview",
            sourceCheckpoint = "135e526d0dab85c415b3afdcefd7b70fe2c82e2f",
            sourceControlFlowCommit = "72163e9ae11f39f6579423841a2e20529b986fab",
            sourceControlFlowParent = "91a8f128ae155e2fe046015e8d66f9ba4b306b30",
            sourceLoggerRemovalCommit = "9e51e6c5da29de8e617b65b59d46882cde6d3b64",
            selector,
            sourceSelectorPath = source.Path,
            sourceSelectorBlob = source.Blob,
            selectedSourceServices = source.Members,
            status,
            modeledSourceExitCode,
            observationsVerified = false,
            sourceHelperExecutionVerified = false,
            callerDirectoryRestoredVerified = false,
            successorMappingVerified = false,
            sourceRequiresInnerPopLocation = true,
            sourceRequiresOuterCallerDirectoryRestore = true,
            sourceLeafCheck = "Test-Path -LiteralPath -PathType Leaf",
            sourceShellSelection = new
            {
                core = "PSHOME/pwsh.exe",
                other = "PSHOME/powershell.exe",
                arguments = new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "source deploy.ps1" },
                invocationPerformed = false
            },
            retiredSuccessorDispositions = source.Members.Contains("maliev.predictionservice.api", StringComparer.Ordinal)
                ? new object[] { new { sourceService = "maliev.predictionservice.api", disposition = "OwnerRetired", runtimeRegistered = false, recreateRuntimeAllowed = false } }
                : Array.Empty<object>(),
            unresolvedGates = new[]
            {
                "Supplied outcomes are not live verification of child scripts or source helper execution",
                "Source directory restoration and deployment orchestration require independent acceptance",
                "Successor consolidation and release ownership are not verified by this model",
                "Prediction retains source selection but its owner-retired successor must not be recreated"
            },
            trace
        });
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static (string Path, string Blob, string[] Members) ReleaseSelectorSource(string selector)
    {
        string[] all =
        [
            "maliev.authservice.api",
            "maliev.countryservice.api",
            "maliev.currencyservice.api",
            "maliev.customerservice.api",
            "maliev.emailservice.api",
            "maliev.employeeservice.api",
            "maliev.intranet",
            "maliev.invoiceservice.api",
            "maliev.jobservice.api",
            "maliev.materialservice.api",
            "maliev.messageservice.api",
            "maliev.orderservice.api",
            "maliev.orderstatusservice.api",
            "maliev.paymentservice.api",
            "maliev.pdfservice.api",
            "maliev.predictionservice.api",
            "maliev.purchaseorderservice.api",
            "maliev.quotationrequestservice.api",
            "maliev.quotationservice.api",
            "maliev.receiptservice.api",
            "maliev.supplierservice.api",
            "maliev.uploadservice.api",
            "maliev.web"
        ];
        return selector switch
        {
            "all" => ("deploy_all.ps1",
                "f74d2c83fecd5f3e9ff49712caa46a09f12677a4", all),
            "api" => ("deploy_api.ps1",
                "c6604936515c87d9a9337c4b84a72ceb94e63c7e", all.Where(member => member is not "maliev.intranet" and not "maliev.web").ToArray()),
            "web" => ("deploy_web.ps1",
                "c0d90c21e655f66e30e35cb08b29fbf62428446b", ["maliev.web"]),
            "intranet" => ("deploy_intranet.ps1",
                "4fb465cb04f7c174ec8130ca8c4ecf1df424d6ba", ["maliev.intranet"]),
            "pdf" => ("deploy_pdf.ps1",
                "240e57135f446a56e1f507aa255f338057652ffc", ["maliev.pdfservice.api"]),
            _ => throw new JsonException("Unknown source selector.")
        };
    }

    private static bool? SelectorBoolean(JsonElement child, string name)
    {
        if (!child.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) { return null; }
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { throw new JsonException("A supplied observation must be a boolean or null."); }
        return value.GetBoolean();
    }

    private static void RequireSelectorProperties(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) { throw new JsonException("An observation object is required."); }
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) { throw new JsonException("Unknown observation property."); }
        }
    }

    private static void RejectDuplicateSelectorProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) { throw new JsonException("Duplicate observation properties are not permitted."); }
                RejectDuplicateSelectorProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray()) { RejectDuplicateSelectorProperties(child); }
        }
    }

    /// <summary>Reviews the final source Web port and backend association without claiming workload adoption.</summary>
    /// <param name="acmeEmail">The explicit ACME contact for the unchanged edge review.</param>
    /// <param name="existingStaticIpName">The explicit existing static-IP name for the unchanged edge review.</param>
    /// <returns>An inert schema-five contract with a deliberately incomplete Deployment fragment.</returns>
    public static string RenderWebBackendReview(string acmeEmail, string existingStaticIpName)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(Render(acmeEmail, existingStaticIpName, 3))!.AsObject();
        root["schemaVersion"] = 5;
        var objects = root["objects"]!.AsArray();
        objects.Add(JsonSerializer.SerializeToNode(new
        {
            apiVersion = "v1",
            kind = "Service",
            metadata = new
            {
                name = "legacy-maliev-web",
                @namespace = Namespace,
                annotations = new Dictionary<string, string>
                {
                    ["cloud.google.com/backend-config"] = "{\"ports\":{\"8080\":\"legacy-maliev-web-backend\"}}"
                }
            },
            spec = new
            {
                ports = new[] { new { port = 8080, protocol = "TCP", targetPort = 8080, name = "http" } },
                selector = new { run = "maliev-web" },
                sessionAffinity = "None",
                type = "NodePort"
            }
        }));
        objects.Add(JsonSerializer.SerializeToNode(new
        {
            apiVersion = "cloud.google.com/v1",
            kind = "BackendConfig",
            metadata = new { name = "legacy-maliev-web-backend", @namespace = Namespace },
            spec = new { timeoutSec = 120 }
        }));
        root["webBackendReview"] = JsonSerializer.SerializeToNode(new
        {
            semantics = "UnadoptedSourceWebBackendContract",
            sourceCommit = "f0b3422f23ff037c3f8f91f0338afcab2202e733",
            sourceParent = "e27008a3f323a92dbf05076d93ac5c4f4d3f3e1e",
            sourceFinal = "edf4e45d7ffce2f55b0d607ca82d51e81c330781",
            sourceTestPath = "Maliev.Web.Tests/TechnicalSeoTests.cs",
            sourceTestIntroducedBlob = "36a6926fded7d6143d1a9e272c51df888153a340",
            sourceTestFinalBlob = "ed42caf9765a8b7ea19eb190b009532f9dca795d",
            sourceServicePath = "Maliev.Web/service.yaml",
            sourceServiceBlob = "603d98cc37b09355eb8219835187df84faf4aa77",
            sourceBackendPath = "Maliev.Web/backendconfig.yaml",
            sourceBackendFinalBlob = "5b17aa9410ff1cb82ce0a07a35c40be4948e0859",
            finalHeaderOwner = "ApplicationSecurityHeaderPolicy",
            selectorOwnershipVerified = false,
            namespaceAdoptionVerified = false,
            runtimeAcceptanceProven = false,
            backendAdoptionVerified = false,
            targetImage = (string?)null,
            deploymentPortFragment = new
            {
                fragmentOnly = true,
                validFullDeployment = false,
                sourcePath = "Maliev.Web/deployment.yaml",
                sourceBlob = "bc840ee931dcd45d4b9a1788f27963962c8ad453",
                sourceNamespace = "maliev",
                spec = new
                {
                    selector = new { matchLabels = new { run = "maliev-web" } },
                    template = new
                    {
                        metadata = new { labels = new { run = "maliev-web" } },
                        spec = new
                        {
                            containers = new[]
                            {
                                new { name = "maliev-web", ports = new[] { new { containerPort = 8080, protocol = "TCP" } } }
                            }
                        }
                    }
                }
            },
            unresolvedGates = new[]
            {
                "The source run label is provenance, not verified successor selector ownership",
                "Target image, complete Deployment and namespace adoption require independent owner acceptance",
                "AppHost #33 Aspire and deployment approval, live routing and backend adoption remain unproven"
            }
        });
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Opts into a resource-intent review without executing the retained source helper.</summary>
    /// <param name="acmeEmail">The explicitly supplied ACME contact.</param>
    /// <param name="existingStaticIpName">The explicitly supplied existing static-IP name.</param>
    /// <param name="schemaVersion">One preserves the original report; two adds resource intent; three adds an unexecuted installer plan; five adds the unadopted Web backend contract.</param>
    /// <returns>An inert review envelope, never a deployment or patch.</returns>
    public static string Render(string acmeEmail, string existingStaticIpName, int schemaVersion)
    {
        if (schemaVersion == 5) { return RenderWebBackendReview(acmeEmail, existingStaticIpName); }
        if (schemaVersion == 3)
        {
            var versionThree = System.Text.Json.Nodes.JsonNode.Parse(Render(acmeEmail, existingStaticIpName, 2))!.AsObject();
            versionThree["schemaVersion"] = 3;
            versionThree["controllerInstallationReview"] = JsonSerializer.SerializeToNode(InstallerReview());
            return versionThree.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        if (schemaVersion is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), "Only review schema versions one, two and three are supported.");
        }
        var legacy = Render(acmeEmail, existingStaticIpName);
        if (schemaVersion == 1) { return legacy; }
        var root = System.Text.Json.Nodes.JsonNode.Parse(legacy)!.AsObject();
        root["schemaVersion"] = 2;
        root["workloadResourceReview"] = JsonSerializer.SerializeToNode(new
        {
            sourceCommit = "c2b26cd90bf0cd7838c8fe3044c66c2ad3d6a511",
            sourceParent = "da5bf11d23285569cb8020eec7ac9ba6acf4a67d",
            sourceCheckpoint = "135e526d0dab85c415b3afdcefd7b70fe2c82e2f",
            sourceScript = "update-replicas-and-memory.ps1",
            sourceScriptBlob = "b8aae2274eb5b8cbfd676b22e036123015b0f334",
            semantics = "ConditionalTextReplacementIntent",
            operationExecutionVerified = false,
            manifestAdoptionVerified = false,
            capacityAccepted = false,
            unresolvedGates = new[]
            {
                "Source workload to successor manifest ownership requires independent acceptance",
                "Invoice and Payment consolidation does not imply summed Accounting replicas",
                "Source Intranet sizing does not establish successor BFF sizing",
                "Existing manifest text matches, effective sizing and capacity require independent acceptance"
            },
            workloads = new[]
            {
                ResourceIntent("Maliev.AuthService.Api",
                    "e5bfb452fc469c2bf3046b15c7d36674cf0dc2ad", 2, null, null),
                ResourceIntent("Maliev.CustomerService.Api",
                    "6f9446866c889f28c4126af6067cfa8f15089efe", 2, null, 256),
                ResourceIntent("Maliev.OrderService.Api",
                    "d6b5940a5b729ecf6b646f6d34dd0001c8897578", 2, null, 256),
                ResourceIntent("Maliev.InvoiceService.Api",
                    "314df3239fa4afe5867e3dec58bf99e58673ca61", 2, null, null),
                ResourceIntent("Maliev.QuotationService.Api",
                    "335838886cfc312644f22f1261aedd15386acc3e", 2, null, 256),
                ResourceIntent("Maliev.PaymentService.Api",
                    "314f85f644899fc34376a2ff6e2543e3cc329b5b", 2, null, null),
                ResourceIntent("Maliev.Intranet",
                    "aeff3f7eca1bac004f59159398f3192ec2bfd915", 2, 256, 384),
                ResourceIntent("Maliev.MaterialService.Api",
                    "04f86f9f94af3acf09fd5331a0c7458036ec9d0d", null, null, 256)
            }
        });
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Returns inert source-control-flow text; this method never runs any command.</summary>
    /// <returns>Three fixed text artifacts requiring independent review, never executable export names.</returns>
    public static IReadOnlyDictionary<string, string> RenderInstallerReviewArtifacts() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["deploy-certmanager.ps1.txt"] = """
                # INERT REVIEW TEXT ONLY. Export does not authorize execution, installation, or deployment.
                $certmanager_version = 'v1.18.2'
                # see https://github.com/cert-manager/cert-manager/releases for available releases

                ######################
                # Installation
                ######################

                if (-not (Test-Path -Path ".backup")) {
                    Write-Host "'.backup' folder not found. Creating it..."
                    New-Item -Path ".backup" -ItemType Directory | Out-Null
                }

                Write-Host "Backing up existing certificates and cluster issuers..."
                kubectl get certificates -A -o yaml > .backup/certificates-backup-$(Get-Date -Format 'yyyyMMdd-HHmmss').yaml 2>$null
                kubectl get clusterissuers -o yaml > .backup/clusterissuers-backup-$(Get-Date -Format 'yyyyMMdd-HHmmss').yaml 2>$null

                Write-Host "Creating cert-manager namespace..."
                kubectl create namespace cert-manager --dry-run=client -o yaml | kubectl apply -f -

                Write-Host "Disabling resource validation on cert-manager namespace..."
                kubectl label namespace cert-manager cert-manager.io/disable-validation=true --overwrite

                # if encounter 'permission denied' error, uncomment this line
                # kubectl create clusterrolebinding cluster-admin-binding --clusterrole=cluster-admin --user=$(gcloud config get-value core/account)

                Write-Host "Checking for existing cert-manager installation..."
                $existingCRDs = kubectl get crd | Select-String "cert-manager.io"
                if ($existingCRDs) {
                    Write-Host "Found existing cert-manager CRDs. Performing complete cleanup for upgrade..."

                    # Uninstall everything including CRDs to ensure clean upgrade
                    Write-Host "Removing all cert-manager resources..."
                    kubectl delete namespace cert-manager --ignore-not-found=true

                    Write-Host "Removing cert-manager CRDs with timeout..."
                    kubectl delete crd certificaterequests.cert-manager.io --ignore-not-found=true --timeout=30s
                    kubectl delete crd certificates.cert-manager.io --ignore-not-found=true --timeout=30s
                    kubectl delete crd challenges.acme.cert-manager.io --ignore-not-found=true --timeout=30s
                    kubectl delete crd clusterissuers.cert-manager.io --ignore-not-found=true --timeout=30s
                    kubectl delete crd issuers.cert-manager.io --ignore-not-found=true --timeout=30s
                    kubectl delete crd orders.acme.cert-manager.io --ignore-not-found=true --timeout=30s

                    Write-Host "Force cleaning any remaining CRDs..."
                    kubectl patch crd certificaterequests.cert-manager.io -p '{\"metadata\":{\"finalizers\":[]}}' --type=merge 2>$null
                    kubectl patch crd certificates.cert-manager.io -p '{\"metadata\":{\"finalizers\":[]}}' --type=merge 2>$null
                    kubectl patch crd challenges.acme.cert-manager.io -p '{\"metadata\":{\"finalizers\":[]}}' --type=merge 2>$null
                    kubectl patch crd clusterissuers.cert-manager.io -p '{\"metadata\":{\"finalizers\":[]}}' --type=merge 2>$null
                    kubectl patch crd issuers.cert-manager.io -p '{\"metadata\":{\"finalizers\":[]}}' --type=merge 2>$null
                    kubectl patch crd orders.acme.cert-manager.io -p '{\"metadata\":{\"finalizers\":[]}}' --type=merge 2>$null

                    Write-Host "Final cleanup with force delete..."
                    kubectl delete crd certificaterequests.cert-manager.io --force --grace-period=0 2>$null
                    kubectl delete crd certificates.cert-manager.io --force --grace-period=0 2>$null
                    kubectl delete crd challenges.acme.cert-manager.io --force --grace-period=0 2>$null
                    kubectl delete crd clusterissuers.cert-manager.io --force --grace-period=0 2>$null
                    kubectl delete crd issuers.cert-manager.io --force --grace-period=0 2>$null
                    kubectl delete crd orders.acme.cert-manager.io --force --grace-period=0 2>$null

                    Write-Host "Waiting for cleanup to complete..."
                    Start-Sleep -Seconds 5
                }

                Write-Host "Installing cert-manager $certmanager_version..."
                kubectl apply -f https://github.com/cert-manager/cert-manager/releases/download/$certmanager_version/cert-manager.yaml

                Write-Host "Configuring feature gates for compatibility..."
                # Disable ACME HTTP01 strict path validation if using ingress-nginx >= 1.8.0
                kubectl patch deployment cert-manager -n cert-manager --type='json' -p='[{"op": "add", "path": "/spec/template/spec/containers/0/args/-", "value": "--feature-gates=ACMEHTTP01IngressPathTypeExact=false"}]' --dry-run=client

                Write-Host "Waiting for cert-manager pods to be ready..."
                kubectl wait --namespace cert-manager --for=condition=Ready pods --selector=app.kubernetes.io/instance=cert-manager --timeout=180s

                ######################
                # Verify installation
                ######################

                Write-Host "Listing cert-manager pods..."
                kubectl get pods --namespace cert-manager

                Write-Host "Creating test resources..."
                kubectl apply -f .\test-resources.yaml

                Write-Host "Waiting for test certificate to be ready..."
                kubectl wait certificate/selfsigned-cert --namespace cert-manager-test --for=condition=Ready --timeout=120s

                Write-Host "Describing test certificate..."
                kubectl describe certificate selfsigned-cert -n cert-manager-test

                Write-Host "Cleaning up test resources..."
                kubectl delete -f .\test-resources.yaml

                Write-Host "All done."
                pause
                """ + "\n",
            ["deploy-clusterissuers.ps1.txt"] = """
                # INERT REVIEW TEXT ONLY. Export does not authorize execution, installation, or deployment.
                # Create cluster issuers
                kubectl apply -f .\prod-clusterissuers.yaml

                # Describe cluster issuers
                kubectl describe clusterissuer

                # List all cluster issuers
                kubectl get clusterissuer

                pause
                """ + "\n",
            ["deploy-certificate-prod.ps1.txt"] = """
                # INERT REVIEW TEXT ONLY. Export does not authorize execution, installation, or deployment.
                # #### Create certificates ####
                kubectl apply -f certificates-prod.yaml

                pause
                """ + "\n"
        };

    private static object InstallerReview() => new
    {
        sourceCommit = "dc03cc8e425f43b67fc04b40af4e18e0aeebf3d1",
        sourceParent = "a5b9ee39149c64d7649b708e894520f788cdfb61",
        sourceCheckpoint = "135e526d0dab85c415b3afdcefd7b70fe2c82e2f",
        sourceInstallerPath = "Maliev.CertManager/deploy-certmanager.ps1",
        sourceInstallerBeforeBlob = "ed5f31758a1e37adc7a4b04263bfe051232890a7",
        sourceInstallerBlob = "e909854a49c63bcd8b4dcaebad5187b46cb50630",
        sourceSmokePath = "Maliev.CertManager/test-resources.yaml",
        sourceSmokeBlob = "72496d6803cc66854e9b99fd99b6c646f95048f7",
        requestedSourceVersion = "v1.18.2",
        sourceInstallManifestUrl = "https://github.com/cert-manager/cert-manager/releases/download/v1.18.2/cert-manager.yaml",
        semantics = "UnexecutedSourceInstallationPlan",
        status = "Unverified",
        executionAllowed = false,
        controllerInstalledVerified = false,
        smokeTestVerified = false,
        backupVerified = false,
        namespaceOwnershipVerified = false,
        nativeExitPropagationVerified = false,
        sourceChecksNativeExitCodes = false,
        sourceSetsStopErrorPreference = false,
        commentedClusterAdminBindingIncluded = false,
        sourceLocalPreparation = new
        {
            backupDirectory = ".backup",
            createDirectoryCondition = "Test-Path .backup is false",
            certificateBackupPattern = "certificates-backup-yyyyMMdd-HHmmss.yaml",
            clusterIssuerBackupPattern = "clusterissuers-backup-yyyyMMdd-HHmmss.yaml",
            backupStandardErrorSuppressed = true,
            existingCrdQueryCondition = "kubectl get crd output matches Select-String cert-manager.io",
            conditionalCleanupDelaySeconds = 5,
            terminalPause = true,
            performed = false
        },
        unresolvedGates = new[]
        {
            "Source plan does not authorize destructive namespace, CRD or finalizer operations",
            "Installed controller version, backup integrity and native exit propagation are unverified",
            "Source quoting and native argument interpretation require independent acceptance",
            "Namespace ownership, self-signed issuance and resource cleanup require independent acceptance"
        },
        operations = new[]
        {
                InstallerStep(1, "backup-certificates",
                    "kubectl get certificates -A -o yaml > .backup/certificates-backup-$(Get-Date -Format 'yyyyMMdd-HHmmss').yaml 2>$null",
                    "Always", false, null),
                InstallerStep(2, "backup-clusterissuers",
                    "kubectl get clusterissuers -o yaml > .backup/clusterissuers-backup-$(Get-Date -Format 'yyyyMMdd-HHmmss').yaml 2>$null",
                    "Always", false, null),
                InstallerStep(3, "create-namespace-client-dry-run-and-apply",
                    "kubectl create namespace cert-manager --dry-run=client -o yaml | kubectl apply -f -",
                    "Always", false, null),
                InstallerStep(4, "label-namespace",
                    "kubectl label namespace cert-manager cert-manager.io/disable-validation=true --overwrite",
                    "Always", false, null),
                InstallerStep(5, "discover-existing-crds",
                    "$existingCRDs = kubectl get crd | Select-String \"cert-manager.io\"",
                    "Always", false, null),
                InstallerStep(6, "delete-controller-namespace",
                    "kubectl delete namespace cert-manager --ignore-not-found=true",
                    "ExistingCrdQueryMatched", false, null),
                InstallerStep(7, "delete-crd-certificaterequests.cert-manager.io",
                    "kubectl delete crd certificaterequests.cert-manager.io --ignore-not-found=true --timeout=30s",
                    "ExistingCrdQueryMatched", false, 30),
                InstallerStep(8, "delete-crd-certificates.cert-manager.io",
                    "kubectl delete crd certificates.cert-manager.io --ignore-not-found=true --timeout=30s",
                    "ExistingCrdQueryMatched", false, 30),
                InstallerStep(9, "delete-crd-challenges.acme.cert-manager.io",
                    "kubectl delete crd challenges.acme.cert-manager.io --ignore-not-found=true --timeout=30s",
                    "ExistingCrdQueryMatched", false, 30),
                InstallerStep(10, "delete-crd-clusterissuers.cert-manager.io",
                    "kubectl delete crd clusterissuers.cert-manager.io --ignore-not-found=true --timeout=30s",
                    "ExistingCrdQueryMatched", false, 30),
                InstallerStep(11, "delete-crd-issuers.cert-manager.io",
                    "kubectl delete crd issuers.cert-manager.io --ignore-not-found=true --timeout=30s",
                    "ExistingCrdQueryMatched", false, 30),
                InstallerStep(12, "delete-crd-orders.acme.cert-manager.io",
                    "kubectl delete crd orders.acme.cert-manager.io --ignore-not-found=true --timeout=30s",
                    "ExistingCrdQueryMatched", false, 30),
                InstallerStep(13, "clear-finalizers-certificaterequests.cert-manager.io",
                    "kubectl patch crd certificaterequests.cert-manager.io -p '{\\\"metadata\\\":{\\\"finalizers\\\":[]}}' --type=merge 2>$null",
                    "ExistingCrdQueryMatched", false, null),
                InstallerStep(14, "clear-finalizers-certificates.cert-manager.io",
                    "kubectl patch crd certificates.cert-manager.io -p '{\\\"metadata\\\":{\\\"finalizers\\\":[]}}' --type=merge 2>$null",
                    "ExistingCrdQueryMatched", false, null),
                InstallerStep(15, "clear-finalizers-challenges.acme.cert-manager.io",
                    "kubectl patch crd challenges.acme.cert-manager.io -p '{\\\"metadata\\\":{\\\"finalizers\\\":[]}}' --type=merge 2>$null",
                    "ExistingCrdQueryMatched", false, null),
                InstallerStep(16, "clear-finalizers-clusterissuers.cert-manager.io",
                    "kubectl patch crd clusterissuers.cert-manager.io -p '{\\\"metadata\\\":{\\\"finalizers\\\":[]}}' --type=merge 2>$null",
                    "ExistingCrdQueryMatched", false, null),
                InstallerStep(17, "clear-finalizers-issuers.cert-manager.io",
                    "kubectl patch crd issuers.cert-manager.io -p '{\\\"metadata\\\":{\\\"finalizers\\\":[]}}' --type=merge 2>$null",
                    "ExistingCrdQueryMatched", false, null),
                InstallerStep(18, "clear-finalizers-orders.acme.cert-manager.io",
                    "kubectl patch crd orders.acme.cert-manager.io -p '{\\\"metadata\\\":{\\\"finalizers\\\":[]}}' --type=merge 2>$null",
                    "ExistingCrdQueryMatched", false, null),
                InstallerStep(19, "force-delete-crd-certificaterequests.cert-manager.io",
                    "kubectl delete crd certificaterequests.cert-manager.io --force --grace-period=0 2>$null",
                    "ExistingCrdQueryMatched", false, null),
                InstallerStep(20, "force-delete-crd-certificates.cert-manager.io",
                    "kubectl delete crd certificates.cert-manager.io --force --grace-period=0 2>$null",
                    "ExistingCrdQueryMatched", false, null),
                InstallerStep(21, "force-delete-crd-challenges.acme.cert-manager.io",
                    "kubectl delete crd challenges.acme.cert-manager.io --force --grace-period=0 2>$null",
                    "ExistingCrdQueryMatched", false, null),
                InstallerStep(22, "force-delete-crd-clusterissuers.cert-manager.io",
                    "kubectl delete crd clusterissuers.cert-manager.io --force --grace-period=0 2>$null",
                    "ExistingCrdQueryMatched", false, null),
                InstallerStep(23, "force-delete-crd-issuers.cert-manager.io",
                    "kubectl delete crd issuers.cert-manager.io --force --grace-period=0 2>$null",
                    "ExistingCrdQueryMatched", false, null),
                InstallerStep(24, "force-delete-crd-orders.acme.cert-manager.io",
                    "kubectl delete crd orders.acme.cert-manager.io --force --grace-period=0 2>$null",
                    "ExistingCrdQueryMatched", false, null),
                InstallerStep(25, "apply-controller-manifest",
                    "kubectl apply -f https://github.com/cert-manager/cert-manager/releases/download/$certmanager_version/cert-manager.yaml",
                    "Always", false, null),
                InstallerStep(26, "feature-gate-client-dry-run",
                    "kubectl patch deployment cert-manager -n cert-manager --type='json' -p='[{\"op\": \"add\", \"path\": \"/spec/template/spec/containers/0/args/-\", \"value\": \"--feature-gates=ACMEHTTP01IngressPathTypeExact=false\"}]' --dry-run=client",
                    "Always", true, null),
                InstallerStep(27, "wait-controller-pods",
                    "kubectl wait --namespace cert-manager --for=condition=Ready pods --selector=app.kubernetes.io/instance=cert-manager --timeout=180s",
                    "Always", false, 180),
                InstallerStep(28, "list-controller-pods",
                    "kubectl get pods --namespace cert-manager",
                    "Always", false, null),
                InstallerStep(29, "apply-smoke-resources",
                    "kubectl apply -f .\\test-resources.yaml",
                    "Always", false, null),
                InstallerStep(30, "wait-smoke-certificate",
                    "kubectl wait certificate/selfsigned-cert --namespace cert-manager-test --for=condition=Ready --timeout=120s",
                    "Always", false, 120),
                InstallerStep(31, "describe-smoke-certificate",
                    "kubectl describe certificate selfsigned-cert -n cert-manager-test",
                    "Always", false, null),
                InstallerStep(32, "delete-smoke-resources",
                    "kubectl delete -f .\\test-resources.yaml",
                    "Always", false, null)
        },
        sourceSmokeResources = new object[]
        {
            new { apiVersion = "v1", kind = "Namespace", metadata = new { name = "cert-manager-test" } },
            new
            {
                apiVersion = "cert-manager.io/v1", kind = "Issuer",
                metadata = new { name = "test-selfsigned", @namespace = "cert-manager-test" },
                spec = new { selfSigned = new { } }
            },
            new
            {
                apiVersion = "cert-manager.io/v1", kind = "Certificate",
                metadata = new { name = "selfsigned-cert", @namespace = "cert-manager-test" },
                spec = new
                {
                    dnsNames = new[] { "maliev.com" }, secretName = "selfsigned-cert-tls",
                    issuerRef = new { name = "test-selfsigned" }
                }
            }
        }
    };

    private static object InstallerStep(int sourceOrder, string id, string sourceInvocation, string sourceCondition, bool clientDryRunOnly, int? timeoutSeconds) => new
    {
        sourceOrder,
        id,
        sourceInvocation,
        sourceCondition,
        clientDryRunOnly,
        timeoutSeconds,
        executionAllowed = false,
        sourceExitCodeChecked = false
    };

    private static object ResourceIntent(string workload, string checkpointBlob, int? replicas, int? requestMi, int? limitMi) => new
    {
        sourceWorkload = workload,
        sourceManifestPath = workload + "/deployment.yaml",
        sourceCheckpointManifestBlob = checkpointBlob,
        replicas,
        memoryRequestMi = requestMi,
        memoryLimitMi = limitMi,
        preconditions = new
        {
            fileExistsCheck = "Test-Path",
            missingFileBehavior = "Skip",
            contentMatchVerified = false,
            replicas = replicas.HasValue ? Replacement("replicas: 1", "replicas: 2") : null,
            memoryRequest = requestMi.HasValue ? Replacement(@"(requests:\s+cpu: \d+m\s+memory: )\d+Mi", "${1}256Mi") : null,
            memoryLimit = limitMi.HasValue ? (limitMi == 384
                ? Replacement(@"(limits:\s+cpu: \d+m\s+memory: )\d+Mi", "${1}384Mi")
                : Replacement(@"(limits:\s+cpu: \d+m\s+memory: )192Mi", "${1}256Mi")) : null
        }
    };

    private static object Replacement(string pattern, string replacement) => new
    {
        @operator = "PowerShell -replace",
        pattern,
        replacement,
        caseInsensitive = true,
        allMatches = true,
        noMatchBehavior = "Unchanged"
    };

    /// <summary>Creates an inert JSON envelope of Kubernetes objects and unresolved acceptance gates.</summary>
    /// <param name="acmeEmail">The owner-reviewed ACME contact; never inferred from a machine account.</param>
    /// <param name="existingStaticIpName">The existing static-IP resource name; no new address is allocated.</param>
    /// <returns>A review envelope that is deliberately not a Kubernetes List.</returns>
    public static string Render(string acmeEmail, string existingStaticIpName)
    {
        if (string.IsNullOrWhiteSpace(acmeEmail) || !System.Net.Mail.MailAddress.TryCreate(acmeEmail, out var address)
            || address.Address != acmeEmail)
        {
            throw new ArgumentException("An explicit ACME email address is required.", nameof(acmeEmail));
        }

        if (string.IsNullOrWhiteSpace(existingStaticIpName)
            || existingStaticIpName.Length > 63
            || !System.Text.RegularExpressions.Regex.IsMatch(existingStaticIpName, @"\A[a-z](?:[a-z0-9-]*[a-z0-9])?\z"))
        {
            throw new ArgumentException("An existing static-IP resource name is required.", nameof(existingStaticIpName));
        }

        string[] hosts = ["www.maliev.com", "maliev.com", "api.maliev.com", "intranet.maliev.com"];
        var objects = new List<object>
        {
            new
            {
                apiVersion = "networking.gke.io/v1beta1", kind = "FrontendConfig",
                metadata = new { name = "legacy-maliev-https-redirect", @namespace = Namespace },
                spec = new { redirectToHttps = new { enabled = true, responseCodeName = "PERMANENT_REDIRECT" } }
            },
            new
            {
                apiVersion = "cert-manager.io/v1", kind = "ClusterIssuer",
                metadata = new { name = IssuerName },
                spec = new
                {
                    acme = new
                    {
                        email = acmeEmail, server = "https://acme-v02.api.letsencrypt.org/directory",
                        privateKeySecretRef = new { name = IssuerName },
                        solvers = new[]
                        {
                            new { selector = new { dnsNames = new[] { LineChatbotHost } }, http01 = new { ingress = new { name = SourceLineChatbotIngressName } } },
                            new { selector = new { dnsNames = hosts }, http01 = new { ingress = new { name = IngressName } } }
                        }
                    }
                }
            }
        };

        foreach (var host in hosts.Append(LineChatbotHost))
        {
            var secret = host == LineChatbotHost ? "line-chatbot-tls" : host.Replace('.', '-') + "-tls";
            objects.Add(new
            {
                apiVersion = "cert-manager.io/v1",
                kind = "Certificate",
                metadata = new { name = secret, @namespace = Namespace },
                spec = new
                {
                    secretName = secret,
                    renewBefore = "720h",
                    commonName = host,
                    dnsNames = new[] { host },
                    issuerRef = new { name = IssuerName, kind = "ClusterIssuer" }
                }
            });
        }

        objects.Add(new
        {
            apiVersion = "networking.k8s.io/v1",
            kind = "Ingress",
            metadata = new
            {
                name = IngressName,
                @namespace = Namespace,
                annotations = new Dictionary<string, string>
                {
                    ["kubernetes.io/ingress.class"] = "gce",
                    ["kubernetes.io/ingress.allow-http"] = "true",
                    ["kubernetes.io/ingress.global-static-ip-name"] = existingStaticIpName,
                    ["networking.gke.io/v1beta1.FrontendConfig"] = "legacy-maliev-https-redirect"
                }
            },
            spec = new
            {
                tls = hosts.Select(host => new { hosts = new[] { host }, secretName = host.Replace('.', '-') + "-tls" }),
                rules = hosts.Select(host => new
                {
                    host,
                    http = new
                    {
                        paths = (host == "api.maliev.com" ? ApiRoutes : [("/", host == "intranet.maliev.com" ? "intranet-bff" : "web")])
                            .Select(route => new
                            {
                                path = route.Item1,
                                pathType = route.Item1 is "/customers/v1/validate" or "/Employees/v1/validate" ? "Exact" : "Prefix",
                                backend = new { service = new { name = "legacy-maliev-" + route.Item2 + (route.Item2 is "web" or "intranet-bff" ? "" : "-service"), port = new { number = route.Item2 is "auth" or "employee" or "quotation" or "intranet-bff" ? 80 : 8080 } } }
                            })
                    }
                })
            }
        });

        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            reviewOnly = true,
            productionDeploymentAllowed = false,
            cutoverPercent = 0,
            externalIngressDependencies = new[]
            {
                new
                {
                    host = LineChatbotHost,
                    sourceIngressName = SourceLineChatbotIngressName,
                    sourceNamespace = "maliev",
                    sourceCertificateSecretName = "line-chatbot-tls",
                    ownershipVerified = false,
                    runtimeRegistered = false
                }
            },
            unresolvedGates = new[]
            {
                "AppHost #33 owner Aspire review and deployment approval",
                "Existing static-IP ownership and capacity verification",
                "Cluster-scoped issuer ownership and private-key secret isolation",
                "Retained backend Service names, ports and namespace/selector ownership acceptance",
                "Real retained API route/rewriting acceptance",
                "Source line-chatbot ingress ownership and namespace mapping require independent acceptance",
                "Installed cert-manager version and certificate issuance acceptance"
            },
            objects
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
