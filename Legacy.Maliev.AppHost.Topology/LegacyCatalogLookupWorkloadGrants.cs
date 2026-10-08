namespace Legacy.Maliev.AppHost.Topology;

/// <summary>Appends Catalog lookup read grants to an existing server workload permission list.</summary>
/// <remarks>
/// The graph owner must apply the result to the existing Web or Intranet server identity.
/// This helper does not grant browser/customer permissions or enable a live provider.
/// </remarks>
public static class LegacyCatalogLookupWorkloadGrants
{
    /// <summary>Gets the administrative lookup read permission.</summary>
    public const string LocationsRead = "legacy-catalog.locations.read";

    /// <summary>Gets the company lookup read permission.</summary>
    public const string CompaniesRead = "legacy-catalog.companies.read";

    /// <summary>Preserves existing grants and their order, appending only missing lookup read grants.</summary>
    /// <param name="existingPermissions">The complete existing server workload permission list.</param>
    /// <returns>An immutable copy with the two lookup read grants present exactly once.</returns>
    public static IReadOnlyList<string> AppendTo(IReadOnlyList<string> existingPermissions)
    {
        ArgumentNullException.ThrowIfNull(existingPermissions);

        var permissions = new List<string>(existingPermissions.Count + 2);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var permission in existingPermissions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(permission);
            if (seen.Add(permission))
            {
                permissions.Add(permission);
            }
        }

        foreach (var permission in new[] { LocationsRead, CompaniesRead })
        {
            if (seen.Add(permission))
            {
                permissions.Add(permission);
            }
        }

        return permissions.AsReadOnly();
    }
}
