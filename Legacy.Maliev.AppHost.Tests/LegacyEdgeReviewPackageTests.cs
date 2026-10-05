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
        Assert.DoesNotContain(ApiPaths(package.RootElement), path => MatchesKubernetesPrefix(path.GetProperty("path").GetString()!, requestPath));
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
            Assert.DoesNotContain(ApiPaths(root), path => MatchesKubernetesPrefix(path.GetProperty("path").GetString()!, "/orders2/7"));
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

    [Fact]
    public void Render_VersionTwoAddsOnlyConditionalIntentWhileExplicitOneIsByteIdentical()
    {
        var legacy = LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip");
        Assert.Equal(legacy, LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip", 1));
        using var original = JsonDocument.Parse(legacy);
        using var versionTwo = JsonDocument.Parse(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip", 2));
        Assert.False(original.RootElement.TryGetProperty("workloadResourceReview", out _));
        Assert.Equal(2, versionTwo.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(original.RootElement.EnumerateObject().Count() + 1, versionTwo.RootElement.EnumerateObject().Count());
        foreach (var property in original.RootElement.EnumerateObject().Where(property => property.Name != "schemaVersion"))
        {
            Assert.True(JsonElement.DeepEquals(property.Value, versionTwo.RootElement.GetProperty(property.Name)), property.Name);
        }
        Assert.Equal(8, versionTwo.RootElement.GetProperty("objects").GetArrayLength());
        Assert.Equal(31, ApiPaths(versionTwo.RootElement).Length);
    }

    [Theory]
    [InlineData("Maliev.AuthService.Api", 2, null, null, "e5bfb452fc469c2bf3046b15c7d36674cf0dc2ad")]
    [InlineData("Maliev.CustomerService.Api", 2, null, 256, "6f9446866c889f28c4126af6067cfa8f15089efe")]
    [InlineData("Maliev.OrderService.Api", 2, null, 256, "d6b5940a5b729ecf6b646f6d34dd0001c8897578")]
    [InlineData("Maliev.InvoiceService.Api", 2, null, null, "314df3239fa4afe5867e3dec58bf99e58673ca61")]
    [InlineData("Maliev.QuotationService.Api", 2, null, 256, "335838886cfc312644f22f1261aedd15386acc3e")]
    [InlineData("Maliev.PaymentService.Api", 2, null, null, "314f85f644899fc34376a2ff6e2543e3cc329b5b")]
    [InlineData("Maliev.Intranet", 2, 256, 384, "aeff3f7eca1bac004f59159398f3192ec2bfd915")]
    [InlineData("Maliev.MaterialService.Api", null, null, 256, "04f86f9f94af3acf09fd5331a0c7458036ec9d0d")]
    public void Render_ResourceIntentRetainsExactSourceWorkloadChangesAndUntouchedNulls(
        string workload, int? replicas, int? requestMi, int? limitMi, string blob)
    {
        using var package = JsonDocument.Parse(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip", 2));
        var record = ResourceRecord(package.RootElement, workload);
        Assert.Equal(workload + "/deployment.yaml", record.GetProperty("sourceManifestPath").GetString());
        Assert.Equal(blob, record.GetProperty("sourceCheckpointManifestBlob").GetString());
        Assert.Equal(replicas, NullableInt(record.GetProperty("replicas")));
        Assert.Equal(requestMi, NullableInt(record.GetProperty("memoryRequestMi")));
        Assert.Equal(limitMi, NullableInt(record.GetProperty("memoryLimitMi")));
        var preconditions = record.GetProperty("preconditions");
        Assert.Equal("Test-Path", preconditions.GetProperty("fileExistsCheck").GetString());
        Assert.Equal("Skip", preconditions.GetProperty("missingFileBehavior").GetString());
        Assert.False(preconditions.GetProperty("contentMatchVerified").GetBoolean());
        Assert.Equal(replicas.HasValue, preconditions.GetProperty("replicas").ValueKind != JsonValueKind.Null);
        Assert.Equal(requestMi.HasValue, preconditions.GetProperty("memoryRequest").ValueKind != JsonValueKind.Null);
        Assert.Equal(limitMi.HasValue, preconditions.GetProperty("memoryLimit").ValueKind != JsonValueKind.Null);
        Assert.False(record.TryGetProperty("successorWorkload", out _));
    }

    [Fact]
    public void Render_ResourceIntentRetainsSourceProvenanceAndUnacceptedConsolidationGates()
    {
        using var package = JsonDocument.Parse(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip", 2));
        var root = package.RootElement;
        var review = root.GetProperty("workloadResourceReview");
        Assert.Equal("c2b26cd90bf0cd7838c8fe3044c66c2ad3d6a511", review.GetProperty("sourceCommit").GetString());
        Assert.Equal("da5bf11d23285569cb8020eec7ac9ba6acf4a67d", review.GetProperty("sourceParent").GetString());
        Assert.Equal("135e526d0dab85c415b3afdcefd7b70fe2c82e2f", review.GetProperty("sourceCheckpoint").GetString());
        Assert.Equal("update-replicas-and-memory.ps1", review.GetProperty("sourceScript").GetString());
        Assert.Equal("b8aae2274eb5b8cbfd676b22e036123015b0f334", review.GetProperty("sourceScriptBlob").GetString());
        Assert.Equal("ConditionalTextReplacementIntent", review.GetProperty("semantics").GetString());
        foreach (var gate in new[] { "operationExecutionVerified", "manifestAdoptionVerified", "capacityAccepted" })
        {
            Assert.False(review.GetProperty(gate).GetBoolean());
        }
        var records = review.GetProperty("workloads").EnumerateArray().ToArray();
        Assert.Equal(8, records.Length);
        Assert.Equal(8, records.Select(record => record.GetProperty("sourceWorkload").GetString()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(7, records.Count(record => NullableInt(record.GetProperty("replicas")) == 2));
        Assert.Equal(4, records.Count(record => NullableInt(record.GetProperty("memoryLimitMi")) == 256));
        Assert.Single(records, record => record.GetProperty("memoryRequestMi").ValueKind != JsonValueKind.Null);
        Assert.Contains(review.GetProperty("unresolvedGates").EnumerateArray(), gate => gate.GetString()!.Contains("Accounting", StringComparison.Ordinal));
        Assert.Contains(review.GetProperty("unresolvedGates").EnumerateArray(), gate => gate.GetString()!.Contains("BFF", StringComparison.Ordinal));
        Assert.DoesNotContain(root.GetProperty("objects").EnumerateArray(), item => item.GetProperty("kind").GetString() is "Deployment" or "Patch");
        Assert.True(root.GetProperty("reviewOnly").GetBoolean());
        Assert.False(root.GetProperty("productionDeploymentAllowed").GetBoolean());
        Assert.Equal(0, root.GetProperty("cutoverPercent").GetInt32());
    }

    [Theory]
    [InlineData("Maliev.AuthService.Api", "replicas", "replicas: 1", "replicas: 2")]
    [InlineData("Maliev.AuthService.Api", "replicas", "replicas: 2", "replicas: 2")]
    [InlineData("Maliev.AuthService.Api", "replicas", "replicas: 10", "replicas: 20")]
    [InlineData("Maliev.AuthService.Api", "replicas", "replicas:  1", "replicas:  1")]
    [InlineData("Maliev.CustomerService.Api", "memoryLimit", "limits:\n cpu: 100m\n memory: 192Mi", "limits:\n cpu: 100m\n memory: 256Mi")]
    [InlineData("Maliev.CustomerService.Api", "memoryLimit", "limits:\n cpu: 100m\n memory: 256Mi", "limits:\n cpu: 100m\n memory: 256Mi")]
    [InlineData("Maliev.CustomerService.Api", "memoryLimit", "limits:\n cpu: 1\n memory: 192Mi", "limits:\n cpu: 1\n memory: 192Mi")]
    [InlineData("Maliev.CustomerService.Api", "memoryLimit", "requests:\n cpu: 100m\n memory: 192Mi", "requests:\n cpu: 100m\n memory: 192Mi")]
    [InlineData("Maliev.Intranet", "memoryRequest", "requests:\n cpu: 15m\n memory: 128Mi", "requests:\n cpu: 15m\n memory: 256Mi")]
    [InlineData("Maliev.Intranet", "memoryRequest", "requests:\n cpu: 15m\n memory: 384Mi", "requests:\n cpu: 15m\n memory: 256Mi")]
    [InlineData("Maliev.Intranet", "memoryLimit", "limits:\n cpu: 75m\n memory: 192Mi", "limits:\n cpu: 75m\n memory: 384Mi")]
    [InlineData("Maliev.Intranet", "memoryLimit", "limits:\n cpu: 75m\n memory: 512Mi", "limits:\n cpu: 75m\n memory: 384Mi")]
    [InlineData("Maliev.Intranet", "memoryLimit", "limits:\n memory: 512Mi", "limits:\n memory: 512Mi")]
    public void Render_ResourcePreconditionsDescribeConditionalTextMatchesRatherThanUnconditionalSizing(
        string workload, string operation, string input, string expected)
    {
        using var package = JsonDocument.Parse(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip", 2));
        var replacement = ResourceRecord(package.RootElement, workload).GetProperty("preconditions").GetProperty(operation);
        Assert.Equal("PowerShell -replace", replacement.GetProperty("operator").GetString());
        Assert.True(replacement.GetProperty("caseInsensitive").GetBoolean());
        Assert.True(replacement.GetProperty("allMatches").GetBoolean());
        Assert.Equal("Unchanged", replacement.GetProperty("noMatchBehavior").GetString());
        // Independently apply the emitted regex to bounded text, never to a file or workload.
        Assert.Equal(expected, System.Text.RegularExpressions.Regex.Replace(input,
            replacement.GetProperty("pattern").GetString()!, replacement.GetProperty("replacement").GetString()!,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void Render_RejectsUnreviewedResourceSchemaVersions(int version) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip", version));

    [Fact]
    public async Task WriteReviewScript_VersionTwoConsumesConditionalIntentAndPreservesExclusiveOutput()
    {
        var path = Path.Combine(Path.GetTempPath(), "apphost-resource-cli-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var first = await RunResourceReviewScript(path, "2");
            Assert.True(first.ExitCode == 0, first.Error);
            var bytes = await File.ReadAllBytesAsync(path);
            using var package = JsonDocument.Parse(bytes);
            Assert.Equal(2, package.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(8, package.RootElement.GetProperty("workloadResourceReview").GetProperty("workloads").GetArrayLength());
            Assert.Equal(384, ResourceRecord(package.RootElement, "Maliev.Intranet").GetProperty("memoryLimitMi").GetInt32());
            Assert.Equal(JsonValueKind.Null, ResourceRecord(package.RootElement, "Maliev.MaterialService.Api").GetProperty("replicas").ValueKind);
            Assert.Equal("replicas: 1", ResourceRecord(package.RootElement, "Maliev.AuthService.Api")
                .GetProperty("preconditions").GetProperty("replicas").GetProperty("pattern").GetString());
            Assert.False(package.RootElement.GetProperty("productionDeploymentAllowed").GetBoolean());
            Assert.Equal(8, package.RootElement.GetProperty("objects").GetArrayLength());
            Assert.Equal(31, ApiPaths(package.RootElement).Length);
            var second = await RunResourceReviewScript(path, "2");
            Assert.NotEqual(0, second.ExitCode);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("4", false)]
    [InlineData("2", true)]
    [InlineData("3", true)]
    public async Task WriteReviewScript_RejectsInvalidSchemaOrHashBeforeCreatingOutput(string version, bool wrongHash)
    {
        var path = Path.Combine(Path.GetTempPath(), "apphost-resource-rejected-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var result = await RunResourceReviewScript(path, version, wrongHash);
            Assert.NotEqual(0, result.ExitCode);
            Assert.False(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task WriteReviewScript_ExplicitVersionOnePreservesOriginalBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), "apphost-resource-v1-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var result = await RunResourceReviewScript(path, "1");
            Assert.True(result.ExitCode == 0, result.Error);
            Assert.Equal(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip"), await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("invalid", "existing-ip")]
    [InlineData("owner@maliev.test", "UPPERCASE")]
    public void Render_VersionTwoStillRejectsInvalidOwnerInputs(string email, string staticIp) =>
        Assert.Throws<ArgumentException>(() => LegacyEdgeReviewPackage.Render(email, staticIp, 2));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Render_InstallerVersionThreeKeepsPriorEnvelopePropertiesAndDefaultOne(int priorVersion)
    {
        var priorText = LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip", priorVersion);
        if (priorVersion == 1)
        {
            Assert.Equal(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip"), priorText);
        }
        using var prior = JsonDocument.Parse(priorText);
        using var current = JsonDocument.Parse(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip", 3));
        Assert.False(prior.RootElement.TryGetProperty("controllerInstallationReview", out _));
        Assert.Equal(3, current.RootElement.GetProperty("schemaVersion").GetInt32());
        foreach (var property in prior.RootElement.EnumerateObject().Where(property => property.Name != "schemaVersion"))
        {
            Assert.True(JsonElement.DeepEquals(property.Value, current.RootElement.GetProperty(property.Name)), property.Name);
        }
        Assert.Equal(8, current.RootElement.GetProperty("objects").GetArrayLength());
        Assert.Equal(31, ApiPaths(current.RootElement).Length);
        Assert.False(current.RootElement.GetProperty("productionDeploymentAllowed").GetBoolean());
    }

    [Fact]
    public void Render_InstallerPlanRetainsExactSourceVersionAndTwoPathProvenance()
    {
        using var package = JsonDocument.Parse(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip", 3));
        var review = package.RootElement.GetProperty("controllerInstallationReview");
        Assert.Equal("dc03cc8e425f43b67fc04b40af4e18e0aeebf3d1", review.GetProperty("sourceCommit").GetString());
        Assert.Equal("a5b9ee39149c64d7649b708e894520f788cdfb61", review.GetProperty("sourceParent").GetString());
        Assert.Equal("135e526d0dab85c415b3afdcefd7b70fe2c82e2f", review.GetProperty("sourceCheckpoint").GetString());
        Assert.Equal("Maliev.CertManager/deploy-certmanager.ps1", review.GetProperty("sourceInstallerPath").GetString());
        Assert.Equal("ed5f31758a1e37adc7a4b04263bfe051232890a7", review.GetProperty("sourceInstallerBeforeBlob").GetString());
        Assert.Equal("e909854a49c63bcd8b4dcaebad5187b46cb50630", review.GetProperty("sourceInstallerBlob").GetString());
        Assert.Equal("Maliev.CertManager/test-resources.yaml", review.GetProperty("sourceSmokePath").GetString());
        Assert.Equal("72496d6803cc66854e9b99fd99b6c646f95048f7", review.GetProperty("sourceSmokeBlob").GetString());
        Assert.Equal("v1.18.2", review.GetProperty("requestedSourceVersion").GetString());
        Assert.Equal("https://github.com/cert-manager/cert-manager/releases/download/v1.18.2/cert-manager.yaml",
            review.GetProperty("sourceInstallManifestUrl").GetString());
        Assert.Equal("UnexecutedSourceInstallationPlan", review.GetProperty("semantics").GetString());
        Assert.Equal("Unverified", review.GetProperty("status").GetString());
    }

    [Fact]
    public void Render_InstallerPlanKeepsAllAcceptanceAndSourceFailurePropagationUnverified()
    {
        using var package = JsonDocument.Parse(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip", 3));
        var review = package.RootElement.GetProperty("controllerInstallationReview");
        foreach (var property in new[] { "executionAllowed", "controllerInstalledVerified", "smokeTestVerified", "backupVerified",
            "namespaceOwnershipVerified", "nativeExitPropagationVerified", "sourceChecksNativeExitCodes",
            "sourceSetsStopErrorPreference", "commentedClusterAdminBindingIncluded" })
        {
            Assert.False(review.GetProperty(property).GetBoolean());
        }
        var preparation = review.GetProperty("sourceLocalPreparation");
        Assert.Equal(".backup", preparation.GetProperty("backupDirectory").GetString());
        Assert.Equal("Test-Path .backup is false", preparation.GetProperty("createDirectoryCondition").GetString());
        Assert.Equal("certificates-backup-yyyyMMdd-HHmmss.yaml", preparation.GetProperty("certificateBackupPattern").GetString());
        Assert.Equal("clusterissuers-backup-yyyyMMdd-HHmmss.yaml", preparation.GetProperty("clusterIssuerBackupPattern").GetString());
        Assert.True(preparation.GetProperty("backupStandardErrorSuppressed").GetBoolean());
        Assert.False(preparation.GetProperty("performed").GetBoolean());
        Assert.Equal(5, preparation.GetProperty("conditionalCleanupDelaySeconds").GetInt32());
        Assert.True(preparation.GetProperty("terminalPause").GetBoolean());
        Assert.Contains(review.GetProperty("unresolvedGates").EnumerateArray(), gate => gate.GetString()!.Contains("destructive", StringComparison.Ordinal));
        Assert.Contains(review.GetProperty("unresolvedGates").EnumerateArray(), gate => gate.GetString()!.Contains("quoting", StringComparison.Ordinal));
        foreach (var operation in review.GetProperty("operations").EnumerateArray())
        {
            Assert.False(operation.GetProperty("executionAllowed").GetBoolean());
            Assert.False(operation.GetProperty("sourceExitCodeChecked").GetBoolean());
            Assert.DoesNotContain("clusterrolebinding", operation.GetProperty("sourceInvocation").GetString()!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Render_InstallerPlanRetainsAllSourceOperationsInOrderWithoutInventingUpgradeSafety()
    {
        using var package = JsonDocument.Parse(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip", 3));
        var operations = package.RootElement.GetProperty("controllerInstallationReview").GetProperty("operations").EnumerateArray().ToArray();
        string[] expected =
        [
            "backup-certificates", "backup-clusterissuers", "create-namespace-client-dry-run-and-apply", "label-namespace",
            "discover-existing-crds", "delete-controller-namespace",
            "delete-crd-certificaterequests.cert-manager.io", "delete-crd-certificates.cert-manager.io",
            "delete-crd-challenges.acme.cert-manager.io", "delete-crd-clusterissuers.cert-manager.io",
            "delete-crd-issuers.cert-manager.io", "delete-crd-orders.acme.cert-manager.io",
            "clear-finalizers-certificaterequests.cert-manager.io", "clear-finalizers-certificates.cert-manager.io",
            "clear-finalizers-challenges.acme.cert-manager.io", "clear-finalizers-clusterissuers.cert-manager.io",
            "clear-finalizers-issuers.cert-manager.io", "clear-finalizers-orders.acme.cert-manager.io",
            "force-delete-crd-certificaterequests.cert-manager.io", "force-delete-crd-certificates.cert-manager.io",
            "force-delete-crd-challenges.acme.cert-manager.io", "force-delete-crd-clusterissuers.cert-manager.io",
            "force-delete-crd-issuers.cert-manager.io", "force-delete-crd-orders.acme.cert-manager.io",
            "apply-controller-manifest", "feature-gate-client-dry-run", "wait-controller-pods", "list-controller-pods",
            "apply-smoke-resources", "wait-smoke-certificate", "describe-smoke-certificate", "delete-smoke-resources"
        ];
        Assert.Equal(expected, operations.Select(operation => operation.GetProperty("id").GetString()));
        Assert.Equal(Enumerable.Range(1, 32), operations.Select(operation => operation.GetProperty("sourceOrder").GetInt32()));
        Assert.Equal(32, operations.Select(operation => operation.GetProperty("id").GetString()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("kubectl create namespace cert-manager --dry-run=client -o yaml | kubectl apply -f -",
            operations[2].GetProperty("sourceInvocation").GetString());
        Assert.False(operations[2].GetProperty("clientDryRunOnly").GetBoolean());
        Assert.Equal("$existingCRDs = kubectl get crd | Select-String \"cert-manager.io\"", operations[4].GetProperty("sourceInvocation").GetString());
        Assert.Contains("--dry-run=client", operations[25].GetProperty("sourceInvocation").GetString()!, StringComparison.Ordinal);
        Assert.True(operations[25].GetProperty("clientDryRunOnly").GetBoolean());
        Assert.Contains("app.kubernetes.io/instance=cert-manager", operations[26].GetProperty("sourceInvocation").GetString()!, StringComparison.Ordinal);
        Assert.Equal("kubectl wait certificate/selfsigned-cert --namespace cert-manager-test --for=condition=Ready --timeout=120s",
            operations[29].GetProperty("sourceInvocation").GetString());
    }

    [Theory]
    [InlineData(1, "Always", false, null)]
    [InlineData(3, "Always", false, null)]
    [InlineData(5, "Always", false, null)]
    [InlineData(6, "ExistingCrdQueryMatched", false, null)]
    [InlineData(7, "ExistingCrdQueryMatched", false, 30)]
    [InlineData(24, "ExistingCrdQueryMatched", false, null)]
    [InlineData(26, "Always", true, null)]
    [InlineData(27, "Always", false, 180)]
    [InlineData(30, "Always", false, 120)]
    public void Render_InstallerConditionsAndClientOnlyDryRunDoNotClaimAppliedState(int order, string condition, bool dryRunOnly, int? timeout)
    {
        using var package = JsonDocument.Parse(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip", 3));
        var operation = Assert.Single(package.RootElement.GetProperty("controllerInstallationReview").GetProperty("operations").EnumerateArray(),
            item => item.GetProperty("sourceOrder").GetInt32() == order);
        Assert.Equal(condition, operation.GetProperty("sourceCondition").GetString());
        Assert.Equal(dryRunOnly, operation.GetProperty("clientDryRunOnly").GetBoolean());
        Assert.Equal(timeout, NullableInt(operation.GetProperty("timeoutSeconds")));
        Assert.False(operation.GetProperty("executionAllowed").GetBoolean());
    }

    [Theory]
    [InlineData("certificaterequests.cert-manager.io")]
    [InlineData("certificates.cert-manager.io")]
    [InlineData("challenges.acme.cert-manager.io")]
    [InlineData("clusterissuers.cert-manager.io")]
    [InlineData("issuers.cert-manager.io")]
    [InlineData("orders.acme.cert-manager.io")]
    public void Render_InstallerSixCrdCleanupTargetsRemainConditionalData(string crd)
    {
        using var package = JsonDocument.Parse(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip", 3));
        var operations = package.RootElement.GetProperty("controllerInstallationReview").GetProperty("operations").EnumerateArray().ToArray();
        foreach (var prefix in new[] { "delete-crd-", "clear-finalizers-", "force-delete-crd-" })
        {
            var operation = Assert.Single(operations, item => item.GetProperty("id").GetString() == prefix + crd);
            Assert.Equal("ExistingCrdQueryMatched", operation.GetProperty("sourceCondition").GetString());
            Assert.Contains(crd, operation.GetProperty("sourceInvocation").GetString()!, StringComparison.Ordinal);
            Assert.False(operation.GetProperty("executionAllowed").GetBoolean());
        }
        var finalizer = Assert.Single(operations, item => item.GetProperty("id").GetString() == "clear-finalizers-" + crd);
        Assert.Contains("\\\"metadata\\\"", finalizer.GetProperty("sourceInvocation").GetString()!, StringComparison.Ordinal);
        Assert.Contains("--type=merge 2>$null", finalizer.GetProperty("sourceInvocation").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_InstallerSmokeResourcesKeepExactSourceWireWithoutProductionAdoption()
    {
        using var package = JsonDocument.Parse(LegacyEdgeReviewPackage.Render("owner@maliev.test", "existing-ip", 3));
        using var expected = JsonDocument.Parse("""
            [
              {"apiVersion":"v1","kind":"Namespace","metadata":{"name":"cert-manager-test"}},
              {"apiVersion":"cert-manager.io/v1","kind":"Issuer","metadata":{"name":"test-selfsigned","namespace":"cert-manager-test"},"spec":{"selfSigned":{}}},
              {"apiVersion":"cert-manager.io/v1","kind":"Certificate","metadata":{"name":"selfsigned-cert","namespace":"cert-manager-test"},"spec":{"dnsNames":["maliev.com"],"secretName":"selfsigned-cert-tls","issuerRef":{"name":"test-selfsigned"}}}
            ]
            """);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, package.RootElement.GetProperty("controllerInstallationReview").GetProperty("sourceSmokeResources")));
        Assert.Equal(8, package.RootElement.GetProperty("objects").GetArrayLength());
        Assert.False(package.RootElement.TryGetProperty("kind", out _));
        Assert.DoesNotContain(package.RootElement.GetProperty("objects").EnumerateArray(),
            item => item.GetProperty("metadata").GetProperty("name").GetString() == "selfsigned-cert");
    }

    [Fact]
    public async Task WriteReviewScript_VersionThreeEmitsOnlyUnexecutedPlanAndPreservesExclusiveOutput()
    {
        var path = Path.Combine(Path.GetTempPath(), "apphost-installer-cli-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var first = await RunResourceReviewScript(path, "3");
            Assert.True(first.ExitCode == 0, first.Error);
            var bytes = await File.ReadAllBytesAsync(path);
            using var package = JsonDocument.Parse(bytes);
            Assert.Equal(3, package.RootElement.GetProperty("schemaVersion").GetInt32());
            var installer = package.RootElement.GetProperty("controllerInstallationReview");
            Assert.Equal(32, installer.GetProperty("operations").GetArrayLength());
            Assert.Equal(3, installer.GetProperty("sourceSmokeResources").GetArrayLength());
            Assert.False(installer.GetProperty("executionAllowed").GetBoolean());
            Assert.False(installer.GetProperty("controllerInstalledVerified").GetBoolean());
            Assert.False(installer.GetProperty("smokeTestVerified").GetBoolean());
            Assert.False(installer.GetProperty("nativeExitPropagationVerified").GetBoolean());
            Assert.Equal(8, package.RootElement.GetProperty("objects").GetArrayLength());
            Assert.Equal(31, ApiPaths(package.RootElement).Length);
            Assert.False(package.RootElement.GetProperty("productionDeploymentAllowed").GetBoolean());
            var second = await RunResourceReviewScript(path, "3");
            Assert.NotEqual(0, second.ExitCode);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("invalid", "existing-ip")]
    [InlineData("owner@maliev.test", "UPPERCASE")]
    public void Render_VersionThreeStillRejectsInvalidOwnerInputs(string email, string staticIp) =>
        Assert.Throws<ArgumentException>(() => LegacyEdgeReviewPackage.Render(email, staticIp, 3));

    [Theory]
    [InlineData("[]", "Unknown", null, "Unknown")]
    [InlineData("[{\"sourceService\":\"maliev.pdfservice.api\",\"deployScriptIsFile\":true,\"exitCode\":0}]", "Unknown", null, "Unknown")]
    [InlineData("[{\"sourceService\":\"maliev.pdfservice.api\",\"deployScriptIsFile\":true,\"invocationFailed\":null,\"exitCode\":0}]", "Unknown", null, "Unknown")]
    [InlineData("[{\"sourceService\":\"maliev.pdfservice.api\",\"deployScriptIsFile\":null,\"invocationFailed\":false,\"exitCode\":0}]", "Unknown", null, "Unknown")]
    [InlineData("[{\"sourceService\":\"maliev.pdfservice.api\",\"deployScriptIsFile\":true,\"invocationFailed\":false,\"exitCode\":null}]", "Unknown", null, "Unknown")]
    [InlineData("[{\"sourceService\":\"maliev.pdfservice.api\",\"deployScriptIsFile\":false}]", "ReportedFailure", 1, "MissingLeaf")]
    [InlineData("[{\"sourceService\":\"maliev.pdfservice.api\",\"deployScriptIsFile\":true,\"invocationFailed\":true}]", "ReportedFailure", 1, "InvocationFailed")]
    [InlineData("[{\"sourceService\":\"maliev.pdfservice.api\",\"deployScriptIsFile\":true,\"invocationFailed\":false,\"exitCode\":42}]", "ReportedFailure", 1, "ChildExitFailure")]
    [InlineData("[{\"sourceService\":\"maliev.pdfservice.api\",\"deployScriptIsFile\":true,\"invocationFailed\":false,\"exitCode\":-5}]", "ReportedFailure", 1, "ChildExitFailure")]
    [InlineData("[{\"sourceService\":\"maliev.pdfservice.api\",\"deployScriptIsFile\":true,\"invocationFailed\":false,\"exitCode\":0}]", "ReportedComplete", 0, "ReportedSuccess")]
    public async Task WriteReviewScript_SelectorSuppliedOutcomesRequireAllSuccessEvidence(string children, string status, int? modeledExit, string stepOutcome)
    {
        var result = await RunSelectorReview(SelectorInput("pdf", children));
        Assert.True(result.ExitCode == 0, result.Error);
        using var document = JsonDocument.Parse(result.Output!);
        var review = document.RootElement.GetProperty("releaseSelectorReview");
        Assert.Equal(status, review.GetProperty("status").GetString());
        Assert.Equal(modeledExit, NullableInt(review.GetProperty("modeledSourceExitCode")));
        var step = Assert.Single(review.GetProperty("trace").EnumerateArray());
        Assert.Equal(stepOutcome, step.GetProperty("outcome").GetString());
        Assert.True(step.GetProperty("consumed").GetBoolean());
        Assert.False(review.GetProperty("observationsVerified").GetBoolean());
        Assert.False(review.GetProperty("sourceHelperExecutionVerified").GetBoolean());
        Assert.False(review.GetProperty("callerDirectoryRestoredVerified").GetBoolean());
        Assert.False(document.RootElement.GetProperty("productionDeploymentAllowed").GetBoolean());
        Assert.Equal(0, document.RootElement.GetProperty("cutoverPercent").GetInt32());
    }

    [Theory]
    [InlineData("all", 23, "deploy_all.ps1", "f74d2c83fecd5f3e9ff49712caa46a09f12677a4", true)]
    [InlineData("api", 21, "deploy_api.ps1",
        "c6604936515c87d9a9337c4b84a72ceb94e63c7e", true)]
    [InlineData("web", 1, "deploy_web.ps1", "c0d90c21e655f66e30e35cb08b29fbf62428446b", false)]
    [InlineData("intranet", 1, "deploy_intranet.ps1", "4fb465cb04f7c174ec8130ca8c4ecf1df424d6ba", false)]
    [InlineData("pdf", 1, "deploy_pdf.ps1", "240e57135f446a56e1f507aa255f338057652ffc", false)]
    public async Task WriteReviewScript_SelectorGroupsRetainExactSourceAndRetiredSuccessorDisposition(
        string selector, int count, string path, string blob, bool predictionSelected)
    {
        var result = await RunSelectorReview(SelectorInput(selector, "[]"));
        Assert.True(result.ExitCode == 0, result.Error);
        using var document = JsonDocument.Parse(result.Output!);
        var review = document.RootElement.GetProperty("releaseSelectorReview");
        Assert.Equal(path, review.GetProperty("sourceSelectorPath").GetString());
        Assert.Equal(blob, review.GetProperty("sourceSelectorBlob").GetString());
        Assert.Equal("135e526d0dab85c415b3afdcefd7b70fe2c82e2f", review.GetProperty("sourceCheckpoint").GetString());
        var members = review.GetProperty("selectedSourceServices").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(count, members.Length);
        Assert.DoesNotContain("maliev.loggerservice.api", members);
        Assert.Equal(predictionSelected, members.Contains("maliev.predictionservice.api", StringComparer.Ordinal));
        var retired = review.GetProperty("retiredSuccessorDispositions").EnumerateArray().ToArray();
        if (predictionSelected)
        {
            var disposition = Assert.Single(retired);
            Assert.Equal("maliev.predictionservice.api", disposition.GetProperty("sourceService").GetString());
            Assert.Equal("OwnerRetired", disposition.GetProperty("disposition").GetString());
            Assert.False(disposition.GetProperty("runtimeRegistered").GetBoolean());
            Assert.False(disposition.GetProperty("recreateRuntimeAllowed").GetBoolean());
        }
        else { Assert.Empty(retired); }
        Assert.False(review.GetProperty("successorMappingVerified").GetBoolean());
        Assert.True(review.GetProperty("sourceRequiresInnerPopLocation").GetBoolean());
        Assert.True(review.GetProperty("sourceRequiresOuterCallerDirectoryRestore").GetBoolean());
        Assert.False(review.GetProperty("callerDirectoryRestoredVerified").GetBoolean());
        Assert.Equal(4, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(8, document.RootElement.GetProperty("objects").GetArrayLength());
        Assert.Equal(31, ApiPaths(document.RootElement).Length);
        Assert.Equal(32, document.RootElement.GetProperty("controllerInstallationReview").GetProperty("operations").GetArrayLength());
        Assert.Equal(8, document.RootElement.GetProperty("workloadResourceReview").GetProperty("workloads").GetArrayLength());
    }

    [Theory]
    [InlineData("true,false,23", "ReportedFailure", 1, "ChildExitFailure")]
    [InlineData("true,null,0", "Unknown", null, "Unknown")]
    public async Task WriteReviewScript_SelectorTraceStopsFirstFailureOrUnknownAndDoesNotConsumeLaterReports(
        string firstValues, string status, int? modeledExit, string firstOutcome)
    {
        var values = firstValues.Split(',');
        var children = "[{\"sourceService\":\"maliev.authservice.api\",\"deployScriptIsFile\":" + values[0]
            + ",\"invocationFailed\":" + values[1] + ",\"exitCode\":" + values[2]
            + "},{\"sourceService\":\"maliev.countryservice.api\",\"deployScriptIsFile\":true,\"invocationFailed\":false,\"exitCode\":14}]";
        var result = await RunSelectorReview(SelectorInput("api", children));
        Assert.True(result.ExitCode == 0, result.Error);
        using var document = JsonDocument.Parse(result.Output!);
        var review = document.RootElement.GetProperty("releaseSelectorReview");
        Assert.Equal(status, review.GetProperty("status").GetString());
        Assert.Equal(modeledExit, NullableInt(review.GetProperty("modeledSourceExitCode")));
        var trace = review.GetProperty("trace").EnumerateArray().ToArray();
        Assert.Equal(21, trace.Length);
        Assert.Equal("maliev.authservice.api", trace[0].GetProperty("sourceService").GetString());
        Assert.Equal(firstOutcome, trace[0].GetProperty("outcome").GetString());
        Assert.True(trace[0].GetProperty("consumed").GetBoolean());
        foreach (var step in trace.Skip(1))
        {
            Assert.False(step.GetProperty("consumed").GetBoolean());
            Assert.Equal("NotReached", step.GetProperty("outcome").GetString());
        }
    }

    [Fact]
    public async Task WriteReviewScript_SelectorReportedCompletionRetainsSourceOrderAndDoesNotAdmitRetiredRuntime()
    {
        string[] expected = ["maliev.authservice.api", "maliev.countryservice.api", "maliev.currencyservice.api",
            "maliev.customerservice.api", "maliev.emailservice.api", "maliev.employeeservice.api", "maliev.intranet",
            "maliev.invoiceservice.api", "maliev.jobservice.api", "maliev.materialservice.api", "maliev.messageservice.api",
            "maliev.orderservice.api", "maliev.orderstatusservice.api", "maliev.paymentservice.api", "maliev.pdfservice.api",
            "maliev.predictionservice.api", "maliev.purchaseorderservice.api", "maliev.quotationrequestservice.api",
            "maliev.quotationservice.api", "maliev.receiptservice.api", "maliev.supplierservice.api", "maliev.uploadservice.api", "maliev.web"];
        var children = JsonSerializer.Serialize(expected.Select(service => new
        {
            sourceService = service,
            deployScriptIsFile = true,
            invocationFailed = false,
            exitCode = 0
        }));
        var result = await RunSelectorReview(SelectorInput("all", children));
        Assert.True(result.ExitCode == 0, result.Error);
        using var document = JsonDocument.Parse(result.Output!);
        var review = document.RootElement.GetProperty("releaseSelectorReview");
        Assert.Equal(expected, review.GetProperty("selectedSourceServices").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("ReportedComplete", review.GetProperty("status").GetString());
        Assert.Equal(0, review.GetProperty("modeledSourceExitCode").GetInt32());
        Assert.All(review.GetProperty("trace").EnumerateArray(), step =>
        {
            Assert.True(step.GetProperty("consumed").GetBoolean());
            Assert.Equal("ReportedSuccess", step.GetProperty("outcome").GetString());
        });
        Assert.False(review.GetProperty("sourceHelperExecutionVerified").GetBoolean());
        Assert.False(review.GetProperty("observationsVerified").GetBoolean());
        Assert.False(document.RootElement.GetProperty("productionDeploymentAllowed").GetBoolean());
        Assert.False(Assert.Single(review.GetProperty("retiredSuccessorDispositions").EnumerateArray()).GetProperty("runtimeRegistered").GetBoolean());
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"selector\":\"pdf\",\"children\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"selector\":\"unknown\",\"children\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"selector\":\"PDF\",\"children\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"selector\":\"pdf\",\"children\":[],\"extra\":true}")]
    [InlineData("{\"schemaVersion\":1,\"selector\":\"pdf\",\"selector\":\"pdf\",\"children\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"selector\":\"pdf\",\"children\":null}")]
    [InlineData("{\"schemaVersion\":1,\"selector\":\"pdf\"}")]
    [InlineData("{\"schemaVersion\":1,\"selector\":\"pdf\",\"children\":[{\"sourceService\":\"unknown\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"selector\":\"pdf\",\"children\":[{\"sourceService\":\"maliev.pdfservice.api\",\"extra\":false}]}")]
    [InlineData("{\"schemaVersion\":1,\"selector\":\"pdf\",\"children\":[{\"sourceService\":\"maliev.pdfservice.api\",\"deployScriptIsFile\":1}]}")]
    [InlineData("{\"schemaVersion\":1,\"selector\":\"pdf\",\"children\":[{\"sourceService\":\"maliev.pdfservice.api\",\"invocationFailed\":\"false\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"selector\":\"pdf\",\"children\":[{\"sourceService\":\"maliev.pdfservice.api\",\"exitCode\":1.5}]}")]
    [InlineData("{\"schemaVersion\":1,\"selector\":\"pdf\",\"children\":[{\"sourceService\":\"maliev.pdfservice.api\",\"exitCode\":2147483648}]}")]
    [InlineData("{\"schemaVersion\":1,\"selector\":\"pdf\",\"children\":[{\"sourceService\":\"maliev.pdfservice.api\",\"deployScriptIsFile\":false,\"invocationFailed\":false}]}")]
    [InlineData("{\"schemaVersion\":1,\"selector\":\"pdf\",\"children\":[{\"sourceService\":\"maliev.pdfservice.api\",\"deployScriptIsFile\":false,\"exitCode\":0}]}")]
    [InlineData("{\"schemaVersion\":1,\"selector\":\"pdf\",\"children\":[{\"sourceService\":\"maliev.pdfservice.api\",\"invocationFailed\":true,\"exitCode\":0}]}")]
    [InlineData("{\"schemaVersion\":1,\"selector\":\"pdf\",\"children\":[{\"sourceService\":\"maliev.pdfservice.api\"},{\"sourceService\":\"maliev.pdfservice.api\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"selector\":\"api\",\"children\":[{\"sourceService\":\"maliev.authservice.api\",\"deployScriptIsFile\":false},{\"sourceService\":\"maliev.countryservice.api\",\"exitCode\":\"bad\"}]}")]
    public async Task WriteReviewScript_SelectorRejectsWholeMalformedOrIncoherentInputBeforeOutput(string input)
    {
        var result = await RunSelectorReview(input);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Null(result.Output);
    }

    [Fact]
    public async Task WriteReviewScript_SelectorEnforcesExactByteBoundUtf8AndExclusiveOutput()
    {
        var valid = SelectorInput("pdf", "[{\"sourceService\":\"maliev.pdfservice.api\",\"deployScriptIsFile\":true,\"invocationFailed\":false,\"exitCode\":0}]");
        var padded = valid + new string(' ', 32 * 1024 - System.Text.Encoding.UTF8.GetByteCount(valid));
        var atLimit = await RunSelectorReview(padded, verifyExclusive: true);
        Assert.True(atLimit.ExitCode == 0, atLimit.Error);
        Assert.NotNull(atLimit.Output);
        var oversized = await RunSelectorReview(padded + " ");
        Assert.NotEqual(0, oversized.ExitCode);
        Assert.Null(oversized.Output);
        var invalidUtf8 = await RunSelectorReview(valid, invalidUtf8: true);
        Assert.NotEqual(0, invalidUtf8.ExitCode);
        Assert.Null(invalidUtf8.Output);
    }

    private static string SelectorInput(string selector, string children) =>
        "{\"schemaVersion\":1,\"selector\":\"" + selector + "\",\"children\":" + children + "}";

    private static async Task<(int ExitCode, string Error, string? Output)> RunSelectorReview(string input, bool invalidUtf8 = false, bool verifyExclusive = false)
    {
        var prefix = Path.Combine(Path.GetTempPath(), "apphost-selector-" + Guid.NewGuid().ToString("N"));
        var inputPath = prefix + "-input.json";
        var outputPath = prefix + "-output.json";
        try
        {
            if (invalidUtf8) { await File.WriteAllBytesAsync(inputPath, [0xff, 0xfe, 0xff]); }
            else { await File.WriteAllTextAsync(inputPath, input, new System.Text.UTF8Encoding(false)); }
            var result = await RunResourceReviewScript(outputPath, "4", observationPath: inputPath);
            var output = File.Exists(outputPath) ? await File.ReadAllTextAsync(outputPath) : null;
            if (verifyExclusive && result.ExitCode == 0)
            {
                var second = await RunResourceReviewScript(outputPath, "4", observationPath: inputPath);
                Assert.NotEqual(0, second.ExitCode);
                Assert.Equal(output, await File.ReadAllTextAsync(outputPath));
            }
            return (result.ExitCode, result.Error, output);
        }
        finally
        {
            File.Delete(inputPath);
            File.Delete(outputPath);
        }
    }

    private static JsonElement ResourceRecord(JsonElement root, string workload) =>
        Assert.Single(root.GetProperty("workloadResourceReview").GetProperty("workloads").EnumerateArray(),
            record => record.GetProperty("sourceWorkload").GetString() == workload);

    private static int? NullableInt(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();

    private static async Task<(int ExitCode, string Error)> RunResourceReviewScript(string path, string version, bool wrongHash = false, string? observationPath = null)
    {
        var hash = wrongHash ? new string('0', 64)
            : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(LegacyEdgeReviewPackage).Assembly.Location)));
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(RepositoryRoot(), "scripts", "write-edge-review-package.ps1"),
            "-AcmeEmail", "owner@maliev.test", "-ExistingStaticIpName", "existing-ip", "-OutputPath", path,
            "-ReviewedAssemblySha256", hash, "-SchemaVersion", version })
        {
            start.ArgumentList.Add(argument);
        }
        if (observationPath is not null)
        {
            start.ArgumentList.Add("-ReleaseObservationPath");
            start.ArgumentList.Add(observationPath);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The resource review process did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException cancellation)
        {
            try
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); }
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanupTimeout.Token);
                await Task.WhenAll(output, error).WaitAsync(cleanupTimeout.Token);
            }
            catch (Exception cleanupFailure)
            {
                cancellation.Data["ProcessCleanupFailure"] = cleanupFailure;
            }
            throw;
        }
        await Task.WhenAll(output, error);
        return (process.ExitCode, await error);
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
