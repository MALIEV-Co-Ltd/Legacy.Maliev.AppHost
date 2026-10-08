using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Legacy.Maliev.AppHost.Topology;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class LegacyCertificateReviewDiagnosticsTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("2026-10-05T00:00:00Z", "None")]
    [InlineData("2026-10-04T23:50:00.0000001Z", "None")]
    [InlineData("2026-10-04T23:50:00Z", "None")]
    [InlineData("2026-10-04T23:50:00+00:00", "None")]
    [InlineData("2026-10-04T23:49:59.9999999Z", "Info")]
    [InlineData("2026-10-04T23:49:01Z", "Info")]
    public void Diagnose_V2UsesPreciseRunbookPendingThresholdWithoutShellMinuteRounding(string pendingSince, string alert)
    {
        var evidence = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            condition = "Ready",
            notAfterUtc = "2026-11-04T00:00:00Z",
            issuerReady = true,
            challengeFailed = false,
            challengeObservation = new { pendingSinceUtc = pendingSince }
        });
        using var result = JsonDocument.Parse(LegacyCertificateReviewDiagnostics.Diagnose(evidence, AsOf));
        Assert.Equal(2, result.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(alert, result.RootElement.GetProperty("challengePendingAlert").GetString());
        Assert.Equal("Unknown", result.RootElement.GetProperty("challengeFailureAlert").GetString());
        Assert.Equal("Ready", result.RootElement.GetProperty("status").GetString());
        Assert.Equal("None", result.RootElement.GetProperty("expiryAlert").GetString());
        Assert.False(result.RootElement.GetProperty("runtimeAcceptanceProven").GetBoolean());
        Assert.False(result.RootElement.GetProperty("productionDeploymentAllowed").GetBoolean());
    }

    [Theory]
    [InlineData(0, "None")]
    [InlineData(3, "None")]
    [InlineData(4, "Critical")]
    [InlineData(int.MaxValue, "Critical")]
    public void Diagnose_V2UsesObservedFailureCountWithoutInferringConsistencyWithTheLegacyFlag(int failures, string alert)
    {
        var evidence = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            condition = "Ready",
            notAfterUtc = "2026-11-04T00:00:00Z",
            issuerReady = true,
            challengeFailed = true,
            challengeObservation = new { failureCount = failures }
        });
        using var result = JsonDocument.Parse(LegacyCertificateReviewDiagnostics.Diagnose(evidence, AsOf));
        Assert.Equal(alert, result.RootElement.GetProperty("challengeFailureAlert").GetString());
        Assert.Equal("Unknown", result.RootElement.GetProperty("challengePendingAlert").GetString());
        Assert.Equal("Ready", result.RootElement.GetProperty("status").GetString());
        Assert.Equal("InspectChallengeAndIngress", Assert.Single(result.RootElement.GetProperty("checks").EnumerateArray()).GetString());
        Assert.False(result.RootElement.GetProperty("runtimeAcceptanceProven").GetBoolean());
    }

    [Theory]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown"}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":null}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"pendingSinceUtc":null}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"pendingSinceUtc":null,"failureCount":null}}""")]
    public void Diagnose_V2LeavesAbsentObservationsIndependentlyUnknown(string evidence)
    {
        using var result = JsonDocument.Parse(LegacyCertificateReviewDiagnostics.Diagnose(evidence, AsOf));
        Assert.Equal("Unknown", result.RootElement.GetProperty("challengePendingAlert").GetString());
        Assert.Equal("Unknown", result.RootElement.GetProperty("challengeFailureAlert").GetString());
        Assert.Equal("Unknown", result.RootElement.GetProperty("status").GetString());
        Assert.Equal("Unknown", result.RootElement.GetProperty("expiryAlert").GetString());
        Assert.Equal(new[] { "ObtainCertificateExpiry", "ObtainCertificateCondition", "ObtainClusterIssuerCondition", "ObtainChallengeCondition" },
            result.RootElement.GetProperty("checks").EnumerateArray().Select(check => check.GetString()));
    }

    [Theory]
    [InlineData("Ready")]
    [InlineData("Renewing")]
    [InlineData("Failed")]
    [InlineData("Unknown")]
    public void Diagnose_V2PreservesIndependentCertificateAndIssuerChecks(string condition)
    {
        var evidence = JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            condition,
            notAfterUtc = "2026-10-06T00:00:00Z",
            issuerReady = false,
            challengeFailed = true,
            challengeObservation = new { pendingSinceUtc = "2026-10-04T23:49:59Z", failureCount = 0 }
        });
        using var result = JsonDocument.Parse(LegacyCertificateReviewDiagnostics.Diagnose(evidence, AsOf));
        Assert.Equal(condition, result.RootElement.GetProperty("status").GetString());
        Assert.Equal(condition, result.RootElement.GetProperty("reportedCondition").GetString());
        Assert.Equal("Critical", result.RootElement.GetProperty("expiryAlert").GetString());
        Assert.Equal("Info", result.RootElement.GetProperty("challengePendingAlert").GetString());
        Assert.Equal("None", result.RootElement.GetProperty("challengeFailureAlert").GetString());
        Assert.Contains(result.RootElement.GetProperty("checks").EnumerateArray(), check => check.GetString() == "InspectClusterIssuerConfiguration");
        Assert.Contains(result.RootElement.GetProperty("checks").EnumerateArray(), check => check.GetString() == "InspectChallengeAndIngress");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Diagnose_UnversionedAndExplicitV1PreserveTheAcceptedReportShape(bool explicitVersion)
    {
        const string legacy = """{"condition":"Unknown","notAfterUtc":null,"issuerReady":null,"challengeFailed":null}""";
        var evidence = explicitVersion ? """{"schemaVersion":1,"condition":"Unknown","notAfterUtc":null,"issuerReady":null,"challengeFailed":null}""" : legacy;
        var output = LegacyCertificateReviewDiagnostics.Diagnose(evidence, AsOf);
        Assert.Equal(LegacyCertificateReviewDiagnostics.Diagnose(legacy, AsOf), output);
        using var result = JsonDocument.Parse(output);
        Assert.Equal(new[] { "schemaVersion", "evidenceOnly", "runtimeAcceptanceProven", "productionDeploymentAllowed", "asOfUtc", "reportedCondition", "status", "expiryAlert", "checks" },
            result.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(1, result.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Unknown", result.RootElement.GetProperty("status").GetString());
        Assert.Equal("Unknown", result.RootElement.GetProperty("expiryAlert").GetString());
        Assert.Equal(AsOf, result.RootElement.GetProperty("asOfUtc").GetDateTimeOffset());
        Assert.True(result.RootElement.GetProperty("evidenceOnly").GetBoolean());
        Assert.False(result.RootElement.GetProperty("runtimeAcceptanceProven").GetBoolean());
        Assert.False(result.RootElement.GetProperty("productionDeploymentAllowed").GetBoolean());
    }

    [Theory]
    [InlineData("""{"schemaVersion":0,"condition":"Unknown"}""")]
    [InlineData("""{"schemaVersion":3,"condition":"Unknown"}""")]
    [InlineData("""{"schemaVersion":null,"condition":"Unknown"}""")]
    [InlineData("""{"schemaVersion":"2","condition":"Unknown"}""")]
    [InlineData("""{"schemaVersion":true,"condition":"Unknown"}""")]
    [InlineData("""{"schemaVersion":1.0,"condition":"Unknown"}""")]
    [InlineData("""{"schemaVersion":1,"condition":"Unknown","challengeObservation":{}}""")]
    [InlineData("""{"condition":"Unknown","challengeObservation":null}""")]
    [InlineData("""{"schemaVersion":2,"schemaVersion":2,"condition":"Unknown"}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":null,"challengeObservation":{}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"unknown":true}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"failureCount":0,"failureCount":4}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"pendingSinceUtc":null,"pendingSinceUtc":null}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":"unknown"}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":[]}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":true}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"failureCount":-1}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"failureCount":2147483648}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"failureCount":0.5}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"failureCount":"4"}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"failureCount":true}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"failureCount":3.0}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"failureCount":1e0}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"pendingSinceUtc":"2026-10-05T00:00:00.0000001Z"}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"pendingSinceUtc":"2026-10-04T23:49:59"}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"pendingSinceUtc":"2026-10-05T06:49:59+07:00"}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"pendingSinceUtc":"invalid"}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"pendingSinceUtc":123}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"pendingSinceUtc":true}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"pendingSinceUtc":"2026-10-04T23:49:59.99999999Z"}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"pendingSinceUtc":"2026-10-04Z"}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"pendingSinceUtc":"2026-10-04T23:49Z"}}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","unexpected":true}""")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","failureCount":4}""")]
    public void Diagnose_V2RejectsInvalidVersionsShapesCountsAndPendingTimes(string evidence)
    {
        Assert.Throws<InvalidDataException>(() => LegacyCertificateReviewDiagnostics.Diagnose(evidence, AsOf));
    }

    [Theory]
    [InlineData("2026-10-04T23:59:59.9999999Z", "Critical", "Expired")]
    [InlineData("2026-10-05T00:00:00Z", "Critical", "Expired")]
    [InlineData("2026-10-05T00:00:00.0000001Z", "Critical", "Ready")]
    [InlineData("2026-10-11T23:59:59.9999999Z", "Critical", "Ready")]
    [InlineData("2026-10-12T00:00:00Z", "Warning", "Ready")]
    [InlineData("2026-10-12T00:00:00+00:00", "Warning", "Ready")]
    [InlineData("2026-10-12T00:00:00.0000001Z", "Warning", "Ready")]
    [InlineData("2026-11-03T23:59:59.9999999Z", "Warning", "Ready")]
    [InlineData("2026-11-04T00:00:00Z", "None", "Ready")]
    [InlineData("2026-11-04T00:00:00+00:00", "None", "Ready")]
    [InlineData("2026-11-04T00:00:00.0000001Z", "None", "Ready")]
    public void Diagnose_RetainsSourceExpiryAlertsAtExactUtcBoundaries(string expiry, string alert, string status)
    {
        var evidence = JsonSerializer.Serialize(new { condition = "Ready", notAfterUtc = expiry, issuerReady = true, challengeFailed = false });
        using var result = JsonDocument.Parse(LegacyCertificateReviewDiagnostics.Diagnose(evidence, AsOf));
        Assert.Equal(alert, result.RootElement.GetProperty("expiryAlert").GetString());
        Assert.Equal(status, result.RootElement.GetProperty("status").GetString());
        Assert.False(result.RootElement.GetProperty("runtimeAcceptanceProven").GetBoolean());
        Assert.False(result.RootElement.GetProperty("productionDeploymentAllowed").GetBoolean());
    }

    [Theory]
    [InlineData("Ready")]
    [InlineData("Renewing")]
    [InlineData("Failed")]
    [InlineData("Unknown")]
    public void Diagnose_ExpiryAlertDoesNotRewriteReportedConditionOrSuppressExistingChecks(string condition)
    {
        var evidence = JsonSerializer.Serialize(new { condition, notAfterUtc = "2026-10-06T00:00:00Z", issuerReady = false, challengeFailed = true });
        using var result = JsonDocument.Parse(LegacyCertificateReviewDiagnostics.Diagnose(evidence, AsOf));
        Assert.Equal("Critical", result.RootElement.GetProperty("expiryAlert").GetString());
        Assert.Equal(condition, result.RootElement.GetProperty("reportedCondition").GetString());
        Assert.Equal(condition, result.RootElement.GetProperty("status").GetString());
        Assert.Contains(result.RootElement.GetProperty("checks").EnumerateArray(), check => check.GetString() == "InspectClusterIssuerConfiguration");
        Assert.Contains(result.RootElement.GetProperty("checks").EnumerateArray(), check => check.GetString() == "InspectChallengeAndIngress");
    }

    [Theory]
    [InlineData("Ready", "2026-11-01T00:00:00Z", "Ready")]
    [InlineData("Ready", "2026-11-01T00:00:00+00:00", "Ready")]
    [InlineData("Ready", null, "Unknown")]
    [InlineData("Unknown", null, "Unknown")]
    [InlineData("Renewing", "2026-11-01T00:00:00Z", "Renewing")]
    [InlineData("Renewing", null, "Renewing")]
    [InlineData("Failed", "2026-11-01T00:00:00Z", "Failed")]
    [InlineData("Ready", "2026-10-05T00:00:00Z", "Expired")]
    [InlineData("Ready", "2026-10-05T00:00:00+00:00", "Expired")]
    [InlineData("Ready", "2026-10-04T23:59:59Z", "Expired")]
    [InlineData("Renewing", "2026-10-04T23:59:59Z", "Expired")]
    [InlineData("Failed", "2026-10-04T23:59:59Z", "Expired")]
    public void Diagnose_UsesReportedStateAndActualExpiryWithoutClaimingRuntimeAcceptance(string condition, string? expiry, string status)
    {
        var evidence = JsonSerializer.Serialize(new { condition, notAfterUtc = expiry, issuerReady = true, challengeFailed = false });
        using var result = JsonDocument.Parse(LegacyCertificateReviewDiagnostics.Diagnose(evidence, AsOf));
        Assert.Equal(condition, result.RootElement.GetProperty("reportedCondition").GetString());
        Assert.Equal(status, result.RootElement.GetProperty("status").GetString());
        Assert.True(result.RootElement.GetProperty("evidenceOnly").GetBoolean());
        Assert.False(result.RootElement.GetProperty("runtimeAcceptanceProven").GetBoolean());
        Assert.False(result.RootElement.GetProperty("productionDeploymentAllowed").GetBoolean());
        Assert.Equal(AsOf, result.RootElement.GetProperty("asOfUtc").GetDateTimeOffset());
    }

    [Fact]
    public void Diagnose_RetainsFailedRequestAndRenewalChecksAlongsideIssuerAndChallengeFailures()
    {
        using var result = JsonDocument.Parse(LegacyCertificateReviewDiagnostics.Diagnose(
            """{"condition":"Failed","notAfterUtc":"2026-10-04T23:59:59Z","issuerReady":false,"challengeFailed":true}""", AsOf));
        Assert.Equal("Expired", result.RootElement.GetProperty("status").GetString());
        Assert.Equal(new[] { "InspectCertificateRequestFailure", "InspectRenewalEvents", "InspectClusterIssuerConfiguration", "InspectChallengeAndIngress" },
            result.RootElement.GetProperty("checks").EnumerateArray().Select(check => check.GetString()));
    }

    [Theory]
    [InlineData("""{"condition":"Unknown"}""")]
    [InlineData("""{"condition":"Unknown","notAfterUtc":null,"issuerReady":null,"challengeFailed":null}""")]
    public void Diagnose_LeavesMissingObservationsUnknown(string evidence)
    {
        using var result = JsonDocument.Parse(LegacyCertificateReviewDiagnostics.Diagnose(evidence, AsOf));
        Assert.Equal("Unknown", result.RootElement.GetProperty("status").GetString());
        Assert.Equal("Unknown", result.RootElement.GetProperty("expiryAlert").GetString());
        Assert.Equal(new[] { "ObtainCertificateExpiry", "ObtainCertificateCondition", "ObtainClusterIssuerCondition", "ObtainChallengeCondition" },
            result.RootElement.GetProperty("checks").EnumerateArray().Select(check => check.GetString()));
    }

    [Fact]
    public void Diagnose_DoesNotDuplicateRenewalChecksForExpiredRenewingCertificate()
    {
        using var result = JsonDocument.Parse(LegacyCertificateReviewDiagnostics.Diagnose(
            """{"condition":"Renewing","notAfterUtc":"2026-10-04T23:59:59Z","issuerReady":true,"challengeFailed":false}""", AsOf));
        Assert.Equal("InspectRenewalEvents", Assert.Single(result.RootElement.GetProperty("checks").EnumerateArray()).GetString());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"condition":true}""")]
    [InlineData("""{"condition":"Unknown","condition":"Ready"}""")]
    [InlineData("""{"condition":"Unknown","message":"unreviewed"}""")]
    [InlineData("""{"condition":"unrecognized"}""")]
    [InlineData("""{"condition":"Ready","notAfterUtc":123}""")]
    [InlineData("""{"condition":"Ready","notAfterUtc":"not-a-timestamp"}""")]
    [InlineData("""{"condition":"Ready","notAfterUtc":"2026-11-01T00:00:00"}""")]
    [InlineData("""{"condition":"Ready","notAfterUtc":"2026-11-01T07:00:00+07:00"}""")]
    [InlineData("""{"condition":"Ready","issuerReady":"true"}""")]
    [InlineData("""{"condition":"Ready","challengeFailed":1}""")]
    public void Diagnose_RejectsAmbiguousOrUnsupportedEvidence(string evidence)
    {
        Assert.Throws<InvalidDataException>(() => LegacyCertificateReviewDiagnostics.Diagnose(evidence, AsOf));
    }

    [Fact]
    public void Diagnose_RejectsOversizedEvidenceAndNonUtcReviewInstant()
    {
        Assert.Throws<ArgumentException>(() => LegacyCertificateReviewDiagnostics.Diagnose(new string(' ', 32768) + "{}", AsOf));
        Assert.Throws<ArgumentException>(() => LegacyCertificateReviewDiagnostics.Diagnose("""{"condition":"Unknown"}""", AsOf.ToOffset(TimeSpan.FromHours(7))));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Diagnose_RejectsEmptyEvidence(string evidence)
    {
        Assert.Throws<ArgumentException>(() => LegacyCertificateReviewDiagnostics.Diagnose(evidence, AsOf));
    }

    [Fact]
    public void Diagnose_RejectsMalformedJson()
    {
        Assert.ThrowsAny<JsonException>(() => LegacyCertificateReviewDiagnostics.Diagnose("{", AsOf));
    }

    [Fact]
    public void ValidateDiagnosticArtifact_RequiresItsOwnUnchangedCompiledSource()
    {
        var assembly = typeof(LegacyCertificateReviewDiagnostics).Assembly.Location;
        var pdb = Path.ChangeExtension(assembly, ".pdb");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly)));
        var root = RepositoryRoot();
        var source = Path.Combine(root, "Legacy.Maliev.AppHost.Topology", "LegacyCertificateReviewDiagnostics.cs");
        LegacyRendererSourceProvenance.ValidateCertificateDiagnostics(assembly, pdb, source, hash);
        Assert.Throws<InvalidDataException>(() => LegacyRendererSourceProvenance.ValidateCertificateDiagnostics(
            assembly, pdb, Path.Combine(root, "Legacy.Maliev.AppHost.Topology", "LegacyEdgeReviewPackage.cs"), hash));
        var temporary = Path.Combine(Path.GetTempPath(), "apphost-diagnostic-source-" + Guid.NewGuid().ToString("N") + ".cs");
        try
        {
            File.WriteAllText(temporary, File.ReadAllText(source) + "\n// modified owned copy\n");
            Assert.Throws<InvalidDataException>(() => LegacyRendererSourceProvenance.ValidateCertificateDiagnostics(assembly, pdb, temporary, hash));
        }
        finally { File.Delete(temporary); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReviewScript_UsesReviewedArtifactAndPreservesExistingOutput(bool validEvidence)
    {
        var directory = Path.Combine(Path.GetTempPath(), "apphost-certificate-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var evidencePath = Path.Combine(directory, "evidence.json");
        var outputPath = Path.Combine(directory, "review.json");
        try
        {
            await File.WriteAllTextAsync(evidencePath, validEvidence ? """{"condition":"Unknown"}""" : """{"condition":"Ready","message":"unsupported"}""");
            var first = await RunReviewScript(evidencePath, outputPath);
            if (validEvidence)
            {
                Assert.Equal(0, first);
                using var report = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
                Assert.Equal("Unknown", report.RootElement.GetProperty("status").GetString());
                Assert.Equal("Unknown", report.RootElement.GetProperty("expiryAlert").GetString());
                Assert.False(report.RootElement.GetProperty("runtimeAcceptanceProven").GetBoolean());
                var original = await File.ReadAllBytesAsync(outputPath);
                Assert.NotEqual(0, await RunReviewScript(evidencePath, outputPath));
                Assert.Equal(original, await File.ReadAllBytesAsync(outputPath));
            }
            else
            {
                Assert.NotEqual(0, first);
                Assert.False(File.Exists(outputPath));
            }
        }
        finally
        {
            File.Delete(evidencePath);
            File.Delete(outputPath);
            Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData("2026-10-05T00:00:00")]
    [InlineData("2026-10-05T07:00:00+07:00")]
    public async Task ReviewScript_RejectsReviewInstantWithoutExplicitUtc(string reviewInstant)
    {
        var directory = Path.Combine(Path.GetTempPath(), "apphost-certificate-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var evidencePath = Path.Combine(directory, "evidence.json");
        var outputPath = Path.Combine(directory, "review.json");
        try
        {
            await File.WriteAllTextAsync(evidencePath, """{"condition":"Unknown"}""");
            Assert.NotEqual(0, await RunReviewScript(evidencePath, outputPath, reviewInstant));
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            File.Delete(evidencePath);
            File.Delete(outputPath);
            Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData("2026-10-06T00:00:00Z", "Critical")]
    [InlineData("2026-10-12T00:00:00Z", "Warning")]
    [InlineData("2026-11-04T00:00:00Z", "None")]
    [InlineData(null, "Unknown")]
    public async Task ReviewScript_EmitsSourceExpiryAlertsFromTheReviewedCompiledConsumer(string? expiry, string alert)
    {
        var directory = Path.Combine(Path.GetTempPath(), "apphost-certificate-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var evidencePath = Path.Combine(directory, "evidence.json");
        var outputPath = Path.Combine(directory, "review.json");
        try
        {
            await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(new
            {
                condition = "Ready",
                notAfterUtc = expiry,
                issuerReady = true,
                challengeFailed = false
            }));
            Assert.Equal(0, await RunReviewScript(evidencePath, outputPath));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            Assert.Equal(alert, report.RootElement.GetProperty("expiryAlert").GetString());
            Assert.Equal(expiry is null ? "Unknown" : "Ready", report.RootElement.GetProperty("status").GetString());
            Assert.Equal("Ready", report.RootElement.GetProperty("reportedCondition").GetString());
            Assert.False(report.RootElement.GetProperty("runtimeAcceptanceProven").GetBoolean());
            Assert.False(report.RootElement.GetProperty("productionDeploymentAllowed").GetBoolean());
            var original = await File.ReadAllBytesAsync(outputPath);
            Assert.NotEqual(0, await RunReviewScript(evidencePath, outputPath));
            Assert.Equal(original, await File.ReadAllBytesAsync(outputPath));
        }
        finally
        {
            File.Delete(evidencePath);
            File.Delete(outputPath);
            Directory.Delete(directory);
        }
    }

    [Theory]
    [InlineData("""{"condition":"Unknown"}""", true, 1, "Unknown", "Unknown")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":null}""", true, 2, "Unknown", "Unknown")]
    [InlineData("""{"schemaVersion":2,"condition":"Ready","challengeObservation":{"pendingSinceUtc":"2026-10-04T23:49:01Z","failureCount":4}}""", true, 2, "Info", "Critical")]
    [InlineData("""{"schemaVersion":2,"condition":"Ready","challengeFailed":true,"challengeObservation":{"pendingSinceUtc":"2026-10-04T23:50:00Z","failureCount":0}}""", true, 2, "None", "None")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"pendingSinceUtc":"2026-10-05T00:00:00.0000001Z"}}""", false, 2, "Unknown", "Unknown")]
    [InlineData("""{"schemaVersion":2,"condition":"Unknown","challengeObservation":{"failureCount":-1}}""", false, 2, "Unknown", "Unknown")]
    public async Task ReviewScript_PreservesLegacyAndUsesReviewedV2ObservationsBeforeCreatingOutput(string evidence, bool valid, int schemaVersion, string pendingAlert, string failureAlert)
    {
        var directory = Path.Combine(Path.GetTempPath(), "apphost-certificate-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var evidencePath = Path.Combine(directory, "evidence.json");
        var outputPath = Path.Combine(directory, "review.json");
        try
        {
            await File.WriteAllTextAsync(evidencePath, evidence);
            var exitCode = await RunReviewScript(evidencePath, outputPath);
            if (valid)
            {
                Assert.Equal(0, exitCode);
                using var report = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
                Assert.Equal(schemaVersion, report.RootElement.GetProperty("schemaVersion").GetInt32());
                Assert.True(report.RootElement.GetProperty("evidenceOnly").GetBoolean());
                Assert.False(report.RootElement.GetProperty("runtimeAcceptanceProven").GetBoolean());
                Assert.False(report.RootElement.GetProperty("productionDeploymentAllowed").GetBoolean());
                if (schemaVersion == 2)
                {
                    Assert.Equal(pendingAlert, report.RootElement.GetProperty("challengePendingAlert").GetString());
                    Assert.Equal(failureAlert, report.RootElement.GetProperty("challengeFailureAlert").GetString());
                    if (failureAlert == "None")
                    {
                        Assert.Contains(report.RootElement.GetProperty("checks").EnumerateArray(), check => check.GetString() == "InspectChallengeAndIngress");
                    }
                }
                else
                {
                    Assert.False(report.RootElement.TryGetProperty("challengePendingAlert", out _));
                    Assert.False(report.RootElement.TryGetProperty("challengeFailureAlert", out _));
                }
                var original = await File.ReadAllBytesAsync(outputPath);
                Assert.NotEqual(0, await RunReviewScript(evidencePath, outputPath));
                Assert.Equal(original, await File.ReadAllBytesAsync(outputPath));
            }
            else
            {
                Assert.NotEqual(0, exitCode);
                Assert.False(File.Exists(outputPath));
            }
        }
        finally
        {
            File.Delete(evidencePath);
            File.Delete(outputPath);
            Directory.Delete(directory);
        }
    }

    private static async Task<int> RunReviewScript(string evidencePath, string outputPath, string? reviewInstant = null)
    {
        var assembly = typeof(LegacyCertificateReviewDiagnostics).Assembly.Location;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly)));
        // This test-owned artifact correspondence is not an independently accepted build receipt.
        var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(RepositoryRoot(), "scripts", "review-certificate-status.ps1"),
            "-EvidencePath", evidencePath, "-AsOfUtc", reviewInstant ?? AsOf.ToString("O"), "-OutputPath", outputPath, "-ReviewedAssemblySha256", hash })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The review process did not start.");
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
        return process.ExitCode;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.AppHost.slnx"))) { return directory.FullName; }
        }
        throw new DirectoryNotFoundException("The owned AppHost test checkout was not found.");
    }
}
