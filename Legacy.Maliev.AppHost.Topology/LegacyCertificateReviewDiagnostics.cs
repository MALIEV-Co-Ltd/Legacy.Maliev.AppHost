using System.Text;
using System.Text.Json;

namespace Legacy.Maliev.AppHost.Topology;

/// <summary>Interprets supplied certificate observations without querying or changing infrastructure.</summary>
public static class LegacyCertificateReviewDiagnostics
{
    /// <summary>Retains the source runbook's certificate states and outstanding diagnostic checks.</summary>
    /// <param name="evidenceJson">Bounded owner-supplied condition, expiry, issuer and challenge observations.</param>
    /// <param name="asOfUtc">The explicit UTC instant used to inspect the supplied expiry.</param>
    /// <returns>An evidence-only diagnostic envelope that cannot establish runtime acceptance.</returns>
    public static string Diagnose(string evidenceJson, DateTimeOffset asOfUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceJson);
        if (Encoding.UTF8.GetByteCount(evidenceJson) > 32768 || asOfUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Bounded evidence and an explicit UTC review instant are required.");
        }
        using var document = JsonDocument.Parse(evidenceJson, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Certificate evidence must be an object.");
        }
        var schemaVersion = 1;
        if (root.TryGetProperty("schemaVersion", out var version))
        {
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out schemaVersion) || schemaVersion is not (1 or 2))
            {
                throw new InvalidDataException("An explicit supported integer observation schema version is required.");
            }
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            var supported = property.Name is "condition" or "notAfterUtc" or "issuerReady" or "challengeFailed" or "schemaVersion"
                || schemaVersion == 2 && property.Name == "challengeObservation";
            if (!keys.Add(property.Name) || !supported)
            {
                throw new InvalidDataException("Unknown or duplicate certificate evidence field.");
            }
        }
        if (!root.TryGetProperty("condition", out var conditionElement) || conditionElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("An explicit reported certificate condition is required.");
        }
        var condition = conditionElement.GetString();
        if (condition is not ("Unknown" or "Ready" or "Renewing" or "Failed"))
        {
            throw new InvalidDataException("Unsupported reported certificate condition.");
        }
        var notAfter = ReadOptionalUtcTimestamp(root, "notAfterUtc");
        DateTimeOffset? pendingSince = null;
        int? failureCount = null;
        if (schemaVersion == 2 && root.TryGetProperty("challengeObservation", out var observation) && observation.ValueKind != JsonValueKind.Null)
        {
            if (observation.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Challenge observations must be an object or unknown.");
            }
            var observationKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in observation.EnumerateObject())
            {
                if (!observationKeys.Add(property.Name) || property.Name is not ("pendingSinceUtc" or "failureCount"))
                {
                    throw new InvalidDataException("Unknown or duplicate challenge observation field.");
                }
            }
            pendingSince = ReadOptionalUtcTimestamp(observation, "pendingSinceUtc");
            if (pendingSince is { } pendingInstant)
            {
                var timestamp = observation.GetProperty("pendingSinceUtc").GetString()!;
                var dateLength = timestamp.Length - (timestamp.EndsWith('Z') ? 1 : 6);
                if ((dateLength != 19 && (dateLength < 21 || dateLength > 27 || timestamp[19] != '.')) || pendingInstant > asOfUtc)
                {
                    throw new InvalidDataException("Pending observations require explicit UTC at no more than tick precision and cannot start after the review instant.");
                }
            }
            if (observation.TryGetProperty("failureCount", out var failures) && failures.ValueKind != JsonValueKind.Null)
            {
                if (failures.ValueKind != JsonValueKind.Number || !failures.TryGetInt32(out var count) || count < 0)
                {
                    throw new InvalidDataException("Observed failure count must be a nonnegative integer or unknown.");
                }
                failureCount = count;
            }
        }
        var issuerReady = ReadOptionalBoolean(root, "issuerReady");
        var challengeFailed = ReadOptionalBoolean(root, "challengeFailed");
        var checks = new List<string>();
        if (notAfter is null) { checks.Add("ObtainCertificateExpiry"); }
        if (condition == "Unknown") { checks.Add("ObtainCertificateCondition"); }
        if (condition == "Failed") { checks.Add("InspectCertificateRequestFailure"); }
        if (condition == "Renewing") { checks.Add("InspectRenewalEvents"); }
        var status = condition == "Ready" && notAfter is null ? "Unknown" : condition;
        if (notAfter is { } expiration && expiration <= asOfUtc)
        {
            status = "Expired";
            checks.Add("InspectRenewalEvents");
        }
        if (issuerReady is null) { checks.Add("ObtainClusterIssuerCondition"); }
        else if (!issuerReady.Value) { checks.Add("InspectClusterIssuerConfiguration"); }
        if (challengeFailed is null) { checks.Add("ObtainChallengeCondition"); }
        else if (challengeFailed.Value) { checks.Add("InspectChallengeAndIngress"); }
        // Source runbook alerts use strict seven/thirty-day UTC boundaries, independently
        // of the reported condition. Missing expiry cannot establish absence of an alert.
        var expiryAlert = notAfter is null ? "Unknown"
            : notAfter.Value - asOfUtc < TimeSpan.FromDays(7) ? "Critical"
            : notAfter.Value - asOfUtc < TimeSpan.FromDays(30) ? "Warning"
            : "None";
        var report = new
        {
            schemaVersion = 1,
            evidenceOnly = true,
            runtimeAcceptanceProven = false,
            productionDeploymentAllowed = false,
            asOfUtc,
            reportedCondition = condition,
            status,
            expiryAlert,
            checks = checks.Distinct(StringComparer.Ordinal)
        };
        var options = new JsonSerializerOptions { WriteIndented = true };
        if (schemaVersion == 1) { return JsonSerializer.Serialize(report, options); }
        // V2 deliberately retains the prose's precise >10-minute threshold rather than the
        // source shell example's integer-minute floor. Supplied observations do not prove health.
        var challengePendingAlert = pendingSince is null ? "Unknown"
            : asOfUtc - pendingSince.Value > TimeSpan.FromMinutes(10) ? "Info" : "None";
        var challengeFailureAlert = failureCount is null ? "Unknown" : failureCount > 3 ? "Critical" : "None";
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 2,
            report.evidenceOnly,
            report.runtimeAcceptanceProven,
            report.productionDeploymentAllowed,
            report.asOfUtc,
            report.reportedCondition,
            report.status,
            report.expiryAlert,
            report.checks,
            challengePendingAlert,
            challengeFailureAlert
        }, options);
    }

    private static DateTimeOffset? ReadOptionalUtcTimestamp(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) { return null; }
        if (value.ValueKind != JsonValueKind.String || !value.TryGetDateTimeOffset(out var parsed)
            || parsed.Offset != TimeSpan.Zero
            || !(value.GetString()!.EndsWith('Z') || value.GetString()!.EndsWith("+00:00", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Observation timestamps must be explicit UTC or unknown.");
        }
        return parsed;
    }

    private static bool? ReadOptionalBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) { return null; }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException("Issuer and challenge observations must be boolean or unknown.")
        };
    }
}
