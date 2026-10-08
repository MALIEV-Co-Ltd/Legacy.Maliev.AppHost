using Legacy.Maliev.AppHost.Topology;

namespace Legacy.Maliev.AppHost.Tests;

public sealed class LegacyCatalogLookupWorkloadGrantsTests
{
    [Fact]
    public void AppendTo_PreservesExistingPermissionsAndAddsOnlyLookupReads()
    {
        var existing = new[] { "legacy-customer.addresses.update", "legacy-catalog.countries.read" };

        var result = LegacyCatalogLookupWorkloadGrants.AppendTo(existing);

        Assert.Equal(new[]
        {
            "legacy-customer.addresses.update",
            "legacy-catalog.countries.read",
            "legacy-catalog.locations.read",
            "legacy-catalog.companies.read",
        }, result);
        Assert.Equal(2, existing.Length);
        existing[0] = "changed-after-copy";
        Assert.Equal("legacy-customer.addresses.update", result[0]);
    }

    [Fact]
    public void AppendTo_ReapplicationDoesNotDuplicateGrants()
    {
        var existing = new[]
        {
            "legacy-catalog.companies.read",
            "legacy-customer.customers.read",
            "legacy-catalog.locations.read",
            "legacy-catalog.companies.read",
        };

        var once = LegacyCatalogLookupWorkloadGrants.AppendTo(existing);
        var twice = LegacyCatalogLookupWorkloadGrants.AppendTo(once);

        Assert.Equal(new[]
        {
            "legacy-catalog.companies.read",
            "legacy-customer.customers.read",
            "legacy-catalog.locations.read",
        }, once);
        Assert.Equal(once, twice);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void AppendTo_RejectsInvalidExistingGrant(string? invalidPermission)
    {
        Assert.ThrowsAny<ArgumentException>(() => LegacyCatalogLookupWorkloadGrants.AppendTo([invalidPermission!]));
    }

    [Fact]
    public void AppendTo_RejectsMissingExistingList()
    {
        Assert.Throws<ArgumentNullException>(() => LegacyCatalogLookupWorkloadGrants.AppendTo(null!));
    }

    [Fact]
    public void AppendTo_ReturnsImmutablePermissions()
    {
        var permissions = LegacyCatalogLookupWorkloadGrants.AppendTo([]);

        Assert.Throws<NotSupportedException>(() => ((IList<string>)permissions).Add("legacy-catalog.locations.write"));
    }
}
