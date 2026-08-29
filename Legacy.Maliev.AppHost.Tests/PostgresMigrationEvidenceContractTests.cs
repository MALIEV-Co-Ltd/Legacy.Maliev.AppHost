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
        var result = await RunValidatorAsync(evidence, evidence.RequiredAsOfUtc);
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
    [InlineData("table-row-drift")]
    [InlineData("column-null-drift")]
    [InlineData("aggregate-drift")]
    [InlineData("batch-hash-drift")]
    [InlineData("missing-table")]
    [InlineData("missing-column")]
    [InlineData("missing-aggregate")]
    [InlineData("missing-batch")]
    [InlineData("table-inventory-hash-drift")]
    [InlineData("foreign-key-inventory-hash-drift")]
    [InlineData("sequence-inventory-hash-drift")]
    [InlineData("batch-inventory-hash-drift")]
    [InlineData("signed-inventory-count-drift")]
    [InlineData("observed-inventory-count-drift")]
    [InlineData("foreign-key-inventory-omitted")]
    [InlineData("foreign-key-inventory-added")]
    [InlineData("sequence-inventory-omitted")]
    [InlineData("sequence-inventory-added")]
    [InlineData("expired-evidence")]
    [InlineData("foreign-target-generation")]
    [InlineData("foreign-restore-id")]
    [InlineData("tampered-run-id")]
    [InlineData("database-case-drift")]
    public async Task Validator_RejectsSignedButIncompleteOrUnsafeEvidence(string mutation)
    {
        using var evidence = TemporaryEvidence.Create(mutation);
        var result = await RunValidatorAsync(evidence, evidence.RequiredAsOfUtc);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Theory]
    [InlineData("payload-tamper")]
    [InlineData("signature-tamper")]
    [InlineData("unknown-key")]
    public async Task Validator_RejectsUntrustedOrTamperedAttestation(string mutation)
    {
        using var evidence = TemporaryEvidence.Create(mutation);
        var result = await RunValidatorAsync(evidence, evidence.RequiredAsOfUtc);
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task Validator_AllowsDifferentSourceAndTargetSchemaHashes()
    {
        using var evidence = TemporaryEvidence.Create("different-schema-hashes");
        var result = await RunValidatorAsync(evidence, evidence.RequiredAsOfUtc);
        Assert.True(result.ExitCode == 0, result.StandardError);
    }

    [Fact]
    public async Task Validator_AllowsEmptyRelationshipInventoriesOnlyWhenSignedPlanExpectsNone()
    {
        using var evidence = TemporaryEvidence.Create("planned-empty-relations");
        var result = await RunValidatorAsync(evidence, evidence.RequiredAsOfUtc);
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
    public async Task Validator_AtomicallyRejectsReceiptReplay()
    {
        using var evidence = TemporaryEvidence.Create();
        var first = await RunValidatorAsync(evidence, evidence.RequiredAsOfUtc);
        var replay = await RunValidatorAsync(evidence, evidence.RequiredAsOfUtc);

        Assert.True(first.ExitCode == 0, first.StandardError);
        Assert.NotEqual(0, replay.ExitCode);
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
        Assert.Contains("ConsumptionLedgerPath", script, StringComparison.Ordinal);
        Assert.Contains("sourceNullCount", script, StringComparison.Ordinal);
        Assert.Contains("batchInventorySha256", script, StringComparison.Ordinal);
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
            "-ConsumptionLedgerPath", evidence.LedgerPath,
            "-ExpectedRunId", "11111111-1111-4111-8111-111111111111",
            "-ExpectedTargetGeneration", "shadow-generation-1",
            "-ExpectedRestoreId", "restore-current",
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

        private TemporaryEvidence(string path, string publicKeyPath, string ledgerPath, string requiredAsOfUtc, string directory)
        {
            Path = path;
            PublicKeyPath = publicKeyPath;
            LedgerPath = ledgerPath;
            RequiredAsOfUtc = requiredAsOfUtc;
            _directory = directory;
        }

        public string Path { get; }
        public string PublicKeyPath { get; }
        public string LedgerPath { get; }
        public string RequiredAsOfUtc { get; }

        public static TemporaryEvidence Create(string? mutation = null)
        {
            var directory = Directory.CreateTempSubdirectory("legacy-postgres-evidence-v2-");
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string mappingHash = new('c', 64);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var root = BuildRoot(mappingHash, now);
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
            string ledgerPath = System.IO.Path.Combine(directory.FullName, "consumed");
            File.WriteAllText(path, root.ToJsonString());
            File.WriteAllText(publicKeyPath, key.ExportSubjectPublicKeyInfoPem());
            return new TemporaryEvidence(path, publicKeyPath, ledgerPath, now.AddMinutes(-30).ToString("O"), directory.FullName);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        private static JsonObject BuildRoot(string mappingHash, DateTimeOffset now) => new()
        {
            ["schemaVersion"] = 2,
            ["source"] = new JsonObject
            {
                ["system"] = "sqlserver",
                ["snapshotId"] = "source-current",
                ["capturedAtUtc"] = now.AddMinutes(-20).ToString("O"),
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
                ["databases"] = new JsonArray(MigratedDatabases.Select(name => DatabasePlan(name)).ToArray()),
            },
            ["target"] = new JsonObject
            {
                ["system"] = "postgresql",
                ["cluster"] = "legacy-postgres-main",
                ["namespace"] = "maliev-legacy",
                ["mode"] = "shadow",
                ["generation"] = "shadow-generation-1",
                ["capturedAtUtc"] = now.AddMinutes(-10).ToString("O"),
                ["restoreId"] = "restore-current",
            },
            ["execution"] = new JsonObject
            {
                ["runId"] = "11111111-1111-4111-8111-111111111111",
                ["evidenceId"] = "22222222-2222-4222-8222-222222222222",
                ["issuedAtUtc"] = now.AddMinutes(-5).ToString("O"),
                ["expiresAtUtc"] = now.AddMinutes(15).ToString("O"),
                ["leaseId"] = "33333333-3333-4333-8333-333333333333",
                ["leaseAcquiredAtUtc"] = now.AddMinutes(-4).ToString("O"),
                ["leaseExpiresAtUtc"] = now.AddMinutes(10).ToString("O"),
                ["targetGeneration"] = "shadow-generation-1",
                ["restoreId"] = "restore-current",
                ["state"] = "completed",
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

        private static JsonObject DatabasePlan(string name) => new()
        {
            ["name"] = name,
            ["tableInventorySha256"] = new string('f', 64),
            ["foreignKeyInventorySha256"] = new string('a', 64),
            ["sequenceInventorySha256"] = new string('b', 64),
            ["expectedTableCount"] = 1L,
            ["expectedForeignKeyCount"] = 1L,
            ["expectedSequenceCount"] = 1L,
            ["tables"] = new JsonArray(new JsonObject
            {
                ["name"] = "dbo.records",
                ["columns"] = new JsonArray("id", "value"),
                ["approvedAggregates"] = new JsonArray("id_range"),
                ["expectedColumnCount"] = 2L,
                ["expectedAggregateCount"] = 1L,
                ["expectedBatchCount"] = 1L,
                ["batchInventorySha256"] = new string('c', 64),
            }),
            ["foreignKeys"] = new JsonArray("fk_parent"),
            ["sequences"] = new JsonArray("primary_id"),
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
                ["tableInventorySha256"] = new string('f', 64),
                ["foreignKeyInventorySha256"] = new string('a', 64),
                ["sequenceInventorySha256"] = new string('b', 64),
                ["tableCount"] = 1L,
                ["foreignKeyCount"] = 1L,
                ["sequenceCount"] = 1L,
                ["tables"] = new JsonArray(new JsonObject
                {
                    ["name"] = "dbo.records",
                    ["sourceRowCount"] = 10L + index,
                    ["targetRowCount"] = 10L + index,
                    ["columnCount"] = 2L,
                    ["aggregateCount"] = 1L,
                    ["batchCount"] = 1L,
                    ["columns"] = new JsonArray(
                        Column("id", 0),
                        Column("value", 1)),
                    ["aggregates"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "id_range",
                        ["sourceValueSha256"] = new string('d', 64),
                        ["targetValueSha256"] = new string('d', 64),
                    }),
                    ["batchInventorySha256"] = new string('c', 64),
                    ["batches"] = new JsonArray(new JsonObject
                    {
                        ["ordinal"] = 0L,
                        ["sourceRowCount"] = 10L + index,
                        ["targetRowCount"] = 10L + index,
                        ["sourceContentSha256"] = new string(contentSeed, 64),
                        ["targetContentSha256"] = new string(contentSeed, 64),
                    }),
                    ["parity"] = "exact",
                }),
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

        private static JsonObject Column(string name, long nullCount) => new()
        {
            ["name"] = name,
            ["sourceNullCount"] = nullCount,
            ["targetNullCount"] = nullCount,
        };

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
                case "table-row-drift": ((JsonObject)((JsonArray)first["tables"]!)[0]!)["targetRowCount"] = 9L; break;
                case "column-null-drift": ((JsonObject)((JsonArray)((JsonObject)((JsonArray)first["tables"]!)[0]!)["columns"]!)[1]!)["targetNullCount"] = 2L; break;
                case "aggregate-drift": ((JsonObject)((JsonArray)((JsonObject)((JsonArray)first["tables"]!)[0]!)["aggregates"]!)[0]!)["targetValueSha256"] = new string('e', 64); break;
                case "batch-hash-drift": ((JsonObject)((JsonArray)((JsonObject)((JsonArray)first["tables"]!)[0]!)["batches"]!)[0]!)["targetContentSha256"] = new string('e', 64); break;
                case "missing-table": ((JsonArray)first["tables"]!).Clear(); break;
                case "missing-column": ((JsonArray)((JsonObject)((JsonArray)first["tables"]!)[0]!)["columns"]!).RemoveAt(0); break;
                case "missing-aggregate": ((JsonArray)((JsonObject)((JsonArray)first["tables"]!)[0]!)["aggregates"]!).Clear(); break;
                case "missing-batch": ((JsonArray)((JsonObject)((JsonArray)first["tables"]!)[0]!)["batches"]!).Clear(); break;
                case "table-inventory-hash-drift": first["tableInventorySha256"] = new string('e', 64); break;
                case "foreign-key-inventory-hash-drift": first["foreignKeyInventorySha256"] = new string('e', 64); break;
                case "sequence-inventory-hash-drift": first["sequenceInventorySha256"] = new string('e', 64); break;
                case "batch-inventory-hash-drift": ((JsonObject)((JsonArray)first["tables"]!)[0]!)["batchInventorySha256"] = new string('e', 64); break;
                case "signed-inventory-count-drift": ((JsonObject)((JsonArray)((JsonObject)root["mapping"]!)["databases"]!)[0]!)["expectedForeignKeyCount"] = 0L; break;
                case "observed-inventory-count-drift": first["sequenceCount"] = 0L; break;
                case "foreign-key-inventory-omitted": ((JsonArray)first["foreignKeys"]!).Clear(); break;
                case "foreign-key-inventory-added": ((JsonArray)first["foreignKeys"]!).Add(new JsonObject { ["name"] = "fk_unknown", ["sourceRelationshipCount"] = 0L, ["targetRelationshipCount"] = 0L, ["orphanCount"] = 0L }); break;
                case "sequence-inventory-omitted": ((JsonArray)first["sequences"]!).Clear(); break;
                case "sequence-inventory-added": ((JsonArray)first["sequences"]!).Add(new JsonObject { ["name"] = "unknown", ["sourceNextValue"] = 1L, ["targetNextValue"] = 1L }); break;
                case "expired-evidence": ((JsonObject)root["execution"]!)["expiresAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"); break;
                case "foreign-target-generation": ((JsonObject)root["execution"]!)["targetGeneration"] = "foreign-generation"; break;
                case "foreign-restore-id": ((JsonObject)root["execution"]!)["restoreId"] = "foreign-restore"; break;
                case "tampered-run-id": ((JsonObject)root["execution"]!)["runId"] = "44444444-4444-4444-8444-444444444444"; break;
                case "database-case-drift": first["name"] = "country"; break;
                case "planned-empty-relations":
                    JsonObject firstPlan = (JsonObject)((JsonArray)((JsonObject)root["mapping"]!)["databases"]!)[0]!;
                    ((JsonArray)firstPlan["foreignKeys"]!).Clear();
                    ((JsonArray)firstPlan["sequences"]!).Clear();
                    firstPlan["expectedForeignKeyCount"] = 0L;
                    firstPlan["expectedSequenceCount"] = 0L;
                    ((JsonArray)first["foreignKeys"]!).Clear();
                    ((JsonArray)first["sequences"]!).Clear();
                    first["foreignKeyCount"] = 0L;
                    first["sequenceCount"] = 0L;
                    break;
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
