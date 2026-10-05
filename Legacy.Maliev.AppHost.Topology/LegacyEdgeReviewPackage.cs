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
        ("/Jobs", "career"), ("/accounting", "accounting")
    ];

    /// <summary>Opts into a resource-intent review without executing the retained source helper.</summary>
    /// <param name="acmeEmail">The explicitly supplied ACME contact.</param>
    /// <param name="existingStaticIpName">The explicitly supplied existing static-IP name.</param>
    /// <param name="schemaVersion">One preserves the original report; two adds conditional resource intent.</param>
    /// <returns>An inert review envelope, never a deployment or patch.</returns>
    public static string Render(string acmeEmail, string existingStaticIpName, int schemaVersion)
    {
        if (schemaVersion is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), "Only review schema versions one and two are supported.");
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
                                pathType = "Prefix",
                                backend = new { service = new { name = "legacy-maliev-" + route.Item2 + (route.Item2 is "web" or "intranet-bff" ? "" : "-service"), port = new { number = route.Item2 is "auth" or "employee" or "intranet-bff" ? 80 : 8080 } } }
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
