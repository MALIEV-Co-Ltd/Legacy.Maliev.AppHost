using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class PostgresMigrationEvidenceContractTests
{
    private static readonly string[] MigratedDatabases =
    [
        "Country", "Currency", "Customer", "CustomerIdentity", "DataProtectionKeys",
        "DataProtectionKeysEmployee", "Employee", "EmployeeIdentity", "Invoice", "JobOffers",
        "Material", "Message", "Order", "OrderStatus", "Payment", "PurchaseOrder", "Quotation",
        "QuotationRequest", "Receipt", "Supplier", "Upload",
    ];

    [Fact]
    public async Task Validator_AcceptsSignedMappedSchemaAndReconciledContent()
    {
        using var evidence = TemporaryEvidence.Create();
        var result = await RunValidatorAsync(evidence, "2026-08-07T00:00:00Z");
        Assert.True(result.ExitCode == 0, result.StandardError);
    }

    [Theory]
    [InlineData("source-stale")]
    [InlineData("target-before-source")]
    [InlineData("row-count-drift")]
    [InlineData("content-drift")]
    [InlineData("foreign-key-drift")]
    [InlineData("foreign-key-orphan")]
    [InlineData("sequence-drift")]
    [InlineData("mapping-hash-drift")]
    [InlineData("duplicate-database")]
    [InlineData("missing-database")]
    [InlineData("wrong-disposition")]
    [InlineData("archive-not-immutable")]
    [InlineData("archive-missing")]
    [InlineData("unknown-field")]
    [InlineData("sensitive-field")]
    [InlineData("future-source")]
    [InlineData("unsupported-version")]
    [InlineData("non-shadow-target")]
    public async Task Validator_RejectsSignedButIncompleteOrUnsafeEvidence(string mutation)
    {
        using var evidence = TemporaryEvidence.Create(mutation);
        var result = await RunValidatorAsync(evidence, "2026-08-07T00:00:00Z");
        Assert.NotEqual(0, result.ExitCode);
    }

    [Theory]
    [InlineData("payload-tamper")]
    [InlineData("signature-tamper")]
    [InlineData("unknown-key")]
    public async Task Validator_RejectsUntrustedOrTamperedAttestation(string mutation)
    {
        using var evidence = TemporaryEvidence.Create(mutation);
        var result = await RunValidatorAsync(evidence, "2026-08-07T00:00:00Z");
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Validator_AllowsDifferentSourceAndTargetSchemaHashes()
    {
        using var evidence = TemporaryEvidence.Create("different-schema-hashes");
        var result = await RunValidatorAsync(evidence, "2026-08-07T00:00:00Z");
        Assert.True(result.ExitCode == 0, result.StandardError);
    }

    [Fact]
    public async Task Validator_RejectsNonUtcRequiredCutoff()
    {
        using var evidence = TemporaryEvidence.Create();
        var result = await RunValidatorAsync(evidence, "2026-08-07T07:00:00+07:00");
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public void WorkflowAndDocs_ExposeTheReadOnlySignedV2Gate()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "verify-postgres-migration-evidence.ps1"));
        var docs = File.ReadAllText(Path.Combine(root, "docs", "postgres-migration-evidence.md"));
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        Assert.Contains("-TrustedPublicKeyPath", docs, StringComparison.Ordinal);
        Assert.Contains("docs/postgres-migration-evidence.md", readme, StringComparison.Ordinal);
        Assert.Contains("mappingPlanSha256", script, StringComparison.Ordinal);
        Assert.Contains("foreignKeys", script, StringComparison.Ordinal);
        Assert.Contains("sequences", script, StringComparison.Ordinal);
        Assert.Contains("VerifyHash", script, StringComparison.Ordinal);
        Assert.Contains("productionDataWritesAllowed", script, StringComparison.Ordinal);
        Assert.Contains("Assert-NoSensitiveKeys", script, StringComparison.Ordinal);
        Assert.DoesNotContain("kubectl", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gcloud", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("psql", script, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int ExitCode, string StandardError)> RunValidatorAsync(TemporaryEvidence evidence, string requiredAsOfUtc)
    {
        var root = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            "-NoLogo", "-NoProfile", "-File", Path.Combine(root, "scripts", "verify-postgres-migration-evidence.ps1"),
            "-EvidencePath", evidence.Path,
            "-ExpectedDatabase", string.Join(',', MigratedDatabases),
            "-RequiredAsOfUtc", requiredAsOfUtc,
            "-TrustedPublicKeyPath", evidence.PublicKeyPath,
            "-ExpectedAttestationKeyId", "migration-review-2026-08",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("PowerShell could not be started.");
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, standardError);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.AppHost.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class TemporaryEvidence : IDisposable
    {
        private readonly string _directory;

        private TemporaryEvidence(string path, string publicKeyPath, string directory)
        {
            Path = path;
            PublicKeyPath = publicKeyPath;
            _directory = directory;
        }

        public string Path { get; }
        public string PublicKeyPath { get; }

        public static TemporaryEvidence Create(string? mutation = null)
        {
            var directory = Directory.CreateTempSubdirectory("legacy-postgres-evidence-v2-");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string mappingHash = new('c', 64);
            var root = BuildRoot(mappingHash);
            ApplyMutation(root, mutation, mappingHash);
            Sign(root, key, mutation == "unknown-key" ? "untrusted-key" : "migration-review-2026-08");

            if (mutation == "payload-tamper")
            {
                ((JsonObject)root["target"]!)["restoreId"] = "restore-tampered-after-signing";
            }
            else if (mutation == "signature-tamper")
            {
                ((JsonObject)root["attestation"]!)["signatureBase64"] = Convert.ToBase64String(new byte[64]);
            }

            string path = System.IO.Path.Combine(directory.FullName, "evidence.json");
            string publicKeyPath = System.IO.Path.Combine(directory.FullName, "trusted-public-key.pem");
            File.WriteAllText(path, root.ToJsonString());
            File.WriteAllText(publicKeyPath, key.ExportSubjectPublicKeyInfoPem());
            return new TemporaryEvidence(path, publicKeyPath, directory.FullName);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        private static JsonObject BuildRoot(string mappingHash) => new()
        {
            ["schemaVersion"] = 2,
            ["source"] = new JsonObject
            {
                ["system"] = "sqlserver",
                ["snapshotId"] = "source-2026-08-07",
                ["capturedAtUtc"] = "2026-08-07T00:05:00.0000000+00:00",
                ["backup"] = new JsonObject
                {
                    ["uri"] = "gs://maliev.com/database/full/2026-08-07/",
                    ["manifestSha256"] = new string('a', 64),
                    ["databaseInventorySha256"] = new string('b', 64),
                    ["objectGeneration"] = "generation-20260807",
                    ["immutable"] = true,
                },
            },
            ["mapping"] = new JsonObject
            {
                ["schemaPlanVersion"] = "2.0",
                ["planSha256"] = mappingHash,
                ["sourceCommitSha"] = new string('d', 40),
                ["runnerDigestSha256"] = new string('e', 64),
            },
            ["target"] = new JsonObject
            {
                ["system"] = "postgresql",
                ["cluster"] = "legacy-postgres-main",
                ["namespace"] = "maliev-legacy",
                ["mode"] = "shadow",
                ["generation"] = "shadow-generation-1",
                ["capturedAtUtc"] = "2026-08-07T00:30:00.0000000+00:00",
                ["restoreId"] = "restore-2026-08-07",
            },
            ["inventory"] = BuildInventory(),
            ["archives"] = new JsonArray(Archive("Hangfire", '1'), Archive("Log", '2')),
            ["databases"] = new JsonArray(MigratedDatabases.Select((name, index) => Database(name, mappingHash, index)).ToArray()),
            ["parity"] = "exact",
            ["constraints"] = new JsonObject
            {
                ["productionDataWritesAllowed"] = false,
                ["canonicalTargetMutationAllowed"] = false,
                ["cutoverPercent"] = 0,
                ["newNodePoolAllowed"] = false,
                ["cloudSqlAllowed"] = false,
                ["additionalInfrastructureCostAllowed"] = false,
            },
        };

        private static JsonArray BuildInventory()
        {
            (string Name, string Owner, string Disposition)[] entries =
            [
                ("ContactRequest", "Legacy.Maliev.CompatibilityContracts", "review_hold"),
                ("Country", "Legacy.Maliev.CatalogService", "migrate"), ("Currency", "Legacy.Maliev.CatalogService", "migrate"),
                ("Customer", "Legacy.Maliev.CustomerService", "migrate"), ("CustomerIdentity", "Legacy.Maliev.AuthService", "migrate"),
                ("DataProtectionKeys", "Legacy.Maliev.AuthService", "migrate"), ("DataProtectionKeysEmployee", "Legacy.Maliev.AuthService", "migrate"),
                ("Employee", "Legacy.Maliev.EmployeeService", "migrate"), ("EmployeeIdentity", "Legacy.Maliev.AuthService", "migrate"),
                ("Hangfire", "Legacy.Maliev.CompatibilityContracts", "archive_only"), ("Invoice", "Legacy.Maliev.AccountingService", "migrate"),
                ("JobOffers", "Legacy.Maliev.CareerService", "migrate"), ("LocationData", "Legacy.Maliev.CompatibilityContracts", "review_hold"),
                ("Log", "Legacy.Maliev.CompatibilityContracts", "archive_only"), ("MachineLearning", "Legacy.Maliev.CompatibilityContracts", "excluded"),
                ("MachineLearningData", "Legacy.Maliev.CompatibilityContracts", "excluded"), ("Material", "Legacy.Maliev.CatalogService", "migrate"),
                ("Message", "Legacy.Maliev.ContactService", "migrate"), ("Order", "Legacy.Maliev.OrderService", "migrate"),
                ("OrderStatus", "Legacy.Maliev.OrderService", "migrate"), ("Payment", "Legacy.Maliev.AccountingService", "migrate"),
                ("PurchaseOrder", "Legacy.Maliev.ProcurementService", "migrate"), ("Quotation", "Legacy.Maliev.QuotationService", "migrate"),
                ("QuotationRequest", "Legacy.Maliev.QuotationService", "migrate"), ("Receipt", "Legacy.Maliev.AccountingService", "migrate"),
                ("Supplier", "Legacy.Maliev.ProcurementService", "migrate"), ("Upload", "Legacy.Maliev.FileService", "migrate"),
            ];
            return new JsonArray(entries.Select(entry => new JsonObject
            {
                ["name"] = entry.Name,
                ["owner"] = entry.Owner,
                ["disposition"] = entry.Disposition,
            }).ToArray());
        }

        private static JsonObject Archive(string name, char seed) => new()
        {
            ["name"] = name,
            ["disposition"] = "archive_only",
            ["backupArtifactSha256"] = new string(seed, 64),
            ["sourceSchemaSha256"] = new string('3', 64),
            ["sourceContentSha256"] = new string('4', 64),
            ["immutable"] = true,
        };

        private static JsonObject Database(string name, string mappingHash, int index)
        {
            char sourceSchemaSeed = (char)('a' + (index % 6));
            char targetSchemaSeed = (char)('1' + (index % 6));
            char contentSeed = (char)('7' + (index % 3));
            return new JsonObject
            {
                ["name"] = name,
                ["sourceSchemaSha256"] = new string(sourceSchemaSeed, 64),
                ["mappingPlanSha256"] = mappingHash,
                ["targetSchemaSha256"] = new string(targetSchemaSeed, 64),
                ["sourceRowCount"] = 10L + index,
                ["targetRowCount"] = 10L + index,
                ["sourceContentSha256"] = new string(contentSeed, 64),
                ["targetContentSha256"] = new string(contentSeed, 64),
                ["foreignKeys"] = new JsonArray(new JsonObject
                {
                    ["name"] = "fk_parent",
                    ["sourceRelationshipCount"] = 2L,
                    ["targetRelationshipCount"] = 2L,
                    ["orphanCount"] = 0L,
                }),
                ["sequences"] = new JsonArray(new JsonObject
                {
                    ["name"] = "primary_id",
                    ["sourceNextValue"] = 100L + index,
                    ["targetNextValue"] = 100L + index,
                }),
                ["parity"] = "exact",
            };
        }

        private static void ApplyMutation(JsonObject root, string? mutation, string mappingHash)
        {
            JsonObject source = (JsonObject)root["source"]!;
            JsonObject target = (JsonObject)root["target"]!;
            JsonArray databases = (JsonArray)root["databases"]!;
            JsonObject first = (JsonObject)databases[0]!;
            switch (mutation)
            {
                case "source-stale": source["capturedAtUtc"] = "2026-08-06T23:59:59.0000000+00:00"; break;
                case "target-before-source": target["capturedAtUtc"] = "2026-08-07T00:04:59.0000000+00:00"; break;
                case "row-count-drift": first["targetRowCount"] = 99L; break;
                case "content-drift": first["targetContentSha256"] = new string('f', 64); break;
                case "foreign-key-drift": ((JsonObject)((JsonArray)first["foreignKeys"]!)[0]!)["targetRelationshipCount"] = 1L; break;
                case "foreign-key-orphan": ((JsonObject)((JsonArray)first["foreignKeys"]!)[0]!)["orphanCount"] = 1L; break;
                case "sequence-drift": ((JsonObject)((JsonArray)first["sequences"]!)[0]!)["targetNextValue"] = 999L; break;
                case "mapping-hash-drift": first["mappingPlanSha256"] = new string('f', 64); break;
                case "duplicate-database": databases.Add(Database(MigratedDatabases[0], mappingHash, 0)); break;
                case "missing-database": databases.RemoveAt(0); break;
                case "wrong-disposition": ((JsonObject)((JsonArray)root["inventory"]!)[1]!)["disposition"] = "excluded"; break;
                case "archive-not-immutable": ((JsonObject)((JsonArray)root["archives"]!)[0]!)["immutable"] = false; break;
                case "archive-missing": ((JsonArray)root["archives"]!).RemoveAt(0); break;
                case "unknown-field": root["unexpected"] = true; break;
                case "sensitive-field": source["apiToken"] = "must-not-be-recorded"; break;
                case "future-source": source["capturedAtUtc"] = "2099-08-07T00:05:00.0000000+00:00"; break;
                case "unsupported-version": root["schemaVersion"] = 99; break;
                case "non-shadow-target": target["mode"] = "canonical"; break;
                case "different-schema-hashes": first["sourceSchemaSha256"] = new string('a', 64); first["targetSchemaSha256"] = new string('b', 64); break;
            }
        }

        private static void Sign(JsonObject root, ECDsa key, string keyId)
        {
            byte[] payload = Encoding.UTF8.GetBytes(Canonicalize(root));
            byte[] hash = SHA256.HashData(payload);
            root["attestation"] = new JsonObject
            {
                ["algorithm"] = "ECDSA_P256_SHA256",
                ["keyId"] = keyId,
                ["payloadSha256"] = Convert.ToHexString(hash).ToLowerInvariant(),
                ["signatureBase64"] = Convert.ToBase64String(key.SignHash(hash)),
            };
        }

        private static string Canonicalize(JsonNode node) => Sort(node).ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        private static JsonNode Sort(JsonNode node) => node switch
        {
            JsonObject obj => new JsonObject(obj.Where(property => property.Key != "attestation")
                .OrderBy(property => property.Key, StringComparer.Ordinal)
                .Select(property => KeyValuePair.Create(property.Key, property.Value is null ? null : Sort(property.Value))).ToArray()),
            JsonArray array => new JsonArray(array.Select(value => value is null ? null : Sort(value)).ToArray()),
            _ => JsonNode.Parse(node.ToJsonString())!,
        };
    }
}
