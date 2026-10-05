using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Legacy.Maliev.AppHost.Topology;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class LegacyEdgeReviewPackageTests
{
    [Fact]
    public void Render_PreservesHttpsAndCertificateIntentWithoutAuthorizingDeployment()
    {
        using var package = JsonDocument.Parse(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-owner-reviewed-ip"));
        var root = package.RootElement;
        Assert.True(root.TryGetProperty("reviewOnly", out _), "The edge review renderer must return an explicit inert review envelope.");
        Assert.True(root.GetProperty("reviewOnly").GetBoolean());
        Assert.False(root.GetProperty("productionDeploymentAllowed").GetBoolean());
        Assert.Equal(0, root.GetProperty("cutoverPercent").GetInt32());
        Assert.False(root.TryGetProperty("kind", out _));
        var objects = root.GetProperty("objects").EnumerateArray().ToArray();
        Assert.Equal(8, objects.Length);
        foreach (var item in objects.Where(item => item.GetProperty("kind").GetString() != "ClusterIssuer"))
        {
            Assert.Equal("maliev-legacy", item.GetProperty("metadata").GetProperty("namespace").GetString());
        }

        var redirect = Single(objects, "FrontendConfig").GetProperty("spec").GetProperty("redirectToHttps");
        Assert.True(redirect.GetProperty("enabled").GetBoolean());
        Assert.Equal("PERMANENT_REDIRECT", redirect.GetProperty("responseCodeName").GetString());
        var issuerObject = Single(objects, "ClusterIssuer");
        var issuerName = issuerObject.GetProperty("metadata").GetProperty("name").GetString();
        Assert.Equal("legacy-maliev-letsencrypt-prod", issuerName);
        Assert.False(issuerObject.GetProperty("metadata").TryGetProperty("namespace", out _));
        var issuer = issuerObject.GetProperty("spec").GetProperty("acme");
        Assert.Equal("owner@maliev.test", issuer.GetProperty("email").GetString());
        Assert.Equal("https://acme-v02.api.letsencrypt.org/directory", issuer.GetProperty("server").GetString());
        Assert.Equal(issuerName, issuer.GetProperty("privateKeySecretRef").GetProperty("name").GetString());
        var solver = Assert.Single(issuer.GetProperty("solvers").EnumerateArray(),
            item => item.GetProperty("http01").GetProperty("ingress").GetProperty("name").GetString() == "legacy-maliev-edge");
        Assert.Equal("legacy-maliev-edge", solver.GetProperty("http01").GetProperty("ingress").GetProperty("name").GetString());
        Assert.Equal(4, solver.GetProperty("selector").GetProperty("dnsNames").GetArrayLength());

        var ingress = Single(objects, "Ingress");
        var annotations = ingress.GetProperty("metadata").GetProperty("annotations");
        Assert.Equal("existing-owner-reviewed-ip", annotations.GetProperty("kubernetes.io/ingress.global-static-ip-name").GetString());
        Assert.Equal("true", annotations.GetProperty("kubernetes.io/ingress.allow-http").GetString());
        Assert.Equal("gce", annotations.GetProperty("kubernetes.io/ingress.class").GetString());
        Assert.Equal(Single(objects, "FrontendConfig").GetProperty("metadata").GetProperty("name").GetString(),
            annotations.GetProperty("networking.gke.io/v1beta1.FrontendConfig").GetString());
        Assert.False(annotations.TryGetProperty("cert-manager.io/cluster-issuer", out _));
        var certificates = objects.Where(item => item.GetProperty("kind").GetString() == "Certificate").ToArray();
        Assert.Equal(5, certificates.Length);
        foreach (var certificate in certificates)
        {
            var spec = certificate.GetProperty("spec");
            Assert.Equal("720h", spec.GetProperty("renewBefore").GetString());
            Assert.Equal(issuerName, spec.GetProperty("issuerRef").GetProperty("name").GetString());
            Assert.Equal("ClusterIssuer", spec.GetProperty("issuerRef").GetProperty("kind").GetString());
            Assert.False(spec.TryGetProperty("privateKey", out _));
            var host = Assert.Single(spec.GetProperty("dnsNames").EnumerateArray()).GetString();
            Assert.Equal(spec.GetProperty("commonName").GetString(), host);
            if (host != "line-chatbot.maliev.com")
            {
                var tls = Assert.Single(ingress.GetProperty("spec").GetProperty("tls").EnumerateArray(),
                    item => item.GetProperty("hosts")[0].GetString() == host);
                Assert.Equal(spec.GetProperty("secretName").GetString(), tls.GetProperty("secretName").GetString());
            }
        }
        Assert.Contains(root.GetProperty("unresolvedGates").EnumerateArray(), gate => gate.GetString()!.Contains("line-chatbot", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_RetainsSeparateLineChatbotCertificateAndNamedSolverWithoutInventingRuntimeOwnership()
    {
        using var package = JsonDocument.Parse(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip"));
        var root = package.RootElement;
        var objects = root.GetProperty("objects").EnumerateArray().ToArray();
        var issuer = Single(objects, "ClusterIssuer");
        var solvers = issuer.GetProperty("spec").GetProperty("acme").GetProperty("solvers").EnumerateArray().ToArray();
        Assert.Equal(2, solvers.Length);
        Assert.Equal(new[] { "line-chatbot.maliev.com" },
            solvers[0].GetProperty("selector").GetProperty("dnsNames").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("line-chatbot-ingress", solvers[0].GetProperty("http01").GetProperty("ingress").GetProperty("name").GetString());
        foreach (var solver in solvers)
        {
            Assert.False(solver.GetProperty("http01").GetProperty("ingress").TryGetProperty("class", out _));
        }
        Assert.DoesNotContain(solvers[1].GetProperty("selector").GetProperty("dnsNames").EnumerateArray(),
            item => item.GetString() == "line-chatbot.maliev.com");

        var certificate = Assert.Single(objects, item => item.GetProperty("kind").GetString() == "Certificate"
            && item.GetProperty("spec").GetProperty("commonName").GetString() == "line-chatbot.maliev.com");
        Assert.Equal("line-chatbot-tls", certificate.GetProperty("metadata").GetProperty("name").GetString());
        Assert.Equal("line-chatbot-tls", certificate.GetProperty("spec").GetProperty("secretName").GetString());
        Assert.Equal(issuer.GetProperty("metadata").GetProperty("name").GetString(),
            certificate.GetProperty("spec").GetProperty("issuerRef").GetProperty("name").GetString());
        var ingress = Single(objects, "Ingress");
        Assert.DoesNotContain(ingress.GetProperty("spec").GetProperty("rules").EnumerateArray(),
            rule => rule.GetProperty("host").GetString() == "line-chatbot.maliev.com");
        Assert.DoesNotContain(ingress.GetProperty("spec").GetProperty("tls").EnumerateArray(),
            tls => tls.GetProperty("hosts").EnumerateArray().Any(host => host.GetString() == "line-chatbot.maliev.com"));

        var dependency = Assert.Single(root.GetProperty("externalIngressDependencies").EnumerateArray());
        Assert.Equal("line-chatbot.maliev.com", dependency.GetProperty("host").GetString());
        Assert.Equal("line-chatbot-ingress", dependency.GetProperty("sourceIngressName").GetString());
        Assert.Equal("maliev", dependency.GetProperty("sourceNamespace").GetString());
        Assert.Equal("line-chatbot-tls", dependency.GetProperty("sourceCertificateSecretName").GetString());
        Assert.False(dependency.GetProperty("ownershipVerified").GetBoolean());
        Assert.False(dependency.GetProperty("runtimeRegistered").GetBoolean());
        Assert.True(root.GetProperty("reviewOnly").GetBoolean());
        Assert.False(root.GetProperty("productionDeploymentAllowed").GetBoolean());
        Assert.Equal(0, root.GetProperty("cutoverPercent").GetInt32());
    }

    [Fact]
    public void Render_MapsEveryRetainedSourceRouteAndOmitsRetiredLoggerAndPrediction()
    {
        using var package = JsonDocument.Parse(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip"));
        Assert.True(package.RootElement.TryGetProperty("objects", out _), "The edge renderer must emit the retained source routing objects.");
        var rules = Single(package.RootElement.GetProperty("objects").EnumerateArray().ToArray(), "Ingress")
            .GetProperty("spec").GetProperty("rules").EnumerateArray().ToArray();
        Assert.Equal(new[] { "www.maliev.com", "maliev.com", "api.maliev.com", "intranet.maliev.com" },
            rules.Select(rule => rule.GetProperty("host").GetString()));
        var api = Assert.Single(rules, rule => rule.GetProperty("host").GetString() == "api.maliev.com");
        var paths = api.GetProperty("http").GetProperty("paths").EnumerateArray().ToArray();
        string[] expectedPaths = ["/auth", "/countries", "/currencies", "/customers", "/materials", "/suppliers",
            "/orderstatuses", "/uploads", "/orders", "/emails", "/quotations", "/employees", "/payments", "/pdfs",
            "/jobs", "/invoices", "/messages", "/purchaseorders", "/receipts", "/quotationrequests",
            "/country", "/documents", "/customer", "/order", "/quotation", "/employee", "/catalog",
            "/procurement", "/file", "/Jobs", "/accounting"];
        Assert.Equal(expectedPaths, paths.Select(path => path.GetProperty("path").GetString()));
        string[] expectedServices = ["auth", "country", "catalog", "customer", "catalog", "procurement",
            "order", "file", "order", "notification", "quotation", "employee", "accounting", "document",
            "career", "accounting", "contact", "procurement", "accounting", "quotation",
            "country", "document", "customer", "order", "quotation", "employee", "catalog",
            "procurement", "file", "career", "accounting"];
        for (var index = 0; index < expectedPaths.Length; index++)
        {
            AssertService(paths, expectedPaths[index], "legacy-maliev-" + expectedServices[index] + "-service");
        }
        foreach (var path in paths)
        {
            Assert.Equal("Prefix", path.GetProperty("pathType").GetString());
            var expectedPort = path.GetProperty("path").GetString() is "/auth" or "/employees" or "/employee" ? 80 : 8080;
            Assert.Equal(expectedPort, path.GetProperty("backend").GetProperty("service").GetProperty("port").GetProperty("number").GetInt32());
        }
        var intranet = Assert.Single(rules, rule => rule.GetProperty("host").GetString() == "intranet.maliev.com");
        AssertService(intranet.GetProperty("http").GetProperty("paths").EnumerateArray().ToArray(), "/", "legacy-maliev-intranet-bff");
        foreach (var web in rules.Where(rule => rule.GetProperty("host").GetString() is "www.maliev.com" or "maliev.com"))
        {
            AssertService(web.GetProperty("http").GetProperty("paths").EnumerateArray().ToArray(), "/", "legacy-maliev-web");
        }
    }

    [Theory]
    [InlineData("api.maliev.com", "/auth", "legacy-maliev-auth-service")]
    [InlineData("api.maliev.com", "/employees", "legacy-maliev-employee-service")]
    [InlineData("intranet.maliev.com", "/", "legacy-maliev-intranet-bff")]
    public void Render_UsesCommittedBackendServicePortRatherThanItsContainerPort(string host, string path, string service)
    {
        using var package = JsonDocument.Parse(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip"));
        AssertBackendPort(package.RootElement, host, path, service, 80);
        Assert.False(package.RootElement.GetProperty("productionDeploymentAllowed").GetBoolean());
        Assert.Contains(package.RootElement.GetProperty("unresolvedGates").EnumerateArray(),
            gate => gate.GetString()!.Contains("namespace/selector ownership", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/country/v1/countries", "/country", "country", 8080)]
    [InlineData("/documents/scalar", "/documents", "document", 8080)]
    [InlineData("/customer/scalar", "/customer", "customer", 8080)]
    [InlineData("/order/scalar", "/order", "order", 8080)]
    [InlineData("/quotation/scalar", "/quotation", "quotation", 8080)]
    [InlineData("/employee/scalar", "/employee", "employee", 80)]
    [InlineData("/catalog/scalar", "/catalog", "catalog", 8080)]
    [InlineData("/procurement/scalar", "/procurement", "procurement", 8080)]
    [InlineData("/file/scalar", "/file", "file", 8080)]
    [InlineData("/Jobs/scalar", "/Jobs", "career", 8080)]
    [InlineData("/accounting/scalar", "/accounting", "accounting", 8080)]
    public void Render_ForwardsCommittedCanonicalApiAndScalarPathsWithoutRewriting(string requestPath, string prefix, string service, int port)
    {
        using var package = JsonDocument.Parse(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip"));
        AssertCanonicalRequest(package.RootElement, requestPath, prefix, service, port);
        Assert.False(package.RootElement.GetProperty("productionDeploymentAllowed").GetBoolean());
        Assert.Equal(0, package.RootElement.GetProperty("cutoverPercent").GetInt32());
    }

    [Theory]
    [InlineData("/countrywide/v1/countries")]
    [InlineData("/customers2/7")]
    [InlineData("/ordering/v1/orders")]
    [InlineData("/orders2/7")]
    [InlineData("/file-system/uploads")]
    [InlineData("/catalogue/scalar")]
    [InlineData("/JobsExtra/scalar")]
    [InlineData("/predictions")]
    [InlineData("/logger")]
    [InlineData("/swagger")]
    public void Render_DoesNotClaimUnrelatedOrRetiredPathSegments(string requestPath)
    {
        using var package = JsonDocument.Parse(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip"));
        Assert.Empty(ApiPaths(package.RootElement).Where(path => MatchesKubernetesPrefix(path.GetProperty("path").GetString()!, requestPath)));
    }

    [Theory]
    [InlineData("/orders/7", "/orders", "order")]
    [InlineData("/order/v1/orders/7", "/order", "order")]
    [InlineData("/orderstatuses/7", "/orderstatuses", "order")]
    [InlineData("/jobs/scalar", "/jobs", "career")]
    [InlineData("/Jobs/scalar", "/Jobs", "career")]
    [InlineData("/customer/v1/customers", "/customer", "customer")]
    [InlineData("/customers/7", "/customers", "customer")]
    [InlineData("/quotations/7", "/quotations", "quotation")]
    [InlineData("/quotation/v1/quotations", "/quotation", "quotation")]
    public void Render_KeepsLegacyAndCanonicalSegmentsUnambiguousUnderLongestPrefixSelection(string requestPath, string prefix, string service)
    {
        using var package = JsonDocument.Parse(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip"));
        AssertCanonicalRequest(package.RootElement, requestPath, prefix, service, 8080);
    }

    [Fact]
    public void Render_AllDeclaredApiPrefixesAreUniqueAndHaveOneSegmentMatch()
    {
        using var package = JsonDocument.Parse(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip"));
        var paths = ApiPaths(package.RootElement);
        Assert.Equal(31, paths.Length);
        Assert.Equal(31, paths.Select(path => path.GetProperty("path").GetString()).Distinct(StringComparer.Ordinal).Count());
        foreach (var path in paths)
        {
            var prefix = path.GetProperty("path").GetString()!;
            Assert.Equal("Prefix", path.GetProperty("pathType").GetString());
            Assert.Single(paths, candidate => MatchesKubernetesPrefix(candidate.GetProperty("path").GetString()!, prefix + "/retained-segment"));
        }
    }

    [Fact]
    public async Task WriteReviewScript_EmitsCommittedServicePortsWithoutClaimingNamespaceOrRuntimeAcceptance()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), "apphost-edge-cli-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var assembly = typeof(LegacyEdgeReviewPackage).Assembly.Location;
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly)));
            // This test-owned artifact correspondence is not an independently accepted build receipt.
            var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(RepositoryRoot(), "scripts", "write-edge-review-package.ps1"),
                "-AcmeEmail", "owner@maliev.test", "-ExistingStaticIpName", "existing-ip", "-OutputPath", outputPath, "-ReviewedAssemblySha256", hash })
            {
                start.ArgumentList.Add(argument);
            }
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The edge review process did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            await Task.WhenAll(output, error);
            Assert.Equal(0, process.ExitCode);
            using var package = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            var root = package.RootElement;
            AssertBackendPort(root, "api.maliev.com", "/auth", "legacy-maliev-auth-service", 80);
            AssertBackendPort(root, "api.maliev.com", "/employees", "legacy-maliev-employee-service", 80);
            AssertBackendPort(root, "intranet.maliev.com", "/", "legacy-maliev-intranet-bff", 80);
            AssertBackendPort(root, "api.maliev.com", "/countries", "legacy-maliev-country-service", 8080);
            AssertBackendPort(root, "api.maliev.com", "/emails", "legacy-maliev-notification-service", 8080);
            Assert.Equal(31, ApiPaths(root).Length);
            foreach (var entry in new[]
            {
                ("/country/v1/countries", "/country", "country"), ("/documents/scalar", "/documents", "document"),
                ("/customer/scalar", "/customer", "customer"), ("/order/scalar", "/order", "order"),
                ("/quotation/scalar", "/quotation", "quotation"), ("/employee/scalar", "/employee", "employee"),
                ("/catalog/scalar", "/catalog", "catalog"), ("/procurement/scalar", "/procurement", "procurement"),
                ("/file/scalar", "/file", "file"), ("/Jobs/scalar", "/Jobs", "career"),
                ("/accounting/scalar", "/accounting", "accounting")
            })
            {
                AssertCanonicalRequest(root, entry.Item1, entry.Item2, entry.Item3, entry.Item3 == "employee" ? 80 : 8080);
            }
            Assert.Empty(ApiPaths(root).Where(path => MatchesKubernetesPrefix(path.GetProperty("path").GetString()!, "/orders2/7")));
            Assert.True(root.GetProperty("reviewOnly").GetBoolean());
            Assert.False(root.GetProperty("productionDeploymentAllowed").GetBoolean());
            Assert.Equal(0, root.GetProperty("cutoverPercent").GetInt32());
            Assert.False(root.TryGetProperty("kind", out _));
            Assert.False(Assert.Single(root.GetProperty("externalIngressDependencies").EnumerateArray()).GetProperty("ownershipVerified").GetBoolean());
        }
        finally { File.Delete(outputPath); }
    }

    [Theory]
    [InlineData("", "existing-ip")]
    [InlineData("invalid", "existing-ip")]
    [InlineData("Name <owner@maliev.test>", "existing-ip")]
    [InlineData("owner@maliev.test", "")]
    [InlineData("owner@maliev.test", "UPPERCASE")]
    [InlineData("owner@maliev.test", "untrusted\nannotation")]
    [InlineData("owner@maliev.test", "existing-ip\n")]
    [InlineData("owner@maliev.test", "existing-ip\r\n")]
    public void Render_RejectsMissingOrInvalidOwnerInputs(string email, string staticIp)
    {
        Assert.Throws<ArgumentException>(() => LegacyEdgeReviewPackage.Render(email, staticIp));
    }

    [Fact]
    public void Render_AcceptsOneCharacterStaticIpName()
    {
        using var package = JsonDocument.Parse(LegacyEdgeReviewPackage.Render("owner@maliev.test", "a"));
        Assert.Equal("a", Single(package.RootElement.GetProperty("objects").EnumerateArray().ToArray(), "Ingress")
            .GetProperty("metadata").GetProperty("annotations").GetProperty("kubernetes.io/ingress.global-static-ip-name").GetString());
    }

    private static void AssertBackendPort(JsonElement root, string host, string path, string service, int port)
    {
        var ingress = Single(root.GetProperty("objects").EnumerateArray().ToArray(), "Ingress");
        Assert.Equal("maliev-legacy", ingress.GetProperty("metadata").GetProperty("namespace").GetString());
        var rule = Assert.Single(ingress.GetProperty("spec").GetProperty("rules").EnumerateArray(),
            item => item.GetProperty("host").GetString() == host);
        var backend = Assert.Single(rule.GetProperty("http").GetProperty("paths").EnumerateArray(),
            item => item.GetProperty("path").GetString() == path).GetProperty("backend").GetProperty("service");
        Assert.Equal(service, backend.GetProperty("name").GetString());
        Assert.Equal(port, backend.GetProperty("port").GetProperty("number").GetInt32());
    }

    private static JsonElement[] ApiPaths(JsonElement root) =>
        Assert.Single(Single(root.GetProperty("objects").EnumerateArray().ToArray(), "Ingress")
            .GetProperty("spec").GetProperty("rules").EnumerateArray(), rule => rule.GetProperty("host").GetString() == "api.maliev.com")
            .GetProperty("http").GetProperty("paths").EnumerateArray().ToArray();

    // Independent Kubernetes Prefix specification model over the actual renderer/CLI output;
    // this verifies declared routing only and cannot establish a running GCE controller's behavior.
    private static bool MatchesKubernetesPrefix(string prefix, string requestPath) =>
        string.Equals(prefix, requestPath, StringComparison.Ordinal) || requestPath.StartsWith(prefix + "/", StringComparison.Ordinal);

    private static void AssertCanonicalRequest(JsonElement root, string requestPath, string prefix, string service, int port)
    {
        var matches = ApiPaths(root).Where(path => MatchesKubernetesPrefix(path.GetProperty("path").GetString()!, requestPath))
            .OrderByDescending(path => path.GetProperty("path").GetString()!.Length).ToArray();
        var selected = Assert.Single(matches);
        Assert.Equal(prefix, selected.GetProperty("path").GetString());
        AssertBackendPort(root, "api.maliev.com", prefix, "legacy-maliev-" + service + "-service", port);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.AppHost.slnx"))) { return directory.FullName; }
        }
        throw new DirectoryNotFoundException("The owned AppHost test checkout was not found.");
    }

    private static JsonElement Single(JsonElement[] objects, string kind) =>
        Assert.Single(objects, item => item.GetProperty("kind").GetString() == kind);

    private static void AssertService(JsonElement[] paths, string path, string service) =>
        Assert.Equal(service, Assert.Single(paths, item => item.GetProperty("path").GetString() == path)
            .GetProperty("backend").GetProperty("service").GetProperty("name").GetString());
}
