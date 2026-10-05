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
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!keys.Add(property.Name) || property.Name is not ("condition" or "notAfterUtc" or "issuerReady" or "challengeFailed"))
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
        DateTimeOffset? notAfter = null;
        if (root.TryGetProperty("notAfterUtc", out var expiry) && expiry.ValueKind != JsonValueKind.Null)
        {
            if (expiry.ValueKind != JsonValueKind.String || !expiry.TryGetDateTimeOffset(out var parsed)
                || parsed.Offset != TimeSpan.Zero
                || !(expiry.GetString()!.EndsWith('Z') || expiry.GetString()!.EndsWith("+00:00", StringComparison.Ordinal)))
            {
                throw new InvalidDataException("Certificate expiry must be an explicit UTC timestamp or unknown.");
            }
            notAfter = parsed;
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
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            evidenceOnly = true,
            runtimeAcceptanceProven = false,
            productionDeploymentAllowed = false,
            asOfUtc,
            reportedCondition = condition,
            status,
            checks = checks.Distinct(StringComparer.Ordinal)
        }, new JsonSerializerOptions { WriteIndented = true });
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
