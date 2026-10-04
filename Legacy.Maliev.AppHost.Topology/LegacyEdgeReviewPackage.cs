using System.Text.Json;

namespace Legacy.Maliev.AppHost.Topology;

/// <summary>Renders the retained source edge intentions for review without applying infrastructure.</summary>
public static class LegacyEdgeReviewPackage
{
    /// <summary>Gets the only namespace permitted in a legacy edge review.</summary>
    public const string Namespace = "maliev-legacy";

    private const string IngressName = "legacy-maliev-edge";
    private const string IssuerName = "legacy-maliev-letsencrypt-prod";

    private static readonly (string Path, string Service)[] ApiRoutes =
    [
        ("/auth", "auth"), ("/countries", "country"), ("/currencies", "catalog"),
        ("/customers", "customer"), ("/materials", "catalog"), ("/suppliers", "procurement"),
        ("/orderstatuses", "order"), ("/uploads", "file"), ("/orders", "order"),
        ("/emails", "notification"), ("/quotations", "quotation"), ("/employees", "employee"),
        ("/payments", "accounting"), ("/pdfs", "document"), ("/jobs", "career"),
        ("/invoices", "accounting"), ("/messages", "contact"), ("/purchaseorders", "procurement"),
        ("/receipts", "accounting"), ("/quotationrequests", "quotation")
    ];

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
                        solvers = new[] { new { selector = new { dnsNames = hosts }, http01 = new { ingress = new { name = IngressName } } } }
                    }
                }
            }
        };

        foreach (var host in hosts)
        {
            var secret = host.Replace('.', '-') + "-tls";
            objects.Add(new
            {
                apiVersion = "cert-manager.io/v1", kind = "Certificate",
                metadata = new { name = secret, @namespace = Namespace },
                spec = new
                {
                    secretName = secret, renewBefore = "720h", commonName = host, dnsNames = new[] { host },
                    issuerRef = new { name = IssuerName, kind = "ClusterIssuer" }
                }
            });
        }

        objects.Add(new
        {
            apiVersion = "networking.k8s.io/v1", kind = "Ingress",
            metadata = new
            {
                name = IngressName, @namespace = Namespace,
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
                                path = route.Item1, pathType = "Prefix",
                                backend = new { service = new { name = "legacy-maliev-" + route.Item2 + (route.Item2 is "web" or "intranet-bff" ? "" : "-service"), port = new { number = 8080 } } }
                            })
                    }
                })
            }
        });

        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1, reviewOnly = true, productionDeploymentAllowed = false, cutoverPercent = 0,
            unresolvedGates = new[]
            {
                "AppHost #33 owner Aspire review and deployment approval",
                "Existing static-IP ownership and capacity verification",
                "Cluster-scoped issuer ownership and private-key secret isolation",
                "Retained backend Service port 8080 acceptance",
                "Real retained API route/rewriting acceptance",
                "Source line-chatbot certificate/solver ownership",
                "Installed cert-manager version and certificate issuance acceptance"
            },
            objects
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
