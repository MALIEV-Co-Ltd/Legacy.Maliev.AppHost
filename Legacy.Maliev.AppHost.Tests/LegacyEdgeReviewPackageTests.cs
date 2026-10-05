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
        Assert.Equal(7, objects.Length);
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
        var solver = Assert.Single(issuer.GetProperty("solvers").EnumerateArray());
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
        Assert.Equal(4, certificates.Length);
        foreach (var certificate in certificates)
        {
            var spec = certificate.GetProperty("spec");
            Assert.Equal("720h", spec.GetProperty("renewBefore").GetString());
            Assert.Equal(issuerName, spec.GetProperty("issuerRef").GetProperty("name").GetString());
            Assert.Equal("ClusterIssuer", spec.GetProperty("issuerRef").GetProperty("kind").GetString());
            Assert.False(spec.TryGetProperty("privateKey", out _));
            var host = Assert.Single(spec.GetProperty("dnsNames").EnumerateArray()).GetString();
            Assert.Equal(spec.GetProperty("commonName").GetString(), host);
            var tls = Assert.Single(ingress.GetProperty("spec").GetProperty("tls").EnumerateArray(),
                item => item.GetProperty("hosts")[0].GetString() == host);
            Assert.Equal(spec.GetProperty("secretName").GetString(), tls.GetProperty("secretName").GetString());
        }
        Assert.Contains(root.GetProperty("unresolvedGates").EnumerateArray(), gate => gate.GetString()!.Contains("line-chatbot", StringComparison.Ordinal));
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
            "/jobs", "/invoices", "/messages", "/purchaseorders", "/receipts", "/quotationrequests"];
        Assert.Equal(expectedPaths, paths.Select(path => path.GetProperty("path").GetString()));
        string[] expectedServices = ["auth", "country", "catalog", "customer", "catalog", "procurement",
            "order", "file", "order", "notification", "quotation", "employee", "accounting", "document",
            "career", "accounting", "contact", "procurement", "accounting", "quotation"];
        for (var index = 0; index < expectedPaths.Length; index++)
        {
            AssertService(paths, expectedPaths[index], "legacy-maliev-" + expectedServices[index] + "-service");
        }
        foreach (var path in paths)
        {
            Assert.Equal("Prefix", path.GetProperty("pathType").GetString());
            Assert.Equal(8080, path.GetProperty("backend").GetProperty("service").GetProperty("port").GetProperty("number").GetInt32());
        }
        var intranet = Assert.Single(rules, rule => rule.GetProperty("host").GetString() == "intranet.maliev.com");
        AssertService(intranet.GetProperty("http").GetProperty("paths").EnumerateArray().ToArray(), "/", "legacy-maliev-intranet-bff");
        foreach (var web in rules.Where(rule => rule.GetProperty("host").GetString() is "www.maliev.com" or "maliev.com"))
        {
            AssertService(web.GetProperty("http").GetProperty("paths").EnumerateArray().ToArray(), "/", "legacy-maliev-web");
        }
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

    private static JsonElement Single(JsonElement[] objects, string kind) =>
        Assert.Single(objects, item => item.GetProperty("kind").GetString() == kind);

    private static void AssertService(JsonElement[] paths, string path, string service) =>
        Assert.Equal(service, Assert.Single(paths, item => item.GetProperty("path").GetString() == path)
            .GetProperty("backend").GetProperty("service").GetProperty("name").GetString());
}
