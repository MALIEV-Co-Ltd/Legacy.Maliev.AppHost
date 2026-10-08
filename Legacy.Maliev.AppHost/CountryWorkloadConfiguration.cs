using System.Collections;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.Memory;

internal sealed class CountryWorkloadConfiguration
{
    private CountryWorkloadConfiguration(string clientId, string secret, string hash, string origin, string[] permissions, string environmentName)
    {
        ClientId = clientId;
        ClientSecret = secret;
        SecretSha256 = hash;
        IamOrigin = origin;
        Permissions = permissions;
        EnvironmentName = environmentName;
    }

    internal string ClientId { get; }
    internal string ClientSecret { get; }
    internal string SecretSha256 { get; }
    internal string IamOrigin { get; }
    internal IReadOnlyList<string> Permissions { get; }
    internal string EnvironmentName { get; }

    internal static IReadOnlyDictionary<string, string?> CaptureEnvironment()
    {
        var captured = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = ((string)entry.Key).Replace("__", ":", StringComparison.Ordinal);
            if (key.StartsWith("CountryWorkload:", StringComparison.OrdinalIgnoreCase))
            {
                captured[key] = (string?)entry.Value;
            }
        }
        return captured;
    }

    internal static void RestoreEnvironment(IConfigurationBuilder configuration, IReadOnlyDictionary<string, string?> captured)
    {
        if (captured.Count == 0) return;
        // Restore only the selected caller's configuration, never its ambient process variables.
        // Command-line input keeps normal precedence over environment input.
        var commandLineIndex = configuration.Sources.ToList().FindLastIndex(source => source is CommandLineConfigurationSource);
        var source = new MemoryConfigurationSource { InitialData = captured };
        if (commandLineIndex < 0) configuration.Add(source);
        else configuration.Sources.Insert(commandLineIndex, source);
    }

    internal static CountryWorkloadConfiguration? Read(IConfiguration configuration, string environmentName)
    {
        var section = configuration.GetSection("CountryWorkload");
        var enabled = section["Enabled"];
        if (enabled is null) return null;
        if (!bool.TryParse(enabled, out var active)) throw InvalidConfiguration();
        if (!active) return null;
        if (!StringComparer.OrdinalIgnoreCase.Equals(environmentName, "Development")
            && !StringComparer.OrdinalIgnoreCase.Equals(environmentName, "Testing")) throw InvalidConfiguration();

        var clientId = section["ClientId"];
        var secret = section["ClientSecret"];
        var hash = section["SecretSha256"];
        var origin = section["IamOrigin"];
        var permissionEntries = section.GetSection("Permissions").GetChildren().ToArray();
        var permissions = permissionEntries.Select(entry => entry.Value).ToArray();
        // Client identifiers become Auth configuration keys; reject separators and existing callers.
        if (string.IsNullOrEmpty(clientId) || clientId.Length > 128
            || clientId.Any(character => !(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            || clientId[0] == '-' || clientId is "legacy-web" or "legacy-intranet" or "legacy-quotation" or "legacy-accounting"
            || configuration.GetSection("ServiceClients:Clients").GetChildren().Any(client => StringComparer.OrdinalIgnoreCase.Equals(client.Key, clientId))
            || string.IsNullOrWhiteSpace(secret) || secret.Length is < 16 or > 1024
            || hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)
            || permissions.Length is < 1 or > 4
            || permissions.Any(permission => permission is not ("legacy-country.countries.read"
                or "legacy-country.countries.create" or "legacy-country.countries.update" or "legacy-country.countries.delete"))
            || permissions.Distinct(StringComparer.Ordinal).Count() != permissions.Length
            || !permissionEntries.Select((entry, index) => entry.Key == index.ToString(System.Globalization.CultureInfo.InvariantCulture)).All(valid => valid)
            || !IsApprovedOriginShape(origin))
            throw InvalidConfiguration();

        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        if (!CryptographicOperations.FixedTimeEquals(actualHash, Convert.FromHexString(hash))) throw InvalidConfiguration();
        return new(clientId, secret, Convert.ToHexStringLower(actualHash), origin!, permissions.Select(permission => permission!).ToArray(), environmentName);
    }

    private static bool IsApprovedOriginShape(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin) || origin.Length > 2048 || origin.Any(char.IsWhiteSpace) || origin.Contains('\\')
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        var logical = uri.Scheme == "https+http" && uri.Port == -1 && uri.Host is "iamservice" or "legacy-maliev-iam-service";
        var comparison = logical ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var authority = uri.GetLeftPart(UriPartial.Authority);
        if (!string.Equals(origin, authority, comparison) && !string.Equals(origin, authority + "/", comparison)) return false;
        // Read requires Development/Testing before accepting the producer's loopback HTTP policy.
        return uri.Scheme == "https" || logical || uri.Scheme == "http" && uri.IsLoopback;
    }

    private static InvalidOperationException InvalidConfiguration() => new("CountryWorkload opt-in requires an explicit valid identity, matching credential hash, IAM origin and exact Country permissions.");
}
