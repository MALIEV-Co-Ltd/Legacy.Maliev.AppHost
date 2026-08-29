namespace Legacy.Maliev.AppHost.Topology;

/// <summary>Resolves the consolidated-secret keys used for explicit GKE database validation.</summary>
public static class LegacyGkeDatabaseCredentialKeys
{
    /// <summary>Gets the username and password keys for a legacy database.</summary>
    /// <param name="databaseName">The unchanged legacy PostgreSQL database name.</param>
    /// <returns>The consolidated-secret keys for the database credentials.</returns>
    /// <exception cref="ArgumentException">Thrown when the database name is blank.</exception>
    public static (string Username, string Password) For(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        var prefix = databaseName.Equals("Auth", StringComparison.OrdinalIgnoreCase)
            ? "legacy-auth-refresh-sessions"
            : $"legacy-postgres-{ToKebabCase(databaseName)}";

        return ($"{prefix}-username", $"{prefix}-password");
    }

    private static string ToKebabCase(string value)
    {
        var result = new System.Text.StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index > 0 && char.IsUpper(character))
            {
                result.Append('-');
            }

            result.Append(char.ToLowerInvariant(character));
        }

        return result.ToString();
    }
}
